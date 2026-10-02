using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// ADR-144: the GeometryServer asked the way the ArcGIS Maps SDK for JavaScript asks it — its parameter names, its
/// wrappers, one answer per input, and its token in the body of a POST. The requests are the SDK 4.30's own shapes,
/// read from its published <c>rest/geometryService</c> and <c>*Parameters.toJSON</c>.
/// </summary>
public sealed class GeometryServerClientRequestsTests : ArcGisClient
{
    private const string Service = "/rest/services/Utilities/Geometry/GeometryServer";

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    /// <summary>A form POST with the token in the body, as the SDK sends one too long for a URL.</summary>
    private async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(string operation, params (string Key, string Value)[] fields)
    {
        string root = await RequireServerAsync();
        string? token = await TokenAsync(root);
        Assert.False(token is null, "No administrator credential; set the suite's user and password.");
        using HttpClient http = Client();
        using FormUrlEncodedContent content = new([.. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)),
            new KeyValuePair<string, string>("f", "json"), new KeyValuePair<string, string>("token", token!)]);
        using HttpResponseMessage response = await http.PostAsync(new Uri(root + Service + "/" + operation), content);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone());
    }

    private const string Square = "{\"rings\":[[[0,0],[0,10],[10,10],[10,0],[0,0]]]}";
    private const string Inner = "{\"rings\":[[[2,2],[2,4],[4,4],[4,2],[2,2]]]}";
    private const string Far = "{\"rings\":[[[100,100],[100,110],[110,110],[110,100],[100,100]]]}";

    [Fact]
    public async Task A_token_in_the_body_of_a_post_is_the_callers()
    {
        (HttpStatusCode status, JsonElement body) = await PostAsync("simplify",
            ("sr", "3857"), ("geometries", $"{{\"geometryType\":\"esriGeometryPolygon\",\"geometries\":[{Square}]}}"));

        Assert.True(status == HttpStatusCode.OK && !body.TryGetProperty("error", out _), body.ToString());
    }

    [Fact]
    public async Task Buffer_takes_the_sdks_inSR_and_answers_one_geometry_an_input_unless_asked_to_union()
    {
        string points = "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[{\"x\":0,\"y\":0},{\"x\":100,\"y\":0}]}";

        (_, JsonElement each) = await PostAsync("buffer", ("inSR", "3857"), ("outSR", "3857"), ("geometries", points), ("distances", "10"));
        Assert.Equal(2, each.GetProperty("geometries").GetArrayLength());

        (_, JsonElement merged) = await PostAsync("buffer", ("inSR", "3857"), ("geometries", points), ("distances", "10"), ("unionResults", "true"));
        Assert.Single(merged.GetProperty("geometries").EnumerateArray());
    }

    [Fact]
    public async Task Measurement_reads_polygons_and_polylines_as_the_sdk_names_them()
    {
        (_, JsonElement areas) = await PostAsync("areasAndLengths", ("sr", "3857"), ("polygons", $"[{Square}]"));
        Assert.Equal(100, areas.GetProperty("areas")[0].GetDouble(), 6);

        (_, JsonElement lengths) = await PostAsync("lengths", ("sr", "3857"), ("polylines", "[{\"paths\":[[[0,0],[3,4]]]}]"));
        Assert.Equal(5, lengths.GetProperty("lengths")[0].GetDouble(), 6);

        (_, JsonElement labels) = await PostAsync("labelPoints", ("sr", "3857"), ("polygons", $"[{Square}]"));
        Assert.Single(labels.GetProperty("labelPoints").EnumerateArray());
    }

    [Fact]
    public async Task Intersect_reads_the_wrapped_second_operand_and_keeps_each_input_in_its_place()
    {
        // Three inputs: inside the square, outside it, and the square itself. ArcGIS answers three, in order, the second
        // empty — where one combined answer used to come back.
        (_, JsonElement body) = await PostAsync("intersect", ("sr", "3857"),
            ("geometries", $"{{\"geometryType\":\"esriGeometryPolygon\",\"geometries\":[{Inner},{Far},{Square}]}}"),
            ("geometry", $"{{\"geometryType\":\"esriGeometryPolygon\",\"geometry\":{Square}}}"));

        JsonElement[] answers = [.. body.GetProperty("geometries").EnumerateArray()];
        Assert.Equal(3, answers.Length);
        Assert.NotEqual(0, answers[0].GetProperty("rings").GetArrayLength());
        Assert.Equal(0, answers[1].GetProperty("rings").GetArrayLength());
        Assert.NotEqual(0, answers[2].GetProperty("rings").GetArrayLength());
    }

    [Fact]
    public async Task Simplify_keeps_a_polygon_inside_another_as_its_own_answer()
    {
        (_, JsonElement body) = await PostAsync("simplify", ("sr", "3857"),
            ("geometries", $"{{\"geometryType\":\"esriGeometryPolygon\",\"geometries\":[{Square},{Inner}]}}"));

        Assert.Equal(2, body.GetProperty("geometries").GetArrayLength());
    }

    [Fact]
    public async Task Distance_reads_the_wrapped_operands()
    {
        (_, JsonElement body) = await PostAsync("distance", ("sr", "3857"),
            ("geometry1", "{\"geometryType\":\"esriGeometryPoint\",\"geometry\":{\"x\":0,\"y\":0}}"),
            ("geometry2", "{\"geometryType\":\"esriGeometryPoint\",\"geometry\":{\"x\":3,\"y\":4}}"));

        Assert.Equal(5, body.GetProperty("distance").GetDouble(), 6);
    }

    [Fact]
    public async Task Units_are_converted_in_a_projected_reference_and_what_cannot_be_honoured_is_refused()
    {
        // ADR-145. A 1-kilometre buffer in Web Mercator is 1,000 of its metres wide each way.
        (_, JsonElement buffered) = await PostAsync("buffer", ("inSR", "3857"),
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[{\"x\":0,\"y\":0}]}"), ("distances", "1"), ("unit", "9036"));
        double[] xs = [.. buffered.GetProperty("geometries")[0].GetProperty("rings")[0].EnumerateArray().Select(p => p[0].GetDouble())];
        Assert.InRange(xs.Max(), 999, 1001);

        (_, JsonElement apart) = await PostAsync("distance", ("sr", "3857"),
            ("geometry1", "{\"x\":0,\"y\":0}"), ("geometry2", "{\"x\":3000,\"y\":4000}"), ("distanceUnit", "9036"));
        Assert.Equal(5, apart.GetProperty("distance").GetDouble(), 9);

        string kilometre = "{\"rings\":[[[0,0],[0,1000],[1000,1000],[1000,0],[0,0]]]}";
        (_, JsonElement measured) = await PostAsync("areasAndLengths", ("sr", "3857"), ("polygons", $"[{kilometre}]"),
            ("lengthUnit", "9036"), ("areaUnit", "{\"areaUnit\":\"esriSquareKilometers\"}"));
        Assert.Equal(1, measured.GetProperty("areas")[0].GetDouble(), 9);
        Assert.Equal(4, measured.GetProperty("lengths")[0].GetDouble(), 9);

        // Metres in degrees, a geodesic measure, a buffer in another reference: each refused, saying why.
        (_, JsonElement degrees) = await PostAsync("buffer", ("inSR", "4326"),
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[{\"x\":29,\"y\":41}]}"), ("distances", "1000"), ("unit", "9001"));
        Assert.Contains("geographic", degrees.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        (_, JsonElement geodesic) = await PostAsync("distance", ("sr", "3857"),
            ("geometry1", "{\"x\":0,\"y\":0}"), ("geometry2", "{\"x\":1,\"y\":1}"), ("geodesic", "true"));
        Assert.Contains("geodesic", geodesic.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        (_, JsonElement ellipsoid) = await PostAsync("areasAndLengths", ("sr", "3857"), ("polygons", $"[{kilometre}]"), ("calculationType", "geodesic"));
        Assert.Contains("calculationType", ellipsoid.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        (_, JsonElement elsewhere) = await PostAsync("buffer", ("inSR", "3857"), ("bufferSR", "32635"),
            ("geometries", "{\"geometryType\":\"esriGeometryPoint\",\"geometries\":[{\"x\":0,\"y\":0}]}"), ("distances", "1"));
        Assert.Contains("bufferSR", elsewhere.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cut_takes_a_list_of_targets_and_says_which_each_piece_came_from()
    {
        (_, JsonElement body) = await PostAsync("cut", ("sr", "3857"),
            ("target", $"{{\"geometryType\":\"esriGeometryPolygon\",\"geometries\":[{Square},{Far}]}}"),
            ("cutter", "{\"paths\":[[[5,-1],[5,11]]]}"));

        int[] indexes = [.. body.GetProperty("cutIndexes").EnumerateArray().Select(i => i.GetInt32())];
        Assert.Equal(body.GetProperty("geometries").GetArrayLength(), indexes.Length);
        Assert.Equal(2, indexes.Count(i => i == 0));
        Assert.Equal(1, indexes.Count(i => i == 1));
    }
}
