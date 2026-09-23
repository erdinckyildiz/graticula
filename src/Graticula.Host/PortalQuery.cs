using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

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
/// <b>What it understands</b> — the fields in <see cref="Known"/>, bare words matched
/// against the title, and since 2026-09-23 the search reference's grammar around them:
/// <c>AND</c>, <c>OR</c>, <c>NOT</c>, <c>-</c>, parentheses, <c>field:( … )</c> and a
/// trailing <c>*</c> (V-48). <c>ownerfolder</c> is accepted and ignored,
/// because this server has no portal folders and every item is therefore at the
/// root of the one that would exist.
/// </para>
/// </remarks>
internal static class PortalQuery
{
    /// <summary>Clauses that are understood, and one that is deliberately ignored.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "owner", "url", "title", "tags", "ownerfolder", "orgid", "accountid", "access", "group", "typekeywords", "id",
    };

    /// <summary>Whether an item satisfies a query.</summary>
    /// <param name="item">The item, as it will be written.</param>
    /// <param name="query">The <c>q</c> parameter, as the client wrote it.</param>
    /// <param name="groups">
    /// The groups the item is shared with, for a <c>group:</c> clause; null for something that is not
    /// shared into groups, which no <c>group:</c> clause matches.
    /// </param>
    /// <returns>Whether it matches.</returns>
    /// <remarks>
    /// <b><c>group:</c> since 2026-09-15.</b> ArcGIS Pro's portal pane and the Python API find a
    /// group's content with <c>q=group:&lt;id&gt;</c>, and this answered no clause it did not know
    /// with nothing, so a service shared with a group was found by nobody who looked for it there.
    /// </remarks>
    public static bool Matches(object item, string? query, IReadOnlyCollection<Guid>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        // <b>A query that does not parse, or names a field this cannot read, is one this cannot
        // answer</b> — so nothing, not everything, and for the whole query rather than for the clause:
        // `type:"Feature Service" OR url:https://geocode…` must not be answered by the half it knows.
        if (Parse(query) is not { } parsed || parsed.Unknown)
        {
            return false;
        }

        return parsed.Root.Evaluate(item, groups) ?? true;
    }

    /// <summary>A parsed query, and whether it names a field this server cannot evaluate.</summary>
    private sealed record Parsed(Node Root, bool Unknown);

    /// <summary>
    /// Parses the search reference's grammar — V-48, the third ArcGIS review.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What was missing.</b> Clauses were read as a flat list joined by AND, so the Map Viewer's
    /// <c>(type:"Feature Service" OR type:"Map Service")</c>, the Python API's
    /// <c>owner:x AND title:roads</c>, a field group <c>type:("Feature Service" "Map Service")</c> and a
    /// trailing wildcard <c>title:ist*</c> each answered zero items — <c>AND</c> was a word looked for in
    /// the title — with nothing to say the syntax was the reason.
    /// </para>
    /// <para>
    /// <b>The rules are the search reference's</b>
    /// (developers.arcgis.com/rest/users-groups-and-items/search-reference): AND is the default between
    /// terms; the operators are <c>AND</c>, <c>OR</c>, <c>NOT</c> and a leading <c>-</c> or <c>+</c>, and
    /// must be in capitals, so a lower-case <c>and</c> is a word; parentheses group; <c>field:( … )</c>
    /// applies a field to every term inside; and <c>*</c> is a wildcard only at the end of a term.
    /// </para>
    /// </remarks>
    private static Parsed? Parse(string query)
    {
        List<string> tokens = Tokens(query);
        int position = 0;
        bool unknown = false;

        Node? Or(string? field)
        {
            List<Node> any = [];

            do
            {
                if (And(field) is not { } next)
                {
                    return null;
                }

                any.Add(next);
            }
            while (position < tokens.Count && tokens[position] == "OR" && ++position > 0);

            return any.Count == 1 ? any[0] : new AnyOf(any);
        }

        Node? And(string? field)
        {
            List<Node> all = [];

            while (position < tokens.Count && tokens[position] is not (")" or "OR"))
            {
                if (tokens[position] == "AND")
                {
                    position++;
                    continue;
                }

                if (Unary(field) is not { } next)
                {
                    return null;
                }

                all.Add(next);
            }

            return all.Count switch
            {
                0 => null,
                1 => all[0],
                _ => new AllOf(all),
            };
        }

        Node? Unary(string? field)
        {
            string token = tokens[position];

            if (token is "NOT" or "-")
            {
                position++;
                return position < tokens.Count && Unary(field) is { } negated ? new Not(negated) : null;
            }

            if (token == "+")
            {
                position++;
                return position < tokens.Count ? Unary(field) : null;
            }

            return Primary(field);
        }

        Node? Primary(string? field)
        {
            string token = tokens[position++];

            if (token == "(")
            {
                Node? inner = Or(field);

                if (inner is null || position >= tokens.Count || tokens[position] != ")")
                {
                    return null;
                }

                position++;
                return inner;
            }

            if (token == ")")
            {
                return null;
            }

            // `name:` — a field, applying to the value or the group that follows it.
            if (token.Length > 1 && token[^1] == ':' && field is null)
            {
                string named = token[..^1];

                if (!Known.Contains(named))
                {
                    unknown = true;
                }

                return position < tokens.Count && tokens[position] is not (")" or "AND" or "OR")
                    ? (tokens[position] == "(" ? Primary(named) : Term(named, tokens[position++]))
                    : null;
            }

            return Term(field, token);
        }

        Node? Term(string? field, string token) =>
            token is "(" or ")" ? null : new Word(field, Unquote(token));

        Node? root = tokens.Count == 0 ? new AllOf([]) : Or(null);

        return root is null || position != tokens.Count ? null : new Parsed(root, unknown);
    }

    /// <summary>
    /// Splits a query into parentheses, quoted phrases, operators, <c>field:</c> names and words.
    /// </summary>
    /// <remarks>
    /// A field name is the letters before the first colon of a word, so <c>url:https://…</c> is the field
    /// <c>url</c> and the value <c>https://…</c>. A leading <c>-</c> or <c>+</c> is its own token only when
    /// something follows it in the same word, so a lone hyphen is still a word.
    /// </remarks>
    private static List<string> Tokens(string query)
    {
        List<string> tokens = [];
        int i = 0;
        bool afterField = false;

        while (i < query.Length)
        {
            char c = query[i];

            if (char.IsWhiteSpace(c))
            {
                afterField = false;
                i++;
            }
            else if (c is '(' or ')')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c is '-' or '+' && i + 1 < query.Length && !char.IsWhiteSpace(query[i + 1]))
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (c == '"')
            {
                int close = query.IndexOf('"', i + 1);
                int end = close < 0 ? query.Length : close + 1;
                tokens.Add(query[i..end]);
                i = end;
            }
            else
            {
                int start = i;

                while (i < query.Length && !char.IsWhiteSpace(query[i]) && query[i] is not ('(' or ')' or '"'))
                {
                    if (!afterField && query[i] == ':' && i > start
                        && query.AsSpan(start, i - start).ToString().All(char.IsAsciiLetter))
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                tokens.Add(query[start..i]);
                afterField = tokens[^1][^1] == ':' && !afterField;
            }
        }

        return tokens;
    }

    /// <summary>One node of a parsed query. Null from <see cref="Evaluate"/> means *no constraint*.</summary>
    private abstract record Node
    {
        public abstract bool? Evaluate(object item, IReadOnlyCollection<Guid>? groups);
    }

    private sealed record AllOf(IReadOnlyList<Node> Nodes) : Node
    {
        public override bool? Evaluate(object item, IReadOnlyCollection<Guid>? groups) =>
            Nodes.All(node => node.Evaluate(item, groups) != false);
    }

    private sealed record AnyOf(IReadOnlyList<Node> Nodes) : Node
    {
        public override bool? Evaluate(object item, IReadOnlyCollection<Guid>? groups) =>
            Nodes.Any(node => node.Evaluate(item, groups) != false);
    }

    private sealed record Not(Node Inner) : Node
    {
        public override bool? Evaluate(object item, IReadOnlyCollection<Guid>? groups) =>
            Inner.Evaluate(item, groups) is { } held ? !held : null;
    }

    private sealed record Word(string? Field, string Value) : Node
    {
        public override bool? Evaluate(object item, IReadOnlyCollection<Guid>? groups)
        {
            if (Value.Length == 0)
            {
                return null;
            }

            // <b>`*` is ArcGIS's match-all and clients send it as their default.</b> It
            // was being treated as a literal word to look for in the title, so
            // `search?q=*` answered `total: 0` with nothing to say that the syntax was
            // unsupported — an empty portal, from the query a client makes first. Found
            // by the second failure gate. Only the bare form is match-all: `title:*`
            // stays a literal, because a field with a value is a question about that
            // field and answering it with *everything* would be a different lie.
            if (Field is null && Value == "*")
            {
                return null;
            }

            if (Field is null)
            {
                // A bare word matches the title, which is what a person typing into
                // a search box means. A trailing wildcard adds nothing to *contains*.
                return Contains(PortalQuery.Field(item, "title"), Value.TrimEnd('*'));
            }

            if (string.Equals(Field, "ownerfolder", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(Field, "group", StringComparison.OrdinalIgnoreCase))
            {
                return Guid.TryParse(Value, out Guid group) && groups is not null && groups.Contains(group);
            }

            // `accountid` is ArcGIS's other name for the organisation an item belongs to — V-66.
            string field = string.Equals(Field, "accountid", StringComparison.OrdinalIgnoreCase) ? "orgid" : Field;

            bool prefix = Value.Length > 1 && Value[^1] == '*';
            string wanted = prefix ? Value[..^1] : Value;

            return field.ToLowerInvariant() is "tags" or "typekeywords"
                ? ContainsAny(PortalQuery.Field(item, field), wanted, prefix)
                : Same(PortalQuery.Field(item, field), wanted, prefix);
        }
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

    private static bool Same(object? actual, string wanted, bool prefix) =>
        actual?.ToString() is { } text
        && (prefix
            ? text.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
            : string.Equals(text, wanted, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(object? actual, string wanted) =>
        actual is not null
        && actual.ToString()?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true;

    private static bool ContainsAny(object? actual, string wanted, bool prefix) =>
        actual is IEnumerable<string> values && values.Any(value => Same(value, wanted, prefix));
}
