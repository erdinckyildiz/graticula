using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The basemap gallery is a group, its vector tile services are basemaps a portal client finds the way the Maps SDK
/// looks for them, and the default is one of them — ADR-184.
/// </summary>
/// <remarks>
/// <para>
/// <b>The SDK's own path is what is walked</b>: <c>portals/self</c>'s <c>basemapGalleryGroupQuery</c> to
/// <c>community/groups</c>, its first group to <c>content/groups/{id}/search?q=type:"Web Map"</c>, and each result's
/// <c>data</c> for its <c>baseMap</c> — read from <c>esri/portal/Portal.js</c>, 4.31. Signed out, because a
/// showcase's visitors are.
/// </para>
/// <para>
/// <b>In the catalogue-walk collection, because it publishes</b> (D-185), and the gallery is put back in
/// <c>finally</c>: it is every map's, as the ground is.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class TheBasemapGalleryIsAGroupTests : ArcGisClient
{
    private const string Open = "zz_gallery_open";
    private const string Closed = "zz_gallery_closed";
    private const string Group = "zz_basemap_gallery";

    private static JsonElement Root(string body) => JsonDocument.Parse(body).RootElement;

    [Fact]
    public async Task A_vector_tile_service_shared_with_the_gallery_is_a_basemap_the_SDK_finds_and_can_be_the_default()
    {
        await RequireServerAsync();

        (int read, string beforeBody) = await AdminAsync(HttpMethod.Get, "/admin/settings/basemaps");
        Assert.True(read == 200, $"reading the gallery: {read} {beforeBody}");
        JsonElement before = Root(beforeBody);
        string? galleryBefore = before.GetProperty("gallery").ValueKind == JsonValueKind.Object
            ? before.GetProperty("gallery").GetProperty("id").GetString()
            : null;
        string? defaultBefore = before.GetProperty("default").ValueKind == JsonValueKind.String
            ? before.GetProperty("default").GetString()
            : null;

        try
        {
            (int open, string openBody) = await PublishOneAsync(Open, Open, sharing: "public", skip: 0);
            Assert.True(open is 200 or 201, $"publishing {Open}: {open} {openBody}");
            (int closed, string closedBody) = await PublishOneAsync(Closed, Closed, sharing: "private", skip: 1);
            Assert.True(closed is 200 or 201, $"publishing {Closed}: {closed} {closedBody}");

            (int made, string madeBody) = await AdminAsync(HttpMethod.Post, "/admin/groups", JsonSerializer.Serialize(new { name = Group }));
            Assert.True(made is 200 or 201, $"making the group: {made} {madeBody}");

            foreach (string name in new[] { Open, Closed })
            {
                (int shared, string sharedBody) = await AdminAsync(HttpMethod.Put, $"/admin/groups/{Group}/items/{name}");
                Assert.True(shared == 200, $"sharing {name} into the gallery: {shared} {sharedBody}");
            }

            // ---- the gallery chosen; a default that is not in it is refused ----
            (int refused, string why) = await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps",
                JsonSerializer.Serialize(new { gallery = Group, @default = "0123456789abcdef0123456789abcdef" }));
            Assert.Equal(400, refused);
            Assert.Contains("not in the gallery", why, StringComparison.Ordinal);

            (int set, string setBody) = await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps",
                JsonSerializer.Serialize(new { gallery = Group, @default = (string?)null }));
            Assert.True(set == 200, $"choosing the gallery: {set} {setBody}");

            // The administrator sees both entries.
            string[] titles = [.. Root(setBody).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("title").GetString()!)];
            Assert.Contains(Open, titles);
            Assert.Contains(Closed, titles);

            // ---- the SDK's path, signed out ----
            (HttpStatusCode selfStatus, string self) = await AnonymousAsync("/sharing/rest/portals/self");
            Assert.Equal(HttpStatusCode.OK, selfStatus);
            string query = Root(self).GetProperty("basemapGalleryGroupQuery").GetString()!;
            Assert.False(string.IsNullOrEmpty(query), "portals/self names no basemap gallery.");

            (_, string groups) = await AnonymousAsync($"/sharing/rest/community/groups?q={Uri.EscapeDataString(query)}&num=1");
            JsonElement found = Root(groups).GetProperty("results");
            Assert.True(found.GetArrayLength() == 1, $"A signed-out caller cannot find the gallery group by '{query}': {groups}");
            string groupId = found[0].GetProperty("id").GetString()!;

            // The group's own content search, which is where the SDK asks — a 404 there was the first thing the real
            // gallery widget found.
            (_, string items) = await AnonymousAsync(
                $"/sharing/rest/content/groups/{groupId}/search?q={Uri.EscapeDataString("type:\"Web Map\" -type:\"Web Application\"")}&num=100&sortField=name&sortOrder=desc");
            JsonElement[] entries = [.. Root(items).GetProperty("results").EnumerateArray()];

            // The public one, as a Web Map; the private one not to a stranger.
            JsonElement entry = Assert.Single(entries, e => e.GetProperty("title").GetString() == Open);
            Assert.Equal("Web Map", entry.GetProperty("type").GetString());
            Assert.DoesNotContain(entries, e => e.GetProperty("title").GetString() == Closed);
            string entryId = entry.GetProperty("id").GetString()!;

            (_, string data) = await AnonymousAsync($"/sharing/rest/content/items/{entryId}/data");
            JsonElement layer = Root(data).GetProperty("baseMap").GetProperty("baseMapLayers")[0];
            Assert.Equal("VectorTileLayer", layer.GetProperty("layerType").GetString());
            Assert.EndsWith($"/{Open}/VectorTileServer/resources/styles/root.json", layer.GetProperty("styleUrl").GetString(), StringComparison.Ordinal);

            // The item itself, as the gallery's thumbnail and title come from it.
            (HttpStatusCode itemStatus, string item) = await AnonymousAsync($"/sharing/rest/content/items/{entryId}");
            Assert.Equal(HttpStatusCode.OK, itemStatus);
            Assert.Equal("Web Map", Root(item).GetProperty("type").GetString());

            // An ordinary search does not list every gallery service twice.
            (_, string everything) = await AnonymousAsync($"/sharing/rest/search?q={Uri.EscapeDataString(Open)}&num=100");
            Assert.DoesNotContain(Root(everything).GetProperty("results").EnumerateArray(), e => e.GetProperty("id").GetString() == entryId);

            // ---- the default: the entry, then OpenStreetMap even with the entry there ----
            (int chose, string choseBody) = await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps",
                JsonSerializer.Serialize(new { gallery = Group, @default = entryId }));
            Assert.True(chose == 200, $"choosing the default: {chose} {choseBody}");

            (_, string withDefault) = await AnonymousAsync("/sharing/rest/portals/self");
            JsonElement basemap = Root(withDefault).GetProperty("defaultBasemap");
            Assert.Equal(Open, basemap.GetProperty("baseMapLayers")[0].GetProperty("id").GetString());

            (int osm, _) = await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps",
                JsonSerializer.Serialize(new { gallery = Group, @default = "osm" }));
            Assert.Equal(200, osm);

            (_, string onOsm) = await AnonymousAsync("/sharing/rest/portals/self");
            Assert.Equal("OpenStreetMap",
                Root(onOsm).GetProperty("defaultBasemap").GetProperty("baseMapLayers")[0].GetProperty("layerType").GetString());
        }
        finally
        {
            await AdminAsync(HttpMethod.Put, "/admin/settings/basemaps",
                JsonSerializer.Serialize(new { gallery = galleryBefore, @default = defaultBefore }));
            await AdminAsync(HttpMethod.Delete, $"/admin/groups/{Group}");
            await UnpublishAsync(Open, Open);
            await UnpublishAsync(Closed, Closed);
        }
    }
}
