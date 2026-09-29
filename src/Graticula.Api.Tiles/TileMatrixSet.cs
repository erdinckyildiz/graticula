using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Graticula.Geometries;
using Graticula.Tiles;

namespace Graticula.Api.Tiles;

/// <summary>One tile matrix: one level of a <see cref="TileMatrixSet"/>, in OGC 17-083r4's terms.</summary>
/// <param name="Id">Its identifier — the level number, written as a string, as every registered set writes it.</param>
/// <param name="ScaleDenominator">The scale at OGC's standardized rendering pixel of 0.28 mm.</param>
/// <param name="CellSize">Ground units per cell.</param>
/// <param name="TileWidth">Cells across a tile.</param>
/// <param name="TileHeight">Cells down a tile.</param>
/// <param name="MatrixWidth">Tiles across the level.</param>
/// <param name="MatrixHeight">Tiles down the level.</param>
public sealed record TileMatrix(
    string Id, double ScaleDenominator, double CellSize, int TileWidth, int TileHeight, long MatrixWidth, long MatrixHeight);

/// <summary>
/// A tile matrix set — the grid a vector tile service is cut on, described the way OGC 17-083r4
/// (Two Dimensional Tile Matrix Set 2.0) describes one, for OGC API Tiles and WMTS — ADR-097.
/// </summary>
/// <remarks>
/// <para>
/// <b>A description of a <see cref="VectorTileScheme"/>, never a second grid.</b> The scheme decides
/// where a tile is (ADR-096) and the tile route, the seed and the cache all ask it. This type only says
/// the same grid in the standard's vocabulary; it carries the scheme it describes, and a tile asked for
/// through it is addressed by <see cref="Address"/> into that scheme and served by the one tile path, so
/// a tile fetched through OGC API Tiles, WMTS or the ArcGIS face is the same cache entry.
/// </para>
/// <para>
/// <b>Web Mercator is the registered <c>WebMercatorQuad</c>, verbatim — 256 cells, not 512.</b> This
/// server's tiles are drawn at 512 pixels (<see cref="VectorTileScheme.TileSize"/>), and the obvious way
/// to be honest about that would be <c>WebMercatorQuad</c> with <c>tileWidth</c> 512. It would be the
/// dishonest answer. 17-083r4 §6.1.1 computes a tile's extent from its matrix as <i>tileWidth ×
/// cellSize</i>, and <c>cellSize</c> from <c>scaleDenominator × 0.28 mm</c>; a set that kept the
/// registered scales and said 512 describes tiles twice the width of the ones served, and one that halved
/// the scales to keep the extents is not <c>WebMercatorQuad</c> — a client that recognises the set by its
/// URI, as OGC API Tiles §8 recommends, would trust the name and read the wrong numbers. The two grids
/// have the same levels, the same matrix sizes and the same tile boundaries to the digit: level <i>z</i>,
/// row <i>r</i>, column <i>c</i> of <c>WebMercatorQuad</c> is <c>tile/z/r/c</c> of the ArcGIS face. And a
/// vector tile has no cells — it is a 4096-unit grid of coordinates (ADR-021) — so "256" and "512" are
/// two conventions for the size it is <i>drawn</i> at, not two tiles. That convention stays where it
/// means something: the ArcGIS <c>tileInfo</c> and the style's zooms. OGC API Tiles itself says a
/// TileJSON document <i>usually implies a WebMercatorQuad TileMatrixSet</i>.
/// </para>
/// <para>
/// <b>Any other scheme is a set this server defines, at 512 cells.</b> No register has the TUREF grids,
/// so there is no registered convention to keep: the set's <c>cellSize</c> is the scheme's resolution
/// exactly — the number the admin API and the ArcGIS <c>tileInfo</c> state — and <c>tileWidth</c> is 512,
/// so its extents come out right by the standard's own arithmetic.
/// </para>
/// <para>
/// <b>The scale is 0.28 mm, not 96 dpi, and the two faces differ on purpose.</b> OGC (17-083r4 §6.1.1,
/// WMTS 1.0 §6.1, WMS 1.3.0) defines a scale denominator against a <i>standardized rendering pixel</i> of
/// 0.28 mm; ArcGIS states its <c>lods</c> at 96 dpi, 0.2645833 mm. So the same grid has two scale columns:
/// Web Mercator level 0 is 559,082,264.0287178 here (256 cells at 0.28 mm) and 295,828,763.795777 in the
/// ArcGIS document (512 pixels at 96 dpi). Both are right in their own standard; neither is copied into the
/// other.
/// </para>
/// </remarks>
public sealed class TileMatrixSet
{
    /// <summary>OGC's standardized rendering pixel, in metres — 17-083r4 §6.1.1.</summary>
    public const double RenderingPixel = 0.28e-3;

    /// <summary>The registered identifier of the Web Mercator set.</summary>
    public const string WebMercatorQuadId = "WebMercatorQuad";

    /// <summary>The registered URI of <see cref="WebMercatorQuadId"/>.</summary>
    public const string WebMercatorQuadUri = "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad";

    /// <summary>The well-known scale set <c>WebMercatorQuad</c> declares — 17-083r4 Annex C, Table C.4.</summary>
    public const string GoogleMapsCompatible = "http://www.opengis.net/def/wkss/OGC/1.0/GoogleMapsCompatible";

    /// <summary>The cells across a registered <c>WebMercatorQuad</c> tile.</summary>
    public const int WebMercatorQuadCells = 256;

    /// <summary>The prefix of a set this server derived from a scheme no built-in names.</summary>
    public const string CustomPrefix = "custom-";

    private TileMatrixSet(
        string id, string title, string? uri, string? wellKnownScaleSet, VectorTileScheme scheme,
        IReadOnlyList<TileMatrix> matrices)
    {
        Id = id;
        Title = title;
        Uri = uri;
        WellKnownScaleSet = wellKnownScaleSet;
        Scheme = scheme;
        Matrices = matrices;
    }

    /// <summary>The registered <c>WebMercatorQuad</c>, over <see cref="VectorTileScheme.WebMercator"/>.</summary>
    /// <remarks>
    /// <b>Computed from PostGIS's half-width, and equal to the register's table.</b> The level-0 cell is
    /// 2 × 20037508.342789244 / 256 = 156,543.03392804097 m and its scale that over 0.28 mm =
    /// 559,082,264.0287178 — Table C.4's figures; the register's JSON rounds both to fifteen digits. The
    /// origin is <see cref="TileAddress.WebMercatorHalfExtent"/>, which the register writes rounded to
    /// 20037508.3427892.
    /// </remarks>
    public static TileMatrixSet WebMercatorQuad { get; } = new(
        WebMercatorQuadId,
        "Google Maps Compatible for the World",
        WebMercatorQuadUri,
        GoogleMapsCompatible,
        VectorTileScheme.WebMercator,
        [.. Enumerable.Range(0, TileAddress.MaxZoom + 1).Select(z =>
        {
            double cell = TileAddress.WebMercatorHalfExtent * 2.0 / WebMercatorQuadCells / (1L << z);
            return new TileMatrix(
                z.ToString(CultureInfo.InvariantCulture),
                cell / RenderingPixel,
                cell,
                WebMercatorQuadCells,
                WebMercatorQuadCells,
                1L << z,
                1L << z);
        })]);

    /// <summary>The identifier a request names the set by.</summary>
    public string Id { get; }

    /// <summary>What a person reads.</summary>
    public string Title { get; }

    /// <summary>The registered URI, or null for a set only this server defines.</summary>
    public string? Uri { get; }

    /// <summary>The well-known scale set it declares, or null.</summary>
    public string? WellKnownScaleSet { get; }

    /// <summary>The grid it describes, which is what a tile is addressed into.</summary>
    public VectorTileScheme Scheme { get; }

    /// <summary>Its levels, coarsest first.</summary>
    public IReadOnlyList<TileMatrix> Matrices { get; }

    /// <summary>The reference's EPSG code.</summary>
    public int Srid => Scheme.Srid;

    /// <summary>The reference, as an OGC CRS URI.</summary>
    public string Crs => string.Create(CultureInfo.InvariantCulture, $"http://www.opengis.net/def/crs/EPSG/0/{Srid}");

    /// <summary>The reference, as the URN WMTS 1.0 writes in <c>ows:SupportedCRS</c>.</summary>
    public string CrsUrn => string.Create(CultureInfo.InvariantCulture, $"urn:ogc:def:crs:EPSG::{Srid}");

    /// <summary>
    /// Whether the reference's authority writes the north ordinate first — every TUREF grid does
    /// (ADR-060) — which decides the order of the origin.
    /// </summary>
    public bool NorthFirst => AxisOrder.IsLatitudeFirst(Srid);

    /// <summary>The axes in the order the reference's authority writes them — <c>orderedAxes</c>.</summary>
    /// <remarks>
    /// The registered <c>WebMercatorQuad</c> says <c>["X","Y"]</c> and is repeated as it is; a set this
    /// server defines names the axes by direction, because the abbreviations differ between registers and
    /// the direction is what decides how <see cref="Origin"/> is read.
    /// </remarks>
    public IReadOnlyList<string> OrderedAxes =>
        Scheme.IsWebMercator ? ["X", "Y"] : NorthFirst ? ["N", "E"] : ["E", "N"];

    /// <summary>The top-left corner, in the reference's own axis order — 17-083r4's <c>pointOfOrigin</c> and WMTS's <c>TopLeftCorner</c>.</summary>
    public (double First, double Second) Origin =>
        NorthFirst ? (Scheme.OriginY, Scheme.OriginX) : (Scheme.OriginX, Scheme.OriginY);

    /// <summary>The set that describes a scheme.</summary>
    /// <param name="scheme">A service's scheme.</param>
    /// <returns><see cref="WebMercatorQuad"/>, a built-in's set, or a custom one named by the grid's key.</returns>
    /// <remarks>
    /// <b>A built-in's name only when its numbers are the built-in's.</b> A service keeps the numbers it was
    /// given (ADR-096 §5.1), and a later build could move a built-in; a service whose stored grid no longer
    /// matches is described as <c>custom-</c> plus its key, so one identifier never names two grids.
    /// </remarks>
    public static TileMatrixSet For(VectorTileScheme scheme)
    {
        ArgumentNullException.ThrowIfNull(scheme);

        if (scheme.IsWebMercator)
        {
            return WebMercatorQuad;
        }

        BuiltInTileScheme? builtIn = VectorTileSchemes.BuiltIn
            .FirstOrDefault(b => string.Equals(b.Scheme.Fingerprint, scheme.Fingerprint, StringComparison.Ordinal));

        return Defined(
            builtIn?.Id ?? CustomPrefix + scheme.Key,
            builtIn?.Title ?? string.Create(CultureInfo.InvariantCulture, $"Custom grid in EPSG:{scheme.Srid}"),
            scheme);
    }

    /// <summary>Every set this server defines without a service: <see cref="WebMercatorQuad"/> and the built-ins.</summary>
    public static IReadOnlyList<TileMatrixSet> Standing { get; } =
        [WebMercatorQuad, .. VectorTileSchemes.BuiltIn.Select(b => Defined(b.Id, b.Title, b.Scheme))];

    /// <summary>
    /// The tile a matrix, a row and a column name, or why they name none.
    /// </summary>
    /// <param name="tileMatrix">The matrix identifier as the request sent it.</param>
    /// <param name="row">The row, top first.</param>
    /// <param name="column">The column, left first.</param>
    /// <param name="address">The tile in <see cref="Scheme"/>'s grid.</param>
    /// <returns>Null, or a sentence saying what is out of range.</returns>
    /// <remarks>
    /// <b>Row is <see cref="TileAddress.Y"/> and column is <see cref="TileAddress.X"/>, top-left origin</b>
    /// — 17-083r4's <c>cornerOfOrigin</c> default, and the ArcGIS face's <c>tile/{z}/{y}/{x}</c>. The
    /// matrix is an integer level and nothing else: <c>"07"</c> and <c>"+7"</c> are not identifiers of this
    /// set, so they are refused rather than read as 7.
    /// </remarks>
    public string? Address(string tileMatrix, long row, long column, out TileAddress address)
    {
        address = default;
        TileMatrix? matrix = Matrices.FirstOrDefault(m => string.Equals(m.Id, tileMatrix, StringComparison.Ordinal));

        if (matrix is null)
        {
            return $"'{tileMatrix}' is not a tile matrix of {Id}; its matrices are 0 to {Matrices.Count - 1}.";
        }

        if (row < 0 || row >= matrix.MatrixHeight || column < 0 || column >= matrix.MatrixWidth)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"Row {row}, column {column} is outside tile matrix {matrix.Id} of {Id}, which has rows 0 to "
                + $"{matrix.MatrixHeight - 1} and columns 0 to {matrix.MatrixWidth - 1}.");
        }

        address = new TileAddress(int.Parse(matrix.Id, CultureInfo.InvariantCulture), (int)column, (int)row);

        // The scheme's own rule has the last word, so this can never admit a tile the tile route would refuse.
        return Scheme.Rejection(address);
    }

    /// <summary>Writes the set as 17-083r4's JSON encoding (the <c>json-tilematrixset</c> class).</summary>
    /// <param name="json">The writer.</param>
    /// <param name="links">Links to write with it, or none.</param>
    public void WriteJson(Utf8JsonWriter json, IEnumerable<(string Rel, string Type, string Href, string? Title)>? links = null)
    {
        ArgumentNullException.ThrowIfNull(json);

        json.WriteStartObject();
        json.WriteString("id", Id);
        json.WriteString("title", Title);

        if (Uri is { } uri)
        {
            json.WriteString("uri", uri);
        }

        json.WriteString("crs", Crs);
        json.WriteStartArray("orderedAxes");

        foreach (string axis in OrderedAxes)
        {
            json.WriteStringValue(axis);
        }

        json.WriteEndArray();

        if (WellKnownScaleSet is { } wkss)
        {
            json.WriteString("wellKnownScaleSet", wkss);
        }

        if (links is not null)
        {
            TileDocuments.WriteLinks(json, links);
        }

        (double first, double second) = Origin;

        json.WriteStartArray("tileMatrices");

        foreach (TileMatrix matrix in Matrices)
        {
            json.WriteStartObject();
            json.WriteString("id", matrix.Id);
            json.WriteNumber("scaleDenominator", matrix.ScaleDenominator);
            json.WriteNumber("cellSize", matrix.CellSize);
            json.WriteString("cornerOfOrigin", "topLeft");
            json.WriteStartArray("pointOfOrigin");
            json.WriteNumberValue(first);
            json.WriteNumberValue(second);
            json.WriteEndArray();
            json.WriteNumber("tileWidth", matrix.TileWidth);
            json.WriteNumber("tileHeight", matrix.TileHeight);
            json.WriteNumber("matrixWidth", matrix.MatrixWidth);
            json.WriteNumber("matrixHeight", matrix.MatrixHeight);
            json.WriteEndObject();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    /// <summary>A set this server defines over a scheme: 512 cells, the scheme's resolution as the cell, 0.28 mm scales.</summary>
    private static TileMatrixSet Defined(string id, string title, VectorTileScheme scheme) =>
        new(
            id,
            title,
            uri: null,
            wellKnownScaleSet: null,
            scheme,
            [.. Enumerable.Range(0, scheme.LevelCount).Select(level => new TileMatrix(
                level.ToString(CultureInfo.InvariantCulture),
                scheme.Resolution(level) / RenderingPixel,
                scheme.Resolution(level),
                VectorTileScheme.TileSize,
                VectorTileScheme.TileSize,
                scheme.TilesAcross(level),
                scheme.TilesDown(level)))]);
}
