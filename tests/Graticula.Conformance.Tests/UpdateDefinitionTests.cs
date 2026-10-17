using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A hosted service's admin document and ArcGIS's <c>updateDefinition</c> — both 404 until the ArcGIS gap review of
/// 2026-10-17, so the ArcGIS API for Python's <c>manager.update_definition</c> had nothing to call.
/// </summary>
[Collection("catalogue walk")]
public sealed class UpdateDefinitionTests : ArcGisClient
{
    private static string Editable()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_EDITABLE is not set, so there is no hosted service to change.");
        return qualified;
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, Dictionary<string, string> form)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{root}{path}") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_description_is_changed_through_update_definition_and_read_back_from_the_service()
    {
        string service = Editable();
        string admin = $"/rest/admin/services/{service}/FeatureServer";

        JsonElement document = await GetJsonAsync(admin);
        Assert.Equal("FeatureServer", document.GetProperty("adminServiceInfo").GetProperty("type").GetString());
        Assert.Contains("Query", document.GetProperty("capabilities").GetString(), StringComparison.Ordinal);
        string before = document.GetProperty("serviceDescription").GetString() ?? string.Empty;

        string marker = $"Changed by updateDefinition {Guid.NewGuid():N}";

        try
        {
            (HttpStatusCode status, string body) = await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = JsonSerializer.Serialize(new { serviceDescription = marker }),
                ["f"] = "json",
            });

            Assert.True(status == HttpStatusCode.OK, body);
            Assert.True(JsonDocument.Parse(body).RootElement.GetProperty("success").GetBoolean(), body);
            // The service document writes it as `description` (FeatureServerMetadataWriter.Service).
            Assert.Equal(marker, (await GetJsonAsync($"/rest/services/{service}/FeatureServer")).GetProperty("description").GetString());
        }
        finally
        {
            await PostAsync($"{admin}/updateDefinition", new()
            {
                ["updateDefinition"] = JsonSerializer.Serialize(new { serviceDescription = before }),
                ["f"] = "json",
            });
        }
    }

    [Fact]
    public async Task A_key_this_server_does_not_change_is_refused_by_name_and_nothing_is_written()
    {
        string service = Editable();
        string admin = $"/rest/admin/services/{service}/FeatureServer";
        string before = (await GetJsonAsync(admin)).GetProperty("serviceDescription").GetString() ?? string.Empty;

        (HttpStatusCode status, string body) = await PostAsync($"{admin}/updateDefinition", new()
        {
            ["updateDefinition"] = JsonSerializer.Serialize(new { serviceDescription = "should not land", hasStaticData = true }),
            ["f"] = "json",
        });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("hasStaticData", body, StringComparison.Ordinal);
        Assert.Equal(before, (await GetJsonAsync(admin)).GetProperty("serviceDescription").GetString() ?? string.Empty);

        (HttpStatusCode sync, string syncBody) = await PostAsync($"{admin}/updateDefinition", new()
        {
            ["updateDefinition"] = JsonSerializer.Serialize(new { capabilities = "Query,Sync" }),
            ["f"] = "json",
        });

        Assert.Equal(HttpStatusCode.BadRequest, sync);
        Assert.Contains("Sync", syncBody, StringComparison.Ordinal);
    }
}
