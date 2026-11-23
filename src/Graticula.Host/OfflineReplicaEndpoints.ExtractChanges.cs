using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Api.ArcGis.Offline;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Platform.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Graticula.Host;

/// <summary>
/// <c>FeatureServer/extractChanges</c> on a hosted service that tracks its changes — ADR-311.
/// </summary>
/// <remarks>
/// <para>
/// <b>The replica download's difference, asked without a replica.</b> Each layer's window is read by
/// <see cref="WindowAsync"/> — the features its history names since the window's start, read as they were then and as
/// they are at its end, through the layer's feature source with the replica filters (<see cref="Filter"/>) — and its
/// attachments by <see cref="AttachmentChangesAsync"/> from the attachment table's own record (ADR-290). Nothing here
/// keeps a record of its own.
/// </para>
/// <para>
/// <b>Generations are moments in epoch milliseconds</b>, as a replica's are, and stand on D-401's race exactly as a
/// download does (ADR-311 §5.9).
/// </para>
/// <para>
/// <b>Sources.</b> Esri's public REST reference, <i>Extract Changes (Feature Service)</i>
/// (https://developers.arcgis.com/rest/services-reference/enterprise/extract-changes-feature-service/, read 2026-11-22):
/// its parameters, the answer's keys, and that <c>deleteIds</c> are GlobalIDs.
/// </para>
/// </remarks>
internal static partial class OfflineReplicaEndpoints
{
    /// <summary>One layer of an extract, planned: its reads, its shape and its window.</summary>
    private sealed record ExtractLayer(Planned Plan, OfflineLayer Shape, long From, long To, long MinServerGen);

    /// <summary>One layer's changes, read.</summary>
    private sealed record ExtractedLayer(
        ExtractLayer Layer,
        IReadOnlyList<OfflineRow> Inserts,
        IReadOnlyList<OfflineRow> Updates,
        IReadOnlyList<OfflineKey> Deletes,
        IReadOnlyList<OfflineRow> DeletedAsTheyWere,
        IReadOnlyList<long>? FieldUpdates,
        bool? GeometryUpdates,
        AttachmentDownload? Attachments);

    private static async Task ExtractChangesAsync(HttpContext context, string serviceName, ReplicaFaces faces, CancellationToken cancellation)
    {
        IServiceProvider services = context.RequestServices;

        if (await ServiceLookup.FeatureServiceAsync(context, services.GetRequiredService<CatalogFallback>(), serviceName, cancellation)
                .ConfigureAwait(false) is not { } service)
        {
            return;
        }

        ServiceContexts contexts = services.GetRequiredService<ServiceContexts>();
        List<(PublishedLayer Layer, LayerDescription Described)> described =
            await ChangeTrackingFlags.DescribeAsync(service, contexts, cancellation).ConfigureAwait(false);

        if (ChangeTrackingFlags.NotHonoured(service, described) is { } notHonoured)
        {
            await RefuseAsync(context, 400, notHonoured).ConfigureAwait(false);
            return;
        }

        string capabilities = await Program.ServiceCapabilitiesAsync(context, service, contexts, cancellation).ConfigureAwait(false);

        if (!capabilities.Split(',', StringSplitOptions.TrimEntries).Contains("Query", StringComparer.Ordinal))
        {
            await RefuseAsync(context, 403, "Extracting changes reads the layers, and this service offers you no Query.").ConfigureAwait(false);
            return;
        }

        Func<string, string?> field = await FieldsAsync(context, cancellation).ConfigureAwait(false);
        int defaultSrid = service.Srid ?? service.LayersAndTables[0].PublishedSrid;

        if (!ExtractChangesRequest.TryRead(field, service, defaultSrid, out ExtractChangesRequest? read, out string? error))
        {
            await RefuseAsync(context, 400, error!).ConfigureAwait(false);
            return;
        }

        ExtractChangesRequest request = read!;
        RequestPrincipal? caller = context.Features.Get<RequestPrincipal>();

        if (request.Async && caller is not { Principal.IsAnonymous: false })
        {
            await RefuseAsync(context, 401,
                "async=true makes a job, and a job belongs to somebody: sign in, or send async=false for the changes themselves "
                + "(ADR-311 §5.7).").ConfigureAwait(false);
            return;
        }

        ReplicaJobs jobs = services.GetRequiredService<ReplicaJobs>();
        // D-401: "now" is no later than any transaction still writing, so the client's next window starts before it.
        long now = (await ClosedGenerationAsync(
                services.GetRequiredService<LayerConnections>(), request.Layers.Select(id => service.LayerOrTable(id)!), jobs.Now,
                cancellation).ConfigureAwait(false))
            .ToUnixTimeMilliseconds();
        List<(Planned Plan, ExtractWindow Window, long To, long Min)> planned = [];

        foreach (int id in request.Layers)
        {
            PublishedLayer layer = service.LayerOrTable(id)!;
            (IFeatureSource source, LayerDescription about) = await contexts.GetAsync(layer, cancellation).ConfigureAwait(false);
            ExtractWindow window = request.Windows[id];
            long min = about.ArchivedSince?.ToUnixTimeMilliseconds() ?? 0;

            // INFERRED (ADR-311 §5.12 #6): a window's end in the future is read as now, and answered as now.
            long to = Math.Min(window.To ?? now, now);

            // ADR-311 §5.5: the reference says a client below minServerGen cannot extract changes and must query in full.
            if (window.From < min)
            {
                await RefuseAsync(context, 400,
                    $"Layer {id}: serverGen {window.From} is before {min}, its minServerGen — the earliest moment its history "
                    + "answers for. What changed before then cannot be told. Query the layer in full instead, and extract its "
                    + "changes from its minServerGen or later (ADR-311 §5.5).").ConfigureAwait(false);
                return;
            }

            if (window.From > to)
            {
                await RefuseAsync(context, 400,
                    $"Layer {id}: serverGen {window.From} is after now ({now}). A generation is a moment this server has passed."
                    ).ConfigureAwait(false);
                return;
            }

            if (await LayerDocumentAsync(context, faces, serviceName, id).ConfigureAwait(false) is not { } document)
            {
                await RefuseAsync(context, 502, $"Layer {id}'s own document could not be read for the extract.").ConfigureAwait(false);
                return;
            }

            ReplicaLayerQuery query = request.Queries[id];
            CreateReplicaRequest scope = new("extract", [id], new Dictionary<int, ReplicaLayerQuery> { [id] = query },
                request.Area, request.Area?.Envelope, request.AreaSrid, null, request.Attachments, "none", "none", request.Async, "json");

            if (Filter.Of(query, scope, about, out string? why) is not { } filter)
            {
                await RefuseAsync(context, 400, $"layerQueries: layer {id}'s where clause does not read: {why}").ConfigureAwait(false);
                return;
            }

            planned.Add((new Planned(id, layer, source, about, document, filter) { OutWkid = request.OutSrid }, window, to, min));
        }

        if (await WktsAsync(context, planned.Select(p => p.Plan.Wkid).Distinct(), cancellation).ConfigureAwait(false) is not { } wkts)
        {
            return;
        }

        List<ExtractLayer> plan = [];

        foreach ((Planned p, ExtractWindow window, long to, long min) in planned)
        {
            OfflineLayer shape = OfflineReplicaShapes.Layer(
                service.Name, p.Id, p.Document, wkts[p.Wkid], _ => Empty<OfflineRow>(), _ => Empty<OfflineAttachment>());
            shape = shape with { Wkid = p.Wkid };

            if (shape.GlobalIdField is null)
            {
                await RefuseAsync(context, 400,
                    $"Layer {p.Id} has no GlobalID field, and extractChanges names a deleted feature by its GlobalID (ADR-311 §5.3)."
                    ).ConfigureAwait(false);
                return;
            }

            plan.Add(new ExtractLayer(p, shape, window.From, to, min));
        }

        if (request.FieldsToCompare is { } compare
            && compare.FirstOrDefault(f => plan.All(l => l.Shape.Fields.All(s => !s.Name.Equals(f, StringComparison.OrdinalIgnoreCase)))) is { } unknown)
        {
            await RefuseAsync(context, 400, $"fieldsToCompare: '{unknown}' is not a field of any layer asked for.").ConfigureAwait(false);
            return;
        }

        LayerConnections connections = services.GetRequiredService<LayerConnections>();
        string serviceUrl = TileFaces.Origin(context) + FeatureServerPath(context);

        if (!request.Async)
        {
            (List<ExtractedLayer>? layers, string? refused) =
                await ExtractAsync(connections, plan, request, cancellation).ConfigureAwait(false);

            if (refused is not null)
            {
                await RefuseAsync(context, 400, refused).ConfigureAwait(false);
                return;
            }

            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.CacheControl = "private, no-store";
            await using Utf8JsonWriter json = new(context.Response.Body);
            WriteExtract(json, request, plan, layers!, "esriTransportTypeEmbedded", serviceUrl);
            await json.FlushAsync(cancellation).ConfigureAwait(false);
            return;
        }

        if (jobs.Start(caller!.Principal.Id, service.Id, ExtractChangesRequest.ResponseType) is not { } job)
        {
            await BusyAsync(context).ConfigureAwait(false);
            return;
        }

        job.DownloadName = service.Name + "_changes.json";

        async Task WorkAsync(IServiceProvider scoped, CancellationToken stopping)
        {
            job.Status = "ExportChanges";
            job.Updated = jobs.Now;

            (List<ExtractedLayer>? layers, string? refused) = await ExtractAsync(connections, plan, request, stopping).ConfigureAwait(false);

            if (refused is not null)
            {
                job.Status = "Failed";
                job.Error = refused;
                job.Updated = jobs.Now;
                return;
            }

            string file = jobs.FileFor(job, ".json");
            job.File = file;

            await using (FileStream stream = new(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await using (Utf8JsonWriter json = new(stream))
            {
                WriteExtract(json, request, plan, layers!, "esriTransportTypeUrl", serviceUrl);
                await json.FlushAsync(stopping).ConfigureAwait(false);
            }

            job.Generations = [.. plan.Select(l => new ReplicaLayer(l.Plan.Id, l.To, "none", string.Empty, false))];
            job.Status = "Completed";
            job.Updated = jobs.Now;
        }

        await RunAsync(context, job, true, WorkAsync, cancellation).ConfigureAwait(false);
    }

    /// <summary>Reads each layer's changes; answers why not when an attachment window cannot be vouched for.</summary>
    private static async Task<(List<ExtractedLayer>? Layers, string? Refused)> ExtractAsync(
        LayerConnections connections, IReadOnlyList<ExtractLayer> plan, ExtractChangesRequest request, CancellationToken cancellation)
    {
        List<ExtractedLayer> layers = [];

        foreach (ExtractLayer layer in plan)
        {
            Planned p = layer.Plan;
            OfflineLayer shape = layer.Shape;
            DateTimeOffset from = DateTimeOffset.FromUnixTimeMilliseconds(layer.From);
            DateTimeOffset to = DateTimeOffset.FromUnixTimeMilliseconds(layer.To);

            (Dictionary<long, OfflineRow> then, Dictionary<long, OfflineRow> now) =
                await WindowAsync(connections.HistoryFor(p.Layer), p, shape, from, to, cancellation).ConfigureAwait(false);

            ReplicaChanges diff = OfflineReplicaShapes.Diff(then, now, OfflineReplicaShapes.IndexOf(shape, shape.GlobalIdField!));
            int objectIdAt = OfflineReplicaShapes.IndexOf(shape, shape.ObjectIdField);
            List<OfflineRow> deleted = [.. diff.Deletes.Select(d => then[d.ObjectId])];

            List<long>? fieldUpdates = null;
            if (request.FieldsToCompare is { } compare)
            {
                int[] watched = [.. shape.Fields
                    .Select((f, i) => (f, i))
                    .Where(x => compare.Contains(x.f.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(x => x.i)];

                fieldUpdates = [.. diff.Updates
                    .Select(row => ObjectIdOf(row, objectIdAt))
                    .Where(oid => then.TryGetValue(oid, out OfflineRow? before) && watched.Any(i => !SameValue(before.Values[i], now[oid].Values[i])))];
            }

            bool? geometryUpdates = null;
            if (request.HasGeometryUpdates && shape.GeometryType is not null)
            {
                geometryUpdates = diff.Updates.Any(row =>
                {
                    long oid = ObjectIdOf(row, objectIdAt);
                    return then.TryGetValue(oid, out OfflineRow? before) && GeometryText(before.Geometry, shape.Wkid) != GeometryText(row.Geometry, shape.Wkid);
                });
            }

            AttachmentDownload? attachments = null;
            if (request.Attachments && !request.IdsOnly && !request.ExtentOnly && shape.HasAttachments && p.Layer.KeepsAttachments)
            {
                attachments = await AttachmentChangesAsync(connections, p, shape, from, to, diff, new HashSet<Guid>(), cancellation)
                    .ConfigureAwait(false);

                // INFERRED (ADR-311 §5.12 #8): a synchronising device must be moved on (D-423); a client extracting changes
                // is told instead, and may ask without attachments.
                if (attachments.FullResend)
                {
                    return (null,
                        $"Layer {p.Id}'s record of attachment changes does not reach back to serverGen {layer.From} (it began later, "
                        + "or the attachment table was emptied since), so its attachment changes cannot be told. Send "
                        + "returnAttachments=false for the feature changes, or extract from a later generation.");
                }
            }

            layers.Add(new ExtractedLayer(layer, diff.Inserts, diff.Updates, diff.Deletes, deleted, fieldUpdates, geometryUpdates, attachments));
        }

        return (layers, null);
    }

    /// <summary>Writes an extract's answer — ADR-311 §5.6.</summary>
    private static void WriteExtract(
        Utf8JsonWriter json, ExtractChangesRequest request, IReadOnlyList<ExtractLayer> plan, IReadOnlyList<ExtractedLayer> layers,
        string transportType, string serviceUrl)
    {
        json.WriteStartObject();

        if (!request.IdsOnly && !request.ExtentOnly)
        {
            json.WriteString("transportType", transportType);
            json.WriteString("responseType", ExtractChangesRequest.ResponseType);
        }

        json.WriteStartArray("layerServerGens");
        foreach (ExtractLayer layer in plan)
        {
            json.WriteStartObject();
            json.WriteNumber("id", layer.Plan.Id);
            json.WriteNumber("minServerGen", layer.MinServerGen);
            json.WriteNumber("serverGen", layer.To);
            json.WriteEndObject();
        }

        json.WriteEndArray();

        if (request.ExtentOnly)
        {
            WriteExtent(json, request, layers);
            json.WriteEndObject();
            return;
        }

        json.WriteStartArray("edits");

        foreach (ExtractedLayer layer in layers)
        {
            OfflineLayer shape = layer.Layer.Shape;
            int objectIdAt = OfflineReplicaShapes.IndexOf(shape, shape.ObjectIdField);
            IReadOnlyList<OfflineRow> inserts = request.ReturnInserts ? layer.Inserts : [];
            IReadOnlyList<OfflineRow> updates = request.ReturnUpdates ? layer.Updates : [];
            IReadOnlyList<OfflineKey> deletes = request.ReturnDeletes ? layer.Deletes : [];

            json.WriteStartObject();
            json.WriteNumber("id", layer.Layer.Plan.Id);

            if (request.IdsOnly)
            {
                json.WriteStartObject("objectIds");
                WriteIds(json, "adds", inserts.Select(r => ObjectIdOf(r, objectIdAt)));
                WriteIds(json, "updates", updates.Select(r => ObjectIdOf(r, objectIdAt)));
                WriteIds(json, "deletes", deletes.Select(d => d.ObjectId));
                json.WriteEndObject();
            }
            else
            {
                json.WriteStartObject("features");
                json.WriteStartArray("adds");
                foreach (OfflineRow row in inserts)
                {
                    OfflineJsonReplica.WriteFeature(json, shape, row);
                }

                json.WriteEndArray();
                json.WriteStartArray("updates");
                foreach (OfflineRow row in updates)
                {
                    OfflineJsonReplica.WriteFeature(json, shape, row);
                }

                json.WriteEndArray();

                if (request.DeletedFeatures)
                {
                    json.WriteStartArray("deletes");
                    foreach (OfflineRow row in request.ReturnDeletes ? layer.DeletedAsTheyWere : [])
                    {
                        OfflineJsonReplica.WriteFeature(json, shape, row);
                    }

                    json.WriteEndArray();
                }
                else
                {
                    // The reference: "just the feature's identifier (e.g. globalid)".
                    json.WriteStartArray("deleteIds");
                    foreach (OfflineKey key in deletes)
                    {
                        json.WriteStringValue(OfflineJsonReplica.Braced(key.GlobalId));
                    }

                    json.WriteEndArray();
                }

                json.WriteEndObject();

                if (layer.Attachments is { } attached)
                {
                    json.WriteStartObject("attachments");
                    json.WriteStartArray("adds");
                    foreach (OfflineAttachment attachment in attached.Inserts)
                    {
                        OfflineJsonReplica.WriteAttachment(json, shape.Id, attachment, serviceUrl, request.AttachmentsByUrl);
                    }

                    json.WriteEndArray();
                    json.WriteStartArray("updates");
                    foreach (OfflineAttachment attachment in attached.Updates)
                    {
                        OfflineJsonReplica.WriteAttachment(json, shape.Id, attachment, serviceUrl, request.AttachmentsByUrl);
                    }

                    json.WriteEndArray();
                    json.WriteStartArray("deleteIds");
                    foreach (OfflineKey key in attached.Deletes)
                    {
                        json.WriteStringValue(OfflineJsonReplica.Braced(key.GlobalId));
                    }

                    json.WriteEndArray();
                    json.WriteEndObject();
                }
            }

            if (layer.FieldUpdates is { } fieldUpdates)
            {
                WriteIds(json, "fieldUpdates", fieldUpdates);
            }

            if (layer.GeometryUpdates is { } moved)
            {
                json.WriteBoolean("hasGeometryUpdates", moved);
            }

            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>
    /// The envelope of the changed features — added and updated as they are, deleted as they were — across the layers asked
    /// for, as <c>extent</c> (<c>INFERRED</c>, ADR-311 §5.12 #3); null when nothing with a shape changed.
    /// </summary>
    private static void WriteExtent(Utf8JsonWriter json, ExtractChangesRequest request, IReadOnlyList<ExtractedLayer> layers)
    {
        IEnumerable<Geometry?> shapes = layers.SelectMany(l =>
            (request.ReturnInserts ? l.Inserts.Select(r => r.Geometry) : [])
            .Concat(request.ReturnUpdates ? l.Updates.Select(r => r.Geometry) : [])
            .Concat(request.ReturnDeletes ? l.DeletedAsTheyWere.Select(r => r.Geometry) : []));

        Envelope? extent = Union(shapes.Select(g => g is { IsEmpty: false } some ? some.Envelope : (Envelope?)null));
        int wkid = layers.FirstOrDefault(l => l.Layer.Shape.GeometryType is not null)?.Layer.Shape.Wkid ?? request.OutSrid ?? 4326;

        if (extent is not { } box)
        {
            json.WriteNull("extent");
            return;
        }

        json.WriteStartObject("extent");
        json.WriteNumber("xmin", box.MinX);
        json.WriteNumber("ymin", box.MinY);
        json.WriteNumber("xmax", box.MaxX);
        json.WriteNumber("ymax", box.MaxY);
        json.WriteStartObject("spatialReference");
        json.WriteNumber("wkid", wkid);
        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static void WriteIds(Utf8JsonWriter json, string name, IEnumerable<long> ids)
    {
        json.WriteStartArray(name);
        foreach (long id in ids)
        {
            json.WriteNumberValue(id);
        }

        json.WriteEndArray();
    }

    private static long ObjectIdOf(OfflineRow row, int objectIdAt) =>
        Convert.ToInt64(row.Values[objectIdAt], System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether two values of one field are the same, bytes compared by content.</summary>
    internal static bool SameValue(object? a, object? b) =>
        a is byte[] x && b is byte[] y ? x.AsSpan().SequenceEqual(y) : Equals(a, b);

    /// <summary>A shape as ArcGIS JSON text, to tell whether an update moved it; empty for none.</summary>
    internal static string GeometryText(Geometry? geometry, int wkid)
    {
        if (geometry is not { IsEmpty: false })
        {
            return string.Empty;
        }

        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            ArcGisGeometryWriter.Write(json, geometry, wkid);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
