using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// OGC API Tiles, TileJSON and WMTS against a running server — ADR-097.
/// </summary>
/// <remarks>
/// <para>
/// <b>The claim is that the three standard faces serve the tiles the ArcGIS face serves</b>, and it is
/// tested the only way that can fail: one tile is fetched through the ArcGIS face, then through OGC API
/// Tiles, WMTS KVP and WMTS REST, and each must be the same bytes, the same ETag and — the ArcGIS request
/// having built or read it — a cache <c>HIT</c>. A face that built through its own code would pass the
/// bytes and fail the <c>HIT</c>; one that addressed a different tile would fail the bytes.
/// </para>
/// <para>
/// <b>References none of the server's assemblies</b>, like the rest of this suite, so the paths, media
/// types and identifiers are written out here from the standards rather than read from the constants the
/// server uses.
/// </para>
/// <para>
/// <b>Against <c>GRATICULA_TEST_TILE_SERVICE</c>, and in the catalogue-walk collection</b>, with the other
/// classes that read that service's cache state; the sharing case publishes and removes a private service
/// of its own.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class StandardTileFacesConformanceTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private const string Ogc = "/ogc/tiles/v1";

    private const string Mvt = "application/vnd.mapbox-vector-tile";

    private const string TilesConf = "http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/";

    private const string TmsConf = "http://www.opengis.net/spec/tms/2.0/conf/";

    private const string RelTilingScheme = "http://www.opengis.net/def/rel/ogc/1.0/tiling-scheme";

    private const string RelTilesetsVector = "http://www.opengis.net/def/rel/ogc/1.0/tilesets-vector";

    private const string Wmts = "http://www.opengis.net/wmts/1.0";

    private const string Ows = "http://www.opengis.net/ows/1.1";

    /// <summary>Every class this face may claim, each with the observation that makes it true below.</summary>
    private static readonly string[] Proven =
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

    // ---------- the documents ----------

    /// <summary>Landing page, conformance, collections, tilesets, tileset and tile matrix sets answer and lead to one another.</summary>
    [Fact]
    public async Task The_landing_page_leads_to_every_resource_and_each_answers()
    {
        (string qualified, string id) = await TileServiceAsync();

        JsonElement landing = await OgcJsonAsync(Ogc);

        foreach (string rel in (string[])["self", "conformance", "data", "http://www.opengis.net/def/rel/ogc/1.0/tiling-schemes"])
        {
            Assert.True(Link(landing, rel) is { Length: > 0 }, $"The landing page has no `{rel}` link.");
        }

        JsonElement collections = await OgcJsonAsync(Ogc + "/collections");
        JsonElement collection = collections.GetProperty("collections").EnumerateArray()
            .SingleOrDefault(c => c.GetProperty("id").GetString() == id);

        Assert.True(
            collection.ValueKind == JsonValueKind.Object,
            $"`{qualified}` is a tile service the suite may read, and /collections has no `{id}`: {collections}");

        string? tilesets = Link(collection, RelTilesetsVector);
        Assert.True(tilesets is { Length: > 0 }, $"`{id}` has no {RelTilesetsVector} link.");

        JsonElement list = await OgcJsonAsync(tilesets!);
        JsonElement tileset = Assert.Single(list.GetProperty("tilesets").EnumerateArray());

        Assert.Equal("vector", tileset.GetProperty("dataType").GetString());
        Assert.True(Link(tileset, RelTilingScheme) is { Length: > 0 }, "A listed tileset has no tiling-scheme link.");

        JsonElement metadata = await OgcJsonAsync(Link(tileset, "self")!);
        JsonElement item = metadata.GetProperty("links").EnumerateArray()
            .Single(l => l.GetProperty("rel").GetString() == "item");

        Assert.True(item.GetProperty("templated").GetBoolean(), "The tile link is not marked templated.");
        Assert.Equal(Mvt, item.GetProperty("type").GetString());
        Assert.Contains("{tileMatrix}/{tileRow}/{tileCol}", item.GetProperty("href").GetString(), StringComparison.Ordinal);

        JsonElement set = await OgcJsonAsync(Link(metadata, RelTilingScheme)!);
        Assert.True(set.GetProperty("tileMatrices").GetArrayLength() > 0, $"The tile matrix set has no matrices: {set}");

        JsonElement sets = await OgcJsonAsync(Ogc + "/tileMatrixSets");
        Assert.Contains(sets.GetProperty("tileMatrixSets").EnumerateArray(), s => s.GetProperty("id").GetString() == "WebMercatorQuad");
        Assert.Contains(sets.GetProperty("tileMatrixSets").EnumerateArray(), s => s.GetProperty("id").GetString() == set.GetProperty("id").GetString());
    }

    /// <summary>Nothing is claimed without an observation here, and nothing observed is unclaimed.</summary>
    [Fact]
    public async Task Every_conformance_claim_is_proven_and_every_proof_is_claimed()
    {
        await RequireServerAsync();

        string[] claimed = [.. (await OgcJsonAsync(Ogc + "/conformance")).GetProperty("conformsTo").EnumerateArray().Select(c => c.GetString()!)];

        Assert.True(
            claimed.All(c => Proven.Contains(c, StringComparer.Ordinal)),
            "OGC API Tiles claims classes nothing here proves: " + string.Join(", ", claimed.Except(Proven)));

        Assert.True(
            Proven.All(p => claimed.Contains(p, StringComparer.Ordinal)),
            "This file proves classes the server does not claim: " + string.Join(", ", Proven.Except(claimed)));

        // `core`, `mvt`: a tile is served, as a Mapbox Vector Tile. `tileset`, `tilesets-list`,
        // `geodata-tilesets`, `tilesetmetadata`: The_landing_page_leads_to_every_resource_and_each_answers walks
        // them. `tilematrixset`, `json-tilematrixset`: the set's arithmetic is checked here.
        (_, string id) = await TileServiceAsync();
        JsonElement tileset = await OgcJsonAsync(await TilesetPathAsync(id));
        JsonElement set = await OgcJsonAsync(Link(tileset, RelTilingScheme)!);

        foreach (JsonElement matrix in set.GetProperty("tileMatrices").EnumerateArray())
        {
            double scale = matrix.GetProperty("scaleDenominator").GetDouble();
            double cell = matrix.GetProperty("cellSize").GetDouble();

            // 17-083r4 §6.1.1: the cell is the scale times the standardized 0.28 mm pixel, in metres here.
            Assert.True(
                Math.Abs((scale * 0.28e-3) - cell) <= cell * 1e-9,
                $"Tile matrix {matrix.GetProperty("id").GetString()} says scale {scale} and cell {cell}, which 0.28 mm does not join.");
        }

        (HttpStatusCode status, _, string? media, _, _) = await FetchAsync(Ogc + $"/collections/{Uri.EscapeDataString(id)}/tiles/{set.GetProperty("id").GetString()}/0/0/0");

        Assert.True(status is HttpStatusCode.OK or HttpStatusCode.NoContent, $"A level-0 tile answered {(int)status}.");

        if (status == HttpStatusCode.OK)
        {
            Assert.Equal(Mvt, media);
        }
    }

    // ---------- one tile, four addresses ----------

    /// <summary>
    /// A tile through the ArcGIS face, OGC API Tiles, WMTS KVP and WMTS REST is the same bytes and the same
    /// ETag, and every request after the first is a cache HIT.
    /// </summary>
    [Fact]
    public async Task A_tile_is_the_same_bytes_and_the_same_cache_entry_on_every_face()
    {
        (string qualified, string id) = await TileServiceAsync();
        (int z, long row, long column, string set) = await APopulatedTileAsync(qualified, id);

        (HttpStatusCode arcStatus, byte[] arcGis, _, _, string? arcTag) =
            await FetchAsync($"/rest/services/{qualified}/VectorTileServer/tile/{z}/{row}/{column}.pbf");

        Assert.Equal(HttpStatusCode.OK, arcStatus);

        string zs = z.ToString(CultureInfo.InvariantCulture);
        string rs = row.ToString(CultureInfo.InvariantCulture);
        string cs = column.ToString(CultureInfo.InvariantCulture);

        (string Face, string Path)[] faces =
        [
            ("OGC API Tiles", $"{Ogc}/collections/{Uri.EscapeDataString(id)}/tiles/{set}/{zs}/{rs}/{cs}"),
            ("WMTS KVP", $"/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={Uri.EscapeDataString(id)}&STYLE=default"
                + $"&FORMAT={Uri.EscapeDataString(Mvt)}&TILEMATRIXSET={set}&TILEMATRIX={zs}&TILEROW={rs}&TILECOL={cs}"),
            ("WMTS REST", $"/wmts/1.0.0/{Uri.EscapeDataString(id)}/default/{set}/{zs}/{rs}/{cs}.pbf"),
        ];

        foreach ((string face, string path) in faces)
        {
            (HttpStatusCode status, byte[] bytes, string? media, string? state, string? tag) = await FetchAsync(path);

            Assert.True(status == HttpStatusCode.OK, $"{face} answered {(int)status} for the tile the ArcGIS face served: {Encoding.UTF8.GetString(bytes)}");
            Assert.Equal(Mvt, media);
            Assert.True(
                bytes.AsSpan().SequenceEqual(arcGis),
                $"{face} served {bytes.Length} bytes and the ArcGIS face {arcGis.Length} for level {z}, row {row}, column {column}.");
            Assert.True(
                state == "HIT",
                $"{face} was X-Tile-Cache: {state} for a tile the ArcGIS face had just served. It reads a cache entry "
                + "the ArcGIS face does not, or builds through its own code (ADR-097 §5.5).");
            Assert.Equal(arcTag, tag);
        }

        // The validator works across faces: a tag the ArcGIS face gave is a 304 on the OGC face.
        (HttpStatusCode revalidated, _, _, _, _) = await FetchAsync(faces[0].Path, arcTag);
        Assert.Equal(HttpStatusCode.NotModified, revalidated);
    }

    /// <summary>The TileJSON's template, filled in, is a real tile — the ArcGIS face's at the same address.</summary>
    [Fact]
    public async Task The_TileJSON_template_resolves_to_a_real_tile()
    {
        (string qualified, string id) = await TileServiceAsync();
        string tileset = await TilesetPathAsync(id);

        if (!tileset.EndsWith("/WebMercatorQuad", StringComparison.Ordinal))
        {
            // A service on another grid has no TileJSON, and says why.
            (HttpStatusCode refused, byte[] said, string? media, _, _) = await FetchAsync(tileset + "?f=tilejson");
            Assert.Equal(HttpStatusCode.NotAcceptable, refused);
            Assert.Equal("application/problem+json", media);
            Assert.Contains("TileJSON", Encoding.UTF8.GetString(said), StringComparison.Ordinal);
            return;
        }

        JsonElement tileJson = await OgcJsonAsync(tileset + "?f=tilejson");

        Assert.Equal("3.0.0", tileJson.GetProperty("tilejson").GetString());
        Assert.True(tileJson.GetProperty("vector_layers").GetArrayLength() > 0, $"No vector_layers: {tileJson}");

        (int z, long row, long column, _) = await APopulatedTileAsync(qualified, id);

        string url = tileJson.GetProperty("tiles")[0].GetString()!
            .Replace("{z}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{y}", row.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{x}", column.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        Assert.DoesNotContain("{", url, StringComparison.Ordinal);

        (HttpStatusCode status, byte[] bytes, string? type, _, _) = await FetchAsync(new Uri(url).PathAndQuery);
        (HttpStatusCode arcStatus, byte[] arcGis, _, _, _) = await FetchAsync($"/rest/services/{qualified}/VectorTileServer/tile/{z}/{row}/{column}.pbf");

        Assert.Equal(HttpStatusCode.OK, arcStatus);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Mvt, type);
        Assert.True(bytes.AsSpan().SequenceEqual(arcGis), $"The TileJSON template's tile is {bytes.Length} bytes; the ArcGIS face's {arcGis.Length}.");
        Assert.InRange(z, tileJson.GetProperty("minzoom").GetInt32(), tileJson.GetProperty("maxzoom").GetInt32());
    }

    /// <summary>The WMTS capabilities list the service with its set, and its ResourceURL resolves.</summary>
    [Fact]
    public async Task The_WMTS_capabilities_list_the_service_and_its_template_resolves()
    {
        (string qualified, string id) = await TileServiceAsync();
        (HttpStatusCode status, byte[] body, string? media, _, _) = await FetchAsync("/wmts?service=WMTS&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/xml", media);

        XmlDocument xml = new();
        xml.LoadXml(Encoding.UTF8.GetString(body));
        XmlNamespaceManager ns = new(xml.NameTable);
        ns.AddNamespace("w", Wmts);
        ns.AddNamespace("ows", Ows);

        XmlNode? layer = xml.SelectSingleNode($"/w:Capabilities/w:Contents/w:Layer[ows:Identifier='{id}']", ns);
        Assert.True(layer is not null, $"The capabilities have no layer `{id}` for `{qualified}`.");

        string set = layer!.SelectSingleNode("w:TileMatrixSetLink/w:TileMatrixSet", ns)!.InnerText;
        Assert.NotNull(xml.SelectSingleNode($"/w:Capabilities/w:Contents/w:TileMatrixSet[ows:Identifier='{set}']", ns));

        (int z, long row, long column, _) = await APopulatedTileAsync(qualified, id);
        string template = ((XmlElement)layer.SelectSingleNode("w:ResourceURL", ns)!).GetAttribute("template")
            .Replace("{Style}", "default", StringComparison.Ordinal)
            .Replace("{TileMatrixSet}", set, StringComparison.Ordinal)
            .Replace("{TileMatrix}", z.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{TileRow}", row.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{TileCol}", column.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        (HttpStatusCode tile, _, string? type, _, _) = await FetchAsync(new Uri(template).PathAndQuery);
        Assert.Equal(HttpStatusCode.OK, tile);
        Assert.Equal(Mvt, type);

        // A second RESTful address answers the same document.
        (HttpStatusCode rest, _, _, _, _) = await FetchAsync("/wmts/1.0.0/WMTSCapabilities.xml");
        Assert.Equal(HttpStatusCode.OK, rest);
    }

    // ---------- refusals ----------

    /// <summary>Each face refuses in its own vocabulary, with the status its standard gives.</summary>
    [Fact]
    public async Task Each_face_refuses_in_its_own_vocabulary()
    {
        (_, string id) = await TileServiceAsync();

        (HttpStatusCode absent, _, string? absentType, _, _) = await FetchAsync(Ogc + "/collections/zz_no_such_tiles");
        Assert.Equal(HttpStatusCode.NotFound, absent);
        Assert.Equal("application/problem+json", absentType);

        string tileset = await TilesetPathAsync(id);
        (HttpStatusCode outside, _, string? outsideType, _, _) = await FetchAsync(tileset + "/0/5/5");
        Assert.Equal(HttpStatusCode.BadRequest, outside);
        Assert.Equal("application/problem+json", outsideType);

        (HttpStatusCode missing, byte[] report, string? reportType, _, _) = await FetchAsync("/wmts?service=WMTS&request=GetTile&version=1.0.0");
        Assert.Equal(HttpStatusCode.BadRequest, missing);
        Assert.Equal("application/xml", reportType);
        Assert.Equal("MissingParameterValue", ExceptionCode(report));

        (HttpStatusCode unsupported, byte[] unsupportedReport, _, _, _) = await FetchAsync("/wmts?service=WMTS&request=GetFeatureInfo");
        Assert.Equal(HttpStatusCode.NotImplemented, unsupported);
        Assert.Equal("OperationNotSupported", ExceptionCode(unsupportedReport));
    }

    /// <summary>
    /// A private service is not on any of the three faces for an anonymous caller — not listed, and answered
    /// as absent — and is on them for a caller who may read it.
    /// </summary>
    [Fact]
    public async Task A_private_service_is_invisible_to_an_anonymous_caller_on_every_face()
    {
        const string Private = "zz_tiles_private";

        await RequireServerAsync();

        try
        {
            (int published, string said) = await PublishOneAsync(Private, Private, sharing: "private", skip: 2);
            Assert.True(published is 200 or 201, $"publishing {Private}: {published} {said}");

            // The suite's account sees it, which is what makes the anonymous absence below mean something. The
            // listing may be remembered for a moment, so it is asked a few times.
            bool seen = false;

            for (int attempt = 0; attempt < 20 && !seen; attempt++)
            {
                seen = (await OgcJsonAsync(Ogc + "/collections")).GetProperty("collections").EnumerateArray()
                    .Any(c => c.GetProperty("id").GetString() == Private);

                if (!seen)
                {
                    await Task.Delay(500);
                }
            }

            Assert.True(seen, $"`{Private}` was published and the suite's account never saw it on /collections.");

            (HttpStatusCode listed, string collections) = await AnonymousAsync(Ogc + "/collections");
            Assert.Equal(HttpStatusCode.OK, listed);
            Assert.DoesNotContain($"\"{Private}\"", collections, StringComparison.Ordinal);

            foreach (string path in (string[])
            [
                $"{Ogc}/collections/{Private}",
                $"{Ogc}/collections/{Private}/tiles",
                $"{Ogc}/collections/{Private}/tiles/WebMercatorQuad",
                $"{Ogc}/collections/{Private}/tiles/WebMercatorQuad?f=tilejson",
                $"{Ogc}/collections/{Private}/tiles/WebMercatorQuad/0/0/0",
            ])
            {
                (HttpStatusCode status, _) = await AnonymousAsync(path);
                Assert.True(status == HttpStatusCode.NotFound, $"An anonymous {path} answered {(int)status}.");
            }

            (HttpStatusCode capabilities, string document) = await AnonymousAsync("/wmts?service=WMTS&request=GetCapabilities");
            Assert.Equal(HttpStatusCode.OK, capabilities);
            Assert.DoesNotContain($">{Private}<", document, StringComparison.Ordinal);

            foreach (string path in (string[])
            [
                $"/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={Private}&STYLE=default&FORMAT={Uri.EscapeDataString(Mvt)}"
                    + "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0",
                $"/wmts/1.0.0/{Private}/default/WebMercatorQuad/0/0/0.pbf",
            ])
            {
                (HttpStatusCode status, string body) = await AnonymousAsync(path);
                Assert.True(status == HttpStatusCode.BadRequest, $"An anonymous {path} answered {(int)status}: {body}");
                Assert.Contains("InvalidParameterValue", body, StringComparison.Ordinal);
                Assert.Contains("locator=\"LAYER\"", body, StringComparison.Ordinal);
            }

            // And the same absent answer the ArcGIS face gives it.
            (HttpStatusCode arcGis, _) = await AnonymousAsync($"/rest/services/{Private}/VectorTileServer");
            Assert.Equal(HttpStatusCode.NotFound, arcGis);

            // While the account that may read it is served through every face.
            (HttpStatusCode mine, _, _, _, _) = await FetchAsync($"{Ogc}/collections/{Private}/tiles/WebMercatorQuad/0/0/0");
            Assert.True(mine is HttpStatusCode.OK or HttpStatusCode.NoContent, $"The suite's account was refused its own tile: {(int)mine}.");
        }
        finally
        {
            await UnpublishAsync(Private, Private);
        }
    }

    // ---------- helpers ----------

    private async Task<(string Qualified, string Id)> TileServiceAsync()
    {
        await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(string.IsNullOrWhiteSpace(configured), $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        // The standard faces' id is the qualified name with a dot where the folder ends (ADR-097 §5.1).
        string qualified = configured!.Trim('/');
        return (qualified, qualified.Replace('/', '.'));
    }

    /// <summary>The fixture's tileset address, from its tilesets list.</summary>
    private async Task<string> TilesetPathAsync(string id)
    {
        JsonElement list = await OgcJsonAsync($"{Ogc}/collections/{Uri.EscapeDataString(id)}/tiles");
        string self = Link(list.GetProperty("tilesets")[0], "self")!;

        return new Uri(self).AbsolutePath;
    }

    /// <summary>
    /// A tile the ArcGIS face serves with something in it: the middle of the service's box, from the first level
    /// every layer draws at, down until a tile is not empty.
    /// </summary>
    private async Task<(int Z, long Row, long Column, string Set)> APopulatedTileAsync(string qualified, string id)
    {
        JsonElement tileset = await OgcJsonAsync(await TilesetPathAsync(id));
        JsonElement set = await OgcJsonAsync(Link(tileset, RelTilingScheme)!);
        bool northFirst = set.GetProperty("orderedAxes")[0].GetString() is "N" or "Lat";

        // <b>A feature's own vertex, not the middle of the box — 2026-09-29.</b> The fixture's twelve parcels
        // leave the middle of their box empty, and at the first levels each parcel is smaller than a pixel and
        // left out (ADR-085), so the middle found nothing down to level 7. A vertex of a real feature, asked for
        // in the tile matrix set's own reference, is inside a populated tile at every level it is drawn.
        string crs = set.GetProperty("crs").ValueKind == JsonValueKind.String
            ? set.GetProperty("crs").GetString()!
            : set.GetProperty("crs").GetProperty("uri").GetString()!;
        string epsg = crs[(crs.LastIndexOf('/') + 1)..];

        (HttpStatusCode featureStatus, byte[] featureBody, _, _, _) = await FetchAsync(
            $"/rest/services/{qualified}/FeatureServer/0/query?where=1%3D1&resultRecordCount=1&returnGeometry=true"
            + $"&outSR={epsg}&f=json");

        Assert.True(featureStatus == HttpStatusCode.OK, $"The feature query answered {(int)featureStatus}.");

        JsonElement geometry = JsonDocument.Parse(featureBody).RootElement
            .GetProperty("features")[0].GetProperty("geometry");
        JsonElement vertex = geometry.TryGetProperty("rings", out JsonElement rings) ? rings[0][0]
            : geometry.TryGetProperty("paths", out JsonElement paths) ? paths[0][0]
            : default;
        (double x, double y) = vertex.ValueKind == JsonValueKind.Array
            ? (vertex[0].GetDouble(), vertex[1].GetDouble())
            : (geometry.GetProperty("x").GetDouble(), geometry.GetProperty("y").GetDouble());

        int from = tileset.GetProperty("layers").EnumerateArray()
            .Select(l => l.TryGetProperty("minTileMatrix", out JsonElement m) ? int.Parse(m.GetString()!, CultureInfo.InvariantCulture) : 0)
            .DefaultIfEmpty(0)
            .Min();

        JsonElement[] matrices = [.. set.GetProperty("tileMatrices").EnumerateArray()];

        for (int z = from; z < Math.Min(matrices.Length, 19); z++)
        {
            JsonElement matrix = matrices[z];
            (double ox, double oy) = Point(matrix.GetProperty("pointOfOrigin"), northFirst);
            double span = matrix.GetProperty("cellSize").GetDouble() * matrix.GetProperty("tileWidth").GetInt32();
            long column = Math.Clamp((long)Math.Floor((x - ox) / span), 0, matrix.GetProperty("matrixWidth").GetInt64() - 1);
            long row = Math.Clamp((long)Math.Floor((oy - y) / span), 0, matrix.GetProperty("matrixHeight").GetInt64() - 1);

            (HttpStatusCode status, _, _, _, _) = await FetchAsync($"/rest/services/{qualified}/VectorTileServer/tile/{z}/{row}/{column}.pbf");

            if (status == HttpStatusCode.OK)
            {
                return (z, row, column, set.GetProperty("id").GetString()!);
            }
        }

        Assert.Fail($"No tile of `{qualified}` at a feature's vertex had anything in it at levels {from} to {Math.Min(matrices.Length, 19) - 1}.");
        return default;

        static (double X, double Y) Point(JsonElement pair, bool northFirst) =>
            northFirst ? (pair[1].GetDouble(), pair[0].GetDouble()) : (pair[0].GetDouble(), pair[1].GetDouble());
    }

    private async Task<JsonElement> OgcJsonAsync(string pathOrUrl)
    {
        string path = pathOrUrl.StartsWith("http", StringComparison.Ordinal) ? new Uri(pathOrUrl).PathAndQuery : pathOrUrl;
        (HttpStatusCode status, byte[] body, string? media, _, _) = await FetchAsync(path);

        Assert.True(status == HttpStatusCode.OK, $"{path} answered {(int)status}: {Encoding.UTF8.GetString(body)}");
        Assert.Equal("application/json", media);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, byte[] Body, string? Media, string? CacheState, string? ETag)> FetchAsync(
        string path, string? ifNoneMatch = null)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            await response.Content.ReadAsByteArrayAsync(),
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? state) ? state.FirstOrDefault() : null,
            response.Headers.ETag?.ToString());
    }

    private static string? Link(JsonElement document, string rel) =>
        document.TryGetProperty("links", out JsonElement links)
            ? links.EnumerateArray()
                .Where(l => l.GetProperty("rel").GetString() == rel)
                .Select(l => l.GetProperty("href").GetString())
                .FirstOrDefault()
            : null;

    private static string ExceptionCode(byte[] report)
    {
        XmlDocument xml = new();
        xml.LoadXml(Encoding.UTF8.GetString(report));

        Assert.Equal(Ows, xml.DocumentElement!.NamespaceURI);
        Assert.Equal("ExceptionReport", xml.DocumentElement.LocalName);

        return ((XmlElement)xml.DocumentElement.FirstChild!).GetAttribute("exceptionCode");
    }
}
