using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Clearing a service's tiles for an area and a level empties those and keeps the rest.
/// </summary>
/// <remarks>
/// <b>2026-10-16, ADR-017 §3.1 step 4 — *fix it, scoped*.</b> <c>POST /admin/services/{name}/cache/clear</c> took no
/// body and emptied the whole service; with a seed's shape of body it now empties only the tiles at those levels that
/// meet that area, through the cache's purge by address (ADR-186). Asserted by the header, as the edit test is.
/// </remarks>
[Collection("catalogue walk")]
public sealed class AScopedClearEmptiesOnlyItsAreaTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_EDITABLE";
    private const int Zoom = 14;
    private const double Half = 20037508.342789244;

    [Fact]
    public async Task A_scoped_clear_empties_the_tile_it_names_and_keeps_one_across_the_world()
    {
        string root = await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(string.IsNullOrWhiteSpace(configured), $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        string service = configured!.Trim('/');
        JsonElement layer = await GetJsonAsync($"/rest/services/{service}/FeatureServer/0");
        JsonElement extent = layer.GetProperty("extent");
        int srid = extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32();
        double x = (extent.GetProperty("xmin").GetDouble() + extent.GetProperty("xmax").GetDouble()) / 2;
        double y = (extent.GetProperty("ymin").GetDouble() + extent.GetProperty("ymax").GetDouble()) / 2;

        (double mx, double my) = srid switch
        {
            3857 or 102100 => (x, y),
            4326 => (x * Half / 180, Math.Log(Math.Tan((90 + y) * Math.PI / 360)) * Half / Math.PI),
            _ => throw new InvalidOperationException($"The layer is in {srid}; this test places a tile in 3857 or 4326 only."),
        };

        int n = 1 << Zoom;
        int column = (int)((mx + Half) / (2 * Half) * n);
        int row = (int)((Half - my) / (2 * Half) * n);
        double size = 2 * Half / n;

        Uri near = Tile(root, service, row, column);
        Uri far = Tile(root, service, row, (column + n / 2) % n);

        await StateAsync(root, near);
        await StateAsync(root, far);
        Assert.Equal("HIT", await StateAsync(root, near));
        Assert.Equal("HIT", await StateAsync(root, far));

        // The near tile's own box, a tenth inside each edge so that it touches no neighbour.
        double left = -Half + column * size, top = Half - row * size;
        string body = string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"minZoom\":{Zoom},\"maxZoom\":{Zoom},\"extent\":{{\"xmin\":{left + size / 10},\"ymin\":{top - size + size / 10},"
            + $"\"xmax\":{left + size - size / 10},\"ymax\":{top - size / 10},\"spatialReference\":{{\"wkid\":3857}}}}}}");

        string[] parts = service.Split('/');
        string path = parts.Length == 2
            ? $"/admin/services/{parts[1]}/cache/clear?folder={parts[0]}"
            : $"/admin/services/{parts[0]}/cache/clear";

        (int status, string answer) = await AdminAsync(HttpMethod.Post, path, body);
        Assert.True(status == 200, $"The scoped clear answered {status}: {answer}");
        Assert.True(JsonDocument.Parse(answer).RootElement.GetProperty("cleared").GetInt32() >= 1, answer);

        Assert.NotEqual("HIT", await StateAsync(root, near));
        Assert.Equal("HIT", await StateAsync(root, far));
    }

    private static Uri Tile(string root, string service, int row, int column) => new(string.Create(
        CultureInfo.InvariantCulture, $"{root}/rest/services/{service}/VectorTileServer/tile/{Zoom}/{row}/{column}.pbf"));

    private static async Task<string> StateAsync(string root, Uri tile)
    {
        using HttpClient http = new(new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        });
        using HttpRequestMessage request = new(HttpMethod.Get, tile);
        await AuthenticateAsync(request, root);
        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent, $"The tile answered {(int)response.StatusCode}.");

        return response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? values) ? values.Single() : "(none)";
    }
}
