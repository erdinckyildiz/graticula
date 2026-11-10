using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Graticula.Api.ArcGis;
using Graticula.Geometries;
using Graticula.Platform.Catalog;

namespace Graticula.Host;

/// <summary>One layer's filter in <c>createReplica</c>'s <c>layerQueries</c>.</summary>
/// <param name="QueryOption"><c>useFilter</c>, <c>all</c> or <c>none</c>.</param>
/// <param name="Where">The clause, or empty for none.</param>
/// <param name="UseGeometry">Whether the replica's geometry filters the layer.</param>
internal sealed record ReplicaLayerQuery(string QueryOption, string Where, bool UseGeometry)
{
    /// <summary>What a layer the caller did not describe is: every row in the area.</summary>
    public static ReplicaLayerQuery Default { get; } = new("useFilter", string.Empty, true);
}

/// <summary>A <c>createReplica</c> request, read and checked — ADR-288 §5.4.</summary>
/// <param name="Name">The replica's name.</param>
/// <param name="Layers">The layer ids, in service order.</param>
/// <param name="Queries">Each layer's filter.</param>
/// <param name="Filter">The area, in <paramref name="Srid"/>, or null for the whole layer.</param>
/// <param name="Extent">The area's envelope, or null.</param>
/// <param name="Srid">The reference of <paramref name="Filter"/>.</param>
/// <param name="GeometryJson">The area as the client sent it, for the replica's own document.</param>
/// <param name="ReturnAttachments">Whether attachments are copied.</param>
/// <param name="AttachmentsSyncDirection"><c>none</c>, <c>upload</c> or <c>bidirectional</c>.</param>
/// <param name="SyncModel"><c>none</c> or <c>perLayer</c>.</param>
/// <param name="Async">Whether the client asked for a status URL.</param>
internal sealed record CreateReplicaRequest(
    string Name,
    IReadOnlyList<int> Layers,
    IReadOnlyDictionary<int, ReplicaLayerQuery> Queries,
    Geometry? Filter,
    Envelope? Extent,
    int Srid,
    string? GeometryJson,
    bool ReturnAttachments,
    string AttachmentsSyncDirection,
    string SyncModel,
    bool Async)
{
    /// <summary>Whether the replica is registered, so the server keeps its generations.</summary>
    public bool Registered => SyncModel == "perLayer";
}

/// <summary>One layer of a <c>synchronizeReplica</c>'s <c>syncLayers</c>.</summary>
/// <param name="Id">The layer.</param>
/// <param name="ServerGeneration">The generation the client holds, or null for the server's record of it.</param>
/// <param name="Direction"><c>download</c>, <c>upload</c>, <c>bidirectional</c> or <c>none</c>.</param>
internal sealed record SyncLayerRequest(int Id, long? ServerGeneration, string Direction);

/// <summary>A <c>synchronizeReplica</c> request, read and checked — ADR-288 §5.4.</summary>
/// <param name="ReplicaId">The replica.</param>
/// <param name="Direction">The replica-wide direction.</param>
/// <param name="Layers">The layers asked for, or empty for all of the replica's.</param>
/// <param name="UploadId">The uploaded delta's id, or null.</param>
/// <param name="Async">Whether the client asked for a status URL.</param>
/// <param name="ReturnAttachments">Whether attachments travel.</param>
/// <param name="CloseReplica">Whether the replica is unregistered once synchronised.</param>
internal sealed record SynchronizeReplicaRequest(
    Guid ReplicaId,
    string Direction,
    IReadOnlyList<SyncLayerRequest> Layers,
    string? UploadId,
    bool Async,
    bool ReturnAttachments,
    bool CloseReplica);

/// <summary>
/// Reads <c>createReplica</c> and <c>synchronizeReplica</c> as Esri's public REST reference describes them — ADR-288.
/// </summary>
/// <remarks>
/// <para>
/// <b>Sources.</b> Esri, <i>Create Replica (Feature Service)</i> and <i>Synchronize Replica (Feature Service)</i>,
/// developers.arcgis.com/rest/services-reference/enterprise/create-replica/ and …/synchronize-replica/ (public, read
/// 2026-11-10); the refusal of <c>perReplica</c> is the sentence ArcGIS Enterprise 12.1 answered on a hosted service
/// the same day (ADR-288 §4).
/// </para>
/// <para>
/// <b>What is refused, by name.</b> <c>dataFormat</c> other than <c>sqlite</c> (ADR-288 §5.4, <c>INFERRED</c>);
/// <c>perReplica</c>; <c>transportType=esriTransportTypeEmbedded</c>, which cannot carry a file; <c>targetType=server</c>,
/// a server-to-server replica this server does not register. Each is refused rather than ignored, because a client told
/// it has the replica it asked for builds on it (ADR-008 §2).
/// </para>
/// </remarks>
internal static partial class OfflineReplicaRequests
{
    /// <summary>ArcGIS's refusal of a per-replica sync on a hosted service, word for word.</summary>
    public const string PerReplicaRefusal =
        "Enterprise hosted feature services do not support replicas of type 'perReplica'.";

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex ReplicaName();

    /// <summary>Whether a replica name is one ArcGIS accepts: letters, digits and underscores.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True when it is.</returns>
    public static bool IsReplicaName(string? name) => name is { Length: > 0 and <= 128 } && ReplicaName().IsMatch(name);

    /// <summary>Reads a <c>createReplica</c> request.</summary>
    /// <param name="field">A parameter by name, from the form or the query.</param>
    /// <param name="service">The service, for its layers and its reference.</param>
    /// <param name="request">The request, when this returns true.</param>
    /// <param name="error">Why not, when it returns false.</param>
    /// <returns>Whether it can be answered.</returns>
    public static bool TryReadCreate(
        Func<string, string?> field, PublishedService service, out CreateReplicaRequest? request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(service);

        request = null;

        string format = (field("dataFormat") ?? "json").Trim();
        if (!format.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            error = $"dataFormat '{format}' is not offered: this server writes replicas as a mobile geodatabase "
                + "(dataFormat=sqlite) only, which is what ArcGIS Field Maps and ArcGIS Pro ask for; JSON replicas are a later "
                + "step (ADR-288 §5.4).";
            return false;
        }

        string model = (field("syncModel") ?? "none").Trim();
        if (model.Equals("perReplica", StringComparison.OrdinalIgnoreCase))
        {
            error = PerReplicaRefusal;
            return false;
        }

        if (!model.Equals("none", StringComparison.OrdinalIgnoreCase) && !model.Equals("perLayer", StringComparison.OrdinalIgnoreCase))
        {
            error = $"syncModel '{model}' is not one of none, perLayer.";
            return false;
        }

        model = model.Equals("none", StringComparison.OrdinalIgnoreCase) ? "none" : "perLayer";

        if (field("transportType") is { } transport && transport.Trim().Length > 0
            && !transport.Trim().Equals("esriTransportTypeUrl", StringComparison.OrdinalIgnoreCase))
        {
            error = $"transportType '{transport.Trim()}' is not offered: a mobile geodatabase is a file, sent by URL "
                + "(esriTransportTypeUrl).";
            return false;
        }

        if (field("targetType") is { } target && target.Trim().Length > 0
            && !target.Trim().Equals("client", StringComparison.OrdinalIgnoreCase))
        {
            error = $"targetType '{target.Trim()}' is not offered: replicas here are registered for clients only.";
            return false;
        }

        string name = (field("replicaName") ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = "replica_" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        }

        if (!IsReplicaName(name))
        {
            error = $"replicaName '{name}' is not a valid replica name: it may hold letters, digits and underscores only, "
                + "as ArcGIS requires.";
            return false;
        }

        if (!TryReadLayers(field("layers"), service, out List<int> layers, out error))
        {
            return false;
        }

        if (!TryReadQueries(field("layerQueries"), layers, out Dictionary<int, ReplicaLayerQuery> queries, out error))
        {
            return false;
        }

        int defaultSrid = service.Srid ?? (service.LayersAndTables.Count > 0 ? service.LayersAndTables[0].PublishedSrid : 4326);

        if (!TryReadArea(field("geometry"), field("geometryType"), field("inSR"), defaultSrid,
                out Geometry? filter, out Envelope? extent, out int srid, out error))
        {
            return false;
        }

        string direction = (field("attachmentsSyncDirection") ?? "bidirectional").Trim();
        if (direction.Length == 0)
        {
            direction = "bidirectional";
        }

        if (direction is not ("none" or "upload" or "bidirectional"))
        {
            error = $"attachmentsSyncDirection '{direction}' is not one of none, upload, bidirectional.";
            return false;
        }

        request = new CreateReplicaRequest(
            name,
            layers,
            queries,
            filter,
            extent,
            srid,
            AreaJson(field("geometry"), extent, srid),
            IsTrue(field("returnAttachments")),
            direction,
            model,
            IsTrue(field("async")));
        error = null;
        return true;
    }

    /// <summary>Reads a <c>synchronizeReplica</c> request.</summary>
    /// <param name="field">A parameter by name.</param>
    /// <param name="request">The request, when this returns true.</param>
    /// <param name="error">Why not, when it returns false.</param>
    /// <returns>Whether it can be answered.</returns>
    public static bool TryReadSynchronize(Func<string, string?> field, out SynchronizeReplicaRequest? request, out string? error)
    {
        ArgumentNullException.ThrowIfNull(field);

        request = null;

        if (!TryReadReplicaId(field("replicaID"), out Guid id))
        {
            error = $"replicaID '{field("replicaID")}' is not a replica id.";
            return false;
        }

        if (field("dataFormat") is { } format && format.Trim().Length > 0
            && !format.Trim().Equals("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            error = $"dataFormat '{format.Trim()}' is not offered: changes travel as a mobile geodatabase (dataFormat=sqlite) "
                + "only (ADR-288 §5.4).";
            return false;
        }

        string direction = (field("syncDirection") ?? "bidirectional").Trim();
        if (direction.Length == 0)
        {
            direction = "bidirectional";
        }

        if (direction.Equals("snapshot", StringComparison.OrdinalIgnoreCase))
        {
            error = "syncDirection 'snapshot' is not offered: it replaces the whole replica, which createReplica does. "
                + "Send download, upload or bidirectional.";
            return false;
        }

        if (direction is not ("download" or "upload" or "bidirectional"))
        {
            error = $"syncDirection '{direction}' is not one of download, upload, bidirectional.";
            return false;
        }

        List<SyncLayerRequest> layers = [];
        string syncLayers = (field("syncLayers") ?? string.Empty).Trim();

        if (syncLayers.Length > 0 && syncLayers != "*")
        {
            try
            {
                foreach (JsonElement entry in JsonDocument.Parse(syncLayers).RootElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out JsonElement layerId) || !layerId.TryGetInt32(out int number))
                    {
                        error = "Each entry of syncLayers needs a numeric id.";
                        return false;
                    }

                    long? generation = entry.TryGetProperty("serverGen", out JsonElement gen) && gen.ValueKind == JsonValueKind.Number
                        ? gen.GetInt64()
                        : null;
                    string layerDirection = entry.TryGetProperty("syncDirection", out JsonElement d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString()!
                        : direction;

                    if (layerDirection is not ("download" or "upload" or "bidirectional" or "none"))
                    {
                        error = $"syncLayers: layer {number}'s syncDirection '{layerDirection}' is not one of download, upload, "
                            + "bidirectional, none.";
                        return false;
                    }

                    layers.Add(new SyncLayerRequest(number, generation, layerDirection));
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                error = "syncLayers must be a JSON array of {\"id\": layer id, \"serverGen\": generation, \"syncDirection\": …}.";
                return false;
            }
        }

        string? upload = field("editsUploadID")?.Trim();
        if (upload is { Length: > 0 })
        {
            string uploadFormat = (field("editsUploadFormat") ?? "sqlite").Trim();
            if (!uploadFormat.Equals("sqlite", StringComparison.OrdinalIgnoreCase))
            {
                error = $"editsUploadFormat '{uploadFormat}' is not read: an upload is a mobile geodatabase delta (sqlite).";
                return false;
            }
        }

        request = new SynchronizeReplicaRequest(
            id,
            direction,
            layers,
            upload is { Length: > 0 } ? upload : null,
            IsTrue(field("async")),
            !string.Equals(field("returnAttachments")?.Trim(), "false", StringComparison.OrdinalIgnoreCase),
            IsTrue(field("closeReplica")));
        error = null;
        return true;
    }

    /// <summary>Reads a replica id with or without braces.</summary>
    /// <param name="text">The text.</param>
    /// <param name="id">The id.</param>
    /// <returns>Whether it is one.</returns>
    public static bool TryReadReplicaId(string? text, out Guid id) =>
        Guid.TryParse((text ?? string.Empty).Trim().Trim('{', '}'), out id);

    /// <summary>A replica id as ArcGIS writes it: upper case, without braces.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The text.</returns>
    public static string Wire(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>Reads <c>layers</c>: <c>0,1</c> or <c>[0,1]</c>, each a layer or table of the service.</summary>
    internal static bool TryReadLayers(string? raw, PublishedService service, out List<int> layers, out string? error)
    {
        layers = [];
        error = null;
        string text = (raw ?? string.Empty).Trim().TrimStart('[').TrimEnd(']');

        if (text.Length == 0)
        {
            error = "layers is required: the ids of the layers and tables to take offline, as 0,1 or [0,1].";
            return false;
        }

        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                error = $"layers: '{part}' is not a layer id.";
                return false;
            }

            if (service.LayerOrTable(id) is null)
            {
                error = $"layers: {id} is not a layer or table of this service.";
                return false;
            }

            if (!layers.Contains(id))
            {
                layers.Add(id);
            }
        }

        layers.Sort();
        return true;
    }

    /// <summary>Reads <c>layerQueries</c>: <c>{"0":{"queryOption":"useFilter","where":"…","useGeometry":true}}</c>.</summary>
    internal static bool TryReadQueries(
        string? raw, IReadOnlyList<int> layers, out Dictionary<int, ReplicaLayerQuery> queries, out string? error)
    {
        queries = layers.ToDictionary(id => id, _ => ReplicaLayerQuery.Default);
        error = null;
        string text = (raw ?? string.Empty).Trim();

        if (text.Length == 0)
        {
            return true;
        }

        try
        {
            JsonElement root = JsonDocument.Parse(text).RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "layerQueries must be a JSON object keyed by layer id.";
                return false;
            }

            foreach (JsonProperty entry in root.EnumerateObject())
            {
                if (!int.TryParse(entry.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || !queries.ContainsKey(id))
                {
                    error = $"layerQueries names '{entry.Name}', which is not one of the layers asked for.";
                    return false;
                }

                JsonElement query = entry.Value;
                string option = query.TryGetProperty("queryOption", out JsonElement o) && o.ValueKind == JsonValueKind.String
                    ? o.GetString()!
                    : "useFilter";

                if (option is not ("useFilter" or "all" or "none"))
                {
                    error = $"layerQueries: layer {id}'s queryOption '{option}' is not one of useFilter, all, none.";
                    return false;
                }

                string where = query.TryGetProperty("where", out JsonElement w) && w.ValueKind == JsonValueKind.String
                    ? w.GetString()!.Trim()
                    : string.Empty;
                bool useGeometry = !query.TryGetProperty("useGeometry", out JsonElement g)
                    || g.ValueKind != JsonValueKind.False;

                queries[id] = new ReplicaLayerQuery(option, where, useGeometry);
            }
        }
        catch (JsonException)
        {
            error = "layerQueries is not JSON.";
            return false;
        }

        return true;
    }

    /// <summary>Reads the area: an envelope as JSON or <c>xmin,ymin,xmax,ymax</c>, or a polygon as JSON.</summary>
    internal static bool TryReadArea(
        string? geometry,
        string? geometryType,
        string? inSr,
        int defaultSrid,
        out Geometry? filter,
        out Envelope? extent,
        out int srid,
        out string? error)
    {
        filter = null;
        extent = null;
        srid = defaultSrid;
        error = null;
        string text = (geometry ?? string.Empty).Trim();

        if (inSr is { Length: > 0 } && !TryReadWkid(inSr.Trim(), out srid))
        {
            error = $"inSR '{inSr}' is not a spatial reference id.";
            return false;
        }

        if (text.Length == 0)
        {
            return true;
        }

        string type = (geometryType ?? "esriGeometryEnvelope").Trim();

        if (text[0] != '{')
        {
            double[] numbers = [.. text.Split(',', StringSplitOptions.TrimEntries)
                .Select(n => double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN)];

            if (numbers.Length != 4 || numbers.Any(double.IsNaN))
            {
                error = "geometry must be an envelope as xmin,ymin,xmax,ymax or as JSON, or a polygon as JSON.";
                return false;
            }

            extent = new Envelope(numbers[0], numbers[1], numbers[2], numbers[3]);
            filter = Rectangle(extent.Value);
            return true;
        }

        try
        {
            JsonElement root = JsonDocument.Parse(text).RootElement;

            if (inSr is not { Length: > 0 }
                && root.TryGetProperty("spatialReference", out JsonElement reference)
                && TryReadWkid(reference.GetRawText(), out int declared))
            {
                srid = declared;
            }

            if (root.TryGetProperty("xmin", out _))
            {
                extent = new Envelope(
                    root.GetProperty("xmin").GetDouble(), root.GetProperty("ymin").GetDouble(),
                    root.GetProperty("xmax").GetDouble(), root.GetProperty("ymax").GetDouble());
                filter = Rectangle(extent.Value);
                return true;
            }

            if (!type.Equals("esriGeometryPolygon", StringComparison.OrdinalIgnoreCase) && !root.TryGetProperty("rings", out _))
            {
                error = $"geometryType '{type}' is not offered: an offline area is an envelope or a polygon.";
                return false;
            }

            if (!ArcGisGeometryReader.TryRead(root, srid, out Geometry? read, out string? why) || read is null || read.IsEmpty)
            {
                error = $"geometry could not be read as a polygon: {why}";
                return false;
            }

            filter = read;
            extent = read.Envelope;
            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            error = "geometry must be an envelope as xmin,ymin,xmax,ymax or as JSON, or a polygon as JSON.";
            return false;
        }
    }

    /// <summary>
    /// The area as JSON for the registry and the replica's own document: as sent when it was JSON, and the envelope with
    /// its reference when it was four numbers.
    /// </summary>
    internal static string? AreaJson(string? sent, Envelope? extent, int srid)
    {
        string text = (sent ?? string.Empty).Trim();

        if (text.Length == 0 || extent is not { } box)
        {
            return null;
        }

        return text[0] == '{'
            ? text
            : string.Create(CultureInfo.InvariantCulture,
                $"{{\"xmin\":{box.MinX},\"ymin\":{box.MinY},\"xmax\":{box.MaxX},\"ymax\":{box.MaxY},\"spatialReference\":{{\"wkid\":{srid}}}}}");
    }

    /// <summary>A rectangle as a polygon.</summary>
    internal static Polygon Rectangle(Envelope box) => new(new LinearRing(XySequence.Wrap(
        [box.MinX, box.MinY, box.MaxX, box.MinY, box.MaxX, box.MaxY, box.MinX, box.MaxY, box.MinX, box.MinY])));

    /// <summary>Reads a spatial reference: a number, or <c>{"wkid":…}</c> / <c>{"latestWkid":…}</c>.</summary>
    internal static bool TryReadWkid(string text, out int srid)
    {
        srid = 0;

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int plain))
        {
            srid = ArcGisGeometryReader.Canonical(plain);
            return true;
        }

        try
        {
            JsonElement root = JsonDocument.Parse(text).RootElement;

            foreach (string name in (string[])["latestWkid", "wkid"])
            {
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value)
                    && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int found))
                {
                    srid = ArcGisGeometryReader.Canonical(found);
                    return true;
                }
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    private static bool IsTrue(string? value) =>
        string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
}
