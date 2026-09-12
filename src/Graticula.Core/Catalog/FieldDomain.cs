using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Catalog;

/// <summary>
/// A value a domain names — a code, a bound, or a subtype's default: a number or a piece of text.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two cases and no type of its own, because the column decides the rest — ADR-065.</b> A
/// domain is a claim about a column, so the column's type is what says whether <c>1</c> is a
/// small integer, a double or a date; the value only has to remember which of JSON's two scalar
/// shapes it arrived as. A date is its epoch milliseconds, which is how the ArcGIS REST API
/// writes one.
/// </para>
/// <para>
/// <b>A <see cref="decimal"/> rather than a <see cref="double"/></b>, so that a code on a
/// 32-bit integer column round-trips exactly and a comparison with the value a client sent is
/// an equality rather than a tolerance. A single-precision column is the one place that is not
/// enough on its own, and <see cref="DomainRules"/> compares those in their own precision.
/// </para>
/// </remarks>
public readonly record struct DomainValue
{
    private DomainValue(decimal? number, string? text)
    {
        Number = number;
        Text = text;
    }

    /// <summary>The value as a number, or null when it is text.</summary>
    public decimal? Number { get; }

    /// <summary>The value as text, or null when it is a number.</summary>
    public string? Text { get; }

    /// <summary>A number.</summary>
    /// <param name="number">The number.</param>
    /// <returns>The value.</returns>
    /// <remarks>
    /// <b>Trailing zeros are dropped</b>, so <c>1.50</c> read from one document and <c>1.5</c> from
    /// another are one code and write back the same way. Dividing by one at the largest scale is
    /// the arithmetic that does it; the value is unchanged.
    /// </remarks>
    public static DomainValue Of(decimal number) =>
        new(number / 1.0000000000000000000000000000m, null);

    /// <summary>A piece of text.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The value.</returns>
    public static DomainValue Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new(null, text);
    }

    /// <summary>Whether this is a number.</summary>
    public bool IsNumber => Number is not null;

    /// <inheritdoc/>
    public override string ToString() =>
        Number is { } number ? number.ToString(CultureInfo.InvariantCulture) : $"'{Text}'";
}

/// <summary>Whether a domain lists its values or bounds them.</summary>
public enum DomainKind
{
    /// <summary>A list of codes, each with a name a person reads.</summary>
    CodedValue,

    /// <summary>A least and a greatest value, both allowed.</summary>
    Range,
}

/// <summary>One entry of a coded-value domain.</summary>
/// <param name="Code">What is stored.</param>
/// <param name="Name">What a person is shown.</param>
public sealed record CodedValue(DomainValue Code, string Name);

/// <summary>
/// What values a column may hold — ADR-065.
/// </summary>
/// <remarks>
/// <para>
/// <b>The two kinds the ArcGIS REST API's domain objects name, and not the third.</b> A
/// <c>codedValue</c> domain lists codes with names and a <c>range</c> domain bounds a number or a
/// date; the <c>inherited</c> object is not a domain but a pointer from a subtype to the field's
/// own, and <see cref="LayerSubtypes"/> expresses it by the absence of an entry.
/// </para>
/// <para>
/// <b>A domain this server reports is a domain this server enforces</b> — ADR-013 condition 5.
/// <see cref="DomainRules.Refusal"/> is the one check both writing faces reach through the
/// writer they share, so a coded value a client draws as a drop-down is a coded value a hand-made
/// <c>applyEdits</c> cannot get around.
/// </para>
/// </remarks>
public sealed class FieldDomain
{
    private readonly HashSet<string>? _texts;
    private readonly HashSet<decimal>? _numbers;

    private FieldDomain(
        string name, DomainKind kind, IReadOnlyList<CodedValue> codes, DomainValue? min, DomainValue? max)
    {
        Name = name;
        Kind = kind;
        Codes = codes;
        Min = min;
        Max = max;

        // <b>A set beside the list, built once</b>, because a geodatabase domain can hold
        // thousands of codes and the check runs for every value of every feature written.
        if (kind == DomainKind.CodedValue)
        {
            _texts = new HashSet<string>(StringComparer.Ordinal);
            _numbers = [];

            foreach (CodedValue coded in codes)
            {
                if (coded.Code.Number is { } number)
                {
                    _numbers.Add(number);
                }
                else if (coded.Code.Text is { } text)
                {
                    _texts.Add(text);
                }
            }
        }
    }

    /// <summary>The domain's name, which a client shows and never matches on.</summary>
    public string Name { get; }

    /// <summary>Whether it lists or bounds.</summary>
    public DomainKind Kind { get; }

    /// <summary>The codes, in the order they were given; empty for a range.</summary>
    public IReadOnlyList<CodedValue> Codes { get; }

    /// <summary>The least value allowed, for a range.</summary>
    public DomainValue? Min { get; }

    /// <summary>The greatest value allowed, for a range.</summary>
    public DomainValue? Max { get; }

    /// <summary>A coded-value domain.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="codes">Its codes.</param>
    /// <returns>The domain; <see cref="DomainRules.Refuse(FieldDomain, Graticula.Features.FieldType, int?)"/> says whether it fits a column.</returns>
    public static FieldDomain Coded(string name, IEnumerable<CodedValue> codes)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(codes);

        return new FieldDomain(name, DomainKind.CodedValue, [.. codes], null, null);
    }

    /// <summary>A range domain.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="min">The least value allowed.</param>
    /// <param name="max">The greatest value allowed.</param>
    /// <returns>The domain.</returns>
    public static FieldDomain Range(string name, DomainValue min, DomainValue max)
    {
        ArgumentNullException.ThrowIfNull(name);

        return new FieldDomain(name, DomainKind.Range, [], min, max);
    }

    /// <summary>Whether a code of this exact number is listed.</summary>
    internal bool Lists(decimal number) => _numbers?.Contains(number) == true;

    /// <summary>Whether a code of this exact text is listed.</summary>
    internal bool Lists(string text) => _texts?.Contains(text) == true;

    /// <summary>The name a code carries, or null when it is not listed.</summary>
    /// <param name="code">A code.</param>
    /// <returns>Its name.</returns>
    public string? NameOf(DomainValue code) =>
        Codes.FirstOrDefault(c => c.Code == code)?.Name;

    /// <summary>
    /// Whether two domains say the same thing, entry for entry.
    /// </summary>
    /// <param name="other">The other domain.</param>
    /// <returns>Whether they are the same claim.</returns>
    /// <remarks>
    /// <b>Structural, and named rather than an <c>Equals</c> override</b>, because a domain is
    /// held by a record struct whose equality decides whether a description changed; a list
    /// compared by reference would call every re-read domain a change.
    /// </remarks>
    public bool SameAs(FieldDomain? other) =>
        other is not null
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Kind == other.Kind
        && Min == other.Min
        && Max == other.Max
        && Codes.SequenceEqual(other.Codes);
}
