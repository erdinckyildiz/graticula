using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Two defects the OGC API Styles and Records work found in faces it did not own, 2026-10-06.
/// </summary>
/// <remarks>
/// <para>
/// <b>A vector tile service's WMTS switch did nothing.</b> <c>/admin/services/{name}/ogc</c> accepted <c>WMTS</c> for
/// any service, and only the image-service WMTS read it: the vector WMTS lists through <c>TileFaces.Serves</c>, which it
/// shares with OGC API Tiles, and nothing there asked. An owner who turned WMTS off was told it was off.
/// </para>
/// <para>
/// <b>OGC API Features refused <c>?token=</c></b> as an unknown parameter, so a client that signs in through the query
/// string — the way ArcGIS clients and a pasted URL do — could not read a private collection there at all.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AWmtsSwitchAndAQueryTokenAreHonouredTests : ArcGisClient
{
    private static string? TileService => Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");

    private async Task<(HttpStatusCode Status, string Body)> SignedAsync(HttpMethod method, string root, string path, string? json = null)
    {
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
    public async Task TurningWmtsOffTakesAVectorServiceOffWmtsOnly()
    {
        Assert.False(string.IsNullOrWhiteSpace(TileService),
            "GRATICULA_TEST_TILE_SERVICE is not set, so this FAILS rather than skips: it borrows that service's switch.");

        string root = await RequireServerAsync();
        string folder = TileService!.Split('/')[0];
        string name = TileService.Split('/')[1];
        string id = $"{folder}.{name}";
        string switchPath = $"/admin/services/{name}/ogc?folder={folder}";

        (_, string before) = await SignedAsync(HttpMethod.Get, root, "/wmts?SERVICE=WMTS&REQUEST=GetCapabilities");
        Assert.Contains($">{id}<", before, StringComparison.Ordinal);

        try
        {
            (HttpStatusCode off, string why) = await SignedAsync(HttpMethod.Put, root, switchPath, """{"off":["WMTS"]}""");
            Assert.True(off == HttpStatusCode.OK, $"{(int)off}: {why}");

            (_, string without) = await SignedAsync(HttpMethod.Get, root, "/wmts?SERVICE=WMTS&REQUEST=GetCapabilities");
            Assert.DoesNotContain($">{id}<", without, StringComparison.Ordinal);

            (HttpStatusCode tile, string refusal) = await SignedAsync(HttpMethod.Get, root,
                $"/wmts?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0&LAYER={id}&STYLE=default&FORMAT=application/vnd.mapbox-vector-tile"
                + "&TILEMATRIXSET=WebMercatorQuad&TILEMATRIX=0&TILEROW=0&TILECOL=0");
            Assert.Equal(HttpStatusCode.BadRequest, tile);
            Assert.Contains("LAYER", refusal, StringComparison.Ordinal);

            // OGC API Tiles has no switch of its own and is not WMTS: the service stays there.
            (HttpStatusCode tiles, _) = await SignedAsync(HttpMethod.Get, root, $"/ogc/tiles/v1/collections/{id}?f=json");
            Assert.Equal(HttpStatusCode.OK, tiles);
        }
        finally
        {
            await SignedAsync(HttpMethod.Put, root, switchPath, """{"off":[]}""");
        }

        (_, string back) = await SignedAsync(HttpMethod.Get, root, "/wmts?SERVICE=WMTS&REQUEST=GetCapabilities");
        Assert.Contains($">{id}<", back, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OgcFeaturesReadsTheTokenInTheQuery()
    {
        Assert.False(string.IsNullOrWhiteSpace(TileService), "GRATICULA_TEST_TILE_SERVICE is not set.");

        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(string.IsNullOrEmpty(token), "No account is configured to sign in with.");

        string layer = TileService!.Split('/')[1];

        using HttpResponseMessage response = await Http.GetAsync(
            new Uri($"{root}/ogc/features/v1/collections/{layer}/items?limit=1&f=json&token={Uri.EscapeDataString(token!)}"));

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
