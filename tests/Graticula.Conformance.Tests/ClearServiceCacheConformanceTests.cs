using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>POST /admin/services/{name}/cache/clear</c> empties a service's cached tiles, and the read-back says so.
/// </summary>
/// <remarks>
/// <b>2026-09-30.</b> A publisher had no way to clear their own service's tiles; the only control that did
/// was Server's <i>Forget remembered shape</i>, admin-only and named for something else. ArcGIS offers
/// <i>Rebuild cache</i> on the item.
/// </remarks>
public sealed class ClearServiceCacheConformanceTests : ArcGisClient
{
    [Fact]
    public async Task Clearing_a_service_removes_the_tiles_it_had_cached()
    {
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE") ?? "hosted/ci_parcels";
        int cut = service.LastIndexOf('/');
        string bare = cut < 0 ? service : service[(cut + 1)..];
        string at = $"/admin/services/{Uri.EscapeDataString(bare)}/cache";

        // One tile asked for, so there is something to clear.
        await StatusOfAsync($"/rest/services/{service}/VectorTileServer/tile/0/0/0.pbf");

        (int status, string body) = await AdminAsync(HttpMethod.Post, $"{at}/clear{FolderQuery(service)}");

        Assert.True(status == 200, $"Clearing the cache answered {status}: {body}");

        // <b>At least the tile just asked for</b> — not "nothing is left afterwards", which another class
        // drawing the same service in parallel would make untrue without anything being wrong.
        int cleared = JsonDocument.Parse(body).RootElement.GetProperty("cleared").GetInt32();

        Assert.True(cleared >= 1, $"The tile asked for a moment ago was not among what was cleared: {body}");
    }

    [Fact]
    public async Task Clearing_a_service_that_is_not_there_is_not_found()
    {
        (int status, string body) = await AdminAsync(
            HttpMethod.Post, "/admin/services/zz_no_such_service_anywhere/cache/clear?folder=hosted");

        Assert.True(status == 404, $"Clearing a service that does not exist answered {status}: {body}");
    }
}
