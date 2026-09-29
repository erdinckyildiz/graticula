using System;
using System.Collections.Generic;
using System.Globalization;
using Graticula.Geometries;

namespace Graticula.Tiles;

/// <summary>
/// The tiles of one zoom level that an area touches, as a rectangle of the grid.
/// </summary>
/// <param name="Z">The level.</param>
/// <param name="MinX">The westmost column.</param>
/// <param name="MinY">The northmost row — rows count down from the top, as a tile address does.</param>
/// <param name="MaxX">The eastmost column, inclusive.</param>
/// <param name="MaxY">The southmost row, inclusive.</param>
/// <remarks>
/// <para>
/// <b>A rectangle rather than a list, because a list is the thing a seed cannot afford.</b> A seed at
/// the cap is a quarter of a million addresses; held as a list per level they are memory spent to
/// say what four numbers already say. <see cref="At"/> turns an index back into an address, so the
/// only state a seed keeps per level is how far along the rectangle it has got — ADR-093 §5.3.
/// </para>
/// <para>
/// <b>Row by row, west to east and then north to south.</b> Any fixed order would do for resuming;
/// this one is the order a person reads a map in, so a seed stopped half-way has filled the northern
/// half of the area, which is a state somebody can describe.
/// </para>
/// </remarks>
public readonly record struct TileRange(int Z, int MinX, int MinY, int MaxX, int MaxY)
{
    /// <summary>How many columns the range spans.</summary>
    public long Width => (long)MaxX - MinX + 1;

    /// <summary>How many rows the range spans.</summary>
    public long Height => (long)MaxY - MinY + 1;

    /// <summary>How many tiles the range holds.</summary>
    public long Count => Width * Height;

    /// <summary>The address of the tile at a position in the range's order.</summary>
    /// <param name="index">0 up to <see cref="Count"/>, exclusive.</param>
    /// <returns>The address.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the range.</exception>
    public TileAddress At(long index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);

        long row = index / Width;
        long column = index % Width;

        return new TileAddress(Z, (int)(MinX + column), (int)(MinY + row));
    }

    /// <summary>Whether an address is inside the range.</summary>
    /// <param name="address">The address.</param>
    /// <returns>True when it is.</returns>
    public bool Contains(TileAddress address) =>
        address.Z == Z
        && address.X >= MinX && address.X <= MaxX
        && address.Y >= MinY && address.Y <= MaxY;

    /// <inheritdoc/>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"z{Z} x{MinX}–{MaxX} y{MinY}–{MaxY} ({Count} tiles)");
}

/// <summary>
/// Which tiles a seed of an area over a range of levels covers, and in what order — ADR-093.
/// </summary>
/// <remarks>
/// <para>
/// <b>Pure arithmetic, and the count is exact.</b> ADR-093 §5.2 refuses a seed larger than the cap
/// before it starts, and a refusal that says *about* how many tiles it would have been is one an
/// operator cannot narrow against. So the count is the sum of the levels' rectangles, computed the
/// same way the seed walks them, and nothing is estimated.
/// </para>
/// <para>
/// <b>Low levels first — [ADR-010](../../../docs/adr/ADR-010-caching.md) §6a.</b> A low level is
/// the most expensive tile to build and the one every map at every zoom passes through, so a seed
/// interrupted after its first hour has done the hour that mattered most.
/// </para>
/// </remarks>
public sealed class TileSeedPlan
{
    /// <summary>
    /// The largest latitude Web Mercator draws, in degrees — the square's north and south edges.
    /// </summary>
    public const double MaximumLatitude = 85.0511287798066;

    private TileSeedPlan(IReadOnlyList<TileRange> levels, Envelope area)
    {
        Levels = levels;
        Area = area;

        long total = 0;

        foreach (TileRange level in levels)
        {
            total += level.Count;
        }

        Total = total;
    }

    /// <summary>One rectangle per level, lowest level first.</summary>
    public IReadOnlyList<TileRange> Levels { get; }

    /// <summary>The area, in Web Mercator, clipped to the square the grid covers.</summary>
    public Envelope Area { get; }

    /// <summary>How many tiles the whole seed covers.</summary>
    public long Total { get; }

    /// <summary>
    /// The plan for an area in Web Mercator over a range of levels.
    /// </summary>
    /// <param name="webMercator">The area, in EPSG:3857 metres.</param>
    /// <param name="minZoom">The lowest level, inclusive.</param>
    /// <param name="maxZoom">The highest level, inclusive.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="ArgumentException">A level is outside the pyramid or the range is backwards,
    /// or the area is empty or entirely outside the square.</exception>
    /// <remarks>
    /// <b>A tile is in the seed when the area touches it, edges included.</b> A vector tile is
    /// encoded with a buffer, so a feature on an area's edge is drawn by the tile beside it too —
    /// the reasoning the tile map gives for counting a touch, and a seed that left those tiles out
    /// would leave a cold seam along every edge of the area it was asked to fill.
    /// </remarks>
    public static TileSeedPlan For(Envelope webMercator, int minZoom, int maxZoom)
    {
        if (minZoom < 0 || maxZoom > TileAddress.MaxZoom || minZoom > maxZoom)
        {
            throw new ArgumentException(
                $"The levels are {minZoom} to {maxZoom}; a seed covers levels 0 to "
                + $"{TileAddress.MaxZoom}, lowest first, and the first may not be above the last.");
        }

        if (webMercator.IsEmpty
            || double.IsNaN(webMercator.MinX) || double.IsNaN(webMercator.MinY)
            || double.IsNaN(webMercator.MaxX) || double.IsNaN(webMercator.MaxY))
        {
            throw new ArgumentException("The area is empty, so there is nothing to seed.");
        }

        const double half = TileAddress.WebMercatorHalfExtent;

        if (webMercator.MaxX < -half || webMercator.MinX > half
            || webMercator.MaxY < -half || webMercator.MinY > half)
        {
            throw new ArgumentException(
                "The area is entirely outside the Web Mercator square, so no tile covers it. Is it in "
                + "degrees and labelled as metres?");
        }

        Envelope clipped = new(
            Math.Max(webMercator.MinX, -half),
            Math.Max(webMercator.MinY, -half),
            Math.Min(webMercator.MaxX, half),
            Math.Min(webMercator.MaxY, half));

        List<TileRange> levels = new(maxZoom - minZoom + 1);

        for (int z = minZoom; z <= maxZoom; z++)
        {
            levels.Add(RangeOf(clipped, z));
        }

        return new TileSeedPlan(levels, clipped);
    }

    /// <summary>The rectangle of one level that an area in Web Mercator touches.</summary>
    /// <param name="webMercator">The area, already inside the square.</param>
    /// <param name="z">The level.</param>
    /// <returns>The rectangle.</returns>
    public static TileRange RangeOf(Envelope webMercator, int z)
    {
        const double half = TileAddress.WebMercatorHalfExtent;

        long side = 1L << z;
        double size = 2 * half / side;

        // <b>Floor on both edges, and the east and south edges clamp to the grid.</b> A box whose
        // eastern edge lies exactly on a tile boundary touches the next tile's western edge, and
        // the touch counts (see <see cref="For"/>); the clamp keeps the square's own edge — the
        // antimeridian and 85° south — inside the last column and row rather than one past them.
        int minX = Column(webMercator.MinX);
        int maxX = Column(webMercator.MaxX);
        int minY = Row(webMercator.MaxY);
        int maxY = Row(webMercator.MinY);

        return new TileRange(z, minX, minY, maxX, maxY);

        int Column(double x) => (int)Math.Clamp(Math.Floor((x + half) / size), 0, side - 1);

        int Row(double y) => (int)Math.Clamp(Math.Floor((half - y) / size), 0, side - 1);
    }

    /// <summary>
    /// An area in longitude and latitude, in Web Mercator — the closed formula, clipped at the
    /// latitudes the square ends at.
    /// </summary>
    /// <param name="geographic">The area in degrees, x longitude and y latitude.</param>
    /// <returns>The area in EPSG:3857 metres.</returns>
    /// <remarks>
    /// <b>Arithmetic rather than the projector, and only for this one pair.</b> WGS 84 to Web
    /// Mercator is a formula with no datum in it and is monotonic on each axis, so the corners of
    /// the box are the box — the property <c>VectorTileEndpoints.InWebMercatorAsync</c> relies on
    /// for the same pair. Anything else goes through PROJ, which is not reachable from here.
    /// </remarks>
    public static Envelope FromGeographic(Envelope geographic)
    {
        return new Envelope(
            X(geographic.MinX), Y(geographic.MinY), X(geographic.MaxX), Y(geographic.MaxY));

        static double X(double longitude) =>
            Math.Clamp(longitude, -180, 180) * TileAddress.WebMercatorHalfExtent / 180.0;

        static double Y(double latitude)
        {
            double clamped = Math.Clamp(latitude, -MaximumLatitude, MaximumLatitude) * Math.PI / 180.0;

            return Math.Log(Math.Tan((Math.PI / 4) + (clamped / 2)))
                * TileAddress.WebMercatorHalfExtent / Math.PI;
        }
    }

    /// <summary>
    /// Where a seed resumes: the first level that is not finished, and how far into it.
    /// </summary>
    /// <param name="done">
    /// For each level, in the plan's order, how many tiles from the start of its range are done.
    /// </param>
    /// <returns>The position of the level in <see cref="Levels"/> and the index to go on from, or
    /// null when every level is done.</returns>
    /// <exception cref="ArgumentException">The cursors do not match the plan.</exception>
    /// <remarks>
    /// <b>One number per level is the whole of a seed's memory, and it is enough because the order
    /// is fixed.</b> A tile before the cursor is done and one at or after it is not; a restart goes on
    /// from the cursor and anything it rebuilds that the lost run had already written is found in the
    /// cache and counted as present — ADR-093 §5.4.
    /// </remarks>
    public (int Level, long Index)? ResumeFrom(IReadOnlyList<long> done)
    {
        ArgumentNullException.ThrowIfNull(done);

        if (done.Count != Levels.Count)
        {
            throw new ArgumentException(
                $"The seed has {Levels.Count} levels and {done.Count} cursors were given.", nameof(done));
        }

        for (int level = 0; level < Levels.Count; level++)
        {
            long at = done[level];

            if (at < 0 || at > Levels[level].Count)
            {
                throw new ArgumentException(
                    $"Level {Levels[level].Z}'s cursor is {at}, outside 0 to {Levels[level].Count}.",
                    nameof(done));
            }

            if (at < Levels[level].Count)
            {
                return (level, at);
            }
        }

        return null;
    }

    /// <summary>
    /// How long what is left will take, from the rate observed so far.
    /// </summary>
    /// <param name="done">Tiles finished in the time observed.</param>
    /// <param name="remaining">Tiles still to do.</param>
    /// <param name="observed">How long the finished ones took.</param>
    /// <returns>The estimate, or null when there is nothing to estimate from.</returns>
    /// <remarks>
    /// <b>Straight-line, and it errs on the pessimistic side for the reason §6a gives.</b> Low levels
    /// come first and cost the most per tile, so a rate taken early in a seed is slower than the rate
    /// the rest will go at: the estimate falls as the seed goes on rather than rising, which is the
    /// direction an operator can plan around.
    /// </remarks>
    public static TimeSpan? Remaining(long done, long remaining, TimeSpan observed)
    {
        if (done <= 0 || remaining < 0 || observed <= TimeSpan.Zero)
        {
            return null;
        }

        return TimeSpan.FromSeconds(observed.TotalSeconds / done * remaining);
    }
}
