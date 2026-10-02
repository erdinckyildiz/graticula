using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-135: a service counts the requests it answers; its owner sees them with the item, the portal face as
/// <c>numViews</c>, and the administrator reads every service's.
/// </summary>
/// <remarks>
/// <b>Counted from what the test asks</b>: the service's count before and after a known number of its own requests,
/// written at once through the administrator's flush rather than after the minute.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ServiceUsageTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string root, string token, HttpMethod method, string path)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<long> CountedAsync(string root, string token, string service)
    {
        (_, string listed) = await SendAsync(root, token, HttpMethod.Get, "/content/items");
        JsonElement item = JsonDocument.Parse(listed).RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("name").GetString() == service);
        return item.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object
            ? usage.GetProperty("requests30").GetInt64() : 0;
    }

    [Fact]
    public async Task A_services_requests_are_counted_and_shown_with_its_item()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        (_, string listed) = await SendAsync(root, token!, HttpMethod.Get, "/content/items");
        string service = JsonDocument.Parse(listed).RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("kind").GetString() == "FeatureServer" && i.GetProperty("status").GetString() == "started")
            .GetProperty("name").GetString()!;

        (HttpStatusCode flushed, string flushBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/usage/flush");
        Assert.True(flushed == HttpStatusCode.OK, $"Flushing the counts answered {(int)flushed}: {flushBody}");
        long before = await CountedAsync(root, token!, service);

        for (int i = 0; i < 5; i++)
        {
            (HttpStatusCode asked, _) = await SendAsync(root, token!, HttpMethod.Get, $"/rest/services/{service}/FeatureServer?f=json");
            Assert.Equal(HttpStatusCode.OK, asked);
        }

        // A request that fails is not a use.
        await SendAsync(root, token!, HttpMethod.Get, $"/rest/services/{service}/FeatureServer/9999/nothing");

        await SendAsync(root, token!, HttpMethod.Post, "/admin/usage/flush");
        long after = await CountedAsync(root, token!, service);
        Assert.True(after - before >= 5 && after - before < 7, $"Five requests were counted as {after - before}.");

        // The portal face's numViews is the same count, all time.
        (_, string search) = await SendAsync(root, token!, HttpMethod.Get,
            $"/sharing/rest/search?q={Uri.EscapeDataString(service.Split('/').Last())}&f=json");
        JsonElement found = JsonDocument.Parse(search).RootElement.GetProperty("results").EnumerateArray()
            .First(r => r.GetProperty("name").GetString() == service.Split('/').Last());
        Assert.True(found.GetProperty("numViews").GetInt64() >= after, $"numViews is {found.GetProperty("numViews")}.");

        // The administrator reads every service's use.
        (HttpStatusCode read, string all) = await SendAsync(root, token!, HttpMethod.Get, "/admin/usage");
        Assert.Equal(HttpStatusCode.OK, read);
        Assert.NotEmpty(JsonDocument.Parse(all).RootElement.GetProperty("services").EnumerateArray());
    }
}
