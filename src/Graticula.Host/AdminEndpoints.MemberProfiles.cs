using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>A profile as a member, or an administrator, sends it.</summary>
internal sealed record ProfileRequest(string? DisplayName, string? Email, string? Bio, string? Visibility);

/// <summary>A category as the admin surface takes it.</summary>
internal sealed record MemberCategoryRequest(string? Name, Guid? ParentId);

/// <summary>Members put into categories and taken out of others.</summary>
internal sealed record CategorizeRequest(IReadOnlyList<string>? Members, IReadOnlyList<Guid>? Add, IReadOnlyList<Guid>? Remove);

/// <summary>
/// Profiles, photos and member categories — migration 90, 2026-10-17, by owner decision after Portal's pages.
/// </summary>
/// <remarks>
/// <para>
/// <b>A profile is read by whom its visibility says</b>: <c>private</c>, its member and the administrators who manage
/// members; <c>organization</c>, anybody signed in; <c>public</c>, anybody. A profile the reader may not see is a 404,
/// as an unshared layer is, so a name cannot be confirmed by asking for its profile.
/// </para>
/// <para>
/// <b>It is changed by its member or by an administrator who manages members</b> — Portal's rule, and the owner's:
/// *"her kullanıcı ya da admin onlara fotoğraf koyabilse"*. Categories are the administrator's alone.
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapMemberProfiles(WebApplication app)
    {
        app.MapGet("/rest/members/{name}/profile", GetProfileAsync);
        app.MapPut("/rest/members/{name}/profile", SetProfileAsync);
        app.MapGet("/rest/members/{name}/photo", GetPhotoAsync);
        app.MapPut("/rest/members/{name}/photo", SetPhotoAsync);
        app.MapDelete("/rest/members/{name}/photo", DeletePhotoAsync);

        app.MapGet("/admin/member-categories", ListMemberCategoriesAsync);
        app.MapPost("/admin/member-categories", CreateMemberCategoryAsync);
        app.MapPut("/admin/member-categories/{id:guid}", RenameMemberCategoryAsync);
        app.MapDelete("/admin/member-categories/{id:guid}", DeleteMemberCategoryAsync);
        app.MapPost("/admin/members/categorize", CategorizeMembersAsync);
    }

    /// <summary>Whether the caller is the member named, and whether they manage members.</summary>
    private static (bool Self, bool Manager, bool SignedIn) WhoAsks(HttpContext context, string name)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        bool signedIn = current.Principal.Kind != PrincipalKind.Anonymous;
        return (signedIn && string.Equals(current.QualifiedName, name, StringComparison.Ordinal),
            current.Authorization.Allows(Privilege.AdminManageMembers),
            signedIn);
    }

    /// <summary>Whether this caller may read a profile with this visibility.</summary>
    private static bool MayRead(HttpContext context, MemberProfile profile)
    {
        (bool self, bool manager, bool signedIn) = WhoAsks(context, profile.Name);
        return self || manager || profile.Visibility switch
        {
            "public" => true,
            // ADR-204: the organisation is this tenant's; a member of another who reached its host is not of it.
            "organization" => signedIn && context.Features.Get<RequestPrincipal>()?.Authorization.IsOutsider != true,

            // A profile's visibility is not a sharing scope and has no `group`: anything else is private.
            // enum-default-is-deliberate: private
            _ => false,
        };
    }

    private static async Task GetProfileAsync(
        HttpContext context, string name, IMemberProfiles profiles, IMemberDirectory directory,
        PostgresSecondFactorStore secondFactors, CancellationToken cancellation)
    {
        MemberProfile? profile = await profiles.GetProfileAsync(name, cancellation).ConfigureAwait(false);

        if (profile is null || !MayRead(context, profile))
        {
            await Refuse(context, 404, $"No member '{name}' whose profile you may read.").ConfigureAwait(false);
            return;
        }

        (bool self, bool manager, _) = WhoAsks(context, name);

        // What they hold is said only to themselves and to whoever manages members: a profile shown to the
        // organization is who somebody is, not what they may do.
        MemberListing? listed = null;
        if (self || manager)
        {
            MemberPage page = await directory.SearchMembersAsync(
                new MemberQuery(Text: name, Count: 50), cancellation).ConfigureAwait(false);
            listed = page.Members.FirstOrDefault(l => l.Member.Name == name);
        }

        // D-307: whether they have a second factor on — to themselves and to whoever manages members, as what they hold
        // is; to anybody else it would say which accounts a password alone opens.
        bool? secondFactor = listed is { } known
            ? (await secondFactors.FindAsync(known.Member.Id, cancellation).ConfigureAwait(false))?.Enabled ?? false
            : null;

        await Results.Json(new
        {
            name = profile.Name,
            displayName = profile.DisplayName,
            email = self || manager || profile.Visibility != "private" ? profile.Email : null,
            bio = profile.Bio,
            visibility = profile.Visibility,
            createdAt = profile.CreatedAt,
            disabled = profile.IsDisabled,
            photo = profile.PhotoUpdatedAt is { } at
                ? $"/rest/members/{Uri.EscapeDataString(profile.Name)}/photo?v={at.ToUnixTimeMilliseconds()}"
                : null,
            categories = profile.Categories,
            roles = listed?.Member.Roles,
            userType = listed?.Member.UserType,
            lastSignIn = listed?.LastSignIn,
            secondFactor,
            mayEdit = self || manager,
            mayCategorize = manager,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task SetProfileAsync(
        HttpContext context, string name, ProfileRequest request, IMemberProfiles profiles, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        (bool self, bool manager, _) = WhoAsks(context, name);

        if (!self && !manager)
        {
            await Refuse(context, 403, "A profile is changed by its member or by an administrator who manages members.")
                .ConfigureAwait(false);
            return;
        }

        string visibility = string.IsNullOrWhiteSpace(request.Visibility) ? "organization" : request.Visibility.Trim();

        if (!MemberProfileRules.Visibilities.Contains(visibility))
        {
            await Refuse(context, 400, "'visibility' is private, organization or public.").ConfigureAwait(false);
            return;
        }

        if (request.Bio is { Length: > MemberProfileRules.LongestBio })
        {
            await Refuse(context, 400, $"A bio is at most {MemberProfileRules.LongestBio} characters.").ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(request.Email) && (request.Email.Length > 254 || !request.Email.Contains('@', StringComparison.Ordinal)))
        {
            await Refuse(context, 400, "That is not an e-mail address.").ConfigureAwait(false);
            return;
        }

        if (request.DisplayName is { Length: > 255 })
        {
            await Refuse(context, 400, "A display name is at most 255 characters.").ConfigureAwait(false);
            return;
        }

        bool found = await profiles.SetProfileAsync(
            name, new ProfileEdit(request.DisplayName, request.Email, request.Bio, visibility), cancellation)
            .ConfigureAwait(false);

        await AuditAsync(context, audit, "member.profile", name, Detail(new { visibility, self }), found, cancellation)
            .ConfigureAwait(false);

        if (!found)
        {
            await Refuse(context, 404, $"No member '{name}'.").ConfigureAwait(false);
            return;
        }

        await GetProfileAsync(
            context, name, profiles, context.RequestServices.GetRequiredService<IMemberDirectory>(),
            context.RequestServices.GetRequiredService<PostgresSecondFactorStore>(), cancellation)
            .ConfigureAwait(false);
    }

    private static async Task GetPhotoAsync(
        HttpContext context, string name, IMemberProfiles profiles, CancellationToken cancellation)
    {
        MemberProfile? profile = await profiles.GetProfileAsync(name, cancellation).ConfigureAwait(false);
        MemberPhoto? photo = profile is not null && MayRead(context, profile)
            ? await profiles.GetPhotoAsync(name, cancellation).ConfigureAwait(false)
            : null;

        if (photo is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // The address carries the photo's time (`?v=`), so a changed photo is a new address and this may be kept.
        context.Response.Headers.CacheControl = "private, max-age=86400";
        context.Response.ContentType = photo.ContentType;
        await context.Response.Body.WriteAsync(photo.Content, cancellation).ConfigureAwait(false);
    }

    private static async Task SetPhotoAsync(
        HttpContext context, string name, IMemberProfiles profiles, IAuditLog audit, CancellationToken cancellation)
    {
        (bool self, bool manager, _) = WhoAsks(context, name);

        if (!self && !manager)
        {
            await Refuse(context, 403, "A photo is set by its member or by an administrator who manages members.")
                .ConfigureAwait(false);
            return;
        }

        string type = (context.Request.ContentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

        if (!MemberProfileRules.PhotoTypes.Contains(type))
        {
            await Refuse(context, 415, "A photo is a PNG, JPEG or GIF, sent as the request body with its type.")
                .ConfigureAwait(false);
            return;
        }

        using MemoryStream body = new();
        byte[] buffer = new byte[81920];
        int read;

        while ((read = await context.Request.Body.ReadAsync(buffer, cancellation).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > MemberProfileRules.LargestPhoto)
            {
                await Refuse(context, 413, "A photo is at most a megabyte; 200 × 200 pixels is all a profile shows.")
                    .ConfigureAwait(false);
                return;
            }

            body.Write(buffer, 0, read);
        }

        if (body.Length == 0 || !LooksLike(type, body.GetBuffer().AsSpan(0, (int)body.Length)))
        {
            await Refuse(context, 400, $"That is not a {type.Replace("image/", string.Empty, StringComparison.Ordinal).ToUpperInvariant()} image.")
                .ConfigureAwait(false);
            return;
        }

        bool found = await profiles.SetPhotoAsync(name, type, body.ToArray(), cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "member.photo", name, Detail(new { type, bytes = body.Length, self }), found, cancellation)
            .ConfigureAwait(false);

        if (!found)
        {
            await Refuse(context, 404, $"No member '{name}'.").ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    /// <summary>Whether the bytes begin as the type they claim: what a browser is told is what it gets.</summary>
    private static bool LooksLike(string type, ReadOnlySpan<byte> bytes) => type switch
    {
        "image/png" => bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg" => bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]),
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        _ => false,
    };

    private static async Task DeletePhotoAsync(
        HttpContext context, string name, IMemberProfiles profiles, IAuditLog audit, CancellationToken cancellation)
    {
        (bool self, bool manager, _) = WhoAsks(context, name);

        if (!self && !manager)
        {
            await Refuse(context, 403, "A photo is removed by its member or by an administrator who manages members.")
                .ConfigureAwait(false);
            return;
        }

        bool had = await profiles.DeletePhotoAsync(name, cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "member.photo.delete", name, Detail(new { self }), true, cancellation)
            .ConfigureAwait(false);
        context.Response.StatusCode = had ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
    }

    private static async Task ListMemberCategoriesAsync(
        HttpContext context, IMemberProfiles profiles, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<MemberCategory> all = await profiles.ListCategoriesAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            categories = all.Select(c => new { c.Id, parentId = c.ParentId, c.Name, members = c.Members }),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task CreateMemberCategoryAsync(
        HttpContext context, MemberCategoryRequest request, IMemberProfiles profiles, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers).ConfigureAwait(false))
        {
            return;
        }

        string? name = CategoryName(request.Name, out string? why);

        if (name is null)
        {
            await Refuse(context, 400, why!).ConfigureAwait(false);
            return;
        }

        MemberCategory? made;

        try
        {
            made = await profiles.CreateCategoryAsync(name, request.ParentId, cancellation).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == Npgsql.PostgresErrorCodes.ForeignKeyViolation)
        {
            await Refuse(context, 400, "There is no such category to put it under.").ConfigureAwait(false);
            return;
        }

        if (made is null)
        {
            await Refuse(context, 409, $"There is already a category named '{name}' there.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "member.category.create", name, Detail(new { made.Id, parent = request.ParentId }), true, cancellation)
            .ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status201Created;
        await Results.Json(new { made.Id, parentId = made.ParentId, made.Name, members = 0 }).ExecuteAsync(context).ConfigureAwait(false);
    }

    private static async Task RenameMemberCategoryAsync(
        HttpContext context, Guid id, MemberCategoryRequest request, IMemberProfiles profiles, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers).ConfigureAwait(false))
        {
            return;
        }

        string? name = CategoryName(request.Name, out string? why);

        if (name is null)
        {
            await Refuse(context, 400, why!).ConfigureAwait(false);
            return;
        }

        bool? renamed = await profiles.RenameCategoryAsync(id, name, cancellation).ConfigureAwait(false);

        if (renamed is null)
        {
            await Refuse(context, 409, $"There is already a category named '{name}' there.").ConfigureAwait(false);
            return;
        }

        if (renamed == false)
        {
            await Refuse(context, 404, "No such category.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(context, audit, "member.category.rename", name, Detail(new { id }), true, cancellation).ConfigureAwait(false);
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task DeleteMemberCategoryAsync(
        HttpContext context, Guid id, IMemberProfiles profiles, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers).ConfigureAwait(false))
        {
            return;
        }

        bool found = await profiles.DeleteCategoryAsync(id, cancellation).ConfigureAwait(false);
        await AuditAsync(context, audit, "member.category.delete", id.ToString(), Detail(new { id }), found, cancellation)
            .ConfigureAwait(false);
        context.Response.StatusCode = found ? StatusCodes.Status204NoContent : StatusCodes.Status404NotFound;
    }

    private static async Task CategorizeMembersAsync(
        HttpContext context, CategorizeRequest request, IMemberProfiles profiles, IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageMembers).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<string> members = request.Members ?? [];

        if (members.Count == 0 || members.Count > 1000)
        {
            await Refuse(context, 400, "'members' names between one and a thousand members.").ConfigureAwait(false);
            return;
        }

        int found = await profiles.CategorizeAsync(members, request.Add ?? [], request.Remove ?? [], cancellation)
            .ConfigureAwait(false);
        await AuditAsync(context, audit, "member.categorize", null,
            Detail(new { members = members.Count, add = request.Add, remove = request.Remove }), true, cancellation)
            .ConfigureAwait(false);
        await Results.Json(new { members = found }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A category's name, trimmed, or null with the reason.</summary>
    private static string? CategoryName(string? asked, out string? why)
    {
        string name = (asked ?? string.Empty).Trim();
        why = name.Length == 0 ? "A category needs a name."
            : name.Length > MemberProfileRules.LongestCategory ? $"A category's name is at most {MemberProfileRules.LongestCategory} characters."
            : null;
        return why is null ? name : null;
    }
}
