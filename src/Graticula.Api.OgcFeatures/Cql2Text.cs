using System;
using System.Text;
using System.Text.Json;
using Graticula.Features;

namespace Graticula.Api.OgcFeatures;

/// <summary>
/// OGC API Features Part 3's <c>filter</c> in CQL2 text, at the Basic CQL2 level — ADR-165: written into the
/// predicate grammar every other filter on this server goes through, and the queryables document that names what it
/// may filter on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Basic CQL2 is the comparison language this server already parses.</b> Comparisons, <c>AND</c>, <c>OR</c>,
/// <c>NOT</c>, <c>IS [NOT] NULL</c>, and from Advanced Comparison <c>LIKE</c>, <c>BETWEEN</c> and <c>IN</c> read the
/// same in CQL2 text and in the where-clause grammar, so the text is translated where they differ and handed to that
/// parser: <c>TIMESTAMP('…')</c> and <c>DATE('…')</c> become the quoted instant, which the parser binds by the
/// column's type, and <c>CASEI(…)</c> becomes <c>UPPER(…)</c>.
/// </para>
/// <para>
/// <b>What is not Basic is refused by name</b> — the spatial (<c>S_…</c>), temporal (<c>T_…</c>) and array
/// (<c>A_…</c>) functions — rather than misread.
/// </para>
/// </remarks>
public static class Cql2Text
{
    /// <summary>The filter language, as <c>filter-lang</c> names it.</summary>
    public const string Language = "cql2-text";

    /// <summary>The CQL2 text written in the where-clause grammar, or why it cannot be.</summary>
    /// <param name="filter">The <c>filter</c> parameter.</param>
    /// <param name="where">The translated text.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it translated.</returns>
    public static bool TryTranslate(string filter, out string where, out string? error)
    {
        ArgumentNullException.ThrowIfNull(filter);
        StringBuilder written = new(filter.Length);
        where = string.Empty;
        error = null;
        int i = 0;

        while (i < filter.Length)
        {
            char c = filter[i];

            // A string literal is copied as it is, doubled quotes and all.
            if (c == '\'')
            {
                int end = i + 1;

                while (end < filter.Length && !(filter[end] == '\'' && (end + 1 >= filter.Length || filter[end + 1] != '\'')))
                {
                    end += filter[end] == '\'' ? 2 : 1;
                }

                written.Append(filter, i, Math.Min(end + 1, filter.Length) - i);
                i = end + 1;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int end = i;

                while (end < filter.Length && (char.IsLetterOrDigit(filter[end]) || filter[end] == '_'))
                {
                    end++;
                }

                string word = filter[i..end];
                int next = end;

                while (next < filter.Length && char.IsWhiteSpace(filter[next]))
                {
                    next++;
                }

                bool call = next < filter.Length && filter[next] == '(';
                string upper = word.ToUpperInvariant();

                if (call && (upper.StartsWith("S_", StringComparison.Ordinal) || upper.StartsWith("T_", StringComparison.Ordinal)
                    || upper.StartsWith("A_", StringComparison.Ordinal)))
                {
                    error = $"`{word}` is a spatial, temporal or array function, and this server's filter is Basic CQL2 — "
                        + "comparisons, AND, OR, NOT, IS NULL, LIKE, BETWEEN and IN. Filter by place with `bbox` and by time "
                        + "with `datetime`.";
                    return false;
                }

                // TIMESTAMP('…') and DATE('…'): the literal alone, bound by the column's type.
                if (call && upper is "TIMESTAMP" or "DATE")
                {
                    int open = next + 1;
                    int close = filter.IndexOf(')', open);

                    if (close < 0)
                    {
                        error = $"`{word}(` is not closed.";
                        return false;
                    }

                    written.Append(filter.AsSpan(open, close - open).Trim());
                    i = close + 1;
                    continue;
                }

                // CASEI of a literal is the literal in capitals, which is what UPPER of a column is compared with.
                if (call && upper == "CASEI")
                {
                    int open = next + 1;

                    while (open < filter.Length && char.IsWhiteSpace(filter[open]))
                    {
                        open++;
                    }

                    int close = filter.IndexOf(')', open);

                    if (open < filter.Length && filter[open] == '\'' && close > open
                        && filter.AsSpan(open, close - open).TrimEnd().EndsWith("'", StringComparison.Ordinal))
                    {
                        written.Append(filter.AsSpan(open, close - open).TrimEnd().ToString().ToUpperInvariant());
                        i = close + 1;
                        continue;
                    }
                }

                written.Append(call && upper == "CASEI" ? "UPPER" : word);
                i = end;
                continue;
            }

            written.Append(c);
            i++;
        }

        where = written.ToString();
        return true;
    }

    /// <summary>
    /// The queryables document — a JSON Schema of the properties a filter may name, with their types.
    /// </summary>
    /// <param name="self">This document's address.</param>
    /// <param name="collection">The collection.</param>
    /// <returns>The JSON.</returns>
    public static string Queryables(string self, CollectionMetadata collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        using System.IO.MemoryStream buffer = new();

        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteString("$schema", "https://json-schema.org/draft/2020-12/schema");
            json.WriteString("$id", self);
            json.WriteString("type", "object");
            json.WriteString("title", collection.Title);
            json.WriteStartObject("properties");

            foreach (FieldDescription field in collection.Fields)
            {
                json.WriteStartObject(field.Name);
                json.WriteString("title", field.Name);

                switch (field.Type)
                {
                    case FieldType.Integer or FieldType.SmallInteger or FieldType.BigInteger:
                        json.WriteString("type", "integer");
                        break;
                    case FieldType.Double or FieldType.Single:
                        json.WriteString("type", "number");
                        break;
                    case FieldType.Boolean:
                        json.WriteString("type", "boolean");
                        break;
                    case FieldType.Date:
                        json.WriteString("type", "string");
                        json.WriteString("format", "date-time");
                        break;
                    default:
                        json.WriteString("type", "string");
                        break;
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteBoolean("additionalProperties", false);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
