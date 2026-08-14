using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GisServer.Catalog;
using GisServer.Geometries;

namespace GisServer.Api.ArcGis;

/// <summary>
/// The documents an ArcGIS client reads before it asks for a single tile.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written from the published REST specification only</b> (CLAUDE.md §5). The
/// field names, the <c>tile/{z}/{y}/{x}.pbf</c> template and the LOD table are
/// documented behaviour; nothing here reproduces Esri source or undocumented
/// internals.
/// </para>
/// <para>
/// <b>This is where a tile service is discovered or is invisible.</b> The
/// FeatureServer surface taught this lesson expensively: the query endpoint
/// worked perfectly and no client could find it, because <c>/rest/info</c>, the
/// catalogue and the service document did not exist. A tile endpoint with no
/// service document is the same failure with a different noun.
/// </para>
/// </remarks>
public static class VectorTileServerMetadataWriter
{
    /// <summary>Tile pixel size the LOD table is computed for.</summary>
    /// <remarks>
    /// <b>512, not 256.</b> Esri's own vector basemaps declare 512, and the LOD
    /// <c>scale</c> values follow from it. This does not change which tile covers
    /// what — the z/x/y grid is identical either way — it changes the scale a
    /// renderer associates with a level, so declaring 256 while serving the
    /// standard grid makes every label and line width come out at half size.
    /// </remarks>
    public const int TileSize = 512;

    /// <summary>Resolution in metres per pixel at level 0, for a 512-pixel tile.</summary>
    /// <remarks>The Web Mercator span, 40075016.6855785 m, divided by 512.</remarks>
    public const double Level0Resolution = 78271.51696402048;

    /// <summary>Scale denominator at level 0, for a 512-pixel tile at 96 dpi.</summary>
    public const double Level0Scale = 295828763.795777;

    /// <summary>Web Mercator half-extent in metres.</summary>
    public const double WorldExtent = 20037508.342788905;

    /// <summary>
    /// The VectorTileServer service document.
    /// </summary>
    /// <param name="serviceName">The published layer name.</param>
    /// <param name="sourceLayerName">The layer name written inside each tile.</param>
    /// <param name="extent">The data's extent, or null when it is unknown.</param>
    /// <param name="maxZoom">Deepest level served.</param>
    /// <returns>The document.</returns>
    public static object Service(
        string serviceName, string sourceLayerName, Envelope? extent, int maxZoom)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        object full = ExtentOf(extent);

        return new
        {
            currentVersion = FeatureServerMetadataWriter.CurrentVersion,
            name = serviceName,
            capabilities = "TilesOnly",
            type = "indexedVector",

            // Relative, so the service works behind a reverse proxy that mounts
            // it somewhere other than the root. An absolute URL built from the
            // request host is the classic way a service works in development and
            // returns unreachable links in production.
            tiles = new[] { "tile/{z}/{y}/{x}.pbf" },
            defaultStyles = "resources/styles",

            // Said explicitly rather than left out. exportTilesAllowed absent is
            // read by some clients as "unknown", and offering a bulk export we
            // do not implement produces a failure at the least convenient moment.
            exportTilesAllowed = false,
            initialExtent = full,
            fullExtent = full,
            minScale = 0,
            maxScale = 0,
            maxzoom = maxZoom,
            tileInfo = TileInfo(maxZoom),
            resourceInfo = new
            {
                styleVersion = 8,
                tileCompression = "none",
                cacheInfo = new
                {
                    storageInfo = new { packetSize = 128, storageFormat = "compactV2" },
                },
            },
        };
    }

    /// <summary>
    /// The tiling scheme: origin, spatial reference and one entry per level.
    /// </summary>
    private static object TileInfo(int maxZoom) => new
    {
        rows = TileSize,
        cols = TileSize,
        dpi = 96,
        format = "pbf",

        // Top-left of the world, which is where XYZ row 0 begins. An origin at
        // the bottom-left is the TMS scheme and produces a map that is upside
        // down while looking entirely plausible.
        origin = new { x = -WorldExtent, y = WorldExtent },
        spatialReference = new { wkid = 102100, latestWkid = 3857 },
        lods = Enumerable.Range(0, maxZoom + 1).Select(level => new
        {
            level,
            resolution = Level0Resolution / Math.Pow(2, level),
            scale = Level0Scale / Math.Pow(2, level),
        }).ToArray(),
    };

    /// <summary>
    /// A Mapbox GL style, which is what <c>resources/styles/root.json</c> serves.
    /// </summary>
    /// <param name="sourceLayerName">The layer name inside the tiles.</param>
    /// <param name="geometryType">What to draw it as.</param>
    /// <returns>The style document.</returns>
    /// <remarks>
    /// <para>
    /// <b>A default style, and it is deliberately plain.</b> Cartography is a
    /// Tier 1 concern this project has not started, and a style document is the
    /// smallest possible placeholder that makes a service visible in a client
    /// rather than blank. Anything more decorative here would be an unrecorded
    /// cartographic decision.
    /// </para>
    /// <para>
    /// <b>The source URL is <c>../../</c></b> — two levels up from
    /// <c>resources/styles/root.json</c> is the service root, which is what the
    /// client re-reads to find the tile template. Getting this wrong yields a
    /// style that loads and a map that stays empty, with nothing in the console.
    /// </para>
    /// </remarks>
    public static object Style(string sourceLayerName, GeometryKind geometryType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLayerName);

        Dictionary<string, object> paint = geometryType switch
        {
            GeometryKind.Point or GeometryKind.MultiPoint => new()
            {
                ["circle-radius"] = 3,
                ["circle-color"] = "#1f6f8b",
                ["circle-opacity"] = 0.85,
            },
            GeometryKind.LineString or GeometryKind.MultiLineString => new()
            {
                ["line-color"] = "#1f6f8b",
                ["line-width"] = 1.2,
            },
            _ => new()
            {
                ["fill-color"] = "#8fb8cc",
                ["fill-outline-color"] = "#1c3a52",
                ["fill-opacity"] = 0.65,
            },
        };

        string type = geometryType switch
        {
            GeometryKind.Point or GeometryKind.MultiPoint => "circle",
            GeometryKind.LineString or GeometryKind.MultiLineString => "line",
            _ => "fill",
        };

        return new
        {
            version = 8,
            sources = new Dictionary<string, object>
            {
                ["esri"] = new { type = "vector", url = "../../" },
            },
            // A dictionary rather than an anonymous type, because the style spec
            // spells it "source-layer" and a C# member cannot carry a hyphen.
            // The alternative is a naming policy on the serialiser, which would
            // then apply to every other document this assembly writes and rename
            // fields ArcGIS clients match exactly.
            layers = new object[]
            {
                new Dictionary<string, object>
                {
                    ["id"] = sourceLayerName,
                    ["type"] = type,
                    ["source"] = "esri",
                    ["source-layer"] = sourceLayerName,
                    ["paint"] = paint,
                },
            },
        };
    }

    /// <summary>An extent, or the whole world when the layer's is unknown.</summary>
    /// <remarks>
    /// <b>Never omitted.</b> A client with no extent either shows the whole world
    /// or refuses to add the layer, and both look like the service is broken.
    /// <c>ST_EstimatedExtent</c> returns nothing for a table that has never been
    /// analysed, which is exactly the state a freshly loaded table is in.
    /// </remarks>
    private static object ExtentOf(Envelope? extent)
    {
        Envelope box = extent ?? new Envelope(-WorldExtent, -WorldExtent, WorldExtent, WorldExtent);

        return new
        {
            xmin = box.MinX,
            ymin = box.MinY,
            xmax = box.MaxX,
            ymax = box.MaxY,
            spatialReference = new { wkid = 102100, latestWkid = 3857 },
        };
    }
}
