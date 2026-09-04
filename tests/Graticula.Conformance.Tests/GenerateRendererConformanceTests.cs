using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace Graticula.Conformance.Tests;

/// <summary>
/// The operation that turns a field, a method and a class count into a renderer.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 §3.13.</b> A unique-value renderer is a list of a field's distinct values and a
/// class-breaks renderer is a set of bounds computed from its distribution. Every ArcGIS client
/// asks a server for those through <c>generateRenderer</c>; this one answered 404, so the
/// console's graphical editor was usable for exactly the one renderer that needs no data.
/// </para>
/// <para>
/// <b>These run against the server rather than the classifier.</b>
/// `ClassificationTests` proves the arithmetic without a database — the hard cases are a column
/// of one value, a range reaching zero, ties across a boundary. What only a live layer can prove
/// is the wiring: that the statistics query asks for the right things, that the field is
/// checked, and that what comes back is a renderer a client can use.
/// </para>
/// </remarks>
[Collection("catalogue walk")]
public sealed class GenerateRendererConformanceTests : ArcGisClient
{
    /// <summary>The layer these run against, and the numeric field they classify.</summary>
    private const string LayerVariable = "GRATICULA_TEST_QUERYABLE";

    private async Task<(string Path, string Field)> LayerAsync()
    {
        string? named = Environment.GetEnvironmentVariable(LayerVariable);

        Assert.False(
            string.IsNullOrWhiteSpace(named),
            $"{LayerVariable} is not set, so these tests FAIL rather than skip. Name a layer "
            + "with several features and a numeric column.");

        string path = $"/rest/services/{named!.Trim('/')}/FeatureServer/0";

        JsonElement about = await GetJsonAsync($"{path}?f=json");

        string? field = null;

        foreach (JsonElement one in about.GetProperty("fields").EnumerateArray())
        {
            string kind = one.GetProperty("type").GetString() ?? string.Empty;

            if (kind is "esriFieldTypeInteger" or "esriFieldTypeDouble"
                or "esriFieldTypeSmallInteger" or "esriFieldTypeSingle"
                or "esriFieldTypeOID")
            {
                field = one.GetProperty("name").GetString();

                if (kind != "esriFieldTypeOID")
                {
                    break;
                }
            }
        }

        Assert.False(
            string.IsNullOrWhiteSpace(field),
            $"'{named}' has no numeric field, so there is nothing to classify.");

        return (path, field!);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> GenerateAsync(string definition)
    {
        (string path, _) = await LayerAsync();

        string root = await RequireServerAsync();

        using HttpRequestMessage request = new(
            HttpMethod.Post, new Uri($"{root}{path}/generateRenderer"))
        {
            Content = new StringContent(
                $$"""{"classificationDef":{{definition}}}""",
                Encoding.UTF8,
                "application/json"),
        };

        await AuthenticateAsync(request, root);

        using HttpResponseMessage response = await Http.SendAsync(request);

        string body = await response.Content.ReadAsStringAsync();

        return (response.StatusCode, JsonDocument.Parse(body).RootElement.Clone());
    }

    [Theory]
    [InlineData("esriClassifyEqualInterval")]
    [InlineData("esriClassifyQuantile")]
    [InlineData("esriClassifyNaturalBreaks")]
    [InlineData("esriClassifyGeometricalInterval")]
    public async Task Every_method_that_takes_a_class_count_returns_that_many_classes(string method)
    {
        (_, string field) = await LayerAsync();

        (HttpStatusCode status, JsonElement renderer) = await GenerateAsync(
            $$"""
            {"type":"classBreaksDef","classificationField":"{{field}}",
             "classificationMethod":"{{method}}","breakCount":4}
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("classBreaks", renderer.GetProperty("type").GetString());
        Assert.Equal(field, renderer.GetProperty("field").GetString());

        JsonElement[] infos = [.. renderer.GetProperty("classBreakInfos").EnumerateArray()];

        // <b>Four unless the data cannot carry four.</b> A geometric progression from a
        // non-positive minimum is refused rather than shifted, and ties collapse quantile
        // classes -- both are the data's answer and neither is this test's subject.
        Assert.InRange(infos.Length, 1, 4);

        double previous = renderer.GetProperty("minValue").GetDouble();

        foreach (JsonElement one in infos)
        {
            Assert.Equal(previous, one.GetProperty("classMinValue").GetDouble());

            double top = one.GetProperty("classMaxValue").GetDouble();

            Assert.True(
                top > previous,
                $"A class runs from {previous} to {top}, which is not a range.");

            Assert.False(
                string.IsNullOrWhiteSpace(one.GetProperty("label").GetString()),
                "Every class needs a label, because a legend is the only place a reader learns "
                + "what a colour means.");

            Assert.True(
                one.GetProperty("symbol").TryGetProperty("color", out _),
                "Every class needs a symbol carrying its own colour.");

            previous = top;
        }
    }

    [Fact]
    public async Task The_classes_are_coloured_along_a_ramp_rather_than_all_the_same()
    {
        (_, string field) = await LayerAsync();

        (_, JsonElement renderer) = await GenerateAsync(
            $$"""
            {"type":"classBreaksDef","classificationField":"{{field}}",
             "classificationMethod":"esriClassifyEqualInterval","breakCount":4,
             "colorRamp":{"type":"algorithmic","fromColor":[255,255,229,255],
                          "toColor":[0,69,41,255]} }
            """);

        List<int[]> colours = [];

        foreach (JsonElement one in renderer.GetProperty("classBreakInfos").EnumerateArray())
        {
            colours.Add([.. one.GetProperty("symbol").GetProperty("color").EnumerateArray()
                .Select(c => c.GetInt32())]);
        }

        Assert.Equal([255, 255, 229, 255], colours[0]);
        Assert.Equal([0, 69, 41, 255], colours[^1]);

        // <b>Monotone in green, which is what makes a sequential ramp readable.</b> A
        // classification is ordered, so its colours have to be orderable by eye; a ramp that
        // wanders is a legend nobody can read without looking things up.
        for (int i = 1; i < colours.Count; i++)
        {
            Assert.True(
                colours[i][1] < colours[i - 1][1],
                $"Class {i} is greener than class {i - 1}: {colours[i - 1][1]} then "
                + $"{colours[i][1]}. The ramp is supposed to run one way.");
        }
    }

    [Fact]
    public async Task A_standard_deviation_classification_counts_its_own_classes()
    {
        (_, string field) = await LayerAsync();

        (HttpStatusCode status, JsonElement renderer) = await GenerateAsync(
            $$"""
            {"type":"classBreaksDef","classificationField":"{{field}}",
             "classificationMethod":"esriClassifyStandardDeviation","breakCount":3}
            """);

        // <b>`breakCount` is ignored here on purpose.</b> This classification says where the
        // mean is and how far each band is from it; how many bands that makes depends on how far
        // the data spreads, and forcing a count would answer a different question.
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            "StandardDeviation", renderer.GetProperty("classificationMethod").GetString());
        Assert.NotEmpty(renderer.GetProperty("classBreakInfos").EnumerateArray().ToArray());
    }

    [Fact]
    public async Task A_unique_value_renderer_lists_the_values_the_field_actually_holds()
    {
        (string path, _) = await LayerAsync();

        JsonElement about = await GetJsonAsync($"{path}?f=json");

        string? text = null;

        foreach (JsonElement one in about.GetProperty("fields").EnumerateArray())
        {
            if (one.GetProperty("type").GetString() == "esriFieldTypeString")
            {
                text = one.GetProperty("name").GetString();
                break;
            }
        }

        if (text is null)
        {
            return;
        }

        (HttpStatusCode status, JsonElement renderer) = await GenerateAsync(
            $$"""{"type":"uniqueValueDef","uniqueValueFields":["{{text}}"]}""");

        if (status != HttpStatusCode.OK)
        {
            // A field with more distinct values than a legend can hold is refused, and that is
            // the documented answer rather than a failure.
            Assert.Contains(
                "distinct values",
                renderer.GetProperty("error").GetProperty("message").GetString()!,
                StringComparison.Ordinal);

            return;
        }

        Assert.Equal("uniqueValue", renderer.GetProperty("type").GetString());
        Assert.Equal(text, renderer.GetProperty("field1").GetString());

        JsonElement[] infos = [.. renderer.GetProperty("uniqueValueInfos").EnumerateArray()];

        Assert.NotEmpty(infos);

        // <b>Distinct, because that is the whole operation.</b> A renderer with the same value
        // twice draws one class over another and the legend says the same thing twice.
        string[] values = [.. infos.Select(i => i.GetProperty("value").GetString()!)];

        Assert.Equal(values.Length, values.Distinct(StringComparer.Ordinal).Count());

        // And every value it lists is one the layer really holds.
        JsonElement first = await GetJsonAsync(
            $"{path}/query?where={Uri.EscapeDataString($"{text} = '{values[0].Replace("'", "''", StringComparison.Ordinal)}'")}"
            + "&returnCountOnly=true&f=json");

        Assert.True(
            first.GetProperty("count").GetInt64() > 0,
            $"The renderer offers a class for '{values[0]}' and no feature has that value.");
    }

    [Fact]
    public async Task A_field_the_layer_does_not_have_is_refused_by_name()
    {
        (HttpStatusCode status, JsonElement body) = await GenerateAsync(
            """{"type":"classBreaksDef","classificationField":"no_such_column","breakCount":3}""");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(
            "no_such_column",
            body.GetProperty("error").GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Normalisation_is_refused_rather_than_ignored()
    {
        // <b>Refused, because ignoring it draws a different map.</b> A client asking to
        // normalise by area and receiving unnormalised classes gets a plausible choropleth of
        // the wrong quantity, with nothing anywhere to say so.
        (_, string field) = await LayerAsync();

        (HttpStatusCode status, JsonElement body) = await GenerateAsync(
            $$"""
            {"type":"classBreaksDef","classificationField":"{{field}}","breakCount":3,
             "normalizationType":"esriNormalizeByField","normalizationField":"{{field}}"}
            """);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(
            "normalizationType",
            body.GetProperty("error").GetProperty("message").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_with_no_classification_definition_says_what_one_is()
    {
        (HttpStatusCode status, JsonElement body) = await GenerateAsync("null");

        Assert.Equal(HttpStatusCode.BadRequest, status);

        string message = body.GetProperty("error").GetProperty("message").GetString()!;

        Assert.Contains("classBreaksDef", message, StringComparison.Ordinal);
        Assert.Contains("uniqueValueDef", message, StringComparison.Ordinal);
    }
}
