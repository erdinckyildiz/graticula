using System;
using System.Collections.Generic;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Render.Skia.Tests;

/// <summary>
/// The raster face labels in the scripts the tile face does, from the same fonts, and shapes them.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-100](../../docs/adr/ADR-100-labels-in-more-scripts.md), 2026-09-29.</b> The tile face's
/// glyph ranges became a composite of DejaVu Sans and the Noto families; a WMS map drawn in one
/// set of fonts beside a vector tile of the same layer in another is the thing D-161 was decided
/// to prevent, so the renderer draws from the same list, read from the same file.
/// </para>
/// <para>
/// <b>Asserted by which file drew each run, not by pixels.</b> Pixels pass on any machine that has
/// the script installed, which is every developer machine and not the air-gapped image; the face
/// named is the fact about what this product carries.
/// </para>
/// </remarks>
public sealed class LabelScriptsTests
{
    private static readonly Rgba Ink = new(0, 0, 0, 255);

    private static MapSymbol.Label Label(double size) => new(Ink, size, Rgba.Transparent, 0);

    private static double Width(string text)
    {
        using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(400, 80);

        PixelBox box = canvas.MeasureLabel(text, Label(32), 200, 50);

        return box.MaxX - box.MinX;
    }

    [Theory]
    [InlineData("القاهرة", "NotoSansArabic-Regular.ttf")]
    [InlineData("ירושלים", "NotoSansHebrew-Regular.ttf")]
    [InlineData("Երևան", "NotoSansArmenian-Regular.ttf")]
    [InlineData("თბილისი", "NotoSansGeorgian-Regular.ttf")]
    [InlineData("दिल्ली", "NotoSansDevanagari-Regular.ttf")]
    [InlineData("ঢাকা", "NotoSansBengali-Regular.ttf")]
    [InlineData("சென்னை", "NotoSansTamil-Regular.ttf")]
    [InlineData("กรุงเทพ", "NotoSansThai-Regular.ttf")]
    [InlineData("Kırşehir", "DejaVuSans.ttf")]
    public void A_label_is_drawn_from_the_font_the_tile_face_uses(string label, string file)
    {
        // The same rule make-glyphs.py applies: a script font first in its own blocks, DejaVu
        // first everywhere else. One run, one file, and not the machine's.
        Assert.Equal([file], SkiaMapCanvas.FacesFor(label));
    }

    [Theory]
    [InlineData("القاهرة")]
    [InlineData("दिल्ली")]
    [InlineData("กรุงเทพ")]
    public void A_script_the_stack_carries_never_reaches_the_machines_fonts(string label)
    {
        List<string> said = [];
        SkiaMapCanvas.Missing = missing => said.Add(missing);

        try
        {
            using IMapCanvas canvas = new SkiaMapCanvasFactory().Create(240, 60);

            canvas.Clear(Rgba.Transparent);
            canvas.DrawLabel(label, Label(24), 120, 40);

            Assert.Empty(said);
        }
        finally
        {
            SkiaMapCanvas.Missing = null;
        }
    }

    [Fact]
    public void Arabic_is_shaped_rather_than_drawn_letter_by_letter()
    {
        // <b>Lam followed by alef is one ligature</b> in every Arabic font, and it is narrower
        // than the two letters in their isolated forms side by side — which is what an unshaped
        // renderer draws. Before ADR-100 this label was a row of isolated letters.
        double together = Width("لا");
        double apart = Width("ل") + Width("ا");

        Assert.True(
            together < apart - 4,
            $"lam-alef measured {together:0.0} px against {apart:0.0} for the two letters apart, "
            + "so the shaper did not form the ligature.");
    }

    [Fact]
    public void A_Devanagari_conjunct_is_one_glyph_rather_than_its_parts()
    {
        // <b>क + virama + ष is the conjunct क्ष</b>, one glyph. Unshaped, it is a ka with a
        // visible virama beside a ssa — which is what MapLibre draws from glyph ranges, and ADR-100
        // says so. The raster face shapes, so it is narrower than its parts.
        double conjunct = Width("क्ष");
        double parts = Width("क") + Width("्") + Width("ष");

        Assert.True(
            conjunct < parts - 4,
            $"क्ष measured {conjunct:0.0} px against {parts:0.0} for its parts, so it was not shaped.");
    }

    [Fact]
    public void A_number_in_a_right_to_left_label_is_drawn_on_the_left()
    {
        // <b>Logical order is the word and then the number; the drawing order is the reverse</b>,
        // and the number itself still reads left to right. The digits are DejaVu's, as they are
        // on the tile face.
        Assert.Equal(
            ["DejaVuSans.ttf", "NotoSansArabic-Regular.ttf"],
            SkiaMapCanvas.FacesFor("شارع 15"));
    }

    [Fact]
    public void A_space_inside_an_Arabic_label_does_not_split_it()
    {
        // Taken literally, the first-font rule would draw the space from DejaVu and break the
        // label into three runs, and then the words come out in the wrong order.
        Assert.Equal(["NotoSansArabic-Regular.ttf"], SkiaMapCanvas.FacesFor("مدينة نصر"));
    }

    [Fact]
    public void A_mixed_label_takes_each_script_from_its_own_font()
    {
        Assert.Equal(
            ["DejaVuSans.ttf", "NotoSansDevanagari-Regular.ttf"],
            SkiaMapCanvas.FacesFor("Delhi दिल्ली"));
    }
}
