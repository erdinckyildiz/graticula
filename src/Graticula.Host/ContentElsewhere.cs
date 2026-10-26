using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// What a member may read in other organisations on this server, found from home — ADR-220 §5.4, ArcGIS Online's search
/// beyond *My organization*: items shared with every organisation, public items, and items shared into groups of other
/// organisations the member is in. Each is opened at its own organisation's host, where the member reads as an outsider.
/// </summary>
/// <remarks>
/// <b>One narrow statement on the root pool</b>, because the rows are other organisations'. It reads items only, never
/// their data, and returns only what the reading rule (<c>LayerAccess.Evaluate</c>) would let an outsider read there:
/// the levels <c>allorganizations</c> and <c>public</c>, and groups that admit other organisations and hold the member.
/// </remarks>
internal static class ContentElsewhereEndpoints
{
    /// <summary>At most this many answers to one search.</summary>
    internal const int Limit = 200;

    public static void Map(WebApplication app) => app.MapGet("/admin/search/elsewhere", SearchAsync);

    private static IResult Refused(int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);

    private static async Task<IResult> SearchAsync(
        HttpContext context, string? q, NpgsqlDataSource root, TenantDirectory directory, CancellationToken cancellation)
    {
        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        if (current.Principal.IsAnonymous)
        {
            return Refused(401, "Sign in.");
        }

        // From home only: at another organisation's host the member is already elsewhere.
        if (!TenantScope.MultiTenant || current.HomeOrganisation is not null
            || context.Features.Get<TenantFeature>() is not { } here)
        {
            return Results.Ok(new { results = Array.Empty<object>(), total = 0, limit = Limit });
        }

        string needle = (q ?? string.Empty).Trim();
        await using NpgsqlCommand command = root.CreateCommand(
            """
            select i.id, i.kind, i.title, i.snippet, i.sharing, i.tenant_id, o.name,
                   exists (select 1 from item_group ig
                             join sharing_group g on g.id = ig.group_id and g.admits_other_tenants
                             join sharing_group_member m on m.group_id = g.id and m.principal_id = @me
                            where ig.item_id = i.id) as through_group
              from item i
              left join principal o on o.id = i.owner_principal_id
             where i.tenant_id <> @here
               and (i.sharing in ('allorganizations', 'public')
                    or exists (select 1 from item_group ig
                                 join sharing_group g on g.id = ig.group_id and g.admits_other_tenants
                                 join sharing_group_member m on m.group_id = g.id and m.principal_id = @me
                                where ig.item_id = i.id))
               and (@q = ''
                    or i.title ilike '%' || @q || '%'
                    or coalesce(i.snippet, '') ilike '%' || @q || '%'
                    or exists (select 1 from unnest(i.tags) t where t ilike '%' || @q || '%'))
             order by lower(i.title)
             limit @limit
            """);
        command.Parameters.AddWithValue("here", here.Tenant.Id);
        command.Parameters.AddWithValue("me", current.Principal.Id);
        command.Parameters.AddWithValue("q", needle);
        command.Parameters.AddWithValue("limit", Limit + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        string port = context.Request.Host.Port is { } p ? $":{p}" : string.Empty;
        List<object> results = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            // A suspended organisation is not opened to anybody, so nothing of it is offered.
            if (directory.All.FirstOrDefault(t => t.Id == reader.GetGuid(5)) is not { Suspended: false } owner)
            {
                continue;
            }

            Guid id = reader.GetGuid(0);
            results.Add(new
            {
                id = id.ToString("N"),
                kind = reader.GetString(1),
                title = reader.GetString(2),
                snippet = reader.IsDBNull(3) ? null : reader.GetString(3),
                sharing = reader.GetString(4),
                throughGroup = reader.GetBoolean(7),
                owner = reader.IsDBNull(6) ? null : reader.GetString(6),
                organization = new { key = owner.Key, name = owner.Name },
                url = owner.Hosts.Count == 0
                    ? null
                    : $"{context.Request.Scheme}://{owner.Hosts[0]}{port}/studio/item/{id:N}",
            });
        }

        bool more = results.Count > Limit;
        return Results.Ok(new
        {
            results = results.Take(Limit),
            total = Math.Min(results.Count, Limit),
            more,
            limit = Limit,
            note = "Items of other organizations on this server that you may read: those shared with all organizations, "
                + "public ones, and those shared into their groups you are in. Each opens at its organization's address.",
        });
    }
}
