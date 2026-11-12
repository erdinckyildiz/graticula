using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Api.ArcGis.Offline;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>
/// ArcGIS's JSON replica — <c>createReplica</c> with <c>dataFormat=json</c>, and the JSON edits of
/// <c>synchronizeReplica</c> — ADR-288 §11a (the owner's change of 2026-11-12), the shape measured on ArcGIS Server 12.1
/// (research note §5c).
/// </summary>
/// <remarks>
/// <para>
/// <b>Rows are the ones the mobile geodatabase gets.</b> The same <see cref="OfflineReplicaEndpoints.RowsAsync"/> reads
/// them through the layer's feature source, with the replica's filter, in the layer's published reference, with Z and M
/// kept and the history moment of a registered replica, so a JSON copy and a SQLite copy of one request hold the same
/// rows. Dates are epoch milliseconds, GlobalIDs braced and upper case, as in a query answer.
/// </para>
/// <para>
/// <b>An attachment's <c>url</c> is the layer's own attachment route</b> —
/// <c>&lt;FeatureServer&gt;/&lt;layer&gt;/&lt;object id&gt;/attachments/&lt;attachment id&gt;</c> — which serves the bytes to
/// anyone who may read the layer, so the replica's owner fetches them with the token they already hold. ArcGIS writes a
/// file into its job directory instead; a route that exists already needs no second copy of the bytes and no second
/// access check (<c>INFERRED</c>, recorded in ADR-288 §11a).
/// </para>
/// <para>
/// <b>The file is streamed.</b> Features are written as they are read; only the (object id, GlobalID) pairs of a layer are
/// held, to list its attachments afterwards.
/// </para>
/// </remarks>
internal static class OfflineJsonReplica
{
    /// <summary>The media type of a JSON replica's file.</summary>
    public const string MediaType = "application/json";

    /// <summary>Writes a replica's layers, features and attachment descriptions to <paramref name="file"/>.</summary>
    /// <param name="attachments">A layer's attachment store.</param>
    /// <param name="plan">The plan.</param>
    /// <param name="replicaId">The replica's id when it is registered; null for a snapshot.</param>
    /// <param name="generation">The moment the copy is of.</param>
    /// <param name="generations">The layers' server generations.</param>
    /// <param name="featureServerUrl">The FeatureServer address, which attachment URLs hang from.</param>
    /// <param name="file">Where to write.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteAsync(
        Func<PublishedLayer, IAttachmentStore> attachments,
        PackagePlan plan,
        Guid? replicaId,
        DateTimeOffset generation,
        IReadOnlyList<ReplicaLayer> generations,
        string featureServerUrl,
        string file,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(generations);

        CreateReplicaRequest request = plan.Request;
        DateTimeOffset? moment = request.Registered ? generation : null;

        try
        {
            await using FileStream stream = new(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using Utf8JsonWriter json = new(stream);

            json.WriteStartObject();
            json.WriteString("replicaName", request.Name);

            if (replicaId is { } id)
            {
                json.WriteString("replicaID", OfflineReplicaRequests.Wire(id));
            }

            json.WriteString("targetType", "client");
            json.WriteString("transportType", "esriTransportTypeUrl");
            json.WriteString("responseType", "esriReplicaResponseTypeData");
            json.WriteString("syncModel", request.SyncModel);
            WriteGenerations(json, generations);

            json.WriteStartArray("layers");

            foreach (OfflineReplicaEndpoints.Planned p in plan.Layers)
            {
                OfflineLayer shape = Shape(plan, p);
                List<(long ObjectId, Guid GlobalId)> owners = [];
                int objectIdAt = OfflineReplicaShapes.IndexOf(shape, shape.ObjectIdField);
                int globalAt = shape.GlobalIdField is { } g ? OfflineReplicaShapes.IndexOf(shape, g) : -1;

                json.WriteStartObject();
                json.WriteNumber("id", p.Id);
                json.WriteStartArray("features");

                await foreach (OfflineRow row in OfflineReplicaEndpoints.RowsAsync(p, shape.Fields, moment, null, cancellation).ConfigureAwait(false))
                {
                    WriteFeature(json, shape, row);

                    if (globalAt >= 0 && objectIdAt >= 0 && row.Values[objectIdAt] is { } oid && row.Values[globalAt] is Guid gid)
                    {
                        owners.Add((Convert.ToInt64(oid, System.Globalization.CultureInfo.InvariantCulture), gid));
                    }
                }

                json.WriteEndArray();
                json.WriteStartArray("attachments");

                if (request.ReturnAttachments && shape.HasAttachments && p.Layer.KeepsAttachments && p.Filter.Option != "none")
                {
                    IAttachmentStore store = attachments(p.Layer);

                    foreach ((long objectId, Guid parent) in owners)
                    {
                        foreach (AttachmentInfo info in await store.ListAsync(objectId, cancellation).ConfigureAwait(false))
                        {
                            json.WriteStartObject();
                            json.WriteNumber("attachmentId", info.Id);
                            json.WriteString("globalId", Braced(OfflineReplicaShapes.AttachmentGlobalId(p.Layer.Id, info.Id)));
                            json.WriteString("parentGlobalId", Braced(parent));
                            json.WriteString("contentType", info.ContentType);
                            json.WriteNumber("size", info.Size);
                            json.WriteString("name", info.Name);
                            json.WriteString("url", AttachmentUrl(featureServerUrl, p.Id, objectId, info.Id));
                            json.WriteEndObject();
                        }
                    }
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            await json.FlushAsync(cancellation).ConfigureAwait(false);
        }
        catch
        {
            ReplicaJobs.TryDelete(file);
            throw;
        }
    }

    /// <summary>The address a layer's attachment is fetched from.</summary>
    /// <param name="featureServerUrl">The FeatureServer address.</param>
    /// <param name="layerId">The layer.</param>
    /// <param name="objectId">The feature.</param>
    /// <param name="attachmentId">The attachment.</param>
    /// <returns>The URL.</returns>
    public static string AttachmentUrl(string featureServerUrl, int layerId, long objectId, int attachmentId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{featureServerUrl.TrimEnd('/')}/{layerId}/{objectId}/attachments/{attachmentId}");

    /// <summary>Writes the download of a <c>synchronizeReplica</c> as ArcGIS's JSON edits.</summary>
    /// <param name="file">Where to write.</param>
    /// <param name="replica">The replica.</param>
    /// <param name="changes">Each layer's changes, with the shape its rows are written by.</param>
    /// <param name="generations">The layers' generations after this sync.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>A task.</returns>
    public static async Task WriteChangesAsync(
        string file,
        ReplicaRecord replica,
        IReadOnlyList<OfflineLayerChanges> changes,
        IReadOnlyList<ReplicaLayer> generations,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(replica);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(generations);

        try
        {
            await using FileStream stream = new(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            await using Utf8JsonWriter json = new(stream);

            json.WriteStartObject();
            json.WriteString("replicaName", replica.Name);
            json.WriteString("replicaID", OfflineReplicaRequests.Wire(replica.Id));
            json.WriteString("targetType", "client");
            json.WriteString("transportType", "esriTransportTypeUrl");
            json.WriteString("responseType", "esriReplicaResponseTypeEdits");
            json.WriteString("syncModel", "perLayer");
            WriteGenerations(json, generations);

            json.WriteStartArray("edits");

            foreach (OfflineLayerChanges layer in changes)
            {
                json.WriteStartObject();
                json.WriteNumber("id", layer.Layer.Id);
                json.WriteStartObject("features");

                json.WriteStartArray("adds");
                foreach (OfflineRow row in layer.Inserts)
                {
                    WriteFeature(json, layer.Layer, row);
                }

                json.WriteEndArray();
                json.WriteStartArray("updates");
                foreach (OfflineRow row in layer.Updates)
                {
                    WriteFeature(json, layer.Layer, row);
                }

                json.WriteEndArray();

                // INFERRED: the object ids the client holds, which is what ArcGIS's `deleteIds` names (D-409 records it).
                json.WriteStartArray("deleteIds");
                foreach (OfflineKey key in layer.Deletes)
                {
                    json.WriteNumberValue(key.ObjectId);
                }

                json.WriteEndArray();
                json.WriteEndObject();

                // Attachments the server changed do not travel in a download here, in either format (D-409).
                json.WriteStartObject("attachments");
                json.WriteStartArray("adds");
                json.WriteEndArray();
                json.WriteStartArray("updates");
                json.WriteEndArray();
                json.WriteStartArray("deleteIds");
                json.WriteEndArray();
                json.WriteEndObject();

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
            await json.FlushAsync(cancellation).ConfigureAwait(false);
        }
        catch
        {
            ReplicaJobs.TryDelete(file);
            throw;
        }
    }

    /// <summary>A planned layer as the port describes it, without rows.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="p">The layer.</param>
    /// <returns>The shape.</returns>
    internal static OfflineLayer Shape(PackagePlan plan, OfflineReplicaEndpoints.Planned p) =>
        OfflineReplicaShapes.Layer(
            plan.Service.Name, p.Id, p.Document, plan.Wkts[p.Wkid],
            _ => OfflineReplicaEndpoints.Empty<OfflineRow>(), _ => OfflineReplicaEndpoints.Empty<OfflineAttachment>());

    private static void WriteGenerations(Utf8JsonWriter json, IReadOnlyList<ReplicaLayer> generations)
    {
        json.WriteStartArray("layerServerGens");

        foreach (ReplicaLayer layer in generations)
        {
            json.WriteStartObject();
            json.WriteNumber("id", layer.Id);
            json.WriteNumber("serverGen", layer.ServerGeneration);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void WriteFeature(Utf8JsonWriter json, OfflineLayer layer, OfflineRow row)
    {
        json.WriteStartObject();

        if (row.Geometry is { IsEmpty: false } geometry && layer.GeometryType is not null)
        {
            json.WritePropertyName("geometry");
            ArcGisGeometryWriter.Write(json, geometry, layer.Wkid);
        }

        json.WriteStartObject("attributes");

        for (int i = 0; i < layer.Fields.Count; i++)
        {
            json.WritePropertyName(layer.Fields[i].Name);
            WriteValue(json, row.Values[i]);
        }

        json.WriteEndObject();
        json.WriteEndObject();
    }

    private static string Braced(Guid id) => "{" + id.ToString("D").ToUpperInvariant() + "}";

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null:
            case byte[]:
                json.WriteNullValue();
                break;
            case string text:
                json.WriteStringValue(text);
                break;
            case Guid id:
                json.WriteStringValue(Braced(id));
                break;
            case DateTimeOffset moment:
                json.WriteNumberValue(moment.ToUnixTimeMilliseconds());
                break;
            case DateTime time:
                json.WriteNumberValue(new DateTimeOffset(DateTime.SpecifyKind(time, DateTimeKind.Utc)).ToUnixTimeMilliseconds());
                break;
            case bool flag:
                json.WriteNumberValue(flag ? 1 : 0);
                break;
            case long whole:
                json.WriteNumberValue(whole);
                break;
            case int whole:
                json.WriteNumberValue(whole);
                break;
            case short whole:
                json.WriteNumberValue(whole);
                break;
            case double real:
                if (double.IsFinite(real)) json.WriteNumberValue(real); else json.WriteNullValue();
                break;
            case float real:
                if (float.IsFinite(real)) json.WriteNumberValue(real); else json.WriteNullValue();
                break;
            case decimal real:
                json.WriteNumberValue(real);
                break;
            default:
                json.WriteStringValue(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
        }
    }
}
