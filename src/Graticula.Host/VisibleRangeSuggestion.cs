using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;

namespace Graticula.Host;

/// <summary>
/// The scale a layer should stop drawing at when zoomed out, measured from its data — ADR-070 §5.2.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner, 2026-09-14:</b> <i>"biz de koyalım onları. max 2000 feature mesela."</i> ArcGIS Online
/// sets a visible range when a layer is published from a file, <i>"based on the data"</i>, and does
/// not say how. This is how this server does it: the first tile level at which no tile holds more
/// than <see cref="FeaturesPerTile"/> features. <b>10,000 and not 2,000, by the owner's choice the
/// same day</b>, shown what each figure meant on two cities' buildings: 2,000 put both at 1:4,514,
/// street level; 10,000 puts Istanbul at 1:18,056 and New York at 1:9,028 (ADR-070 §4).
/// </para>
/// <para>
/// <b>Counted, not assumed uniform.</b> Dividing Istanbul's 1.27 million buildings by the area of its
/// province's box gives about 1,200 per level-13 tile; the densest level-13 tile measured holds
/// 26,229, because a city is not spread evenly over its province. So the search counts real
/// tiles, starting from the level at which the whole extent fits in one or two, and at each level
/// goes on only into the children of the <see cref="Branches"/> densest. That can miss a dense
/// area whose parent was not among the densest — the answer is an estimate for a default an
/// administrator can change, and is said to be one.
/// </para>
/// <para>
/// <b>Every count goes through the layer's own reader</b>, and so pays its statement timeout and
/// its concurrency budget like any query would; the whole search has a deadline of its own.
/// </para>
/// </remarks>
internal static class VisibleRangeSuggestion
{
    /// <summary>The most features one vector tile may hold at the scale suggested.</summary>
    public const int FeaturesPerTile = 10_000;

    /// <summary>How many of the densest tiles at one level are followed into the next.</summary>
    private const int Branches = 3;

    /// <summary>The most tiles counted at one level.</summary>
    private const int MostTilesPerLevel = 16;

    /// <summary>The deepest level searched; a layer still denser than this is limited to it.</summary>
    private const int DeepestLevel = 20;

    /// <summary>How long the whole search may take.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private const int WebMercator = 3857;
    private const double HalfWorld = 20_037_508.342789244;

    /// <summary>What the search found.</summary>
    /// <param name="MinScale">The suggested minimum scale, 0 for no limit, or null when there is no suggestion.</param>
    /// <param name="Level">The first vector tile level that draws, or null.</param>
    /// <param name="DensestTile">The most features counted in one tile at that level, or null.</param>
    /// <param name="Counted">How many tile counts the search ran.</param>
    /// <param name="Reason">Why there is no suggestion, or null.</param>
    public sealed record Result(double? MinScale, int? Level, long? DensestTile, int Counted, string? Reason);

    /// <summary>Searches for the first level whose densest tile holds at most <see cref="FeaturesPerTile"/>.</summary>
    /// <param name="source">The layer's reader.</param>
    /// <param name="described">What the reader says about the layer, for its extent.</param>
    /// <param name="srid">The layer's reference.</param>
    /// <param name="projector">For putting the extent and tiles into the layer's reference.</param>
    /// <param name="cancellationToken">The caller's.</param>
    /// <returns>The suggestion, or a result with a reason.</returns>
    public static async Task<Result> SuggestAsync(
        IFeatureSource source,
        LayerDescription described,
        int srid,
        IProjector projector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(described);
        ArgumentNullException.ThrowIfNull(projector);

        using CancellationTokenSource deadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Deadline);

        int counted = 0;

        try
        {
            long total = await source.CountUpToAsync(
                new FeatureQuery(1, includeGeometry: false), FeaturesPerTile + 1, deadline.Token)
                .ConfigureAwait(false);
            counted++;

            if (total <= FeaturesPerTile)
            {
                return new Result(0, 0, total, counted, null);
            }

            if (await ServedExtent.InAsync(described.Extent, srid, WebMercator, projector, deadline.Token)
                .ConfigureAwait(false) is not { } extent
                || !(extent.MaxX > extent.MinX || extent.MaxY > extent.MinY))
            {
                return new Result(null, null, null, counted, "the layer's extent is not known, so there are no tiles to count");
            }

            double span = Math.Max(extent.MaxX - extent.MinX, extent.MaxY - extent.MinY);
            int level = Math.Clamp((int)Math.Floor(Math.Log2(2 * HalfWorld / Math.Max(span, 1))), 0, DeepestLevel);
            List<(int X, int Y)> candidates = TilesCovering(extent, level);

            while (candidates.Count > MostTilesPerLevel && level > 0)
            {
                level--;
                candidates = TilesCovering(extent, level);
            }

            while (true)
            {
                int z = level;
                long[] counts = new long[candidates.Count];

                await Parallel.ForEachAsync(
                    Enumerable.Range(0, candidates.Count),
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = deadline.Token },
                    async (i, token) =>
                    {
                        counts[i] = await source.CountAsync(
                            new FeatureQuery(
                                1,
                                includeGeometry: false,
                                spatial: new SpatialFilter(
                                    Rectangle(TileEnvelope(z, candidates[i].X, candidates[i].Y)),
                                    SpatialRelation.EnvelopeIntersects),
                                filterSrid: srid == WebMercator ? null : WebMercator),
                            token).ConfigureAwait(false);
                    }).ConfigureAwait(false);

                counted += candidates.Count;
                long densest = counts.Max();

                if (densest <= FeaturesPerTile || level >= DeepestLevel)
                {
                    return new Result(
                        level == 0 ? 0 : VisibleScaleRange.VectorTileScale(level), level, densest, counted, null);
                }

                candidates = [.. candidates
                    .Select((tile, i) => (tile, count: counts[i]))
                    .Where(t => t.count > FeaturesPerTile)
                    .OrderByDescending(t => t.count)
                    .Take(Branches)
                    .SelectMany(t => Children(t.tile))];
                level++;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new Result(null, null, null, counted, $"counting took longer than {Deadline.TotalSeconds:0} seconds");
        }
    }

    private static IEnumerable<(int X, int Y)> Children((int X, int Y) tile) =>
    [
        (tile.X * 2, tile.Y * 2), (tile.X * 2 + 1, tile.Y * 2),
        (tile.X * 2, tile.Y * 2 + 1), (tile.X * 2 + 1, tile.Y * 2 + 1),
    ];

    private static List<(int X, int Y)> TilesCovering(Envelope extent, int z)
    {
        double size = 2 * HalfWorld / Math.Pow(2, z);
        int last = (1 << z) - 1;
        int x0 = Math.Clamp((int)Math.Floor((extent.MinX + HalfWorld) / size), 0, last);
        int x1 = Math.Clamp((int)Math.Floor((extent.MaxX + HalfWorld) / size), 0, last);
        int y0 = Math.Clamp((int)Math.Floor((HalfWorld - extent.MaxY) / size), 0, last);
        int y1 = Math.Clamp((int)Math.Floor((HalfWorld - extent.MinY) / size), 0, last);

        List<(int, int)> tiles = [];

        for (int x = x0; x <= x1; x++)
        {
            for (int y = y0; y <= y1; y++)
            {
                tiles.Add((x, y));
            }
        }

        return tiles;
    }

    private static Envelope TileEnvelope(int z, int x, int y)
    {
        double size = 2 * HalfWorld / Math.Pow(2, z);

        return new Envelope(
            -HalfWorld + x * size,
            HalfWorld - (y + 1) * size,
            -HalfWorld + (x + 1) * size,
            HalfWorld - y * size);
    }

    private static Polygon Rectangle(Envelope box) =>
        new(new LinearRing(XySequence.Wrap(
        [
            box.MinX, box.MinY,
            box.MaxX, box.MinY,
            box.MaxX, box.MaxY,
            box.MinX, box.MaxY,
            box.MinX, box.MinY,
        ])));
}
