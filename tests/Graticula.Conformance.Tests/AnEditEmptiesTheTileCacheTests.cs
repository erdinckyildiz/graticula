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
/// An edit that is kept empties the edited layer's cached tiles.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-09-15, against the showcase.</b> ADR-069 said the tile cache is told to drop a
/// layer's entries on an edit, and nothing told it. A tile fetched before an <c>applyEdits</c>
/// delete came back after it with <c>X-Tile-Cache: HIT</c>, the same ETag and the same bytes, so a
/// removed feature went on being drawn for the layer's whole cache lifetime.
/// </para>
/// <para>
/// <b>The header is what is asserted, not the bytes.</b> Whether the rebuilt tile differs depends
/// on what else is in it; whether this server went back to the datastore for it does not.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AnEditEmptiesTheTileCacheTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_EDITABLE";
    private const int Zoom = 14;
    private const double Half = 20037508.342789244;

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    [Fact]
    public async Task A_deleted_feature_is_not_served_from_a_cached_tile()
    {
        string root = await RequireServerAsync();
        string? configured = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(configured),
            $"{ServiceVariable} is not set, so this test FAILS rather than skips.");

        string service = configured!.Trim('/');
        JsonElement layer = await GetJsonAsync($"/rest/services/{service}/FeatureServer/0");

        int srid = layer.GetProperty("extent").GetProperty("spatialReference").GetProperty("wkid").GetInt32();
        JsonElement extent = layer.GetProperty("extent");
        double x = (extent.GetProperty("xmin").GetDouble() + extent.GetProperty("xmax").GetDouble()) / 2;
        double y = (extent.GetProperty("ymin").GetDouble() + extent.GetProperty("ymax").GetDouble()) / 2;

        (double mx, double my) = srid switch
        {
            3857 or 102100 => (x, y),
            4326 => (x * Half / 180, Math.Log(Math.Tan((90 + y) * Math.PI / 360)) * Half / Math.PI),
            _ => throw new InvalidOperationException(
                $"The editable layer is in {srid}; this test places a feature in 3857 or 4326 only."),
        };

        string field = layer.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("type").GetString() == "esriFieldTypeString"
                && f.GetProperty("editable").GetBoolean())
            .GetProperty("name").GetString()!;

        JsonElement added = await EditAsync(root, service, "addFeatures", string.Create(
            CultureInfo.InvariantCulture,
            $"[{{\"geometry\":{{\"x\":{x},\"y\":{y},\"spatialReference\":{{\"wkid\":{srid}}}}},"
            + $"\"attributes\":{{\"{field}\":\"tile cache probe\"}}}}]"));

        JsonElement result = added.GetProperty("addResults").EnumerateArray().Single();
        Assert.True(result.GetProperty("success").GetBoolean(), $"The probe feature was not added: {added}");
        long id = result.GetProperty("objectId").GetInt64();

        int n = 1 << Zoom;
        int column = (int)((mx + Half) / (2 * Half) * n);
        int row = (int)((Half - my) / (2 * Half) * n);
        Uri tile = new(string.Create(
            CultureInfo.InvariantCulture,
            $"{root}/rest/services/{service}/VectorTileServer/tile/{Zoom}/{row}/{column}.pbf"));

        bool deleted = false;

        try
        {
            await CacheStateAsync(root, tile);
            string warm = await CacheStateAsync(root, tile);

            Assert.True(
                warm == "HIT",
                $"The tile was not served from the cache on its second fetch (X-Tile-Cache: {warm}), so "
                + "this test cannot say anything about emptying it. Is the layer's cache lifetime zero?");

            JsonElement removed = await EditAsync(root, service, "deleteFeatures", null, ("objectIds", id.ToString(CultureInfo.InvariantCulture)));
            deleted = removed.GetProperty("deleteResults").EnumerateArray().Single().GetProperty("success").GetBoolean();
            Assert.True(deleted, $"The probe feature was not deleted: {removed}");

            string after = await CacheStateAsync(root, tile);

            Assert.True(
                after != "HIT",
                "A tile cached before a feature in it was deleted was served from the cache after the "
                + "delete (X-Tile-Cache: HIT). The deleted feature is drawn until the cache lifetime runs out.");
        }
        finally
        {
            if (!deleted)
            {
                await EditAsync(root, service, "deleteFeatures", null, ("objectIds", id.ToString(CultureInfo.InvariantCulture)));
            }
        }
    }

    private static async Task<string> CacheStateAsync(string root, Uri tile)
    {
        using HttpClient http = Client();
        using HttpRequestMessage request = new(HttpMethod.Get, tile);
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent,
            $"The tile answered {(int)response.StatusCode}.");

        return response.Headers.TryGetValues("X-Tile-Cache", out IEnumerable<string>? values)
            ? values.Single()
            : "(none)";
    }

    private static async Task<JsonElement> EditAsync(
        string root, string service, string operation, string? features, params (string Key, string Value)[] extra)
    {
        using HttpClient http = Client();

        List<KeyValuePair<string, string>> form = [new("f", "json")];

        if (features is not null)
        {
            form.Add(new("features", features));
        }

        form.AddRange(extra.Select(e => new KeyValuePair<string, string>(e.Key, e.Value)));

        using FormUrlEncodedContent content = new(form);
        using HttpRequestMessage request = new(
            HttpMethod.Post, new Uri($"{root}/rest/services/{service}/FeatureServer/0/{operation}"))
        {
            Content = content,
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
