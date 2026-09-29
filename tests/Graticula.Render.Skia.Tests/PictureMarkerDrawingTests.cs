using System;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Testing;
using Xunit;

namespace Graticula.Render.Skia.Tests;

/// <summary>
/// A picture marker lands where the style says, the right way up — ADR-099, read back from the pixels.
/// </summary>
/// <remarks>
/// <b>A picture whose top half and bottom half differ</b>, so that a test can tell a picture drawn
/// upside down, turned the wrong way or offset the wrong way from one drawn right — every one of which
/// passes a test that only asks whether something was drawn.
/// </remarks>
public sealed class PictureMarkerDrawingTests
{
    /// <summary>Twenty pixels square: red above, blue below.</summary>
    private static readonly byte[] Split = TestPictures.Png(20, 20, (_, y) =>
        y < 10 ? ((byte)255, (byte)0, (byte)0, (byte)255) : ((byte)0, (byte)0, (byte)255, (byte)255));

    private static readonly MarkerPicture Picture = MarkerPicture.FromBytes(Split, null, "the test");

    private static Rgba PixelAt(byte[] png, int x, int y)
    {
        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);

        SkiaSharp.SKColor colour = bitmap.GetPixel(x, y);

        return new Rgba(colour.Red, colour.Green, colour.Blue, colour.Alpha);
    }

    private static byte[] Draw(MapSymbol.Picture symbol, double x = 50, double y = 50)
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(100, 100);

        canvas.Clear(Rgba.Transparent);
        canvas.DrawPicture(x, y, symbol);

        return canvas.Encode(MapImageFormat.Png, 100);
    }

    private static bool Reddish(Rgba c) => c.A > 200 && c.R > 200 && c.B < 60;

    private static bool Bluish(Rgba c) => c.A > 200 && c.B > 200 && c.R < 60;

    [Fact]
    public void The_picture_is_drawn_centred_on_its_point_and_the_right_way_up()
    {
        byte[] png = Draw(new MapSymbol.Picture(Picture, 20, 20, 0, 0, 0, 1));

        Assert.True(Reddish(PixelAt(png, 50, 44)), "Above the point should be the picture's top, red.");
        Assert.True(Bluish(PixelAt(png, 50, 56)), "Below the point should be the picture's bottom, blue.");
        Assert.Equal(0, PixelAt(png, 50, 35).A);
        Assert.Equal(0, PixelAt(png, 35, 50).A);
    }

    [Fact]
    public void A_half_turn_puts_the_bottom_on_top()
    {
        byte[] png = Draw(new MapSymbol.Picture(Picture, 20, 20, 0, 0, 180, 1));

        Assert.True(Bluish(PixelAt(png, 50, 44)));
        Assert.True(Reddish(PixelAt(png, 50, 56)));
    }

    [Fact]
    public void A_quarter_turn_is_clockwise()
    {
        // Turned clockwise, the red top swings to the right-hand side.
        byte[] png = Draw(new MapSymbol.Picture(Picture, 20, 20, 0, 0, 90, 1));

        Assert.True(Reddish(PixelAt(png, 56, 50)));
        Assert.True(Bluish(PixelAt(png, 44, 50)));
    }

    [Fact]
    public void An_offset_moves_the_picture_down_the_screen_and_turns_with_it()
    {
        byte[] down = Draw(new MapSymbol.Picture(Picture, 20, 20, 0, 20, 0, 1));

        Assert.Equal(0, PixelAt(down, 50, 50).A);
        Assert.True(Reddish(PixelAt(down, 50, 64)));
        Assert.True(Bluish(PixelAt(down, 50, 76)));

        // An offset to the right, turned a quarter clockwise, is an offset downward.
        byte[] turned = Draw(new MapSymbol.Picture(Picture, 20, 20, 20, 0, 90, 1));

        Assert.Equal(0, PixelAt(turned, 70, 50).A);
        Assert.NotEqual(0, PixelAt(turned, 50, 70).A);
    }

    [Fact]
    public void Opacity_is_applied_to_the_picture()
    {
        Rgba half = PixelAt(Draw(new MapSymbol.Picture(Picture, 20, 20, 0, 0, 0, 0.5)), 50, 44);

        Assert.InRange(half.A, 110, 145);
    }

    [Fact]
    public void A_picture_whose_body_does_not_decode_draws_nothing_and_does_not_fail_the_map()
    {
        byte[] damaged = (byte[])Split.Clone();

        // The header is intact, so it is accepted; the compressed rows after it are not a picture.
        for (int i = 40; i < damaged.Length - 12; i++)
        {
            damaged[i] = 0xAB;
        }

        MarkerPicture broken = MarkerPicture.FromBytes(damaged, null, "the test");

        byte[] png = Draw(new MapSymbol.Picture(broken, 20, 20, 0, 0, 0, 1));

        Assert.Equal(0, PixelAt(png, 50, 50).A);
    }

    [Fact]
    public void A_stored_picture_renderer_draws_the_picture_where_a_circle_would_have_been()
    {
        System.Text.Json.Nodes.JsonObject marker = new()
        {
            ["type"] = "CIMPictureMarker",
            ["enable"] = true,
            ["size"] = 15,
            ["url"] = TestPictures.DataUri(Split),
        };

        string renderer = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = "CIMSimpleRenderer",
            ["symbol"] = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "CIMSymbolReference",
                ["symbol"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = "CIMPointSymbol",
                    ["symbolLayers"] = new System.Text.Json.Nodes.JsonArray(marker),
                },
            },
        }.ToJsonString();

        SymbologyPlan plan = SymbologyPlan.Compile(renderer);

        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(100, 100);

        canvas.Clear(Rgba.Transparent);

        MapRenderer map = new(canvas, new PixelTransform(new Envelope(0, 0, 100, 100), 100, 100), geographic: false);

        map.Draw(plan, [new Feature("1", new Point(50, 50), new FeatureSchema(["kind"]), ["x"])]);

        byte[] png = canvas.Encode(MapImageFormat.Png, 100);

        // 15 points is 20 pixels, so the picture spans 40 to 60 on both axes.
        Assert.True(Reddish(PixelAt(png, 50, 44)));
        Assert.True(Bluish(PixelAt(png, 50, 56)));
        Assert.Equal(0, PixelAt(png, 50, 38).A);
    }
}
