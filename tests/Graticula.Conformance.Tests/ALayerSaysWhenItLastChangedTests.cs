using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// A layer's document names its service's item and, for a hosted layer, when it last changed — ArcGIS's
/// <c>serviceItemId</c> and <c>editingInfo</c> on <i>Layer (Feature Service)</i>, which the ArcGIS veteran's review of
/// 2026-11-03 found missing. The Maps SDK's <c>FeatureLayer.refresh()</c> reads <c>editingInfo.lastEditDate</c>.
/// </summary>
[Collection("catalogue walk")]
public sealed class ALayerSaysWhenItLastChangedTests : ArcGisClient
{
    private static string Editable()
    {
        string qualified = Environment.GetEnvironmentVariable("GRATICULA_TEST_EDITABLE") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(qualified), "GRATICULA_TEST_EDITABLE is not set, so there is no hosted layer to edit.");
        return qualified;
    }

    [Fact]
    public async Task A_hosted_layer_names_its_services_item_and_its_last_edit_moves_with_an_edit()
    {
        string service = Editable();
        string layer = $"/rest/services/{service}/FeatureServer/0";

        JsonElement serviceDocument = await GetJsonAsync($"/rest/services/{service}/FeatureServer");
        JsonElement before = await GetJsonAsync(layer);

        Assert.Equal(serviceDocument.GetProperty("serviceItemId").GetString(), before.GetProperty("serviceItemId").GetString());

        // The layers listing carries the same layer document.
        JsonElement listed = (await GetJsonAsync($"/rest/services/{service}/FeatureServer/layers")).GetProperty("layers")[0];
        Assert.Equal(before.GetProperty("serviceItemId").GetString(), listed.GetProperty("serviceItemId").GetString());

        long? was = before.TryGetProperty("editingInfo", out JsonElement info) ? info.GetProperty("dataLastEditDate").GetInt64() : null;

        (int added, string body) = await AdminAsync(HttpMethod.Post,
            $"{layer}/addFeatures?f=json&features="
            + Uri.EscapeDataString("[{\"attributes\":{},\"geometry\":{\"x\":29,\"y\":41,\"spatialReference\":{\"wkid\":4326}}}]"));

        Assert.True(added == 200, $"addFeatures answered {added}: {body}");
        JsonElement result = JsonDocument.Parse(body).RootElement.GetProperty("addResults")[0];
        Assert.True(result.GetProperty("success").GetBoolean(), body);

        try
        {
            JsonElement after = await GetJsonAsync(layer);
            JsonElement editing = after.GetProperty("editingInfo");
            long data = editing.GetProperty("dataLastEditDate").GetInt64();

            Assert.True(was is null || data > was, $"dataLastEditDate did not move: {was} then {data}.");
            Assert.True(editing.GetProperty("lastEditDate").GetInt64() >= data);

            // Milliseconds since the epoch, and a moment of this test rather than an invented one.
            Assert.InRange(DateTimeOffset.FromUnixTimeMilliseconds(data), DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(10));
        }
        finally
        {
            await AdminAsync(HttpMethod.Post,
                $"{layer}/deleteFeatures?f=json&objectIds={result.GetProperty("objectId").GetInt64()}");
        }
    }
}
