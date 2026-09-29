namespace Graticula.Api.Tiles;

/// <summary>
/// The addresses, media types and URIs the standard tile faces are made of — ADR-097.
/// </summary>
/// <remarks>
/// <b>One place, because three documents and two route tables name them.</b> A link relation typed
/// twice is a link relation that will one day be typed differently, and a client following links finds
/// the resource at one spelling and not the other.
/// </remarks>
public static class TileNames
{
    /// <summary>
    /// Where OGC API Tiles lives: beside <c>/ogc/features/v1</c>, which ADR-042 §5.1 versioned in the
    /// path for exactly this neighbour.
    /// </summary>
    public const string OgcBase = "/ogc/tiles/v1";

    /// <summary>Where WMTS lives: beside <c>/wfs</c> and <c>/wms</c>.</summary>
    public const string WmtsPath = "/wmts";

    /// <summary>A Mapbox Vector Tile — OGC API Tiles §16.7.</summary>
    public const string Mvt = "application/vnd.mapbox-vector-tile";

    /// <summary>Plain JSON.</summary>
    public const string Json = "application/json";

    /// <summary>
    /// The media type a TileJSON alternate is linked with — the one OGC 17-083r4 Annex H's example uses.
    /// TileJSON has no registered type; the document itself is served as <see cref="Json"/>, which is what
    /// every TileJSON client reads.
    /// </summary>
    public const string TileJsonLink = "application/json+vnd.mapbox.tilejson";

    /// <summary>The <c>f</c> value that asks a Web Mercator tileset for its TileJSON.</summary>
    public const string TileJsonFormat = "tilejson";

    /// <summary>The <c>f</c> value that asks a document for its HTML page.</summary>
    public const string HtmlFormat = "html";

    /// <summary>Relation: the tilesets of vector tiles of a collection — OGC API Tiles §11.</summary>
    public const string RelTilesetsVector = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-vector";

    /// <summary>Relation: a tile matrix set's definition — OGC API Tiles /req/tileset/description D.</summary>
    public const string RelTilingScheme = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme";

    /// <summary>Relation: the list of tile matrix sets — OGC API Tiles /rec/tileset/tmxslink D.</summary>
    public const string RelTilingSchemes = "http://www.opengis.net/def/rel/ogc/1.0/tiling-schemes";

    /// <summary>Relation: the conformance declaration.</summary>
    public const string RelConformance = "http://www.opengis.net/def/rel/ogc/1.0/conformance";

    /// <summary>Relation: the collections.</summary>
    public const string RelData = "http://www.opengis.net/def/rel/ogc/1.0/data";

    /// <summary>Relation: the geospatial data resource a tileset is made of.</summary>
    public const string RelGeodata = "http://www.opengis.net/def/rel/ogc/1.0/geodata";

    /// <summary>The OGC API Tiles Part 1 conformance-class prefix.</summary>
    public const string TilesConf = "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/";

    /// <summary>The Two Dimensional Tile Matrix Set 2.0 conformance-class prefix.</summary>
    public const string TmsConf = "http://www.opengis.net/spec/tms/2.0/conf/";

    /// <summary>
    /// What this face claims. Each has a proof in the conformance suite
    /// (<c>OgcTilesConformanceTests</c>), in both directions, as ADR-005 condition 2 requires of the
    /// features face.
    /// </summary>
    /// <remarks>
    /// <b>Not claimed, and why.</b> <c>dataset-tilesets</c>: there is no dataset-wide tileset — a tile
    /// is one service's (VectorTileEndpoints.Concatenate), and a server-wide one would mix grids.
    /// <c>collections-selection</c>, <c>datetime</c>: neither parameter is honoured, and a claim a
    /// client acts on and is ignored is worse than none. <c>oas30</c>: no OpenAPI definition is
    /// served for this face. <c>xml</c>: JSON only. <c>json-tilematrixsetlimits</c>: no limits are
    /// published (ADR-097 §5.2).
    /// </remarks>
    public static readonly string[] ConformsTo =
    [
        TilesConf + "core",
        TilesConf + "tileset",
        TilesConf + "tilesets-list",
        TilesConf + "geodata-tilesets",
        TilesConf + "mvt",
        TmsConf + "tilematrixset",
        TmsConf + "json-tilematrixset",
        TmsConf + "tilesetmetadata",
        TmsConf + "json-tilesetmetadata",
    ];
}
