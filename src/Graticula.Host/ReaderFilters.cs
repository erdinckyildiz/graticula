using System;
using Graticula.Features;
using Graticula.Platform.Catalog;
using Graticula.Providers.PostGis;

namespace Graticula.Host;

/// <summary>
/// A view filter that names its reader — <c>${username}</c>, ADR-300 §5.2 — on its way to the layer's own parser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Parsed by the layer's own <see cref="WhereClause"/>, not by a second parser.</b> The variable stands as a string
/// literal no member can be called (<see cref="ReaderSession.Marker"/>), so the filter is accepted and refused exactly as
/// any view filter is — its columns, its operators, its types — and the parsed filter carries the marker as one of its
/// values, which <see cref="PostGisImporter.CreateViewAsync"/> replaces with the reader's setting.
/// </para>
/// <para>
/// <b>Survey123 writes it bare</b> (<c>Creator=${username}</c>); a filter that quotes it (<c>'${username}'</c>) means the
/// same and is read the same. It may stand only as a whole value: inside a longer literal (<c>'%${username}%'</c>) it
/// would be the characters themselves, which is never what was meant, so that is refused with a sentence.
/// </para>
/// </remarks>
internal static class ReaderFilters
{
    private static readonly string Literal = "'" + ReaderSession.Marker + "'";

    /// <summary>Whether a written filter names its reader.</summary>
    /// <param name="written">The filter as written, or null.</param>
    /// <returns>Whether it does.</returns>
    public static bool Names(string? written) =>
        written is not null && written.Contains(PublishedLayer.ReaderVariable, StringComparison.Ordinal);

    /// <summary>The filter with each <c>${username}</c> standing as the marked literal.</summary>
    /// <param name="written">The filter as written.</param>
    /// <returns>The filter to parse.</returns>
    public static string Prepare(string written)
    {
        ArgumentNullException.ThrowIfNull(written);

        if (!Names(written))
        {
            return written;
        }

        return written
            .Replace("'" + PublishedLayer.ReaderVariable + "'", Literal, StringComparison.Ordinal)
            .Replace(PublishedLayer.ReaderVariable, Literal, StringComparison.Ordinal);
    }

    /// <summary>The parser's words with the marker written back as the variable the publisher wrote.</summary>
    /// <param name="error">The parser's refusal.</param>
    /// <returns>The refusal, readable.</returns>
    public static string? Words(string? error) =>
        error?.Replace(Literal, PublishedLayer.ReaderVariable, StringComparison.Ordinal)
            .Replace(ReaderSession.Marker, PublishedLayer.ReaderVariable, StringComparison.Ordinal);

    /// <summary>
    /// Why a parsed filter cannot stand — <c>${username}</c> inside a longer value — or null when it can.
    /// </summary>
    /// <param name="parsed">The parsed filter.</param>
    /// <returns>The refusal, or null.</returns>
    public static string? Misplaced(ParsedWhere parsed)
    {
        foreach (object? value in parsed.Parameters)
        {
            if (value is string text
                && text.Contains(ReaderSession.Marker, StringComparison.Ordinal)
                && !string.Equals(text, ReaderSession.Marker, StringComparison.Ordinal))
            {
                return $"{PublishedLayer.ReaderVariable} may stand only as a whole value, as in "
                    + $"created_user = {PublishedLayer.ReaderVariable}; inside a longer text it would be read as those "
                    + "characters.";
            }
        }

        return null;
    }
}
