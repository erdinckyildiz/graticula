using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Cartography;
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
using Microsoft.Extensions.Configuration;

namespace Graticula.Host;

/// <summary>
/// <b>ArcGIS Pro's publish path for a hosted feature layer</b> — ADR-191: <i>Share As Web Layer → Copy all data →
/// Feature</i> uploads a service definition and asks the portal to publish it; its geodatabase is imported by the
/// same pipeline Studio's geodatabase upload uses (ADR-038).
/// </summary>
/// <remarks>
/// <para>
/// <b>The sequence is the one measured on dev.graticula.com</b> (ADR-191 §1): <c>isServiceNameAvailable</c>, then
/// <c>addItem</c> with <c>multipart=true</c>, <c>addPart</c>, <c>commit</c>, the item's <c>status</c>, and
/// <c>publish</c> with <c>filetype=serviceDefinition</c>.
/// </para>
/// <para>
/// <b>A staged service definition lives in this process</b>, under the import scratch directory, until it is
/// published or the scratch sweep takes it — the same per-node, restart-forgetting shape as ArcGIS
/// <c>uploads/upload</c> (ADR-105). Publishing from Pro to a multi-node deployment must reach the node that took the
/// upload; nothing here has run on two nodes.
/// </para>
/// </remarks>
internal static partial class PortalEndpoints
{
    /// <summary>A service definition being uploaded or published, by the item id this portal gave it.</summary>
    private sealed class StagedDefinition(string id, Guid owner, string directory, string title)
    {
        public string Id { get; } = id;
        public Guid Owner { get; } = owner;
        public string Directory { get; } = directory;
        public string Title { get; } = title;
        public string File => System.IO.Path.Combine(Directory, "item.sd");
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public Guid? Job { get; set; }
        public string? ServiceItemId { get; set; }
        public string? ServiceUrl { get; set; }

        /// <summary>
        /// The job id an overwrite answered with. It is written while Pro waits, so it has no job of its own; Pro reads
        /// a publish answer without a <c>jobId</c> as a failure and deletes the service item it named (dev, 2026-10-17).
        /// </summary>
        public string? WrittenJob { get; set; }
    }

    private static readonly ConcurrentDictionary<string, StagedDefinition> Staged = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The most service definitions remembered at once; the oldest is forgotten first.</summary>
    private const int MaximumStaged = 256;

    /// <summary>The most parts one upload may have.</summary>
    private const int MaximumParts = 10_000;

    /// <summary>
    /// Whether Pro may publish here — ADR-191: on unless a deployment sets <c>Graticula:PublishFromPro</c> to false. It
    /// carries the portal's /studio address, the Enterprise version pair, the hosting server and these routes.
    /// </summary>
    internal static bool PublishingFromPro(IConfiguration configuration) =>
        configuration.GetValue("Graticula:PublishFromPro", true);

    /// <summary>
    /// Whether Pro's calls are written to disk — <c>Graticula:ProPublishCapture</c>, which only dev.graticula.com sets:
    /// the wire log, the unknown-call recorder and the share forms. Never on by default; it is a measuring tool.
    /// </summary>
    internal static bool RecordingPro(IConfiguration configuration) =>
        configuration.GetValue<bool>("Graticula:ProPublishCapture");

    /// <summary>The publish routes — mapped only where publishing from Pro is served.</summary>
    private static void MapProPublish(WebApplication app)
    {
        if (!PublishingFromPro(app.Configuration))
        {
            return;
        }

        foreach (string pattern in new[]
                 {
                     $"{Path}/content/users/{{username}}/addItem",
                     $"{Path}/content/users/{{username}}/{{folderId:length(32)}}/addItem",
                 })
        {
            app.MapPost(pattern, AddDefinitionAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }

        // Under a portal folder as well: Pro sends the parts to where it added the item (dev, 2026-10-17 — a share
        // into a folder uploaded nothing, addPart and commit answered 404).
        foreach (string user in new[] { $"{Path}/content/users/{{username}}", $"{Path}/content/users/{{username}}/{{folderId:length(32)}}" })
        {
            app.MapPost($"{user}/items/{{id}}/addPart", AddPartAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            app.MapPost($"{user}/items/{{id}}/commit", CommitAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
            app.MapMethods($"{user}/items/{{id}}/status", ["GET", "POST"], ItemStatusAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }

        foreach (string pattern in new[]
                 {
                     $"{Path}/content/users/{{username}}/publish",
                     $"{Path}/content/users/{{username}}/{{folderId:length(32)}}/publish",
                 })
        {
            app.MapPost(pattern, PublishDefinitionAsync).Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }

        foreach (string pattern in new[]
                 {
                     $"{Path}/accounts/{{id}}/isServiceNameAvailable",
                     $"{Path}/portals/{{id}}/isServiceNameAvailable",
                 })
        {
            app.MapMethods(pattern, ["GET", "POST"], ServiceNameAvailableAsync)
                .Governed(SharingGovernedExtensions.ByOwnership).DisableAntiforgery();
        }
    }

    /// <summary>The caller, when they are the user in the path and may publish features; otherwise null.</summary>
    private static RequestPrincipal? Publisher(HttpContext context, string username) =>
        Self(context, username) is { } current && current.Authorization.Allows(Privilege.ContentPublishFeatures)
            ? current
            : null;

    /// <summary>Portal's <c>addItem</c>: a service definition to publish, or the web map Pro shares after it.</summary>
    private static async Task<IResult> AddDefinitionAsync(
        HttpContext context, ImportScratch scratch, IWebMapStore maps, IContentFolderStore folders, IAuditLog audit,
        string username, CancellationToken cancellation)
    {
        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);
        string type = First(form, context, "type");

        // Share As Web Map: the layers first, then this (PortalEndpoints.ProWebMap).
        if (string.Equals(type, "Web Map", StringComparison.OrdinalIgnoreCase))
        {
            return await AddWebMapAsync(context, form, username, maps, folders, audit, cancellation).ConfigureAwait(false);
        }

        if (Publisher(context, username) is not { } current)
        {
            return PortalError(403, "Publishing needs the privilege to publish features.");
        }

        if (!string.Equals(type, "Service Definition", StringComparison.OrdinalIgnoreCase))
        {
            return PortalError(400,
                $"A service definition or a web map is added from Pro here (ADR-191); '{type}' is neither.");
        }

        string id = Guid.NewGuid().ToString("N");
        string directory = System.IO.Path.Combine(scratch.Directory, "portal", id);
        System.IO.Directory.CreateDirectory(directory);

        StagedDefinition staged = new(id, current.Principal.Id, directory,
            First(form, context, "title") is { Length: > 0 } title ? title : First(form, context, "filename"));

        // Uploaded in one request when not multipart: the file is the item.
        if (!string.Equals(First(form, context, "multipart"), "true", StringComparison.OrdinalIgnoreCase)
            && form.Files.GetFile("file") is { Length: > 0 } file)
        {
            await using FileStream target = File.Create(staged.File);
            await file.CopyToAsync(target, cancellation).ConfigureAwait(false);
        }

        // Bounded: an entry whose folder the scratch sweep took is gone, and past MaximumStaged the oldest go first.
        foreach (StagedDefinition gone in Staged.Values.Where(s => !System.IO.Directory.Exists(s.Directory)).ToList())
        {
            Staged.TryRemove(gone.Id, out _);
        }

        foreach (StagedDefinition oldest in Staged.Values.OrderBy(s => s.Created).Take(Math.Max(0, Staged.Count - MaximumStaged + 1)).ToList())
        {
            Staged.TryRemove(oldest.Id, out _);
        }

        Staged[id] = staged;
        return Results.Ok(new { success = true, id, folder = (string?)null });
    }

    /// <summary>Portal's <c>addPart</c>: one numbered part of a multipart upload.</summary>
    private static async Task<IResult> AddPartAsync(
        HttpContext context, string username, string id, CancellationToken cancellation)
    {
        if (Publisher(context, username) is not { } current || Mine(id, current) is not { } staged)
        {
            return Unknown("Item");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);

        if (!int.TryParse(First(form, context, "partNum"), out int part) || part is < 1 or > MaximumParts
            || form.Files.GetFile("file") is not { } file)
        {
            return PortalError(400, $"A part is a 'file' with a 'partNum' from 1 to {MaximumParts:N0}.");
        }

        await using FileStream target = File.Create(System.IO.Path.Combine(staged.Directory, $"part-{part:D5}"));
        await file.CopyToAsync(target, cancellation).ConfigureAwait(false);

        return Results.Ok(new { success = true, id, partNum = part });
    }

    /// <summary>Portal's <c>commit</c>: the parts, in their numbers' order, become the item.</summary>
    private static async Task<IResult> CommitAsync(
        HttpContext context, string username, string id, CancellationToken cancellation)
    {
        if (Publisher(context, username) is not { } current || Mine(id, current) is not { } staged)
        {
            return Unknown("Item");
        }

        string[] parts = [.. System.IO.Directory.GetFiles(staged.Directory, "part-*").Order(StringComparer.Ordinal)];

        if (parts.Length > 0)
        {
            await using FileStream target = File.Create(staged.File);
            foreach (string part in parts)
            {
                await using (FileStream source = File.OpenRead(part))
                {
                    await source.CopyToAsync(target, cancellation).ConfigureAwait(false);
                }

                File.Delete(part);
            }
        }

        return File.Exists(staged.File)
            ? Results.Ok(new { success = true, id })
            : PortalError(400, "Nothing was uploaded to commit.");
    }

    /// <summary>Portal's item <c>status</c>: the upload's, or the publish job's when it has one.</summary>
    private static async Task<IResult> ItemStatusAsync(
        HttpContext context, IJobStore jobs, string username, string id, CancellationToken cancellation)
    {
        if (Self(context, username) is not { } current)
        {
            return Unknown("User");
        }

        string askedJob = context.Request.Query["jobId"].ToString();
        if (askedJob.Length > 0 && Staged.Values.Any(s => s.Owner == current.Principal.Id
                && string.Equals(s.WrittenJob, askedJob, StringComparison.OrdinalIgnoreCase)))
        {
            return Results.Ok(new { status = "completed", itemId = id, jobId = askedJob });
        }

        Guid? job = Guid.TryParse(askedJob, out Guid asked) ? asked
            : Staged.Values.FirstOrDefault(s => s.Owner == current.Principal.Id
                && (string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.ServiceItemId, id, StringComparison.OrdinalIgnoreCase)))?.Job;

        if (job is not { } jobId)
        {
            return Results.Ok(new { status = "completed", itemId = id });
        }

        JobRecord? record = await jobs.FindAsync(jobId, current.Principal.Id, false, cancellation).ConfigureAwait(false);

        return Results.Ok(record?.Status switch
        {
            JobStatus.Done => (object)new { status = "completed", itemId = id, jobId = jobId.ToString("N") },
            JobStatus.Failed or JobStatus.Cancelled => new
            {
                status = "failed", itemId = id, jobId = jobId.ToString("N"),
                statusMessage = record.Failure ?? "The publish failed.",
            },
            null => new { status = "failed", itemId = id, statusMessage = "No such publish job." },
            _ => new { status = "processing", itemId = id, jobId = jobId.ToString("N"), progress = record.Progress },
        });
    }

    /// <summary>Portal's <c>isServiceNameAvailable</c>: free when no service of that name is in <c>hosted</c>.</summary>
    private static async Task<IResult> ServiceNameAvailableAsync(
        HttpContext context, IAdminCatalog catalog, CancellationToken cancellation)
    {
        string name = context.Request.Query["name"].ToString().Trim();
        if (name.Length == 0 && context.Request.HasFormContentType)
        {
            name = (await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false))["name"].ToString().Trim();
        }

        return Results.Ok(new
        {
            available = name.Length > 0 && await catalog
                .FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, name, cancellation).ConfigureAwait(false) is null,
        });
    }

    /// <summary>
    /// Portal's <c>publish</c> of a service definition: its geodatabase is inspected and imported, as Studio's upload
    /// is, into a hosted service of the name the definition gives.
    /// </summary>
    private static async Task<IResult> PublishDefinitionAsync(
        HttpContext context,
        IJobStore jobs,
        JobSignal signal,
        IAdminCatalog catalog,
        GeodatabaseReader reader,
        ImportScratch scratch,
        PostgresLayerCatalog layerCatalog,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        ThumbnailWarmer warmer,
        string username,
        CancellationToken cancellation)
    {
        if (Publisher(context, username) is not { } current)
        {
            return PortalError(403, "Publishing needs the privilege to publish features.");
        }

        IFormCollection form = await context.Request.ReadFormAsync(cancellation).ConfigureAwait(false);

        if (!string.Equals(First(form, context, "filetype"), "serviceDefinition", StringComparison.OrdinalIgnoreCase))
        {
            return PortalError(400,
                "Only a service definition is published from Pro here (Copy all data → Feature, ADR-191).");
        }

        if (Mine(First(form, context, "itemid"), current) is not { } staged || !File.Exists(staged.File))
        {
            return PortalError(404, "That service definition was not uploaded here by you, or is gone.");
        }

        if (!reader.Available)
        {
            return PortalError(503, "This server's geodatabase reader is not available, so nothing can be published.");
        }

        Guid datastore = await HostedDataEndpoints.DatastoreIdAsync(catalog, cancellation).ConfigureAwait(false);
        if (datastore == Guid.Empty)
        {
            return PortalError(503, "The datastore is not registered as a data source.");
        }

        // <b>Unpacked before the job exists</b>: the inspector claims a queued job by itself, and an inspect job
        // created first was claimed while its zip was still being written — GDAL "could not open" the half file
        // (dev, 2026-10-17, the fifth publish). The zip is written beside, then moved to the job's own path.
        string unpacked = System.IO.Path.Combine(staged.Directory, "data.zip");
        ServiceDefinition definition;
        try
        {
            definition = await ServiceDefinitionArchive.ExtractAsync(staged.File, unpacked, cancellation).ConfigureAwait(false);
        }
        catch (Exception unreadable) when (unreadable is InvalidDataException or IOException
            or SharpCompress.Common.ArchiveException or InvalidOperationException)
        {
            return PortalError(400, unreadable.Message);
        }

        string service = definition.ServiceName.Trim();
        if (service.Length is 0 or > 128 || service.AsSpan().IndexOfAny("/\\?#%") >= 0)
        {
            return PortalError(400, $"'{service}' cannot be a service name here: at most 128 characters, none of / \\ ? # %.");
        }

        if (await catalog.FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, service, cancellation)
                .ConfigureAwait(false) is { } existing)
        {
            if (!string.Equals(First(form, context, "overwrite"), "true", StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(unpacked);
                return PortalError(409, $"There is already a service called '{service}' in hosted. Share it under "
                    + "another name, or overwrite it (Overwrite Web Layer).");
            }

            try
            {
                return await OverwriteAsync(
                    context, staged, definition, existing, unpacked, reader, catalog, layerCatalog, importer, contexts, tiles, audit,
                    warmer, cancellation).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(unpacked);
            }
        }

        JobRecord inspection = await jobs.CreateAsync(
            current.Principal.Id, JobKind.GeodatabaseInspect, $"Reading {staged.Title} from ArcGIS Pro",
            JsonSerializer.Serialize(new { file = staged.Title, bytes = new FileInfo(unpacked).Length }),
            cancellation).ConfigureAwait(false);
        File.Move(unpacked, scratch.PathFor(inspection.Id), overwrite: true);

        signal.Wake(inspection.Kind);
        JobRecord? inspected = await AwaitJobAsync(jobs, inspection.Id, current.Principal.Id, TimeSpan.FromMinutes(2), cancellation)
            .ConfigureAwait(false);

        if (inspected?.Status != JobStatus.Done || LayerNames(inspected.Detail) is not { Count: > 0 } layers)
        {
            return PortalError(400, inspected?.Failure
                ?? "The service definition's geodatabase could not be read in time, or holds no layer to publish.");
        }

        JobRecord import = await jobs.CreateAsync(
            current.Principal.Id, JobKind.GeodatabaseImport, $"Publishing {service} from ArcGIS Pro",
            JsonSerializer.Serialize(new
            {
                archive = inspection.Id,
                owner = current.Principal.Id,
                datastore,
                service,
                folder = FeatureServerMetadataWriter.HostedFolder,
                sharing = SharingScope.Private.ToString(),
                layers,
            }),
            cancellation).ConfigureAwait(false);

        staged.Job = import.Id;
        signal.Wake(import.Kind);

        // Small layers finish while Pro waits, so it can name the new item; a long import answers with the job.
        JobRecord? imported = await AwaitJobAsync(jobs, import.Id, current.Principal.Id, TimeSpan.FromMinutes(3), cancellation)
            .ConfigureAwait(false);

        if (imported?.Status is JobStatus.Failed or JobStatus.Cancelled)
        {
            return PortalError(500, imported.Failure ?? "The import failed.");
        }

        ServiceAtAddress? made = imported?.Status == JobStatus.Done
            ? await catalog.FindServiceAtAsync(FeatureServerMetadataWriter.HostedFolder, service, cancellation).ConfigureAwait(false)
            : null;

        if (made is { } published)
        {
            if (definition.DrawingInfos.Count > 0)
            {
                List<PublishedLayer> fresh = [.. (await layerCatalog.ListAsync(cancellation).ConfigureAwait(false))
                    .Where(l => l.ServiceId == published.Id)];
                List<(PublishedLayer, string)> named = ProNamed(fresh, definition);
                await NameLayersAsync(named, catalog, contexts, cancellation).ConfigureAwait(false);
                await ApplySymbologyAsync(definition, named, catalog, warmer, cancellation).ConfigureAwait(false);
            }

            await ApplyDetailsAsync(definition.Item, service, catalog, cancellation).ConfigureAwait(false);
        }

        staged.ServiceItemId = made is { } done ? done.Id.ToString("N") : staged.Id;
        // The server's address, not the portal's: under /studio the portal's base is not where the service answers
        // (Pro asked /studio/rest/services/... and got 404, dev 2026-10-17).
        string url = $"{context.Request.Scheme}://{context.Request.Host}/rest/services/{FeatureServerMetadataWriter.HostedFolder}/{Uri.EscapeDataString(service)}/FeatureServer";
        staged.ServiceUrl = url;

        return Results.Ok(new
        {
            services = new[]
            {
                new
                {
                    type = "Feature Service",
                    serviceurl = url,
                    encodedServiceURL = url,
                    size = new FileInfo(staged.File).Length,
                    jobId = import.Id.ToString("N"),
                    serviceItemId = staged.ServiceItemId,
                    success = true,
                },
            },
        });
    }

    /// <summary>
    /// <b>Overwrite Web Layer</b> — ADR-191 §5.6: the service keeps its id, URL, layer indexes, sharing and groups,
    /// and each layer's rows are replaced by the geodatabase's layer of the same name through ADR-103's overwrite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not unpublish and publish again</b>, which is what an overwrite looks like from Pro: a layer index is never
    /// reused here, so a republished service would be a different service to every map that names it.
    /// </para>
    /// <para>
    /// <b>The same layers or nothing</b>: a geodatabase that adds or removes a layer changes what the service is, and
    /// ADR-103 §5.4 keeps a schema change out of an overwrite. Every layer is read and checked before any is written,
    /// so a refused file leaves the service as it was; a write that fails part-way leaves the layers before it
    /// replaced.
    /// </para>
    /// </remarks>
    private static async Task<IResult> OverwriteAsync(
        HttpContext context,
        StagedDefinition staged,
        ServiceDefinition definition,
        ServiceAtAddress existing,
        string archive,
        GeodatabaseReader reader,
        IAdminCatalog catalog,
        PostgresLayerCatalog layerCatalog,
        PostGisImporter importer,
        ServiceContexts contexts,
        ITileCache tiles,
        IAuditLog audit,
        ThumbnailWarmer warmer,
        CancellationToken cancellation)
    {
        List<PublishedLayer> current = [.. (await layerCatalog.ListAsync(cancellation).ConfigureAwait(false))
            .Where(l => l.ServiceId == existing.Id)
            .OrderBy(l => l.LayerIndex)];

        if (current.Count == 0)
        {
            return PortalError(409, $"'{existing.Name}' has no layer this server can overwrite.");
        }

        List<string> offered = [];
        using (JsonDocument listed = await reader.AskAsync(new { op = "layers", archive }, ShapefileViaReader.Deadline, cancellation)
            .ConfigureAwait(false))
        {
            if (listed.RootElement.TryGetProperty("layers", out JsonElement each) && each.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement layer in each.EnumerateArray())
                {
                    if (layer.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } text)
                    {
                        offered.Add(text);
                    }
                }
            }
        }

        // By name — the layer's title, its catalogue name or its table — and a lone layer for a lone layer.
        static bool Same(PublishedLayer layer, string name) => SameLayer(layer, name);

        List<(PublishedLayer Layer, string From)> pairs = [];
        if (current.Count == 1 && offered.Count == 1)
        {
            pairs.Add((current[0], offered[0]));
        }
        else
        {
            foreach (PublishedLayer layer in current)
            {
                if (offered.FirstOrDefault(o => Same(layer, o)) is { } from)
                {
                    pairs.Add((layer, from));
                }
            }
        }

        if (pairs.Count != current.Count || offered.Count != current.Count)
        {
            return PortalError(409,
                $"'{existing.Name}' has {current.Count} layer(s) ({string.Join(", ", current.Select(l => l.Title ?? l.Definition.Name))}) "
                + $"and the shared map has {offered.Count} ({string.Join(", ", offered)}). Overwriting keeps a service's layers "
                + "and replaces their data; adding or removing a layer makes a new service. Nothing was written.");
        }

        List<(PublishedLayer Layer, ImportedDataset Data)> read = [];
        foreach ((PublishedLayer layer, string from) in pairs)
        {
            // The guard Update data asks: whose it is, hosted, not a view, no views over it. It answers for itself.
            if (await HostedDataEndpoints.HostedLayerAsync(
                    context, layerCatalog, layer.Definition.Name, "overwrite", cancellation,
                    $"{FeatureServerMetadataWriter.HostedFolder}/{existing.Name}")
                .ConfigureAwait(false) is null)
            {
                return Results.Empty;
            }

            (ImportedDataset? dataset, string? error) = await ShapefileViaReader.ReadAsync(
                reader, archive, 0, null, HostedDataEndpoints.Limits(context), cancellation, from).ConfigureAwait(false);

            if (dataset is null)
            {
                return PortalError(400, $"Layer '{from}' could not be read, so nothing was written: {error}");
            }

            read.Add((layer, dataset));
        }

        foreach ((PublishedLayer layer, ImportedDataset data) in read)
        {
            // Protection, the lock wait and a value that does not fit are refused here, in Update data's words.
            if (await HostedDataEndpoints.WriteUpdateAsync(
                    context, layer, data, true, importer, contexts, tiles, catalog, audit, warmer, cancellation)
                .ConfigureAwait(false) is null)
            {
                return Results.Empty;
            }
        }

        // ArcGIS's overwrite takes the map's drawing too: the service is what the map now is.
        // By Pro's own layer names, not the geodatabase's dataset names the pairs above are made of: the drawing is keyed
        // by what Pro called the map layer (a lone layer for a lone layer, as the publish pairs them). Paired by dataset
        // name, a lone layer's drawing never matched and an overwrite dropped the map's symbology (found 2026-10-18).
        List<(PublishedLayer, string)> named = ProNamed(current, definition);
        await NameLayersAsync(named, catalog, contexts, cancellation).ConfigureAwait(false);
        await ApplySymbologyAsync(definition, named, catalog, warmer, cancellation).ConfigureAwait(false);

        // Only what the pane held: an overwrite with no summary leaves the one the item has.
        await ApplyDetailsAsync(definition.Item, existing.Name, catalog, cancellation).ConfigureAwait(false);

        staged.ServiceItemId = existing.Id.ToString("N");
        string url = $"{context.Request.Scheme}://{context.Request.Host}/rest/services/{FeatureServerMetadataWriter.HostedFolder}/{Uri.EscapeDataString(existing.Name)}/FeatureServer";
        staged.ServiceUrl = url;
        staged.WrittenJob = Guid.NewGuid().ToString("N");

        return Results.Ok(new
        {
            services = new[]
            {
                new
                {
                    type = "Feature Service",
                    serviceurl = url,
                    encodedServiceURL = url,
                    size = new FileInfo(staged.File).Length,
                    jobId = staged.WrittenJob,
                    serviceItemId = staged.ServiceItemId,
                    success = true,
                },
            },
        });
    }

    /// <summary>
    /// The published layers paired with the name Pro gave each map layer in the service definition: a lone layer with a
    /// lone drawing whatever they are called (the dataset is <c>tur_admin1</c>, the map layer <i>Provinces by area</i>),
    /// otherwise by <see cref="SameLayer"/>. The symbology follows this pairing, on a publish and on an overwrite.
    /// </summary>
    internal static List<(PublishedLayer, string)> ProNamed(IReadOnlyList<PublishedLayer> layers, ServiceDefinition definition) =>
        layers.Count == 1 && definition.DrawingInfos.Count == 1
            ? [(layers[0], definition.DrawingInfos.Keys.First())]
            : [.. layers.Select(l => (l, definition.DrawingInfos.Keys.FirstOrDefault(k => SameLayer(l, k))))
                .Where(p => p.Item2 is not null).Select(p => (p.l, p.Item2!))];

    /// <summary>
    /// Gives each paired layer Pro's map-layer name as its title (ADR-191 section 5.7), which is what the ArcGIS faces
    /// call a layer (<see cref="PublishedLayer.DisplayName"/>). The catalogue name stays the dataset's: it is the
    /// address, it is unique, and a name with spaces would have to be escaped in every face (ADR-179 section 1). A
    /// layer already carrying that title is left alone.
    /// </summary>
    internal static async Task NameLayersAsync(
        IEnumerable<(PublishedLayer Layer, string From)> layers, IAdminCatalog catalog, ServiceContexts contexts,
        CancellationToken cancellation)
    {
        foreach ((PublishedLayer layer, string from) in layers)
        {
            string title = from.Trim();
            if (title.Length is 0 or > 256 || string.Equals(layer.Title, title, StringComparison.Ordinal))
            {
                continue;
            }

            if (await catalog.SetLayerTitleAsync(layer.Id, title, cancellation).ConfigureAwait(false))
            {
                contexts.Forget(layer);
            }
        }
    }

    /// <summary>
    /// Stores what Pro's pane held on the service, where Studio's item page reads it: the summary as the description
    /// (the one text the item has) and the tags. Nothing is written for a field Pro did not send.
    /// </summary>
    internal static async Task ApplyDetailsAsync(
        ItemDetails details, string service, IAdminCatalog catalog, CancellationToken cancellation)
    {
        string? text = (details.Snippet ?? details.Description)?.Trim();
        if (text is { Length: > 0 })
        {
            if (text.Length > AdminEndpoints.MaximumDescriptionLength)
            {
                text = text[..AdminEndpoints.MaximumDescriptionLength];
            }

            await catalog.SetServiceDescriptionAsync(service, FeatureServerMetadataWriter.HostedFolder, text, cancellation)
                .ConfigureAwait(false);
        }

        if (details.Tags is { Length: > 0 } raw)
        {
            IEnumerable<string> words = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Length > ItemTags.MaximumLength ? t[..ItemTags.MaximumLength] : t);
            if (ItemTags.Normalise(words.Take(ItemTags.MaximumCount), out _) is { Count: > 0 } tags)
            {
                await catalog.SetServiceTagsAsync(service, FeatureServerMetadataWriter.HostedFolder, tags, cancellation)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether a layer is the one a geodatabase layer of this name became: its title, catalogue name or table.</summary>
    private static bool SameLayer(PublishedLayer layer, string name) =>
        string.Equals(layer.Title, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(layer.Definition.Name, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(layer.Definition.TableName, PostGisImporter.ColumnNameFor(name), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stores each layer's drawing from Pro's map — ADR-191 §5.7: the <c>drawingInfo</c> the service definition carries,
    /// read the way Studio stores a pasted one (<see cref="SymbologyConversion.Read"/>, to CIM). A drawing this server
    /// cannot read leaves the layer on its generated appearance, and the publish goes on: the data is what was asked
    /// for, and a layer drawn plainly is not a failed share.
    /// </summary>
    private static async Task ApplySymbologyAsync(
        ServiceDefinition definition, IEnumerable<(PublishedLayer Layer, string From)> layers, IAdminCatalog catalog,
        ThumbnailWarmer warmer, CancellationToken cancellation)
    {
        foreach ((PublishedLayer layer, string from) in layers)
        {
            // A table draws nothing, so its drawing info, if Pro sent one, has nowhere to go (ADR-213).
            if (layer.GeometryType is not { } geometryType || !definition.DrawingInfos.TryGetValue(from, out string? drawing))
            {
                continue;
            }

            try
            {
                SymbologyWrite written = SymbologyConversion.Read(drawing, geometryType);
                await catalog.SetSymbologyAsync(layer.Id, written.Canonical, cancellation).ConfigureAwait(false);
                warmer.Redraw(layer.Id);
            }
            catch (SymbologyException)
            {
                // The generated appearance stays; see the summary.
            }
        }
    }

    /// <summary>The staged definition with this id, when it is the caller's.</summary>
    private static StagedDefinition? Mine(string id, RequestPrincipal current) =>
        Staged.TryGetValue(id ?? string.Empty, out StagedDefinition? staged) && staged.Owner == current.Principal.Id
            ? staged
            : null;

    /// <summary>A field from the form, or from the query string where Pro puts some of them.</summary>
    private static string First(IFormCollection form, HttpContext context, string name) =>
        form[name].ToString() is { Length: > 0 } posted ? posted.Trim() : context.Request.Query[name].ToString().Trim();

    /// <summary>Waits for a job to stop, polling, for at most <paramref name="patience"/>.</summary>
    private static async Task<JobRecord?> AwaitJobAsync(
        IJobStore jobs, Guid id, Guid owner, TimeSpan patience, CancellationToken cancellation)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + patience;
        while (true)
        {
            JobRecord? record = await jobs.FindAsync(id, owner, false, cancellation).ConfigureAwait(false);
            if (record is null || record.Status is JobStatus.Done or JobStatus.Failed or JobStatus.Cancelled
                || DateTimeOffset.UtcNow > until)
            {
                return record;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellation).ConfigureAwait(false);
        }
    }

    /// <summary>The layer names an inspection found, in its order.</summary>
    private static List<string> LayerNames(string? detail)
    {
        List<string> names = [];
        if (string.IsNullOrEmpty(detail))
        {
            return names;
        }

        using JsonDocument document = JsonDocument.Parse(detail);
        if (document.RootElement.TryGetProperty("layers", out JsonElement layers) && layers.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement layer in layers.EnumerateArray())
            {
                if (layer.TryGetProperty("name", out JsonElement name) && name.GetString() is { Length: > 0 } text)
                {
                    names.Add(text);
                }
            }
        }

        return names;
    }
}
