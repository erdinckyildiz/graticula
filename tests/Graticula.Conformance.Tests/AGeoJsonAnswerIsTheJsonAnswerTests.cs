using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// That <c>f=geojson</c> is the json answer in GeoJSON's shape — V-55, the third ArcGIS review.
/// </summary>
/// <remarks>
/// <b>Decided by the owner 2026-09-23.</b> ArcGIS has answered <c>f=geojson</c> since 10.4 and, since 10.8, in
/// RFC 7946's WGS 84 when no <c>outSR</c> is named; this server refused it. The same parsed query is written as a
/// FeatureCollection, so the rows, the ids and the attributes are compared with the json answer rather than with
/// a fixture.
/// </remarks>
[Collection("catalogue walk")]
public sealed class AGeoJsonAnswerIsTheJsonAnswerTests : ArcGisClient
{
    private async Task<(string Path, string Oid)> LayerAsync()
    {
        string? name = Environment.GetEnvironmentVariable(AdvertisedCapabilityTests.LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{AdvertisedCapabilityTests.LayerVariable} is not set, so this test FAILS rather than skips.");

        string path = $"/rest/services/{name}/FeatureServer/0";
        JsonElement document = await GetJsonAsync(path);

        Assert.Contains("geoJSON", document.GetProperty("supportedQueryFormats").GetString(), StringComparison.Ordinal);
        return (path, document.GetProperty("objectIdField").GetString()!);
    }

    private async Task<(HttpStatusCode Status, string? MediaType, string Body)> GetAsync(string path)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri($"{root}{path}"));
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_features_match_the_json_answer_and_come_back_in_wgs84()
    {
        (string path, string oid) = await LayerAsync();
        string query = $"{path}/query?where=1%3D1&outFields=*&orderByFields={oid}&resultRecordCount=10&returnGeometry=true";

        JsonElement json = await GetJsonAsync(query + "&outSR=4326");
        (HttpStatusCode status, string? media, string body) = await GetAsync(query + "&f=geojson");

        Assert.True(status == HttpStatusCode.OK, $"f=geojson answered {(int)status}: {body}");
        Assert.Equal("application/geo+json", media);

        JsonElement collection = JsonDocument.Parse(body).RootElement;
        Assert.Equal("FeatureCollection", collection.GetProperty("type").GetString());

        // No outSR named: WGS 84, and so no crs member (RFC 7946).
        Assert.False(collection.TryGetProperty("crs", out _), "A WGS 84 answer named a crs.");

        JsonElement[] expected = [.. json.GetProperty("features").EnumerateArray()];
        JsonElement[] actual = [.. collection.GetProperty("features").EnumerateArray()];

        Assert.NotEmpty(actual);
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < actual.Length; i++)
        {
            JsonElement attributes = expected[i].GetProperty("attributes");
            JsonElement properties = actual[i].GetProperty("properties");

            Assert.Equal("Feature", actual[i].GetProperty("type").GetString());
            Assert.Equal(attributes.GetProperty(oid).GetInt64(), actual[i].GetProperty("id").GetInt64());

            // The same values by the same code: a date is epoch milliseconds in both.
            foreach (JsonProperty attribute in attributes.EnumerateObject())
            {
                Assert.Equal(attribute.Value.GetRawText(), properties.GetProperty(attribute.Name).GetRawText());
            }
        }

        Assert.Equal(
            json.GetProperty("exceededTransferLimit").GetBoolean(),
            collection.GetProperty("properties").GetProperty("exceededTransferLimit").GetBoolean());
    }

    [Fact]
    public async Task Another_reference_is_named_and_a_shape_that_is_not_features_is_refused()
    {
        (string path, string oid) = await LayerAsync();

        (HttpStatusCode status, _, string body) = await GetAsync(
            $"{path}/query?where=1%3D1&outFields={oid}&resultRecordCount=1&outSR=3857&f=geojson");

        Assert.True(status == HttpStatusCode.OK, $"f=geojson&outSR=3857 answered {(int)status}: {body}");
        Assert.Equal(
            "EPSG:3857",
            JsonDocument.Parse(body).RootElement.GetProperty("crs").GetProperty("properties").GetProperty("name").GetString());

        foreach (string shape in (string[])["returnCountOnly=true", "returnIdsOnly=true", "returnExtentOnly=true"])
        {
            (HttpStatusCode refused, _, string why) = await GetAsync($"{path}/query?where=1%3D1&{shape}&f=geojson");

            Assert.True(refused == HttpStatusCode.BadRequest, $"{shape} with f=geojson answered {(int)refused}: {why}");
            Assert.Contains("f=geojson", why, StringComparison.Ordinal);
        }

        (HttpStatusCode measured, _, string measure) = await GetAsync($"{path}/query?where=1%3D1&returnM=true&f=geojson");

        Assert.True(measured == HttpStatusCode.BadRequest, $"returnM=true with f=geojson answered {(int)measured}: {measure}");
    }
}
