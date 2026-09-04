using System;
using System.Collections.Generic;
using Graticula.Features;
using Graticula.Geometries;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Graticula.Host.Tests;

/// <summary>
/// The only statistic that carries an argument of its own.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-052 §3.11.</b> Four of CIM's seven classification methods fall out of
/// <c>min</c>, <c>max</c>, <c>avg</c> and <c>stddev</c>, all of which this server has served
/// since ADR-008. Quantile classification needs the value at a fraction of the way through the
/// sorted column, and nothing above can produce it — so the query face refused
/// <c>percentile_cont</c> with a message saying it *needs an ordered-set aggregate and is not
/// implemented*, which was true and is a strange thing for a server over PostgreSQL to say.
/// </para>
/// <para>
/// <b>The fraction is required, not defaulted.</b> A percentile with no fraction is a request
/// nobody meant to make; answering it with the median would be inventing the question.
/// </para>
/// </remarks>
public sealed class PercentileStatisticTests
{
    private const int Srid = 3857;

    private static readonly FieldDescription[] Fields =
    [
        new("objectid", FieldType.Integer, false, null),
        new("nufus", FieldType.Integer, true, null),
    ];

    private static QueryCollection Query(params (string Key, string Value)[] pairs)
    {
        Dictionary<string, Microsoft.Extensions.Primitives.StringValues> values = [];

        foreach ((string key, string value) in pairs)
        {
            values[key] = value;
        }

        return new QueryCollection(values);
    }

    private static FeatureQuery Parse(string statistics)
    {
        Assert.True(
            FeatureServerQueryParameters.TryParse(
                Query(("outStatistics", statistics), ("returnGeometry", "false")),
                "objectid", Srid, Fields,
                out FeatureQuery? query, out _, out string? error),
            error);

        return query!;
    }

    private static string Refuse(string statistics)
    {
        Assert.False(FeatureServerQueryParameters.TryParse(
            Query(("outStatistics", statistics), ("returnGeometry", "false")),
            "objectid", Srid, Fields, out _, out _, out string? error));

        Assert.False(string.IsNullOrWhiteSpace(error), "A refusal must say why.");

        return error!;
    }

    [Fact]
    public void Both_percentile_kinds_are_read_with_their_fraction_and_direction()
    {
        FeatureQuery query = Parse(
            """
            [{"statisticType":"PERCENTILE_CONT","statisticParameters":{"value":0.25},
              "onStatisticField":"nufus","outStatisticFieldName":"q1"},
             {"statisticType":"percentile_disc","statisticParameters":{"value":0.9,"orderBy":"DESC"},
              "onStatisticField":"nufus","outStatisticFieldName":"top"}]
            """);

        StatisticRequest first = query.Statistics[0];
        StatisticRequest second = query.Statistics[1];

        Assert.Equal(StatisticKind.PercentileContinuous, first.Kind);
        Assert.Equal(0.25, first.Fraction);
        Assert.False(first.Descending);

        // Case-insensitive, because Esri's own documentation writes the type in capitals and
        // every other type here in lower case.
        Assert.Equal(StatisticKind.PercentileDiscrete, second.Kind);
        Assert.Equal(0.9, second.Fraction);
        Assert.True(second.Descending);
    }

    [Fact]
    public void A_percentile_without_its_fraction_is_refused_rather_than_assumed_to_be_the_median()
    {
        string error = Refuse(
            """
            [{"statisticType":"PERCENTILE_CONT","onStatisticField":"nufus",
              "outStatisticFieldName":"x"}]
            """);

        Assert.Contains("statisticParameters", error, StringComparison.Ordinal);
        Assert.Contains("0.5 is the median", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.4")]
    [InlineData("-0.2")]
    public void A_fraction_outside_zero_to_one_is_refused(string value)
    {
        string error = Refuse(
            $$"""
            [{"statisticType":"PERCENTILE_CONT","statisticParameters":{"value":{{value}}},
              "onStatisticField":"nufus","outStatisticFieldName":"x"}]
            """);

        Assert.Contains("fraction from 0 to 1", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direction_that_is_neither_ascending_nor_descending_is_refused()
    {
        // <b>Refused rather than ignored.</b> An unrecognised `orderBy` silently treated as
        // ascending answers a different question from the one asked, and the caller has no way
        // to see that it happened.
        string error = Refuse(
            """
            [{"statisticType":"PERCENTILE_CONT","statisticParameters":{"value":0.5,"orderBy":"SIDEWAYS"},
              "onStatisticField":"nufus","outStatisticFieldName":"x"}]
            """);

        Assert.Contains("neither 'ASC' nor 'DESC'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ordinary_statistic_still_needs_no_parameters_and_carries_no_fraction()
    {
        FeatureQuery query = Parse(
            """
            [{"statisticType":"avg","onStatisticField":"nufus","outStatisticFieldName":"mean"}]
            """);

        StatisticRequest only = Assert.Single(query.Statistics);

        Assert.Equal(StatisticKind.Avg, only.Kind);
        Assert.Equal(0, only.Fraction);
    }

    [Fact]
    public void The_refusal_for_an_unknown_type_no_longer_says_percentile_is_unimplemented()
    {
        // The old message ended *Percentile needs an ordered-set aggregate and is not
        // implemented*. Leaving that sentence in place after implementing it is how a server
        // teaches people not to try.
        string error = Refuse(
            """
            [{"statisticType":"median","onStatisticField":"nufus","outStatisticFieldName":"x"}]
            """);

        Assert.Contains("percentile_cont", error, StringComparison.Ordinal);
        Assert.DoesNotContain("not implemented", error, StringComparison.Ordinal);
    }
}
