using System;
using Graticula.Features;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// A UUID column — a GlobalID — is compared as a UUID and matched as text in the spelling of the face that asked.
/// </summary>
/// <remarks>
/// Found by the OGC CITE WFS suite, red every day from 2026-09-16: every filter language sends its literals as text,
/// and PostgreSQL compares neither <c>uuid = text</c> nor <c>uuid like text</c>.
/// </remarks>
public sealed class AGlobalIdIsComparedAsAGuidTests
{
    private static readonly string[] Columns = ["globalid", "label"];
    private static readonly Guid Known = Guid.Parse("52e48c7a-df90-4af8-a6ce-3e1daa03305c");

    private static ParsedWhere Emit(AttributePredicate predicate, bool arcGis)
    {
        Assert.True(
            PredicateSql.TryEmit(
                predicate, Columns, c => $"\"{c}\"", out ParsedWhere parsed, out string? error,
                dialect: SqlDialect.PostgreSql.WithGuids(["globalid"], arcGis)),
            error);
        return parsed;
    }

    [Theory]
    [InlineData("{52E48C7A-DF90-4AF8-A6CE-3E1DAA03305C}")]
    [InlineData("52e48c7a-df90-4af8-a6ce-3e1daa03305c")]
    public void An_equality_binds_the_value_as_a_guid_however_it_was_written(string written)
    {
        ParsedWhere parsed = Emit(new AttributePredicate.Comparison("globalid", ComparisonOperator.Equal, written, IgnoreCase: false), arcGis: true);

        Assert.Equal("\"globalid\" = @w0", parsed.Sql);
        Assert.Equal(Known, Assert.IsType<Guid>(Assert.Single(parsed.Parameters)));
    }

    [Theory]
    [InlineData(true, "('{' || upper(\"globalid\"::text) || '}') like @w0")]
    [InlineData(false, "(\"globalid\"::text) like @w0")]
    public void A_pattern_matches_the_text_the_face_writes(bool arcGis, string sql)
    {
        ParsedWhere parsed = Emit(new AttributePredicate.Matches("globalid", "%305C}", Negated: false, IgnoreCase: false), arcGis);

        Assert.Equal(sql, parsed.Sql);
    }

    [Fact]
    public void A_value_that_is_not_a_guid_is_refused_by_name()
    {
        Assert.False(PredicateSql.TryEmit(
            new AttributePredicate.Comparison("globalid", ComparisonOperator.Equal, "nope", IgnoreCase: false),
            Columns, c => $"\"{c}\"", out _, out string? error,
            dialect: SqlDialect.PostgreSql.WithGuids(["globalid"], arcGisText: true)));

        Assert.Contains("'nope' is not one", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_that_is_not_a_guid_is_untouched()
    {
        ParsedWhere parsed = Emit(new AttributePredicate.Comparison("label", ComparisonOperator.Equal, "x", IgnoreCase: false), arcGis: true);

        Assert.Equal("\"label\" = @w0", parsed.Sql);
        Assert.Equal("x", Assert.Single(parsed.Parameters));
    }
}
