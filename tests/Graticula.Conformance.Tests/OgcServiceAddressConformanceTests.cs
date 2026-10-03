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
}
