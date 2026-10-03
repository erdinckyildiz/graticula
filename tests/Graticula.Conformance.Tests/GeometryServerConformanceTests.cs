using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// GeometryServer over HTTP: what it does, and what it says when it will not.
/// </summary>
/// <remarks>
/// The refusals carry as much weight as the operations. Half this surface is
/// deliberately absent, and a client meeting a 501 needs to learn that the
/// server made a decision rather than that it is broken.
/// </remarks>
public sealed class GeometryServerConformanceTests : ArcGisClient
{
    private const string Root = "/rest/services/Utilities/Geometry/GeometryServer";

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private async Task<JsonElement> PostAsync(
        string operation, params (string Key, string Value)[] fields)
    {
        string root = await RequireServerAsync();
        using HttpClient http = Client();

        List<KeyValuePair<string, string>> form = [];

        foreach ((string key, string value) in fields)
        {
            form.Add(new KeyValuePair<string, string>(key, value));
        }

        using FormUrlEncodedContent content = new(form);
        using HttpRequestMessage request =
            new(HttpMethod.Post, new Uri(root + Root + "/" + operation)) { Content = content };

        // The geometry service is shared with the organisation, not the public
        // — owner correction 2026-08-15 — so these calls sign in. The test that
        // anonymous access is refused is below, and it deliberately does not.
        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<int> StatusOfPostAsync(string operation)
    {
        string root = await RequireServerAsync();
        using HttpClient http = Client();

        using FormUrlEncodedContent content = new([new KeyValuePair<string, string>("f", "json")]);
        using HttpRequestMessage request =
            new(HttpMethod.Post, new Uri(root + Root + "/" + operation)) { Content = content };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        return (int)response.StatusCode;
    }

    // ---------- discovery ----------

    [Fact]
    public async Task The_service_lists_what_it_does_and_what_it_does_not()
    {
        // ArcGIS clients probe by calling. Saying so up front turns a series of
        // 501s into one document.
        JsonElement service = await GetJsonAsync(Root);

        Assert.True(service.GetProperty("supportedOperations").GetArrayLength() > 0);
        // ADR-160: findTransformations was the last refusal; none is left.
        Assert.Equal(0, service.GetProperty("unsupportedOperations").GetArrayLength());
        Assert.Contains("findTransformations", service.GetProperty("supportedOperations").EnumerateArray().Select(o => o.GetString()));
        Assert.True(service.GetProperty("maximumVertices").GetInt32() > 0);
    }

    // ---------- project ----------

    [Fact]
    public async Task Projecting_Istanbul_from_degrees_to_metres_lands_where_it_should()
    {
        // 28.9784E 41.0082N is Sultanahmet. In Web Mercator that is roughly
        // 3,225,861 / 5,013,551 — checkable against any projection tool, which
        // is the point of asserting it rather than whatever the server returned.
        JsonElement result = await PostAsync(
            "project",
            ("inSR", "4326"),
            ("outSR", "102100"),
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\","
                + "\"geometries\":[{\"x\":28.9784,\"y\":41.0082}]}"),
            ("f", "json"));

        JsonElement point = result.GetProperty("geometries")[0];

        Assert.Equal(3_225_860.7, point.GetProperty("x").GetDouble(), 1);
        Assert.Equal(5_013_551.2, point.GetProperty("y").GetDouble(), 1);
    }

    [Fact]
    public async Task A_projection_says_which_engine_did_it()
    {
        // geometry-crs-policy §3: several transformation paths usually exist and
        // they differ by metres. A silent default is the problem; a documented
        // one is not.
        JsonElement transformation = (await PostAsync(
            "project",
            ("inSR", "4326"),
            ("outSR", "3857"),
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[{\"x\":0,\"y\":0}]}"),
            ("f", "json"))).GetProperty("transformation");

        Assert.False(string.IsNullOrWhiteSpace(transformation.GetProperty("engine").GetString()));
        Assert.Equal(4326, transformation.GetProperty("fromSR").GetInt32());
        Assert.Equal(3857, transformation.GetProperty("toSR").GetInt32());
    }

    [Fact]
    public async Task Esris_own_code_for_Web_Mercator_is_accepted()
    {
        // 102100 is what ArcGIS clients send. Comparing the number to 3857 and
        // refusing is what broke every FeatureServer request from a real SDK.
        JsonElement result = await PostAsync(
            "project",
            ("inSR", "{\"wkid\":4326}"),
            ("outSR", "102113"),
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[{\"x\":0,\"y\":0}]}"),
            ("f", "json"));

        Assert.Equal(1, result.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task A_batch_comes_back_in_the_order_it_went_in()
    {
        // The worst possible failure for a projection service: every coordinate
        // right, every one attached to the wrong input. Three points far apart
        // so a reordering cannot hide.
        JsonElement result = await PostAsync(
            "project",
            ("inSR", "4326"),
            ("outSR", "3857"),
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":["
                + "{\"x\":-90,\"y\":0},{\"x\":0,\"y\":0},{\"x\":90,\"y\":0}]}"),
            ("f", "json"));

        JsonElement points = result.GetProperty("geometries");

        Assert.Equal(3, points.GetArrayLength());
        Assert.True(points[0].GetProperty("x").GetDouble() < -1_000_000);
        Assert.Equal(0, points[1].GetProperty("x").GetDouble(), 3);
        Assert.True(points[2].GetProperty("x").GetDouble() > 1_000_000);
    }

    // ---------- measures ----------

    [Fact]
    public async Task A_square_with_a_hole_measures_what_arithmetic_says()
    {
        // 100x100 with a 20x20 hole: 9,600 of area, 480 of perimeter including
        // the hole's boundary. Shell clockwise, as ArcGIS winds them.
        JsonElement result = await PostAsync(
            "areasAndLengths",
            ("sr", "3857"),
            ("geometries", "{\"geometryType\":\"esriGeometryPolygon\",\"geometries\":[{\"rings\":["
                + "[[0,0],[0,100],[100,100],[100,0],[0,0]],"
                + "[[40,40],[60,40],[60,60],[40,60],[40,40]]]}]}"),
            ("f", "json"));

        Assert.Equal(9_600, result.GetProperty("areas")[0].GetDouble(), 6);
        Assert.Equal(480, result.GetProperty("lengths")[0].GetDouble(), 6);
    }

    [Fact]
    public async Task Measurements_say_out_loud_that_they_are_planar()
    {
        // In Web Mercator, area is overstated by sec squared of the latitude —
        // 1.75x at Istanbul. A number without that caveat is a land area
        // somebody will quote.
        JsonElement result = await PostAsync(
            "lengths",
            ("sr", "3857"),
            ("geometries", "{\"geometryType\":\"esriGeometryPolyline\","
                + "\"geometries\":[{\"paths\":[[[0,0],[3,4]]]}]}"),
            ("f", "json"));

        Assert.Equal(5, result.GetProperty("lengths")[0].GetDouble(), 6);
        Assert.Contains(
            "planar",
            result.GetProperty("note").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_label_point_lands_inside_a_C_shape_rather_than_in_its_notch()
    {
        // The centroid of this shape is near x=43, which is in the notch and
        // outside the polygon. PostGIS's ST_PointOnSurface gives x=15.
        JsonElement result = await PostAsync(
            "labelPoints",
            ("sr", "3857"),
            ("geometries", "{\"geometryType\":\"esriGeometryPolygon\",\"geometries\":[{\"rings\":[["
                + "[0,0],[0,100],[100,100],[100,70],[30,70],[30,30],[100,30],[100,0],[0,0]]]}]}"),
            ("f", "json"));

        double x = result.GetProperty("labelPoints")[0].GetProperty("x").GetDouble();

        Assert.True(x < 30, $"the label landed at x={x}, inside the notch");
    }

    // ---------- grid strings ----------

    /// <summary>
    /// A coordinate written as MGRS reads back to where it started.
    /// </summary>
    /// <remarks>
    /// <b>Through HTTP, because the round trip crosses two operations.</b> The
    /// converter is checked against PROJ in the platform tests; what this checks
    /// is that the two endpoints agree on field names, notation names and the
    /// shape of the answer. They are separate handlers reading separate
    /// parameters, and nothing but a test makes them agree.
    /// </remarks>
    [Fact]
    public async Task A_coordinate_survives_the_trip_through_MGRS_and_back()
    {
        JsonElement written = await PostAsync(
            "toGeoCoordinateString",
            ("sr", "4326"),
            ("coordinates", "[[32.8597, 39.9334]]"),
            ("conversionType", "MGRS"),
            ("addSpaces", "false"),
            ("f", "json"));

        string reference = written.GetProperty("strings")[0].GetString()!;

        // Zone 36, band S: Ankara. A wrong lettering scheme changes these.
        Assert.StartsWith("36S", reference, StringComparison.Ordinal);

        JsonElement read = await PostAsync(
            "fromGeoCoordinateString",
            ("sr", "4326"),
            ("strings", $"[\"{reference}\"]"),
            ("conversionType", "MGRS"),
            ("f", "json"));

        JsonElement pair = read.GetProperty("coordinates")[0];

        Assert.Equal(32.8597, pair[0].GetDouble(), 4);
        Assert.Equal(39.9334, pair[1].GetDouble(), 4);
    }

    /// <summary>
    /// A coordinate in a projected reference goes through the datastore's PROJ.
    /// </summary>
    /// <remarks>
    /// <b>The response says which engine did it</b>, for the reason
    /// geometry-crs-policy §3 gives: several transformation paths usually exist
    /// and they differ by metres. A caller cannot judge a grid reference without
    /// knowing what produced the degrees behind it.
    /// </remarks>
    [Fact]
    public async Task A_projected_coordinate_names_the_engine_that_converted_it()
    {
        JsonElement written = await PostAsync(
            "toGeoCoordinateString",
            ("sr", "3857"),
            ("coordinates", "[[3657868.0, 4863676.0]]"),
            ("conversionType", "MGRS"),
            ("f", "json"));

        Assert.StartsWith("36S", written.GetProperty("strings")[0].GetString()!,
            StringComparison.Ordinal);

        Assert.Contains(
            "PROJ",
            written.GetProperty("transformation").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A polar coordinate is refused, and the refusal names the coordinate.
    /// </summary>
    /// <remarks>
    /// <b>By index, because a caller sending two hundred cannot otherwise find
    /// it.</b> "Outside the UTM grid" with no index leaves them bisecting their
    /// own request.
    /// </remarks>
    [Fact]
    public async Task A_polar_coordinate_is_refused_by_index()
    {
        JsonElement result = await PostAsync(
            "toGeoCoordinateString",
            ("sr", "4326"),
            ("coordinates", "[[0, 10], [0, 88]]"),
            ("conversionType", "MGRS"),
            ("f", "json"));

        string message = result.GetProperty("error").GetProperty("message").GetString()!;

        Assert.Contains("Coordinate 1", message, StringComparison.Ordinal);
        Assert.Contains("Polar Stereographic", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unknown notation is refused with the list of the known ones.
    /// </summary>
    [Fact]
    public async Task An_unknown_notation_lists_the_ones_that_work()
    {
        JsonElement result = await PostAsync(
            "toGeoCoordinateString",
            ("sr", "4326"),
            ("coordinates", "[[0, 10]]"),
            ("conversionType", "Maidenhead"),
            ("f", "json"));

        string message = result.GetProperty("error").GetProperty("message").GetString()!;

        Assert.Contains("MGRS", message, StringComparison.Ordinal);
        Assert.Contains("DMS", message, StringComparison.Ordinal);

        // GARS and GEOREF were the gap this test named until ADR-146 filled it; the list now has all eight.
        Assert.Contains("GARS", message, StringComparison.Ordinal);
        Assert.Contains("GeoRef", message, StringComparison.Ordinal);
    }

    // ---------- the refusals ----------

    // ---------- datum transformations (ADR-160) ----------

    /// <summary>
    /// findTransformations lists the ways from ED50 to WGS 84 — the one for all of Europe first, those that apply in
    /// Turkey given its extent — and project applies the one named, either way round, refusing one that leads
    /// elsewhere.
    /// </summary>
    /// <remarks>
    /// <b>Until 2026-10-03 this was the last 501</b>, refused for want of PROJ's operation database (Q-100). The
    /// list is generated from it at build time now, and the datastore applies the one chosen.
    /// </remarks>
    [Fact]
    public async Task Transformations_are_listed_and_the_one_named_is_applied()
    {
        JsonElement first = await PostAsync("findTransformations", ("inSR", "4230"), ("outSR", "4326"));
        Assert.Equal(1133, first.GetProperty("wkid").GetInt32());
        Assert.True(first.GetProperty("transformForward").GetBoolean());

        JsonElement turkey = await PostAsync("findTransformations", ("inSR", "23035"), ("outSR", "3857"), ("numOfResults", "5"), ("extentOfInterest", """{"xmin":26,"ymin":36,"xmax":45,"ymax":42,"spatialReference":{"wkid":4326}}"""));
        JsonElement[] listed = [.. turkey.GetProperty("transformations").EnumerateArray()];
        Assert.NotEmpty(listed);
        Assert.DoesNotContain(listed, t => t.ToString().Contains("Catalonia", StringComparison.Ordinal));

        const string Point = """{"geometryType":"esriGeometryPoint","geometries":[{"x":29,"y":41}]}""";
        JsonElement pinned = await PostAsync("project", ("inSR", "4230"), ("outSR", "4326"), ("geometries", Point), ("transformation", "1133"));
        double x = pinned.GetProperty("geometries")[0].GetProperty("x").GetDouble();
        Assert.InRange(x, 28.999, 28.9999);
        Assert.Equal(1133, pinned.GetProperty("transformation").GetProperty("applied")[0].GetProperty("wkid").GetInt32());
        Assert.Equal(10, pinned.GetProperty("transformation").GetProperty("accuracyMetres").GetDouble());

        JsonElement back = await PostAsync("project", ("inSR", "4326"), ("outSR", "4230"), ("geometries", Point), ("transformation", """{"wkid":1133,"transformForward":false}"""));
        Assert.InRange(back.GetProperty("geometries")[0].GetProperty("x").GetDouble(), 29.0001, 29.001);

        JsonElement wrong = await PostAsync("project", ("inSR", "4326"), ("outSR", "4230"), ("geometries", Point), ("transformation", "1133"));
        Assert.Contains("goes from 4230 to 4326", wrong.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---------- the editing calculations (Q-99, ADR-150) ----------

    private const string PolygonType = "esriGeometryPolygon";
    private const string PolylineType = "esriGeometryPolyline";

    private static string GeometryList(string type, params string[] geometries) =>
        "{\"geometryType\":\"" + type + "\",\"geometries\":[" + string.Join(",", geometries) + "]}";

    private static string Path(params (double X, double Y)[] points) =>
        "{\"paths\":[[" + string.Join(",", points.Select(p => FormattableString.Invariant($"[{p.X},{p.Y}]"))) + "]]}";

    private static string Ring(params (double X, double Y)[] points) =>
        "{\"rings\":[[" + string.Join(",", points.Select(p => FormattableString.Invariant($"[{p.X},{p.Y}]"))) + "]]}";

    private static double RingArea(JsonElement polygon)
    {
        JsonElement ring = polygon.GetProperty("rings")[0];
        double twice = 0;

        for (int i = 1; i < ring.GetArrayLength(); i++)
        {
            twice += (ring[i - 1][0].GetDouble() * ring[i][1].GetDouble()) - (ring[i][0].GetDouble() * ring[i - 1][1].GetDouble());
        }

        return Math.Abs(twice) / 2;
    }

    /// <summary>
    /// autoComplete fills the gap a line closes against two polygons, and nothing the polygons already cover.
    /// </summary>
    [Fact]
    public async Task AutoComplete_fills_the_gap_a_line_closes_between_two_polygons()
    {
        // Two 10 × 10 squares with a 10-wide gap between them along the bottom; a line across their tops closes it.
        JsonElement result = await PostAsync(
            "autoComplete",
            ("sr", "3857"),
            ("polygons", GeometryList(PolygonType,
                Ring((0, 0), (0, 10), (10, 10), (10, 0), (0, 0)),
                Ring((20, 0), (20, 10), (30, 10), (30, 0), (20, 0)))),
            ("polylines", GeometryList(PolylineType,
                Path((10, 10), (20, 10)),
                Path((10, 0), (20, 0)))),
            ("f", "json"));

        JsonElement geometries = result.GetProperty("geometries");

        Assert.Equal(1, geometries.GetArrayLength());
        Assert.Equal(100, RingArea(geometries[0]), 6);
    }

    /// <summary>
    /// reshape replaces the part of a polygon's boundary between the reshaper's crossings, keeping the larger side.
    /// </summary>
    [Fact]
    public async Task Reshape_pushes_a_polygons_edge_out_along_the_reshaper()
    {
        // A 10 × 10 square; the reshaper leaves it through the top edge, runs 5 above it, and comes back in.
        JsonElement result = await PostAsync(
            "reshape",
            ("sr", "3857"),
            ("target", "{\"rings\":[[[0,0],[0,10],[10,10],[10,0],[0,0]]]}"),
            ("reshaper", "{\"paths\":[[[2,8],[2,15],[8,15],[8,8]]]}"),
            ("f", "json"));

        // 100 + a 6 × 5 bump.
        Assert.Equal(130, RingArea(result.GetProperty("geometry")), 6);
    }

    /// <summary>
    /// reshape of a line replaces its middle and keeps its direction.
    /// </summary>
    [Fact]
    public async Task Reshape_of_a_line_replaces_its_middle_and_keeps_its_ends()
    {
        JsonElement result = await PostAsync(
            "reshape",
            ("sr", "3857"),
            ("target", Path((0, 0), (10, 0))),
            ("reshaper", Path((3, -1), (3, 4), (7, 4), (7, -1))),
            ("f", "json"));

        JsonElement path = result.GetProperty("geometry").GetProperty("paths")[0];

        Assert.Equal(0, path[0][0].GetDouble());
        Assert.Equal(10, path[path.GetArrayLength() - 1][0].GetDouble());
        Assert.Contains(path.EnumerateArray(), point => point[1].GetDouble() == 4);
    }

    /// <summary>
    /// trimExtend trims a crossing line to the left of the guide, extends a short one to it, and answers an empty
    /// line for one it can do neither to.
    /// </summary>
    [Fact]
    public async Task TrimExtend_trims_extends_and_leaves_empty_what_it_cannot_reach()
    {
        // The guide runs north along x = 5; its left is the west.
        JsonElement result = await PostAsync(
            "trimExtend",
            ("sr", "3857"),
            ("polylines", GeometryList(PolylineType,
                Path((0, 0), (10, 0)),
                Path((0, 2), (3, 2)),
                Path((0, 4), (0, 6)))),
            ("trimExtendTo", Path((5, -10), (5, 10))),
            ("f", "json"));

        JsonElement geometries = result.GetProperty("geometries");
        Assert.Equal(3, geometries.GetArrayLength());

        JsonElement trimmed = geometries[0].GetProperty("paths")[0];
        Assert.Equal(0, trimmed[0][0].GetDouble());
        Assert.Equal(5, trimmed[trimmed.GetArrayLength() - 1][0].GetDouble(), 6);

        JsonElement extended = geometries[1].GetProperty("paths")[0];
        Assert.Equal(5, extended[extended.GetArrayLength() - 1][0].GetDouble(), 6);

        Assert.Equal(0, geometries[2].GetProperty("paths").GetArrayLength());
    }

    private async Task<string> ReasonAsync(string operation) =>
        (await PostAsync(operation, ("f", "json")))
            .GetProperty("error").GetProperty("message").GetString()!;

    // ---------- overlay, and the bound that makes it offerable ----------

    /// <summary>
    /// A comb of the given number of teeth, wound the way ArcGIS requires.
    /// </summary>
    /// <remarks>
    /// <b>Fixed span, narrowing teeth.</b> Widening the comb with its tooth
    /// count makes two combs at right angles stop overlapping, and the
    /// adversarial input quietly becomes a cheap one — a test that passes for
    /// the wrong reason.
    /// </remarks>
    private static string Comb(int teeth, bool horizontal, double span = 100)
    {
        double width = span / (2 * teeth);

        List<double[]> points = [];

        void Add(double x, double y) => points.Add(horizontal ? [y, x] : [x, y]);

        for (int i = 0; i < teeth; i++)
        {
            double left = i * 2 * width;

            Add(left, 0);
            Add(left, span);
            Add(left + width, span);
            Add(left + width, 0);
        }

        Add(0, 0);

        // ArcGIS reads a counter-clockwise first ring as a hole, and refuses a
        // hole with no shell. Clockwise is a negative shoelace area.
        double area = 0;

        for (int i = 0; i < points.Count - 1; i++)
        {
            area += (points[i][0] * points[i + 1][1]) - (points[i + 1][0] * points[i][1]);
        }

        if (area > 0)
        {
            points.Reverse();
        }

        return JsonSerializer.Serialize(new { rings = new[] { points } });
    }

    private static string Polygons(string ring) =>
        JsonSerializer.Serialize(new { geometryType = "esriGeometryPolygon" })
            .TrimEnd('}')
        + ",\"geometries\":[" + ring + "]}";

    [Fact]
    public async Task An_ordinary_intersection_is_computed()
    {
        JsonElement result = await PostAsync(
            "intersect",
            ("sr", "3857"),
            ("geometries", Polygons("{\"rings\":[[[0,0],[0,10],[10,10],[10,0],[0,0]]]}")),
            ("geometry", "{\"rings\":[[[5,5],[5,15],[15,15],[15,5],[5,5]]]}"),
            ("f", "json"));

        Assert.False(
            result.TryGetProperty("error", out JsonElement failed),
            failed.ValueKind == JsonValueKind.Object
                ? failed.GetProperty("message").GetString()
                : string.Empty);

        Assert.Single(result.GetProperty("geometries").EnumerateArray().ToArray());

        // The cost is reported so a caller batching these can see how close to
        // the limits they are before they cross one.
        JsonElement cost = Require(result, "cost",
            "A caller cannot otherwise tell a cheap overlay from one that nearly hit the deadline.");

        Assert.True(cost.GetProperty("candidatePairs").GetInt64() > 0);
    }

    /// <summary>
    /// The input that took the machine down is refused, and the server lives.
    /// </summary>
    /// <remarks>
    /// <b>This is the whole of Q-97 in one test.</b> benchmarks/geometry-overlay
    /// measured a 6,408-vertex comb pair costing 153 seconds and 16.7 GB — the
    /// run pushed the host into swap and killed the Docker daemon with it, and
    /// the finding was recorded as: one unauthenticated request would have done
    /// that. The same shape is now stopped, and the assertion after it is that
    /// the server is still answering at all.
    /// </remarks>
    /// <remarks>
    /// <b>It used to answer 400 and now answers 503, and that is the owner's
    /// decision arriving in a test.</b> The pre-flight caught this input before
    /// any arithmetic, which was cheap — and it was also measured
    /// under-predicting an adversarial case by fourteen times, so it was never
    /// the bound. On 2026-08-15 the owner ruled that the server does not decide
    /// on the caller's behalf what is worth attempting, and the pre-flight was
    /// turned off by default. The request is now attempted and killed on the
    /// deadline. <b>What matters did not change:</b> the work stops, and the
    /// server is still serving afterwards. What changed is that it costs ten
    /// seconds of one worker instead of eighty milliseconds — the price of not
    /// refusing work on a guess.
    /// </remarks>
    [Fact]
    public async Task The_adversarial_input_that_took_the_host_down_is_refused()
    {
        Stopwatch clock = Stopwatch.StartNew();

        JsonElement result = await PostAsync(
            "intersect",
            ("sr", "3857"),
            ("geometries", Polygons(Comb(800, horizontal: false))),
            ("geometry", Comb(800, horizontal: true)),
            ("f", "json"));

        JsonElement error = Require(
            result, "error", "The 6,408-vertex comb pair was computed rather than stopped.");

        // 503, not 400: the caller sent something the server was willing to
        // attempt and could not finish, which is a different statement from
        // "your request was malformed".
        Assert.Equal(503, error.GetProperty("code").GetInt32());
        Assert.Equal("Deadline", error.GetProperty("reason").GetString());

        // <b>The deadline is ten seconds and the measured cost was 153.</b> If
        // this ever takes minutes, the process is not being killed and the only
        // thing standing between this server and the Docker daemon is gone.
        Assert.True(
            clock.Elapsed < TimeSpan.FromSeconds(40),
            $"the refusal took {clock.Elapsed.TotalSeconds:0.#} seconds, so the worker is not "
            + "being killed on its deadline.");

        // Still serving, which is the property the whole design exists for.
        Assert.Equal(200, await StatusOfAsync("/healthz/ready"));
    }

    /// <summary>
    /// The pre-flight still works when an operator asks for it.
    /// </summary>
    /// <remarks>
    /// <b>Not asserted here, and this comment is the reason.</b> The threshold is
    /// a constructor argument on the pool rather than a runtime setting, so a
    /// conformance test against a running server cannot switch it on.
    /// <c>GeometryWorkerPoolTests.The_pre_flight_refuses_the_adversarial_comb_before_any_arithmetic</c>
    /// covers it at the layer where it can be configured. Recorded rather than
    /// left as a silent gap in coverage.
    /// </remarks>
    [Fact]
    public async Task The_service_document_says_the_pre_flight_is_off()
    {
        JsonElement document = await GetJsonAsync(Root);

        Assert.Equal(0, document.GetProperty("maximumCandidatePairs").GetInt64());
    }

    [Fact]
    public async Task A_real_sized_overlay_is_not_refused_by_the_pre_flight()
    {
        // <b>The other half of the threshold.</b> A limit low enough to be safe
        // is worthless if it is also low enough to refuse ordinary work — that
        // was the second question A-042 asked and the one that is easy to skip.
        JsonElement result = await PostAsync(
            "intersect",
            ("sr", "3857"),
            ("geometries", Polygons(Comb(50, horizontal: false))),
            ("geometry", Comb(50, horizontal: true)),
            ("f", "json"));

        Assert.False(
            result.TryGetProperty("error", out JsonElement failed),
            failed.ValueKind == JsonValueKind.Object
                ? failed.GetProperty("message").GetString()
                : string.Empty);

        Assert.NotEmpty(result.GetProperty("geometries").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task The_service_document_states_the_limits_it_enforces()
    {
        // A caller should be able to find out what will be refused without
        // being refused first.
        JsonElement document = await GetJsonAsync(Root);

        string[] supported =
        [
            .. document.GetProperty("supportedOperations").EnumerateArray()
                .Select(o => o.GetString()!),
        ];

        foreach (string operation in (string[])[
            "project", "areasAndLengths", "lengths", "labelPoints",
            "convexHull", "densify", "generalize",
            "intersect", "union", "difference",
            "cut", "buffer", "offset", "simplify", "relation", "distance",
            "toGeoCoordinateString", "fromGeoCoordinateString"])
        {
            Assert.Contains(operation, supported);
        }

        // <b>The deadline is the bound and must be stated.</b> The pre-flight is
        // not: it is off by default since 2026-08-15, because it was measured
        // under-predicting by fourteen times and the owner ruled that the server
        // does not decide for the caller what is worth attempting. Zero here
        // means "no pre-flight", and asserting it is positive would be asserting
        // that the server second-guesses its callers.
        Assert.True(document.GetProperty("deadlineSeconds").GetDouble() > 0);
        Assert.True(document.GetProperty("maximumCandidatePairs").GetInt64() >= 0);
    }

    [Fact]
    public async Task A_missing_spatial_reference_is_refused_with_the_field_name()
    {
        JsonElement result = await PostAsync(
            "lengths",
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[]}"),
            ("f", "json"));

        Assert.Contains(
            "sr",
            result.GetProperty("error").GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    // ---------- sharing ----------

    /// <summary>
    /// The geometry service answers strangers the same way a private layer does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This service was reachable anonymously until 2026-08-15</b>, and not
    /// because anyone decided it should be. Sharing was a property of a layer,
    /// this service has no layer, and so nothing governed it — a gap rather than
    /// a decision, found by the project owner asking why the geometry server was
    /// not in the service list.
    /// </para>
    /// <para>
    /// <b>404 rather than 401</b>, matching every other unshared resource here:
    /// a 403 would confirm the service exists, and a private service that
    /// confirms its own existence is only half private.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task An_anonymous_caller_does_not_reach_an_organisation_shared_service()
    {
        string root = await RequireServerAsync();

        // Deliberately not AuthenticateAsync: this is the anonymous case.
        using HttpClient http = Client();
        using FormUrlEncodedContent content = new([new KeyValuePair<string, string>("f", "json")]);

        using HttpResponseMessage response =
            await http.PostAsync(new Uri(root + Root + "/project"), content);

        Assert.Equal(404, (int)response.StatusCode);
    }
}
