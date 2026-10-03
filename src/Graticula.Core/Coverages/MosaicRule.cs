using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Graticula.Geometries;

namespace Graticula.Coverages;

/// <summary>How a mosaic orders its images where they overlap — ArcGIS's <c>mosaicMethod</c>, ADR-152.</summary>
public enum MosaicMethod
{
    /// <summary>By object id.</summary>
    None = 0,

    /// <summary>Only the images named, in the order named.</summary>
    LockRaster = 1,

    /// <summary>Nearest the view's north-west corner first.</summary>
    Northwest = 2,

    /// <summary>Nearest the view's centre first.</summary>
    Center = 3,

    /// <summary>Nearest the view's centre first — this server's images are all taken looking straight down.</summary>
    Nadir = 4,

    /// <summary>Nearest a point first.</summary>
    Viewpoint = 5,

    /// <summary>Nearest a value of a field first.</summary>
    Attribute = 6,
}

/// <summary>
/// What becomes of a pixel several images cover — ArcGIS's <c>mosaicOperation</c>, ADR-152 and ADR-158: the one on top,
/// or the pixels combined.
/// </summary>
public enum MosaicOperation
{
    /// <summary>The image on top, which the order chooses — MT_FIRST and MT_LAST.</summary>
    Top = 0,

    /// <summary>The smallest value — MT_MIN.</summary>
    Minimum = 1,

    /// <summary>The largest value — MT_MAX.</summary>
    Maximum = 2,

    /// <summary>The mean of the values — MT_MEAN.</summary>
    Mean = 3,

    /// <summary>A mean weighted toward each image's interior, so seams fade — MT_BLEND.</summary>
    Blend = 4,

    /// <summary>The sum of the values — MT_SUM.</summary>
    Sum = 5,
}

/// <summary>One image of a mosaic as its catalog lists it — ADR-152.</summary>
/// <param name="Id">Its object id, which stays its own when others are added and removed.</param>
/// <param name="Name">What it is called: the file it came from.</param>
/// <param name="Extent">Where it lies, in the mosaic's reference.</param>
/// <param name="PixelSize">Its cell size, in the mosaic's units.</param>
/// <param name="Acquired">When it was taken, if anyone said (ADR-153).</param>
/// <param name="Position">Where it is in the mosaic's own order, from zero: later is drawn over earlier.</param>
/// <param name="Variable">The variable it is a slice of, in a multidimensional service (ADR-159).</param>
/// <param name="Dimensions">Its values along the dimensions other than time — a depth, a pressure level (ADR-159).</param>
public sealed record CatalogImage(
    int Id,
    string Name,
    Envelope Extent,
    double PixelSize,
    DateTimeOffset? Acquired,
    int Position,
    string? Variable = null,
    IReadOnlyDictionary<string, double>? Dimensions = null);

/// <summary>
/// One entry of ArcGIS's <c>multidimensionalDefinition</c>: a variable, a dimension, and the values or ranges of it
/// wanted — ADR-159. A missing variable applies to every variable; a missing dimension chooses only the variable.
/// </summary>
/// <param name="Variable">The variable's name, or null.</param>
/// <param name="Dimension">The dimension's name — <c>StdTime</c> for time — or null.</param>
/// <param name="Values">The values wanted, each a range; one value is a range from itself to itself.</param>
public sealed record DimensionSlice(string? Variable, string? Dimension, IReadOnlyList<(double From, double To)> Values)
{
    /// <summary>The name time goes by in a definition, as ArcGIS names it.</summary>
    public const string Time = "StdTime";

    /// <summary>ArcGIS's name for a vertical dimension, read as a service's one dimension besides time.</summary>
    public const string Height = "StdZ";

    /// <summary>Whether a value is one of those wanted: within a range, or equal to a value up to rounding.</summary>
    /// <param name="value">The image's value.</param>
    /// <returns>Whether it is wanted.</returns>
    public bool Wants(double value) =>
        Values.Any(v => value >= v.From - Tolerance(v.From) && value <= v.To + Tolerance(v.To));

    private static double Tolerance(double v) => Math.Max(1, Math.Abs(v)) * 1e-9;

    /// <summary>
    /// The images a multidimensional service draws for a definition — ADR-159. The variables named, or the first; for
    /// every dimension other than time, the values named, or its first; for time, the instants named, or every one —
    /// left to <c>time</c> and to the drawing order, which puts the latest on top, as ADR-153 drew a series before.
    /// </summary>
    /// <param name="images">The catalog, in its order.</param>
    /// <param name="definition">The request's definition, or null.</param>
    /// <param name="error">Why the definition was refused: a variable or dimension the service does not have.</param>
    /// <returns>The images kept, in the catalog's order.</returns>
    public static List<CatalogImage> Slice(
        IReadOnlyList<CatalogImage> images, IReadOnlyList<DimensionSlice>? definition, out string? error)
    {
        ArgumentNullException.ThrowIfNull(images);
        error = null;
        definition ??= [];

        List<string> variables = [.. images.Where(i => i.Variable is not null).Select(i => i.Variable!).Distinct(StringComparer.Ordinal)];
        HashSet<string> named = [.. definition.Where(d => d.Variable is not null).Select(d => d.Variable!)];

        foreach (string wanted in named)
        {
            if (!variables.Contains(wanted, StringComparer.Ordinal))
            {
                error = $"`multidimensionalDefinition` names the variable '{wanted}', and this image service has "
                    + $"{string.Join(", ", variables)}.";
                return [];
            }
        }

        HashSet<string> chosen = named.Count > 0 ? named : variables.Count > 0 ? [variables[0]] : [];
        List<CatalogImage> kept = [.. images.Where(i => i.Variable is null || chosen.Contains(i.Variable))];
        List<string> dimensionNames = [.. kept.SelectMany(i => i.Dimensions?.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.Ordinal)];

        // ArcGIS calls a vertical dimension StdZ; where there is one dimension besides time, StdZ is it.
        if (dimensionNames.Count == 1 && !dimensionNames.Contains(Height, StringComparer.Ordinal))
        {
            definition = [.. definition.Select(d => d.Dimension == Height ? d with { Dimension = dimensionNames[0] } : d)];
        }

        foreach (DimensionSlice slice in definition.Where(d => d.Dimension is not null))
        {
            if (slice.Dimension != Time && !dimensionNames.Contains(slice.Dimension!, StringComparer.Ordinal))
            {
                error = $"`multidimensionalDefinition` names the dimension '{slice.Dimension}', and this image service's "
                    + $"{string.Join(", ", chosen)} has {string.Join(", ", [Time, .. dimensionNames])}.";
                return [];
            }
        }

        foreach (string variable in chosen)
        {
            bool Mine(CatalogImage i) => i.Variable == variable;
            List<DimensionSlice> own = [.. definition.Where(d => d.Dimension is not null && (d.Variable is null || d.Variable == variable))];

            foreach (string dimension in dimensionNames)
            {
                DimensionSlice? given = own.FirstOrDefault(d => d.Dimension == dimension);

                // A dimension the definition leaves out is held at its first value, so a pixel has one answer.
                double? first = kept.Where(Mine)
                    .Select(i => i.Dimensions is { } d && d.TryGetValue(dimension, out double v) ? v : (double?)null)
                    .FirstOrDefault(v => v is not null);
                kept = [.. kept.Where(i => !Mine(i) || i.Dimensions is not { } d || !d.TryGetValue(dimension, out double value)
                    || (given is not null ? given.Wants(value) : value == first))];
            }

            DimensionSlice? time = own.FirstOrDefault(d => d.Dimension == Time);

            if (time is not null)
            {
                kept = [.. kept.Where(i => !Mine(i) || (i.Acquired is { } at && time.Wants(at.ToUnixTimeMilliseconds())))];
            }
        }

        return kept;
    }
}

/// <summary>
/// ArcGIS's <c>mosaicRule</c>: which of a mosaic's images are drawn and which is on top where they overlap — ADR-152.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering, and since ADR-158 combining.</b> <c>MT_FIRST</c> (the first in the order on top) and <c>MT_LAST</c>
/// take one image's pixel; <c>MT_MIN</c>, <c>MT_MAX</c>, <c>MT_MEAN</c>, <c>MT_BLEND</c> and <c>MT_SUM</c> combine
/// overlapping pixels. The seamline method is refused by name: it needs seamlines this server does not make.
/// </para>
/// <para>
/// <b><c>multidimensionalDefinition</c></b> chooses a multidimensional service's variables and slices — ADR-159,
/// <see cref="DimensionSlice.Slice"/>.
/// </para>
/// <para>
/// <b><c>where</c> is read here and evaluated by the caller</b>, against the catalog's fields, by the same parser every
/// other <c>where</c> on this server goes through.
/// </para>
/// </remarks>
public sealed record MosaicRule
{
    /// <summary>The catalog's fields a rule may sort by or filter on.</summary>
    public static IReadOnlyList<string> Fields { get; } = ["OBJECTID", "Name", "AcquisitionDate", "ZOrder", "LowPS", "CenterX", "CenterY"];

    /// <summary>How images are ordered.</summary>
    public MosaicMethod Method { get; init; }

    /// <summary>The images a lock names, in its order.</summary>
    public IReadOnlyList<int>? LockRasterIds { get; init; }

    /// <summary>The images the rule is limited to.</summary>
    public IReadOnlyList<int>? Fids { get; init; }

    /// <summary>A <c>where</c> over the catalog's fields, evaluated by the caller.</summary>
    public string? Where { get; init; }

    /// <summary>The field an attribute order sorts by.</summary>
    public string? SortField { get; init; }

    /// <summary>The value an attribute order measures nearness to.</summary>
    public string? SortValue { get; init; }

    /// <summary>Whether an order runs from nearest (true) or from farthest.</summary>
    public bool Ascending { get; init; } = true;

    /// <summary>Whether the first in the order is on top (<c>MT_FIRST</c>) rather than the last.</summary>
    public bool FirstOnTop { get; init; } = true;

    /// <summary>What becomes of a pixel several images cover — ADR-158.</summary>
    public MosaicOperation Operation { get; init; }

    /// <summary>A viewpoint's position, for <see cref="MosaicMethod.Viewpoint"/>.</summary>
    public (double X, double Y)? Viewpoint { get; init; }

    /// <summary>The variables and dimension values a multidimensional service draws — ADR-159 — or null.</summary>
    public IReadOnlyList<DimensionSlice>? Multidimensional { get; init; }

    /// <summary>Reads a <c>mosaicRule</c>; empty and <c>{}</c> are no rule.</summary>
    /// <param name="json">The parameter.</param>
    /// <param name="rule">The rule, or null for none.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it is one this server applies.</returns>
    public static bool TryParse(string? json, out MosaicRule? rule, out string? error)
    {
        rule = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json) || json.Replace(" ", string.Empty, StringComparison.Ordinal) == "{}")
        {
            return true;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "`mosaicRule` is a JSON object naming a `mosaicMethod`.";
                return false;
            }

            string method = Text(root, "mosaicMethod") ?? "esriMosaicNone";
            MosaicMethod? known = method.ToUpperInvariant() switch
            {
                "ESRIMOSAICNONE" => MosaicMethod.None,
                "ESRIMOSAICLOCKRASTER" => MosaicMethod.LockRaster,
                "ESRIMOSAICNORTHWEST" => MosaicMethod.Northwest,
                "ESRIMOSAICCENTER" => MosaicMethod.Center,
                "ESRIMOSAICNADIR" => MosaicMethod.Nadir,
                "ESRIMOSAICVIEWPOINT" => MosaicMethod.Viewpoint,
                "ESRIMOSAICATTRIBUTE" => MosaicMethod.Attribute,
                _ => null,
            };

            if (known is null)
            {
                error = $"`mosaicRule`'s mosaicMethod '{method}' is not one this server orders by: it orders by "
                    + "esriMosaicNone, esriMosaicLockRaster, esriMosaicNorthwest, esriMosaicCenter, esriMosaicNadir, "
                    + "esriMosaicViewpoint and esriMosaicAttribute. A seamline needs seamlines this server does not make.";
                return false;
            }

            string operation = Text(root, "mosaicOperation") ?? "MT_FIRST";
            bool first = !operation.Equals("MT_LAST", StringComparison.OrdinalIgnoreCase);

            // ADR-158: the pixels several images cover may be combined rather than one taken.
            MosaicOperation? combined = operation.ToUpperInvariant() switch
            {
                "MT_FIRST" or "MT_LAST" => MosaicOperation.Top,
                "MT_MIN" => MosaicOperation.Minimum,
                "MT_MAX" => MosaicOperation.Maximum,
                "MT_MEAN" => MosaicOperation.Mean,
                "MT_BLEND" => MosaicOperation.Blend,
                "MT_SUM" => MosaicOperation.Sum,
                _ => null,
            };

            if (combined is null)
            {
                error = $"`mosaicRule`'s mosaicOperation '{operation}' is not one this server applies: it applies MT_FIRST, "
                    + "MT_LAST, MT_MIN, MT_MAX, MT_MEAN, MT_BLEND and MT_SUM.";
                return false;
            }

            List<DimensionSlice>? slices = null;

            if (root.TryGetProperty("multidimensionalDefinition", out JsonElement dimensions)
                && dimensions.ValueKind == JsonValueKind.Array && dimensions.GetArrayLength() > 0
                && !TrySlices(dimensions, out slices, out error))
            {
                return false;
            }

            List<int>? locked = Ids(root, "lockRasterIds");

            if (known == MosaicMethod.LockRaster && (locked is null || locked.Count == 0))
            {
                error = "esriMosaicLockRaster draws the images named in `lockRasterIds`, and names none.";
                return false;
            }

            string? sortField = Text(root, "sortField");

            if (known == MosaicMethod.Attribute)
            {
                if (sortField is null || !Fields.Contains(sortField, StringComparer.OrdinalIgnoreCase))
                {
                    error = $"esriMosaicAttribute sorts by a field of the catalog: {string.Join(", ", Fields)}.";
                    return false;
                }
            }

            (double, double)? viewpoint = root.TryGetProperty("viewpoint", out JsonElement point) && point.ValueKind == JsonValueKind.Object
                && point.TryGetProperty("x", out JsonElement x) && x.TryGetDouble(out double vx)
                && point.TryGetProperty("y", out JsonElement y) && y.TryGetDouble(out double vy)
                ? (vx, vy) : null;

            rule = new MosaicRule
            {
                Method = known.Value,
                LockRasterIds = locked,
                Fids = Ids(root, "fids"),
                Where = Text(root, "where") is { Length: > 0 } where && where.Trim() != "1=1" ? where : null,
                SortField = sortField,
                SortValue = root.TryGetProperty("sortValue", out JsonElement value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number
                    ? value.ToString() : null,
                Ascending = !root.TryGetProperty("ascending", out JsonElement ascending) || ascending.ValueKind != JsonValueKind.False,
                FirstOnTop = first,
                Operation = combined.Value,
                Viewpoint = viewpoint,
                Multidimensional = slices is { Count: > 0 } ? slices : null,
            };

            return true;
        }
        catch (JsonException)
        {
            error = "`mosaicRule` is not JSON.";
            return false;
        }
    }

    /// <summary>
    /// The images drawn, in the order they are drawn — the last over the others — for a view of the mosaic.
    /// </summary>
    /// <param name="images">The catalog, after any <c>where</c>.</param>
    /// <param name="view">The extent drawn, in the mosaic's reference.</param>
    /// <returns>Their positions in the mosaic, in drawing order; empty when the rule leaves none.</returns>
    public IReadOnlyList<int> DrawingOrder(IReadOnlyList<CatalogImage> images, Envelope view)
    {
        ArgumentNullException.ThrowIfNull(images);

        IEnumerable<CatalogImage> kept = images;

        if (Fids is { Count: > 0 } fids)
        {
            kept = kept.Where(i => fids.Contains(i.Id));
        }

        List<CatalogImage> ordered;

        if (Method == MosaicMethod.LockRaster)
        {
            Dictionary<int, CatalogImage> byId = kept.ToDictionary(i => i.Id);
            ordered = [.. LockRasterIds!.Where(byId.ContainsKey).Select(id => byId[id])];
        }
        else
        {
            (double X, double Y) target = Method switch
            {
                MosaicMethod.Northwest => (view.MinX, view.MaxY),
                MosaicMethod.Viewpoint when Viewpoint is { } p => (p.X, p.Y),
                _ => ((view.MinX + view.MaxX) / 2, (view.MinY + view.MaxY) / 2),
            };

            Func<CatalogImage, double> key = Method switch
            {
                MosaicMethod.Northwest or MosaicMethod.Center or MosaicMethod.Nadir or MosaicMethod.Viewpoint => i =>
                    Math.Pow(((i.Extent.MinX + i.Extent.MaxX) / 2) - target.X, 2) + Math.Pow(((i.Extent.MinY + i.Extent.MaxY) / 2) - target.Y, 2),
                MosaicMethod.Attribute => i => Nearness(i),
                _ => i => i.Id,
            };

            ordered = [.. kept.OrderBy(key).ThenBy(i => i.Id)];

            if (!Ascending && Method is MosaicMethod.Attribute or MosaicMethod.None)
            {
                ordered.Reverse();
            }
        }

        // The first in the order is on top under MT_FIRST, so it is drawn last.
        if (FirstOnTop)
        {
            ordered.Reverse();
        }

        return [.. ordered.Select(i => i.Position)];
    }

    private double Nearness(CatalogImage image)
    {
        string field = SortField!;

        if (field.Equals("AcquisitionDate", StringComparison.OrdinalIgnoreCase))
        {
            double at = image.Acquired is { } when ? when.ToUnixTimeMilliseconds() : double.MaxValue / 4;
            return SortValue is null ? at : Math.Abs(at - DateValue(SortValue));
        }

        if (field.Equals("Name", StringComparison.OrdinalIgnoreCase))
        {
            // A name has no distance: equal first, then alphabetical.
            return SortValue is not null && image.Name.Equals(SortValue, StringComparison.OrdinalIgnoreCase) ? -1 : 0;
        }

        double own = field.ToUpperInvariant() switch
        {
            "LOWPS" => image.PixelSize,
            "CENTERX" => (image.Extent.MinX + image.Extent.MaxX) / 2,
            "CENTERY" => (image.Extent.MinY + image.Extent.MaxY) / 2,
            "ZORDER" => image.Position,
            _ => image.Id,
        };

        return SortValue is not null && double.TryParse(SortValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double wanted)
            ? Math.Abs(own - wanted)
            : own;
    }

    /// <summary>A date as ArcGIS sends a sort value: epoch milliseconds, or text such as 2024/06/01.</summary>
    private static double DateValue(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double epoch)
            ? epoch
            : DateTimeOffset.TryParse(text.Replace('/', '-'), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset when)
                ? when.ToUnixTimeMilliseconds()
                : 0;

    /// <summary>
    /// ArcGIS's <c>multidimensionalDefinition</c>: objects naming a <c>variableName</c>, a <c>dimensionName</c> and its
    /// <c>values</c> — numbers, or two-number arrays for ranges; time in milliseconds since 1970 — ADR-159.
    /// </summary>
    private static bool TrySlices(JsonElement list, out List<DimensionSlice>? slices, out string? error)
    {
        slices = [];
        error = null;

        foreach (JsonElement item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = "`multidimensionalDefinition` is a list of objects, each naming a variableName, a dimensionName and values.";
                return false;
            }

            List<(double, double)> values = [];

            if (item.TryGetProperty("values", out JsonElement given) && given.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement value in given.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.Number)
                    {
                        values.Add((value.GetDouble(), value.GetDouble()));
                    }
                    else if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 2
                        && value[0].ValueKind == JsonValueKind.Number && value[1].ValueKind == JsonValueKind.Number)
                    {
                        double a = value[0].GetDouble(), b = value[1].GetDouble();
                        values.Add((Math.Min(a, b), Math.Max(a, b)));
                    }
                    else
                    {
                        error = $"`multidimensionalDefinition`'s values are numbers, or [from, to] for a range; {value} is neither.";
                        return false;
                    }
                }
            }

            string? dimension = Text(item, "dimensionName") is { Length: > 0 } d ? d : null;

            if (dimension is not null && values.Count == 0)
            {
                error = $"`multidimensionalDefinition` names the dimension '{dimension}' and no values of it.";
                return false;
            }

            slices.Add(new DimensionSlice(Text(item, "variableName") is { Length: > 0 } v ? v : null, dimension, values));
        }

        return true;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<int>? Ids(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<int> ids = [];

        foreach (JsonElement id in list.EnumerateArray())
        {
            if (id.TryGetInt32(out int value))
            {
                ids.Add(value);
            }
        }

        return ids;
    }
}
