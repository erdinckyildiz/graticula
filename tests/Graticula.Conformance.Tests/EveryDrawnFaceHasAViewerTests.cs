using System;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A browser that opens a drawable face's address gets a page, and the page's viewers open that face.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-10-10, when the owner clicked VectorTileServer in the services directory and got the raw
/// document.</b> The FeatureServer and MapServer links beside it opened a page with <em>View in</em> links; this face
/// answered JSON whatever the browser asked for, so there was no way from the directory to look at its tiles.
/// </para>
/// <para>
/// <b>And the MapServer page's Map Viewer link opened the features.</b> The viewer tried FeatureServer first, so the
/// link on the map face showed the other face — a link that works and goes to the wrong place, which no test of
/// either page alone would see.
/// </para>
/// </remarks>
public sealed class EveryDrawnFaceHasAViewerTests : ArcGisClient
{
    private static string? TileService => Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");

    [Fact]
    public async Task A_vector_tile_service_is_a_page_whose_viewers_open_its_tiles()
    {
        Assert.False(string.IsNullOrWhiteSpace(TileService), "GRATICULA_TEST_TILE_SERVICE is not set.");

        string page = await GetHtmlAsync($"/rest/services/{TileService}/VectorTileServer");
        string named = Uri.EscapeDataString(TileService!);

        Assert.True(
            page.Contains("<html", StringComparison.OrdinalIgnoreCase),
            "A browser asking for the VectorTileServer page was answered something other than a page — the raw "
            + "document the directory's link opened on 2026-10-10.");

        Assert.Contains($"/studio/webmap.html?face=vectortileserver&amp;service={named}", page, StringComparison.Ordinal);
        Assert.Contains($"/studio/map.html?face=vectortileserver&amp;service={named}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_map_service_pages_map_viewer_opens_the_map_face()
    {
        Assert.False(string.IsNullOrWhiteSpace(TileService), "GRATICULA_TEST_TILE_SERVICE is not set.");

        string page = await GetHtmlAsync($"/rest/services/{TileService}/MapServer");

        Assert.Contains(
            $"/studio/webmap.html?face=mapserver&amp;service={Uri.EscapeDataString(TileService!)}",
            page,
            StringComparison.Ordinal);
    }
}
