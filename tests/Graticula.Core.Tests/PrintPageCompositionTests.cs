using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Graticula.Cartography;
using Graticula.Cartography.Printing;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A print's page is laid out, its map is placed and scaled into its frame, and what surrounds the map is drawn — ADR-265.
/// </summary>
/// <remarks>
/// Drawn into a canvas that records rather than rasterises, so each assertion is about where a thing went and how big it
/// was, which is what the composition decides; how it looks is the rasteriser's and is tested there.
/// </remarks>
public sealed class PrintPageCompositionTests
{
    [Fact]
    public void An_a4_landscape_page_at_150_dpi_has_its_paper_size_in_pixels_and_the_legend_on_the_right()
    {
        PageLayout layout = PageComposer.Layout(PageTemplate.Named("A4 Landscape")!, 150, legend: true);

        Assert.Equal((int)Math.Round(11.69 * 150), layout.Width);
        Assert.Equal((int)Math.Round(8.27 * 150), layout.Height);
        Assert.True(layout.HasLegend);
        Assert.True(layout.Legend.MinX > layout.Map.MaxX, "The legend is not right of the map on a landscape page.");
        Assert.True(layout.Title.MaxY < layout.Map.MinY && layout.Map.MaxY < layout.Footer.MinY);
        Assert.Equal(150 / 96.0, layout.Ratio, 9);

        // The renderer is told the frame in screen pixels, and those times the ratio cover the frame.
        Assert.Equal(layout.Map.MaxX - layout.Map.MinX, layout.LogicalMapWidth * layout.Ratio, 0);
    }

    [Fact]
    public void A_portrait_page_puts_the_legend_under_the_map_and_without_a_legend_the_map_takes_its_room()
    {
        PageTemplate letter = PageTemplate.Named("Letter ANSI A Portrait")!;
        PageLayout with = PageComposer.Layout(letter, 96, legend: true);
        PageLayout without = PageComposer.Layout(letter, 96, legend: false);

        Assert.True(with.Legend.MinY > with.Map.MaxY);
        Assert.False(without.HasLegend);
        Assert.True(without.Map.MaxY > with.Map.MaxY);
    }

    [Theory]
    [InlineData("a4 landscape", "A4 Landscape")]
    [InlineData("Letter Portrait", "Letter ANSI A Portrait")]
    [InlineData(null, "MAP_ONLY")]
    [InlineData("", "MAP_ONLY")]
    public void A_template_is_found_by_its_name_or_its_older_name(string? asked, string expected) =>
        Assert.Equal(expected, PageTemplate.Named(asked)!.Name);

    [Fact]
    public void A_template_this_server_does_not_have_is_not_quietly_another()
    {
        Assert.Null(PageTemplate.Named("A0 Landscape"));
        Assert.Throws<ArgumentException>(() => PageComposer.Layout(PageTemplate.MapOnly, 96, legend: false));
    }

    [Theory]
    [InlineData(7.3, 5)]
    [InlineData(2.4, 2)]
    [InlineData(1, 1)]
    [InlineData(0.37, 0.2)]
    [InlineData(1830, 1000)]
    public void A_scale_bar_stands_for_a_round_length_no_longer_than_it_may(double most, double expected) =>
        Assert.Equal(expected, PageComposer.NiceLength(most), 9);

    [Fact]
    public void A_placed_canvas_moves_and_scales_every_coordinate_and_length_onto_the_page()
    {
        Recording page = new(1000, 800);
        PlacedCanvas window = new(page, 100, 50, 200, 150, ratio: 2);

        Assert.Equal(200, window.Width);
        Assert.Equal(150, window.Height);

        PixelPath line = new();
        line.Begin(closed: false);
        line.Add(0, 0);
        line.Add(10, 20);
        line.End();
        window.StrokeLine(line, new MapSymbol.Stroke(Rgba.Black, 1.5, [4, 2]));

        (PixelPath placed, MapSymbol.Stroke stroke) = page.Strokes.Single();
        Assert.Equal([100, 50, 120, 90], placed.Coordinates.ToArray());
        Assert.Equal(3, stroke.Width);
        Assert.Equal([8.0, 4.0], stroke.Dash!);

        window.DrawMarker(5, 5, new MapSymbol.Marker(Rgba.Black, 3, Rgba.White, 1));
        Assert.Equal((110, 60, 6.0, 2.0), page.Markers.Single());

        window.DrawLabel("Ankara", new MapSymbol.Label(Rgba.Black, 12, Rgba.White, 1), 50, 25);
        Assert.Equal(("Ankara", 200, 100, 24.0), page.Labels.Single());

        // Measured on the page and answered in the window's own pixels, so label placement keeps working in them.
        PixelBox measured = window.MeasureLabel("Ankara", new MapSymbol.Label(Rgba.Black, 12, Rgba.White, 1), 50, 25);
        Assert.Equal(50, (measured.MinX + measured.MaxX) / 2, 6);
        Assert.Equal(25, (measured.MinY + measured.MaxY) / 2, 6);

        window.DrawImage(new Rgba[4], 2, 2, new PixelBox(0, 0, 10, 10), Resampling.Nearest);
        Assert.Equal((new PixelBox(100, 50, 120, 70), Resampling.Nearest), page.Images.Single());
    }

    [Fact]
    public void A_page_masks_the_map_spilling_past_its_frame_and_draws_title_scale_arrow_legend_and_credits()
    {
        PageLayout layout = PageComposer.Layout(PageTemplate.Named("A4 Portrait")!, 96, legend: true);
        Recording page = new(layout.Width, layout.Height);

        PageComposer.Mask(page, layout);

        // Four bands of paper round the frame, then its outline.
        Assert.Equal(4, page.Areas.Count(a => a.Symbol.Colour == Rgba.White && a.Symbol.OutlineColour.IsInvisible));
        Assert.Single(page.Strokes);

        bool swatchDrawn = false;
        IReadOnlyList<string> notes = PageComposer.DrawFurniture(page, layout, new PageFurniture(
            "Parcels of Çankaya",
            "Survey office",
            "© the municipality",
            MetresPerPagePixel: 10,
            ScaleBarUnits.Default,
            [new LegendGroup("Parcels", [new LegendRow("Residential", (at, side) =>
            {
                swatchDrawn = true;
                using IMapCanvas swatch = at.Create(side, side);
                swatch.Clear(new Rgba(200, 0, 0, 255));
            })])]));

        Assert.Empty(notes);
        Assert.True(swatchDrawn);
        string[] texts = [.. page.Labels.Select(l => l.Text)];
        Assert.Contains("Parcels of Çankaya", texts);
        Assert.Contains("Legend", texts);
        Assert.Contains("Parcels", texts);
        Assert.Contains("Residential", texts);
        Assert.Contains("N", texts);
        Assert.Contains("Survey office", texts);
        Assert.Contains("© the municipality", texts);

        // 10 m a page pixel at 96 dpi: an inch is 960 m of ground, which is 1:37,795.
        Assert.Contains("Scale 1:" + Math.Round(10 * 96 / 0.0254).ToString("N0", CultureInfo.InvariantCulture), texts);

        // Two inches of bar at most is 1,920 m: one kilometre, and one mile.
        Assert.Contains("1 km", texts);
        Assert.Contains("1 mi", texts);

        // The swatch was drawn on the page, inside the legend panel.
        Assert.Contains(page.Areas, a => a.Symbol.Colour == new Rgba(200, 0, 0, 255)
            && a.Path.Coordinates[0] >= layout.Legend.MinX && a.Path.Coordinates[1] >= layout.Legend.MinY);
    }

    [Fact]
    public void A_page_whose_scale_could_not_be_measured_has_no_scale_bar_and_says_so()
    {
        PageLayout layout = PageComposer.Layout(PageTemplate.Named("A3 Landscape")!, 96, legend: false);
        Recording page = new(layout.Width, layout.Height);

        IReadOnlyList<string> notes = PageComposer.DrawFurniture(page, layout, new PageFurniture(
            null, null, null, MetresPerPagePixel: null, ScaleBarUnits.Default, []));

        Assert.Contains(notes, n => n.Contains("scale bar was left off", StringComparison.Ordinal));
        Assert.DoesNotContain(page.Labels, l => l.Text.StartsWith("Scale 1:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pdf_holds_the_picture_on_a_page_of_the_paper_size_and_its_cross_references_point_at_its_objects()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];
        byte[] pdf = PdfPage.Wrap(jpeg, 1754, 1240, 11.69, 8.27, "Çankaya (draft)");
        string text = Encoding.Latin1.GetString(pdf);

        Assert.StartsWith("%PDF-1.4\n", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 841.68 595.44]", text, StringComparison.Ordinal);
        Assert.Contains("/Width 1754 /Height 1240", text, StringComparison.Ordinal);
        Assert.Contains("/Filter /DCTDecode /Length 4 >>", text, StringComparison.Ordinal);

        // The title is UTF-16 in hex, so a parenthesis needs no escaping and a Turkish letter survives.
        Assert.Contains("/Title <FEFF00C70061006E006B", text, StringComparison.Ordinal);

        // Every xref entry is the byte offset of "n 0 obj".
        int xref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], CultureInfo.InvariantCulture);
        Assert.StartsWith("xref", text[xref..], StringComparison.Ordinal);
        string[] entries = text[xref..].Split('\n');

        for (int n = 1; n <= 6; n++)
        {
            int offset = int.Parse(entries[2 + n][..10], CultureInfo.InvariantCulture);
            Assert.StartsWith($"{n} 0 obj", text[offset..], StringComparison.Ordinal);
        }
    }

    /// <summary>Records what is drawn on it.</summary>
    private sealed class Recording(int width, int height) : IMapCanvas
    {
        public List<(PixelPath Path, MapSymbol.Area Symbol)> Areas { get; } = [];

        public List<(PixelPath Path, MapSymbol.Stroke Symbol)> Strokes { get; } = [];

        public List<(double X, double Y, double Radius, double Outline)> Markers { get; } = [];

        public List<(string Text, double X, double Y, double Size)> Labels { get; } = [];

        public List<(PixelBox Box, Resampling How)> Images { get; } = [];

        public int Width => width;

        public int Height => height;

        public void Clear(Rgba colour)
        {
        }

        public void FillArea(PixelPath path, MapSymbol.Area symbol) => Areas.Add((Copy(path), symbol));

        public void StrokeLine(PixelPath path, MapSymbol.Stroke symbol) => Strokes.Add((Copy(path), symbol));

        public void DrawMarker(double x, double y, MapSymbol.Marker symbol) =>
            Markers.Add((x, y, symbol.Radius, symbol.OutlineWidth));

        public void DrawPicture(double x, double y, MapSymbol.Picture symbol)
        {
        }

        public PixelBox MeasureLabel(string text, MapSymbol.Label symbol, double x, double y) =>
            new(x - (text.Length * symbol.Size / 4), y - (symbol.Size / 2), x + (text.Length * symbol.Size / 4), y + (symbol.Size / 2));

        public void DrawLabel(string text, MapSymbol.Label symbol, double x, double y) => Labels.Add((text, x, y, symbol.Size));

        public void DrawImage(ReadOnlySpan<Rgba> pixels, int imageWidth, int imageHeight, PixelBox destination) =>
            Images.Add((destination, Resampling.Bilinear));

        public void DrawImage(ReadOnlySpan<Rgba> pixels, int imageWidth, int imageHeight, PixelBox destination, Resampling how) =>
            Images.Add((destination, how));

        public byte[] Encode(MapImageFormat format, int quality) => [];

        public void Dispose()
        {
        }

        private static PixelPath Copy(PixelPath path)
        {
            PixelPath copy = new();
            ReadOnlySpan<double> xy = path.Coordinates;

            foreach (PixelPath.Figure figure in path.Figures)
            {
                copy.Begin(figure.Closed);

                for (int i = figure.Start; i < figure.Start + figure.Count; i++)
                {
                    copy.Add(xy[2 * i], xy[(2 * i) + 1]);
                }
            }

            copy.End();
            return copy;
        }
    }
}
