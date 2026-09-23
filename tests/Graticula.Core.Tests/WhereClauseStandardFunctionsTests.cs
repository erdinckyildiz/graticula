using System;
using System.Collections.Generic;
using Graticula.Features;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ArcGIS's standardized query functions and arithmetic, as ADR-083 put them into the where grammar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asserted on the statement, in both dialects.</b> What a function means is what it is spelled as, and
/// the spellings that differ between PostgreSQL and DuckDB were measured on both before this was written
/// (<see cref="SqlDialect"/>'s remarks); the datastore tests run a set of these against each database.
/// </para>
/// </remarks>
public sealed class WhereClauseStandardFunctionsTests
{
    private static readonly string[] Columns = ["name", "population", "area", "code", "founded", "objectid"];

    private static readonly Dictionary<string, FieldType> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = FieldType.Text,
        ["code"] = FieldType.Text,
        ["population"] = FieldType.Integer,
        ["objectid"] = FieldType.Integer,
        ["area"] = FieldType.Double,
        ["founded"] = FieldType.Date,
    };

    private static string Quote(string name) => $"\"{name}\"";

    private static ParsedWhere Ok(string clause, bool typed = true)
    {
        Assert.True(
            WhereClause.TryParse(clause, Columns, Quote, out ParsedWhere parsed, out string? error, typed ? Types : null),
            error);

        return parsed;
    }

    private static string Duck(string clause)
    {
        ParsedWhere parsed = Ok(clause);

        Assert.True(
            PredicateSql.TryEmit(parsed.Predicate, Columns, Quote, out ParsedWhere emitted, out string? error, dialect: SqlDialect.DuckDb),
            error);

        return emitted.Sql;
    }

    private static string Refused(string clause)
    {
        Assert.False(
            WhereClause.TryParse(clause, Columns, Quote, out _, out string? error, Types),
            $"'{clause}' was accepted.");

        return error!;
    }

    [Theory]
    [InlineData("CAST(population AS FLOAT) > 1.5", "cast(\"population\" as double precision) > @w0")]
    [InlineData("CAST(area AS INT) = 7", "cast(\"area\" as integer) = @w0")]
    [InlineData("CAST(population AS VARCHAR(3)) = '123'", "substring(cast(\"population\" as varchar) from 1 for 3) = @w0")]
    [InlineData("CHAR_LENGTH(name) > 5", "char_length(\"name\") > @w0")]
    [InlineData("SUBSTRING(code, 1, 2) = 'TR'", "substring(\"code\" from 1 for 2) = @w0")]
    [InlineData("SUBSTRING(code FROM 3) = '06'", "substring(\"code\" from 3) = @w0")]
    [InlineData("POSITION('-', code) = 3", "position(@w0 in \"code\") = @w1")]
    [InlineData("POSITION('-' IN code) = 3", "position(@w0 in \"code\") = @w1")]
    [InlineData("TRIM(name) = 'Ankara'", "trim(both ' ' from \"name\") = @w0")]
    [InlineData("TRIM(LEADING ' ' FROM name) = 'Ankara'", "trim(leading ' ' from \"name\") = @w0")]
    [InlineData("UPPER(TRIM(name)) = 'ANKARA'", "upper(trim(both ' ' from \"name\")) = @w0")]
    [InlineData("CONCAT(code, name) = 'TR06Ankara'", "concat(\"code\", \"name\") = @w0")]
    [InlineData("ABS(area) < 3", "abs(\"area\") < @w0")]
    [InlineData("LOG(area) > 1", "ln(\"area\") > @w0")]
    [InlineData("LOG10(area) > 1", "log10(\"area\") > @w0")]
    [InlineData("ROUND(area, 1) = 7.5", "round(cast(\"area\" as numeric), 1) = @w0")]
    [InlineData("ROUND(area) = 8", "round(\"area\") = @w0")]
    [InlineData("TRUNCATE(area, 1) = 7.5", "trunc(cast(\"area\" as numeric), 1) = @w0")]
    [InlineData("MOD(population, 2) = 0", "mod(cast(\"population\" as numeric), cast(@w0 as numeric)) = @w1")]
    [InlineData("EXTRACT(YEAR FROM founded) = 2026", "extract(year from \"founded\") = @w0")]
    [InlineData("COALESCE(name, code) = 'x'", "coalesce(\"name\", \"code\") = @w0")]
    [InlineData("NULLIF(population, 0) IS NULL", "nullif(\"population\", @w0) is null")]
    public void A_standardized_function_reaches_postgresql_as_its_spelling(string clause, string sql)
    {
        Assert.Equal(sql, Ok(clause).Sql);
    }

    [Theory]
    [InlineData("population / 2 = 3", "(\"population\" // $w0) = $w1")]
    [InlineData("area / 2 = 3.75", "(\"area\" / $w0) = $w1")]
    [InlineData("ROUND(area, 1) = 7.5", "round(\"area\", 1) = $w0")]
    [InlineData("MOD(area, 2) = 1.5", "mod(\"area\", $w0) = $w1")]
    [InlineData("CAST(code AS VARCHAR(2)) = 'TR'", "substring(cast(\"code\" as varchar) from 1 for 2) = $w0")]
    public void DuckDB_gets_its_own_spelling_where_the_two_differ(string clause, string sql)
    {
        Assert.Equal(sql, Duck(clause));
    }

    [Fact]
    public void Integer_division_truncates_in_postgresql_as_written()
    {
        Assert.Equal("(\"population\" / @w0) = @w1", Ok("population / 2 = 3").Sql);
    }

    [Fact]
    public void Precedence_is_the_trees_and_the_brackets_say_it()
    {
        Assert.Equal("((\"population\" + @w0) * @w1) > @w2", Ok("(population + 1) * 2 > 10").Sql);
        Assert.Equal("(\"population\" + (@w0 * @w1)) > @w2", Ok("population + 1 * 2 > 10").Sql);
    }

    [Fact]
    public void A_bracket_opens_a_value_as_well_as_a_group()
    {
        Assert.Equal("(\"population\" + @w0) > @w1", Ok("(population + 1) > 3").Sql);
        Assert.Equal("\"population\" > @w0", Ok("(population) > 3").Sql);

        // The group still reads as the group it always was.
        Assert.Equal(
            "(\"name\" = @w0 or \"code\" = @w1) and \"population\" > @w2",
            Ok("(name = 'a' or code = 'b') and population > 3").Sql);
    }

    [Fact]
    public void Two_columns_compare_and_subtract()
    {
        Assert.Equal("(\"population\" - \"objectid\") > @w0", Ok("population - objectid > 0").Sql);
        Assert.Equal("\"population\" > \"objectid\"", Ok("population > objectid").Sql);
    }

    [Fact]
    public void A_negative_literal_is_still_a_literal()
    {
        ParsedWhere parsed = Ok("population > -5");

        Assert.Equal("\"population\" > @w0", parsed.Sql);
        Assert.Equal(-5L, parsed.Parameters[0]);
    }

    [Fact]
    public void A_date_written_as_esri_writes_it_is_read_once_here()
    {
        ParsedWhere parsed = Ok("founded > CAST('09/23/2026' AS DATE)");

        Assert.Equal("\"founded\" > @w0", parsed.Sql);
        Assert.Equal(new DateTimeOffset(2026, 9, 23, 0, 0, 0, TimeSpan.Zero), parsed.Parameters[0]);
    }

    [Fact]
    public void Current_date_is_accepted_as_esri_writes_it_with_brackets()
    {
        Assert.Equal("\"founded\" < @w0", Ok("founded < CURRENT_DATE()").Sql);
        Assert.Equal("\"founded\" < @w0", Ok("founded < CURRENT_TIME").Sql);
    }

    [Fact]
    public void A_bare_upper_on_a_column_keeps_its_own_path()
    {
        // UPPER(column) is the model's case-insensitive comparison (2026-09-15), not a function call.
        ParsedWhere parsed = Ok("UPPER(name) = 'ANKARA'");

        Assert.IsType<AttributePredicate.Comparison>(parsed.Predicate);
        Assert.Equal("lower(\"name\") = lower(@w0)", parsed.Sql);
    }

    [Theory]
    [InlineData("SUBSTRING(population, 1, 2) = '12'", "'population' holds a number")]
    [InlineData("CHAR_LENGTH(area) > 1", "'area' holds a number")]
    [InlineData("ABS(name) > 1", "'name' holds text")]
    [InlineData("EXTRACT(YEAR FROM name) = 2026", "'name' holds text")]
    [InlineData("name + 1 = 2", "'name' holds text")]
    [InlineData("CHAR_LENGTH(name) = 'five'", "compares a number with text")]
    [InlineData("TRIM('x' FROM name) = 'a'", "TRIM removes spaces")]
    [InlineData("SUBSTRING(code, population, 2) = 'a'", "whole number written in the clause")]
    [InlineData("CAST(population AS BLOB) = 1", "'blob' is none of them")]
    [InlineData("EXTRACT(WEEK FROM founded) = 1", "'week' is none of them")]
    [InlineData("POWER(area) > 1", "POWER takes 2 arguments")]
    [InlineData("CURRENT_USER = 'ci'", "CURRENT_USER is not evaluated")]
    [InlineData("CHAR_LENGTH('abc') = 3", "neither names a field")]
    [InlineData("UPPER('a') LIKE 'A'", "neither names a field")]
    public void A_function_given_the_wrong_kind_is_refused_naming_it(string clause, string said)
    {
        Assert.Contains(said, Refused(clause), StringComparison.Ordinal);
    }

    [Fact]
    public void Without_column_types_nothing_is_refused_for_its_kind()
    {
        // A caller that did not say what the columns hold gets the database's answer, as it always has.
        Assert.Equal("substring(\"population\" from 1 for 2) = @w0", Ok("SUBSTRING(population, 1, 2) = '12'", typed: false).Sql);
    }

    [Fact]
    public void Nesting_is_bounded_inside_functions_as_it_is_in_brackets()
    {
        string deep = new string('(', 40) + "population" + new string(')', 40) + " > 1";
        string calls = string.Concat(System.Linq.Enumerable.Repeat("ABS(", 40)) + "area" + new string(')', 40) + " > 1";

        Assert.Contains("nests more than", Refused(deep), StringComparison.Ordinal);
        Assert.Contains("nests more than", Refused(calls), StringComparison.Ordinal);
    }

    [Fact]
    public void Like_in_and_between_take_a_computed_left_side()
    {
        Assert.Equal("upper(trim(both ' ' from \"name\")) like @w0", Ok("UPPER(TRIM(name)) LIKE 'ANK%'").Sql);
        Assert.Equal("substring(\"code\" from 1 for 2) in (@w0, @w1)", Ok("SUBSTRING(code, 1, 2) IN ('TR', 'DE')").Sql);
        Assert.Equal("extract(year from \"founded\") between @w0 and @w1", Ok("EXTRACT(YEAR FROM founded) BETWEEN 2000 AND 2010").Sql);
        Assert.Equal("\"population\" between (\"objectid\" * @w0) and @w1", Ok("population BETWEEN objectid * 2 AND 100").Sql);
    }
}
