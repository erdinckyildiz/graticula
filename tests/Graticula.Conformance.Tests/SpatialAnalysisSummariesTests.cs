using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-222 conditions 3 and 4: bins, <c>SummarizeWithin</c>, <c>JoinFeatures</c>, a result returned without being saved,
/// and a feature collection as an input — each tested by a relation the fixture cannot move.
/// </summary>
/// <remarks>
/// <para>
/// <b>Relations rather than counts.</b> The suite's layers differ between CI and a local fixture, so no number of
/// features is asserted: bins hold every point once; a summary keeps every polygon and measures no more of a polygon
/// than it covers; a one-to-many join has one row per pair a one-to-one join counted; a left join keeps every target.
/// </para>
/// <para>
/// Written 2026-10-29, with the tools.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class SpatialAnalysisSummariesTests : ArcGisClient
{
    private const string Service = "/rest/services/System/SpatialAnalysisTools/GPServer";

    private static HttpClient Unverified() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    [Fact]
    public async Task Bins_summaries_and_joins_hold_their_relations_and_a_result_may_be_returned_unsaved()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string Layer(string variable, string fallback) =>
            $"{root}/rest/services/{(Environment.GetEnvironmentVariable(variable) ?? fallback).Trim('/')}/FeatureServer/0";
        string buildings = Layer(QueryCapabilityConformanceTests.LayerVariable, "hosted/ci_buildings");
        string parcels = Layer("GRATICULA_TEST_TILE_SERVICE", "hosted/ci_parcels");
        string points = Layer("GRATICULA_TEST_TEMPORAL", "hosted/ci_observations");
        using HttpClient http = Unverified();

        async Task<int> CountAsync(string url)
        {
            JsonElement counted = await GetAsync(http, token, $"{url}/query?where=1%3D1&returnCountOnly=true&f=json");
            return counted.GetProperty("count").GetInt32();
        }

        int pointCount = await CountAsync(points);
        int parcelCount = await CountAsync(parcels);

        // ---- bins: every point in a bin, once; no empty bin
        JsonElement[] hexagons = Features(await ResultAsync(http, root, token, "AggregatePoints", "aggregatedLayer", new()
        {
            ["pointLayer"] = Json(new { url = points }),
            ["binType"] = "Hexagon",
            ["binSize"] = "750",
            ["binSizeUnit"] = "Meters",
        }));
        Assert.NotEmpty(hexagons);
        Assert.All(hexagons, h => Assert.True(Attribute(h, "point_count").GetInt32() >= 1, "A bin with no point was kept."));
        Assert.Equal(pointCount, hexagons.Sum(h => Attribute(h, "point_count").GetInt32()));
        Assert.Equal(7, hexagons[0].GetProperty("geometry").GetProperty("rings")[0].GetArrayLength());

        // ---- SummarizeWithin, bins: the same relation through the other task
        JsonElement[] squares = Features(await ResultAsync(http, root, token, "SummarizeWithin", "resultLayer", new()
        {
            ["summaryLayer"] = Json(new { url = points }),
            ["binType"] = "Square",
            ["binSize"] = "1",
            ["binSizeUnit"] = "Kilometers",
        }));
        Assert.Equal(pointCount, squares.Sum(s => Attribute(s, "point_count").GetInt32()));

        // ---- SummarizeWithin, polygons in polygons: every parcel kept, and no more area inside one than it has
        JsonElement[] summarized = Features(await ResultAsync(http, root, token, "SummarizeWithin", "resultLayer", new()
        {
            ["sumWithinLayer"] = Json(new { url = parcels }),
            ["summaryLayer"] = Json(new { url = buildings }),
            ["shapeUnits"] = "SquareMeters",
        }));
        Assert.Equal(parcelCount, summarized.Length);
        Assert.Contains(summarized, s => Attribute(s, "count").GetInt32() > 0);

        JsonElement areas = await GetAsync(http, token,
            $"{root}/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths?f=json&sr=3857&calculationType=geodesic&areaUnit="
            + Uri.EscapeDataString("{\"areaUnit\":\"esriSquareMeters\"}") + "&polygons="
            + Uri.EscapeDataString(JsonSerializer.Serialize(summarized.Select(s => s.GetProperty("geometry")))));
        double[] whole = [.. areas.GetProperty("areas").EnumerateArray().Select(a => Math.Abs(a.GetDouble()))];

        for (int i = 0; i < summarized.Length; i++)
        {
            double inside = Attribute(summarized[i], "sum_area_squaremeters").GetDouble();
            Assert.True(inside <= whole[i] * 1.001 + 1, $"Parcel {i} holds {inside:F1} m² of buildings and covers {whole[i]:F1} m².");
        }

        // ---- JoinFeatures: one-to-many has a row per pair one-to-one counted; left keeps every target
        JsonElement[] oneToOne = Features(await ResultAsync(http, root, token, "JoinFeatures", "outputLayer", new()
        {
            ["targetLayer"] = Json(new { url = parcels }),
            ["joinLayer"] = Json(new { url = buildings }),
            ["spatialRelationship"] = "Intersects",
        }));
        Assert.All(oneToOne, j => Assert.True(Attribute(j, "count").GetInt32() >= 1, "An inner join kept a target with no match."));

        JsonElement[] oneToMany = Features(await ResultAsync(http, root, token, "JoinFeatures", "outputLayer", new()
        {
            ["targetLayer"] = Json(new { url = parcels }),
            ["joinLayer"] = Json(new { url = buildings }),
            ["spatialRelationship"] = "Intersects",
            ["joinOperation"] = "JoinOneToMany",
        }));
        Assert.Equal(oneToOne.Sum(j => Attribute(j, "count").GetInt32()), oneToMany.Length);

        JsonElement[] left = Features(await ResultAsync(http, root, token, "JoinFeatures", "outputLayer", new()
        {
            ["targetLayer"] = Json(new { url = parcels }),
            ["joinLayer"] = Json(new { url = buildings }),
            ["spatialRelationship"] = "Intersects",
            ["joinType"] = "Left",
            ["summaryFields"] = Json(new[] { new { statisticType = "MAX", onStatisticField = "floors" } }),
        }));
        Assert.Equal(parcelCount, left.Length);
        Assert.True(left[0].GetProperty("attributes").TryGetProperty("max_floors", out _), $"No max_floors: {left[0]}");

        // ---- a collection as the input, buffered and returned
        JsonElement buffered = await ResultAsync(http, root, token, "CreateBuffers", "bufferLayer", new()
        {
            ["inputLayer"] = Json(new
            {
                layerDefinition = new
                {
                    geometryType = "esriGeometryPoint",
                    fields = new object[] { new { name = "Label", type = "esriFieldTypeString" } },
                },
                featureSet = new
                {
                    geometryType = "esriGeometryPoint",
                    spatialReference = new { wkid = 4326 },
                    features = new object[]
                    {
                        new { attributes = new { Label = "a" }, geometry = new { x = 28.97, y = 41.01 } },
                        new { attributes = new { Label = "b" }, geometry = new { x = 28.98, y = 41.02 } },
                    },
                },
            }),
            ["distances"] = "[100]",
        });
        JsonElement[] rings = Features(buffered);
        Assert.Equal(2, rings.Length);
        Assert.Equal("esriGeometryPolygon", buffered.GetProperty("value").GetProperty("featureSet").GetProperty("geometryType").GetString());
        Assert.Equal(["a", "b"], rings.Select(r => Attribute(r, "label").GetString()).Order());
        Assert.All(rings, r => Assert.Equal(100, Attribute(r, "buff_dist").GetDouble()));
        Assert.Equal("objectid", buffered.GetProperty("value").GetProperty("layerDefinition").GetProperty("fields")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Sides_ends_outputs_and_snapping_are_built_as_documented()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string buildings = $"{root}/rest/services/{(Environment.GetEnvironmentVariable(QueryCapabilityConformanceTests.LayerVariable) ?? "hosted/ci_buildings").Trim('/')}/FeatureServer/0";
        string parcels = $"{root}/rest/services/{(Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE") ?? "hosted/ci_parcels").Trim('/')}/FeatureServer/0";
        using HttpClient http = Unverified();

        // ---- Outside a polygon: less than the whole buffer, feature by feature
        async Task<Dictionary<int, double>> AreasAsync(Dictionary<string, string> form) =>
            Features(await ResultAsync(http, root, token, "CreateBuffers", "bufferLayer", form))
                .ToDictionary(f => Attribute(f, "orig_fid").GetInt32(), f => Attribute(f, "analysisarea").GetDouble());

        Dictionary<int, double> full = await AreasAsync(new() { ["inputLayer"] = Json(new { url = buildings }), ["distances"] = "[50]" });
        Dictionary<int, double> outside = await AreasAsync(new()
        {
            ["inputLayer"] = Json(new { url = buildings }), ["distances"] = "[50]", ["sideType"] = "Outside",
        });
        Assert.Equal(full.Keys.Order(), outside.Keys.Order());
        Assert.All(outside, o => Assert.True(o.Value < full[o.Key], $"Feature {o.Key}'s outside buffer is not smaller than its whole one."));

        // ---- a line, sent as a collection: one side is about half, flat ends less than round
        string line = Json(new
        {
            layerDefinition = new { geometryType = "esriGeometryPolyline", fields = Array.Empty<object>() },
            featureSet = new
            {
                geometryType = "esriGeometryPolyline",
                spatialReference = new { wkid = 3857 },
                features = new object[] { new { attributes = new { }, geometry = new { paths = new[] { new[] { new[] { 3657000.0, 4862000 }, new[] { 3658000.0, 4862000 } } } } } },
            },
        });
        double Area(JsonElement result) => Attribute(Features(result).Single(), "analysisarea").GetDouble();
        double both = Area(await ResultAsync(http, root, token, "CreateBuffers", "bufferLayer", new() { ["inputLayer"] = line, ["distances"] = "[100]" }));
        double left = Area(await ResultAsync(http, root, token, "CreateBuffers", "bufferLayer", new() { ["inputLayer"] = line, ["distances"] = "[100]", ["sideType"] = "Left" }));
        double flat = Area(await ResultAsync(http, root, token, "CreateBuffers", "bufferLayer", new() { ["inputLayer"] = line, ["distances"] = "[100]", ["endType"] = "Flat" }));
        Assert.InRange(left / both, 0.35, 0.5);
        Assert.True(flat < both, $"Flat ends ({flat}) are not less than round ones ({both}).");

        // ---- outputType: the meeting lines and points of two polygon layers
        JsonElement lines = await ResultAsync(http, root, token, "OverlayLayers", "outputLayer", new()
        {
            ["inputLayer"] = Json(new { url = buildings }), ["overlayLayer"] = Json(new { url = parcels }), ["outputType"] = "Line",
        });
        Assert.Equal("esriGeometryPolyline", lines.GetProperty("value").GetProperty("featureSet").GetProperty("geometryType").GetString());
        JsonElement points = await ResultAsync(http, root, token, "OverlayLayers", "outputLayer", new()
        {
            ["inputLayer"] = Json(new { url = buildings }), ["overlayLayer"] = Json(new { url = parcels }), ["outputType"] = "Point",
        });
        Assert.Equal("esriGeometryMultipoint", points.GetProperty("value").GetProperty("featureSet").GetProperty("geometryType").GetString());

        // ---- snapping: an intersection with a tolerance runs, and keeps at least what it kept without one
        int plain = Features(await ResultAsync(http, root, token, "OverlayLayers", "outputLayer", new()
        {
            ["inputLayer"] = Json(new { url = buildings }), ["overlayLayer"] = Json(new { url = parcels }),
        })).Length;
        int snapped = Features(await ResultAsync(http, root, token, "OverlayLayers", "outputLayer", new()
        {
            ["inputLayer"] = Json(new { url = buildings }), ["overlayLayer"] = Json(new { url = parcels }), ["tolerance"] = "1",
        })).Length;
        Assert.True(snapped >= plain, $"Snapping within 1 unit lost pieces: {snapped} against {plain}.");
    }

    [Fact]
    public async Task A_summary_grouped_by_a_field_writes_each_value_apart_with_its_share_and_the_majority()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string points = $"{root}/rest/services/{(Environment.GetEnvironmentVariable("GRATICULA_TEST_TEMPORAL") ?? "hosted/ci_observations").Trim('/')}/FeatureServer/0";
        using HttpClient http = Unverified();

        JsonElement fields = await GetAsync(http, token, $"{points}?f=json");
        string group = fields.GetProperty("fields").EnumerateArray()
            .Select(f => (Name: f.GetProperty("name").GetString()!, Type: f.GetProperty("type").GetString()!))
            .First(f => f.Type == "esriFieldTypeString").Name;
        int pointCount = (await GetAsync(http, token, $"{points}/query?where=1%3D1&returnCountOnly=true&f=json")).GetProperty("count").GetInt32();

        // ---- returned unsaved: the areas, and the groups as a table collection
        Dictionary<string, string> form = new()
        {
            ["pointLayer"] = Json(new { url = points }),
            ["binType"] = "Square",
            ["binSize"] = "1",
            ["binSizeUnit"] = "Kilometers",
            ["groupByField"] = group,
            ["minorityMajority"] = "true",
            ["percentPoints"] = "true",
        };
        (JsonElement areas, JsonElement groups) = await GroupedAsync(http, root, token, "AggregatePoints", "aggregatedLayer", "groupSummary", form);

        JsonElement[] bins = Features(areas);
        JsonElement[] rows = [.. groups.GetProperty("value").GetProperty("featureSet").GetProperty("features").EnumerateArray()];
        Assert.Equal("Table", groups.GetProperty("value").GetProperty("layerDefinition").GetProperty("type").GetString());
        Assert.Equal(pointCount, rows.Sum(r => Attribute(r, "point_count").GetInt32()));

        foreach (IGrouping<int, JsonElement> area in rows.GroupBy(r => Attribute(r, "join_id").GetInt32()))
        {
            Assert.Equal(100, area.Sum(r => Attribute(r, "percent_points").GetDouble()), 3);

            // The majority on the area is a value of its group with the most points.
            JsonElement bin = bins.Single(b => Attribute(b, "join_id").GetInt32() == area.Key);
            int most = area.Max(r => Attribute(r, "point_count").GetInt32());
            Assert.Contains(Attribute(bin, $"majority_{group.ToLowerInvariant()}").GetString(),
                area.Where(r => Attribute(r, "point_count").GetInt32() == most).Select(r => Attribute(r, group.ToLowerInvariant()).GetString()));
        }

        // ---- saved: the groups are the result service's table
        string name = "zz_grp_" + Guid.NewGuid().ToString("N")[..8];
        form["outputName"] = Json(new { serviceProperties = new { name } });

        try
        {
            (JsonElement layer, JsonElement table) = await GroupedAsync(http, root, token, "AggregatePoints", "aggregatedLayer", "groupSummary", form);
            Assert.EndsWith("/FeatureServer/0", layer.GetProperty("value").GetProperty("url").GetString(), StringComparison.Ordinal);

            // Drawn by its count from the start (design review 2026-10-29), not in one flat colour.
            string layerUrl = layer.GetProperty("value").GetProperty("url").GetString()!;
            JsonElement drawn = await GetAsync(http, token, $"{(layerUrl.StartsWith("http", StringComparison.Ordinal) ? layerUrl : root + layerUrl)}?f=json");
            JsonElement renderer = drawn.GetProperty("drawingInfo").GetProperty("renderer");
            Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
            Assert.Equal("point_count", renderer.GetProperty("field").GetString());
            string tableUrl = table.GetProperty("value").GetProperty("url").GetString()!;
            Assert.EndsWith("/FeatureServer/1", tableUrl, StringComparison.Ordinal);
            JsonElement described = await GetAsync(http, token, $"{(tableUrl.StartsWith("http", StringComparison.Ordinal) ? tableUrl : root + tableUrl)}?f=json");
            Assert.Equal("Table", described.GetProperty("type").GetString());
        }
        finally
        {
            (int status, string body) = await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");

            if (status == 200 && JsonDocument.Parse(body).RootElement.TryGetProperty("recycled", out JsonElement entry) && entry.ValueKind == JsonValueKind.String)
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
            }
        }
    }

    [Fact]
    public async Task A_result_overwrites_a_layer_of_its_own_kind_and_keeps_its_item()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string buildings = $"{root}/rest/services/{(Environment.GetEnvironmentVariable(QueryCapabilityConformanceTests.LayerVariable) ?? "hosted/ci_buildings").Trim('/')}/FeatureServer/0";
        string name = "zz_ow_" + Guid.NewGuid().ToString("N")[..8];
        using HttpClient http = Unverified();

        async Task<JsonElement> SavedAsync(string task, string result, Dictionary<string, string> form)
        {
            (JsonElement value, _) = await GroupedAsync(http, root, token, task, result, null, form);
            return value.GetProperty("value");
        }

        async Task<double> AreaAsync(string url) =>
            (await GetAsync(http, token, $"{(url.StartsWith("http", StringComparison.Ordinal) ? url : root + url)}/query?where=1%3D1&outFields=analysisarea&returnGeometry=false&f=json"))
                .GetProperty("features").EnumerateArray().Sum(f => Attribute(f, "analysisarea").GetDouble());

        try
        {
            JsonElement first = await SavedAsync("CreateBuffers", "bufferLayer", new()
            {
                ["inputLayer"] = Json(new { url = buildings }), ["distances"] = "[50]",
                ["outputName"] = Json(new { serviceProperties = new { name } }),
            });
            string itemId = first.GetProperty("itemId").GetString()!;
            double before = await AreaAsync(first.GetProperty("url").GetString()!);

            JsonElement second = await SavedAsync("CreateBuffers", "bufferLayer", new()
            {
                ["inputLayer"] = Json(new { url = buildings }), ["distances"] = "[200]",
                ["outputName"] = Json(new { itemProperties = new { itemId, overwrite = true } }),
            });
            Assert.Equal(itemId, second.GetProperty("itemId").GetString());
            Assert.Equal(first.GetProperty("url").GetString(), second.GetProperty("url").GetString());
            Assert.True(await AreaAsync(second.GetProperty("url").GetString()!) > before, "The overwritten layer still holds the first run's buffers.");

            // Another kind is refused before it is queued.
            using HttpRequestMessage points = new(HttpMethod.Post, new Uri($"{root}{Service}/FindCentroids/submitJob"))
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json", ["inputLayer"] = Json(new { url = buildings }),
                    ["outputName"] = Json(new { itemProperties = new { itemId, overwrite = true } }),
                }),
            };
            points.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage refused = await http.SendAsync(points);
            Assert.Equal(400, (int)refused.StatusCode);
            Assert.Contains("only with its own kind", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            (int status, string body) = await AdminAsync(HttpMethod.Delete, $"/admin/featureservices/{name}?folder=hosted&drop=true");

            if (status == 200 && JsonDocument.Parse(body).RootElement.TryGetProperty("recycled", out JsonElement entry) && entry.ValueKind == JsonValueKind.String)
            {
                await AdminAsync(HttpMethod.Delete, $"/admin/recycle-bin/{entry.GetString()}");
            }
        }
    }

    /// <summary>Submits a grouped summary and answers both of its result parameters.</summary>
    private static async Task<(JsonElement Areas, JsonElement Groups)> GroupedAsync(
        HttpClient http, string root, string token, string task, string result, string? groupResult, Dictionary<string, string> form)
    {
        form["f"] = "json";
        using HttpRequestMessage submit = new(HttpMethod.Post, new Uri($"{root}{Service}/{task}/submitJob")) { Content = new FormUrlEncodedContent(form) };
        submit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage submitted = await http.SendAsync(submit);
        JsonElement job = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.True(job.TryGetProperty("jobId", out JsonElement id), $"{task} was not submitted: {job}");

        JsonElement status = default;

        for (int i = 0; i < 120; i++)
        {
            status = await GetAsync(http, token, $"{root}{Service}/{task}/jobs/{id.GetString()}?f=json");

            if (status.GetProperty("jobStatus").GetString() is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled")
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.True(status.GetProperty("jobStatus").GetString() == "esriJobSucceeded", $"{task} did not succeed: {status}");
        JsonElement main = await GetAsync(http, token, $"{root}{Service}/{task}/jobs/{id.GetString()}/results/{result}?f=json");

        if (groupResult is null)
        {
            return (main, default);
        }

        Assert.True(status.GetProperty("results").TryGetProperty(groupResult, out _), $"A grouped {task} lists no {groupResult}: {status}");
        return (main, await GetAsync(http, token, $"{root}{Service}/{task}/jobs/{id.GetString()}/results/{groupResult}?f=json"));
    }

    [Theory]
    [InlineData("AggregatePoints", "binType=Hexagon", "binSize is required")]
    [InlineData("JoinFeatures", "", "needs a spatialRelationship")]
    [InlineData("JoinFeatures", "spatialRelationship=Intersects&joinOperation=JoinOneToMany&summaryFields=[\"floors Max\"]", "one-to-one join")]
    [InlineData("SummarizeWithin", "sumShape=false", "summaryFields is required")]
    [InlineData("JoinFeatures", "spatialRelationship=Intersects&joinType=Sideways", "joinType is Inner or Left")]
    [InlineData("AggregatePoints", "binType=Square&binSize=1&groupByField=nothing_here", "no 'nothing_here' to group by")]
    public async Task What_cannot_be_run_is_refused_before_it_is_queued(string task, string extra, string said)
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        string points = $"{root}/rest/services/{(Environment.GetEnvironmentVariable("GRATICULA_TEST_TEMPORAL") ?? "hosted/ci_observations").Trim('/')}/FeatureServer/0";
        string parcels = $"{root}/rest/services/{(Environment.GetEnvironmentVariable("GRATICULA_TEST_TILE_SERVICE") ?? "hosted/ci_parcels").Trim('/')}/FeatureServer/0";
        using HttpClient http = Unverified();

        Dictionary<string, string> form = new() { ["f"] = "json" };

        switch (task)
        {
            case "AggregatePoints":
                form["pointLayer"] = Json(new { url = points });
                break;
            case "JoinFeatures":
                form["targetLayer"] = Json(new { url = parcels });
                form["joinLayer"] = Json(new { url = parcels });
                break;
            default:
                form["summaryLayer"] = Json(new { url = points });
                form["sumWithinLayer"] = Json(new { url = parcels });
                break;
        }

        foreach (string pair in extra.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            form[pair[..pair.IndexOf('=', StringComparison.Ordinal)]] = pair[(pair.IndexOf('=', StringComparison.Ordinal) + 1)..];
        }

        using HttpRequestMessage request = new(HttpMethod.Post, new Uri($"{root}{Service}/{task}/submitJob")) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(400, (int)response.StatusCode);
        Assert.Contains(said, body, StringComparison.Ordinal);
    }

    /// <summary>The area around both squares of the null-weight test.</summary>
    private static readonly string[] Summaries = ["v Mean", "v Sum"];

    private static readonly double[][][] Around = [[[-0.01, -0.01], [-0.01, 0.02], [0.04, 0.02], [0.04, -0.01], [-0.01, -0.01]]];

    /// <summary>
    /// A feature whose value is null takes no part in a weighted statistic, its weight included. Two squares of equal
    /// area lie wholly inside one area: one holds 10, the other null. ArcGIS documents nulls as excluded from every
    /// statistic, so the mean is 10. The weighted mean used to divide by both squares' parts and answered 5. Found
    /// 2026-10-30, when grapy's SummarizeWithin was built to these names and checked against the documentation.
    /// </summary>
    [Fact]
    public async Task A_null_value_is_left_out_of_a_weighted_mean_with_its_weight()
    {
        string root = await RequireServerAsync();
        string token = (await TokenAsync(root))!;
        using HttpClient http = Unverified();

        static object Square(double x, double size) => new
        {
            rings = new[] { new[] { new[] { x, 0.0 }, new[] { x, size }, new[] { x + size, size }, new[] { x + size, 0.0 }, new[] { x, 0.0 } } },
        };

        JsonElement summary = await ResultAsync(http, root, token, "SummarizeWithin", "resultLayer", new()
        {
            ["sumWithinLayer"] = Json(new
            {
                layerDefinition = new { geometryType = "esriGeometryPolygon", fields = new object[] { new { name = "zone", type = "esriFieldTypeString" } } },
                featureSet = new
                {
                    geometryType = "esriGeometryPolygon",
                    spatialReference = new { wkid = 4326 },
                    features = new object[]
                    {
                        new
                        {
                            attributes = new { zone = "all" },
                            geometry = new { rings = Around },
                        },
                    },
                },
            }),
            ["summaryLayer"] = Json(new
            {
                layerDefinition = new { geometryType = "esriGeometryPolygon", fields = new object[] { new { name = "v", type = "esriFieldTypeDouble" } } },
                featureSet = new
                {
                    geometryType = "esriGeometryPolygon",
                    spatialReference = new { wkid = 4326 },
                    features = new object[]
                    {
                        new { attributes = new Dictionary<string, object?> { ["v"] = 10.0 }, geometry = Square(0.0, 0.01) },
                        new { attributes = new Dictionary<string, object?> { ["v"] = null }, geometry = Square(0.02, 0.01) },
                    },
                },
            }),
            ["summaryFields"] = Json(Summaries),
        });

        JsonElement area = Assert.Single(Features(summary));
        Assert.Equal(10.0, Attribute(area, "mean_v").GetDouble(), 9);
        Assert.Equal(10.0, Attribute(area, "sum_v").GetDouble(), 9);
        Assert.Equal(2, Attribute(area, "count").GetInt32());
    }

    private static JsonElement[] Features(JsonElement result) =>
        [.. result.GetProperty("value").GetProperty("featureSet").GetProperty("features").EnumerateArray()];

    private static JsonElement Attribute(JsonElement feature, string name) =>
        feature.GetProperty("attributes").GetProperty(name);

    private static async Task<JsonElement> GetAsync(HttpClient http, string token, string url)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await http.SendAsync(request);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>Submits a job, waits for it, and answers its result parameter — a feature collection, since none is named.</summary>
    private static async Task<JsonElement> ResultAsync(
        HttpClient http, string root, string token, string task, string result, Dictionary<string, string> form)
    {
        form["f"] = "json";
        using HttpRequestMessage submit = new(HttpMethod.Post, new Uri($"{root}{Service}/{task}/submitJob")) { Content = new FormUrlEncodedContent(form) };
        submit.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage submitted = await http.SendAsync(submit);
        JsonElement job = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync()).RootElement.Clone();
        Assert.True(job.TryGetProperty("jobId", out JsonElement id), $"{task} was not submitted: {job}");

        JsonElement status = default;

        for (int i = 0; i < 120; i++)
        {
            status = await GetAsync(http, token, $"{root}{Service}/{task}/jobs/{id.GetString()}?f=json");

            if (status.GetProperty("jobStatus").GetString() is "esriJobSucceeded" or "esriJobFailed" or "esriJobCancelled")
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.True(status.GetProperty("jobStatus").GetString() == "esriJobSucceeded", $"{task} did not succeed: {status}");
        JsonElement value = await GetAsync(http, token, $"{root}{Service}/{task}/jobs/{id.GetString()}/results/{result}?f=json");
        Assert.Equal("GPString", value.GetProperty("dataType").GetString());
        Assert.True(value.GetProperty("value").TryGetProperty("featureSet", out _), $"{task} named no output and returned no collection: {value}");
        return value;
    }

    private static string Json(object value) => JsonSerializer.Serialize(value);
}
