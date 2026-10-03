using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Graticula.Cartography;
using Graticula.Coverages;

namespace Graticula.Raster.Tiff;

/// <summary>
/// A raster attribute table written beside an image as GDAL and ArcGIS write one — <c>image.tif.aux.xml</c>, a
/// <c>GDALRasterAttributeTable</c> on its first band — ADR-154. Read, never written: a file registered in place keeps
/// what its owner's tools made.
/// </summary>
/// <remarks>
/// Fields are known by GDAL's usage codes — value (5, MinMax), pixel count (1), name (2), red, green, blue (6, 7, 8) —
/// and, where a table says only "generic", by ArcGIS's names: <c>Value</c>, <c>Count</c>, <c>ClassName</c> or
/// <c>Class_Name</c>, <c>Red</c>, <c>Green</c>, <c>Blue</c>. A colour from 0 to 1 is read as a fraction, as GDAL writes one.
/// </remarks>
public static class PamAttributeTable
{
    /// <summary>The table beside an image, or null when there is none it reads.</summary>
    /// <param name="imagePath">The image.</param>
    /// <returns>The table.</returns>
    public static RasterAttributeTable? TryRead(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        string sidecar = imagePath + ".aux.xml";

        if (!File.Exists(sidecar))
        {
            return null;
        }

        try
        {
            XElement? table = XElement.Load(sidecar).Descendants("GDALRasterAttributeTable").FirstOrDefault();
            return table is null ? null : Read(table);
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static RasterAttributeTable? Read(XElement table)
    {
        List<(int Index, string Name, int Usage)> fields = [.. table.Elements("FieldDefn").Select(f => (
            int.TryParse((string?)f.Attribute("index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : -1,
            ((string?)f.Element("Name") ?? string.Empty).Trim(),
            int.TryParse((string?)f.Element("Usage"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int u) ? u : 0))];

        int Find(int usage, params string[] names) =>
            fields.FirstOrDefault(f => f.Usage == usage).Name is { Length: > 0 } byUsage
                ? fields.First(f => f.Usage == usage).Index
                : fields.FirstOrDefault(f => names.Any(n => f.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) is { Name.Length: > 0 } byName
                    ? byName.Index : -1;

        int value = Find(5, "Value");
        int count = Find(1, "Count");
        int name = Find(2, "ClassName", "Class_Name", "Name", "Class");
        int red = Find(6, "Red"), green = Find(7, "Green"), blue = Find(8, "Blue");

        if (value < 0)
        {
            return null;
        }

        List<AttributeClass> classes = [];

        foreach (XElement row in table.Elements("Row"))
        {
            string[] cells = [.. row.Elements("F").Select(f => f.Value.Trim())];
            string Cell(int at) => at >= 0 && at < cells.Length ? cells[at] : string.Empty;

            if (!double.TryParse(Cell(value), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                continue;
            }

            Rgba? colour = null;

            if (red >= 0 && green >= 0 && blue >= 0
                && double.TryParse(Cell(red), NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
                && double.TryParse(Cell(green), NumberStyles.Float, CultureInfo.InvariantCulture, out double g)
                && double.TryParse(Cell(blue), NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                bool fraction = r <= 1 && g <= 1 && b <= 1;
                byte Byte(double c) => (byte)Math.Clamp(Math.Round(fraction ? c * 255 : c), 0, 255);
                colour = new Rgba(Byte(r), Byte(g), Byte(b), 255);
            }

            long? pixels = long.TryParse(Cell(count), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : null;
            string named = Cell(name);
            classes.Add(new AttributeClass(v, named.Length > 0 ? named : v.ToString(CultureInfo.InvariantCulture), colour, pixels));
        }

        return classes.Count == 0 ? null : new RasterAttributeTable(classes);
    }
}
