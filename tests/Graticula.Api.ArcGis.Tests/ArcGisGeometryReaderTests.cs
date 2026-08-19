using System;
using System.Text.Json;
using Graticula.Api.ArcGis;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.ArcGis.Tests;

/// <summary>
/// What the reader accepts, and — mostly — what it refuses.
/// </summary>
/// <remarks>
/// This is the entry point of the write path, so every leniency here is a way
/// for a client to destroy data it did not know it had. The refusals are the
/// interesting half.
/// </remarks>
public sealed class ArcGisGeometryReaderTests
{
    private const int Srid = 3857;

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static Geometry Read(string text)
    {
        Assert.True(
            ArcGisGeometryReader.TryRead(Json(text), Srid, out Geometry? geometry, out string? error),
            error);

        return geometry!;
    }

    private static string Refuse(string text)
    {
        Assert.False(
            ArcGisGeometryReader.TryRead(Json(text), Srid, out _, out string? error));

        Assert.False(string.IsNullOrWhiteSpace(error), "A refusal must say why.");
        return error!;
    }

    // ---------- what it accepts ----------

    [Fact]
    public void A_point_reads()
    {
        Point point = Assert.IsType<Point>(Read("""{"x":1.5,"y":2.5}"""));

        Assert.Equal(1.5, point.X);
        Assert.Equal(2.5, point.Y);
    }

    [Fact]
    public void A_null_x_is_an_empty_point_not_an_error()
    {
        // ArcGIS spells an empty geometry this way, and a feature may
        // legitimately have attributes and no location.
        Assert.True(Assert.IsType<Point>(Read("""{"x":null}""")).IsEmpty);
    }

    [Fact]
    public void One_path_is_a_LineString_and_two_are_a_MultiLineString()
    {
        Assert.IsType<LineString>(Read("""{"paths":[[[0,0],[1,1]]]}"""));
        Assert.IsType<MultiLineString>(Read("""{"paths":[[[0,0],[1,1]],[[2,2],[3,3]]]}"""));
    }

    [Fact]
    public void A_clockwise_ring_is_a_shell()
    {
        Polygon polygon = Assert.IsType<Polygon>(
            Read("""{"rings":[[[0,0],[0,10],[10,10],[10,0],[0,0]]]}"""));

        Assert.Empty(polygon.Holes);
        Assert.False(polygon.Shell.IsCounterClockwise);
    }

    [Fact]
    public void A_counter_clockwise_ring_after_a_shell_is_its_hole()
    {
        Polygon polygon = Assert.IsType<Polygon>(Read(
            """
            {"rings":[
              [[0,0],[0,10],[10,10],[10,0],[0,0]],
              [[2,2],[4,2],[4,4],[2,4],[2,2]]
            ]}
            """));

        LinearRing hole = Assert.Single(polygon.Holes);
        Assert.True(hole.IsCounterClockwise);
    }

    [Fact]
    public void Two_shells_are_a_MultiPolygon_and_each_keeps_its_own_hole()
    {
        // The reconstruction Q-90 is about. Four rings could equally be read as
        // one polygon with three holes, which is a valid geometry nobody sent.
        MultiPolygon multi = Assert.IsType<MultiPolygon>(Read(
            """
            {"rings":[
              [[0,0],[0,10],[10,10],[10,0],[0,0]],
              [[2,2],[4,2],[4,4],[2,4],[2,2]],
              [[20,0],[20,10],[30,10],[30,0],[20,0]],
              [[22,2],[24,2],[24,4],[22,4],[22,2]]
            ]}
            """));

        Assert.Equal(2, multi.Parts.Count);
        Assert.Single(multi.Parts[0].Holes);
        Assert.Single(multi.Parts[1].Holes);
    }

    [Fact]
    public void An_unclosed_ring_is_closed_rather_than_refused()
    {
        // The one leniency, and it is safe because it is not a guess: a ring is
        // closed by definition, so the missing vertex is already implied. Real
        // clients send these, and refusing would fail an edit for a reason the
        // user cannot act on.
        Polygon polygon = Assert.IsType<Polygon>(
            Read("""{"rings":[[[0,0],[0,10],[10,10],[10,0]]]}"""));

        ReadOnlySpan<double> xy = polygon.Shell.Coordinates.AsSpan();

        Assert.Equal(xy[0], xy[^2]);
        Assert.Equal(xy[1], xy[^1]);
    }

    [Fact]
    public void A_matching_spatial_reference_is_accepted()
    {
        Read("""{"x":1,"y":2,"spatialReference":{"wkid":3857}}""");
    }

    [Fact]
    public void An_absent_spatial_reference_means_the_layer_own()
    {
        // What every ArcGIS client assumes and every ArcGIS server does.
        Read("""{"x":1,"y":2}""");
    }

    // ---------- what it refuses ----------

    [Fact]
    public void A_declared_Z_is_refused_rather_than_dropped()
    {
        // ADR-008 §4.5a on the way in. Accepting this would store the geometry
        // flat and tell the client it succeeded.
        Assert.Contains("Z or M", Refuse("""{"hasZ":true,"x":1,"y":2}"""), StringComparison.Ordinal);
    }

    [Fact]
    public void An_undeclared_third_ordinate_is_refused_too()
    {
        // The sneakier version: hasZ absent, but the positions carry three
        // numbers. Trusting the flag alone would let this through.
        Assert.Contains(
            "did not declare",
            Refuse("""{"paths":[[[0,0,5],[1,1,6]]]}"""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_spatial_reference_is_refused_rather_than_reprojected()
    {
        // Reprojecting on write moves somebody's geometry as a side effect of
        // saving it, and the client cannot tell that it happened.
        string error = Refuse("""{"x":1,"y":2,"spatialReference":{"wkid":4326}}""");

        Assert.Contains("does not reproject", error, StringComparison.Ordinal);
        Assert.Contains("4326", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hole_before_any_shell_is_refused_rather_than_promoted()
    {
        // Counter-clockwise first. Promoting it to a shell would produce a
        // feature the client did not send; it is genuinely malformed.
        Assert.Contains(
            "before the shell",
            Refuse("""{"rings":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}"""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_ring_with_too_few_positions_is_refused()
    {
        Assert.Contains(
            "at least 4",
            Refuse("""{"rings":[[[0,0],[1,1],[0,0]]]}"""),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"rings":[]}""")]
    [InlineData("""{"paths":[]}""")]
    [InlineData("""{"points":[]}""")]
    public void An_empty_part_list_is_refused(string json) => Refuse(json);

    [Fact]
    public void A_geometry_with_no_recognisable_member_is_refused_with_the_list()
    {
        string error = Refuse("""{"xmin":0,"ymin":0,"xmax":1,"ymax":1}""");

        Assert.Contains("rings", error, StringComparison.Ordinal);
        Assert.Contains("Envelope", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_coordinate_is_refused()
    {
        Refuse("""{"x":"1","y":2}""");
        Refuse("""{"paths":[[[0,0],["a",1]]]}""");
    }

    [Fact]
    public void A_point_with_x_and_no_y_is_refused()
    {
        Assert.Contains("no 'y'", Refuse("""{"x":1}"""), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_refusal_says_something_the_caller_can_act_on()
    {
        // The same guard the error classifier has. A per-feature failure in a
        // batch of five hundred is only useful if it names what to change.
        foreach (string json in (string[])
            ["""{"hasZ":true,"x":1,"y":2}""",
             """{"x":1,"y":2,"spatialReference":{"wkid":4326}}""",
             """{"rings":[[[0,0],[10,0],[10,10],[0,10],[0,0]]]}""",
             """{"rings":[]}""",
             """{"nonsense":1}"""])
        {
            Assert.True(Refuse(json).Length > 30, $"the refusal for {json} is too terse.");
        }
    }

    // ---------- Esri's own codes for Web Mercator ----------

    /// <summary>
    /// 102100 and 102113 are accepted for a 3857 layer, and a genuinely different code is not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found by the independent §66 Correctness gate, 2026-08-19, and it had a measurable symptom:
    /// the same spatial reference was honoured for an envelope filter and refused for a polygon.</b>
    /// `FeatureServerQueryParameters` canonicalised 102100/102113 to 3857 for `inSR`, `outSR` and
    /// envelope filters — its own comment says *comparing the numbers alone refuses the SDK on every
    /// request against a 3857 layer, which is exactly what happened* — while this reader, which every
    /// **non-envelope** filter and every **`applyEdits`** geometry goes through, compared integers.
    /// </para>
    /// <para>
    /// <b>So an ordinary edit from a Web Mercator ArcGIS client was refused.</b> `Geometry.toJSON()` in
    /// the ArcGIS JS API routinely carries `spatialReference: {wkid: 102100}`, and this is the one check
    /// standing in front of every add and update. There is now one implementation of the rule, in this
    /// class, and the query parser defers to it.
    /// </para>
    /// <para>
    /// <b>4326 is in the theory because the rule must not become *accept anything*.</b> A geometry in
    /// degrees against a layer in metres is a real refusal — reprojecting it would move the feature —
    /// and a fix that swallowed it would be worse than the defect.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(102100, true)]
    [InlineData(102113, true)]
    [InlineData(3857, true)]
    [InlineData(4326, false)]
    public void Esris_own_web_mercator_codes_are_the_layers_own(int wkid, bool accepted)
    {
        string text =
            "{\"x\":1.5,\"y\":2.5,\"spatialReference\":{\"wkid\":"
            + wkid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}";

        bool read = ArcGisGeometryReader.TryRead(
            Json(text), 3857, out Geometry? geometry, out string? error);

        Assert.True(
            read == accepted,
            accepted
                ? $"wkid {wkid} is Esri's own code for the projection 3857 names, and every ArcGIS "
                  + $"client sends it: {error}"
                : $"wkid {wkid} is a different coordinate system and reprojecting it would move the "
                  + "geometry, so it must be refused rather than accepted.");

        if (accepted)
        {
            Assert.NotNull(geometry);
        }
        else
        {
            // And the refusal names both codes, because *send it in the layer's own reference* is not
            // actionable when the client believes it already did.
            Assert.Contains("102100", error!, StringComparison.Ordinal);
        }
    }
}
