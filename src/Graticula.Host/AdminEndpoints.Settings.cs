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

namespace Graticula.Host;

/// <summary>What the server's settings are set to.</summary>
/// <param name="PageSize">
/// The server's page size, or null to go back to the configured one — ADR-084. A service that set its own
/// keeps it.
/// </param>
internal sealed record ServerSettingsRequest(int? PageSize);

/// <summary>The server's map ground — Q-110, ADR-086.</summary>
/// <param name="Services">Tile services as <c>folder/name</c>, bottom first; empty or null for OpenStreetMap.</param>
internal sealed record ServerGroundRequest(IReadOnlyList<string>? Services);

/// <summary>
/// The server's own settings — V-70, ADR-084: set for the whole server from the console, not per service.
/// </summary>
/// <remarks>
/// In its own file for the reason <c>AdminEndpoints.FieldOverrides.cs</c> gives.
/// </remarks>
internal static partial class AdminEndpoints
{
    private static void MapServerSettings(WebApplication app)
    {
        app.MapGet("/admin/settings", ServerSettingsAsync);
        app.MapPut("/admin/settings", SetServerSettingsAsync);
        app.MapPut("/admin/settings/ground", SetServerGroundAsync);
    }

    /// <summary>The server's settings, each with where its value came from.</summary>
    /// <remarks>
    /// <b>Where it came from is half the answer.</b> A page size of 1000 reads the same whether an operator
    /// chose it or nobody did, and the screen has to say which, because *reset* means something only for the
    /// first.
    /// </remarks>
    private static async Task ServerSettingsAsync(
        HttpContext context,
        ServerPageSize pageSize,
        ServerGround ground,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        ServerPageSize.Reading reading = await pageSize.ReadAsync(cancellation).ConfigureAwait(false);
        ServerGround.Reading chosen = await ground.ReadAsync(cancellation).ConfigureAwait(false);

        await Results.Json(Describe(reading, await DescribeAsync(chosen, catalog, cancellation).ConfigureAwait(false)))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Stores the server's map ground, or clears it so OpenStreetMap's tiles are drawn again.</summary>
    /// <remarks>
    /// <b>Its own route rather than a field beside the page size</b>, because there a missing field and a
    /// null field both have to mean something, and for the page size null already means *go back to the
    /// configured one*. Two settings, two doors, and neither Save can undo the other.
    /// </remarks>
    private static async Task SetServerGroundAsync(
        HttpContext context,
        ServerGroundRequest request,
        ServerGround ground,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        IReadOnlyList<string> asked = request.Services ?? [];

        (string? refusal, IReadOnlyList<string> names) =
            await ground.CheckAsync(asked, cancellation).ConfigureAwait(false);

        if (refusal is not null)
        {
            await AuditAsync(
                context, audit, "server.settings", ServerGround.Name,
                Detail(new { services = asked }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        IReadOnlyList<string> before = await ground
            .SetAsync(names, current?.Principal.Id, cancellation)
            .ConfigureAwait(false);

        await AuditAsync(
            context, audit, "server.settings", ServerGround.Name,
            Detail(new { from = before, to = names }),
            succeeded: true, cancellation).ConfigureAwait(false);

        ServerGround.Reading now = await ground.ReadAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new { ground = await DescribeAsync(now, catalog, cancellation).ConfigureAwait(false) })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The ground, each service with its sharing and whether it runs, and every service that could be.</summary>
    /// <remarks>
    /// <para>
    /// <b>Who will not see it is the screen's warning to give.</b> A ground shared with a group is drawn for
    /// that group and left out for everybody else, which is a legitimate choice and a surprising one; the
    /// screen can only say so if it is told. Sharing and running are two facts, because the fix for each is
    /// different — the design review of 2026-09-24 found one flag sending an operator to change the sharing of
    /// a service that was stopped.
    /// </para>
    /// <para>
    /// <b>The candidates come from here, not from the services directory.</b> The directory answers what an
    /// anonymous caller may read, and a service is published private by default, so the list the screen built
    /// from it left out exactly the extract an operator had just imported — found in the same review.
    /// </para>
    /// </remarks>
    private static async Task<object> DescribeAsync(
        ServerGround.Reading reading,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        CancellationToken cancellation)
    {
        IReadOnlyList<Graticula.Platform.Catalog.PublishedService> all =
            (await catalog.ListServicesAsync(cancellation).ConfigureAwait(false)).Services ?? [];

        static object Row(string name, Graticula.Platform.Catalog.PublishedService? service) => new
        {
            name,
            exists = service is not null,
            sharing = service?.Sharing.ToString().ToLowerInvariant(),
            running = service?.IsRunning ?? false,
            everybody = service is { Sharing: SharingScope.Public },
        };

        List<object> services = [];

        foreach (string qualified in reading.Services)
        {
            services.Add(Row(
                qualified,
                all.FirstOrDefault(s => string.Equals(s.QualifiedName, qualified, StringComparison.OrdinalIgnoreCase))));
        }

        object[] candidates = all
            .Where(s => s.Layers.Count > 0 && s.Limits.AllowsTiles(dataSupportsIt: true))
            .OrderBy(s => s.QualifiedName, StringComparer.OrdinalIgnoreCase)
            .Select(s => Row(s.QualifiedName, s))
            .ToArray();

        return new { services, candidates, most = ServerGround.MostServices, changedAt = reading.ChangedAt };
    }

    /// <summary>Stores the server's settings.</summary>
    /// <remarks>
    /// <b><c>admin:manageServer</c></b>, as a service's own page size is: how much one query may carry is
    /// server administration, and the privilege that sets it for one service sets it for all.
    /// </remarks>
    private static async Task SetServerSettingsAsync(
        HttpContext context,
        ServerSettingsRequest request,
        ServerPageSize pageSize,
        ServerGround ground,
        Graticula.Platform.Postgres.CatalogFallback catalog,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        if (request.PageSize is { } asked && pageSize.Refusal(asked) is { } refusal)
        {
            await AuditAsync(
                context, audit, "server.settings", ServerPageSize.Name,
                Detail(new { pageSize = asked }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, refusal).ConfigureAwait(false);
            return;
        }

        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        (StoredSetting? before, ServerPageSize.Reading now) = await pageSize
            .SetAsync(request.PageSize, current?.Principal.Id, cancellation)
            .ConfigureAwait(false);

        await AuditAsync(
            context, audit, "server.settings", ServerPageSize.Name,
            Detail(new
            {
                from = before is null ? null : before.Value,
                to = request.PageSize?.ToString(CultureInfo.InvariantCulture),
                inForce = now.Value,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        ServerGround.Reading chosen = await ground.ReadAsync(cancellation).ConfigureAwait(false);

        await Results.Json(Describe(now, await DescribeAsync(chosen, catalog, cancellation).ConfigureAwait(false)))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static object Describe(ServerPageSize.Reading reading, object ground) => new
    {
        ground,
        pageSize = new
        {
            value = reading.Value,

            // `stored` when an operator set it here, `configured` when it is the deployment's
            // Graticula:DefaultRecordCount or ArcGIS's 1000.
            source = reading.Stored is null ? "configured" : "stored",
            stored = reading.Stored,
            configured = reading.Fallback,
            ceiling = reading.Ceiling,
            changedAt = reading.ChangedAt,

            // A stored value the ceiling has since come down under is applied as the ceiling, and said so.
            clamped = reading.Stored is { } stored && stored != reading.Value,
        },
    };
}
