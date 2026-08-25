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

    [Theory]
    [InlineData("Kırşehir", "Turkish")]
    [InlineData("Αθήνα", "Greek")]
    [InlineData("Москва", "Cyrillic")]
    public void The_bundled_face_draws_the_scripts_this_product_promises(string label, string script)
    {
        // <b>[D-161](../../docs/architecture-debt.md), owner decision 2026-08-25.</b> The
        // face travels with the assembly, so these three draw on an image with no system
        // fonts at all — which is the air-gapped case Q-15 ended on. Asserted through the
        // substitution hook rather than only through pixels: pixels alone would pass on a
        // machine that happens to have the script installed, which is a fact about the
        // runner and not about what this product carries.
        // <b>The face is asserted to be present, not inferred from pixels.</b> This test
        // passed with the resource removed until this line existed: the machine it runs
        // on has Turkish, Greek and Cyrillic system fonts, so the label drew from those
        // and nothing was reported. D-161's whole point is the machine with neither.
        Assert.Equal("DejaVu Sans", SkiaMapCanvas.BundledFace);

        List<string> said = [];
        SkiaMapCanvas.Missing = missing => said.Add(missing);

        try
        {
            using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(200, 50);

            canvas.Clear(Rgba.Transparent);
            canvas.DrawLabel(label, new MapSymbol.Label(Ink, 20, Rgba.Transparent, 0), 100, 32);

            Assert.True(
                said.Count == 0,
                $"{script} reached the substitution path, so the bundled face did not draw "
                + "it and the deployment is relying on the machine's fonts for a script "
                + "this product says it carries.");

            Assert.True(
                InkedPixels(canvas.Encode(MapImageFormat.Png, 90)) > 0,
                $"A {script} label drew nothing.");
        }
        finally
        {
            SkiaMapCanvas.Missing = null;
        }
    }

    [Fact]
    public void Cjk_is_not_promised_and_says_so_when_absent()
    {
        // <b>The other half of the decision, asserted so it stays a decision.</b> The
        // owner chose Latin, Turkish, Greek and Cyrillic; the file that was already here
        // carries Arabic as well, measured, and does not carry Han. So CJK is the one
        // script this product does not promise. That is only defensible while the
        // absence is loud,
        // so this pins the shape rather than the outcome: on a machine with CJK fonts
        // the substitution succeeds and nothing is said, and on one without it is named.
        // What must never happen is boxes with silence, and `FontFor` cannot produce
        // that: it either finds a face or calls the hook.
        List<string> said = [];
        SkiaMapCanvas.Missing = missing => said.Add(missing);

        try
        {
            using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(200, 60);

            canvas.Clear(Rgba.Transparent);
            canvas.DrawLabel("東京", new MapSymbol.Label(Ink, 28, Rgba.Transparent, 0), 100, 40);

            bool drew = InkedPixels(canvas.Encode(MapImageFormat.Png, 90)) > 0;

            Assert.True(
                drew || said.Count > 0,
                "A CJK label was neither drawn nor reported, which is the silent-boxes "
                + "failure D-161 exists to keep impossible.");
        }
        finally
        {
            SkiaMapCanvas.Missing = null;
        }
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
