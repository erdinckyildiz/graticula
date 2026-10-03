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
/// ADR-149: the geometry service the portal names to ArcGIS clients is this server's own, or another an administrator
/// chose — ArcGIS Enterprise's Utility Services setting.
/// </summary>
[Collection("catalogue walk")]
public sealed class GeometryServiceSettingTests : ArcGisClient
{
    private async Task<(HttpStatusCode Status, string Body)> SendAsync(string root, string? token, HttpMethod method, string path, object? body = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}");
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string? Named(string self) =>
        JsonDocument.Parse(self).RootElement.GetProperty("helperServices") is { } helpers
        && helpers.TryGetProperty("geometry", out JsonElement geometry) ? geometry.GetProperty("url").GetString() : null;

    [Fact]
    public async Task The_portal_names_the_geometry_service_an_administrator_chose_and_its_own_when_cleared()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");
        const string Elsewhere = "https://gis.example.com/arcgis/rest/services/Utilities/Geometry/GeometryServer";

        try
        {
            (HttpStatusCode bad, string badBody) = await SendAsync(root, token, HttpMethod.Put, "/admin/settings/geometry-service",
                new { url = "https://gis.example.com/arcgis/rest/services/Other/MapServer" });
            // Refused, as every admin refusal here is, with an error document that says what the address must be.
            Assert.Contains("\"error\"", badBody, StringComparison.Ordinal);
            Assert.Contains("/GeometryServer", badBody, StringComparison.Ordinal);
            _ = bad;

            // The ux review, 2026-10-03: an http service from an https portal is blocked by browsers, so it is refused;
            // another spelling is stored as ArcGIS spells it; and this server's own, typed in, is the default.
            if (root.StartsWith("https:", StringComparison.Ordinal))
            {
                (_, string httpBody) = await SendAsync(root, token, HttpMethod.Put, "/admin/settings/geometry-service",
                    new { url = "http://gis.example.com/arcgis/rest/services/Utilities/Geometry/GeometryServer" });
                Assert.Contains("https", httpBody, StringComparison.Ordinal);
                Assert.Contains("\"error\"", httpBody, StringComparison.Ordinal);
            }

            (_, string lower) = await SendAsync(root, token, HttpMethod.Put, "/admin/settings/geometry-service",
                new { url = "https://gis.example.com/arcgis/rest/services/Utilities/Geometry/geometryserver/" });
            Assert.Equal(Elsewhere, JsonDocument.Parse(lower).RootElement.GetProperty("url").GetString());

            (_, string ownTyped) = await SendAsync(root, token, HttpMethod.Put, "/admin/settings/geometry-service",
                new { url = root.TrimEnd('/') + "/rest/services/Utilities/Geometry/GeometryServer" });
            Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(ownTyped).RootElement.GetProperty("url").ValueKind);

            (HttpStatusCode set, string setBody) = await SendAsync(root, token, HttpMethod.Put, "/admin/settings/geometry-service", new { url = Elsewhere });
            Assert.True(set == HttpStatusCode.OK, setBody);
            (_, string self) = await SendAsync(root, token, HttpMethod.Get, "/sharing/rest/portals/self?f=json");
            Assert.Equal(Elsewhere, Named(self));
        }
        finally
        {
            await SendAsync(root, token, HttpMethod.Put, "/admin/settings/geometry-service", new { url = (string?)null });
        }

        (_, string own) = await SendAsync(root, token, HttpMethod.Get, "/sharing/rest/portals/self?f=json");
        Assert.EndsWith("/rest/services/Utilities/Geometry/GeometryServer", Named(own) ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", Named(own) ?? "", StringComparison.Ordinal);
    }
}
