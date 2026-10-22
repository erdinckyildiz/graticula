using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-218: a service an administrator pins is warmed within seconds and renewed again before its shape would expire;
/// unpinned, it stops being.
/// </summary>
[Collection("catalogue walk")]
public sealed class APinnedServiceIsKeptWarmTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string root, string token, HttpMethod method, string path, string? json = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}")
        {
            Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static DateTimeOffset? WarmedAt(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("warmedAt") is { ValueKind: JsonValueKind.String } at
            ? at.GetDateTimeOffset()
            : null;

    [Fact]
    public async Task A_pinned_service_is_warmed_and_renewed_and_an_unpinned_one_is_not()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string name = $"zz_pin_{Guid.NewGuid():N}"[..16];
        string pin = $"/admin/services/{name}/pinned?folder=hosted";

        try
        {
            (HttpStatusCode made, string madeBody) = await SendAsync(root, token!, HttpMethod.Post, "/admin/hosted/define",
                $$"""{"name":"{{name}}","geometryType":"point","fields":[{"name":"label","type":"Text"}],"sharing":"private"}""");
            Assert.True(made == HttpStatusCode.Created, $"Defining the layer answered {(int)made}: {madeBody}");

            (HttpStatusCode before, string beforeBody) = await SendAsync(root, token!, HttpMethod.Get, pin);
            Assert.True(before == HttpStatusCode.OK, $"Reading the pin answered {(int)before}: {beforeBody}");
            Assert.False(JsonDocument.Parse(beforeBody).RootElement.GetProperty("pinned").GetBoolean());

            (HttpStatusCode missing, _) = await SendAsync(root, token!, HttpMethod.Put, pin, "{}");
            Assert.Equal(HttpStatusCode.BadRequest, missing);

            (HttpStatusCode pinned, string pinnedBody) = await SendAsync(root, token!, HttpMethod.Put, pin, """{"pinned":true}""");
            Assert.True(pinned == HttpStatusCode.OK, $"Pinning answered {(int)pinned}: {pinnedBody}");

            // Warmed within seconds: the pin wakes the round rather than waiting for the next one.
            DateTimeOffset? first = null;
            for (int i = 0; i < 20 && first is null; i++)
            {
                await Task.Delay(500);
                first = WarmedAt((await SendAsync(root, token!, HttpMethod.Get, pin)).Body);
            }

            Assert.True(first is not null, "A pinned service was not warmed within ten seconds of being pinned.");

            // Renewed again before its shape's thirty seconds run out — the mechanism, not the first warm.
            DateTimeOffset? again = first;
            for (int i = 0; i < 30 && again == first; i++)
            {
                await Task.Delay(1000);
                again = WarmedAt((await SendAsync(root, token!, HttpMethod.Get, pin)).Body);
            }

            Assert.True(again > first, $"A pinned service warmed at {first:O} was not renewed within thirty seconds.");

            (HttpStatusCode unpinned, string unpinnedBody) = await SendAsync(root, token!, HttpMethod.Put, pin, """{"pinned":false}""");
            Assert.True(unpinned == HttpStatusCode.OK, $"Unpinning answered {(int)unpinned}: {unpinnedBody}");
            JsonElement after = JsonDocument.Parse(unpinnedBody).RootElement;
            Assert.False(after.GetProperty("pinned").GetBoolean());
            Assert.Equal(JsonValueKind.Null, after.GetProperty("warmedAt").ValueKind);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }
}
