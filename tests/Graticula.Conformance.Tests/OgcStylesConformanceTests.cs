using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// OGC API – Styles against a running server — ADR-176.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no OGC executable test suite for OGC API Styles</b> (checked 2026-10-06: no <c>ets-ogcapi-styles</c>
/// repository under <c>opengeospatial</c> and no image under <c>ogccite</c>; the draft's own Annex A is commented
/// out until its requirements settle). So these cases are written from 20-009's requirements — draft 1's Annex A
/// test <c>/conf/core/1</c> step by step for the list, the stylesheets and the metadata — and the claim rests on
/// them.
/// </para>
/// <para>
/// <b>References none of the server's assemblies</b>, like the rest of this suite: the paths, media types and
/// URIs are typed in from the specification. The stored-style and sharing cases publish services of their own and
/// remove them, so the shared fixture's <c>ci_parcels</c> is read and never restyled.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class OgcStylesConformanceTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_TILE_SERVICE";

    private const string Ogc = "/ogc/styles/v1";

    private const string Mapbox = "application/vnd.mapbox.style+json";

    private const string Sld11 = "application/vnd.ogc.sld+xml;version=1.1";

    private const string Sld10 = "application/vnd.ogc.sld+xml;version=1.0";

    private const string RelStyles = "http://www.opengis.net/def/rel/ogc/1.0/styles";

    private const string StylesConf = "http://www.opengis.net/spec/ogcapi-styles-1/1.0/conf/";

    private const string CommonConf = "http://www.opengis.net/spec/ogcapi-common-1/1.0/conf/";

    /// <summary>Every class this face may claim, each with the observation that makes it true below.</summary>
    private static readonly string[] Proven =
    [
        CommonConf + "core",
        CommonConf + "landing-page",
        CommonConf + "json",
        CommonConf + "html",
        CommonConf + "oas30",
        StylesConf + "core",
        StylesConf + "html",
    ];

    // ---------- the documents ----------

    /// <summary>
    /// Nothing is claimed without an observation here, and nothing observed is unclaimed; the landing page leads to
    /// the conformance, the API definition (OpenAPI 3.0) and the styles, and each answers in JSON and in HTML.
    /// </summary>
    [Fact]
    public async Task Every_claim_is_proven_and_the_landing_page_leads_to_each_resource()
    {
        await RequireServerAsync();

        string[] claimed = [.. (await JsonAsync(Ogc + "/conformance")).GetProperty("conformsTo").EnumerateArray().Select(c => c.GetString()!)];

        Assert.True(
            claimed.All(c => Proven.Contains(c, StringComparer.Ordinal)),
            "OGC API Styles claims classes nothing here proves: " + string.Join(", ", claimed.Except(Proven)));
        Assert.True(
            Proven.All(p => claimed.Contains(p, StringComparer.Ordinal)),
            "This file proves classes the server does not claim: " + string.Join(", ", Proven.Except(claimed)));

        JsonElement landing = await JsonAsync(Ogc);

        // core: /req/core/base-resource-link.
        string styles = Link(landing, RelStyles) ?? string.Empty;
        Assert.EndsWith(Ogc + "/styles", styles, StringComparison.Ordinal);

        // common landing-page and oas30: the definition is OpenAPI 3.0, served as such.
        (HttpStatusCode status, string body, string? media) = await FetchAsync(Link(landing, "service-desc")!);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("application/vnd.oai.openapi+json", media);
        Assert.StartsWith("3.0.", JsonDocument.Parse(body).RootElement.GetProperty("openapi").GetString(), StringComparison.Ordinal);
        Assert.True(
            JsonDocument.Parse(body).RootElement.GetProperty("paths").TryGetProperty("/styles/{styleId}", out _),
            "The API definition does not describe /styles/{styleId}.");

        // html, in both classes: every JSON resource has an HTML form, by f and by Accept.
        string id = await KnownStyleIdAsync();

        foreach (string path in (string[])[Ogc, Ogc + "/conformance", Ogc + "/api", Ogc + "/styles", $"{Ogc}/styles/{id}/metadata"])
        {
            (HttpStatusCode html, string page, string? type) = await FetchAsync(path + (path.Contains('?') ? "&" : "?") + "f=html");
            Assert.True(html == HttpStatusCode.OK, $"{path}?f=html answered {(int)html}.");
            Assert.Equal("text/html", type);
            Assert.Contains("<html", page, StringComparison.OrdinalIgnoreCase);

            (HttpStatusCode accepted, _, string? negotiated) = await FetchAsync(path, "text/html");
            Assert.True(accepted == HttpStatusCode.OK && negotiated == "text/html", $"{path} with Accept: text/html answered {(int)accepted} {negotiated}.");
        }
    }

    /// <summary>
    /// Draft 1's <c>/conf/core/1</c>: the list names a known service's symbology style; ids are unique; every style
    /// has a typed <c>stylesheet</c> link at <c>/styles/{id}</c>, and fetching it with that type as Accept answers
    /// 200 in that type; every style has a <c>describedby</c> link at <c>/styles/{id}/metadata</c> that answers JSON
    /// with an <c>id</c>.
    /// </summary>
    [Fact]
    public async Task The_list_names_a_known_style_and_every_link_in_it_answers_in_its_type()
    {
        string known = await KnownStyleIdAsync();
        JsonElement[] styles = [.. (await JsonAsync(Ogc + "/styles")).GetProperty("styles").EnumerateArray()];

        Assert.Contains(styles, s => s.GetProperty("id").GetString() == known);
        Assert.Equal(styles.Length, styles.Select(s => s.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());

        JsonElement style = styles.First(s => s.GetProperty("id").GetString() == known);
        string[] types = [.. Links(style, "stylesheet").Select(l => l.Type)];
        Assert.Equal([Mapbox, Sld11, Sld10], types);

        foreach ((string href, string type) in Links(style, "stylesheet"))
        {
            Assert.Equal($"{Ogc}/styles/{known}", new Uri(href).AbsolutePath);

            // Step 7: the type of the link, as Accept, with the f the link carries taken off.
            string bare = href[..href.IndexOf('?', StringComparison.Ordinal)];
            (HttpStatusCode status, string body, string? media, string? version) = await FetchWithVersionAsync(bare, type);
            Assert.True(status == HttpStatusCode.OK, $"{bare} with Accept: {type} answered {(int)status}: {body}");
            Assert.Equal(type.Split(';')[0], media);

            if (type.Contains("version=", StringComparison.Ordinal))
            {
                Assert.Equal(type.Split("version=")[1], version);
            }

            // And by the link itself.
            (HttpStatusCode viaF, _, string? fMedia) = await FetchAsync(href);
            Assert.Equal(HttpStatusCode.OK, viaF);
            Assert.Equal(type.Split(';')[0], fMedia);
        }

        (string metadataHref, string metadataType) = Links(style, "describedby").Single();
        Assert.Equal($"{Ogc}/styles/{known}/metadata", new Uri(metadataHref).AbsolutePath);
        (HttpStatusCode found, string metadata, string? metadataMedia) = await FetchAsync(metadataHref, metadataType);
        Assert.Equal(HttpStatusCode.OK, found);
        Assert.Equal("application/json", metadataMedia);

        JsonElement described = JsonDocument.Parse(metadata).RootElement;
        Assert.Equal(known, described.GetProperty("id").GetString());
        Assert.Equal(3, described.GetProperty("stylesheets").GetArrayLength());
        Assert.True(described.GetProperty("layers").GetArrayLength() > 0, "The metadata names no layer.");
    }

    /// <summary>
    /// The symbology style's SLD 1.1 and 1.0 are well-formed SLD of the right version, a NamedLayer a layer of the
    /// service, named as the Mapbox stylesheet names its source layers; the Mapbox stylesheet is a version 8 style
    /// whose source is the service's VectorTileServer, absolute, and answers.
    /// </summary>
    [Fact]
    public async Task Both_encodings_of_the_symbology_style_parse_and_name_the_same_layers()
    {
        string known = await KnownStyleIdAsync();

        (HttpStatusCode status, string body, string? media) = await FetchAsync($"{Ogc}/styles/{known}?f=mapbox");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(Mapbox, media);

        JsonElement mapbox = JsonDocument.Parse(body).RootElement;
        Assert.Equal(8, mapbox.GetProperty("version").GetInt32());

        string[] sourceLayers = [.. mapbox.GetProperty("layers").EnumerateArray()
            .Where(l => l.TryGetProperty("source-layer", out _))
            .Select(l => l.GetProperty("source-layer").GetString()!)
            .Distinct(StringComparer.Ordinal)];
        Assert.NotEmpty(sourceLayers);

        // Absolute, so a client that fetched the style from here finds the tiles; and the address answers.
        foreach (JsonProperty source in mapbox.GetProperty("sources").EnumerateObject())
        {
            string url = source.Value.GetProperty("url").GetString()!;
            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.Contains("/VectorTileServer", url, StringComparison.Ordinal);
            (HttpStatusCode tiles, string service, _) = await FetchAsync(url + "?f=json");
            Assert.True(tiles == HttpStatusCode.OK, $"The style's source {url} answered {(int)tiles}: {service}");
        }

        foreach ((string f, string version) in new[] { ("sld11", "1.1.0"), ("sld10", "1.0.0") })
        {
            (HttpStatusCode sldStatus, string sld, string? sldMedia) = await FetchAsync($"{Ogc}/styles/{known}?f={f}");
            Assert.Equal(HttpStatusCode.OK, sldStatus);
            Assert.Equal("application/vnd.ogc.sld+xml", sldMedia);

            XDocument document = XDocument.Parse(sld);
            Assert.Equal("StyledLayerDescriptor", document.Root!.Name.LocalName);
            Assert.Equal("http://www.opengis.net/sld", document.Root.Name.NamespaceName);
            Assert.Equal(version, document.Root.Attribute("version")?.Value);

            string[] named = [.. document.Root.Elements().Where(e => e.Name.LocalName == "NamedLayer")
                .Select(e => e.Elements().First(n => n.Name.LocalName == "Name").Value)];
            Assert.Equal(sourceLayers.OrderBy(n => n, StringComparer.Ordinal), named.Distinct().OrderBy(n => n, StringComparer.Ordinal));

            // /rec/sld-se/style-names B: a UserStyle is named with the style's id.
            Assert.All(
                document.Descendants().Where(e => e.Name.LocalName == "UserStyle"),
                s => Assert.Equal(known, s.Elements().First(n => n.Name.LocalName == "Name").Value));
        }
    }

    /// <summary>
    /// A style stored on a service is listed under <c>{service}.{name}</c>, served as Mapbox with its relative source
    /// made absolute, refused as SLD with a 406 problem naming the symbology style, and gone when it is removed.
    /// </summary>
    [Fact]
    public async Task A_stored_style_is_listed_served_as_mapbox_and_refused_as_sld()
    {
        const string Service = "zz_styles_stored";

        await RequireServerAsync();

        try
        {
            (int published, string said) = await PublishOneAsync(Service, Service, sharing: "private", skip: 0);
            Assert.True(published is 200 or 201, $"publishing {Service}: {published} {said}");

            string style = $$$"""
                {"version":8,"name":"Dark","sources":{"esri":{"type":"vector","url":"../../"}},
                 "layers":[{"id":"a","type":"fill","source":"esri","source-layer":"{{{Service}}}","paint":{"fill-color":"#222"}}]}
                """;

            (int stored, string answer) = await AdminAsync(HttpMethod.Put, $"/admin/services/{Service}/styles/dark{FolderQuery(Service)}", style);
            Assert.True(stored == 200, $"storing the style: {stored} {answer}");

            string id = Service + ".dark";
            Assert.True(await ListedAsync(id), $"`{id}` was stored and never appeared in {Ogc}/styles.");

            JsonElement listed = (await JsonAsync(Ogc + "/styles")).GetProperty("styles").EnumerateArray()
                .First(s => s.GetProperty("id").GetString() == id);
            Assert.Equal([Mapbox], Links(listed, "stylesheet").Select(l => l.Type));

            (HttpStatusCode status, string body, string? media) = await FetchAsync($"{Ogc}/styles/{id}");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(Mapbox, media);

            JsonElement served = JsonDocument.Parse(body).RootElement;
            Assert.Equal("Dark", served.GetProperty("name").GetString());
            Assert.EndsWith(
                $"/rest/services/{Service}/VectorTileServer/",
                served.GetProperty("sources").GetProperty("esri").GetProperty("url").GetString(),
                StringComparison.Ordinal);

            (HttpStatusCode refused, string problem, string? problemType) = await FetchAsync($"{Ogc}/styles/{id}?f=sld11");
            Assert.Equal(HttpStatusCode.NotAcceptable, refused);
            Assert.Equal("application/problem+json", problemType);
            Assert.Contains($"{Service}._symbology", problem, StringComparison.Ordinal);

            (HttpStatusCode byAccept, _, _) = await FetchAsync($"{Ogc}/styles/{id}", Sld10);
            Assert.Equal(HttpStatusCode.NotAcceptable, byAccept);

            Assert.Equal(200, (await AdminAsync(HttpMethod.Delete, $"/admin/services/{Service}/styles/dark{FolderQuery(Service)}")).Status);
            (HttpStatusCode gone, _, _) = await FetchAsync($"{Ogc}/styles/{id}");
            Assert.Equal(HttpStatusCode.NotFound, gone);
        }
        finally
        {
            await AdminAsync(HttpMethod.Delete, $"/admin/services/{Service}/styles/dark{FolderQuery(Service)}");
            await UnpublishAsync(Service, Service);
        }
    }

    /// <summary>
    /// A private service's styles are not listed for an anonymous caller, and each of their addresses answers 404 —
    /// while the suite's account, which may read the service, sees them.
    /// </summary>
    [Fact]
    public async Task A_private_service_s_styles_are_absent_for_an_anonymous_caller()
    {
        const string Private = "zz_styles_private";
        string id = Private + "._symbology";

        await RequireServerAsync();

        try
        {
            (int published, string said) = await PublishOneAsync(Private, Private, sharing: "private", skip: 1);
            Assert.True(published is 200 or 201, $"publishing {Private}: {published} {said}");

            Assert.True(await ListedAsync(id), $"`{Private}` was published and the suite's account never saw `{id}`.");

            (HttpStatusCode listed, string styles) = await AnonymousAsync(Ogc + "/styles");
            Assert.Equal(HttpStatusCode.OK, listed);
            Assert.DoesNotContain(Private, styles, StringComparison.Ordinal);

            foreach (string path in (string[])[$"{Ogc}/styles/{id}", $"{Ogc}/styles/{id}/metadata"])
            {
                (HttpStatusCode status, string body) = await AnonymousAsync(path);
                Assert.True(status == HttpStatusCode.NotFound, $"An anonymous {path} answered {(int)status}: {body}");
            }

            (HttpStatusCode mine, _, _) = await FetchAsync($"{Ogc}/styles/{id}?f=sld11");
            Assert.Equal(HttpStatusCode.OK, mine);
        }
        finally
        {
            await UnpublishAsync(Private, Private);
        }
    }

    /// <summary>An unknown style, and an id that cannot be one, are 404 problems; an f nobody defines is a 400.</summary>
    [Fact]
    public async Task An_unknown_style_is_a_404_problem()
    {
        string known = await KnownStyleIdAsync();
        string service = known[..known.LastIndexOf('.')];

        foreach (string path in (string[])
        [
            $"{Ogc}/styles/{service}.nosuch",
            $"{Ogc}/styles/{service}.nosuch/metadata",
            $"{Ogc}/styles/zz_no_such_service._symbology",
            $"{Ogc}/styles/nodot",
            $"{Ogc}/styles/{service}.root",
        ])
        {
            (HttpStatusCode status, string body, string? media) = await FetchAsync(path);
            Assert.True(status == HttpStatusCode.NotFound, $"{path} answered {(int)status}: {body}");
            Assert.Equal("application/problem+json", media);

            JsonElement problem = JsonDocument.Parse(body).RootElement;
            Assert.Equal(404, problem.GetProperty("status").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        }

        (HttpStatusCode bad, _, string? badMedia) = await FetchAsync($"{Ogc}/styles/{known}?f=cscss");
        Assert.Equal(HttpStatusCode.BadRequest, bad);
        Assert.Equal("application/problem+json", badMedia);
    }

    // ---------- helpers ----------

    /// <summary>The fixture's tile service's symbology style: its tile faces' id and <c>._symbology</c>.</summary>
    private async Task<string> KnownStyleIdAsync()
    {
        await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(string.IsNullOrWhiteSpace(configured), $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        return configured!.Trim('/').Replace('/', '.') + "._symbology";
    }

    /// <summary>Whether a style id appears in the list — asked a few times, since the listing may be remembered briefly.</summary>
    private async Task<bool> ListedAsync(string id)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            if ((await JsonAsync(Ogc + "/styles")).GetProperty("styles").EnumerateArray().Any(s => s.GetProperty("id").GetString() == id))
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private async Task<JsonElement> JsonAsync(string path)
    {
        (HttpStatusCode status, string body, string? media) = await FetchAsync(path, "application/json");

        Assert.True(status == HttpStatusCode.OK, $"{path} answered {(int)status}: {body}");
        Assert.Equal("application/json", media);

        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<(HttpStatusCode Status, string Body, string? Media)> FetchAsync(string pathOrUrl, string? accept = null)
    {
        (HttpStatusCode status, string body, string? media, _) = await FetchWithVersionAsync(pathOrUrl, accept);
        return (status, body, media);
    }

    private async Task<(HttpStatusCode Status, string Body, string? Media, string? Version)> FetchWithVersionAsync(
        string pathOrUrl, string? accept)
    {
        string root = await RequireServerAsync();
        Uri uri = pathOrUrl.StartsWith("http", StringComparison.Ordinal) ? new Uri(pathOrUrl) : new Uri(root + pathOrUrl);

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        await AuthenticateAsync(request, root);

        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);

        return (
            response.StatusCode,
            Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync()),
            response.Content.Headers.ContentType?.MediaType,
            response.Content.Headers.ContentType?.Parameters.FirstOrDefault(p => p.Name == "version")?.Value);
    }

    private static string? Link(JsonElement document, string rel) =>
        document.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == rel)
            .Select(l => l.GetProperty("href").GetString())
            .FirstOrDefault();

    private static (string Href, string Type)[] Links(JsonElement document, string rel) =>
        [.. document.GetProperty("links").EnumerateArray()
            .Where(l => l.GetProperty("rel").GetString() == rel)
            .Select(l => (l.GetProperty("href").GetString()!, l.GetProperty("type").GetString()!))];
}
