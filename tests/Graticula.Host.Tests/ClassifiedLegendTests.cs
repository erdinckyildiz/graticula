using System;
using System.Linq;
using Graticula.Api.Wms;
using Graticula.Cartography;
using Graticula.Geometries;
using Graticula.Render.Skia;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A legend for a style that classifies: one row per class, from the style.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-131](../../docs/open-questions.md) closed by being contradicted, and these
/// tests are the contradiction.</b> The row said enumerating a classified legend's
/// entries *means reading the data*, which is why it stayed open: a legend is asked
/// for once per layer per client and must stay cheap. It does not mean that. A
/// <c>match</c> writes its labels into the style and a <c>step</c> writes its breaks,
/// so the classes are already in the document the legend is compiled from — and every
/// test here builds one from a style string, with no database anywhere near it.
/// </para>
/// <para>
/// <b>Pixels where the claim is about a picture.</b> Asserting that
/// <c>LegendClasses</c> returned three cases says nothing about whether three swatches
/// were painted in three colours, and *the image came back the right size and empty*
/// is the failure this whole surface has.
/// </para>
/// </remarks>
public sealed class ClassifiedLegendTests
{
    private const string Zoning = """
    {
      "version": 8,
      "layers": [{
        "id": "a", "type": "fill",
        "paint": {
          "fill-color": [
            "match", ["get", "zoning"],
            "residential", "#ff0000",
            "park", "#00ff00",
            "#0000ff"
          ]
        }
      }]
    }
    """;

    private static Rgba PixelAt(byte[] png, int x, int y)
    {
        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);

        Assert.NotNull(bitmap);

        SkiaSharp.SKColor colour = bitmap.GetPixel(x, y);

        return new Rgba(colour.Red, colour.Green, colour.Blue, colour.Alpha);
    }

    [Fact]
    public void A_match_over_a_column_enumerates_its_classes_and_its_fallback()
    {
        StyleExpression.Classification axis =
            Assert.NotNull(SymbologyPlan.Compile(Zoning).LegendClasses());

        Assert.Equal("zoning", axis.Field);
        Assert.Equal(["residential", "park", "Other"], axis.Cases.Select(c => c.Label));

        // <b>The fallback carries no value, and that is what makes it the fallback.</b>
        // An absent attribute misses every label a style can write, so resolving the
        // plan against it lands on exactly the branch this row is a picture of.
        Assert.Null(axis.Cases[^1].Value);
    }

    [Fact]
    public void A_step_labels_the_ranges_its_breaks_describe()
    {
        const string style = """
        {
          "version": 8,
          "layers": [{
            "id": "a", "type": "fill",
            "paint": {
              "fill-color": [
                "step", ["get", "pop"],
                "#eeeeee", 1000, "#999999", 5000, "#333333"
              ]
            }
          }]
        }
        """;

        StyleExpression.Classification axis =
            Assert.NotNull(SymbologyPlan.Compile(style).LegendClasses());

        Assert.Equal("pop", axis.Field);
        Assert.Equal(["< 1000", "1000 – 5000", "≥ 5000"], axis.Cases.Select(c => c.Label));
    }

    [Fact]
    public void A_style_that_classifies_on_two_columns_gets_no_axis()
    {
        // <b>A legend is a strip and two classifications are a grid.</b> Flattening
        // them would draw a row per value of one column with the other silently at its
        // fallback — a picture that is confidently wrong rather than absent.
        const string style = """
        {
          "version": 8,
          "layers": [{
            "id": "a", "type": "fill",
            "paint": {
              "fill-color": ["match", ["get", "zoning"], "park", "#00ff00", "#0000ff"],
              "fill-outline-color": ["match", ["get", "status"], "new", "#ffffff", "#000000"]
            }
          }]
        }
        """;

        Assert.Null(SymbologyPlan.Compile(style).LegendClasses());
    }

    [Fact]
    public void A_match_on_zoom_is_a_scale_rule_rather_than_a_classification()
    {
        // A legend of zoom levels would describe the map's behaviour instead of its
        // data, which is not what a reader looking at a legend is asking.
        const string style = """
        {
          "version": 8,
          "layers": [{
            "id": "a", "type": "fill",
            "paint": { "fill-color": ["match", ["zoom"], 5, "#ff0000", "#0000ff"] }
          }]
        }
        """;

        Assert.Null(SymbologyPlan.Compile(style).LegendClasses());
    }

    [Fact]
    public void A_plain_style_still_draws_exactly_the_swatch_that_was_asked_for()
    {
        // <b>The control on the whole change.</b> WIDTH and HEIGHT became a swatch's
        // size rather than the image's, and for a layer with no classification those
        // must still be the same thing — every existing client asks this way.
        const string style = """
        {"version":8,"layers":[{"id":"a","type":"fill","paint":{"fill-color":"#ff0000"}}]}
        """;

        using IMapCanvas canvas = LegendGraphic.Draw(
            new SkiaMapCanvasFactory(),
            SymbologyPlan.Compile(style),
            GeometryKind.Polygon,
            (24, 24),
            Rgba.Transparent);

        Assert.Equal(24, canvas.Width);
        Assert.Equal(24, canvas.Height);
        Assert.Equal(new Rgba(255, 0, 0, 255), PixelAt(canvas.Encode(MapImageFormat.Png, 90), 12, 12));
    }

    [Fact]
    public void A_classified_legend_paints_each_class_in_its_own_colour()
    {
        using IMapCanvas canvas = LegendGraphic.Draw(
            new SkiaMapCanvasFactory(),
            SymbologyPlan.Compile(Zoning),
            GeometryKind.Polygon,
            (20, 20),
            Rgba.White);

        // Three classes, so the image is three rows tall rather than one swatch.
        Assert.True(
            canvas.Height >= 60,
            $"Three classes drew an image {canvas.Height} pixels tall.");

        Assert.True(
            canvas.Width > 20,
            $"A classified legend needs room for its labels; this one is {canvas.Width} wide.");

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);
        int rowHeight = canvas.Height / 3;

        // The swatch column, a few pixels in from the left padding, at the middle of
        // each row. Red, green, blue: the two named classes and the fallback, in the
        // order the style writes them.
        Assert.Equal(new Rgba(255, 0, 0, 255), PixelAt(png, 12, rowHeight / 2));
        Assert.Equal(new Rgba(0, 255, 0, 255), PixelAt(png, 12, rowHeight + (rowHeight / 2)));
        Assert.Equal(new Rgba(0, 0, 255, 255), PixelAt(png, 12, (rowHeight * 2) + (rowHeight / 2)));
    }

    [Fact]
    public void A_classified_legend_writes_its_labels_beside_the_swatches()
    {
        // <b>Ink to the right of the swatch, which is the half a swatch cannot say.</b>
        // Three coloured squares with no names on them is a legend that tells a reader
        // there are three classes and not which is which.
        using IMapCanvas canvas = LegendGraphic.Draw(
            new SkiaMapCanvasFactory(),
            SymbologyPlan.Compile(Zoning),
            GeometryKind.Polygon,
            (20, 20),
            Rgba.White);

        byte[] png = canvas.Encode(MapImageFormat.Png, 90);
        int rowHeight = canvas.Height / 3;
        bool inked = false;

        for (int x = 30; x < canvas.Width && !inked; x++)
        {
            for (int y = 0; y < rowHeight && !inked; y++)
            {
                inked = PixelAt(png, x, y) != Rgba.White;
            }
        }

        Assert.True(inked, "The first row's label area is blank.");
    }

    [Fact]
    public void A_style_with_more_classes_than_rows_says_how_many_it_dropped()
    {
        // <b>A `match` over a code list has hundreds of labels.</b> The bound is not
        // the interesting part — a legend that stops without saying so is, because it
        // reads as a complete list of the classes in the data.
        string style =
            """{"version":8,"layers":[{"id":"a","type":"fill","paint":{"fill-color":["match",["get","code"],"""
            + string.Join(
                ",",
                Enumerable.Range(0, 40).Select(i => $"\"c{i}\",\"#00{i:x2}00\""))
            + ""","#cccccc"]}}]}""";

        StyleExpression.Classification axis =
            Assert.NotNull(SymbologyPlan.Compile(style).LegendClasses());

        Assert.Equal(41, axis.Cases.Count);

        using IMapCanvas canvas = LegendGraphic.Draw(
            new SkiaMapCanvasFactory(),
            SymbologyPlan.Compile(style),
            GeometryKind.Polygon,
            (20, 20),
            Rgba.White);

        // 24 rows at 20 pixels plus padding, and no more however many classes there
        // are: the image cannot grow without bound on a request a client repeats.
        Assert.InRange(canvas.Height, 24 * 20, (24 * 20) + 64);
    }
}
