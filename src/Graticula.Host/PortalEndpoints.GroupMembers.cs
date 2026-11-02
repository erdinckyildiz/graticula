using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// A group's members through the portal — <c>community/groups/{id}/users</c> and <c>userList</c>, read-only, each a door
/// onto Studio's own group read (ADR-256 §5a).
/// </summary>
/// <remarks>
/// <para>
/// <b>The ArcGIS veteran's review of v1.0.499: a group's members could not be read.</b> Both routes answered 404, and
/// the ArcGIS API for Python's <c>group.get_members()</c> and <c>group.user_list()</c> call them — a script that adds
/// users with <c>addUsers</c> (ADR-256) could not then see whom it had added.
/// </para>
/// <para>
/// <b>Studio's read, not a second one.</b> The group is found by id among those the caller can see, as every group door
/// finds it, and then Studio's own <c>GET /admin/groups/{name}</c> runs inside the request: whether the caller may see
/// the group's members at all — a member of it, or an administrator, and only the owner and managers when the group
/// hides its members (ADR-036 §4g, owner decision 2026-08-25) — is decided there, once, for both surfaces. What it
/// withholds is refused here with ArcGIS's 403 rather than answered as an empty group, for the reason Studio gives:
/// an empty list says *nobody is in it*.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    private static void MapGroupMembers(WebApplication app)
    {
        string group = $"{Path}/community/groups/{{id:length(32)}}";

        Discoverable(app, $"{group}/users", GroupUsersAsync).Governed(SharingGovernedExtensions.ByFiltering);
        Discoverable(app, $"{group}/userList", GroupUserListAsync).Governed(SharingGovernedExtensions.ByFiltering);
    }

    /// <summary>One member as Studio's group read answered it.</summary>
    private sealed record GroupMemberRead(string Username, string? FullName, string MemberType, long? Joined);

    /// <summary>
    /// The group's owner and members as Studio's own read gives them to this caller, or the refusal to answer.
    /// </summary>
    private static async Task<(string? Owner, List<GroupMemberRead>? Members, IResult? Refused)> GroupMembersReadAsync(
        HttpContext context, IGroupDirectory groups, string id, CancellationToken cancellation)
    {
        if (await GroupByIdAsync(context, groups, id, cancellation).ConfigureAwait(false) is not { } group)
        {
            return (null, null, NoGroup(id));
        }

        (int status, string? message, JsonElement answer) = await InnerAnswerAsync(context,
            () => AdminEndpoints.DescribeGroupAsync(context, group.Name, groups, cancellation)).ConfigureAwait(false);

        if (status is < 200 or >= 300 || answer.ValueKind != JsonValueKind.Object)
        {
            return (null, null, status == 404 ? NoGroup(id) : PortalError(status, message ?? $"Group '{id}' could not be read."));
        }

        if (!answer.TryGetProperty("members", out JsonElement members) || members.ValueKind != JsonValueKind.Array)
        {
            return (null, null, PortalError(403,
                "This group's members are shown to its owner and managers only, and to nobody outside it."));
        }

        List<GroupMemberRead> read = [];

        foreach (JsonElement member in members.EnumerateArray())
        {
            string standing = member.GetProperty("standing").GetString() ?? "member";

            read.Add(new GroupMemberRead(
                member.GetProperty("name").GetString()!,
                member.TryGetProperty("displayName", out JsonElement shown) && shown.ValueKind == JsonValueKind.String
                    ? shown.GetString()
                    : null,
                standing switch
                {
                    "owner" => "owner",
                    "manager" => "admin",
                    _ => "member",
                },
                member.TryGetProperty("joined", out JsonElement joined) && joined.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(joined.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at)
                    ? at.ToUnixTimeMilliseconds()
                    : null));
        }

        string? owner = answer.TryGetProperty("owner", out JsonElement held) && held.ValueKind == JsonValueKind.String
            ? held.GetString()
            : read.FirstOrDefault(m => m.MemberType == "owner")?.Username;

        return (owner, read, null);
    }

    /// <summary>
    /// Portal's group <c>users</c> — the Python API's <c>group.get_members()</c>: <c>owner</c>, <c>admins</c> and
    /// <c>users</c>, each a username.
    /// </summary>
    /// <remarks>
    /// <b>INFERRED, for the owner to confirm:</b> the owner is listed among <c>admins</c> as well as in <c>owner</c>, and
    /// <c>users</c> holds the members who are neither — ArcGIS's reference shows the owner in <c>admins</c> and does not
    /// say whether <c>users</c> repeats the admins.
    /// </remarks>
    private static async Task<IResult> GroupUsersAsync(
        HttpContext context, IGroupDirectory groups, string id, CancellationToken cancellation)
    {
        (string? owner, List<GroupMemberRead>? members, IResult? refused) =
            await GroupMembersReadAsync(context, groups, id, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        return Results.Ok(new
        {
            owner,
            admins = members!.Where(m => m.MemberType is "owner" or "admin").Select(m => m.Username).ToArray(),
            users = members!.Where(m => m.MemberType == "member").Select(m => m.Username).ToArray(),
        });
    }

    /// <summary>
    /// Portal's group <c>userList</c> — the Python API's <c>group.user_list()</c>: the owner, and a page of the other
    /// members with <c>username</c>, <c>fullName</c>, <c>memberType</c> and <c>joined</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The parameters the reference documents, as far as this holds them:</b> <c>start</c>, <c>num</c>,
    /// <c>sortField</c> (<c>username</c>, <c>fullName</c>, <c>joined</c>, <c>memberType</c>) and <c>sortOrder</c>, as
    /// every page is cut (<see cref="PortalPage"/>); <c>name</c> narrows to members whose username or full name contains
    /// it; <c>memberType</c> to <c>owner</c>, <c>admin</c> or <c>member</c>; and <c>joined</c> to a
    /// <c>start,end</c> pair of epoch milliseconds (INFERRED — the reference names the filter, not its format), and is
    /// refused in any other form rather than ignored.
    /// </para>
    /// <para>
    /// <b>What is not held is not invented:</b> <c>thumbnail</c> is null, and there is no <c>orgId</c> per member.
    /// </para>
    /// </remarks>
    private static async Task<IResult> GroupUserListAsync(
        HttpContext context, IGroupDirectory groups, string id, CancellationToken cancellation)
    {
        (string? owner, List<GroupMemberRead>? members, IResult? refused) =
            await GroupMembersReadAsync(context, groups, id, cancellation).ConfigureAwait(false);

        if (refused is not null)
        {
            return refused;
        }

        IFormCollection form = await FormOf(context, cancellation).ConfigureAwait(false);
        IEnumerable<GroupMemberRead> listed = members!;

        if (First(form, context, "name") is { Length: > 0 } name)
        {
            listed = listed.Where(m => m.Username.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (m.FullName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (First(form, context, "memberType") is { Length: > 0 } type)
        {
            if (type is not ("owner" or "admin" or "member"))
            {
                return PortalError(400, "'memberType' is owner, admin or member.");
            }

            listed = listed.Where(m => m.MemberType == type);
        }

        if (First(form, context, "joined") is { Length: > 0 } joined)
        {
            string[] bounds = joined.Split(',', StringSplitOptions.TrimEntries);

            if (bounds.Length != 2
                || !long.TryParse(bounds[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long from)
                || !long.TryParse(bounds[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long to))
            {
                return PortalError(400, "'joined' is read as two epoch milliseconds, start and end, separated by a comma.");
            }

            listed = listed.Where(m => m.Joined is { } at && at >= from && at <= to);
        }

        GroupMemberRead? owning = members!.FirstOrDefault(m => m.MemberType == "owner");
        List<object> rows = [.. listed.Where(m => m.MemberType != "owner").Select(Row)];
        PortalPage.Page page = PortalPage.Of(rows, context, form);

        return Results.Ok(new
        {
            owner = owning is not null ? Row(owning) : owner is null ? null : (object)new { username = owner, fullName = (string?)null, memberType = "owner", thumbnail = (string?)null, joined = (long?)null },
            users = page.Results,
            total = page.Total,
            start = page.Start,
            num = page.Num,
            nextStart = page.NextStart,
        });

        static object Row(GroupMemberRead member) => new
        {
            username = member.Username,
            fullName = member.FullName,
            memberType = member.MemberType,
            thumbnail = (string?)null,
            joined = member.Joined,
        };
    }
}
