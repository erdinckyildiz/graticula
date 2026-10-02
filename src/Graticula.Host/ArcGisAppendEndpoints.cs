using System;
using System.Collections.Concurrent;
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
/// <b>What is offered, and what is refused by name.</b> GeoJSON and zipped shapefiles; <c>fieldMappings</c>,
/// <c>appendFields</c>, <c>truncateExisting</c> and, since ADR-116, <c>upsert</c> with <c>upsertMatchingField</c>,
/// <c>skipUpdates</c>, <c>skipInserts</c> and <c>updateGeometry</c>. <c>appendItemId</c>, the other formats and
/// <c>async=true</c> are refused with the reason rather than ignored, because a caller told it succeeded at something
/// it did not do builds on it (ADR-008 §2).
/// </para>
/// <para>
/// <b>An upload is kept for an hour, on this node, for the caller who made it.</b> It is a file waiting to be
/// appended, not an item; a second node behind a balancer does not see it, which is recorded in ADR-105.
/// </para>
/// </remarks>
internal static class ArcGisAppendEndpoints
{
    /// <summary>How long an upload waits to be appended.</summary>
    private static readonly TimeSpan Kept = TimeSpan.FromHours(1);

    /// <summary>How many uploads may wait at once; past it an upload is refused until one is appended or expires.</summary>
    internal const int MaximumUploads = 64;

    private sealed record Upload(string Path, string Name, string Owner, DateTimeOffset At);

    private static readonly ConcurrentDictionary<string, Upload> Uploads = new(StringComparer.Ordinal);

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
        }
    }

    private static async Task UploadAsync(
        HttpContext context,
        string serviceName,
        CatalogFallback services,
        ImportScratch scratch,
        CancellationToken cancellation)
    {
        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false))
        {
            return;
        }

        string? folder = context.Request.RouteValues.TryGetValue("folder", out object? given) ? given as string : null;
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

        try
        {
            form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        }
        catch (InvalidDataException e)
        {
            await RefuseAsync(context, 413, $"The upload is larger than this server accepts. ({e.Message})").ConfigureAwait(false);
            return;
        }

        if (form.Files.GetFile("file") is not { } file)
        {
            await RefuseAsync(context, 400, "A 'file' part is required.").ConfigureAwait(false);
            return;
        }

        if (file.Length > HostedDataEndpoints.MaximumBytes)
        {
            await RefuseAsync(context, 413,
                $"The file is {file.Length / 1048576} MB and the limit is {HostedDataEndpoints.MaximumBytes / 1048576} MB.")
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
        string path = System.IO.Path.Combine(directory, id);

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

    private static async Task AppendAsync(
        HttpContext context,
        string serviceName,
        int layerId,
        CatalogFallback services,
        PostgresLayerCatalog layers,
        PostGisImporter importer,
        ServiceContexts contexts,
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
        IFormCollection form = context.Request.HasFormContentType
            ? await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false)
            : new FormCollection(context.Request.Query.ToDictionary(q => q.Key, q => q.Value));

        bool replace = IsTrue(form["truncateExisting"]);

        if (await ArcGisAdminEndpoints.HostedLayerAtAsync(
                context, services, layers, serviceName, layerId, replace ? "overwrite" : "append to", cancellation)
            .ConfigureAwait(false) is not { } found)
        {
            return;
        }

        // ADR-116: upsert, matched on the field named — the nightly sync's way.
        Upsert? upsert = HostedDataEndpoints.UpsertOf(form, out string? upsertWhy);

        if (upsertWhy is not null)
        {
            await RefuseAsync(context, 400, upsertWhy).ConfigureAwait(false);
            return;
        }

        if (IsTrue(form["async"]))
        {
            await RefuseAsync(context, 400,
                "'async=true' is not offered: append runs in one transaction and answers when it is done. Send async=false.")
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(form["appendItemId"]) || !string.IsNullOrWhiteSpace(form["edits"]))
        {
            await RefuseAsync(context, 400,
                "Only 'appendUploadId' is read: upload the file to this service's uploads/upload first. Nothing was written.")
                .ConfigureAwait(false);
            return;
        }

        string format = form["appendUploadFormat"].ToString().Trim();

        if (!format.Equals("geojson", StringComparison.OrdinalIgnoreCase)
            && !format.Equals("shapefile", StringComparison.OrdinalIgnoreCase))
        {
            await RefuseAsync(context, 400,
                $"'appendUploadFormat' is '{format}'; this server appends 'geojson' and 'shapefile'. Nothing was written.")
                .ConfigureAwait(false);
            return;
        }

        string id = form["appendUploadId"].ToString().Trim();
        string owner = context.Features.Get<RequestPrincipal>()?.Principal.Id.ToString() ?? string.Empty;

        Forget();

        if (!Uploads.TryGetValue(id, out Upload? upload) || !string.Equals(upload.Owner, owner, StringComparison.Ordinal))
        {
            await RefuseAsync(context, 400,
                $"No upload '{id}' of yours is waiting here. An upload is kept for an hour, on the server it was sent to.")
                .ConfigureAwait(false);
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

        DateTimeOffset submitted = DateTimeOffset.UtcNow;
        AppendResult? result;

        await using (FileStream stream = File.OpenRead(upload.Path))
        {
            FormFile file = new(stream, 0, stream.Length, "file", upload.Name);

            if (await HostedDataEndpoints.ReadUpdateFileAsync(context, form, file, jobs, signal, reader, scratch, cancellation)
                .ConfigureAwait(false) is not { } dataset)
            {
                return;
            }

            result = await HostedDataEndpoints.WriteUpdateAsync(
                context, found, dataset, replace, importer, contexts, tiles, catalog, audit, warmer, cancellation, mappings, only,
                upsert)
                .ConfigureAwait(false);
        }

        if (result is null)
        {
            return;
        }

        // Appended once: the upload is spent.
        if (Uploads.TryRemove(id, out Upload? spent)) TryDelete(spent.Path);

        await Results.Json(new
        {
            layerName = found.Definition.Name,
            submissionTime = submitted.ToUnixTimeMilliseconds(),
            lastUpdatedTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            recordCount = result.Rows + result.Updated,
            status = "Completed",
        }).ExecuteAsync(context).ConfigureAwait(false);
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

    /// <summary>Drops the uploads older than an hour.</summary>
    private static void Forget()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow - Kept;

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
