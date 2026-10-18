using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A statistic over an SQL expression, and a count of distinct values — both false in the layer document until the
/// ArcGIS gap review (ADR-197 §2, ADR-008 §4a-iv).
/// </summary>
/// <remarks>
/// Checked against the layer's own statistics rather than known values: the average of twice the object id is twice
/// the average, and a count of distinct object ids is the count of features.
/// </remarks>
public sealed class StatisticOverAnExpressionTests : ArcGisClient
{
    private static string Queryable()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_QUERYABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_QUERYABLE is not set, so there is no layer to ask.");
        return qualified;
    }

    [Fact]
    public async Task The_average_of_twice_a_field_is_twice_its_average()
    {
        string layer = $"/rest/services/{Queryable()}/FeatureServer/0";
        JsonElement document = await GetJsonAsync(layer);
        string oid = document.GetProperty("objectIdField").GetString()!;
        Assert.True(document.GetProperty("advancedQueryCapabilities").GetProperty("supportsSqlExpression").GetBoolean());

        string statistics = Uri.EscapeDataString(
            $"[{{\"statisticType\":\"avg\",\"onStatisticField\":\"{oid}\",\"outStatisticFieldName\":\"plain\"}},"
            + $"{{\"statisticType\":\"AVG\",\"onStatisticField\":\"{oid}*2\",\"outStatisticFieldName\":\"doubled\"}}]");
        JsonElement answer = await GetJsonAsync($"{layer}/query?where=1%3D1&outStatistics={statistics}");
        JsonElement attributes = answer.GetProperty("features").EnumerateArray().Single().GetProperty("attributes");

        Assert.Equal(attributes.GetProperty("plain").GetDouble() * 2, attributes.GetProperty("doubled").GetDouble(), 6);
    }

    /// <summary>
    /// D-301: an expression groups, answered as <c>Expr1</c> as Esri's server answers one (sampleserver6, 2026-10-18), and
    /// the groups together hold every feature; an expression in the order is refused, as Esri's server refuses it.
    /// </summary>
    [Fact]
    public async Task A_grouped_expression_is_answered_as_expr1_and_covers_every_feature()
    {
        string layer = $"/rest/services/{Queryable()}/FeatureServer/0";
        string oid = (await GetJsonAsync(layer)).GetProperty("objectIdField").GetString()!;
        long total = (await GetJsonAsync($"{layer}/query?where=1%3D1&returnCountOnly=true")).GetProperty("count").GetInt64();

        string statistics = Uri.EscapeDataString(
            $"[{{\"statisticType\":\"count\",\"onStatisticField\":\"{oid}\",\"outStatisticFieldName\":\"n\"}}]");
        string grouped = Uri.EscapeDataString($"FLOOR({oid}/100)");
        JsonElement answer = await GetJsonAsync($"{layer}/query?where=1%3D1&outStatistics={statistics}&groupByFieldsForStatistics={grouped}");

        Assert.Contains(answer.GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "Expr1");
        Assert.Equal(total, answer.GetProperty("features").EnumerateArray()
            .Sum(f => f.GetProperty("attributes").GetProperty("n").GetInt64()));

        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        string ordered = Uri.EscapeDataString($"FLOOR({oid}/100) DESC");
        (System.Net.HttpStatusCode code, _) = await RequestAsync(System.Net.Http.HttpMethod.Get,
            $"{root}{layer}/query?where=1%3D1&outStatistics={statistics}&groupByFieldsForStatistics={grouped}&orderByFields={ordered}&f=json",
            token!, null);
        Assert.Equal(400, (int)code);
    }

    [Fact]
    public async Task A_count_of_distinct_ids_is_the_count_of_features()
    {
        string layer = $"/rest/services/{Queryable()}/FeatureServer/0";
        JsonElement document = await GetJsonAsync(layer);
        string oid = document.GetProperty("objectIdField").GetString()!;
        Assert.True(document.GetProperty("advancedQueryCapabilities").GetProperty("supportsCountDistinct").GetBoolean());

        JsonElement all = await GetJsonAsync($"{layer}/query?where=1%3D1&returnCountOnly=true");
        JsonElement distinct = await GetJsonAsync(
            $"{layer}/query?where=1%3D1&returnCountOnly=true&returnDistinctValues=true&outFields={oid}");

        Assert.Equal(all.GetProperty("count").GetInt64(), distinct.GetProperty("count").GetInt64());
    }

    [Theory]
    [InlineData("{0}) IS NULL OR (1=1")]
    [InlineData("{0}; drop table x")]
    public async Task An_expression_that_is_not_one_value_is_refused(string shape)
    {
        string layer = $"/rest/services/{Queryable()}/FeatureServer/0";
        string oid = (await GetJsonAsync(layer)).GetProperty("objectIdField").GetString()!;
        string statistics = Uri.EscapeDataString(
            $"[{{\"statisticType\":\"count\",\"onStatisticField\":\"{string.Format(System.Globalization.CultureInfo.InvariantCulture, shape, oid)}\",\"outStatisticFieldName\":\"n\"}}]");

        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        (System.Net.HttpStatusCode code, string body) = await RequestAsync(
            System.Net.Http.HttpMethod.Get, $"{root}{layer}/query?where=1%3D1&outStatistics={statistics}&f=json", token!, null);

        Assert.Equal(400, (int)code);
        Assert.Contains("onStatisticField", body, StringComparison.Ordinal);
    }
}
