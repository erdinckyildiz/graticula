using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using Graticula.Api.Tiles;
using Graticula.Cartography;
using Graticula.Features;
using Graticula.Geometries;
using Graticula.Tiles;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-097 without a server: the tile matrix sets and their arithmetic, the TileJSON, the OGC tileset
/// documents, the WMTS capabilities, its KVP reading and its exception report.
/// </summary>
/// <remarks>
/// <para>
/// <b>The numbers are the specification's, typed in from it</b> — 17-083r4 Annex C Table C.4 and D.1 for
/// <c>WebMercatorQuad</c> — not derived from the code under test, so a test cannot agree with a wrong
/// formula by computing it the same way.
/// </para>
/// <para>
/// The live half — the three faces answering, a tile byte-identical across them and a cache hit, a private
/// service invisible to an anonymous caller — is <c>StandardTileFacesConformanceTests</c>, against a server.
/// </para>
/// </remarks>
public sealed class StandardTileFacesTests
{
    private static readonly VectorTileScheme Tm30 = VectorTileSchemes.Find("turef-tm30")!.Scheme;

    // ---------- the tile matrix sets ----------

    /// <summary>WebMercatorQuad is the registered set: 256 cells, Table C.4's scales and cells, the registered URI.</summary>
    [Fact]
    public void WebMercatorQuad_is_the_registered_definition_with_its_0_28_mm_scales()
    {
        TileMatrixSet set = TileMatrixSet.WebMercatorQuad;

        Assert.Equal("WebMercatorQuad", set.Id);
        Assert.Equal("http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad", set.Uri);
        Assert.Equal("http://www.opengis.net/def/crs/EPSG/0/3857", set.Crs);
        Assert.Equal("http://www.opengis.net/def/wkss/OGC/1.0/GoogleMapsCompatible", set.WellKnownScaleSet);
        Assert.Equal(23, set.Matrices.Count);

        // Table C.4, level 0, 15 and 22 — scale denominator and cell size as the standard prints them.
        AssertClose(559_082_264.0287178, set.Matrices[0].ScaleDenominator, 1e-12);
        AssertClose(156_543.0339280410, set.Matrices[0].CellSize, 1e-12);
        AssertClose(17_061.83667079827, set.Matrices[15].ScaleDenominator, 1e-12);
        AssertClose(4.777314267823516, set.Matrices[15].CellSize, 1e-12);

        foreach (TileMatrix matrix in set.Matrices)
        {
            Assert.Equal(256, matrix.TileWidth);
            Assert.Equal(256, matrix.TileHeight);

            // The standard's own definition: cell size is the scale denominator times 0.28 mm.
            AssertClose(matrix.ScaleDenominator * 0.28e-3, matrix.CellSize, 1e-12);
        }
    }

    /// <summary>
    /// The 256-cell OGC set and the 512-pixel ArcGIS grid are the same tiles: WebMercatorQuad's cell at level z
    /// is exactly twice this server's pixel at level z, and the two scale columns differ by 0.28 mm against 96 dpi.
    /// </summary>
    [Fact]
    public void The_OGC_and_ArcGIS_scales_describe_one_grid_by_two_conventions()
    {
        for (int z = 0; z <= TileAddress.MaxZoom; z++)
        {
            TileMatrix matrix = TileMatrixSet.WebMercatorQuad.Matrices[z];

            AssertClose(2 * VectorTileScheme.WebMercator.Resolution(z), matrix.CellSize, 1e-12);

            // ArcGIS: 512 pixels at 96 dpi and 39.37 inches to the metre; OGC: 256 cells at 0.28 mm. Same ground per tile.
            double arcGis = VectorTileScheme.WebMercator.Scale(z);
            double tileGround = matrix.CellSize * matrix.TileWidth;

            AssertClose(tileGround, arcGis / (96 * 39.37) * 512, 1e-6);
            AssertClose(tileGround, matrix.ScaleDenominator * 0.28e-3 * 256, 1e-12);
        }

        // The two level-0 figures a reader will meet, pinned so a change to either is seen.
        AssertClose(295_828_763.795777, VectorTileScheme.WebMercator.Scale(0), 1e-12);
        AssertClose(559_082_264.0287178, TileMatrixSet.WebMercatorQuad.Matrices[0].ScaleDenominator, 1e-12);
    }

    /// <summary>
    /// Every tile's box by 17-083r4 §6.1.1's arithmetic — origin, cell, tile width — is the box the tile path
    /// builds for the same level, row and column.
    /// </summary>
    [Theory]
    [InlineData("webmercator")]
    [InlineData("turef-tm30")]
    [InlineData("turef-gk12")]
    public void A_tile_is_where_the_standard_s_arithmetic_puts_it(string scheme)
    {
        VectorTileScheme grid = scheme == "webmercator" ? VectorTileScheme.WebMercator : VectorTileSchemes.Find(scheme)!.Scheme;
        TileMatrixSet set = TileMatrixSet.For(grid);
        (double originX, double originY) = set.NorthFirst ? (set.Origin.Second, set.Origin.First) : set.Origin;

        foreach (int level in (int[])[0, 1, 3, 7, 12])
        {
            TileMatrix matrix = set.Matrices[level];
            double span = matrix.CellSize * matrix.TileWidth;

            foreach ((long row, long column) in (ReadOnlySpan<(long, long)>)[(0, 0), (matrix.MatrixHeight - 1, matrix.MatrixWidth - 1), (matrix.MatrixHeight / 2, matrix.MatrixWidth / 3)])
            {
                Assert.Null(set.Address(matrix.Id, row, column, out TileAddress address));

                Envelope expected = grid.Envelope(address);

                // TileRow counts down from the top, TileCol right from the left — cornerOfOrigin topLeft.
                AssertClose(originX + (column * span), expected.MinX, 1e-9, absolute: 1e-6);
                AssertClose(originY - (row * span), expected.MaxY, 1e-9, absolute: 1e-6);
                AssertClose(originX + ((column + 1) * span), expected.MaxX, 1e-9, absolute: 1e-6);
                AssertClose(originY - ((row + 1) * span), expected.MinY, 1e-9, absolute: 1e-6);
            }
        }
    }

    /// <summary>A matrix, a row and a column become the ArcGIS face's z, y and x — so they are one cache entry.</summary>
    [Fact]
    public void Row_is_y_and_column_is_x_and_the_grid_is_the_service_s_own()
    {
        TileMatrixSet set = TileMatrixSet.WebMercatorQuad;

        Assert.Null(set.Address("3", 2, 5, out TileAddress address));
        Assert.Equal(new TileAddress(3, 5, 2), address);

        // The set carries the scheme itself, so a tile asked for through it is keyed as the ArcGIS route keys it.
        Assert.Same(VectorTileScheme.WebMercator, set.Scheme);
        Assert.Same(Tm30, TileMatrixSet.For(Tm30).Scheme);
    }

    /// <summary>What is not in the grid is refused, and a matrix id is the level's own spelling.</summary>
    [Theory]
    [InlineData("3", 8, 0)]
    [InlineData("3", 0, 8)]
    [InlineData("3", -1, 0)]
    [InlineData("23", 0, 0)]
    [InlineData("07", 0, 0)]
    [InlineData("+7", 0, 0)]
    [InlineData("seven", 0, 0)]
    public void An_address_outside_the_set_is_refused_with_a_sentence(string matrix, long row, long column) =>
        Assert.False(string.IsNullOrWhiteSpace(TileMatrixSet.WebMercatorQuad.Address(matrix, row, column, out _)));

    /// <summary>A TUREF grid is a set this server defines: 512 cells, its resolution as the cell, north first.</summary>
    [Fact]
    public void A_TUREF_set_is_512_cells_at_its_own_resolution_and_north_first()
    {
        TileMatrixSet set = TileMatrixSet.For(Tm30);

        Assert.Equal("turef-tm30", set.Id);
        Assert.Null(set.Uri);
        Assert.Null(set.WellKnownScaleSet);
        Assert.Equal("http://www.opengis.net/def/crs/EPSG/0/5254", set.Crs);
        Assert.Equal("urn:ogc:def:crs:EPSG::5254", set.CrsUrn);
        Assert.True(set.NorthFirst);
        Assert.Equal(["N", "E"], set.OrderedAxes);
        Assert.Equal((4_921_000d, 97_000d), set.Origin);
        Assert.Equal(Tm30.LevelCount, set.Matrices.Count);

        // ADR-096 §5.2's table: TM30 level 0 is 1,173.828125 m a pixel; its 0.28 mm scale is that over 0.28 mm.
        Assert.Equal(3460.9375, set.Matrices[0].CellSize);
        AssertClose(3460.9375 / 0.28e-3, set.Matrices[0].ScaleDenominator, 1e-12);
        Assert.Equal(512, set.Matrices[0].TileWidth);
        Assert.Equal(1, set.Matrices[0].MatrixWidth);
        Assert.Equal(Tm30.TilesAcross(5), set.Matrices[5].MatrixWidth);
    }

    /// <summary>A built-in's name only for a built-in's numbers; anything else is custom, named by its grid.</summary>
    [Fact]
    public void A_grid_that_is_not_a_built_in_s_numbers_is_custom_and_named_by_its_key()
    {
        Assert.Null(VectorTileScheme.Create(
            "turef-tm30", 5254, 364_000, 4_593_000, VectorTileScheme.Halving(1000, 10), out VectorTileScheme? moved));

        TileMatrixSet set = TileMatrixSet.For(moved!);

        Assert.Equal(TileMatrixSet.CustomPrefix + moved!.Key, set.Id);
        Assert.DoesNotContain(TileMatrixSet.Standing, s => s.Id == set.Id);
        Assert.Equal(1 + VectorTileSchemes.BuiltIn.Count, TileMatrixSet.Standing.Count);
    }

    /// <summary>The JSON is 17-083r4's encoding, and the Web Mercator one reads like the register's.</summary>
    [Fact]
    public void The_set_is_written_in_the_standard_s_JSON()
    {
        JsonElement set = Json(TileDocuments.TileMatrixSetDocument("https://x/ogc/tiles/v1", TileMatrixSet.WebMercatorQuad));

        Assert.Equal("WebMercatorQuad", set.GetProperty("id").GetString());
        Assert.Equal(TileMatrixSet.WebMercatorQuadUri, set.GetProperty("uri").GetString());
        Assert.Equal(["X", "Y"], set.GetProperty("orderedAxes").EnumerateArray().Select(a => a.GetString()));

        JsonElement first = set.GetProperty("tileMatrices")[0];

        Assert.Equal("0", first.GetProperty("id").GetString());
        Assert.Equal("topLeft", first.GetProperty("cornerOfOrigin").GetString());
        Assert.Equal(-TileAddress.WebMercatorHalfExtent, first.GetProperty("pointOfOrigin")[0].GetDouble());
        Assert.Equal(TileAddress.WebMercatorHalfExtent, first.GetProperty("pointOfOrigin")[1].GetDouble());
        Assert.Equal(256, first.GetProperty("tileWidth").GetInt32());
        Assert.Equal(1, first.GetProperty("matrixWidth").GetInt32());

        JsonElement tm30 = Json(TileDocuments.TileMatrixSetDocument("https://x/ogc/tiles/v1", TileMatrixSet.For(Tm30)));

        // North first, because EPSG:5254's authority writes it so (ADR-060).
        Assert.Equal(4_921_000, tm30.GetProperty("tileMatrices")[0].GetProperty("pointOfOrigin")[0].GetDouble());
        Assert.False(tm30.TryGetProperty("uri", out _));
    }

    // ---------- OGC API Tiles documents ----------

    /// <summary>Everything claimed is a class the standards define; nothing unbuilt is claimed.</summary>
    [Fact]
    public void The_conformance_declaration_claims_what_is_built_and_nothing_else()
    {
        string[] claimed = [.. Json(TileDocuments.Conformance()).GetProperty("conformsTo").EnumerateArray().Select(c => c.GetString()!)];

        Assert.Contains("http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/core", claimed);
        Assert.Contains("http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/mvt", claimed);
        Assert.Contains("http://www.opengis.net/spec/tms/2.0/conf/json-tilematrixset", claimed);
        Assert.DoesNotContain(claimed, c => c.EndsWith("/oas30", StringComparison.Ordinal));
        Assert.DoesNotContain(claimed, c => c.EndsWith("/dataset-tilesets", StringComparison.Ordinal));
        Assert.DoesNotContain(claimed, c => c.EndsWith("/collections-selection", StringComparison.Ordinal));
        Assert.DoesNotContain(claimed, c => c.EndsWith("/datetime", StringComparison.Ordinal));
    }

    /// <summary>The tileset names its set, links it by the relation the requirement names, and templates its tiles.</summary>
    [Fact]
    public void A_Web_Mercator_tileset_links_its_set_its_template_and_its_TileJSON()
    {
        const string Root = "https://x/ogc/tiles/v1";
        JsonElement tileset = Json(TileDocuments.Tileset(Root, Service(TileMatrixSet.WebMercatorQuad)));

        Assert.Equal("vector", tileset.GetProperty("dataType").GetString());
        Assert.Equal(TileMatrixSet.WebMercatorQuadUri, tileset.GetProperty("tileMatrixSetURI").GetString());

        JsonElement[] links = [.. tileset.GetProperty("links").EnumerateArray()];

        Assert.Contains(links, l => l.GetProperty("rel").GetString() == TileNames.RelTilingScheme
            && l.GetProperty("href").GetString() == Root + "/tileMatrixSets/WebMercatorQuad");

        JsonElement item = Assert.Single(links, l => l.GetProperty("rel").GetString() == "item");
        Assert.True(item.GetProperty("templated").GetBoolean());
        Assert.Equal(TileNames.Mvt, item.GetProperty("type").GetString());
        Assert.Equal(
            Root + "/collections/hosted.parcels/tiles/WebMercatorQuad/{tileMatrix}/{tileRow}/{tileCol}",
            item.GetProperty("href").GetString());

        Assert.Contains(links, l => l.GetProperty("rel").GetString() == "alternate"
            && l.GetProperty("href").GetString()!.EndsWith("?f=tilejson", StringComparison.Ordinal));

        JsonElement layer = tileset.GetProperty("layers")[0];
        Assert.Equal("parcels", layer.GetProperty("id").GetString());
        Assert.Equal(2, layer.GetProperty("geometryDimension").GetInt32());
        Assert.Equal("integer", layer.GetProperty("propertiesSchema").GetProperty("properties").GetProperty("objectid").GetProperty("type").GetString());
    }

    /// <summary>A TUREF tileset offers no TileJSON, and its box is in its own reference, north first.</summary>
    [Fact]
    public void A_TUREF_tileset_has_no_TileJSON_and_a_north_first_box()
    {
        JsonElement tileset = Json(TileDocuments.Tileset("https://x/ogc/tiles/v1", Service(TileMatrixSet.For(Tm30))));

        Assert.DoesNotContain(tileset.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "alternate");
        Assert.False(tileset.TryGetProperty("tileMatrixSetURI", out _));
        Assert.Equal(4_000_000, tileset.GetProperty("boundingBox").GetProperty("lowerLeft")[0].GetDouble());
        Assert.Equal(400_000, tileset.GetProperty("boundingBox").GetProperty("lowerLeft")[1].GetDouble());
    }

    /// <summary>The collection id is one path segment; a folder and a name meet at a dot.</summary>
    [Fact]
    public void A_collection_id_is_one_segment()
    {
        Assert.Equal("parcels", TileDocuments.CollectionId(null, "parcels"));
        Assert.Equal("hosted.parcels", TileDocuments.CollectionId("hosted", "parcels"));
    }

    /// <summary>An id is looked up as every folder and name it can mean, so a dotted id finds its folder.</summary>
    [Fact]
    public void An_id_is_read_as_the_root_and_as_every_split_at_a_dot()
    {
        Assert.Equal([(null, "parcels")], TileFaces.Readings("parcels"));
        Assert.Equal([(null, "hosted.parcels"), ("hosted", "parcels")], TileFaces.Readings("hosted.parcels"));
        Assert.Equal(
            [(null, "a.b.c"), ("a", "b.c"), ("a.b", "c")],
            TileFaces.Readings("a.b.c"));

        // A leading or trailing dot names no folder or no service.
        Assert.Equal([(null, ".x")], TileFaces.Readings(".x"));
        Assert.Equal([(null, "x.")], TileFaces.Readings("x."));
    }

    // ---------- TileJSON ----------

    /// <summary>TileJSON 3.0.0 with the tile template, the layers' fields and zooms, bounds and a centre inside them.</summary>
    [Fact]
    public void TileJSON_is_3_0_0_with_the_template_and_what_the_tile_carries()
    {
        JsonElement document = Json(TileJson.Write(
            "hosted/parcels",
            "https://x/ogc/tiles/v1/collections/hosted.parcels/tiles/WebMercatorQuad/{z}/{y}/{x}",
            [new TileJsonLayer("parcels", Fields, 10, 22)],
            10,
            22,
            new Envelope(28.9, 40.9, 29.1, 41.1),
            null));

        Assert.Equal("3.0.0", document.GetProperty("tilejson").GetString());
        Assert.Equal("xyz", document.GetProperty("scheme").GetString());
        Assert.Contains("{z}/{y}/{x}", document.GetProperty("tiles")[0].GetString(), StringComparison.Ordinal);
        Assert.Equal(10, document.GetProperty("minzoom").GetInt32());
        Assert.Equal(22, document.GetProperty("maxzoom").GetInt32());

        JsonElement center = document.GetProperty("center");
        Assert.Equal(29.0, center[0].GetDouble(), 6);
        Assert.Equal(41.0, center[1].GetDouble(), 6);
        Assert.InRange(center[2].GetInt32(), 10, 22);

        JsonElement fields = document.GetProperty("vector_layers")[0].GetProperty("fields");
        Assert.Equal("Number", fields.GetProperty("objectid").GetString());
        Assert.Equal("String", fields.GetProperty("name").GetString());
    }

    /// <summary>Bounds outside the Mercator band are clamped to it, where TileJSON's own default puts them.</summary>
    [Fact]
    public void TileJSON_bounds_stay_inside_the_Mercator_band()
    {
        JsonElement bounds = Json(TileJson.Write("w", "t/{z}/{x}/{y}", [], 0, 5, new Envelope(-200, -90, 200, 90), null))
            .GetProperty("bounds");

        Assert.Equal(-180, bounds[0].GetDouble());
        Assert.Equal(-85.0511288, bounds[1].GetDouble(), 6);
        Assert.Equal(180, bounds[2].GetDouble());
    }

    /// <summary>The levels a layer is listed at are the ones the tile path draws it at.</summary>
    [Fact]
    public void A_layer_s_levels_are_the_tile_path_s_own()
    {
        Assert.Equal((0, 22), TileFaces.LevelsOf(VectorTileScheme.WebMercator, VisibleScaleRange.Unlimited));

        // A range down to 1:100,000 is drawn from the level whose tile is on screen at that scale.
        (int? min, int? max) = TileFaces.LevelsOf(VectorTileScheme.WebMercator, new VisibleScaleRange(100_000, 0));

        Assert.Equal(22, max);
        Assert.True(VectorTileScheme.WebMercator.Draws(new VisibleScaleRange(100_000, 0), min!.Value));
        Assert.False(VectorTileScheme.WebMercator.Draws(new VisibleScaleRange(100_000, 0), min.Value - 1));
    }

    // ---------- WMTS ----------

    /// <summary>
    /// A document has a ServiceProvider, and Sections names which parts it carries — OGC's WMTS 1.0 suite,
    /// 2026-10-06, failed both on the image services' WMTS, and this one had neither.
    /// </summary>
    [Fact]
    public void The_capabilities_carry_a_service_provider_and_only_the_sections_asked_for()
    {
        static string[] Parts(WmtsSections? sections)
        {
            XmlDocument xml = new();
            xml.LoadXml(Encoding.UTF8.GetString(WmtsCapabilities.Write(
                "https://x/wmts", [new WmtsLayer("roads", "roads", null, null, TileMatrixSet.WebMercatorQuad)], sections)));
            return [.. xml.DocumentElement!.ChildNodes.OfType<XmlElement>().Select(e => e.LocalName)];
        }

        Assert.Equal(
            ["ServiceIdentification", "ServiceProvider", "OperationsMetadata", "Contents", "ServiceMetadataURL"],
            Parts(null));

        Assert.True(WmtsSections.TryRead("ServiceProvider,contents", out WmtsSections? two, out _));
        Assert.Equal(["ServiceProvider", "Contents", "ServiceMetadataURL"], Parts(two));

        Assert.True(WmtsSections.TryRead("All", out WmtsSections? all, out _));
        Assert.Same(WmtsSections.All, all);
        Assert.False(WmtsSections.TryRead("Nonsense", out _, out WmtsFault? fault));
        Assert.Equal("Sections", fault!.Locator);
    }

    /// <summary>The capabilities are well formed, in WMTS 1.0's and OWS 1.1's namespaces, and say what a client needs.</summary>
    [Fact]
    public void The_capabilities_are_WMTS_1_0_0_with_each_layer_s_set()
    {
        byte[] bytes = WmtsCapabilities.Write(
            "https://x/wmts",
            [
                new WmtsLayer("hosted.parcels", "hosted/parcels", null, new Envelope(28, 40, 30, 42), TileMatrixSet.WebMercatorQuad),
                new WmtsLayer("roads", "roads", "Roads", null, TileMatrixSet.WebMercatorQuad),
                new WmtsLayer("hosted.tm30", "hosted/tm30", null, null, TileMatrixSet.For(Tm30)),
            ]);

        XmlDocument xml = new();
        xml.LoadXml(Encoding.UTF8.GetString(bytes));

        XmlNamespaceManager ns = new(xml.NameTable);
        ns.AddNamespace("w", WmtsCapabilities.Wmts);
        ns.AddNamespace("ows", WmtsFault.Ows);

        Assert.Equal("Capabilities", xml.DocumentElement!.LocalName);
        Assert.Equal(WmtsCapabilities.Wmts, xml.DocumentElement.NamespaceURI);
        Assert.Equal("1.0.0", xml.DocumentElement.GetAttribute("version"));
        Assert.Equal("OGC WMTS", xml.SelectSingleNode("//ows:ServiceIdentification/ows:ServiceType", ns)!.InnerText);

        XmlNodeList layers = xml.SelectNodes("/w:Capabilities/w:Contents/w:Layer", ns)!;
        Assert.Equal(3, layers.Count);

        XmlNode parcels = layers[0]!;
        Assert.Equal("hosted.parcels", parcels.SelectSingleNode("ows:Identifier", ns)!.InnerText);
        Assert.Equal(TileNames.Mvt, parcels.SelectSingleNode("w:Format", ns)!.InnerText);
        Assert.Equal("default", parcels.SelectSingleNode("w:Style[@isDefault='true']/ows:Identifier", ns)!.InnerText);
        Assert.Equal("WebMercatorQuad", parcels.SelectSingleNode("w:TileMatrixSetLink/w:TileMatrixSet", ns)!.InnerText);
        Assert.Equal("28 40", parcels.SelectSingleNode("ows:WGS84BoundingBox/ows:LowerCorner", ns)!.InnerText);
        Assert.Equal(
            "https://x/wmts/1.0.0/hosted.parcels/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.pbf",
            ((XmlElement)parcels.SelectSingleNode("w:ResourceURL", ns)!).GetAttribute("template"));

        // Each set once, and the Web Mercator one with its well-known scale set and its registered numbers.
        XmlNodeList sets = xml.SelectNodes("/w:Capabilities/w:Contents/w:TileMatrixSet", ns)!;
        Assert.Equal(2, sets.Count);

        XmlNode quad = sets[0]!;
        Assert.Equal("urn:ogc:def:crs:EPSG::3857", quad.SelectSingleNode("ows:SupportedCRS", ns)!.InnerText);
        Assert.Equal(WmtsCapabilities.GoogleMapsCompatibleUrn, quad.SelectSingleNode("w:WellKnownScaleSet", ns)!.InnerText);
        Assert.Equal("-20037508.342789244 20037508.342789244", quad.SelectSingleNode("w:TileMatrix/w:TopLeftCorner", ns)!.InnerText);
        Assert.Equal("256", quad.SelectSingleNode("w:TileMatrix/w:TileWidth", ns)!.InnerText);
        Assert.StartsWith("559082264.028717", quad.SelectSingleNode("w:TileMatrix/w:ScaleDenominator", ns)!.InnerText, StringComparison.Ordinal);

        // TM30's corner is northing first, as its reference writes it.
        XmlNode tm30 = sets[1]!;
        Assert.Equal("4921000 97000", tm30.SelectSingleNode("w:TileMatrix/w:TopLeftCorner", ns)!.InnerText);
        Assert.Equal("512", tm30.SelectSingleNode("w:TileMatrix/w:TileWidth", ns)!.InnerText);
        Assert.Null(tm30.SelectSingleNode("w:WellKnownScaleSet", ns));

        // Both bindings of GetTile are described.
        Assert.Equal(2, xml.SelectNodes("//ows:Operation[@name='GetTile']/ows:DCP/ows:HTTP/ows:Get", ns)!.Count);
    }

    /// <summary>KVP names are read without case and values with it; a GetTile is read whole.</summary>
    [Fact]
    public void A_GetTile_is_read_whatever_the_case_of_its_names()
    {
        Assert.True(WmtsRequest.TryParse(
            Query("service=wmts&Request=GetTile&version=1.0.0&layer=hosted.parcels&style=default"
                + "&format=application/vnd.mapbox-vector-tile&TileMatrixSet=WebMercatorQuad&TileMatrix=3&TileRow=2&TileCol=5"),
            out WmtsRequest? request,
            out WmtsFault? fault));

        Assert.Null(fault);
        Assert.Equal(WmtsOperation.GetTile, request!.Operation);
        Assert.Equal("hosted.parcels", request.Layer);
        Assert.Equal((2L, 5L), (request.TileRow, request.TileCol));
        Assert.Null(request.StyleOrFormatFault());
    }

    /// <summary>Each wrong request gets the code, locator and status WMTS Table 28 gives it.</summary>
    [Theory]
    [InlineData("request=GetCapabilities", "MissingParameterValue", "SERVICE", 400)]
    [InlineData("service=WMS&request=GetCapabilities", "InvalidParameterValue", "SERVICE", 400)]
    [InlineData("service=WMTS", "MissingParameterValue", "REQUEST", 400)]
    [InlineData("service=WMTS&request=GetFeatureInfo", "OperationNotSupported", "REQUEST", 501)]
    [InlineData("service=WMTS&request=GetBOGUS", "InvalidParameterValue", "REQUEST", 400)]
    [InlineData("service=WMTS&request=GetCapabilities&sections=Nonsense", "InvalidParameterValue", "Sections", 400)]
    [InlineData("service=WMTS&request=GetCapabilities&AcceptVersions=2.0.0", "VersionNegotiationFailed", "AcceptVersions", 400)]
    [InlineData("service=WMTS&request=GetTile&layer=a", "MissingParameterValue", "VERSION", 400)]
    [InlineData("service=WMTS&request=GetTile&version=1.1.0&layer=a", "InvalidParameterValue", "VERSION", 400)]
    [InlineData("service=WMTS&request=GetTile&version=1.0.0&layer=a&style=default&format=f&tilematrixset=s&tilerow=0&tilecol=0", "MissingParameterValue", "TILEMATRIX", 400)]
    [InlineData("service=WMTS&request=GetTile&version=1.0.0&layer=a&style=default&format=f&tilematrixset=s&tilematrix=0&tilerow=-1&tilecol=0", "InvalidParameterValue", "TILEROW", 400)]
    public void A_wrong_request_is_refused_as_WMTS_refuses_it(string query, string code, string locator, int status)
    {
        Assert.False(WmtsRequest.TryParse(Query(query), out _, out WmtsFault? fault));
        Assert.Equal(code, fault!.Code);
        Assert.Equal(locator, fault.Locator);
        Assert.Equal(status, fault.Status);
    }

    /// <summary>A style other than default, or a format other than MVT, is named in the refusal.</summary>
    [Fact]
    public void Only_the_default_style_and_the_vector_tile_format_are_offered()
    {
        WmtsRequest tile = new(WmtsOperation.GetTile, "a", "default", TileNames.Mvt, "WebMercatorQuad", "0", 0, 0);

        Assert.Null(tile.StyleOrFormatFault());
        Assert.Null((tile with { Style = "" }).StyleOrFormatFault());
        Assert.Equal("STYLE", (tile with { Style = "dark" }).StyleOrFormatFault()!.Locator);
        Assert.Equal("FORMAT", (tile with { Format = "image/png" }).StyleOrFormatFault()!.Locator);
    }

    /// <summary>The exception report is OWS Common 1.1's, with the code and locator as attributes.</summary>
    [Fact]
    public void The_exception_report_is_OWS_1_1()
    {
        XmlDocument xml = new();
        xml.LoadXml(Encoding.UTF8.GetString(new WmtsFault(WmtsFault.TileOutOfRange, "TILEROW", "Row 9 is outside.").ToXml()));

        Assert.Equal("ExceptionReport", xml.DocumentElement!.LocalName);
        Assert.Equal(WmtsFault.Ows, xml.DocumentElement.NamespaceURI);
        Assert.Equal("1.1.0", xml.DocumentElement.GetAttribute("version"));

        XmlElement exception = (XmlElement)xml.DocumentElement.FirstChild!;
        Assert.Equal("TileOutOfRange", exception.GetAttribute("exceptionCode"));
        Assert.Equal("TILEROW", exception.GetAttribute("locator"));
        Assert.Equal("Row 9 is outside.", exception.InnerText);
        Assert.Equal(400, new WmtsFault(WmtsFault.TileOutOfRange, null, "x").Status);
        Assert.Equal(500, new WmtsFault(WmtsFault.NoApplicableCode, null, "x").Status);
    }

    // ---------- helpers ----------

    private static readonly IReadOnlyList<FieldDescription> Fields =
    [
        new FieldDescription("objectid", FieldType.Integer, false, null),
        new FieldDescription("name", FieldType.Text, true, 80),
    ];

    private static TiledService Service(TileMatrixSet set) =>
        new(
            "hosted.parcels",
            "hosted/parcels",
            null,
            set,
            new Envelope(28, 40, 30, 42),
            new Envelope(400_000, 4_000_000, 500_000, 4_100_000),
            [new TileLayerInfo("parcels", 2, Fields, 0, 22)]);

    private static Func<string, string?> Query(string query)
    {
        Dictionary<string, string> pairs = new(StringComparer.OrdinalIgnoreCase);

        foreach (string pair in query.Split('&'))
        {
            int at = pair.IndexOf('=', StringComparison.Ordinal);
            pairs[pair[..at]] = pair[(at + 1)..];
        }

        return name => pairs.GetValueOrDefault(name);
    }

    private static JsonElement Json(byte[] bytes) => JsonDocument.Parse(bytes).RootElement.Clone();

    private static void AssertClose(double expected, double actual, double relative, double absolute = 0) =>
        Assert.True(
            Math.Abs(expected - actual) <= Math.Max(absolute, Math.Abs(expected) * relative),
            $"Expected {expected:R}, was {actual:R}.");
}
