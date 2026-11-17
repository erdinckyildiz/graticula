using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Graticula.Host.Forms.Reports;

/// <summary>One statistic of <c>| stats:"type,field,outName"</c>: its type, the question or field, and the name it is read by.</summary>
internal sealed record ReportStatistic(string Type, string Field, string OutName);

/// <summary>
/// A map a placeholder draws — <c>${location}</c>, <c>${location | map:"…" | mapScale:… | size:…}</c>, <c>${$shape}</c>,
/// <c>${$map}</c> — ADR-301 §5.10. Its geometries are in EPSG:4326, as a report's records are read.
/// </summary>
/// <param name="Table">The table the geometries are records of (empty for the form's layer); empty with no records.</param>
/// <param name="ObjectIds">Those records' object ids, which with the table say which geometries are drawn.</param>
/// <param name="Shapes">The geometries, longitude and latitude.</param>
/// <param name="WebMap">The web map's item id; null or empty for the organisation's default basemap.</param>
/// <param name="Scale">The scale's denominator; 0 to fit the geometries.</param>
/// <param name="Extent"><c>mapExtent</c>: xmin, ymin, xmax, ymax and the wkid they are in; null when not given.</param>
/// <param name="Width">Pixels.</param>
/// <param name="Height">Pixels.</param>
/// <param name="CurrentLayer">Whether the records are drawn with their layer's own symbols (<c>drawingInfo:"currentLayer"</c>).</param>
internal sealed record ReportMapRequest(
    string Table,
    IReadOnlyList<long> ObjectIds,
    IReadOnlyList<Graticula.Geometries.Geometry> Shapes,
    string? WebMap,
    double Scale,
    IReadOnlyList<double>? Extent,
    int Width,
    int Height,
    bool CurrentLayer)
{
    /// <summary>What says two requests draw the same map.</summary>
    public string Key => string.Join('|',
        "map", Table, string.Join(',', ObjectIds.Select(i => i.ToString(CultureInfo.InvariantCulture))), WebMap ?? string.Empty,
        Scale.ToString("R", CultureInfo.InvariantCulture),
        Extent is null ? string.Empty : string.Join(',', Extent.Select(e => e.ToString("R", CultureInfo.InvariantCulture))),
        Width.ToString(CultureInfo.InvariantCulture), Height.ToString(CultureInfo.InvariantCulture), CurrentLayer ? "own" : "marker",
        Shapes.Count == 0 ? string.Empty : string.Join(';', Shapes.Select(s => s.Envelope is { IsEmpty: false } e
            ? string.Create(CultureInfo.InvariantCulture, $"{e.MinX:R},{e.MinY:R},{e.MaxX:R},{e.MaxY:R}") : "-")));
}

/// <summary>A map drawn: a PNG of the size asked.</summary>
internal sealed record ReportMap(byte[] Png);

/// <summary>
/// A query a placeholder asks of one of the form's tables — <c>where</c>, <c>stats</c>, <c>orderByFields</c>,
/// <c>resultRecordCount</c>, <c>outFields</c>, <c>returnDistinctValues</c> — read as Survey123's <i>Report queries</i> page
/// writes them, and answered through the layer's own query as the asker (ADR-301 §5.11).
/// </summary>
/// <param name="Table">The table's name: empty for the form's layer, else the repeat's.</param>
/// <param name="Where">The where clause, in the form's question names; null for none.</param>
/// <param name="Important">Whether <c>!important</c> was written: the whole layer, not only the records the report is made of.</param>
/// <param name="WithinIds">For the form's layer, the records the query is kept to; null with <c>!important</c>.</param>
/// <param name="WithinParents">For a repeat, the GlobalIDs of the records whose rows the query is kept to; null with <c>!important</c>.</param>
/// <param name="Statistics">The statistics; empty for rows.</param>
/// <param name="GroupBy">The fields grouped by, comma separated, in question names.</param>
/// <param name="Having">The having clause.</param>
/// <param name="OrderBy">The order, in question names.</param>
/// <param name="Count">The most rows.</param>
/// <param name="OutFields">The fields of distinct rows, comma separated.</param>
/// <param name="Distinct">Whether distinct values are asked for.</param>
internal sealed record ReportQueryRequest(
    string Table,
    string? Where,
    bool Important,
    IReadOnlyList<long>? WithinIds,
    IReadOnlyList<Guid>? WithinParents,
    IReadOnlyList<ReportStatistic> Statistics,
    string? GroupBy,
    string? Having,
    string? OrderBy,
    int? Count,
    string? OutFields,
    bool Distinct)
{
    /// <summary>Whether it answers rows of values (statistics or distinct values), not records.</summary>
    public bool AnswersRows => Statistics.Count > 0 || (Distinct && OutFields is { Length: > 0 });

    public string Key
    {
        get
        {
            StringBuilder key = new("query|");
            key.Append(Table).Append('|').Append(Where).Append('|').Append(Important ? '!' : '-').Append('|');
            key.AppendJoin(',', WithinIds ?? []).Append('|').AppendJoin(',', WithinParents ?? []).Append('|');
            key.AppendJoin(';', Statistics.Select(s => $"{s.Type},{s.Field},{s.OutName}")).Append('|');
            key.Append(GroupBy).Append('|').Append(Having).Append('|').Append(OrderBy).Append('|');
            key.Append(Count?.ToString(CultureInfo.InvariantCulture)).Append('|').Append(OutFields).Append('|').Append(Distinct);
            return key.ToString();
        }
    }
}

/// <summary>What a query answers: records (with their repeats and the files the template reads), or rows of values.</summary>
internal sealed record ReportQueryResult(IReadOnlyList<ReportRecord> Records, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>
/// What a report reads beside the records it is made of: maps and queries. The engine is synchronous and makes no request
/// of its own; a fill asks this for what it needs, and what it does not have yet is fetched and the fill run again
/// (<see cref="ReportPrefetch"/>).
/// </summary>
internal interface IReportLookups
{
    /// <summary>The map, or null when it has not been drawn yet.</summary>
    ReportMap? Map(ReportMapRequest request);

    /// <summary>The answer, or null when it has not been asked yet.</summary>
    ReportQueryResult? Query(ReportQueryRequest request);
}

/// <summary>
/// The lookups a report is filled from: what has been fetched, by key, and what a fill asked for that has not — which the
/// endpoint fetches, as the asker, before filling again. A fill with nothing missing is the report.
/// </summary>
internal sealed class ReportPrefetch : IReportLookups
{
    private readonly Dictionary<string, ReportMap> _maps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportQueryResult> _queries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportMapRequest> _missingMaps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReportQueryRequest> _missingQueries = new(StringComparer.Ordinal);

    /// <summary>What the report says it could not do in full — a basemap absent, a layer left out — once each.</summary>
    public SortedSet<string> Notes { get; } = new(StringComparer.Ordinal);

    public IReadOnlyCollection<ReportMapRequest> MissingMaps => _missingMaps.Values;

    public IReadOnlyCollection<ReportQueryRequest> MissingQueries => _missingQueries.Values;

    public bool Complete => _missingMaps.Count == 0 && _missingQueries.Count == 0;

    public int MapsDrawn => _maps.Count;

    public ReportMap? Map(ReportMapRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_maps.TryGetValue(request.Key, out ReportMap? map))
        {
            return map;
        }

        _missingMaps.TryAdd(request.Key, request);
        return null;
    }

    public ReportQueryResult? Query(ReportQueryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_queries.TryGetValue(request.Key, out ReportQueryResult? result))
        {
            return result;
        }

        _missingQueries.TryAdd(request.Key, request);
        return null;
    }

    public void Add(ReportMapRequest request, ReportMap map)
    {
        ArgumentNullException.ThrowIfNull(request);
        _maps[request.Key] = map;
        _missingMaps.Remove(request.Key);
    }

    public void Add(ReportQueryRequest request, ReportQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        _queries[request.Key] = result;
        _missingQueries.Remove(request.Key);
    }

    /// <summary>Forgets what was missing, before a fill that asks again.</summary>
    public void ClearMissing()
    {
        _missingMaps.Clear();
        _missingQueries.Clear();
    }
}

/// <summary>Reading <c>| stats</c>, <c>| where</c> and the map methods' arguments — Survey123's <i>Report queries</i> and <i>Map expressions</i> pages.</summary>
internal static class ReportQueries
{
    /// <summary>The statistic types Survey123's <i>Report queries</i> page lists.</summary>
    public static readonly IReadOnlySet<string> StatisticTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "avg", "count", "sum", "stddev", "min", "max", "var", "range", "first", "last", "median",
    };

    /// <summary>The methods that ask a query of a table.</summary>
    public static readonly IReadOnlySet<string> QueryMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "where", "stats", "orderByFields", "resultRecordCount", "outFields", "returnDistinctValues",
    };

    /// <summary>The methods a map takes.</summary>
    public static readonly IReadOnlySet<string> MapMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "map", "mapScale", "mapExtent", "size", "drawingInfo", "where", "orderByFields", "resultRecordCount",
    };

    /// <summary>The default size of a map with no <c>| size</c>, in pixels (INFERRED: the size Survey123's examples write).</summary>
    public const int DefaultMapWidth = 400;

    /// <summary>The default height of a map, in pixels (INFERRED).</summary>
    public const int DefaultMapHeight = 300;

    /// <summary>The largest side of a map in a report, in pixels (INFERRED).</summary>
    public const int MaximumMapSide = 2000;

    /// <summary>
    /// Reads <c>"sum,pop2000,populationTotal;avg,income"</c>: statistics separated by <c>;</c>, each a type, a field and,
    /// if wanted, the name it is read by — <c>type_field</c> when none is given (INFERRED; the page names none).
    /// </summary>
    /// <exception cref="ReportSyntaxException">The text is not statistics.</exception>
    public static IReadOnlyList<ReportStatistic> Statistics(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ReportSyntaxException("'| stats' takes the statistic and the field, as stats:\"sum,amount\" or stats:\"count,objectid,total\".");
        }

        List<ReportStatistic> found = [];

        foreach (string one in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = one.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length is < 2 or > 3 || parts.Any(p => p.Length == 0))
            {
                throw new ReportSyntaxException($"'{one}' in '| stats' is a statistic, a field and, if wanted, a name for the result, separated by commas: \"sum,amount,total\".");
            }

            if (!StatisticTypes.Contains(parts[0]))
            {
                throw new ReportSyntaxException($"'{parts[0]}' is not a statistic: the statistics are {string.Join(", ", StatisticTypes.Order(StringComparer.Ordinal))}.");
            }

            string outName = parts.Length == 3 ? parts[2] : $"{parts[0].ToLowerInvariant()}_{parts[1]}";

            if (!outName.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                throw new ReportSyntaxException($"'{outName}' is not a name a statistic can be read by: letters, digits and '_'.");
            }

            found.Add(new ReportStatistic(parts[0].ToLowerInvariant(), parts[1], outName));
        }

        if (found.Count == 0)
        {
            throw new ReportSyntaxException("'| stats' names no statistic.");
        }

        return found;
    }

    /// <summary>A where clause with <c>!important</c> taken off its end, and whether it was there.</summary>
    public static (string Where, bool Important) Important(string where)
    {
        ArgumentNullException.ThrowIfNull(where);
        string trimmed = where.TrimEnd();

        return trimmed.EndsWith("!important", StringComparison.OrdinalIgnoreCase)
            ? (trimmed[..^"!important".Length].TrimEnd(), true)
            : (trimmed, false);
    }
}
