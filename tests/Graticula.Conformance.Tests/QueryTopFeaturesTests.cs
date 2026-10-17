using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>queryTopFeatures</c> answers the first features of each group — a 404 until the ArcGIS gap review of 2026-10-17.
/// </summary>
/// <remarks>
/// Checked against the layer's own statistics rather than a fixture's known values: one feature per group, as many
/// groups as a grouped count finds, and each the group's highest object id when that is the order asked.
/// </remarks>
public sealed class QueryTopFeaturesTests : ArcGisClient
{
    private static string Queryable()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_QUERYABLE is not set, so there is no layer to ask.");
        return qualified;
    }

    [Fact]
    public async Task The_first_feature_of_each_group_is_the_one_the_order_puts_first()
    {
        string layer = $"/rest/services/{Queryable()}/FeatureServer/0";
        JsonElement document = await GetJsonAsync(layer);
        string oid = document.GetProperty("objectIdField").GetString()!;
        string? group = document.GetProperty("displayField").GetString();
        Assert.False(string.IsNullOrEmpty(group), "The layer names no display field to group by.");
        Assert.True(document.GetProperty("advancedQueryCapabilities").GetProperty("supportsTopFeaturesQuery").GetBoolean());

        string statistics = Uri.EscapeDataString(
            $"[{{\"statisticType\":\"max\",\"onStatisticField\":\"{oid}\",\"outStatisticFieldName\":\"top\"}}]");
        JsonElement grouped = await GetJsonAsync(
            $"{layer}/query?where=1%3D1&outStatistics={statistics}&groupByFieldsForStatistics={group}&resultRecordCount=2000");
        HashSet<long> expected = [.. grouped.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("attributes").GetProperty("top").GetInt64())];

        string filter = Uri.EscapeDataString($"{{\"groupByFields\":\"{group}\",\"topCount\":1,\"orderByFields\":\"{oid} DESC\"}}");
        JsonElement top = await GetJsonAsync(
            $"{layer}/queryTopFeatures?where=1%3D1&topFilter={filter}&outFields={oid},{group}&returnGeometry=false&resultRecordCount=2000");
        HashSet<long> found = [.. top.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("attributes").GetProperty(oid).GetInt64())];

        Assert.Equal(expected.Count, found.Count);
        Assert.Subset(expected, found);

        JsonElement counted = await GetJsonAsync($"{layer}/queryTopFeatures?where=1%3D1&topFilter={filter}&returnCountOnly=true");
        Assert.Equal(expected.Count, counted.GetProperty("count").GetInt64());
    }

    [Fact]
    public async Task A_filter_naming_what_the_layer_does_not_have_is_refused()
    {
        string layer = $"/rest/services/{Queryable()}/FeatureServer/0";
        string filter = Uri.EscapeDataString("{\"groupByFields\":\"no_such_field\",\"topCount\":1}");

        (int status, string body) = await StatusAndBodyAsync($"{layer}/queryTopFeatures?where=1%3D1&topFilter={filter}&f=json");

        Assert.Equal(400, status);
        Assert.Contains("no_such_field", body, StringComparison.Ordinal);
    }

    private async Task<(int Status, string Body)> StatusAndBodyAsync(string path)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        (System.Net.HttpStatusCode code, string body) = await RequestAsync(System.Net.Http.HttpMethod.Get, $"{root}{path}", token!, null);
        return ((int)code, body);
    }
}
