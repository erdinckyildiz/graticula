using System;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Api.Wfs.Tests;

/// <summary>
/// The bbox parameter, which is the filter almost every client actually sends.
/// </summary>
public sealed class WfsBoundingBoxTests
{
    private static SpatialFilter Ok(string text, int defaultSrid, out int srid)
    {
        Assert.True(
            WfsBoundingBox.TryParse(text, defaultSrid, out SpatialFilter? filter, out srid,
                out WfsFault? fault),
            fault?.Text);

        Assert.NotNull(filter);
        return filter!;
    }

    private static Envelope EnvelopeOf(SpatialFilter filter) => filter.Geometry.Envelope;

    [Fact]
    public void Four_numbers_in_a_projected_reference_are_easting_first()
    {
        Envelope box = EnvelopeOf(Ok("100,200,300,400", 3857, out int srid));

        Assert.Equal(3857, srid);
        Assert.Equal(100, box.MinX);
        Assert.Equal(200, box.MinY);
        Assert.Equal(300, box.MaxX);
        Assert.Equal(400, box.MaxY);
    }

    [Fact]
    public void In_4326_the_first_number_is_a_latitude()
    {
        // <b>The reason this has its own reader.</b> Reading a 4326 bbox as
        // easting-first works perfectly until the answer is empty, and then says
        // nothing about why — the same silent failure Q-96 recorded for tiles.
        Envelope box = EnvelopeOf(Ok("35,25,43,45", 4326, out _));

        Assert.Equal(25, box.MinX);
        Assert.Equal(35, box.MinY);
        Assert.Equal(45, box.MaxX);
        Assert.Equal(43, box.MaxY);
    }

    [Fact]
    public void A_fifth_field_names_the_reference_and_decides_the_order()
    {
        Envelope box = EnvelopeOf(Ok("35,25,43,45,urn:ogc:def:crs:EPSG::4326", 3857, out int srid));

        Assert.Equal(4326, srid);
        Assert.Equal(25, box.MinX);
        Assert.Equal(35, box.MinY);
    }

    [Theory]
    [InlineData("EPSG:3857")]
    [InlineData("http://www.opengis.net/def/crs/EPSG/0/3857")]
    [InlineData("urn:ogc:def:crs:EPSG::3857")]
    [InlineData("3857")]
    public void Every_spelling_of_a_reference_a_client_might_send_resolves(string crs)
    {
        Ok($"1,2,3,4,{crs}", 4326, out int srid);

        Assert.Equal(3857, srid);
    }

    [Fact]
    public void A_bbox_is_the_envelope_test_because_that_is_what_wfs_defines_it_as()
    {
        // Asking for the exact relation would be a slower question than the client
        // asked, and a different one.
        Assert.Equal(SpatialRelation.EnvelopeIntersects, Ok("1,2,3,4", 3857, out _).Relation);
    }

    [Fact]
    public void An_absent_bbox_is_not_a_refusal()
    {
        Assert.True(WfsBoundingBox.TryParse(null, 3857, out SpatialFilter? filter, out _, out _));
        Assert.Null(filter);

        Assert.True(WfsBoundingBox.TryParse("", 3857, out filter, out _, out _));
        Assert.Null(filter);
    }

    [Theory]
    [InlineData("1,2,3")]
    [InlineData("1,2,3,4,5,6")]
    [InlineData("a,2,3,4")]
    public void A_malformed_bbox_is_refused_with_what_is_wrong(string text)
    {
        Assert.False(WfsBoundingBox.TryParse(text, 3857, out _, out _, out WfsFault? fault));
        Assert.Equal(WfsFaultCode.InvalidParameterValue, fault!.Code);
        Assert.Equal("bbox", fault.Locator);
    }

    [Fact]
    public void An_inverted_box_is_refused_rather_than_silently_matching_nothing()
    {
        // A box whose upper corner is below its lower one matches nothing, and a
        // client that transposed its arguments would read that as "no data here".
        Assert.False(
            WfsBoundingBox.TryParse("10,10,0,0", 3857, out _, out _, out WfsFault? fault));

        Assert.Contains("lower corner", fault!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unrecognisable_reference_is_refused_rather_than_defaulted()
    {
        // Defaulting a CRS is how a filter ends up compared against data in
        // another one.
        Assert.False(
            WfsBoundingBox.TryParse("1,2,3,4,NAD83", 3857, out _, out _, out WfsFault? fault));

        Assert.Contains("urn:ogc:def:crs:EPSG", fault!.Text, StringComparison.Ordinal);
    }
}
