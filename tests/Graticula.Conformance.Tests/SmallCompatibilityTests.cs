using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The small compatibility findings of the veteran review of 2026-10-17: JSONP (V-85), the organisation's documents
/// under <c>self</c> (V-86).
/// </summary>
public sealed class SmallCompatibilityTests : ArcGisClient
{
    [Fact]
    public async Task Callback_wraps_the_answer_in_the_function_it_names()
    {
        string root = await RequireServerAsync();

        using HttpResponseMessage response = await Http.GetAsync(new Uri($"{root}/rest/info?f=json&callback=dojo.cb_1"));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/javascript", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("/**/dojo.cb_1(", body, StringComparison.Ordinal);
        Assert.EndsWith(");", body, StringComparison.Ordinal);

        using JsonDocument inside = JsonDocument.Parse(body["/**/dojo.cb_1(".Length..^2]);
        Assert.True(inside.RootElement.TryGetProperty("currentVersion", out _));
    }

    [Fact]
    public async Task An_error_reaches_the_callback_because_a_script_answered_4xx_never_runs()
    {
        string root = await RequireServerAsync();

        using HttpResponseMessage response = await Http.GetAsync(
            new Uri($"{root}/rest/services/no_such_folder/no_such_service/FeatureServer?f=json&callback=cb"));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("/**/cb(", body, StringComparison.Ordinal);
        Assert.Contains("\"error\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_callback_that_is_not_a_name_is_refused_and_never_echoed()
    {
        string root = await RequireServerAsync();

        using HttpResponseMessage response = await Http.GetAsync(new Uri($"{root}/rest/info?f=json&callback=alert(1)"));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("alert(1)", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_query_with_a_callback_is_answered_rather_than_refused_for_an_unknown_parameter()
    {
        string root = await RequireServerAsync();
        string service = await AnyFeatureServerAsync();

        JsonElement listing = await GetJsonAsync($"/rest/services/{service}/FeatureServer");
        int layer = listing.GetProperty("layers")[0].GetProperty("id").GetInt32();

        using HttpResponseMessage response = await Http.GetAsync(new Uri(
            $"{root}/rest/services/{service}/FeatureServer/{layer}/query?where=1%3D1&returnCountOnly=true&f=json&callback=cb"));
        string body = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("/**/cb(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
    }

    private async Task<string> AnyFeatureServerAsync()
    {
        JsonElement root = await GetJsonAsync("/rest/services");
        foreach (JsonElement folder in root.GetProperty("folders").EnumerateArray())
        {
            JsonElement listing = await GetJsonAsync($"/rest/services/{folder.GetString()}");
            foreach (JsonElement service in listing.GetProperty("services").EnumerateArray())
            {
                string name = service.GetProperty("name").GetString()!;
                if (service.GetProperty("type").GetString() == "FeatureServer"
                    && !name.Contains("corpus_", StringComparison.Ordinal) && !name.Contains("zz_", StringComparison.Ordinal))
                {
                    return name;
                }
            }
        }

        throw new InvalidOperationException("The server lists no FeatureServer.");
    }

    [Theory]
    [InlineData("categorySchema", "categorySchema")]
    [InlineData("resources", "resources")]
    [InlineData("subscriptionInfo", "type")]
    public async Task The_organisations_documents_answer_under_self_as_under_its_id(string document, string property)
    {
        JsonElement self = await GetJsonAsync($"/sharing/rest/portals/self/{document}");

        Assert.True(self.TryGetProperty(property, out _), $"portals/self/{document} has no '{property}'.");
    }
}
