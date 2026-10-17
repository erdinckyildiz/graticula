using System.Collections.Generic;
using Graticula.Features;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// ArcGIS's havingClause is parsed and bound, never passed through — Q-109, closing the refusal D-41 put in place
/// after the clause was found appended to SQL unparsed.
/// </summary>
public sealed class HavingClauseTests
{
    private static readonly Dictionary<string, FieldType> Fields = new()
    {
        ["population"] = FieldType.Integer,
        ["province"] = FieldType.Text,
    };

    private static string Quote(string name) => $"\"{name}\"";

    [Fact]
    public void An_aggregate_comparison_becomes_sql_with_its_literal_bound()
    {
        Assert.True(HavingClause.TryParse("SUM(population) > 1000000", Fields, ["province"], Quote, out ParsedWhere parsed, out string? error), error);

        Assert.Contains("sum(\"population\")", parsed.Sql);
        Assert.Contains("@h0", parsed.Sql);
        Assert.DoesNotContain("1000000", parsed.Sql);
        Assert.Equal(1000000L, System.Convert.ToInt64(Assert.Single(parsed.Parameters), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Count_star_and_a_grouped_field_are_read_too()
    {
        Assert.True(HavingClause.TryParse("COUNT(*) >= 5 AND province <> 'Ankara'", Fields, ["province"], Quote, out ParsedWhere parsed, out string? error), error);

        Assert.Contains("count(*)", parsed.Sql);
        Assert.Contains("\"province\"", parsed.Sql);
        Assert.DoesNotContain("Ankara", parsed.Sql);
    }

    [Theory]
    [InlineData("SUM(population) > 1; drop table x", "could not be read")]
    [InlineData("SUM(secret) > 1", "not a field")]
    [InlineData("SUM(*) > 1", "COUNT(*)")]
    [InlineData("population > 1", "names none")]
    public void What_is_not_a_having_clause_is_refused_with_a_reason(string clause, string reason)
    {
        Assert.False(HavingClause.TryParse(clause, Fields, [], Quote, out _, out string? error));
        Assert.Contains(reason, error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_aggregate_written_inside_a_string_stays_a_string()
    {
        Assert.True(HavingClause.TryParse("COUNT(*) > 0 AND province = 'SUM(population)'", Fields, ["province"], Quote, out ParsedWhere parsed, out string? error), error);

        Assert.DoesNotContain("sum(", parsed.Sql);
        Assert.Contains("SUM(population)", (IEnumerable<object?>)parsed.Parameters);
    }
}
