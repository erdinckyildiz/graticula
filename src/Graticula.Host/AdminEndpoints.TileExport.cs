using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Graticula.Host;

/// <summary>
/// Exporting a vector tile service's tiles as a package, and whether its readers may — ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside the seed, under the service, addressed by folder and name</b> (<see cref="MapTileSeed"/>, D-275), with the
/// seed's permission: <c>content:publishTiles</c> for all of it, whose service it is (ADR-075) to start, delete or
/// change the policy, and that the caller may read the service to see anything.
/// </para>
/// <para>
/// <b>The owner exports whatever the policy says.</b> The policy governs ArcGIS's <c>exportTiles</c> — what a
/// service's readers may take away — and an owner or administrator on this surface may already read every tile; so an
/// export started here is not refused because <c>exportTilesAllowed</c> is off, which is also how an owner tries the
/// feature before offering it (ADR-098 §5.5).
/// </para>
/// </remarks>
internal static partial class AdminEndpoints
{
    private static readonly string[] BothFormats = ["vtpk", "pmtiles"];

    private static readonly string[] VtpkOnly = ["vtpk"];

    private static void MapTileExport(WebApplication app)
    {
        app.MapGet("/admin/services/{name}/exports", ListExportsAsync);
        app.MapPost("/admin/services/{name}/exports", StartExportAsync);
        app.MapPut("/admin/services/{name}/exports/policy", SetExportPolicyAsync);
        app.MapGet("/admin/services/{name}/exports/{id:guid}", GetExportAsync);
        app.MapDelete("/admin/services/{name}/exports/{id:guid}", DeleteExportAsync);
        app.MapGet("/admin/services/{name}/exports/{id:guid}/download", DownloadExportAsync);
    }

    /// <summary>What an export is asked for.</summary>
    /// <param name="Format"><c>vtpk</c> or <c>pmtiles</c>.</param>
    /// <param name="MinZoom">The lowest level, when a run of levels is meant.</param>
    /// <param name="MaxZoom">The highest level.</param>
    /// <param name="Levels">The levels as a list — wins over the pair above.</param>
    /// <param name="Extent">The area, or null for the whole service.</param>
    internal sealed record ExportRequest(
        string? Format, int? MinZoom, int? MaxZoom, IReadOnlyList<int>? Levels, SeedExtent? Extent);

    /// <summary>A service's export policy on the wire.</summary>
    /// <param name="Allowed">Whether <c>exportTiles</c> is offered.</param>
    /// <param name="Anonymous">Whether a caller who is not signed in may use it.</param>
    /// <param name="MaxTiles">The most tiles one export may hold, or null for the server's ceiling.</param>
    internal sealed record ExportPolicyRequest(
        bool Allowed,
        bool? Anonymous,
        [property: JsonPropertyName("maxExportTilesCount")] int? MaxTiles);

    /// <summary>The service's exports, its policy, and what the server allows — the console's one read.</summary>
    private static async Task ListExportsAsync(
        HttpContext context,
        string name,
        string? folder,
        PostgresLayerCatalog owners,
        ITileExportStore exports,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await ReadableSeedServiceAsync(context, owners, name, folder, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        IReadOnlyList<TileExportState> found = await exports.ListAsync(service.Id, 20, cancellation).ConfigureAwait(false);
        long held = await exports.HeldBytesAsync(cancellation).ConfigureAwait(false);
        (int Min, int Max)? defaults = DefaultSeedLevels(service);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            tilingScheme = new { id = service.TileScheme.Id, wkid = service.TileScheme.Srid, levels = service.TileScheme.LevelCount },
            formats = service.TileScheme.IsWebMercator ? BothFormats : VtpkOnly,
            policy = Wire(service.Limits.Export, settings),
            exportTilesAllowed = service.Limits.AllowsTileExport(dataSupportsIt: true),
            defaults = defaults is { } d ? new { minZoom = d.Min, maxZoom = d.Max } : null,
            cap = service.Limits.Export.MaximumOf(settings.TileExportMaximumTiles),
            budget = new
            {
                bytes = settings.TileExportBudgetBytes,
                heldBytes = held,
                freeBytes = Math.Max(0, settings.TileExportBudgetBytes - held),
                retentionHours = settings.TileExportRetentionHours,
                setting = "Graticula:TileExportBudgetMB",
            },
            exports = found.Select(export => Wire(export, service)),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Sets whether the service's readers may export it through ArcGIS's <c>exportTiles</c> — off for every service
    /// until this is called (ADR-098 §5.5).
    /// </summary>
    private static async Task SetExportPolicyAsync(
        HttpContext context,
        string name,
        string? folder,
        ExportPolicyRequest? request,
        PostgresLayerCatalog owners,
        ITileExportStore exports,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await owners.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        if (!await ManagesServiceAsync(
                context, owners, service.Folder, service.Name, "change who may export the tiles of", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        if (request is null)
        {
            await Refuse(context, 400,
                "Send {\"allowed\": true|false, \"anonymous\": true|false, \"maxExportTilesCount\": n or null}.")
                .ConfigureAwait(false);
            return;
        }

        TileExportPolicy policy = new(request.Allowed, request.Anonymous ?? false, request.MaxTiles);

        if (policy.Problem() is { } problem)
        {
            await Refuse(context, 400, problem).ConfigureAwait(false);
            return;
        }

        TileExportPolicy? before = await exports.SetPolicyAsync(service.Id, policy, cancellation).ConfigureAwait(false);

        if (before is null)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.tiles.export.policy", service.QualifiedName,
            Detail(new { from = Wire(before, settings), to = Wire(policy, settings) }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            policy = Wire(policy, settings),
            note = policy.Allowed
                ? "The service document now says exportTilesAllowed: true to every caller who may read it"
                  + (policy.Anonymous ? ", signed in or not" : " and is signed in")
                  + ", and ArcGIS clients may take its tiles offline. A caller still needs to be able to read the service."
                : "The service document says exportTilesAllowed: false, and exportTiles is refused. Packages already "
                  + "written can no longer be fetched through the ArcGIS address.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Starts an export, or with <c>?dryRun=true</c> counts and estimates one.</summary>
    private static async Task StartExportAsync(
        HttpContext context,
        string name,
        string? folder,
        ExportRequest? request,
        PostgresLayerCatalog owners,
        ITileExportStore exports,
        ServiceContexts contexts,
        IProjector projector,
        ITileCache cache,
        GeoParquetSources geoParquet,
        JobSignal signal,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await owners.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        if (!await ManagesServiceAsync(
                context, owners, service.Folder, service.Name, "export the tiles of", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        TileExportFormat format;

        switch (request?.Format?.Trim().ToLowerInvariant())
        {
            case null or "" or "vtpk":
                format = TileExportFormat.Vtpk;
                break;
            case "pmtiles":
                format = TileExportFormat.PmTiles;
                break;
            default:
                await Refuse(context, 400, $"'{request!.Format}' is not a package format here: vtpk or pmtiles.")
                    .ConfigureAwait(false);
                return;
        }

        IReadOnlyList<int>? levels = request?.Levels is { Count: > 0 } list
            ? [.. list.Distinct().Order()]
            : request?.MinZoom is { } min
                ? [.. Enumerable.Range(min, Math.Max(0, (request.MaxZoom ?? min) - min) + 1)]
                : null;

        if (request?.MinZoom is { } lo && request.MaxZoom is { } hi && hi < lo)
        {
            await Refuse(context, 400, $"'minZoom' {lo} is above 'maxZoom' {hi}.").ConfigureAwait(false);
            return;
        }

        VectorTileExportEndpoints.Ask ask = new(format, levels, request?.Extent, "admin");

        (VectorTileExportEndpoints.Planned? planned, VectorTileExportEndpoints.Refusal? refused) =
            await VectorTileExportEndpoints.PlanAsync(service, ask, settings, contexts, projector, cache, geoParquet, cancellation)
                .ConfigureAwait(false);

        if (planned is null)
        {
            await RefuseExport(context, refused!).ConfigureAwait(false);
            return;
        }

        bool dryRun = string.Equals(context.Request.Query["dryRun"], "true", StringComparison.OrdinalIgnoreCase);

        if (dryRun)
        {
            long held = await exports.HeldBytesAsync(cancellation).ConfigureAwait(false);

            await Results.Json(new
            {
                name = service.Name,
                folder = service.Folder,
                format = PostgresTileExportStore.Wire(format),
                levels = planned.Plan.Levels.Select(level => new
                {
                    zoom = level.Z,
                    tiles = level.Count,
                    drawn = service.Layers.Any(layer => service.TileScheme.Draws(layer.VisibleRange, level.Z)),
                }),
                area = Wire(planned.Plan.Area, service.TileScheme.IsWebMercator ? VectorTileEndpoints.WebMercator : service.TileScheme.Srid),
                whole = planned.Whole,
                tiles = planned.Plan.Total,
                cap = planned.Cap,
                estimatedBytes = planned.EstimatedBytes,
                sampled = planned.Sampled,
                budget = settings.TileExportBudgetBytes,
                heldBytes = held,
                fits = held + planned.EstimatedBytes <= settings.TileExportBudgetBytes,
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        (TileExportState? started, VectorTileExportEndpoints.Refusal? full) = await VectorTileExportEndpoints.StartAsync(
            exports, current.Principal.Id, service, ask, planned, settings, signal, cancellation).ConfigureAwait(false);

        if (started is null)
        {
            await RefuseExport(context, full!).ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.tiles.export", service.QualifiedName,
            Detail(new
            {
                job = started.Job.Id,
                origin = "admin",
                format = PostgresTileExportStore.Wire(format),
                levels = started.Levels,
                tiles = started.Total,
                estimatedBytes = started.EstimatedBytes,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        string watch = ExportAddress(service, started.Job.Id);
        context.Response.Headers.Location = watch;

        await Results.Json(
            new
            {
                job = started.Job.Id,
                status = "pending",
                watch,
                jobStatus = $"/admin/jobs/{started.Job.Id}",
                export = Wire(started, service),
                note = "The export runs on a job worker, lowest level first, taking each tile from the cache or building it "
                    + "as a request would, and then writes the package. It is kept for "
                    + $"{settings.TileExportRetentionHours} hours once written.",
            },
            statusCode: 202).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One export.</summary>
    private static async Task GetExportAsync(
        HttpContext context,
        string name,
        Guid id,
        string? folder,
        PostgresLayerCatalog owners,
        ITileExportStore exports,
        CancellationToken cancellation)
    {
        if (await ReadableSeedServiceAsync(context, owners, name, folder, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        if (await exports.FindAsync(id, cancellation).ConfigureAwait(false) is not { } export || export.ServiceId != service.Id)
        {
            await Refuse(context, 404, $"No export '{id}' of '{service.QualifiedName}'.").ConfigureAwait(false);
            return;
        }

        await Results.Json(Wire(export, service)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Cancels an export that is running and deletes its package, whatever state it is in.</summary>
    private static async Task DeleteExportAsync(
        HttpContext context,
        string name,
        Guid id,
        string? folder,
        PostgresLayerCatalog owners,
        ITileExportStore exports,
        TileExporter exporter,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishTiles).ConfigureAwait(false))
        {
            return;
        }

        string? at = FolderOf(folder);

        if (await owners.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            await Refuse(context, 404, NoService(name, at)).ConfigureAwait(false);
            return;
        }

        if (!await ManagesServiceAsync(
                context, owners, service.Folder, service.Name, "delete a tile export of", cancellation)
                .ConfigureAwait(false))
        {
            return;
        }

        if (await exports.FindAsync(id, cancellation).ConfigureAwait(false) is not { } export || export.ServiceId != service.Id)
        {
            await Refuse(context, 404, $"No export '{id}' of '{service.QualifiedName}'.").ConfigureAwait(false);
            return;
        }

        bool cancelled = await exports.CancelAsync(id, cancellation).ConfigureAwait(false);

        if (cancelled)
        {
            exporter.Stop(id);
        }

        // <b>The file goes now, and is marked gone only once it is.</b> A download reading it holds it open on some
        // systems; the sweep then deletes it on its next pass and marks it.
        bool gone = exporter.DeleteFiles(export.Token);

        if (gone)
        {
            await exports.MarkRemovedAsync(id, cancellation).ConfigureAwait(false);
        }

        await AuditAsync(
            context, audit, "service.tiles.export.delete", service.QualifiedName,
            Detail(new { job = id, cancelled, removed = gone }), succeeded: true, cancellation).ConfigureAwait(false);

        TileExportState? now = await exports.FindAsync(id, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            export = now is null ? null : Wire(now, service),
            note = cancelled
                ? "Cancelled, and its partial package deleted. The tiles it had built stay in the cache, as a seed's do."
                : gone
                    ? "The package is deleted."
                    : "The package is being read and will be deleted on the next sweep, within a minute.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Streams a written package — with its length, ranges, and its type.</summary>
    private static async Task DownloadExportAsync(
        HttpContext context,
        string name,
        Guid id,
        string? folder,
        PostgresLayerCatalog owners,
        ITileExportStore exports,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await ReadableSeedServiceAsync(context, owners, name, folder, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        if (await exports.FindAsync(id, cancellation).ConfigureAwait(false) is not { } export || export.ServiceId != service.Id)
        {
            await Refuse(context, 404, $"No export '{id}' of '{service.QualifiedName}'.").ConfigureAwait(false);
            return;
        }

        await AuditAsync(
            context, audit, "service.tiles.export.download", service.QualifiedName,
            Detail(new { job = id, origin = "admin", range = context.Request.Headers.Range.ToString() }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await VectorTileExportEndpoints.ServePackageAsync(context, export, service, settings).ConfigureAwait(false);
    }

    private static Task RefuseExport(HttpContext context, VectorTileExportEndpoints.Refusal refusal) =>
        Results.Json(
            refusal.Extra is null
                ? new { error = new { code = refusal.Status, message = refusal.Message, details = refusal.Details } }
                : (object)new { error = new { code = refusal.Status, message = refusal.Message, details = refusal.Details }, detail = refusal.Extra },
            statusCode: refusal.Status)
            .ExecuteAsync(context);

    private static string ExportAddress(PublishedService service, Guid job) =>
        $"/admin/services/{Uri.EscapeDataString(service.Name)}/exports/{job}"
        + (service.Folder is null ? string.Empty : $"?folder={Uri.EscapeDataString(service.Folder)}");

    private static object Wire(TileExportPolicy policy, HostSettings settings) => new
    {
        allowed = policy.Allowed,
        anonymous = policy.Anonymous,
        maxExportTilesCount = policy.MaximumTiles,
        serverMaxExportTilesCount = settings.TileExportMaximumTiles,
    };

    /// <summary>One export on the wire. The download address is given; the token alone is never a field.</summary>
    private static object Wire(TileExportState export, PublishedService service)
    {
        bool downloadable = export.Job.Status == JobStatus.Done && export.RemovedAt is null;

        return new
        {
            id = export.Job.Id,
            status = export.Job.Status.ToString().ToLowerInvariant(),
            format = PostgresTileExportStore.Wire(export.Format),
            origin = export.Origin,
            levels = export.Levels,
            whole = export.Whole,
            tiles = export.Total,
            done = export.Done,
            stored = export.Stored,
            percent = export.Job.Progress,
            estimatedBytes = export.EstimatedBytes,
            bytes = export.Bytes,
            created = export.Job.Created,
            started = export.Job.Started,
            finished = export.Job.Finished,
            expires = export.ExpiresAt,
            removed = export.RemovedAt,
            failure = export.Job.Failure,
            pausedUntil = export.PausedUntil,
            pausedBecause = export.PausedBecause,
            download = downloadable ? ExportAddressOf(service, export.Job.Id, "/download") : null,
            watch = ExportAddress(service, export.Job.Id),
        };
    }

    private static string ExportAddressOf(PublishedService service, Guid job, string tail) =>
        $"/admin/services/{Uri.EscapeDataString(service.Name)}/exports/{job}{tail}"
        + (service.Folder is null ? string.Empty : $"?folder={Uri.EscapeDataString(service.Folder)}");
}
