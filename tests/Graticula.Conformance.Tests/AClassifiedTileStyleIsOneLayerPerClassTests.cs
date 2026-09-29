using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The tile style of a classified layer is one filtered style layer per class, with no
/// expression over a feature's attribute.
/// </summary>
/// <remarks>
/// <para>
/// <b>[D-280](../../docs/architecture-debt.md), measured by the owner on 2026-09-29 in ArcGIS Pro
/// 3.x against the showcase.</b> The layer whose generated style carried `line-color` as a
/// `match` over its province field drew nothing; the one with a constant colour drew. The unit
/// tests build the per-class form in memory; this reads what a client is actually served, through
/// the store, the derivation and the metadata writer that copies each style layer out — a writer
/// that dropped `filter` would draw every class on top of every other and pass every unit test.
/// </para>
/// <para>
/// <b>The fixture is the seed's `_many` layer</b>, stored as a `uniqueValue` renderer on `kind`
/// with two classes and a default (`tools/seed-conformance.py`).
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class AClassifiedTileStyleIsOneLayerPerClassTests : ArcGisClient
{
    [Fact]
    public async Task The_seeded_unique_value_layer_is_served_as_filtered_layers_with_constant_paint()
    {
        string service = await ManyServiceAsync();

        JsonElement style = await GetJsonAsync(
            $"/rest/services/{service}/VectorTileServer/resources/styles/root.json");

        JsonElement[] layers = [.. style.GetProperty("layers").EnumerateArray()
            .Where(l => l.TryGetProperty("source-layer", out JsonElement s)
                && (s.GetString() ?? string.Empty).EndsWith("_many", StringComparison.Ordinal))];

        Assert.True(
            layers.Length == 3,
            $"Two classes and a default should be three style layers; {service} serves "
            + $"{layers.Length}: {string.Join(", ", layers.Select(l => l.GetRawText()))}");

        List<string> filters = [];

        foreach (JsonElement layer in layers)
        {
            Assert.True(
                layer.TryGetProperty("filter", out JsonElement filter),
                $"{layer.GetProperty("id").GetString()} has no filter, so it draws every feature "
                + "in one class's colour.");

            filters.Add(filter.GetRawText().Replace(" ", string.Empty, StringComparison.Ordinal));

            foreach (JsonProperty paint in layer.GetProperty("paint").EnumerateObject())
            {
                Assert.True(
                    paint.Value.ValueKind != JsonValueKind.Array
                    || !paint.Value.GetRawText().Contains("\"get\"", StringComparison.Ordinal),
                    $"`{paint.Name}` on {layer.GetProperty("id").GetString()} reads a feature's "
                    + $"attribute ({paint.Value.GetRawText()}), which ArcGIS Pro does not draw.");
            }
        }

        // <b>The default at the bottom</b>, so a class is drawn over it where they meet.
        Assert.StartsWith("[\"!in\",\"kind\"", filters[0], StringComparison.Ordinal);
        Assert.Contains("[\"==\",\"kind\",\"residential\"]", filters);
        Assert.Contains("[\"==\",\"kind\",\"commercial\"]", filters);
    }

    /// <summary>The seed's `_many` service, by the end of its name.</summary>
    /// <returns>Its qualified name.</returns>
    private async Task<string> ManyServiceAsync()
    {
        JsonElement hosted = await GetJsonAsync("/rest/services/hosted");

        string? found = hosted.TryGetProperty("services", out JsonElement services)
            ? services.EnumerateArray()
                .Select(s => s.TryGetProperty("name", out JsonElement n) ? n.GetString() : null)
                .FirstOrDefault(n => n is not null && n.EndsWith("_many", StringComparison.Ordinal))
            : null;

        // Not a silent pass: the seed publishes this layer, and a suite that shrugs when its
        // fixture is missing reports green on nothing.
        Assert.True(
            found is not null,
            "The seed's `_many` service is not in the hosted folder; run tools/seed-conformance.py.");

        return found!;
    }
}
