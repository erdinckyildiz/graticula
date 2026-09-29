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

/// <summary>The external origins a style may fetch from — ADR-094.</summary>
/// <param name="Origins">Each as <c>https://host</c>, <c>https://host:port</c> or <c>https://*.host</c>; empty or null for none.</param>
internal sealed record StyleOriginsRequest(IReadOnlyList<string>? Origins);

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
        app.MapGet("/admin/settings/style-origins", StyleOriginsAsync);
        app.MapPut("/admin/settings/style-origins", SetStyleOriginsAsync);
    }

    /// <summary>The origins styles may fetch from, and what that means.</summary>
    /// <remarks>
    /// <b><c>admin:manageServer</c>, not the publisher's privilege.</b> ADR-094's whole argument is that
    /// which hosts every viewer's browser may be sent to is not the style author's decision; a route the
    /// author could call would make it theirs again.
    /// </remarks>
    private static async Task StyleOriginsAsync(
        HttpContext context,
        StyleOriginList origins,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        StyleOriginList.Reading reading = await origins.ReadAsync(cancellation).ConfigureAwait(false);

        await Results.Json(DescribeOrigins(reading, removed: [])).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Replaces the list of origins styles may fetch from, or empties it.</summary>
    /// <remarks>
    /// <para>
    /// <b>The whole list, replaced, rather than one origin added or removed.</b> The screen shows the list
    /// and saves it, as the ground is saved; two routes for add and remove would be two places for the
    /// normalisation to differ.
    /// </para>
    /// <para>
    /// <b>A removal is not refused while a stored style names the origin.</b> The owner's rule is that such
    /// a style stops being served — the generated one is, and its read-back says why — which is ADR-028
    /// condition 3's answer to a style that no longer fits, applied to one more reason. Refusing instead
    /// would make an administrator unable to withdraw trust from a host without first editing every
    /// publisher's styles.
    /// </para>
    /// </remarks>
    private static async Task SetStyleOriginsAsync(
        HttpContext context,
        StyleOriginsRequest request,
        StyleOriginList origins,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await Authorize.RequireAsync(context, Privilege.AdminManageServer).ConfigureAwait(false))
        {
            return;
        }

        if (!Graticula.Api.ArcGis.StyleOrigins.TryParseList(
                request.Origins, out IReadOnlyList<Graticula.Api.ArcGis.StyleOrigin> parsed, out string? refusal))
        {
            await AuditAsync(
                context, audit, "server.settings", StyleOriginList.Name,
                Detail(new { origins = request.Origins }), succeeded: false, cancellation).ConfigureAwait(false);

            await Refuse(context, 400, refusal!).ConfigureAwait(false);
            return;
        }

        RequestPrincipal? current = context.Features.Get<RequestPrincipal>();

        IReadOnlyList<Graticula.Api.ArcGis.StyleOrigin> before =
            await origins.SetAsync(parsed, current?.Principal.Id, cancellation).ConfigureAwait(false);

        string[] from = [.. before.Select(o => o.Text)];
        string[] to = [.. parsed.Select(o => o.Text)];

        await AuditAsync(
            context, audit, "server.settings", StyleOriginList.Name,
            Detail(new { from, to }), succeeded: true, cancellation).ConfigureAwait(false);

        StyleOriginList.Reading now = await origins.ReadAsync(cancellation).ConfigureAwait(false);

        await Results.Json(DescribeOrigins(now, [.. from.Except(to, StringComparer.Ordinal)]))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static object DescribeOrigins(StyleOriginList.Reading reading, IReadOnlyList<string> removed) => new
    {
        origins = reading.Origins.Select(o => o.Text),
        most = Graticula.Api.ArcGis.StyleOrigins.MostOrigins,
        changedAt = reading.ChangedAt,
        removed,
        note = reading.Origins.Count == 0
            ? "No external origin is allowed: a style may name only this server's own resources."
            : "A style may fetch from these origins as well as from this server, and the Server and Studio pages "
              + "allow them in their security policy so their maps draw those styles too.",
        removedNote = removed.Count == 0
            ? null
            : "A stored style that names a removed origin is no longer served: the generated style is, within "
              + "thirty seconds on every node, and the style's read-back says why.",
    };

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
            .Where(s => ServiceFaces.Tileable(s) && s.Limits.AllowsTiles(dataSupportsIt: true)

                // ADR-096: a ground is drawn beneath Web Mercator maps (ServerGround.CheckAsync says why).
                && s.TileScheme.IsWebMercator)
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
