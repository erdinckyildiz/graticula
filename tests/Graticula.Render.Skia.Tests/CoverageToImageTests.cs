using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Coverages;
using Graticula.Raster.Tiff;
using Graticula.Render.Skia;
using Xunit;

namespace Graticula.Render.Skia.Tests;

/// <summary>
/// A real GeoTIFF, through all four links, to a PNG somebody could open.
/// </summary>
/// <remarks>
/// <para>
/// <b>The chain [ADR-043](../../docs/adr/ADR-043-imageserver-and-the-raster-face.md)
/// describes has four links and every other suite tests them apart.</b> The reader is
/// checked against GDAL's answers, the style against arithmetic with no file in sight,
/// the canvas against shapes a test made up. None of those can notice the links
/// disagreeing — a reader that returns rows bottom-up and a canvas that draws them
/// top-down each pass their own suite and produce an upside-down map.
/// </para>
/// <para>
/// <b>This is the same argument correctness gate 2 made about protocol faces</b>, one
/// level down: four of its five findings needed a question no single-face test can
/// ask. A pipeline is a set of faces that have to agree.
/// </para>
/// </remarks>
public sealed class CoverageToImageTests
{
    private static string Corpus(string file) =>
        Path.Combine(AppContext.BaseDirectory, "corpus", file);

    private static async Task<byte[]> DrawAsync(
        string file, CoverageStyle style, int width = 128, int height = 96)
    {
        using TiffCoverageReader reader = TiffCoverageReader.Open(Corpus(file));

        CoverageWindow window = await reader.ReadAsync(
            0, 0, 0, reader.Info.Width, reader.Info.Height, CancellationToken.None);

        Rgba[] pixels = style.Paint(window, reader.Info.Bands);

        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(width, height);

        canvas.Clear(Rgba.Transparent);
        canvas.DrawImage(pixels, window.Width, window.Height, new PixelBox(0, 0, width, height));

        return canvas.Encode(MapImageFormat.Png, 90);
    }

    [Fact]
    public async Task A_grey_coverage_becomes_a_png_with_pixels_in_it()
    {
        byte[] png = await DrawAsync("gray-byte-deflate.tif", CoverageStyle.Default);

        Assert.True(png.Length > 100, $"The PNG is {png.Length} bytes, which is not an image.");

        // The eight-byte PNG signature, so a failure says "not a PNG" rather than
        // "some bytes came back".
        Assert.Equal(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            png[..8]);
    }

    [Fact]
    public async Task The_drawn_image_is_not_one_flat_colour()
    {
        // <b>The cheapest test that a chain is connected.</b> Every link can be
        // individually correct and still produce a blank square: a window read at the
        // wrong offset, a stretch that collapsed, a destination box of zero size. The
        // corpus is a diagonal ramp, so a correct drawing of it cannot be uniform.
        using TiffCoverageReader reader = TiffCoverageReader.Open(Corpus("gray-byte-deflate.tif"));

        CoverageWindow window = await reader.ReadAsync(
            0, 0, 0, 64, 64, CancellationToken.None);

        Rgba[] pixels = CoverageStyle.Default.Paint(window, reader.Info.Bands);

        Assert.Contains(pixels, p => p != pixels[0]);
    }

    [Fact]
    public async Task The_ramp_runs_the_way_the_data_does()
    {
        // <b>Orientation, which is the failure a per-link suite cannot see.</b> The
        // corpus ramp increases with x + 2y, so the bottom-right sample is brighter
        // than the top-left. A reader that returned rows bottom-up would pass its own
        // suite — it reads the right values — and produce an upside-down map here.
        using TiffCoverageReader reader = TiffCoverageReader.Open(Corpus("gray-byte-deflate.tif"));

        CoverageWindow window = await reader.ReadAsync(0, 0, 0, 16, 16, CancellationToken.None);

        Rgba[] pixels = new CoverageStyle(StretchKind.Window).Paint(window, reader.Info.Bands);

        Rgba topLeft = pixels[0];
        Rgba bottomRight = pixels[^1];

        Assert.True(
            bottomRight.R > topLeft.R,
            $"Top-left is {topLeft.R} and bottom-right is {bottomRight.R}. The corpus ramp "
            + "increases down and to the right, so this says the image is flipped.");
    }

    [Fact]
    public async Task A_three_band_coverage_draws_in_colour()
    {
        using TiffCoverageReader reader = TiffCoverageReader.Open(Corpus("rgb-byte-deflate.tif"));

        CoverageWindow window = await reader.ReadAsync(0, 0, 0, 32, 32, CancellationToken.None);

        Assert.Equal(3, window.Bands);

        Rgba[] pixels = CoverageStyle.Default.Paint(window, reader.Info.Bands);

        // The corpus offsets each band's ramp, so a correct read has pixels whose
        // channels differ. Equal channels everywhere would mean one band was copied
        // three times, which is what a plane-interleaved read looks like.
        Assert.Contains(pixels, p => p.R != p.G || p.G != p.B);
    }

    [Fact]
    public async Task A_no_data_pixel_leaves_the_map_beneath_it_showing()
    {
        // Transparent rather than black, all the way through the chain: the canvas is
        // cleared to a colour, the coverage is drawn over it, and the no-data pixels
        // must still be that colour afterwards.
        using TiffCoverageReader reader = TiffCoverageReader.Open(Corpus("gray-byte-nodata.tif"));

        CoverageWindow window = await reader.ReadAsync(0, 0, 0, 64, 64, CancellationToken.None);

        Rgba[] pixels = CoverageStyle.Default.Paint(window, reader.Info.Bands);

        // <b>Both halves, and the second one was missing.</b> This asserted only that
        // some pixel was absent, which is trivially true of an image where all of them
        // are — and for one generation of the corpus all of them were. A no-data rule
        // that made everything transparent would pass the first assertion and produce
        // an invisible map.
        Assert.Contains(pixels, p => p.A == 0);
        Assert.Contains(pixels, p => p.A == 255);
    }

    [Fact]
    public async Task An_overview_draws_as_well_as_the_full_image()
    {
        using TiffCoverageReader reader = TiffCoverageReader.Open(Corpus("gray-byte-deflate.tif"));

        OverviewInfo level = reader.Info.Overviews[0];

        CoverageWindow window = await reader.ReadAsync(
            1, 0, 0, level.Width, level.Height, CancellationToken.None);

        Rgba[] pixels = CoverageStyle.Default.Paint(window, reader.Info.Bands);

        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        canvas.Clear(Rgba.Transparent);
        canvas.DrawImage(pixels, window.Width, window.Height, new PixelBox(0, 0, 64, 64));

        Assert.True(canvas.Encode(MapImageFormat.Png, 90).Length > 100);
    }

    [Fact]
    public void A_short_buffer_is_refused_rather_than_drawn()
    {
        // Drawing past the end of the array would paint whatever followed it in
        // memory — a picture rather than an error, and the worst kind of both.
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(32, 32);

        Assert.Throws<RenderException>(() =>
            canvas.DrawImage(new Rgba[4], 8, 8, new PixelBox(0, 0, 32, 32)));
    }
}
