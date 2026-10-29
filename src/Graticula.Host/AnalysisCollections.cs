using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Providers.PostGis;

namespace Graticula.Host;

/// <summary>
/// Feature collections in an analysis — ADR-222 condition 4: one sent as an input, and a result returned as one when the
/// request names no output.
/// </summary>
/// <remarks>
/// <para>
/// <b>A collection is read twice, by the same code</b>: once when the job is submitted, so a malformed one is refused
/// before it is queued, and once by the runner, which writes it to the run's own table. Both ask
/// <see cref="TryRead"/>, so the plan cannot accept what the run then fails on.
/// </para>
/// <para>
/// <b>It is given to the planner as a layer that exists only in memory</b> — <see cref="LayerOf"/> — because a planner
/// asks a layer two things, its kind of geometry and the name of its id column, and a collection answers both. Every
/// tool's checks and SQL therefore stay the ones a hosted layer gets.
/// </para>
/// </remarks>
internal static class AnalysisCollections
{
    /// <summary>The most features a collection may carry in, and a result may carry out.</summary>
    public const int Limit = 50_000;

    /// <summary>A feature collection, read: its kind, reference, columns and features.</summary>
    /// <param name="Kind">Its kind of geometry.</param>
    /// <param name="Srid">The reference its shapes are in.</param>
    /// <param name="Columns">Its columns by stored name, each with the name its features use.</param>
    /// <param name="Features">Its features as sent.</param>
    internal sealed record Collection(
        GeometryKind Kind, int Srid, IReadOnlyList<(CollectionColumn Column, string Sent)> Columns, JsonArray Features)
    {
        /// <summary>What a planner and a filter read: its fields, by stored name.</summary>
        public LayerDescription Described =>
            new([.. Columns.Select(c => new FieldDescription(c.Column.Name, c.Column.Type, true, null))], null);
    }

    /// <summary>Whether a parameter's value is a feature collection rather than a layer's url.</summary>
    /// <param name="node">The parameter, parsed.</param>
    /// <returns>Whether it carries a featureSet or a layerDefinition.</returns>
    public static bool IsCollection(JsonNode? node) =>
        node is JsonObject o && (o["featureSet"] is not null || o["layerDefinition"] is not null);

    /// <summary>Reads a feature collection, or says why it cannot be read.</summary>
    /// <param name="node">The parameter's value.</param>
    /// <param name="parameter">Its name, for the sentence.</param>
    /// <param name="collection">The collection, on success.</param>
    /// <param name="why">Why not, on failure.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryRead(JsonObject node, string parameter, out Collection? collection, out string? why)
    {
        ArgumentNullException.ThrowIfNull(node);
        collection = null;
        why = null;

        JsonObject? definition = node["layerDefinition"] as JsonObject;
        JsonObject? set = node["featureSet"] as JsonObject;
        JsonArray features = set?["features"] as JsonArray ?? [];

        string? type = set?["geometryType"]?.GetValue<string>() ?? definition?["geometryType"]?.GetValue<string>();
        GeometryKind? kind = type switch
        {
            "esriGeometryPoint" => GeometryKind.Point,
            "esriGeometryMultipoint" => GeometryKind.MultiPoint,
            "esriGeometryPolyline" => GeometryKind.MultiLineString,
            "esriGeometryPolygon" => GeometryKind.MultiPolygon,
            _ => null,
        };

        if (kind is null)
        {
            why = $"{parameter} is a feature collection with no geometryType this server reads: esriGeometryPoint, "
                + "esriGeometryMultipoint, esriGeometryPolyline or esriGeometryPolygon.";
            return false;
        }

        if (features.Count > Limit)
        {
            why = $"{parameter} carries {features.Count:N0} features; a collection sent to an analysis may carry "
                + $"{Limit:N0}. Publish it as a hosted layer and name its url instead.";
            return false;
        }

        JsonNode? reference = set?["spatialReference"] ?? definition?["spatialReference"]
            ?? (features.FirstOrDefault() as JsonObject)?["geometry"]?["spatialReference"];
        int srid = ArcGisGeometryReader.Canonical(
            reference?["latestWkid"]?.GetValue<int>() ?? reference?["wkid"]?.GetValue<int>() ?? 4326);

        List<(CollectionColumn, string)> columns = [];
        HashSet<string> taken = new(StringComparer.OrdinalIgnoreCase) { "objectid", "geom" };

        foreach (JsonObject field in (definition?["fields"] as JsonArray ?? Fields(set)).OfType<JsonObject>())
        {
            string? name = field["name"]?.GetValue<string>();
            string esri = field["type"]?.GetValue<string>() ?? "esriFieldTypeString";

            if (string.IsNullOrWhiteSpace(name) || esri is "esriFieldTypeOID" or "esriFieldTypeGeometry" or "esriFieldTypeGlobalID")
            {
                continue;
            }

            FieldType fieldType = esri switch
            {
                "esriFieldTypeSmallInteger" => FieldType.SmallInteger,
                "esriFieldTypeInteger" => FieldType.Integer,
                "esriFieldTypeBigInteger" => FieldType.BigInteger,
                "esriFieldTypeSingle" => FieldType.Single,
                "esriFieldTypeDouble" => FieldType.Double,
                "esriFieldTypeDate" or "esriFieldTypeDateOnly" or "esriFieldTypeTimestampOffset" => FieldType.Date,
                "esriFieldTypeGUID" => FieldType.Guid,
                _ => FieldType.Text,
            };

            string stored = PostGisImporter.ColumnNameFor(name);

            for (int i = 1; !taken.Add(stored); i++)
            {
                stored = PostGisImporter.ColumnNameFor($"{name}_{i.ToString(CultureInfo.InvariantCulture)}");
            }

            columns.Add((new CollectionColumn(stored, fieldType), name));
        }

        collection = new Collection(kind.Value, srid, columns, features);
        return true;
    }

    /// <summary>
    /// The fields a feature set without a layer definition implies: each attribute of its first feature, typed by its
    /// value.
    /// </summary>
    private static JsonArray Fields(JsonObject? set)
    {
        JsonArray fields = [];

        if ((set?["fields"] as JsonArray) is { } declared)
        {
            return declared;
        }

        if ((set?["features"] as JsonArray)?.FirstOrDefault()?["attributes"] is JsonObject attributes)
        {
            foreach ((string name, JsonNode? value) in attributes)
            {
                fields.Add(new JsonObject
                {
                    ["name"] = name,
                    ["type"] = value?.GetValueKind() switch
                    {
                        JsonValueKind.Number => "esriFieldTypeDouble",
                        _ => "esriFieldTypeString",
                    },
                });
            }
        }

        return fields;
    }

    /// <summary>The rows a collection writes, each shape as WKB in the collection's reference — or why one cannot be read.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="parameter">Its parameter's name, for the sentence.</param>
    /// <param name="rows">The rows, on success.</param>
    /// <param name="why">Why not, on failure.</param>
    /// <returns>Whether every feature was read.</returns>
    public static bool TryRows(Collection collection, string parameter, out IReadOnlyList<CollectionRow> rows, out string? why)
    {
        ArgumentNullException.ThrowIfNull(collection);
        List<CollectionRow> read = [];
        rows = read;
        why = null;
        int index = 0;

        foreach (JsonObject feature in collection.Features.OfType<JsonObject>())
        {
            index++;
            JsonObject? attributes = feature["attributes"] as JsonObject;
            object?[] values = new object?[collection.Columns.Count];

            for (int i = 0; i < collection.Columns.Count; i++)
            {
                (CollectionColumn column, string sent) = collection.Columns[i];
                JsonNode? value = attributes?.FirstOrDefault(a => string.Equals(a.Key, sent, StringComparison.OrdinalIgnoreCase)).Value;

                if (!TryValue(value, column.Type, out values[i]))
                {
                    why = $"Feature {index} of {parameter}: '{sent}' holds {value?.ToJsonString()}, which is not a {column.Type}.";
                    return false;
                }
            }

            byte[]? wkb = null;

            if (feature["geometry"] is JsonObject shape)
            {
                using JsonDocument parsed = JsonDocument.Parse(shape.ToJsonString());

                if (!ArcGisGeometryReader.TryReadForEdit(parsed.RootElement, collection.Srid, out Geometry? geometry, out int? other, out string? bad))
                {
                    why = $"Feature {index} of {parameter}: {bad}";
                    return false;
                }

                if (other is not null)
                {
                    why = $"Feature {index} of {parameter} is in {other}, and the collection is in {collection.Srid}: one reference per collection.";
                    return false;
                }

                wkb = WkbWriter.ToArray(geometry!);
            }

            read.Add(new CollectionRow(values, wkb));
        }

        return true;
    }

    private static bool TryValue(JsonNode? node, FieldType type, out object? value)
    {
        value = null;

        if (node is null || node.GetValueKind() == JsonValueKind.Null)
        {
            return true;
        }

        try
        {
            JsonValueKind kind = node.GetValueKind();
            value = type switch
            {
                FieldType.SmallInteger => (short)Number(node),
                FieldType.Integer => (int)Number(node),
                FieldType.BigInteger => (long)Number(node),
                FieldType.Single => (float)Number(node),
                FieldType.Double => Number(node),
                FieldType.Date => kind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)Number(node))
                    : DateTimeOffset.Parse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
                FieldType.Guid => Guid.Parse(node.GetValue<string>()),
                _ => kind == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString(),
            };

            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException)
        {
            return false;
        }
    }

    private static double Number(JsonNode node) =>
        node.GetValueKind() == JsonValueKind.String
            ? double.Parse(node.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : node.GetValue<double>();

    /// <summary>
    /// The collection as a layer only the planner and the runner see: its kind, and an id column named as the run's table
    /// names it.
    /// </summary>
    /// <param name="collection">The collection.</param>
    /// <param name="table">The table it is, or will be, written to.</param>
    /// <returns>The layer.</returns>
    public static PublishedLayer LayerOf(Collection collection, string table)
    {
        ArgumentNullException.ThrowIfNull(collection);

        return new PublishedLayer(
            Guid.Empty,
            new LayerDefinition(table, PostGisImporter.HostedSchema, table, "geom", collection.Srid, "objectid", "objectid", isHosted: true),
            "collection",
            "collection",
            collection.Kind,
            null,
            SharingScope.Private,
            ServiceStatus.Started);
    }

    /// <summary>A run's table, read back as the feature collection a result without a name is returned as.</summary>
    /// <param name="read">What the run wrote.</param>
    /// <param name="kind">The result's kind of geometry, or null for a table — a grouped summary's groups.</param>
    /// <returns><c>{layerDefinition, featureSet}</c>.</returns>
    public static JsonObject Write(AnalysisRows read, GeometryKind? kind)
    {
        ArgumentNullException.ThrowIfNull(read);
        string? geometryType = kind is { } shaped ? ArcGisGeometryWriter.TypeName(shaped) : null;
        int wkid = read.Srid;

        JsonArray fields = [];

        foreach ((string name, string type) in read.Columns)
        {
            fields.Add(new JsonObject
            {
                ["name"] = name,
                ["type"] = name == "objectid" ? "esriFieldTypeOID" : EsriType(type),
                ["alias"] = name,
            });
        }

        JsonArray features = [];

        foreach (CollectionRow row in read.Rows)
        {
            JsonObject attributes = [];

            for (int i = 0; i < read.Columns.Count; i++)
            {
                attributes[read.Columns[i].Name] = Value(row.Values[i]);
            }

            JsonObject feature = new() { ["attributes"] = attributes };

            if (row.Wkb is { } wkb)
            {
                ArrayBufferWriter<byte> buffer = new();

                using (Utf8JsonWriter writer = new(buffer))
                {
                    ArcGisGeometryWriter.Write(writer, WkbReader.Read(wkb), wkid);
                }

                feature["geometry"] = JsonNode.Parse(buffer.WrittenSpan);
            }

            features.Add(feature);
        }

        JsonObject definition = new() { ["objectIdField"] = "objectid", ["fields"] = fields };
        JsonObject set = new() { ["features"] = features };

        // A table carries no geometry type and no reference, as ArcGIS's table collections do not.
        if (geometryType is not null)
        {
            definition["geometryType"] = geometryType;
            definition["spatialReference"] = new JsonObject { ["wkid"] = wkid };
            set["geometryType"] = geometryType;
            set["spatialReference"] = new JsonObject { ["wkid"] = wkid };
        }
        else
        {
            definition["type"] = "Table";
        }

        return new JsonObject { ["layerDefinition"] = definition, ["featureSet"] = set };
    }

    /// <summary>A PostgreSQL type as ArcGIS names a field; a 64-bit integer as a string, as every face here writes one.</summary>
    private static string EsriType(string type) => type switch
    {
        "smallint" or "boolean" => "esriFieldTypeSmallInteger",
        "integer" => "esriFieldTypeInteger",
        "real" => "esriFieldTypeSingle",
        "double precision" => "esriFieldTypeDouble",
        _ when type.StartsWith("numeric", StringComparison.Ordinal) => "esriFieldTypeDouble",
        _ when type.StartsWith("timestamp", StringComparison.Ordinal) || type == "date" => "esriFieldTypeDate",
        "uuid" => "esriFieldTypeGUID",
        _ => "esriFieldTypeString",
    };

    private static JsonNode? Value(object? value) => value switch
    {
        null => null,
        bool b => b ? 1 : 0,
        short s => s,
        int i => i,
        long l => l.ToString(CultureInfo.InvariantCulture),
        float f => f,
        double d => d,
        decimal m => (double)m,
        DateTime t => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        DateTimeOffset o => o.ToUnixTimeMilliseconds(),
        Guid g => "{" + g.ToString().ToUpperInvariant() + "}",
        _ => value.ToString(),
    };
}
