using System.Collections.Generic;
using System.Linq;
using Graticula.Features;
using Xunit;

namespace Graticula.Core.Tests;

/// <summary>
/// An SQL expression in onStatisticField is read by the where grammar and bound, never passed through
/// (supportsSqlExpression, the ArcGIS gap review; ADR-008 §4a-iv).
/// </summary>
public sealed class StatisticExpressionTests
{
    private static readonly Dictionary<string, FieldType> Fields = new()
    {
        ["str_angle"] = FieldType.Double,
        ["population"] = FieldType.Integer,
        ["province"] = FieldType.Text,
    };

    private static string Quote(string name) => $"\"{name}\"";

    [Fact]
    public void Esris_own_example_becomes_sql_with_its_literal_bound()
    {
        Assert.True(StatisticExpression.TryParse("str_angle*100", Fields, Quote, 2, out BoundExpression? bound, out string? error), error);

        Assert.Contains("\"str_angle\"", bound!.Sql);
        Assert.Contains("@s2_0", bound.Sql);
        Assert.DoesNotContain("100", bound.Sql);
        KeyValuePair<string, object?> value = Assert.Single(bound.Parameters);
        Assert.Equal("s2_0", value.Key);
        Assert.Equal(100L, System.Convert.ToInt64(value.Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_function_over_a_text_field_says_it_yields_text()
    {
        Assert.True(StatisticExpression.TryParse("UPPER(province)", Fields, Quote, 0, out BoundExpression? bound, out string? error), error);

        Assert.Equal(ExpressionKind.Text, bound!.Kind);
    }

    [Theory]
    [InlineData("population) IS NULL OR (1=1", "onStatisticField")]
    [InlineData("population; drop table x", "neither a field")]
    [InlineData("secret*2", "neither a field")]
    [InlineData("(select 1)", "neither a field")]
    public void What_is_not_one_expression_over_the_layers_fields_is_refused(string text, string reason)
    {
        Assert.False(StatisticExpression.TryParse(text, Fields, Quote, 0, out _, out string? error));
        Assert.Contains(reason, error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("population", false)]
    [InlineData("str_angle*100", true)]
    [InlineData("ABS(population)", true)]
    public void A_name_is_not_an_expression(string text, bool expression) =>
        Assert.Equal(expression, StatisticExpression.IsExpression(text));

    [Fact]
    public void Each_statistic_has_placeholders_of_its_own()
    {
        Assert.True(StatisticExpression.TryParse("population + 1", Fields, Quote, 0, out BoundExpression? first, out _));
        Assert.True(StatisticExpression.TryParse("population + 2", Fields, Quote, 1, out BoundExpression? second, out _));

        Assert.Empty(first!.Parameters.Select(p => p.Key).Intersect(second!.Parameters.Select(p => p.Key)));
    }
}
