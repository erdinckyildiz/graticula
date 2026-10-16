using System;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ArcGIS's Feature resource, <c>FeatureServer/{layer}/{objectId}</c>: one feature by its id
/// (<see href="../../docs/architecture-debt.md">D-298</see>).
/// </summary>
/// <remarks>
/// Esri's server answers it with every attribute and the geometry whatever <c>outFields</c> says, and with a
/// 404 envelope for an id that is not there (sampleserver6, 2026-10-16). It was a 404 here for every id. The
/// resource is answered by the layer's own query, so the third test asks the question that matters most about
/// that: a caller the query refuses is refused here too.
/// </remarks>
[Collection("catalogue walk")]
public sealed class AFeatureIsReadByItsIdTests : ArcGisClient
{
    private static string? Layer()
    {
        string? named = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE");

        if (string.IsNullOrWhiteSpace(named))
        {
            return null;
        }

        return "/rest/services/" + (named.Contains('/', StringComparison.Ordinal) ? named : "hosted/" + named)
            + "/FeatureServer/0";
    }

    [Fact]
    public async Task A_feature_comes_back_whole_under_its_id()
    {
        if (Layer() is not { } layer)
        {
            return;
        }

        JsonElement document = await GetJsonAsync(layer);
        string oid = document.GetProperty("objectIdField").GetString()!;
        string[] fields = [.. document.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()!)];

        long id = (await GetJsonAsync($"{layer}/query?where=1%3D1&returnIdsOnly=true"))
            .GetProperty("objectIds").EnumerateArray().First().GetInt64();

        // outFields asks for one field and is ignored, as Esri's server ignores it.
        JsonElement feature = (await GetJsonAsync($"{layer}/{id}?outFields={oid}")).GetProperty("feature");

        Assert.Equal(id, feature.GetProperty("attributes").GetProperty(oid).GetInt64());
        Assert.True(feature.TryGetProperty("geometry", out JsonElement geometry) && geometry.ValueKind == JsonValueKind.Object,
            $"The feature came back without its geometry: {feature}");

        string[] answered = [.. feature.GetProperty("attributes").EnumerateObject().Select(p => p.Name)];
        Assert.Empty(fields.Except(answered, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task An_id_that_is_not_there_is_a_404_that_says_so()
    {
        if (Layer() is not { } layer)
        {
            return;
        }

        string root = await RequireServerAsync();
        using System.Net.Http.HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, new Uri($"{root}{layer}/987654321987?f=json"));
        await AuthenticateAsync(request, root);
        using System.Net.Http.HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("987654321987", JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_caller_the_query_refuses_is_refused_the_feature_too()
    {
        if (Layer() is not { } layer)
        {
            return;
        }

        long id = (await GetJsonAsync($"{layer}/query?where=1%3D1&returnIdsOnly=true"))
            .GetProperty("objectIds").EnumerateArray().First().GetInt64();

        (HttpStatusCode query, _) = await AnonymousAsync($"{layer}/query?where=1%3D1&returnCountOnly=true&f=json");
        (HttpStatusCode feature, string body) = await AnonymousAsync($"{layer}/{id}?f=json");

        if (query == HttpStatusCode.OK)
        {
            Assert.Equal(HttpStatusCode.OK, feature);
        }
        else
        {
            Assert.True(feature != HttpStatusCode.OK, $"The query refused an anonymous caller ({(int)query}) and the feature did not: {body}");
            Assert.DoesNotContain("\"feature\"", body, StringComparison.Ordinal);
        }
    }
}
