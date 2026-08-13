using System.Collections.Generic;
using GisServer.Features;
using GisServer.Geometries;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace GisServer.Host.Tests;

/// <summary>
/// What the query endpoint accepts, and what it refuses.
/// </summary>
/// <remarks>
/// The refusals carry as much weight as the acceptances. ADR-008 §2's principle
/// is that we never degrade silently, and a parameter list is exactly where that
/// principle decays: ignoring one unknown parameter is a one-line change that
/// turns a wrong answer into the normal case.
/// </remarks>
public sealed class FeatureServerQueryParametersTests
{
    private static QueryCollection Query(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> values = [];
        foreach ((string key, string value) in pairs)
        {
            values[key] = value;
        }

        return new QueryCollection(values);
    }

    [Theory]
    [InlineData("where")]
    [InlineData("outStatistics")]
    [InlineData("returnCountOnly")]
    [InlineData("resultOffset")]
    [InlineData("objectIds")]
    [InlineData("orderByFields")]
    public void A_parameter_that_changes_the_answer_is_refused_rather_than_ignored(string parameter)
    {
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query((parameter, "anything")), "objectid", out FeatureQuery? query, out string? error);

        Assert.False(parsed);
        Assert.Null(query);
        Assert.Contains(parameter, error!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void The_object_id_is_always_requested_even_when_outFields_omits_it()
    {
        // The bug this pins: outFields=name produced a response whose
        // objectIdFieldName named a field the features did not carry. A client
        // cannot page or select against that, and nothing in the response says
        // so — it simply looks like every feature has a null object id.
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query(("outFields", "name")), "objectid", out FeatureQuery? query, out _);

        Assert.True(parsed);
        Assert.Contains("objectid", query!.Fields!);
        Assert.Contains("name", query.Fields!);
    }

    [Fact]
    public void The_object_id_is_not_requested_twice_when_outFields_already_names_it()
    {
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query(("outFields", "objectid,name")), "objectid", out FeatureQuery? query, out _);

        Assert.True(parsed);
        Assert.Equal(["objectid", "name"], query!.Fields!);
    }

    [Fact]
    public void An_envelope_is_parsed_in_the_order_ArcGIS_sends_it()
    {
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query(("geometry", "1,2,3,4")), "objectid", out FeatureQuery? query, out _);

        Assert.True(parsed);
        Envelope box = query!.BoundingBox!.Value;
        Assert.Equal(1, box.MinX);
        Assert.Equal(2, box.MinY);
        Assert.Equal(3, box.MaxX);
        Assert.Equal(4, box.MaxY);
    }

    [Fact]
    public void A_geometry_that_is_not_an_envelope_is_refused_with_the_reason()
    {
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query(("geometry", "{\"rings\":[]}")), "objectid", out _, out string? error);

        Assert.False(parsed);
        Assert.Contains("envelope", error!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_for_more_than_the_maximum_is_clamped_rather_than_refused()
    {
        // Clamping, not refusing, because a client asking for everything is
        // asking a reasonable question badly. exceededTransferLimit is how the
        // response tells them there is more.
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query(("resultRecordCount", "999999999")), "objectid", out FeatureQuery? query, out _);

        Assert.True(parsed);
        Assert.Equal(FeatureQuery.MaximumLimit, query!.Limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("many")]
    public void A_record_count_that_is_not_a_positive_integer_is_refused(string value)
    {
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query(("resultRecordCount", value)), "objectid", out _, out string? error);

        Assert.False(parsed);
        Assert.NotNull(error);
    }

    [Fact]
    public void outFields_star_is_refused_because_the_catalogue_cannot_describe_the_columns()
    {
        bool parsed = FeatureServerQueryParameters.TryParse(
            Query(("outFields", "*")), "objectid", out _, out string? error);

        Assert.False(parsed);
        Assert.Contains("column types", error!, System.StringComparison.Ordinal);
    }
}
