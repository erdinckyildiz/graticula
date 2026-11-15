using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Api.ArcGis.Offline;

namespace Graticula.Host;

/// <summary>
/// The JSON upload of <c>synchronizeReplica</c> — the <c>edits</c> parameter, D-426 — read into what the geodatabase upload
/// is read into, so that one rule (ADR-288 §5.5) and one applying path serve both.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape is the public reference's</b> (<i>Synchronize Replica</i>, ArcGIS REST API, <c>edits</c>): an array with one
/// entry per layer, <c>{ "id": 0, "features": { "adds": [], "updates": [], "deleteIds": [] },
/// "attachments": { "adds": [], "updates": [], "deleteIds": [] } }</c>. A feature is <c>{ "attributes", "geometry" }</c>;
/// an attachment is <c>{ "globalid", "parentGlobalid", "contentType", "name", "data" }</c>. Names are read without regard to
/// case, because the reference spells <c>globalid</c> and <c>parentGlobalid</c> as it does and a client's own habit differs.
/// </para>
/// <para>
/// <b><c>INFERRED</c>, where the reference is silent or this server chooses:</b> the flat shape
/// <c>{ "id", "adds", "updates", "deletes" }</c> is accepted as well as <c>features</c> (a client that was told it by the
/// task and not by the page); <c>deleteIds</c> are GlobalIDs, because a replica syncs by GlobalID and a number is refused
/// by name instead of being taken for an object id; an attachment's <c>data</c> is base64 and an attachment that has only a
/// <c>url</c> is refused, because this server does not fetch an address a client names.
/// </para>
/// </remarks>
internal static class OfflineJsonEdits
{
    /// <summary>One layer's entry.</summary>
    /// <param name="Id">The layer.</param>
    /// <param name="Adds">Feature adds, as sent.</param>
    /// <param name="Updates">Feature updates, as sent.</param>
    /// <param name="Deletes">GlobalIDs of features to delete.</param>
    /// <param name="AttachmentAdds">Attachment adds.</param>
    /// <param name="AttachmentUpdates">Attachment updates.</param>
    /// <param name="AttachmentDeletes">GlobalIDs of attachments to delete.</param>
    internal sealed record Entry(
        int Id,
        IReadOnlyList<JsonObject> Adds,
        IReadOnlyList<JsonObject> Updates,
        IReadOnlyList<Guid> Deletes,
        IReadOnlyList<JsonObject> AttachmentAdds,
        IReadOnlyList<JsonObject> AttachmentUpdates,
        IReadOnlyList<Guid> AttachmentDeletes);

    /// <summary>Reads an <c>edits</c> array.</summary>
    /// <param name="edits">The parameter's text, already known to be a JSON array.</param>
    /// <param name="entries">The layers' entries.</param>
    /// <param name="error">Why not, naming the part.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryRead(string edits, out List<Entry> entries, out string? error)
    {
        ArgumentNullException.ThrowIfNull(edits);

        entries = [];
        error = null;

        JsonArray root;

        try
        {
            root = JsonNode.Parse(edits)?.AsArray() ?? [];
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            error = "edits is not a JSON array.";
            return false;
        }

        foreach (JsonNode? node in root)
        {
            if (node is not JsonObject layer || Number(layer, "id") is not { } id)
            {
                error = "Each entry of edits needs a numeric id: [{\"id\": 0, \"features\": {\"adds\": [], \"updates\": [], \"deleteIds\": []}, "
                    + "\"attachments\": {\"adds\": [], \"updates\": [], \"deleteIds\": []}}].";
                return false;
            }

            JsonObject features = Member(layer, "features") as JsonObject ?? layer;
            JsonObject attachments = Member(layer, "attachments") as JsonObject ?? new JsonObject();

            if (!TryObjects(features, "adds", id, out List<JsonObject> adds, out error)
                || !TryObjects(features, "updates", id, out List<JsonObject> updates, out error)
                || !TryGlobalIds(features, id, "features", out List<Guid> deletes, out error)
                || !TryObjects(attachments, "adds", id, out List<JsonObject> attachmentAdds, out error)
                || !TryObjects(attachments, "updates", id, out List<JsonObject> attachmentUpdates, out error)
                || !TryGlobalIds(attachments, id, "attachments", out List<Guid> attachmentDeletes, out error))
            {
                return false;
            }

            entries.Add(new Entry(id, adds, updates, deletes, attachmentAdds, attachmentUpdates, attachmentDeletes));
        }

        return true;
    }

    /// <summary>
    /// A layer's feature edits as the service's <c>applyEdits</c> takes them with <c>useGlobalIds=true</c>, by the rule the
    /// geodatabase upload follows (<see cref="OfflineReplicaShapes.Edits"/>): an add of a GlobalID the server holds is an update,
    /// and an update or delete of one it no longer holds is dropped and counted.
    /// </summary>
    /// <param name="entry">The layer's entry.</param>
    /// <param name="layer">The layer's shape.</param>
    /// <param name="globalIdField">Its GlobalID field.</param>
    /// <param name="editable">The fields a client may write.</param>
    /// <param name="existing">The GlobalIDs the server holds now, of every feature the entry names.</param>
    /// <param name="error">Why the entry cannot be read, when this returns null.</param>
    /// <returns>The edits, or null.</returns>
    public static ReplicaLayerEdits? Edits(
        Entry entry, OfflineLayer layer, string globalIdField, IReadOnlySet<string> editable, IReadOnlySet<Guid> existing, out string? error)
    {
        error = null;

        List<JsonObject> adds = [];
        List<JsonObject> updates = [];
        int dropped = 0;

        foreach (JsonObject feature in entry.Adds)
        {
            if (!GlobalIdOf(feature, globalIdField, out Guid id, out error))
            {
                return null;
            }

            (existing.Contains(id) ? updates : adds).Add(Written(feature, layer, globalIdField, editable));
        }

        foreach (JsonObject feature in entry.Updates)
        {
            if (!GlobalIdOf(feature, globalIdField, out Guid id, out error))
            {
                return null;
            }

            if (existing.Contains(id))
            {
                updates.Add(Written(feature, layer, globalIdField, editable));
            }
            else
            {
                dropped++;
            }
        }

        List<Guid> deletes = [.. entry.Deletes.Where(existing.Contains)];
        dropped += entry.Deletes.Count - deletes.Count;

        return new ReplicaLayerEdits(
            layer.Id,
            adds.Count == 0 ? null : new JsonArray([.. adds.Select(a => (JsonNode?)a)]).ToJsonString(),
            updates.Count == 0 ? null : new JsonArray([.. updates.Select(a => (JsonNode?)a)]).ToJsonString(),
            deletes.Count == 0 ? null : new JsonArray([.. deletes.Select(d => (JsonNode?)Braced(d))]).ToJsonString(),
            dropped);
    }

    /// <summary>Every GlobalID an entry's features name.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="globalIdField">The GlobalID field.</param>
    /// <returns>The GlobalIDs.</returns>
    public static HashSet<Guid> FeatureGlobalIds(Entry entry, string globalIdField)
    {
        HashSet<Guid> ids = [.. entry.Deletes];

        foreach (JsonObject feature in entry.Adds.Concat(entry.Updates))
        {
            if (GlobalIdOf(feature, globalIdField, out Guid id, out _))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>The GlobalIDs of the features an entry adds.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="globalIdField">The GlobalID field.</param>
    /// <returns>The GlobalIDs.</returns>
    public static IEnumerable<Guid> AddedGlobalIds(Entry entry, string globalIdField) =>
        entry.Adds.Select(a => GlobalIdOf(a, globalIdField, out Guid id, out _) ? id : (Guid?)null).OfType<Guid>();

    /// <summary>The attachment changes of an entry, as the geodatabase upload carries them.</summary>
    /// <param name="entry">The entry.</param>
    /// <param name="layer">The layer's shape.</param>
    /// <param name="error">Why not.</param>
    /// <returns>The changes (with no rows), or null.</returns>
    public static OfflineLayerChanges? Attachments(Entry entry, OfflineLayer layer, out string? error)
    {
        error = null;

        if (!TryAttachments(entry.AttachmentAdds, out List<OfflineAttachment> adds, out error)
            || !TryAttachments(entry.AttachmentUpdates, out List<OfflineAttachment> updates, out error))
        {
            return null;
        }

        return new OfflineLayerChanges(
            layer, [], [], [], adds, updates, [.. entry.AttachmentDeletes.Select(id => new OfflineKey(0, id))]);
    }

    private static bool TryAttachments(IReadOnlyList<JsonObject> sent, out List<OfflineAttachment> read, out string? error)
    {
        read = [];
        error = null;

        foreach (JsonObject one in sent)
        {
            if (!Guid.TryParse(Text(one, "globalid")?.Trim('{', '}'), out Guid id)
                || !Guid.TryParse(Text(one, "parentGlobalid")?.Trim('{', '}'), out Guid parent))
            {
                error = "An attachment in edits needs a globalid and a parentGlobalid (the GlobalID of its feature).";
                return false;
            }

            if (Text(one, "data") is not { } data)
            {
                error = $"Attachment {id} has no data. A url is not fetched from here, because this server does not read an address a "
                    + "client names; send the bytes as base64 in data, or upload a mobile geodatabase delta.";
                return false;
            }

            byte[] bytes;

            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                error = $"Attachment {id}: data is not base64.";
                return false;
            }

            read.Add(new OfflineAttachment(0, parent, id, Text(one, "name") ?? "attachment", Text(one, "contentType") ?? "application/octet-stream", bytes));
        }

        return true;
    }

    private static JsonObject Written(JsonObject feature, OfflineLayer layer, string globalIdField, IReadOnlySet<string> editable)
    {
        JsonObject attributes = new();

        if (Member(feature, "attributes") is JsonObject sent)
        {
            foreach (OfflineField field in layer.Fields)
            {
                bool isGlobal = string.Equals(field.Name, globalIdField, StringComparison.OrdinalIgnoreCase);

                // The GlobalID names the row; the object id and every field a client may not write are the server's own.
                if (Member(sent, field.Name) is { } value && (isGlobal || (editable.Contains(field.Name) && field.EsriType != "esriFieldTypeOID")))
                {
                    attributes[field.Name] = value.DeepClone();
                }
            }
        }

        JsonObject written = new() { ["attributes"] = attributes };

        if (Member(feature, "geometry") is { } geometry)
        {
            written["geometry"] = geometry.DeepClone();
        }

        return written;
    }

    private static bool GlobalIdOf(JsonObject feature, string field, out Guid id, out string? error)
    {
        id = Guid.Empty;
        error = null;

        if (Member(feature, "attributes") is JsonObject attributes
            && Text(attributes, field)?.Trim('{', '}') is { } text && Guid.TryParse(text, out id))
        {
            return true;
        }

        error = $"A feature in edits has no {field}. A replica syncs by GlobalID, so every feature sent must carry its own in its attributes.";
        return false;
    }

    private static bool TryObjects(JsonObject from, string name, int layer, out List<JsonObject> read, out string? error)
    {
        read = [];
        error = null;

        if (Member(from, name) is not { } node)
        {
            return true;
        }

        if (node is not JsonArray array || array.Any(n => n is not JsonObject))
        {
            error = $"edits: layer {layer}'s {name} is not an array of objects.";
            return false;
        }

        read = [.. array.Select(n => (JsonObject)n!)];
        return true;
    }

    private static bool TryGlobalIds(JsonObject from, int layer, string part, out List<Guid> read, out string? error)
    {
        read = [];
        error = null;

        JsonNode? node = Member(from, "deleteIds") ?? Member(from, "deletes");

        if (node is null)
        {
            return true;
        }

        if (node is not JsonArray array)
        {
            error = $"edits: layer {layer}'s {part} deleteIds is not an array.";
            return false;
        }

        foreach (JsonNode? one in array)
        {
            if (one is JsonValue value && value.TryGetValue(out string? text) && Guid.TryParse(text.Trim('{', '}'), out Guid id))
            {
                read.Add(id);
            }
            else
            {
                error = $"edits: layer {layer}'s {part} deleteIds names '{one}', which is not a GlobalID. A replica syncs by GlobalID, "
                    + "so deleteIds are GlobalIDs and not object ids.";
                return false;
            }
        }

        return true;
    }

    private static JsonNode? Member(JsonObject from, string name) =>
        from.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonObject from, string name) =>
        Member(from, name) is JsonValue v && v.TryGetValue(out string? text) ? text : null;

    private static int? Number(JsonObject from, string name) =>
        Member(from, name) is JsonValue v && v.TryGetValue(out int number) ? number : null;

    private static string Braced(Guid id) => "{" + id.ToString("D", CultureInfo.InvariantCulture).ToUpperInvariant() + "}";
}
