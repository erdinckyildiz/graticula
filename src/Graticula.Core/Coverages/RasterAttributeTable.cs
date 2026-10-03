using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Cartography;

namespace Graticula.Coverages;

/// <summary>One class of a classified image: its value, what it is called, and the colour it is drawn in — ADR-154.</summary>
/// <param name="Value">The pixel value.</param>
/// <param name="Name">What the class is called — ArcGIS's <c>ClassName</c>.</param>
/// <param name="Colour">The colour it is drawn in, or null to leave the drawing to the stretch.</param>
/// <param name="Count">How many pixels have it, when the table says.</param>
public sealed record AttributeClass(double Value, string Name, Rgba? Colour, long? Count = null);

/// <summary>
/// A classified image's raster attribute table — ADR-154, ArcGIS's: a row per value with its class name and colour,
/// which <c>identify</c> names, the legend lists and the picture is drawn in.
/// </summary>
public sealed class RasterAttributeTable
{
    private readonly Dictionary<double, AttributeClass> _byValue;

    /// <summary>Makes a table of classes; a value given twice keeps its first.</summary>
    /// <param name="classes">The classes.</param>
    public RasterAttributeTable(IEnumerable<AttributeClass> classes)
    {
        ArgumentNullException.ThrowIfNull(classes);
        Classes = [.. classes.GroupBy(c => c.Value).Select(g => g.First()).OrderBy(c => c.Value)];
        _byValue = Classes.ToDictionary(c => c.Value);
    }

    /// <summary>The classes, by value.</summary>
    public IReadOnlyList<AttributeClass> Classes { get; }

    /// <summary>Whether it gives colours, and so draws the picture.</summary>
    public bool Colours => Classes.Any(c => c.Colour is not null);

    /// <summary>The class a value is, or null.</summary>
    /// <param name="value">A pixel value.</param>
    /// <returns>The class.</returns>
    public AttributeClass? Find(double value) => _byValue.GetValueOrDefault(value);

    /// <summary>
    /// Paints a window of the image's first band in its classes' colours; a value with no class, or no colour, and the
    /// band's no-data, are transparent.
    /// </summary>
    /// <param name="window">The values.</param>
    /// <param name="bands">The image's bands.</param>
    /// <returns>The pixels.</returns>
    public Rgba[] Paint(CoverageWindow window, IReadOnlyList<BandInfo> bands)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(bands);

        double? noData = bands.Count > 0 ? bands[0].NoData : null;
        Rgba[] pixels = new Rgba[window.Width * window.Height];

        for (int i = 0; i < pixels.Length; i++)
        {
            double value = window.Samples[i * window.Bands];
            pixels[i] = double.IsNaN(value) || value == noData || Find(value)?.Colour is not { } colour ? Rgba.Transparent : colour;
        }

        return pixels;
    }

    /// <summary>A colour as #rrggbb.</summary>
    /// <param name="colour">The colour.</param>
    /// <returns>The text.</returns>
    public static string Hex(Rgba colour) =>
        string.Create(CultureInfo.InvariantCulture, $"#{colour.R:x2}{colour.G:x2}{colour.B:x2}");

    /// <summary>A colour from #rrggbb, or null.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The colour.</returns>
    public static Rgba? FromHex(string? text) =>
        text is { Length: 7 } && text[0] == '#'
        && int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)
            ? new Rgba((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255)
            : null;
}
