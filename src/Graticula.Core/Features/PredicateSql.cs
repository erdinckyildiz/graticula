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
    /// <param name="placeholder">
    /// How the target spells the <c>n</c>th bound value, counted from zero, when it differs from the
    /// dialect's own. Omitted, it is the dialect's — <c>@w0</c> for PostgreSQL.
    /// </param>
    /// <param name="dialect">The datastore's spellings; PostgreSQL when omitted.</param>
    /// <returns>Whether it emitted.</returns>
    /// <remarks>
    /// <b>The placeholder was the whole dialect difference until 2026-09-23</b> (ADR-066 §4), and the
    /// remark here said the parameter would become a dialect object when a second difference arrived.
    /// ArcGIS's standardized functions brought three (ADR-083), and <see cref="SqlDialect"/> holds them.
    /// </remarks>
    public static bool TryEmit(
        AttributePredicate? predicate,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        out ParsedWhere parsed,
        out string? error,
        Func<int, string>? placeholder = null,
        SqlDialect? dialect = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(quote);

        dialect ??= SqlDialect.PostgreSql;

        if (placeholder is not null)
        {
            dialect = dialect.WithPlaceholder(placeholder);
        }

        parsed = default;
        error = null;

        if (predicate is null)
        {
            parsed = new ParsedWhere(string.Empty, []);
            return true;
        }

        StringBuilder sql = new();
        List<object?> parameters = [];

        if (!Write(predicate, sql, parameters, columns, quote, dialect, 0, out error))
        {
            return false;
        }

        parsed = new ParsedWhere(sql.ToString(), parameters, predicate);
        return true;
    }

    private static bool Write(
        AttributePredicate node,
        StringBuilder sql,
        List<object?> parameters,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        SqlDialect dialect,
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
                return Branch(and.Left, " and ", and.Right, sql, parameters, columns, quote, dialect, depth, out error);

            case AttributePredicate.Disjunction or:
                return Branch(or.Left, " or ", or.Right, sql, parameters, columns, quote, dialect, depth, out error);

            case AttributePredicate.Negation not:
                sql.Append("not (");

                if (!Write(not.Operand, sql, parameters, columns, quote, dialect, depth + 1, out error))
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

                /*
                  <b>Case is folded on both sides, or on neither.</b> Filter Encoding's
                  `matchCase="false"` asks for a comparison that disregards letter case,
                  and `lower(col) = lower(@p)` is the portable way to say it — one that
                  works for every comparison operator rather than only equality, because
                  ordering text case-insensitively is a legitimate thing to ask.

                  <b>And only for text, because only text has case.</b> `lower()` on an
                  integer column is a type error in PostgreSQL, so a caller who sets
                  matchCase on a numeric comparison gets the comparison they would have
                  got anyway rather than a 500. Filter Encoding says matchCase applies to
                  string comparison; a number silently ignoring it is the specification
                  rather than a shortcut.

                  <b>It defeats an index on that column</b>, which is the cost and is the
                  caller's to choose. A functional index on `lower(col)` is a
                  deployment's answer if it matters.
                */
                bool folded = compare.IgnoreCase && compare.Value is string;

                if (folded)
                {
                    sql.Append("lower(").Append(comparand).Append(')');
                }
                else
                {
                    sql.Append(comparand);
                }

                sql.Append(' ').Append(Spelling(compare.Operator)).Append(' ');

                if (folded)
                {
                    sql.Append("lower(").Append(Bind(parameters, dialect, compare.Value)).Append(')');
                }
                else
                {
                    sql.Append(Bind(parameters, dialect, compare.Value));
                }

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

                // <b>`ilike` rather than `lower(col) like lower(@p)`.</b> PostgreSQL has
                // the operator, it is what a reader of the statement expects, and it
                // keeps the pattern's escape character meaning what it meant — folding a
                // pattern through `lower()` is safe today and is one more thing to get
                // right if the escaping ever changes.
                sql.Append(matched).Append(like.Negated
                        ? (like.IgnoreCase ? " not ilike " : " not like ")
                        : (like.IgnoreCase ? " ilike " : " like "))
                   .Append(Bind(parameters, dialect, like.Pattern));

                return true;

            case AttributePredicate.Between between:
                if (!Resolve(between.Column, columns, quote, out string? bounded, out error))
                {
                    return false;
                }

                sql.Append(bounded).Append(between.Negated ? " not between " : " between ")
                   .Append(Bind(parameters, dialect, between.Low)).Append(" and ")
                   .Append(Bind(parameters, dialect, between.High));

                return true;

            case AttributePredicate.OneOf list:
                return WriteIn(list, sql, parameters, columns, quote, dialect, out error);

            case AttributePredicate.ExpressionComparison compared:
                if (!Expression(compared.Left, sql, parameters, columns, quote, dialect, depth + 1, out error))
                {
                    return false;
                }

                sql.Append(' ').Append(Spelling(compared.Operator)).Append(' ');

                return Expression(compared.Right, sql, parameters, columns, quote, dialect, depth + 1, out error);

            case AttributePredicate.ExpressionIsNull isNull:
                if (!Expression(isNull.Operand, sql, parameters, columns, quote, dialect, depth + 1, out error))
                {
                    return false;
                }

                sql.Append(isNull.Negated ? " is not null" : " is null");
                return true;

            case AttributePredicate.ExpressionMatches like:
                if (!Expression(like.Operand, sql, parameters, columns, quote, dialect, depth + 1, out error))
                {
                    return false;
                }

                sql.Append(like.Negated ? " not like " : " like ").Append(Bind(parameters, dialect, like.Pattern));
                return true;

            case AttributePredicate.ExpressionBetween between:
                if (!Expression(between.Operand, sql, parameters, columns, quote, dialect, depth + 1, out error))
                {
                    return false;
                }

                sql.Append(between.Negated ? " not between " : " between ");

                if (!Expression(between.Low, sql, parameters, columns, quote, dialect, depth + 1, out error))
                {
                    return false;
                }

                sql.Append(" and ");

                return Expression(between.High, sql, parameters, columns, quote, dialect, depth + 1, out error);

            case AttributePredicate.ExpressionOneOf list:
                if (list.Values is null || list.Values.Count == 0)
                {
                    error = "An 'in' has no values, and an empty list matches nothing.";
                    return false;
                }

                if (!Expression(list.Operand, sql, parameters, columns, quote, dialect, depth + 1, out error))
                {
                    return false;
                }

                sql.Append(list.Negated ? " not in (" : " in (");

                for (int i = 0; i < list.Values.Count; i++)
                {
                    sql.Append(i == 0 ? string.Empty : ", ").Append(Bind(parameters, dialect, list.Values[i]));
                }

                sql.Append(')');
                return true;

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
        SqlDialect dialect,
        int depth,
        out string? error)
    {
        // A disjunction under a conjunction needs its brackets or the statement
        // means something else; every other pairing is safe flat, because the
        // tree's own shape already carries the precedence.
        bool bracket = keyword == " and ";

        if (!Side(left, bracket, sql, parameters, columns, quote, dialect, depth, out error))
        {
            return false;
        }

        sql.Append(keyword);

        return Side(right, bracket, sql, parameters, columns, quote, dialect, depth, out error);
    }

    private static bool Side(
        AttributePredicate side,
        bool bracketDisjunction,
        StringBuilder sql,
        List<object?> parameters,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        SqlDialect dialect,
        int depth,
        out string? error)
    {
        bool brackets = bracketDisjunction && side is AttributePredicate.Disjunction;

        if (brackets)
        {
            sql.Append('(');
        }

        if (!Write(side, sql, parameters, columns, quote, dialect, depth + 1, out error))
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
        SqlDialect dialect,
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
            placeholders.Add(Bind(parameters, dialect, value));
        }

        sql.Append(column).Append(list.Negated ? " not in (" : " in (")
           .Append(string.Join(", ", placeholders)).Append(')');

        return true;
    }

    /// <summary>Writes a computed value, bracketed wherever it is not a single term.</summary>
    /// <remarks>
    /// <para>
    /// <b>Every arithmetic node in brackets.</b> Precedence is the tree's, so the brackets the caller wrote
    /// are gone and the ones written here are what keeps <c>(a + b) * c</c> meaning what it meant. Writing
    /// them around every node rather than only where needed costs characters and nothing else.
    /// </para>
    /// <para>
    /// <b>An <c>int</c> constant is written into the statement; everything else is bound.</b> The parser
    /// makes an <c>int</c> only for a number a function needs whole — places, a substring's start and
    /// length — because Npgsql binds a <c>long</c> as <c>bigint</c> and PostgreSQL has
    /// <c>round(numeric, integer)</c> and <c>substring(text, integer, integer)</c> with no <c>bigint</c>
    /// overloads. It is digits the parser read and formatted invariantly, never caller text.
    /// </para>
    /// </remarks>
    private static bool Expression(
        ScalarExpression node,
        StringBuilder sql,
        List<object?> parameters,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        SqlDialect dialect,
        int depth,
        out string? error)
    {
        error = null;

        if (depth > MaximumDepth)
        {
            error =
                $"The expression nests more than {MaximumDepth} levels deep. The limit exists because "
                + "emitting is recursive and a deep enough tree would exhaust the stack.";
            return false;
        }

        bool Inner(ScalarExpression inner, out string? why) =>
            Expression(inner, sql, parameters, columns, quote, dialect, depth + 1, out why);

        switch (node)
        {
            case ScalarExpression.ColumnValue column:
                if (!Resolve(column.Column, columns, quote, out string? quoted, out error))
                {
                    return false;
                }

                sql.Append(quoted);
                return true;

            case ScalarExpression.Constant { Value: int whole }:
                sql.Append(whole.ToString(CultureInfo.InvariantCulture));
                return true;

            case ScalarExpression.Constant constant:
                sql.Append(Bind(parameters, dialect, constant.Value));
                return true;

            case ScalarExpression.Negated negated:
                sql.Append("(-");

                if (!Inner(negated.Operand, out error))
                {
                    return false;
                }

                sql.Append(')');
                return true;

            case ScalarExpression.Arithmetic arithmetic:
                sql.Append('(');

                if (!Inner(arithmetic.Left, out error))
                {
                    return false;
                }

                sql.Append(arithmetic.Operator switch
                {
                    ArithmeticOperator.Add => " + ",
                    ArithmeticOperator.Subtract => " - ",
                    ArithmeticOperator.Multiply => " * ",
                    _ => arithmetic.Integral ? $" {dialect.IntegerDivision} " : " / ",
                });

                if (!Inner(arithmetic.Right, out error))
                {
                    return false;
                }

                sql.Append(')');
                return true;

            case ScalarExpression.Cast cast:
                return WriteCast(cast, sql, Inner, out error);

            case ScalarExpression.Extract extract:
                sql.Append("extract(").Append(extract.Part switch
                {
                    DatePart.Year => "year",
                    DatePart.Month => "month",
                    DatePart.Day => "day",
                    DatePart.Hour => "hour",
                    DatePart.Minute => "minute",
                    _ => "second",
                }).Append(" from ");

                if (!Inner(extract.Operand, out error))
                {
                    return false;
                }

                sql.Append(')');
                return true;

            case ScalarExpression.Trim trim:
                sql.Append("trim(").Append(trim.Side switch
                {
                    TrimSide.Leading => "leading",
                    TrimSide.Trailing => "trailing",
                    _ => "both",
                }).Append(" ' ' from ");

                if (!Inner(trim.Operand, out error))
                {
                    return false;
                }

                sql.Append(')');
                return true;

            case ScalarExpression.FunctionCall call:
                return WriteCall(call, sql, dialect, Inner, out error);

            default:
                error = $"'{node.GetType().Name}' is not an expression this emitter knows.";
                return false;
        }
    }

    private delegate bool EmitOne(ScalarExpression node, out string? error);

    private static bool WriteCast(ScalarExpression.Cast cast, StringBuilder sql, EmitOne inner, out string? error)
    {
        // A length is a cut, written the one way both dialects cut alike (SqlDialect's remarks).
        if (cast.Target == CastTarget.Text && cast.Length is { } length)
        {
            sql.Append("substring(cast(");

            if (!inner(cast.Operand, out error))
            {
                return false;
            }

            sql.Append(" as varchar) from 1 for ").Append(length.ToString(CultureInfo.InvariantCulture)).Append(')');
            return true;
        }

        sql.Append("cast(");

        if (!inner(cast.Operand, out error))
        {
            return false;
        }

        sql.Append(" as ").Append(cast.Target switch
        {
            CastTarget.Integer => "integer",
            CastTarget.SmallInteger => "smallint",
            CastTarget.BigInteger => "bigint",

            // `float` is eight bytes in PostgreSQL and four in DuckDB; ArcGIS means eight by FLOAT.
            CastTarget.Double => "double precision",
            CastTarget.Real => "real",
            CastTarget.Text => "varchar",
            _ => "date",
        }).Append(')');

        return true;
    }

    private static bool WriteCall(
        ScalarExpression.FunctionCall call, StringBuilder sql, SqlDialect dialect, EmitOne inner, out string? error)
    {
        IReadOnlyList<ScalarExpression> a = call.Arguments;
        error = null;

        // `round`, `trunc` and `mod` over a number PostgreSQL has for `numeric` only (SqlDialect).
        bool Numeric(ScalarExpression operand, out string? why)
        {
            if (!dialect.NumericForPlaces)
            {
                return inner(operand, out why);
            }

            sql.Append("cast(");

            if (!inner(operand, out why))
            {
                return false;
            }

            sql.Append(" as numeric)");
            return true;
        }

        // `name(first sep second [sep2 third])`, each argument through `emit`.
        bool Write(string name, string separator, string? second, EmitOne first, out string? why)
        {
            sql.Append(name).Append('(');

            if (!first(a[0], out why))
            {
                return false;
            }

            for (int i = 1; i < a.Count; i++)
            {
                sql.Append(i == 1 ? separator : second ?? separator);

                if (!(call.Function == ScalarFunction.Mod ? Numeric(a[i], out why) : inner(a[i], out why)))
                {
                    return false;
                }
            }

            sql.Append(')');
            return true;
        }

        return call.Function switch
        {
            // With a number of places the number is cast; alone, both dialects round every number.
            ScalarFunction.Round => Write("round", ", ", null, a.Count == 2 ? Numeric : inner, out error),
            ScalarFunction.Truncate => Write("trunc", ", ", null, a.Count == 2 ? Numeric : inner, out error),
            ScalarFunction.Mod => Write("mod", ", ", null, Numeric, out error),
            ScalarFunction.Position => Write("position", " in ", null, inner, out error),
            ScalarFunction.Substring => Write("substring", " from ", " for ", inner, out error),
            _ => Write(
                call.Function switch
                {
                    ScalarFunction.Abs => "abs",
                    ScalarFunction.Ceiling => "ceiling",
                    ScalarFunction.Floor => "floor",
                    ScalarFunction.Cos => "cos",
                    ScalarFunction.Sin => "sin",
                    ScalarFunction.Tan => "tan",

                    // ArcGIS's LOG is the natural logarithm; SQL's `log` is base ten in both dialects.
                    ScalarFunction.Log => "ln",
                    ScalarFunction.Log10 => "log10",
                    ScalarFunction.Power => "power",
                    ScalarFunction.NullIf => "nullif",
                    ScalarFunction.Coalesce => "coalesce",
                    ScalarFunction.CharLength => "char_length",
                    ScalarFunction.Concat => "concat",
                    ScalarFunction.Upper => "upper",
                    _ => "lower",
                },
                ", ",
                null,
                inner,
                out error),
        };
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

    private static string Bind(List<object?> parameters, SqlDialect dialect, object? value)
    {
        parameters.Add(value);
        return dialect.Placeholder(parameters.Count - 1);
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
