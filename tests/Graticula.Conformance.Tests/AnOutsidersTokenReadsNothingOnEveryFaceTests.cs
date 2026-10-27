using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A token from one organisation opens another's host as an outsider and reads nothing that organisation keeps to itself,
/// on the faces <c>TenantsAreKeptApartTests</c> did not walk — ADR-204 condition 4: a WFS GetFeature, an OGC API item
/// and the collection's items, and a vector tile.
/// </summary>
/// <remarks>
/// <b>Each read is made twice</b>: by the organisation's own administrator, who must get the feature — so the address is
/// right and there is something to leak — and by a member of the main organisation at the same host, who must not.
/// </remarks>
[Collection("server settings")]
public sealed class AnOutsidersTokenReadsNothingOnEveryFaceTests : ArcGisClient
{
    private string _root = string.Empty;

    [Fact]
    public async Task A_member_of_another_organisation_reads_no_feature_by_WFS_OGC_or_tile()
    {
        _root = await RequireServerAsync();
        string admin = await TokenAsync(_root) ?? throw new InvalidOperationException("No administrator credential.");

        string run = Guid.NewGuid().ToString("N")[..6];
        string key = $"of{run}", host = $"of{run}.localhost";
        string layer = $"zz_kept_{run}";
        string marker = $"kept-{run}";

        (HttpStatusCode made, JsonElement tenant) = await SendAsync(HttpMethod.Post, "/admin/tenants", null, admin, new
        {
            key, name = $"Outsider faces {run}", hosts = new[] { host }, administrator = $"ofadmin{run}",
        });

        if (made == HttpStatusCode.Conflict
            && tenant.GetProperty("error").GetProperty("message").GetString()!.Contains("superuser", StringComparison.Ordinal))
        {
            return;
        }

        Assert.True(made == HttpStatusCode.Created, $"Making an organisation answered {(int)made}: {tenant}");

        try
        {
            string owner = await SettleAsync($"ofadmin{run}", tenant.GetProperty("administrator").GetProperty("password").GetString()!, host);

            // Kept to the organisation: shared with its own members and nobody else.
            (HttpStatusCode defined, JsonElement design) = await SendAsync(HttpMethod.Post, "/admin/hosted/define", host, owner, new
            {
                name = layer, geometryType = "point", fields = new[] { new { name = "label", type = "Text" } }, sharing = "organization",
            });
            Assert.True(defined == HttpStatusCode.Created, $"Defining the layer answered {(int)defined}: {design}");

            using (FormUrlEncodedContent add = new([
                new("f", "json"),
                new("features", JsonSerializer.Serialize(new[]
                {
                    new { geometry = new { x = 30.5, y = 39.5, spatialReference = new { wkid = 4326 } }, attributes = new { label = marker } },
                })),
            ]))
            {
                (HttpStatusCode added, JsonElement addBody) = await SendFormAsync(
                    $"/rest/services/hosted/{layer}/FeatureServer/0/addFeatures", host, owner, add);
                Assert.True(added == HttpStatusCode.OK && addBody.ToString().Contains("\"success\":true", StringComparison.Ordinal),
                    $"Adding a feature answered {(int)added}: {addBody}");
            }

            // An outsider: a member of the main organisation, signed in there, at this organisation's host.
            (_, JsonElement member) = await SendAsync(HttpMethod.Post, "/admin/members", null, admin, new { name = $"visitor{run}", role = "user" });
            string visitor = await SettleAsync($"visitor{run}", member.GetProperty("password").GetString()!, null);

            // ---- OGC API Features: the collection is found by its owner, and its items are not the outsider's ----
            (HttpStatusCode listed, string collections) = await GetTextAsync("/ogc/features/v1/collections?f=json", host, owner);
            Assert.True(listed == HttpStatusCode.OK, $"The owner's collections answered {(int)listed}: {collections}");
            string? collection = JsonDocument.Parse(collections).RootElement.GetProperty("collections").EnumerateArray()
                .Select(c => c.GetProperty("id").GetString())
                .FirstOrDefault(id => id is not null && id.Contains(layer, StringComparison.Ordinal));
            Assert.False(collection is null, $"The owner's collections do not list {layer}: {collections}");

            string items = $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection!)}/items?f=json";
            (HttpStatusCode ownerItems, string ownerBody) = await GetTextAsync(items, host, owner);
            Assert.True(ownerItems == HttpStatusCode.OK && ownerBody.Contains(marker, StringComparison.Ordinal),
                $"The owner's items did not carry the feature ({(int)ownerItems}): {ownerBody}");

            string? featureId = JsonDocument.Parse(ownerBody).RootElement.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("id").ToString()).FirstOrDefault();
            Assert.False(featureId is null, $"The owner's item has no id: {ownerBody}");

            (HttpStatusCode outsiderItems, string outsiderItemsBody) = await GetTextAsync(items, host, visitor);
            Assert.False(outsiderItemsBody.Contains(marker, StringComparison.Ordinal),
                $"An outsider read the organisation's features through OGC items ({(int)outsiderItems}).");

            (HttpStatusCode outsiderItem, string outsiderItemBody) = await GetTextAsync(
                $"/ogc/features/v1/collections/{Uri.EscapeDataString(collection!)}/items/{Uri.EscapeDataString(featureId!)}?f=json", host, visitor);
            Assert.False(outsiderItem == HttpStatusCode.OK || outsiderItemBody.Contains(marker, StringComparison.Ordinal),
                $"An outsider read one of the organisation's features through an OGC item ({(int)outsiderItem}).");

            // ---- WFS GetFeature ----
            string wfs = $"/wfs?service=WFS&version=2.0.0&request=GetFeature&typenames={layer}&outputFormat=application/json";
            (HttpStatusCode ownerWfs, string ownerWfsBody) = await GetTextAsync(wfs, host, owner);
            Assert.True(ownerWfs == HttpStatusCode.OK && ownerWfsBody.Contains(marker, StringComparison.Ordinal),
                $"The owner's WFS GetFeature did not carry the feature ({(int)ownerWfs}): {ownerWfsBody[..Math.Min(400, ownerWfsBody.Length)]}");

            (HttpStatusCode outsiderWfs, string outsiderWfsBody) = await GetTextAsync(wfs, host, visitor);
            Assert.False(outsiderWfsBody.Contains(marker, StringComparison.Ordinal),
                $"An outsider read the organisation's features through WFS GetFeature ({(int)outsiderWfs}).");

            // ---- A vector tile: the whole world at level 0 holds the point ----
            string tile = $"/rest/services/hosted/{layer}/VectorTileServer/tile/0/0/0.pbf";
            (HttpStatusCode ownerTile, byte[] ownerBytes) = await GetBytesAsync(tile, host, owner);
            Assert.True(ownerTile == HttpStatusCode.OK && ownerBytes.Length > 0,
                $"The owner's tile 0/0/0 answered {(int)ownerTile} with {ownerBytes.Length} bytes; there is nothing to compare with.");

            (HttpStatusCode outsiderTile, byte[] outsiderBytes) = await GetBytesAsync(tile, host, visitor);
            Assert.False(outsiderTile == HttpStatusCode.OK && outsiderBytes.Length > 0,
                $"An outsider was given the organisation's tile 0/0/0: {(int)outsiderTile}, {outsiderBytes.Length} bytes.");
        }
        finally
        {
            await SendAsync(HttpMethod.Delete, $"/admin/tenants/{key}?purge=true", null, admin);
            await SendAsync(HttpMethod.Delete, $"/admin/members/visitor{run}", null, admin);
        }
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string? host, string? token)
    {
        HttpRequestMessage request = new(method, $"{_root}{path}");

        if (host is not null)
        {
            request.Headers.Host = host;
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return request;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method, string path, string? host, string? token, object? json = null)
    {
        using HttpRequestMessage request = Request(method, path, host, token);
        request.Content = json is null ? null : JsonContent.Create(json);
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendFormAsync(string path, string? host, string? token, HttpContent form)
    {
        using HttpRequestMessage request = Request(HttpMethod.Post, path, host, token);
        request.Content = form;
        using HttpResponseMessage response = await Http.SendAsync(request);
        string text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private async Task<(HttpStatusCode Status, string Body)> GetTextAsync(string path, string? host, string? token)
    {
        using HttpRequestMessage request = Request(HttpMethod.Get, path, host, token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<(HttpStatusCode Status, byte[] Body)> GetBytesAsync(string path, string? host, string? token)
    {
        using HttpRequestMessage request = Request(HttpMethod.Get, path, host, token);
        using HttpResponseMessage response = await Http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private async Task<string> SettleAsync(string name, string issued, string? host)
    {
        (_, JsonElement first) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, null, new { name, password = issued });
        string own = "Own-" + Guid.NewGuid().ToString("N");
        await SendAsync(HttpMethod.Post, "/rest/auth/password", host, first.GetProperty("token").GetString(),
            new { currentPassword = issued, newPassword = own });
        (_, JsonElement again) = await SendAsync(HttpMethod.Post, "/rest/auth/login", host, null, new { name, password = own });
        return again.GetProperty("token").GetString()!;
    }
}
