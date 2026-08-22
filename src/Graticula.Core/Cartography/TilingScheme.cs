using System;
using System.Collections.Generic;
using System.Globalization;
using Graticula.Coverages;
using Graticula.Geometries;

namespace Graticula.Cartography;

/// <summary>One resolution of a tiling scheme.</summary>
/// <param name="Level">Its index, counting from the coarsest at zero.</param>
/// <param name="Resolution">Ground units per pixel at this level.</param>
/// <param name="Scale">The map scale it corresponds to, for clients that ask in scales.</param>
public readonly record struct TileLevel(int Level, double Resolution, double Scale);

/// <summary>
/// A grid of tiles over the ground: where the grid starts, how big a tile is, and what
/// resolutions it has.
/// </summary>
/// <remarks>
/// <para>
/// <b>A scheme is not a cache, and keeping the two apart is the whole reason this type
/// exists.</b> Esri's model ties <c>tileInfo</c> to <c>singleFusedMapCache</c> so tightly
/// that the two read as one fact, and they are not: a scheme is an agreement about how to
/// name a piece of ground, and a cache is a decision to keep the picture of it. This
/// server publishes the first and not the second — a tile is rendered when it is asked
/// for, out of the same coverage <c>exportImage</c> reads — so <c>singleFusedMapCache</c>
/// stays false while <c>tileInfo</c> is populated.
/// </para>
/// <para>
/// <b>Why it exists at all is worth recording, because the honest answer is unflattering
/// and the dishonest one was available.</b> ArcGIS Pro's raster reader refuses an image
/// service whose <c>capabilities</c> does not contain <c>Tilemap</c>
/// ([Q-134](../../../docs/open-questions.md), where the grid is measured). Typing the word
/// costs nothing and would have been untrue, which
/// [ADR-043](../../../docs/adr/ADR-043-imageserver-and-the-raster-face.md) condition 5
/// forbids and correctness gate 2 had already caught once. So the operation is served
/// instead, and serving it needs a scheme in which a client can name a tile.
/// </para>
/// <para>
/// <b>Three references, and the third is the one that makes this general.</b> Web Mercator
/// and WGS 84 get the schemes every ArcGIS and web client already knows, because a scheme
/// nobody shares is a scheme nobody can use. Anything else gets one derived from the
/// coverage's own extent: the origin at its top-left corner, level zero covering it in a
/// single tile, halving down until a pixel is at least as fine as the coverage's own. A
/// derived scheme is not interoperable with anything, and it does not need to be — it is
/// how a client asks *this* service for *this* coverage in pieces.
/// </para>
/// </remarks>
public sealed class TilingScheme
{
    /// <summary>The nominal dots per inch a scale is computed at.</summary>
    /// <remarks>
    /// 96, which is what every ArcGIS tiling scheme states and what a client will assume
    /// if this server states something else. It only affects <see cref="TileLevel.Scale"/>,
    /// which is a courtesy for clients that choose a level by scale rather than by
    /// resolution; the geometry is decided by the resolution alone.
    /// </remarks>
    public const double Dpi = 96.0;

    /// <summary>Inches in a metre, for turning a resolution into a scale.</summary>
    /// <remarks>
    /// <b>39.37, and not 39.3700787, and the difference is the whole reason this is a named
    /// constant.</b> An inch is 0.0254 metres exactly, which makes a metre 39.37007874
    /// inches; every published ArcGIS tiling scheme states its scales as though it were
    /// 39.37, the US survey convention. Web Mercator level 0 is 591657527.591555 in every
    /// client's tables and 591658710.908977 if the exact figure is used.
    ///
    /// <b>Clients match levels by scale</b>, so a scheme whose scales are a millionth off
    /// from everybody else's is a scheme whose levels do not line up with the basemap it is
    /// drawn over. Being exactly right here would be being wrong.
    /// </remarks>
    private const double InchesPerMetre = 39.37;

    /// <summary>Degrees to metres at the equator, for a geographic scheme's scale.</summary>
    /// <remarks>
    /// <b>Only ever used for the scale figure, never for the geometry.</b> A degree is not
    /// a fixed distance, so any single number here is wrong away from the equator. Esri's
    /// own geographic schemes state scales computed this way, so a client comparing this
    /// service's levels with another's gets figures that line up. Using it to place a tile
    /// would be a real error; using it to label one is the convention.
    /// </remarks>
    private const double MetresPerDegree = 111319.49079327358;

    private TilingScheme(
        double originX,
        double originY,
        double frameWidth,
        double frameHeight,
        int tileSize,
        int srid,
        IReadOnlyList<TileLevel> levels)
    {
        OriginX = originX;
        OriginY = originY;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;
        TileSize = tileSize;
        Srid = srid;
        Levels = levels;
    }

    /// <summary>Easting or longitude of the grid's top-left corner.</summary>
    public double OriginX { get; }

    /// <summary>Northing or latitude of the grid's top-left corner.</summary>
    public double OriginY { get; }

    /// <summary>How much ground the whole grid spans east to west.</summary>
    /// <remarks>
    /// <b>The grid is finite and this is how wide it is.</b> Without it a tile route can
    /// only refuse a negative row, which leaves every row past the last one answering about
    /// ground the scheme has no name for — and, at the top of the integer range, wrapping to
    /// a negative one mid-block.
    /// </remarks>
    public double FrameWidth { get; }

    /// <summary>How much ground the whole grid spans north to south.</summary>
    /// <remarks>
    /// <b>Not always the same as <see cref="FrameWidth"/>.</b> ArcGIS's WGS 84 scheme is two
    /// tiles wide and one tall at level zero, because the world is 360 degrees across and 180
    /// down. Treating the grid as square would have put its southern half outside itself.
    /// </remarks>
    public double FrameHeight { get; }

    /// <summary>Tile edge in pixels; square, and 256 in every scheme this builds.</summary>
    public int TileSize { get; }

    /// <summary>The reference the grid is laid out in.</summary>
    public int Srid { get; }

    /// <summary>Coarsest first.</summary>
    public IReadOnlyList<TileLevel> Levels { get; }

    /// <summary>Builds the scheme a coverage is tiled in.</summary>
    /// <param name="info">The coverage.</param>
    /// <returns>Its scheme.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="info"/> is null.</exception>
    public static TilingScheme For(CoverageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return info.Srid switch
        {
            3857 => WebMercator(),
            4326 => Geographic(),
            _ => Derived(info),
        };
    }

    /// <summary>How many tiles a level is wide.</summary>
    /// <param name="level">Which resolution.</param>
    /// <returns>The column count, counting from zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> is not one this scheme has.
    /// </exception>
    public int TilesAcross(int level) => Count(level, FrameWidth);

    /// <summary>How many tiles a level is tall.</summary>
    /// <param name="level">Which resolution.</param>
    /// <returns>The row count, counting from zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> is not one this scheme has.
    /// </exception>
    public int TilesDown(int level) => Count(level, FrameHeight);

    /// <summary>The ground a tile covers.</summary>
    /// <param name="level">Which resolution.</param>
    /// <param name="row">Its row, counting down from the origin.</param>
    /// <param name="column">Its column, counting right from the origin.</param>
    /// <returns>The tile's extent.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="level"/> is not one this scheme has.
    /// </exception>
    /// <remarks>
    /// <b>Rows count downward and columns rightward from the top-left origin</b>, which is
    /// the ArcGIS convention and the opposite of the one OGC's WMTS inherited from tile
    /// matrix sets in some references. Getting it backwards draws a map that is correct
    /// tile by tile and mirrored as a whole, which is a failure that looks like a
    /// projection bug and is not one.
    /// </remarks>
    public Envelope Tile(int level, int row, int column)
    {
        if (level < 0 || level >= Levels.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "This scheme has levels 0 to "
                    + (Levels.Count - 1).ToString(CultureInfo.InvariantCulture) + ".");
        }

        double span = Levels[level].Resolution * TileSize;

        double minX = OriginX + (column * span);
        double maxY = OriginY - (row * span);

        return new Envelope(minX, maxY - span, minX + span, maxY);
    }

    /// <summary>Whether a tile names ground this coverage has pixels for.</summary>
    /// <param name="info">The coverage.</param>
    /// <param name="level">Which resolution.</param>
    /// <param name="row">Its row.</param>
    /// <param name="column">Its column.</param>
    /// <returns>Whether the two overlap.</returns>
    /// <remarks>
    /// <para>
    /// <b>This is what <c>tilemap</c> answers, and the honest answer is *overlap*, not
    /// *drawn*.</b> A tile that meets the coverage's extent may still come back mostly
    /// transparent, because a coverage's extent is a rectangle and its no-data is not. The
    /// operation exists so a client can skip tiles that would certainly be empty, and
    /// promising more than that would mean reading pixels to answer a question about
    /// geometry.
    /// </para>
    /// <para>
    /// <b>Touching at an edge is not overlapping.</b> Two tiles that share a boundary with
    /// the coverage's extent contain none of it, and reporting them as present would put a
    /// blank request on every client's critical path along two sides of every coverage.
    /// </para>
    /// </remarks>
    public bool Covers(CoverageInfo info, int level, int row, int column)
    {
        ArgumentNullException.ThrowIfNull(info);

        Envelope tile = Tile(level, row, column);
        Envelope ground = info.Extent;

        return tile.MinX < ground.MaxX
            && tile.MaxX > ground.MinX
            && tile.MinY < ground.MaxY
            && tile.MaxY > ground.MinY;
    }

    /// <summary>The level whose pixels are closest to a coverage's own.</summary>
    /// <param name="info">The coverage.</param>
    /// <returns>The finest level worth publishing for it.</returns>
    /// <remarks>
    /// <b>The finest level at which a tile pixel is still no finer than a coverage
    /// pixel.</b> Publishing levels past that offers a client a resolution this service can
    /// only reach by magnifying, which costs bandwidth for no detail. The rule is the one
    /// <see cref="CoveragePlanner"/> already applies in the other direction, and the two
    /// are deliberately the same sentence read from each end.
    /// </remarks>
    public int FinestUsefulLevel(CoverageInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        double native = Math.Min(info.PixelWidth, info.PixelHeight);
        int finest = 0;

        for (int i = 0; i < Levels.Count; i++)
        {
            if (Levels[i].Resolution >= native)
            {
                finest = i;
            }
        }

        return finest;
    }

    /// <summary>Tiles needed to span some ground at a level.</summary>
    /// <param name="level">Which resolution.</param>
    /// <param name="frame">The ground to span.</param>
    /// <returns>At least one.</returns>
    /// <remarks>
    /// <b>The tolerance is not slack, it is the difference between one tile and two.</b> Web
    /// Mercator's published level-zero resolution times 256 is 40075016.68556804 and the
    /// published half-width times two is 40075016.685574 — the same number, written down
    /// twice by different people. Dividing them gives 1.00000000000015, and a plain ceiling
    /// turns that into two tiles at level zero, which puts the whole world in the left half
    /// of a grid twice its size. Subtracting an epsilon first is what makes the published
    /// numbers agree with each other.
    /// </remarks>
    private int Count(int level, double frame)
    {
        if (level < 0 || level >= Levels.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level),
                level,
                "This scheme has levels 0 to "
                    + (Levels.Count - 1).ToString(CultureInfo.InvariantCulture) + ".");
        }

        double span = Levels[level].Resolution * TileSize;

        return Math.Max(1, (int)Math.Ceiling((frame / span) - 1e-9));
    }

    private static TilingScheme WebMercator()
    {
        // The scheme every web map and every ArcGIS client already shares: the whole
        // world in one 256-pixel tile at level 0, halving from there. The origin is the
        // top-left of the Web Mercator square, not the ellipsoid's, and the numbers are
        // the published ones rather than recomputed, so that a tile named here is the
        // same piece of ground as a tile named anywhere else.
        const double half = 20037508.342787;
        const double first = 156543.03392800014;

        return new TilingScheme(
            -half, half, half * 2, half * 2, 256, 3857, Halving(first, 24, 1.0));
    }

    private static TilingScheme Geographic()
    {
        // ArcGIS's GCS WGS 84 scheme: level 0 is two tiles wide and one tall, covering
        // -180..180 and -90..90, so the first resolution is 360 / 512 degrees per pixel.
        const double first = 0.703125;

        return new TilingScheme(
            -180.0, 90.0, 360.0, 180.0, 256, 4326, Halving(first, 22, MetresPerDegree));
    }

    private static TilingScheme Derived(CoverageInfo info)
    {
        // <b>A scheme of this coverage's own, because there is no shared one to use.</b>
        // The origin is the coverage's top-left corner and level 0 is one tile across its
        // longer side, so level 0 is the whole thing and every level below it subdivides.
        // Nothing else in the world names tiles this way, which is fine: a derived scheme
        // is how a client asks this service for this coverage, not a common language.
        double span = Math.Max(
            info.Extent.MaxX - info.Extent.MinX, info.Extent.MaxY - info.Extent.MinY);

        double first = span / 256.0;
        double native = Math.Min(info.PixelWidth, info.PixelHeight);

        // One past the level that reaches the coverage's own pixel size, so the finest
        // published level is never coarser than the data. Bounded, because a coverage with
        // a pathological extent-to-pixel ratio would otherwise ask for hundreds.
        int count = 1;

        while (count < 24 && first / (1 << (count - 1)) > native)
        {
            count++;
        }

        // <b>A metre per unit, because a derived scheme has no way to know better.</b>
        // The reference is one this server was handed and its units are whatever they are;
        // treating them as metres makes the stated scale wrong for a geographic reference
        // that is neither 4326 nor projected. The resolution beside it is exact and is what
        // places a tile, so a client choosing by resolution is unaffected and one choosing
        // by scale picks a neighbouring level. Recording it rather than hiding it.
        // <b>Square, and deliberately so.</b> Level zero is one tile across the longer
        // side, so the shorter side has room to spare and the grid is the same size both
        // ways. A frame cut to the coverage's own aspect would make the last row or column
        // a fraction of a tile, and a fractional tile has no name in this scheme.
        return new TilingScheme(
            info.Extent.MinX, info.Extent.MaxY, span, span, 256, info.Srid,
            Halving(first, count, 1.0));
    }

    /// <summary>Levels halving from a first resolution.</summary>
    /// <param name="first">Ground units per pixel at level zero.</param>
    /// <param name="count">How many levels.</param>
    /// <param name="metresPerUnit">
    /// One for a scheme in metres; <see cref="MetresPerDegree"/> for one in degrees.
    /// </param>
    /// <returns>The levels, coarsest first.</returns>
    private static TileLevel[] Halving(double first, int count, double metresPerUnit)
    {
        TileLevel[] levels = new TileLevel[count];

        for (int i = 0; i < count; i++)
        {
            double resolution = first / (1 << i);

            levels[i] = new TileLevel(
                i, resolution, resolution * metresPerUnit * Dpi * InchesPerMetre);
        }

        return levels;
    }
}
