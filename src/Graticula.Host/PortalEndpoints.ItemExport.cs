using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Jobs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// The portal's <c>export</c>: a feature layer's data as a new item of its caller's, as the ArcGIS API for Python's
/// <c>item.export()</c> asks for it (ADR-263, the ArcGIS veteran's review of v1.0.499, #6).
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-106's revisit trigger, met.</b> It refused the item because there was no item table and no <c>addItem</c>; both
/// exist now. The export is ADR-106's own — Studio's Start, run inside this request, under its rules of who may export
/// and with its formats, caps and audit line — and the item is a document of the caller's, made at once so a client can
/// open it and ask its status, and filled with the file when the job has written it.
/// </para>
/// <para>
/// <b>Which export an item is waiting for is held in this process.</b> A restart before the job finishes leaves the item
/// empty and says so when asked; the file itself is still in Studio's exports for its retention.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>The exports whose items are waiting for their file: item id → the job, who started it, and when.</summary>
    private static readonly ConcurrentDictionary<string, (Guid Job, Guid Owner, DateTimeOffset At)> WaitingExports =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many exports may wait for their file at once; past it the oldest is forgotten, its item left empty.</summary>
    private const int MaximumWaitingExports = 256;

    private static void MapItemExport(WebApplication app)
    {
        foreach (string user in new[] { $"{Path}/content/users/{{username}}", $"{Path}/content/users/{{username}}/{{folderId:length(32)}}" })
        {
            app.MapPost($"{user}/export", ExportItemAsync).Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        }
    }

    /// <summary>
    /// <c>export</c>: <c>itemId</c>, <c>exportFormat</c>, <c>title</c> and <c>exportParameters</c> (its <c>layers</c>), answered
    /// as the portal answers — the new item's id, the job's, the format.
    /// </summary>
    private static async Task<IResult> ExportItemAsync(HttpContext context, string username, CancellationToken cancellation)
    {
        if (VectorTileExportEndpoints.CrossSiteByCookie(context) is not null)
        {
            return PortalError(403, "Send a token to export from another site.");
        }

        if (await ActingForAsync(context, username, cancellation).ConfigureAwait(false) is not { Caller: var current })
        {
            return Unknown("User");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string itemId = form["itemId"].ToString().Trim();
        string format = form["exportFormat"].ToString().Trim();

        if (await WritableItemAsync(context, itemId, cancellation).ConfigureAwait(false) is not { Service: { } service })
        {
            return PortalError(400, "Only a feature layer's data is exported here; that item is not one, or does not exist.");
        }

        IReadOnlyList<int>? layers = null;
        if (form["exportParameters"].ToString() is { Length: > 0 } parameters)
        {
            try
            {
                using JsonDocument said = JsonDocument.Parse(parameters);
                if (said.RootElement.TryGetProperty("layers", out JsonElement chosen) && chosen.ValueKind == JsonValueKind.Array)
                {
                    layers = [.. chosen.EnumerateArray()
                        .Select(l => l.ValueKind == JsonValueKind.Object && l.TryGetProperty("id", out JsonElement id) ? id : l)
                        .Where(id => id.ValueKind == JsonValueKind.Number).Select(id => id.GetInt32())];
                }
            }
            catch (JsonException)
            {
                return PortalError(400, "exportParameters is not JSON.");
            }
        }

        int layerCount = layers is { Count: > 0 } ? layers.Count : service.LayersAndTables.Count;
        if (layerCount > 1 && format.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant() is "geojson" or "featurecollection" or "esrijson")
        {
            return PortalError(400, $"A {format} item holds one layer; name one in exportParameters' layers. "
                + "Several layers export together as a File Geodatabase, a GeoPackage, a Shapefile or a CSV.");
        }

        (int status, string? message, JsonElement answer) = await InnerAnswerAsync(context, () =>
                FeatureExportEndpoints.StartRequestAsync(context, service.Name, service.Folder,
                    new FeatureExportEndpoints.Ask(format, layers is { Count: > 0 } ? layers : null), cancellation))
            .ConfigureAwait(false);

        if (status >= 400 || !answer.TryGetProperty("id", out JsonElement started) || !Guid.TryParse(started.GetString(), out Guid job))
        {
            return PortalError(status >= 400 ? status : 500, message ?? "The export was not started.");
        }

        string fileName = answer.TryGetProperty("export", out JsonElement export) && export.TryGetProperty("fileName", out JsonElement named)
            ? named.GetString() ?? "export" : "export";
        string title = form["title"].ToString().Trim() is { Length: > 0 } given ? given : System.IO.Path.GetFileNameWithoutExtension(fileName);
        title = title.Length > WebMaps.MaximumTitleLength ? title[..WebMaps.MaximumTitleLength] : title;

        // The item is made now, so a client can open it and ask its status; its file is the job's, once written.
        IDocumentStore documents = context.RequestServices.GetRequiredService<IDocumentStore>();
        Document item = await documents.CreateAsync(title, form["snippet"].ToString() is { Length: > 0 } snippet ? snippet : null,
            current.Principal.Id, SharingScope.Private, fileName, KindOfExport(fileName), [], cancellation).ConfigureAwait(false);
        while (WaitingExports.Count >= MaximumWaitingExports
            && WaitingExports.OrderBy(w => w.Value.At).FirstOrDefault() is { Key: { } oldest })
        {
            WaitingExports.TryRemove(oldest, out _);
        }

        WaitingExports[item.Id] = (job, current.Principal.Id, DateTimeOffset.UtcNow);

        return Results.Ok(new
        {
            type = KindOfExport(fileName).ArcGisType,
            size = -1,
            jobId = job.ToString(),
            exportItemId = item.Id,
            serviceItemId = itemId,
            exportFormat = format,
        });
    }

    /// <summary>
    /// The status of an export's item, from its job: <c>processing</c> until written, then <c>completed</c> once the file is
    /// in the item, or <c>failed</c> with the job's reason.
    /// </summary>
    private static async Task<IResult?> ExportStatusAsync(HttpContext context, string id, RequestPrincipal current, CancellationToken cancellation)
    {
        if (!WaitingExports.TryGetValue(id, out (Guid Job, Guid Owner, DateTimeOffset At) waiting) || waiting.Owner != current.Principal.Id)
        {
            return null;
        }

        IServiceProvider services = context.RequestServices;
        FeatureExportState? export = await services.GetRequiredService<IFeatureExportStore>().FindAsync(waiting.Job, cancellation)
            .ConfigureAwait(false);

        if (export is null || export.Job.Status is JobStatus.Failed or JobStatus.Cancelled)
        {
            WaitingExports.TryRemove(id, out _);
            return Results.Ok(new { status = "failed", itemId = id, jobId = waiting.Job.ToString(), jobType = "export",
                statusMessage = export?.Job.Failure ?? "The export's job is gone." });
        }

        if (export.Job.Status != JobStatus.Done)
        {
            return Results.Ok(new { status = "processing", itemId = id, jobId = waiting.Job.ToString(), jobType = "export",
                statusMessage = $"{export.Job.Progress}%" });
        }

        HostSettings settings = services.GetRequiredService<HostSettings>();
        string? path = FeatureExporter.FileOf(settings.FeatureExportDirectory, export.Token);

        if (path is null || !File.Exists(path) || new FileInfo(path).Length > settings.DocumentMaximumBytes)
        {
            WaitingExports.TryRemove(id, out _);
            return Results.Ok(new { status = "failed", itemId = id, jobId = waiting.Job.ToString(), jobType = "export",
                statusMessage = path is not null && File.Exists(path)
                    ? $"The file is larger than an item may hold here ({settings.DocumentMaximumBytes / 1048576} MB); download it from the layer's Export data in Studio."
                    : "The export's file is gone." });
        }

        byte[] content = await File.ReadAllBytesAsync(path, cancellation).ConfigureAwait(false);
        await services.GetRequiredService<IDocumentStore>().ReplaceAsync(id, export.FileName, KindOfExport(export.FileName), content, cancellation)
            .ConfigureAwait(false);
        WaitingExports.TryRemove(id, out _);

        return Results.Ok(new { status = "completed", itemId = id, jobId = waiting.Job.ToString(), jobType = "export" });
    }

    /// <summary>The item kind an export's file is, by the name ADR-106's packaging gave it.</summary>
    private static DocumentKind KindOfExport(string fileName) => Documents.Named(
        fileName.EndsWith(".gpkg", StringComparison.OrdinalIgnoreCase) ? "geopackage"
        : fileName.EndsWith(".kml", StringComparison.OrdinalIgnoreCase) ? "kml"
        : fileName.EndsWith("-csv.zip", StringComparison.OrdinalIgnoreCase) ? "csvcollection"
        : fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? "featurecollection"
        : fileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "xlsx"
        : fileName.EndsWith(".gdb.zip", StringComparison.OrdinalIgnoreCase) ? "filegdb"
        : fileName.EndsWith("-shapefile.zip", StringComparison.OrdinalIgnoreCase) ? "shapefile"
        : fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "csv"
        : "geojson")!;
}
