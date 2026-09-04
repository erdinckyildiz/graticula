using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Cartography;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// One colour per class, for as many classes as a document can hold.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by the owner asking why they could not colour a layer by its name.</b> The
/// unique-value ceiling was 64, chosen on the reasoning that a field with hundreds of values is
/// *usually an identifier*. Turkey has 81 provinces, and one colour per province is an ordinary
/// map that the ceiling refused outright.
/// </para>
/// <para>
/// <b>Raising it exposed the second half.</b> The palette was seven colours repeated at three
/// quarters of the lightness, which for eighty classes is twelve rounds of dimming and pairs
/// nobody can tell apart — so the ceiling was hiding a palette that could not have served the
/// classes it was about to be asked for.
/// </para>
/// </remarks>
public sealed class DistinctColoursTests
{
    [Fact]
    public void The_first_seven_are_the_palette_somebody_chose()
    {
        // A map of five categories should get the colours picked to survive the common
        // colour-blindnesses, not the output of a formula.
        List<Rgba> colours = GenerateRendererEndpoints.Distinct(7);

        for (int i = 0; i < 7; i++)
        {
            (byte red, byte green, byte blue) =
                GeneratedSymbology.Bytes(GeneratedSymbology.Palette[i]);

            Assert.Equal(new Rgba(red, green, blue, 255), colours[i]);
        }
    }

    [Fact]
    public void Eighty_one_provinces_get_eighty_one_different_colours()
    {
        List<Rgba> colours = GenerateRendererEndpoints.Distinct(81);

        Assert.Equal(81, colours.Count);
        Assert.Equal(81, colours.Distinct().Count());
    }

    [Fact]
    public void No_two_classes_are_closer_than_the_eye_can_split()
    {
        // <b>Measured, not asserted.</b> The old palette repeated seven hues dimmed a quarter
        // each round; at eighty classes that puts pairs within a handful of units of each other
        // in RGB, which is two classes the reader reads as one. The threshold below is a
        // straight RGB distance — crude, and crude is enough to catch a repeat.
        List<Rgba> colours = GenerateRendererEndpoints.Distinct(81);

        double worst = Closest(colours);

        Assert.True(
            worst > 40,
            $"The closest pair of eighty-one classes is {worst:0.#} apart in RGB. Two classes "
            + "that close read as one.");
    }

    /// <summary>The distance between the two nearest colours in a palette.</summary>
    private static double Closest(List<Rgba> colours)
    {
        double worst = double.MaxValue;

        for (int i = 0; i < colours.Count; i++)
        {
            for (int j = i + 1; j < colours.Count; j++)
            {
                worst = Math.Min(worst, Math.Sqrt(
                    Math.Pow(colours[i].R - colours[j].R, 2)
                    + Math.Pow(colours[i].G - colours[j].G, 2)
                    + Math.Pow(colours[i].B - colours[j].B, 2)));
            }
        }

        return worst;
    }

    [Fact]
    public void The_worst_pair_is_reported_so_the_number_is_a_measurement()
    {
        // <b>The guarantee narrows with the count, and that is honest rather than a defect.</b>
        // 720 colours on the grid have to serve however many classes are asked for; with more
        // classes there is simply less room. What matters is that the rule takes the best that
        // is left rather than following a formula off a cliff.
        foreach (int classes in (int[])[8, 20, 81, 256])
        {
            List<Rgba> colours = GenerateRendererEndpoints.Distinct(classes);
            double worst = Closest(colours);

            Assert.True(
                worst > 20,
                $"At {classes} classes the closest pair is {worst:0.#} apart in RGB.");
        }
    }

    [Fact]
    public void The_hues_stay_spread_all_the_way_to_the_ceiling()
    {
        // The golden angle's whole property: consecutive multiples never land near each other,
        // so the spread holds at any count rather than degrading once a palette runs out.
        List<Rgba> colours = GenerateRendererEndpoints.Distinct(
            GenerateRendererEndpoints.MostValues);

        Assert.Equal(GenerateRendererEndpoints.MostValues, colours.Count);
        Assert.Equal(GenerateRendererEndpoints.MostValues, colours.Distinct().Count());

        // And none of them is black, white or invisible, which is what a bad conversion gives.
        foreach (Rgba one in colours)
        {
            Assert.Equal(255, one.A);
            Assert.True(
                one.R + one.G + one.B > 60 && one.R + one.G + one.B < 720,
                $"{one} is nearly black or nearly white.");
        }
    }

    [Fact]
    public void The_ceiling_is_the_document_size_rather_than_a_judgement()
    {
        // <b>256 fits every geometry inside the stored-document cap.</b> Measured: a
        // unique-value class costs 478 characters for a polygon symbol and 690 for a point, so
        // the document runs out at about 548 and 379 classes. The constant is not taste.
        Assert.True(
            GenerateRendererEndpoints.MostValues * 690
                < Graticula.Cartography.SymbologyConversion.MaximumCharacters,
            $"{GenerateRendererEndpoints.MostValues} point classes at 690 characters each do "
            + "not fit in a stored document, so the ceiling promises something the store will "
            + "refuse.");
    }
}
