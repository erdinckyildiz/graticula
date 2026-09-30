using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Graticula.Cartography;
using Graticula.Geometries;

namespace Graticula.Tiles;

/// <summary>
/// The grid a vector tile service is cut on: a reference, an origin, a tile size and a list of
/// resolutions — [ADR-096](../../../docs/adr/ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md).
/// </summary>
/// <remarks>
/// <para>
/// <b>Web Mercator is one scheme among several since 2026-09-29, and it is still the one every
/// service has unless somebody chose another.</b> Until then the grid was a constant —
/// <c>ST_TileEnvelope</c>'s default bounds, <see cref="TileAddress.WebMercatorEnvelope"/>, a
/// hard-coded <c>tileInfo</c> — and a service in a Turkish national grid could only be drawn over a
/// Mercator basemap. The owner decided a service may be tiled in another reference; this type is what a
/// service carries to say which.
/// </para>
/// <para>
/// <b><see cref="WebMercator"/> is not computed like the others, and that is the whole of the
/// compatibility promise.</b> Every question it is asked — the envelope of a tile, whether a level is
/// simplified, which levels a visible range draws at, the rectangle an area covers — is answered by the
/// code that answered it before this type existed (<see cref="TileAddress"/>,
/// <see cref="VisibleScaleRange.CarriesVectorTile"/>, <see cref="TileSeedPlan.RangeOf(Envelope, int)"/>),
/// and its <see cref="Fingerprint"/> is null so no cache key moves. A Mercator tile built after this
/// change is the same bytes under the same key as one built before it.
/// </para>
/// <para>
/// <b>Not <see cref="TilingScheme"/>, though the two describe the same kind of thing.</b> That one is
/// ImageServer's (ADR-044): 256 pixels, derived from a coverage's own extent, its Web Mercator the
/// ArcGIS-published half-width (20037508.342787) rather than PostGIS's (20037508.342789244). Reusing it
/// here would have moved every Mercator tile envelope by two millimetres at the edge of the world — the
/// disagreement <see cref="TileAddress.WebMercatorHalfExtent"/> exists to prevent — and made a raster
/// face's grid a vector face's cache identity.
/// </para>
/// <para>
/// <b>Level zero is one tile, and the grid is square.</b> Every scheme here — built in, derived or
/// custom — has its whole frame in one tile at level zero, so a level's tile count is the frame over
/// that level's tile span, rounded up (<see cref="TilesAcross"/>). With resolutions that halve this is
/// the familiar 2^z by 2^z; with an explicit list it is whatever the list makes it.
/// </para>
/// <para>
/// <b>Metres are assumed, and a geographic reference is refused</b> (<see cref="Create"/>). A scale is
/// a resolution times 96 dots per inch times 39.37 inches per metre, and generalisation compares a
/// resolution with Web Mercator's at z14; both need ground units that are metres. A reference in US feet
/// would be tiled correctly and have its stated scales wrong by a factor of 3.28 — INFERRED acceptable,
/// named in ADR-096 §6, because no reference the owner asked for is in feet.
/// </para>
/// </remarks>
public sealed class VectorTileScheme
{
    /// <summary>Tile edge in pixels, for every scheme — what ArcGIS vector basemaps and MapLibre draw at.</summary>
    public const int TileSize = 512;

    /// <summary>The identity of the Web Mercator scheme.</summary>
    public const string WebMercatorId = "webmercator";

    /// <summary>The identity every scheme that is not built in carries.</summary>
    public const string CustomId = "custom";

    /// <summary>The most levels a scheme may have.</summary>
    /// <remarks>
    /// <b>Thirty, so a tile count always fits in an <see cref="int"/>.</b> A halving scheme's level 30
    /// is 2^30 tiles across; the address route parses integers, and a level past what it can name is a
    /// level nobody can ask for.
    /// </remarks>
    public const int MostLevels = 30;

    /// <summary>
    /// Web Mercator's resolution at level 14 in a 512-pixel tile — the coarsest level at which a Mercator
    /// tile is simplified (ADR-085), expressed as ground rather than as a level number.
    /// </summary>
    /// <remarks>
    /// <b>Generalisation is about how big a pixel is on the ground, so another scheme is generalised by
    /// its pixel, not by its level number.</b> 2 × 20037508.342789244 / 2^14 / 512 = 4.777314267823516 m.
    /// A level whose pixel is at least this big is simplified at half a pixel, which for Web Mercator is
    /// exactly levels 0 to 14 — the rule ADR-085 wrote as <c>@z &lt;= 14</c>.
    /// </remarks>
    public static readonly double SimplifiedDownToResolution =
        TileAddress.WebMercatorHalfExtent * 2.0 / (1L << 14) / TileSize;

    /// <summary>A relative tolerance for comparing resolutions, so the last digit of a double does not move a level across a threshold.</summary>
    private const double Tolerance = 1e-9;

    private readonly double[] _resolutions;

    private VectorTileScheme(
        string id, int srid, double originX, double originY, double[] resolutions, bool webMercator)
    {
        Id = id;
        Srid = srid;
        OriginX = originX;
        OriginY = originY;
        _resolutions = resolutions;
        IsWebMercator = webMercator;
        Fingerprint = webMercator ? null : Canonical(srid, originX, originY, resolutions);
        Key = webMercator
            ? WebMercatorId
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Fingerprint!)).AsSpan(0, 6));
    }

    /// <summary>The scheme every service has unless somebody chose another.</summary>
    /// <remarks>
    /// 512-pixel tiles, levels 0 to <see cref="TileAddress.MaxZoom"/>, the origin at PostGIS's own
    /// top-left of the Web Mercator square — the grid <c>ST_TileEnvelope</c> cuts.
    /// </remarks>
    public static VectorTileScheme WebMercator { get; } = new(
        WebMercatorId,
        3857,
        -TileAddress.WebMercatorHalfExtent,
        TileAddress.WebMercatorHalfExtent,
        [.. Enumerable.Range(0, TileAddress.MaxZoom + 1)
            .Select(z => TileAddress.WebMercatorHalfExtent * 2.0 / (1L << z) / TileSize)],
        webMercator: true);

    /// <summary>Its name: <see cref="WebMercatorId"/>, a built-in's, or <see cref="CustomId"/>.</summary>
    /// <remarks>
    /// <b>A label, not the identity.</b> Two services called <c>turef-tm30</c> set a year apart carry
    /// whatever numbers the built-in had when each was set; <see cref="Fingerprint"/> is what says two
    /// grids are the same grid.
    /// </remarks>
    public string Id { get; }

    /// <summary>The reference the grid is laid out in.</summary>
    public int Srid { get; }

    /// <summary>Easting of the grid's top-left corner.</summary>
    public double OriginX { get; }

    /// <summary>Northing of the grid's top-left corner.</summary>
    public double OriginY { get; }

    /// <summary>Ground units per pixel at each level, coarsest first.</summary>
    public IReadOnlyList<double> Resolutions => _resolutions;

    /// <summary>Whether this is the Web Mercator scheme, answered by the code that answered before schemes existed.</summary>
    public bool IsWebMercator { get; }

    /// <summary>How many levels it has.</summary>
    public int LevelCount => _resolutions.Length;

    /// <summary>The finest level.</summary>
    public int MaxLevel => _resolutions.Length - 1;

    /// <summary>
    /// The grid written out canonically, for a tile's cache key — or null for Web Mercator.
    /// </summary>
    /// <remarks>
    /// <b>Null for Web Mercator, so no existing key moves</b> (<see cref="TileCacheKey.FingerprintOf"/>
    /// appends nothing for a null). For any other grid it is every number that places a tile, so a
    /// service moved between two schemes, or between two definitions of one, never reads the other's
    /// tiles.
    /// </remarks>
    public string? Fingerprint { get; }

    /// <summary>A short identity for the grid: <see cref="WebMercatorId"/>, or twelve hex characters of <see cref="Fingerprint"/>'s hash.</summary>
    /// <remarks>What a seed records beside its area, so a seed resumed after the service changed scheme knows its area is in the wrong reference.</remarks>
    public string Key { get; }

    /// <summary>The ground the whole grid covers: level zero's one tile.</summary>
    public Envelope Frame
    {
        get
        {
            double span = _resolutions[0] * TileSize;
            return new Envelope(OriginX, OriginY - span, OriginX + span, OriginY);
        }
    }

    /// <summary>Ground units per pixel at a level.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The resolution.</returns>
    public double Resolution(int level) => _resolutions[CheckLevel(level)];

    /// <summary>The ArcGIS scale of a level: its resolution at 96 dpi and 39.37 inches to the metre.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The scale denominator.</returns>
    /// <remarks>
    /// For Web Mercator this is <see cref="VisibleScaleRange.VectorTileScale"/>'s table — the same
    /// product, and the level-0 figure 295,828,763.795777 every ArcGIS vector basemap states.
    /// </remarks>
    public double Scale(int level) =>
        IsWebMercator ? VisibleScaleRange.VectorTileScale(CheckLevel(level)) : VisibleScaleRange.ScaleOf(Resolution(level));

    /// <summary>How many tiles a level is wide.</summary>
    /// <param name="level">The level.</param>
    /// <returns>At least one.</returns>
    public int TilesAcross(int level)
    {
        if (IsWebMercator)
        {
            return 1 << CheckLevel(level);
        }

        double frame = _resolutions[0] * TileSize;
        double span = Resolution(level) * TileSize;

        // <b>The epsilon is what makes one tile one tile</b> — TilingScheme.Count's reasoning: a frame
        // divided by its own span in floating point can come out a hair above a whole number.
        return (int)Math.Max(1, Math.Ceiling((frame / span) - 1e-9));
    }

    /// <summary>How many tiles a level is tall; the grid is square, so the same as <see cref="TilesAcross"/>.</summary>
    /// <param name="level">The level.</param>
    /// <returns>At least one.</returns>
    public int TilesDown(int level) => TilesAcross(level);

    /// <summary>Why an address is not in this grid, or null when it is.</summary>
    /// <param name="address">The address.</param>
    /// <returns>A sentence, or null.</returns>
    public string? Rejection(TileAddress address)
    {
        if (IsWebMercator)
        {
            return address.Rejection();
        }

        if (address.Z < 0 || address.Z > MaxLevel)
        {
            return $"Level {address.Z} is outside 0–{MaxLevel}.";
        }

        int side = TilesAcross(address.Z);

        return address.X < 0 || address.X >= side || address.Y < 0 || address.Y >= side
            ? $"Tile {address.X},{address.Y} is outside the {side}×{side} grid at level {address.Z}."
            : null;
    }

    /// <summary>The ground a tile covers, in <see cref="Srid"/>.</summary>
    /// <param name="address">The tile; the caller has already checked it with <see cref="Rejection"/>.</param>
    /// <returns>The unexpanded tile box.</returns>
    /// <remarks>
    /// <b>For Web Mercator, <see cref="TileAddress.WebMercatorEnvelope"/></b> — which is what
    /// <c>ST_TileEnvelope</c> returns, and what the Mercator statement still asks PostGIS for. For any
    /// other scheme the box is computed here and handed to PostGIS as numbers, so only one side decides
    /// where a tile is, which is the property the Mercator path gets by asking PostGIS.
    /// </remarks>
    public Envelope Envelope(TileAddress address)
    {
        if (IsWebMercator)
        {
            return address.WebMercatorEnvelope();
        }

        double span = Resolution(address.Z) * TileSize;
        double minX = OriginX + (address.X * span);
        double maxY = OriginY - (address.Y * span);

        return new Envelope(minX, maxY - span, minX + span, maxY);
    }

    /// <summary>The Web Mercator level whose pixel is nearest in size to this level's.</summary>
    /// <param name="level">The level.</param>
    /// <returns>The level itself on Web Mercator; otherwise the nearest Mercator level, 0 to <see cref="TileAddress.MaxZoom"/>.</returns>
    /// <remarks>
    /// <b>For what was written against a Mercator level number but means a pixel's size</b> — the reason
    /// <see cref="Simplifies"/> keys on the pixel. TM30's level 8 is 4.59 m a pixel, Mercator's level 14
    /// (2026-09-30: an export's size guess read level 8 as Mercator's and asked for 85 GB).
    /// </remarks>
    public int MercatorLevelOf(int level)
    {
        if (IsWebMercator)
        {
            return CheckLevel(level);
        }

        double mercatorLevel0 = TileAddress.WebMercatorHalfExtent * 2.0 / TileSize;

        return (int)Math.Clamp(Math.Round(Math.Log2(mercatorLevel0 / Resolution(level))), 0, TileAddress.MaxZoom);
    }

    /// <summary>Whether a level's tiles are simplified at half a pixel — ADR-085, keyed on the pixel's size.</summary>
    /// <param name="level">The level.</param>
    /// <returns>True where a pixel is at least <see cref="SimplifiedDownToResolution"/> on the ground.</returns>
    /// <remarks>
    /// For Web Mercator, exactly levels 0 to 14, which is what the statement's own <c>@z &lt;= 14</c> says;
    /// the Mercator statement keeps asking it that way, so its text did not move.
    /// </remarks>
    public bool Simplifies(int level) =>
        IsWebMercator
            ? CheckLevel(level) <= 14
            : Resolution(level) >= SimplifiedDownToResolution * (1 - Tolerance);

    /// <summary>Whether a layer's visible range draws anywhere a tile of this level is shown — ADR-070.</summary>
    /// <param name="range">The layer's range.</param>
    /// <param name="level">The level.</param>
    /// <returns>False when every scale the tile is drawn at is outside the range.</returns>
    /// <remarks>
    /// <b>A tile is on screen from its own level's scale down to the next level's</b>, where a client asks
    /// for the finer tile — <see cref="VisibleScaleRange.CarriesVectorTile"/>'s reasoning, which for Web
    /// Mercator is exactly that method. The finest level has no next one and is kept until twice as zoomed
    /// in, as Mercator's is.
    /// </remarks>
    public bool Draws(VisibleScaleRange range, int level)
    {
        if (IsWebMercator)
        {
            return range.CarriesVectorTile(level);
        }

        double coarsest = Scale(level);
        double finest = level < MaxLevel ? Scale(level + 1) : coarsest / 2;

        return range.CarriesTileBetween(coarsest, finest);
    }

    /// <summary>The level-zero scale a style's zoom is measured from — for a style's <c>minzoom</c> and <c>maxzoom</c>.</summary>
    public double Level0Scale => Scale(0);

    /// <summary>The rectangle of one level that an area in <see cref="Srid"/> touches, edges included.</summary>
    /// <param name="area">The area, already inside <see cref="Frame"/>.</param>
    /// <param name="level">The level.</param>
    /// <returns>The rectangle.</returns>
    public TileRange RangeOf(Envelope area, int level)
    {
        if (IsWebMercator)
        {
            return TileSeedPlan.RangeOf(area, level);
        }

        double size = Resolution(level) * TileSize;
        int side = TilesAcross(level);

        // Floor on both edges and clamp to the grid, as TileSeedPlan.RangeOf does for Mercator: a box
        // whose edge lies on a tile boundary touches the next tile, and the frame's own edge stays in
        // the last column and row.
        return new TileRange(
            level,
            Column(area.MinX),
            Row(area.MaxY),
            Column(area.MaxX),
            Row(area.MinY));

        int Column(double x) => (int)Math.Clamp(Math.Floor((x - OriginX) / size), 0, side - 1);

        int Row(double y) => (int)Math.Clamp(Math.Floor((OriginY - y) / size), 0, side - 1);
    }

    /// <summary>
    /// A scheme from its numbers, or the reason they do not make one.
    /// </summary>
    /// <param name="id">Its name — a built-in's, or <see cref="CustomId"/>.</param>
    /// <param name="srid">The reference: a projected EPSG code, not 3857 (which is <see cref="WebMercator"/>).</param>
    /// <param name="originX">Easting of the top-left corner.</param>
    /// <param name="originY">Northing of the top-left corner.</param>
    /// <param name="resolutions">Ground units per pixel, coarsest first, each finer than the last.</param>
    /// <param name="scheme">The scheme, when the numbers make one.</param>
    /// <returns>Null, or a sentence saying what is wrong.</returns>
    public static string? Create(
        string id, int srid, double originX, double originY, IReadOnlyList<double> resolutions,
        out VectorTileScheme? scheme)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        scheme = null;

        if (srid is 3857 or 102100 or 102113 or 900913)
        {
            return "Web Mercator is the default scheme and is not defined by numbers; clear the scheme to use it.";
        }

        if (srid <= 0)
        {
            return $"'{srid}' is not a spatial reference. Give the EPSG code of a projected reference, such as 5254.";
        }

        if (AxisOrder.IsGeographic(srid))
        {
            return $"EPSG:{srid} is geographic. A vector tile scheme here is laid out in a projected reference "
                + "whose units are metres, because its scales and its generalisation are measured in metres.";
        }

        if (!double.IsFinite(originX) || !double.IsFinite(originY))
        {
            return "The origin's two numbers must be finite.";
        }

        if (resolutions.Count is 0 or > MostLevels)
        {
            return $"A scheme has 1 to {MostLevels} levels; this one has {resolutions.Count}.";
        }

        for (int i = 0; i < resolutions.Count; i++)
        {
            if (!double.IsFinite(resolutions[i]) || resolutions[i] <= 0)
            {
                return $"Level {i}'s resolution is {resolutions[i].ToString(CultureInfo.InvariantCulture)}; "
                    + "a resolution is a positive number of metres per pixel.";
            }

            if (i > 0 && resolutions[i] >= resolutions[i - 1])
            {
                return $"Level {i}'s resolution is not finer than level {i - 1}'s. Levels go from the coarsest "
                    + "to the finest.";
            }
        }

        // <b>A tile count has to be an integer a URL can name.</b> Checked on the finest level, which has
        // the most tiles.
        if (resolutions[0] / resolutions[^1] > int.MaxValue)
        {
            return "The finest level would have more tiles across than a tile address can number.";
        }

        scheme = new VectorTileScheme(id, srid, originX, originY, [.. resolutions], webMercator: false);
        return null;
    }

    /// <summary>Resolutions halving from a level-zero one.</summary>
    /// <param name="first">Ground units per pixel at level zero.</param>
    /// <param name="count">How many levels.</param>
    /// <returns>The list.</returns>
    public static double[] Halving(double first, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MostLevels);

        return [.. Enumerable.Range(0, count).Select(level => first / (1L << level))];
    }

    /// <summary>
    /// A grid derived from the ground a reference is meant for — the rule the built-in TUREF schemes were
    /// made by, and what a scheme named by its reference alone gets.
    /// </summary>
    /// <param name="id">Its name.</param>
    /// <param name="srid">The projected reference.</param>
    /// <param name="area">The reference's area of use, already projected into it and enclosing the samples of its edges.</param>
    /// <param name="scheme">The scheme.</param>
    /// <returns>Null, or why the area makes no grid.</returns>
    /// <remarks>
    /// <para>
    /// <b>Four steps, all of them arithmetic a person can redo</b> — ADR-096 §5.2:
    /// </para>
    /// <list type="number">
    /// <item><b>The origin is the area's top-left corner, rounded outward to the kilometre</b> — west down,
    /// north up — so the grid contains the whole area and its numbers can be read aloud.</item>
    /// <item><b>Level zero spans the area's longer side from that origin</b>, rounded up to the kilometre.</item>
    /// <item><b>Its resolution is that span over 512</b>, which is exact in binary because 512 is a power of
    /// two: TM30's 1,772 km is 3460.9375 m per pixel, no rounding anywhere.</item>
    /// <item><b>Levels halve until a pixel is no bigger than Web Mercator's at z22</b> (0.0186613838586856 m),
    /// so the finest level is at least as fine as the finest Mercator level this server serves.</item>
    /// </list>
    /// </remarks>
    public static string? Derive(string id, int srid, Envelope area, out VectorTileScheme? scheme)
    {
        scheme = null;

        if (area.IsEmpty || !double.IsFinite(area.MinX) || !double.IsFinite(area.MinY)
            || !double.IsFinite(area.MaxX) || !double.IsFinite(area.MaxY)
            || area.MaxX <= area.MinX || area.MaxY <= area.MinY)
        {
            return $"The area EPSG:{srid} is meant for could not be put into its own coordinates, so no grid can "
                + "be derived from it. Give the origin and the resolutions instead.";
        }

        double originX = Math.Floor(area.MinX / 1000.0) * 1000.0;
        double originY = Math.Ceiling(area.MaxY / 1000.0) * 1000.0;
        double span = Math.Ceiling(Math.Max(area.MaxX - originX, originY - area.MinY) / 1000.0) * 1000.0;
        double first = span / TileSize;

        double finest = WebMercator.Resolution(TileAddress.MaxZoom);
        int count = 1;

        while (count < MostLevels && first / (1L << (count - 1)) > finest * (1 + Tolerance))
        {
            count++;
        }

        return Create(id, srid, originX, originY, Halving(first, count), out scheme);
    }

    /// <summary>The scheme as the catalogue stores it: the numbers, never only the name.</summary>
    /// <returns>JSON, or null for Web Mercator — which is stored as no scheme at all.</returns>
    /// <remarks>
    /// <b>The numbers, so a built-in changed by a later build does not move a service already set to
    /// it.</b> A service keeps the grid it was given; its clients have that grid's <c>tileInfo</c>, and
    /// a grid that moved under them would draw every tile in the wrong place until they reloaded.
    /// </remarks>
    public string? ToJson()
    {
        if (IsWebMercator)
        {
            return null;
        }

        JsonObject stored = new()
        {
            ["id"] = Id,
            ["wkid"] = Srid,
            ["origin"] = new JsonObject { ["x"] = OriginX, ["y"] = OriginY },
            ["tileSize"] = TileSize,
            ["resolutions"] = new JsonArray([.. _resolutions.Select(r => (JsonNode)r)]),
        };

        return stored.ToJsonString();
    }

    /// <summary>Reads a stored scheme, or says why it cannot be read.</summary>
    /// <param name="json">The stored JSON, or null for Web Mercator.</param>
    /// <param name="scheme">The scheme.</param>
    /// <returns>Null, or the reason.</returns>
    public static string? Parse(string? json, out VectorTileScheme scheme)
    {
        scheme = WebMercator;

        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(json) is not JsonObject stored
                || stored["wkid"]?.GetValue<int>() is not { } srid
                || stored["origin"] is not JsonObject origin
                || origin["x"]?.GetValue<double>() is not { } x
                || origin["y"]?.GetValue<double>() is not { } y
                || stored["resolutions"] is not JsonArray list)
            {
                return "The stored tiling scheme is missing its wkid, origin or resolutions.";
            }

            if (stored["tileSize"]?.GetValue<int>() is { } size && size != TileSize)
            {
                return $"The stored tiling scheme has {size}-pixel tiles, and this build cuts {TileSize}.";
            }

            string id = stored["id"]?.GetValue<string>() is { Length: > 0 } named ? named : CustomId;
            // A null in the list reads as not-a-number, which Create refuses with a sentence — never as an
            // exception here, which would take down every read of the catalogue, not just this service's tiles.
            double[] resolutions = [.. list.Select(r => r is null ? double.NaN : r.GetValue<double>())];

            string? wrong = Create(id, srid, x, y, resolutions, out VectorTileScheme? made);

            if (wrong is not null)
            {
                return wrong;
            }

            scheme = made!;
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            return "The stored tiling scheme is not a document this build can read: " + e.Message;
        }
    }

    private static string Canonical(int srid, double originX, double originY, double[] resolutions) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"srid={srid};origin={originX:R},{originY:R};size={TileSize};res={string.Join(",", resolutions.Select(r => r.ToString("R", CultureInfo.InvariantCulture)))}");

    private int CheckLevel(int level)
    {
        if (level < 0 || level > MaxLevel)
        {
            throw new ArgumentOutOfRangeException(
                nameof(level), level, $"This scheme has levels 0 to {MaxLevel}.");
        }

        return level;
    }
}
