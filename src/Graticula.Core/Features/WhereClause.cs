using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Graticula.Features;

/// <summary>
/// A parsed <c>where</c> clause: the caller's predicate, rebuilt as ours.
/// </summary>
/// <param name="Sql">Parameterised SQL, safe to interpolate into a statement.</param>
/// <param name="Parameters">The values its placeholders bind to, in order.</param>
/// <param name="Predicate">
/// The tree <paramref name="Sql"/> was emitted from, or <see langword="null"/> when there is no
/// predicate. A datastore that does not speak PostgreSQL's dialect emits its own statement from
/// this rather than rewriting that one — [D-162](../../../docs/architecture-debt.md), ADR-066 §4.
/// </param>
public readonly record struct ParsedWhere(
    string Sql,
    IReadOnlyList<object?> Parameters,
    AttributePredicate? Predicate = null);

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
/// <see cref="AttributePredicate"/> and <see cref="PredicateSql"/> rebuilds the
/// statement from it: identifiers are matched against the layer's real columns
/// and re-quoted by us, operators come from a fixed table, and every literal
/// becomes a bound parameter. A string that fails to parse is refused with a
/// position, not passed through. That is ADR-008 §4.6's two-step applied to the
/// one input that is a whole language.
/// </para>
/// <para>
/// <b>The paragraph above described the design for four days before the code
/// had it</b> — the parser appended SQL as it descended, and there was no tree
/// ([D-117](../../../docs/architecture-debt.md)). The security properties were
/// all present, so nothing was wrong; what was missing was the seam. It was
/// built when a second front end arrived rather than in advance, which is §82's
/// rule, and the cost of the delay was one stale comment rather than a defect.
/// See [ADR-039](../../../docs/adr/ADR-039-wfs-is-the-first-surface-after-v1.md) §5.
/// </para>
/// <para>
/// <b>What it does not accept, and will not by accident:</b> subqueries, <c>;</c>,
/// comments, <c>CASE</c>, and every function outside ArcGIS's standardized query list. Each is
/// absent because the grammar has no rule for it, so adding one is a deliberate act rather than an
/// oversight.
/// </para>
/// <para>
/// <b>The standardized list arrived on 2026-09-23, by the owner's decision (V-75,
/// [ADR-083](../../../docs/adr/ADR-083-the-where-clause-evaluates-arcgis-standard-functions.md))</b> —
/// <c>CAST</c>, <c>EXTRACT</c>, the string and numeric functions and <c>+ - * /</c>. They build
/// <see cref="ScalarExpression"/> nodes, which <see cref="PredicateSql"/> spells per dialect; a
/// function never reaches SQL as the caller's text. What a column holds is checked where the caller
/// gave the types, so <c>SUBSTRING(population, 1, 2)</c> is refused naming the field rather than
/// answered with a database's error.
/// </para>
/// <para>
/// <b>What was added on 2026-09-15, because ArcGIS clients write it</b> — found by a review
/// from an ArcGIS user's side: <c>UPPER(field)</c> and <c>LOWER(field)</c> on the left of
/// <c>=</c>, <c>&lt;&gt;</c>, <c>LIKE</c> and <c>IN</c>, which is how the Maps SDK's search
/// asks for a case-insensitive match; <c>DATE '…'</c> and <c>TIMESTAMP '…'</c> literals and
/// <c>CURRENT_DATE</c> / <c>CURRENT_TIMESTAMP</c>, optionally minus or plus a number of days
/// or an <c>INTERVAL 'n' DAY|HOUR|MINUTE|SECOND</c>, which is how Dashboards and Experience
/// Builder write *the last 30 days*; and a number compared with a date field, read as epoch
/// milliseconds, which is what an ArcGIS date is. <b>None of them reaches SQL as a
/// function.</b> A case function becomes the model's <c>IgnoreCase</c>, with a literal that
/// the function could never equal answered as nothing; a date expression is evaluated here,
/// once, into a bound value. Every emitter already speaks both, so no dialect learned a
/// function to make this work.
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
    /// <param name="types">
    /// What each column holds, where the caller knows. Optional, and only dates need it.
    /// </param>
    /// <param name="clock">
    /// What <c>CURRENT_DATE</c> and <c>CURRENT_TIMESTAMP</c> mean for this parse; the system clock
    /// when not given.
    /// </param>
    /// <returns>Whether it parsed.</returns>
    /// <remarks>
    /// <b><c>types</c> exists so that a date literal can be bound as a date, which is
    /// [Q-124](../../../docs/open-questions.md).</b> The grammar has no date literal and
    /// does not need one: a quoted string is how every SQL-92 dialect writes a timestamp.
    /// What was missing was the knowledge that the column on the other side of the
    /// comparison holds one, so <c>observed_at = '2026-08-02'</c> reached PostgreSQL as
    /// <c>timestamp with time zone = text</c> and came back as *operator does not exist* —
    /// a 400 telling an operator nothing, on the filter a time series most wants.
    /// <b>Optional rather than required, because a caller that does not know the types
    /// gets exactly the behaviour it had</b>, and every caller that does know them is
    /// holding a <c>LayerDescription</c> already.
    /// </remarks>
    public static bool TryParse(
        string? text,
        IReadOnlyCollection<string> columns,
        Func<string, string> quote,
        out ParsedWhere parsed,
        out string? error,
        IReadOnlyDictionary<string, FieldType>? types = null,
        TimeProvider? clock = null)
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

        Parser parser = new(clause, columns, types, (clock ?? TimeProvider.System).GetUtcNow());

        if (!parser.TryParse(out AttributePredicate? predicate, out error))
        {
            return false;
        }

        return PredicateSql.TryEmit(predicate, columns, quote, out parsed, out error);
    }

    /// <summary>Recursive descent over the grammar above.</summary>
    private sealed class Parser(
        string text,
        IReadOnlyCollection<string> columns,
        IReadOnlyDictionary<string, FieldType>? types,
        DateTimeOffset now)
    {
        /// <summary>A case function applied to the column on the left.</summary>
        private enum Fold
        {
            None,
            Upper,
            Lower,
        }
        /// <summary>
        /// A literal, given the type of the column it is being compared with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Only dates, and only when the caller said which columns are dates.</b> A
        /// number is already a number by the time it gets here and text is already text;
        /// a date arrives as a quoted string that PostgreSQL will not compare with a
        /// timestamp, and nothing else in the grammar has that problem.
        /// </para>
        /// <para>
        /// <b>UTC when the literal does not say, matching the other two surfaces.</b>
        /// `AssumeUniversal | AdjustToUniversal` is what OGC API Features and the WFS
        /// filter reader use, so `'2026-08-02'` means the same moment on all three. A
        /// server whose answers move when its time zone is reconfigured is worse than one
        /// that is arguably an hour off.
        /// </para>
        /// </remarks>
        private bool Typed(string column, object? value, out object? bound, out string? error)
        {
            bound = value;
            error = null;

            bool knownDate = types is not null
                && types.TryGetValue(column, out FieldType known)
                && known == FieldType.Date;

            // <b>A date expression is for a date field.</b> `DATE '…'` against a text or number
            // column would reach the database as a type mismatch and come back as a database's
            // sentence; saying so here names the field.
            if (value is DateTimeOffset && types is not null && types.ContainsKey(column) && !knownDate)
            {
                error = $"'{column}' does not hold a date, so it cannot be compared with a date.";
                return false;
            }

            // <b>An ArcGIS date is epoch milliseconds, and clients compare with one.</b> Sent as
            // a number against a timestamp column it reached PostgreSQL as `timestamp >= bigint`
            // and was answered with the database's own message.
            if (knownDate && value is long milliseconds)
            {
                if (milliseconds < DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
                    || milliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
                {
                    error = $"'{column}' holds a date and {milliseconds} is outside any date as epoch milliseconds.";
                    return false;
                }

                bound = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
                return true;
            }

            if (value is not string text || !knownDate)
            {
                return true;
            }

            if (!DateTimeOffset.TryParse(
                    text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset moment))
            {
                error =
                    $"'{column}' holds a date and '{text}' is not one. Write it as "
                    + "'2026-08-02' or '2026-08-02T14:30:00Z'.";

                return false;
            }

            bound = moment;
            return true;
        }

        /// <summary>The fixed table the grammar's operators come from.</summary>
        private static readonly (string Spelling, ComparisonOperator Meaning)[] Operators =
        [
            ("<>", ComparisonOperator.NotEqual),
            ("!=", ComparisonOperator.NotEqual),
            ("<=", ComparisonOperator.LessThanOrEqual),
            (">=", ComparisonOperator.GreaterThanOrEqual),
            ("=", ComparisonOperator.Equal),
            ("<", ComparisonOperator.LessThan),
            (">", ComparisonOperator.GreaterThan),
        ];

        private int _at;
        private int _depth;

        public bool TryParse(out AttributePredicate? predicate, out string? error)
        {
            if (!Or(out predicate, out error))
            {
                return false;
            }

            SkipSpace();

            if (_at < text.Length)
            {
                error = Unexpected("end of the clause");
                predicate = null;
                return false;
            }

            return true;
        }

        // or := and ( OR and )*
        private bool Or(out AttributePredicate? node, out string? error)
        {
            if (!And(out node, out error))
            {
                return false;
            }

            while (Keyword("or"))
            {
                if (!And(out AttributePredicate? right, out error))
                {
                    node = null;
                    return false;
                }

                node = new AttributePredicate.Disjunction(node!, right!);
            }

            return true;
        }

        // and := unary ( AND unary )*
        private bool And(out AttributePredicate? node, out string? error)
        {
            if (!Unary(out node, out error))
            {
                return false;
            }

            while (Keyword("and"))
            {
                if (!Unary(out AttributePredicate? right, out error))
                {
                    node = null;
                    return false;
                }

                node = new AttributePredicate.Conjunction(node!, right!);
            }

            return true;
        }

        // unary := NOT unary | '(' or ')' | predicate
        private bool Unary(out AttributePredicate? node, out string? error)
        {
            error = null;
            node = null;
            SkipSpace();

            if (Keyword("not"))
            {
                if (!Unary(out AttributePredicate? operand, out error))
                {
                    return false;
                }

                node = new AttributePredicate.Negation(operand!);
                return true;
            }

            if (Peek() == '(')
            {
                // <b>A bracket opens a group of predicates or a value, and only reading on says
                // which</b> — `(a = 1 or b = 2)` against `(population + 1) > 3`, since ADR-083 gave
                // the grammar arithmetic. The group is tried first, because it is what every clause
                // before ADR-083 meant; when it does not parse, the same text is read again as a
                // predicate whose left side starts with a bracket. Of two failures, the one that got
                // further through the clause is the one reported, because it is the one that
                // understood more of what was written.
                int start = _at;
                int depth = _depth;

                if (Group(out node, out error))
                {
                    return true;
                }

                int groupFailedAt = _at;
                string? groupError = error;

                _at = start;
                _depth = depth;

                if (Predicate(out node, out error))
                {
                    return true;
                }

                if (groupFailedAt > _at)
                {
                    error = groupError;
                }

                return false;
            }

            return Predicate(out node, out error);
        }

        // group := '(' or ')'
        private bool Group(out AttributePredicate? node, out string? error)
        {
            node = null;

            if (!Enter(out error))
            {
                return false;
            }

            if (!Or(out node, out error))
            {
                return false;
            }

            SkipSpace();

            if (Peek() != ')')
            {
                error = Unexpected("')'");
                node = null;
                return false;
            }

            _at++;
            _depth--;

            // The brackets are not kept. They said which tree to build, and
            // the tree now says it; PredicateSql puts back the ones SQL
            // precedence needs, which is a different and smaller set.
            return true;
        }

        // predicate := expression ( IS [NOT] NULL | [NOT] IN (list) | [NOT] LIKE lit
        //                         | [NOT] BETWEEN expression AND expression | op expression )
        //
        // <b>A bare column on the left keeps every rule it had before ADR-083.</b> Its literals are
        // typed against the column (dates, epoch milliseconds), `UPPER(column)` is still the model's
        // case-insensitive comparison, and a literal on the right still builds a `Comparison` — so a
        // clause that parsed before this grammar grew functions builds the same tree it built then.
        private bool Predicate(out AttributePredicate? node, out string? error)
        {
            node = null;
            int start = _at;

            Fold fold = CaseFunction();

            if (fold != Fold.None)
            {
                if (TryColumnName(out string? foldedColumn) && Peek() == ')')
                {
                    _at++;
                    return Folded(foldedColumn!, fold, out node, out error);
                }

                // `UPPER(TRIM(name))` and the like: a function over more than a column, which the
                // expression path answers.
                _at = start;
            }

            if (!Expression(out ScalarExpression? left, out error))
            {
                return false;
            }

            if (left is not ScalarExpression.ColumnValue { Column: var column })
            {
                return Computed(left!, out node, out error);
            }

            SkipSpace();

            if (Keyword("is"))
            {
                bool negated = Keyword("not");

                if (!Keyword("null"))
                {
                    error = Unexpected("'null' after 'is'");
                    return false;
                }

                node = new AttributePredicate.IsNull(column, negated);
                return true;
            }

            bool not = Keyword("not");

            if (Keyword("in"))
            {
                return In(column, not, out node, out error);
            }

            if (Keyword("like"))
            {
                if (!Literal(out object? pattern, out error))
                {
                    return false;
                }

                if (pattern is not string text)
                {
                    error = "'like' compares text, so its right-hand side must be a quoted string.";
                    return false;
                }

                node = new AttributePredicate.Matches(column, text, not);
                return true;
            }

            if (Keyword("between"))
            {
                if (!Expression(out ScalarExpression? low, out error))
                {
                    return false;
                }

                if (!Keyword("and"))
                {
                    error = Unexpected("'and' in a 'between'");
                    return false;
                }

                if (!Expression(out ScalarExpression? high, out error))
                {
                    return false;
                }

                if (low is ScalarExpression.Constant lowConstant && high is ScalarExpression.Constant highConstant)
                {
                    if (!Typed(column, lowConstant.Value, out object? lowBound, out error)
                        || !Typed(column, highConstant.Value, out object? highBound, out error))
                    {
                        return false;
                    }

                    node = new AttributePredicate.Between(column, lowBound, highBound, not);
                    return true;
                }

                return Bounded(left, low!, high!, not, out node, out error);
            }

            if (not)
            {
                error = Unexpected("'in', 'like' or 'between' after 'not'");
                return false;
            }

            if (!Operator(out ComparisonOperator op, out error))
            {
                return false;
            }

            if (!Expression(out ScalarExpression? right, out error))
            {
                return false;
            }

            if (right is ScalarExpression.Constant constant)
            {
                if (!Typed(column, constant.Value, out object? bound, out error))
                {
                    return false;
                }

                node = new AttributePredicate.Comparison(column, op, bound);
                return true;
            }

            return Compared(left, op, right!, out node, out error);
        }

        /// <summary>
        /// Refuses a test with no field anywhere in it — the one shape ADR-083 did not open.
        /// </summary>
        /// <remarks>
        /// <b>The grammar has never had a rule for comparing two literals, and functions did not change why.</b>
        /// <c>'a'='a'</c> and <c>1&lt;2</c> are true or false for every row, which is what a tautology probe is
        /// for; <c>1=1</c> alone is answered as every row before the grammar sees it
        /// (<c>FeatureServerQueryParameters.Reduce</c>). A function or arithmetic means something only over a
        /// field, so requiring one costs no clause anybody writes — and keeps V-65's
        /// <c>… OR kod='TR-34' AND 1=0</c> refused with a sentence rather than answered.
        /// </remarks>
        private static bool NamesAField(out string? error, params ScalarExpression[] sides)
        {
            error = null;

            if (sides.Any(Mentions))
            {
                return true;
            }

            string Written(ScalarExpression side) => side is ScalarExpression.Constant constant
                ? Convert.ToString(constant.Value, CultureInfo.InvariantCulture) ?? "null"
                : "an expression";

            error = $"'{Written(sides[0])}' is tested against '{Written(sides[^1])}' and neither names a field of this "
                + "layer, so the answer is the same for every row. The grammar has no rule for that: it is the shape an "
                + "injection probe takes. Write 1=1 alone for every row.";
            return false;
        }

        private static bool Mentions(ScalarExpression expression) => expression switch
        {
            ScalarExpression.ColumnValue => true,
            ScalarExpression.Constant => false,
            ScalarExpression.Arithmetic arithmetic => Mentions(arithmetic.Left) || Mentions(arithmetic.Right),
            ScalarExpression.Negated negated => Mentions(negated.Operand),
            ScalarExpression.FunctionCall call => call.Arguments.Any(Mentions),
            ScalarExpression.Cast cast => Mentions(cast.Operand),
            ScalarExpression.Extract extract => Mentions(extract.Operand),
            ScalarExpression.Trim trim => Mentions(trim.Operand),
            _ => false,
        };

        /// <summary>A predicate whose left side is computed rather than a bare column — ADR-083.</summary>
        private bool Computed(ScalarExpression left, out AttributePredicate? node, out string? error)
        {
            node = null;
            SkipSpace();

            bool testsAField = Peek() is not ('=' or '<' or '>' or '!') && !Word().StartsWith("between", StringComparison.Ordinal);

            // IS, LIKE and IN test the left side alone, so it is the one that has to name a field.
            if (testsAField && !NamesAField(out error, left))
            {
                return false;
            }

            if (Keyword("is"))
            {
                bool negated = Keyword("not");

                if (!Keyword("null"))
                {
                    error = Unexpected("'null' after 'is'");
                    return false;
                }

                node = new AttributePredicate.ExpressionIsNull(left, negated);
                error = null;
                return true;
            }

            bool not = Keyword("not");

            if (Keyword("like"))
            {
                if (!Literal(out object? pattern, out error))
                {
                    return false;
                }

                if (pattern is not string text)
                {
                    error = "'like' compares text, so its right-hand side must be a quoted string.";
                    return false;
                }

                if (!Requires(left, "LIKE", out error, ExpressionKind.Text))
                {
                    return false;
                }

                node = new AttributePredicate.ExpressionMatches(left, text, not);
                return true;
            }

            if (Keyword("in"))
            {
                SkipSpace();

                if (Peek() != '(')
                {
                    error = Unexpected("'(' after 'in'");
                    return false;
                }

                _at++;

                List<object?> values = [];

                while (true)
                {
                    if (!Literal(out object? value, out error))
                    {
                        return false;
                    }

                    if (!ForKind(left.Kind, new ScalarExpression.Constant(value), out ScalarExpression typed, out error))
                    {
                        return false;
                    }

                    values.Add(((ScalarExpression.Constant)typed).Value);

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
                node = new AttributePredicate.ExpressionOneOf(left, values, not);
                return true;
            }

            if (Keyword("between"))
            {
                if (!Expression(out ScalarExpression? low, out error))
                {
                    return false;
                }

                if (!Keyword("and"))
                {
                    error = Unexpected("'and' in a 'between'");
                    return false;
                }

                if (!Expression(out ScalarExpression? high, out error))
                {
                    return false;
                }

                return Bounded(left, low!, high!, not, out node, out error);
            }

            if (not)
            {
                error = Unexpected("'in', 'like' or 'between' after 'not'");
                return false;
            }

            if (!Operator(out ComparisonOperator op, out error))
            {
                return false;
            }

            if (!Expression(out ScalarExpression? right, out error))
            {
                return false;
            }

            return Compared(left, op, right!, out node, out error);
        }

        private static bool Compared(
            ScalarExpression left, ComparisonOperator op, ScalarExpression right, out AttributePredicate? node, out string? error)
        {
            node = null;

            if (!NamesAField(out error, left, right)
                || !ForKind(left.Kind, right, out right, out error)
                || !ForKind(right.Kind, left, out left, out error))
            {
                return false;
            }

            if (!Comparable(left.Kind, right.Kind))
            {
                error = $"This compares {Name(left.Kind)} with {Name(right.Kind)}, and those cannot be compared.";
                return false;
            }

            node = new AttributePredicate.ExpressionComparison(left, op, right);
            return true;
        }

        private static bool Bounded(
            ScalarExpression operand,
            ScalarExpression low,
            ScalarExpression high,
            bool not,
            out AttributePredicate? node,
            out string? error)
        {
            node = null;

            if (!NamesAField(out error, operand, low, high)
                || !ForKind(operand.Kind, low, out low, out error)
                || !ForKind(operand.Kind, high, out high, out error))
            {
                return false;
            }

            if (!Comparable(operand.Kind, low.Kind) || !Comparable(operand.Kind, high.Kind))
            {
                error = $"A 'between' on {Name(operand.Kind)} needs bounds of the same kind.";
                return false;
            }

            node = new AttributePredicate.ExpressionBetween(operand, low, high, not);
            return true;
        }

        /// <summary>
        /// A constant given the kind of the value it meets — a date from a string or from epoch
        /// milliseconds, as <see cref="Typed"/> does for a column.
        /// </summary>
        private static bool ForKind(
            ExpressionKind kind, ScalarExpression value, out ScalarExpression typed, out string? error)
        {
            typed = value;
            error = null;

            if (kind != ExpressionKind.Date || value is not ScalarExpression.Constant constant)
            {
                return true;
            }

            switch (constant.Value)
            {
                case string text when TryMoment(text, out DateTimeOffset moment):
                    typed = new ScalarExpression.Constant(moment);
                    return true;

                case string text:
                    error = $"'{text}' is compared with a date and is not one. Write it as '2026-08-02' or "
                        + "'2026-08-02 14:30:00'.";
                    return false;

                case long milliseconds
                    when milliseconds >= DateTimeOffset.MinValue.ToUnixTimeMilliseconds()
                        && milliseconds <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds():
                    typed = new ScalarExpression.Constant(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds));
                    return true;

                default:
                    return true;
            }
        }

        private static bool TryMoment(string text, out DateTimeOffset moment) =>
            DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out moment);

        private static bool Numeric(ExpressionKind kind) => kind is ExpressionKind.Integer or ExpressionKind.Real;

        private static bool Comparable(ExpressionKind a, ExpressionKind b) =>
            a == ExpressionKind.Unknown || b == ExpressionKind.Unknown || a == b || (Numeric(a) && Numeric(b));

        private static string Name(ExpressionKind kind) => kind switch
        {
            ExpressionKind.Integer or ExpressionKind.Real => "a number",
            ExpressionKind.Text => "text",
            ExpressionKind.Date => "a date",
            ExpressionKind.Boolean => "true or false",
            _ => "a value",
        };

        /// <summary>Refuses an argument of a kind the function cannot take, naming both.</summary>
        private static bool Requires(
            ScalarExpression argument, string function, out string? error, params ExpressionKind[] allowed)
        {
            error = null;

            if (argument.Kind == ExpressionKind.Unknown || Array.IndexOf(allowed, argument.Kind) >= 0
                || (Array.IndexOf(allowed, ExpressionKind.Real) >= 0 && argument.Kind == ExpressionKind.Integer))
            {
                return true;
            }

            string what = argument is ScalarExpression.ColumnValue column
                ? $"'{column.Column}' holds {Name(argument.Kind)}"
                : $"its argument is {Name(argument.Kind)}";

            error = $"{function} takes {Name(allowed[0])}, and {what}.";
            return false;
        }

        private ExpressionKind KindOf(string column) =>
            types is not null && types.TryGetValue(column, out FieldType type)
                ? type switch
                {
                    FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger => ExpressionKind.Integer,
                    FieldType.Single or FieldType.Double => ExpressionKind.Real,
                    FieldType.Text => ExpressionKind.Text,
                    FieldType.Date => ExpressionKind.Date,
                    FieldType.Boolean => ExpressionKind.Boolean,
                    _ => ExpressionKind.Unknown,
                }
                : ExpressionKind.Unknown;

        /// <summary>A column name, or nothing and the position unmoved.</summary>
        private bool TryColumnName(out string? column)
        {
            int start = _at;

            if (Column(out column, out _))
            {
                return true;
            }

            _at = start;
            column = null;
            return false;
        }

        // expression := term ( ( '+' | '-' ) term )*
        private bool Expression(out ScalarExpression? value, out string? error)
        {
            if (!Term(out value, out error))
            {
                return false;
            }

            while (true)
            {
                char c = Peek();

                if (c is not ('+' or '-'))
                {
                    return true;
                }

                _at++;

                if (!Term(out ScalarExpression? right, out error)
                    || !Arithmetic(c == '+' ? ArithmeticOperator.Add : ArithmeticOperator.Subtract, value!, right!, out value, out error))
                {
                    return false;
                }
            }
        }

        // term := factor ( ( '*' | '/' ) factor )*
        private bool Term(out ScalarExpression? value, out string? error)
        {
            if (!Factor(out value, out error))
            {
                return false;
            }

            while (true)
            {
                char c = Peek();

                if (c is not ('*' or '/'))
                {
                    return true;
                }

                _at++;

                if (!Factor(out ScalarExpression? right, out error)
                    || !Arithmetic(c == '*' ? ArithmeticOperator.Multiply : ArithmeticOperator.Divide, value!, right!, out value, out error))
                {
                    return false;
                }
            }
        }

        private static bool Arithmetic(
            ArithmeticOperator op, ScalarExpression left, ScalarExpression right, out ScalarExpression? value, out string? error)
        {
            value = null;
            error = null;

            foreach (ScalarExpression side in (ScalarExpression[])[left, right])
            {
                if (side.Kind is not (ExpressionKind.Unknown or ExpressionKind.Integer or ExpressionKind.Real))
                {
                    error = side is ScalarExpression.ColumnValue column
                        ? $"'{column.Column}' holds {Name(side.Kind)}, and arithmetic is for numbers."
                        : $"Arithmetic is for numbers, and one side is {Name(side.Kind)}. A date is offset with "
                          + "CURRENT_DATE - 30 or INTERVAL '30' DAY.";
                    return false;
                }
            }

            value = new ScalarExpression.Arithmetic(op, left, right);
            return true;
        }

        // factor := '-' factor | primary — a minus in front of a digit is the number's own sign.
        private bool Factor(out ScalarExpression? value, out string? error)
        {
            SkipSpace();

            if (Peek() == '-' && !(_at + 1 < text.Length && (char.IsDigit(text[_at + 1]) || text[_at + 1] == '.')))
            {
                _at++;

                if (!Factor(out ScalarExpression? operand, out error))
                {
                    value = null;
                    return false;
                }

                if (!Arithmetic(ArithmeticOperator.Subtract, new ScalarExpression.Constant(0L), operand!, out _, out error))
                {
                    value = null;
                    return false;
                }

                value = new ScalarExpression.Negated(operand!);
                return true;
            }

            return Primary(out value, out error);
        }

        // primary := '(' expression ')' | date-expression | literal | function '(' … ')' | column
        private bool Primary(out ScalarExpression? value, out string? error)
        {
            value = null;
            SkipSpace();

            if (_at >= text.Length)
            {
                error = Unexpected("a value");
                return false;
            }

            if (Peek() == '(')
            {
                if (!Enter(out error))
                {
                    return false;
                }

                if (!Expression(out value, out error))
                {
                    return false;
                }

                if (Peek() != ')')
                {
                    error = Unexpected("')'");
                    value = null;
                    return false;
                }

                _at++;
                _depth--;
                return true;
            }

            if (!DateExpression(out object? moment, out error, out bool dated))
            {
                return false;
            }

            if (dated)
            {
                value = new ScalarExpression.Constant(moment);
                return true;
            }

            char c = text[_at];

            if (c is '\'' or '.' or '-' or '+' || char.IsDigit(c) || Word() is "null" or "true" or "false")
            {
                if (!Literal(out object? literal, out error))
                {
                    return false;
                }

                value = new ScalarExpression.Constant(literal);
                return true;
            }

            string word = Word();

            if (word == "current_user")
            {
                error = "CURRENT_USER is not evaluated here: a where clause is parsed without knowing who "
                    + "sent it, so it has nothing to compare with. Compare with the name itself.";
                return false;
            }

            int after = _at + word.Length;

            while (after < text.Length && char.IsWhiteSpace(text[after]))
            {
                after++;
            }

            if (word.Length > 0 && after < text.Length && text[after] == '(')
            {
                _at = after;

                if (!Enter(out error))
                {
                    return false;
                }

                if (!Function(word, out value, out error))
                {
                    return false;
                }

                _depth--;
                return true;
            }

            if (!Column(out string? column, out error))
            {
                return false;
            }

            value = new ScalarExpression.ColumnValue(column!, KindOf(column!));
            return true;
        }

        /// <summary>The identifier at the cursor, lower-cased, without moving; empty when there is none.</summary>
        private string Word()
        {
            SkipSpace();
            int end = _at;

            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }

            return text[_at..end].ToLowerInvariant();
        }

        /// <summary>Consumes an opening bracket, counting it against <see cref="MaximumDepth"/>.</summary>
        private bool Enter(out string? error)
        {
            error = null;

            if (++_depth > MaximumDepth)
            {
                error =
                    $"The where clause nests more than {MaximumDepth} parentheses deep. The limit exists "
                    + "because parsing is recursive and a deep enough input would exhaust the stack, which "
                    + "cannot be caught.";
                return false;
            }

            _at++;
            return true;
        }

        /// <summary>
        /// One function of ArcGIS's standardized query list, its name already read and its bracket
        /// consumed; leaves the closing bracket consumed too.
        /// </summary>
        private bool Function(string name, out ScalarExpression? value, out string? error)
        {
            value = null;

            switch (name)
            {
                case "cast":
                    return CastCall(out value, out error);

                case "extract":
                    return ExtractCall(out value, out error);

                case "trim":
                    return TrimCall(out value, out error);

                case "position":
                    return PositionCall(out value, out error);

                case "substring":
                    return SubstringCall(out value, out error);

                case "round" or "truncate":
                    return PlacesCall(name == "round" ? ScalarFunction.Round : ScalarFunction.Truncate, out value, out error);
            }

            (ScalarFunction function, int least, int most, ExpressionKind argument)? known = name switch
            {
                "abs" => (ScalarFunction.Abs, 1, 1, ExpressionKind.Real),
                "ceiling" => (ScalarFunction.Ceiling, 1, 1, ExpressionKind.Real),
                "floor" => (ScalarFunction.Floor, 1, 1, ExpressionKind.Real),
                "cos" => (ScalarFunction.Cos, 1, 1, ExpressionKind.Real),
                "sin" => (ScalarFunction.Sin, 1, 1, ExpressionKind.Real),
                "tan" => (ScalarFunction.Tan, 1, 1, ExpressionKind.Real),
                "log" => (ScalarFunction.Log, 1, 1, ExpressionKind.Real),
                "log10" => (ScalarFunction.Log10, 1, 1, ExpressionKind.Real),
                "power" => (ScalarFunction.Power, 2, 2, ExpressionKind.Real),
                "mod" => (ScalarFunction.Mod, 2, 2, ExpressionKind.Real),
                "nullif" => (ScalarFunction.NullIf, 2, 2, ExpressionKind.Unknown),
                "coalesce" => (ScalarFunction.Coalesce, 1, 16, ExpressionKind.Unknown),
                "char_length" => (ScalarFunction.CharLength, 1, 1, ExpressionKind.Text),
                "concat" => (ScalarFunction.Concat, 2, 16, ExpressionKind.Unknown),
                "upper" => (ScalarFunction.Upper, 1, 1, ExpressionKind.Text),
                "lower" => (ScalarFunction.Lower, 1, 1, ExpressionKind.Text),
                _ => null,
            };

            if (known is not { } call)
            {
                error = $"'{name.ToUpperInvariant()}' is not a function this server evaluates in a where clause. "
                    + Supported;
                return false;
            }

            List<ScalarExpression> arguments = [];

            while (true)
            {
                if (!Expression(out ScalarExpression? argument, out error))
                {
                    return false;
                }

                if (call.argument != ExpressionKind.Unknown
                    && !Requires(argument!, name.ToUpperInvariant(), out error, call.argument))
                {
                    return false;
                }

                arguments.Add(argument!);

                if (Peek() == ',')
                {
                    _at++;
                    continue;
                }

                break;
            }

            if (!Close(name, out error))
            {
                return false;
            }

            if (arguments.Count < call.least || arguments.Count > call.most)
            {
                error = call.least == call.most
                    ? $"{name.ToUpperInvariant()} takes {call.least} argument{(call.least == 1 ? string.Empty : "s")}, "
                      + $"and was given {arguments.Count}."
                    : $"{name.ToUpperInvariant()} takes {call.least} to {call.most} arguments, and was given {arguments.Count}.";
                return false;
            }

            value = new ScalarExpression.FunctionCall(call.function, arguments);
            return true;
        }

        private bool Close(string name, out string? error)
        {
            error = null;

            if (Peek() != ')')
            {
                error = Unexpected($"')' closing {name.ToUpperInvariant()}");
                return false;
            }

            _at++;
            return true;
        }

        /// <summary>A whole number written in the clause, for places, a start or a length.</summary>
        private bool WholeNumber(string what, out ScalarExpression? value, out string? error)
        {
            value = null;

            // A column or an expression here is refused with what belongs here, not with *expected a value*.
            if (!Literal(out object? literal, out error)
                || literal is not long whole || whole < int.MinValue || whole > int.MaxValue)
            {
                error = $"{what} is a whole number written in the clause.";
                return false;
            }

            value = new ScalarExpression.Constant((int)whole);
            return true;
        }

        // CAST ( expression AS type )
        private bool CastCall(out ScalarExpression? value, out string? error)
        {
            value = null;

            if (!Expression(out ScalarExpression? operand, out error))
            {
                return false;
            }

            if (!Keyword("as"))
            {
                error = Unexpected("AS in CAST");
                return false;
            }

            string type = Word();
            _at += type.Length;

            CastTarget? target = type switch
            {
                "int" or "integer" => CastTarget.Integer,
                "smallint" => CastTarget.SmallInteger,
                "bigint" => CastTarget.BigInteger,
                "float" or "double" => CastTarget.Double,
                "real" => CastTarget.Real,
                "varchar" or "char" or "character" or "text" => CastTarget.Text,
                "date" => CastTarget.Date,
                _ => null,
            };

            if (target is not { } to)
            {
                error = $"CAST converts to INT, SMALLINT, BIGINT, FLOAT, REAL, VARCHAR or DATE, and '{type}' is none of them.";
                return false;
            }

            if (type == "double")
            {
                Keyword("precision");
            }

            if (type == "character")
            {
                Keyword("varying");
            }

            int? length = null;

            if (to == CastTarget.Text && Peek() == '(')
            {
                _at++;

                if (!WholeNumber("A text length", out ScalarExpression? written, out error))
                {
                    return false;
                }

                int cut = (int)((ScalarExpression.Constant)written!).Value!;

                if (cut < 1)
                {
                    error = "A text length is at least 1.";
                    return false;
                }

                length = cut;

                if (!Close("the text length", out error))
                {
                    return false;
                }
            }

            if (!Close("cast", out error))
            {
                return false;
            }

            if (to == CastTarget.Date)
            {
                // A written date is read here, once, as DATE '…' is — so what a string means as a date does
                // not depend on the datastore's date style.
                if (operand is ScalarExpression.Constant { Value: string written })
                {
                    if (!TryMoment(written, out DateTimeOffset moment))
                    {
                        error = $"CAST('{written}' AS DATE): that is not a date. Write it as '2026-08-02' or "
                            + "'08/02/2026'.";
                        return false;
                    }

                    value = new ScalarExpression.Constant(
                        new DateTimeOffset(moment.UtcDateTime.Date, TimeSpan.Zero));
                    return true;
                }

                if (!Requires(operand!, "CAST AS DATE", out error, ExpressionKind.Text, ExpressionKind.Date))
                {
                    return false;
                }
            }
            else if (to != CastTarget.Text
                && !Requires(operand!, "CAST to a number", out error, ExpressionKind.Real, ExpressionKind.Text, ExpressionKind.Boolean))
            {
                return false;
            }

            value = new ScalarExpression.Cast(operand!, to, length);
            return true;
        }

        // EXTRACT ( part FROM expression )
        private bool ExtractCall(out ScalarExpression? value, out string? error)
        {
            value = null;

            string word = Word();
            _at += word.Length;

            DatePart? part = word switch
            {
                "year" => DatePart.Year,
                "month" => DatePart.Month,
                "day" => DatePart.Day,
                "hour" => DatePart.Hour,
                "minute" => DatePart.Minute,
                "second" => DatePart.Second,
                _ => null,
            };

            if (part is not { } which)
            {
                error = $"EXTRACT takes YEAR, MONTH, DAY, HOUR, MINUTE or SECOND, and '{word}' is none of them.";
                return false;
            }

            if (!Keyword("from"))
            {
                error = Unexpected("FROM in EXTRACT");
                return false;
            }

            if (!Expression(out ScalarExpression? operand, out error)
                || !Requires(operand!, "EXTRACT", out error, ExpressionKind.Date)
                || !Close("extract", out error))
            {
                return false;
            }

            value = new ScalarExpression.Extract(which, operand!);
            return true;
        }

        // TRIM ( [BOTH | LEADING | TRAILING] [' '] [FROM] expression )
        private bool TrimCall(out ScalarExpression? value, out string? error)
        {
            value = null;
            error = null;

            TrimSide side = TrimSide.Both;
            bool named = true;

            if (Keyword("leading"))
            {
                side = TrimSide.Leading;
            }
            else if (Keyword("trailing"))
            {
                side = TrimSide.Trailing;
            }
            else if (!Keyword("both"))
            {
                named = false;
            }

            int beforeCharacter = _at;
            bool character = false;

            if (Peek() == '\'')
            {
                if (!Literal(out object? written, out error))
                {
                    return false;
                }

                if (Keyword("from"))
                {
                    if (written is not " ")
                    {
                        error = "TRIM removes spaces, as ArcGIS defines it, so the character before FROM is ' '.";
                        return false;
                    }

                    character = true;
                }
                else
                {
                    // `TRIM('  text  ')`: the literal was the operand, not the character.
                    _at = beforeCharacter;
                }
            }

            if (named && !character && !Keyword("from"))
            {
                error = Unexpected("FROM in TRIM");
                return false;
            }

            if (!Expression(out ScalarExpression? operand, out error)
                || !Requires(operand!, "TRIM", out error, ExpressionKind.Text)
                || !Close("trim", out error))
            {
                return false;
            }

            value = new ScalarExpression.Trim(side, operand!);
            return true;
        }

        // POSITION ( needle { , | IN } haystack )
        private bool PositionCall(out ScalarExpression? value, out string? error)
        {
            value = null;

            if (!Expression(out ScalarExpression? needle, out error)
                || !Requires(needle!, "POSITION", out error, ExpressionKind.Text))
            {
                return false;
            }

            if (Peek() == ',')
            {
                _at++;
            }
            else if (!Keyword("in"))
            {
                error = Unexpected("',' or IN in POSITION");
                return false;
            }

            if (!Expression(out ScalarExpression? haystack, out error)
                || !Requires(haystack!, "POSITION", out error, ExpressionKind.Text)
                || !Close("position", out error))
            {
                return false;
            }

            value = new ScalarExpression.FunctionCall(ScalarFunction.Position, [needle!, haystack!]);
            return true;
        }

        // SUBSTRING ( text { , | FROM } start [ { , | FOR } length ] )
        private bool SubstringCall(out ScalarExpression? value, out string? error)
        {
            value = null;

            if (!Expression(out ScalarExpression? operand, out error)
                || !Requires(operand!, "SUBSTRING", out error, ExpressionKind.Text))
            {
                return false;
            }

            if (Peek() == ',')
            {
                _at++;
            }
            else if (!Keyword("from"))
            {
                error = Unexpected("',' or FROM in SUBSTRING");
                return false;
            }

            if (!WholeNumber("SUBSTRING's start", out ScalarExpression? from, out error))
            {
                return false;
            }

            List<ScalarExpression> arguments = [operand!, from!];

            bool more = Peek() == ',';

            if (more)
            {
                _at++;
            }

            if (more || Keyword("for"))
            {
                if (!WholeNumber("SUBSTRING's length", out ScalarExpression? length, out error))
                {
                    return false;
                }

                if ((int)((ScalarExpression.Constant)length!).Value! < 0)
                {
                    error = "SUBSTRING's length cannot be negative.";
                    return false;
                }

                arguments.Add(length);
            }

            if (!Close("substring", out error))
            {
                return false;
            }

            value = new ScalarExpression.FunctionCall(ScalarFunction.Substring, arguments);
            return true;
        }

        // ROUND | TRUNCATE ( number [ , places ] )
        private bool PlacesCall(ScalarFunction function, out ScalarExpression? value, out string? error)
        {
            value = null;
            string name = function == ScalarFunction.Round ? "ROUND" : "TRUNCATE";

            if (!Expression(out ScalarExpression? operand, out error)
                || !Requires(operand!, name, out error, ExpressionKind.Real))
            {
                return false;
            }

            List<ScalarExpression> arguments = [operand!];

            if (Peek() == ',')
            {
                _at++;

                if (!WholeNumber($"{name}'s number of places", out ScalarExpression? places, out error))
                {
                    return false;
                }

                arguments.Add(places!);
            }

            if (!Close(name, out error))
            {
                return false;
            }

            value = new ScalarExpression.FunctionCall(function, arguments);
            return true;
        }

        /// <summary>What the grammar evaluates, for the message that refuses anything else.</summary>
        private const string Supported =
            "It evaluates ArcGIS's standardized query functions — CAST, EXTRACT, CHAR_LENGTH, CONCAT, POSITION, "
            + "SUBSTRING, TRIM, UPPER, LOWER, ABS, CEILING, FLOOR, ROUND, TRUNCATE, MOD, POWER, LOG, LOG10, SIN, "
            + "COS, TAN, NULLIF, COALESCE — with + - * /, and CURRENT_DATE, CURRENT_TIMESTAMP, INTERVAL, DATE '…' "
            + "and TIMESTAMP '…' for dates. Anything else is refused rather than passed to the database.";

        private bool In(string column, bool not, out AttributePredicate? node, out string? error)
        {
            node = null;

            SkipSpace();

            if (Peek() != '(')
            {
                error = Unexpected("'(' after 'in'");
                return false;
            }

            _at++;

            List<object?> values = [];

            while (true)
            {
                if (!Literal(out object? value, out error)
                    || !Typed(column, value, out object? bound, out error))
                {
                    return false;
                }

                values.Add(bound);

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

            node = new AttributePredicate.OneOf(column, values, not);
            error = null;
            return true;
        }

        /// <summary>
        /// Consumes <c>UPPER(</c> or <c>LOWER(</c> when it is next, and says which.
        /// </summary>
        /// <remarks>
        /// <b>Only when an opening bracket follows</b>, so a column that happens to be called
        /// <c>upper</c> is still a column.
        /// </remarks>
        private Fold CaseFunction()
        {
            SkipSpace();
            int start = _at;

            if (Keyword("upper") && Peek() == '(')
            {
                _at++;
                return Fold.Upper;
            }

            _at = start;

            if (Keyword("lower") && Peek() == '(')
            {
                _at++;
                return Fold.Lower;
            }

            _at = start;
            return Fold.None;
        }

        /// <summary>
        /// A predicate over <c>UPPER(column)</c> or <c>LOWER(column)</c>, as the model's
        /// case-insensitive comparison.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Exact rather than approximate.</b> <c>UPPER(name) = 'ANKARA'</c> holds exactly when
        /// the name equals <c>ANKARA</c> ignoring case — but <c>UPPER(name) = 'Ankara'</c> holds for
        /// no row at all, because no upper-cased value has a lower-case letter in it. So a literal
        /// the function could not produce is answered as nothing (and <c>&lt;&gt;</c> as every row
        /// with a value), rather than as the case-insensitive match somebody might have meant.
        /// </para>
        /// <para>
        /// <b>Ordering is refused.</b> <c>UPPER(name) &lt; 'M'</c> depends on how the database
        /// orders folded text, which the model has no way to state.
        /// </para>
        /// </remarks>
        private bool Folded(string column, Fold fold, out AttributePredicate? node, out string? error)
        {
            node = null;
            error = null;

            string function = fold == Fold.Upper ? "UPPER" : "LOWER";

            if (Keyword("is"))
            {
                bool negated = Keyword("not");

                if (!Keyword("null"))
                {
                    error = Unexpected("'null' after 'is'");
                    return false;
                }

                node = new AttributePredicate.IsNull(column, negated);
                return true;
            }

            bool not = Keyword("not");

            if (Keyword("like"))
            {
                if (!Literal(out object? pattern, out error))
                {
                    return false;
                }

                if (pattern is not string text)
                {
                    error = "'like' compares text, so its right-hand side must be a quoted string.";
                    return false;
                }

                node = Folds(fold, text) == text
                    ? new AttributePredicate.Matches(column, text, not, IgnoreCase: true)
                    : not
                        ? new AttributePredicate.IsNull(column, Negated: true)
                        : new AttributePredicate.MatchesNothing();

                return true;
            }

            if (Keyword("in"))
            {
                SkipSpace();

                if (Peek() != '(')
                {
                    error = Unexpected("'(' after 'in'");
                    return false;
                }

                _at++;

                AttributePredicate? any = null;

                while (true)
                {
                    if (!Literal(out object? value, out error))
                    {
                        return false;
                    }

                    if (value is not string text)
                    {
                        error = $"{function}() is text, so every value in its 'in' list must be a quoted string.";
                        return false;
                    }

                    if (Folds(fold, text) == text)
                    {
                        AttributePredicate one = new AttributePredicate.Comparison(
                            column, ComparisonOperator.Equal, text, IgnoreCase: true);

                        any = any is null ? one : new AttributePredicate.Disjunction(any, one);
                    }

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

                AttributePredicate matched = any ?? new AttributePredicate.MatchesNothing();

                node = not
                    ? new AttributePredicate.Conjunction(
                        new AttributePredicate.IsNull(column, Negated: true),
                        new AttributePredicate.Negation(matched))
                    : matched;

                return true;
            }

            if (not)
            {
                error = Unexpected("'in' or 'like' after 'not'");
                return false;
            }

            if (!Operator(out ComparisonOperator op, out error))
            {
                return false;
            }

            if (op is not (ComparisonOperator.Equal or ComparisonOperator.NotEqual))
            {
                error =
                    $"{function}() may be compared with =, <>, LIKE or IN. Ordering folded text "
                    + "depends on the database's collation, which this server does not choose for "
                    + "the caller.";
                return false;
            }

            if (!Literal(out object? literal, out error))
            {
                return false;
            }

            if (literal is not string comparand)
            {
                error = $"{function}() is text, so it must be compared with a quoted string.";
                return false;
            }

            node = Folds(fold, comparand) == comparand
                ? new AttributePredicate.Comparison(column, op, comparand, IgnoreCase: true)
                : op == ComparisonOperator.Equal
                    ? new AttributePredicate.MatchesNothing()
                    : new AttributePredicate.IsNull(column, Negated: true);

            return true;
        }

        private static string Folds(Fold fold, string value) =>
            fold == Fold.Upper ? value.ToUpperInvariant() : value.ToLowerInvariant();

        /// <summary>
        /// <c>DATE '…'</c>, <c>TIMESTAMP '…'</c>, <c>CURRENT_DATE</c> or <c>CURRENT_TIMESTAMP</c>
        /// with an optional offset, evaluated to a moment.
        /// </summary>
        /// <param name="value">The moment, when one was written.</param>
        /// <param name="error">Why it was refused.</param>
        /// <param name="present">Whether a date expression was next at all.</param>
        /// <returns>False only for a date expression that is malformed.</returns>
        /// <remarks>
        /// <b>Evaluated once, here, against the clock the parse was given.</b> The database never
        /// sees <c>current_timestamp</c>, so two dialects cannot disagree about what now means, and
        /// a test can say which now it is.
        /// </remarks>
        private bool DateExpression(out object? value, out string? error, out bool present)
        {
            value = null;
            error = null;
            present = false;

            SkipSpace();
            int start = _at;

            foreach (string kind in (string[])["date", "timestamp"])
            {
                if (Keyword(kind) && Peek() == '\'')
                {
                    present = true;

                    if (!Literal(out object? written, out error))
                    {
                        return false;
                    }

                    if (written is not string text
                        || !DateTimeOffset.TryParse(
                            text,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out DateTimeOffset moment))
                    {
                        error =
                            $"{kind.ToUpperInvariant()} '{written}' is not a date. Write it as "
                            + "'2026-08-02' or '2026-08-02 14:30:00'.";
                        return false;
                    }

                    value = moment;
                    return true;
                }

                _at = start;
            }

            DateTimeOffset? origin = null;

            if (Keyword("current_timestamp") || Keyword("current_time"))
            {
                origin = now;
            }
            else if (Keyword("current_date"))
            {
                origin = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            }

            if (origin is null)
            {
                _at = start;
                return true;
            }

            present = true;

            // <b>ArcGIS's list writes them as functions — `CURRENT_DATE()` — and SQL as keywords</b>, so
            // an empty pair of brackets after one is accepted and means nothing more. ADR-083.
            int beforeBrackets = _at;

            if (Peek() == '(')
            {
                _at++;

                if (Peek() == ')')
                {
                    _at++;
                }
                else
                {
                    _at = beforeBrackets;
                }
            }

            char sign = Peek();

            if (sign is not ('-' or '+'))
            {
                value = origin.Value;
                return true;
            }

            _at++;

            TimeSpan offset;

            if (Keyword("interval"))
            {
                if (!Literal(out object? amount, out error))
                {
                    return false;
                }

                if (!double.TryParse(
                        Convert.ToString(amount, CultureInfo.InvariantCulture),
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double count)
                    || !double.IsFinite(count)
                    || Math.Abs(count) > 1_000_000_000)
                {
                    error = $"INTERVAL '{amount}' is not a number of units this server can offset a date by.";
                    return false;
                }

                if (Keyword("day"))
                {
                    offset = TimeSpan.FromDays(Math.Min(Math.Abs(count), 3_650_000) * Math.Sign(count));
                }
                else if (Keyword("hour"))
                {
                    offset = TimeSpan.FromHours(Math.Min(Math.Abs(count), 87_600_000) * Math.Sign(count));
                }
                else if (Keyword("minute"))
                {
                    offset = TimeSpan.FromMinutes(count);
                }
                else if (Keyword("second"))
                {
                    offset = TimeSpan.FromSeconds(count);
                }
                else
                {
                    error = Unexpected("DAY, HOUR, MINUTE or SECOND after an interval");
                    return false;
                }
            }
            else
            {
                // A bare number is days, which is what the ArcGIS standardized query examples mean
                // by `CURRENT_TIMESTAMP - 30`.
                if (!Literal(out object? days, out error))
                {
                    return false;
                }

                if (days is not (long or double)
                    || !double.IsFinite(Convert.ToDouble(days, CultureInfo.InvariantCulture))
                    || Math.Abs(Convert.ToDouble(days, CultureInfo.InvariantCulture)) > 3_650_000)
                {
                    error = "A date may be offset by a number of days, up to ten thousand years, or by an INTERVAL.";
                    return false;
                }

                offset = TimeSpan.FromDays(Convert.ToDouble(days, CultureInfo.InvariantCulture));
            }

            try
            {
                value = sign == '-' ? origin.Value - offset : origin.Value + offset;
            }
            catch (ArgumentOutOfRangeException)
            {
                error = "That offset reaches past the first or last date this server can represent.";
                return false;
            }

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

            // <b>A name followed by a parenthesis is a function, and saying *not a field* sent the reader looking
            // for a column — V-75, the fourth ArcGIS review.</b> `CAST(…)` answered *'CAST' is not a field of this
            // layer*. Which functions are evaluated is the grammar's to say, so it says it.
            int look = _at;

            while (look < text.Length && char.IsWhiteSpace(text[look]))
            {
                look++;
            }

            error = look < text.Length && text[look] == '('
                ? $"'{column}' is not a function this server evaluates in a where clause. " + Supported
                : $"'{column}' is not a field of this layer. The where clause may only mention "
                  + "fields the layer document lists.";

            column = null;
            return false;
        }

        private bool Operator(out ComparisonOperator op, out string? error)
        {
            error = null;
            op = default;

            SkipSpace();

            // Longest first, or '<' would match the front of '<='. '!=' is
            // accepted because clients send it and becomes NotEqual, the same
            // value '<>' produces — the database sees one spelling either way.
            foreach ((string candidate, ComparisonOperator meaning) in Operators)
            {
                if (text.AsSpan(_at).StartsWith(candidate, StringComparison.Ordinal))
                {
                    _at += candidate.Length;
                    op = meaning;
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

            if (!DateExpression(out value, out error, out bool dated))
            {
                return false;
            }

            if (dated)
            {
                return true;
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
