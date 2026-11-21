using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Formats;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Graticula.Platform.Postgres;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Graticula.Host;

/// <summary>
/// ArcGIS's <c>uploads/upload</c> and <c>append</c> on a hosted feature layer — ADR-105.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same engine as Studio's Update data (ADR-103), behind the addresses ArcGIS clients call.</b> The ArcGIS API
/// for Python's <c>FeatureLayer.append</c> uploads a file to the service and then asks the layer to append it by the
/// upload's id; the public reference documents both operations. What this server does with the rows is exactly what
/// <c>POST /admin/hosted/{layer}/append</c> does: one transaction, the layer's schema kept, nothing written if any of
/// it does not fit.
/// </para>
/// <para>
/// <b>What is offered, and what is refused by name.</b> GeoJSON and zipped shapefiles, and since ADR-257 a CSV and a
/// workbook; <c>fieldMappings</c>, <c>appendFields</c>, <c>truncateExisting</c> and, since ADR-116, <c>upsert</c> with
/// <c>upsertMatchingField</c>, <c>skipUpdates</c>, <c>skipInserts</c> and <c>updateGeometry</c>. Since ADR-257 the file
/// may be an item the caller added (<c>appendItemId</c>) as well as an upload. <c>edits</c>, the other formats and
/// <c>async=true</c> are refused with the reason rather than ignored, because a caller told it succeeded at something
/// it did not do builds on it (ADR-008 §2).
/// </para>
/// <para>
/// <b>An upload is kept for an hour, on this node, for the caller who made it.</b> It is a file waiting to be
/// appended, not an item; a second node behind a balancer does not see it, which is recorded in ADR-105.
/// </para>
/// </remarks>
internal static partial class ArcGisAppendEndpoints
{
    /// <summary>How long an upload waits to be appended.</summary>
    private static readonly TimeSpan Kept = TimeSpan.FromHours(1);

    /// <summary>How many uploads may wait at once; past it an upload is refused until one is appended or expires.</summary>
    internal const int MaximumUploads = 64;

    private sealed record Upload(string Path, string Name, string Owner, DateTimeOffset At);

    private static readonly ConcurrentDictionary<string, Upload> Uploads = new(StringComparer.Ordinal);

    /// <summary>The handoff kind uploads wait under on a site (ADR-012 phase 2).</summary>
    private const string UploadKind = "append-uploads";

    /// <summary>Maps the routes.</summary>
    /// <param name="app">The application.</param>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        foreach (string folder in (string[])["", "/{folder}"])
        {
            app.MapPost($"/rest/services{folder}/{{serviceName}}/FeatureServer/uploads/upload", UploadAsync)
                .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
            app.MapPost($"/rest/services{folder}/{{serviceName}}/FeatureServer/{{layerId:int}}/append", AppendAsync)
                .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();

            // ADR-310: the same operation on the service, over several layers, answered with each layer's count.
            app.MapPost($"/rest/services{folder}/{{serviceName}}/FeatureServer/append", ServiceAppendAsync)
                .Governed(SharingGovernedExtensions.ByPrivilege).DisableAntiforgery();
        }
    }

    private static async Task UploadAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback services,
        ImportScratch scratch,
        CancellationToken cancellation)
    {
        string? folder = context.Request.RouteValues.TryGetValue("folder", out object? given) ? given as string : null;

        // ADR-288: a field worker who may edit a sync-enabled service uploads the edits they made offline here, and a
        // field worker does not publish. Everybody else needs what an append needs, asked first as it always was.
        if (!await OfflineReplicaEndpoints.MayUploadEditsAsync(context, services, folder, serviceName, cancellation).ConfigureAwait(false)
            && !await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        CatalogAnswer answer = await services.FindServiceAsync(folder, serviceName, cancellation).ConfigureAwait(false);

        if (answer.Service is null)
        {
            await RefuseAsync(context, 404, $"There is no feature service '{Named(folder, serviceName)}'.").ConfigureAwait(false);
            return;
        }

        if (!context.Request.HasFormContentType)
        {
            await RefuseAsync(context, 400, "Post the file as multipart/form-data with a 'file' field.").ConfigureAwait(false);
            return;
        }

        IFormCollection form;
        HostedDataEndpoints.AllowUpload(context);

        try
        {
            form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidDataException
            or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            await RefuseAsync(context, 413, $"The upload is larger than this server accepts. {HostedDataEndpoints.LargerLayer} ({e.Message})").ConfigureAwait(false);
            return;
        }

        if (form.Files.GetFile("file") is not { } file)
        {
            await RefuseAsync(context, 400, "A 'file' part is required.").ConfigureAwait(false);
            return;
        }

        if (file.Length > HostedDataEndpoints.MaximumBytes(context))
        {
            await RefuseAsync(context, 413,
                $"The file is {file.Length / 1048576} MB and the limit is {HostedDataEndpoints.MaximumBytes(context) / 1048576} MB. {HostedDataEndpoints.LargerLayer}")
                .ConfigureAwait(false);
            return;
        }

        Forget();

        // The bound on what waits: a file is disk, and an upload nobody appends is kept an hour.
        if (Uploads.Count >= MaximumUploads)
        {
            await RefuseAsync(context, 503,
                $"{MaximumUploads} uploads are already waiting to be appended here. Append or wait for one to expire.")
                .ConfigureAwait(false);
            return;
        }

        string id = "i" + Guid.NewGuid().ToString("N");
        string directory = System.IO.Path.Combine(scratch.Directory, "uploads");
        System.IO.Directory.CreateDirectory(directory);

        // ADR-012 phase 2: on a site the bytes wait in the shared server directories, so the append may reach any node.
        string path = SiteHandoff.FileOf(UploadKind, id) ?? System.IO.Path.Combine(directory, id);

        try
        {
            await using FileStream written = File.Create(path);
            await file.CopyToAsync(written, cancellation).ConfigureAwait(false);
        }
        catch
        {
            // ADR-139's review: an upload stopped part way left its bytes here, kept by nothing that would remove them.
            File.Delete(path);
            throw;
        }

        string owner = context.Features.Get<RequestPrincipal>()?.Principal.Id.ToString() ?? string.Empty;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string fileName = System.IO.Path.GetFileName(file.FileName ?? "upload");
        Uploads[id] = new Upload(path, fileName, owner, now);
        SiteHandoff.Save(UploadKind, id, Uploads[id]);

        await Results.Json(new
        {
            success = true,
            item = new
            {
                itemID = id,
                itemName = fileName,
                description = form["description"].ToString(),
                date = now.ToUnixTimeMilliseconds(),
                committed = true,
            },
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>One layer an append writes into, and how it is written.</summary>
    /// <param name="Layer">The layer.</param>
    /// <param name="Registered">
    /// Whether it is a registered layer in somebody's database, written through the layer's edit writer (ADR-310), rather
    /// than a hosted one written by the importer (ADR-103).
    /// </param>
    /// <param name="Mappings">Its <c>fieldMappings</c>, source to target, or null.</param>
    /// <param name="Only">Its <c>appendFields</c>, or null.</param>
    private sealed record Target(
        PublishedLayer Layer, bool Registered, IReadOnlyDictionary<string, string>? Mappings, IReadOnlyCollection<string>? Only);

    /// <summary>What every append is, whichever layer or service it names, and how many of it was written.</summary>
    private sealed record Written(PublishedLayer Layer, int Records);

    private static async Task AppendAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback services,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        LayerConnections connections,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        IJobStore jobs,
        JobSignal signal,
        GeodatabaseReader reader,
        ImportScratch scratch,
        ThumbnailWarmer warmer,
        CancellationToken cancellation)
    {
        IFormCollection form = await FormOfAsync(context, cancellation).ConfigureAwait(false);

        bool replace = IsTrue(form["truncateExisting"]);

        if (await ResolveAsync(context, services, layers, serviceName, layerId, replace, cancellation).ConfigureAwait(false)
            is not { } resolved)
        {
            return;
        }

        IReadOnlyDictionary<string, string>? mappings;
        IReadOnlyCollection<string>? only;

        try
        {
            mappings = Mappings(form["fieldMappings"]);
            only = Only(form["appendFields"]);
        }
        catch (JsonException e)
        {
            await RefuseAsync(context, 400, $"'fieldMappings' or 'appendFields' is not the JSON ArcGIS describes: {e.Message}")
                .ConfigureAwait(false);
            return;
        }

        Target target = resolved with { Mappings = mappings, Only = only };

        // ADR-116: upsert, matched on the field named — the nightly sync's way.
        Upsert? upsert = HostedDataEndpoints.UpsertOf(form, out string? upsertWhy);

        if (await RefusedAsync(context, form, [target], replace, upsertWhy).ConfigureAwait(false))
        {
            return;
        }

        string format = form["appendUploadFormat"].ToString().Trim();
        DateTimeOffset submitted = DateTimeOffset.UtcNow;

        if (await LoadAsync(context, form, format, jobs, signal, reader, scratch, cancellation).ConfigureAwait(false)
            is not { } loaded)
        {
            return;
        }

        if (await WriteAsync(
                context, target, loaded.Dataset, replace, upsert, importer, contexts, connections, tiles, catalog, audit, warmer,
                cancellation).ConfigureAwait(false) is not { } written)
        {
            return;
        }

        loaded.Spend();

        await Results.Json(new
        {
            layerName = written.Layer.Definition.Name,
            submissionTime = submitted.ToUnixTimeMilliseconds(),
            lastUpdatedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            recordCount = written.Records,
            status = "Completed",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// ArcGIS's <c>append</c> on the service — the same operation over several layers, answered with the layers' counts
    /// (ADR-310; the public reference is <c>append-feature-service</c>).
    /// </summary>
    /// <remarks>
    /// <b>Not a second implementation.</b> Each layer is resolved, refused and written by the code the layer's own
    /// <c>append</c> uses; this one names the layers, gives each its <c>fieldMappings</c>, reads the file once and answers
    /// with <c>layers</c>. A file this server reads holds one table, so one <c>layerMappings</c> entry's
    /// <c>sourceTableName</c> is accepted for whatever it says (INFERRED, ADR-310 §5.4).
    /// </remarks>
    private static async Task ServiceAppendAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback services,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
        LayerConnections connections,
        ITileCache tiles,
        IAdminCatalog catalog,
        IAuditLog audit,
        IJobStore jobs,
        JobSignal signal,
        GeodatabaseReader reader,
        ImportScratch scratch,
        ThumbnailWarmer warmer,
        CancellationToken cancellation)
    {
        IFormCollection form = await FormOfAsync(context, cancellation).ConfigureAwait(false);
        string? folder = context.Request.RouteValues.TryGetValue("folder", out object? given) ? given as string : null;

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        CatalogAnswer answer = await services.FindServiceAsync(folder, serviceName, cancellation).ConfigureAwait(false);

        if (answer.Service is null)
        {
            await RefuseAsync(context, 404, $"There is no feature service '{Named(folder, serviceName)}'.").ConfigureAwait(false);
            return;
        }

        List<int> ids;
        Dictionary<int, IReadOnlyDictionary<string, string>?> mappings = [];

        try
        {
            ids = LayerIds(form["layers"], form["layerMappings"], mappings, answer.Service, out string? why);

            if (why is not null)
            {
                await RefuseAsync(context, 400, why).ConfigureAwait(false);
                return;
            }
        }
        catch (JsonException e)
        {
            await RefuseAsync(context, 400, $"'layers' or 'layerMappings' is not the JSON ArcGIS describes: {e.Message}")
                .ConfigureAwait(false);
            return;
        }

        Upsert? upsert = HostedDataEndpoints.UpsertOf(form, out string? upsertWhy);
        List<Target> targets = [];

        foreach (int id in ids)
        {
            // The layer's own door: the privilege, whose layer it is, and hosted or registered — every layer before any write.
            if (await ResolveAsync(context, services, layers, serviceName, id, replace: false, cancellation).ConfigureAwait(false)
                is not { } resolved)
            {
                return;
            }

            targets.Add(resolved with { Mappings = mappings.GetValueOrDefault(id) });
        }

        if (await RefusedAsync(context, form, targets, replace: false, upsertWhy).ConfigureAwait(false))
        {
            return;
        }

        string format = form["appendUploadFormat"].ToString().Trim();
        DateTimeOffset submitted = DateTimeOffset.UtcNow;

        if (await LoadAsync(context, form, format, jobs, signal, reader, scratch, cancellation).ConfigureAwait(false)
            is not { } loaded)
        {
            return;
        }

        List<Written> done = [];

        foreach (Target target in targets)
        {
            if (await WriteAsync(
                    context, target, loaded.Dataset, replace: false, upsert, importer, contexts, connections, tiles, catalog, audit,
                    warmer, cancellation).ConfigureAwait(false) is not { } written)
            {
                // The refusal is written; the layers before it were written, each in its own transaction.
                return;
            }

            done.Add(written);
        }

        loaded.Spend();

        await Results.Json(new
        {
            recordCount = done.Sum(w => w.Records),
            layers = done.Select(w => new
            {
                recordCount = w.Records,
                attachmentCount = 0,
                id = w.Layer.LayerIndex,
                layerName = w.Layer.Definition.Name,
            }),
            operationName = "append",
            lastUpdatedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            layerName = done[^1].Layer.Definition.Name,
            submissionTime = submitted.ToUnixTimeMilliseconds(),
            status = "Completed",
        }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// The layers a service-level append writes: <c>layers</c>, or the ids <c>layerMappings</c> names, or the service's one
    /// layer; and each one's field mappings, put in <paramref name="mappings"/>.
    /// </summary>
    private static List<int> LayerIds(
        string? layersJson, string? mappingsJson, Dictionary<int, IReadOnlyDictionary<string, string>?> mappings,
        PublishedService service, out string? why)
    {
        why = null;
        List<int> ids = [];

        if (!string.IsNullOrWhiteSpace(layersJson))
        {
            foreach (JsonElement one in JsonDocument.Parse(layersJson).RootElement.EnumerateArray())
            {
                if (one.ValueKind != JsonValueKind.Number || !one.TryGetInt32(out int id))
                {
                    why = "'layers' is a JSON array of layer ids, such as [0,1].";
                    return ids;
                }

                if (!ids.Contains(id)) ids.Add(id);
            }
        }

        List<int> mapped = [];

        if (!string.IsNullOrWhiteSpace(mappingsJson))
        {
            foreach (JsonElement one in JsonDocument.Parse(mappingsJson).RootElement.EnumerateArray())
            {
                if (!one.TryGetProperty("id", out JsonElement idElement) || !idElement.TryGetInt32(out int id))
                {
                    why = "Each 'layerMappings' entry needs the 'id' of the layer it maps into.";
                    return ids;
                }

                if (mapped.Contains(id))
                {
                    why = $"'layerMappings' maps into layer {id} twice. Only one source can map to a layer.";
                    return ids;
                }

                mapped.Add(id);
                mappings[id] = one.TryGetProperty("fieldMappings", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array
                    ? Mappings(fields.GetRawText())
                    : null;
            }
        }

        if (ids.Count == 0)
        {
            ids.AddRange(mapped);
        }
        else if (mapped.Except(ids).ToList() is { Count: > 0 } stray)
        {
            why = $"'layerMappings' names layer {stray[0]}, which 'layers' does not.";
            return ids;
        }

        if (ids.Count == 0 && service.LayersAndTables.Count == 1)
        {
            ids.Add(service.LayersAndTables[0].LayerIndex);
        }

        if (ids.Count == 0)
        {
            why = "Name the layers to append into with 'layers' (or 'layerMappings'): this service has "
                + $"{service.LayersAndTables.Count}.";
        }
        else if (ids.Count > 1 && mapped.Count == 0)
        {
            why = "A file this server reads holds one table, so appending it into several layers needs 'layerMappings' to say "
                + "that is what is meant for each. Nothing was written.";
        }

        return ids;
    }

    /// <summary>The form of an append, or the query string's parameters when it was not posted as one.</summary>
    private static async Task<IFormCollection> FormOfAsync(HttpContext context, CancellationToken cancellation) =>
        context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : new FormCollection(context.Request.Query.ToDictionary(q => q.Key, q => q.Value));

    /// <summary>
    /// The layer an append names, after the privilege — hosted, written by the importer, or registered, written through
    /// the layer's writer (ADR-310) — or the refusal, and null.
    /// </summary>
    private static async Task<Target?> ResolveAsync(
        HttpContext context, CatalogFallback services, PostgresLayerCatalog layers, string serviceName, int layerId, bool replace,
        CancellationToken cancellation)
    {
        string? folder = context.Request.RouteValues.TryGetValue("folder", out object? given) ? given as string : null;
        CatalogAnswer answer = await services.FindServiceAsync(folder, serviceName, cancellation).ConfigureAwait(false);

        // <b>Registered is a layer the importer may not write</b>: one whose source is not the datastore, and also one the
        // datastore serves from a schema this server did not create (ADR-058 §5h) — its table is somebody's, and the
        // layer's writer is the door to it.
        if (answer.Service?.LayerOrTable(layerId) is { } probe
            && !HostedDataEndpoints.AlterableSchema(probe.Definition.IsHosted, probe.Definition.SchemaName))
        {
            // A layer the caller cannot read is a 404 from the lookup, as for every other FeatureServer route.
            if (await ServiceLookup.LayerAsync(context, services, serviceName, layerId, cancellation).ConfigureAwait(false)
                is not { } registered)
            {
                return null;
            }

            // The rows only: appending is adding features, and asks what adding features asks (ADR-075, ArcGIS applyEdits).
            if (!await Authorize.RequireEditAsync(
                    context, Privilege.FeaturesEdit, registered, addsOnly: true, updatesThroughView: false).ConfigureAwait(false))
            {
                return null;
            }

            return new Target(registered, true, null, null);
        }

        return await ArcGisAdminEndpoints.HostedLayerAtAsync(
                context, services, layers, serviceName, layerId, replace ? "overwrite" : "append to", cancellation)
            .ConfigureAwait(false) is { } found
            ? new Target(found, false, null, null)
            : null;
    }

    /// <summary>
    /// The refusals every append shares, before a file is read: <c>upsert</c> and <c>truncateExisting</c> where the layer
    /// is registered, <c>async</c>, <c>edits</c> and a format this server does not read. True when one was written.
    /// </summary>
    private static async Task<bool> RefusedAsync(
        HttpContext context, IFormCollection form, IReadOnlyList<Target> targets, bool replace, string? upsertWhy)
    {
        if (upsertWhy is not null)
        {
            await RefuseAsync(context, 400, upsertWhy).ConfigureAwait(false);
            return true;
        }

        if (targets.FirstOrDefault(t => t.Registered) is { } registered)
        {
            string name = registered.Layer.Definition.Name;

            if (replace)
            {
                await RefuseAsync(context, 400,
                    $"'{name}' is a registered layer, so 'truncateExisting' is not offered: emptying a table in the database it "
                    + "was registered from is the owner of that database's to do. Append adds features. Nothing was written.")
                    .ConfigureAwait(false);
                return true;
            }

            if (HostedDataEndpoints.UpsertOf(form, out _) is not null)
            {
                await RefuseAsync(context, 400,
                    $"'{name}' is a registered layer, so 'upsert' is not offered: a registered layer is appended to, and "
                    + "applyEdits updates the features. Nothing was written.").ConfigureAwait(false);
                return true;
            }
        }

        if (IsTrue(form["async"]))
        {
            await RefuseAsync(context, 400,
                "'async=true' is not offered: append runs in one transaction and answers when it is done. Send async=false.")
                .ConfigureAwait(false);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(form["edits"]))
        {
            await RefuseAsync(context, 400,
                "'edits' is not read: upload the file to this service's uploads/upload, or add it as an item, first. "
                + "Nothing was written.")
                .ConfigureAwait(false);
            return true;
        }

        string format = form["appendUploadFormat"].ToString().Trim();

        if (!Formats.Contains(format))
        {
            await RefuseAsync(context, 400,
                $"'appendUploadFormat' is '{format}'; this server appends {string.Join(", ", Formats.Select(f => $"'{f}'"))}. "
                + "Nothing was written.")
                .ConfigureAwait(false);
            return true;
        }

        return false;
    }

    /// <summary>A source file read, and what spends it once it has been written.</summary>
    private sealed record Loaded(ImportedDataset Dataset, Action Spend);

    /// <summary>
    /// The file <c>appendUploadId</c> or <c>appendItemId</c> names, read as <paramref name="format"/>; or null with the
    /// refusal written.
    /// </summary>
    private static async Task<Loaded?> LoadAsync(
        HttpContext context, IFormCollection form, string format, IJobStore jobs, JobSignal signal, GeodatabaseReader reader,
        ImportScratch scratch, CancellationToken cancellation)
    {
        string id = form["appendUploadId"].ToString().Trim();
        string itemId = form["appendItemId"].ToString().Trim();
        RequestPrincipal? caller = context.Features.Get<RequestPrincipal>();
        string owner = caller?.Principal.Id.ToString() ?? string.Empty;

        Forget();

        Upload upload;
        byte[]? itemContent = null;

        // ADR-257: an item the caller added (a document) is appended as an upload is — the same read, the same write.
        if (itemId.Length > 0)
        {
            if (caller is null || await ItemFileAsync(context, caller, itemId, format, cancellation).ConfigureAwait(false)
                    is not { } item)
            {
                return null;
            }

            (upload, itemContent) = item;
        }
        else if ((Uploads.TryGetValue(id, out Upload? held) ? held : SiteHandoff.Load<Upload>(UploadKind, id)) is not { } waiting
            || !string.Equals(waiting.Owner, owner, StringComparison.Ordinal))
        {
            await RefuseAsync(context, 400,
                $"No upload '{id}' of yours is waiting here. An upload is kept for an hour, on the server it was sent to.")
                .ConfigureAwait(false);
            return null;
        }
        else
        {
            upload = waiting;
        }

        ImportedDataset? dataset;

        await using (Stream stream = itemContent is not null ? new MemoryStream(itemContent, writable: false) : File.OpenRead(upload.Path))
        {
            FormFile file = new(stream, 0, stream.Length, "file", upload.Name);

            dataset = string.Equals(format, "jsonl", StringComparison.OrdinalIgnoreCase)
                ? await ReadJsonLinesAsync(context, file, cancellation).ConfigureAwait(false)
                : await HostedDataEndpoints.ReadUpdateFileAsync(context, form, file, jobs, signal, reader, scratch, cancellation)
                    .ConfigureAwait(false);
        }

        if (dataset is null)
        {
            return null;
        }

        // Appended once: the upload is spent. An item is not: it stays the caller's, as it was.
        return new Loaded(dataset, () =>
        {
            if (itemContent is not null) return;
            if (Uploads.TryRemove(id, out Upload? spent)) TryDelete(spent.Path);
            SiteHandoff.Remove(UploadKind, id);
        });
    }

    /// <summary>
    /// Writes a dataset into one layer — through the importer into a hosted one, through the layer's writer into a
    /// registered one — and says how many features that was; or null with the refusal written.
    /// </summary>
    private static async Task<Written?> WriteAsync(
        HttpContext context, Target target, ImportedDataset dataset, bool replace, Upsert? upsert, PostGisImporter importer,
        ServiceContexts contexts, LayerConnections connections, ITileCache tiles, IAdminCatalog catalog, IAuditLog audit,
        ThumbnailWarmer warmer, CancellationToken cancellation)
    {
        if (target.Registered)
        {
            return await AppendRegisteredAsync(context, target, dataset, contexts, connections, audit, cancellation)
                .ConfigureAwait(false);
        }

        AppendResult? result = await HostedDataEndpoints.WriteUpdateAsync(
            context, target.Layer, dataset, replace, importer, contexts, tiles, catalog, audit, warmer, cancellation,
            target.Mappings, target.Only, upsert).ConfigureAwait(false);

        return result is null ? null : new Written(target.Layer, result.Rows + result.Updated);
    }

    /// <summary>
    /// A JSON Lines file — one GeoJSON feature on each line — read as the GeoJSON feature collection it is the lines of.
    /// </summary>
    private static async Task<ImportedDataset?> ReadJsonLinesAsync(HttpContext context, FormFile file, CancellationToken cancellation)
    {
        using MemoryStream joined = new();

        await using (Utf8JsonWriter writer = new(joined))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "FeatureCollection");
            writer.WriteStartArray("features");

            using StreamReader lines = new(file.OpenReadStream(), System.Text.Encoding.UTF8);
            int number = 0;

            while (await lines.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                number++;

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using JsonDocument feature = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });

                    if (feature.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new JsonException("a feature is a JSON object");
                    }

                    feature.RootElement.WriteTo(writer);
                }
                catch (JsonException e)
                {
                    await RefuseAsync(context, 400,
                        $"Line {number} of the JSON Lines file is not a GeoJSON feature: {e.Message}. Nothing was written.")
                        .ConfigureAwait(false);
                    return null;
                }
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        joined.Position = 0;

        JsonElement root = (await JsonDocument.ParseAsync(joined, new JsonDocumentOptions { MaxDepth = 32 }, cancellation)
            .ConfigureAwait(false)).RootElement;

        if (!GeoJsonFeatures.TryRead(root, HostedDataEndpoints.Limits(context), out ImportedDataset? dataset, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return null;
        }

        return dataset;
    }

    /// <summary>
    /// The <c>appendUploadFormat</c>s read — what <c>HostedDataEndpoints.ReadUpdateFileAsync</c> reads: GeoJSON and a
    /// zipped shapefile (ADR-105), and a CSV or a workbook since ADR-257, which that reader already took for Update data.
    /// </summary>
    internal static readonly HashSet<string> Formats = new(StringComparer.OrdinalIgnoreCase) { "geojson", "jsonl", "shapefile", "csv", "excel" };

    /// <summary>The ArcGIS item type each format is added as, for <c>appendItemId</c>.</summary>
    private static readonly FrozenDictionary<string, string> ItemTypes = new Dictionary<string, string>
    {
        ["geojson"] = "GeoJson",
        ["shapefile"] = "Shapefile",
        ["csv"] = "CSV",
        ["excel"] = "Microsoft Excel",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The file of the item <c>appendItemId</c> names — a document the caller may read, of the format asked — as an
    /// upload named for its file and its bytes; or null, with the refusal written.
    /// </summary>
    private static async Task<(Upload Upload, byte[] Content)?> ItemFileAsync(
        HttpContext context, RequestPrincipal caller, string itemId, string format, CancellationToken cancellation)
    {
        IDocumentStore? documents = context.RequestServices.GetService(typeof(IDocumentStore)) as IDocumentStore;

        if (documents is null || !Guid.TryParse(itemId, out _)
            || await documents.FindAsync(itemId.ToLowerInvariant(), cancellation).ConfigureAwait(false) is not { } document
            || !LayerAccess.Evaluate(document.Sharing, document.Owner, caller.Principal, caller.Authorization, document.SharedWith).IsAllowed())
        {
            await RefuseAsync(context, 400, $"No item '{itemId}' that you may read is here. Nothing was written.").ConfigureAwait(false);
            return null;
        }

        if (!ItemTypes.TryGetValue(format, out string? expected))
        {
            await RefuseAsync(context, 400,
                $"'appendUploadFormat' {format} is appended from an upload (uploads/upload), not from an item. Nothing was written.")
                .ConfigureAwait(false);
            return null;
        }

        if (!string.Equals(document.Kind.ArcGisType, expected, StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(context, 400,
                $"'{document.Title}' is a {document.Kind.ArcGisType} item and 'appendUploadFormat' says {format}. Nothing was written.")
                .ConfigureAwait(false);
            return null;
        }

        if (await documents.ContentAsync(document.Id, cancellation).ConfigureAwait(false) is not { } content)
        {
            await RefuseAsync(context, 400, $"'{document.Title}' has no file. Nothing was written.").ConfigureAwait(false);
            return null;
        }

        return (new Upload(string.Empty, document.FileName, caller.Principal.Id.ToString(), DateTimeOffset.UtcNow), content);
    }

    /// <summary>ArcGIS <c>fieldMappings</c>: <c>[{"name": target, "source": source}]</c>, as source → target.</summary>
    internal static Dictionary<string, string>? Mappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        Dictionary<string, string> map = new(StringComparer.Ordinal);

        foreach (JsonElement one in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string? target = one.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
            string? source = one.TryGetProperty("source", out JsonElement s) ? s.GetString() : null;
            if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(source)) map[source] = target;
        }

        return map;
    }

    /// <summary>ArcGIS <c>appendFields</c>: the layer's columns to write.</summary>
    private static HashSet<string>? Only(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? null
            : [.. JsonDocument.Parse(json).RootElement.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];

    /// <summary>
    /// The file of an upload waiting for this caller, or null — for <c>synchronizeReplica</c>'s <c>editsUploadID</c>
    /// (ADR-288), which reads the delta a client uploaded here.
    /// </summary>
    /// <param name="id">The upload's id.</param>
    /// <param name="owner">The caller's principal id, as the upload recorded it.</param>
    /// <returns>The file's path, or null when no upload of theirs is waiting.</returns>
    internal static string? WaitingUploadOf(string id, string owner)
    {
        Forget();

        return (Uploads.TryGetValue(id, out Upload? held) ? held : SiteHandoff.Load<Upload>(UploadKind, id)) is { } waiting
            && string.Equals(waiting.Owner, owner, StringComparison.Ordinal)
            ? waiting.Path
            : null;
    }

    /// <summary>Removes an upload that has been used, and its file.</summary>
    /// <param name="id">The upload's id.</param>
    internal static void Spend(string id)
    {
        if (Uploads.TryRemove(id, out Upload? spent))
        {
            TryDelete(spent.Path);
        }

        SiteHandoff.Remove(UploadKind, id);
    }

    /// <summary>Drops the uploads older than an hour.</summary>
    private static void Forget()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow - Kept;
        SiteHandoff.Sweep(UploadKind, Kept);

        foreach ((string id, Upload upload) in Uploads)
        {
            if (upload.At < before && Uploads.TryRemove(id, out _)) TryDelete(upload.Path);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { /* a scratch file that will not go now goes with the scratch directory */ }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsTrue(string? value) => (value ?? string.Empty).Trim().Equals("true", StringComparison.OrdinalIgnoreCase);

    private static string Named(string? folder, string service) => folder is null ? service : $"{folder}/{service}";

    private static Task RefuseAsync(HttpContext context, int code, string message) =>
        Results.Json(new { error = new { code, message, details = Array.Empty<string>() } }, statusCode: code).ExecuteAsync(context);
}
