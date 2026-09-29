using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Host;
using Graticula.Platform.Catalog;
using Graticula.Platform.Identity;
using Graticula.Providers.PostGis;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-096 at the host: what a request for a scheme becomes, and what a scheme does to a tile's key and a
/// seed's record.
/// </summary>
/// <remarks>
/// Without a database or a server: the grid arithmetic is <c>VectorTileSchemeTests</c>', the envelope SQL is
/// <c>ATileSchemeIsCutInItsOwnReferenceTests</c>' against PostGIS, and a live service switched between grids
/// is <c>ATilingSchemeIsTheServiceSGridTests</c>'.
/// </remarks>
public sealed class TilingSchemeTests
{
    private static readonly GeoParquetSources Sources = new(null);

    private static readonly IReadOnlyList<FieldDescription> Attributes =
    [
        new FieldDescription("objectid", FieldType.Integer, false, null),
        new FieldDescription("name", FieldType.Text, true, 80),
    ];

    private static PublishedLayer Layer(Guid id) =>
        new(
            id,
            new LayerDefinition("parcels", "public", "parcels", "geom", 5254, "objectid", "objectid", isHosted: true),
            "datastore",
            "Host=db;Database=gis",
            GeometryKind.Polygon,
            owner: null,
            SharingScope.Public,
            ServiceStatus.Started);

    private static VectorTileScheme Tm30 => VectorTileSchemes.Find("turef-tm30")!.Scheme;

    // ---------- the cache key ----------

    [Fact]
    public void A_Mercator_key_is_what_it_was_and_another_grid_s_is_not()
    {
        Guid id = Guid.NewGuid();
        TileAddress tile = new(8, 150, 99);

        TileCacheKey before = VectorTileEndpoints.KeyOf(Layer(id), Attributes, tile, Sources);
        TileCacheKey mercator = VectorTileEndpoints.KeyOf(Layer(id), Attributes, tile, Sources, VectorTileScheme.WebMercator);
        TileCacheKey tm30 = VectorTileEndpoints.KeyOf(Layer(id), Attributes, tile, Sources, Tm30);
        TileCacheKey gk10 = VectorTileEndpoints.KeyOf(
            Layer(id), Attributes, tile, Sources, VectorTileSchemes.Find("turef-gk10")!.Scheme);

        // No grid is written for Web Mercator, so the fingerprint is the one computed before ADR-096.
        Assert.Equal(before, mercator);
        Assert.Equal(
            TileCacheKey.FingerprintOf(5254, "geom", ["objectid", "name"], PostGisTileSource.Extent, PostGisTileSource.Buffer),
            mercator.Fingerprint);

        // Every other grid has its own, so a service switched between them never reads the other's tile —
        // on this node or on one the switch's purge did not reach.
        Assert.NotEqual(mercator.Fingerprint, tm30.Fingerprint);
        Assert.NotEqual(tm30.Fingerprint, gk10.Fingerprint);
        Assert.NotEqual(mercator.Path(), tm30.Path());
    }

    // ---------- a seed records its grid ----------

    [Fact]
    public void A_seed_s_detail_says_which_grid_its_area_is_on_and_a_Mercator_seed_s_says_nothing()
    {
        Assert.Equal("0123456789ab", TileSeeder.SchemeOf("""{"service":"roads","scheme":"0123456789ab"}"""));
        Assert.Null(TileSeeder.SchemeOf("""{"service":"roads","minZoom":0}"""));
        Assert.Null(TileSeeder.SchemeOf("not json"));
        Assert.Null(TileSeeder.SchemeOf(null));
    }

    [Fact]
    public void A_service_s_default_levels_are_its_scheme_s()
    {
        PublishedService tm30 = new(
            Guid.NewGuid(), "parcels", null, "FeatureServer", null, null,
            SharingScope.Public, ServiceStatus.Started, [Layer(Guid.NewGuid())])
        {
            TileScheme = Tm30,
        };

        // Nothing limits the layer, so every level draws: 0 up to the seed ceiling of 14, inside TM30's 0–16.
        Assert.Equal((0, AdminEndpointsDefaults.Ceiling), AdminEndpointsDefaults.Of(tm30));
    }

    // ---------- what a request asks for ----------

    [Fact]
    public async Task Nothing_asked_for_and_webmercator_by_name_are_both_Web_Mercator()
    {
        Assert.Same(VectorTileScheme.WebMercator, (await AskAsync(null)).Scheme);
        Assert.Same(VectorTileScheme.WebMercator, (await AskAsync(new(null, null, null, null, null, null))).Scheme);
        Assert.Same(VectorTileScheme.WebMercator, (await AskAsync(new("WebMercator", null, null, null, null, null))).Scheme);
        Assert.Same(VectorTileScheme.WebMercator, (await AskAsync(new(null, 3857, null, null, null, null))).Scheme);
    }

    [Fact]
    public async Task A_built_in_is_asked_for_by_name()
    {
        (VectorTileScheme? scheme, string? refusal) = await AskAsync(new("turef-tm30", null, null, null, null, null));

        Assert.Null(refusal);
        Assert.Equal(Tm30.Fingerprint, scheme!.Fingerprint);

        (_, string? unknown) = await AskAsync(new("turef-tm31", null, null, null, null, null));
        Assert.Contains("turef-tm30", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_with_numbers_beside_it_is_refused_rather_than_read_one_way()
    {
        (_, string? refusal) = await AskAsync(new("turef-tm30", 5254, new(0, 0), 10, 3, null));

        Assert.Contains("not both", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_custom_grid_halving_and_one_with_a_list_are_both_taken()
    {
        (VectorTileScheme? halving, string? a) = await AskAsync(new(null, 5254, new(400_000, 4_600_000), 100, 4, null));

        Assert.Null(a);
        Assert.Equal([100.0, 50.0, 25.0, 12.5], halving!.Resolutions);
        Assert.Equal(VectorTileScheme.CustomId, halving.Id);

        (VectorTileScheme? listed, string? b) = await AskAsync(new(null, 5254, new(400_000, 4_600_000), null, null, [100.0, 30.0]));

        Assert.Null(b);
        Assert.Equal([100.0, 30.0], listed!.Resolutions);

        (_, string? both) = await AskAsync(new(null, 5254, new(0, 0), 100, 4, [100.0]));
        Assert.Contains("not both", both, StringComparison.Ordinal);

        (_, string? noOrigin) = await AskAsync(new(null, 5254, null, 100, 4, null));
        Assert.Contains("origin", noOrigin, StringComparison.Ordinal);

        (_, string? geographic) = await AskAsync(new(null, 4326, new(0, 0), 1, 3, null));
        Assert.Contains("geographic", geographic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reference_the_projector_does_not_know_is_refused()
    {
        (_, string? refusal) = await AskAsync(new(null, 999_999, null, null, null, null));

        Assert.Contains("999999", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wkid_alone_is_derived_from_its_area_of_use()
    {
        // The fake projector moves 5254's area of use (28.5–31.5°E, 36.06–41.46°N) onto the box TM30's
        // built-in was derived from, so the grid derived here must be the built-in's.
        (VectorTileScheme? derived, string? refusal) = await AskAsync(new(null, 5254, null, null, null, null));

        Assert.Null(refusal);
        Assert.Equal(Tm30.Fingerprint, derived!.Fingerprint);
        Assert.Equal(VectorTileScheme.CustomId, derived.Id);

        // A reference whose area of use this deployment cannot say is refused with what to send instead.
        (_, string? unknown) = await AskAsync(new(null, 2320, null, null, null, null));
        Assert.Contains("origin", unknown, StringComparison.Ordinal);
    }

    private static Task<(VectorTileScheme? Scheme, string? Refusal)> AskAsync(TilingSchemeRequest? request) =>
        AdminEndpoints.SchemeFromAsync(request, new Tm30Projector(), CancellationToken.None);

    /// <summary>The seed-default reader, reached through the endpoint class's own function.</summary>
    private static class AdminEndpointsDefaults
    {
        public const int Ceiling = AdminEndpoints.DefaultSeedCeiling;

        public static (int, int)? Of(PublishedService service) =>
            AdminEndpoints.DefaultSeedLevels(service) is { } levels ? (levels.Min, levels.Max) : null;
    }

    /// <summary>
    /// Knows every code but 999999; says 5254's area of use; projects degrees onto TM30 by an affine map that
    /// sends that area's corners to the built-in's projected box.
    /// </summary>
    private sealed class Tm30Projector : IProjector
    {
        private static readonly Envelope Degrees = new(28.5, 36.06, 31.5, 41.46);

        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken)
        {
            Assert.Equal(4326, fromSrid);
            Assert.Equal(5254, toSrid);

            Envelope box = VectorTileSchemes.Find("turef-tm30")!.Projected;

            IReadOnlyList<Geometry> moved = [.. geometries.Cast<Point>().Select(p => (Geometry)new Point(
                box.MinX + ((p.X - Degrees.MinX) / (Degrees.MaxX - Degrees.MinX) * (box.MaxX - box.MinX)),
                box.MinY + ((p.Y - Degrees.MinY) / (Degrees.MaxY - Degrees.MinY) * (box.MaxY - box.MinY))))];

            return Task.FromResult((moved, new ProjectionProvenance("test", null)));
        }

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(srid != 999_999);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) =>
            Task.FromResult<Envelope?>(srid == 5254 ? Degrees : null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectionProvenance("test", null));
    }
}
