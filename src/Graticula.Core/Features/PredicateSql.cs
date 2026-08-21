using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Graticula.Features;

/// <summary>
/// Turns an <see cref="AttributePredicate"/> into parameterised SQL.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one place a predicate becomes a statement.</b> Every front end — the
/// SQL-92 grammar ArcGIS sends, the Filter Encoding 2.0 that WFS sends — produces
/// a tree and stops. Nothing else in this repository writes a comparison into
/// SQL, which is what makes the safety argument reviewable in one file instead of
/// once per protocol.
/// </para>
/// <para>
/// <b>It re-matches every column against the layer, and that is deliberate
/// duplication.</b> <see cref="WhereClause"/> already matches them, to refuse an
/// unknown field with a position the caller can act on. This match is the
/// guarantee rather than the message: an identifier reaches SQL only after being
/// found in the layer's own list, so the emitted text is a name we already had.
/// A front end written later cannot open an injection by forgetting a step,
/// because the step it would forget is not in the front end.
/// </para>
/// <para>
/// <b>Parentheses are computed, not remembered.</b> The tree carries no node for
/// grouping; this adds the brackets precedence requires — around a disjunction
/// under a conjunction, and around anything under a negation — and drops the ones
/// the caller wrote and did not need. So <c>(a and b) or c</c> comes back without
/// its brackets and means what it meant, while <c>(a or b) and c</c> keeps them
/// because without them it would not.
/// </para>
/// </remarks>
public static class PredicateSql
{
    /// <summary>How deep the tree may nest.</summary>
    /// <remarks>
    /// <b>Its own bound rather than the parser's.</b> <see cref="WhereClause"/>
    /// caps recursion while parsing, which protects the trees it builds; this
    /// method is public and the next front end reads XML, where nesting is free to
    /// write and a deep enough document is a stack overflow that .NET cannot
    /// catch. A guard that lives in one producer is not a guard.
    /// </remarks>
    public const int MaximumDepth = 32;

    /// <summary>Emits a predicate as SQL with its parameters.</summary>
    /// <param name="predicate">The tree, or <see langword="null"/> for no predicate.</param>
    /// <param name="columns">Column names the predicate may mention.</param>
    /// <param name="quote">How to quote a column name for the target dialect.</param>
    /// <param name="parsed">The statement fragment and the values it binds.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it emitted.</returns>
    public static bool TryEmit(
        AttributePredicate? predicate,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        out ParsedWhere parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(quote);

        parsed = default;
        error = null;

        if (predicate is null)
        {
            parsed = new ParsedWhere(string.Empty, []);
            return true;
        }

        StringBuilder sql = new();
        List<object?> parameters = [];

        if (!Write(predicate, sql, parameters, columns, quote, 0, out error))
        {
            return false;
        }

        parsed = new ParsedWhere(sql.ToString(), parameters);
        return true;
    }

    private static bool Write(
        AttributePredicate node,
        StringBuilder sql,
        List<object?> parameters,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        int depth,
        out string? error)
    {
        error = null;

        if (depth > MaximumDepth)
        {
            error =
                $"The predicate nests more than {MaximumDepth} levels deep. The limit exists "
                + "because emitting is recursive and a deep enough tree would exhaust the stack, "
                + "which cannot be caught.";
            return false;
        }

        switch (node)
        {
            // <b>`false`, and the planner deletes it.</b> There is no parameter to bind and
            // nothing to resolve against the columns, so this is the one predicate that
            // cannot fail to emit — which is the point of it existing at all.
            case AttributePredicate.MatchesNothing:
                sql.Append("false");
                return true;

            case AttributePredicate.Conjunction and:
                return Branch(and.Left, " and ", and.Right, sql, parameters, columns, quote, depth, out error);

            case AttributePredicate.Disjunction or:
                return Branch(or.Left, " or ", or.Right, sql, parameters, columns, quote, depth, out error);

            case AttributePredicate.Negation not:
                sql.Append("not (");

                if (!Write(not.Operand, sql, parameters, columns, quote, depth + 1, out error))
                {
                    return false;
                }

                sql.Append(')');
                return true;

            case AttributePredicate.Comparison compare:
                if (!Resolve(compare.Column, columns, quote, out string? comparand, out error))
                {
                    return false;
                }

                sql.Append(comparand).Append(' ').Append(Spelling(compare.Operator)).Append(' ')
                   .Append(Bind(parameters, compare.Value));

                return true;

            case AttributePredicate.IsNull isNull:
                if (!Resolve(isNull.Column, columns, quote, out string? nullable, out error))
                {
                    return false;
                }

                sql.Append(nullable).Append(isNull.Negated ? " is not null" : " is null");
                return true;

            case AttributePredicate.Matches like:
                if (!Resolve(like.Column, columns, quote, out string? matched, out error))
                {
                    return false;
                }

                sql.Append(matched).Append(like.Negated ? " not like " : " like ")
                   .Append(Bind(parameters, like.Pattern));

                return true;

            case AttributePredicate.Between between:
                if (!Resolve(between.Column, columns, quote, out string? bounded, out error))
                {
                    return false;
                }

                sql.Append(bounded).Append(between.Negated ? " not between " : " between ")
                   .Append(Bind(parameters, between.Low)).Append(" and ")
                   .Append(Bind(parameters, between.High));

                return true;

            case AttributePredicate.OneOf list:
                return WriteIn(list, sql, parameters, columns, quote, out error);

            default:
                // Unreachable while every node is handled above, and a compile-time
                // check is not available for a hierarchy this open. If a node is
                // added and forgotten, this refuses rather than emitting nothing.
                error = $"'{node.GetType().Name}' is not a predicate this emitter knows.";
                return false;
        }
    }

    private static bool Branch(
        AttributePredicate left,
        string keyword,
        AttributePredicate right,
        StringBuilder sql,
        List<object?> parameters,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        int depth,
        out string? error)
    {
        // A disjunction under a conjunction needs its brackets or the statement
        // means something else; every other pairing is safe flat, because the
        // tree's own shape already carries the precedence.
        bool bracket = keyword == " and ";

        if (!Side(left, bracket, sql, parameters, columns, quote, depth, out error))
        {
            return false;
        }

        sql.Append(keyword);

        return Side(right, bracket, sql, parameters, columns, quote, depth, out error);
    }

    private static bool Side(
        AttributePredicate side,
        bool bracketDisjunction,
        StringBuilder sql,
        List<object?> parameters,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        int depth,
        out string? error)
    {
        bool brackets = bracketDisjunction && side is AttributePredicate.Disjunction;

        if (brackets)
        {
            sql.Append('(');
        }

        if (!Write(side, sql, parameters, columns, quote, depth + 1, out error))
        {
            return false;
        }

        if (brackets)
        {
            sql.Append(')');
        }

        return true;
    }

    private static bool WriteIn(
        AttributePredicate.OneOf list,
        StringBuilder sql,
        List<object?> parameters,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        out string? error)
    {
        if (!Resolve(list.Column, columns, quote, out string? column, out error))
        {
            return false;
        }

        if (list.Values is null || list.Values.Count == 0)
        {
            // 'in ()' is not valid SQL, and a front end can build one where the
            // grammar cannot. Refusing names the fault; emitting it would make
            // the database name something else.
            error = $"An 'in' on '{list.Column}' has no values, and an empty list matches nothing.";
            return false;
        }

        List<string> placeholders = new(list.Values.Count);

        foreach (object? value in list.Values)
        {
            placeholders.Add(Bind(parameters, value));
        }

        sql.Append(column).Append(list.Negated ? " not in (" : " in (")
           .Append(string.Join(", ", placeholders)).Append(')');

        return true;
    }

    /// <summary>Matches a name against the layer's columns and quotes what was found.</summary>
    private static bool Resolve(
        string? column,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        out string? quoted,
        out string? error)
    {
        quoted = null;
        error = null;

        foreach (string known in columns)
        {
            if (string.Equals(known, column, StringComparison.OrdinalIgnoreCase))
            {
                quoted = quote(known);
                return true;
            }
        }

        error =
            $"'{column}' is not a field of this layer. A predicate may only mention fields the "
            + "layer document lists.";

        return false;
    }

    private static string Bind(List<object?> parameters, object? value)
    {
        parameters.Add(value);
        return $"@w{(parameters.Count - 1).ToString(CultureInfo.InvariantCulture)}";
    }

    private static string Spelling(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equal => "=",
        ComparisonOperator.NotEqual => "<>",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not a comparison operator."),
    };
}
