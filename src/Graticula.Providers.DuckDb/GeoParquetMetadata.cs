using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Graticula.Geometries;

namespace Graticula.Providers.DuckDb;

/// <summary>
/// What a GeoParquet file's <c>geo</c> metadata says about its primary geometry column.
/// </summary>
/// <param name="Column">The primary geometry column.</param>
/// <param name="Srid">
/// The EPSG code of its coordinate reference, or null when the file does not name one this server
/// can use — <see cref="Problem"/> then says why.
/// </param>
/// <param name="Kind">
/// The geometry kind every row has, or null when the file lists none or lists several families.
/// </param>
/// <param name="Bbox">The file's own bounding box, when it records one.</param>
/// <param name="Covering">
/// The struct column holding each row's box — GeoParquet 1.1's <c>covering</c> — or null.
/// </param>
/// <param name="Problem">Why a layer cannot be served from this file, or null.</param>
/// <remarks>
/// <para>
/// <b>Read by this server rather than trusted to DuckDB</b>, even though DuckDB reads the same
/// metadata to type the column. The SRID a layer is published with decides every projection it
/// takes part in, and <c>st_crs</c> answers a PROJJSON document or a label such as
/// <c>OGC:CRS84</c>, never a number — so turning that into an EPSG code is ours either way, and
/// reading the specification's own key is the version of it that can be checked against the
/// specification.
/// </para>
/// <para>
/// <b>An absent <c>crs</c> is OGC:CRS84 and a null one is unknown</b>, which is GeoParquet 1.1's
/// own distinction and not a convenience: a file whose writer said <em>I do not know</em> is not
/// published as longitude and latitude on this server's guess.
/// </para>
/// </remarks>
public sealed record GeoParquetMetadata(
    string Column,
    int? Srid,
    GeometryKind? Kind,
    Envelope? Bbox,
    string? Covering,
    string? Problem)
{
    /// <summary>Reads the <c>geo</c> key's JSON.</summary>
    /// <param name="json">The value of the file's <c>geo</c> key-value metadata.</param>
    /// <returns>What it says, or a problem saying why it cannot be used.</returns>
    public static GeoParquetMetadata Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Unusable(
                "The file has no 'geo' metadata, so it is Parquet but not GeoParquet: nothing says "
                + "which column is the geometry or what reference its coordinates are in.");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unusable("The 'geo' metadata is not a JSON object.");
            }

            if (!root.TryGetProperty("primary_column", out JsonElement primary)
                || primary.ValueKind != JsonValueKind.String
                || primary.GetString() is not { Length: > 0 } column)
            {
                return Unusable("The 'geo' metadata names no primary_column.");
            }

            if (!root.TryGetProperty("columns", out JsonElement columns)
                || columns.ValueKind != JsonValueKind.Object
                || !columns.TryGetProperty(column, out JsonElement described)
                || described.ValueKind != JsonValueKind.Object)
            {
                return Unusable($"The 'geo' metadata does not describe its primary column '{column}'.");
            }

            string? encoding = described.TryGetProperty("encoding", out JsonElement e)
                && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;

            if (!string.Equals(encoding, "WKB", StringComparison.OrdinalIgnoreCase))
            {
                return new GeoParquetMetadata(
                    column, null, null, null, null,
                    $"The geometry column '{column}' is encoded as '{encoding}', and this server "
                    + "reads WKB only. GeoArrow's native encodings are a later GeoParquet addition; "
                    + "rewriting the file with WKB encoding serves it.");
            }

            (int? srid, string? crsProblem) = ReferenceOf(described);
            (GeometryKind? kind, string? kindProblem) = KindOf(described);

            return new GeoParquetMetadata(
                column,
                srid,
                kind,
                BboxOf(described),
                CoveringOf(described),
                crsProblem ?? kindProblem);
        }
        catch (JsonException failure)
        {
            return Unusable($"The 'geo' metadata is not valid JSON: {failure.Message}");
        }
        catch (InvalidOperationException)
        {
            // <b>A value of the wrong JSON kind somewhere the specification names another</b> — a
            // number in `geometry_types`, an object where a string belongs. Found by a security
            // review: it escaped as an exception and failed the whole folder with a 500, and a
            // GeoParquet file is often somebody else's data.
            return Unusable("The 'geo' metadata has a value of the wrong kind where GeoParquet names another.");
        }
    }

    private static GeoParquetMetadata Unusable(string problem) =>
        new(string.Empty, null, null, null, null, problem);

    private static (int? Srid, string? Problem) ReferenceOf(JsonElement column)
    {
        if (!column.TryGetProperty("crs", out JsonElement crs))
        {
            // GeoParquet 1.1: "If the crs field is absent, the CRS is OGC:CRS84".
            return (4326, null);
        }

        switch (crs.ValueKind)
        {
            case JsonValueKind.Null:
                return (null,
                    "The file says its coordinate reference is unknown (crs: null). Publishing it "
                    + "as any particular reference would be this server's guess, so it is not "
                    + "published; rewriting the file with its reference named serves it.");

            case JsonValueKind.String:
                return FromLabel(crs.GetString());

            case JsonValueKind.Object:
                if (crs.TryGetProperty("id", out JsonElement id) && FromId(id) is { } direct)
                {
                    return (direct, null);
                }

                if (crs.TryGetProperty("ids", out JsonElement ids)
                    && ids.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement each in ids.EnumerateArray())
                    {
                        if (FromId(each) is { } listed)
                        {
                            return (listed, null);
                        }
                    }
                }

                string name = crs.TryGetProperty("name", out JsonElement n)
                    && n.ValueKind == JsonValueKind.String
                        ? n.GetString() ?? "unnamed"
                        : "unnamed";

                return (null,
                    $"The file's coordinate reference '{name}' carries no EPSG identifier, and "
                    + "every projection this server performs is addressed by one.");

            default:
                return (null, "The file's 'crs' is neither a PROJJSON object nor null.");
        }
    }

    private static (int? Srid, string? Problem) FromLabel(string? label)
    {
        if (label is null)
        {
            return (null, "The file's 'crs' is an empty string.");
        }

        if (label.Equals("OGC:CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return (4326, null);
        }

        const string Prefix = "EPSG:";

        if (label.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(label.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int code)
            && code > 0)
        {
            return (code, null);
        }

        return (null, $"The file's coordinate reference '{label}' is not an EPSG code.");
    }

    private static int? FromId(JsonElement id)
    {
        if (id.ValueKind != JsonValueKind.Object
            || !id.TryGetProperty("authority", out JsonElement authority)
            || authority.ValueKind != JsonValueKind.String
            || !id.TryGetProperty("code", out JsonElement code))
        {
            return null;
        }

        string? text = code.ValueKind switch
        {
            JsonValueKind.Number => code.GetRawText(),
            JsonValueKind.String => code.GetString(),
            _ => null,
        };

        if (string.Equals(authority.GetString(), "OGC", StringComparison.OrdinalIgnoreCase)
            && string.Equals(text, "CRS84", StringComparison.OrdinalIgnoreCase))
        {
            return 4326;
        }

        return string.Equals(authority.GetString(), "EPSG", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int epsg)
            && epsg > 0
                ? epsg
                : null;
    }

    private static (GeometryKind? Kind, string? Problem) KindOf(JsonElement column)
    {
        if (!column.TryGetProperty("geometry_types", out JsonElement types)
            || types.ValueKind != JsonValueKind.Array)
        {
            // Required by the specification, and DuckDB refuses to read the column without it —
            // measured, *does not have geometry types* — so saying so here names the fault in
            // the file's own vocabulary instead of the engine's.
            return (null,
                "The 'geo' metadata has no geometry_types list, which GeoParquet requires; the "
                + "file was written by something that does not follow the specification.");
        }

        if (types.GetArrayLength() == 0)
        {
            // The specification allows an empty list for "any type"; the kind is then read from
            // the rows by whoever needs it.
            return (null, null);
        }

        HashSet<string> family = new(StringComparer.Ordinal);
        bool multi = false;

        foreach (JsonElement type in types.EnumerateArray())
        {
            if (type.ValueKind != JsonValueKind.String)
            {
                return (null, "The file's geometry_types list holds something that is not a type name.");
            }

            string name = (type.GetString() ?? string.Empty).Trim();

            // "Point Z" is a Point with a third ordinate, which WkbReader drops and says so.
            int space = name.IndexOf(' ', StringComparison.Ordinal);

            if (space > 0)
            {
                name = name[..space];
            }

            switch (name)
            {
                case "Point": family.Add("point"); break;
                case "MultiPoint": family.Add("point"); multi = true; break;
                case "LineString": family.Add("line"); break;
                case "MultiLineString": family.Add("line"); multi = true; break;
                case "Polygon": family.Add("polygon"); break;
                case "MultiPolygon": family.Add("polygon"); multi = true; break;
                default:
                    return (null,
                        $"The file declares geometry type '{name}', which a feature layer cannot "
                        + "carry — a layer has one of point, line or polygon.");
            }
        }

        if (family.Count > 1)
        {
            return (null,
                "The file mixes geometry families (" + string.Join(", ", types.EnumerateArray())
                + "), and a feature layer has exactly one. Splitting the file by type serves each.");
        }

        GeometryKind kind = (family.Contains("point"), family.Contains("line"), multi) switch
        {
            (true, _, false) => GeometryKind.Point,
            (true, _, true) => GeometryKind.MultiPoint,
            (_, true, false) => GeometryKind.LineString,
            (_, true, true) => GeometryKind.MultiLineString,
            (_, _, false) => GeometryKind.Polygon,
            _ => GeometryKind.MultiPolygon,
        };

        return (kind, null);
    }

    private static Envelope? BboxOf(JsonElement column)
    {
        if (!column.TryGetProperty("bbox", out JsonElement bbox)
            || bbox.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<double> values = [];

        foreach (JsonElement each in bbox.EnumerateArray())
        {
            if (each.ValueKind != JsonValueKind.Number)
            {
                return null;
            }

            values.Add(each.GetDouble());
        }

        // Two dimensions are four numbers and three are six: [xmin, ymin, zmin, xmax, ymax, zmax].
        return values.Count switch
        {
            4 => new Envelope(values[0], values[1], values[2], values[3]),
            6 => new Envelope(values[0], values[1], values[3], values[4]),
            _ => null,
        };
    }

    /// <summary>
    /// The covering struct, when all four corners are fields of one struct column.
    /// </summary>
    /// <remarks>
    /// The specification allows each corner to name any column path; every writer measured writes
    /// <c>["&lt;col&gt;_bbox", "xmin"]</c> and its siblings, and that is the only shape the filter
    /// is written for. Any other shape is ignored rather than guessed at, which costs row-group
    /// pruning and never an answer — the exact box test runs on the geometry regardless.
    /// </remarks>
    private static string? CoveringOf(JsonElement column)
    {
        if (!column.TryGetProperty("covering", out JsonElement covering)
            || covering.ValueKind != JsonValueKind.Object
            || !covering.TryGetProperty("bbox", out JsonElement bbox)
            || bbox.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? parent = null;

        foreach (string corner in new[] { "xmin", "ymin", "xmax", "ymax" })
        {
            if (!bbox.TryGetProperty(corner, out JsonElement path)
                || path.ValueKind != JsonValueKind.Array
                || path.GetArrayLength() != 2
                || path[0].ValueKind != JsonValueKind.String
                || path[1].ValueKind != JsonValueKind.String
                || path[0].GetString() is not { Length: > 0 } column0
                || !string.Equals(path[1].GetString(), corner, StringComparison.Ordinal)
                || (parent is not null && !string.Equals(parent, column0, StringComparison.Ordinal)))
            {
                return null;
            }

            parent = column0;
        }

        return parent;
    }
}
