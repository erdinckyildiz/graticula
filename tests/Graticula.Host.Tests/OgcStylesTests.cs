using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Graticula.Api.Wms;
using Graticula.Geometries;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// ADR-176 without a server: how a style is named and read back, how a stylesheet is negotiated, how a Mapbox style's
/// relative addresses are made absolute, the documents' shapes, and SLD 1.0 beside 1.1.
/// </summary>
/// <remarks>
/// The live half — the list filtered by sharing, both encodings fetched and parsed, a 404 problem for an unknown
/// style — is <c>OgcStylesConformanceTests</c>, against a server.
/// </remarks>
public sealed class OgcStylesTests
{
    private const string Root = "https://example.org/ogc/styles/v1";

    private const string TilesRoot = "https://example.org/ogc/tiles/v1";

    private static readonly IReadOnlyList<(string, GeometryKind)> Layers =
        [("parcels", GeometryKind.Polygon), ("roads", GeometryKind.MultiLineString), ("wells", GeometryKind.Point)];

    private static OgcStyle Symbology(string collection = "hosted.parcels") =>
        new(OgcStylesDocuments.StyleId(collection, null), collection, collection.Replace('.', '/'), null, null,
            IsDefault: true, null, Layers);

    private static OgcStyle Stored(string name = "dark", string collection = "hosted.parcels") =>
        new(OgcStylesDocuments.StyleId(collection, name), collection, collection.Replace('.', '/'), "About it.", name,
            IsDefault: false, new DateTimeOffset(2026, 10, 6, 9, 30, 0, TimeSpan.Zero), Layers);

    // ---------- the id ----------

    /// <summary>The id is the tile faces' service id, a dot and the style's name, read back by its last dot.</summary>
    [Theory]
    [InlineData("parcels", "dark", "parcels.dark")]
    [InlineData("hosted.parcels", "default", "hosted.parcels.default")]
    [InlineData("hosted.parcels", null, "hosted.parcels._symbology")]
    [InlineData("a.b.c", "Light-2", "a.b.c.Light-2")]
    public void A_style_id_is_its_service_and_its_name_and_reads_back(string collection, string? name, string id)
    {
        Assert.Equal(id, OgcStylesDocuments.StyleId(collection, name));
        Assert.True(OgcStylesDocuments.TryParseStyleId(id, out string readCollection, out string? readName));
        Assert.Equal(collection, readCollection);
        Assert.Equal(name, readName);

        // Nothing in it needs escaping in a path.
        Assert.Equal(id, Uri.EscapeDataString(id));
    }

    /// <summary>An id with no service, no style, a name no style may have, or <c>root</c>, names nothing.</summary>
    [Theory]
    [InlineData("parcels")]
    [InlineData(".dark")]
    [InlineData("parcels.")]
    [InlineData("parcels.root")]
    [InlineData("parcels.-dark")]
    [InlineData("parcels.da rk")]
    [InlineData("")]
    public void An_id_that_cannot_be_one_is_refused(string id)
    {
        Assert.False(OgcStylesDocuments.TryParseStyleId(id, out _, out _));
    }

    /// <summary><c>_symbology</c> can never be a stored style's name, so its id never changes meaning.</summary>
    [Fact]
    public void The_symbology_suffix_is_not_a_name_an_author_can_store()
    {
        Assert.False(Graticula.Api.ArcGis.StyleNames.TryValidate(OgcStylesDocuments.SymbologySuffix, out _));
    }

    // ---------- negotiation ----------

    /// <summary><c>f</c> in both drafts' spellings, and it wins over Accept.</summary>
    [Theory]
    [InlineData("mapbox", "Mapbox")]
    [InlineData("mbgl", "Mapbox")]
    [InlineData("sld11", "Sld11")]
    [InlineData("SLD", "Sld11")]
    [InlineData("sld10", "Sld10")]
    [InlineData("html", "Html")]
    public void F_chooses_the_stylesheet_whatever_accept_says(string f, string expected)
    {
        (StyleAnswer? answer, Graticula.Api.OgcFeatures.OgcProblem? refusal) =
            OgcStylesDocuments.Negotiate(f, "application/vnd.ogc.cartosym+json", Symbology());

        Assert.Null(refusal);
        Assert.Equal(Enum.Parse<StyleAnswer>(expected), answer);
    }

    /// <summary>An <c>f</c> nobody defines is a 400; one defined and not offered for this style is a 406.</summary>
    [Fact]
    public void An_unknown_f_is_400_and_an_encoding_not_offered_is_406()
    {
        Assert.Equal(400, OgcStylesDocuments.Negotiate("cscss", null, Symbology()).Refusal?.Status);

        (StyleAnswer? answer, Graticula.Api.OgcFeatures.OgcProblem? refusal) = OgcStylesDocuments.Negotiate("sld11", null, Stored());
        Assert.Null(answer);
        Assert.Equal(406, refusal?.Status);

        // The refusal says where the SLD this server can write is.
        Assert.Contains("hosted.parcels._symbology", refusal!.ToJson(), StringComparison.Ordinal);
    }

    /// <summary>Accept, by q and then order; a wildcard or nothing is the native stylesheet; nothing acceptable is 406.</summary>
    [Theory]
    [InlineData(null, "Mapbox")]
    [InlineData("*/*", "Mapbox")]
    [InlineData("application/json", "Mapbox")]
    [InlineData("application/vnd.mapbox.style+json", "Mapbox")]
    [InlineData("application/vnd.ogc.sld+xml;version=1.1", "Sld11")]
    [InlineData("application/vnd.ogc.sld+xml; version=\"1.0\"", "Sld10")]
    [InlineData("application/vnd.ogc.sld+xml", "Sld11")]
    [InlineData("text/xml", "Sld11")]
    [InlineData("application/vnd.mapbox.style+json;q=0.5, application/vnd.ogc.sld+xml;version=1.0", "Sld10")]
    [InlineData("application/vnd.ogc.cartosym+css, application/vnd.ogc.sld+xml;version=1.1;q=0.2", "Sld11")]
    [InlineData("text/html,application/xhtml+xml,*/*;q=0.8", "Html")]
    public void Accept_chooses_among_what_the_symbology_style_has(string? accept, string expected)
    {
        (StyleAnswer? answer, Graticula.Api.OgcFeatures.OgcProblem? refusal) =
            OgcStylesDocuments.Negotiate(null, accept, Symbology());

        Assert.Null(refusal);
        Assert.Equal(Enum.Parse<StyleAnswer>(expected), answer);
    }

    /// <summary>A stored style is Mapbox only: SLD in Accept falls through to what else was accepted, or 406.</summary>
    [Fact]
    public void A_stored_style_is_offered_as_mapbox_only()
    {
        Assert.Equal(
            StyleAnswer.Mapbox,
            OgcStylesDocuments.Negotiate(null, "application/vnd.ogc.sld+xml;version=1.1, */*;q=0.1", Stored()).Answer);

        Assert.Equal(406, OgcStylesDocuments.Negotiate(null, "application/vnd.ogc.sld+xml;version=1.1", Stored()).Refusal?.Status);
        Assert.Equal(406, OgcStylesDocuments.Negotiate(null, "application/vnd.ogc.cartosym+json", Symbology()).Refusal?.Status);
        Assert.Equal(406, OgcStylesDocuments.Negotiate(null, "application/vnd.ogc.sld+xml;version=2.0", Symbology()).Refusal?.Status);
    }

    // ---------- absolute addresses ----------

    private static readonly Uri WrittenFor =
        new("https://example.org/rest/services/hosted/parcels/VectorTileServer/resources/styles/root.json");

    /// <summary>The generated style's relative source, glyphs and sprite resolve to the VectorTileServer's own.</summary>
    [Fact]
    public void A_vector_tile_server_style_s_relative_addresses_resolve_to_its_resources()
    {
        string style = """
            {"version":8,"sources":{"esri":{"type":"vector","url":"../../"},
             "extra":{"type":"vector","tiles":["../../tile/{z}/{y}/{x}.pbf","https://other.example/{z}/{x}/{y}.pbf"]}},
             "glyphs":"../fonts/{fontstack}/{range}.pbf","sprite":"../sprites/sprite",
             "layers":[{"id":"a","type":"fill","source":"esri","source-layer":"parcels","metadata":{"url":"../keep"}}]}
            """;

        JsonNode resolved = JsonNode.Parse(OgcStylesDocuments.AbsoluteAddresses(style, WrittenFor))!;

        Assert.Equal("https://example.org/rest/services/hosted/parcels/VectorTileServer/", (string?)resolved["sources"]!["esri"]!["url"]);
        Assert.Equal(
            "https://example.org/rest/services/hosted/parcels/VectorTileServer/tile/{z}/{y}/{x}.pbf",
            (string?)resolved["sources"]!["extra"]!["tiles"]![0]);
        Assert.Equal("https://other.example/{z}/{x}/{y}.pbf", (string?)resolved["sources"]!["extra"]!["tiles"]![1]);
        Assert.Equal(
            "https://example.org/rest/services/hosted/parcels/VectorTileServer/resources/fonts/{fontstack}/{range}.pbf",
            (string?)resolved["glyphs"]);
        Assert.Equal(
            "https://example.org/rest/services/hosted/parcels/VectorTileServer/resources/sprites/sprite",
            (string?)resolved["sprite"]);

        // Nothing but the fetching addresses is touched.
        Assert.Equal("../keep", (string?)resolved["layers"]![0]!["metadata"]!["url"]);
    }

    /// <summary>A sprite array (style spec 8's multi-sprite form) is resolved item by item.</summary>
    [Fact]
    public void Each_sprite_of_an_array_is_resolved()
    {
        string style = """{"version":8,"sources":{},"sprite":[{"id":"a","url":"../sprites/sprite"},{"id":"b","url":"https://x.example/s"}],"layers":[]}""";

        JsonNode resolved = JsonNode.Parse(OgcStylesDocuments.AbsoluteAddresses(style, WrittenFor))!;

        Assert.Equal("https://example.org/rest/services/hosted/parcels/VectorTileServer/resources/sprites/sprite", (string?)resolved["sprite"]![0]!["url"]);
        Assert.Equal("https://x.example/s", (string?)resolved["sprite"]![1]!["url"]);
    }

    /// <summary>A style whose addresses are all absolute is the author's file byte for byte (ADR-028).</summary>
    [Fact]
    public void A_style_with_nothing_relative_is_served_byte_for_byte()
    {
        string style = "{ \"version\": 8,\n  \"sources\": { \"s\": { \"type\": \"vector\", \"url\": \"https://a.example/tiles.json\" } },\n"
                       + "  \"glyphs\": \"mapbox://fonts/{fontstack}/{range}.pbf\", \"layers\": [] }";

        Assert.Same(style, OgcStylesDocuments.AbsoluteAddresses(style, WrittenFor));
        Assert.Same("not json", OgcStylesDocuments.AbsoluteAddresses("not json", WrittenFor));
    }

    // ---------- documents ----------

    /// <summary>The landing page links to the styles with the registered relation, and to the API and conformance.</summary>
    [Fact]
    public void The_landing_page_links_to_the_styles_the_api_and_the_conformance()
    {
        JsonElement landing = JsonDocument.Parse(OgcStylesDocuments.Landing(Root)).RootElement;

        Assert.Equal(Root + "/styles", Href(landing, "http://www.opengis.net/def/rel/ogc/1.0/styles"));
        Assert.Equal(Root + "/api", Href(landing, "service-desc"));
        Assert.Equal(Root + "/conformance", Href(landing, "http://www.opengis.net/def/rel/ogc/1.0/conformance"));
        Assert.Equal(
            "application/vnd.oai.openapi+json;version=3.0",
            landing.GetProperty("links").EnumerateArray().First(l => l.GetProperty("rel").GetString() == "service-desc")
                .GetProperty("type").GetString());
    }

    /// <summary>Core and html are claimed; the encoding classes, whose requirements are all about writing, are not.</summary>
    [Fact]
    public void Only_the_read_classes_are_claimed()
    {
        string[] claimed = [.. JsonDocument.Parse(OgcStylesDocuments.Conformance()).RootElement
            .GetProperty("conformsTo").EnumerateArray().Select(c => c.GetString()!)];

        Assert.Contains("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/core", claimed);
        Assert.Contains("http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/oas30", claimed);

        foreach (string write in (string[])["manage-styles", "style-validation", "mapbox-styles", "sld-10", "sld-11", "sld-se"])
        {
            Assert.DoesNotContain("http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/" + write, claimed);
        }
    }

    /// <summary>
    /// <c>/req/core/styles-success</c>: one item a style, unique ids, a typed <c>stylesheet</c> link for each encoding
    /// at <c>/styles/{id}</c>, and draft 1's <c>describedby</c> to the metadata.
    /// </summary>
    [Fact]
    public void The_style_list_has_a_typed_stylesheet_link_for_each_encoding()
    {
        JsonElement list = JsonDocument.Parse(OgcStylesDocuments.Styles(Root, [Symbology(), Stored()])).RootElement;
        JsonElement[] styles = [.. list.GetProperty("styles").EnumerateArray()];

        Assert.Equal(2, styles.Length);
        Assert.Equal(styles.Length, styles.Select(s => s.GetProperty("id").GetString()).Distinct().Count());
        Assert.False(list.TryGetProperty("default", out _));

        string[] symbology = [.. Links(styles[0], "stylesheet").Select(l => l.Type)];
        Assert.Equal(
            ["application/vnd.mapbox.style+json", "application/vnd.ogc.sld+xml;version=1.1", "application/vnd.ogc.sld+xml;version=1.0"],
            symbology);

        foreach ((string href, _) in Links(styles[0], "stylesheet"))
        {
            Assert.StartsWith(Root + "/styles/hosted.parcels._symbology?f=", href, StringComparison.Ordinal);
        }

        Assert.Equal(["application/vnd.mapbox.style+json"], Links(styles[1], "stylesheet").Select(l => l.Type));
        Assert.Equal(Root + "/styles/hosted.parcels.dark/metadata", Links(styles[1], "describedby").Single().Href);
        Assert.Equal("2026-10-06T09:30:00Z", styles[1].GetProperty("updated").GetString());
    }

    /// <summary>Draft 1's metadata: id, scope, a stylesheet per encoding with its specification, the layers with their data.</summary>
    [Fact]
    public void A_style_s_metadata_names_its_stylesheets_and_its_layers()
    {
        JsonElement metadata = JsonDocument.Parse(OgcStylesDocuments.Metadata(Root, TilesRoot, Symbology())).RootElement;

        Assert.Equal("hosted.parcels._symbology", metadata.GetProperty("id").GetString());
        Assert.Equal("style", metadata.GetProperty("scope").GetString());

        JsonElement[] sheets = [.. metadata.GetProperty("stylesheets").EnumerateArray()];
        Assert.Equal(["8", "1.1.0", "1.0.0"], sheets.Select(s => s.GetProperty("version").GetString()));
        Assert.All(sheets, s => Assert.False(s.GetProperty("native").GetBoolean()));
        Assert.All(sheets, s => Assert.Equal("stylesheet", s.GetProperty("link").GetProperty("rel").GetString()));

        Assert.Equal(
            ["polygon", "line", "point"],
            metadata.GetProperty("layers").EnumerateArray().Select(l => l.GetProperty("type").GetString()));
        Assert.Equal(
            TilesRoot + "/collections/hosted.parcels/tiles",
            metadata.GetProperty("layers")[0].GetProperty("sampleData").GetProperty("href").GetString());

        JsonElement stored = JsonDocument.Parse(OgcStylesDocuments.Metadata(Root, TilesRoot, Stored())).RootElement;
        Assert.True(stored.GetProperty("stylesheets")[0].GetProperty("native").GetBoolean());
        Assert.Equal("2026-10-06T09:30:00Z", stored.GetProperty("dates").GetProperty("revision").GetString());
    }

    /// <summary>The API definition is OpenAPI 3.0 with the six paths and nothing else.</summary>
    [Fact]
    public void The_api_definition_is_openapi_3_with_the_six_paths()
    {
        JsonElement api = JsonDocument.Parse(OgcStylesDocuments.OpenApi(Root)).RootElement;

        Assert.StartsWith("3.0.", api.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.Equal(
            ["/", "/conformance", "/api", "/styles", "/styles/{styleId}", "/styles/{styleId}/metadata"],
            api.GetProperty("paths").EnumerateObject().Select(p => p.Name));
        Assert.Equal(Root, api.GetProperty("servers")[0].GetProperty("url").GetString());
    }

    // ---------- SLD ----------

    private static readonly IReadOnlyList<(string Name, JsonNode? DrawingInfo, IReadOnlyList<string> Losses)> SldLayers =
    [
        ("parcels", JsonNode.Parse("""
            {"renderer":{"type":"uniqueValue","field1":"zone","uniqueValueInfos":[
              {"value":"A","label":"Zone A","symbol":{"type":"esriSFS","color":[255,0,0,255],"outline":{"type":"esriSLS","color":[0,0,0,255],"width":1}}}],
             "defaultSymbol":{"type":"esriSFS","color":[200,200,200,128]},"defaultLabel":"Other"}}
            """), []),
        ("roads", JsonNode.Parse("""
            {"renderer":{"type":"classBreaks","field":"lanes","minValue":1,"classBreakInfos":[
              {"classMaxValue":2,"label":"Small","symbol":{"type":"esriSLS","color":[0,0,255,255],"width":1.5,"style":"esriSLSDash"}},
              {"classMaxValue":6,"label":"Big","symbol":{"type":"esriSLS","color":[0,0,128,255],"width":3}}]}}
            """), []),
        ("wells", JsonNode.Parse("""{"renderer":{"type":"simple","symbol":{"type":"esriSMS","color":[0,128,0,255],"size":6}}}"""), []),
        ("odd", JsonNode.Parse("""{"renderer":{"type":"heatmap"}}"""), []),
    ];

    /// <summary>WMS GetStyles' document is unchanged by the new parameters' defaults.</summary>
    [Fact]
    public void The_wms_sld_is_what_it_was_by_default()
    {
        XDocument written = XDocument.Parse(StyledLayerDescriptor.Write(SldLayers));
        XNamespace se = "http://www.opengis.net/se";

        Assert.Equal("1.1.0", written.Root!.Attribute("version")?.Value);
        Assert.Empty(written.Descendants(se + "FeatureTypeName"));
        Assert.All(
            written.Descendants().Where(e => e.Name.LocalName == "UserStyle"),
            s => Assert.Equal("default", s.Element(se + "Name")?.Value));
    }

    /// <summary>For OGC API Styles: the UserStyle carries the style's id and each FeatureTypeStyle names its layer.</summary>
    [Fact]
    public void The_styles_sld_names_its_style_and_its_feature_types()
    {
        XDocument written = XDocument.Parse(StyledLayerDescriptor.Write(SldLayers, "hosted.parcels._symbology", namesFeatureTypes: true));
        XNamespace se = "http://www.opengis.net/se";

        Assert.Equal(["parcels", "roads", "wells"], written.Descendants(se + "FeatureTypeName").Select(e => e.Value));
        Assert.All(
            written.Descendants().Where(e => e.Name.LocalName == "UserStyle"),
            s => Assert.Equal("hosted.parcels._symbology", s.Element(se + "Name")?.Value));

        // The heatmap layer has no SLD form and says so with its NamedStyle.
        Assert.Single(written.Descendants(), e => e.Name.LocalName == "NamedStyle");
    }

    /// <summary>
    /// SLD 1.0 is the same rules in SLD 1.0's vocabulary: every element in the sld or ogc namespace, CssParameter, Title
    /// directly in a Rule, and the same number of rules and filters.
    /// </summary>
    [Fact]
    public void Sld_1_0_is_the_same_rules_in_its_own_vocabulary()
    {
        XDocument eleven = XDocument.Parse(StyledLayerDescriptor.Write(SldLayers, "s", namesFeatureTypes: true));
        XDocument ten = XDocument.Parse(StyledLayerDescriptor.Write10(SldLayers, "s", namesFeatureTypes: true));
        XNamespace sld = "http://www.opengis.net/sld";

        Assert.Equal("1.0.0", ten.Root!.Attribute("version")?.Value);
        Assert.Contains("sld/1.0.0/StyledLayerDescriptor.xsd", ten.Root.Attributes().First(a => a.Name.LocalName == "schemaLocation").Value, StringComparison.Ordinal);
        Assert.All(
            ten.Descendants(),
            e => Assert.Contains(e.Name.NamespaceName, (string[])["http://www.opengis.net/sld", "http://www.opengis.net/ogc"]));
        Assert.DoesNotContain(ten.Descendants(), e => e.Name.LocalName is "SvgParameter" or "Description");
        Assert.Equal(
            eleven.Descendants().Count(e => e.Name.LocalName == "SvgParameter"),
            ten.Descendants(sld + "CssParameter").Count());
        Assert.Equal(
            eleven.Descendants().Count(e => e.Name.LocalName == "Rule"),
            ten.Descendants(sld + "Rule").Count());
        Assert.Equal(
            eleven.Descendants().Count(e => e.Name.LocalName == "Filter"),
            ten.Descendants().Count(e => e.Name.LocalName == "Filter"));

        // A rule's title is straight in the rule, before its filter.
        XElement rule = ten.Descendants(sld + "Rule").First();
        Assert.Equal(["Name", "Title", "Filter", "PolygonSymbolizer"], rule.Elements().Select(e => e.Name.LocalName));
    }

    private static string? Href(JsonElement document, string rel) =>
        document.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == rel)
            .Select(l => l.GetProperty("href").GetString())
            .FirstOrDefault();

    private static (string Href, string Type)[] Links(JsonElement document, string rel) =>
        [.. document.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == rel)
            .Select(l => (l.GetProperty("href").GetString()!, l.GetProperty("type").GetString()!))];
}
