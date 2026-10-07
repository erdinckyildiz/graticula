using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A layer's title, set by its publisher, is what WMS, WFS and OGC API Features show — ADR-179.
/// </summary>
/// <remarks>
/// <b>Three faces composed a title each, and one setting now replaces all three.</b> A test per face would let one of
/// them be forgotten the next time a face is added; this one sets the title once and reads it back from every face,
/// then clears it and reads the composed one back, so the fixture is left as it was found.
/// </remarks>
[Collection("catalogue walk")]
public sealed class ALayerTitleIsWhatTheOgcFacesShowTests : ArcGisClient
{
    private static string? TileService => Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");

    private async Task<(HttpStatusCode Status, string Body)> SignedAsync(HttpMethod method, string path, string? json = null)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(method, new Uri(root + path));
        await AuthenticateAsync(request, root);

        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_title_is_shown_by_every_ogc_face_and_cleared_back_to_the_composed_one()
    {
        Assert.False(string.IsNullOrWhiteSpace(TileService), "GRATICULA_TEST_TILE_SERVICE is not set.");
        string layer = TileService!.Split('/')[1];
        string title = $"Parcels, titled {Guid.NewGuid():N}"[..32];

        (HttpStatusCode set, string answer) = await SignedAsync(HttpMethod.Put, $"/admin/layers/{layer}/title",
            JsonSerializer.Serialize(new { title }));
        Assert.True(set == HttpStatusCode.OK, $"{(int)set}: {answer}");

        try
        {
            foreach (string path in (string[])
                [
                    "/wms?service=WMS&request=GetCapabilities&version=1.3.0",
                    "/wfs?service=WFS&request=GetCapabilities&version=2.0.0",
                    $"/ogc/features/v1/collections/{layer}?f=json",
                ])
            {
                (_, string body) = await SignedAsync(HttpMethod.Get, path);
                Assert.True(body.Contains(title, StringComparison.Ordinal), $"{path} does not show the title.");
            }

            // And the two listings Studio's Settings › Feature layer draws the box from — the administrative one
            // and an owner's own — say what is set, or the box shows empty over a title that is there.
            foreach (string listing in (string[]) ["/admin/layers", "/content/layers"])
            {
                (_, string body) = await SignedAsync(HttpMethod.Get, listing);
                Assert.True(body.Contains(title, StringComparison.Ordinal), $"{listing} does not carry the title.");
            }

            (HttpStatusCode tooLong, _) = await SignedAsync(HttpMethod.Put, $"/admin/layers/{layer}/title",
                JsonSerializer.Serialize(new { title = new string('x', 257) }));
            Assert.Equal(HttpStatusCode.BadRequest, tooLong);
        }
        finally
        {
            await SignedAsync(HttpMethod.Put, $"/admin/layers/{layer}/title", """{"title":""}""");
        }

        (_, string cleared) = await SignedAsync(HttpMethod.Get, $"/ogc/features/v1/collections/{layer}?f=json");
        Assert.DoesNotContain(title, cleared, StringComparison.Ordinal);
        Assert.Contains($" — {layer}", JsonDocument.Parse(cleared).RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
    }
}
