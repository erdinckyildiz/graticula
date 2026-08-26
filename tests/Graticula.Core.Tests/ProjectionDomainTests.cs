using Graticula.Geometries;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// What a reference can represent, which is not what it is meant to be used for.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-164](../../docs/architecture-debt.md), 2026-08-26.</b> EPSG:3857 is used for the
/// whole world and cannot represent a latitude of 90°: the projection sends the pole to
/// infinity. `bbox=-180,-90,180,90` is how an OGC API Features client asks for everything,
/// and asking a Web Mercator layer for it made PostGIS raise and the server answer 400 — but
/// only on a layer with no rows, because a layer with rows had its own extent to clamp
/// against first.
/// </para>
/// <para>
/// <b>Null means *do not clamp*, and reading it as *nothing is representable* would empty
/// every query.</b> That is the one way this file could be misused, so it is asserted rather
/// than left to the doc comment.
/// </para>
/// </remarks>
public sealed class ProjectionDomainTests
{
    [Theory]
    [InlineData(3857)]
    [InlineData(102100)]
    public void Web_mercator_stops_short_of_the_pole(int srid)
    {
        Envelope domain = Assert.NotNull(ProjectionDomain.Of(srid));

        Assert.Equal(-180, domain.MinX);
        Assert.Equal(180, domain.MaxX);

        // <b>arctan(sinh(π)), the latitude that makes the map square.</b> A defining
        // property of the projection rather than a number from a register, which is why
        // this file can carry it without becoming a table that goes stale.
        Assert.Equal(-85.05112877980659, domain.MinY, 9);
        Assert.Equal(85.05112877980659, domain.MaxY, 9);

        // The pole is outside it, which is the whole reason this exists.
        Assert.True(domain.MaxY < 90);
    }

    [Theory]
    [InlineData(4326)]
    [InlineData(4258)]
    [InlineData(4269)]
    public void A_geographic_reference_holds_the_whole_sphere(int srid)
    {
        Envelope domain = Assert.NotNull(ProjectionDomain.Of(srid));

        Assert.Equal(-180, domain.MinX);
        Assert.Equal(-90, domain.MinY);
        Assert.Equal(180, domain.MaxX);
        Assert.Equal(90, domain.MaxY);
    }

    [Theory]
    [InlineData(2180)]
    [InlineData(3006)]
    [InlineData(27700)]
    [InlineData(5254)]
    public void A_reference_this_server_cannot_answer_for_says_so(int srid)
    {
        // <b>Null is *do not clamp*.</b> A caller that read it as *nothing is representable*
        // would clamp every bounding box to nothing and answer no features anywhere — the
        // silent wrong answer this whole change exists to avoid. D-164 stays open for these.
        Assert.Null(ProjectionDomain.Of(srid));
    }

    [Fact]
    public void The_limit_is_the_one_every_tile_scheme_uses()
    {
        // Stated as a constant so a caller can name it, and pinned so a well-meant rounding
        // to 85.05 does not shift a tile boundary by 130 metres.
        Assert.Equal(85.05112877980659, ProjectionDomain.WebMercatorLatitude, 12);
    }
}
