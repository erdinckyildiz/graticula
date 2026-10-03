using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-162: WMS and WFS at a service's own address, as ArcGIS gives them, narrowed to that service; image services as
/// WMS layers, drawn; GetFeatureInfo answering text/xml as XML.
/// </summary>
[Collection("catalogue walk")]
public sealed class OgcServiceAddressConformanceTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string? Type, byte[] Body)> GetAsync(string root, string token, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsByteArrayAsync());
    }

    private static string[] LayerNames(byte[] capabilities) =>
        [.. XDocument.Parse(System.Text.Encoding.UTF8.GetString(capabilities)).Descendants()
            .Where(e => e.Name.LocalName == "Layer" && e.Attribute("queryable") is not null)
            .Select(e => e.Elements().First(n => n.Name.LocalName == "Name").Value)];

    [Fact]
    public async Task An_image_service_is_a_wms_layer_at_its_own_address_and_drawn()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_wms_{Guid.NewGuid():N}"[..16];

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(MosaicCatalogConformanceTests.Constant(100)), "file", "hundred.tif");
            form.Add(new StringContent(name), "name");
            using HttpRequestMessage upload = new(HttpMethod.Post, $"{root}/admin/coverages/upload") { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage made = await Http.SendAsync(upload);
            Assert.True(made.StatusCode == HttpStatusCode.Created, await made.Content.ReadAsStringAsync());
        }

        try
        {
            string own = $"/rest/services/hosted/{name}/ImageServer/WMSServer";
            (HttpStatusCode status, _, byte[] capabilities) = await GetAsync(root, token!, $"{own}?service=WMS&request=GetCapabilities&version=1.3.0");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal([$"hosted/{name}"], LayerNames(capabilities));
            Assert.Contains(own, System.Text.Encoding.UTF8.GetString(capabilities), StringComparison.Ordinal);

            (_, _, byte[] everything) = await GetAsync(root, token!, "/wms?service=WMS&request=GetCapabilities&version=1.3.0");
            Assert.Contains($"hosted/{name}", LayerNames(everything));

            // The image covers 30–30.08° E, 40.92–41° N; 1.3.0's EPSG:4326 is latitude first.
            (_, string? type, byte[] png) = await GetAsync(root, token!,
                $"{own}?service=WMS&request=GetMap&version=1.3.0&layers=hosted/{name}&styles=&crs=EPSG:4326"
                + "&bbox=40.9,29.98,41.02,30.1&width=60&height=60&format=image/png&transparent=true");
            Assert.Equal("image/png", type);
            using SkiaSharp.SKBitmap bitmap = SkiaSharp.SKBitmap.Decode(png);
            Assert.NotNull(bitmap);
            Assert.True(bitmap.GetPixel(30, 30).Alpha > 0, "The middle of the image service drew nothing.");
            Assert.Equal(0, bitmap.GetPixel(1, 1).Alpha);

            (_, string? legendType, _) = await GetAsync(root, token!,
                $"{own}?service=WMS&request=GetLegendGraphic&version=1.3.0&layer=hosted/{name}&format=image/png");
            Assert.Equal("image/png", legendType);

            // ADR-163: WMTS at ArcGIS's address, over the service's own grid — a tile is the ArcGIS tile, byte for byte.
            string wmts = $"/rest/services/hosted/{name}/ImageServer/WMTS";
            (_, _, byte[] wmtsCapabilities) = await GetAsync(root, token!, $"{wmts}?service=WMTS&request=GetCapabilities");
            XDocument document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(wmtsCapabilities));
            Assert.Equal("urn:ogc:def:crs:EPSG::4326", document.Descendants().First(e => e.Name.LocalName == "SupportedCRS").Value);
            Assert.Contains(document.Descendants(), e => e.Name.LocalName == "ResourceURL"
                && e.Attribute("template")!.Value.EndsWith($"{wmts}/tile/1.0.0/{name}/{{Style}}/{{TileMatrixSet}}/{{TileMatrix}}/{{TileRow}}/{{TileCol}}", StringComparison.Ordinal));

            // Level 8 on the WGS 84 grid: 0.703125° tiles, so 30.04° E, 40.96° N is column 298, row 69.
            (_, string? tileType, byte[] tile) = await GetAsync(root, token!, $"{wmts}/tile/1.0.0/{name}/default/default028mm/8/69/298");
            (_, _, byte[] kvp) = await GetAsync(root, token!,
                $"{wmts}?service=WMTS&request=GetTile&version=1.0.0&layer={name}&style=default&tilematrixset=default028mm&tilematrix=8&tilerow=69&tilecol=298&format=image/png");
            (_, _, byte[] arcgis) = await GetAsync(root, token!, $"/rest/services/hosted/{name}/ImageServer/tile/8/69/298");
            Assert.Equal("image/png", tileType);
            Assert.Equal(arcgis, tile);
            Assert.Equal(arcgis, kvp);

            // ADR-164: generateKml is a KMZ whose overlay Google Earth redraws from the service's own WMS address.
            (_, string? kmzType, byte[] kmz) = await GetAsync(root, token!, $"/rest/services/hosted/{name}/ImageServer/generateKml");
            Assert.Equal("application/vnd.google-earth.kmz", kmzType);
            using System.IO.Compression.ZipArchive archive = new(new System.IO.MemoryStream(kmz));
            using System.IO.StreamReader doc = new(archive.GetEntry("doc.kml")!.Open());
            XDocument kml = XDocument.Parse(await doc.ReadToEndAsync());
            string href = kml.Descendants().First(e => e.Name.LocalName == "href").Value;
            Assert.Contains($"/rest/services/hosted/{name}/ImageServer/WMSServer?", href, StringComparison.Ordinal);
            Assert.Equal("onStop", kml.Descendants().First(e => e.Name.LocalName == "viewRefreshMode").Value);
            (_, string? earthType, _) = await GetAsync(root, token!, href[href.IndexOf("/rest/", StringComparison.Ordinal)..]
                + "&BBOX=29.9,40.9,30.2,41.1&WIDTH=64&HEIGHT=64");
            Assert.Equal("image/png", earthType);

            // ADR-166: its owner turns WMS, WMTS and KML off one by one; the ArcGIS face stays; on again, all answer.
            async Task<HttpStatusCode> OffAsync(string json)
            {
                using HttpRequestMessage put = new(HttpMethod.Put, $"{root}/admin/services/{name}/ogc?folder=hosted")
                {
                    Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
                };
                put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answered = await Http.SendAsync(put);
                return answered.StatusCode;
            }

            Assert.Equal(HttpStatusCode.OK, await OffAsync("""{"off":["WMS","WMTS","KML"]}"""));
            (_, _, byte[] without) = await GetAsync(root, token!, "/wms?service=WMS&request=GetCapabilities&version=1.3.0");
            Assert.DoesNotContain($"hosted/{name}", LayerNames(without));
            (HttpStatusCode wmtsOff, _, _) = await GetAsync(root, token!, $"{wmts}/tile/1.0.0/{name}/default/default028mm/8/69/298");
            Assert.Equal(HttpStatusCode.NotFound, wmtsOff);
            (_, _, byte[] kmlOff) = await GetAsync(root, token!, $"/rest/services/hosted/{name}/ImageServer/generateKml");
            Assert.Contains("turned KML off", System.Text.Encoding.UTF8.GetString(kmlOff), StringComparison.Ordinal);
            (_, string? arcgisType, _) = await GetAsync(root, token!, $"/rest/services/hosted/{name}/ImageServer/tile/8/69/298");
            Assert.Equal("image/png", arcgisType);
            Assert.Equal(HttpStatusCode.BadRequest, await OffAsync("""{"off":["FTP"]}"""));
            Assert.Equal(HttpStatusCode.OK, await OffAsync("""{"off":[]}"""));
            (_, _, byte[] back) = await GetAsync(root, token!, "/wms?service=WMS&request=GetCapabilities&version=1.3.0");
            Assert.Contains($"hosted/{name}", LayerNames(back));

            // ADR-167: the fees and access constraints its owner states are in its own WMS document, and nowhere else.
            using (HttpRequestMessage terms = new(HttpMethod.Put, $"{root}/admin/services/{name}/ogc/terms?folder=hosted")
            {
                Content = new StringContent("""{"fees":"No charge","accessConstraints":"CC BY 4.0"}""", System.Text.Encoding.UTF8, "application/json"),
            })
            {
                terms.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage said = await Http.SendAsync(terms);
                Assert.Equal(HttpStatusCode.OK, said.StatusCode);
            }

            (_, _, byte[] stated) = await GetAsync(root, token!, $"{own}?service=WMS&request=GetCapabilities&version=1.3.0");
            XElement service = XDocument.Parse(System.Text.Encoding.UTF8.GetString(stated)).Descendants().First(e => e.Name.LocalName == "Service");
            Assert.Equal("No charge", service.Elements().First(e => e.Name.LocalName == "Fees").Value);
            Assert.Equal("CC BY 4.0", service.Elements().First(e => e.Name.LocalName == "AccessConstraints").Value);
            (_, _, byte[] server) = await GetAsync(root, token!, "/wms?service=WMS&request=GetCapabilities&version=1.3.0");
            Assert.DoesNotContain("CC BY 4.0", System.Text.Encoding.UTF8.GetString(server), StringComparison.Ordinal);
        }
        finally
        {
            using HttpRequestMessage remove = new(HttpMethod.Delete, $"{root}/admin/coverages/{name}?folder=hosted");
            remove.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage _ = await Http.SendAsync(remove);
        }
    }

    [Fact]
    public async Task A_feature_service_s_own_wfs_and_wms_list_its_layers_only()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (_, _, byte[] listing) = await GetAsync(root, token!, "/rest/services/hosted?f=json");
        string? service = JsonDocument.Parse(listing).RootElement.GetProperty("services").EnumerateArray()
            .Where(s => s.GetProperty("type").GetString() == "FeatureServer")
            .Select(s => s.GetProperty("name").GetString())
            .FirstOrDefault();
        Assert.False(service is null, "No hosted feature service to look at.");

        (_, _, byte[] all) = await GetAsync(root, token!, "/wfs?service=WFS&request=GetCapabilities&version=2.0.0");
        (HttpStatusCode status, _, byte[] own) = await GetAsync(root, token!,
            $"/rest/services/{service}/FeatureServer/WFSServer?service=WFS&request=GetCapabilities&version=2.0.0");
        Assert.Equal(HttpStatusCode.OK, status);

        static string[] Types(byte[] document) => [.. XDocument.Parse(System.Text.Encoding.UTF8.GetString(document)).Descendants()
            .Where(e => e.Name.LocalName == "FeatureType").Select(e => e.Elements().First(n => n.Name.LocalName == "Name").Value)];

        string[] mine = Types(own), every = Types(all);
        Assert.NotEmpty(mine);
        Assert.True(mine.Length < every.Length, $"{service}'s own WFS lists {mine.Length} types, as many as the server's {every.Length}.");
        Assert.All(mine, t => Assert.Contains(t, every));
        Assert.Contains($"/rest/services/{service}/FeatureServer/WFSServer", System.Text.Encoding.UTF8.GetString(own), StringComparison.Ordinal);

        (_, _, byte[] wms) = await GetAsync(root, token!, $"/rest/services/{service}/MapServer/WMSServer?service=WMS&request=GetCapabilities&version=1.3.0");
        Assert.NotEmpty(LayerNames(wms));
        Assert.True(LayerNames(wms).Length <= mine.Length);
    }

    [Fact]
    public async Task A_service_s_inspire_settings_make_its_own_wms_a_view_service_and_its_wfs_a_download_service()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (_, _, byte[] listing) = await GetAsync(root, token!, "/rest/services/hosted?f=json");
        string? service = JsonDocument.Parse(listing).RootElement.GetProperty("services").EnumerateArray()
            .Where(s => s.GetProperty("type").GetString() == "FeatureServer")
            .Select(s => s.GetProperty("name").GetString())
            .FirstOrDefault();
        Assert.False(service is null, "No hosted feature service to look at.");
        string name = service![(service.IndexOf('/', StringComparison.Ordinal) + 1)..];

        async Task<HttpStatusCode> SetAsync(string json)
        {
            using HttpRequestMessage put = new(HttpMethod.Put, $"{root}/admin/services/{name}/ogc/inspire?folder=hosted")
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
            };
            put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage answered = await Http.SendAsync(put);
            return answered.StatusCode;
        }

        const string Vs = "http://inspire.ec.europa.eu/schemas/inspire_vs/1.0", Dls = "http://inspire.ec.europa.eu/schemas/inspire_dls/1.0";
        const string Common = "http://inspire.ec.europa.eu/schemas/common/1.0";
        const string Record = "https://catalogue.example/csw?service=CSW&request=GetRecordById&id=abc";
        string wms = $"/rest/services/{service}/MapServer/WMSServer?service=WMS&request=GetCapabilities&version=1.3.0";
        string wfs = $"/rest/services/{service}/FeatureServer/WFSServer?service=WFS&request=GetCapabilities&version=2.0.0";

        try
        {
            Assert.Equal(HttpStatusCode.OK, await SetAsync(
                $$"""{"metadataUrl":"{{Record}}","language":"tur","datasetCode":"TR.ROADS.1","datasetNamespace":"https://data.example/id"}"""));

            // Its own WMS is a View service, scenario 1: the record and the language.
            (_, _, byte[] view) = await GetAsync(root, token!, wms);
            XElement vs = XDocument.Parse(System.Text.Encoding.UTF8.GetString(view)).Descendants(XName.Get("ExtendedCapabilities", Vs)).Single();
            Assert.Equal(Record, vs.Descendants(XName.Get("URL", Common)).Single().Value);
            Assert.All(vs.Descendants(XName.Get("Language", Common)), l => Assert.Equal("tur", l.Value));

            // Its own WFS is a Download service naming its data set.
            (_, _, byte[] download) = await GetAsync(root, token!, wfs);
            XElement dls = XDocument.Parse(System.Text.Encoding.UTF8.GetString(download)).Descendants(XName.Get("ExtendedCapabilities", Dls)).Single();
            Assert.Equal("TR.ROADS.1", dls.Descendants(XName.Get("Code", Common)).Single().Value);
            Assert.Equal("ExtendedCapabilities", dls.Parent!.Name.LocalName);
            Assert.Equal("OperationsMetadata", dls.Parent.Parent!.Name.LocalName);

            // The server's own documents are not this service's, and say nothing of it.
            (_, _, byte[] server) = await GetAsync(root, token!, "/wms?service=WMS&request=GetCapabilities&version=1.3.0");
            Assert.DoesNotContain(Vs, System.Text.Encoding.UTF8.GetString(server), StringComparison.Ordinal);

            // Without a data set it is a View service only; a bad language and a namespace without a code are refused.
            Assert.Equal(HttpStatusCode.OK, await SetAsync($$"""{"metadataUrl":"{{Record}}","language":"eng"}"""));
            (_, _, byte[] viewOnly) = await GetAsync(root, token!, wfs);
            Assert.DoesNotContain(Dls, System.Text.Encoding.UTF8.GetString(viewOnly), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest, await SetAsync($$"""{"metadataUrl":"{{Record}}","language":"english"}"""));
            Assert.Equal(HttpStatusCode.BadRequest, await SetAsync($$"""{"metadataUrl":"{{Record}}","datasetNamespace":"x"}"""));
            Assert.Equal(HttpStatusCode.BadRequest, await SetAsync("""{"metadataUrl":"not an address"}"""));
            Assert.Equal(HttpStatusCode.BadRequest, await SetAsync($$"""{"metadataUrl":"{{Record}}","language":"xyz"}"""));

            // A 639-2/T code is INSPIRE's /B one; a language with no record is no setting at all, as the form sends it.
            Assert.Equal(HttpStatusCode.OK, await SetAsync($$"""{"metadataUrl":"{{Record}}","language":"deu"}"""));
            (_, _, byte[] german) = await GetAsync(root, token!, wms);
            Assert.Contains("<inspire_common:Language>ger</inspire_common:Language>", System.Text.Encoding.UTF8.GetString(german), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.OK, await SetAsync("""{"metadataUrl":"","language":"eng","datasetCode":"","datasetNamespace":""}"""));
        }
        finally
        {
            Assert.Equal(HttpStatusCode.OK, await SetAsync("{}"));
        }

        (_, _, byte[] cleared) = await GetAsync(root, token!, wms);
        Assert.DoesNotContain(Vs, System.Text.Encoding.UTF8.GetString(cleared), StringComparison.Ordinal);
    }
}
