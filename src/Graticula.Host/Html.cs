using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;

namespace Graticula.Host;

/// <summary>
/// Markup that is safe to write into a page: text that has been escaped, or markup this server wrote.
/// </summary>
/// <remarks>
/// <para>
/// <b>ADR-023 condition 3, 2026-10-12: the encoding discipline replaced by something structural.</b> Until this
/// type, every HTML page this server writes was a <c>string</c> built by interpolation, and whether a value had
/// been passed through <c>WebUtility.HtmlEncode</c> was a thing each line remembered or did not. The condition
/// asked for *a type that carries "already escaped", or a renderer that cannot be handed a raw string* before the
/// surface grew past the four document kinds it had; by then there were fifteen files writing HTML.
/// </para>
/// <para>
/// <b>So a hole is escaped unless it is already <see cref="Html"/>.</b> <c>Html.Of($"&lt;td&gt;{name}&lt;/td&gt;")</c>
/// escapes <c>name</c> because it is a string, and leaves <c>{cell}</c> alone because it is markup. The page
/// writer (<c>RestDirectory.Wrap</c>) takes <see cref="Html"/>, so a raw string cannot reach a page without
/// going through one of the two named doors — <see cref="Text"/>, which escapes, and <see cref="Markup"/>, which
/// does not and is meant for this server's own literals. Both are greppable, which is the point: an unescaped
/// value now has a name at the line that wrote it.
/// </para>
/// <para>
/// <b>Escaped exactly as before</b>: <see cref="WebUtility.HtmlEncode(string)"/>, and numbers formatted with the
/// invariant culture, which is what every page's <c>StringBuilder.Append(CultureInfo.InvariantCulture, …)</c> did.
/// The conversion was checked by crawling every page the local fixture serves before and after it and comparing
/// them byte for byte.
/// </para>
/// </remarks>
public readonly struct Html : IEquatable<Html>
{
    private readonly string? _value;

    private Html(string value) => _value = value;

    /// <summary>Nothing.</summary>
    public static Html Empty => default;

    /// <summary>Whether this is empty.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>Text, escaped. Null is empty.</summary>
    public static Html Text(string? text) => new(WebUtility.HtmlEncode(text ?? string.Empty));

    /// <summary>
    /// Markup written by this server, passed through as it is. Never a value that came from a request, a
    /// catalogue row or a file.
    /// </summary>
    public static Html Markup(string markup) => new(markup ?? string.Empty);

    /// <summary>Markup from an interpolated string, its string holes escaped and its <see cref="Html"/> holes kept.</summary>
    public static Html Of(ref HtmlInterpolatedStringHandler html) => html.ToHtml();

    /// <summary>The parts, one after another.</summary>
    public static Html Concat(IEnumerable<Html> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        StringBuilder joined = new();

        foreach (Html part in parts)
        {
            joined.Append(part._value);
        }

        return new(joined.ToString());
    }

    /// <summary>The parts with a separator of markup between them.</summary>
    public static Html Join(Html separator, IEnumerable<Html> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);

        return new(string.Join(separator._value, EnumerateValues(parts)));
    }

    private static IEnumerable<string?> EnumerateValues(IEnumerable<Html> parts)
    {
        foreach (Html part in parts)
        {
            yield return part._value;
        }
    }

    /// <summary>The two, one after the other.</summary>
    public static Html operator +(Html left, Html right) => new(left._value + right._value);

    /// <summary>The markup.</summary>
    public override string ToString() => _value ?? string.Empty;

    /// <inheritdoc/>
    public bool Equals(Html other) => string.Equals(ToString(), other.ToString(), StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Html other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>Equal markup.</summary>
    public static bool operator ==(Html left, Html right) => left.Equals(right);

    /// <summary>Different markup.</summary>
    public static bool operator !=(Html left, Html right) => !left.Equals(right);
}

/// <summary>Builds <see cref="Html"/> from an interpolated string, escaping every hole that is not already markup.</summary>
[InterpolatedStringHandler]
public ref struct HtmlInterpolatedStringHandler
{
    private DefaultInterpolatedStringHandler _inner;

    /// <summary>Starts a handler; called by the compiler.</summary>
    public HtmlInterpolatedStringHandler(int literalLength, int formattedCount) =>
        _inner = new DefaultInterpolatedStringHandler(literalLength, formattedCount, CultureInfo.InvariantCulture);

    /// <summary>The literal parts are this server's markup.</summary>
    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    /// <summary>Markup is kept.</summary>
    public void AppendFormatted(Html value) => _inner.AppendLiteral(value.ToString());

    /// <summary>A builder's markup is kept, as its <see cref="HtmlBuilder.ToHtml"/> would be.</summary>
    public void AppendFormatted(HtmlBuilder? value) => _inner.AppendLiteral(value?.ToString() ?? string.Empty);

    /// <summary>Text is escaped.</summary>
    public void AppendFormatted(string? value) => _inner.AppendLiteral(WebUtility.HtmlEncode(value ?? string.Empty));

    /// <summary>Anything else is formatted with the invariant culture, then escaped.</summary>
    public void AppendFormatted<T>(T value) => AppendFormatted(value, format: null);

    /// <summary>Formatted with the invariant culture and the format given, then escaped.</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        string text = value switch
        {
            null => string.Empty,
            IFormattable formattable => formattable.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

        _inner.AppendLiteral(WebUtility.HtmlEncode(text));
    }

    internal Html ToHtml() => Html.Markup(_inner.ToStringAndClear());
}

/// <summary>A page body, which only takes <see cref="Html"/>.</summary>
public sealed class HtmlBuilder
{
    private readonly StringBuilder _text = new();

    /// <summary>Adds markup.</summary>
    public HtmlBuilder Append(Html html)
    {
        _text.Append(html.ToString());
        return this;
    }

    /// <summary>Adds an interpolated string, its string holes escaped.</summary>
    public HtmlBuilder Append(ref HtmlInterpolatedStringHandler html) => Append(html.ToHtml());

    /// <summary>Whether anything has been added.</summary>
    public int Length => _text.Length;

    /// <summary>What has been built.</summary>
    public Html ToHtml() => Html.Markup(_text.ToString());

    /// <summary>The markup, as a string — for writing to a response. A page takes <see cref="ToHtml"/>.</summary>
    public override string ToString() => _text.ToString();
}
