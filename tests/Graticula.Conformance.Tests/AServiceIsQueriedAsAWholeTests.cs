using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>FeatureServer/query</c> answers several layers at once, each as its own query would.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written 2026-09-23 for V-82, the owner's decision that it be built.</b> It was a 404, and some
/// Dashboards and Experience Builder widgets query a service rather than its layers.
/// </para>
/// <para>
/// <b>Asserted against the layer's own query, not against numbers written here.</b> The service-level
/// answer is built by running each layer's query, so what matters is that the two agree — a count, the ids,
/// a refusal — whatever the fixture holds.
/// </para>
/// </remarks>
public sealed class AServiceIsQueriedAsAWholeTests : ArcGisClient
{
    private async Task<(string Service, int[] Layers)> ServiceAsync()
    {
        string? name = Environment.GetEnvironmentVariable(MultiLayerServiceConformanceTests.ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{MultiLayerServiceConformanceTests.ServiceVariable} is not set, so this FAILS rather than skips: "
            + "a query across layers needs a service with more than one.");

        JsonElement document = await GetJsonAsync($"/rest/services/{name}/FeatureServer");

        int[] layers = [.. document.GetProperty("layers").EnumerateArray()
            .Where(layer => !layer.TryGetProperty("subLayerIds", out JsonElement children)
                || children.ValueKind != JsonValueKind.Array)
            .Select(layer => layer.GetProperty("id").GetInt32())];

        Assert.True(layers.Length > 1, $"{name} has {layers.Length} layer(s) with features; this needs two.");

        return (name!, layers);
    }

    private static string Encoded(string value) => Uri.EscapeDataString(value);

    [Fact]
    public async Task Each_layer_counts_what_its_own_query_counts()
    {
        (string service, int[] layers) = await ServiceAsync();

        string definitions = "{" + string.Join(",", layers.Take(2).Select(id => $"\"{id}\":\"1=1\"")) + "}";

        JsonElement whole = await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer/query?layerDefs={Encoded(definitions)}&returnCountOnly=true");

        JsonElement[] answered = [.. whole.GetProperty("layers").EnumerateArray()];

        Assert.Equal(layers.Take(2), answered.Select(layer => layer.GetProperty("id").GetInt32()));

        foreach (JsonElement layer in answered)
        {
            int id = layer.GetProperty("id").GetInt32();

            JsonElement own = await GetJsonAsync(
                $"/rest/services/{service}/FeatureServer/{id}/query?where=1%3D1&returnCountOnly=true");

            Assert.Equal(own.GetProperty("count").GetInt32(), layer.GetProperty("count").GetInt32());
        }
    }

    [Fact]
    public async Task The_array_form_carries_each_layers_own_fields()
    {
        (string service, int[] layers) = await ServiceAsync();

        string definitions =
            $"[{{\"layerId\":{layers[0]},\"where\":\"1=1\",\"outFields\":\"*\"}},"
            + $"{{\"layerId\":{layers[1]},\"where\":\"1=1\"}}]";

        JsonElement whole = await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer/query?layerDefs={Encoded(definitions)}&returnGeometry=false");

        JsonElement[] answered = [.. whole.GetProperty("layers").EnumerateArray()];

        Assert.Equal(2, answered.Length);

        JsonElement all = answered[0];
        JsonElement only = answered[1];

        Assert.True(
            all.GetProperty("fields").GetArrayLength() > only.GetProperty("fields").GetArrayLength(),
            "The first layer asked for every field and the second for the default, and both came back with "
            + $"{all.GetProperty("fields").GetArrayLength()} and {only.GetProperty("fields").GetArrayLength()}: "
            + "the per-layer outFields was not applied.");

        Assert.True(only.TryGetProperty("objectIdFieldName", out _), $"The second layer's answer is not a query answer: {only}");
    }

    [Fact]
    public async Task A_clause_one_layer_refuses_refuses_the_request_and_names_the_layer()
    {
        (string service, int[] layers) = await ServiceAsync();

        string definitions = $"{{\"{layers[0]}\":\"1=1\",\"{layers[1]}\":\"no_such_column = 1\"}}";

        (System.Net.HttpStatusCode status, string body) = await AnonymousAsync(
            $"/rest/services/{service}/FeatureServer/query?layerDefs={Encoded(definitions)}");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains($"Layer {layers[1]}", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"layers\"", body, StringComparison.Ordinal);
    }

    /// <summary>JSON asked for twice is JSON — the layer query takes it, and this refused it for a day.</summary>
    [Fact]
    public async Task Asking_for_json_twice_is_asking_for_json()
    {
        (string service, int[] layers) = await ServiceAsync();

        // The helper adds its own f=json, so this sends two.
        JsonElement whole = await GetJsonAsync(
            $"/rest/services/{service}/FeatureServer/query?layerDefs={Encoded($"{{\"{layers[0]}\":\"1=1\"}}")}"
            + "&returnCountOnly=true&f=json");

        Assert.Single(whole.GetProperty("layers").EnumerateArray());
    }

    [Fact]
    public async Task No_layerDefs_is_refused_with_what_to_send()
    {
        (string service, _) = await ServiceAsync();

        (System.Net.HttpStatusCode status, string body) = await AnonymousAsync(
            $"/rest/services/{service}/FeatureServer/query");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, status);
        Assert.Contains("layerDefs", body, StringComparison.Ordinal);
    }
}
