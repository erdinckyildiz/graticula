using System;
using System.Collections.Generic;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Render.Skia.Tests;

/// <summary>
/// The rasteriser adapter, checked by reading the pixels back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pixels, not mock calls.</b> A test asserting that <c>FillArea</c> called
/// something would pass against a canvas that drew nothing, and *drew nothing* is
/// the failure mode a map server has — the request succeeds, the image is the right
/// size, and it is empty. So every assertion here decodes the PNG and looks at a
/// coordinate.
/// </para>
/// <para>
/// <b>This is also the test that proves the port has a working implementation at
/// all</b>, which is what makes <see cref="IMapCanvas"/> a boundary rather than an
/// aspiration.
/// </para>
/// </remarks>
public sealed class SkiaMapCanvasTests
{
    private static readonly Rgba Red = new(255, 0, 0, 255);
    private static readonly Rgba Blue = new(0, 0, 255, 255);

    /// <summary>Decodes a PNG and reads one pixel, so a test can look at the image.</summary>
    private static Rgba PixelAt(byte[] png, int x, int y)
    {
        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);

        Assert.NotNull(bitmap);

        SkiaSharp.SKColor colour = bitmap.GetPixel(x, y);

        return new Rgba(colour.Red, colour.Green, colour.Blue, colour.Alpha);
    }

    private static PixelPath Square(double left, double top, double size)
    {
        PixelPath path = new();

        path.Begin(closed: true);
        path.Add(left, top);
        path.Add(left + size, top);
        path.Add(left + size, top + size);
        path.Add(left, top + size);
        path.End();

        return path;
    }

    [Fact]
    public void An_empty_canvas_encodes_a_png_of_the_requested_size()
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 32);

        canvas.Clear(Rgba.Transparent);

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);

        // The PNG signature, from the format's own specification. A test asserting
        // only "some bytes came back" passes for a JPEG, which is exactly the
        // confusion a FORMAT parameter can produce.
        Assert.Equal<byte>([0x89, 0x50, 0x4E, 0x47], png[..4]);

        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);

        Assert.Equal(64, bitmap.Width);
        Assert.Equal(32, bitmap.Height);
    }

    [Fact]
    public void A_transparent_clear_leaves_transparent_pixels()
    {
        // <b>ADR-041 condition 5's other half.</b> A map that matched nothing must
        // come back transparent rather than white, or a client compositing two
        // layers gets the lower one erased by the upper one's background.
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(8, 8);

        canvas.Clear(Rgba.Transparent);

        Assert.Equal(0, PixelAt(canvas.Encode(MapImageFormat.Png, 90), 4, 4).A);
    }

    [Fact]
    public void A_filled_area_paints_inside_and_not_outside()
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        canvas.Clear(Rgba.Transparent);
        canvas.FillArea(Square(16, 16, 32), new MapSymbol.Area(Red, Rgba.Transparent, 0));

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);

        Assert.Equal(Red, PixelAt(png, 32, 32));
        Assert.Equal(0, PixelAt(png, 4, 4).A);
    }

    [Fact]
    public void A_hole_is_a_hole_rather_than_a_second_fill()
    {
        // <b>The even-odd rule, asserted.</b> A ring inside another ring is a hole
        // whichever way it winds, and a renderer using non-zero winding fills it
        // whenever the producer emits both rings in the same direction — which
        // several do. The defect is invisible on any polygon without holes.
        PixelPath path = new();

        path.Begin(closed: true);
        path.Add(8, 8);
        path.Add(56, 8);
        path.Add(56, 56);
        path.Add(8, 56);

        path.Begin(closed: true);
        path.Add(24, 24);
        path.Add(40, 24);
        path.Add(40, 40);
        path.Add(24, 40);
        path.End();

        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        canvas.Clear(Rgba.Transparent);
        canvas.FillArea(path, new MapSymbol.Area(Red, Rgba.Transparent, 0));

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);

        Assert.Equal(Red, PixelAt(png, 16, 32));
        Assert.Equal(0, PixelAt(png, 32, 32).A);
    }

    [Fact]
    public void A_stroked_line_paints_along_its_path()
    {
        PixelPath path = new();

        path.Begin(closed: false);
        path.Add(0, 32);
        path.Add(64, 32);
        path.End();

        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        canvas.Clear(Rgba.Transparent);
        canvas.StrokeLine(path, new MapSymbol.Stroke(Blue, 4, null));

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);

        Assert.Equal(Blue, PixelAt(png, 32, 32));
        Assert.Equal(0, PixelAt(png, 32, 8).A);
    }

    [Fact]
    public void A_dash_pattern_leaves_gaps()
    {
        PixelPath path = new();

        path.Begin(closed: false);
        path.Add(0, 32);
        path.Add(64, 32);
        path.End();

        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        canvas.Clear(Rgba.Transparent);

        // Square caps would fill the gaps back in on a round-capped stroke, so this
        // also asserts that the pattern survives the cap style the canvas chose.
        canvas.StrokeLine(path, new MapSymbol.Stroke(Blue, 2, new List<double> { 8, 8 }));

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);

        int painted = 0;

        for (int x = 0; x < 64; x++)
        {
            if (PixelAt(png, x, 32).A > 0)
            {
                painted++;
            }
        }

        Assert.InRange(painted, 8, 56);
    }

    [Fact]
    public void A_marker_paints_a_disc_and_its_outline()
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        canvas.Clear(Rgba.Transparent);
        canvas.DrawMarker(32, 32, new MapSymbol.Marker(Red, 10, Blue, 3));

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);

        Assert.Equal(Red, PixelAt(png, 32, 32));
        Assert.True(PixelAt(png, 32, 22).B > 128, "The marker's outline is not on its edge.");
        Assert.Equal(0, PixelAt(png, 2, 2).A);
    }

    [Fact]
    public void A_label_measures_wider_for_more_text_and_taller_for_a_larger_size()
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(64, 64);

        MapSymbol.Label small = new(Rgba.Black, 10, Rgba.Transparent, 0);
        MapSymbol.Label large = small with { Size = 20 };

        PixelBox one = canvas.MeasureLabel("i", small, 32, 32);
        PixelBox many = canvas.MeasureLabel("iiiiiiii", small, 32, 32);
        PixelBox bigger = canvas.MeasureLabel("i", large, 32, 32);

        Assert.True(many.MaxX - many.MinX > one.MaxX - one.MinX);
        Assert.True(bigger.MaxY - bigger.MinY > one.MaxY - one.MinY);

        // Centred on the anchor, which is what the placer assumes when it decides
        // whether a box is on the image.
        Assert.Equal(32, (one.MinX + one.MaxX) / 2, 1);
    }

    [Fact]
    public void A_label_paints_pixels()
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(96, 48);

        canvas.Clear(Rgba.Transparent);
        canvas.DrawLabel("MMMM", new MapSymbol.Label(Red, 24, Rgba.White, 2), 48, 30);

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);

        int painted = 0;

        for (int y = 0; y < 48; y++)
        {
            for (int x = 0; x < 96; x++)
            {
                if (PixelAt(png, x, y).A > 0)
                {
                    painted++;
                }
            }
        }

        // <b>A number rather than "more than zero".</b> A font that resolved to
        // nothing draws blanks, and blanks are indistinguishable from a label that
        // was placed and simply had no glyphs — which is the air-gapped font
        // question (Q-15) arriving as a silent failure.
        Assert.True(painted > 200, $"Only {painted} pixels were painted for a 24-pixel label.");
    }

    [Fact]
    public void Jpeg_is_a_jpeg_rather_than_a_png_with_another_name()
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(16, 16);

        canvas.Clear(Rgba.White);

        byte[] jpeg = canvas.Encode(MapImageFormat.Jpeg, 80);

        Assert.Equal<byte>([0xFF, 0xD8, 0xFF], jpeg[..3]);
    }

    [Fact]
    public void An_unknown_format_is_refused_rather_than_defaulted()
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(8, 8);

        Assert.Throws<RenderException>(() => canvas.Encode((MapImageFormat)99, 90));
    }
}
