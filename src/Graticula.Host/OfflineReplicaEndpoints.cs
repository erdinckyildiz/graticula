using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Admin;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;
using Npgsql;

namespace Graticula.Host;

/// <summary>The routes a replica's documents and edits are taken from, handed in by <see cref="Program"/>.</summary>
/// <param name="ServiceDocument">Answers <c>FeatureServer</c> for a service name.</param>
/// <param name="LayerDocument">Answers <c>FeatureServer/{id}</c> for a service name and a layer id.</param>
/// <param name="ApplyEdits">Answers the service's <c>applyEdits</c> for a service name.</param>
internal sealed record ReplicaFaces(
    Func<HttpContext, string, Task> ServiceDocument,
    Func<HttpContext, string, int, Task> LayerDocument,
    Func<HttpContext, string, Task> ApplyEdits);

/// <summary>
/// Offline sync on a hosted feature service — ADR-288: <c>createReplica</c>, <c>synchronizeReplica</c>,
/// <c>unRegisterReplica</c>, <c>replicas</c> and <c>replicas/{id}</c>, the jobs they answer through, and the
/// administrator's switch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sources.</b> Esri's public REST reference — <i>Create Replica</i>, <i>Synchronize Replica</i>,
/// <i>Unregister Replica</i>, <i>Replicas</i> and <i>Replica</i> (developers.arcgis.com/rest/services-reference/enterprise/,
/// read 2026-11-10) — and what ArcGIS Server 12.1 answered on a hosted service the same evening (ADR-288 §4). The
/// mobile geodatabase itself is written behind <see cref="IMobileGeodatabaseWriter"/>, which this file calls and never
/// implements; a client's upload is read behind <see cref="IOfflineDeltaReader"/>, whose implementation here starts a
/// child process (<see cref="OfflineUploadReader"/>, ADR-288 condition 7), so no client's file is parsed in this one.
/// </para>
/// <para>
/// <b>One implementation of everything a replica carries.</b> The service and layer documents in the file are the ones
/// <c>FeatureServer</c> and <c>FeatureServer/{id}</c> answer this caller, reached on a request of their own as
/// <see cref="ServiceQuery"/> reaches the layer query; an upload is applied by the service's own <c>applyEdits</c> with
/// <c>useGlobalIds=true</c>, so every check an edit passes — sharing, ownership, the capability ceiling, domains — is the
/// edit's.
/// </para>
/// <para>
/// <b>Built on the layer's history</b> (ADR-078, ADR-288 §5.3 <c>INFERRED</c>): a registered replica's copy is the layer
/// <i>as it was</i> at its server generation, and a download is the difference between the layer at the replica's
/// generation and at the new one, read with <c>historicMoment</c> through the same feature source a query reads.
/// </para>
/// <para>
/// <b>Who.</b> Creating and synchronising is for a caller whose capabilities offer an edit — the ones the documents say
/// <c>Sync</c> to. A replica is its creator's: only they synchronise it, read it and download its files; an
/// administrator sees and unregisters every replica.
/// </para>
/// </remarks>
internal static class OfflineReplicaEndpoints
{
    /// <summary>How many object ids one query of a download names.</summary>
    private const int IdsPerQuery = 1000;

    /// <summary>How many changes one page of a layer's history holds while a download reads it.</summary>
    private const int HistoryPage = 5000;

    /// <summary>Maps the routes under one FeatureServer prefix.</summary>
    /// <param name="app">The application.</param>
    /// <param name="prefix"><c>/rest/services</c> or <c>/rest/services/{folder}</c>.</param>
    /// <param name="faces">The routes the documents and the edits are taken from.</param>
    public static void Map(WebApplication app, string prefix, ReplicaFaces faces)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(faces);

        string at = $"{prefix}/{{serviceName}}/FeatureServer";

        app.MapPost($"{at}/createReplica", (HttpContext context, string serviceName, CancellationToken cancellation) =>
                CreateAsync(context, serviceName, faces, cancellation))
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapPost($"{at}/synchronizeReplica", (HttpContext context, string serviceName, CancellationToken cancellation) =>
                SynchronizeAsync(context, serviceName, faces, cancellation))
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapPost($"{at}/unRegisterReplica", UnregisterAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapMethods($"{at}/replicas", ["GET", "POST"], ListAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapMethods($"{at}/replicas/{{replicaId}}", ["GET", "POST"], ReplicaAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapMethods($"{at}/jobs/{{jobId:guid}}", ["GET", "POST"], JobAsync)
            .Governed(SharingGovernedExtensions.ByService).DisableAntiforgery();
        app.MapGet($"{at}/replicafiles/{{jobId:guid}}", FileAsync)
            .Governed(SharingGovernedExtensions.ByService);
    }

    /// <summary>Maps the administrator's switch: <c>POST /admin/services/{name}/sync</c>.</summary>
    /// <param name="app">The application.</param>
    public static void MapAdmin(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/admin/services/{name}/sync", SwitchRouteAsync);
    }

    // ------------------------------------------------------------------------------------------------ the switch

    /// <summary>Turns sync on or off: the body <c>{"enabled": true}</c>.</summary>
    private static async Task SwitchRouteAsync(
        HttpContext context, string name, string? folder, SyncSwitchRequest request, PostgresLayerCatalog layers,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? at = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        if (!await Authorize.RequireAsync(context, Privilege.ContentPublishFeatures).ConfigureAwait(false)
            || !await AdminEndpoints.ManagesServiceAsync(
                context, layers, at, name, request.Enabled ? "enable sync on" : "disable sync on", cancellation).ConfigureAwait(false)
            || await layers.FindServiceAsync(at, name, cancellation).ConfigureAwait(false) is not { } service)
        {
            return;
        }

        (int status, object answer) = await SwitchAsync(context, service, at, request.Enabled, cancellation).ConfigureAwait(false);
        await Results.Json(answer, statusCode: status).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Turns a hosted service's sync on or off — the administrator's switch and <c>updateDefinition</c>'s
    /// <c>syncEnabled</c> both. Enabling turns on each layer's history; disabling unregisters every replica
    /// (ADR-288 §11.1, <c>INFERRED</c>). The caller has already been checked to manage the service.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="service">The service.</param>
    /// <param name="folder">Its folder.</param>
    /// <param name="enabled">On or off.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The status and the answer to write.</returns>
    internal static async Task<(int Status, object Answer)> SwitchAsync(
        HttpContext context, PublishedService service, string? folder, bool enabled, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(service);

        IServiceProvider services = context.RequestServices;
        IOfflineSyncStore store = services.GetRequiredService<IOfflineSyncStore>();
        LayerConnections connections = services.GetRequiredService<LayerConnections>();
        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        IAuditLog audit = services.GetRequiredService<IAuditLog>();

        if (enabled && SyncFlags.Refusal(service) is { } refusal)
        {
            return (409, Error(409, refusal));
        }

        List<string> historyStarted = [];

        if (enabled)
        {
            foreach (PublishedLayer layer in service.LayersAndTables)
            {
                IFeatureHistory history = connections.HistoryFor(layer);

                try
                {
                    if (!await history.IsOnAsync(cancellation).ConfigureAwait(false))
                    {
                        await history.EnableAsync(cancellation).ConfigureAwait(false);
                        historyStarted.Add(layer.Definition.Name);
                    }
                }
                catch (Exception e) when (e is DbException or InvalidOperationException)
                {
                    return (409, Error(409,
                        $"Sync needs the history of '{layer.Definition.Name}', and it could not be turned on: {e.Message}"
                        + (historyStarted.Count > 0 ? $" History was turned on for {string.Join(", ", historyStarted)} before this." : string.Empty)
                        + " Sync is still off."));
                }

                contexts.Forget(layer);
            }
        }

        int unregistered = await store.SetEnabledAsync(service.Id, enabled, cancellation).ConfigureAwait(false);

        foreach (PublishedLayer layer in service.LayersAndTables)
        {
            contexts.Forget(layer);
        }

        await AdminEndpoints.AuditAsync(context, audit, "service.sync", service.Name,
            AdminEndpoints.Detail(new { folder, enabled, historyStarted, unregistered }), succeeded: true, cancellation)
            .ConfigureAwait(false);

        return (200, new
        {
            name = service.Name,
            folder,
            syncEnabled = enabled,
            historyStarted,
            unregistered,
            note = enabled
                ? "Sync is on. Every layer keeps its history, which offline copies are synchronised from (ADR-288 §5.3)."
                : $"Sync is off, and its {unregistered} registered replicas are gone; a device holding one cannot synchronise it "
                    + "again. Each layer's history is kept.",
        });
    }

    /// <summary>
    /// Whether the caller may upload offline edits to this service's <c>uploads/upload</c> without the publishing
    /// privilege an append needs: they may edit it and it is sync-enabled. Answers nothing.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <param name="catalog">The catalogue.</param>
    /// <param name="folder">The folder from the route.</param>
    /// <param name="serviceName">The service name from the route.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>True when they may.</returns>
    internal static async Task<bool> MayUploadEditsAsync(
        HttpContext context, CatalogFallback catalog, string? folder, string serviceName, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Features.Get<RequestPrincipal>() is not { Principal.IsAnonymous: false }
            || await ServiceLookup.ReadableQuietlyAsync(context, catalog, folder, serviceName, cancellation).ConfigureAwait(false)
                is not { } service
            || !SyncFlags.IsOn(service))
        {
            return false;
        }

        string capabilities = await Program.ServiceCapabilitiesAsync(
            context, service, context.RequestServices.GetRequiredService<ServiceContexts>(), cancellation).ConfigureAwait(false);

        return SyncDocument.OffersSync(capabilities);
    }

    // ------------------------------------------------------------------------------------------------ createReplica

    /// <summary>
    /// <c>createReplica</c> — Esri, <i>Create Replica (Feature Service)</i>: the layers asked for, filtered by the area
    /// and each layer's clause, written as a mobile geodatabase, and registered when <c>syncModel=perLayer</c>.
    /// </summary>
    private static async Task CreateAsync(HttpContext context, string serviceName, ReplicaFaces faces, CancellationToken cancellation)
    {
        if (await AdmitAsync(context, serviceName, cancellation).ConfigureAwait(false) is not { } admitted)
        {
            return;
        }

        (PublishedService service, RequestPrincipal caller) = admitted;
        IServiceProvider services = context.RequestServices;
        Func<string, string?> field = await FieldsAsync(context, cancellation).ConfigureAwait(false);

        if (!OfflineReplicaRequests.TryReadCreate(field, service, out CreateReplicaRequest? request, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        // A JSON replica is written by this server; only a mobile geodatabase needs the writer a deployment installs.
        IMobileGeodatabaseWriter? writer = null;

        if (!request!.IsJson)
        {
            if (await WriterAsync(context).ConfigureAwait(false) is not { } installed)
            {
                return;
            }

            writer = installed;
        }

        // ---- everything the file needs from the request, while the request is here ----
        (int serviceStatus, byte[] serviceBody) = await ServiceQuery.InnerRequestAsync(
            context, $"{FeatureServerPath(context)}", HttpMethods.Get, Json(), null, null, inner => faces.ServiceDocument(inner, serviceName))
            .ConfigureAwait(false);

        if (serviceStatus != StatusCodes.Status200OK)
        {
            await RefuseAsync(context, serviceStatus, "The service's own document could not be read for the replica.").ConfigureAwait(false);
            return;
        }

        string serviceDocument = Encoding.UTF8.GetString(serviceBody);

        // The planning and the writing are OfflinePackager's, which a map area's packaging calls without a request.
        (PackagePlan? plan, PackageRefusal? refusal) = await OfflinePackager.PlanAsync(
            services, service, request!, serviceDocument, async id => await LayerDocumentAsync(context, faces, serviceName, id).ConfigureAwait(false),
            cancellation).ConfigureAwait(false);

        if (refusal is not null)
        {
            await RefuseAsync(context, refusal.Status, refusal.Message).ConfigureAwait(false);
            return;
        }

        ReplicaJobs jobs = services.GetRequiredService<ReplicaJobs>();

        if (jobs.Start(caller.Principal.Id, service.Id, "esriReplicaResponseTypeData") is not { } job)
        {
            await BusyAsync(context).ConfigureAwait(false);
            return;
        }

        Guid? replicaId = request!.Registered ? Guid.NewGuid() : null;
        DateTimeOffset generation = DateTimeOffset.FromUnixTimeMilliseconds(jobs.Now.ToUnixTimeMilliseconds());
        string serviceUrl = TileFaces.Origin(context) + FeatureServerPath(context);
        ReplicaOwner owner = new(caller.Principal.Id, caller.Principal.Name);

        job.ReplicaName = request.Name;
        job.ReplicaId = replicaId;
        job.DownloadName = request.Name + (request.IsJson ? ".json" : ".geodatabase");

        async Task WorkAsync(CancellationToken stopping)
        {
            job.Status = "ExportingData";
            job.Updated = jobs.Now;

            string file = jobs.FileFor(job, request.IsJson ? ".json" : ".geodatabase");
            IReadOnlyList<ReplicaLayer> generations = request.IsJson
                ? await OfflinePackager.WriteJsonAsync(
                    services, plan!, replicaId, owner, serviceUrl, generation, file, stopping).ConfigureAwait(false)
                : await OfflinePackager.WriteAsync(
                    services, writer!, plan!, replicaId, owner, serviceUrl, generation, file, stopping).ConfigureAwait(false);
            job.File = file;

            if (replicaId is not null)
            {
                job.Generations = generations;
            }

            job.Status = "Completed";
            job.Updated = jobs.Now;
        }

        await RunAsync(context, job, request.Async, WorkAsync, cancellation, request.Embedded).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ synchronizeReplica

    /// <summary>
    /// <c>synchronizeReplica</c> — Esri, <i>Synchronize Replica (Feature Service)</i>: the client's uploaded edits applied
    /// through <c>applyEdits</c>, then what changed on the server since each layer's generation, written as a delta.
    /// </summary>
    /// <remarks>
    /// <b>The upload is applied before the answer, even with <c>async=true</c></b>, because it is applied through the
    /// service's own <c>applyEdits</c> on a request of the caller's; the download is what runs behind the status URL.
    /// Bidirectional is upload, then download; the rows the upload named by GlobalID are left out of the download, so a
    /// client is not sent back the edits it has just made (its insert would come back as a second row).
    /// </remarks>
    private static async Task SynchronizeAsync(HttpContext context, string serviceName, ReplicaFaces faces, CancellationToken cancellation)
    {
        if (await AdmitAsync(context, serviceName, cancellation).ConfigureAwait(false) is not { } admitted)
        {
            return;
        }

        (PublishedService service, RequestPrincipal caller) = admitted;
        IServiceProvider services = context.RequestServices;
        Func<string, string?> field = await FieldsAsync(context, cancellation).ConfigureAwait(false);

        if (!OfflineReplicaRequests.TryReadSynchronize(field, out SynchronizeReplicaRequest? request, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        IMobileGeodatabaseWriter? writer = null;

        if (request!.DataFormat != "json")
        {
            if (await WriterAsync(context).ConfigureAwait(false) is not { } installed)
            {
                return;
            }

            writer = installed;
        }

        IOfflineSyncStore store = services.GetRequiredService<IOfflineSyncStore>();

        if (await OwnReplicaAsync(context, store, service, caller, request.ReplicaId, cancellation).ConfigureAwait(false) is not { } replica)
        {
            return;
        }

        // The layers this sync covers, each with the generation it starts from and its direction.
        List<(ReplicaLayer Stored, long From, string Direction)> covered = [];

        foreach (ReplicaLayer stored in replica.Layers)
        {
            SyncLayerRequest? asked = request.Layers.FirstOrDefault(l => l.Id == stored.Id);

            if (request.Layers.Count > 0 && asked is null)
            {
                continue;
            }

            covered.Add((stored, asked?.ServerGeneration ?? stored.ServerGeneration, asked?.Direction ?? request.Direction));
        }

        if (request.Layers.FirstOrDefault(l => replica.Layers.All(r => r.Id != l.Id)) is { } stranger)
        {
            await RefuseAsync(context, 400, $"syncLayers names layer {stranger.Id}, which is not in replica {OfflineReplicaRequests.Wire(replica.Id)}.")
                .ConfigureAwait(false);
            return;
        }

        bool uploading = request.UploadId is not null && covered.Any(c => c.Direction is "upload" or "bidirectional");
        bool downloading = covered.Any(c => c.Direction is "download" or "bidirectional");
        string? uploadPath = null;

        if (uploading)
        {
            uploadPath = ArcGisAppendEndpoints.WaitingUploadOf(request.UploadId!, caller.Principal.Id.ToString());

            if (uploadPath is null)
            {
                await RefuseAsync(context, 400,
                    $"editsUploadID '{request.UploadId}': no upload of yours is waiting here. Upload the delta to this service's "
                    + "uploads/upload first; an upload is kept for an hour, on the server it was sent to.").ConfigureAwait(false);
                return;
            }
        }

        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        LayerConnections connections = services.GetRequiredService<LayerConnections>();
        List<Planned> planned = [];

        foreach ((ReplicaLayer stored, _, _) in covered)
        {
            if (service.LayerOrTable(stored.Id) is not { } layer)
            {
                await RefuseAsync(context, 409,
                    $"Layer {stored.Id} of replica {OfflineReplicaRequests.Wire(replica.Id)} is no longer in the service; the replica "
                    + "cannot be synchronised. Unregister it and take the area offline again.").ConfigureAwait(false);
                return;
            }

            (IFeatureSource source, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            if (await LayerDocumentAsync(context, faces, serviceName, stored.Id).ConfigureAwait(false) is not { } document)
            {
                await RefuseAsync(context, 502, $"Layer {stored.Id}'s own document could not be read for the sync.").ConfigureAwait(false);
                return;
            }

            ReplicaLayerQuery query = new(stored.QueryOption, stored.Where, stored.UseGeometry);
            Geometry? area = null;
            int srid = replica.Wkid ?? layer.PublishedSrid;

            if (replica.Geometry is { } stored2
                && OfflineReplicaRequests.TryReadArea(stored2, null, replica.Wkid?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    srid, out Geometry? read, out _, out int readSrid, out _))
            {
                area = read;
                srid = readSrid;
            }

            CreateReplicaRequest scope = new(replica.Name, [stored.Id], new Dictionary<int, ReplicaLayerQuery> { [stored.Id] = query },
                area, area?.Envelope, srid, replica.Geometry, replica.ReturnAttachments, replica.AttachmentsSyncDirection, "perLayer", false);

            if (Filter.Of(query, scope, described, out string? why) is not { } filter)
            {
                await RefuseAsync(context, 409, $"Layer {stored.Id}'s stored filter no longer reads: {why}").ConfigureAwait(false);
                return;
            }

            planned.Add(new Planned(stored.Id, layer, source, described, document, filter));
        }

        if (await WktsAsync(context, planned.Select(p => p.Wkid), cancellation).ConfigureAwait(false) is not { } wkts)
        {
            return;
        }

        List<OfflineLayer> shapes = [.. planned.Select(p => OfflineReplicaShapes.Layer(
            service.Name, p.Id, p.Document, wkts[p.Wkid], _ => Empty<OfflineRow>(), _ => Empty<OfflineAttachment>()))];

        ReplicaJobs jobs = services.GetRequiredService<ReplicaJobs>();
        if (jobs.Start(caller.Principal.Id, service.Id,
                downloading ? "esriReplicaResponseTypeEdits" : "esriReplicaResponseTypeNoEdits") is not { } job)
        {
            await BusyAsync(context).ConfigureAwait(false);
            return;
        }

        job.ReplicaName = replica.Name;
        job.ReplicaId = replica.Id;
        bool jsonSync = request.DataFormat == "json";
        job.DownloadName = replica.Name + (jsonSync ? "_changes.json" : "_changes.geodatabase");

        // ---- the upload, on this request ----
        HashSet<Guid> uploaded = [];

        if (uploading)
        {
            job.Status = "ImportChanges";

            string? refused = await UploadAsync(
                context, faces, serviceName, connections, uploadPath!, shapes, planned,
                [.. covered.Where(c => c.Direction is "upload" or "bidirectional").Select(c => c.Stored.Id)],
                request.ReturnAttachments && replica.ReturnAttachments && replica.AttachmentsSyncDirection != "none",
                uploaded, cancellation).ConfigureAwait(false);

            if (refused is not null)
            {
                job.Status = "Failed";
                job.Error = refused;
                job.Updated = jobs.Now;
                await RefuseAsync(context, 400, refused).ConfigureAwait(false);
                return;
            }

            ArcGisAppendEndpoints.Spend(request.UploadId!);
        }

        // The new generation is taken after the upload, so the client's own edits are inside this download's window and
        // are left out of it by GlobalID (`uploaded`).
        DateTimeOffset generation = DateTimeOffset.FromUnixTimeMilliseconds(jobs.Now.ToUnixTimeMilliseconds());

        async Task WorkAsync(CancellationToken stopping)
        {
            List<ReplicaLayer> advanced = [];
            List<OfflineLayerChanges> jsonChanges = [];

            if (downloading)
            {
                job.Status = "ExportChanges";
                job.Updated = jobs.Now;
                List<OfflineLayerChanges> changes = [];

                foreach ((Planned p, OfflineLayer shape) in planned.Zip(shapes))
                {
                    (ReplicaLayer stored, long from, string direction) = covered.First(c => c.Stored.Id == p.Id);

                    if (direction is not ("download" or "bidirectional"))
                    {
                        continue;
                    }

                    ReplicaChanges found = OfflineReplicaShapes.WithoutOwn(
                        await ChangesAsync(
                            connections, p, shape, DateTimeOffset.FromUnixTimeMilliseconds(from), generation, stopping).ConfigureAwait(false),
                        OfflineReplicaShapes.IndexOf(shape, shape.GlobalIdField!),
                        uploaded);

                    if (found.Inserts.Count + found.Updates.Count + found.Deletes.Count > 0)
                    {
                        changes.Add(new OfflineLayerChanges(shape, found.Inserts, found.Updates, found.Deletes, [], [], []));
                    }
                }

                string file = jobs.FileFor(job, jsonSync ? ".json" : ".geodatabase");

                if (!jsonSync)
                {
                    await writer!.WriteDeltaAsync(new OfflineDelta(replica.Id, generation.ToUnixTimeMilliseconds(), changes, service.Name), file, stopping)
                        .ConfigureAwait(false);
                }

                job.File = file;
                jsonChanges = changes;
            }

            foreach (ReplicaLayer stored in replica.Layers)
            {
                bool down = covered.Any(c => c.Stored.Id == stored.Id && c.Direction is "download" or "bidirectional");

                // A layer only uploaded keeps its generation: advancing it would skip the server's changes it has not had.
                advanced.Add(down ? stored with { ServerGeneration = generation.ToUnixTimeMilliseconds() } : stored);
            }

            if (jsonSync && downloading && job.File is { } jsonFile)
            {
                // JSON edits state the generations the sync leaves the replica at, so they are written after they are known.
                await OfflineJsonReplica.WriteChangesAsync(jsonFile, replica, jsonChanges, advanced, stopping).ConfigureAwait(false);
            }

            if (request.CloseReplica)
            {
                await store.DeleteAsync(replica.Id, stopping).ConfigureAwait(false);
            }
            else
            {
                await store.AdvanceAsync(replica.Id, advanced, jobs.Now, stopping).ConfigureAwait(false);
            }

            job.Generations = advanced;
            job.Status = "Completed";
            job.Updated = jobs.Now;
        }

        await RunAsync(context, job, request.Async, WorkAsync, cancellation).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an uploaded delta and applies it: the rows through the service's <c>applyEdits</c> by GlobalID, the
    /// attachments through each layer's attachment store. Answers null when it was applied, or why not; every GlobalID
    /// the upload names is added to <paramref name="uploaded"/>.
    /// </summary>
    private static async Task<string?> UploadAsync(
        HttpContext context,
        ReplicaFaces faces,
        string serviceName,
        LayerConnections connections,
        string path,
        List<OfflineLayer> shapes,
        List<Planned> planned,
        HashSet<int> uploadable,
        bool attachments,
        HashSet<Guid> uploaded,
        CancellationToken cancellation)
    {
        OfflineDelta delta;

        // ADR-288 condition 2: what a client uploads is kept for reading when `Graticula:Offline:KeepUploadsIn` names a
        // folder. Off unless set, and meant for a measurement, not for a site — the file is the client's data.
        if (context.RequestServices.GetService<Microsoft.Extensions.Configuration.IConfiguration>()?["Graticula:Offline:KeepUploadsIn"]
                is { Length: > 0 } keep)
        {
            try
            {
                Directory.CreateDirectory(keep);
                File.Copy(path, Path.Combine(keep, $"upload-{DateTime.UtcNow:yyyyMMddHHmmssfff}.geodatabase"), overwrite: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A measurement aid that fails does not fail the sync.
            }
        }

        // ADR-288 condition 7: the upload is parsed in a child process, never here. A build without a reader registered
        // refuses rather than falling back to reading in-process.
        if (context.RequestServices.GetService<IOfflineDeltaReader>() is not { } reader)
        {
            return "This server has no offline reader installed, so it cannot read an upload (ADR-288 condition 7). "
                + "Nothing was applied.";
        }

        try
        {
            delta = await reader.ReadDeltaAsync(path, shapes, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or FormatException or NotSupportedException or InvalidOperationException)
        {
            return $"The uploaded delta could not be read: {e.Message} Nothing was applied.";
        }

        List<ReplicaLayerEdits> edits = [];

        foreach (OfflineLayerChanges changes in delta.Layers.Where(c => uploadable.Contains(c.Layer.Id)))
        {
            Planned p = planned.First(x => x.Id == changes.Layer.Id);
            OfflineLayer shape = shapes.First(s => s.Id == changes.Layer.Id);
            OfflineLayerChanges named = changes with { Layer = shape };

            if (shape.GlobalIdField is not { } globalIdField)
            {
                return $"Layer {shape.Id} has no GlobalID, so its offline edits cannot be named. Nothing was applied.";
            }

            HashSet<Guid> referenced = OfflineReplicaShapes.GlobalIdsOf(named);
            IReadOnlyDictionary<Guid, long> existing = referenced.Count == 0
                ? new Dictionary<Guid, long>()
                : await connections.ObjectIdsOfAsync(p.Layer, globalIdField, referenced, cancellation).ConfigureAwait(false);

            // <b>A row the client changed and the server had already deleted is not the client's own edit</b> — ADR-288
            // §5.5, measured 2026-11-10: its update is dropped, so the download must still send the server's delete, or
            // the phone keeps a row the server no longer has. Only what was applied is left out of the download.
            HashSet<Guid> changedOrRemoved = OfflineReplicaShapes.GlobalIdsOf(named with
            {
                Inserts = [],
                AttachmentInserts = [],
                AttachmentUpdates = [],
                AttachmentDeletes = [],
            });
            uploaded.UnionWith(referenced.Where(id => !changedOrRemoved.Contains(id) || existing.ContainsKey(id)));

            ReplicaLayerEdits layerEdits = OfflineReplicaShapes.Edits(
                named, OfflineReplicaShapes.EditableFields(p.Document), existing.Keys.ToHashSet());
            if (layerEdits.Adds is not null || layerEdits.Updates is not null || layerEdits.Deletes is not null)
            {
                edits.Add(layerEdits);
            }
        }

        if (edits.Count > 0)
        {
            JsonArray entries = [];

            foreach (ReplicaLayerEdits e in edits)
            {
                JsonObject entry = new() { ["id"] = e.LayerId };

                if (e.Adds is not null) entry["adds"] = JsonNode.Parse(e.Adds);
                if (e.Updates is not null) entry["updates"] = JsonNode.Parse(e.Updates);
                if (e.Deletes is not null) entry["deletes"] = JsonNode.Parse(e.Deletes);

                entries.Add(entry);
            }

            Dictionary<string, StringValues> form = new(StringComparer.OrdinalIgnoreCase)
            {
                ["f"] = "json",
                ["edits"] = entries.ToJsonString(),
                ["useGlobalIds"] = "true",
                ["rollbackOnFailure"] = "true",
            };

            (int status, byte[] body) = await ServiceQuery.InnerRequestAsync(
                context, FeatureServerPath(context) + "/applyEdits", HttpMethods.Post, [], form, null,
                inner => faces.ApplyEdits(inner, serviceName)).ConfigureAwait(false);

            if (EditFailure(status, body) is { } failed)
            {
                return $"The uploaded edits were refused, and none was applied: {failed}";
            }
        }

        if (attachments)
        {
            foreach (OfflineLayerChanges changes in delta.Layers.Where(c => uploadable.Contains(c.Layer.Id)))
            {
                Planned p = planned.First(x => x.Id == changes.Layer.Id);

                if (await ApplyAttachmentsAsync(connections, p, shapes.First(s => s.Id == p.Id), changes, cancellation).ConfigureAwait(false)
                    is { } refused)
                {
                    return refused;
                }
            }
        }

        return null;
    }

    /// <summary>What went wrong in an <c>applyEdits</c> answer, or null when every edit succeeded.</summary>
    internal static string? EditFailure(int status, byte[] body)
    {
        JsonNode? answer;

        try
        {
            answer = body.Length == 0 ? null : JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return $"applyEdits answered {status} with a body that is not JSON.";
        }

        if (answer is JsonObject refusal && refusal["error"]?["message"] is JsonValue message && message.TryGetValue(out string? said))
        {
            return said;
        }

        if (status != StatusCodes.Status200OK)
        {
            return $"applyEdits answered {status}.";
        }

        if (answer is JsonArray layers)
        {
            foreach (JsonNode? layer in layers)
            {
                foreach (string kind in (string[])["addResults", "updateResults", "deleteResults"])
                {
                    if (layer?[kind] is JsonArray results
                        && results.FirstOrDefault(r => r?["success"] is JsonValue s && s.TryGetValue(out bool ok) && !ok) is { } bad)
                    {
                        string? description = bad["error"]?["description"]?.GetValue<string>();
                        return $"layer {layer["id"]}: {description ?? "an edit was refused"}";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Applies a layer's uploaded attachment changes. Answers null, or why not.</summary>
    private static async Task<string?> ApplyAttachmentsAsync(
        LayerConnections connections, Planned p, OfflineLayer shape, OfflineLayerChanges changes, CancellationToken cancellation)
    {
        if (changes.AttachmentInserts.Count + changes.AttachmentUpdates.Count + changes.AttachmentDeletes.Count == 0)
        {
            return null;
        }

        if (!p.Layer.KeepsAttachments || shape.GlobalIdField is not { } globalIdField)
        {
            return $"Layer {p.Id} keeps no attachments, and the upload carries some. The rows were applied; the attachments were not.";
        }

        IAttachmentStore store = connections.AttachmentsFor(p.Layer);
        HashSet<Guid> parents = [.. changes.AttachmentInserts.Concat(changes.AttachmentUpdates).Select(a => a.ParentGlobalId)];
        IReadOnlyDictionary<Guid, long> owners = parents.Count == 0
            ? new Dictionary<Guid, long>()
            : await connections.ObjectIdsOfAsync(p.Layer, globalIdField, parents, cancellation).ConfigureAwait(false);

        foreach (OfflineAttachment added in changes.AttachmentInserts)
        {
            if (!owners.TryGetValue(added.ParentGlobalId, out long feature))
            {
                continue; // its feature was deleted on the server, whose delete wins (ADR-288 §5.5)
            }

            await using MemoryStream bytes = new(added.Data.ToArray(), writable: false);
            (string sniffed, Stream content) = await ContentSniffer.SniffAsync(bytes, cancellation).ConfigureAwait(false);
            await store.AddAsync(feature, Path.GetFileName(added.Name), sniffed, added.ContentType, content, cancellation).ConfigureAwait(false);
        }

        foreach (OfflineAttachment changed in changes.AttachmentUpdates)
        {
            if (!owners.TryGetValue(changed.ParentGlobalId, out long feature)
                || changed.AttachmentId is < 1 or > int.MaxValue
                || OfflineReplicaShapes.AttachmentGlobalId(p.Layer.Id, changed.AttachmentId) != changed.GlobalId)
            {
                continue;
            }

            await using MemoryStream bytes = new(changed.Data.ToArray(), writable: false);
            (string sniffed, Stream content) = await ContentSniffer.SniffAsync(bytes, cancellation).ConfigureAwait(false);
            await store.UpdateAsync(feature, (int)changed.AttachmentId, Path.GetFileName(changed.Name), sniffed, changed.ContentType, content, cancellation)
                .ConfigureAwait(false);
        }

        // An attachment is deleted only when its id is one this server gave and its GlobalID is the one derived from it.
        int[] deletes = [.. changes.AttachmentDeletes
            .Where(k => k.ObjectId is >= 1 and <= int.MaxValue && OfflineReplicaShapes.AttachmentGlobalId(p.Layer.Id, k.ObjectId) == k.GlobalId)
            .Select(k => (int)k.ObjectId)];

        if (deletes.Length > 0)
        {
            await store.DeleteAsync(deletes, cancellation).ConfigureAwait(false);
        }

        return null;
    }

    // ------------------------------------------------------------------------------------------------ the rest

    /// <summary><c>unRegisterReplica</c> — Esri, <i>Unregister Replica</i>: the registration goes; the device keeps its file.</summary>
    private static async Task UnregisterAsync(
        HttpContext context, string serviceName, CatalogFallback catalog, IOfflineSyncStore store, IAuditLog audit, CancellationToken cancellation)
    {
        if (await ServiceLookup.FeatureServiceAsync(context, catalog, serviceName, cancellation).ConfigureAwait(false) is not { } service
            || await SignedInAsync(context).ConfigureAwait(false) is not { } caller)
        {
            return;
        }

        Func<string, string?> field = await FieldsAsync(context, cancellation).ConfigureAwait(false);

        if (!OfflineReplicaRequests.TryReadReplicaId(field("replicaID"), out Guid id))
        {
            await RefuseAsync(context, 400, $"replicaID '{field("replicaID")}' is not a replica id.").ConfigureAwait(false);
            return;
        }

        if (await OwnReplicaAsync(context, store, service, caller, id, cancellation).ConfigureAwait(false) is not { } replica)
        {
            return;
        }

        await store.DeleteAsync(replica.Id, cancellation).ConfigureAwait(false);
        await AdminEndpoints.AuditAsync(context, audit, "service.replica.unregister", service.Name,
            AdminEndpoints.Detail(new { replica = replica.Id, name = replica.Name }), succeeded: true, cancellation).ConfigureAwait(false);

        await Results.Json(new { success = true }).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>replicas</c> — Esri, <i>Replicas</i>: the caller's own, or every one to an administrator.</summary>
    private static async Task ListAsync(
        HttpContext context, string serviceName, CatalogFallback catalog, IOfflineSyncStore store, CancellationToken cancellation)
    {
        if (await ServiceLookup.FeatureServiceAsync(context, catalog, serviceName, cancellation).ConfigureAwait(false) is not { } service
            || await SignedInAsync(context).ConfigureAwait(false) is not { } caller)
        {
            return;
        }

        bool all = IsAdministrator(caller);
        IReadOnlyList<ReplicaRecord> replicas = await store.ListAsync(service.Id, cancellation).ConfigureAwait(false);

        await Results.Json(replicas
                .Where(r => all || r.OwnerId == caller.Principal.Id)
                .Select(r => new { replicaName = r.Name, replicaID = OfflineReplicaRequests.Wire(r.Id) }))
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary><c>replicas/{id}</c> — Esri, <i>Replica</i>: one registration, as ArcGIS describes it.</summary>
    private static async Task ReplicaAsync(
        HttpContext context, string serviceName, string replicaId, CatalogFallback catalog, IOfflineSyncStore store,
        CancellationToken cancellation)
    {
        if (await ServiceLookup.FeatureServiceAsync(context, catalog, serviceName, cancellation).ConfigureAwait(false) is not { } service
            || await SignedInAsync(context).ConfigureAwait(false) is not { } caller)
        {
            return;
        }

        if (!OfflineReplicaRequests.TryReadReplicaId(replicaId, out Guid id))
        {
            await RefuseAsync(context, 400, $"'{replicaId}' is not a replica id.").ConfigureAwait(false);
            return;
        }

        if (await OwnReplicaAsync(context, store, service, caller, id, cancellation).ConfigureAwait(false) is not { } replica)
        {
            return;
        }

        await Results.Json(OfflineReplicaShapes.Replica(replica)).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A replica job's status — the <c>statusUrl</c> of an asynchronous request.</summary>
    private static async Task JobAsync(
        HttpContext context, string serviceName, Guid jobId, CatalogFallback catalog, ReplicaJobs jobs, CancellationToken cancellation)
    {
        if (await ServiceLookup.FeatureServiceAsync(context, catalog, serviceName, cancellation).ConfigureAwait(false) is not { } service
            || await SignedInAsync(context).ConfigureAwait(false) is not { } caller)
        {
            return;
        }

        if (jobs.Find(jobId) is not { } job || job.ServiceId != service.Id || (job.Owner != caller.Principal.Id && !IsAdministrator(caller)))
        {
            await RefuseAsync(context, 404,
                $"There is no replica job {jobId} of yours here. A job is kept for a day, on the server that ran it, and does not "
                + "survive its restart.").ConfigureAwait(false);
            return;
        }

        await Results.Json(OfflineReplicaShapes.Status(job, ResultUrl(context, job))).ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>A replica job's file — to the caller who asked for it, and nobody else.</summary>
    private static async Task FileAsync(
        HttpContext context, string serviceName, Guid jobId, CatalogFallback catalog, ReplicaJobs jobs, CancellationToken cancellation)
    {
        if (await ServiceLookup.FeatureServiceAsync(context, catalog, serviceName, cancellation).ConfigureAwait(false) is not { } service
            || await SignedInAsync(context).ConfigureAwait(false) is not { } caller)
        {
            return;
        }

        if (jobs.Find(jobId) is not { File: { } file } job || job.ServiceId != service.Id || job.Owner != caller.Principal.Id
            || !File.Exists(file))
        {
            await RefuseAsync(context, 404, $"There is no replica file {jobId} of yours here.").ConfigureAwait(false);
            return;
        }

        context.Response.Headers.CacheControl = "private, no-store";
        await Results.File(file, file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? OfflineJsonReplica.MediaType : "application/x-sqlite3",
                job.DownloadName).ExecuteAsync(context).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------------------------------------ reading rows

    /// <summary>A layer of a replica, with what its rows are read through.</summary>
    internal sealed record Planned(
        int Id, PublishedLayer Layer, IFeatureSource Source, LayerDescription Described, string Document, Filter Filter)
    {
        /// <summary>The reference its document states, which its rows are read in.</summary>
        public int Wkid => OfflineReplicaShapes.WkidOf(JsonDocument.Parse(Document).RootElement) ?? Layer.PublishedSrid;
    }

    /// <summary>What a layer's rows are filtered by in a replica.</summary>
    internal sealed record Filter(string Option, string Where, bool UseGeometry, ParsedWhere? Parsed, Geometry? Area, int AreaSrid)
    {
        public static Filter? Of(ReplicaLayerQuery query, CreateReplicaRequest request, LayerDescription described, out string? why)
        {
            why = null;
            ParsedWhere? parsed = null;

            if (query.QueryOption == "useFilter" && query.Where.Length > 0)
            {
                Dictionary<string, FieldType> types = new(StringComparer.OrdinalIgnoreCase);

                foreach (FieldDescription field in described.Fields)
                {
                    types[field.Name] = field.Type;
                }

                if (!WhereClause.TryParse(query.Where, [.. described.Fields.Select(f => f.Name)], LayerDefinition.Quote,
                        out ParsedWhere where, out why, types))
                {
                    return null;
                }

                parsed = where;
            }

            Geometry? area = query.QueryOption == "useFilter" && query.UseGeometry ? request.Filter : null;
            return new Filter(query.QueryOption, query.Where, query.UseGeometry, parsed, area, request.Srid);
        }
    }

    /// <summary>
    /// A layer's rows as the port carries them, in object id order: those its filter keeps, at <paramref name="moment"/>
    /// or now, and only those of <paramref name="ids"/> when given.
    /// </summary>
    internal static async IAsyncEnumerable<OfflineRow> RowsAsync(
        Planned p, IReadOnlyList<OfflineField> fields, DateTimeOffset? moment, IReadOnlyList<long>? ids,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (p.Filter.Option == "none" && ids is null)
        {
            yield break;
        }

        string identity = p.Layer.Definition.IdentityColumn;
        List<string> names = [.. p.Described.Fields.Select(f => f.Name)];
        bool table = p.Layer.IsTable;
        GeometryOrdinates keep = p.Described.StoredOrdinates;

        List<IReadOnlyList<long>?> batches = [];

        if (ids is null)
        {
            batches.Add(null);
        }
        else
        {
            batches.AddRange(ids.Chunk(IdsPerQuery));
        }

        foreach (IReadOnlyList<long>? batch in batches)
        {
            for (int offset = 0; ; offset += FeatureQuery.MaximumLimit)
            {
                FeatureQuery query = new(
                    FeatureQuery.MaximumLimit,
                    fields: names,
                    offset: offset,
                    includeGeometry: !table,
                    orderBy: [new SortKey(identity, false)],
                    spatial: p.Filter.Area is null || table ? null : new SpatialFilter(p.Filter.Area),
                    identities: batch,
                    outSrid: table ? null : p.Wkid,
                    where: p.Filter.Option == "all" ? null : p.Filter.Parsed,
                    filterSrid: p.Filter.Area is null || table ? null : p.Filter.AreaSrid)
                {
                    KeepOrdinates = keep,
                    HistoricMoment = moment,
                };

                int read = 0;

                await foreach (Feature feature in p.Source.ReadAsync(query, cancellation).ConfigureAwait(false))
                {
                    read++;
                    object?[] values = new object?[fields.Count];

                    for (int i = 0; i < fields.Count; i++)
                    {
                        int at = feature.Schema.IndexOf(fields[i].Name);
                        values[i] = at < 0 ? null : OfflineReplicaShapes.Normalise(feature[at], fields[i].EsriType);
                    }

                    yield return new OfflineRow(values, feature.Geometry);
                }

                if (read < FeatureQuery.MaximumLimit)
                {
                    break;
                }
            }
        }
    }

    /// <summary>The attachments of the rows a replica holds, each read whole, as the port carries them.</summary>
    internal static async IAsyncEnumerable<OfflineAttachment> AttachmentsAsync(
        LayerConnections connections, Planned p, OfflineLayer shape, DateTimeOffset? moment,
        [EnumeratorCancellation] CancellationToken cancellation)
    {
        if (shape.GlobalIdField is not { } globalIdField)
        {
            yield break;
        }

        int objectIdAt = OfflineReplicaShapes.IndexOf(shape, shape.ObjectIdField);
        int globalAt = OfflineReplicaShapes.IndexOf(shape, globalIdField);
        IAttachmentStore store = connections.AttachmentsFor(p.Layer);
        List<(long ObjectId, Guid GlobalId)> owners = [];

        await foreach (OfflineRow row in RowsAsync(p, shape.Fields, moment, null, cancellation).ConfigureAwait(false))
        {
            if (row.Values[objectIdAt] is { } oid && row.Values[globalAt] is Guid gid)
            {
                owners.Add((Convert.ToInt64(oid, System.Globalization.CultureInfo.InvariantCulture), gid));
            }
        }

        foreach ((long objectId, Guid parent) in owners)
        {
            foreach (AttachmentInfo info in await store.ListAsync(objectId, cancellation).ConfigureAwait(false))
            {
                OpenAttachment? open = await store.OpenAsync(info.Id, cancellation).ConfigureAwait(false);

                if (open is null)
                {
                    continue;
                }

                byte[] data;

                await using (open.ConfigureAwait(false))
                {
                    using MemoryStream copy = new();
                    await open.Content.CopyToAsync(copy, cancellation).ConfigureAwait(false);
                    data = copy.ToArray();
                }

                yield return new OfflineAttachment(
                    info.Id, parent, OfflineReplicaShapes.AttachmentGlobalId(p.Layer.Id, info.Id), info.Name, info.ContentType, data);
            }
        }
    }

    /// <summary>
    /// What changed in a layer between two generations — ADR-288 §5.4: the features its history names since
    /// <paramref name="from"/>, read as they were then and as they are at <paramref name="to"/>, and compared.
    /// </summary>
    private static async Task<ReplicaChanges> ChangesAsync(
        LayerConnections connections, Planned p, OfflineLayer shape, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellation)
    {
        (HashSet<long> changed, bool emptied) = await ChangedSinceAsync(connections.HistoryFor(p.Layer), from, cancellation)
            .ConfigureAwait(false);

        if (changed.Count == 0 && !emptied)
        {
            return new ReplicaChanges([], [], []);
        }

        int objectIdAt = OfflineReplicaShapes.IndexOf(shape, shape.ObjectIdField);
        int globalAt = OfflineReplicaShapes.IndexOf(shape, shape.GlobalIdField!);

        // A truncate names no feature, so every feature there was then is a candidate.
        IReadOnlyList<long>? ids = emptied ? null : [.. changed.Order()];

        async Task<Dictionary<long, OfflineRow>> AtAsync(DateTimeOffset moment)
        {
            Dictionary<long, OfflineRow> rows = [];

            await foreach (OfflineRow row in RowsAsync(p, shape.Fields, moment, ids, cancellation).ConfigureAwait(false))
            {
                if (row.Values[objectIdAt] is { } oid)
                {
                    rows[Convert.ToInt64(oid, System.Globalization.CultureInfo.InvariantCulture)] = row;
                }
            }

            return rows;
        }

        Dictionary<long, OfflineRow> then = await AtAsync(from).ConfigureAwait(false);
        Dictionary<long, OfflineRow> now = await AtAsync(to).ConfigureAwait(false);

        return OfflineReplicaShapes.Diff(then, now, globalAt);
    }

    /// <summary>
    /// The features a layer's history names after a moment, and whether it was emptied since — read newest first, page
    /// by page, until the history reaches the moment.
    /// </summary>
    internal static async Task<(HashSet<long> Changed, bool Emptied)> ChangedSinceAsync(
        IFeatureHistory history, DateTimeOffset since, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(history);

        HashSet<long> changed = [];
        bool emptied = false;
        DateTimeOffset? before = null;
        int limit = HistoryPage;

        while (true)
        {
            IReadOnlyList<FeatureChange> page = await history.ChangesAsync(before, limit, cancellation).ConfigureAwait(false);
            bool reached = false;

            foreach (FeatureChange change in page)
            {
                if (change.At <= since)
                {
                    reached = true;
                    break;
                }

                if (change.Kind == "emptied")
                {
                    emptied = true;
                }
                else
                {
                    changed.Add(change.ObjectId);
                }
            }

            if (reached || page.Count < limit)
            {
                return (changed, emptied);
            }

            // <b>A page that ends inside one moment is read again from just after it</b>, because one transaction's
            // changes share a moment and `before` is strict: the next page would skip the rest of them. The set makes
            // the repeat harmless; a page that is all one moment is read again, wider, from where it began.
            DateTimeOffset last = page[^1].At;

            if (page[0].At == last)
            {
                limit *= 2;
                continue;
            }

            before = last.AddTicks(1);
        }
    }

    // ------------------------------------------------------------------------------------------------ helpers

    /// <summary>
    /// Checks what both replica operations need, and answers when one is missing: the service, a signed-in caller, sync
    /// on, and an edit offered to them.
    /// </summary>
    private static async Task<(PublishedService Service, RequestPrincipal Caller)?> AdmitAsync(
        HttpContext context, string serviceName, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;

        if (await ServiceLookup.FeatureServiceAsync(context, services.GetRequiredService<CatalogFallback>(), serviceName, cancellation)
                .ConfigureAwait(false) is not { } service
            || await SignedInAsync(context).ConfigureAwait(false) is not { } caller)
        {
            return null;
        }

        if (!SyncFlags.IsOn(service))
        {
            await RefuseAsync(context, 400,
                $"Sync is not enabled on '{service.QualifiedName}'. Its owner enables it (updateDefinition syncEnabled, or "
                + "POST /admin/services/{name}/sync); only hosted feature services can have it (ADR-288 §5.8)."
                + (SyncFlags.Refusal(service) is { } why ? " " + why : string.Empty)).ConfigureAwait(false);
            return null;
        }

        string capabilities = await Program.ServiceCapabilitiesAsync(
            context, service, services.GetRequiredService<ServiceContexts>(), cancellation).ConfigureAwait(false);

        if (!SyncDocument.OffersSync(capabilities))
        {
            await RefuseAsync(context, 403,
                "Taking this service offline and synchronising it is for those who may edit it, and you may not.").ConfigureAwait(false);
            return null;
        }

        return (service, caller);
    }

    /// <summary>
    /// The mobile geodatabase writer from DI, or null after answering 501: the port's implementation lives in another
    /// project (ADR-288 §6), and a server without it says so rather than failing.
    /// </summary>
    private static async Task<IMobileGeodatabaseWriter?> WriterAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<IMobileGeodatabaseWriter>() is { } writer)
        {
            return writer;
        }

        await RefuseAsync(context, 501,
            "This server has no mobile geodatabase writer installed, so it cannot write the file an offline copy is "
            + "(ADR-288 §6). Nothing was created or changed.").ConfigureAwait(false);
        return null;
    }

    /// <summary>The caller, or null after answering 401 to an anonymous one.</summary>
    private static async Task<RequestPrincipal?> SignedInAsync(HttpContext context)
    {
        if (context.Features.Get<RequestPrincipal>() is { Principal.IsAnonymous: false } caller)
        {
            return caller;
        }

        await RefuseAsync(context, 401, "Replicas belong to somebody: sign in first.").ConfigureAwait(false);
        return null;
    }

    /// <summary>A replica of this service the caller owns or administers, or null after answering.</summary>
    private static async Task<ReplicaRecord?> OwnReplicaAsync(
        HttpContext context, IOfflineSyncStore store, PublishedService service, RequestPrincipal caller, Guid id,
        CancellationToken cancellation)
    {
        ReplicaRecord? replica = await store.FindAsync(id, cancellation).ConfigureAwait(false);

        if (replica is null || replica.ServiceId != service.Id)
        {
            await RefuseAsync(context, 404, $"Replica {OfflineReplicaRequests.Wire(id)} is not registered on this service.").ConfigureAwait(false);
            return null;
        }

        if (replica.OwnerId != caller.Principal.Id && !IsAdministrator(caller))
        {
            await RefuseAsync(context, 403, $"Replica {OfflineReplicaRequests.Wire(id)} is not yours.").ConfigureAwait(false);
            return null;
        }

        return replica;
    }

    private static bool IsAdministrator(RequestPrincipal caller) =>
        LayerAccess.MayManage(null, caller.Principal, caller.Authorization);

    /// <summary>Runs a job: here, answering its result, or behind a status URL.</summary>
    private static async Task RunAsync(
        HttpContext context, ReplicaJob job, bool async, Func<CancellationToken, Task> work, CancellationToken cancellation,
        bool embedded = false)
    {
        ReplicaJobs jobs = context.RequestServices.GetRequiredService<ReplicaJobs>();

        async Task GuardedAsync(CancellationToken stopping)
        {
            try
            {
                await work(stopping).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException || !stopping.IsCancellationRequested)
            {
                job.Status = "Failed";
                job.Error = e is OperationCanceledException ? "The job was stopped." : $"The replica could not be written: {e.Message}";
                job.Updated = jobs.Now;

                if (job.File is { } file)
                {
                    ReplicaJobs.TryDelete(file);
                    job.File = null;
                }
            }
        }

        if (!async)
        {
            await GuardedAsync(cancellation).ConfigureAwait(false);
            int status = job.Status == "Failed" ? 500 : 200;

            // esriTransportTypeEmbedded: the JSON replica itself is the answer, as ArcGIS answers it.
            if (embedded && status == 200 && job.File is { } embeddedFile && File.Exists(embeddedFile))
            {
                context.Response.Headers.CacheControl = "private, no-store";
                await Results.File(embeddedFile, OfflineJsonReplica.MediaType).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await Results.Json(OfflineReplicaShapes.Status(job, ResultUrl(context, job)), statusCode: status)
                .ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        CancellationToken stopping = context.RequestServices.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        _ = Task.Run(() => GuardedAsync(stopping), CancellationToken.None);

        await Results.Json(new { statusUrl = TileFaces.Origin(context) + FeatureServerPath(context) + $"/jobs/{job.Id:D}" })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>Where a finished job's file is downloaded, or null.</summary>
    private static string? ResultUrl(HttpContext context, ReplicaJob job) =>
        job.Status == "Completed" && job.File is not null
            ? TileFaces.Origin(context) + FeatureServerPath(context) + $"/replicafiles/{job.Id:D}"
            : null;

    /// <summary>The path of the FeatureServer this request is under, without the path base.</summary>
    internal static string FeatureServerPath(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        int end = path.IndexOf("/FeatureServer", StringComparison.OrdinalIgnoreCase);
        return end < 0 ? path : path[..(end + "/FeatureServer".Length)];
    }

    /// <summary>A layer's own document, as <c>FeatureServer/{id}?f=json</c> answers this caller, or null.</summary>
    private static async Task<string?> LayerDocumentAsync(HttpContext context, ReplicaFaces faces, string serviceName, int id)
    {
        (int status, byte[] body) = await ServiceQuery.InnerRequestAsync(
            context, FeatureServerPath(context) + "/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HttpMethods.Get, Json(), null, new Dictionary<string, object?> { ["layerId"] = id },
            inner => faces.LayerDocument(inner, serviceName, id)).ConfigureAwait(false);

        return status == StatusCodes.Status200OK && body.Length > 0 ? Encoding.UTF8.GetString(body) : null;
    }

    /// <summary>The WKT of each reference, from the datastore's own definitions; null after answering when one is unknown.</summary>
    private static async Task<Dictionary<int, string>?> WktsAsync(HttpContext context, IEnumerable<int> wkids, CancellationToken cancellation)
    {
        (Dictionary<int, string>? wkts, PackageRefusal? refusal) =
            await OfflinePackager.WktsAsync(context.RequestServices, wkids, cancellation).ConfigureAwait(false);

        if (refusal is not null)
        {
            await RefuseAsync(context, refusal.Status, refusal.Message).ConfigureAwait(false);
            return null;
        }

        return wkts;
    }

    private static async Task<Func<string, string?>> FieldsAsync(HttpContext context, CancellationToken cancellation)
    {
        ArcGisParameters parameters = await ArcGisParameters.ReadAsync(context, cancellation).ConfigureAwait(false);
        return name => parameters[name] is { Count: > 0 } value && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString() : null;
    }

    private static Dictionary<string, StringValues> Json() => new(StringComparer.OrdinalIgnoreCase) { ["f"] = "json" };

    internal static Envelope? Union(IEnumerable<Envelope?> boxes)
    {
        Envelope? sofar = null;

        foreach (Envelope? box in boxes)
        {
            if (box is not { IsEmpty: false } b)
            {
                continue;
            }

            sofar = sofar is { } s
                ? new Envelope(Math.Min(s.MinX, b.MinX), Math.Min(s.MinY, b.MinY), Math.Max(s.MaxX, b.MaxX), Math.Max(s.MaxY, b.MaxY))
                : b;
        }

        return sofar;
    }

#pragma warning disable CS1998 // an empty sequence awaits nothing
    internal static async IAsyncEnumerable<T> Empty<T>()
    {
        yield break;
    }
#pragma warning restore CS1998

    private static Task BusyAsync(HttpContext context) =>
        RefuseAsync(context, 503,
            $"{ReplicaJobs.MaximumJobs} replica jobs are already held here, each kept a day after it finishes. Try again later.");

    private static object Error(int code, string message) => new { error = new { code, message, details = Array.Empty<string>() } };

    private static Task RefuseAsync(HttpContext context, int code, string message) =>
        Results.Json(Error(code, message), statusCode: code).ExecuteAsync(context);
}

/// <summary>Turns a service's sync on or off.</summary>
/// <param name="Enabled">Whether it is on.</param>
internal sealed record SyncSwitchRequest(bool Enabled);
