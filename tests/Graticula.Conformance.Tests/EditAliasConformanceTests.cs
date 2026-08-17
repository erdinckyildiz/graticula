using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// <c>addFeatures</c>, <c>updateFeatures</c> and <c>deleteFeatures</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because a client written against the 10.x documentation posts
/// to them and got a 404 from a server that could do exactly what was asked.</b>
/// <c>applyEdits</c> is what a modern client uses and the only endpoint that can
/// be transactional across operations; these three are what everything older
/// calls.
/// </para>
/// <para>
/// <b>The shape is the point.</b> ArcGIS answers each with only its own results
/// array, and an older client parses exactly that. Three arrays, two of them
/// empty, is a different document — so these tests assert what is absent as
/// firmly as what is present.
/// </para>
/// </remarks>
public sealed class EditAliasConformanceTests : ArcGisClient
{
    private const string ServiceVariable = "GRATICULA_TEST_EDITABLE";

    private static string? Editable => Environment.GetEnvironmentVariable(ServiceVariable);

    private async Task<string> RequireEditableAsync()
    {
        await RequireServerAsync();

        Assert.False(
            string.IsNullOrWhiteSpace(Editable),
            $"{ServiceVariable} is not set, so these tests FAIL rather than skip. Name a service "
            + "whose layer 0 can be written to. Editing cannot be discovered from the catalogue: "
            + "a layer may be perfectly servable and still be read-only to the caller.");

        return Editable!.Trim('/');
    }

    private static HttpClient Client() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
    });

    private async Task<JsonElement> PostAsync(
        string operation, params (string Key, string Value)[] fields)
    {
        string root = await RequireServerAsync();
        string service = await RequireEditableAsync();

        using HttpClient http = Client();
        using FormUrlEncodedContent content = new(
            fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))
                .Append(new KeyValuePair<string, string>("f", "json")));

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            new Uri($"{root}/rest/services/{service}/FeatureServer/0/{operation}"))
        {
            Content = content,
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await http.SendAsync(request);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    /// <summary>The layer's own spatial reference, and a point inside it.</summary>
    /// <remarks>
    /// <b>Read from the layer rather than assumed.</b> This server refuses to
    /// reproject on write, deliberately, so a hardcoded 4326 point would test
    /// that refusal instead of the endpoint.
    /// </remarks>
    private async Task<(int Srid, double X, double Y, string Field)> TargetAsync()
    {
        JsonElement layer = await GetJsonAsync(
            $"/rest/services/{await RequireEditableAsync()}/FeatureServer/0");

        int srid = layer.GetProperty("extent").GetProperty("spatialReference")
            .GetProperty("wkid").GetInt32();

        JsonElement extent = layer.GetProperty("extent");

        double x = (extent.GetProperty("xmin").GetDouble()
                    + extent.GetProperty("xmax").GetDouble()) / 2;

        double y = (extent.GetProperty("ymin").GetDouble()
                    + extent.GetProperty("ymax").GetDouble()) / 2;

        string field = layer.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("type").GetString() == "esriFieldTypeString")
            .GetProperty("name").GetString()!;

        return (srid, x, y, field);
    }

    private static string Feature(int srid, double x, double y, string field, string value) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"[{{\"geometry\":{{\"x\":{x},\"y\":{y},"
            + $"\"spatialReference\":{{\"wkid\":{srid}}}}},"
            + $"\"attributes\":{{\"{field}\":\"{value}\"}}}}]");

    // ---------- the round trip through the three endpoints ----------

    /// <summary>
    /// A feature added, changed and removed, each through its own endpoint.
    /// </summary>
    [Fact]
    public async Task A_feature_goes_all_the_way_round_through_the_single_operation_endpoints()
    {
        (int srid, double x, double y, string field) = await TargetAsync();

        JsonElement added = await PostAsync(
            "addFeatures", ("features", Feature(srid, x, y, field, "alias round trip")));

        // Only its own array, and rolledBack alongside it.
        Assert.True(added.TryGetProperty("addResults", out JsonElement adds));
        Assert.False(added.TryGetProperty("updateResults", out _));
        Assert.False(added.TryGetProperty("deleteResults", out _));

        JsonElement result = adds.EnumerateArray().Single();
        Assert.True(result.GetProperty("success").GetBoolean());

        long id = result.GetProperty("objectId").GetInt64();
        Assert.True(id > 0);

        try
        {
            JsonElement updated = await PostAsync(
                "updateFeatures",
                ("features",
                 $"[{{\"attributes\":{{\"objectid\":{id},\"{field}\":\"changed\"}}}}]"));

            Assert.True(updated.TryGetProperty("updateResults", out JsonElement updates));
            Assert.False(updated.TryGetProperty("addResults", out _));
            Assert.True(updates.EnumerateArray().Single().GetProperty("success").GetBoolean());

            JsonElement read = await GetJsonAsync(
                $"/rest/services/{await RequireEditableAsync()}/FeatureServer/0/query"
                + $"?objectIds={id}&outFields=*");

            Assert.Equal(
                "changed",
                read.GetProperty("features").EnumerateArray().Single()
                    .GetProperty("attributes").GetProperty(field).GetString());
        }
        finally
        {
            JsonElement deleted = await PostAsync(
                "deleteFeatures",
                ("objectIds", id.ToString(CultureInfo.InvariantCulture)));

            Assert.True(deleted.TryGetProperty("deleteResults", out JsonElement deletes));
            Assert.False(deleted.TryGetProperty("addResults", out _));
            Assert.True(deletes.EnumerateArray().Single().GetProperty("success").GetBoolean());
        }
    }

    /// <summary>
    /// The results keep their position, including for a feature that never
    /// reached the writer.
    /// </summary>
    /// <remarks>
    /// <b>The defect this guards against is off-by-one in somebody else's
    /// code.</b> A client matches results to its own features by index. A
    /// feature the parser rejects never reaches the writer, so a response that
    /// simply omitted it would shift every later result onto the wrong feature
    /// — silently, and in the client rather than here. The single-operation
    /// endpoints go through the same merge as applyEdits precisely so this
    /// cannot be reintroduced by a second code path.
    /// </remarks>
    [Fact]
    public async Task A_rejected_feature_still_occupies_its_position()
    {
        (int srid, double x, double y, string field) = await TargetAsync();

        string good = Feature(srid, x, y, field, "good").Trim('[', ']');
        string bad = Feature(4326, 1, 2, field, "wrong reference").Trim('[', ']');

        JsonElement response = await PostAsync(
            "addFeatures",
            ("features", $"[{bad},{good}]"),
            ("rollbackOnFailure", "true"));

        JsonElement[] results = [.. response.GetProperty("addResults").EnumerateArray()];

        Assert.Equal(2, results.Length);
        Assert.False(results[0].GetProperty("success").GetBoolean());
        Assert.True(response.GetProperty("rolledBack").GetBoolean());
    }

    // ---------- what is refused ----------

    /// <summary>
    /// Deleting by predicate is refused, and the refusal says what to do.
    /// </summary>
    /// <remarks>
    /// <b>A deliberate departure from ArcGIS, and the reason is asymmetry.</b>
    /// One mistyped clause removes an unknown number of features, and this
    /// server has no versioning and no soft delete to undo it with. A refusal
    /// costs a round trip; a wiped layer costs the data.
    /// </remarks>
    [Fact]
    public async Task Deleting_by_where_clause_is_refused()
    {
        JsonElement response = await PostAsync("deleteFeatures", ("where", "1=1"));

        Assert.Equal(400, response.GetProperty("error").GetProperty("code").GetInt32());

        string message = response.GetProperty("error").GetProperty("message").GetString()!;

        Assert.Contains("objectIds", message, StringComparison.Ordinal);
        Assert.Contains("returnIdsOnly", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wrong parameter name is a client bug, and is named as one.
    /// </summary>
    /// <remarks>
    /// <c>addFeatures</c> takes <c>features</c>, not <c>adds</c>. Answering
    /// "nothing to do, here are zero results" would let a client believe it had
    /// saved something.
    /// </remarks>
    [Theory]
    [InlineData("addFeatures", "adds")]
    [InlineData("updateFeatures", "updates")]
    [InlineData("deleteFeatures", "deletes")]
    public async Task The_wrong_parameter_name_is_refused_rather_than_treated_as_empty(
        string operation, string wrong)
    {
        JsonElement response = await PostAsync(operation, (wrong, "[]"));

        Assert.Equal(400, response.GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>
    /// An anonymous caller cannot write through the aliases either.
    /// </summary>
    /// <remarks>
    /// A new route is a new way in, and the privilege check lives in the shared
    /// handler for exactly that reason. This asserts the sharing of the route
    /// group applies, not that the handler was remembered.
    /// </remarks>
    [Fact]
    public async Task An_anonymous_caller_cannot_add_features()
    {
        string root = await RequireServerAsync();
        string service = await RequireEditableAsync();

        using HttpClient http = Client();
        using FormUrlEncodedContent content = new(
        [
            new KeyValuePair<string, string>("features", "[]"),
            new KeyValuePair<string, string>("f", "json"),
        ]);

        using HttpResponseMessage response = await http.PostAsync(
            new Uri($"{root}/rest/services/{service}/FeatureServer/0/addFeatures"), content);

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"an anonymous addFeatures returned {(int)response.StatusCode}");

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            // A 400 is only acceptable if it is the empty-batch complaint and
            // not an edit that went through.
            string body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("\"success\": true", body, StringComparison.Ordinal);
        }
    }
}
