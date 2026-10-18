using System;
using System.Collections.Generic;
using System.Linq;

namespace Graticula.Features;

/// <summary>An expression a statistic is computed over, parsed and bound: SQL of this server's own and its values.</summary>
/// <param name="Text">The expression as the client wrote it, for messages.</param>
/// <param name="Sql">The fragment, with every literal a named placeholder.</param>
/// <param name="Parameters">Each placeholder's name, without its <c>@</c>, and the value bound to it.</param>
/// <param name="Kind">What it yields, as far as the parser can tell.</param>
public sealed record BoundExpression(
    string Text,
    string Sql,
    IReadOnlyList<KeyValuePair<string, object?>> Parameters,
    ExpressionKind Kind);

/// <summary>
/// An SQL expression a statistics query groups by, and the name it is answered under — <c>Expr1</c>, <c>Expr2</c>, as
/// ArcGIS names them (D-301, measured against Esri's sampleserver6 on 2026-10-18).
/// </summary>
/// <param name="Name">The column it is answered as.</param>
/// <param name="Expression">The expression, parsed and bound.</param>
public sealed record GroupExpression(string Name, BoundExpression Expression);

/// <summary>
/// An SQL expression in <c>outStatistics</c>' <c>onStatisticField</c> — <c>str_angle*100</c> in Esri's own example —
/// read by the where grammar (the ArcGIS gap review, 2026-10-17).
/// </summary>
/// <remarks>
/// <para>
/// <b>No caller text reaches SQL, as for <c>where</c> and <c>havingClause</c>.</b> The expression is handed to
/// <see cref="WhereClause"/> as the operand of <c>IS NULL</c>, and the tree that comes back must be exactly that one
/// test: anything else — a closing parenthesis that smuggles in an <c>OR</c>, a second statement — leaves a different
/// tree and is refused. Only the operand is then written, by the same writer a predicate's sides go through, so its
/// columns are the layer's and its literals are bound.
/// </para>
/// <para>
/// <b>Why borrow the predicate grammar rather than write an expression one.</b> ADR-083 already taught it ArcGIS's
/// standardized functions, arithmetic and casts, and those are what an expression in a statistic is made of; a second
/// grammar would be a second set of rules about which functions are allowed.
/// </para>
/// </remarks>
public static class StatisticExpression
{
    /// <summary>The placeholder for statistic <paramref name="statistic"/>'s <paramref name="index"/>th literal.</summary>
    public static string Placeholder(int statistic, int index) => $"@s{statistic}_{index}";

    /// <summary>Whether the text is an expression rather than a name: anything but letters, digits and underscores.</summary>
    public static bool IsExpression(string text) =>
        !string.IsNullOrWhiteSpace(text) && !text.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Parses an expression over the layer's fields, or says why not.</summary>
    /// <param name="text">The expression as sent.</param>
    /// <param name="fields">The layer's fields and their types.</param>
    /// <param name="quote">Quotes a column name for the dialect.</param>
    /// <param name="statistic">Which statistic of the request this is, so its placeholders are its own.</param>
    /// <param name="bound">The SQL and its values.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(
        string text,
        IReadOnlyDictionary<string, FieldType> fields,
        Func<string, string> quote,
        int statistic,
        out BoundExpression? bound,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(quote);

        bound = null;

        if (!WhereClause.TryParse($"({text}) IS NULL", [.. fields.Keys], quote, out ParsedWhere parsed, out error,
                fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase)))
        {
            error = $"'onStatisticField' of '{text}' is neither a field of this layer nor an expression this server "
                + $"reads: {error}";
            return false;
        }

        ScalarExpression? operand = parsed.Predicate switch
        {
            AttributePredicate.ExpressionIsNull { Negated: false } test => test.Operand,
            AttributePredicate.IsNull { Negated: false } test => new ScalarExpression.ColumnValue(test.Column, ExpressionKind.Unknown),
            _ => null,
        };

        if (operand is null)
        {
            error = $"'onStatisticField' of '{text}' is not one expression. A statistic is computed over a field or a "
                + "single value made of fields, numbers, arithmetic and the standardized SQL functions.";
            return false;
        }

        SqlDialect dialect = SqlDialect.PostgreSql.WithGuids(
            fields.Where(f => f.Value == FieldType.Guid).Select(f => f.Key), arcGisText: true);

        if (!PredicateSql.TryEmitExpression(operand, [.. fields.Keys], quote, i => Placeholder(statistic, i),
                out string sql, out IReadOnlyList<object?> values, out error, dialect))
        {
            return false;
        }

        bound = new BoundExpression(
            text,
            sql,
            [.. values.Select((value, i) => new KeyValuePair<string, object?>(Placeholder(statistic, i)[1..], value))],
            operand.Kind);
        return true;
    }
}
