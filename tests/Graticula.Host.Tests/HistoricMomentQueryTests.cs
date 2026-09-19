using System;
using System.Collections.Generic;
using Graticula.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>historicMoment</c> on a query — ADR-078: the layer as it was, where the layer keeps its history,
/// and a refusal where it does not.
/// </summary>
/// <remarks>
/// Written 2026-09-19. The parameter sat on the ignored list with <i>"there is no history"</i> until then,
/// so a question about the past was answered with the present.
/// </remarks>
public sealed class HistoricMomentQueryTests
{
    private static readonly FieldDescription[] Fields =
    [
        new("objectid", FieldType.Integer, false, null),
        new("label", FieldType.Text, true, null),
    ];

    private static bool Parse(bool archived, out FeatureQuery? query, out string? error, params (string Key, string Value)[] pairs)
    {
        Dictionary<string, StringValues> values = new() { ["where"] = "1=1" };

        foreach ((string key, string value) in pairs)
        {
            values[key] = value;
        }

        return FeatureServerQueryParameters.TryParse(
            new QueryCollection(values), "objectid", 4326, Fields, out query, out _, out error, archived: archived);
    }

    [Fact]
    public void A_moment_on_a_layer_that_keeps_its_history_reaches_the_query()
    {
        Assert.True(Parse(true, out FeatureQuery? query, out string? error, ("historicMoment", "1735689600000")), error);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1735689600000), query!.HistoricMoment);
    }

    [Fact]
    public void A_moment_on_a_layer_that_keeps_none_is_refused_rather_than_answered_with_the_present()
    {
        Assert.False(Parse(false, out _, out string? error, ("historicMoment", "1735689600000")));

        Assert.Contains("does not keep its history", error, StringComparison.Ordinal);
    }

    [Fact]
    public void No_moment_is_the_present_on_any_layer()
    {
        Assert.True(Parse(false, out FeatureQuery? query, out string? error), error);
        Assert.Null(query!.HistoricMoment);

        // An empty field, which is what the query form sends for a box left blank.
        Assert.True(Parse(false, out query, out error, ("historicMoment", "")), error);
        Assert.Null(query!.HistoricMoment);
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("1.5")]
    [InlineData("99999999999999999")]
    public void What_is_not_a_moment_is_refused_with_the_reason(string moment)
    {
        Assert.False(Parse(true, out _, out string? error, ("historicMoment", moment)));
        Assert.Contains("milliseconds since 1970", error, StringComparison.Ordinal);
    }
}
