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
        // <b>A thousand, because there is no ceiling to walk to any more.</b> This asked for
        // `MostValues` — 256 — and that constant is gone: ADR-054 withdrew the stored-document
        // cap it was derived from. The property under test never depended on the number, only on
        // there being a lot of them, so the number is now stated here rather than borrowed from
        // a limit that no longer exists.
        const int Many = 1_000;

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        List<Rgba> colours = GenerateRendererEndpoints.Distinct(Many);

        long took = clock.ElapsedMilliseconds;

        Assert.Equal(Many, colours.Count);
        Assert.Equal(Many, colours.Distinct().Count());

        // <b>And it has to be affordable, because this runs inside a request.</b> The grid walk
        // that chooses each colour against everything already taken is quadratic; past a few
        // hundred it stops being worth its cost, which is why the cheap walk takes over.
        // <b>1.5 seconds, against a measured 372 ms here.</b> Loose enough for a shared runner
        // and tight enough to catch what this actually cost before 2026-09-05: filling the whole
        // greedy grid was <b>7.1 s</b> and the square roots inside the distance were another
        // <b>2.0</b>. Both were paid inside a request by every classification ever drawn.
        Assert.True(took < 1_500, $"A thousand colours took {took} ms.");

        // And none of them is black, white or invisible, which is what a bad conversion gives.
        foreach (Rgba one in colours)
        {
            Assert.Equal(255, one.A);
            Assert.True(
                one.R + one.G + one.B > 60 && one.R + one.G + one.B < 720,
                $"{one} is nearly black or nearly white.");
        }
    }

    /// <summary>
    /// The read bound is above what one document can carry, so the fit decides and not the read.
    /// </summary>
    /// <remarks>
    /// <b>This test used to assert the opposite arrangement, and the arrangement changed.</b> It
    /// checked that 256 classes fit inside the stored-document cap — *the ceiling is the document
    /// size rather than a judgement*. ADR-054 withdrew that cap by owner decision, so there is no
    /// ceiling to justify. What is worth pinning instead is the ordering of the two numbers that
    /// are left: `Counted` bounds how many distinct values are read, and the parser's bound is
    /// what the classifier shrinks to fit. Reading fewer than a document can hold would make the
    /// read the silent truncator, which is the shape the old ceiling had and the reason it was
    /// wrong.
    /// </remarks>
    [Fact]
    public void The_values_read_are_more_than_one_document_is_likely_to_hold()
    {
        // Measured on the owner's own data: a CIM unique-value class costs about 646 characters,
        // and a point symbol is the heaviest of the three geometries.
        const int PerClass = 646;

        Assert.True(
            GenerateRendererEndpoints.Counted * PerClass
                > Graticula.Cartography.SymbologyConversion.MaximumReadCharacters,
            $"{GenerateRendererEndpoints.Counted} classes at {PerClass} characters each still fit "
            + "in one request, so the distinct read is what limits a classification rather than "
            + "the fit — and a read limit truncates without saying so.");
    }
}
