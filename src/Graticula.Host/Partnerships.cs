using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Postgres;
using Graticula.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// Two organisations partner before their groups mix — ADR-219, ArcGIS Online's partnered collaboration. An
/// organisation's administrator invites another by its host name or key; the other's accepts or declines; either
/// suspends, resumes or ends it; each says whether the other may find its members.
/// </summary>
/// <remarks>
/// <b>Every statement but one runs on the request's own pool</b>, where the floor shows a partnership to its two parties
/// and nobody else (§5.6). Ending one changes membership in both organisations' groups, and that statement runs on the
/// root pool, narrowed to the two.
/// </remarks>
internal static class PartnershipEndpoints
{
    /// <summary>At most this many partnerships that have not ended, per organisation — ArcGIS Online's limit.</summary>
    internal const int Limit = 20;

    /// <summary>An invitation: the other organisation by host name or key, and whether it may find this one's members.</summary>
    internal sealed record InviteRequest(string? Organization, bool? ShareMembers);

    /// <summary>A change: accept, suspend, resume or end; and, alone or beside it, this side's member visibility.</summary>
    internal sealed record ChangeRequest(string? Action, bool? ShareMembers);

    private sealed record Row(
        Guid Id, Guid Inviter, Guid Invitee, string Status, bool InviterShares, bool InviteeShares, string InvitedBy,
        DateTimeOffset InvitedAt, DateTimeOffset ChangedAt, DateTimeOffset StatusAt);

    public static void Map(WebApplication app)
    {
        app.MapGet("/admin/partnerships", ListAsync);
        app.MapPost("/admin/partnerships", InviteAsync);
        app.MapPut("/admin/partnerships/{id:guid}", ChangeAsync);
        app.MapDelete("/admin/partnerships/{id:guid}", DeleteAsync);
        app.MapGet("/admin/partnerships/{id:guid}/ending", EndingAsync);
    }

    private static IResult Refused(int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);

    /// <summary>The request's organisation where its administrator asks on a server of several, or why not.</summary>
    private static (Tenant? Here, IResult? Refusal) Administering(HttpContext context)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return (null, Refused(401, "Sign in."));
        }

        if (!current.Authorization.IsAdministrator || current.HomeOrganisation is not null)
        {
            return (null, Refused(403, "Partnerships are this organization's administrator's to make and change."));
        }

        if (!TenantScope.MultiTenant || context.Features.Get<TenantFeature>() is not { } feature)
        {
            return (null, Refused(409, "This server holds one organization, so there is no other to partner with."));
        }

        return (feature.Tenant, null);
    }

    private static NpgsqlDataSource Pool(NpgsqlDataSource root) => TenantDataSources.Route(root);

    private static async Task<List<Row>> ReadAsync(NpgsqlDataSource root, Guid? id, CancellationToken cancellation)
    {
        await using NpgsqlCommand command = Pool(root).CreateCommand(
            """
            select id, inviter, invitee, status, inviter_shares_members, invitee_shares_members, invited_by, invited_at, changed_at,
                   status_at
              from partnership
             where @id::uuid is null or id = @id
             order by invited_at
            """);
        command.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)id ?? DBNull.Value);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        List<Row> rows = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            rows.Add(new Row(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3), reader.GetBoolean(4),
                reader.GetBoolean(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8), reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return rows;
    }

    private static object Describe(Row row, Tenant here, TenantDirectory directory)
    {
        bool invitedByUs = row.Inviter == here.Id;
        Guid other = invitedByUs ? row.Invitee : row.Inviter;
        Tenant? partner = directory.All.FirstOrDefault(t => t.Id == other);

        return new
        {
            id = row.Id,
            organization = new { key = partner?.Key, name = partner?.Name, host = partner is { Hosts.Count: > 0 } ? partner.Hosts[0] : null },
            status = row.Status,
            invitedByUs,
            weShareMembers = invitedByUs ? row.InviterShares : row.InviteeShares,
            theyShareMembers = invitedByUs ? row.InviteeShares : row.InviterShares,
            invitedBy = row.InvitedBy,
            invitedAt = row.InvitedAt,
            changedAt = row.ChangedAt,

            // Since when it has had this status — accepted, suspended, resumed or ended; a change of visibility leaves it.
            statusChangedAt = row.StatusAt,
        };
    }

    private static async Task<IResult> ListAsync(HttpContext context, NpgsqlDataSource root, TenantDirectory directory, CancellationToken cancellation)
    {
        (Tenant? here, IResult? refusal) = Administering(context);

        if (here is null)
        {
            return refusal!;
        }

        List<Row> rows = await ReadAsync(root, null, cancellation).ConfigureAwait(false);
        return Results.Ok(new
        {
            partnerships = rows.Select(r => Describe(r, here, directory)),
            limit = Limit,
            note = "A group limited to partners admits members of an organization only while its partnership is active. "
                + "Ending one removes the other's members from groups limited to partners, in both organizations.",
        });
    }

    private static async Task<IResult> InviteAsync(
        HttpContext context, InviteRequest request, NpgsqlDataSource root, TenantDirectory directory, IAuditLog audit,
        CancellationToken cancellation)
    {
        (Tenant? here, IResult? refusal) = Administering(context);

        if (here is null)
        {
            return refusal!;
        }

        string named = (request.Organization ?? string.Empty).Trim().ToLowerInvariant();

        if (named.Length == 0)
        {
            return Refused(400, "'organization' is required: the other organization's host name, or its key.");
        }

        Tenant? other = directory.All.FirstOrDefault(t =>
            string.Equals(t.Key, named, StringComparison.OrdinalIgnoreCase)
            || t.Hosts.Contains(named, StringComparer.OrdinalIgnoreCase));

        if (other is null || other.Suspended)
        {
            return Refused(404, $"No organization on this server is reached at '{named}'.");
        }

        if (other.Id == here.Id)
        {
            return Refused(400, "An organization does not partner with itself.");
        }

        List<Row> rows = await ReadAsync(root, null, cancellation).ConfigureAwait(false);

        if (rows.FirstOrDefault(r => r.Status != "ended" && (r.Inviter == other.Id || r.Invitee == other.Id)) is { } open)
        {
            return Refused(409, $"There is already a partnership with {other.Name}, {open.Status}.");
        }

        if (rows.Count(r => r.Status != "ended") >= Limit)
        {
            return Refused(409, $"This organization has {Limit} partnerships, the most it may have. End one first.");
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;
        await using NpgsqlCommand command = Pool(root).CreateCommand(
            """
            insert into partnership (inviter, invitee, inviter_shares_members, invited_by)
            values (@here, @other, @shares, @by)
            returning id
            """);
        command.Parameters.AddWithValue("here", here.Id);
        command.Parameters.AddWithValue("other", other.Id);
        command.Parameters.AddWithValue("shares", request.ShareMembers ?? false);
        command.Parameters.AddWithValue("by", current.Principal.Name);
        Guid id;

        try
        {
            id = (Guid)(await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false))!;
        }
        catch (PostgresException taken) when (taken.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The other organisation invited this one at the same moment.
            return Refused(409, $"There is already a partnership with {other.Name}.");
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "partnership.invite", other.Key, AdminEndpoints.Detail(new { partnership = id, shareMembers = request.ShareMembers ?? false }),
            succeeded: true, cancellation).ConfigureAwait(false);

        Row made = (await ReadAsync(root, id, cancellation).ConfigureAwait(false)).Single();
        return Results.Json(Describe(made, here, directory), statusCode: 201);
    }

    private static async Task<IResult> ChangeAsync(
        HttpContext context, Guid id, ChangeRequest request, NpgsqlDataSource root, TenantDirectory directory,
        IAuditLog audit, CancellationToken cancellation)
    {
        (Tenant? here, IResult? refusal) = Administering(context);

        if (here is null)
        {
            return refusal!;
        }

        if ((await ReadAsync(root, id, cancellation).ConfigureAwait(false)).SingleOrDefault() is not { } row)
        {
            return Refused(404, "No such partnership.");
        }

        bool inviter = row.Inviter == here.Id;
        string action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();

        // From, to, and who may: ADR-219 §5.1.
        (string? to, string? why) = action switch
        {
            "" => ((string?)row.Status, (string?)null),
            "accept" when inviter => (null, "Only the invited organization accepts a partnership."),
            "accept" when row.Status != "pending" => (null, $"The partnership is {row.Status}, not waiting to be accepted."),
            "accept" => ("active", null),
            "suspend" when row.Status != "active" => (null, $"Only an active partnership is suspended; this one is {row.Status}."),
            "suspend" => ("suspended", null),
            "resume" when row.Status != "suspended" => (null, $"Only a suspended partnership is resumed; this one is {row.Status}."),
            "resume" => ("active", null),
            "end" when row.Status is not ("active" or "suspended") =>
                (null, row.Status == "pending"
                    ? "A partnership that was never accepted is withdrawn or declined, not ended: DELETE it."
                    : "The partnership has already ended."),
            "end" => ("ended", null),
            _ => (null, $"'{request.Action}' is not a change: accept, suspend, resume or end."),
        };

        if (to is null)
        {
            return Refused(action is "accept" or "suspend" or "resume" or "end" ? 409 : 400, why!);
        }

        if (row.Status == "ended" && request.ShareMembers is not null)
        {
            return Refused(409, "The partnership has ended.");
        }

        if (row.Status == "pending" && inviter is false && action != "accept" && request.ShareMembers is not null)
        {
            return Refused(409, "Accept the partnership to say whether its partner may find your members.");
        }

        // Ending changes both organisations' groups, so it runs on the root pool — with the party named here, since the
        // floor names none there — and in one transaction with the status, so a partnership is never ended with the
        // partner still in its groups.
        bool ending = to == "ended" && row.Status != "ended";
        await using NpgsqlConnection connection = await (ending ? root : Pool(root)).OpenConnectionAsync(cancellation).ConfigureAwait(false);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellation).ConfigureAwait(false);
        await using NpgsqlCommand command = new(
            """
            update partnership
               set status = @to,
                   inviter_shares_members = case when @inviter then coalesce(@shares, inviter_shares_members) else inviter_shares_members end,
                   invitee_shares_members = case when @inviter then invitee_shares_members else coalesce(@shares, invitee_shares_members) end,
                   changed_at = now(),
                   status_at = case when status = @to then status_at else now() end
             where id = @id and status = @from and @here in (inviter, invitee)
            """, connection, transaction);
        command.Parameters.AddWithValue("here", here.Id);
        command.Parameters.AddWithValue("to", to);
        command.Parameters.AddWithValue("inviter", inviter);
        command.Parameters.AddWithValue("shares", NpgsqlTypes.NpgsqlDbType.Boolean, (object?)request.ShareMembers ?? DBNull.Value);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("from", row.Status);

        if (await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false) != 1)
        {
            return Refused(409, "The partnership changed while this was asked. Read it again.");
        }

        (int removed, int demoted) = ending
            ? await EndAsync(connection, transaction, row.Inviter, row.Invitee, cancellation).ConfigureAwait(false)
            : (0, 0);
        await transaction.CommitAsync(cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(
            context, audit, $"partnership.{(action.Length == 0 ? "visibility" : action)}", id.ToString(),
            AdminEndpoints.Detail(new { shareMembers = request.ShareMembers, removed, demoted }),
            succeeded: true, cancellation).ConfigureAwait(false);

        Row changed = (await ReadAsync(root, id, cancellation).ConfigureAwait(false)).Single();
        return Results.Ok(new { partnership = Describe(changed, here, directory), removed, demoted });
    }

    /// <summary>
    /// What ending a partnership does to both organisations' groups — ADR-219 §5.5: the other's members leave groups
    /// limited to partners, and its managers become members in groups that admit any organisation. On the root pool,
    /// because the groups are two organisations', and narrowed to those two.
    /// </summary>
    private static async Task<(int Removed, int Demoted)> EndAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, Guid a, Guid b, CancellationToken cancellation)
    {
        const string Across = """
            sharing_group g, principal p
            where m.group_id = g.id and m.principal_id = p.id
              and ((g.tenant_id = @a and p.tenant_id = @b) or (g.tenant_id = @b and p.tenant_id = @a))
            """;

        await using NpgsqlCommand remove = new(
            $"delete from sharing_group_member m using {Across} and g.admits_partners_only", connection, transaction);
        remove.Parameters.AddWithValue("a", a);
        remove.Parameters.AddWithValue("b", b);
        int removed = await remove.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);

        await using NpgsqlCommand demote = new(
            $"update sharing_group_member m set membership = 'member' from {Across} and m.membership = 'manager'",
            connection, transaction);
        demote.Parameters.AddWithValue("a", a);
        demote.Parameters.AddWithValue("b", b);
        int demoted = await demote.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);

        return (removed, demoted);
    }

    /// <summary>
    /// What ending a partnership would do, changing nothing — ArcGIS Online lists the affected groups before it ends one:
    /// each group, whose it is, and how many of the other organisation's members would leave it or stop managing it.
    /// </summary>
    private static async Task<IResult> EndingAsync(
        HttpContext context, Guid id, NpgsqlDataSource root, TenantDirectory directory, CancellationToken cancellation)
    {
        (Tenant? here, IResult? refusal) = Administering(context);

        if (here is null)
        {
            return refusal!;
        }

        if ((await ReadAsync(root, id, cancellation).ConfigureAwait(false)).SingleOrDefault() is not { } row)
        {
            return Refused(404, "No such partnership.");
        }

        // The same narrowing EndAsync deletes and updates by, read on the root pool for the same reason.
        await using NpgsqlCommand command = root.CreateCommand(
            """
            select g.tenant_id, coalesce(g.title, g.name),
                   count(*) filter (where g.admits_partners_only),
                   count(*) filter (where not g.admits_partners_only and m.membership = 'manager')
              from sharing_group_member m
              join sharing_group g on g.id = m.group_id
              join principal p on p.id = m.principal_id
             where (g.tenant_id = @a and p.tenant_id = @b) or (g.tenant_id = @b and p.tenant_id = @a)
             group by g.tenant_id, coalesce(g.title, g.name)
            having count(*) filter (where g.admits_partners_only) > 0
                or count(*) filter (where not g.admits_partners_only and m.membership = 'manager') > 0
             order by 1, 2
            """);
        command.Parameters.AddWithValue("a", row.Inviter);
        command.Parameters.AddWithValue("b", row.Invitee);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        List<object> groups = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            Guid owner = reader.GetGuid(0);
            groups.Add(new
            {
                title = reader.GetString(1),
                ours = owner == here.Id,
                organization = directory.All.FirstOrDefault(t => t.Id == owner)?.Name,
                removed = reader.GetInt64(2),
                demoted = reader.GetInt64(3),
            });
        }

        return Results.Ok(new { partnership = id, status = row.Status, groups });
    }

    /// <summary>Withdraws or declines a partnership never accepted, or deletes one that has ended.</summary>
    private static async Task<IResult> DeleteAsync(
        HttpContext context, Guid id, NpgsqlDataSource root, IAuditLog audit, CancellationToken cancellation)
    {
        (Tenant? here, IResult? refusal) = Administering(context);

        if (here is null)
        {
            return refusal!;
        }

        if ((await ReadAsync(root, id, cancellation).ConfigureAwait(false)).SingleOrDefault() is not { } row)
        {
            return Refused(404, "No such partnership.");
        }

        if (row.Status is "active" or "suspended")
        {
            return Refused(409, $"The partnership is {row.Status}. End it first; an ended partnership is then deleted.");
        }

        await using NpgsqlCommand command = Pool(root).CreateCommand("delete from partnership where id = @id and status = @status");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", row.Status);

        if (await command.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false) != 1)
        {
            return Refused(409, "The partnership changed while this was asked. Read it again.");
        }

        string act = row.Status == "ended" ? "delete" : row.Inviter == here.Id ? "withdraw" : "decline";
        await AdminEndpoints.AuditAsync(
            context, audit, $"partnership.{act}", id.ToString(), AdminEndpoints.Detail(new { status = row.Status }),
            succeeded: true, cancellation).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// The members of active partners who let this organisation find them, named as this organisation names them —
    /// ADR-219 §5.2 — and not already in the group. On the root pool, because the members are other organisations'.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> PartnerCandidatesAsync(
        NpgsqlDataSource root, Guid here, string group, CancellationToken cancellation)
    {
        await using NpgsqlCommand command = root.CreateCommand(
            """
            select t.key || E'\\' || p.name
              from partnership x
              join tenant t on t.id = case when x.inviter = @here then x.invitee else x.inviter end
              join principal p on p.tenant_id = t.id
             where x.status = 'active' and @here in (x.inviter, x.invitee)
               and case when x.inviter = @here then x.invitee_shares_members else x.inviter_shares_members end
               and p.kind = 'user' and p.disabled_at is null
               and not exists (select 1 from sharing_group g join sharing_group_member m on m.group_id = g.id
                                where g.tenant_id = @here and lower(g.name) = lower(@group) and m.principal_id = p.id)
             order by 1
             limit 1000
            """);
        command.Parameters.AddWithValue("here", here);
        command.Parameters.AddWithValue("group", group);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);
        List<string> names = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
