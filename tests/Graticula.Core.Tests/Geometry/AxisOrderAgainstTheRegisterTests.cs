using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests.Geometry;

/// <summary>
/// The axis order and the unit come from the authority, not from a number range.
/// </summary>
/// <remarks>
/// <para>
/// <b>[Q-123](../../../docs/open-questions.md), answered 2026-09-09 —
/// [ADR-060](../../../docs/adr/ADR-060-the-axis-order-comes-from-the-register.md).</b> Both
/// questions used to be the same expression, <c>srid is &gt;= 4000 and &lt;= 4999</c>, and both
/// were wrong for two thirds of the codes that matter: **2,125 EPSG codes are north/south-first
/// and 1,444 are outside that block**.
/// </para>
/// <para>
/// <b>The table below is what makes this test worth having.</b> The guard that existed —
/// <c>GateFindingsTests</c>'s theory over 4326, 4258 and 4269 — could not fail for any reason
/// Q-123 describes, because every code in it is inside the block. What is needed is a table
/// spanning all four quadrants: geographic inside, geographic **outside**, projected
/// easting-first, projected **northing**-first. Four of these ten went red against the code as
/// it stood.
/// </para>
/// <para>
/// <b>The expected column is the authority's, read from PROJ's own <c>axis</c> table</b> — not
/// from <c>spatial_ref_sys</c>, which renders in visualisation order, and not from this server's
/// own answer, which would make the test agree with the code by construction.
/// </para>
/// </remarks>
public sealed class AxisOrderAgainstTheRegisterTests
{
    /// <summary>
    /// The authority's answer for a code in each of the four quadrants.
    /// </summary>
    /// <param name="srid">The EPSG code.</param>
    /// <param name="northFirst">Whether the authority lists the north-south ordinate first.</param>
    /// <param name="geographic">Whether it is measured in degrees.</param>
    /// <param name="what">What it is, so a failure names something a person recognises.</param>
    [Theory]

    // ---------------------------------------------------------------- geographic, inside 4000-4999
    [InlineData(4326, true, true, "WGS 84")]
    [InlineData(4258, true, true, "ETRS89")]
    [InlineData(4269, true, true, "NAD83")]

    // ---------------------------------------------------------------- geographic, outside it
    // <b>TUREF, and it is the case this whole answer exists for.</b> Its axis definition is
    // identical to 4326's and its number is 5252, so the old rule transposed it — measured on a
    // running server, one feature, one second apart.
    [InlineData(5252, true, true, "TUREF")]
    [InlineData(3824, true, true, "TWD97")]

    // ---------------------------------------------------------------- projected, easting first
    [InlineData(3857, false, false, "WGS 84 / Pseudo-Mercator")]
    [InlineData(27700, false, false, "OSGB36 / British National Grid")]
    [InlineData(2039, false, false, "Israel 1993 / Israeli TM Grid")]

    // ---------------------------------------------------------------- projected, northing first
    // <b>Every Turkish national grid is in this quadrant</b> — 5251-5259, 5263-5264, 5269-5275 —
    // and the old rule wrote every one of them easting first.
    [InlineData(5253, true, false, "TUREF / TM27")]
    [InlineData(2180, true, false, "ETRS89 / Poland CS92")]
    [InlineData(3006, true, false, "SWEREF99 TM")]
    public void The_authority_decides_the_order_and_the_unit(
        int srid, bool northFirst, bool geographic, string what)
    {
        Assert.True(
            AxisOrder.IsLatitudeFirst(srid) == northFirst,
            $"EPSG:{srid} ({what}) — the authority lists its north-south ordinate "
            + $"{(northFirst ? "first" : "second")} and this server says otherwise. Every "
            + "coordinate served in that reference is transposed, which looks to a reader like "
            + "data in the wrong place rather than like a bug.");

        Assert.True(
            AxisOrder.IsGeographic(srid) == geographic,
            $"EPSG:{srid} ({what}) is {(geographic ? "in degrees" : "not in degrees")} and this "
            + "server says otherwise. That is the scale denominator, the projection domain and "
            + "the units this server reports, all computed from the wrong kind of number.");
    }

    /// <summary>
    /// The two questions are not one question.
    /// </summary>
    /// <remarks>
    /// <b>The shape of the old defect, asserted so it cannot come back as a tidy-up.</b> One
    /// expression answered both, and the table above contains a code in every quadrant precisely
    /// because no single predicate can. If somebody ever collapses them again, this is the test
    /// that says why they cannot: 5253 is north-first and in metres; 3824 is north-first and in
    /// degrees; 3857 is neither.
    /// </remarks>
    [Fact]
    public void North_first_and_in_degrees_are_different_questions()
    {
        Assert.True(AxisOrder.IsLatitudeFirst(5253) && !AxisOrder.IsGeographic(5253));
        Assert.True(AxisOrder.IsLatitudeFirst(3824) && AxisOrder.IsGeographic(3824));
        Assert.True(!AxisOrder.IsLatitudeFirst(3857) && !AxisOrder.IsGeographic(3857));
    }

    /// <summary>
    /// The register is a register and not a range wearing one.
    /// </summary>
    /// <remarks>
    /// <b>A test that would pass against the old expression proves nothing</b>, and this file's
    /// whole value is that it would not. This asserts the shape directly: there are codes
    /// outside 4000–4999 that are north-first, and codes inside it that are not — so no
    /// interval predicate can produce these answers, whatever its bounds.
    /// </remarks>
    [Fact]
    public void No_range_over_the_code_could_answer_this()
    {
        IReadOnlyList<int> outsideAndNorth = [5252, 5253, 2180, 3006, 3824];

        Assert.All(
            outsideAndNorth,
            srid => Assert.True(
                AxisOrder.IsLatitudeFirst(srid) && !(srid is >= 4000 and <= 4999),
                $"EPSG:{srid} is meant to be north-first and outside 4000–4999; it is not both, "
                + "so this test has stopped demonstrating what it was written to demonstrate."));

        // <b>And the other direction.</b> 4000–4999 holds codes that are not geographic 2D —
        // geocentric and geographic 3D among them — which is the half the old expression got
        // wrong in the permissive direction rather than the missing one.
        Assert.False(
            AxisOrder.IsGeographic(4936),
            "EPSG:4936 (ETRS89 geocentric) is metres from the centre of the earth and is inside "
            + "4000–4999. If this server calls it geographic, it will report degrees for a "
            + "reference measured in metres.");
    }
}
