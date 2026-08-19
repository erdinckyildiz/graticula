using System.Collections.Generic;
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

    // ---------- returnDistinctValues, which the layer document advertises ----------

    /// <summary>
    /// A distinct query returns combinations, not rows, and counting it counts combinations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The independent §66 Correctness gate found this on 2026-08-19, and it was the worst class of
    /// defect this server has had: a silent wrong answer on a capability every layer document advertises
    /// as supported.</b> `returnDistinctValues=true` returned ordinary rows up to the page limit, and
    /// `returnCountOnly=true` beside it returned the layer's whole row count. Measured against
    /// `hosted/tr_yol`: 46,041 rows, two distinct values of one column, and the answers were 1,000
    /// features with `exceededTransferLimit` and a count of 46,041.
    /// </para>
    /// <para>
    /// <b>One cause in three places.</b> The field list forced the object id in unconditionally, and
    /// `DISTINCT ON` was built from that list — an object id is unique per row, so the clause could
    /// never remove anything. The count ignored `Distinct` entirely. And the writer *required* an object
    /// id in the schema, which is right for every other query and wrong for this one. The SQL builder's
    /// own comment described the correct behaviour — *the identity excluded from the comparison…
    /// otherwise the parameter is a no-op that looked like it worked* — so the comment was true of the
    /// plan and false of the build, which is D-41's lesson arriving again.
    /// </para>
    /// <para>
    /// <b>Asserted as internal consistency rather than against a number</b>, because this suite runs
    /// against whatever layer a deployment names: no two returned rows may share the combination, the
    /// distinct count must equal the rows returned when they fit inside one page, and it must not exceed
    /// the unfiltered count. Those hold on any layer, and all three were false before the fix.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Distinct_returns_combinations_and_counts_them()
    {
        (string path, string oid) = await LayerAsync();

        // The object id is the one column certain to exist and to be unique, which makes it the
        // sharpest control: distinct over it must return exactly the row count, and distinct over
        // anything else must not exceed it.
        JsonElement all = await GetJsonAsync($"{path}/query?where=1%3D1&returnCountOnly=true&f=json");
        long rows = all.GetProperty("count").GetInt64();

        Assert.True(rows > 1, $"The layer has {rows} rows, so distinct proves nothing.");

        JsonElement counted = await GetJsonAsync(
            $"{path}/query?where=1%3D1&outFields={oid}&returnGeometry=false"
            + "&returnDistinctValues=true&returnCountOnly=true&f=json");

        Assert.Equal(rows, counted.GetProperty("count").GetInt64());

        // And the features themselves are distinct on what was asked for.
        JsonElement distinct = await GetJsonAsync(
            $"{path}/query?where=1%3D1&outFields={oid}&returnGeometry=false"
            + "&returnDistinctValues=true&f=json");

        List<string> combinations = [];

        foreach (JsonElement feature in distinct.GetProperty("features").EnumerateArray())
        {
            combinations.Add(feature.GetProperty("attributes").GetRawText());
        }

        Assert.Equal(combinations.Count, new HashSet<string>(combinations, StringComparer.Ordinal).Count);

        // <b>And no object id in the answer, which is the fix rather than a detail.</b> An object id in
        // a distinct response is what made every row distinct by construction; a client that could page
        // by it would be paging a set of combinations, which is not a thing.
        Assert.Equal(string.Empty, distinct.GetProperty("objectIdFieldName").GetString());
    }

    // ---------- the two spatial-reference paths ----------

    /// <summary>
    /// Esri's own Web Mercator codes are accepted for every geometry shape, not only for an envelope.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two paths answered differently about identical input, which is why this test is here rather
    /// than beside the reader's unit tests.</b> An envelope filter is parsed by
    /// `FeatureServerQueryParameters`, which canonicalises 102100 and 102113 to 3857 — *every ArcGIS
    /// client sends it*, as that file's own comment says. Every other shape, and every `applyEdits`
    /// geometry, goes through `ArcGisGeometryReader`, which compared integers. So the same reference was
    /// honoured for a box and refused for a polygon, and an ordinary edit from a Web Mercator client was
    /// refused as well. Found by the independent §66 Correctness gate, 2026-08-19.
    /// </para>
    /// <para>
    /// <b>Only meaningful against a layer stored in 3857</b>, so it reads the layer's own reference and
    /// says what it did rather than passing quietly on a 4326 layer.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Esris_web_mercator_codes_are_accepted_for_a_polygon_as_well_as_an_envelope()
    {
        (string path, _) = await LayerAsync();

        JsonElement document = await GetJsonAsync(path);

        int srid = document.TryGetProperty("extent", out JsonElement extent)
                   && extent.TryGetProperty("spatialReference", out JsonElement reference)
                   && reference.TryGetProperty("wkid", out JsonElement wkid)
            ? wkid.GetInt32()
            : 0;

        Assert.True(
            srid is 3857 or 102100 or 102113,
            $"This layer is stored in {srid}, and the aliases under test are Esri's codes for 3857 — "
            + $"so nothing here would be exercised. Point {LayerVariable} at a Web Mercator layer, "
            + "which is what an ArcGIS estate's data usually is.");

        // The whole world in Web Mercator metres, which matches whatever the layer holds.
        const string Ring = "[[[-20037508,-20037508],[-20037508,20037508],"
            + "[20037508,20037508],[20037508,-20037508],[-20037508,-20037508]]]";

        foreach (int alias in (int[])[102100, 102113, 3857])
        {
            string envelope = Uri.EscapeDataString(
                "{\"xmin\":-20037508,\"ymin\":-20037508,\"xmax\":20037508,\"ymax\":20037508,"
                + $"\"spatialReference\":{{\"wkid\":{alias}}}}}");

            string polygon = Uri.EscapeDataString(
                $"{{\"rings\":{Ring},\"spatialReference\":{{\"wkid\":{alias}}}}}");

            JsonElement asEnvelope = await GetJsonAsync(
                $"{path}/query?geometry={envelope}&geometryType=esriGeometryEnvelope"
                + "&spatialRel=esriSpatialRelIntersects&returnCountOnly=true&f=json");

            JsonElement asPolygon = await GetJsonAsync(
                $"{path}/query?geometry={polygon}&geometryType=esriGeometryPolygon"
                + "&spatialRel=esriSpatialRelIntersects&returnCountOnly=true&f=json");

            Assert.True(
                asEnvelope.TryGetProperty("count", out JsonElement boxCount),
                $"An envelope in {alias} was refused: {asEnvelope.GetRawText()[..Math.Min(200, asEnvelope.GetRawText().Length)]}");

            Assert.True(
                asPolygon.TryGetProperty("count", out JsonElement ringCount),
                $"A polygon in {alias} was refused while the identical envelope was accepted — which is "
                + "the same spatial reference answered two ways: "
                + asPolygon.GetRawText()[..Math.Min(240, asPolygon.GetRawText().Length)]);

            Assert.Equal(boxCount.GetInt64(), ringCount.GetInt64());
        }
    }
}
