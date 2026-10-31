using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Graticula.Host;

/// <summary>
/// The site's machines, read and set — ADR-012 §5 phase 0 item 6: <c>/admin/site/machines</c>, after ArcGIS Server
/// Manager's Machines page and the administrator API's <c>machines</c> resource (§1c).
/// </summary>
/// <remarks>
/// <b>The system's</b>, as a machine of an ArcGIS site is the site's and no organisation's: on
/// <see cref="TenantRouting.SystemOnly"/>, and behind <see cref="Privilege.AdminManageServer"/>.
/// </remarks>
internal static class SiteEndpoints
{
    /// <summary>Registers the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/admin/site/machines", ListAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapPut("/admin/site/machines/{name}", SetAsync).Governed(SharingGovernedExtensions.ByPrivilege);
        app.MapDelete("/admin/site/machines/{name}", RemoveAsync).Governed(SharingGovernedExtensions.ByPrivilege);
    }

    /// <summary>A machine's configured state, as an administrator sets it.</summary>
    /// <param name="State">started, stopped or maintenance.</param>
    internal sealed record StateRequest(string? State);

    private static async Task<IResult> ListAsync(
        HttpContext context, NpgsqlDataSource store, SiteNode node, SiteSingletons singletons, SiteAnnouncements site,
        IConfiguration configuration, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        IReadOnlyList<Machine> machines;

        try
        {
            machines = await SiteNodes.ReadAsync(store, cancellation).ConfigureAwait(false);
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            machines = [];
        }

        IReadOnlyList<(string Node, bool Seen)> checkedDirectories = node.Registered
            ? await node.CheckDirectoriesAsync(cancellation).ConfigureAwait(false)
            : [];

        return Results.Ok(new
        {
            thisMachine = node.Name,
            // ADR-012 phase 1: where the server directories are, and whether this machine sees every other's check file.
            directories = new
            {
                shared = node.SharedDirectories is not null,
                path = node.SharedDirectories,
                seen = checkedDirectories.Where(c => c.Seen).Select(c => c.Node),
                unseen = checkedDirectories.Where(c => !c.Seen).Select(c => c.Node),
            },
            nodesSetting = SiteNodes.Configured(configuration),
            listening = site.Listening,
            sweeps = SiteSingletons.Names,
            machines = machines.Select(m => new
            {
                name = m.Name,
                address = m.Address,
                version = m.Version,
                startedAt = m.StartedAt,
                lastSeen = m.LastSeen,
                status = m.Up ? "up" : "down",
                configuredState = m.ConfiguredState,
                ready = m.Up && m.Ready,
                leads = m.Leads,
                self = m.Name == node.Name,
            }),
        });
    }

    private static async Task<IResult> SetAsync(
        HttpContext context, string name, StateRequest request, NpgsqlDataSource store, SiteNode node, SiteAnnouncements site,
        IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        string? state = request.State?.Trim().ToLowerInvariant();

        if (state is null || !SiteNode.States.Contains(state))
        {
            return Refused(400, $"'state' is one of {string.Join(", ", SiteNode.States)}.");
        }

        if (!await SiteNode.SetStateAsync(store, name, state, cancellation).ConfigureAwait(false))
        {
            return Refused(404, $"No machine '{name}' in this site.");
        }

        if (name == node.Name)
        {
            await node.ApplyAsync(state).ConfigureAwait(false);
        }
        else
        {
            // At once rather than at that node's next heartbeat.
            site.Announce(SiteAnnouncements.Machine, name);
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "site.machine", name, AdminEndpoints.Detail(new { state }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        return Results.Ok(new { name, configuredState = state });
    }

    private static async Task<IResult> RemoveAsync(
        HttpContext context, string name, NpgsqlDataSource store, IAuditLog audit, CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return Results.Empty;
        }

        Machine? found = (await SiteNodes.ReadAsync(store, cancellation).ConfigureAwait(false)).FirstOrDefault(m => m.Name == name);

        if (found is null)
        {
            return Refused(404, $"No machine '{name}' in this site.");
        }

        // ArcGIS unregisters a machine that is gone; one that still answers would write itself back at its next heartbeat.
        if (found.Up)
        {
            return Refused(409,
                $"'{name}' is up — it wrote its heartbeat {(DateTimeOffset.UtcNow - found.LastSeen).TotalSeconds:0} s ago. Stop its "
                + "process first; a machine that is still running writes itself back into the site within ten seconds.");
        }

        await using NpgsqlCommand remove = store.CreateCommand("delete from site_node where name = @name");
        remove.Parameters.AddWithValue("name", name);
        await remove.ExecuteNonQueryAsync(cancellation).ConfigureAwait(false);

        await AdminEndpoints.AuditAsync(context, audit, "site.machine.remove", name, AdminEndpoints.Detail(new { removed = true }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        return Results.Ok(new { name, removed = true });
    }

    private static IResult Refused(int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status);
}

/// <summary>A machine of the site as its row says.</summary>
internal sealed record Machine(
    string Name, string Address, string Version, DateTimeOffset StartedAt, DateTimeOffset LastSeen, bool Up,
    string ConfiguredState, bool Ready, IReadOnlyList<string> Leads);

/// <summary>Reading the site's machines — ADR-012.</summary>
internal static class SiteNodes
{
    /// <summary>The nodes an operator says the site has: <c>Graticula:Nodes</c>, one when unset.</summary>
    public static int Configured(IConfiguration configuration) =>
        int.TryParse(configuration["Graticula:Nodes"] ?? configuration["GisServer:Nodes"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
        && n > 0
            ? n
            : 1;

    /// <summary>Every machine, the ones that are up first.</summary>
    public static async Task<IReadOnlyList<Machine>> ReadAsync(NpgsqlDataSource store, CancellationToken cancellation)
    {
        await using NpgsqlCommand read = store.CreateCommand(
            """
            select name, address, version, started_at, last_seen, last_seen > now() - @silence, configured_state, ready, leads
              from site_node
             order by last_seen > now() - @silence desc, name
            """);
        read.Parameters.AddWithValue("silence", SiteNode.Silence);
        await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

        List<Machine> machines = [];

        while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
        {
            machines.Add(new Machine(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetFieldValue<DateTimeOffset>(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetBoolean(5),
                reader.GetString(6), reader.GetBoolean(7), reader.GetFieldValue<string[]>(8)));
        }

        return machines;
    }

    /// <summary>
    /// The nodes the connection budget is sized for: the larger of <c>Graticula:Nodes</c> and the nodes up now, this
    /// one included — so a site that has grown past its setting is still told whether it fits (ADR-012 item 3).
    /// </summary>
    public static async Task<int> CountForBudgetAsync(NpgsqlDataSource store, IConfiguration configuration, CancellationToken cancellation)
    {
        int configured = Configured(configuration);

        try
        {
            await using NpgsqlCommand count = store.CreateCommand(
                "select count(*) from site_node where last_seen > now() - @silence");
            count.Parameters.AddWithValue("silence", SiteNode.Silence);
            int up = Convert.ToInt32(await count.ExecuteScalarAsync(cancellation).ConfigureAwait(false), CultureInfo.InvariantCulture);

            // This node has not written itself yet when the budget is compared, so it is one more than the rows say.
            return Math.Max(configured, up + 1);
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            return configured;
        }
    }

    /// <summary>
    /// The data sources that serve a pinned service: each node keeps a connection open to each (ADR-218), outside the
    /// pools the ceiling already counts.
    /// </summary>
    public static async Task<int> PinnedSourcesAsync(NpgsqlDataSource store, CancellationToken cancellation)
    {
        try
        {
            await using NpgsqlCommand count = store.CreateCommand(
                "select count(distinct l.data_source_id) from layer l join service s on s.id = l.service_id where s.pinned");
            return Convert.ToInt32(await count.ExecuteScalarAsync(cancellation).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        catch (PostgresException missing) when (missing.SqlState is "42P01" or "42703")
        {
            return 0;
        }
    }

    /// <summary>On how many of the site's up nodes a pinned service was warmed in the last minute, and which were not.</summary>
    public static async Task<(int Warm, int Of, IReadOnlyList<string> Cold)> WarmOnAsync(
        NpgsqlDataSource store, Guid service, CancellationToken cancellation)
    {
        try
        {
            await using NpgsqlCommand read = store.CreateCommand(
                """
                select n.name, exists (select 1 from site_node_warm w
                                        where w.node = n.name and w.service_id = @service and w.warmed_at > now() - interval '1 minute')
                  from site_node n
                 where n.last_seen > now() - @silence
                 order by n.name
                """);
            read.Parameters.AddWithValue("service", service);
            read.Parameters.AddWithValue("silence", SiteNode.Silence);
            await using NpgsqlDataReader reader = await read.ExecuteReaderAsync(cancellation).ConfigureAwait(false);

            int warm = 0, of = 0;
            List<string> cold = [];

            while (await reader.ReadAsync(cancellation).ConfigureAwait(false))
            {
                of++;

                if (reader.GetBoolean(1))
                {
                    warm++;
                }
                else
                {
                    cold.Add(reader.GetString(0));
                }
            }

            return (warm, of, cold);
        }
        catch (PostgresException missing) when (missing.SqlState == "42P01")
        {
            return (0, 0, []);
        }
    }
}
