using System.Collections.Generic;

namespace Graticula.Features;

/// <summary>How a field is compared with a value.</summary>
/// <remarks>
/// <b>An enumeration rather than an operator string, and that is the point.</b>
/// What keeps <see cref="WhereClause"/> safe is not that it validates text; it is
/// that the operator it emits comes from a fixed table rather than from the
/// caller. A second front end assembling SQL from a string would lose that
/// property without anything failing. It cannot lose this one.
/// </remarks>
public enum ComparisonOperator
{
    /// <summary><c>=</c>.</summary>
    Equal,

    /// <summary><c>&lt;&gt;</c>. <c>!=</c> is accepted on the wire and normalised to this.</summary>
    NotEqual,

    /// <summary><c>&lt;</c>.</summary>
    LessThan,

    /// <summary><c>&lt;=</c>.</summary>
    LessThanOrEqual,

    /// <summary><c>&gt;</c>.</summary>
    GreaterThan,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterThanOrEqual,
}

/// <summary>
/// A caller's attribute predicate, parsed, as a tree.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because there is about to be a second front end.</b>
/// <see cref="WhereClause"/> used to parse and emit in one pass — its own remark
/// described a tree that was not there, which is [D-117](../../../docs/architecture-debt.md).
/// With one caller that cost nothing. With two it costs the property the whole
/// design rests on: [ADR-039](../../../docs/adr/ADR-039-wfs-is-the-first-surface-after-v1.md)
/// §5 adds Filter Encoding 2.0 over WFS, and the only alternatives to a shared
/// target were a second SQL emitter or a compatibility adapter that writes SQL —
/// which §51 exists to prevent.
/// </para>
/// <para>
/// <b>A column here is a column name, not caller text.</b> Every producer matches
/// the name against the layer's own columns before building a node, and
/// <see cref="PredicateSql"/> matches it again before quoting it. The second check
/// is not redundant: it is what makes the emitter safe on its own, so a front end
/// written later cannot introduce an injection by forgetting the first.
/// </para>
/// <para>
/// <b>What is not here:</b> arithmetic, function calls, column-to-column
/// comparison and subqueries. Each is absent because there is no node for it, so
/// adding one is a deliberate act rather than an oversight — the same reasoning
/// the grammar uses, moved to where both front ends meet.
/// </para>
/// </remarks>
public abstract record AttributePredicate
{
    private protected AttributePredicate()
    {
    }

    /// <summary>Both sides hold.</summary>
    public sealed record Conjunction(AttributePredicate Left, AttributePredicate Right) : AttributePredicate;

    /// <summary>Either side holds.</summary>
    public sealed record Disjunction(AttributePredicate Left, AttributePredicate Right) : AttributePredicate;

    /// <summary>The operand does not hold.</summary>
    public sealed record Negation(AttributePredicate Operand) : AttributePredicate;

    /// <summary>A field compared with one value.</summary>
    /// <param name="Column">A column of the layer, matched rather than pattern-checked.</param>
    /// <param name="Operator">One of six, from a fixed table.</param>
    /// <param name="Value">Bound as a parameter, never written into the statement.</param>
    /// <param name="IgnoreCase">
    /// Whether letter case is disregarded, which only means anything for text.
    /// </param>
    /// <remarks>
    /// <b><c>IgnoreCase</c> exists because Filter Encoding 2.0 has <c>matchCase</c> and
    /// this model had no way to express it.</b> The WFS face refused
    /// <c>matchCase="false"</c> outright — the honest thing to do while the capability was
    /// missing, since answering a case-sensitive comparison to a caller who asked for a
    /// case-insensitive one is a wrong answer rather than a smaller one, and CITE's
    /// <c>propertyIsEqualTo_caseSensitive</c> reported it as a 400 where 200 was due.
    /// </remarks>
    public sealed record Comparison(
        string Column,
        ComparisonOperator Operator,
        object? Value,
        bool IgnoreCase = false)
        : AttributePredicate;

    /// <summary>A field is null, or is not.</summary>
    public sealed record IsNull(string Column, bool Negated) : AttributePredicate;

    /// <summary>A field matches a SQL <c>like</c> pattern, or does not.</summary>
    /// <param name="Column">A column of the layer.</param>
    /// <param name="Pattern">A SQL <c>like</c> pattern, already escaped.</param>
    /// <param name="Negated">Whether the match is inverted.</param>
    /// <param name="IgnoreCase">Whether letter case is disregarded.</param>
    public sealed record Matches(
        string Column,
        string Pattern,
        bool Negated,
        bool IgnoreCase = false)
        : AttributePredicate;

    /// <summary>A field falls between two values inclusive, or does not.</summary>
    public sealed record Between(string Column, object? Low, object? High, bool Negated)
        : AttributePredicate;

    /// <summary>A field is one of a list, or is none of them.</summary>
    public sealed record OneOf(string Column, IReadOnlyList<object?> Values, bool Negated)
        : AttributePredicate;

    /// <summary>
    /// Nothing matches, and that is the answer rather than a failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Because "no rows" and "your request was wrong" are different answers and a
    /// query model with no way to say the first says the second.</b> A WFS
    /// <c>ResourceId</c> naming a resource this server does not have selects nothing —
    /// the request is well formed and the collection comes back empty. Before this
    /// existed the only way to express it was an <see cref="OneOf"/> with an empty list,
    /// which <c>PredicateSql</c> correctly refuses, so the caller got a 400 saying the
    /// identity column holds whole numbers. True, and not what they asked.
    /// </para>
    /// <para>
    /// <b>It emits <c>false</c>, which the planner removes.</b> There is no cost to
    /// asking the database a question with no answer, and asking it keeps one path
    /// through the code rather than a short circuit in each front end that would then
    /// have to reproduce paging, counting and the response shape.
    /// </para>
    /// </remarks>
    public sealed record MatchesNothing : AttributePredicate;
}
