using System;
using System.Collections.Generic;
using Graticula.Features;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// The case functions and date expressions ArcGIS clients write into <c>where</c>.
/// </summary>
/// <remarks>
/// Written 2026-09-15, after a review from an ArcGIS user's side: <c>UPPER(il) = 'ANKARA'</c>,
/// <c>observed_at &gt; DATE '2026-01-01'</c>, <c>CURRENT_TIMESTAMP - 30</c> and
/// <c>observed_at &gt;= 1735689600000</c> were all refused or reached the database as a type error.
/// The Maps SDK's search writes the first and Dashboards writes the second and third.
/// </remarks>
public sealed class WhereClauseFunctionsAndDatesTests
{
    private static readonly string[] Columns = ["objectid", "il", "observed_at", "area"];

    private static readonly Dictionary<string, FieldType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["objectid"] = FieldType.Integer,
        ["il"] = FieldType.Text,
        ["observed_at"] = FieldType.Date,
        ["area"] = FieldType.Double,
    };

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 10, 30, 0, TimeSpan.Zero);

    private static ParsedWhere Ok(string clause)
    {
        Assert.True(
            WhereClause.TryParse(clause, Columns, n => $"\"{n}\"", out ParsedWhere parsed, out string? error, Types, new FixedClock(Now)),
            error);
        return parsed;
    }

    private static string Refused(string clause)
    {
        Assert.False(
            WhereClause.TryParse(clause, Columns, n => $"\"{n}\"", out _, out string? error, Types, new FixedClock(Now)),
            $"'{clause}' was accepted.");
        return error!;
    }

    // ---------- case functions ----------

    [Theory]
    [InlineData("UPPER(il) = 'ANKARA'")]
    [InlineData("upper(il) = 'ANKARA'")]
    [InlineData("LOWER(il) = 'ankara'")]
    public void A_case_function_compared_with_a_value_it_could_produce_is_a_case_insensitive_comparison(string clause)
    {
        ParsedWhere parsed = Ok(clause);

        Assert.Equal("lower(\"il\") = lower(@w0)", parsed.Sql);
    }

    [Fact]
    public void A_case_function_like_is_ilike()
    {
        ParsedWhere parsed = Ok("UPPER(il) LIKE '%ANK%'");

        Assert.Equal("\"il\" ilike @w0", parsed.Sql);
        Assert.Equal("%ANK%", Assert.Single(parsed.Parameters));
    }

    [Fact]
    public void A_value_the_function_could_never_produce_matches_nothing()
    {
        // UPPER(il) has no lower-case letters in it, so it equals 'Ankara' for no row.
        Assert.Equal("false", Ok("UPPER(il) = 'Ankara'").Sql);
        Assert.Equal("false", Ok("UPPER(il) LIKE '%ank%'").Sql);
    }

    [Fact]
    public void Not_equal_to_a_value_it_could_never_produce_is_every_row_with_a_value()
    {
        Assert.Equal("\"il\" is not null", Ok("UPPER(il) <> 'Ankara'").Sql);
    }

    [Fact]
    public void A_case_function_in_a_list_keeps_only_the_values_it_could_produce()
    {
        ParsedWhere parsed = Ok("UPPER(il) IN ('ANKARA', 'Izmir', 'BURSA')");

        Assert.Equal(["ANKARA", "BURSA"], parsed.Parameters);
    }

    [Fact]
    public void Ordering_folded_text_is_refused_by_name()
    {
        Assert.Contains("=, <>, LIKE or IN", Refused("UPPER(il) < 'M'"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SUBSTRING(il) = 'A'")]
    [InlineData("UPPER(il = 'A'")]
    [InlineData("UPPER(pg_class) = 'A'")]
    [InlineData("UPPER(il) = UPPER(il)")]
    public void Anything_but_the_two_functions_over_a_real_column_is_still_refused(string clause)
    {
        Refused(clause);
    }

    // ---------- dates ----------

    [Theory]
    [InlineData("observed_at > DATE '2026-01-01'")]
    [InlineData("observed_at > date '2026-01-01'")]
    [InlineData("observed_at > TIMESTAMP '2026-01-01 00:00:00'")]
    public void A_date_literal_is_bound_as_a_moment(string clause)
    {
        ParsedWhere parsed = Ok(clause);

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), Assert.Single(parsed.Parameters));
    }

    [Theory]
    [InlineData("observed_at > CURRENT_TIMESTAMP - 30", -30.0)]
    [InlineData("observed_at > CURRENT_TIMESTAMP - INTERVAL '30' DAY", -30.0)]
    [InlineData("observed_at > CURRENT_TIMESTAMP + 1", 1.0)]
    [InlineData("observed_at > CURRENT_TIMESTAMP", 0.0)]
    public void Now_and_an_offset_are_evaluated_once_against_the_clock(string clause, double days)
    {
        Assert.Equal(Now.AddDays(days), Assert.Single(Ok(clause).Parameters));
    }

    [Fact]
    public void Current_date_is_midnight_utc_today()
    {
        Assert.Equal(
            new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero),
            Assert.Single(Ok("observed_at >= CURRENT_DATE").Parameters));
    }

    [Fact]
    public void A_number_compared_with_a_date_is_epoch_milliseconds()
    {
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1735689600000),
            Assert.Single(Ok("observed_at >= 1735689600000").Parameters));
    }

    [Fact]
    public void A_date_compared_with_a_field_that_is_not_one_is_refused_by_name()
    {
        Assert.Contains("'area' does not hold a date", Refused("area > DATE '2026-01-01'"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("observed_at > DATE 'yesterday'")]
    [InlineData("observed_at > CURRENT_TIMESTAMP - INTERVAL '30' FORTNIGHT")]
    [InlineData("observed_at > CURRENT_TIMESTAMP - 99999999999")]
    public void A_malformed_date_expression_is_refused(string clause)
    {
        Refused(clause);
    }

    [Fact]
    public void A_column_called_date_is_still_a_column()
    {
        string[] columns = ["date"];

        Assert.True(
            WhereClause.TryParse("date = 'x'", columns, n => $"\"{n}\"", out ParsedWhere parsed, out string? error),
            error);
        Assert.Equal("\"date\" = @w0", parsed.Sql);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Theory]
    [InlineData("CAST(name AS INTEGER) = 1", "CAST")]
    [InlineData("SUBSTRING (name, 1, 2) = 'ab'", "SUBSTRING")]
    [InlineData("COALESCE(name, 'x') = 'x'", "COALESCE")]
    public void A_function_it_does_not_evaluate_is_named_as_a_function(string clause, string function)
    {
        // V-75, the fourth ArcGIS review: these answered "'CAST' is not a field of this layer".
        string refused = Refused(clause);

        Assert.Contains($"'{function}' is not a function", refused, StringComparison.Ordinal);
        Assert.Contains("UPPER", refused, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_without_a_parenthesis_is_still_a_field()
    {
        Assert.Contains("'nosuch' is not a field of this layer", Refused("nosuch = 1"), StringComparison.Ordinal);
    }
}
