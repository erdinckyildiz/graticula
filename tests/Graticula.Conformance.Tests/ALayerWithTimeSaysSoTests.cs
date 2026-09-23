using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A layer with a time field reports <c>timeInfo</c> on its FeatureServer document and filters a
/// query by <c>time</c>.
/// </summary>
/// <remarks>
/// Written 2026-09-15: the time field reached WMS only, so the Maps SDK's time slider never offered
/// such a layer and <c>time=</c> was refused on every query.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ALayerWithTimeSaysSoTests : ArcGisClient
{
    private const string TemporalVariable = "GRATICULA_TEST_TEMPORAL";

    [Fact]
    public async Task The_document_reports_the_time_field_and_a_window_narrows_the_query()
    {
        await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(TemporalVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{TemporalVariable} is not set, so this test FAILS rather than skips.");

        string service = configured!.Trim('/');
        JsonElement layer = await GetJsonAsync($"/rest/services/{service}/FeatureServer/0");

        Assert.True(
            layer.TryGetProperty("timeInfo", out JsonElement timeInfo) && timeInfo.ValueKind == JsonValueKind.Object,
            $"{service} has one date field and its layer document carries no timeInfo.");

        string field = timeInfo.GetProperty("startTimeField").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(field));

        JsonElement extent = timeInfo.GetProperty("timeExtent");
        long from = extent[0].GetInt64();
        long until = extent[1].GetInt64();
        Assert.True(from <= until, $"timeExtent runs backwards: {extent}");

        long all = (await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true"))
            .GetProperty("count").GetInt64();

        long first = (await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&time={from},{from}"))
            .GetProperty("count").GetInt64();

        long window = (await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&time={from},{until}"))
            .GetProperty("count").GetInt64();

        Assert.True(first >= 1, "The first moment of the extent selects nothing, and a feature is at it by definition.");
        Assert.True(window <= all, $"A window over the whole extent selected {window} of {all}.");

        if (from < until)
        {
            Assert.True(first < all || all == first, $"The first moment selected {first} of {all}.");
        }

        (HttpStatusCode refused, _) = await AnonymousAsync(
            $"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&time=yesterday");

        Assert.NotEqual(HttpStatusCode.OK, refused);
    }

    [Fact]
    public async Task A_date_statistic_is_a_number_like_every_other_date()
    {
        // V-69, the fourth ArcGIS review: max of a date field came back as ISO text, while the same field in a
        // feature was epoch milliseconds, so a Dashboards indicator reading the newest date got a string.
        await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(TemporalVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{TemporalVariable} is not set, so this test FAILS rather than skips.");

        string service = configured!.Trim('/');
        JsonElement timeInfo = (await GetJsonAsync($"/rest/services/{service}/FeatureServer/0")).GetProperty("timeInfo");
        string field = timeInfo.GetProperty("startTimeField").GetString()!;
        long until = timeInfo.GetProperty("timeExtent")[1].GetInt64();

        string statistics = Uri.EscapeDataString(
            $"[{{\"statisticType\":\"max\",\"onStatisticField\":\"{field}\",\"outStatisticFieldName\":\"newest\"}}]");

        JsonElement answer = await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer/0/query?where=1%3D1&outStatistics={statistics}");

        JsonElement newest = answer.GetProperty("features")[0].GetProperty("attributes").GetProperty("newest");

        Assert.Equal(JsonValueKind.Number, newest.ValueKind);
        Assert.Equal(until, newest.GetInt64());
    }

    [Fact]
    public async Task The_map_face_knows_the_time_and_draws_by_it()
    {
        // V-73, the fourth ArcGIS review: the MapServer documents carried no timeInfo and export ignored time,
        // drawing the same image byte for byte, while WMS TIME filtered the same layer.
        string root = await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(TemporalVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{TemporalVariable} is not set, so this test FAILS rather than skips.");

        string service = configured!.Trim('/');
        JsonElement feature = (await GetJsonAsync($"/rest/services/{service}/FeatureServer/0")).GetProperty("timeInfo");
        JsonElement map = (await GetJsonAsync($"/rest/services/{service}/MapServer/0")).GetProperty("timeInfo");
        JsonElement whole = await GetJsonAsync($"/rest/services/{service}/MapServer");

        Assert.Equal(feature.GetProperty("startTimeField").GetString(), map.GetProperty("startTimeField").GetString());
        Assert.Equal(JsonValueKind.Object, whole.GetProperty("timeInfo").ValueKind);

        long from = feature.GetProperty("timeExtent")[0].GetInt64();
        long until = feature.GetProperty("timeExtent")[1].GetInt64();
        JsonElement extent = whole.GetProperty("fullExtent");
        string bbox = string.Join(",", extent.GetProperty("xmin").GetDouble(), extent.GetProperty("ymin").GetDouble(),
            extent.GetProperty("xmax").GetDouble(), extent.GetProperty("ymax").GetDouble());

        byte[] all = await ImageAsync(root, $"/rest/services/{service}/MapServer/export?bbox={bbox}&size=256,256&f=image");
        byte[] first = await ImageAsync(root, $"/rest/services/{service}/MapServer/export?bbox={bbox}&size=256,256&f=image&time={from},{from}");

        if (from < until)
        {
            Assert.False(all.AsSpan().SequenceEqual(first), "export drew the same image with a time window as without one.");
        }

        (HttpStatusCode _, string refused) = await AnonymousAsync(
            $"/rest/services/{service}/MapServer/export?bbox={bbox}&size=64,64&f=json&time=yesterday");

        Assert.Contains("epoch milliseconds", refused, StringComparison.Ordinal);
    }

    private async Task<byte[]> ImageAsync(string root, string path)
    {
        using System.Net.Http.HttpRequestMessage request = new(System.Net.Http.HttpMethod.Get, new Uri(root + path));
        await AuthenticateAsync(request, root);
        using System.Net.Http.HttpResponseMessage response = await Http.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }
}
