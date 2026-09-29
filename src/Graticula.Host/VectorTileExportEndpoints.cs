using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Microsoft.Net.Http.Headers;

namespace Graticula.Host;

/// <summary>
/// ArcGIS's <c>exportTiles</c> and <c>estimateExportTilesSize</c> on a VectorTileServer, the job they make, and the
/// package it leaves — ADR-098.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the published REST specification only</b> (CLAUDE.md §5): the Vector Tile Service's <em>Export
/// Tiles</em> operation — its parameters <c>levels</c>, <c>exportExtent</c>, <c>polygon</c> and <c>f</c>, its answer
/// <c>{"jobId", "jobStatus": "esriJobSubmitted"}</c>, the job at <c>jobs/{jobId}</c>, and the package at
/// <c>results/out_service_url</c> — and the service resource's <c>exportTilesAllowed</c> and <c>maxExportTilesCount</c>.
/// What that documentation does not spell out — the job resource's full shape, the result parameter's
/// <c>dataType</c>, <c>estimateExportTilesSize</c>'s answer on a vector tile service — is <b>INFERRED</b> from the
/// Geoprocessing job resource and the map service's operation of the same name, and ADR-098 §4 lists each.
/// </para>
/// <para>
/// <b>Governed like every tile route, and then asked once more.</b> Each route resolves the service through
/// <see cref="VectorTileEndpoints.TileableAsync"/>, so a caller who may not read the service is told what any other
/// route would tell them; then <see cref="MayExport"/> asks whether the service offers exports to this caller. That
/// second question is asked again at download time, so turning exports off, or unsharing the service, stops a package
/// being fetched from that moment — which a pre-signed address could not do.
/// </para>
/// </remarks>
internal static class VectorTileExportEndpoints
{
    /// <summary>The result parameter that carries the package's address — the documented name.</summary>
    internal const string OutServiceUrl = "out_service_url";

    /// <summary>The result parameter of an estimate — <b>INFERRED</b> from the map service's operation.</summary>
    internal const string OutEstimates = "out_service_tile_estimates";

    private const string EstimatePrefix = "estimate-";

    /// <summary>Maps the routes, at the root and in any folder, as the tile routes are.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (string prefix in (string[])["/rest/services", "/rest/services/{folder}"])
        {
            string at = $"{prefix}/{{serviceName}}/VectorTileServer";

            app.MapMethods($"{at}/exportTiles", ["GET", "POST"], ExportTilesAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapMethods($"{at}/estimateExportTilesSize", ["GET", "POST"], EstimateAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{at}/jobs/{{jobId}}", JobAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{at}/jobs/{{jobId}}/results/{{parameter}}", ResultAsync)
                .Governed(SharingGovernedExtensions.ByService);
            app.MapGet($"{at}/jobs/{{jobId}}/package/{{file}}", DownloadAsync)
                .Governed(SharingGovernedExtensions.ByService);
        }
    }

    /// <summary>
    /// Whether a caller may export this service: the tile face is on, exports are offered, and a caller who is not
    /// signed in is let in only where the service says so — ADR-098 §5.5.
    /// </summary>
    /// <param name="service">The service, which the caller has already been found to read.</param>
    /// <param name="anonymous">Whether the caller is not signed in.</param>
    /// <returns>True when they may.</returns>
    internal static bool MayExport(PublishedService service, bool anonymous) =>
        service.Limits.AllowsTileExport(dataSupportsIt: true)
        && (!anonymous || service.Limits.Export.Anonymous);

    /// <summary>The service document with the export advertised — the two documented properties, set.</summary>
    /// <param name="document">The document as the route builds it.</param>
    /// <param name="maximumTiles">The most tiles one export may hold here.</param>
    /// <returns>The document to serve.</returns>
    /// <remarks>
    /// <b>Only here, so every other caller's document is unchanged byte for byte.</b> Serialised with the web defaults
    /// <c>Results.Ok</c> uses, so the fields read the same way whichever path wrote them. <c>capabilities</c> is not
    /// touched: the documented example of a service that exports says <c>TilesOnly</c> beside
    /// <c>exportTilesAllowed: true</c>, so no word in it is what a client checks (<b>INFERRED</b>, ADR-098 §4).
    /// </remarks>
    internal static object Advertised(object document, long maximumTiles)
    {
        JsonObject node = (JsonObject)JsonSerializer.SerializeToNode(document, Web)!;

        node["exportTilesAllowed"] = true;
        node["maxExportTilesCount"] = maximumTiles;

        return node;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>What an export is asked for, in either vocabulary.</summary>
    /// <param name="Format">The package.</param>
    /// <param name="Levels">The levels, or null for the service's default.</param>
    /// <param name="Extent">The area, or null for the whole service.</param>
    /// <param name="Origin"><c>admin</c> or <c>arcgis</c>.</param>
    internal sealed record Ask(
        TileExportFormat Format, IReadOnlyList<int>? Levels, AdminEndpoints.SeedExtent? Extent, string Origin);

    /// <summary>An export counted and estimated, ready to start.</summary>
    /// <param name="Plan">The walk.</param>
    /// <param name="Whole">Whether the area is the whole service.</param>
    /// <param name="TileBytes">The tiles' estimated bytes.</param>
    /// <param name="EstimatedBytes">The package's estimated bytes.</param>
    /// <param name="Sampled">Whether every level's estimate came from the cache's own tiles.</param>
    /// <param name="Cap">The most tiles this export may hold.</param>
    internal sealed record Planned(
        TileSeedPlan Plan, bool Whole, long TileBytes, long EstimatedBytes, bool Sampled, long Cap);

    /// <summary>A refusal: its status, its sentence, and what it is for a client that branches on it.</summary>
    internal sealed record Refusal(int Status, string Message, string[] Details, object? Extra = null);

    private static readonly string[] TooManyTilesDetail = ["tooManyTiles"];

    private static readonly string[] OverBudgetDetail = ["exceedsExportBudget"];

    /// <summary>Parses ArcGIS's level list: <c>1,2,3</c> or <c>1-4, 7-9</c>.</summary>
    /// <param name="text">The parameter.</param>
    /// <param name="levels">The levels, ascending and distinct.</param>
    /// <returns>The refusal, or null.</returns>
    internal static string? ParseLevels(string? text, out IReadOnlyList<int>? levels)
    {
        levels = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        SortedSet<int> found = [];

        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = part.IndexOf('-', 1);

            if (dash < 0)
            {
                if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int one))
                {
                    return $"'{part}' is not a level. Levels are whole numbers, separated by commas or given as ranges: 1,2,3 or 1-4,7-9.";
                }

                found.Add(one);
                continue;
            }

            if (!int.TryParse(part[..dash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                || !int.TryParse(part[(dash + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int to)
                || from > to)
            {
                return $"'{part}' is not a range of levels. A range is the lower level, a dash and the higher one: 1-4.";
            }

            if (to - from > 64)
            {
                return $"'{part}' spans more levels than any grid has.";
            }

            for (int z = from; z <= to; z++)
            {
                found.Add(z);
            }
        }

        if (found.Count == 0)
        {
            return "No level was named. Name one or more: 1,2,3 or 1-4.";
        }

        levels = [.. found];
        return null;
    }

    /// <summary>Counts and estimates an export — the checks every door asks, in one place.</summary>
    internal static async Task<(Planned? Planned, Refusal? Refusal)> PlanAsync(
        PublishedService service,
        Ask ask,
        HostSettings settings,
        ServiceContexts contexts,
        IProjector projector,
        ITileCache cache,
        GeoParquetSources geoParquet,
        CancellationToken cancellation)
    {
        if (TileSeeder.WhyNotSeedable(service) is { } unseedable)
        {
            return (null, new Refusal(400, unseedable.Replace("seed", "export", StringComparison.Ordinal), []));
        }

        if (ask.Format == TileExportFormat.PmTiles && !service.TileScheme.IsWebMercator)
        {
            return (null, new Refusal(400, TileExportPackage.WhyNotPmTiles(service), []));
        }

        VectorTileScheme scheme = service.TileScheme;
        IReadOnlyList<int> levels;

        if (ask.Levels is { Count: > 0 } asked)
        {
            levels = asked;
        }
        else if (AdminEndpoints.DefaultSeedLevels(service) is { } d)
        {
            levels = [.. Enumerable.Range(d.Min, d.Max - d.Min + 1)];
        }
        else
        {
            return (null, new Refusal(400,
                $"No layer of '{service.QualifiedName}' draws at any level, so there is nothing to export.", []));
        }

        if (levels[0] < 0 || levels[^1] > scheme.MaxLevel)
        {
            return (null, new Refusal(400,
                $"Levels {string.Join(",", levels)}: this service's levels are 0 to {scheme.MaxLevel}.", []));
        }

        (Envelope? found, bool whole, string? unplaced) =
            await AdminEndpoints.AreaOfAsync(service, ask.Extent, contexts, projector, cancellation).ConfigureAwait(false);

        if (found is not { } area)
        {
            return (null, new Refusal(400, unplaced!, []));
        }

        TileSeedPlan plan;

        try
        {
            plan = TileSeedPlan.For(scheme, area, levels[0], levels[^1]).Keeping(levels.ToHashSet());
        }
        catch (ArgumentException wrong)
        {
            return (null, new Refusal(400, wrong.Message, []));
        }

        long cap = service.Limits.Export.MaximumOf(settings.TileExportMaximumTiles);

        if (plan.Total > cap)
        {
            int? fits = AdminEndpoints.HighestLevelThatFits(plan, cap);

            return (null, new Refusal(
                400,
                $"Levels {string.Join(",", plan.Levels.Select(l => l.Z))} over this area are {plan.Total:N0} tiles, and one "
                + $"export of this service may hold at most {cap:N0} (maxExportTilesCount). "
                + (fits is { } top ? $"Up to level {top} fits. " : $"Level {plan.Levels[0].Z} alone is {plan.Levels[0].Count:N0}. ")
                + "Export fewer levels or a smaller area.",
                TooManyTilesDetail,
                new { tiles = plan.Total, cap, fits }));
        }

        (long tileBytes, bool sampled) =
            await TileExporter.TileBytesAsync(service, plan, contexts, cache, geoParquet, cancellation).ConfigureAwait(false);

        return (new Planned(
            plan, whole, tileBytes, TileExportPackage.EstimateBytes(ask.Format, plan.Levels, tileBytes), sampled, cap), null);
    }

    /// <summary>Records a planned export and wakes a worker — or says it does not fit the export budget.</summary>
    internal static async Task<(TileExportState? Started, Refusal? Refusal)> StartAsync(
        ITileExportStore exports,
        Guid owner,
        PublishedService service,
        Ask ask,
        Planned planned,
        HostSettings settings,
        JobSignal signal,
        CancellationToken cancellation)
    {
        IReadOnlyList<int> levels = [.. planned.Plan.Levels.Select(level => level.Z)];

        TileExportStart start = await exports.StartAsync(
            owner,
            new TileExportRequest(
                service.Id, ask.Format, levels, planned.Plan.Area, planned.Whole, planned.Plan.Total,
                planned.EstimatedBytes, TileExporter.NewToken(), service.TileScheme.Key, ask.Origin),
            $"Exporting the tiles of {service.QualifiedName} as a {(ask.Format == TileExportFormat.Vtpk ? "VTPK" : "PMTiles archive")}, "
            + (levels.Count == 1 ? $"level {levels[0]}" : $"levels {levels[0]} to {levels[^1]}"),
            TileExporter.Detail(service, ask.Format, levels, planned.Plan.Total),
            settings.TileExportBudgetBytes,
            cancellation).ConfigureAwait(false);

        if (start.Started is not { } started)
        {
            long held = start.RefusedForSpace ?? 0;

            return (null, new Refusal(
                400,
                $"This export is estimated at {TileSeedEstimate.Size(planned.EstimatedBytes)}, and the packages already "
                + $"kept or being written hold {TileSeedEstimate.Size(held)} of the {TileSeedEstimate.Size(settings.TileExportBudgetBytes)} "
                + "the server keeps for exports (Graticula:TileExportBudgetMB). Delete a package you no longer need, wait for "
                + "one to expire, export fewer levels or a smaller area, or raise the budget.",
                OverBudgetDetail,
                new { estimatedBytes = planned.EstimatedBytes, heldBytes = held, budget = settings.TileExportBudgetBytes }));
        }

        signal.Wake(JobKind.TileExport);
        return (started, null);
    }

    /// <summary>Sends a written package: the whole file or a range of it, with its length and type.</summary>
    /// <remarks>
    /// <b>The path is the store's token inside the export directory</b> (<see cref="TileExporter.FileOf"/>), never a
    /// part of the request; the request's file name is only compared with it. A package this server does not have — it
    /// expired, was removed, or was written by another server whose export directory this one does not share — is a
    /// 404 that says which.
    /// </remarks>
    internal static async Task ServePackageAsync(
        HttpContext context, TileExportState export, PublishedService service, HostSettings settings)
    {
        if (export.Job.Status != JobStatus.Done || export.RemovedAt is not null
            || TileExporter.FileOf(settings.TileExportDirectory, export.Token, export.Format) is not { } path
            || !System.IO.File.Exists(path))
        {
            await Refuse(context, 404,
                export.RemovedAt is not null
                    ? "The package was removed: it expired, or it was deleted. Export again."
                    : export.Job.Status != JobStatus.Done
                        ? $"The export is {export.Job.Status.ToString().ToLowerInvariant()}, so there is no package to download."
                        : "The package is not on this server. It may have been written by another server whose export "
                          + "directory this one does not share (Graticula:TileExportPath).")
                .ConfigureAwait(false);
            return;
        }

        System.IO.FileInfo file = new(path);

        context.Response.Headers.CacheControl = "private, no-store";

        await TypedResults.PhysicalFile(
                path,
                TileExportPackage.MediaType(export.Format),
                TileExportPackage.DownloadName(service, export.Format),
                new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                new EntityTagHeaderValue("\"" + export.Token[..16] + file.Length.ToString(CultureInfo.InvariantCulture) + "\""),
                enableRangeProcessing: true)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>exportTiles</c>: plans an export of this service as a VTPK and answers with its job.</summary>
    private static async Task ExportTilesAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        ITileCache cache,
        GeoParquetSources geoParquet,
        ITileExportStore exports,
        JobSignal signal,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        // <b>Before anything else, so a forged request learns nothing and costs nothing</b> — ADR-098 §5.7.
        if (CrossSiteByCookie(context) is { } forged)
        {
            await Refuse(context, 403, forged).ConfigureAwait(false);
            return;
        }

        if (await ExportableAsync(context, serviceName, catalog, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        Func<string, string?> p = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (await AskAsync(context, p).ConfigureAwait(false) is not { } ask)
        {
            return;
        }

        (Planned? planned, Refusal? refused) = await PlanAsync(
            service, ask, settings, contexts, projector, cache, geoParquet, cancellation).ConfigureAwait(false);

        if (planned is null)
        {
            await RefuseAsync(context, refused!).ConfigureAwait(false);
            return;
        }

        RequestPrincipal current = context.Features.Get<RequestPrincipal>()!;

        (TileExportState? started, Refusal? full) = await StartAsync(
            exports, current.Principal.Id, service, ask, planned, settings, signal, cancellation).ConfigureAwait(false);

        if (started is null)
        {
            await RefuseAsync(context, full!).ConfigureAwait(false);
            return;
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "service.tiles.export", service.QualifiedName,
            AdminEndpoints.Detail(new
            {
                job = started.Job.Id,
                origin = ask.Origin,
                format = "vtpk",
                levels = started.Levels,
                tiles = started.Total,
                estimatedBytes = started.EstimatedBytes,
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { jobId = started.Job.Id.ToString("N"), jobStatus = "esriJobSubmitted" })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Why an <c>exportTiles</c> signed in by the session cookie alone is refused, or null when it is not.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns>The sentence, or null.</returns>
    /// <remarks>
    /// <para>
    /// <b>The cookie authenticates reads only (<c>Authentication.CookieToken</c>), and this is the one read-shaped
    /// write.</b> ArcGIS documents <c>exportTiles</c> as a <c>GET</c>, so a page on another site could make a signed-in
    /// browser start an export — disk and a job worker spent in the victim's name — with an image tag. The cookie's
    /// <c>SameSite=Strict</c> already stops that in a browser that honours it; this does not rely on it.
    /// </para>
    /// <para>
    /// <b>The defence is the one the server already has for a cookie that must do more than read</b>:
    /// <see cref="AuthEndpoints.FromThisOrigin"/>, <c>Sec-Fetch-Site: same-origin</c>, which the browser sets and no
    /// page can. A request signed by a token — the <c>token=</c> parameter, the ArcGIS header or a bearer header, which
    /// is how every ArcGIS client sends it — is not a cookie request and is not asked. An anonymous request is not
    /// asked either: it acts in nobody's name, and <see cref="MayExport"/> decides it.
    /// </para>
    /// </remarks>
    internal static string? CrossSiteByCookie(HttpContext context)
    {
        RequestPrincipal? caller = context.Features.Get<RequestPrincipal>();

        if (caller is null || caller.Principal.IsAnonymous || !Authentication.CookieOnly(context)
            || AuthEndpoints.FromThisOrigin(context.Request))
        {
            return null;
        }

        return "exportTiles starts work in your name, and this request was signed in only by the browser's session "
            + "cookie from a page that is not on this server. Send a token — the token parameter, the "
            + "X-Esri-Authorization header or a bearer header, as ArcGIS clients do — or start the export from the "
            + "service's Caching page, which uses POST /admin/services/{name}/exports.";
    }

    /// <summary>
    /// <c>estimateExportTilesSize</c>: the tile count and the size a VTPK of the request would take, as a job that has
    /// already succeeded.
    /// </summary>
    /// <remarks>
    /// <b>Answered at once, and shaped as a job because clients poll one.</b> Counting is arithmetic and the size is the
    /// cache's own averages, so there is nothing to wait for; the job id carries the answer itself, so any server can
    /// answer its status and nothing is stored. The numbers are the caller's own request, so a forged id tells nobody
    /// anything. <b>INFERRED</b>: the operation is documented on the map service; on a vector tile service its shape
    /// is taken from there (ADR-098 §4).
    /// </remarks>
    private static async Task EstimateAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IProjector projector,
        ITileCache cache,
        GeoParquetSources geoParquet,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await ExportableAsync(context, serviceName, catalog, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        Func<string, string?> p = await ArcGisParameters.LookupAsync(context, cancellation).ConfigureAwait(false);

        if (await AskAsync(context, p).ConfigureAwait(false) is not { } ask)
        {
            return;
        }

        (Planned? planned, Refusal? refused) = await PlanAsync(
            service, ask, settings, contexts, projector, cache, geoParquet, cancellation).ConfigureAwait(false);

        if (planned is null)
        {
            await RefuseAsync(context, refused!).ConfigureAwait(false);
            return;
        }

        string id = EstimatePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { t = planned.Plan.Total, b = planned.EstimatedBytes })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await Results.Json(new { jobId = id, jobStatus = "esriJobSucceeded" }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>jobs/{jobId}</c>: where an export has got to, in the Geoprocessing job's words.</summary>
    private static async Task JobAsync(
        HttpContext context,
        string serviceName,
        string jobId,
        CatalogFallback catalog,
        ITileExportStore exports,
        CancellationToken cancellation)
    {
        if (await VectorTileEndpoints.TileableAsync(context, serviceName, catalog, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        if (Estimate(jobId) is { } estimate)
        {
            await Results.Json(new
            {
                jobId,
                jobStatus = "esriJobSucceeded",
                results = new Dictionary<string, object> { [OutEstimates] = new { paramUrl = "results/" + OutEstimates } },
                inputs = new Dictionary<string, object>(),
                messages = new[]
                {
                    new { type = "esriJobMessageTypeInformative", description = $"{estimate.Tiles:N0} tiles to export." },
                },
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (await ExportOfAsync(context, service, jobId, exports, cancellation).ConfigureAwait(false) is not { } export)
        {
            return;
        }

        List<object> messages =
        [
            new
            {
                type = "esriJobMessageTypeInformative",
                description = $"{export.Done:N0} of {export.Total:N0} tiles walked; {export.Stored:N0} with something in them.",
            },
        ];

        if (export.PausedBecause is { } paused)
        {
            messages.Add(new { type = "esriJobMessageTypeWarning", description = "Waiting for the data source: " + paused });
        }

        if (export.Job.Failure is { } failure)
        {
            messages.Add(new { type = "esriJobMessageTypeError", description = failure });
        }

        if (export.RemovedAt is not null && export.Job.Status == JobStatus.Done)
        {
            messages.Add(new { type = "esriJobMessageTypeWarning", description = "The package has been removed." });
        }

        Dictionary<string, object> results = export.Job.Status == JobStatus.Done && export.RemovedAt is null
            ? new() { [OutServiceUrl] = new { paramUrl = "results/" + OutServiceUrl } }
            : [];

        await Results.Json(new
        {
            jobId = export.Job.Id.ToString("N"),
            jobStatus = StatusOf(export.Job.Status),

            // Not in the documented example; the Geoprocessing job resource of later releases carries one, and a
            // client that does not read it loses nothing (INFERRED).
            progress = new { type = "default", message = "Exporting tiles", percent = export.Job.Progress },
            results,
            inputs = new Dictionary<string, object>
            {
                ["levels"] = new { paramUrl = "inputs/levels" },
                ["exportExtent"] = new { paramUrl = "inputs/exportExtent" },
            },
            messages,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>jobs/{jobId}/results/{parameter}</c>: the package's address, or an estimate's numbers.</summary>
    private static async Task ResultAsync(
        HttpContext context,
        string serviceName,
        string jobId,
        string parameter,
        CatalogFallback catalog,
        ITileExportStore exports,
        CancellationToken cancellation)
    {
        if (await VectorTileEndpoints.TileableAsync(context, serviceName, catalog, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        if (Estimate(jobId) is { } estimate && parameter == OutEstimates)
        {
            await Results.Json(new
            {
                paramName = OutEstimates,
                dataType = "GPString",
                value = new { totalSize = estimate.Bytes, totalTilesToExport = estimate.Tiles },
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (parameter != OutServiceUrl)
        {
            await Refuse(context, 404, $"This job has no result '{parameter}'. An export's is {OutServiceUrl}.")
                .ConfigureAwait(false);
            return;
        }

        if (await ExportOfAsync(context, service, jobId, exports, cancellation).ConfigureAwait(false) is not { } export)
        {
            return;
        }

        if (!await MayExportAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        if (export.Job.Status != JobStatus.Done || export.RemovedAt is not null)
        {
            await Refuse(context, 400,
                export.RemovedAt is not null
                    ? "The package was removed: it expired, or it was deleted. Export again."
                    : $"The export is {export.Job.Status.ToString().ToLowerInvariant()}, so it has no package yet.")
                .ConfigureAwait(false);
            return;
        }

        // <b>Absolute, because a client hands it to a downloader that has no base to resolve against</b> — the address
        // this request came to, with a proxy's path base, as the OGC and TileJSON faces build theirs.
        string url = TileFaces.Origin(context) + context.Request.Path.Value![..context.Request.Path.Value!.LastIndexOf("/results/", StringComparison.Ordinal)]
            + "/package/" + export.Token + TileExportPackage.Extension(export.Format);

        await Results.Json(new
        {
            paramName = OutServiceUrl,
            dataType = "GPString",
            value = url,

            // Ours, beside the documented value, so a script need not know which of the two ArcGIS releases' shapes it
            // is reading.
            downloadUrl = url,
            size = export.Bytes,
            expires = export.ExpiresAt,
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The package itself: re-checks that the caller may read and export the service, then streams it.</summary>
    private static async Task DownloadAsync(
        HttpContext context,
        string serviceName,
        string jobId,
        string file,
        CatalogFallback catalog,
        ITileExportStore exports,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await VectorTileEndpoints.TileableAsync(context, serviceName, catalog, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return;
        }

        if (await ExportOfAsync(context, service, jobId, exports, cancellation).ConfigureAwait(false) is not { } export)
        {
            return;
        }

        if (!await MayExportAsync(context, service).ConfigureAwait(false))
        {
            return;
        }

        // <b>The token must match, compared in constant time</b>: the job id is shown to anybody who may read the
        // job's status, and it is the token that makes the address the caller was given — and nobody else — work.
        string expected = export.Token + TileExportPackage.Extension(export.Format);

        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(file), Encoding.UTF8.GetBytes(expected)))
        {
            await Refuse(context, 404, "No such package.").ConfigureAwait(false);
            return;
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "service.tiles.export.download", service.QualifiedName,
            AdminEndpoints.Detail(new { job = export.Job.Id, origin = "arcgis", range = context.Request.Headers.Range.ToString() }),
            succeeded: true, cancellation).ConfigureAwait(false);

        await ServePackageAsync(context, export, service, settings).ConfigureAwait(false);
    }

    /// <summary>The service, if it may be read, tiles, and offers exports to this caller; otherwise the refusal is written.</summary>
    private static async Task<PublishedService?> ExportableAsync(
        HttpContext context, string serviceName, CatalogFallback catalog, CancellationToken cancellation)
    {
        if (await VectorTileEndpoints.TileableAsync(context, serviceName, catalog, cancellation).ConfigureAwait(false)
            is not { } service)
        {
            return null;
        }

        return await MayExportAsync(context, service).ConfigureAwait(false) ? service : null;
    }

    private static async Task<bool> MayExportAsync(HttpContext context, PublishedService service)
    {
        bool anonymous = context.Features.Get<RequestPrincipal>()?.Principal.IsAnonymous ?? true;

        if (MayExport(service, anonymous))
        {
            return true;
        }

        // <b>403, and it says which half</b>: the caller may read the service — the document already told them
        // `exportTilesAllowed` — so this is not a fact about whether it exists (ADR-018's reason for 404 does not apply).
        await Refuse(context, 403,
            !service.Limits.AllowsTileExport(dataSupportsIt: true)
                ? $"Exporting tiles is not enabled on '{service.QualifiedName}'. Its owner or an administrator can enable it."
                : $"Exporting tiles from '{service.QualifiedName}' needs you to be signed in.")
            .ConfigureAwait(false);

        return false;
    }

    private static async Task<TileExportState?> ExportOfAsync(
        HttpContext context, PublishedService service, string jobId, ITileExportStore exports, CancellationToken cancellation)
    {
        if (!Guid.TryParse(jobId, out Guid id)
            || await exports.FindAsync(id, cancellation).ConfigureAwait(false) is not { } export
            || export.ServiceId != service.Id)
        {
            await Refuse(context, 404, $"No export job '{jobId}' on this service.").ConfigureAwait(false);
            return null;
        }

        return export;
    }

    /// <summary>Reads an ArcGIS request into an ask, or writes the refusal and answers null.</summary>
    private static async Task<Ask?> AskAsync(HttpContext context, Func<string, string?> p)
    {
        string? f = p("f");

        if (f is not null && !string.Equals(f, "json", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(f, "pjson", StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(context, 400, $"f={f}: this operation answers only f=json.").ConfigureAwait(false);
            return null;
        }

        // <b>Level IDs only.</b> `exportBy` names what `levels` counts; this server's levels are its grid's, and a
        // resolution or a scale list would be matched to them by a rule the documentation does not state.
        string? by = p("exportBy");

        if (by is not null && !string.Equals(by, "LevelID", StringComparison.OrdinalIgnoreCase))
        {
            await Refuse(context, 400,
                $"exportBy={by}: this server exports by level ID only. Name the levels by their numbers.")
                .ConfigureAwait(false);
            return null;
        }

        if (ParseLevels(p("levels") ?? p("tileLevels"), out IReadOnlyList<int>? levels) is { } wrong)
        {
            await Refuse(context, 400, wrong).ConfigureAwait(false);
            return null;
        }

        AdminEndpoints.SeedExtent? extent = null;
        string? given = p("exportExtent");

        if (!string.IsNullOrWhiteSpace(given) && !string.Equals(given.Trim(), "DEFAULT", StringComparison.OrdinalIgnoreCase))
        {
            if (Envelope(given) is not { } box)
            {
                await Refuse(context, 400,
                    "exportExtent is an envelope — {\"xmin\":…,\"ymin\":…,\"xmax\":…,\"ymax\":…,\"spatialReference\":{\"wkid\":…}} "
                    + "— or DEFAULT for the whole service.")
                    .ConfigureAwait(false);
                return null;
            }

            extent = box;
        }
        else if (p("polygon") is { Length: > 0 } polygon)
        {
            // <b>The polygon's bounding box</b>: a package holds whole tiles, and every tile the polygon touches is in
            // its box; the tiles in the box that the polygon misses are exported too. Said in ADR-098 §5.3.
            if (Bounds(polygon) is not { } box)
            {
                await Refuse(context, 400,
                    "polygon is a JSON polygon with rings and a spatialReference.").ConfigureAwait(false);
                return null;
            }

            extent = box;
        }

        return new Ask(TileExportFormat.Vtpk, levels, extent, "arcgis");
    }

    private static AdminEndpoints.SeedExtent? Envelope(string text)
    {
        try
        {
            AdminEndpoints.SeedExtent? box = JsonSerializer.Deserialize<AdminEndpoints.SeedExtent>(text, Web);
            return box;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static AdminEndpoints.SeedExtent? Bounds(string text)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(text);

            if (node?["rings"] is not JsonArray rings)
            {
                return null;
            }

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;

            foreach (JsonNode? ring in rings)
            {
                foreach (JsonNode? point in ring as JsonArray ?? [])
                {
                    if (point is JsonArray { Count: >= 2 } xy)
                    {
                        double x = xy[0]!.GetValue<double>();
                        double y = xy[1]!.GetValue<double>();
                        minX = Math.Min(minX, x);
                        minY = Math.Min(minY, y);
                        maxX = Math.Max(maxX, x);
                        maxY = Math.Max(maxY, y);
                    }
                }
            }

            if (minX > maxX)
            {
                return null;
            }

            JsonNode? reference = node["spatialReference"];

            return new AdminEndpoints.SeedExtent(
                minX, minY, maxX, maxY,
                reference is null
                    ? null
                    : new AdminEndpoints.SeedReference(
                        reference["wkid"]?.GetValue<int>(), reference["latestWkid"]?.GetValue<int>()));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static (long Tiles, long Bytes)? Estimate(string jobId)
    {
        if (!jobId.StartsWith(EstimatePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            string body = jobId[EstimatePrefix.Length..].Replace('-', '+').Replace('_', '/');
            body = body.PadRight(body.Length + ((4 - (body.Length % 4)) % 4), '=');

            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(body));

            return (document.RootElement.GetProperty("t").GetInt64(), document.RootElement.GetProperty("b").GetInt64());
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A job's status as the Geoprocessing job resource names it.</summary>
    internal static string StatusOf(JobStatus status) => status switch
    {
        JobStatus.Queued => "esriJobSubmitted",
        JobStatus.Running => "esriJobExecuting",
        JobStatus.Done => "esriJobSucceeded",
        JobStatus.Failed => "esriJobFailed",
        JobStatus.Cancelled => "esriJobCancelled",
        _ => "esriJobFailed",
    };

    private static Task RefuseAsync(HttpContext context, Refusal refusal) =>
        Results.Json(
            refusal.Extra is null
                ? new { error = new { code = refusal.Status, message = refusal.Message, details = refusal.Details } }
                : (object)new { error = new { code = refusal.Status, message = refusal.Message, details = refusal.Details }, detail = refusal.Extra },
            statusCode: refusal.Status)
            .ExecuteAsync(context);

    private static Task Refuse(HttpContext context, int status, string message) =>
        Results.Json(new { error = new { code = status, message, details = Array.Empty<string>() } }, statusCode: status)
            .ExecuteAsync(context);
}
