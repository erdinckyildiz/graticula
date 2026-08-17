using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Graticula.Features;

/// <summary>
/// A parsed <c>where</c> clause: the caller's predicate, rebuilt as ours.
/// </summary>
/// <param name="Sql">Parameterised SQL, safe to interpolate into a statement.</param>
/// <param name="Parameters">The values its placeholders bind to, in order.</param>
public readonly record struct ParsedWhere(string Sql, IReadOnlyList<object?> Parameters);

/// <summary>
/// Parses the subset of SQL-92 that ArcGIS's <c>where</c> parameter uses.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because the alternative is handing user text to the
/// database.</b> ArcGIS defines <c>where</c> as a SQL-92 predicate, and the
/// obvious implementation — paste it after <c>where</c> — is remote code
/// execution on the datastore. <c>1=1; drop table parcels --</c> is not a clever
/// bypass; it is the first thing anybody tries. Until now this server refused
/// every clause except the literal <c>1=1</c>, which was safe and made the most
/// used feature of the query API unusable.
/// </para>
/// <para>
/// <b>Nothing the caller writes reaches SQL as text.</b> The parser produces an
/// expression tree and the emitter rebuilds the statement from it: identifiers
/// are matched against the layer's real columns and re-quoted by us, operators
/// come from a fixed table, and every literal becomes a bound parameter. A
/// string that fails to parse is refused with a position, not passed through.
/// That is ADR-008 §4.6's two-step applied to the one input that is a whole
/// language.
/// </para>
/// <para>
/// <b>What it does not accept, and will not by accident:</b> function calls,
/// subqueries, <c>;</c>, comments, <c>CASE</c>, arithmetic. Each is absent
/// because the grammar has no rule for it, so adding one is a deliberate act
/// rather than an oversight. Arithmetic and functions are the two most likely
/// future requests and both are safe to add — they are shapes, not holes — but
/// they are not here today.
/// </para>
/// <para>
/// <b>Case folding follows SQL, not C#.</b> Keywords are recognised
/// case-insensitively; string literals are compared by the database with the
/// column's own collation, which is the behaviour a SQL user expects and not
/// something this parser should second-guess.
/// </para>
/// </remarks>
public static class WhereClause
{
    /// <summary>
    /// How deep the parentheses may nest.
    /// </summary>
    /// <remarks>
    /// A recursive-descent parser recurses once per bracket, so an input of ten
    /// thousand open parentheses is a stack overflow — which in .NET cannot be
    /// caught and takes the process down. That is a denial of service reachable
    /// from an unauthenticated query string, so the limit is a cap rather than a
    /// nicety. Thirty-two is far past anything a person writes.
    /// </remarks>
    public const int MaximumDepth = 32;

    /// <summary>The longest clause accepted, in characters.</summary>
    /// <remarks>
    /// Long enough for an <c>IN</c> list of a few hundred ids, short enough that
    /// parsing cannot be made expensive on purpose.
    /// </remarks>
    public const int MaximumLength = 8_000;

    /// <summary>Parses a clause against a known set of columns.</summary>
    /// <param name="text">The clause, as the client wrote it.</param>
    /// <param name="columns">Column names the clause may mention.</param>
    /// <param name="quote">How to quote a column name for the target dialect.</param>
    /// <param name="parsed">The rebuilt SQL and its parameters.</param>
    /// <param name="error">Why it was refused, with a position.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(
        string? text,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        out ParsedWhere parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(quote);

        parsed = default;
        error = null;

        string clause = (text ?? string.Empty).Trim();

        if (clause.Length == 0)
        {
            parsed = new ParsedWhere(string.Empty, []);
            return true;
        }

        if (clause.Length > MaximumLength)
        {
            error =
                $"The where clause is {clause.Length} characters and the limit is "
                + $"{MaximumLength}. A predicate that long is usually a generated IN list; pass "
                + "the ids in 'objectIds' instead, which is bounded and indexed.";
            return false;
        }

        Parser parser = new(clause, columns, quote);

        if (!parser.TryParse(out parsed, out error))
        {
            return false;
        }

        return true;
    }

    /// <summary>Recursive descent over the grammar above.</summary>
    private sealed class Parser(
        string text, IReadOnlyCollection<string> columns, Func<string, string> quote)
    {
        private readonly List<object?> _parameters = [];
        private int _at;
        private int _depth;

        public bool TryParse(out ParsedWhere parsed, out string? error)
        {
            parsed = default;

            StringBuilder sql = new();

            if (!Or(sql, out error))
            {
                return false;
            }

            SkipSpace();

            if (_at < text.Length)
            {
                error = Unexpected("end of the clause");
                return false;
            }

            parsed = new ParsedWhere(sql.ToString(), _parameters);
            return true;
        }

        // or := and ( OR and )*
        private bool Or(StringBuilder sql, out string? error)
        {
            if (!And(sql, out error))
            {
                return false;
            }

            while (Keyword("or"))
            {
                sql.Append(" or ");

                if (!And(sql, out error))
                {
                    return false;
                }
            }

            return true;
        }

        // and := unary ( AND unary )*
        private bool And(StringBuilder sql, out string? error)
        {
            if (!Unary(sql, out error))
            {
                return false;
            }

            while (Keyword("and"))
            {
                sql.Append(" and ");

                if (!Unary(sql, out error))
                {
                    return false;
                }
            }

            return true;
        }

        // unary := NOT unary | '(' or ')' | predicate
        private bool Unary(StringBuilder sql, out string? error)
        {
            error = null;
            SkipSpace();

            if (Keyword("not"))
            {
                sql.Append("not ");
                return Unary(sql, out error);
            }

            if (Peek() == '(')
            {
                if (++_depth > MaximumDepth)
                {
                    error =
                        $"The where clause nests more than {MaximumDepth} parentheses deep. The "
                        + "limit exists because parsing is recursive and a deep enough input would "
                        + "exhaust the stack, which cannot be caught.";
                    return false;
                }

                _at++;
                sql.Append('(');

                if (!Or(sql, out error))
                {
                    return false;
                }

                SkipSpace();

                if (Peek() != ')')
                {
                    error = Unexpected("')'");
                    return false;
                }

                _at++;
                _depth--;
                sql.Append(')');
                return true;
            }

            return Predicate(sql, out error);
        }

        // predicate := column ( IS [NOT] NULL | [NOT] IN (list) | [NOT] LIKE lit
        //                     | [NOT] BETWEEN lit AND lit | op lit )
        private bool Predicate(StringBuilder sql, out string? error)
        {
            if (!Column(out string? column, out error))
            {
                return false;
            }

            string quoted = quote(column!);

            SkipSpace();

            if (Keyword("is"))
            {
                bool negated = Keyword("not");

                if (!Keyword("null"))
                {
                    error = Unexpected("'null' after 'is'");
                    return false;
                }

                sql.Append(quoted).Append(negated ? " is not null" : " is null");
                return true;
            }

            bool not = Keyword("not");

            if (Keyword("in"))
            {
                return In(sql, quoted, not, out error);
            }

            if (Keyword("like"))
            {
                if (!Literal(out object? pattern, out error))
                {
                    return false;
                }

                if (pattern is not string)
                {
                    error = "'like' compares text, so its right-hand side must be a quoted string.";
                    return false;
                }

                sql.Append(quoted).Append(not ? " not like " : " like ").Append(Bind(pattern));
                return true;
            }

            if (Keyword("between"))
            {
                if (!Literal(out object? low, out error))
                {
                    return false;
                }

                if (!Keyword("and"))
                {
                    error = Unexpected("'and' in a 'between'");
                    return false;
                }

                if (!Literal(out object? high, out error))
                {
                    return false;
                }

                sql.Append(quoted).Append(not ? " not between " : " between ")
                   .Append(Bind(low)).Append(" and ").Append(Bind(high));

                return true;
            }

            if (not)
            {
                error = Unexpected("'in', 'like' or 'between' after 'not'");
                return false;
            }

            if (!Operator(out string? op, out error))
            {
                return false;
            }

            if (!Literal(out object? value, out error))
            {
                return false;
            }

            sql.Append(quoted).Append(' ').Append(op).Append(' ').Append(Bind(value));
            return true;
        }

        private bool In(StringBuilder sql, string quoted, bool not, out string? error)
        {
            SkipSpace();

            if (Peek() != '(')
            {
                error = Unexpected("'(' after 'in'");
                return false;
            }

            _at++;

            List<string> placeholders = [];

            while (true)
            {
                if (!Literal(out object? value, out error))
                {
                    return false;
                }

                placeholders.Add(Bind(value));

                SkipSpace();

                if (Peek() == ',')
                {
                    _at++;
                    continue;
                }

                break;
            }

            if (Peek() != ')')
            {
                error = Unexpected("',' or ')' in an 'in' list");
                return false;
            }

            _at++;

            sql.Append(quoted).Append(not ? " not in (" : " in (")
               .Append(string.Join(", ", placeholders)).Append(')');

            error = null;
            return true;
        }

        /// <summary>
        /// A column name, matched against the layer's own columns.
        /// </summary>
        /// <remarks>
        /// <b>Matched, not validated by pattern.</b> A name that passes an
        /// identifier regex and does not exist is still a name the caller
        /// invented; comparing against the real list means an unknown column is
        /// a clear error rather than a database one, and means no string the
        /// caller wrote is ever quoted and emitted — the emitted text is the
        /// column name we already had.
        /// </remarks>
        private bool Column(out string? column, out string? error)
        {
            column = null;
            error = null;

            SkipSpace();

            int start = _at;

            if (Peek() == '"')
            {
                _at++;
                int from = _at;

                while (_at < text.Length && text[_at] != '"')
                {
                    _at++;
                }

                if (_at >= text.Length)
                {
                    error = "A quoted column name is not closed.";
                    return false;
                }

                column = text[from.._at];
                _at++;
            }
            else
            {
                while (_at < text.Length && (char.IsLetterOrDigit(text[_at]) || text[_at] == '_'))
                {
                    _at++;
                }

                if (_at == start)
                {
                    error = Unexpected("a column name");
                    return false;
                }

                column = text[start.._at];
            }

            foreach (string known in columns)
            {
                if (string.Equals(known, column, StringComparison.OrdinalIgnoreCase))
                {
                    column = known;
                    return true;
                }
            }

            error =
                $"'{column}' is not a field of this layer. The where clause may only mention "
                + "fields the layer document lists.";

            column = null;
            return false;
        }

        private bool Operator(out string? op, out string? error)
        {
            error = null;
            op = null;

            SkipSpace();

            foreach (string candidate in (string[])["<>", "!=", "<=", ">=", "=", "<", ">"])
            {
                if (text.AsSpan(_at).StartsWith(candidate, StringComparison.Ordinal))
                {
                    _at += candidate.Length;

                    // != is accepted because clients send it; <> is the SQL-92
                    // spelling and the one emitted, so the database sees one form.
                    op = candidate == "!=" ? "<>" : candidate;
                    return true;
                }
            }

            error = Unexpected("a comparison operator");
            return false;
        }

        private bool Literal(out object? value, out string? error)
        {
            error = null;
            value = null;

            SkipSpace();

            if (_at >= text.Length)
            {
                error = Unexpected("a value");
                return false;
            }

            char c = text[_at];

            if (c == '\'')
            {
                _at++;
                StringBuilder literal = new();

                while (_at < text.Length)
                {
                    if (text[_at] == '\'')
                    {
                        // '' inside a string is one quote — SQL's own escape, and
                        // the reason a naive split on quotes gets this wrong.
                        if (_at + 1 < text.Length && text[_at + 1] == '\'')
                        {
                            literal.Append('\'');
                            _at += 2;
                            continue;
                        }

                        _at++;
                        value = literal.ToString();
                        return true;
                    }

                    literal.Append(text[_at]);
                    _at++;
                }

                error = "A string literal is not closed.";
                return false;
            }

            if (Keyword("null"))
            {
                value = null;
                return true;
            }

            if (Keyword("true"))
            {
                value = true;
                return true;
            }

            if (Keyword("false"))
            {
                value = false;
                return true;
            }

            int start = _at;

            if (c is '-' or '+')
            {
                _at++;
            }

            while (_at < text.Length && (char.IsDigit(text[_at]) || text[_at] == '.'))
            {
                _at++;
            }

            string number = text[start.._at];

            if (number.Length == 0 || number is "-" or "+")
            {
                error = Unexpected("a value");
                return false;
            }

            if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
            {
                value = i;
                return true;
            }

            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                value = d;
                return true;
            }

            error = $"'{number}' is not a number this parser understands.";
            return false;
        }

        /// <summary>Adds a value and returns its placeholder.</summary>
        private string Bind(object? value)
        {
            _parameters.Add(value);
            return $"@w{(_parameters.Count - 1).ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>Consumes a keyword if it is next, whole-word.</summary>
        /// <remarks>
        /// <b>Whole-word, or <c>android</c> starts with <c>and</c>.</b> A column
        /// called <c>notes</c> would otherwise be read as <c>not</c> followed by
        /// <c>es</c>, and the failure would be a parse error on a valid clause.
        /// </remarks>
        private bool Keyword(string word)
        {
            SkipSpace();

            if (_at + word.Length > text.Length)
            {
                return false;
            }

            if (!text.AsSpan(_at, word.Length).Equals(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int after = _at + word.Length;

            if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
            {
                return false;
            }

            _at = after;
            return true;
        }

        private void SkipSpace()
        {
            while (_at < text.Length && char.IsWhiteSpace(text[_at]))
            {
                _at++;
            }
        }

        private char Peek()
        {
            SkipSpace();
            return _at < text.Length ? text[_at] : '\0';
        }

        private string Unexpected(string wanted)
        {
            string rest = _at < text.Length
                ? text[_at..Math.Min(text.Length, _at + 24)]
                : "the end of the clause";

            return $"Expected {wanted} at position {_at}, and found \"{rest}\".";
        }
    }
}
