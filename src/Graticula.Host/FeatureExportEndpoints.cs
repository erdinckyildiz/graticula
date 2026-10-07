using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Graticula.Host;

/// <summary>
/// Exporting some of a service's layers as a file, as a job, and fetching the file — ADR-106 §5.8.
/// </summary>
/// <remarks>
/// <para>
/// <b>Beside ADR-098's <c>/exports</c> family and not inside it</b> — <c>/admin/services/{name}/data-exports</c>. The
/// rows are another table, the request another shape and the rule another: a tile package is the owner's and an
/// administrator's to start, and this is any signed-in reader's where the service offers <c>Extract</c>. One family
/// answering two permission rules is where one of them is forgotten.
/// </para>
/// <para>
/// <b>Who may — owner decision 2026-09-30, and the rule <see cref="LayerExportEndpoints"/> already applies.</b> The
/// layer's owner and administrators always; another signed-in reader only for a layer whose served ceiling holds
/// <c>Extract</c>, which the owner's choice put there (unchosen is off); nobody anonymous. <b>Asked again at every
/// download</b>, so turning Extract off, or unsharing the service, stops a reader's next download at once.
/// <b>An export belongs to the caller who started it</b> (its job's owner): only they and an administrator may see it,
/// fetch it or delete it, and anybody else is told there is no such export, as they would be of one that never was.
/// </para>
/// <para>
/// <b>POST and DELETE, so a cookie from another site cannot start or stop one</b> (ADR-098 §5.7): the session cookie
/// signs reads only, so a cookie-only request to either arrives anonymous and is refused; the first thing a POST asks
/// is <see cref="VectorTileExportEndpoints.CrossSiteByCookie"/> in any case, so a forged request learns nothing and
/// costs nothing. <b>One export queued or running per caller</b> — a 409 naming it — so one reader cannot keep the
/// worker from everybody else.
/// </para>
/// </remarks>
internal static class FeatureExportEndpoints
{
    /// <summary>A service the caller may read and a caller who is signed in.</summary>
    private sealed record Opened(PublishedService Service, RequestPrincipal Caller);

    /// <summary>What a feature export is asked for.</summary>
    /// <param name="Format">This server's token or ArcGIS Online's spelling of one of the eight formats.</param>
    /// <param name="Layers">The layer ids, or none for every layer of the service.</param>
    internal sealed record Ask(string? Format, IReadOnlyList<int>? Layers);

    private static readonly string[] Formats =
        ["gpkg", "shapefile", "xlsx", "fgdb", "kml", "csv", "geojson", "esrijson"];

    private static readonly string[] TooManyRows = ["tooManyRows"];
    private static readonly string[] KmlTooLarge = ["kmlTooLarge"];

    private static readonly string[] OverBudget = ["exceedsExportBudget"];

    private static readonly string[] InProgress = ["exportInProgress"];

    /// <summary>Maps the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/admin/services/{name}/data-exports", ListAsync)
            .Governed(SharingGovernedExtensions.ByService);
        app.MapPost("/admin/services/{name}/data-exports", StartAsync)
            .Governed(SharingGovernedExtensions.ByService);
        app.MapGet("/admin/services/{name}/data-exports/{id:guid}", GetAsync)
            .Governed(SharingGovernedExtensions.ByService);
        app.MapDelete("/admin/services/{name}/data-exports/{id:guid}", DeleteAsync)
            .Governed(SharingGovernedExtensions.ByService);
        app.MapGet("/admin/services/{name}/data-exports/{id:guid}/download", DownloadAsync)
            .Governed(SharingGovernedExtensions.ByService);
    }

    /// <summary>What the caller's exports and the service's offer, in one read — the console's.</summary>
    private static async Task ListAsync(
        HttpContext context,
        string name,
        CatalogFallback catalog,
        IFeatureExportStore exports,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await OpenAsync(context, name, catalog, cancellation).ConfigureAwait(false) is not { } opened)
        {
            return;
        }

        (PublishedService service, RequestPrincipal caller) = opened;

        bool administrator = IsAdministrator(caller);
        IReadOnlyList<FeatureExportState> found = await exports
            .ListAsync(service.Id, administrator ? null : caller.Principal.Id, 20, cancellation).ConfigureAwait(false);
        long held = await exports.HeldBytesAsync(cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            name = service.Name,
            folder = service.Folder,
            formats = Formats,
            layers = service.Layers.Select(layer => new
            {
                id = layer.LayerIndex,
                name = layer.Definition.Name,
                geometryType = layer.GeometryType.ToString(),
                mayExport = MayExport(layer, caller),
            }),
            mayExport = service.Layers.Count > 0 && service.Layers.All(layer => MayExport(layer, caller)),
            maxRows = settings.FeatureExportMaximumRows,
            budget = new
            {
                bytes = settings.TileExportBudgetBytes,
                heldBytes = held,
                freeBytes = Math.Max(0, settings.TileExportBudgetBytes - held),
                retentionHours = settings.TileExportRetentionHours,
                setting = "Graticula:ExportBudgetMB",
            },
            everyonesExports = administrator,
            exports = found.Select(export => Wire(export, service)),
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Starts an export, or with <c>?dryRun=true</c> counts and estimates one.</summary>
    private static async Task StartAsync(
        HttpContext context,
        string name,
        Ask? request,
        CatalogFallback catalog,
        ServiceContexts contexts,
        IFeatureExportStore exports,
        JobSignal signal,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        // Before anything else, so a forged request learns nothing and costs nothing (ADR-098 §5.7).
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            // <b>Its own sentence</b>, since the tile export's names the tile export's own way in.
            await RefuseAsync(
                context, 403,
                "An export starts work in your name, and this request was signed in only by the browser's session cookie "
                + "from a page that is not on this server. Send a bearer header, as API clients do, or start the export "
                + "from the service's Overview in Studio.")
                .ConfigureAwait(false);
            return;
        }

        if (await OpenAsync(context, name, catalog, cancellation).ConfigureAwait(false) is not { } opened)
        {
            return;
        }

        (PublishedService service, RequestPrincipal caller) = opened;

        if (FeatureExportPackaging.Parse(request?.Format) is not { } format)
        {
            await RefuseAsync(
                context, 400,
                $"'format' is one of {string.Join(", ", Formats.Select(f => $"'{f}'"))}"
                + (string.IsNullOrWhiteSpace(request?.Format) ? "." : $"; '{request.Format}' is none of them."))
                .ConfigureAwait(false);
            return;
        }

        List<PublishedLayer> layers = [];

        if (request?.Layers is { Count: > 0 } asked)
        {
            foreach (int id in asked.Distinct().Order())
            {
                if (service.Layer(id) is not { } layer)
                {
                    await RefuseAsync(context, 400, $"There is no layer {id} in '{service.Name}'.").ConfigureAwait(false);
                    return;
                }

                layers.Add(layer);
            }
        }
        else
        {
            // Every layer, group layers not being layers: the service's list holds the ones that have rows.
            layers.AddRange(service.Layers.OrderBy(layer => layer.LayerIndex));
        }

        if (layers.Count == 0)
        {
            await RefuseAsync(context, 400, $"'{service.Name}' has no layer with rows to export.").ConfigureAwait(false);
            return;
        }

        List<string> refused = [.. layers.Where(layer => !MayExport(layer, caller)).Select(layer => layer.Definition.Name)];

        if (refused.Count > 0)
        {
            await RefuseAsync(
                context, 403,
                $"{string.Join(", ", refused.Select(n => $"'{n}'"))} {(refused.Count == 1 ? "does" : "do")} not offer Extract, so "
                + "only its owner or an administrator may export it. The owner can offer it in Settings › Feature layer.")
                .ConfigureAwait(false);
            return;
        }

        long cap = settings.FeatureExportMaximumRows;

        FeatureExporter.Weighed weighed =
            await FeatureExporter.WeighAsync(layers, contexts, cap, cancellation, format).ConfigureAwait(false);

        if (weighed.Rows > cap)
        {
            await RefuseAsync(
                context, 400,
                $"These layers hold {(weighed.Rows > cap ? "more than " : string.Empty)}{cap:N0} rows, which is more than one export "
                + "writes (Graticula:FeatureExportMaxRows). Choose fewer layers.",
                TooManyRows, new { rows = weighed.Rows, cap }).ConfigureAwait(false);
            return;
        }

        if (format == FeatureExportFormat.Excel
            && weighed.RowsByLayer.Select((rows, i) => (rows, i)).FirstOrDefault(x => x.rows > FeatureExportPackaging.WorkbookSheetRows)
                is { rows: > 0 } tooLong)
        {
            await RefuseAsync(
                context, 400,
                $"'{layers[tooLong.i].Definition.Name}' has more than {FeatureExportPackaging.WorkbookSheetRows:N0} rows, which is "
                + "more than one sheet of a workbook holds. Export it as another format.",
                TooManyRows, new { rows = tooLong.rows, cap = FeatureExportPackaging.WorkbookSheetRows }).ConfigureAwait(false);
            return;
        }

        // Esri JSON is written by the ArcGIS surface's own writer, which needs an integer object id; a layer published without
        // one was accepted here and failed in the job a moment later (measured 2026-10-07). Refused at the request instead.
        if (format == FeatureExportFormat.EsriJson
            && layers.FirstOrDefault(layer => layer.Definition.IntegerIdentityColumn is null) is { } noObjectId)
        {
            await RefuseAsync(
                context, 400,
                $"'{noObjectId.Definition.Name}' has no integer object-id column, and Esri JSON is written as the ArcGIS surface "
                + "writes it. Export it as GeoJSON or GeoPackage, or publish it with an integer object id.")
                .ConfigureAwait(false);
            return;
        }

        // ADR-106 condition 5: a KML is built in memory before it is written, so it is held to a size the writer's ceiling
        // can hold — refused here, at once, rather than killed by the ceiling minutes into the job.
        if (format == FeatureExportFormat.Kml && weighed.OutputBytes > FeatureExportPackaging.KmlLargestBytes)
        {
            await RefuseAsync(
                context, 400,
                $"A KML of these rows would be about {TileSeedEstimate.Size(weighed.OutputBytes)}, and this server writes a KML of at "
                + $"most {TileSeedEstimate.Size(FeatureExportPackaging.KmlLargestBytes)}: KML is built whole in memory before it is "
                + "written. Export fewer layers, or as GeoPackage, File Geodatabase or GeoJSON, which are written as they are read.",
                KmlTooLarge, new { estimatedBytes = weighed.OutputBytes, largest = FeatureExportPackaging.KmlLargestBytes })
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<int> ids = [.. layers.Select(layer => layer.LayerIndex)];
        FeatureExportPackaging packaging = FeatureExportPackaging.Of(format, ids.Count);
        long held = await exports.HeldBytesAsync(cancellation).ConfigureAwait(false);
        string wire = PostgresFeatureExportStore.Wire(format);

        if (string.Equals(context.Request.Query["dryRun"], "true", StringComparison.OrdinalIgnoreCase))
        {
            await Results.Json(new
            {
                name = service.Name,
                folder = service.Folder,
                format = wire,
                layers = layers.Select((layer, i) => new { id = layer.LayerIndex, name = layer.Definition.Name, rows = weighed.RowsByLayer[i] }),
                rows = weighed.Rows,
                cap,
                fileName = FeatureExportPackaging.DownloadName(service.Name, format, ids.Count),
                zipped = packaging.Zipped,
                oneFile = packaging.OneDataset,
                estimatedBytes = weighed.EstimatedBytes,
                estimateIsAnUpperBound = true,
                budget = settings.TileExportBudgetBytes,
                heldBytes = held,
                fits = held + weighed.EstimatedBytes <= settings.TileExportBudgetBytes,
                losses = Losses(format),
            }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        string token = TileExporter.NewToken();
        string fileName = FeatureExportPackaging.DownloadName(service.Name, format, ids.Count);

        FeatureExportStart start = await exports.StartAsync(
            caller.Principal.Id,
            new FeatureExportRequest(service.Id, format, ids, weighed.Rows, weighed.EstimatedBytes, token, fileName),
            $"Exporting {(ids.Count == 1 ? "one layer" : $"{ids.Count} layers")} of {service.QualifiedName} as {wire}",
            FeatureExporter.Detail(service, format, ids, weighed.Rows),
            settings.TileExportBudgetBytes,
            cancellation).ConfigureAwait(false);

        if (start.AlreadyRunning is { } running)
        {
            await RefuseAsync(
                context, 409,
                "You already have an export waiting or being written. Wait for it, or cancel it, and ask again.",
                InProgress, new { job = running }).ConfigureAwait(false);
            return;
        }

        if (start.Started is not { } started)
        {
            long taken = start.RefusedForSpace ?? 0;

            await RefuseAsync(
                context, 400,
                $"This export is estimated at {TileSeedEstimate.Size(weighed.EstimatedBytes)}, and the files already kept or "
                + $"being written hold {TileSeedEstimate.Size(taken)} of the {TileSeedEstimate.Size(settings.TileExportBudgetBytes)} "
                + "the server keeps for exports (Graticula:ExportBudgetMB). Delete a file you no longer need, wait for one to "
                + "expire, choose fewer layers, or raise the budget.",
                OverBudget, new { estimatedBytes = weighed.EstimatedBytes, heldBytes = taken, budget = settings.TileExportBudgetBytes })
                .ConfigureAwait(false);
            return;
        }

        signal.Wake(JobKind.FeatureExport);

        await AdminEndpoints.AuditAsync(
            context, audit, "service.data.export", service.QualifiedName,
            AdminEndpoints.Detail(new
            {
                job = started.Job.Id,
                format = wire,
                layers = ids,
                rows = weighed.Rows,
                estimatedBytes = weighed.EstimatedBytes,
                through = layers.All(layer => Manages(layer, caller)) ? "owner" : "extract",
            }),
            succeeded: true, cancellation).ConfigureAwait(false);

        string watch = AddressOf(service, started.Job.Id, string.Empty);
        context.Response.Headers.Location = watch;

        await Results.Json(
            new
            {
                id = started.Job.Id,
                status = "queued",
                watch,
                jobStatus = $"/admin/jobs/{started.Job.Id}",
                export = Wire(started, service),
                note = "The export runs on a job worker: each layer's rows are read the way a query reads them and written "
                    + $"as {wire}. It is kept for {settings.TileExportRetentionHours} hours once written, for you and for "
                    + "administrators.",
            },
            statusCode: 202).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One export.</summary>
    private static async Task GetAsync(
        HttpContext context,
        string name,
        Guid id,
        CatalogFallback catalog,
        IFeatureExportStore exports,
        CancellationToken cancellation)
    {
        if (await OpenAsync(context, name, catalog, cancellation).ConfigureAwait(false) is not { } opened)
        {
            return;
        }

        (PublishedService service, RequestPrincipal caller) = opened;

        if (await OwnedAsync(context, service, caller, id, exports, cancellation).ConfigureAwait(false) is not { } export)
        {
            return;
        }

        await Results.Json(Wire(export, service)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Cancels an export that is running and deletes its file, whatever state it is in.</summary>
    private static async Task DeleteAsync(
        HttpContext context,
        string name,
        Guid id,
        CatalogFallback catalog,
        IFeatureExportStore exports,
        FeatureExporter exporter,
        IAuditLog audit,
        CancellationToken cancellation)
    {
        if (await OpenAsync(context, name, catalog, cancellation).ConfigureAwait(false) is not { } opened)
        {
            return;
        }

        (PublishedService service, RequestPrincipal caller) = opened;

        if (await OwnedAsync(context, service, caller, id, exports, cancellation).ConfigureAwait(false) is not { } export)
        {
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

        await AdminEndpoints.AuditAsync(
            context, audit, "service.data.export.delete", service.QualifiedName,
            AdminEndpoints.Detail(new { job = id, cancelled, removed = gone }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        FeatureExportState? now = await exports.FindAsync(id, cancellation).ConfigureAwait(false);

        await Results.Json(new
        {
            export = now is null ? null : Wire(now, service),
            note = cancelled
                ? "Cancelled, and its partial files deleted."
                : gone
                    ? "The file is deleted."
                    : "The file is being read and will be deleted on the next sweep, within a minute.",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Streams a written file — with its length, ranges and type — to the caller who asked for it.</summary>
    private static async Task DownloadAsync(
        HttpContext context,
        string name,
        Guid id,
        string? file,
        CatalogFallback catalog,
        IFeatureExportStore exports,
        IAuditLog audit,
        HostSettings settings,
        CancellationToken cancellation)
    {
        if (await OpenAsync(context, name, catalog, cancellation).ConfigureAwait(false) is not { } opened)
        {
            return;
        }

        (PublishedService service, RequestPrincipal caller) = opened;

        if (await OwnedAsync(context, service, caller, id, exports, cancellation).ConfigureAwait(false) is not { } export)
        {
            return;
        }

        // <b>Asked again at every download</b> (ADR-106 §5.5, §5.7): Extract turned off, or a layer gone, stops a
        // reader's next fetch. The owner and administrators are not asked for Extract.
        List<PublishedLayer> layers = [.. export.Layers.Select(service.Layer).OfType<PublishedLayer>()];

        if (layers.Any(layer => !MayExport(layer, caller)))
        {
            await RefuseAsync(
                context, 403,
                "This export can no longer be downloaded by you: a layer in it no longer offers Extract. "
                + "Its owner or an administrator can still take it.")
                .ConfigureAwait(false);
            return;
        }

        // <b>The token, when the address carries one, is compared in constant time</b> — the address the caller was given
        // and nobody else's (`file`, and not `token`, which ArcGIS clients use for the sign-in).
        if (file is not null
            && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(file), Encoding.UTF8.GetBytes(export.Token)))
        {
            await RefuseAsync(context, 404, "No such export file.").ConfigureAwait(false);
            return;
        }

        if (export.Job.Status != JobStatus.Done || export.RemovedAt is not null
            || FeatureExporter.FileOf(settings.FeatureExportDirectory, export.Token) is not { } path
            || !System.IO.File.Exists(path))
        {
            await RefuseAsync(
                context, 404,
                export.RemovedAt is not null
                    ? "The file was removed: it expired, or it was deleted. Export again."
                    : export.Job.Status != JobStatus.Done
                        ? $"The export is {export.Job.Status.ToString().ToLowerInvariant()}, so there is no file to download."
                        : "The file is not on this server. It may have been written by another server whose export "
                          + "directory this one does not share (Graticula:TileExportPath).")
                .ConfigureAwait(false);
            return;
        }

        await AdminEndpoints.AuditAsync(
            context, audit, "service.data.export.download", service.QualifiedName,
            AdminEndpoints.Detail(new { job = id, range = context.Request.Headers.Range.ToString() }),
            succeeded: true, cancellation).ConfigureAwait(false);

        System.IO.FileInfo info = new(path);

        context.Response.Headers.CacheControl = "private, no-store";

        await TypedResults.PhysicalFile(
                path,
                FeatureExportPackaging.Of(export.Format, export.Layers.Count).MediaType,
                export.FileName,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
                new EntityTagHeaderValue("\"" + export.Token[..16] + info.Length.ToString(CultureInfo.InvariantCulture) + "\""),
                enableRangeProcessing: true)
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The service the route names, if the caller may read it and is signed in; otherwise the refusal is written.
    /// </summary>
    /// <remarks>
    /// <b>404 for a service the caller cannot read, 401 for a caller who is not signed in</b> — nobody anonymous, ever
    /// (owner decision 2, ADR-106) — in that order, which is the order <see cref="LayerExportEndpoints"/> asks them.
    /// </remarks>
    private static async Task<Opened?> OpenAsync(
        HttpContext context, string name, CatalogFallback catalog, CancellationToken cancellation)
    {
        string? folder = context.Request.Query["folder"].ToString() is { Length: > 0 } f ? f : null;

        if (await ServiceLookup.ServiceAsync(context, catalog, name, cancellation, folder ?? string.Empty).ConfigureAwait(false)
            is not { } service)
        {
            return null;
        }

        if (context.Features.Get<RequestPrincipal>() is not { Principal.IsAnonymous: false } caller)
        {
            await RefuseAsync(context, 401, "Sign in to export a layer.").ConfigureAwait(false);
            return null;
        }

        return new Opened(service, caller);
    }

    /// <summary>
    /// The export, if it is of this service and the caller started it or is an administrator; otherwise a 404 is written.
    /// </summary>
    /// <remarks>
    /// <b>The same 404 for an export that is somebody else's as for one that never was</b> — a reader learns nothing of
    /// what another reader has taken.
    /// </remarks>
    private static async Task<FeatureExportState?> OwnedAsync(
        HttpContext context,
        PublishedService service,
        RequestPrincipal caller,
        Guid id,
        IFeatureExportStore exports,
        CancellationToken cancellation)
    {
        if (await exports.FindAsync(id, cancellation).ConfigureAwait(false) is { } export
            && export.ServiceId == service.Id
            && (export.Job.Owner == caller.Principal.Id || IsAdministrator(caller)))
        {
            return export;
        }

        await RefuseAsync(context, 404, $"No export '{id}' of '{service.QualifiedName}'.").ConfigureAwait(false);
        return null;
    }

    private static bool IsAdministrator(RequestPrincipal caller) =>
        LayerAccess.MayManage(null, caller.Principal, caller.Authorization);

    private static bool Manages(PublishedLayer layer, RequestPrincipal caller) =>
        LayerAccess.MayManage(layer.Owner, caller.Principal, caller.Authorization);

    /// <summary>
    /// Whether this caller may export this layer: they manage it, or its served ceiling holds <c>Extract</c> — which the
    /// owner's choice puts there and an unchosen one leaves out (ADR-106 §5.5).
    /// </summary>
    internal static bool MayExport(PublishedLayer layer, RequestPrincipal caller) =>
        Manages(layer, caller) || layer.CapabilityCeiling?.Contains("Extract") == true;

    /// <summary>What a format changes about the data, in words — the dry run's, so nobody finds out from the file.</summary>
    internal static string[] Losses(FeatureExportFormat format) => format switch
    {
        FeatureExportFormat.Excel => ["Attributes only: a workbook carries no geometry.", "Dates are Excel dates."],
        FeatureExportFormat.Kml => ["Coordinates are WGS 84; M values are dropped."],
        FeatureExportFormat.GeoJson => ["Coordinates are WGS 84 (RFC 7946); M values are dropped."],
        FeatureExportFormat.Csv => ["Coordinates are WGS 84: X and Y columns for points, one WKT column for other geometry."],
        FeatureExportFormat.Shapefile =>
        [
            "Field names are cut to ten bytes by the Shapefile format, and text to 254.",
            "One geometry type per layer.",
        ],
        _ => [],
    };

    private static string AddressOf(PublishedService service, Guid job, string tail, string? file = null)
    {
        List<string> query = [];

        if (service.Folder is not null)
        {
            query.Add("folder=" + Uri.EscapeDataString(service.Folder));
        }

        if (file is not null)
        {
            query.Add("file=" + file);
        }

        return $"/admin/services/{Uri.EscapeDataString(service.Name)}/data-exports/{job}{tail}"
            + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query));
    }

    /// <summary>One export on the wire. The download address is given; the token alone is never a field.</summary>
    private static object Wire(FeatureExportState export, PublishedService service)
    {
        bool downloadable = export.Job.Status == JobStatus.Done && export.RemovedAt is null;

        return new
        {
            id = export.Job.Id,
            status = export.Job.Status.ToString().ToLowerInvariant(),
            format = PostgresFeatureExportStore.Wire(export.Format),
            layers = export.Layers.Select(id => new { id, name = service.Layer(id)?.Definition.Name }),
            layerCount = export.Layers.Count,
            phase = export.Job.Status == JobStatus.Running ? export.Phase.ToString().ToLowerInvariant() : null,
            rows = export.RowsTotal,
            rowsWritten = export.RowsWritten,
            percent = export.Job.Progress,
            estimatedBytes = export.EstimatedBytes,
            bytes = export.Bytes,
            fileName = export.FileName,
            owner = export.Job.Owner,
            created = export.Job.Created,
            started = export.Job.Started,
            finished = export.Job.Finished,
            expires = export.ExpiresAt,
            removed = export.RemovedAt,
            failure = export.Job.Failure,
            download = downloadable ? AddressOf(service, export.Job.Id, "/download", export.Token) : null,
            watch = AddressOf(service, export.Job.Id, string.Empty),
        };
    }

    private static Task RefuseAsync(
        HttpContext context, int code, string message, string[]? details = null, object? extra = null) =>
        Results.Json(
            extra is null
                ? new { error = new { code, message, details = details ?? [] } }
                : (object)new { error = new { code, message, details = details ?? [] }, detail = extra },
            statusCode: code)
            .ExecuteAsync(context);
}
