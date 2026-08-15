using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace GisServer.Conformance.Tests;

/// <summary>
/// A service holds layers, and every one of them is reachable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Owner correction, 2026-08-15: "a service is a combination of layers
/// actually. so multiple layers can be shown as a service."</b> Before it, one
/// published layer was one service and the assumption was wired into the URLs —
/// every route in this server ended in a literal <c>/0</c>.
/// </para>
/// <para>
/// <b>These tests need a multi-layer service to exist and will not invent
/// one.</b> Set <c>GISSERVER_TEST_MULTILAYER</c> to its address (for example
/// <c>hosted/EarlyAlert_Reports_HD</c>). Skipping instead would let the whole
/// model regress to one-layer-per-service without a single red test, which is
/// the failure this file exists to prevent.
/// </para>
/// </remarks>
public sealed class MultiLayerServiceConformanceTests : ArcGisClient
{
    /// <summary>Which service to walk.</summary>
    public const string ServiceVariable = "GISSERVER_TEST_MULTILAYER";

    private async Task<(string Service, JsonElement Document)> ServiceAsync()
    {
        string? name = Environment.GetEnvironmentVariable(ServiceVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip. Name a service "
            + "with more than one layer, e.g. hosted/EarlyAlert_Reports_HD. A service is a "
            + "container of layers and nothing else here proves it: every other suite would pass "
            + "against a server that had silently gone back to one layer per service.");

        JsonElement document = await GetJsonAsync($"/rest/services/{name}/FeatureServer");

        return (name!, document);
    }

    [Fact]
    public async Task The_service_document_lists_more_than_one_layer()
    {
        (_, JsonElement document) = await ServiceAsync();

        JsonElement layers = Require(
            document, "layers", "A FeatureServer with no layer list has nothing to add.");

        Assert.True(
            layers.GetArrayLength() > 1,
            $"{ServiceVariable} names a service with {layers.GetArrayLength()} layer(s). Point it "
            + "at one with several, or this suite is testing the single-layer case twice.");
    }

    [Fact]
    public async Task Every_layer_the_service_lists_can_be_fetched_at_its_own_id()
    {
        // The property a service document exists to have. A client reads this
        // list and builds /FeatureServer/{id} from it.
        (string service, JsonElement document) = await ServiceAsync();

        foreach (JsonElement entry in document.GetProperty("layers").EnumerateArray())
        {
            int id = entry.GetProperty("id").GetInt32();
            string name = entry.GetProperty("name").GetString()!;

            JsonElement layer = await GetJsonAsync($"/rest/services/{service}/FeatureServer/{id}");

            Assert.Equal(id, layer.GetProperty("id").GetInt32());
            Assert.Equal(name, layer.GetProperty("name").GetString());
        }
    }

    [Fact]
    public async Task Each_layer_reports_its_own_geometry_type_and_fields()
    {
        // <b>Its own, not the service's.</b> The point of a multi-layer service
        // is that the layers differ; a document that reported the first layer's
        // geometry for all of them would be worse than one that reported none.
        (string service, JsonElement document) = await ServiceAsync();

        List<string> geometryTypes = [];

        foreach (JsonElement entry in document.GetProperty("layers").EnumerateArray())
        {
            JsonElement layer = await GetJsonAsync(
                $"/rest/services/{service}/FeatureServer/{entry.GetProperty("id").GetInt32()}");

            geometryTypes.Add(layer.GetProperty("geometryType").GetString()!);

            JsonElement fields = Require(
                layer, "fields", "A client cannot build a query without the field list.");

            Assert.True(
                fields.GetArrayLength() > 0,
                "A layer with no fields cannot be queried for attributes.");

            // Every field carries a name and a type, which is the minimum a
            // client needs to render an attribute table.
            Assert.All(fields.EnumerateArray(), f =>
            {
                Assert.False(string.IsNullOrWhiteSpace(f.GetProperty("name").GetString()));
                Assert.StartsWith(
                    "esriFieldType", f.GetProperty("type").GetString()!, StringComparison.Ordinal);
            });
        }

        Assert.Equal(
            geometryTypes.Count,
            geometryTypes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task A_layer_id_the_service_does_not_have_is_refused_rather_than_served()
    {
        // Not "layer 0 by default", which is what a server that still assumed
        // one layer per service would do — and it would do it silently.
        (string service, JsonElement document) = await ServiceAsync();

        int beyond = document.GetProperty("layers").EnumerateArray()
            .Select(l => l.GetProperty("id").GetInt32())
            .Max() + 100;

        Assert.Equal(
            404,
            await StatusOfAsync($"/rest/services/{service}/FeatureServer/{beyond}?f=json"));
    }

    [Fact]
    public async Task Every_layer_answers_a_query_at_its_own_id()
    {
        // Metadata resolving per layer while query still went to layer 0 would
        // be the exact half-migration this checks for — and it would look
        // correct for a single-layer service.
        (string service, JsonElement document) = await ServiceAsync();

        foreach (JsonElement entry in document.GetProperty("layers").EnumerateArray())
        {
            int id = entry.GetProperty("id").GetInt32();

            JsonElement result = await GetJsonAsync(
                $"/rest/services/{service}/FeatureServer/{id}/query"
                + "?where=1%3D1&outFields=*&returnGeometry=false");

            // The layer may be empty; what matters is that it answered as
            // itself rather than as layer 0.
            Assert.Equal(
                entry.GetProperty("geometryType").GetString(),
                result.TryGetProperty("geometryType", out JsonElement kind)
                    ? kind.GetString()
                    : entry.GetProperty("geometryType").GetString());

            Assert.True(result.TryGetProperty("features", out _));
        }
    }

    [Fact]
    public async Task The_service_extent_covers_every_layer_that_has_one()
    {
        // A client zooms to this. A service reporting only its first layer's
        // extent opens on a map with the rest off-screen.
        (string service, JsonElement document) = await ServiceAsync();

        if (!document.TryGetProperty("fullExtent", out JsonElement full)
            || full.ValueKind == JsonValueKind.Null)
        {
            // Every layer is empty, so there is no extent to union. That is a
            // fact about the fixture, not a failure.
            return;
        }

        foreach (JsonElement entry in document.GetProperty("layers").EnumerateArray())
        {
            JsonElement layer = await GetJsonAsync(
                $"/rest/services/{service}/FeatureServer/{entry.GetProperty("id").GetInt32()}");

            if (!layer.TryGetProperty("extent", out JsonElement own)
                || own.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            Assert.True(own.GetProperty("xmin").GetDouble() >= full.GetProperty("xmin").GetDouble());
            Assert.True(own.GetProperty("ymin").GetDouble() >= full.GetProperty("ymin").GetDouble());
            Assert.True(own.GetProperty("xmax").GetDouble() <= full.GetProperty("xmax").GetDouble());
            Assert.True(own.GetProperty("ymax").GetDouble() <= full.GetProperty("ymax").GetDouble());
        }
    }

    [Fact]
    public async Task The_catalogue_lists_the_service_once_rather_than_once_per_layer()
    {
        // The regression that a service-shaped catalogue built from a layer list
        // would produce: three entries, all pointing at the same URL.
        (string service, _) = await ServiceAsync();

        string folder = service.Contains('/', StringComparison.Ordinal)
            ? service[..service.IndexOf('/', StringComparison.Ordinal)]
            : string.Empty;

        JsonElement services = (await GetJsonAsync($"/rest/services/{folder}"))
            .GetProperty("services");

        Assert.Single(
            services.EnumerateArray()
                .Where(s => string.Equals(
                    s.GetProperty("name").GetString(), service, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        s.GetProperty("type").GetString(), "FeatureServer", StringComparison.Ordinal))
                .ToArray());
    }
}
