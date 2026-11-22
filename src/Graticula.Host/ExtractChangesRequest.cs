using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>One layer's window in an <c>extractChanges</c> request — ADR-311 §5.5.</summary>
/// <param name="From">The generation the client holds, epoch milliseconds.</param>
/// <param name="To">The window's end, or null for now.</param>
internal readonly record struct ExtractWindow(long From, long? To);

/// <summary>
/// An <c>extractChanges</c> request, read and checked — ADR-311 §5.4. The parameters and their defaults are ArcGIS's
/// public reference (<i>Extract Changes (Feature Service)</i>); every one this server does not serve is refused by name.
/// </summary>
/// <param name="Layers">The layer and table ids, in service order.</param>
/// <param name="Windows">Each layer's window.</param>
/// <param name="Queries">Each layer's filter, read as a replica's <c>layerQueries</c> is.</param>
/// <param name="Area">The geometry filter, in <paramref name="AreaSrid"/>, or null.</param>
/// <param name="AreaSrid">The reference of <paramref name="Area"/>.</param>
/// <param name="OutSrid">The reference to answer in, or null for each layer's own.</param>
/// <param name="ReturnInserts">Whether inserts are answered.</param>
/// <param name="ReturnUpdates">Whether updates are answered.</param>
/// <param name="ReturnDeletes">Whether deletes are answered.</param>
/// <param name="IdsOnly">Object ids only.</param>
/// <param name="ExtentOnly">The extent only.</param>
/// <param name="HasGeometryUpdates">Whether each layer says if an update moved a shape.</param>
/// <param name="Attachments">Whether attachment changes are answered.</param>
/// <param name="AttachmentsByUrl">Whether an attachment is named by its address rather than sent inline.</param>
/// <param name="FieldsToCompare">The fields whose updates are listed in <c>fieldUpdates</c>, or null.</param>
/// <param name="DeletedFeatures">Whether a deleted feature is answered whole, as it was.</param>
/// <param name="Async">Whether the answer is a job.</param>
internal sealed record ExtractChangesRequest(
    IReadOnlyList<int> Layers,
    IReadOnlyDictionary<int, ExtractWindow> Windows,
    IReadOnlyDictionary<int, ReplicaLayerQuery> Queries,
    Geometry? Area,
    int AreaSrid,
    int? OutSrid,
    bool ReturnInserts,
    bool ReturnUpdates,
    bool ReturnDeletes,
    bool IdsOnly,
    bool ExtentOnly,
    bool HasGeometryUpdates,
    bool Attachments,
    bool AttachmentsByUrl,
    IReadOnlyList<string>? FieldsToCompare,
    bool DeletedFeatures,
    bool Async)
{
    /// <summary>ArcGIS's response type for an <c>extractChanges</c> answer and job.</summary>
    public const string ResponseType = "esriDataChangesResponseTypeEdits";

    /// <summary>Reads a request.</summary>
    /// <param name="field">A parameter by name, or null when absent or blank.</param>
    /// <param name="service">The service asked.</param>
    /// <param name="defaultSrid">The reference a geometry without one is read in.</param>
    /// <param name="request">The request, when it reads.</param>
    /// <param name="error">Why not, when it does not.</param>
    /// <returns>Whether it reads.</returns>
    public static bool TryRead(
        Func<string, string?> field, PublishedService service, int defaultSrid,
        out ExtractChangesRequest? request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(service);

        request = null;

        string format = (field("dataFormat") ?? "json").Trim();
        if (!format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            error = format.Equals("sqlite", StringComparison.OrdinalIgnoreCase) || format.Equals("sqllite", StringComparison.OrdinalIgnoreCase)
                ? $"dataFormat '{format}' is not offered: this server answers extractChanges as JSON (dataFormat=json). Its mobile "
                    + "geodatabase writer writes a registered replica's changes, and createReplica and synchronizeReplica are "
                    + "the way to them (ADR-311 §5.4, D-469)."
                : $"dataFormat '{format}' is not one of json, sqlite; this server answers json.";
            return false;
        }

        if (field("changesExtentGridCell") is { } grid && !grid.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            error = $"changesExtentGridCell '{grid.Trim()}' is not offered: an extent of the changes is answered as one envelope "
                + "(changesExtentGridCell=none) (ADR-311 §5.4, D-469).";
            return false;
        }

        // ---- the layers and their windows ----
        Dictionary<int, ExtractWindow> byLayer = [];

        if (field("layerServerGens") is { } layerGens)
        {
            if (!TryReadLayerGens(layerGens, service, byLayer, out error))
            {
                return false;
            }
        }

        ExtractWindow? shared = null;
        if (field("serverGens") is { } gens)
        {
            if (!TryReadGens(gens, out ExtractWindow window, out error))
            {
                return false;
            }

            shared = window;
        }

        List<int> layers;
        if (field("layers") is { } named)
        {
            if (!OfflineReplicaRequests.TryReadLayers(named, service, out layers, out error))
            {
                error = error!.Replace("to take offline", "whose changes to extract", StringComparison.Ordinal);
                return false;
            }
        }
        else if (shared is null && byLayer.Count > 0)
        {
            layers = [.. byLayer.Keys.Order()];
        }
        else
        {
            // INFERRED (ADR-311 §5.12 #2): ArcGIS's own changesUrl names no layers; every layer and table is meant.
            layers = [.. service.LayersAndTables.Select(l => l.LayerIndex).Order()];
        }

        if (layers.Count == 0)
        {
            error = "The service has no layers or tables whose changes could be extracted.";
            return false;
        }

        Dictionary<int, ExtractWindow> windows = [];
        foreach (int id in layers)
        {
            if (byLayer.TryGetValue(id, out ExtractWindow own))
            {
                windows[id] = own;
            }
            else if (shared is { } all)
            {
                windows[id] = all;
            }
            else
            {
                error = byLayer.Count == 0
                    ? "serverGens or layerServerGens is required: send the generation you hold, as serverGens=<gen> or "
                        + "serverGens=[<min>,<max>], or layerServerGens=[{\"id\":0,\"serverGen\":<gen>}]."
                    : $"layerServerGens names no generation for layer {id}, and no serverGens covers it.";
                return false;
            }
        }

        // ---- filters ----
        string? queriesText = field("layerQueries");
        if (queriesText is not null && IncludesRelated(queriesText) is { } related)
        {
            error = $"layerQueries: layer {related}'s includeRelated true is not offered: related rows are not followed here "
                + "(ADR-311 §5.4, D-469). Send includeRelated false, or ask the related layer for its own changes.";
            return false;
        }

        if (!OfflineReplicaRequests.TryReadQueries(queriesText, layers, out Dictionary<int, ReplicaLayerQuery> queries, out error))
        {
            return false;
        }

        string? geometryType = field("geometryType")?.Trim();
        if (geometryType is "esriGeometryPoint" or "esriGeometryMultipoint" or "esriGeometryPolyline")
        {
            error = $"geometryType '{geometryType}' is not offered: the changes are filtered by an envelope or a polygon "
                + "(esriGeometryEnvelope, esriGeometryPolygon) (ADR-311 §5.4, D-469).";
            return false;
        }

        if (!OfflineReplicaRequests.TryReadArea(field("geometry"), geometryType, field("inSR"), defaultSrid,
                out Geometry? area, out _, out int areaSrid, out error))
        {
            error = error!.Replace("an offline area", "an extractChanges filter", StringComparison.Ordinal);
            return false;
        }

        int? outSrid = null;
        if (field("outSR") is { } outSr)
        {
            if (!OfflineReplicaRequests.TryReadWkid(outSr.Trim(), out int read))
            {
                error = $"outSR '{outSr}' is not a spatial reference id.";
                return false;
            }

            outSrid = read;
        }

        // ---- what to answer ----
        bool inserts = !IsFalse(field("returnInserts"));
        bool updates = !IsFalse(field("returnUpdates"));
        bool deletes = !IsFalse(field("returnDeletes"));
        bool idsOnly = OfflineReplicaRequests.IsTrue(field("returnIdsOnly"));
        bool extentOnly = OfflineReplicaRequests.IsTrue(field("returnExtentOnly"));
        bool geometryUpdates = OfflineReplicaRequests.IsTrue(field("returnHasGeometryUpdates"));
        bool deleted = OfflineReplicaRequests.IsTrue(field("returnDeletedFeatures"));

        if (idsOnly && extentOnly)
        {
            error = "returnIdsOnly and returnExtentOnly cannot both be true: ask for the ids or for the extent.";
            return false;
        }

        if (geometryUpdates && !updates)
        {
            error = "returnHasGeometryUpdates true needs returnUpdates true: it says whether an update moved a shape.";
            return false;
        }

        if (deleted && (idsOnly || !deletes))
        {
            error = "returnDeletedFeatures true needs returnDeletes true and returnIdsOnly false: it answers the deleted features "
                + "whole.";
            return false;
        }

        List<string>? compare = null;
        if (field("fieldsToCompare") is { } fieldsText)
        {
            if (!TryReadFields(fieldsText, out compare, out error))
            {
                return false;
            }

            if (!updates)
            {
                error = "fieldsToCompare needs returnUpdates true: it lists the updates that changed one of the fields.";
                return false;
            }
        }

        request = new ExtractChangesRequest(
            layers, windows, queries, area, areaSrid, outSrid, inserts, updates, deletes, idsOnly, extentOnly, geometryUpdates,
            !IsFalse(field("returnAttachments")), !IsFalse(field("returnAttachmentsDataByUrl")), compare, deleted,
            OfflineReplicaRequests.IsTrue(field("async")));
        error = null;
        return true;
    }

    /// <summary>Reads <c>serverGens</c>: <c>g</c>, <c>[g]</c>, <c>min,max</c> or <c>[min,max]</c>.</summary>
    internal static bool TryReadGens(string text, out ExtractWindow window, out string? error)
    {
        window = default;
        string[] parts = text.Trim().TrimStart('[').TrimEnd(']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long[] read = [.. parts.Select(p => long.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out long g) ? g : -1)];

        if (read.Length is < 1 or > 2 || read.Any(g => g < 0))
        {
            error = $"serverGens '{Clip(text)}' is not one generation or a [min, max] pair of whole numbers.";
            return false;
        }

        if (read.Length == 2 && read[0] > read[1])
        {
            error = $"serverGens [{read[0]}, {read[1]}]: the first generation is after the second. Send the smaller first.";
            return false;
        }

        window = new ExtractWindow(read[0], read.Length == 2 ? read[1] : null);
        error = null;
        return true;
    }

    private static bool TryReadLayerGens(
        string text, PublishedService service, Dictionary<int, ExtractWindow> windows, out string? error)
    {
        error = null;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);

            if (parsed.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = "layerServerGens is a list of {\"id\": <layer>, \"serverGen\": <generation>}.";
                return false;
            }

            foreach (JsonElement entry in parsed.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out JsonElement id) || !id.TryGetInt32(out int layer)
                    || !entry.TryGetProperty("serverGen", out JsonElement gen) || !gen.TryGetInt64(out long g) || g < 0)
                {
                    error = "layerServerGens is a list of {\"id\": <layer>, \"serverGen\": <generation>}.";
                    return false;
                }

                if (service.LayerOrTable(layer) is null)
                {
                    error = $"layerServerGens: {layer} is not a layer or table of this service.";
                    return false;
                }

                // minServerGen is the server's to say (ADR-311 §5.5); a client that sends it back is not asking anything.
                windows[layer] = new ExtractWindow(g, null);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            error = "layerServerGens is not JSON: send [{\"id\": <layer>, \"serverGen\": <generation>}].";
            return false;
        }

        return true;
    }

    private static string? IncludesRelated(string text)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);

            if (parsed.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty layer in parsed.RootElement.EnumerateObject())
                {
                    if (layer.Value.ValueKind == JsonValueKind.Object
                        && layer.Value.TryGetProperty("includeRelated", out JsonElement related)
                        && related.ValueKind == JsonValueKind.True)
                    {
                        return layer.Name;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // The replica parser says why it is not JSON.
        }

        return null;
    }

    private static bool TryReadFields(string text, out List<string>? fields, out string? error)
    {
        fields = null;
        error = null;

        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);
            JsonElement list = parsed.RootElement.ValueKind == JsonValueKind.Object
                && parsed.RootElement.TryGetProperty("fields", out JsonElement inner) ? inner : parsed.RootElement;

            if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(f => f.ValueKind != JsonValueKind.String))
            {
                error = "fieldsToCompare is {\"fields\": [\"<name>\", ...]}.";
                return false;
            }

            fields = [.. list.EnumerateArray().Select(f => f.GetString()!.Trim()).Where(f => f.Length > 0)];
        }
        catch (JsonException)
        {
            error = "fieldsToCompare is not JSON: send {\"fields\": [\"<name>\", ...]}.";
            return false;
        }

        if (fields.Count == 0)
        {
            error = "fieldsToCompare names no fields.";
            return false;
        }

        return true;
    }

    private static bool IsFalse(string? value) => string.Equals(value?.Trim(), "false", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string text) => text.Length > 120 ? text[..120] + "..." : text;
}
