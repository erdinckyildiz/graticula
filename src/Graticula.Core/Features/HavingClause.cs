using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Graticula.Features;

/// <summary>
/// ArcGIS's <c>havingClause</c>, parsed and parameterised — Q-109, closing what D-41 refused.
/// </summary>
/// <remarks>
/// <para>
/// <b>No caller text reaches SQL.</b> The clause was appended to the statement unparsed until 2026-08-16, which made it
/// SQL injection on any public layer; it was refused since. This reads it in two steps. Each aggregate call —
/// <c>COUNT(field)</c>, <c>COUNT(*)</c>, <c>SUM</c>, <c>MIN</c>, <c>MAX</c>, <c>AVG</c>, <c>STDDEV</c>, <c>VAR</c> — is
/// found outside quoted strings, its field checked against the layer's, and replaced by a name of this parser's own.
/// What is left is a predicate over those names and the grouped fields, which <see cref="WhereClause"/> parses as it
/// parses <c>where</c>, with every literal bound. The names are turned back into the aggregate calls only when the SQL
/// is written, from this parser's own table, never from the clause.
/// </para>
/// <para>
/// <b>An aggregate the statement does not compute is still allowed</b> — <c>HAVING COUNT(*) &gt; 5</c> over a query
/// that only sums — because SQL evaluates it per group the same way; ArcGIS documents the clause that way.
/// </para>
/// </remarks>
public static partial class HavingClause
{
    /// <summary>The placeholder a having clause's literals are bound under, apart from <c>where</c>'s.</summary>
    public static string Placeholder(int index) => $"@h{index}";

    [GeneratedRegex(@"\b(COUNT|SUM|MIN|MAX|AVG|STDDEV|VAR)\s*\(\s*(\*|[A-Za-z_][A-Za-z0-9_]*)\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex Aggregate();

    /// <summary>Parses a clause into SQL over the grouped statement, or says why not.</summary>
    /// <param name="text">The clause as sent.</param>
    /// <param name="fields">The layer's fields and their types; aggregates may name any of them.</param>
    /// <param name="groupBy">The grouped fields, which the clause may also compare directly.</param>
    /// <param name="quote">Quotes a column name for the dialect.</param>
    /// <param name="parsed">The SQL, with <see cref="Placeholder"/> parameters.</param>
    /// <param name="error">Why it was refused.</param>
    /// <returns>Whether it parsed.</returns>
    public static bool TryParse(
        string text,
        IReadOnlyDictionary<string, FieldType> fields,
        IReadOnlyList<string> groupBy,
        Func<string, string> quote,
        out ParsedWhere parsed,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(groupBy);
        ArgumentNullException.ThrowIfNull(quote);

        parsed = default;
        error = null;

        Dictionary<string, string> aggregates = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder rewritten = new();
        string? refused = null;
        bool inString = false;
        int start = 0;

        // Aggregates are replaced outside single-quoted literals only: 'COUNT(x)' in a string stays a string.
        for (int i = 0; i <= text.Length; i++)
        {
            bool end = i == text.Length;
            if (!end && text[i] != '\'')
            {
                continue;
            }

            string segment = text[start..i];
            rewritten.Append(inString ? segment : Aggregate().Replace(segment, match =>
            {
                string function = match.Groups[1].Value.ToUpperInvariant();
                string field = match.Groups[2].Value;
                string sql;

                if (field == "*")
                {
                    if (function != "COUNT")
                    {
                        refused ??= $"'{function}(*)' is not an aggregate; only COUNT(*) counts rows.";
                        return match.Value;
                    }

                    sql = "count(*)";
                }
                else
                {
                    string? actual = fields.Keys.FirstOrDefault(f => string.Equals(f, field, StringComparison.OrdinalIgnoreCase));
                    if (actual is null)
                    {
                        refused ??= $"The having clause names '{field}', which is not a field of this layer.";
                        return match.Value;
                    }

                    sql = $"{Function(function)}({quote(actual)})";
                }

                string name = $"having_aggregate_{aggregates.Count}";
                aggregates[name] = sql;
                return name;
            }));

            if (!end)
            {
                rewritten.Append('\'');
                inString = !inString;
            }

            start = i + 1;
        }

        if (refused is not null)
        {
            error = refused;
            return false;
        }

        if (aggregates.Count == 0)
        {
            error = "A having clause filters groups by an aggregate — COUNT, SUM, MIN, MAX, AVG, STDDEV or VAR of a "
                + "field — and this one names none. Use 'where' to filter rows before they are grouped.";
            return false;
        }

        Dictionary<string, FieldType> types = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in aggregates.Keys)
        {
            types[name] = FieldType.Double;
        }

        foreach (string group in groupBy)
        {
            if (fields.TryGetValue(group, out FieldType type))
            {
                types[group] = type;
            }
        }

        if (!WhereClause.TryParse(rewritten.ToString(), [.. types.Keys], quote, out ParsedWhere checkedWhere, out error, types)
            || checkedWhere.Predicate is not { } predicate)
        {
            error = error is null ? "The having clause could not be read." : $"The having clause could not be read: {error}";
            return false;
        }

        string Columns(string name) => aggregates.TryGetValue(name, out string? sql) ? sql : quote(name);

        if (!PredicateSql.TryEmit(predicate, [.. types.Keys], Columns, out parsed, out error, Placeholder))
        {
            return false;
        }

        // The aggregates ride along, so an Oracle layer writes the same clause in its own dialect (ADR-205); the
        // functions are ones both databases spell alike.
        parsed = parsed with { Columns = aggregates };

        return true;
    }

    private static string Function(string function) => function switch
    {
        "COUNT" => "count",
        "SUM" => "sum",
        "MIN" => "min",
        "MAX" => "max",
        "AVG" => "avg",
        "STDDEV" => "stddev_samp",
        "VAR" => "var_samp",
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };
}
