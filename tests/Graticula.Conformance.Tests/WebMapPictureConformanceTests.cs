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
/// ADR-119: a web map's description and picture — set by whoever may change it, read with the map and by the portal
/// face, and a picture that is not a PNG refused.
/// </summary>
[Collection("catalogue walk")]
public sealed class WebMapPictureConformanceTests : ArcGisClient
{
    /// <summary>A 1 × 1 PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    private async Task<(HttpStatusCode Status, string Body, byte[] Bytes)> SendAsync(
        string root, string token, HttpMethod method, string path, HttpContent? content = null)
    {
        using HttpRequestMessage request = new(method, $"{root}{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        return (response.StatusCode, Encoding.UTF8.GetString(bytes), bytes);
    }

    [Fact]
    public async Task A_maps_description_and_picture_are_kept_and_said()
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);

        Assert.False(token is null, "No administrator credential; set the suite's user and password.");

        string document = JsonSerializer.Serialize(new
        {
            title = "ADR-119 conformance",
            sharing = "private",
            document = new { operationalLayers = Array.Empty<object>(), baseMap = new { baseMapLayers = Array.Empty<object>(), title = "None" }, version = "2.31" },
        });

        (HttpStatusCode made, string madeBody, _) = await SendAsync(root, token!, HttpMethod.Post, "/content/webmaps",
            new StringContent(document, Encoding.UTF8, "application/json"));
        Assert.True(made == HttpStatusCode.Created, $"Saving the map answered {(int)made}: {madeBody}");
        string id = JsonDocument.Parse(madeBody).RootElement.GetProperty("id").GetString()!;

        try
        {
            (HttpStatusCode described, string describedBody, _) = await SendAsync(root, token!, HttpMethod.Put,
                $"/content/webmaps/{id}/description",
                new StringContent("{\"description\":\"Inspections of 2026, from the field app.\"}", Encoding.UTF8, "application/json"));
            Assert.True(described == HttpStatusCode.OK, $"Setting the description answered {(int)described}: {describedBody}");

            ByteArrayContent notPng = new(Encoding.UTF8.GetBytes("<svg/>"));
            notPng.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            (HttpStatusCode refused, _, _) = await SendAsync(root, token!, HttpMethod.Put, $"/content/webmaps/{id}/thumbnail", notPng);
            Assert.Equal(HttpStatusCode.BadRequest, refused);

            ByteArrayContent png = new(Png);
            png.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            (HttpStatusCode pictured, string picturedBody, _) = await SendAsync(root, token!, HttpMethod.Put, $"/content/webmaps/{id}/thumbnail", png);
            Assert.True(pictured == HttpStatusCode.OK, $"Setting the picture answered {(int)pictured}: {picturedBody}");

            (_, string mapBody, _) = await SendAsync(root, token!, HttpMethod.Get, $"/content/webmaps/{id}");
            JsonElement map = JsonDocument.Parse(mapBody).RootElement;
            Assert.Equal("Inspections of 2026, from the field app.", map.GetProperty("description").GetString());
            string picture = map.GetProperty("thumbnail").GetString()!;

            (HttpStatusCode got, _, byte[] bytes) = await SendAsync(root, token!, HttpMethod.Get, picture);
            Assert.Equal(HttpStatusCode.OK, got);
            Assert.Equal(Png, bytes);

            // The portal face says it and serves it.
            (_, string itemBody, _) = await SendAsync(root, token!, HttpMethod.Get, $"/sharing/rest/content/items/{id}?f=json");
            JsonElement item = JsonDocument.Parse(itemBody).RootElement;
            Assert.Equal("Inspections of 2026, from the field app.", item.GetProperty("description").GetString());
            string file = item.GetProperty("thumbnail").GetString()!;

            (HttpStatusCode portal, _, byte[] portalBytes) = await SendAsync(root, token!, HttpMethod.Get,
                $"/sharing/rest/content/items/{id}/info/{file}");
            Assert.Equal(HttpStatusCode.OK, portal);
            Assert.Equal(Png, portalBytes);
        }
        finally
        {
            await SendAsync(root, token!, HttpMethod.Delete, $"/content/webmaps/{id}");
        }
    }
}
