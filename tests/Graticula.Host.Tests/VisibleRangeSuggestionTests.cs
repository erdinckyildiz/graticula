using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-070 §5.2: the zoomed-out limit a layer is given at publish, counted tile by tile.
/// </summary>
/// <remarks>
/// Against a fake source holding points in Web Mercator, so the counts are exact and the expected level
/// can be worked out by hand; the providers' own counts are what the other suites test.
/// </remarks>
public sealed class VisibleRangeSuggestionTests
{
    private const double HalfWorld = 20_037_508.342789244;

    private sealed class Points(IReadOnlyList<(double X, double Y)> points) : IFeatureSource
    {
        private int _counts;

        public int Counts => _counts;

        public FeatureSchema SchemaFor(FeatureQuery query) => throw new NotSupportedException();

        public IAsyncEnumerable<Feature> ReadAsync(FeatureQuery query, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<LayerDescription> DescribeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<long> CountAsync(FeatureQuery query, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _counts);
            Envelope box = query.Spatial!.Geometry.Envelope;

            return Task.FromResult((long)points.Count(p =>
                p.X >= box.MinX && p.X <= box.MaxX && p.Y >= box.MinY && p.Y <= box.MaxY));
        }

        public Task<long> CountUpToAsync(FeatureQuery query, long ceiling, CancellationToken cancellationToken) =>
            Task.FromResult(Math.Min(points.Count, ceiling));
    }

    private static LayerDescription Described(IReadOnlyList<(double X, double Y)> points) =>
        new([], new Envelope(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y)));

    private static double TileSize(int z) => 2 * HalfWorld / Math.Pow(2, z);

    [Fact]
    public async Task A_layer_no_bigger_than_one_tile_s_worth_draws_at_every_scale()
    {
        List<(double, double)> points = [.. Enumerable.Range(0, VisibleRangeSuggestion.FeaturesPerTile).Select(i => (i * 10.0, i * 10.0))];

        VisibleRangeSuggestion.Result result = await VisibleRangeSuggestion.SuggestAsync(
            new Points(points), Described(points), 3857, new UnusedProjector(), CancellationToken.None);

        Assert.Equal(0, result.MinScale);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task A_dense_cluster_decides_the_level_even_when_the_extent_is_mostly_empty()
    {
        // 25,000 points filling one level-16 tile, and two outliers stretching the extent across a
        // continent. Counted, the cluster overfills every tile it sits in until the tiles split it.
        double cell = TileSize(16);
        double originX = -HalfWorld + 40_000 * cell;
        double originY = HalfWorld - 25_000 * cell;
        List<(double X, double Y)> points =
        [
            .. Enumerable.Range(0, 25_000).Select(i => (originX + (i % 250 + 0.5) * cell / 250, originY - (i / 250 + 0.5) * cell / 100)),
            (-2_000_000, 4_000_000),
            (3_000_000, 7_000_000),
        ];

        Points source = new(points);
        VisibleRangeSuggestion.Result result = await VisibleRangeSuggestion.SuggestAsync(
            source, Described(points), 3857, new UnusedProjector(), CancellationToken.None);

        // The cluster is one level-16 tile wide: at 16 it is still 25,000 in one tile, at 17 it splits
        // into four quarters of 6,250.
        Assert.Equal(17, result.Level);
        Assert.Equal(VisibleScaleRange.VectorTileScale(17), result.MinScale!.Value, 6);
        Assert.True(result.DensestTile <= VisibleRangeSuggestion.FeaturesPerTile);

        // It follows the dense branches rather than counting every tile of the extent at every level.
        Assert.True(source.Counts < 200, $"{source.Counts} counts");
    }

    [Fact]
    public async Task A_layer_with_no_extent_gets_no_suggestion_and_says_why()
    {
        List<(double, double)> points = [.. Enumerable.Range(0, VisibleRangeSuggestion.FeaturesPerTile + 1).Select(i => (0.0, 0.0))];

        VisibleRangeSuggestion.Result result = await VisibleRangeSuggestion.SuggestAsync(
            new Points(points), new LayerDescription([], null), 3857, new UnusedProjector(), CancellationToken.None);

        Assert.Null(result.MinScale);
        Assert.NotNull(result.Reason);
    }

    private sealed class UnusedProjector : IProjector
    {
        public Task<IReadOnlyList<Geometry>> GeneralizeAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, double tolerance, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(IReadOnlyList<Geometry> Projected, ProjectionProvenance Provenance)> ProjectAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            throw new NotSupportedException("a layer in 3857 is never projected");

        public Task<IReadOnlyList<Geometry>?> ProjectToDefinitionAsync(
            IReadOnlyList<Geometry> geometries, int fromSrid, string definition, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> KnowsAsync(int srid, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<Envelope?> DomainOfAsync(int srid, CancellationToken cancellationToken) => Task.FromResult<Envelope?>(null);

        public Task<IReadOnlyList<KnownReference>> ReferencesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<KnownReference>>([]);

        public Task<ProjectionProvenance> DescribeAsync(int fromSrid, int toSrid, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectionProvenance("none", null));
    }
}
