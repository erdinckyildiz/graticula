using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// What the third ArcGIS review found wanting on the MapServer face and the route table — V-52 and V-53.
/// </summary>
/// <remarks>
/// <para>
/// <b>V-53: an ArcGIS client parses every answer as JSON.</b> <c>MapServer/find</c>, which is not implemented,
/// and a GET to <c>applyEdits</c>, which takes POST, answered 404 and 405 with no body at all — and an empty
/// body breaks the client's parser and reads as *the server is unreachable*.
/// </para>
/// <para>
/// <b>V-52: two documents about one layer name one key.</b> The MapServer layer document had no
/// <c>objectIdField</c> and typed the object id <c>esriFieldTypeInteger</c>, while the FeatureServer document
/// of the same layer said <c>esriFieldTypeOID</c>.
/// </para>
/// </remarks>
public sealed class AnArcGisAddressAlwaysAnswersInJsonTests : ArcGisClient
{
    private static string Queryable()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_QUERYABLE is not set, so there is no service to ask.");
        return qualified;
    }

    [Theory]
    [InlineData("MapServer/find?searchText=x&layers=0&f=json", 404, "find")]
    [InlineData("FeatureServer/0/applyEdits?f=json", 405, "POST")]
    [InlineData("FeatureServer/0/noSuchOperation?f=json", 404, "noSuchOperation")]
    public async Task An_address_with_no_answer_still_answers_in_arcgis_s_envelope(string tail, int status, string says)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        (System.Net.HttpStatusCode code, string body) = await RequestAsync(
            HttpMethod.Get, $"{root}/rest/services/{Queryable()}/{tail}", token!, null);

        Assert.Equal(status, (int)code);
        Assert.False(string.IsNullOrWhiteSpace(body), $"{tail} answered {status} with no body, which an ArcGIS client cannot parse.");

        JsonElement error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        Assert.Equal(status, error.GetProperty("code").GetInt32());
        Assert.Contains(says, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Array, error.GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task The_mapserver_layer_names_its_object_id_as_the_featureserver_does()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        (_, string map) = await RequestAsync(HttpMethod.Get, $"{root}/rest/services/{Queryable()}/MapServer/0?f=json", token!, null);
        (_, string feature) = await RequestAsync(HttpMethod.Get, $"{root}/rest/services/{Queryable()}/FeatureServer/0?f=json", token!, null);

        JsonElement mapLayer = JsonDocument.Parse(map).RootElement;
        string objectId = JsonDocument.Parse(feature).RootElement.GetProperty("objectIdField").GetString()!;

        Assert.Equal(objectId, mapLayer.GetProperty("objectIdField").GetString());

        foreach (JsonElement field in mapLayer.GetProperty("fields").EnumerateArray())
        {
            if (field.GetProperty("name").GetString() == objectId)
            {
                Assert.Equal("esriFieldTypeOID", field.GetProperty("type").GetString());
            }
        }
    }
}
