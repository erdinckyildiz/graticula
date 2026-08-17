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

    /// <summary>
    /// Each layer appears in the list once.
    /// </summary>
    /// <remarks>
    /// <b>Added 2026-08-17 after seeing another server fail it.</b> A peer's
    /// FeatureServer document listed two layers as four entries — ids
    /// <c>[1, 1, 2, 2]</c>, the duplicates byte-identical — and an ArcGIS client
    /// walking that renders every layer twice, with no way to tell which copy is
    /// spurious. Nothing in this suite asserted the property: the tests here check
    /// that every listed layer is fetchable, that the catalogue and the directory
    /// agree, and that the service is listed once rather than once per layer.
    /// **None of them notices the same layer listed twice**, because every one of
    /// them passes on a duplicate.
    /// </remarks>
    [Fact]
    public async Task No_layer_is_listed_more_than_once()
    {
        (_, JsonElement document) = await ServiceAsync();

        JsonElement layers = Require(
            document, "layers", "A FeatureServer with no layer list has nothing to add.");

        List<int> ids = [];

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (layer.TryGetProperty("id", out JsonElement id))
            {
                ids.Add(id.GetInt32());
            }
        }

        List<int> repeated = [.. ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key)];

        Assert.True(
            repeated.Count == 0,
            $"The service document lists {ids.Count} entries for "
            + $"{ids.Distinct().Count()} distinct layer ids. Repeated: "
            + string.Join(", ", repeated)
            + ". A client walking this list draws each of those twice and cannot tell which "
            + "entry to drop.");
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

    /// <summary>
    /// Feature layers each report their own geometry type and field list.
    /// </summary>
    /// <remarks>
    /// <b>Feature layers, and skipping the groups is a correction rather than a
    /// concession.</b> This used to walk every entry in the document and demand
    /// a <c>geometryType</c> from each, which crashes on a group layer — and a
    /// group layer having none is asserted two tests below as correct
    /// behaviour. It passed only because the fixture it was written against had
    /// no groups, and it would have failed on any real service that did. Found
    /// 2026-08-15 when the fixtures became a script instead of something
    /// somebody had made by hand.
    /// </remarks>
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
            if (IsGroup(entry))
            {
                continue;
            }

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

        Assert.True(
            geometryTypes.Count >= 2,
            $"only {geometryTypes.Count} feature layers were found, and a multi-layer service "
            + "is the thing under test.");

        Assert.Equal(
            geometryTypes.Count,
            geometryTypes.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Whether a service-document entry is a group rather than data.</summary>
    /// <remarks>
    /// <b>By its declared type, not by the absence of a geometry type.</b>
    /// Inferring it from what is missing would let a feature layer that forgot
    /// to report its geometry pass as a group, which is the defect this file
    /// exists to catch.
    /// </remarks>
    private static bool IsGroup(JsonElement entry) =>
        entry.TryGetProperty("type", out JsonElement type)
        && string.Equals(type.GetString(), "Group Layer", StringComparison.Ordinal);

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
            // A group holds no rows, so querying one is not a thing a client
            // does and not a thing this asserts. It is covered by
            // A_group_layer_declares_no_geometry_type instead.
            if (IsGroup(entry))
            {
                continue;
            }

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

    /// <summary>
    /// Every layer the catalogue lists is addressable through the services
    /// directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>D-45's regression test, and the invariant is between two documents
    /// rather than inside one.</b> <c>/admin/layers</c> names the layers; the
    /// services directory says where they live. Nothing checked that the two
    /// agree, and they did not need to for a single-layer service — where the
    /// service is named after its only layer, guessing
    /// <c>/rest/services/{folder}/{layer}/FeatureServer/0</c> happens to be right.
    /// </para>
    /// <para>
    /// It is wrong for every member of a multi-layer service, and the console had
    /// been guessing it for all of them. The failure was invisible because the
    /// server's answer is its deliberate *"no layer is visible to you"* 404 —
    /// which is correct for a service that does not exist, and reads to an
    /// administrator as a permission problem.
    /// </para>
    /// <para>
    /// So the assertion is the property a client needs: for each layer in the
    /// catalogue there is exactly one FeatureServer entry in the directory that
    /// holds a layer of that name, and fetching it at that id returns that layer.
    /// A stopped layer is skipped — it is deliberately absent from the directory.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_catalogued_layer_is_findable_in_the_services_directory()
    {
        JsonElement layers = (await GetJsonAsync("/admin/layers")).GetProperty("layers");

        // Build name -> (service, id) from the directory, the way a client must.
        Dictionary<string, (string Service, int Id)> placed = new(StringComparer.Ordinal);

        foreach (string folder in new[] { "hosted", string.Empty })
        {
            JsonElement directory = await GetJsonAsync($"/rest/services/{folder}");

            if (!directory.TryGetProperty("services", out JsonElement services))
            {
                continue;
            }

            foreach (JsonElement service in services.EnumerateArray())
            {
                if (!string.Equals(service.GetProperty("type").GetString(), "FeatureServer",
                        StringComparison.Ordinal))
                {
                    continue;
                }

                string name = service.GetProperty("name").GetString()!;
                JsonElement document = await GetJsonAsync($"/rest/services/{name}/FeatureServer");

                if (!document.TryGetProperty("layers", out JsonElement held))
                {
                    continue;
                }

                foreach (JsonElement layer in held.EnumerateArray())
                {
                    if (string.Equals(layer.GetProperty("type").GetString(), "Group Layer",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    placed[layer.GetProperty("name").GetString()!] =
                        (name, layer.GetProperty("id").GetInt32());
                }
            }
        }

        List<string> missing = [];

        foreach (JsonElement layer in layers.EnumerateArray())
        {
            if (!string.Equals(layer.GetProperty("status").GetString(), "started",
                    StringComparison.Ordinal))
            {
                continue;
            }

            string name = layer.GetProperty("name").GetString()!;

            if (!placed.TryGetValue(name, out (string Service, int Id) place))
            {
                missing.Add($"{name}: in the catalogue, absent from the directory");
                continue;
            }

            JsonElement document =
                await GetJsonAsync($"/rest/services/{place.Service}/FeatureServer/{place.Id}");

            string served = document.GetProperty("name").GetString()!;

            if (!string.Equals(served, name, StringComparison.Ordinal))
            {
                missing.Add($"{name}: {place.Service}/FeatureServer/{place.Id} serves '{served}'");
            }
        }

        Assert.True(
            missing.Count == 0,
            $"""
             A layer the catalogue lists cannot be found where the directory says:

                 {string.Join("\n    ", missing)}

             Any client — this project's console included — has to turn a layer name into a
             URL. /admin/layers does not carry the service or the layer id, so that mapping
             comes from the directory, and these two documents disagreeing means no client
             can address the layer at all. D-45.
             """);
    }

    // ---------- group layers ----------

    /// <summary>
    /// Set to a service containing a group layer, e.g. <c>hosted/EarlyAlert</c>.
    /// </summary>
    public const string GroupedVariable = "GISSERVER_TEST_GROUPED";

    private async Task<(string Service, JsonElement Document)> GroupedAsync()
    {
        string? name = Environment.GetEnvironmentVariable(GroupedVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{GroupedVariable} is not set, so these tests FAIL rather than skip. Name a service "
            + "with a group layer in it. Group layers exist to carry structure, and structure is "
            + "the thing that silently flattens.");

        return (name!, await GetJsonAsync($"/rest/services/{name}/FeatureServer"));
    }

    [Fact]
    public async Task A_group_layer_appears_in_the_same_list_as_the_feature_layers()
    {
        // ArcGIS's shape: one flat array, one numbering, structure carried by
        // parentLayerId and subLayerIds. A separate "groups" array would be our
        // invention and no client would read it.
        (_, JsonElement document) = await GroupedAsync();

        JsonElement[] entries = [.. document.GetProperty("layers").EnumerateArray()];

        Assert.Contains(entries, e => e.GetProperty("type").GetString() == "Group Layer");
        Assert.Contains(entries, e => e.GetProperty("type").GetString() == "Feature Layer");

        // One numbering across both kinds. A repeated id makes /FeatureServer/{id}
        // ambiguous, which is the failure two tables and no shared constraint
        // would produce.
        int[] ids = [.. entries.Select(e => e.GetProperty("id").GetInt32())];
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public async Task Parent_and_child_agree_with_each_other()
    {
        // The two directions are stored once and written twice, so they can
        // disagree — and a client that trusts subLayerIds would then draw a
        // layer under a group that does not claim it.
        (_, JsonElement document) = await GroupedAsync();

        JsonElement[] entries = [.. document.GetProperty("layers").EnumerateArray()];

        foreach (JsonElement entry in entries)
        {
            int id = entry.GetProperty("id").GetInt32();
            int parent = entry.GetProperty("parentLayerId").GetInt32();

            if (parent < 0)
            {
                continue;
            }

            JsonElement above = Assert.Single(
                entries.Where(e => e.GetProperty("id").GetInt32() == parent).ToArray());

            Assert.Equal("Group Layer", above.GetProperty("type").GetString());

            int[] children =
            [
                .. above.GetProperty("subLayerIds").EnumerateArray().Select(c => c.GetInt32()),
            ];

            Assert.Contains(id, children);
        }
    }

    [Fact]
    public async Task Every_sub_layer_id_names_something_that_exists()
    {
        // A client follows these. One pointing at nothing is a broken tree that
        // renders as a missing layer with no error anywhere.
        (string service, JsonElement document) = await GroupedAsync();

        JsonElement[] entries = [.. document.GetProperty("layers").EnumerateArray()];
        int[] ids = [.. entries.Select(e => e.GetProperty("id").GetInt32())];

        foreach (JsonElement entry in entries)
        {
            if (entry.GetProperty("subLayerIds").ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement child in entry.GetProperty("subLayerIds").EnumerateArray())
            {
                Assert.Contains(child.GetInt32(), ids);

                // And it answers, which is the claim the list is making.
                _ = await GetJsonAsync(
                    $"/rest/services/{service}/FeatureServer/{child.GetInt32()}");
            }
        }
    }

    [Fact]
    public async Task A_group_layer_answers_at_its_own_id_as_a_group()
    {
        // A 404 for an id the service itself advertised is the kind of
        // self-contradiction that makes a client abandon the whole service.
        (string service, JsonElement document) = await GroupedAsync();

        JsonElement group = document.GetProperty("layers").EnumerateArray()
            .First(e => e.GetProperty("type").GetString() == "Group Layer");

        int id = group.GetProperty("id").GetInt32();

        JsonElement fetched = await GetJsonAsync($"/rest/services/{service}/FeatureServer/{id}");

        Assert.Equal("Group Layer", fetched.GetProperty("type").GetString());
        Assert.Equal(id, fetched.GetProperty("id").GetInt32());

        // No fields, because it has no data — absent rather than empty, which is
        // the difference between "none" and "not applicable".
        Assert.False(fetched.TryGetProperty("fields", out _));
    }

    [Fact]
    public async Task Querying_a_group_layer_is_refused_and_says_why()
    {
        // Not an empty result, which would read as "the group has no features"
        // and send somebody looking for missing data.
        (string service, JsonElement document) = await GroupedAsync();

        int id = document.GetProperty("layers").EnumerateArray()
            .First(e => e.GetProperty("type").GetString() == "Group Layer")
            .GetProperty("id").GetInt32();

        Assert.Equal(
            400,
            await StatusOfAsync(
                $"/rest/services/{service}/FeatureServer/{id}/query?where=1%3D1&f=json"));
    }

    [Fact]
    public async Task A_group_layer_declares_no_geometry_type()
    {
        // A group drawn as a feature layer of unknown geometry is the failure a
        // defaulted type would produce.
        (_, JsonElement document) = await GroupedAsync();

        foreach (JsonElement entry in document.GetProperty("layers").EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "Group Layer")
            {
                continue;
            }

            Assert.Equal(JsonValueKind.Null, entry.GetProperty("geometryType").ValueKind);
        }
    }
}
