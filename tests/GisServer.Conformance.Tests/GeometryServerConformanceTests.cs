using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Linq;
using Xunit;

namespace GisServer.Conformance.Tests;

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
        Assert.True(service.GetProperty("unsupportedOperations").GetArrayLength() > 0);
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

    // ---------- the refusals ----------

    [Theory]
    [InlineData("cut")]
    [InlineData("buffer")]
    [InlineData("convexHull")]
    [InlineData("simplify")]
    public async Task An_unimplemented_operation_answers_501_rather_than_404(string operation)
    {
        // 501 says the server made a decision. 404 says it has no
        // GeometryServer, which is a different and wrong thing to conclude.
        //
        // <b>intersect, difference and union left this list on 2026-08-15</b>,
        // when \"-97 was answered and they were implemented. What remains needs
        // its own reasoning rather than the overlay argument.
        Assert.Equal(501, await StatusOfPostAsync(operation));
    }

    [Fact]
    public async Task A_refusal_explains_itself_and_says_where_the_reasoning_is()
    {
        JsonElement error = (await PostAsync("cut", ("f", "json"))).GetProperty("error");

        string message = error.GetProperty("message").GetString()!;

        Assert.Contains("overlay", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Q-97", message, StringComparison.Ordinal);
        Assert.Contains("project", message, StringComparison.Ordinal);
    }

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
    /// <b>This is the whole of \"-97 in one test.</b> benchmarks/geometry-overlay
    /// measured a 6,408-vertex comb pair costing 153 seconds and 16.7 GB — the
    /// run pushed the host into swap and killed the Docker daemon with it, and
    /// the finding was recorded as: one unauthenticated request would have done
    /// that. The same shape now answers 400 in well under a second, and the
    /// assertion after it is that the server is still answering at all.
    /// </remarks>
    [Fact]
    public async Task The_adversarial_input_that_took_the_host_down_is_refused()
    {
        JsonElement result = await PostAsync(
            "intersect",
            ("sr", "3857"),
            ("geometries", Polygons(Comb(800, horizontal: false))),
            ("geometry", Comb(800, horizontal: true)),
            ("f", "json"));

        JsonElement error = Require(
            result, "error", "The 6,408-vertex comb pair was computed rather than refused.");

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Equal("TooLarge", error.GetProperty("reason").GetString());

        Assert.True(
            error.GetProperty("candidatePairs").GetInt64() > 1_000_000,
            "The pre-flight did not see a large crossing count, so this input is no longer the "
            + "adversarial case.");

        // Still serving, which is the property the whole design exists for.
        Assert.Equal(200, await StatusOfAsync("/healthz/ready"));
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

        Assert.Contains("intersect", supported);
        Assert.Contains("union", supported);
        Assert.Contains("difference", supported);

        Assert.True(document.GetProperty("maximumCandidatePairs").GetInt64() > 0);
        Assert.True(document.GetProperty("overlayDeadlineSeconds").GetDouble() > 0);
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
