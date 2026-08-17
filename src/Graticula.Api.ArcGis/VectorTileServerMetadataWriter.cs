using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Graticula.Cartography;
using Graticula.Catalog;
using Graticula.Geometries;

namespace Graticula.Api.ArcGis;

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
    /// <param name="serviceName">The service name.</param>
    /// <param name="sourceLayerNames">
    /// The layer names written inside each tile, one per layer in the service.
    /// A tile carries all of them, so a client reading this document knows which
    /// <c>source-layer</c> values to expect before it fetches one.
    /// </param>
    /// <param name="extent">The data's extent, or null when it is unknown.</param>
    /// <param name="maxZoom">Deepest level served.</param>
    /// <param name="srid">
    /// The reference <paramref name="extent"/> is expressed in — the layers' own,
    /// not the tile grid's. The grid is always Web Mercator; the data need not be.
    /// </param>
    /// <returns>The document.</returns>
    public static object Service(
        string serviceName,
        IReadOnlyList<string> sourceLayerNames,
        Envelope? extent,
        int maxZoom,
        int srid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentNullException.ThrowIfNull(sourceLayerNames);

        // <b>Refused rather than declared, and finding out why cost a working tile
        // layer.</b> On 2026-08-16 this writer was changed to declare whatever
        // reference the extent was in, on the reasoning that saying the truth beats
        // saying Web Mercator about degrees. That is right about honesty and wrong
        // about the format: a vector tile service's tiling scheme *is* Web Mercator,
        // and clients read `fullExtent` in the scheme's reference. Handed a document
        // whose extent said 4326 while `tileInfo` said 102100, the ArcGIS JS
        // VectorTileLayer read the metadata, the style and the sprites — and then
        // requested no tile at all, silently. Measured in the server log.
        //
        // So the extent must arrive already in Web Mercator, and a caller that has
        // not projected it is refused here instead of producing a document that
        // loads nothing. D-49.
        if (srid is not (3857 or 102100))
        {
            throw new ArgumentOutOfRangeException(
                nameof(srid),
                srid,
                "A vector tile service's extent must be in Web Mercator, because its tiling "
                + "scheme is. Project the layer's extent before writing the document — a "
                + "document whose fullExtent and tileInfo disagree makes a client fetch the "
                + "metadata and then no tiles.");
        }

        object full = ExtentOf(extent, srid);

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
            // <b>No cacheInfo, deliberately.</b> The first version declared
            // storageFormat "compactV2", copied from what an Esri-published
            // service reports. We have no bundle cache — tiles are built per
            // request — so it was a claim about our storage that was simply
            // untrue, and the kind a client could reasonably act on. Describing
            // a capability we do not have is worse than describing none.
            resourceInfo = new
            {
                styleVersion = 8,

                // Served uncompressed. Declaring gzip while sending raw bytes is
                // the failure this field exists to prevent, in the other
                // direction.
                tileCompression = "none",
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
    /// <param name="sourceLayers">
    /// Each layer inside the tiles and what to draw it as, in draw order.
    /// </param>
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
    /// <param name="fontStack">
    /// The font stack the server can actually serve, or null when it shipped
    /// without glyphs. It becomes the <c>glyphs</c> URL, and a style without one
    /// cannot draw a label at all — which is what this server did until
    /// 2026-08-15.
    /// </param>
    public static object Style(
        IReadOnlyList<(string Name, GeometryKind Geometry)> sourceLayers,
        string? fontStack = null)
    {
        ArgumentNullException.ThrowIfNull(sourceLayers);

        Dictionary<string, object> style = new()
        {
            ["version"] = 8,
            ["sources"] = new Dictionary<string, object>
            {
                ["esri"] = new { type = "vector", url = "../../" },
            },
        };

        if (fontStack is { Length: > 0 })
        {
            // <b>{fontstack} and {range} are the client's placeholders, not
            // ours.</b> They are left in the URL literally; a client substitutes
            // the stack from each layer's text-font and the range it needs. The
            // default written here is the stack we have, so a style that names
            // nothing still resolves.
            style["glyphs"] = "../fonts/{fontstack}/{range}.pbf";
            style["sprite"] = "../sprites/sprite";
        }

        return Merge(style, new
        {

            // A dictionary rather than an anonymous type, because the style spec
            // spells it "source-layer" and a C# member cannot carry a hyphen.
            // The alternative is a naming policy on the serialiser, which would
            // then apply to every other document this assembly writes and rename
            // fields ArcGIS clients match exactly.
            layers = sourceLayers.Select(StyleLayer).ToArray(),
        });
    }

    /// <summary>
    /// Folds the optional keys in ahead of the rest, keeping the order a reader
    /// expects: version, sources, glyphs, sprite, layers.
    /// </summary>
    private static Dictionary<string, object> Merge(
        Dictionary<string, object> head, object tail)
    {
        foreach (System.Reflection.PropertyInfo property in tail.GetType().GetProperties())
        {
            head[property.Name] = property.GetValue(tail)!;
        }

        return head;
    }

    /// <summary>
    /// One style layer, keyed and painted by its geometry.
    /// </summary>
    /// <remarks>
    /// <b>The colour comes from <see cref="GeneratedSymbology"/> now, and that is the whole
    /// of ADR-033 §5b on this face.</b> It used to be two constants — every line
    /// <c>#1f6f8b</c> and every fill <c>#8fb8cc</c> — so a map of six layers was six
    /// shades of the same blue, which is the complaint ADR-028 §2A wrote down and could
    /// not fix from here. The same call decides the feature service's `drawingInfo`, so
    /// the two faces agree by construction rather than by two people remembering.
    /// </remarks>
    private static object StyleLayer((string Name, GeometryKind Geometry) source)
    {
        (string sourceLayerName, GeometryKind geometryType) = source;

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceLayerName);

        Appearance appearance = GeneratedSymbology.For(sourceLayerName, geometryType);

        Dictionary<string, object> paint = appearance.Kind switch
        {
            AppearanceKind.Marker => new()
            {
                ["circle-radius"] = appearance.Size,
                ["circle-color"] = appearance.Colour,
                ["circle-opacity"] = appearance.Opacity,
                ["circle-stroke-color"] = appearance.Outline!,
                ["circle-stroke-width"] = appearance.OutlineWidth,
            },
            AppearanceKind.Line => new()
            {
                ["line-color"] = appearance.Colour,
                ["line-width"] = appearance.Size,
                ["line-opacity"] = appearance.Opacity,
            },
            _ => new()
            {
                ["fill-color"] = appearance.Colour,
                ["fill-outline-color"] = appearance.Outline!,
                ["fill-opacity"] = appearance.Opacity,
            },
        };

        string type = appearance.Kind switch
        {
            AppearanceKind.Marker => "circle",
            AppearanceKind.Line => "line",
            _ => "fill",
        };

        return new Dictionary<string, object>
        {
            ["id"] = sourceLayerName,
            ["type"] = type,
            ["source"] = "esri",
            ["source-layer"] = sourceLayerName,
            ["paint"] = paint,
        };
    }

    /// <summary>An extent, or the whole world when the layer's is unknown.</summary>
    /// <remarks>
    /// <para>
    /// <b>Never omitted.</b> A client with no extent either shows the whole world
    /// or refuses to add the layer, and both look like the service is broken.
    /// <c>ST_EstimatedExtent</c> returns nothing for a table that has never been
    /// analysed, which is exactly the state a freshly loaded table is in.
    /// </para>
    /// <para>
    /// <b>It declares the reference the numbers are actually in, which
    /// <c>tileInfo</c> deliberately does not have to match.</b> The tile grid is
    /// Web Mercator by construction — z/x/y is defined on it — but the data's
    /// extent is whatever the layer is stored in. Until 2026-08-16 this stamped
    /// Web Mercator on both, so a layer stored in EPSG:4326 advertised
    /// <c>{24.7, 34.9, 45.6, 42.8}</c> as metres: a box a few tens of metres off
    /// West Africa, for data covering Turkey. A client that zooms to the declared
    /// extent went to the Gulf of Guinea, the tiles it then requested were empty,
    /// and the map looked broken with nothing in any log. The FeatureServer writer
    /// had this right all along — it declares the layer's own SRID beside its
    /// extent — which is why the same layer's <em>Map</em> worked and its
    /// <em>Tiles</em> did not. D-49.
    /// </para>
    /// </remarks>
    private static object ExtentOf(Envelope? extent, int srid)
    {
        _ = srid;   // Validated by the caller; kept so the requirement is in the signature.

        Envelope box = extent ?? new Envelope(-WorldExtent, -WorldExtent, WorldExtent, WorldExtent);

        return new
        {
            xmin = box.MinX,
            ymin = box.MinY,
            xmax = box.MaxX,
            ymax = box.MaxY,

            // 102100 is the ArcGIS well-known id for Web Mercator and clients match
            // on that spelling rather than on 3857.
            spatialReference = new { wkid = 102100, latestWkid = 3857 },
        };
    }
}
