using System;
using Graticula.Api.OgcFeatures;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// A whole-world filter on a layer in a national grid is narrowed to what that grid holds.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-165](../../docs/architecture-debt.md), and the failure it prevents is a silent
/// wrong answer rather than an error.</b> `bbox=-180,-90,180,90` is what the OGC conformance
/// suite sends at every collection, and until now a layer in a projected reference this
/// server had no domain for passed it to `st_transform` unclamped.
/// </para>
/// <para>
/// <b>Measured in PostGIS, and the number is the point.</b> The whole world transformed into
/// EPSG:5254 is
/// <c>POLYGON((500000.0000000002 −10001965.7, 500000.0000000002 10001965.7, …))</c> — every
/// x ordinate identical, because a transverse Mercator has no definition for ±180° and both
/// meridians fold onto the central one. **The filter becomes a zero-width line.**
/// </para>
/// <para>
/// <b>End to end, against a two-row layer in EPSG:5254:</b> unclamped, the whole-world
/// request answered **one** feature — the one whose extent straddles the central meridian,
/// which the sliver happens to cross — and silently dropped the other. 200, well formed, and
/// wrong. Clamped, it answers two.
/// </para>
/// <para>
/// <b>Tested here rather than through a fixture.</b> The alternative is a layer in a national
/// grid with rows on both sides of its central meridian, seeded into every deployment the
/// conformance suite runs against; this asserts the same contract without one.
/// </para>
/// </remarks>
public sealed class ProjectedBboxClampTests
{
    /// <summary>EPSG:5254's area of use, from `postgis_srs` — TUREF / TM30.</summary>
    private static readonly Envelope Turkey = new(28.5, 36.06, 31.5, 41.46);

    private static readonly Envelope World = new(-180, -90, 180, 90);

    private static CollectionMetadata Collection(int srid, Envelope? extent) =>
        new("zz", "zz", null, srid, GeometryKind.Polygon, extent, []);

    /// <summary>
    /// With a domain, the world becomes the area of use.
    /// </summary>
    [Fact]
    public void A_whole_world_filter_is_narrowed_to_the_references_area_of_use()
    {
        Envelope? clamped = OgcFeaturesEndpoints.Clamped(
            World, 4326, Collection(5254, extent: null), Turkey);

        Assert.NotNull(clamped);

        Assert.Equal(Turkey.MinX, clamped!.Value.MinX, 6);
        Assert.Equal(Turkey.MinY, clamped.Value.MinY, 6);
        Assert.Equal(Turkey.MaxX, clamped.Value.MaxX, 6);
        Assert.Equal(Turkey.MaxY, clamped.Value.MaxY, 6);
    }

    /// <summary>
    /// Without one, nothing is narrowed — which is what an older PostGIS gives.
    /// </summary>
    /// <remarks>
    /// <b>The behaviour before any of this existed, asserted so it stays available.</b>
    /// `postgis_srs` arrived in PostGIS 3.4 and this repository declares no minimum version;
    /// a deployment below it answers null and gets exactly what it got yesterday. A repair
    /// that turned *cannot say* into *refuse* would be a worse answer to the same
    /// uncertainty.
    /// </remarks>
    [Fact]
    public void Without_a_domain_the_filter_is_left_alone()
    {
        Envelope? clamped = OgcFeaturesEndpoints.Clamped(
            World, 4326, Collection(5254, extent: null), domain: null);

        Assert.NotNull(clamped);
        Assert.Equal(World.MinX, clamped!.Value.MinX, 6);
        Assert.Equal(World.MaxY, clamped.Value.MaxY, 6);
    }

    /// <summary>
    /// A layer that holds rows knows better than its reference does.
    /// </summary>
    /// <remarks>
    /// <b>The area of use is where the projection is valid, not where the data is.</b> So an
    /// extent, when there is one, is the tighter and truer bound — and this asserts the
    /// domain does not widen it back out.
    /// </remarks>
    [Fact]
    public void An_extent_beats_the_area_of_use()
    {
        Envelope extent = new(30.0, 39.0, 30.5, 39.5);

        Envelope? clamped = OgcFeaturesEndpoints.Clamped(
            World, 4326, Collection(5254, extent), Turkey);

        Assert.NotNull(clamped);
        Assert.Equal(extent.MinX, clamped!.Value.MinX, 6);
        Assert.Equal(extent.MaxX, clamped.Value.MaxX, 6);
    }

    /// <summary>
    /// A filter that does not meet the area of use at all is refused rather than narrowed.
    /// </summary>
    /// <remarks>
    /// <b>Null here means *these do not overlap*, which the caller answers as an empty
    /// collection without a round trip.</b> Distinct from the null above, which means *no
    /// bound is known* — the two are told apart by whether a domain was supplied, and
    /// conflating them would turn an unknown reference into an empty answer.
    /// </remarks>
    [Fact]
    public void A_filter_outside_the_area_of_use_is_disjoint_rather_than_clamped()
    {
        Envelope pacific = new(-170, -80, -160, -70);

        Assert.Null(OgcFeaturesEndpoints.Clamped(
            pacific, 4326, Collection(5254, extent: null), Turkey));
    }
}
