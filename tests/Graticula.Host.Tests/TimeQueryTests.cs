using System;
using System.Collections.Generic;
using Graticula.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// <c>time</c> on a query filters on the layer's time field, and is refused on a layer without one.
/// </summary>
/// <remarks>
/// Written 2026-09-15: <c>time</c> was refused on every layer, though a time field could be declared
/// and WMS already used it.
/// </remarks>
public sealed class TimeQueryTests
{
    private static readonly FieldDescription[] Fields =
    [
        new("objectid", FieldType.Integer, false, null),
        new("label", FieldType.Text, true, null),
        new("observed_at", FieldType.Date, true, null),
    ];

    private static bool Parse(string? timeField, out FeatureQuery? query, out string? error, params (string Key, string Value)[] pairs)
    {
        Dictionary<string, StringValues> values = new() { ["where"] = "1=1" };

        foreach ((string key, string value) in pairs)
        {
            values[key] = value;
        }

        return FeatureServerQueryParameters.TryParse(
            new QueryCollection(values), "objectid", 4326, Fields, out query, out _, out error, timeField: timeField);
    }

    [Fact]
    public void A_window_becomes_two_bound_moments_on_the_time_field()
    {
        Assert.True(Parse("observed_at", out FeatureQuery? query, out string? error, ("time", "1735689600000,1767225600000")), error);

        Assert.Contains("\"observed_at\" >=", query!.Where!.Value.Sql, StringComparison.Ordinal);
        Assert.Contains("\"observed_at\" <=", query.Where!.Value.Sql, StringComparison.Ordinal);
        Assert.Contains(DateTimeOffset.FromUnixTimeMilliseconds(1735689600000), query.Where!.Value.Parameters);
    }

    [Fact]
    public void An_open_end_filters_one_side_only()
    {
        Assert.True(Parse("observed_at", out FeatureQuery? query, out string? error, ("time", "1735689600000,null")), error);

        Assert.Contains(">=", query!.Where!.Value.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("<=", query.Where!.Value.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Time_is_joined_to_the_where_clause_rather_than_replacing_it()
    {
        Assert.True(Parse("observed_at", out FeatureQuery? query, out string? error,
            ("where", "label = 'a'"), ("time", "1735689600000")), error);

        Assert.Contains("\"label\" =", query!.Where!.Value.Sql, StringComparison.Ordinal);
        Assert.Contains("\"observed_at\"", query.Where!.Value.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "1735689600000", "no time field")]
    [InlineData("observed_at", "yesterday", "epoch milliseconds")]
    [InlineData("observed_at", "1767225600000,1735689600000", "starts after it ends")]
    [InlineData("observed_at", "1,2,3", "one instant")]
    public void What_cannot_be_a_window_is_refused_with_the_reason(string? timeField, string time, string reason)
    {
        Assert.False(Parse(timeField, out _, out string? error, ("time", time)));
        Assert.Contains(reason, error, StringComparison.Ordinal);
    }
}
