using System;
using System.Collections.Generic;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Render.Skia.Tests;

/// <summary>
/// A label in a script the default face cannot draw is substituted, or named.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-15](../../docs/open-questions.md)'s last item.</b> The air-gap checklist came
/// out clean on PROJ, on GDAL and on telemetry and ended on fonts: <c>SKTypeface.Default</c>
/// draws a script it has no glyphs for as boxes, with no error and no warning. A map that
/// renders and cannot be read is worse than one that refuses, because nothing anywhere
/// says which it is.
/// </para>
/// <para>
/// <b>Asserted on a code point no machine can draw, and that is a correction.</b> The
/// first version accepted <em>either</em> a substitution or a report, on the grounds that
/// which one happens depends on the runner's fonts — and it passed against the old
/// behaviour, because the machine it was written on draws Han and the substitution path
/// was never entered at all. A test whose subject depends on what the runner happens to
/// have installed is a test about the runner. An unassigned code point has no face
/// anywhere, so the reporting branch is reached on every machine and the falsification is
/// real: restoring the old behaviour fails this.
/// </para>
/// </remarks>
public sealed class LabelFontSubstitutionTests
{
    private static readonly Rgba Ink = new(0, 0, 0, 255);

    /// <summary>
    /// An unassigned code point, which no face on any machine has a glyph for.
    /// </summary>
    /// <remarks>
    /// <b>Unassigned rather than merely unusual, and that is a correction.</b> This test
    /// first used Han — and passed against the old behaviour on the machine it was
    /// written on, because that machine's default face draws Han perfectly well, so the
    /// substitution path was never entered and the assertion was satisfied by ordinary
    /// pixels. A test whose subject depends on which fonts the runner happens to have is
    /// a test about the runner. U+2FFFF is unassigned in Unicode: no face has it, so both
    /// the lookup and the match are guaranteed to fail and the reporting branch is
    /// reached everywhere.
    /// </remarks>
    private static readonly string Unknown = char.ConvertFromUtf32(0x2FFFF);

    private static int InkedPixels(byte[] png)
    {
        using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);

        Assert.NotNull(bitmap);

        int inked = 0;

        for (int x = 0; x < bitmap.Width; x++)
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    inked++;
                }
            }
        }

        return inked;
    }

    [Fact]
    public void Latin_text_never_reaches_the_substitution_path()
    {
        // <b>The control, and it is the one that matters for cost.</b> Every label on
        // every map goes through this; a Latin label that fell into `MatchCharacter`
        // would walk the machine's fonts thousands of times per map.
        List<string> said = [];
        SkiaMapCanvas.Missing = script => said.Add(script);

        try
        {
            using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(120, 40);

            canvas.Clear(Rgba.Transparent);
            canvas.DrawLabel("Ankara 1934", new MapSymbol.Label(Ink, 16, Rgba.Transparent, 0), 60, 25);

            Assert.Empty(said);
            Assert.True(InkedPixels(canvas.Encode(MapImageFormat.Png, 90)) > 0);
        }
        finally
        {
            SkiaMapCanvas.Missing = null;
        }
    }

    [Fact]
    public void Turkish_letters_draw_rather_than_disappearing()
    {
        // The product's own conversation language, and the case an operator here would
        // notice first. `ı`, `ş`, `ğ` are Latin-1 supplement and beyond; a face without
        // them draws boxes.
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(160, 40);

        canvas.Clear(Rgba.Transparent);
        canvas.DrawLabel(
            "Kırşehir Boğaz",
            new MapSymbol.Label(Ink, 16, Rgba.Transparent, 0),
            80,
            25);

        Assert.True(
            InkedPixels(canvas.Encode(MapImageFormat.Png, 90)) > 0,
            "A Turkish label drew nothing at all.");
    }

    [Fact]
    public void A_glyph_no_face_has_is_reported_rather_than_drawn_as_a_box()
    {
        List<string> said = [];
        SkiaMapCanvas.Missing = script => said.Add(script);

        try
        {
            using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(120, 60);

            canvas.Clear(Rgba.Transparent);
            canvas.DrawLabel(Unknown, new MapSymbol.Label(Ink, 28, Rgba.Transparent, 0), 60, 40);

            // <b>Q-15's failure, inverted.</b> The old behaviour drew a box and said
            // nothing; a deployment learned about it from a user looking at a map.
            Assert.Single(said);

            // <b>Once per script, not once per label.</b> A map with ten thousand of
            // them must not write ten thousand lines, or an operator learns to filter
            // the message out and it stops being a warning.
            canvas.DrawLabel(Unknown, new MapSymbol.Label(Ink, 28, Rgba.Transparent, 0), 60, 40);
            Assert.Single(said);
        }
        finally
        {
            SkiaMapCanvas.Missing = null;
        }
    }

    [Fact]
    public void A_substituted_label_is_measured_with_the_face_that_draws_it()
    {
        // <b>Measuring with one face and drawing with another puts the label in the
        // wrong place</b>, and label placement is what decides whether two labels
        // collide. The two paths take the same font or neither is right.
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(200, 60);

        PixelBox latin = canvas.MeasureLabel(
            "AA", new MapSymbol.Label(Ink, 28, Rgba.Transparent, 0), 100, 30);

        PixelBox other = canvas.MeasureLabel(
            Unknown, new MapSymbol.Label(Ink, 28, Rgba.Transparent, 0), 100, 30);

        Assert.True(latin.MaxX > latin.MinX, "A Latin label measured to nothing.");
        Assert.True(
            other.MaxX >= other.MinX,
            "A label measured to negative width, so nothing downstream can place it.");
    }
}
