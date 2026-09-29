using System;
using System.Collections.Generic;
using System.Globalization;
using Graticula.Tiles;

namespace Graticula.Host;

/// <summary>
/// How many bytes a seed will add to the tile cache, and whether the cache has room for them —
/// ADR-093 §3, owner decision 2026-09-29.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asked before a seed starts because the cache's budget is enforced by eviction, and eviction is
/// silent.</b> A seed that outgrows the cache does not fail: it makes the cache delete its own tiles —
/// the highest levels it built — and the first sign an operator gets is a read-back whose <c>cached</c>
/// column no longer matches the seed's <c>built</c>. Making room from other services' deeper tiles is
/// what a cache is for and is not refused; see <see cref="Of"/> for where the line is. The count of
/// tiles was already exact and taken before anything started (ADR-093 §5.2); this puts a size beside
/// it, so the question *will it keep what it builds* is answered by the server rather than by somebody
/// multiplying.
/// </para>
/// <para>
/// <b>An estimate, and it says which kind.</b> Where the cache already holds enough of a layer's tiles
/// at a level, their average size is the guess for the rest of that level; where it does not,
/// <see cref="DefaultPartBytes"/> is. A tile already in the cache under the key the seed writes adds
/// nothing and is left out. Empty tiles are zero-length markers and count as the zero they cost, so a
/// sparse layer's average is as low as its cache is.
/// </para>
/// <para>
/// <b>Pure arithmetic, apart from the cache's own counts</b>, so the maths is tested without a disk
/// and without a database (<c>TileSeedEstimateTests</c>).
/// </para>
/// </remarks>
internal static class TileSeedEstimate
{
    /// <summary>How many cached tiles of a layer at a level make their average worth using.</summary>
    /// <remarks>
    /// <b>Sixteen, so that one unusually dense or empty tile does not decide a level.</b> A handful of
    /// tiles from the middle of a city would put the whole level at the city's density; sixteen is
    /// still what a single map view at that level leaves behind, so an operator who has looked at the
    /// area once has an estimate built from it.
    /// </remarks>
    internal const int SamplesNeeded = 16;

    /// <summary>The smallest guess, reached at level 16 and kept above it.</summary>
    private const long Floor = 16 * 1024;

    /// <summary>The largest guess, reached at level 10 and kept below it.</summary>
    private const long Ceiling = 1024 * 1024;

    /// <summary>
    /// What one layer's part of one tile is assumed to cost when the cache holds too few to say —
    /// 16 KB at level 16 and above, doubling for each level below, up to 1 MB.
    /// </summary>
    /// <param name="zoom">The level.</param>
    /// <returns>Bytes.</returns>
    /// <remarks>
    /// <para>
    /// <b>From the one measurement there is, and deliberately below it.</b> ADR-010 §6a's dense
    /// Istanbul tiles were 13 KB at level 16, 196 KB at level 14 and 1.9 MB at level 12
    /// (<c>benchmarks/mvt-generation/RESULTS.md</c>): the middle of a city, where a seed's area also
    /// holds its sparse edges and its empty tiles. The level-16 figure is kept; below it the guess
    /// doubles per level where the dense tile grew about tenfold every two levels, because a whole
    /// area's average is not its densest tile's.
    /// </para>
    /// <para>
    /// <b>Capped at a megabyte, and the cap costs little.</b> A seed's bytes are in its highest levels,
    /// which hold four times the tiles of the level before; the low levels a cap undercounts are a few
    /// tiles each.
    /// </para>
    /// <para>
    /// <b>INFERRED, listed for confirmation in ADR-093 §3</b>: a default somebody chose, which a
    /// measured estate should replace. It stops mattering for a layer at a level as soon as the cache
    /// holds <see cref="SamplesNeeded"/> of its tiles there.
    /// </para>
    /// </remarks>
    internal static long DefaultPartBytes(int zoom) =>
        zoom >= 16 ? Floor : Math.Min(Ceiling, Floor << Math.Min(16 - Math.Max(0, zoom), 16));

    /// <summary>What the cache holds of one layer at one level.</summary>
    /// <param name="Samples">How many of the layer's tiles are cached at the level, whatever key.</param>
    /// <param name="SampleBytes">Their bytes.</param>
    /// <param name="Present">How many of the seed's own tiles at the level are already there.</param>
    internal readonly record struct Holding(long Samples, long SampleBytes, long Present);

    /// <summary>One layer of the service, as the estimate needs it.</summary>
    /// <param name="Draws">Whether the layer is in a tile at a level — ADR-070.</param>
    /// <param name="Held">What the cache holds of it, by level; a level missing holds nothing.</param>
    internal sealed record Layer(Func<int, bool> Draws, IReadOnlyDictionary<int, Holding> Held);

    /// <summary>One level's share.</summary>
    /// <param name="Zoom">The level.</param>
    /// <param name="Bytes">What the seed is expected to add there.</param>
    /// <param name="Sampled">True when every layer drawn there was estimated from its own cached tiles.</param>
    internal sealed record Level(int Zoom, long Bytes, bool Sampled);

    /// <summary>The estimate, and how it compares with the cache.</summary>
    /// <param name="Levels">Each level's share, lowest first.</param>
    /// <param name="Bytes">The whole seed.</param>
    /// <param name="Budget">The cache's budget.</param>
    /// <param name="Used">What the cache holds now.</param>
    /// <param name="Free">The budget less what it holds, never below zero — information; the fit is not
    /// decided by it alone.</param>
    /// <param name="Protected">What the cache holds, written by the pipeline running now, at levels below
    /// the seed's highest — the bytes eviction keeps in preference to the seed's top level.</param>
    /// <param name="Target">What eviction brings the cache down to: 90% of the budget.</param>
    /// <param name="Fits">Whether the seed keeps every tile it builds.</param>
    /// <param name="HighestLevelThatFits">
    /// The highest level a seed from the same first level could go to and keep every tile, or null when
    /// the first level alone would evict some of its own.
    /// </param>
    internal sealed record Result(
        IReadOnlyList<Level> Levels,
        long Bytes,
        long Budget,
        long Used,
        long Free,
        long Protected,
        long Target,
        bool Fits,
        int? HighestLevelThatFits)
    {
        /// <summary>Whether every level was estimated from the cache's own tiles.</summary>
        public bool Sampled
        {
            get
            {
                foreach (Level level in Levels)
                {
                    if (!level.Sampled)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }

    /// <summary>Estimates a seed.</summary>
    /// <param name="ranges">The seed's rectangle at each level, lowest first.</param>
    /// <param name="layers">The service's layers.</param>
    /// <param name="budget">The cache's budget, in bytes.</param>
    /// <param name="used">What the cache holds now, in bytes.</param>
    /// <param name="heldByLevel">What the cache holds now at each level, of the pipeline running now —
    /// <c>FileSystemTileCache.CurrentBytesByLevel</c>.</param>
    /// <returns>The estimate.</returns>
    /// <remarks>
    /// <para>
    /// <b>A level's bytes are summed over the layers drawn there</b>, because a tile is cached per
    /// layer (<c>VectorTileEndpoints.TileAsync</c>) and every drawn layer adds its own part. A level no
    /// layer draws at adds nothing, as the seed skips it.
    /// </para>
    /// <para>
    /// <b>What <em>fits</em> means: the seed keeps every tile it builds</b> — the owner's worry was a seed
    /// evicting its own tiles, not a seed evicting anybody's. Comparing with the free space alone made
    /// almost every seed on a warm cache refuse, because an LRU cache that has run a while sits near its
    /// budget; but eviction goes highest level first (<c>FileSystemTileCache.EvictIfOverBudgetAsync</c>),
    /// so a seed makes room from whatever sits above it and loses a tile of its own only when what
    /// outranks that tile, with the seed below it, does not fit.
    /// </para>
    /// <para>
    /// <b>So a level <c>L</c> of the seed is kept when</b> the seed's bytes up to <c>L</c> fit in the free
    /// space — nothing is evicted at all — <b>or</b> when those bytes, plus everything the cache holds at
    /// levels below <c>L</c>, fit in the 90% eviction brings it down to. Below <c>L</c> means every service's
    /// tiles, the seed's own levels included: under the order, each of them outranks every tile of the seed
    /// at <c>L</c>. Tiles already at <c>L</c> itself do not count, because within a level the least recently
    /// used goes first and the seed's are the newest. Both sides grow with <c>L</c>, so the levels that
    /// are kept are always a run from the first, and the seed fits when its highest level is kept.
    /// </para>
    /// <para>
    /// <b>Why every level below <c>L</c>, and not only those below the seed's first level.</b> Another
    /// service's tiles at a level inside the seed's range outrank the seed's higher levels: a seed of
    /// levels 10 to 14 over a cache full of other maps' level-10 tiles loses its own level 14 before
    /// their level 10. Counting only what lies below level 10 would call that seed a fit.
    /// </para>
    /// </remarks>
    internal static Result Of(
        IReadOnlyList<TileRange> ranges,
        IReadOnlyList<Layer> layers,
        long budget,
        long used,
        IReadOnlyDictionary<int, long> heldByLevel)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(heldByLevel);

        List<Level> levels = [];
        long total = 0;
        long free = Math.Max(0, budget - used);
        long target = FileSystemTileCache.TargetOf(budget);
        long running = 0;
        long below = 0;
        int? highest = null;
        bool stillFits = true;

        foreach (TileRange range in ranges)
        {
            long bytes = 0;
            bool sampled = true;

            foreach (Layer layer in layers)
            {
                if (!layer.Draws(range.Z))
                {
                    continue;
                }

                Holding held = layer.Held.TryGetValue(range.Z, out Holding found) ? found : default;
                long missing = Math.Max(0, range.Count - held.Present);

                long each;

                if (held.Samples >= SamplesNeeded)
                {
                    each = held.SampleBytes / held.Samples;
                }
                else
                {
                    each = DefaultPartBytes(range.Z);
                    sampled = false;
                }

                bytes = Saturating(bytes, Saturating(missing, each, multiply: true));
            }

            levels.Add(new Level(range.Z, bytes, sampled));
            total = Saturating(total, bytes);
            running = Saturating(running, bytes);
            below = BelowOf(heldByLevel, range.Z);

            if (stillFits && (running <= free || Saturating(below, running) <= target))
            {
                highest = range.Z;
            }
            else
            {
                stillFits = false;
            }
        }

        return new Result(
            levels, total, budget, used, free, below, target, levels.Count == 0 || stillFits, highest);
    }

    /// <summary>What the cache holds at levels strictly below a level.</summary>
    private static long BelowOf(IReadOnlyDictionary<int, long> heldByLevel, int zoom)
    {
        long sum = 0;

        foreach (KeyValuePair<int, long> level in heldByLevel)
        {
            if (level.Key < zoom)
            {
                sum = Saturating(sum, level.Value);
            }
        }

        return sum;
    }

    /// <summary>A byte count as a person reads it: KB under a megabyte, MB under a gigabyte, GB above.</summary>
    /// <param name="bytes">The count.</param>
    /// <returns>The text.</returns>
    internal static string Size(long bytes) => bytes switch
    {
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, bytes) / 1024.0:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1073741824.0:0.##} GB"),
    };

    /// <summary>Adds, or multiplies, without wrapping past <see cref="long.MaxValue"/>.</summary>
    /// <remarks>
    /// <b>Because a wrapped estimate is a negative one, and a negative estimate fits.</b> Nothing a
    /// seed's cap allows comes near it — 250,000 tiles of a megabyte is a quarter of a terabyte — but
    /// the cap is a setting and this is the line that would turn a raised one into a green light.
    /// </remarks>
    private static long Saturating(long a, long b, bool multiply = false)
    {
        try
        {
            return checked(multiply ? a * b : a + b);
        }
        catch (OverflowException)
        {
            return long.MaxValue;
        }
    }
}
