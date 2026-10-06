using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// OGC API Maps (ADR-175) and OGC API Processes (ADR-174), as a client meets them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Maps is WMS under another grammar, and the first test says so in bytes.</b> A collection's map with a box and a
/// size is handed to WMS's GetMap; if the two ever drew different pictures for the same box, a layer's symbology, scale
/// range or sharing would have started to differ between the faces, which is the thing ADR-175 promises cannot happen.
/// </para>
/// <para>
/// <b>Processes is the geometry service under another grammar</b>, governed by that service's sharing: the fixture
/// shares it with the organisation, so an anonymous caller sees no process and can run none.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class OgcMapsAndProcessesConformanceTests : ArcGisClient
{
    private const string Maps = "/ogc/maps/v1";
    private const string Processes = "/ogc/processes/v1";

    private async Task<(HttpStatusCode Status, string? Type, HttpResponseMessage Response, byte[] Body)> GetAsync(
        string path, bool signed = false)
    {
        string root = await RequireServerAsync();
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(root + path));

        if (signed)
        {
            await AuthenticateAsync(request, root);
        }

        HttpResponseMessage response = await Http.SendAsync(request);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, response, body);
    }

    private async Task<(HttpStatusCode Status, HttpResponseMessage Response, string Body)> PostAsync(
        string path, string json, bool signed, string? prefer = null)
    {
        string root = await RequireServerAsync();
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(root + path))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (signed)
        {
            await AuthenticateAsync(request, root);
        }

        if (prefer is not null)
        {
            request.Headers.TryAddWithoutValidation("Prefer", prefer);
        }

        HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, response, await response.Content.ReadAsStringAsync());
    }

    /// <summary>A vector collection with an extent, from the anonymous collections document.</summary>
    private async Task<(string Id, double[] Box)> VectorCollectionAsync()
    {
        (HttpStatusCode status, _, _, byte[] body) = await GetAsync(Maps + "/collections");
        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument document = JsonDocument.Parse(body);

        foreach (JsonElement collection in document.RootElement.GetProperty("collections").EnumerateArray())
        {
            string id = collection.GetProperty("id").GetString()!;

            if (!id.Contains("__", StringComparison.Ordinal)
                && collection.TryGetProperty("extent", out JsonElement extent))
            {
                double[] box = [.. extent.GetProperty("spatial").GetProperty("bbox")[0].EnumerateArray().Select(v => v.GetDouble())];
                return (id, box);
            }
        }

        Assert.Fail("No public vector collection with an extent; the fixture publishes several.");
        return default;
    }

    [Fact]
    public async Task AMapIsTheSamePictureWmsDraws()
    {
        (string id, double[] box) = await VectorCollectionAsync();
        string bbox = string.Join(",", box.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));

        (HttpStatusCode status, string? type, HttpResponseMessage response, byte[] map) =
            await GetAsync($"{Maps}/collections/{Uri.EscapeDataString(id)}/map?bbox={bbox}&width=300&height=200");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("image/png", type);
        Assert.Equal("http://www.opengis.net/def/crs/OGC/1.3/CRS84", response.Headers.GetValues("Content-Crs").Single());
        Assert.Equal(4, response.Headers.GetValues("Content-Bbox").Single().Split(',').Length);

        (HttpStatusCode wmsStatus, _, _, byte[] wms) = await GetAsync(
            $"/wms?SERVICE=WMS&VERSION=1.3.0&REQUEST=GetMap&LAYERS={Uri.EscapeDataString(id)}&STYLES=&CRS=CRS:84"
            + $"&BBOX={bbox}&WIDTH=300&HEIGHT=200&FORMAT=image/png&TRANSPARENT=TRUE");

        Assert.Equal(HttpStatusCode.OK, wmsStatus);
        Assert.True(map.AsSpan().SequenceEqual(wms),
            $"{id}: OGC API Maps and WMS drew different pictures for the same box ({map.Length} and {wms.Length} bytes).");
    }

    [Fact]
    public async Task TheMapsDocumentsAreWhatTheyClaim()
    {
        (HttpStatusCode status, _, _, byte[] body) = await GetAsync(Maps + "/conformance");
        Assert.Equal(HttpStatusCode.OK, status);

        using (JsonDocument conformance = JsonDocument.Parse(body))
        {
            string[] classes = [.. conformance.RootElement.GetProperty("conformsTo").EnumerateArray().Select(c => c.GetString()!)];
            Assert.Contains("http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/core", classes);
            Assert.Contains("http://www.opengis.net/spec/ogcapi-maps-1/1.0/conf/collection-map", classes);
        }

        (status, _, _, body) = await GetAsync(Maps);
        Assert.Equal(HttpStatusCode.OK, status);

        using (JsonDocument landing = JsonDocument.Parse(body))
        {
            Assert.True(landing.RootElement.TryGetProperty("extent", out _), "The landing page describes the dataset's extent.");
        }

        (string id, _) = await VectorCollectionAsync();
        (status, _, _, body) = await GetAsync($"{Maps}/collections/{Uri.EscapeDataString(id)}");
        Assert.Equal(HttpStatusCode.OK, status);

        using JsonDocument collection = JsonDocument.Parse(body);
        Assert.Contains(collection.RootElement.GetProperty("links").EnumerateArray(),
            l => l.GetProperty("rel").GetString() == "http://www.opengis.net/def/rel/ogc/1.0/map");
    }

    [Theory]
    [InlineData("&f=jpeg", "image/jpeg")]
    [InlineData("&bgcolor=NaVy&transparent=false", "image/png")]
    [InlineData("&crs=http://www.opengis.net/def/crs/EPSG/0/3857", "image/png")]
    public async Task AMapIsDrawnAsAsked(string query, string expected)
    {
        (string id, _) = await VectorCollectionAsync();
        (HttpStatusCode status, string? type, _, _) =
            await GetAsync($"{Maps}/collections/{Uri.EscapeDataString(id)}/map?width=200{query}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(expected, type);
    }

    [Fact]
    public async Task TheDatasetMapTakesACollectionByItsAddress()
    {
        string root = await RequireServerAsync();
        (string id, _) = await VectorCollectionAsync();
        string address = Uri.EscapeDataString($"{root}{Maps}/collections/{id}");

        (HttpStatusCode status, string? type, _, _) = await GetAsync($"{Maps}/map?collections={address}&width=200");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("image/png", type);
    }

    [Theory]
    [InlineData("/collections/no_such_collection/map", HttpStatusCode.NotFound)]
    [InlineData("/collections/{id}/map?bbox=1,2,3", HttpStatusCode.BadRequest)]
    [InlineData("/collections/{id}/map?bbox=3,2,1,0", HttpStatusCode.BadRequest)]
    [InlineData("/collections/{id}/map?f=gif", HttpStatusCode.BadRequest)]
    [InlineData("/collections/{id}/map?width=0", HttpStatusCode.BadRequest)]
    [InlineData("/collections/{id}/map?width=999999", HttpStatusCode.BadRequest)]
    [InlineData("/collections/{id}/map?crs=EPSG:999999", HttpStatusCode.BadRequest)]
    [InlineData("/collections/{id}/map?bgcolor=notacolour", HttpStatusCode.BadRequest)]
    [InlineData("/map?collections=no_such_collection", HttpStatusCode.BadRequest)]
    public async Task AMapThatCannotBeDrawnIsAProblem(string path, HttpStatusCode expected)
    {
        (string id, _) = await VectorCollectionAsync();
        (HttpStatusCode status, string? type, _, byte[] body) = await GetAsync(Maps + path.Replace("{id}", id, StringComparison.Ordinal));

        Assert.Equal(expected, status);
        Assert.Equal("application/problem+json", type);
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal((int)expected, problem.RootElement.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ProcessesFollowTheGeometryServicesSharing()
    {
        (HttpStatusCode status, _, _, byte[] body) = await GetAsync(Processes + "/processes", signed: true);
        Assert.Equal(HttpStatusCode.OK, status);

        using (JsonDocument signedIn = JsonDocument.Parse(body))
        {
            string[] ids = [.. signedIn.RootElement.GetProperty("processes").EnumerateArray().Select(p => p.GetProperty("id").GetString()!)];
            Assert.Contains("buffer", ids);
            Assert.Contains("echo", ids);
        }

        (HttpStatusCode anonymous, _, _, byte[] none) = await GetAsync(Processes + "/processes");
        using JsonDocument list = JsonDocument.Parse(none);

        // The fixture shares the geometry service with the organisation; the CITE workflow alone makes it public.
        if (list.RootElement.GetProperty("processes").GetArrayLength() == 0)
        {
            Assert.Equal(HttpStatusCode.OK, anonymous);
            (HttpStatusCode refused, _, _) = await PostAsync(Processes + "/processes/echo/execution",
                """{"inputs":{"stringInput":"x"}}""", signed: false);
            Assert.Equal(HttpStatusCode.NotFound, refused);
        }
    }

    [Fact]
    public async Task TheProcessListIsPaged()
    {
        (HttpStatusCode status, _, _, byte[] body) = await GetAsync(Processes + "/processes?limit=1", signed: true);
        Assert.Equal(HttpStatusCode.OK, status);

        using (JsonDocument page = JsonDocument.Parse(body))
        {
            Assert.Equal(1, page.RootElement.GetProperty("processes").GetArrayLength());
            Assert.Contains(page.RootElement.GetProperty("links").EnumerateArray(), l => l.GetProperty("rel").GetString() == "next");
        }

        (HttpStatusCode refused, _, _, _) = await GetAsync(Processes + "/processes?limit=0", signed: true);
        Assert.Equal(HttpStatusCode.BadRequest, refused);
    }

    [Fact]
    public async Task ABufferIsTheGeometryServicesBuffer()
    {
        (HttpStatusCode status, _, string body) = await PostAsync(Processes + "/processes/buffer/execution",
            """
            {"inputs":{"geometry":{"type":"Point","coordinates":[1000,2000]},"distance":10,
             "crs":"http://www.opengis.net/def/crs/EPSG/0/3857"},"response":"document"}
            """, signed: true);

        Assert.True(status == HttpStatusCode.OK, $"{(int)status}: {body}");

        using JsonDocument result = JsonDocument.Parse(body);
        JsonElement polygon = result.RootElement.GetProperty("result");
        Assert.Equal("Polygon", polygon.GetProperty("type").GetString());

        double[] xs = [.. polygon.GetProperty("coordinates")[0].EnumerateArray().Select(p => p[0].GetDouble())];
        Assert.InRange(xs.Min(), 989.9, 990.1);
        Assert.InRange(xs.Max(), 1009.9, 1010.1);
    }

    [Fact]
    public async Task AJobIsTheCallersAndCanBeDismissed()
    {
        (HttpStatusCode status, HttpResponseMessage response, string body) = await PostAsync(
            Processes + "/processes/echo/execution",
            """{"inputs":{"stringInput":"later"},"response":"document"}""", signed: true, prefer: "respond-async");

        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {body}");
        Assert.NotNull(response.Headers.Location);

        using JsonDocument created = JsonDocument.Parse(body);
        string job = created.RootElement.GetProperty("jobID").GetString()!;

        string state = "";

        for (int i = 0; i < 50 && state != "successful"; i++)
        {
            (_, _, _, byte[] info) = await GetAsync($"{Processes}/jobs/{job}", signed: true);
            using JsonDocument document = JsonDocument.Parse(info);
            state = document.RootElement.GetProperty("status").GetString()!;

            if (state != "successful")
            {
                await Task.Delay(100);
            }
        }

        Assert.Equal("successful", state);

        (HttpStatusCode results, _, _, byte[] answer) = await GetAsync($"{Processes}/jobs/{job}/results", signed: true);
        Assert.Equal(HttpStatusCode.OK, results);
        using (JsonDocument document = JsonDocument.Parse(answer))
        {
            Assert.Equal("later", document.RootElement.GetProperty("stringOutput").GetString());
        }

        (HttpStatusCode stranger, _, _, _) = await GetAsync($"{Processes}/jobs/{job}");
        Assert.Equal(HttpStatusCode.NotFound, stranger);

        string root = await RequireServerAsync();
        using HttpRequestMessage dismiss = new(HttpMethod.Delete, new Uri($"{root}{Processes}/jobs/{job}"));
        await AuthenticateAsync(dismiss, root);
        using HttpResponseMessage dismissed = await Http.SendAsync(dismiss);
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        (HttpStatusCode gone, _, _, _) = await GetAsync($"{Processes}/jobs/{job}", signed: true);
        Assert.Equal(HttpStatusCode.NotFound, gone);
    }
}
