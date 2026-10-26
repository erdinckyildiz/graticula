using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// <b>Sharing what Pro published</b> — ADR-191: the Share pane's <i>Sharing Level</i> (Owner, Organization,
/// Everyone) and its group ticks arrive as the portal's <c>share</c> / <c>shareItems</c> after <c>publish</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same two acts Studio's Share dialog makes</b>: the service's scope through
/// <see cref="IAdminCatalog.SetServiceSharingAsync"/>, and the groups it is in through
/// <see cref="IGroupDirectory.ShareAsync"/> — with the same privileges asked for each (share to public, share to the
/// organization, share to groups), so Pro reaches nothing Studio would refuse.
/// </para>
/// <para>
/// <b>An item id names a service</b>: the service's own id, which is the item id the portal gives it, or the id the
/// publish answered with when the import finished after Pro stopped waiting.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The share routes — mapped where publishing from Pro is served.</summary>
    private static void MapProShare(WebApplication app)
    {
        if (!PublishingFromPro(app.Configuration))
        {
            return;
        }

        app.MapPost($"{Path}/content/users/{{username}}/items/{{id}}/share", ShareItemAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/users/{{username}}/{{folderId:length(32)}}/items/{{id}}/share", ShareItemAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapPost($"{Path}/content/users/{{username}}/shareItems", ShareItemsAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        app.MapMethods($"{Path}/content/items/{{id}}/info/publishResults.json", ["GET", "HEAD"], PublishResultsAsync)
            .Governed(SharingGovernedExtensions.ByOwnership);
        app.MapPost($"{Path}/content/updateItems", UpdateItemsAsync)
            .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();

        // Overwrite Web Layer reads the item here to fill its pane — Sharing Level and groups from `sharing`. A 404
        // left the pane on Owner for a public service, and publishing it would have made the service private.
        app.MapGet($"{Path}/content/users/{{username}}/items/{{id}}", UserItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Path}/content/users/{{username}}/{{folderId:length(32)}}/items/{{id}}", UserItemAsync)
            .Governed(SharingGovernedExtensions.ByFiltering);
        app.MapGet($"{Path}/content/items/{{id}}/relatedItems", (string id) =>
                Results.Ok(new { total = 0, relatedItems = Array.Empty<object>() }))
            .Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>Portal's user item: the item, and who it is shared with.</summary>
    private static async Task<IResult> UserItemAsync(
        HttpContext context, CatalogFallback catalog, ServiceContexts contexts, Graticula.Geometries.IProjector projector,
        IAdminCatalog admin, IWebMapStore maps, string username, string id, CancellationToken cancellation)
    {
        if (Self(context, username) is null)
        {
            return Unknown("User");
        }

        if (await VisibleAsync(context, catalog, cancellation).ConfigureAwait(false) is not { } visible)
        {
            return Unavailable();
        }

        foreach (PublishedService service in visible)
        {
            if (!ItemsOf(context, service).Any(face => string.Equals(face.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            double[][] extent = await ExtentAsync(service, contexts, projector, cancellation).ConfigureAwait(false);
            object item = await WithProtectionAsync(
                ItemsOf(context, service, extent).First(face => string.Equals(face.Id, id, StringComparison.OrdinalIgnoreCase)).Item,
                service, admin, cancellation).ConfigureAwait(false);

            return Results.Ok(new
            {
                item,
                sharing = new
                {
                    access = Access(service.Sharing),
                    groups = service.SharedWith.Select(g => g.ToString("N")).ToArray(),
                },
            });
        }

        // A web map — one Pro added with Share As Web Map, which it reads back here — when it is the user's.
        if (WebMaps.IsId(id) && await maps.FindAsync(id, cancellation).ConfigureAwait(false) is { } map
            && Self(context, username) is { } owner && map.Owner == owner.Principal.Id)
        {
            return Results.Ok(new
            {
                item = MapItem(context, map),
                sharing = new
                {
                    access = Access(map.Sharing),
                    groups = (map.SharedWith ?? []).Select(g => g.ToString("N")).ToArray(),
                },
            });
        }

        return Unknown("Item");
    }

    /// <summary>Portal's item <c>share</c>: one item, to everyone, the organization and/or groups.</summary>
    private static async Task<IResult> ShareItemAsync(
        HttpContext context, IAdminCatalog catalog, IGroupDirectory groups, IWebMapStore maps, string username, string id,
        CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        await RecordFormAsync(context, form, cancellation).ConfigureAwait(false);
        (bool ok, List<string> notShared, string? error) = await ShareOneAsync(
            context, current, catalog, groups, maps, id, form, cancellation).ConfigureAwait(false);

        return ok
            ? Results.Ok(new { notSharedWith = notShared, itemId = id })
            : PortalError(error is { } e && e.StartsWith("No item", StringComparison.Ordinal) ? 404 : 403, error ?? "Not shared.");
    }

    /// <summary>Portal's <c>shareItems</c>: several items, the same way.</summary>
    private static async Task<IResult> ShareItemsAsync(
        HttpContext context, IAdminCatalog catalog, IGroupDirectory groups, IWebMapStore maps, string username,
        CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        await RecordFormAsync(context, form, cancellation).ConfigureAwait(false);
        List<object> results = [];

        foreach (string id in First(form, context, "items").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            (bool ok, List<string> notShared, string? error) = await ShareOneAsync(
                context, current, catalog, groups, maps, id, form, cancellation).ConfigureAwait(false);

            results.Add(ok
                ? new { itemId = id, success = true, notSharedWith = notShared }
                : new { itemId = id, success = false, error = new { code = 403, message = error, details = Array.Empty<string>() } });
        }

        return Results.Ok(new { results });
    }

    /// <summary>Sets one service's scope and groups from a share form; says which groups it could not reach.</summary>
    private static async Task<(bool Ok, List<string> NotShared, string? Error)> ShareOneAsync(
        HttpContext context, RequestPrincipal current, IAdminCatalog catalog, IGroupDirectory groups, IWebMapStore maps,
        string id, IFormCollection form, CancellationToken cancellation)
    {
        string serviceItem = Staged.Values.FirstOrDefault(s => s.Owner == current.Principal.Id
            && string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))?.ServiceItemId ?? id;

        AdminService? service = Guid.TryParse(serviceItem, out Guid serviceId)
            ? (await catalog.ListServicesAsync(cancellation).ConfigureAwait(false))
                .Where(s => s.Id == serviceId).Select(s => (AdminService?)s).FirstOrDefault()
            : null;

        bool everyone = string.Equals(First(form, context, "everyone"), "true", StringComparison.OrdinalIgnoreCase);
        // Pro says the organization as `account` (measured, dev 2026-10-17); the portal API documents `org`.
        bool organization = string.Equals(First(form, context, "account"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(First(form, context, "org"), "true", StringComparison.OrdinalIgnoreCase);
        // Ids only: Pro sends `groups=false` for none (an overwrite shared as Owner, dev 2026-10-17), which was read as a
        // group and made the service group-shared.
        string[] groupIds = [.. First(form, context, "groups")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(g => Guid.TryParseExact(g, "N", out _))];

        SharingScope scope = everyone ? SharingScope.Public
            : organization ? SharingScope.Organization
            : groupIds.Length > 0 ? SharingScope.Group
            : SharingScope.Private;

        if (service is not { } found)
        {
            // A web map Pro added after its layers (Share As Web Map) is shared by the same form.
            return await ShareMapAsync(current, maps, groups, id, scope, groupIds, cancellation).ConfigureAwait(false)
                ?? (false, [], $"No item '{id}' that this portal can share.");
        }

        // ADR-220 §5.3: an ArcGIS client knows no level between the organisation and everyone, so its `org` keeps an item
        // shared with every organisation where it is, rather than narrowing it behind its owner's back.
        if (scope == SharingScope.Organization && found.Sharing == SharingScope.AllOrganizations)
        {
            scope = SharingScope.AllOrganizations;
        }

        Privilege needed = SharingPrivileges.ToShareAt(scope) ?? Privilege.ContentPublishFeatures;

        bool administrator = current.Authorization.Allows(Privilege.AdminManageAllContent);
        if (!current.Authorization.Allows(needed) || (!string.Equals(found.OwnerName, current.Principal.Name, StringComparison.OrdinalIgnoreCase) && !administrator))
        {
            return (false, [], $"Sharing '{found.Name}' {scope.ToString().ToLowerInvariant()} is not yours to do.");
        }

        await catalog.SetServiceSharingAsync(found.Name, found.Folder, scope, cancellation).ConfigureAwait(false);

        List<string> notShared = [];
        if (groupIds.Length > 0)
        {
            IReadOnlyList<GroupSummary> mine = await groups.ListAsync(current.Principal.Id, administrator, cancellation)
                .ConfigureAwait(false);

            foreach (string groupId in groupIds)
            {
                GroupSummary? group = mine.FirstOrDefault(g => string.Equals(g.Id.ToString("N"), groupId, StringComparison.OrdinalIgnoreCase));
                GroupChange outcome = group is null || !current.Authorization.Allows(Privilege.GroupsShareTo)
                    ? GroupChange.Absent
                    : await groups.ShareAsync(current.Principal.Id, administrator, group.Name, found.Name, found.Folder, true, cancellation)
                        .ConfigureAwait(false);

                if (outcome != GroupChange.Done)
                {
                    notShared.Add(groupId);
                }
            }
        }

        return (true, notShared, null);
    }

    /// <summary>Writes a share request's fields (not its token) to the capture directory, while ADR-191 is measured.</summary>
    private static async Task RecordFormAsync(HttpContext context, IFormCollection form, CancellationToken cancellation)
    {
        if (!RecordingPro(context.RequestServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()))
        {
            return;
        }

        string directory = context.RequestServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Graticula:ProPublishCaptureDirectory"]
            ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pro-capture");
        System.IO.Directory.CreateDirectory(directory);
        var fields = form.Where(f => !CapturedSecret(f.Key))
            .ToDictionary(f => f.Key, f => f.Value.ToString());
        var query = context.Request.Query.Where(q => !CapturedSecret(q.Key))
            .ToDictionary(q => q.Key, q => q.Value.ToString());
        await System.IO.File.WriteAllTextAsync(
            System.IO.Path.Combine(directory, $"share-{DateTime.UtcNow:yyyyMMdd'T'HHmmss.fff}.json"),
            System.Text.Json.JsonSerializer.Serialize(new { path = context.Request.Path.Value, query, fields }), cancellation)
            .ConfigureAwait(false);
    }

    /// <summary>The publish results Pro reads from the service definition's item after publishing.</summary>
    private static IResult PublishResultsAsync(HttpContext context, string id)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        StagedDefinition? staged = Staged.Values.FirstOrDefault(s => s.Owner == current.Principal.Id
            && (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.ServiceItemId, id, StringComparison.OrdinalIgnoreCase)));

        return staged?.ServiceUrl is { } url
            ? Results.Ok(new[] { new { type = "Feature Service", serviceurl = url, serviceItemId = staged.ServiceItemId } })
            : Unknown("Item");
    }
}
