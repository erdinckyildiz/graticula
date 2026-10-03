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
/// ADR-151: NDVI, BandArithmetic, ExtractBand and <c>bandIds</c> over HTTP, as ArcGIS clients ask for them, on a
/// four-band image written here — blue 10, green 20, red 30, and a near infrared of 30 on the west half and 90 on the
/// east, so NDVI is 0 in the west and 0.5 in the east.
/// </summary>
[Collection("catalogue walk")]
public sealed class BandFunctionConformanceTests : ArcGisClient
{
    private const int Side = 16;
    private const double Cell = 0.01;

    // A cell's centre in the east half and in the west half.
    private const string East = "30.125,40.955";
    private const string West = "30.025,40.955";

    private static byte[] FourBands()
    {
        using MemoryStream file = new();
        using BinaryWriter w = new(file);

        float[] values = new float[Side * Side * 4];

        for (int row = 0; row < Side; row++)
        {
            for (int column = 0; column < Side; column++)
            {
                int at = ((row * Side) + column) * 4;
                values[at] = 10;
                values[at + 1] = 20;
                values[at + 2] = 30;
                values[at + 3] = column < Side / 2 ? 30 : 90;
            }
        }

        byte[] image = new byte[values.Length * 4];
        Buffer.BlockCopy(values, 0, image, 0, image.Length);

        int imageAt = 8, scaleAt = imageAt + image.Length, tieAt = scaleAt + 24, geoAt = tieAt + 48, bitsAt = geoAt + 32,
            formatAt = bitsAt + 8, ifdAt = formatAt + 8;

        w.Write("II"u8.ToArray());
        w.Write((ushort)42);
        w.Write(ifdAt);
        w.Write(image);
        foreach (double d in new[] { Cell, Cell, 0.0 }) w.Write(d);
        foreach (double d in new[] { 0.0, 0.0, 0.0, 30.0, 41.0, 0.0 }) w.Write(d);
        foreach (ushort k in new ushort[] { 1, 1, 0, 3, 1024, 0, 1, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326 }) w.Write(k);
        foreach (ushort b in new ushort[] { 32, 32, 32, 32 }) w.Write(b);
        foreach (ushort f in new ushort[] { 3, 3, 3, 3 }) w.Write(f);

        (ushort Tag, ushort Type, int Count, int Value)[] tags =
        [
            (256, 3, 1, Side), (257, 3, 1, Side), (258, 3, 4, bitsAt), (259, 3, 1, 1), (262, 3, 1, 1), (273, 4, 1, imageAt),
            (277, 3, 1, 4), (278, 3, 1, Side), (279, 4, 1, image.Length), (284, 3, 1, 1), (339, 3, 4, formatAt),
            (33550, 12, 3, scaleAt), (33922, 12, 6, tieAt), (34735, 3, 16, geoAt),
        ];

        w.Write((ushort)tags.Length);
        foreach ((ushort tag, ushort type, int count, int value) in tags)
        {
            w.Write(tag);
            w.Write(type);
            w.Write(count);

            if (type == 3 && count == 1)
            {
                w.Write((ushort)value);
                w.Write((ushort)0);
            }
            else
            {
                w.Write(value);
            }
        }

        w.Write(0);
        return file.ToArray();
    }

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Rule(string json) => Uri.EscapeDataString(json);

    private async Task<string> IdentifyAsync(string root, string token, string service, string at, string extra)
    {
        (HttpStatusCode status, string body) = await SendAsync(root, token, HttpMethod.Get,
            $"{service}/identify?geometry={at}&geometryType=esriGeometryPoint&{extra}&f=json");
        Assert.True(status == HttpStatusCode.OK, body);
        JsonElement said = JsonDocument.Parse(body).RootElement;
        Assert.False(said.TryGetProperty("error", out _), body);
        return said.GetProperty("value").GetString()!;
    }

    [Fact]
    public async Task Ndvi_band_arithmetic_and_a_choice_of_bands_answer_as_ArcGIS_clients_ask()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_band_{Guid.NewGuid():N}"[..16];
        string service = $"/rest/services/hosted/{name}/ImageServer";

        using (MultipartFormDataContent form = new())
        {
            form.Add(new ByteArrayContent(FourBands()), "file", "four.tif");
            form.Add(new StringContent(name), "name");
            (HttpStatusCode made, string madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/coverages/upload", form);
            Assert.True(made == HttpStatusCode.Created, madeBody);
        }

        try
        {
            string ndvi = Rule("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":3,"Scientific":true}}""");
            Assert.Equal("0.5", await IdentifyAsync(root, token!, service, East, $"renderingRule={ndvi}"));
            Assert.Equal("0", await IdentifyAsync(root, token!, service, West, $"renderingRule={ndvi}"));

            // ArcGIS's default NDVI is 0 to 200.
            string plain = Rule("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":3}}""");
            Assert.Equal("150", await IdentifyAsync(root, token!, service, East, $"renderingRule={plain}"));

            string arithmetic = Rule("""{"rasterFunction":"BandArithmetic","rasterFunctionArguments":{"Method":0,"BandIndexes":"B4 - B3 + B1"}}""");
            Assert.Equal("70", await IdentifyAsync(root, token!, service, East, $"renderingRule={arithmetic}"));

            Assert.Equal("90 30", await IdentifyAsync(root, token!, service, East, "bandIds=3,2"));

            // The statistics of an area through NDVI are NDVI's.
            string area = Uri.EscapeDataString("""{"xmin":30.0,"ymin":40.84,"xmax":30.16,"ymax":41.0,"spatialReference":{"wkid":4326}}""");
            (HttpStatusCode measured, string statistics) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/computeStatisticsHistograms?geometry={area}&geometryType=esriGeometryEnvelope&renderingRule={ndvi}&f=json");
            Assert.True(measured == HttpStatusCode.OK, statistics);
            JsonElement band = JsonDocument.Parse(statistics).RootElement.GetProperty("statistics")[0];
            Assert.Equal(0, band.GetProperty("min").GetDouble(), 6);
            Assert.Equal(0.5, band.GetProperty("max").GetDouble(), 6);

            // Drawn: NDVI by itself, and a JS SDK renderer's stretch laid over it.
            string over = Rule("""{"rasterFunction":"Stretch","rasterFunctionArguments":{"StretchType":5,"Raster":{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":3}}}}""");
            foreach (string drawn in new[] { ndvi, over, arithmetic })
            {
                using HttpRequestMessage picture = new(HttpMethod.Get,
                    $"{root}{service}/exportImage?bbox=30,40.84,30.16,41&bboxSR=4326&size=32,32&format=png&renderingRule={drawn}&f=image");
                picture.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage answered = await Http.SendAsync(picture);
                Assert.Equal("image/png", answered.Content.Headers.ContentType?.MediaType);
            }

            // The functions are listed where clients look for them.
            JsonElement info = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body).RootElement;
            string[] listed = [.. info.GetProperty("rasterFunctionInfos").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];
            Assert.Contains("NDVI", listed);
            Assert.Contains("BandArithmetic", listed);
            Assert.Contains("ExtractBand", listed);

            // What the image does not have is refused, counting as ArcGIS counts.
            string missing = Rule("""{"rasterFunction":"NDVI","rasterFunctionArguments":{"VisibleBandID":2,"InfraredBandID":4}}""");
            (_, string refused) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry={East}&geometryType=esriGeometryPoint&renderingRule={missing}&f=json");
            Assert.Contains("0 to 3", refused, StringComparison.Ordinal);
            (_, string band5) = await SendAsync(root, token!, HttpMethod.Get,
                $"{service}/identify?geometry={East}&geometryType=esriGeometryPoint&bandIds=5&f=json");
            Assert.Contains("0 to 3", band5, StringComparison.Ordinal);

            // Its owner shows it through NDVI by default, as Studio stores it.
            (HttpStatusCode styled, string styledBody) = await SendAsync(root, token!, HttpMethod.Put,
                $"/admin/coverages/{name}/style?folder=hosted",
                new StringContent("""{"stretch":"auto","function":"ndvi:2:3"}""", Encoding.UTF8, "application/json"));
            Assert.True(styled == HttpStatusCode.OK, styledBody);
            JsonElement shown = JsonDocument.Parse((await SendAsync(root, token!, HttpMethod.Get, $"{service}?f=json")).Body).RootElement;
            Assert.Equal("NDVI", shown.GetProperty("defaultRasterFunction").GetString());
            Assert.Equal("150", await IdentifyAsync(root, token!, service, East, "renderingRule="));
        }
        finally
        {
            (HttpStatusCode removed, _) = await SendAsync(root, token!, HttpMethod.Delete, $"/admin/coverages/{name}?folder=hosted");
            Assert.Equal(HttpStatusCode.OK, removed);
        }
    }
}
