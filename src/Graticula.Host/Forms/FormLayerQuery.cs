using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;

namespace Graticula.Host.Forms;

/// <summary>
/// A select question's <c>search()</c> appearance, read: where its choices come from (a CSV uploaded with the form, or a
/// feature layer of this server), and the search and filter it narrows them by — ADR-287 §5.23.
/// </summary>
/// <param name="Table">The first argument as written: <c>'fruits'</c> or <c>"name?url=…&amp;where=…"</c>.</param>
/// <param name="File">For a CSV, its file name with <c>.csv</c>; null for a layer.</param>
/// <param name="Url">For a layer, its address as written, without the parameters that follow it; null for a CSV.</param>
/// <param name="Query">For a layer, the query parameters written after the address (<c>where</c>, <c>orderByFields</c>, …).</param>
/// <param name="SearchType"><c>matches</c>, <c>contains</c>, <c>startswith</c>, <c>endswith</c> or a spatial type; null when there is no search.</param>
/// <param name="SearchColumn">The column or columns (comma-separated) the search reads.</param>
/// <param name="SearchText">The search text: an expression, evaluated by the client.</param>
/// <param name="FilterColumn">The column the filter reads, or null.</param>
/// <param name="FilterText">The filter value: an expression, evaluated by the client.</param>
public sealed record FormSearch(
    string Table,
    string? File,
    string? Url,
    IReadOnlyDictionary<string, string> Query,
    string? SearchType,
    string? SearchColumn,
    string? SearchText,
    string? FilterColumn,
    string? FilterText)
{
    /// <summary>Whether the choices come from a feature layer.</summary>
    public bool IsLayer => Url is not null;
}

/// <summary>A literal layer address a form queries, with where it is written; checked against the catalogue at publish.</summary>
public sealed record QueriedLayer(string Sheet, int Row, string Column, string Url, SubmissionAddress Address);

/// <summary>
/// Reads <c>search()</c> and the layer addresses <c>search()</c> and <c>pulldata("@layer")</c> name — ADR-287 §5.23.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two public sources, one reading.</b> xlsform.org's <i>Dynamic selects from pre-loaded data</i> gives the CSV form,
/// <c>search(csv[, type, column, text[, filterColumn, filterText]])</c>; Survey123's <i>Multiple choice</i> page gives the
/// same arguments with a layer for the table, written <c>"name?url=&lt;layer&gt;"</c>, and the spatial search types.
/// </para>
/// <para>
/// <b>A layer is this server's or it is refused.</b> The address is read as <see cref="SubmissionTarget"/> reads a
/// <c>submission_url</c>, and must name a layer (<c>/FeatureServer/{n}</c>); whether its host is this server's, and
/// whether the layer is there for the publisher to read, needs the request and the catalogue, and is asked at publish.
/// </para>
/// </remarks>
public static class FormLayerQuery
{
    /// <summary>The search types over a column's text (xlsform.org, and Survey123 for a non-spatial table).</summary>
    public static readonly FrozenSet<string> TextSearchTypes = new[] { "matches", "contains", "startswith", "endswith" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The spatial search types Survey123 documents for a layer, each with the ArcGIS REST <c>spatialRel</c> it asks for.
    /// <c>index_intercepts</c> is spelled so on Survey123's page and is read as <c>index_intersects</c>.
    /// </summary>
    public static readonly FrozenDictionary<string, string> SpatialSearchTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["intersects"] = "esriSpatialRelIntersects",
        ["contains"] = "esriSpatialRelContains",
        ["crosses"] = "esriSpatialRelCrosses",
        ["envelope_intersects"] = "esriSpatialRelEnvelopeIntersects",
        ["index_intersects"] = "esriSpatialRelIndexIntersects",
        ["index_intercepts"] = "esriSpatialRelIndexIntersects",
        ["overlaps"] = "esriSpatialRelOverlaps",
        ["touches"] = "esriSpatialRelTouches",
        ["within"] = "esriSpatialRelWithin",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The query parameters a <c>search()</c> layer may carry (INFERRED, §5.23: Survey123 shows where and resultRecordCount).</summary>
    public static readonly FrozenSet<string> SearchParameters = new[]
    {
        "where", "orderByFields", "resultRecordCount", "resultOffset", "returnDistinctValues", "distance", "units", "time",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The query parameters a <c>pulldata("@layer")</c> address may carry (INFERRED, §5.23).</summary>
    public static readonly FrozenSet<string> PulldataParameters = new[]
    {
        "where", "orderByFields", "resultOffset", "outStatistics", "groupByFieldsForStatistics", "having", "distance", "units",
        "time", "returnDistinctValues", "spatialRel", "objectIds",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The parameters Survey123's <i>Query a feature layer</i> says <c>pulldata("@layer")</c> does not take, and <c>token</c>,
    /// which no form here carries, each with the reason.
    /// </summary>
    public static readonly FrozenDictionary<string, string> RefusedParameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["f"] = "the client asks for JSON itself",
        ["outFields"] = "the client asks for the fields it needs itself",
        ["outSR"] = "the client asks for WGS 84 itself, so a location comes back as a geopoint can hold it",
        ["resultRecordCount"] = "pulldata(\"@layer\") gives the first record, so the client asks for one",
        ["returnCountOnly"] = "pulldata(\"@layer\") gives a record or a value of one, not a count; use outStatistics",
        ["returnGeometry"] = "the client asks for the geometry when the call reads it",
        ["returnIDsOnly"] = "pulldata(\"@layer\") gives a record or a value of one, not a list of ids",
        ["token"] = "a credential is never written into a form: the query goes with the signed-in member's own",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>pulldata("@layer")</c> operations, with the fewest and most arguments each takes, the source included.</summary>
    public static readonly FrozenDictionary<string, (int Fewest, int Most)> Operations = new Dictionary<string, (int, int)>(StringComparer.Ordinal)
    {
        ["getRecord"] = (3, 4),
        ["getValue"] = (4, 5),
        ["getRecordAt"] = (4, 5),
        ["getValueAt"] = (5, 6),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Finds <c>search(…)</c> in an appearance cell. Gives the text between its parentheses and the rest of the cell, the
    /// other appearances, with it taken out.
    /// </summary>
    /// <returns>False when the cell has no <c>search(</c>; true otherwise, with <paramref name="error"/> set when it is malformed.</returns>
    public static bool TryExtract(string appearance, out string inner, out string rest, out string? error)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        inner = string.Empty;
        rest = appearance;
        error = null;
        System.Text.RegularExpressions.Match found = SearchStart.Match(appearance);

        if (!found.Success)
        {
            return false;
        }

        int start = found.Index;
        int open = appearance.IndexOf('(', start);
        int depth = 0;
        char quote = '\0';

        for (int i = open; i < appearance.Length; i++)
        {
            char c = appearance[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                inner = appearance[(open + 1)..i];
                rest = (appearance[..start] + " " + appearance[(i + 1)..]).Trim();

                if (TryExtract(rest, out _, out _, out _))
                {
                    error = "An appearance holds one search(); this one has two.";
                }

                return true;
            }
        }

        error = quote != '\0' ? "search(…) has a string that is never closed." : "search(…) is never closed with ')'.";
        return true;
    }

    private static readonly System.Text.RegularExpressions.Regex SearchStart = new(@"(?<![\w\-:])search\s*\(",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A call's arguments as written, split at commas outside quotes and brackets, each trimmed.</summary>
    public static List<string> Arguments(string inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        List<string> arguments = [];
        int depth = 0;
        int start = 0;
        char quote = '\0';

        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }

        string last = inner[start..].Trim();

        if (last.Length > 0 || arguments.Count > 0)
        {
            arguments.Add(last);
        }

        return arguments;
    }

    /// <summary>The text of a string literal argument, or null when the argument is anything else.</summary>
    public static string? Literal(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        string t = argument.Trim();
        return t.Length >= 2 && t[0] is '\'' or '"' && t[^1] == t[0] && t.IndexOf(t[0], 1) == t.Length - 1 ? t[1..^1] : null;
    }

    /// <summary>Reads a <c>search()</c>'s arguments.</summary>
    /// <param name="inner">The text between its parentheses.</param>
    /// <param name="search">The search, when it is one.</param>
    /// <param name="error">Why it is not.</param>
    public static bool TryParseSearch(string inner, out FormSearch? search, out string? error)
    {
        search = null;
        List<string> args = Arguments(inner);

        if (args.Count is not (1 or 4 or 6))
        {
            error = $"search() takes 1, 4 or 6 arguments — search('list'), search('list', 'matches', 'column', ${{q}}) or with a filter column and value after those — not {args.Count}.";
            return false;
        }

        if (Literal(args[0]) is not { Length: > 0 } table)
        {
            error = "search()'s first argument is the CSV's name or the layer's, written in quotes.";
            return false;
        }

        string? file = null;
        string? url = null;
        Dictionary<string, string> query = new(StringComparer.OrdinalIgnoreCase);
        int mark = table.IndexOf('?', StringComparison.Ordinal);

        if (mark >= 0)
        {
            if (!TrySplitTable(table[(mark + 1)..], out url, query, out error))
            {
                return false;
            }

            if (mark == 0)
            {
                error = $"'{table}' names no table before '?url='; Survey123 writes a name for the layer there, as in \"places?url=…\".";
                return false;
            }

            if (!TryLayer(url!, query, SearchParameters, out _, out error))
            {
                return false;
            }
        }
        else
        {
            if (table.Contains('/', StringComparison.Ordinal) || table.Contains('\\', StringComparison.Ordinal) || table.Contains(':', StringComparison.Ordinal))
            {
                error = $"'{table}' is not the name of a CSV uploaded with the form: it is the file's name alone, as search('fruits') reads fruits.csv. A layer is written \"name?url=<its address>\".";
                return false;
            }

            file = table.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? table : table + ".csv";
        }

        string? type = null, column = null, text = null, filterColumn = null, filterText = null;

        if (args.Count >= 4)
        {
            type = Literal(args[1]);

            if (type is null)
            {
                error = "search()'s second argument is the kind of search, in quotes: 'matches', 'contains', 'startswith' or 'endswith'.";
                return false;
            }

            if (!TextSearchTypes.Contains(type) && !(url is not null && SpatialSearchTypes.ContainsKey(type)))
            {
                error = url is null && SpatialSearchTypes.ContainsKey(type)
                    ? $"'{type}' is a spatial search, and a CSV has no geometry; it is for a layer."
                    : $"'{type}' is not a kind of search: it is matches, contains, startswith or endswith{(url is null ? string.Empty : ", or for a layer with geometry " + string.Join(", ", SpatialSearchTypes.Keys.Where(k => k != "index_intercepts").Order(StringComparer.Ordinal)))}.";
                return false;
            }

            column = Literal(args[2]);

            if (column is null)
            {
                error = "search()'s third argument is the column to search, in quotes.";
                return false;
            }

            text = args[3];
        }

        if (args.Count == 6)
        {
            filterColumn = Literal(args[4]);

            if (filterColumn is not { Length: > 0 })
            {
                error = "search()'s fifth argument is the column to filter by, in quotes.";
                return false;
            }

            filterText = args[5];
        }

        search = new FormSearch(table, file, url, query, type, column, text, filterColumn, filterText);
        error = null;
        return true;
    }

    /// <summary>
    /// Splits <c>url=&lt;address&gt;&amp;where=…&amp;…</c>, the part of a search() table after its name. Values may be
    /// percent-encoded, as Survey123 asks for its web app, and are decoded; a '+' stays a '+'.
    /// </summary>
    private static bool TrySplitTable(string text, out string? url, Dictionary<string, string> query, out string? error)
    {
        url = null;

        foreach (string pair in text.Split('&'))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                error = $"'{pair}' is not a parameter: a layer is written \"name?url=<address>\", with &where=… and the like after it.";
                return false;
            }

            string key = pair[..equals].Trim();
            string value = Decode(pair[(equals + 1)..]);

            if (key.Equals("url", StringComparison.OrdinalIgnoreCase))
            {
                url = value.Trim();
            }
            else
            {
                query[key] = value;
            }
        }

        if (url is not { Length: > 0 })
        {
            error = "A search() over a layer names it with url=: \"name?url=<the layer's address>\".";
            return false;
        }

        error = null;
        return true;
    }

    private static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>
    /// Reads a layer address, and any parameters written after it with '?', and checks each parameter against the ones
    /// allowed. The address must name a layer of a feature service: <c>…/FeatureServer/{n}</c>.
    /// </summary>
    /// <param name="url">The address, with or without parameters after '?'.</param>
    /// <param name="query">Parameters already read (a search()'s); those after '?' are added.</param>
    /// <param name="allowed">The parameters accepted.</param>
    /// <param name="address">The address read.</param>
    /// <param name="error">Why it is refused.</param>
    public static bool TryLayer(string url, Dictionary<string, string> query, IReadOnlySet<string> allowed,
        out SubmissionAddress? address, out string? error)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(query);
        address = null;
        int mark = url.IndexOf('?', StringComparison.Ordinal);

        if (mark >= 0)
        {
            foreach (string pair in url[(mark + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                query[equals < 0 ? pair : pair[..equals]] = equals < 0 ? string.Empty : Decode(pair[(equals + 1)..]);
            }
        }

        foreach (string key in query.Keys)
        {
            if (RefusedParameters.TryGetValue(key, out string? why) && !allowed.Contains(key))
            {
                error = $"The query parameter '{key}' is refused: {why}.";
                return false;
            }

            if (!allowed.Contains(key))
            {
                error = $"'{key}' is not a query parameter a form may add here; the ones it may are {string.Join(", ", allowed.Order(StringComparer.OrdinalIgnoreCase))}.";
                return false;
            }
        }

        string path = mark >= 0 ? url[..mark] : url;

        if (!SubmissionTarget.TryParse(path, out address, out error))
        {
            error = error!.Replace("submission_url", "layer address", StringComparison.Ordinal);
            return false;
        }

        if (address!.Layer is null)
        {
            error = $"'{path}' names a feature service and not one of its layers; add the layer's number, as in …/FeatureServer/0.";
            address = null;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether an address names the host that answered the request, said in words when it does not — the same rule a
    /// <c>submission_url</c> is held to (§5.17): a form here queries this server's layers, and no other's.
    /// </summary>
    /// <returns>Null when it is this server's; the refusal otherwise.</returns>
    public static string? Foreign(SubmissionAddress address, string url, string host, string scheme)
    {
        ArgumentNullException.ThrowIfNull(address);
        return SubmissionTarget.IsOwn(address, host, scheme)
            ? null
            : $"'{url}' is on {address.Authority}, and this server answers at {host}. A form queries layers of the server that publishes it, with the member's own sign-in; a layer of another server is not queried from here. Publish that layer here, or use a CSV uploaded with the form.";
    }
}
