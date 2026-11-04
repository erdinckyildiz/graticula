using System;
using System.Collections.Generic;
using System.Globalization;

namespace Graticula.Cartography.Printing;

/// <summary>Where each part of a page goes, in page pixels.</summary>
/// <param name="Width">The page's width.</param>
/// <param name="Height">The page's height.</param>
/// <param name="Map">The map frame.</param>
/// <param name="Title">The title band.</param>
/// <param name="Legend">The legend panel; empty when the page has none.</param>
/// <param name="Footer">The band under the map that holds the scale bar, north arrow and credits.</param>
/// <param name="Ratio">Page pixels per screen pixel: dpi over 96.</param>
/// <param name="Dpi">The page's dots per inch.</param>
public sealed record PageLayout(
    int Width, int Height, PixelBox Map, PixelBox Title, PixelBox Legend, PixelBox Footer, double Ratio, double Dpi)
{
    /// <summary>The map frame's width, in logical (screen) pixels — what the renderer is told.</summary>
    public int LogicalMapWidth => Math.Max(1, (int)Math.Round((Map.MaxX - Map.MinX) / Ratio));

    /// <summary>The map frame's height, in logical pixels.</summary>
    public int LogicalMapHeight => Math.Max(1, (int)Math.Round((Map.MaxY - Map.MinY) / Ratio));

    /// <summary>Whether a legend panel was laid out.</summary>
    public bool HasLegend => Legend.MaxX > Legend.MinX && Legend.MaxY > Legend.MinY;
}

/// <summary>The units a scale bar is drawn in, as <c>layoutOptions.scaleBarOptions</c> names them.</summary>
/// <param name="MetricUnit">An Esri unit name: <c>esriKilometers</c> or <c>esriMeters</c>.</param>
/// <param name="MetricLabel">What the metric bar's end is labelled with.</param>
/// <param name="NonMetricUnit">An Esri unit name: <c>esriMiles</c>, <c>esriFeet</c>, <c>esriYards</c> or <c>esriNauticalMiles</c>.</param>
/// <param name="NonMetricLabel">What the other bar's end is labelled with.</param>
public sealed record ScaleBarUnits(string MetricUnit, string MetricLabel, string NonMetricUnit, string NonMetricLabel)
{
    /// <summary>Kilometres and miles, ArcGIS's default.</summary>
    public static ScaleBarUnits Default { get; } = new("esriKilometers", "km", "esriMiles", "mi");

    /// <summary>A metre's worth of each unit a scale bar may be drawn in.</summary>
    public static double? MetresOf(string? unit) => unit?.Trim().ToUpperInvariant() switch
    {
        "ESRIKILOMETERS" => 1000,
        "ESRIMETERS" => 1,
        "ESRIMILES" => 1609.344,
        "ESRIFEET" => 0.3048,
        "ESRIYARDS" => 0.9144,
        "ESRINAUTICALMILES" => 1852,
        _ => null,
    };
}

/// <summary>One row of a printed legend: a label and the swatch beside it.</summary>
/// <param name="Label">The class's label; empty for a layer drawn with one symbol.</param>
/// <param name="Swatch">
/// Draws the swatch: given a factory whose canvases land where the swatch goes, and the swatch's side in screen pixels.
/// Null for a row with text only.
/// </param>
public sealed record LegendRow(string Label, Action<IMapCanvasFactory, int>? Swatch);

/// <summary>A layer's entry in a printed legend.</summary>
/// <param name="Title">The layer's title.</param>
/// <param name="Rows">Its classes.</param>
public sealed record LegendGroup(string Title, IReadOnlyList<LegendRow> Rows);

/// <summary>What a page shows around its map.</summary>
/// <param name="Title">The title, or null for none.</param>
/// <param name="Author">The author line, or null.</param>
/// <param name="Copyright">The copyright line, or null.</param>
/// <param name="MetresPerPagePixel">
/// The ground a page pixel covers at the map's centre, in metres, or null when it could not be measured — then the page
/// has no scale bar and no printed scale rather than a wrong one.
/// </param>
/// <param name="Units">The scale bar's units.</param>
/// <param name="Legend">The legend, top to bottom.</param>
public sealed record PageFurniture(
    string? Title,
    string? Author,
    string? Copyright,
    double? MetresPerPagePixel,
    ScaleBarUnits Units,
    IReadOnlyList<LegendGroup> Legend);

/// <summary>
/// Lays a page out and draws what surrounds its map — ADR-265 §5.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tier 1 and drawn through the port</b>, like every other picture this server makes: text through
/// <see cref="IMapCanvas.DrawLabel"/>, so the page is set in the bundled faces the map labels use (ADR-100), and bars and
/// the arrow as filled areas. Nothing here knows a rasteriser.
/// </para>
/// <para>
/// <b>The order is fixed: the map, then <see cref="Mask"/>, then <see cref="DrawFurniture"/>.</b> The map is drawn
/// through a <see cref="PlacedCanvas"/>, which cannot clip, so it spills past its frame; the mask paints the paper back
/// over the spill, and the furniture is drawn on clean paper.
/// </para>
/// </remarks>
public static class PageComposer
{
    /// <summary>The dpi a screen pixel is: the CSS pixel, which is what a web map's symbol sizes are written in.</summary>
    public const double ScreenDpi = 96;

    private const double Margin = 0.35;
    private const double TitleBand = 0.6;
    private const double FooterBand = 0.8;
    private const double Gap = 0.12;
    private const double LegendSide = 2.3;
    private const double LegendBand = 2.0;

    private static readonly Rgba Ink = new(33, 33, 33, 255);

    /// <summary>Lays out a page of a template at a dpi.</summary>
    /// <param name="template">The template; not <see cref="PageTemplate.MapOnly"/>.</param>
    /// <param name="dpi">Dots per inch.</param>
    /// <param name="legend">Whether to leave room for a legend.</param>
    /// <returns>The layout.</returns>
    public static PageLayout Layout(PageTemplate template, double dpi, bool legend)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.IsMapOnly)
        {
            throw new ArgumentException("MAP_ONLY has no page; its size is the output size.", nameof(template));
        }

        int width = (int)Math.Round(template.WidthInches * dpi);
        int height = (int)Math.Round(template.HeightInches * dpi);
        double In(double inches) => inches * dpi;

        PixelBox title = new(In(Margin), In(Margin), width - In(Margin), In(Margin + TitleBand));
        PixelBox footer = new(In(Margin), height - In(Margin + FooterBand), width - In(Margin), height - In(Margin));

        double top = title.MaxY + In(Gap);
        double bottom = footer.MinY - In(Gap);
        PixelBox map;
        PixelBox panel = default;

        if (!legend)
        {
            map = new(In(Margin), top, width - In(Margin), bottom);
        }
        else if (template.IsLandscape)
        {
            panel = new(width - In(Margin + LegendSide), top, width - In(Margin), bottom);
            map = new(In(Margin), top, panel.MinX - In(Gap), bottom);
        }
        else
        {
            panel = new(In(Margin), bottom - In(LegendBand), width - In(Margin), bottom);
            map = new(In(Margin), top, width - In(Margin), panel.MinY - In(Gap));
        }

        return new PageLayout(width, height, Snap(map), title, panel, footer, dpi / ScreenDpi, dpi);
    }

    /// <summary>The window a map is drawn through onto a page.</summary>
    /// <param name="page">The page canvas.</param>
    /// <param name="layout">The page's layout.</param>
    /// <returns>A canvas of the map frame's logical size.</returns>
    public static PlacedCanvas MapWindow(IMapCanvas page, PageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new PlacedCanvas(page, layout.Map.MinX, layout.Map.MinY, layout.LogicalMapWidth, layout.LogicalMapHeight, layout.Ratio);
    }

    /// <summary>Paints the paper back over whatever the map drew outside its frame, and outlines the frame.</summary>
    /// <param name="page">The page.</param>
    /// <param name="layout">Its layout.</param>
    public static void Mask(IMapCanvas page, PageLayout layout)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(layout);

        // The frame's right and bottom edges as the window drew them, which rounding can put a pixel off the layout's.
        double right = layout.Map.MinX + (layout.LogicalMapWidth * layout.Ratio);
        double bottom = layout.Map.MinY + (layout.LogicalMapHeight * layout.Ratio);
        MapSymbol.Area paper = new(Rgba.White, Rgba.Transparent, 0);

        Fill(page, new PixelBox(0, 0, layout.Width, layout.Map.MinY), paper);
        Fill(page, new PixelBox(0, bottom, layout.Width, layout.Height), paper);
        Fill(page, new PixelBox(0, layout.Map.MinY, layout.Map.MinX, bottom), paper);
        Fill(page, new PixelBox(right, layout.Map.MinY, layout.Width, bottom), paper);

        PixelPath frame = Rectangle(new PixelBox(layout.Map.MinX, layout.Map.MinY, right, bottom), closed: true);
        page.StrokeLine(frame, new MapSymbol.Stroke(Ink, Math.Max(1, layout.Ratio), null));
    }

    /// <summary>Draws the title, legend, scale bar, printed scale, north arrow and credits.</summary>
    /// <param name="page">The page.</param>
    /// <param name="layout">Its layout.</param>
    /// <param name="furniture">What to draw.</param>
    /// <returns>What could not be drawn, as sentences for the print's messages.</returns>
    public static IReadOnlyList<string> DrawFurniture(IMapCanvas page, PageLayout layout, PageFurniture furniture)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(furniture);

        List<string> notes = [];
        double pt = layout.Dpi / 72;

        if (furniture.Title is { Length: > 0 } title)
        {
            Text(page, Clip(title, 90), 18 * pt, layout.Title.MinX, (layout.Title.MinY + layout.Title.MaxY) / 2);
        }

        if (layout.HasLegend)
        {
            DrawLegend(page, layout, furniture.Legend, pt, notes);
        }

        double footerMiddle = (layout.Footer.MinY + layout.Footer.MaxY) / 2;
        double scaleBarRight = layout.Footer.MinX;

        if (furniture.MetresPerPagePixel is { } metres && metres > 0 && double.IsFinite(metres))
        {
            scaleBarRight = DrawScaleBars(page, layout, furniture.Units, metres, pt);

            // The paper's scale: the ground one page inch covers, over the inch.
            double denominator = metres * layout.Dpi / 0.0254;
            Text(
                page,
                "Scale 1:" + Math.Round(denominator).ToString("N0", CultureInfo.InvariantCulture),
                8 * pt,
                scaleBarRight + (0.3 * layout.Dpi),
                footerMiddle);
        }
        else
        {
            notes.Add("The scale bar was left off: the ground a page inch covers could not be measured at the map's centre.");
        }

        DrawNorthArrow(page, layout, pt);

        double creditsRight = layout.Footer.MaxX;
        double lineHeight = 10 * pt;

        if (furniture.Author is { Length: > 0 } author)
        {
            TextRight(page, Clip(author, 80), 8 * pt, creditsRight, footerMiddle - (lineHeight * 0.6));
        }

        if (furniture.Copyright is { Length: > 0 } copyright)
        {
            TextRight(page, Clip(copyright, 100), 7 * pt, creditsRight, footerMiddle + (lineHeight * 0.6));
        }

        return notes;
    }

    /// <summary>The longest length on a nice number — 1, 2 or 5 times a power of ten — not over the one given.</summary>
    /// <param name="most">The most the bar may stand for, in its unit.</param>
    /// <returns>The length.</returns>
    public static double NiceLength(double most)
    {
        if (!(most > 0) || !double.IsFinite(most))
        {
            return 0;
        }

        double power = Math.Pow(10, Math.Floor(Math.Log10(most)));

        foreach (double step in (double[])[5, 2, 1])
        {
            if (step * power <= most * (1 + 1e-9))
            {
                return step * power;
            }
        }

        return power;
    }

    /// <summary>A number as a scale bar's end is labelled: no more digits than it has.</summary>
    /// <param name="value">The length.</param>
    /// <returns>The text.</returns>
    public static string Format(double value) =>
        value >= 1
            ? value.ToString("#,0.##", CultureInfo.InvariantCulture)
            : value.ToString("0.#####", CultureInfo.InvariantCulture);

    private static double DrawScaleBars(IMapCanvas page, PageLayout layout, ScaleBarUnits units, double metresPerPixel, double pt)
    {
        double longest = 2.0 * layout.Dpi;
        double barHeight = 0.07 * layout.Dpi;
        double left = layout.Footer.MinX;
        double top = layout.Footer.MinY + (0.12 * layout.Dpi);
        double right = left;

        (string Unit, string Label, string Fallback, string FallbackLabel)[] bars =
        [
            (units.MetricUnit, units.MetricLabel, "esriKilometers", "km"),
            (units.NonMetricUnit, units.NonMetricLabel, "esriMiles", "mi"),
        ];

        foreach ((string unit, string label, string fallback, string fallbackLabel) in bars)
        {
            (double unitMetres, string shown) = ScaleBarUnits.MetresOf(unit) is { } known
                ? (known, string.IsNullOrWhiteSpace(label) ? fallbackLabel : label)
                : (ScaleBarUnits.MetresOf(fallback)!.Value, fallbackLabel);

            double length = NiceLength(longest * metresPerPixel / unitMetres);

            if (length <= 0)
            {
                continue;
            }

            double pixels = length * unitMetres / metresPerPixel;

            for (int i = 0; i < 4; i++)
            {
                PixelBox segment = new(left + (pixels * i / 4), top, left + (pixels * (i + 1) / 4), top + barHeight);
                Fill(page, segment, new MapSymbol.Area(i % 2 == 0 ? Ink : Rgba.White, Ink, Math.Max(1, layout.Ratio * 0.75)));
            }

            double labelY = top + barHeight + (5 * pt);
            Text(page, "0", 7 * pt, left - (2 * pt), labelY, centred: true);
            Text(page, $"{Format(length)} {Clip(shown, 12)}", 7 * pt, left + pixels, labelY, centred: true);

            right = Math.Max(right, left + pixels + (0.3 * layout.Dpi));
            top += barHeight + (13 * pt);
        }

        return right;
    }

    private static void DrawNorthArrow(IMapCanvas page, PageLayout layout, double pt)
    {
        double height = 0.4 * layout.Dpi;
        double cx = layout.Footer.MinX + ((layout.Footer.MaxX - layout.Footer.MinX) * 0.62);
        double bottom = layout.Footer.MaxY - (0.06 * layout.Dpi);
        double tip = bottom - height;
        double half = height * 0.32;

        PixelPath left = new();
        left.Begin(closed: true);
        left.Add(cx, tip);
        left.Add(cx, bottom - (height * 0.28));
        left.Add(cx - half, bottom);
        left.End();
        page.FillArea(left, new MapSymbol.Area(Rgba.White, Ink, Math.Max(1, layout.Ratio * 0.75)));

        PixelPath right = new();
        right.Begin(closed: true);
        right.Add(cx, tip);
        right.Add(cx + half, bottom);
        right.Add(cx, bottom - (height * 0.28));
        right.End();
        page.FillArea(right, new MapSymbol.Area(Ink, Ink, Math.Max(1, layout.Ratio * 0.75)));

        Text(page, "N", 9 * pt, cx, tip - (7 * pt), centred: true);
    }

    private static void DrawLegend(IMapCanvas page, PageLayout layout, IReadOnlyList<LegendGroup> groups, double pt, List<string> notes)
    {
        PixelBox panel = layout.Legend;
        double swatch = 0.2 * layout.Dpi;
        double rowHeight = Math.Max(swatch, 9 * pt) + (3 * pt);
        double headingHeight = 12 * pt;
        double columnWidth = Math.Min(panel.MaxX - panel.MinX, 2.3 * layout.Dpi);
        int swatchLogical = Math.Max(4, (int)Math.Round(swatch / layout.Ratio));

        double x = panel.MinX;
        double y = panel.MinY;

        Text(page, "Legend", 11 * pt, x, y + (headingHeight / 2));
        y += headingHeight + (4 * pt);

        int left = 0;
        int total = 0;

        foreach (LegendGroup group in groups)
        {
            total += 1 + group.Rows.Count;
        }

        foreach (LegendGroup group in groups)
        {
            if (!Room(ref x, ref y, headingHeight, panel, columnWidth))
            {
                break;
            }

            Text(page, Clip(group.Title, 40), 9 * pt, x, y + (headingHeight / 2));
            y += headingHeight;
            left++;

            foreach (LegendRow row in group.Rows)
            {
                if (!Room(ref x, ref y, rowHeight, panel, columnWidth))
                {
                    break;
                }

                double middle = y + (rowHeight / 2);

                if (row.Swatch is { } draw)
                {
                    draw(PlacedCanvas.At(page, x, middle - (swatch / 2), layout.Ratio), swatchLogical);
                }

                if (row.Label.Length > 0)
                {
                    Text(page, Clip(row.Label, 36), 8 * pt, x + swatch + (5 * pt), middle);
                }

                y += rowHeight;
                left++;
            }
        }

        if (left < total)
        {
            notes.Add($"The legend had room for {left} of its {total} entries; the rest were left off.");
        }
    }

    /// <summary>Whether a row fits where the cursor is, moving to the next column when it does not.</summary>
    private static bool Room(ref double x, ref double y, double height, PixelBox panel, double columnWidth)
    {
        if (y + height <= panel.MaxY)
        {
            return true;
        }

        if (x + (2 * columnWidth) > panel.MaxX + 0.5)
        {
            return false;
        }

        x += columnWidth;
        y = panel.MinY;
        return y + height <= panel.MaxY;
    }

    private static string Clip(string text, int most)
    {
        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return flat.Length <= most ? flat : string.Concat(flat.AsSpan(0, most - 1), "…");
    }

    private static void Text(IMapCanvas page, string text, double size, double x, double y, bool centred = false)
    {
        MapSymbol.Label symbol = new(Ink, size, Rgba.Transparent, 0);

        if (centred)
        {
            page.DrawLabel(text, symbol, x, y);
            return;
        }

        PixelBox box = page.MeasureLabel(text, symbol, x, y);
        page.DrawLabel(text, symbol, x + ((box.MaxX - box.MinX) / 2), y);
    }

    private static void TextRight(IMapCanvas page, string text, double size, double right, double y)
    {
        MapSymbol.Label symbol = new(Ink, size, Rgba.Transparent, 0);
        PixelBox box = page.MeasureLabel(text, symbol, right, y);
        page.DrawLabel(text, symbol, right - ((box.MaxX - box.MinX) / 2), y);
    }

    private static void Fill(IMapCanvas page, PixelBox box, MapSymbol.Area symbol)
    {
        if (box.MaxX <= box.MinX || box.MaxY <= box.MinY)
        {
            return;
        }

        page.FillArea(Rectangle(box, closed: true), symbol);
    }

    private static PixelPath Rectangle(PixelBox box, bool closed)
    {
        PixelPath path = new();
        path.Begin(closed);
        path.Add(box.MinX, box.MinY);
        path.Add(box.MaxX, box.MinY);
        path.Add(box.MaxX, box.MaxY);
        path.Add(box.MinX, box.MaxY);

        if (!closed)
        {
            path.Add(box.MinX, box.MinY);
        }

        path.End();
        return path;
    }

    private static PixelBox Snap(PixelBox box) =>
        new(Math.Round(box.MinX), Math.Round(box.MinY), Math.Round(box.MaxX), Math.Round(box.MaxY));
}
