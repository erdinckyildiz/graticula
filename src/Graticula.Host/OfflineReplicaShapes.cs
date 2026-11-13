using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using Graticula.Api.ArcGis;
using Graticula.Api.ArcGis.Offline;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>A layer's changes since a generation, before they are written — what <see cref="OfflineReplicaShapes.Diff"/> finds.</summary>
/// <param name="Inserts">Rows absent then and present now.</param>
/// <param name="Updates">Rows present then and now.</param>
/// <param name="Deletes">Rows present then and absent now, by the identity they had then.</param>
internal sealed record ReplicaChanges(IReadOnlyList<OfflineRow> Inserts, IReadOnlyList<OfflineRow> Updates, IReadOnlyList<OfflineKey> Deletes);

/// <summary>One layer's edits as the service's <c>applyEdits</c> takes them, and what was left out and why.</summary>
/// <param name="LayerId">The layer.</param>
/// <param name="Adds">The <c>adds</c> array, or null.</param>
/// <param name="Updates">The <c>updates</c> array, or null.</param>
/// <param name="Deletes">The <c>deletes</c> array of GlobalIDs, or null.</param>
/// <param name="Dropped">Updates and deletes of rows the server no longer has, which the server's delete wins over.</param>
internal sealed record ReplicaLayerEdits(int LayerId, string? Adds, string? Updates, string? Deletes, int Dropped);

/// <summary>
/// The pure halves of ADR-288's replica operations: the documents ArcGIS answers with, the reading of a layer document
/// into the port's <see cref="OfflineLayer"/>, the difference between two moments of a layer, and an uploaded delta
/// turned into the edits <c>applyEdits</c> takes.
/// </summary>
/// <remarks>
/// <b>Kept apart from the routes so each can be tested without a server</b>, and so the routes are only the order in
/// which these are called. The response shapes are the ones ArcGIS Server 12.1 answered on 2026-11-10 (captured outside
/// the repository, ADR-288 §4) and Esri's public <i>Create Replica</i>, <i>Synchronize Replica</i> and <i>Replica</i>
/// references.
/// </remarks>
internal static class OfflineReplicaShapes
{
    /// <summary>The ArcGIS field types that carry no editable value of the row's own.</summary>
    private static readonly HashSet<string> NotWritten = new(StringComparer.Ordinal)
    {
        "esriFieldTypeOID", "esriFieldTypeGeometry", "esriFieldTypeRaster", "esriFieldTypeBlob",
    };

    /// <summary>The status document of a replica job, and the answer of a synchronous one — <c>createReplica</c> or <c>synchronizeReplica</c>.</summary>
    /// <param name="job">The job.</param>
    /// <param name="resultUrl">Where the file is, or null while there is none.</param>
    /// <returns>The document.</returns>
    public static JsonObject Status(ReplicaJob job, string? resultUrl)
    {
        ArgumentNullException.ThrowIfNull(job);

        JsonObject document = new()
        {
            ["status"] = job.Status,
            ["responseType"] = job.ResponseType,
            ["transportType"] = "esriTransportTypeUrl",
            ["targetType"] = "client",
            ["replicaName"] = job.ReplicaName,
            ["replicaID"] = job.ReplicaId is { } id ? OfflineReplicaRequests.Wire(id) : null,
            ["submissionTime"] = job.Submitted.ToUnixTimeMilliseconds(),
            ["lastUpdatedTime"] = job.Updated.ToUnixTimeMilliseconds(),
        };

        if (job.Generations is { Count: > 0 } generations)
        {
            document["layerServerGens"] = new JsonArray([.. generations.Select(g => (JsonNode)new JsonObject
            {
                ["id"] = g.Id,
                ["serverGen"] = g.ServerGeneration,
            })]);
        }

        if (resultUrl is not null)
        {
            document["resultUrl"] = resultUrl;
        }

        if (job.Error is { } error)
        {
            document["error"] = new JsonObject { ["code"] = 500, ["message"] = error };
        }

        return document;
    }

    /// <summary>A registered replica as <c>FeatureServer/replicas/{id}</c> answers it.</summary>
    /// <param name="replica">The replica.</param>
    /// <returns>The document.</returns>
    public static JsonObject Replica(ReplicaRecord replica)
    {
        ArgumentNullException.ThrowIfNull(replica);

        JsonObject document = new()
        {
            ["replicaName"] = replica.Name,
            ["replicaID"] = OfflineReplicaRequests.Wire(replica.Id),
            ["replicaOwner"] = replica.OwnerName,
            ["targetType"] = "client",
            ["syncModel"] = replica.SyncModel,
            ["syncDirection"] = "bidirectional",
            ["returnAttachments"] = replica.ReturnAttachments,
            ["returnsAttachments"] = replica.ReturnAttachments,
            ["attachmentsSyncDirection"] = replica.AttachmentsSyncDirection,
            ["supportsReplicaTracking"] = true,
            ["spatialRel"] = "esriSpatialRelIntersects",
            ["creationDate"] = replica.Created.ToUnixTimeMilliseconds(),
            ["lastSyncDate"] = replica.LastSynced.ToUnixTimeMilliseconds(),
            ["layers"] = new JsonArray([.. replica.Layers.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id,
                ["queryOption"] = l.QueryOption,
                ["where"] = l.Where,
                ["useGeometry"] = l.UseGeometry,
                ["includeRelated"] = true,
            })]),
            ["layerServerGens"] = new JsonArray([.. replica.Layers.Select(l => (JsonNode)new JsonObject
            {
                ["id"] = l.Id,
                ["serverGen"] = l.ServerGeneration,
            })]),
        };

        if (replica.Wkid is { } wkid)
        {
            document["spatialReference"] = new JsonObject { ["wkid"] = wkid, ["latestWkid"] = wkid };
        }

        if (replica.Geometry is { } geometry)
        {
            try
            {
                document["geometry"] = JsonNode.Parse(geometry);
            }
            catch (JsonException)
            {
                // A stored area that is not JSON is the comma form; the document gives it back as it was sent.
                document["geometry"] = geometry;
            }
        }

        return document;
    }

    /// <summary>
    /// A layer's document read into the port's layer — the fields, identities, reference and extent the file is
    /// written with, which are exactly what <c>FeatureServer/{id}</c> said.
    /// </summary>
    /// <param name="serviceName">The service's name, which prefixes each domain's, as the file names domains.</param>
    /// <param name="id">The layer's id.</param>
    /// <param name="document">Its REST document, as written.</param>
    /// <param name="referenceWkt">Its reference's WKT.</param>
    /// <param name="rows">Its rows, read when the writer reaches it.</param>
    /// <param name="attachments">Its attachments, read when the writer reaches it.</param>
    /// <returns>The layer.</returns>
    public static OfflineLayer Layer(
        string serviceName,
        int id,
        string document,
        string referenceWkt,
        Func<CancellationToken, IAsyncEnumerable<OfflineRow>> rows,
        Func<CancellationToken, IAsyncEnumerable<OfflineAttachment>> attachments)
    {
        ArgumentNullException.ThrowIfNull(document);

        JsonElement root = JsonDocument.Parse(document).RootElement;
        string? Text(string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool Flag(string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        string? geometryType = Text("type") == "Table" ? null : Text("geometryType");
        GeometryOrdinates ordinates = (Flag("hasZ") ? GeometryOrdinates.Z : GeometryOrdinates.None)
            | (Flag("hasM") ? GeometryOrdinates.M : GeometryOrdinates.None);

        int wkid = WkidOf(root) ?? 4326;
        List<OfflineField> fields = [];

        if (root.TryGetProperty("fields", out JsonElement written) && written.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement field in written.EnumerateArray())
            {
                string type = field.GetProperty("type").GetString() ?? "esriFieldTypeString";

                if (type == "esriFieldTypeGeometry")
                {
                    continue;
                }

                fields.Add(new OfflineField(
                    field.GetProperty("name").GetString()!,
                    type,
                    field.TryGetProperty("alias", out JsonElement alias) && alias.ValueKind == JsonValueKind.String ? alias.GetString() : null,
                    field.TryGetProperty("length", out JsonElement length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out int l) ? l : null,
                    !field.TryGetProperty("nullable", out JsonElement nullable) || nullable.ValueKind != JsonValueKind.False,
                    DomainOf(serviceName, field)));
            }
        }

        Envelope? extent = null;

        if (root.TryGetProperty("extent", out JsonElement box) && box.ValueKind == JsonValueKind.Object
            && box.TryGetProperty("xmin", out JsonElement xmin) && xmin.ValueKind == JsonValueKind.Number)
        {
            extent = new Envelope(xmin.GetDouble(), box.GetProperty("ymin").GetDouble(),
                box.GetProperty("xmax").GetDouble(), box.GetProperty("ymax").GetDouble());
        }

        return new OfflineLayer(
            id,
            Text("name") ?? $"layer{id.ToString(CultureInfo.InvariantCulture)}",
            document,
            geometryType,
            ordinates,
            wkid,
            AxisOrder.IsGeographic(wkid),
            referenceWkt,
            fields,
            Text("objectIdField") ?? "objectid",
            Text("globalIdField") is { Length: > 0 } global ? global : null,
            Flag("hasAttachments"),
            extent,
            rows,
            attachments);
    }

    /// <summary>The names of a layer document's fields a client may write, from their <c>editable</c> flag.</summary>
    /// <param name="document">The layer's REST document.</param>
    /// <returns>The names.</returns>
    public static HashSet<string> EditableFields(string document)
    {
        HashSet<string> editable = new(StringComparer.OrdinalIgnoreCase);
        JsonElement root = JsonDocument.Parse(document).RootElement;

        if (root.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement field in fields.EnumerateArray())
            {
                string type = field.GetProperty("type").GetString() ?? string.Empty;
                bool writable = !field.TryGetProperty("editable", out JsonElement flag) || flag.ValueKind != JsonValueKind.False;

                if (writable && !NotWritten.Contains(type))
                {
                    editable.Add(field.GetProperty("name").GetString()!);
                }
            }
        }

        return editable;
    }

    /// <summary>The reference a document states, its latest id first.</summary>
    internal static int? WkidOf(JsonElement root)
    {
        foreach (string holder in (string[])["spatialReference", "extent"])
        {
            if (!root.TryGetProperty(holder, out JsonElement element) || element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            JsonElement reference = holder == "extent" && element.TryGetProperty("spatialReference", out JsonElement inner) ? inner : element;

            foreach (string name in (string[])["latestWkid", "wkid"])
            {
                if (reference.ValueKind == JsonValueKind.Object && reference.TryGetProperty(name, out JsonElement value)
                    && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int wkid))
                {
                    return ArcGisGeometryReader.Canonical(wkid);
                }
            }
        }

        return null;
    }

    private static OfflineDomain? DomainOf(string serviceName, JsonElement field)
    {
        if (!field.TryGetProperty("domain", out JsonElement domain) || domain.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Written as given by the writer, so prefixed here as ArcGIS's files name a service's domains: {service}_{domain}.
        string name = serviceName + "_" + (domain.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
            ? n.GetString()!
            : field.GetProperty("name").GetString() + "_domain");
        string type = domain.TryGetProperty("type", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;

        if (type == "codedValue" && domain.TryGetProperty("codedValues", out JsonElement codes))
        {
            List<KeyValuePair<object, string>> values = [];

            foreach (JsonElement code in codes.EnumerateArray())
            {
                JsonElement value = code.GetProperty("code");
                object key = value.ValueKind switch
                {
                    JsonValueKind.Number when value.TryGetInt64(out long whole) => whole,
                    JsonValueKind.Number => value.GetDouble(),
                    _ => value.GetString() ?? string.Empty,
                };
                values.Add(new KeyValuePair<object, string>(key, code.GetProperty("name").GetString() ?? string.Empty));
            }

            return new OfflineCodedDomain(name, values);
        }

        if (type == "range" && domain.TryGetProperty("range", out JsonElement range) && range.GetArrayLength() == 2)
        {
            return new OfflineRangeDomain(name, range[0].GetDouble(), range[1].GetDouble());
        }

        return null;
    }

    /// <summary>
    /// A value as the port carries it for a field of this ArcGIS type — <see cref="long"/>, <see cref="int"/>,
    /// <see cref="short"/>, <see cref="double"/>, <see cref="string"/>, UTC <see cref="DateTimeOffset"/>,
    /// <see cref="Guid"/>, a byte array, or null.
    /// </summary>
    /// <param name="value">The value as the feature source read it.</param>
    /// <param name="esriType">The field's ArcGIS type.</param>
    /// <returns>The value for the file.</returns>
    public static object? Normalise(object? value, string esriType)
    {
        if (value is null or DBNull)
        {
            return null;
        }

        IFormatProvider invariant = CultureInfo.InvariantCulture;

        return esriType switch
        {
            "esriFieldTypeOID" or "esriFieldTypeBigInteger" => Convert.ToInt64(Plain(value), invariant),
            "esriFieldTypeInteger" => Convert.ToInt64(Plain(value), invariant) is var whole && whole is >= int.MinValue and <= int.MaxValue
                ? (object)(int)whole
                : whole,
            "esriFieldTypeSmallInteger" => Convert.ToInt16(Plain(value), invariant),
            "esriFieldTypeDouble" or "esriFieldTypeSingle" => Convert.ToDouble(Plain(value), invariant),
            "esriFieldTypeDate" or "esriFieldTypeTimestampOffset" => Moment(value),
            "esriFieldTypeDateOnly" => value is DateOnly day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : Moment(value),
            "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => value is Guid guid ? guid : Guid.TryParse(value.ToString()?.Trim('{', '}'), out Guid parsed) ? parsed : null,
            "esriFieldTypeBlob" => value as byte[],
            _ => value switch
            {
                string text => text,
                IFormattable formattable => formattable.ToString(null, invariant),
                _ => value.ToString(),
            },
        };
    }

    private static object Plain(object value) => value is bool flag ? (flag ? 1 : 0) : value;

    private static DateTimeOffset? Moment(object value) => value switch
    {
        DateTimeOffset offset => offset.ToUniversalTime(),
        DateTime time => new DateTimeOffset(DateTime.SpecifyKind(time, time.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : time.Kind)).ToUniversalTime(),
        DateOnly day => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        long epoch => DateTimeOffset.FromUnixTimeMilliseconds(epoch),
        _ => DateTimeOffset.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
            ? parsed.ToUniversalTime()
            : null,
    };

    /// <summary>
    /// What changed between two moments of the same rows — ADR-288 §5.4's download: absent then and present now is an
    /// insert, present both is an update, present then and absent now is a delete, named by the GlobalID it had then.
    /// </summary>
    /// <param name="then">The rows at the replica's generation, by object id.</param>
    /// <param name="now">The rows at the new generation, by object id.</param>
    /// <param name="globalIdIndex">Where the GlobalID is among a row's values.</param>
    /// <returns>The changes, each list in object id order.</returns>
    public static ReplicaChanges Diff(
        IReadOnlyDictionary<long, OfflineRow> then, IReadOnlyDictionary<long, OfflineRow> now, int globalIdIndex)
    {
        ArgumentNullException.ThrowIfNull(then);
        ArgumentNullException.ThrowIfNull(now);

        List<OfflineRow> inserts = [];
        List<OfflineRow> updates = [];
        List<OfflineKey> deletes = [];

        foreach (long id in then.Keys.Union(now.Keys).Order())
        {
            bool was = then.TryGetValue(id, out OfflineRow? before);
            bool @is = now.TryGetValue(id, out OfflineRow? after);

            if (@is && !was)
            {
                inserts.Add(after!);
            }
            else if (@is && was)
            {
                updates.Add(after!);
            }
            else if (was && before!.Values[globalIdIndex] is Guid global)
            {
                deletes.Add(new OfflineKey(id, global));
            }
        }

        return new ReplicaChanges(inserts, updates, deletes);
    }

    /// <summary>
    /// A download's changes without the rows the same sync's upload named — the client already holds them as it sent
    /// them, and an insert sent back would be a second row with the same GlobalID.
    /// </summary>
    /// <param name="changes">The download's changes.</param>
    /// <param name="globalIdIndex">Where the GlobalID is among a row's values.</param>
    /// <param name="own">The GlobalIDs the upload named.</param>
    /// <returns>The changes to send.</returns>
    public static ReplicaChanges WithoutOwn(ReplicaChanges changes, int globalIdIndex, IReadOnlySet<Guid> own)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(own);

        if (own.Count == 0)
        {
            return changes;
        }

        bool Foreign(OfflineRow row) => GlobalIdOf(row, globalIdIndex) is not { } id || !own.Contains(id);

        return new ReplicaChanges(
            [.. changes.Inserts.Where(Foreign)],
            [.. changes.Updates.Where(Foreign)],
            [.. changes.Deletes.Where(d => !own.Contains(d.GlobalId))]);
    }

    /// <summary>
    /// An uploaded layer's changes as the service's <c>applyEdits</c> takes them with <c>useGlobalIds=true</c> —
    /// ADR-288 §5.5 (<c>INFERRED</c>): the last edit to reach the server wins, and a row the server deleted stays deleted.
    /// </summary>
    /// <param name="changes">The layer's uploaded changes.</param>
    /// <param name="editable">The fields a client may write (<see cref="EditableFields"/>).</param>
    /// <param name="existing">The GlobalIDs the server holds now, of every row the changes name.</param>
    /// <returns>The edits.</returns>
    /// <remarks>
    /// An insert of a GlobalID the server already holds is an update — the same upload sent twice, after an answer that
    /// never arrived, must not add the row twice. An update or delete of one the server no longer holds is dropped and
    /// counted: the server's delete reached it first.
    /// </remarks>
    public static ReplicaLayerEdits Edits(OfflineLayerChanges changes, IReadOnlySet<string> editable, IReadOnlySet<Guid> existing)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(editable);
        ArgumentNullException.ThrowIfNull(existing);

        OfflineLayer layer = changes.Layer;
        string? globalIdField = layer.GlobalIdField
            ?? throw new InvalidOperationException($"Layer {layer.Id} has no GlobalID, so its offline edits cannot be named.");
        int globalAt = IndexOf(layer, globalIdField);

        List<OfflineRow> adds = [];
        List<OfflineRow> updates = [];
        List<Guid> deletes = [];
        int dropped = 0;

        foreach (OfflineRow row in changes.Inserts)
        {
            (GlobalIdOf(row, globalAt) is { } id && existing.Contains(id) ? updates : adds).Add(row);
        }

        foreach (OfflineRow row in changes.Updates)
        {
            if (GlobalIdOf(row, globalAt) is { } id && existing.Contains(id))
            {
                updates.Add(row);
            }
            else
            {
                dropped++;
            }
        }

        foreach (OfflineKey key in changes.Deletes)
        {
            if (existing.Contains(key.GlobalId))
            {
                deletes.Add(key.GlobalId);
            }
            else
            {
                dropped++;
            }
        }

        return new ReplicaLayerEdits(
            layer.Id,
            adds.Count == 0 ? null : Features(layer, adds, editable, globalIdField),
            updates.Count == 0 ? null : Features(layer, updates, editable, globalIdField),
            deletes.Count == 0 ? null : JsonSerializer.Serialize(deletes.Select(Braced)),
            dropped);
    }

    /// <summary>Every GlobalID an uploaded layer's changes name.</summary>
    /// <param name="changes">The changes.</param>
    /// <returns>The GlobalIDs.</returns>
    public static HashSet<Guid> GlobalIdsOf(OfflineLayerChanges changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        HashSet<Guid> ids = [.. changes.Deletes.Select(d => d.GlobalId)];

        if (changes.Layer.GlobalIdField is { } field)
        {
            int at = IndexOf(changes.Layer, field);

            foreach (OfflineRow row in changes.Inserts.Concat(changes.Updates))
            {
                if (GlobalIdOf(row, at) is { } id)
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>Where a field is among a layer's row values.</summary>
    internal static int IndexOf(OfflineLayer layer, string field)
    {
        for (int i = 0; i < layer.Fields.Count; i++)
        {
            if (string.Equals(layer.Fields[i].Name, field, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new InvalidOperationException($"Layer {layer.Id} has no field '{field}'.");
    }

    /// <summary>
    /// The GlobalID an attachment is given in a replica: this server keeps none for an attachment, so one is derived from
    /// the layer and the attachment's id, the same every time it is asked (ADR-288 §5.4, <c>INFERRED</c>).
    /// </summary>
    /// <param name="layerId">The layer's catalogue id.</param>
    /// <param name="attachmentId">The attachment.</param>
    /// <returns>The GlobalID.</returns>
    public static Guid AttachmentGlobalId(Guid layerId, long attachmentId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"graticula-attachment|{layerId:D}|{attachmentId}")));
        Span<byte> bytes = hash.AsSpan(0, 16);

        // A name-based UUID's version and variant bits, so it reads as what it is.
        bytes[7] = (byte)((bytes[7] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    /// <summary>
    /// The GlobalID a replica calls an attachment by: the one it was stored with, or — for one stored without — the one
    /// derived from its id (ADR-290).
    /// </summary>
    /// <param name="layerId">The layer's catalogue id.</param>
    /// <param name="attachmentId">The attachment.</param>
    /// <param name="kept">The GlobalID it was stored with, or null.</param>
    /// <returns>The GlobalID.</returns>
    public static Guid AttachmentGlobalId(Guid layerId, long attachmentId, Guid? kept) =>
        kept ?? AttachmentGlobalId(layerId, attachmentId);

    /// <summary>
    /// The attachment a device's update or delete names, or null when none is its own — ADR-290.
    /// </summary>
    /// <param name="layerId">The layer's catalogue id.</param>
    /// <param name="attachmentId">The id the device sent.</param>
    /// <param name="globalId">The GlobalID the device sent.</param>
    /// <param name="byKept">The attachments stored under the GlobalIDs the device named, by GlobalID.</param>
    /// <param name="existing">The attachments stored under the ids the device named: each id, and the GlobalID it was stored with.</param>
    /// <returns>The attachment's id on this server, or null.</returns>
    /// <remarks>
    /// <b>A kept GlobalID names the attachment, whatever id the device holds</b> — an attachment the device made has an id
    /// of the device's own and another here. <b>One stored without a GlobalID is named only by its derived one together with
    /// its own id</b>, so a device cannot reach an attachment by counting.
    /// </remarks>
    public static int? ResolveAttachment(
        Guid layerId, long attachmentId, Guid globalId, IReadOnlyDictionary<Guid, int> byKept, IReadOnlyDictionary<int, Guid?> existing)
    {
        ArgumentNullException.ThrowIfNull(byKept);
        ArgumentNullException.ThrowIfNull(existing);

        if (byKept.TryGetValue(globalId, out int id))
        {
            return id;
        }

        return attachmentId is >= 1 and <= int.MaxValue
            && existing.TryGetValue((int)attachmentId, out Guid? kept)
            && kept is null
            && AttachmentGlobalId(layerId, attachmentId) == globalId
            ? (int)attachmentId
            : null;
    }

    /// <summary>Which attachment changes a download sends, split by kind, each with the GlobalID the replica knows it by.</summary>
    /// <param name="Inserts">Attachments the replica does not hold yet.</param>
    /// <param name="Updates">Attachments it holds and the server changed.</param>
    /// <param name="Deletes">Attachments it holds and the server removed.</param>
    public sealed record AttachmentSelection(
        IReadOnlyList<(AttachmentChange Change, Guid GlobalId)> Inserts,
        IReadOnlyList<(AttachmentChange Change, Guid GlobalId)> Updates,
        IReadOnlyList<(AttachmentChange Change, Guid GlobalId)> Deletes);

    /// <summary>
    /// The attachment changes of a download — ADR-290: those a change record vouches for, of the features the replica holds
    /// after the sync, and not the ones the same sync's upload made.
    /// </summary>
    /// <param name="layerId">The layer's catalogue id.</param>
    /// <param name="changes">The net changes between the replica's generation and the download's.</param>
    /// <param name="inReplica">The features the replica holds once this download is applied, by object id.</param>
    /// <param name="newToReplica">The features this download adds to the replica, whose attachments are all sent anyway.</param>
    /// <param name="own">The GlobalIDs the upload named.</param>
    /// <returns>The changes to send.</returns>
    /// <remarks>
    /// <b>A feature the replica loses takes its attachments with it</b> — a geodatabase's attachment relationship is
    /// composite — so an attachment's delete is sent only where its feature stays; a feature the download adds is sent
    /// with every attachment it has now, so the log's entries for it are left out rather than sent twice.
    /// </remarks>
    public static AttachmentSelection SelectAttachments(
        Guid layerId,
        IReadOnlyList<AttachmentChange> changes,
        IReadOnlySet<long> inReplica,
        IReadOnlySet<long> newToReplica,
        IReadOnlySet<Guid> own)
    {
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(inReplica);
        ArgumentNullException.ThrowIfNull(newToReplica);
        ArgumentNullException.ThrowIfNull(own);

        List<(AttachmentChange, Guid)> inserts = [];
        List<(AttachmentChange, Guid)> updates = [];
        List<(AttachmentChange, Guid)> deletes = [];

        foreach (AttachmentChange change in changes)
        {
            if (!inReplica.Contains(change.FeatureId) || newToReplica.Contains(change.FeatureId))
            {
                continue;
            }

            Guid globalId = AttachmentGlobalId(layerId, change.AttachmentId, change.GlobalId);

            if (own.Contains(globalId))
            {
                continue;
            }

            (change.Kind switch
            {
                AttachmentChangeKind.Insert => inserts,
                AttachmentChangeKind.Update => updates,
                _ => deletes,
            }).Add((change, globalId));
        }

        return new AttachmentSelection(inserts, updates, deletes);
    }

    private static Guid? GlobalIdOf(OfflineRow row, int at) => row.Values[at] switch
    {
        Guid id => id,
        string text when Guid.TryParse(text.Trim('{', '}'), out Guid parsed) => parsed,
        _ => null,
    };

    private static string Braced(Guid id) => "{" + id.ToString("D").ToUpperInvariant() + "}";

    private static string Features(OfflineLayer layer, List<OfflineRow> rows, IReadOnlySet<string> editable, string globalIdField)
    {
        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartArray();

            foreach (OfflineRow row in rows)
            {
                json.WriteStartObject();
                json.WriteStartObject("attributes");

                for (int i = 0; i < layer.Fields.Count; i++)
                {
                    OfflineField field = layer.Fields[i];
                    bool isGlobal = string.Equals(field.Name, globalIdField, StringComparison.OrdinalIgnoreCase);

                    // The GlobalID names the row on an update and is kept on an add; the object id is the server's own.
                    if (!isGlobal && (!editable.Contains(field.Name) || NotWritten.Contains(field.EsriType)))
                    {
                        continue;
                    }

                    json.WritePropertyName(field.Name);
                    WriteValue(json, row.Values[i]);
                }

                json.WriteEndObject();

                if (row.Geometry is { IsEmpty: false } geometry && layer.GeometryType is not null)
                {
                    json.WritePropertyName("geometry");
                    ArcGisGeometryWriter.Write(json, geometry, layer.Wkid);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

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
                json.WriteNumberValue(real);
                break;
            case float real:
                json.WriteNumberValue(real);
                break;
            case decimal real:
                json.WriteNumberValue(real);
                break;
            default:
                json.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture));
                break;
        }
    }
}
