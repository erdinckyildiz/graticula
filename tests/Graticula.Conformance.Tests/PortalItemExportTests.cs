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
/// ADR-263, from the ArcGIS veteran's review of v1.0.499 (#6): <c>content/users/{u}/export</c> makes the caller an item of
/// a feature layer's data, as the ArcGIS API for Python's <c>item.export()</c> asks — the item at once, its status from
/// the export's job, its file once written. ADR-106 refused it while there was no item table.
/// </summary>
public sealed class PortalItemExportTests : ArcGisClient
{
    private static string User => Environment.GetEnvironmentVariable(UserVariable)
        ?? throw new InvalidOperationException($"{UserVariable} is not set; this test signs in as that user.");

    private async Task<(JsonElement Json, byte[] Bytes)> SendAsync(HttpMethod method, string path, IDictionary<string, string>? form = null)
    {
        string root = await RequireServerAsync();
        string token = await TokenAsync(root) ?? throw new InvalidOperationException("No token: this test signs in.");

        using HttpRequestMessage request = new(method, $"{root}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        JsonElement json = default;
        if (bytes.Length > 0 && bytes[0] == (byte)'{')
        {
            json = JsonDocument.Parse(bytes).RootElement.Clone();
        }

        return (json, bytes);
    }

    private async Task<string> ItemIdAsync(string service)
    {
        (JsonElement listing, _) = await SendAsync(HttpMethod.Get, "/content/items");
        return listing.GetProperty("items").EnumerateArray()
            .First(i => i.TryGetProperty("name", out JsonElement n) && n.GetString() == service).GetProperty("itemId").GetString()!;
    }

    [Fact]
    public async Task A_layer_is_exported_to_an_item_of_the_callers_and_its_file_arrives_with_its_status()
    {
        string source = await ItemIdAsync("hosted/ci_buildings");
        string title = $"zz export {Guid.NewGuid():N}"[..20];
        string? made = null;

        try
        {
            // One export of a caller's waits at a time (ADR-106), and the suite's other export tests are the same caller's.
            JsonElement started = default;
            for (int attempt = 0; attempt < 120; attempt++)
            {
                (started, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/export",
                    new Dictionary<string, string> { ["itemId"] = source, ["exportFormat"] = "GeoPackage", ["title"] = title, ["f"] = "json" });
                if (!(started.TryGetProperty("error", out JsonElement busy) && busy.GetProperty("code").GetInt32() == 409))
                {
                    break;
                }

                await Task.Delay(1000);
            }

            Assert.True(started.TryGetProperty("exportItemId", out JsonElement id), started.GetRawText());
            made = id.GetString()!;
            string job = started.GetProperty("jobId").GetString()!;
            Assert.Equal("GeoPackage", started.GetProperty("type").GetString());

            // The item is there at once, so a client can open it before the file is.
            (JsonElement item, _) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{made}?f=json");
            Assert.Equal(title, item.GetProperty("title").GetString());

            string status = "processing";
            for (int i = 0; i < 120 && status is not ("completed" or "failed"); i++)
            {
                await Task.Delay(500);
                (JsonElement said, _) = await SendAsync(HttpMethod.Get,
                    $"/sharing/rest/content/users/{User}/items/{made}/status?jobId={job}&jobType=export&f=json");
                status = said.GetProperty("status").GetString()!;
                Assert.NotEqual("failed", status);
            }

            Assert.Equal("completed", status);

            (_, byte[] data) = await SendAsync(HttpMethod.Get, $"/sharing/rest/content/items/{made}/data");
            Assert.StartsWith("SQLite format 3", Encoding.ASCII.GetString(data, 0, Math.Min(15, data.Length)), StringComparison.Ordinal);
        }
        finally
        {
            if (made is not null)
            {
                await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/items/{made}/delete",
                    new Dictionary<string, string> { ["permanentDelete"] = "true", ["f"] = "json" });
            }
        }
    }

    [Fact]
    public async Task Several_layers_are_not_one_GeoJSON_item_and_what_is_not_a_layer_is_not_exported()
    {
        string several = await ItemIdAsync("hosted/ci_EarlyAlert");

        (JsonElement refused, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/export",
            new Dictionary<string, string> { ["itemId"] = several, ["exportFormat"] = "GeoJson", ["f"] = "json" });
        Assert.True(refused.TryGetProperty("error", out _), refused.GetRawText());

        (JsonElement nothing, _) = await SendAsync(HttpMethod.Post, $"/sharing/rest/content/users/{User}/export",
            new Dictionary<string, string> { ["itemId"] = new string('0', 32), ["exportFormat"] = "CSV", ["f"] = "json" });
        Assert.True(nothing.TryGetProperty("error", out _), nothing.GetRawText());
    }
}
