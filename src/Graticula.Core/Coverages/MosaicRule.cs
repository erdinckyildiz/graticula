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

/// <summary>One image of a mosaic as its catalog lists it — ADR-152.</summary>
/// <param name="Id">Its object id, which stays its own when others are added and removed.</param>
/// <param name="Name">What it is called: the file it came from.</param>
/// <param name="Extent">Where it lies, in the mosaic's reference.</param>
/// <param name="PixelSize">Its cell size, in the mosaic's units.</param>
/// <param name="Acquired">When it was taken, if anyone said (ADR-153).</param>
/// <param name="Position">Where it is in the mosaic's own order, from zero: later is drawn over earlier.</param>
public sealed record CatalogImage(int Id, string Name, Envelope Extent, double PixelSize, DateTimeOffset? Acquired, int Position);

/// <summary>
/// ArcGIS's <c>mosaicRule</c>: which of a mosaic's images are drawn and which is on top where they overlap — ADR-152.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ordering, not blending.</b> <c>MT_FIRST</c> (the first in the order on top) and <c>MT_LAST</c> are drawn;
/// <c>MT_MIN</c>, <c>MT_MAX</c>, <c>MT_MEAN</c>, <c>MT_BLEND</c> and <c>MT_SUM</c> combine overlapping pixels and are
/// refused by name, as is the seamline method, which needs seamlines this server does not make.
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

    /// <summary>A viewpoint's position, for <see cref="MosaicMethod.Viewpoint"/>.</summary>
    public (double X, double Y)? Viewpoint { get; init; }

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
            bool first = operation.Equals("MT_FIRST", StringComparison.OrdinalIgnoreCase);

            if (!first && !operation.Equals("MT_LAST", StringComparison.OrdinalIgnoreCase))
            {
                error = $"`mosaicRule`'s mosaicOperation '{operation}' combines overlapping pixels, and this server draws "
                    + "one image over another: MT_FIRST puts the first in the order on top, MT_LAST the last.";
                return false;
            }

            if (root.TryGetProperty("multidimensionalDefinition", out JsonElement dimensions)
                && dimensions.ValueKind == JsonValueKind.Array && dimensions.GetArrayLength() > 0)
            {
                error = "`mosaicRule`'s multidimensionalDefinition chooses among variables and dimensions, and this image "
                    + "service's images have none.";
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
                Viewpoint = viewpoint,
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
