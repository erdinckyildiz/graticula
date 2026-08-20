using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Graticula.Host;

/// <summary>
/// The subset of a portal search query this server can answer truthfully.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because ignoring the query is a wrong answer, not a small
/// one.</b> ArcGIS Pro searches for its geocoder with
/// <c>q=url:https://geocode.arcgis.com/…/GeocodeServer</c>. A server that ignores
/// <c>q</c> and returns everything it has just told Pro that a Turkish provinces
/// layer is a geocoding service — and Pro will use it. The failure is silent at
/// every step.
/// </para>
/// <para>
/// <b>So an unrecognised clause returns nothing rather than everything.</b> That
/// is the whole design: a filter this cannot evaluate is a question this cannot
/// answer, and the honest answer to a question you cannot answer is *no results*,
/// not *all of them*. It is the same rule <c>FilterReader</c> follows for WFS,
/// arrived at from the other direction.
/// </para>
/// <para>
/// <b>What it understands</b> — the clauses Pro actually sends: <c>type</c> and
/// its negation, <c>owner</c>, <c>url</c>, <c>title</c>, <c>tags</c>, and bare
/// words matched against the title. <c>ownerfolder</c> is accepted and ignored,
/// because this server has no portal folders and every item is therefore at the
/// root of the one that would exist.
/// </para>
/// </remarks>
internal static class PortalQuery
{
    /// <summary>Clauses that are understood, and one that is deliberately ignored.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "owner", "url", "title", "tags", "ownerfolder", "orgid", "access",
    };

    /// <summary>Whether an item satisfies a query.</summary>
    /// <param name="item">The item, as it will be written.</param>
    /// <param name="query">The <c>q</c> parameter, as the client wrote it.</param>
    /// <returns>Whether it matches.</returns>
    public static bool Matches(object item, string? query)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        foreach (Match clause in Regex.Matches(
            query, @"(-)?([A-Za-z]+):(""[^""]*""|\S+)|(""[^""]+""|\S+)", RegexOptions.None))
        {
            bool negated = clause.Groups[1].Success;
            string field = clause.Groups[2].Value;
            string value = Unquote(clause.Groups[3].Success ? clause.Groups[3].Value : clause.Groups[4].Value);

            if (value.Length == 0)
            {
                continue;
            }

            // <b>`*` is ArcGIS's match-all and clients send it as their default.</b> It
            // was being treated as a literal word to look for in the title, so
            // `search?q=*` answered `total: 0` with nothing to say that the syntax was
            // unsupported — an empty portal, from the query a client makes first. Found
            // by the second failure gate. Only the bare form is match-all: `title:*`
            // stays a literal, because a field with a value is a question about that
            // field and answering it with *everything* would be a different lie.
            if (field.Length == 0 && value == "*")
            {
                continue;
            }

            if (field.Length == 0)
            {
                // A bare word matches the title, which is what a person typing into
                // a search box means.
                if (!Contains(Field(item, "title"), value))
                {
                    return false;
                }

                continue;
            }

            if (!Known.Contains(field))
            {
                // <b>The clause that matters most.</b> `url:` naming a geocoder on
                // Esri's own servers is a question about somebody else's service,
                // and the only truthful answer this server has is none.
                return false;
            }

            if (string.Equals(field, "ownerfolder", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            bool hit = string.Equals(field, "tags", StringComparison.OrdinalIgnoreCase)
                ? ContainsAny(Field(item, "tags"), value)
                : Equals(Field(item, field), value);

            if (hit == negated)
            {
                return false;
            }
        }

        return true;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;

    /// <summary>
    /// One field of an anonymous item.
    /// </summary>
    /// <remarks>
    /// Reflection, because the item is the same anonymous object that will be
    /// serialised. Building a named type for it would be a second description of an
    /// item, which is the thing this whole surface avoids — and a search that reads
    /// a different object from the one it returns is a search that can disagree
    /// with its own results.
    /// </remarks>
    private static object? Field(object item, string name)
    {
        PropertyInfo? property = item.GetType().GetProperty(
            name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);

        return property?.GetValue(item);
    }

    private static bool Equals(object? actual, string wanted) =>
        actual is not null
        && string.Equals(actual.ToString(), wanted, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(object? actual, string wanted) =>
        actual is not null
        && actual.ToString()?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsAny(object? actual, string wanted)
    {
        if (actual is not IEnumerable<string> values)
        {
            return false;
        }

        foreach (string value in values)
        {
            if (string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
