using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Console.Tests;

/// <summary>
/// The basemap gallery is offered by Map Viewer, a new map starts on its default, and the Settings screen shows
/// what is chosen — ADR-184.
/// </summary>
/// <remarks>
/// <b>Set through the real API</b>, for <see cref="TheServersGroundIsDrawnTests"/>' reason: this suite never lets the
/// screen's writes reach the server, and what is asserted is what the pages do with a gallery that is really stored.
/// In the ground's collection, because the default basemap is every map's.
/// </remarks>
[Collection("server ground")]
public sealed class WebMapOffersTheBasemapGalleryTests : ConsoleTest
{
    private const string Group = "zz_console_gallery";

    [Fact]
    public async Task A_new_map_starts_on_the_gallerys_default_and_the_list_offers_the_gallery()
    {
        (string token, string cookie) = await SignInAsync();
        string service = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE") ?? "hosted/ci_parcels";
        int slash = service.IndexOf('/', StringComparison.Ordinal);
        string folder = service[..slash];
        string name = service[(slash + 1)..];

        try
        {
            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = Group }));
            Assert.True(made is 200 or 201, $"Making the group answered {made}: {madeBody}");

            (int shared, string sharedBody) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{Group}/items/{name}?folder={folder}");
            Assert.True(shared == 200, $"Sharing {service} into the gallery answered {shared}: {sharedBody}");

            (int set, string setBody) = await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps",
                JsonSerializer.Serialize(new { gallery = Group, @default = (string?)null }));
            Assert.True(set == 200, $"Choosing the gallery answered {set}: {setBody}");

            using JsonDocument described = JsonDocument.Parse(setBody);
            string groupId = described.RootElement.GetProperty("gallery").GetProperty("id").GetString()!;
            string entry = described.RootElement.GetProperty("entries").EnumerateArray()
                .First(e => e.GetProperty("service").GetString() == service).GetProperty("id").GetString()!;

            (int chose, string choseBody) = await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps",
                JsonSerializer.Serialize(new { gallery = Group, @default = entry }));
            Assert.True(chose == 200, $"Choosing the default answered {chose}: {choseBody}");

            // ---- Map Viewer: a new map on the default, and the list naming it as the gallery's ----
            await OpenAsync("/studio/webmap.html", token, cookie);
            await WaitForAsync(
                $"typeof wmState === 'object' && !!wmState.doc && wmGallery.some(e => e.id === '{entry}')",
                "Map Viewer never read the basemap gallery.");

            string[] basemap = await Browser.EvaluateAsync<string[]>(
                "(wmState.doc.baseMap.baseMapLayers || []).map(l => l.layerType + ' ' + (l.id || ''))") ?? [];
            Assert.True(basemap.SequenceEqual([$"VectorTileLayer {service}"]),
                $"A new map's basemap is [{string.Join(", ", basemap)}], not the gallery's default {service}.");

            await Browser.EvaluateAsync<bool>("(() => { wmDrawBaseMapChoices(); return true; })()");
            Assert.Equal($"gallery:{entry}", await Browser.EvaluateAsync<string>("document.getElementById('mapBasemap').value"));

            // Choosing OpenStreetMap from the list draws it instead.
            await Browser.EvaluateAsync<bool>("""
                (() => { const s = document.getElementById('mapBasemap'); s.value = 'osm';
                  s.dispatchEvent(new Event('change')); return true; })()
                """);
            Assert.True(await Browser.EvaluateAsync<bool>("wmBase.getVisible() && !wmGround.getVisible()"),
                "OpenStreetMap chosen from the list, but the page still draws the gallery's basemap.");

            // ---- Settings: the gallery and the default as stored ----
            await OpenAsync("/studio/#/orgsettings", token);
            await WaitForAsync(
                $"document.getElementById('basemapGallery')?.value === '{groupId}' && document.querySelector('#basemapDefault input:checked')?.value === '{entry}'",
                "The Settings screen does not show the stored gallery and default.");
            Assert.Contains(name, await Browser.EvaluateAsync<string>("document.getElementById('basemapEntries').textContent") ?? "",
                StringComparison.Ordinal);

            // The ground card says it is not what new maps start on — the design review found the two cards contradicting.
            Assert.False(await Browser.EvaluateAsync<bool>("document.getElementById('groundInUse').hidden"),
                "With a gallery default saved, the Map ground card does not say it is not in use.");

            // <b>No gallery can be chosen</b> — the design review found the list jumping back to the saved group.
            await Browser.EvaluateAsync<bool>("""
                (() => { const s = document.getElementById('basemapGallery'); s.value = '';
                  s.dispatchEvent(new Event('change')); return true; })()
                """);
            await WaitForAsync(
                "document.getElementById('basemapEntries').textContent.startsWith('No gallery')",
                "Choosing No gallery did not preview it.");
            Assert.Equal("", await Browser.EvaluateAsync<string>("document.getElementById('basemapGallery').value"));

            NothingWentWrong(await PageErrorsAsync());
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps", JsonSerializer.Serialize(new { gallery = (string?)null, @default = (string?)null }));
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Group}");
        }
    }
}
