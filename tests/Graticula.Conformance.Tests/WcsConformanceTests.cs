using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Xml.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-170: an image service is a WCS 2.0.1 coverage at <c>/wcs</c> and its own <c>WCSServer</c> — described as a grid,
/// its values answered as GeoTIFF, trimmed, scaled and reprojected, and turned off by its owner.
/// </summary>
[Collection("catalogue walk")]
public sealed class WcsConformanceTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string? Type, byte[] Body)> GetAsync(string root, string token, string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsByteArrayAsync());
    }

    /// <summary>A little-endian TIFF's first image's width and height.</summary>
    private static (int Width, int Height) SizeOf(byte[] tiff)
    {
        Assert.Equal((byte)'I', tiff[0]);
        int ifd = BitConverter.ToInt32(tiff, 4);
        int count = BitConverter.ToUInt16(tiff, ifd);
        int width = 0, height = 0;

        for (int i = 0; i < count; i++)
        {
            int at = ifd + 2 + (i * 12);
            ushort tag = BitConverter.ToUInt16(tiff, at), type = BitConverter.ToUInt16(tiff, at + 2);
            int value = type == 3 ? BitConverter.ToUInt16(tiff, at + 8) : BitConverter.ToInt32(tiff, at + 8);
            (width, height) = tag switch { 256 => (value, height), 257 => (width, value), _ => (width, height) };
        }

        return (width, height);
    }

    private static XDocument Xml(byte[] body) => XDocument.Parse(System.Text.Encoding.UTF8.GetString(body));

    [Fact]
    public async Task An_image_service_is_a_coverage_described_trimmed_scaled_and_answered_as_geotiff()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_wcs_{Guid.NewGuid():N}"[..16];
        string id = $"hosted__{name}";

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
            // The server's document lists it; its own lists only it.
            (HttpStatusCode status, _, byte[] capabilities) = await GetAsync(root, token!, "/wcs?service=WCS&request=GetCapabilities");
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Contains(Xml(capabilities).Descendants(), e => e.Name.LocalName == "CoverageId" && e.Value == id);

            string own = $"/rest/services/hosted/{name}/ImageServer/WCSServer";
            (_, _, byte[] ownCapabilities) = await GetAsync(root, token!, $"{own}?service=WCS&request=GetCapabilities");
            Assert.Equal([id], Xml(ownCapabilities).Descendants().Where(e => e.Name.LocalName == "CoverageId").Select(e => e.Value));

            // The grid: 8 × 8 cells, latitude first in EPSG:4326, from the centre of the top-left cell.
            (_, _, byte[] described) = await GetAsync(root, token!, $"/wcs?service=WCS&version=2.0.1&request=DescribeCoverage&coverageId={id}");
            XDocument description = Xml(described);
            Assert.Equal("7 7", description.Descendants().First(e => e.Name.LocalName == "high").Value);
            Assert.Equal("Lat Long", description.Descendants().First(e => e.Name.LocalName == "Envelope").Attribute("axisLabels")!.Value);
            Assert.Equal("40.995 30.005", description.Descendants().First(e => e.Name.LocalName == "pos").Value);
            Assert.Single(description.Descendants(), e => e.Name.LocalName == "field");

            // The whole coverage at its own resolution, then a quarter of it trimmed, then scaled, then in Web Mercator.
            (_, string? type, byte[] whole) = await GetAsync(root, token!, $"{own}?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}");
            Assert.Equal("image/tiff", type);
            Assert.Equal((8, 8), SizeOf(whole));

            (_, _, byte[] quarter) = await GetAsync(root, token!,
                $"/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}&subset=Lat(40.96,41)&subset=Long(30,30.04)&format=image/tiff");
            Assert.Equal((4, 4), SizeOf(quarter));

            // ADR-173: the same coverage in WCS 1.0.0 — QGIS's Add WCS layer — and the same quarter, byte for byte.
            (_, _, byte[] capabilities10) = await GetAsync(root, token!, "/wcs?service=WCS&version=1.0.0&request=GetCapabilities");
            XDocument old = Xml(capabilities10);
            Assert.Equal("WCS_Capabilities", old.Root!.Name.LocalName);
            Assert.Equal("1.0.0", old.Root.Attribute("version")!.Value);
            Assert.Contains(old.Descendants(), e => e.Name.LocalName == "name" && e.Value == id);

            (_, _, byte[] described10) = await GetAsync(root, token!, $"/wcs?service=WCS&version=1.0.0&request=DescribeCoverage&coverage={id}");
            Assert.Equal("7 7", Xml(described10).Descendants().First(e => e.Name.LocalName == "high").Value);

            (_, string? type10, byte[] quarter10) = await GetAsync(root, token!,
                $"/wcs?service=WCS&version=1.0.0&request=GetCoverage&coverage={id}&crs=EPSG:4326&bbox=30,40.96,30.04,41&width=4&height=4&format=GeoTIFF");
            Assert.Equal("image/tiff", type10);
            Assert.Equal(quarter, quarter10);

            (HttpStatusCode notDefined, _, byte[] notDefinedBody) = await GetAsync(root, token!,
                "/wcs?service=WCS&version=1.0.0&request=GetCoverage&coverage=nothing__here&crs=EPSG:4326&bbox=0,0,1,1&width=1&height=1&format=GeoTIFF");
            Assert.Equal(HttpStatusCode.BadRequest, notDefined);
            Assert.Equal("CoverageNotDefined", Xml(notDefinedBody).Descendants().First(e => e.Name.LocalName == "ServiceException").Attribute("code")!.Value);

            (_, _, byte[] scaled) = await GetAsync(root, token!,
                $"/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}&scalesize=i(16),j(16)");
            Assert.Equal((16, 16), SizeOf(scaled));

            (_, string? mercatorType, byte[] mercator) = await GetAsync(root, token!,
                $"/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}&outputcrs=http://www.opengis.net/def/crs/EPSG/0/3857");
            Assert.Equal("image/tiff", mercatorType);
            Assert.True(mercator.Length > 8, "Web Mercator answered nothing.");

            // Refusals are OWS 2.0 exception reports with WCS's codes.
            (HttpStatusCode missing, _, byte[] noSuch) = await GetAsync(root, token!, "/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId=nothing__here");
            Assert.Equal(HttpStatusCode.NotFound, missing);
            Assert.Equal("NoSuchCoverage", Xml(noSuch).Descendants().First(e => e.Name.LocalName == "Exception").Attribute("exceptionCode")!.Value);
            (HttpStatusCode badAxis, _, byte[] axis) = await GetAsync(root, token!,
                $"/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}&subset=time(1,2)");
            // 404, as WCS 2.0.1's exception table gives for InvalidAxisLabel and InvalidSubsetting — CITE, 2026-10-04.
            Assert.Equal(HttpStatusCode.NotFound, badAxis);
            Assert.Equal("InvalidAxisLabel", Xml(axis).Descendants().First(e => e.Name.LocalName == "Exception").Attribute("exceptionCode")!.Value);

            // At most one subset per axis (requirement 31), and mediaType is multipart/related or absent (29).
            (HttpStatusCode twice, _, byte[] twiceBody) = await GetAsync(root, token!,
                $"/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}&subset=Lat(40.96,40.99)&subset=Lat(40.96,40.99)");
            Assert.Equal(HttpStatusCode.NotFound, twice);
            Assert.Equal("InvalidAxisLabel", Xml(twiceBody).Descendants().First(e => e.Name.LocalName == "Exception").Attribute("exceptionCode")!.Value);
            (HttpStatusCode bogus, _, byte[] bogusBody) = await GetAsync(root, token!,
                $"/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}&mediaType=mediatype_bogus");
            Assert.Equal(HttpStatusCode.BadRequest, bogus);
            Assert.Equal("InvalidParameterValue", Xml(bogusBody).Descendants().First(e => e.Name.LocalName == "Exception").Attribute("exceptionCode")!.Value);

            // ADR-166: its owner turns WCS off; the coverage leaves the document and is refused by id.
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

            Assert.Equal(HttpStatusCode.OK, await OffAsync("""{"off":["WCS"]}"""));
            (_, _, byte[] without) = await GetAsync(root, token!, "/wcs?service=WCS&request=GetCapabilities");
            Assert.DoesNotContain(Xml(without).Descendants(), e => e.Name.LocalName == "CoverageId" && e.Value == id);
            (HttpStatusCode off, _, _) = await GetAsync(root, token!, $"/wcs?service=WCS&version=2.0.1&request=GetCoverage&coverageId={id}");
            Assert.Equal(HttpStatusCode.NotFound, off);
            Assert.Equal(HttpStatusCode.OK, await OffAsync("""{"off":[]}"""));
        }
        finally
        {
            using HttpRequestMessage remove = new(HttpMethod.Delete, $"{root}/admin/coverages/{name}?folder=hosted");
            remove.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage _ = await Http.SendAsync(remove);
        }
    }
}
