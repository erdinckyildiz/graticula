using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-157: imagery in JPEG 2000, NetCDF and an ASCII grid is written as GeoTIFF on its way in, a NetCDF of time steps
/// as a dated image a step; MrSID is refused by name. The JPEG 2000 and the NetCDF are in the raster corpus, made by
/// GDAL from an 8 × 8 grid of 0.01° at 30° E, 41° N whose values are ten a step plus the column.
/// </summary>
[Collection("catalogue walk")]
public sealed class ImageryFormatsConformanceTests : ArcGisClient
{
    private static byte[] Corpus(string name)
    {
        DirectoryInfo? at = new(AppContext.BaseDirectory);
        while (at is not null && at.GetFiles("*.sln").Length == 0) at = at.Parent;
        return File.ReadAllBytes(Path.Combine(at!.FullName, "tests", "Graticula.Raster.Tiff.Tests", "corpus", name));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text.Length == 0 ? "{}" : text).RootElement.Clone());
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(string root, string token, string name, string file, byte[] bytes)
    {
        using MultipartFormDataContent form = new();
        form.Add(new ByteArrayContent(bytes), "file", file);
        form.Add(new StringContent(name), "name");
        return await SendAsync(root, token, HttpMethod.Post, "/admin/coverages/upload", form);
    }

    private async Task<string> ValueAsync(string root, string token, string service, string extra = "")
    {
        (_, JsonElement said) = await SendAsync(root, token, HttpMethod.Get,
            $"{service}/identify?geometry=30.025,40.975&geometryType=esriGeometryPoint&f=json{extra}");
        Assert.False(said.TryGetProperty("error", out _), said.ToString());
        return said.GetProperty("value").GetString()!;
    }

    [Fact]
    public async Task Jpeg_2000_netcdf_and_an_ascii_grid_are_published_and_mrsid_is_refused_by_name()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string jp2 = $"zz_jp2_{Guid.NewGuid():N}"[..16], nc = $"zz_nc_{Guid.NewGuid():N}"[..16], asc = $"zz_asc_{Guid.NewGuid():N}"[..16];

        try
        {
            // JPEG 2000: the first step's values, lossless — column 2 holds 12.
            (HttpStatusCode madeJp2, JsonElement saidJp2) = await UploadAsync(root, token!, jp2, "first.jp2", Corpus("first.jp2"));
            Assert.True(madeJp2 == HttpStatusCode.Created, saidJp2.ToString());
            Assert.Equal("12", await ValueAsync(root, token!, $"/rest/services/hosted/{jp2}/ImageServer"));

            // NetCDF: three monthly steps, three dated images; the last on top, and time chooses a step.
            (HttpStatusCode madeNc, JsonElement saidNc) = await UploadAsync(root, token!, nc, "steps.nc", Corpus("steps.nc"));
            Assert.True(madeNc == HttpStatusCode.Created, saidNc.ToString());
            Assert.Equal(3, saidNc.GetProperty("images").GetInt32());
            string ncService = $"/rest/services/hosted/{nc}/ImageServer";
            JsonElement info = (await SendAsync(root, token!, HttpMethod.Get, $"{ncService}?f=json")).Body;
            long january = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            long march = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
            Assert.Equal(january, info.GetProperty("timeInfo").GetProperty("timeExtent")[0].GetInt64());
            Assert.Equal(march, info.GetProperty("timeInfo").GetProperty("timeExtent")[1].GetInt64());
            Assert.Equal("32", await ValueAsync(root, token!, ncService));
            Assert.Equal("12", await ValueAsync(root, token!, ncService, $"&time={january + 3_600_000}"));

            // An ASCII grid names no reference; its longitudes and latitudes are WGS 84.
            string grid = "ncols 4\nnrows 4\nxllcorner 30\nyllcorner 40.96\ncellsize 0.01\nNODATA_value -9999\n"
                + "5 6 7 8\n5 6 7 8\n5 6 7 8\n5 6 7 8\n";
            (HttpStatusCode madeAsc, JsonElement saidAsc) = await UploadAsync(root, token!, asc, "grid.asc", Encoding.ASCII.GetBytes(grid));
            Assert.True(madeAsc == HttpStatusCode.Created, saidAsc.ToString());
            Assert.Equal(4326, saidAsc.GetProperty("srid").GetInt32());
            Assert.Equal("7", await ValueAsync(root, token!, $"/rest/services/hosted/{asc}/ImageServer"));

            // MrSID: refused, naming the SDK, before anything is written.
            (HttpStatusCode sid, JsonElement saidSid) = await UploadAsync(root, token!, "zz_sid_never", "scene.sid", [1, 2, 3]);
            Assert.Equal(HttpStatusCode.BadRequest, sid);
            Assert.Contains("SDK", saidSid.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            foreach (string name in new[] { jp2, nc, asc })
            {
                await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            }
        }
    }
}
