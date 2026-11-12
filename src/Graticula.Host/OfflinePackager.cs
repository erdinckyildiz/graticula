using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis.Offline;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Graticula.Host;

/// <summary>Why a replica could not be planned: the status a request answers with and the sentence it says.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Message">What is wrong.</param>
internal sealed record PackageRefusal(int Status, string Message);

/// <summary>Who owns a replica.</summary>
/// <param name="Id">The principal.</param>
/// <param name="Name">Their name, as <c>replicaOwner</c> says it.</param>
internal sealed record ReplicaOwner(Guid Id, string Name);

/// <summary>A replica checked and ready to be written.</summary>
/// <param name="Service">The feature service it is taken from.</param>
/// <param name="Request">The request it answers.</param>
/// <param name="ServiceDocument">The service's own document, which the file carries.</param>
/// <param name="Layers">Each layer, with what its rows are read through.</param>
/// <param name="Wkts">The WKT of each reference the layers are written in.</param>
internal sealed record PackagePlan(
    PublishedService Service,
    CreateReplicaRequest Request,
    string ServiceDocument,
    IReadOnlyList<OfflineReplicaEndpoints.Planned> Layers,
    IReadOnlyDictionary<int, string> Wkts);

/// <summary>
/// The core of <c>createReplica</c> without a request — ADR-288 §11a: a replica checked against the service, then written
/// as a mobile geodatabase and registered. <c>createReplica</c> calls it for a client's request; a map area's packaging
/// calls it for each feature service of the map.
/// </summary>
/// <remarks>
/// <para>
/// <b>What is not here is the service's and the layers' own documents.</b> Those are the documents
/// <c>FeatureServer</c> and <c>FeatureServer/{id}</c> answer a caller, with that caller's capabilities in them, and the
/// code that builds them writes to a response; this takes them as text from whoever holds a request. A request that has
/// none (a scheduled refresh) needs those two documents built without one, which is D-404's part of the work.
/// </para>
/// <para>
/// <b>Behaviour moved, not changed.</b> The order of the checks, their statuses and their sentences are those
/// <c>createReplica</c> had when it did all this inline.
/// </para>
/// </remarks>
internal static class OfflinePackager
{
    /// <summary>
    /// Checks a request against the service and reads what the file needs from the catalogue: each layer's source,
    /// description, document and filter, and the references' definitions.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="service">The feature service.</param>
    /// <param name="request">The replica asked for.</param>
    /// <param name="serviceDocument">The service's own document.</param>
    /// <param name="layerDocument">A layer's own document by id, or null when it cannot be read.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The plan, or why not.</returns>
    public static async Task<(PackagePlan? Plan, PackageRefusal? Refusal)> PlanAsync(
        IServiceProvider services,
        PublishedService service,
        CreateReplicaRequest request,
        string serviceDocument,
        Func<int, Task<string?>> layerDocument,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(layerDocument);

        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        LayerConnections connections = services.GetRequiredService<LayerConnections>();
        List<OfflineReplicaEndpoints.Planned> planned = [];

        foreach (int id in request.Layers)
        {
            PublishedLayer layer = service.LayerOrTable(id)!;
            ReplicaLayerQuery query = request.Queries[id];
            (IFeatureSource source, LayerDescription described) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);

            if (await layerDocument(id).ConfigureAwait(false) is not { } document)
            {
                return (null, new PackageRefusal(502, $"Layer {id}'s own document could not be read for the replica."));
            }

            if (OfflineReplicaEndpoints.Filter.Of(query, request, described, out string? why) is not { } filter)
            {
                return (null, new PackageRefusal(400, $"layerQueries: layer {id}'s where could not be read: {why}"));
            }

            if (request.Registered)
            {
                if (GlobalIds.FieldOf(described.Fields) is null)
                {
                    return (null, new PackageRefusal(400,
                        $"Layer {id} ('{layer.Definition.Name}') has no GlobalID, which a synchronised replica names its rows by. "
                        + "Add GlobalIDs to it first, or take a copy that is not synchronised (syncModel=none)."));
                }

                if (!await connections.HistoryFor(layer).IsOnAsync(cancellation).ConfigureAwait(false))
                {
                    return (null, new PackageRefusal(409,
                        $"Layer {id} ('{layer.Definition.Name}') no longer keeps its history, which sync is read from. "
                        + "Turn sync off and on again on the service (ADR-288 §5.3)."));
                }
            }

            planned.Add(new OfflineReplicaEndpoints.Planned(id, layer, source, described, document, filter));
        }

        (Dictionary<int, string>? wkts, PackageRefusal? refusal) =
            await WktsAsync(services, planned.Select(p => p.Wkid), cancellation).ConfigureAwait(false);

        return refusal is not null
            ? (null, refusal)
            : (new PackagePlan(service, request, serviceDocument, planned, wkts!), null);
    }

    /// <summary>The WKT of each reference, from the datastore's own definitions; the refusal when one is unknown.</summary>
    /// <param name="services">The services.</param>
    /// <param name="wkids">The references.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The definitions, or why not.</returns>
    public static async Task<(Dictionary<int, string>? Wkts, PackageRefusal? Refusal)> WktsAsync(
        IServiceProvider services, IEnumerable<int> wkids, CancellationToken cancellation)
    {
        NpgsqlDataSource db = services.GetRequiredKeyedService<NpgsqlDataSource>(Program.DatastorePool);
        Dictionary<int, string> wkts = [];

        foreach (int wkid in wkids.Distinct())
        {
            await using NpgsqlCommand command = db.CreateCommand("select srtext from spatial_ref_sys where srid = @srid");
            command.Parameters.AddWithValue("srid", wkid);

            if (await command.ExecuteScalarAsync(cancellation).ConfigureAwait(false) is not string wkt || wkt.Length == 0)
            {
                return (null, new PackageRefusal(409,
                    $"The datastore has no definition of reference {wkid}, which the offline copy must state."));
            }

            wkts[wkid] = wkt;
        }

        return (wkts, null);
    }

    /// <summary>
    /// Writes a planned replica to <paramref name="file"/> and, when it is registered, records it. A file left behind by
    /// a failure is removed.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="writer">The mobile geodatabase writer.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="replicaId">The replica's id when it is registered; null for a snapshot.</param>
    /// <param name="owner">Who owns it.</param>
    /// <param name="serviceUrl">The service's address as the file states it.</param>
    /// <param name="generation">The moment the copy is of, truncated to a millisecond.</param>
    /// <param name="file">Where to write it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The layers' server generations, which a registered replica holds.</returns>
    public static async Task<IReadOnlyList<ReplicaLayer>> WriteAsync(
        IServiceProvider services,
        IMobileGeodatabaseWriter writer,
        PackagePlan plan,
        Guid? replicaId,
        ReplicaOwner owner,
        string serviceUrl,
        DateTimeOffset generation,
        string file,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(owner);

        LayerConnections connections = services.GetRequiredService<LayerConnections>();
        IOfflineSyncStore store = services.GetRequiredService<IOfflineSyncStore>();
        CreateReplicaRequest request = plan.Request;
        PublishedService service = plan.Service;

        try
        {
            // The copy of a registered replica is the layer as it was at its generation, so the first sync begins exactly
            // where the copy ends; a snapshot is the layer as it is.
            DateTimeOffset? moment = request.Registered ? generation : null;
            List<OfflineLayer> layers = [];

            foreach (OfflineReplicaEndpoints.Planned p in plan.Layers)
            {
                OfflineLayer shape = OfflineReplicaShapes.Layer(
                    service.Name, p.Id, p.Document, plan.Wkts[p.Wkid],
                    _ => OfflineReplicaEndpoints.Empty<OfflineRow>(), _ => OfflineReplicaEndpoints.Empty<OfflineAttachment>());
                layers.Add(shape with
                {
                    Rows = token => OfflineReplicaEndpoints.RowsAsync(p, shape.Fields, moment, null, token),
                    Attachments = token => request.ReturnAttachments && shape.HasAttachments && p.Filter.Option != "none"
                        ? OfflineReplicaEndpoints.AttachmentsAsync(connections, p, shape, moment, token)
                        : OfflineReplicaEndpoints.Empty<OfflineAttachment>(),
                });
            }

            Envelope area = request.Extent
                ?? OfflineReplicaEndpoints.Union(layers.Select(l => l.Extent))
                ?? new Envelope(0, 0, 0, 0);
            List<ReplicaLayer> generations = [.. GenerationsOf(plan, generation)];

            OfflineReplica? replica = replicaId is { } rid
                ? new OfflineReplica(rid, request.Name, serviceUrl, owner.Name, generation.ToUnixTimeMilliseconds(), generation, area, request.Srid)
                : null;

            await writer.WriteAsync(new OfflineCopy(service.Name, plan.ServiceDocument, layers, replica), file, cancellation).ConfigureAwait(false);

            if (replicaId is { } registered)
            {
                await RegisterAsync(store, plan, registered, owner, generations, generation, cancellation).ConfigureAwait(false);
            }

            return generations;
        }
        catch
        {
            ReplicaJobs.TryDelete(file);
            throw;
        }
    }

    /// <summary>The generation each layer of a plan is copied at: the moment, with the filter the layer was read through.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="generation">The moment.</param>
    /// <returns>The layers' generations.</returns>
    public static IReadOnlyList<ReplicaLayer> GenerationsOf(PackagePlan plan, DateTimeOffset generation)
    {
        ArgumentNullException.ThrowIfNull(plan);

        return [.. plan.Layers.Select(p => new ReplicaLayer(
            p.Id, generation.ToUnixTimeMilliseconds(), p.Filter.Option, p.Filter.Where, p.Filter.UseGeometry))];
    }

    /// <summary>
    /// Records a registered replica — the one place a replica is registered, for a mobile geodatabase and for a JSON
    /// replica alike, so <c>synchronizeReplica</c>, <c>replicas</c> and <c>unRegisterReplica</c> see no difference.
    /// </summary>
    /// <param name="store">The registry.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="replicaId">The replica.</param>
    /// <param name="owner">Who owns it.</param>
    /// <param name="generations">The layers' generations.</param>
    /// <param name="generation">The moment the copy is of.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static Task RegisterAsync(
        IOfflineSyncStore store,
        PackagePlan plan,
        Guid replicaId,
        ReplicaOwner owner,
        IReadOnlyList<ReplicaLayer> generations,
        DateTimeOffset generation,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(owner);
        CreateReplicaRequest request = plan.Request;

        return store.CreateAsync(new ReplicaRecord(
            replicaId, request.Name, plan.Service.Id, owner.Id, owner.Name, "perLayer", generations,
            request.ReturnAttachments, request.AttachmentsSyncDirection, request.GeometryJson, request.Srid,
            generation, generation), cancellation);
    }

    /// <summary>
    /// Writes a planned replica as ArcGIS's JSON replica and, when it is registered, records it exactly as
    /// <see cref="WriteAsync"/> does. A file left behind by a failure is removed.
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="replicaId">The replica's id when it is registered; null for a snapshot.</param>
    /// <param name="owner">Who owns it.</param>
    /// <param name="featureServerUrl">The service's address, which attachment URLs hang from.</param>
    /// <param name="generation">The moment the copy is of.</param>
    /// <param name="file">Where to write it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The layers' server generations.</returns>
    public static async Task<IReadOnlyList<ReplicaLayer>> WriteJsonAsync(
        IServiceProvider services,
        PackagePlan plan,
        Guid? replicaId,
        ReplicaOwner owner,
        string featureServerUrl,
        DateTimeOffset generation,
        string file,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(owner);

        IReadOnlyList<ReplicaLayer> generations = GenerationsOf(plan, generation);
        await OfflineJsonReplica.WriteAsync(services.GetRequiredService<LayerConnections>().AttachmentsFor, plan, replicaId, generation, generations, featureServerUrl, file, cancellation)
            .ConfigureAwait(false);

        if (replicaId is { } registered)
        {
            try
            {
                await RegisterAsync(services.GetRequiredService<IOfflineSyncStore>(), plan, registered, owner, generations, generation, cancellation)
                    .ConfigureAwait(false);
            }
            catch
            {
                ReplicaJobs.TryDelete(file);
                throw;
            }
        }

        return generations;
    }

    /// <summary>
    /// A package's <c>properties</c> as ArcGIS's package item states them (research note §5a): what the replica was made of.
    /// </summary>
    /// <param name="plan">The plan the replica was written from.</param>
    /// <param name="replicaId">The replica.</param>
    /// <param name="serviceUrl">The service's address.</param>
    /// <param name="generations">The layers' server generations.</param>
    /// <returns>The JSON text.</returns>
    public static string PackageProperties(
        PackagePlan plan, Guid replicaId, string serviceUrl, IReadOnlyList<ReplicaLayer> generations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(generations);
        CreateReplicaRequest request = plan.Request;

        return JsonSerializer.Serialize(new
        {
            url = serviceUrl,
            layers = request.Layers,
            layerQueries = request.Queries.ToDictionary(
                q => q.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                q => new { queryOption = q.Value.QueryOption, where = q.Value.Where, useGeometry = q.Value.UseGeometry }),
            replicaID = OfflineReplicaRequests.Wire(replicaId),
            layerServerGens = generations.Select(g => new { serverGen = g.ServerGeneration, id = g.Id }),
            returnAttachments = request.ReturnAttachments,
            attachmentsSyncDirection = request.AttachmentsSyncDirection,
            syncModel = request.SyncModel,
            useSyncForRefresh = true,
            hasUtilityNetworkLayer = false,
        });
    }
}
