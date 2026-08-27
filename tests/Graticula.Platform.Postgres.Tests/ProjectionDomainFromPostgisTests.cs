using System;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Geometries;
using Graticula.Providers.PostGis;
using Xunit;

namespace Graticula.Platform.Postgres.Tests;

/// <summary>
/// A projected reference's area of use comes from the projection database.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-165](../../docs/architecture-debt.md).</b> `ProjectionDomain` answers what a
/// reference can hold for geographic references and Web Mercator, from arithmetic, and
/// **null for every projected one** — and null means *do not clamp*. So a caller asking a
/// layer in EPSG:2180 or 5254 for `bbox=-180,-90,180,90` had its filter passed to
/// `st_transform` unclamped, which is what the OGC conformance suite sends at every
/// collection.
/// </para>
/// <para>
/// <b>`postgis_srs` publishes the answer and it is authoritative.</b> The reference's own
/// area of use, in degrees, from the same projection database that performs the transform —
/// no table of ours to maintain and no chance of the two disagreeing.
/// </para>
/// <para>
/// <b>What is asserted is the box, not that a box exists.</b> A test that only checked for
/// non-null would pass against a function returning the whole world, which is the answer
/// that changes nothing.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ProjectionDomainFromPostgisTests : PostgresFixture
{
    private PostGisProjector Projector() => new(DataSource);

    /// <summary>
    /// A national grid answers its own country, not the world.
    /// </summary>
    /// <remarks>
    /// <b>EPSG:5254 is TUREF / TM30, and this deployment already publishes a layer in it.</b>
    /// The row that opened this debt says the case is *one empty table away*, and the number
    /// below is why it matters: a filter of the whole world reaching `st_transform` for a
    /// reference that covers three degrees of longitude is asking the projection to represent
    /// points it has no definition for.
    /// </remarks>
    [Theory]
    [InlineData(5254, 28.5, 36.06, 31.5, 41.46)]
    [InlineData(2180, 14.14, 49.0, 24.15, 55.93)]
    public async Task A_projected_reference_answers_its_area_of_use(
        int srid, double west, double south, double east, double north)
    {
        Envelope? domain = await Projector().DomainOfAsync(srid, CancellationToken.None);

        Assert.True(
            domain is not null,
            $"EPSG:{srid} has no area of use here, so a whole-world filter still reaches "
            + "st_transform unclamped. postgis_srs arrived in PostGIS 3.4; on an older one "
            + "this is the documented answer and D-165 stands.");

        Envelope box = domain!.Value;

        Assert.Equal(west, box.MinX, 2);
        Assert.Equal(south, box.MinY, 2);
        Assert.Equal(east, box.MaxX, 2);
        Assert.Equal(north, box.MaxY, 2);
    }

    /// <summary>
    /// Web Mercator answers the latitude the tile scheme is built on.
    /// </summary>
    /// <remarks>
    /// <b>The control, and it is the one reference where two sources can be compared.</b>
    /// `ProjectionDomain` computes ±85.051° from arctan(sinh(π)); the projection database
    /// says ±85.06. They agree to two decimals, which is what makes the arithmetic path
    /// trustworthy for the cases the database is never asked about.
    /// </remarks>
    [Fact]
    public async Task Web_mercator_agrees_with_the_arithmetic_this_server_already_had()
    {
        Envelope? asked = await Projector().DomainOfAsync(3857, CancellationToken.None);

        Assert.NotNull(asked);

        Envelope computed = ProjectionDomain.Of(3857)!.Value;

        Assert.Equal(computed.MinX, asked!.Value.MinX, 2);
        Assert.Equal(computed.MaxX, asked.Value.MaxX, 2);
        Assert.Equal(computed.MinY, asked.Value.MinY, 1);
        Assert.Equal(computed.MaxY, asked.Value.MaxY, 1);
    }

    /// <summary>
    /// A code the projection database has never heard of answers nothing.
    /// </summary>
    /// <remarks>
    /// <b>And it answers a row of nulls rather than no row</b>, measured against
    /// `EPSG:999999` — so the implementation checks the ordinates and not the row count. A
    /// version that checked whether a row came back would have called the whole world the
    /// area of use of a reference that does not exist.
    /// </remarks>
    [Fact]
    public async Task A_reference_that_does_not_exist_answers_nothing()
    {
        Assert.Null(await Projector().DomainOfAsync(999999, CancellationToken.None));
    }
}
