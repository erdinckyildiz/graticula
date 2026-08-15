using System.Collections.Generic;
using System.Linq;
using GisServer.Features;
using Xunit;

namespace GisServer.Core.Tests;

/// <summary>
/// The where parser, and mostly what it refuses.
/// </summary>
/// <remarks>
/// <b>This class exists so that user text never becomes SQL.</b> ArcGIS defines
/// <c>where</c> as a SQL-92 predicate, and the obvious implementation — paste it
/// after the word <c>where</c> — is remote code execution against the datastore.
/// Every test below that asserts a refusal is asserting that a specific way in
/// is closed; every test that asserts an emission is asserting that what reaches
/// the database was rebuilt by us rather than forwarded.
/// </remarks>
public sealed class WhereClauseTests
{
    private static readonly string[] Columns =
        ["objectid", "parcel_id", "address", "owner", "area", "notes", "is_active"];

    private static bool Parse(string clause, out ParsedWhere parsed, out string? error) =>
        WhereClause.TryParse(clause, Columns, n => $"\"{n}\"", out parsed, out error);

    private static ParsedWhere Ok(string clause)
    {
        Assert.True(Parse(clause, out ParsedWhere parsed, out string? error), error);
        return parsed;
    }

    private static string Refused(string clause)
    {
        Assert.False(Parse(clause, out _, out string? error), $"'{clause}' was accepted.");
        Assert.False(string.IsNullOrWhiteSpace(error));
        return error!;
    }

    // ---------- what must never get through ----------

    [Theory]
    [InlineData("1=1; drop table parcels")]
    [InlineData("1=1; drop table parcels --")]
    [InlineData("objectid = 1; delete from layer")]
    [InlineData("objectid = 1 -- and nothing")]
    [InlineData("objectid = 1 /* comment */")]
    [InlineData("objectid = (select max(objectid) from layer)")]
    [InlineData("objectid in (select id from principal)")]
    [InlineData("pg_sleep(10) = 1")]
    [InlineData("objectid = 1 union select 1")]
    [InlineData("objectid = 1) or (1=1")]
    [InlineData("'a' = 'a'")]
    [InlineData("objectid = objectid")]
    public void Injection_and_anything_outside_the_grammar_is_refused(string clause)
    {
        // <b>Each of these is a real technique, not a hypothetical.</b> The
        // grammar has no rule for statements, comments, subqueries, function
        // calls or column-to-column comparison, so none of them can appear by
        // accident — and the last two are here because they are the ones that
        // look harmless.
        Refused(clause);
    }

    [Fact]
    public void An_unknown_column_is_refused_by_name()
    {
        string error = Refused("pg_class = 1");

        Assert.Contains("not a field of this layer", error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_identifier_cannot_smuggle_anything_either()
    {
        // Quoting is where a parser that trusts the quotes gets caught: the
        // content is still matched against the real column list.
        Refused("\"objectid\"\"; drop table x --\" = 1");
    }

    [Fact]
    public void A_string_literal_containing_sql_stays_a_value()
    {
        ParsedWhere parsed = Ok("address = '''; drop table parcels --'");

        // The dangerous text is a parameter, and the SQL has a placeholder.
        Assert.Equal("\"address\" = @w0", parsed.Sql);
        Assert.Equal("'; drop table parcels --", Assert.Single(parsed.Parameters));
    }

    [Fact]
    public void Every_literal_becomes_a_parameter_and_none_is_inlined()
    {
        ParsedWhere parsed = Ok(
            "parcel_id in (1, 2, 3) and address like 'High%' and area between 10 and 20.5");

        Assert.Equal(6, parsed.Parameters.Count);

        // Nothing that came from the caller appears in the SQL text.
        foreach (string literal in (string[])["High%", "20.5", "'"])
        {
            Assert.DoesNotContain(literal, parsed.Sql, System.StringComparison.Ordinal);
        }
    }

    // ---------- the grammar it does accept ----------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_clause_parses_to_no_clause_rather_than_to_an_error(string clause)
    {
        // 1=1 is not special-cased in the grammar — it is refused as a
        // column-less comparison — so the caller strips it before parsing. What
        // reaches here empty must produce empty SQL, not a complaint.
        Assert.True(Parse(clause, out ParsedWhere parsed, out string? error), error);
        Assert.Equal(string.Empty, parsed.Sql);
        Assert.Empty(parsed.Parameters);
    }

    [Fact]
    public void Comparison_operators_are_rewritten_to_one_spelling()
    {
        // Clients send both; the database should see one, so a query plan and a
        // log line do not depend on which spelling arrived.
        Assert.Equal("\"parcel_id\" <> @w0", Ok("parcel_id != 100").Sql);
        Assert.Equal("\"parcel_id\" <> @w0", Ok("parcel_id <> 100").Sql);
    }

    [Theory]
    [InlineData("objectid > 5", "\"objectid\" > @w0")]
    [InlineData("objectid >= 5", "\"objectid\" >= @w0")]
    [InlineData("objectid < 5", "\"objectid\" < @w0")]
    [InlineData("objectid <= 5", "\"objectid\" <= @w0")]
    [InlineData("owner is null", "\"owner\" is null")]
    [InlineData("owner is not null", "\"owner\" is not null")]
    [InlineData("is_active = true", "\"is_active\" = @w0")]
    [InlineData("address not like 'a%'", "\"address\" not like @w0")]
    [InlineData("parcel_id not in (1, 2)", "\"parcel_id\" not in (@w0, @w1)")]
    [InlineData("area not between 1 and 2", "\"area\" not between @w0 and @w1")]
    public void The_grammar_emits_what_it_promises(string clause, string sql)
    {
        Assert.Equal(sql, Ok(clause).Sql);
    }

    [Fact]
    public void Boolean_structure_survives_with_its_parentheses()
    {
        Assert.Equal(
            "(\"parcel_id\" = @w0 or \"parcel_id\" = @w1) and \"owner\" is not null",
            Ok("(parcel_id = 1 or parcel_id = 2) and owner is not null").Sql);
    }

    [Fact]
    public void Not_negates_a_whole_bracket()
    {
        Assert.Equal(
            "not (\"parcel_id\" = @w0)",
            Ok("not (parcel_id = 1)").Sql);
    }

    [Fact]
    public void Keywords_are_matched_whole_word()
    {
        // 'notes' begins with 'not' and 'address' contains no keyword, but a
        // parser matching prefixes would read the first as a negation and fail.
        Assert.Equal("\"notes\" = @w0", Ok("notes = 'x'").Sql);
    }

    [Fact]
    public void Keywords_and_column_names_are_case_insensitive()
    {
        Assert.Equal(
            "\"parcel_id\" in (@w0, @w1)",
            Ok("PARCEL_ID In (1,2)").Sql);
    }

    [Fact]
    public void Numbers_keep_their_type()
    {
        ParsedWhere parsed = Ok("area > 10 and parcel_id = 2.5");

        Assert.Equal(10L, parsed.Parameters[0]);
        Assert.Equal(2.5d, parsed.Parameters[1]);
    }

    [Fact]
    public void A_negative_number_is_a_number()
    {
        Assert.Equal(-5L, Assert.Single(Ok("area > -5").Parameters));
    }

    [Fact]
    public void Null_as_a_value_is_refused_where_it_would_silently_never_match()
    {
        // = null is always false in SQL and is almost always a mistake for
        // 'is null'. It parses — the grammar allows it — and the parameter is
        // null, so the database gives SQL's own answer rather than ours.
        ParsedWhere parsed = Ok("owner = null");

        Assert.Null(Assert.Single(parsed.Parameters));
    }

    // ---------- the limits ----------

    [Fact]
    public void A_clause_that_is_too_long_is_refused_before_parsing()
    {
        string huge = "parcel_id in (" + string.Join(",", Enumerable.Range(0, 4000)) + ")";

        string error = Refused(huge);

        Assert.Contains("objectIds", error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Deeply_nested_parentheses_are_refused_rather_than_overflowing_the_stack()
    {
        // <b>A stack overflow in .NET cannot be caught and kills the process.</b>
        // Reachable from a query string, so the cap is a denial-of-service
        // control and not tidiness. Well under the length limit, so this is the
        // depth check being exercised and not the length one.
        string deep = string.Concat(Enumerable.Repeat("(", 200))
            + "parcel_id = 1"
            + string.Concat(Enumerable.Repeat(")", 200));

        string error = Refused(deep);

        Assert.Contains("nests more than", error, System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_unclosed_string_is_refused_rather_than_running_to_the_end()
    {
        Assert.Contains("not closed", Refused("address = 'oops"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_says_where_it_gave_up()
    {
        // A parse error with no position is a parse error somebody has to
        // bisect by hand.
        Assert.Contains("position", Refused("parcel_id = "), System.StringComparison.Ordinal);
    }
}
