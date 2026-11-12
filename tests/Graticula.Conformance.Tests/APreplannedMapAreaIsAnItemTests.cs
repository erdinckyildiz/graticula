using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-288 §11a against a running server: a map's owner packages an area of it, the area and its package are portal items
/// of the types and with the relationships Enterprise gives them, the package's file is streamed, and the items are seen
/// exactly when the map is. A refresh puts the old replica and file away and a delete takes the rest.
/// </summary>
/// <remarks>
/// Written 2026-11-12 from a hosted layer defined here, so the fixture's own services are not given a sync switch. Whether
/// ArcGIS Field Maps lists and downloads such an area is ADR-288 condition 3's, and is not measured by this.
/// </remarks>
[Collection("catalogue walk")]
public sealed class APreplannedMapAreaIsAnItemTests : ArcGisClient
{
    private const string Items = "/sharing/rest/content/items/";

    private async Task<(HttpStatusCode Status, JsonElement Body)> JsonAsync(
        HttpMethod method, string path, string? token, object? body = null)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(method, root + path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 && text[0] is '{' or '[' ? JsonDocument.Parse(text).RootElement.Clone() : default);
    }

    private async Task<(HttpStatusCode Status, byte[] Bytes, HttpResponseMessage Headers)> BytesAsync(string path, string? token, string? range = null)
    {
        string root = await RequireServerAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, root + path);

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (range is not null)
        {
            request.Headers.TryAddWithoutValidation("Range", range);
        }

        HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response);
    }

    private static bool Sees(JsonElement item, string id) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out JsonElement found) && found.GetString() == id;

    private async Task<List<string>> RelatedAsync(string item, string type, string direction, string? token)
    {
        (_, JsonElement body) = await JsonAsync(HttpMethod.Get, $"{Items}{item}/relatedItems?f=json&relationshipType={type}&direction={direction}", token);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("relatedItems", out JsonElement related)
            ? [.. related.EnumerateArray().Select(r => r.GetProperty("id").GetString()!)]
            : [];
    }

    private async Task<bool> ReplicaRegisteredAsync(string service, string replica, string token)
    {
        (HttpStatusCode status, JsonElement body) = await JsonAsync(HttpMethod.Get, $"{service}/replicas/{replica}?f=json", token);
        return status == HttpStatusCode.OK && body.ValueKind == JsonValueKind.Object && !body.TryGetProperty("error", out _);
    }

    [Fact]
    public async Task An_area_is_packaged_listed_streamed_shown_to_whoever_sees_the_map_and_put_away_with_it()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string name = "zz_area_" + Guid.NewGuid().ToString("N")[..8];
        string member = "zzarea" + Guid.NewGuid().ToString("N")[..6];

        (HttpStatusCode defined, JsonElement design) = await JsonAsync(HttpMethod.Post, "/admin/hosted/define", token, new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "text", nullable = true } },
            sharing = "private",
        });
        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}.");

        string[] parts = design.GetProperty("services").GetProperty("feature").GetString()!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string service = $"/rest/services/{parts[2]}/{parts[3]}/FeatureServer";
        string mapId = string.Empty;

        try
        {
            using (HttpRequestMessage enable = new(HttpMethod.Post, $"{root}/rest/admin/services/{parts[2]}/{parts[3]}/FeatureServer/updateDefinition")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["updateDefinition"] = """{"syncEnabled":true}""", ["f"] = "json" }),
            })
            {
                enable.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage enabled = await Http.SendAsync(enable);
                Assert.True(enabled.StatusCode == HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
            }

            // A private map holding the layer.
            object document = new
            {
                operationalLayers = new[] { new { id = "l0", layerType = "ArcGISFeatureLayer", url = $"{root}{service}/0", title = "Points" } },
                baseMap = new { title = "none", baseMapLayers = Array.Empty<object>() },
                version = "2.31",
            };
            (HttpStatusCode made, JsonElement map) = await JsonAsync(
                HttpMethod.Post, "/content/webmaps", token, new { title = "Area map " + name, sharing = "private", document });
            Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"Saving a map answered {(int)made}.");
            mapId = map.GetProperty("id").GetString()!;

            object extent = new { xmin = -10, ymin = -10, xmax = 10, ymax = 10, spatialReference = new { wkid = 4326 } };

            // Refusals before anything is made.
            Assert.Equal(HttpStatusCode.BadRequest, (await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", token, new { title = "", extent })).Status);
            Assert.Equal(HttpStatusCode.BadRequest, (await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", token,
                new { title = "Inside out", extent = new { xmin = 10, ymin = -10, xmax = -10, ymax = 10, spatialReference = new { wkid = 4326 } } })).Status);
            Assert.NotEqual(HttpStatusCode.Created, (await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", null, new { title = "Anon", extent })).Status);

            // The area and its one package.
            (HttpStatusCode created, JsonElement area) = await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", token, new { title = "Downtown", extent });
            Assert.True(created == HttpStatusCode.Created, area.ToString());
            string areaId = area.GetProperty("id").GetString()!;
            JsonElement package = Assert.Single(area.GetProperty("packages").EnumerateArray());
            string packageId = package.GetProperty("id").GetString()!;
            string replica = package.GetProperty("replicaId").GetString()!;
            Assert.True(package.GetProperty("replicaRegistered").GetBoolean());
            Assert.True(await ReplicaRegisteredAsync(service, replica, token));

            // The items.
            (_, JsonElement areaItem) = await JsonAsync(HttpMethod.Get, $"{Items}{areaId}?f=json", token);
            Assert.Equal("Map Area", areaItem.GetProperty("type").GetString());
            Assert.Equal(["Map", "Map Area"], areaItem.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()));
            Assert.Equal("complete", areaItem.GetProperty("properties").GetProperty("status").GetString());
            Assert.Equal(10, areaItem.GetProperty("properties").GetProperty("extent").GetProperty("xmax").GetDouble());

            (_, JsonElement packageItem) = await JsonAsync(HttpMethod.Get, $"{Items}{packageId}?f=json", token);
            Assert.Equal("SQLite Geodatabase", packageItem.GetProperty("type").GetString());
            string[] keywords = [.. packageItem.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()!)];
            Assert.Contains("MapAreaPackage", keywords);
            Assert.Contains("Syncable", keywords);
            Assert.Contains(keywords, k => k.StartsWith("ReferenceURL#", StringComparison.Ordinal) && k.EndsWith("/FeatureServer", StringComparison.Ordinal));
            Assert.EndsWith(".geodatabase", packageItem.GetProperty("name").GetString(), StringComparison.Ordinal);
            Assert.Equal(package.GetProperty("size").GetInt64(), packageItem.GetProperty("size").GetInt64());

            JsonElement properties = packageItem.GetProperty("properties");
            foreach (string key in new[]
            {
                "url", "layers", "extent", "layerQueries", "replicaID", "layerServerGens", "returnAttachments",
                "attachmentsSyncDirection", "syncModel", "useSyncForRefresh", "hasUtilityNetworkLayer",
            })
            {
                Assert.True(properties.TryGetProperty(key, out _), $"The package's properties have no '{key}'.");
            }

            // Related, in both directions, with the web map as an origin.
            Assert.Equal([areaId], await RelatedAsync(mapId, "Map2Area", "forward", token));
            Assert.Equal([mapId], await RelatedAsync(areaId, "Map2Area", "reverse", token));
            Assert.Equal([packageId], await RelatedAsync(areaId, "Area2Package", "forward", token));
            Assert.Equal([areaId], await RelatedAsync(packageId, "Area2Package", "reverse", token));

            // The Map Area's data is an empty object (D-408); the package's is its file, whole and by range.
            (_, JsonElement areaData) = await JsonAsync(HttpMethod.Get, $"{Items}{areaId}/data?f=json", token);
            Assert.Equal(JsonValueKind.Object, areaData.ValueKind);
            Assert.Empty(areaData.EnumerateObject());

            (HttpStatusCode whole, byte[] file, HttpResponseMessage headers) = await BytesAsync($"{Items}{packageId}/data", token);
            Assert.Equal(HttpStatusCode.OK, whole);
            Assert.Equal("SQLite format 3\0"u8.ToArray(), file[..16]);
            Assert.Equal(package.GetProperty("size").GetInt64(), file.Length);
            Assert.Equal(package.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(file)));
            Assert.Contains("bytes", headers.Headers.AcceptRanges);
            headers.Dispose();

            (HttpStatusCode partial, byte[] first, HttpResponseMessage ranged) = await BytesAsync($"{Items}{packageId}/data", token, "bytes=0-99");
            Assert.Equal(HttpStatusCode.PartialContent, partial);
            Assert.Equal(file[..100], first);
            ranged.Dispose();

            // Searched for, and in the owner's content.
            (_, JsonElement found) = await JsonAsync(HttpMethod.Get, "/sharing/rest/search?f=json&q=" + Uri.EscapeDataString("type:\"Map Area\""), token);
            Assert.Contains(found.GetProperty("results").EnumerateArray(), r => r.GetProperty("id").GetString() == areaId);

            // Private: nobody else sees any of it, and nobody else may add an area.
            Assert.False(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{areaId}?f=json", null)).Body, areaId));
            Assert.False(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{packageId}?f=json", null)).Body, packageId));
            (_, byte[] anonymous, HttpResponseMessage refused) = await BytesAsync($"{Items}{packageId}/data", null);
            refused.Dispose();
            Assert.False(anonymous.AsSpan().StartsWith("SQLite format 3"u8), "An anonymous caller was given a private package's file.");
            Assert.Empty(await RelatedAsync(mapId, "Map2Area", "forward", null));

            (_, JsonElement issued) = await JsonAsync(HttpMethod.Post, "/admin/members", token, new { name = member, role = "user", userType = "viewer" });
            string issuedPassword = issued.GetProperty("password").GetString()!;
            (_, JsonElement firstSignIn) = await JsonAsync(HttpMethod.Post, "/rest/auth/login", null, new { name = member, password = issuedPassword });
            string newPassword = "Area-reader-" + Guid.NewGuid().ToString("N")[..10] + "-Ok7!";
            await JsonAsync(HttpMethod.Post, "/rest/auth/password", firstSignIn.GetProperty("token").GetString(), new { currentPassword = issuedPassword, newPassword });
            (_, JsonElement signedIn) = await JsonAsync(HttpMethod.Post, "/rest/auth/login", null, new { name = member, password = newPassword });
            string reader = signedIn.GetProperty("token").GetString()!;

            Assert.False(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{areaId}?f=json", reader)).Body, areaId));
            Assert.Equal(HttpStatusCode.NotFound, (await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", reader, new { title = "No", extent })).Status);

            // Shared with the organisation: a member sees all of it and still may not change it. Public: so does anybody.
            foreach (string sharing in new[] { "organization", "public" })
            {
                (HttpStatusCode shared, _) = await JsonAsync(
                    HttpMethod.Put, $"/content/webmaps/{mapId}", token, new { title = "Area map " + name, sharing, document });
                Assert.Equal(HttpStatusCode.OK, shared);

                string? who = sharing == "organization" ? reader : null;
                Assert.True(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{areaId}?f=json", who)).Body, areaId), $"{sharing}: the area");
                Assert.True(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{packageId}?f=json", who)).Body, packageId), $"{sharing}: the package");
                Assert.Equal([areaId], await RelatedAsync(mapId, "Map2Area", "forward", who));

                (HttpStatusCode downloaded, byte[] bytes, HttpResponseMessage response) = await BytesAsync($"{Items}{packageId}/data", who);
                response.Dispose();
                Assert.Equal(HttpStatusCode.OK, downloaded);
                Assert.Equal(file.Length, bytes.Length);
            }

            Assert.Equal(HttpStatusCode.Forbidden, (await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", reader, new { title = "No", extent })).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas/{areaId}/refresh", reader)).Status);
            Assert.Equal(HttpStatusCode.Forbidden, (await JsonAsync(HttpMethod.Delete, $"/admin/maps/{mapId}/areas/{areaId}", reader)).Status);

            // A refresh keeps the package item and puts the old replica away.
            await Task.Delay(1100);
            (HttpStatusCode refreshed, JsonElement again) = await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas/{areaId}/refresh", token);
            Assert.True(refreshed == HttpStatusCode.OK, again.ToString());
            JsonElement renewed = Assert.Single(again.GetProperty("packages").EnumerateArray());
            Assert.Equal(packageId, renewed.GetProperty("id").GetString());
            Assert.NotEqual(replica, renewed.GetProperty("replicaId").GetString());
            Assert.False(await ReplicaRegisteredAsync(service, replica, token));
            Assert.True(await ReplicaRegisteredAsync(service, renewed.GetProperty("replicaId").GetString()!, token));

            // Deleting the area takes its package and replica; deleting the map takes the rest.
            Assert.Equal(HttpStatusCode.OK, (await JsonAsync(HttpMethod.Delete, $"/admin/maps/{mapId}/areas/{areaId}", token)).Status);
            Assert.False(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{areaId}?f=json", token)).Body, areaId));
            Assert.False(await ReplicaRegisteredAsync(service, renewed.GetProperty("replicaId").GetString()!, token));

            (HttpStatusCode second, JsonElement secondArea) = await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", token, new { title = "Second", extent });
            Assert.Equal(HttpStatusCode.Created, second);
            string secondReplica = secondArea.GetProperty("packages")[0].GetProperty("replicaId").GetString()!;
            Assert.Equal(HttpStatusCode.OK, (await JsonAsync(HttpMethod.Delete, $"/content/webmaps/{mapId}", token)).Status);
            mapId = string.Empty;
            Assert.False(await ReplicaRegisteredAsync(service, secondReplica, token));
            Assert.False(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{secondArea.GetProperty("id").GetString()}?f=json", token)).Body, secondArea.GetProperty("id").GetString()!));
        }
        finally
        {
            if (mapId.Length > 0)
            {
                await JsonAsync(HttpMethod.Delete, $"/content/webmaps/{mapId}", token);
            }

            await JsonAsync(HttpMethod.Delete, $"/admin/members/{member}", token);
            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token, json: null);
        }
    }

    /// <summary>
    /// ADR-288 §11a, the owner's change of 2026-11-12: a map whose basemap is a vector tile service of this server gets a tile
    /// package beside its geodatabase, related by Area2Package; a basemap layer that is not ours is named under skipped and
    /// has no item. Needs the fixture's vector tile service, <c>GRATICULA_TEST_TILE_SERVICE</c>.
    /// </summary>
    [Fact]
    public async Task A_basemap_of_ours_is_packaged_as_a_vtpk_and_one_that_is_not_ours_is_named_and_has_no_item()
    {
        string? tiles = Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE");
        Assert.False(string.IsNullOrWhiteSpace(tiles), "GRATICULA_TEST_TILE_SERVICE is not set, so this FAILS rather than skips.");

        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string name = "zz_area_" + Guid.NewGuid().ToString("N")[..8];

        (HttpStatusCode defined, JsonElement design) = await JsonAsync(HttpMethod.Post, "/admin/hosted/define", token, new
        {
            name,
            geometryType = "Point",
            fields = new[] { new { name = "label", type = "text", nullable = true } },
            sharing = "private",
        });
        Assert.True(defined == HttpStatusCode.Created, $"Defining a layer answered {(int)defined}.");

        string[] parts = design.GetProperty("services").GetProperty("feature").GetString()!.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string service = $"/rest/services/{parts[2]}/{parts[3]}/FeatureServer";
        string tileUrl = $"{root}/rest/services/{tiles}/VectorTileServer";
        string mapId = string.Empty;

        try
        {
            using (HttpRequestMessage enable = new(HttpMethod.Post, $"{root}/rest/admin/services/{parts[2]}/{parts[3]}/FeatureServer/updateDefinition")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["updateDefinition"] = """{"syncEnabled":true}""", ["f"] = "json" }),
            })
            {
                enable.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage enabled = await Http.SendAsync(enable);
                Assert.True(enabled.StatusCode == HttpStatusCode.OK, await enabled.Content.ReadAsStringAsync());
            }

            object document = new
            {
                operationalLayers = new[] { new { id = "l0", layerType = "ArcGISFeatureLayer", url = $"{root}{service}/0", title = "Points" } },
                baseMap = new
                {
                    title = "Mixed",
                    baseMapLayers = new object[]
                    {
                        new { id = "ours", layerType = "VectorTileLayer", title = "Ours", url = tileUrl },
                        new { id = "osm", layerType = "OpenStreetMap", title = "OpenStreetMap" },
                        new { id = "esri", layerType = "ArcGISTiledMapServiceLayer", title = "Imagery", url = "https://services.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer" },
                    },
                },
                version = "2.31",
            };
            (HttpStatusCode made, JsonElement map) = await JsonAsync(
                HttpMethod.Post, "/content/webmaps", token, new { title = "Area map " + name, sharing = "private", document });
            Assert.True(made is HttpStatusCode.Created or HttpStatusCode.OK, $"Saving a map answered {(int)made}.");
            mapId = map.GetProperty("id").GetString()!;

            object extent = new { xmin = 0, ymin = 0, xmax = 0.002, ymax = 0.002, spatialReference = new { wkid = 4326 } };
            (HttpStatusCode created, JsonElement area) = await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas", token, new { title = "Tiles", extent });
            Assert.True(created == HttpStatusCode.Created, area.ToString());
            string areaId = area.GetProperty("id").GetString()!;

            JsonElement[] packages = [.. area.GetProperty("packages").EnumerateArray()];
            Assert.Equal(2, packages.Length);
            JsonElement vtpk = Assert.Single(packages, p => p.GetProperty("format").GetString() == "vtpk");
            Assert.Single(packages, p => p.GetProperty("format").GetString() == "sqlite");
            Assert.Equal(JsonValueKind.Null, vtpk.GetProperty("replicaId").ValueKind);
            Assert.EndsWith(".vtpk", vtpk.GetProperty("fileName").GetString(), StringComparison.Ordinal);

            // What could not be packaged is named, and nothing pretends.
            string[] skipped = [.. area.GetProperty("skipped").EnumerateArray().Select(s => s.GetProperty("service").GetString()!)];
            Assert.Contains("OpenStreetMap", skipped);
            Assert.Contains("Imagery", skipped);
            Assert.DoesNotContain("Ours", skipped);

            string vtpkId = vtpk.GetProperty("id").GetString()!;
            (_, JsonElement item) = await JsonAsync(HttpMethod.Get, $"{Items}{vtpkId}?f=json", token);
            Assert.Equal("Vector Tile Package", item.GetProperty("type").GetString());
            string[] keywords = [.. item.GetProperty("typeKeywords").EnumerateArray().Select(k => k.GetString()!)];
            Assert.Contains("MapAreaPackage", keywords);
            Assert.Contains("Package", keywords);
            Assert.Contains("ReferenceURL#" + tileUrl, keywords);
            Assert.EndsWith(".vtpk", item.GetProperty("name").GetString(), StringComparison.Ordinal);

            // Related by Area2Package with the geodatabase, and streamed: a VTPK is a zip.
            List<string> related = await RelatedAsync(areaId, "Area2Package", "forward", token);
            Assert.Equal(2, related.Count);
            Assert.Contains(vtpkId, related);

            (HttpStatusCode downloaded, byte[] file, HttpResponseMessage headers) = await BytesAsync($"{Items}{vtpkId}/data", token);
            headers.Dispose();
            Assert.Equal(HttpStatusCode.OK, downloaded);
            Assert.Equal(vtpk.GetProperty("size").GetInt64(), file.Length);
            Assert.Equal("PK"u8.ToArray(), file[..2]);
            Assert.Equal(vtpk.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(file)));

            // A refresh writes both again and keeps both items.
            await Task.Delay(1100);
            (HttpStatusCode refreshed, JsonElement again) = await JsonAsync(HttpMethod.Post, $"/admin/maps/{mapId}/areas/{areaId}/refresh", token);
            Assert.True(refreshed == HttpStatusCode.OK, again.ToString());
            Assert.Equal(2, again.GetProperty("packages").GetArrayLength());
            Assert.Contains(again.GetProperty("packages").EnumerateArray(), p => p.GetProperty("id").GetString() == vtpkId);

            // Deleting the area takes the tile package's item and file with the rest.
            Assert.Equal(HttpStatusCode.OK, (await JsonAsync(HttpMethod.Delete, $"/admin/maps/{mapId}/areas/{areaId}", token)).Status);
            Assert.False(Sees((await JsonAsync(HttpMethod.Get, $"{Items}{vtpkId}?f=json", token)).Body, vtpkId));
            (HttpStatusCode gone, _, HttpResponseMessage goneHeaders) = await BytesAsync($"{Items}{vtpkId}/data", token);
            goneHeaders.Dispose();
            Assert.NotEqual(HttpStatusCode.OK, gone);
        }
        finally
        {
            if (mapId.Length > 0)
            {
                await JsonAsync(HttpMethod.Delete, $"/content/webmaps/{mapId}", token);
            }

            await RequestAsync(HttpMethod.Delete, $"{root}/admin/featureservices/{parts[3]}?folder={parts[2]}&drop=true", token, json: null);
        }
    }
}
