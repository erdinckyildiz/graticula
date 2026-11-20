using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Formats;

/// <summary>What a caller may not exceed when handing us a file.</summary>
/// <param name="Features">How many features.</param>
/// <param name="Vertices">How many coordinates in total.</param>
/// <param name="Attributes">How many distinct property names.</param>
/// <remarks>
/// <b>Every one of these is a bound on a single pass.</b> Parsing is linear in
/// the bytes, so these caps bound the work exactly — the distinction A-042 got
/// wrong for overlay and which holds here for the same reason it holds for
/// GeometryServer's linear operations.
/// </remarks>
public readonly record struct ImportLimits(int Features, long Vertices, int Attributes)
{
    /// <summary>
    /// Defaults, generous enough that ordinary work never meets them.
    /// </summary>
    /// <remarks>
    /// A million features is larger than anything a person uploads through a
    /// browser; the attribute cap is what stops a file whose every feature
    /// invents new property names from producing a table with fifty thousand
    /// columns, which PostgreSQL refuses at 1,600 anyway and less politely.
    /// </remarks>
    public static ImportLimits Default => new(1_000_000, 50_000_000, 250);
}

/// <summary>How a column's values behave across the whole file.</summary>
/// <remarks>
/// <b>Inferred from every feature, not from the first.</b> A property that holds
/// integers for nine hundred rows and the string "n/a" for the nine hundred and
/// first is a text column. Deciding from a sample produces a table that rejects
/// its own data partway through the load, which is the worst moment to find out.
/// </remarks>
public sealed class InferredColumn
{
    private bool _sawText;
    private bool _sawFractional;
    private bool _sawInteger;
    private bool _sawBoolean;
    private bool _tooBigForInt;
    private bool _allTextIsOgrDate = true;
    private bool _declaredDate;

    /// <summary>The property name as it appeared in the file.</summary>
    public required string Name { get; init; }

    /// <summary>Whether any feature omitted it or set it null.</summary>
    public bool Nullable { get; private set; }

    /// <summary>Longest text seen, for sizing.</summary>
    public int LongestText { get; private set; }

    /// <summary>The type that holds every value seen.</summary>
    public FieldType Type
    {
        get
        {
            // A date the source declared, kept a date when every value it held reads as one (DeclareDates).
            if (_declaredDate && _allTextIsOgrDate && !_sawBoolean && !_sawInteger && !_sawFractional)
            {
                return FieldType.Date;
            }

            if (_sawText)
            {
                return FieldType.Text;
            }

            // A column holding both true and 1 is not a boolean column; the two
            // are different values and collapsing them invents a meaning.
            if (_sawBoolean)
            {
                return _sawInteger || _sawFractional ? FieldType.Text : FieldType.Boolean;
            }

            if (_sawFractional)
            {
                return FieldType.Double;
            }

            return _tooBigForInt ? FieldType.BigInteger : FieldType.Integer;
        }
    }

    /// <summary>Says the source declared this column a date (<see cref="DeclaredDates"/>).</summary>
    public void DeclareDate() => _declaredDate = true;

    /// <summary>Folds one value into what is known about the column.</summary>
    /// <param name="value">The value.</param>
    public void Observe(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null or JsonValueKind.Undefined:
                Nullable = true;
                break;

            case JsonValueKind.True or JsonValueKind.False:
                _sawBoolean = true;
                break;

            case JsonValueKind.Number:
                if (value.TryGetInt32(out _))
                {
                    _sawInteger = true;
                }
                else if (value.TryGetInt64(out _))
                {
                    _sawInteger = true;
                    _tooBigForInt = true;
                }
                else
                {
                    _sawFractional = true;
                }

                break;

            case JsonValueKind.String:
                _sawText = true;
                LongestText = Math.Max(LongestText, value.GetString()?.Length ?? 0);

                if (_allTextIsOgrDate && !OgrDateText.TryParse(value.GetString(), out _))
                {
                    _allTextIsOgrDate = false;
                }

                break;

            default:
                // An object or array in a property. Kept as its JSON text rather
                // than dropped: the alternative is silently losing data the
                // caller believes they uploaded.
                _sawText = true;
                _allTextIsOgrDate = false;
                LongestText = Math.Max(LongestText, value.GetRawText().Length);
                break;
        }
    }
}

/// <summary>Whether the source said this column holds dates — the import reader's header, a field typed <c>Date</c> or <c>DateTime</c>.</summary>
public static class DeclaredDates
{
    /// <summary>
    /// Marks every column the import reader's header types <c>Date</c> or <c>DateTime</c>, so it is created as a date
    /// when every value it held reads as one, and as text otherwise.
    /// </summary>
    /// <param name="header">The reader's header line: <c>{ "fields": [ { "name", "type" } ] }</c>.</param>
    /// <param name="columns">The columns inferred from the rows, by name.</param>
    /// <remarks>
    /// <b>Found 2026-11-20 by ArcGIS Pro (ADR-083 condition 2's check):</b> a file geodatabase date field Pro published
    /// landed as a text column, because the reader hands every value it does not type as OGR's text
    /// (<c>2026/11/20 08:48:03+00</c>) and the columns were inferred from the values alone; ArcGIS Map Viewer then
    /// offered string operators for it. A shapefile's <c>Date</c> field came in the same way.
    /// </remarks>
    public static void DeclareDates(JsonElement header, IReadOnlyDictionary<string, InferredColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (header.ValueKind != JsonValueKind.Object
            || !header.TryGetProperty("fields", out JsonElement fields)
            || fields.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement field in fields.EnumerateArray())
        {
            if (field.ValueKind == JsonValueKind.Object
                && field.TryGetProperty("name", out JsonElement name) && name.GetString() is { } named
                && field.TryGetProperty("type", out JsonElement type) && type.GetString() is "Date" or "DateTime"
                && columns.TryGetValue(named, out InferredColumn? column))
            {
                column.DeclareDate();
            }
        }
    }
}

/// <summary>
/// OGR's text form of a date — <c>YYYY/MM/DD</c>, then optionally <c> HH:MM:SS[.fff]</c>, then optionally a zone
/// <c>+HH</c>, <c>+HHMM</c> or <c>+HH:MM</c> — as an instant. A value with no zone is read as UTC, the reference every
/// layer document states (ADR-077 §10, <c>StoredTime.Utc</c>).
/// </summary>
public static class OgrDateText
{
    /// <summary>Reads <paramref name="text"/> as OGR writes a date, or says it is not one.</summary>
    /// <param name="text">The text.</param>
    /// <param name="value">The instant.</param>
    /// <returns>Whether it was one.</returns>
    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;

        if (text is not { Length: >= 10 } || text[4] != '/' || text[7] != '/')
        {
            return false;
        }

        string body = text;
        TimeSpan offset = TimeSpan.Zero;
        int zone = text.Length > 10 ? text.IndexOfAny(['+', '-'], 10) : -1;

        if (zone > 0)
        {
            string z = text[(zone + 1)..].Replace(":", string.Empty, StringComparison.Ordinal);

            if (z.Length is not (2 or 4) || !int.TryParse(z.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours))
            {
                return false;
            }

            int minutes = 0;

            if (z.Length == 4 && !int.TryParse(z.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out minutes))
            {
                return false;
            }

            offset = new TimeSpan(hours, minutes, 0);
            offset = text[zone] == '-' ? -offset : offset;
            body = text[..zone];
        }

        string[] formats = ["yyyy/MM/dd", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss.FFFFFFF"];

        if (!DateTime.TryParseExact(body, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
        {
            return false;
        }

        try
        {
            value = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), offset);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}

/// <summary>One feature read from a file.</summary>
/// <param name="Geometry">Its geometry, or null where the file had none.</param>
/// <param name="Values">Its properties, keyed by name.</param>
public sealed record ImportedFeature(Geometry? Geometry, IReadOnlyDictionary<string, JsonElement> Values);

/// <summary>Everything a file turned out to contain.</summary>
/// <param name="Features">The features.</param>
/// <param name="Columns">The inferred columns, in the order first seen.</param>
/// <param name="GeometryType">The single geometry type the layer will hold.</param>
/// <param name="Srid">The spatial reference the coordinates are in.</param>
public sealed record ImportedDataset(
    IReadOnlyList<ImportedFeature> Features,
    IReadOnlyList<InferredColumn> Columns,
    GeometryKind? GeometryType,
    int Srid);

/// <summary>
/// Reads a GeoJSON <c>FeatureCollection</c> into something publishable.
/// </summary>
/// <remarks>
/// <para>
/// <b>GeoJSON first, and the reason was a security rule rather than a
/// preference.</b>
/// <see href="../../../docs/security.md">security.md</see>'s upload section says
/// <em>no decompression on upload — archives are not opened, inspected or
/// expanded</em>. A shapefile is a ZIP of three to six files, so accepting one
/// meant either breaking that rule or writing an exception to it; GeoJSON is a
/// single document and needs neither, so it shipped first.
/// <b>The exception was taken on 2026-08-15</b> (Q-98,
/// <see cref="BoundedArchive"/>), so shapefile is accepted now — through one
/// class, under five bounds. This one still needs none of them.
/// </para>
/// <para>
/// <b>Written here rather than adopted.</b> GeoJSON is a small, stable, fully
/// specified format and <c>System.Text.Json</c> does the hard part. A library
/// would bring its own geometry types, which the build-vs-adopt policy forbids
/// on a Tier 1 path, and its own opinions about what to do with the cases this
/// file refuses.
/// </para>
/// <para>
/// <b>The coordinates are 4326 by specification.</b> RFC 7946 removed the
/// ability to declare another CRS precisely because the <c>crs</c> member of the
/// old draft was widely ignored and silently wrong. A file carrying one is
/// refused rather than trusted or ignored.
/// </para>
/// </remarks>
public static class GeoJsonFeatures
{
    /// <summary>The only spatial reference RFC 7946 allows.</summary>
    public const int GeoJsonSrid = 4326;

    /// <summary>
    /// Reads a feature collection.
    /// </summary>
    /// <param name="json">The document.</param>
    /// <param name="limits">What the caller may not exceed.</param>
    /// <param name="dataset">The result, on success.</param>
    /// <param name="error">Why not, on failure.</param>
    /// <returns>Whether it was read.</returns>
    public static bool TryRead(
        JsonElement json, ImportLimits limits, out ImportedDataset? dataset, out string? error)
    {
        dataset = null;
        error = null;

        if (!TryFeatureArray(json, out JsonElement features, out error))
        {
            return false;
        }

        List<ImportedFeature> read = [];
        Dictionary<string, InferredColumn> columns = new(StringComparer.Ordinal);
        List<InferredColumn> order = [];
        long vertices = 0;
        GeometryKind? kind = null;
        int index = 0;

        foreach (JsonElement feature in features.EnumerateArray())
        {
            if (read.Count >= limits.Features)
            {
                error = $"The file has more than {limits.Features:N0} features.";
                return false;
            }

            if (!TryGeometry(feature, index, out Geometry? geometry, out error))
            {
                return false;
            }

            if (geometry is not null)
            {
                vertices += geometry.CoordinateCount;

                if (vertices > limits.Vertices)
                {
                    error = $"The file has more than {limits.Vertices:N0} coordinates.";
                    return false;
                }

                if (!TryUnifyKind(kind, geometry.Kind, index, out kind, out error))
                {
                    return false;
                }
            }

            Dictionary<string, JsonElement> values = new(StringComparer.Ordinal);

            if (feature.TryGetProperty("properties", out JsonElement properties)
                && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in properties.EnumerateObject())
                {
                    if (!columns.TryGetValue(property.Name, out InferredColumn? column))
                    {
                        if (columns.Count >= limits.Attributes)
                        {
                            error =
                                $"The file has more than {limits.Attributes:N0} distinct property "
                                + "names. A table cannot have a column for each.";
                            return false;
                        }

                        column = new InferredColumn { Name = property.Name };
                        columns[property.Name] = column;
                        order.Add(column);
                    }

                    column.Observe(property.Value);
                    values[property.Name] = property.Value.Clone();
                }
            }

            read.Add(new ImportedFeature(geometry, values));
            index++;
        }

        if (read.Count == 0)
        {
            error = "The file has no features in it.";
            return false;
        }

        // A property missing from any feature is nullable, whether or not it was
        // ever explicitly null. Declaring NOT NULL from a file where most rows
        // happen to carry a value is how a load fails on row nine hundred.
        foreach (InferredColumn column in order)
        {
            foreach (ImportedFeature feature in read)
            {
                if (!feature.Values.ContainsKey(column.Name))
                {
                    column.Observe(default);
                    break;
                }
            }
        }

        if (kind is null)
        {
            error = "Every feature in the file has null geometry, so there is nothing to publish.";
            return false;
        }

        dataset = new ImportedDataset(read, order, kind.Value, GeoJsonSrid);
        return true;
    }

    /// <summary>Finds the feature array, and refuses a declared CRS.</summary>
    private static bool TryFeatureArray(JsonElement json, out JsonElement features, out string? error)
    {
        features = default;
        error = null;

        if (json.ValueKind != JsonValueKind.Object)
        {
            error = "The upload must be a GeoJSON FeatureCollection object.";
            return false;
        }

        // <b>Refused, not ignored.</b> The 2008 draft's crs member was widely
        // written and widely ignored, so a file carrying one is a file whose
        // author believes their coordinates are in something other than 4326.
        // Ignoring it publishes their data in the wrong place on the map; RFC
        // 7946 removed the member for exactly this reason.
        if (json.TryGetProperty("crs", out JsonElement crs) && crs.ValueKind != JsonValueKind.Null)
        {
            error =
                "This file declares a 'crs' member. RFC 7946 removed it and requires WGS 84 "
                + "longitude and latitude, so the declaration cannot be honoured — and ignoring "
                + "it would publish the data somewhere it is not. Reproject the file to EPSG:4326 "
                + "before uploading.";
            return false;
        }

        if (!json.TryGetProperty("type", out JsonElement type)
            || type.GetString() != "FeatureCollection")
        {
            error =
                "The upload must be a FeatureCollection. A bare Feature or geometry is not enough "
                + "to make a layer from.";
            return false;
        }

        if (!json.TryGetProperty("features", out features)
            || features.ValueKind != JsonValueKind.Array)
        {
            error = "The FeatureCollection has no 'features' array.";
            return false;
        }

        return true;
    }

    private static bool TryGeometry(
        JsonElement feature, int index, out Geometry? geometry, out string? error)
    {
        geometry = null;
        error = null;

        if (!feature.TryGetProperty("geometry", out JsonElement json)
            || json.ValueKind == JsonValueKind.Null)
        {
            // Allowed by the specification and kept: a feature with attributes
            // and no location is data somebody may want, and refusing the whole
            // file over one is disproportionate.
            return true;
        }

        // ADR-080: an elevation is kept; the importer decides what the column declares.
        return GeoJsonGeometry.TryRead(json, index, keepZ: true, out geometry, out error);
    }

    /// <summary>
    /// Decides the one geometry type the layer will hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A layer holds one type, and GeoJSON does not have to.</b> A file mixing
    /// <c>Polygon</c> and <c>MultiPolygon</c> is ordinary and becomes a
    /// multi-polygon layer, which loses nothing. A file mixing points and
    /// polygons is refused: there is no type that holds both without becoming a
    /// geometry collection, which no ArcGIS client can draw.
    /// </para>
    /// <para>
    /// Promoting rather than refusing matters because exporters produce mixed
    /// singular and plural constantly — one island is a Polygon, two are a
    /// MultiPolygon, in the same file from the same source.
    /// </para>
    /// </remarks>
    private static bool TryUnifyKind(
        GeometryKind? sofar, GeometryKind next, int index, out GeometryKind? unified, out string? error)
    {
        error = null;
        unified = sofar;

        if (sofar is null)
        {
            unified = next;
            return true;
        }

        if (sofar == next)
        {
            return true;
        }

        (GeometryKind Single, GeometryKind Multi)[] families =
        [
            (GeometryKind.Point, GeometryKind.MultiPoint),
            (GeometryKind.LineString, GeometryKind.MultiLineString),
            (GeometryKind.Polygon, GeometryKind.MultiPolygon),
        ];

        foreach ((GeometryKind single, GeometryKind multi) in families)
        {
            bool a = sofar == single || sofar == multi;
            bool b = next == single || next == multi;

            if (a && b)
            {
                unified = multi;
                return true;
            }
        }

        error =
            $"Feature {index} is a {next} and earlier features are {sofar}. A layer holds one "
            + "geometry type, and points and areas have no common type an ArcGIS client can draw. "
            + "Split the file.";
        return false;
    }
}
