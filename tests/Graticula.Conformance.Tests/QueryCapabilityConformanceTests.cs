using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// Every query parameter this server claims, exercised against a real layer.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layer document is a set of promises and this file is where they are
/// kept.</b> Those flags went stale once already — <c>supportsPagination</c> and
/// <c>supportsOrderBy</c> sat <c>false</c> for weeks while both worked — and the
/// only defence against the reverse, a flag that is true and a parameter that
/// errors, is asking the running server.
/// </para>
/// <para>
/// <b>Needs a layer with several features and an integer attribute.</b> Set
/// <c>GRATICULA_TEST_QUERYABLE</c> to one, e.g. <c>hosted/tiles-buildings</c>.
/// </para>
/// </remarks>
public sealed class QueryCapabilityConformanceTests : ArcGisClient
{
    /// <summary>Which layer to exercise.</summary>
    public const string LayerVariable = "GRATICULA_TEST_QUERYABLE";

    private async Task<(string Path, string Oid)> LayerAsync()
    {
        string? name = Environment.GetEnvironmentVariable(LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(name),
            $"{LayerVariable} is not set, so these tests FAIL rather than skip. Name a layer with "
            + "several features, e.g. hosted/tiles-buildings.");

        string path = $"/rest/services/{name}/FeatureServer/0";

        JsonElement document = await GetJsonAsync(path);

        return (path, document.GetProperty("objectIdField").GetString()!);
    }

    private async Task<JsonElement> QueryAsync(string parameters)
    {
        (string path, _) = await LayerAsync();
        return await GetJsonAsync($"{path}/query?{parameters}");
    }

    // ---------- where ----------

    [Fact]
    public async Task A_where_clause_filters_and_the_count_agrees_with_the_features()
    {
        (_, string oid) = await LayerAsync();

        JsonElement features = await QueryAsync(
            $"where={oid}%20%3C%205&outFields=*&returnGeometry=false");

        JsonElement counted = await QueryAsync($"where={oid}%20%3C%205&returnCountOnly=true");

        // <b>The count and the features must come from the same filter.</b>
        // CountAsync used to build its own where clause; identical, and
        // therefore fine, right up until a query could carry more than a box.
        Assert.Equal(
            features.GetProperty("features").GetArrayLength(),
            counted.GetProperty("count").GetInt32());
    }

    [Theory]
    [InlineData("1%3D1%3B%20drop%20table%20x")]
    [InlineData("1%3D1%20--")]
    [InlineData("pg_sleep(5)%20%3D%201")]
    public async Task An_injection_attempt_in_where_is_refused_by_the_server_not_the_database(
        string clause)
    {
        // <b>400, not 500.</b> A 500 would mean the string reached PostgreSQL
        // and PostgreSQL objected — which is the parser having failed to be the
        // thing standing in the way.
        (string path, _) = await LayerAsync();

        Assert.Equal(400, await StatusOfAsync($"{path}/query?where={clause}&f=json"));
    }

    [Fact]
    public async Task A_where_clause_naming_an_unknown_field_is_refused()
    {
        (string path, _) = await LayerAsync();

        Assert.Equal(
            400, await StatusOfAsync($"{path}/query?where=pg_class%20%3D%201&f=json"));
    }

    // ---------- objectIds ----------

    [Fact]
    public async Task ObjectIds_returns_exactly_those_features()
    {
        (_, string oid) = await LayerAsync();

        JsonElement result = await QueryAsync(
            $"objectIds=2,4&outFields={oid}&returnGeometry=false");

        int[] ids =
        [
            .. result.GetProperty("features").EnumerateArray()
                .Select(f => f.GetProperty("attributes").GetProperty(oid).GetInt32()),
        ];

        Assert.Equal([2, 4], ids.Order());
    }

    // ---------- the response shapes ----------

    [Fact]
    public async Task ReturnIdsOnly_answers_with_ids_and_nothing_else()
    {
        (_, string oid) = await LayerAsync();

        JsonElement result = await QueryAsync($"where={oid}%20%3C%204&returnIdsOnly=true");

        Assert.Equal(oid, result.GetProperty("objectIdFieldName").GetString());
        Assert.False(result.TryGetProperty("features", out _));
        Assert.True(result.GetProperty("objectIds").GetArrayLength() > 0);
    }

    [Fact]
    public async Task ReturnExtentOnly_answers_with_a_box_and_a_count()
    {
        (_, string oid) = await LayerAsync();

        JsonElement result = await QueryAsync($"where={oid}%20%3C%204&returnExtentOnly=true");

        Assert.True(result.GetProperty("count").GetInt32() > 0);

        JsonElement extent = result.GetProperty("extent");

        Assert.True(extent.GetProperty("xmax").GetDouble() >= extent.GetProperty("xmin").GetDouble());
        Assert.True(extent.GetProperty("ymax").GetDouble() >= extent.GetProperty("ymin").GetDouble());
        Assert.True(extent.TryGetProperty("spatialReference", out _));
    }

    [Fact]
    public async Task The_reported_extent_actually_contains_the_features()
    {
        // A box computed by a different statement from the features it
        // describes can disagree with them, and the disagreement only shows up
        // when a client zooms to it and the data is off-screen.
        (_, string oid) = await LayerAsync();

        JsonElement extent = (await QueryAsync($"where={oid}%20%3C%204&returnExtentOnly=true"))
            .GetProperty("extent");

        JsonElement features = await QueryAsync($"where={oid}%20%3C%204&outFields={oid}");

        foreach (JsonElement feature in features.GetProperty("features").EnumerateArray())
        {
            foreach (JsonElement ring in feature.GetProperty("geometry").GetProperty("rings")
                .EnumerateArray())
            {
                foreach (JsonElement point in ring.EnumerateArray())
                {
                    Assert.InRange(
                        point[0].GetDouble(),
                        extent.GetProperty("xmin").GetDouble(),
                        extent.GetProperty("xmax").GetDouble());

                    Assert.InRange(
                        point[1].GetDouble(),
                        extent.GetProperty("ymin").GetDouble(),
                        extent.GetProperty("ymax").GetDouble());
                }
            }
        }
    }

    [Fact]
    public async Task Statistics_are_computed_and_typed()
    {
        (_, string oid) = await LayerAsync();

        JsonElement result = await QueryAsync(
            "outStatistics=%5B%7B%22statisticType%22%3A%22count%22%2C%22onStatisticField%22%3A%22"
            + oid
            + "%22%2C%22outStatisticFieldName%22%3A%22n%22%7D%5D");

        JsonElement only = Assert.Single(result.GetProperty("features").EnumerateArray().ToArray());

        Assert.True(only.GetProperty("attributes").GetProperty("n").GetInt64() > 0);

        // Shaped as features so a client reads statistics through the same code
        // path it reads features with.
        Assert.True(result.TryGetProperty("fields", out _));
    }

    [Fact]
    public async Task A_statistic_over_an_unknown_field_is_refused()
    {
        (string path, _) = await LayerAsync();

        Assert.Equal(400, await StatusOfAsync(
            $"{path}/query?outStatistics=%5B%7B%22statisticType%22%3A%22count%22%2C"
            + "%22onStatisticField%22%3A%22nope%22%7D%5D&f=json"));
    }

    [Fact]
    public async Task Two_response_shapes_at_once_are_refused()
    {
        (string path, _) = await LayerAsync();

        Assert.Equal(400, await StatusOfAsync(
            $"{path}/query?returnCountOnly=true&returnIdsOnly=true&f=json"));
    }

    // ---------- geometry ----------

    [Fact]
    public async Task Every_spatial_relationship_is_answered_rather_than_refused()
    {
        // <b>All nine, because a partial implementation looked complete once.</b>
        // The index-only relations answered happily while every real predicate
        // errored on a mixed SRID, so "some of them work" is not evidence.
        (string path, _) = await LayerAsync();

        JsonElement extent = (await QueryAsync("where=1%3D1&returnExtentOnly=true"))
            .GetProperty("extent");

        string box = string.Join(",",
            extent.GetProperty("xmin").GetDouble(),
            extent.GetProperty("ymin").GetDouble(),
            extent.GetProperty("xmax").GetDouble(),
            extent.GetProperty("ymax").GetDouble());

        foreach (string relation in (string[])
        [
            "Intersects", "Contains", "Crosses", "EnvelopeIntersects", "IndexIntersects",
            "Overlaps", "Touches", "Within",
        ])
        {
            Assert.Equal(
                200,
                await StatusOfAsync(
                    $"{path}/query?geometry={Uri.EscapeDataString(box)}"
                    + "&geometryType=esriGeometryEnvelope"
                    + $"&spatialRel=esriSpatialRel{relation}&returnCountOnly=true&f=json"));
        }
    }

    [Fact]
    public async Task Intersects_is_the_union_of_within_and_overlaps()
    {
        // A relation that returns the same count as every other relation is a
        // relation that is not being applied. This asserts they differ in the
        // way the definitions require.
        JsonElement extent = (await QueryAsync("where=1%3D1&returnExtentOnly=true"))
            .GetProperty("extent");

        double midX =
            (extent.GetProperty("xmin").GetDouble() + extent.GetProperty("xmax").GetDouble()) / 2;

        double midY =
            (extent.GetProperty("ymin").GetDouble() + extent.GetProperty("ymax").GetDouble()) / 2;

        string box = Uri.EscapeDataString(string.Join(",",
            extent.GetProperty("xmin").GetDouble(), extent.GetProperty("ymin").GetDouble(),
            midX, midY));

        async Task<int> CountAsync(string relation) =>
            (await QueryAsync(
                $"geometry={box}&geometryType=esriGeometryEnvelope"
                + $"&spatialRel=esriSpatialRel{relation}&returnCountOnly=true"))
                .GetProperty("count").GetInt32();

        int intersects = await CountAsync("Intersects");
        int within = await CountAsync("Within");
        int overlaps = await CountAsync("Overlaps");

        // Every feature inside the box, plus every one straddling its edge, is
        // every feature that meets it — for a polygon layer with no feature
        // merely touching the boundary.
        Assert.Equal(intersects, within + overlaps);
    }

    [Fact]
    public async Task A_relate_pattern_without_the_relation_is_refused_and_the_reverse_too()
    {
        (string path, _) = await LayerAsync();

        Assert.Equal(400, await StatusOfAsync(
            $"{path}/query?geometry=0,0,1,1&geometryType=esriGeometryEnvelope"
            + "&spatialRel=esriSpatialRelRelation&returnCountOnly=true&f=json"));

        Assert.Equal(400, await StatusOfAsync(
            $"{path}/query?geometry=0,0,1,1&geometryType=esriGeometryEnvelope"
            + "&relationParam=T%2A%2A%2A%2A%2A%2A%2A%2A&returnCountOnly=true&f=json"));
    }

    [Fact]
    public async Task A_spatial_parameter_without_a_geometry_is_refused()
    {
        // Ignoring it would mean answering an unfiltered query for a client that
        // believes it sent a filter.
        (string path, _) = await LayerAsync();

        Assert.Equal(400, await StatusOfAsync(
            $"{path}/query?spatialRel=esriSpatialRelWithin&returnCountOnly=true&f=json"));
    }

    [Fact]
    public async Task Distance_converts_between_units()
    {
        JsonElement extent = (await QueryAsync("where=1%3D1&returnExtentOnly=true"))
            .GetProperty("extent");

        string point = Uri.EscapeDataString(string.Join(",",
            (extent.GetProperty("xmin").GetDouble() + extent.GetProperty("xmax").GetDouble()) / 2,
            (extent.GetProperty("ymin").GetDouble() + extent.GetProperty("ymax").GetDouble()) / 2));

        async Task<int> CountAsync(string distance, string units) =>
            (await QueryAsync(
                $"geometry={point}&geometryType=esriGeometryPoint&distance={distance}"
                + $"&units={units}&returnCountOnly=true")).GetProperty("count").GetInt32();

        // The same distance said two ways must select the same features, or the
        // conversion table is decoration.
        Assert.Equal(
            await CountAsync("500", "esriSRUnit_Meter"),
            await CountAsync("0.5", "esriSRUnit_Kilometer"));
    }

    // ---------- output shaping ----------

    [Fact]
    public async Task OutSR_transforms_the_geometry_and_the_response_says_so()
    {
        // <b>Both halves, because reporting the layer's reference after
        // transforming is the failure that puts features in the Gulf of
        // Guinea.</b>
        JsonElement result = await QueryAsync("objectIds=1&outFields=*&outSR=4326");

        Assert.Equal(4326, result.GetProperty("spatialReference").GetProperty("wkid").GetInt32());

        JsonElement point = result.GetProperty("features")[0]
            .GetProperty("geometry").GetProperty("rings")[0][0];

        // Degrees, so within the bounds of the world rather than millions of
        // metres.
        Assert.InRange(point[0].GetDouble(), -180, 180);
        Assert.InRange(point[1].GetDouble(), -90, 90);
    }

    [Fact]
    public async Task GeometryPrecision_rounds_the_coordinates()
    {
        JsonElement rounded = await QueryAsync("objectIds=1&outFields=*&geometryPrecision=1");

        foreach (JsonElement point in rounded.GetProperty("features")[0]
            .GetProperty("geometry").GetProperty("rings")[0].EnumerateArray())
        {
            double x = point[0].GetDouble();

            Assert.Equal(Math.Round(x, 1), x, 6);
        }
    }

    [Fact]
    public async Task MaxAllowableOffset_returns_fewer_vertices_not_more()
    {
        JsonElement full = await QueryAsync("objectIds=1&outFields=*");
        JsonElement thinned = await QueryAsync("objectIds=1&outFields=*&maxAllowableOffset=50");

        int before = full.GetProperty("features")[0]
            .GetProperty("geometry").GetProperty("rings")[0].GetArrayLength();

        int after = thinned.GetProperty("features")[0]
            .GetProperty("geometry").GetProperty("rings")[0].GetArrayLength();

        Assert.True(
            after <= before,
            $"Generalising to 50 units produced {after} vertices where the original had {before}.");
    }

    [Fact]
    public async Task Distinct_needs_no_geometry_and_says_so()
    {
        (string path, _) = await LayerAsync();

        // Two features with identical attributes still have different shapes, so
        // the combination has no answer — and ArcGIS refuses it too.
        Assert.Equal(400, await StatusOfAsync(
            $"{path}/query?outFields=*&returnDistinctValues=true&returnGeometry=true&f=json"));
    }
}
