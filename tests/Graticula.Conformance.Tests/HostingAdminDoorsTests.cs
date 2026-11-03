using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-261, from the ArcGIS veteran's review of v1.0.499 (#7): the hosting server's Administrator API answers a service's
/// status, its start and stop, and the logs' query, as an administrator's script reaches them — through this server's own
/// status and logs. Every one of them was refused by name until now.
/// </summary>
public sealed class HostingAdminDoorsTests : ArcGisClient
{
    private async Task<JsonElement> SendAsync(HttpMethod method, string path, HttpContent? content = null, bool signedIn = true)
    {
        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };

        if (signedIn)
        {
            string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();
        return body.Length == 0 ? default : JsonDocument.Parse(body).RootElement.Clone();
    }

    private Task<JsonElement> PostAsync(string path, IDictionary<string, string>? form = null, bool signedIn = true) =>
        SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form ?? new Dictionary<string, string> { ["f"] = "json" }), signedIn);

    /// <summary>Refused in either envelope: the Administrator API's <c>status: error</c>, or the sign-in's <c>error</c>.</summary>
    private static bool Failed(JsonElement answer) =>
        answer.ValueKind != JsonValueKind.Object
        || (answer.TryGetProperty("status", out JsonElement s) && s.GetString() == "error")
        || answer.TryGetProperty("error", out _);

    [Fact]
    public async Task A_service_is_stopped_and_started_through_the_admin_api_and_the_logs_say_so()
    {
        string name = $"zz_door_{Guid.NewGuid():N}"[..17];
        string service = $"/arcgis/admin/services/hosted/{name}.FeatureServer";

        JsonElement made = await SendAsync(HttpMethod.Post, "/admin/hosted/define", new StringContent(
            $$"""{"name":"{{name}}","geometryType":"point","fields":[{"name":"label","type":"Text"}],"sharing":"private"}""",
            Encoding.UTF8, "application/json"));
        Assert.False(Failed(made), made.GetRawText());

        try
        {
            JsonElement document = await SendAsync(HttpMethod.Get, $"{service}?f=json");
            Assert.Equal(name, document.GetProperty("serviceName").GetString());
            Assert.Equal("FeatureServer", document.GetProperty("type").GetString());

            JsonElement status = await SendAsync(HttpMethod.Get, $"{service}/status?f=json");
            Assert.Equal("STARTED", status.GetProperty("configuredState").GetString());
            Assert.Equal("STARTED", status.GetProperty("realTimeState").GetString());

            // A change is a POST, and nobody signed in may make it.
            Assert.True(Failed(await SendAsync(HttpMethod.Get, $"{service}/stop?f=json")));
            Assert.True(Failed(await PostAsync($"{service}/stop", signedIn: false)));

            JsonElement stopped = await PostAsync($"{service}/stop");
            Assert.Equal("success", stopped.GetProperty("status").GetString());
            Assert.Equal("STOPPED", (await SendAsync(HttpMethod.Get, $"{service}/status?f=json")).GetProperty("configuredState").GetString());

            // Stopped where every client reads it, not only where it was written.
            string root = await RequireServerAsync();
            using (HttpRequestMessage read = new(HttpMethod.Get, $"{root}/rest/services/hosted/{name}/FeatureServer?f=json"))
            {
                read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await TokenAsync(root))!);
                using HttpResponseMessage answer = await Http.SendAsync(read);
                string body = await answer.Content.ReadAsStringAsync();
                Assert.True((int)answer.StatusCode == 503 || body.Contains("\"error\"", StringComparison.Ordinal),
                    $"A stopped service answered {(int)answer.StatusCode}: {body[..Math.Min(body.Length, 200)]}");
            }

            JsonElement started = await PostAsync($"{service}/start");
            Assert.Equal("success", started.GetProperty("status").GetString());
            Assert.Equal("STARTED", (await SendAsync(HttpMethod.Get, $"{service}/status?f=json")).GetProperty("configuredState").GetString());

            // The audit trail is INFO, and a filter on the service narrows to it.
            JsonElement logs = await PostAsync("/arcgis/admin/logs/query", new Dictionary<string, string>
            {
                ["level"] = "INFO", ["pageSize"] = "100", ["f"] = "json",
                ["filter"] = $$"""{"services":["hosted/{{name}}.FeatureServer"]}""",
            });
            JsonElement[] messages = [.. logs.GetProperty("logMessages").EnumerateArray()];
            Assert.Contains(messages, m => m.GetProperty("type").GetString() == "INFO"
                && m.GetProperty("message").GetString()!.StartsWith("service.stop", StringComparison.Ordinal));
            Assert.Contains(messages, m => m.GetProperty("message").GetString()!.StartsWith("service.start", StringComparison.Ordinal));
            Assert.All(messages, m => Assert.Contains(name, m.GetProperty("source").GetString(), StringComparison.Ordinal));

            Assert.True(Failed(await PostAsync("/arcgis/admin/logs/query",
                new Dictionary<string, string> { ["level"] = "LOUD", ["f"] = "json" })));
            Assert.True(Failed(await PostAsync("/arcgis/admin/logs/query", signedIn: false)));
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");
        }
    }

    [Fact]
    public async Task A_service_that_is_not_there_is_not_found_and_an_unknown_operation_is_refused()
    {
        Assert.True(Failed(await SendAsync(HttpMethod.Get, $"/arcgis/admin/services/hosted/zz_none_{Guid.NewGuid():N}.FeatureServer/status?f=json")));
        Assert.True(Failed(await SendAsync(HttpMethod.Get, "/arcgis/admin/services/hosted/ci_buildings.FeatureServer/explode?f=json")));
    }
}
