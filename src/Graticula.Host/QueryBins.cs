using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Graticula.Api.ArcGis;
using Graticula.Catalog;
using Graticula.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Graticula.Host;

/// <summary>What kind of bins a <c>queryBins</c> request asks for.</summary>
internal enum BinKind
{
    /// <summary><c>fixedIntervalBin</c>: equal bins of a declared size.</summary>
    FixedInterval,

    /// <summary><c>autoIntervalBin</c>: a declared number of equal bins over a range.</summary>
    AutoInterval,

    /// <summary><c>fixedBoundariesBin</c>: bins between declared boundaries.</summary>
    FixedBoundaries,

    /// <summary><c>dateBin</c>: calendar or clock bins of a date field.</summary>
    Date,
}

/// <summary>A refusal of a <c>queryBins</c> request that is the caller's to fix, said as a 400.</summary>
internal sealed class BinRefusedException(string message) : Exception(message);

/// <summary>
/// A parsed <c>queryBins</c> request — what is binned, how, and how the answer is written.
/// </summary>
/// <remarks>
/// ADR-309 §4. Parsed here and answered by <see cref="QueryBins"/>; the filters (<c>where</c>, <c>geometry</c> and the
/// rest) and <c>outStatistics</c> are the layer query's own, so a bin counts exactly the features a query would return.
/// </remarks>
internal sealed class BinRequest
{
    /// <summary>The kind of bin.</summary>
    public required BinKind Kind { get; init; }

    /// <summary>The binned column, as the layer names it.</summary>
    public required string Column { get; init; }

    /// <summary>Its type.</summary>
    public required FieldType ColumnType { get; init; }

    /// <summary>Whether the answer is newest or largest first.</summary>
    public bool Descending { get; init; }

    /// <summary>Whether <c>upperBoundary</c> is left out.</summary>
    public bool HideUpperBound { get; init; }

    /// <summary>What <c>lowerBoundary</c> is called.</summary>
    public string LowerName { get; init; } = "lowerBoundary";

    /// <summary>What <c>upperBoundary</c> is called.</summary>
    public string UpperName { get; init; } = "upperBoundary";

    /// <summary>The start, a number or epoch milliseconds, when the caller named one.</summary>
    public double? Start { get; init; }

    /// <summary>The end, a number or epoch milliseconds, when the caller named one.</summary>
    public double? End { get; init; }

    /// <summary>The bin size of a fixed interval.</summary>
    public double Interval { get; init; }

    /// <summary>The count of an auto interval.</summary>
    public int NumberOfBins { get; init; }

    /// <summary>The boundaries of a fixed-boundaries bin, ascending.</summary>
    public IReadOnlyList<double> Boundaries { get; init; } = [];

    /// <summary>A date bin's unit: year, quarter, month, week, day, hour, minute or second.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>How many units a date bin spans.</summary>
    public int Number { get; init; } = 1;

    /// <summary>A date bin's offset, in milliseconds.</summary>
    public long OffsetMilliseconds { get; init; }

    /// <summary>The ISO day a week begins on, 1 (Monday) to 7 (Sunday).</summary>
    public int FirstDayOfWeek { get; init; } = 7;

    /// <summary>Whether bins start at the first value rather than a calendar boundary.</summary>
    public bool SnapToFirst { get; init; }

    /// <summary>Whether the first and last bin keep the full interval rather than stopping at the data.</summary>
    public bool FullIntervalBin { get; init; }
}

/// <summary>
/// <c>queryBins</c>: a layer's features divided into bins of a numeric or date field, with statistics per bin
/// (ADR-309 §4).
/// </summary>
/// <remarks>
/// <para>
/// <b>Built on the layer's own statistics query.</b> A bin is a group whose key is the bin's index, written as an
/// expression of the column (<see cref="GroupExpression"/>, the same machinery <c>groupByFieldsForStatistics</c> uses for an
/// expression), so the filters, the budget and the provider are the query's, and a count here cannot disagree with a
/// <c>returnCountOnly</c> over the same features. The boundaries are computed here from the index, the gaps between the
/// first and last bin are filled with empty bins as the reference's own example shows (a bin with a count of 0 and a null
/// average), and nothing a caller wrote reaches SQL: the column is one of the layer's, every number is a bound parameter.
/// </para>
/// <para>
/// <b>What is read, from the reference at developers.arcgis.com (Query Bins).</b> <c>bin</c> with <c>dateBin</c>,
/// <c>fixedIntervalBin</c>, <c>autoIntervalBin</c> and <c>fixedBoundariesBin</c>, <c>binOrder</c>, <c>hideUpperBound</c>,
/// <c>firstDayOfWeek</c>, <c>snapToData: first</c>, <c>returnFullIntervalBin</c>, the two boundary aliases, <c>where</c>,
/// <c>geometry</c> and <c>outStatistics</c> (default: a <c>frequency</c> count). <b>Refused by name, not ignored:</b>
/// <c>lodBin</c>, <c>classificationBin</c>, <c>stackBy</c>, <c>splitBy</c>, <c>transformation</c>, normalization,
/// <c>snapToData: last</c>, <c>async</c>, <c>returnCentroid</c> and the spatial-aggregate statistics.
/// </para>
/// </remarks>
internal static class QueryBins
{
    /// <summary>The most bins one answer holds, empty ones included.</summary>
    private const int MostBins = 10_000;

    /// <summary>Parameters passed on to the layer query's own parser.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "where", "geometry", "geometryType", "inSR", "outSR", "defaultSR", "spatialRel", "relationParam", "distance", "units",
        "outStatistics", "outTimeReference", "datumTransformation", "f", "token", "callback", "lang", "locale",
        "_", "_ts", "dojo.preventCache",
    };

    /// <summary>Parameters queryBins reads itself.</summary>
    private static readonly HashSet<string> Own = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "binOrder", "lowerBoundaryAlias", "upperBoundaryAlias", "async",
    };

    private static readonly Dictionary<string, long> FixedUnits = new(StringComparer.Ordinal)
    {
        ["second"] = 1_000L,
        ["minute"] = 60_000L,
        ["hour"] = 3_600_000L,
        ["day"] = 86_400_000L,
        ["week"] = 7 * 86_400_000L,
    };

    private static readonly Dictionary<string, int> MonthUnits = new(StringComparer.Ordinal)
    {
        ["month"] = 1,
        ["quarter"] = 3,
        ["year"] = 12,
    };

    /// <summary>
    /// Reads the request, and the parameters the layer query is to be given in place of the whole set.
    /// </summary>
    /// <param name="all">What the caller sent.</param>
    /// <param name="fields">The layer's fields.</param>
    /// <param name="objectIdColumn">The layer's identity column, which the default <c>frequency</c> counts.</param>
    /// <param name="request">The bins.</param>
    /// <param name="forQuery">The shared parameters, with <c>outStatistics</c> filled in.</param>
    /// <param name="error">Why the request is refused.</param>
    /// <returns>Whether it could be read.</returns>
    public static bool TryRead(
        IQueryCollection all,
        IReadOnlyList<FieldDescription> fields,
        string objectIdColumn,
        out BinRequest? request,
        out IQueryCollection? forQuery,
        out string? error)
    {
        request = null;
        forQuery = null;
        error = null;

        Dictionary<string, StringValues> shared = new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, StringValues> parameter in all)
        {
            if (Shared.Contains(parameter.Key))
            {
                shared[parameter.Key] = parameter.Value;
                continue;
            }

            if (Own.Contains(parameter.Key))
            {
                continue;
            }

            error = parameter.Key.ToUpperInvariant() switch
            {
                "RETURNCENTROID" or "QUANTIZATIONPARAMETERS" =>
                    $"'{parameter.Key}' belongs to spatial aggregates and generalised geometry, which queryBins here does not answer. "
                    + "It is refused rather than ignored.",
                _ => $"'{parameter.Key}' is not a queryBins parameter this server reads, and it is refused rather than ignored.",
            };
            return false;
        }

        if (all["async"].ToString().Trim() is { Length: > 0 } asynchronous
            && !string.Equals(asynchronous, "false", StringComparison.OrdinalIgnoreCase))
        {
            error = "'async' is refused: queryBins is answered in the request here (queryBinsCapabilities.supportsAsync is false).";
            return false;
        }

        foreach (string? format in all["f"])
        {
            string given = (format ?? string.Empty).Trim();

            if (given.Length > 0
                && !given.Equals("json", StringComparison.OrdinalIgnoreCase)
                && !given.Equals("pjson", StringComparison.OrdinalIgnoreCase)
                && !given.Equals("html", StringComparison.OrdinalIgnoreCase))
            {
                error = $"'f={given}' is not produced by queryBins here, which answers json.";
                return false;
            }
        }

        // The format queryBins is answered in is json whatever f says, so the layer query is asked for nothing else.
        shared.Remove("f");

        string binText = all["bin"].ToString().Trim();

        if (binText.Length == 0)
        {
            error = "'bin' is required: a JSON object with type, onField and parameters.";
            return false;
        }

        try
        {
            request = ReadBin(binText, all, fields);
        }
        catch (BinRefusedException refused)
        {
            error = refused.Message;
            return false;
        }

        // The default is a histogram: a count under the name `frequency`.
        if (shared.TryGetValue("outStatistics", out StringValues given2) && given2.ToString().Trim().Length > 0)
        {
            shared["outStatistics"] = given2;
        }
        else
        {
            shared["outStatistics"] = JsonSerializer.Serialize(new[]
            {
                new Dictionary<string, string>
                {
                    ["statisticType"] = "count",
                    ["onStatisticField"] = objectIdColumn,
                    ["outStatisticFieldName"] = "frequency",
                },
            });
        }

        forQuery = new QueryCollection(shared);
        return true;
    }

    private static BinRequest ReadBin(string text, IQueryCollection all, IReadOnlyList<FieldDescription> fields)
    {
        JsonElement root;

        try
        {
            root = JsonDocument.Parse(text).RootElement;
        }
        catch (JsonException e)
        {
            throw new BinRefusedException($"'bin' is not valid JSON: {e.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new BinRefusedException("'bin' must be a JSON object with type, onField and parameters.");
        }

        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (property.Name is not ("type" or "onField" or "parameters" or "hideUpperBound" or "firstDayOfWeek"))
            {
                throw new BinRefusedException(
                    $"'bin.{property.Name}' is not supported by queryBins here (stackBy, splitBy, transformation, onExpression and "
                    + "normalization are not answered), and it is refused rather than ignored: it changes what the bins are.");
            }
        }

        string type = Str(root, "type");

        if (type is "lodBin" or "classificationBin")
        {
            throw new BinRefusedException(
                $"'bin.type' of '{type}' is not supported by queryBins here: "
                + (type == "lodBin"
                    ? "lodBin is new at 12.2 and the public reference does not yet describe its parameters, so it is refused rather than guessed. "
                    : "classificationBin needs the stacked-attribute machinery this server does not have. ")
                + "Use dateBin, fixedIntervalBin, autoIntervalBin or fixedBoundariesBin.");
        }

        BinKind kind = type switch
        {
            "dateBin" => BinKind.Date,
            "fixedIntervalBin" => BinKind.FixedInterval,
            "autoIntervalBin" => BinKind.AutoInterval,
            "fixedBoundariesBin" => BinKind.FixedBoundaries,
            _ => throw new BinRefusedException(
                $"'bin.type' of '{type}' is not a bin type: use dateBin, fixedIntervalBin, autoIntervalBin or fixedBoundariesBin."),
        };

        string onField = Str(root, "onField");
        FieldDescription? field = fields.Cast<FieldDescription?>()
            .FirstOrDefault(f => string.Equals(f!.Value.Name, onField, StringComparison.OrdinalIgnoreCase));

        if (field is null)
        {
            throw new BinRefusedException($"'bin.onField' names '{onField}', which is not a field of this layer.");
        }

        FieldType columnType = field.Value.Type;
        bool numeric = columnType is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger or FieldType.Single or FieldType.Double;

        if (kind == BinKind.Date ? columnType != FieldType.Date : !numeric)
        {
            throw new BinRefusedException(
                kind == BinKind.Date
                    ? $"'dateBin' bins a date field, and '{field.Value.Name}' is not one."
                    : $"'{type}' bins a numeric field, and '{field.Value.Name}' is not one. A date field takes dateBin.");
        }

        JsonElement parameters = root.TryGetProperty("parameters", out JsonElement given) && given.ValueKind == JsonValueKind.Object
            ? given
            : throw new BinRefusedException("'bin.parameters' is required and is a JSON object.");

        bool hideUpper = root.TryGetProperty("hideUpperBound", out JsonElement hide) && hide.ValueKind == JsonValueKind.True;
        int firstDay = 7;

        if (root.TryGetProperty("firstDayOfWeek", out JsonElement first))
        {
            if (first.ValueKind != JsonValueKind.Number || !first.TryGetInt32(out firstDay) || firstDay is < 1 or > 7)
            {
                throw new BinRefusedException("'bin.firstDayOfWeek' is 1 (Monday) to 7 (Sunday).");
            }
        }

        string order = all["binOrder"].ToString().Trim();

        if (order.Length > 0 && !order.Equals("ASC", StringComparison.OrdinalIgnoreCase) && !order.Equals("DESC", StringComparison.OrdinalIgnoreCase))
        {
            throw new BinRefusedException("'binOrder' is ASC or DESC.");
        }

        string lowerName = Alias(all["lowerBoundaryAlias"].ToString(), "lowerBoundary");
        string upperName = Alias(all["upperBoundaryAlias"].ToString(), "upperBoundary");

        BinRequest request = new()
        {
            Kind = kind,
            Column = field.Value.Name,
            ColumnType = columnType,
            Descending = order.Equals("DESC", StringComparison.OrdinalIgnoreCase),
            HideUpperBound = hideUpper,
            LowerName = lowerName,
            UpperName = upperName,
            FirstDayOfWeek = firstDay,
        };

        switch (kind)
        {
            case BinKind.FixedInterval:
                Only(parameters, "interval", "start", "end");
                return Copy(request, start: Opt(parameters, "start"), end: Opt(parameters, "end"),
                    interval: Req(parameters, "interval", positive: true));

            case BinKind.AutoInterval:
                Only(parameters, "numberOfBins", "start", "end");
                double bins = Req(parameters, "numberOfBins", positive: true);

                if (bins != Math.Floor(bins) || bins > MostBins)
                {
                    throw new BinRefusedException($"'numberOfBins' is a whole number of at most {MostBins}.");
                }

                return Copy(request, start: Opt(parameters, "start"), end: Opt(parameters, "end"), numberOfBins: (int)bins);

            case BinKind.FixedBoundaries:
            {
                Only(parameters, "boundaries");

                if (!parameters.TryGetProperty("boundaries", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                {
                    throw new BinRefusedException("'boundaries' is required and is an array of numbers.");
                }

                List<double> boundaries = [];

                foreach (JsonElement item in list.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Number)
                    {
                        throw new BinRefusedException("'boundaries' holds numbers only.");
                    }

                    boundaries.Add(item.GetDouble());
                }

                if (boundaries.Count < 2 || boundaries.Count > MostBins + 1
                    || boundaries.Zip(boundaries.Skip(1), (a, b) => b <= a).Any(bad => bad))
                {
                    throw new BinRefusedException(
                        $"'boundaries' needs at least two numbers in ascending order, and at most {MostBins + 1}.");
                }

                return Copy(request, boundaries: boundaries);
            }

            default:
                return ReadDate(request, parameters);
        }
    }

    private static BinRequest ReadDate(BinRequest request, JsonElement parameters)
    {
        Only(parameters, "unit", "number", "offset", "start", "end", "snapToData", "returnFullIntervalBin");

        string unit = Str(parameters, "unit");

        if (!FixedUnits.ContainsKey(unit) && !MonthUnits.ContainsKey(unit))
        {
            throw new BinRefusedException("'unit' is year, quarter, month, week, day, hour, minute or second.");
        }

        double number = parameters.TryGetProperty("number", out _) ? Req(parameters, "number", positive: true) : 1;

        if (number != Math.Floor(number) || number > 100_000)
        {
            throw new BinRefusedException("'number' is a positive whole number.");
        }

        long offset = 0;

        if (parameters.TryGetProperty("offset", out JsonElement offsetElement))
        {
            if (offsetElement.ValueKind != JsonValueKind.Object)
            {
                throw new BinRefusedException("'offset' is an object: {\"number\": 8, \"unit\": \"hour\"}.");
            }

            Only(offsetElement, "number", "unit");
            double offsetNumber = Req(offsetElement, "number", positive: false);
            string offsetUnit = Str(offsetElement, "unit");

            if (offsetUnit is not ("second" or "minute" or "hour" or "day") || offsetNumber != Math.Floor(offsetNumber))
            {
                throw new BinRefusedException("'offset' is a whole number of seconds, minutes, hours or days.");
            }

            offset = (long)offsetNumber * FixedUnits[offsetUnit];
        }

        string snap = parameters.TryGetProperty("snapToData", out JsonElement snapElement) && snapElement.ValueKind == JsonValueKind.String
            ? snapElement.GetString()!
            : string.Empty;

        if (snap.Length > 0 && snap is not "first")
        {
            throw new BinRefusedException(
                snap == "last"
                    ? "'snapToData' of 'last' is not supported by queryBins here: only 'first' is."
                    : "'snapToData' is 'first' here.");
        }

        bool full = parameters.TryGetProperty("returnFullIntervalBin", out JsonElement fullElement) && fullElement.ValueKind == JsonValueKind.True;

        return Copy(request, start: Opt(parameters, "start"), end: Opt(parameters, "end"), unit: unit, number: (int)number,
            offset: offset, snapToFirst: snap == "first", full: full);
    }

    private static BinRequest Copy(
        BinRequest from,
        double? start = null,
        double? end = null,
        double interval = 0,
        int numberOfBins = 0,
        IReadOnlyList<double>? boundaries = null,
        string unit = "",
        int number = 1,
        long offset = 0,
        bool snapToFirst = false,
        bool full = false)
    {
        if (start is { } s && end is { } e && e < s)
        {
            throw new BinRefusedException("'end' is before 'start'.");
        }

        return new BinRequest
        {
            Kind = from.Kind,
            Column = from.Column,
            ColumnType = from.ColumnType,
            Descending = from.Descending,
            HideUpperBound = from.HideUpperBound,
            LowerName = from.LowerName,
            UpperName = from.UpperName,
            FirstDayOfWeek = from.FirstDayOfWeek,
            Start = start,
            End = end,
            Interval = interval,
            NumberOfBins = numberOfBins,
            Boundaries = boundaries ?? [],
            Unit = unit,
            Number = number,
            OffsetMilliseconds = offset,
            SnapToFirst = snapToFirst,
            FullIntervalBin = full,
        };
    }

    private static string Alias(string text, string fallback)
    {
        string name = text.Trim();

        if (name.Length == 0)
        {
            return fallback;
        }

        if (!name.All(c => char.IsLetterOrDigit(c) || c == '_') || char.IsDigit(name[0]))
        {
            throw new BinRefusedException($"'{name}' is not a usable name for a boundary field: letters, digits and underscores.");
        }

        return name;
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : throw new BinRefusedException($"'{name}' is required and is text.");

    private static void Only(JsonElement element, params string[] allowed)
    {
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new BinRefusedException(
                    $"'{property.Name}' is not a parameter of this bin type here ({string.Join(", ", allowed)}), and it is refused rather than ignored.");
            }
        }
    }

    private static double Req(JsonElement element, string name, bool positive)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Number)
        {
            throw new BinRefusedException($"'{name}' is required and is a number.");
        }

        double number = value.GetDouble();

        if (!double.IsFinite(number) || (positive && number <= 0))
        {
            throw new BinRefusedException($"'{name}' must be {(positive ? "a positive " : "a ")}finite number.");
        }

        return number;
    }

    private static double? Opt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !double.IsFinite(value.GetDouble()))
        {
            throw new BinRefusedException($"'{name}' is a number (epoch milliseconds for a date).");
        }

        return value.GetDouble();
    }

    // ---------- answering ----------

    /// <summary>One bin: where it starts and ends.</summary>
    private readonly record struct Cell(double Lower, double Upper);

    /// <summary>The answer in the shape of a statistics response.</summary>
    /// <param name="request">The bins.</param>
    /// <param name="query">The layer query parsed from the shared parameters, whose statistics and filters apply.</param>
    /// <param name="summaries">What answers it.</param>
    /// <param name="cancellation">Cancellation.</param>
    /// <returns>The document to write.</returns>
    public static async Task<object> AnswerAsync(
        BinRequest request, FeatureQuery query, IFeatureSummaries summaries, CancellationToken cancellation)
    {
        bool date = request.Kind == BinKind.Date;

        // The range of the data, when the bins or their clipping depend on it.
        double? minimum = null;
        double? maximum = null;

        bool needsData = request.Kind switch
        {
            BinKind.FixedBoundaries => false,
            BinKind.Date => true,
            BinKind.FixedInterval => request.Start is null,
            _ => request.Start is null || request.End is null,
        };

        if (needsData)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> range = await summaries.StatisticsAsync(
                    Derive(query, [
                        new StatisticRequest(StatisticKind.Min, request.Column, "lo"),
                        new StatisticRequest(StatisticKind.Max, request.Column, "hi"),
                    ], []),
                    cancellation)
                .ConfigureAwait(false);

            if (range.Count > 0)
            {
                minimum = AsNumber(range[0]["lo"]);
                maximum = AsNumber(range[0]["hi"]);
            }
        }

        Func<long, Cell> cell;
        BoundExpression expression;
        long? lastIndex = null;

        switch (request.Kind)
        {
            case BinKind.FixedBoundaries:
            {
                IReadOnlyList<double> b = request.Boundaries;
                cell = i => new Cell(b[(int)i], b[(int)i + 1]);
                lastIndex = b.Count - 2;
                expression = Numeric(request, b, null, 0, 0, b.Count - 1);
                break;
            }

            case BinKind.AutoInterval:
            {
                double? s = request.Start ?? minimum;
                double? e = request.End ?? maximum;

                if (s is null || e is null)
                {
                    return Empty(request, query);
                }

                double width = e.Value > s.Value ? (e.Value - s.Value) / request.NumberOfBins : 1;
                double origin = s.Value;
                int n = e.Value > s.Value ? request.NumberOfBins : 1;
                cell = i => new Cell(origin + (i * width), origin + ((i + 1) * width));
                lastIndex = n - 1;
                expression = Numeric(request, null, origin, width, e.Value, n);
                break;
            }

            case BinKind.FixedInterval:
            {
                double? s = request.Start ?? minimum;

                if (s is null)
                {
                    return Empty(request, query);
                }

                double origin = s.Value;
                double width = request.Interval;
                int? n = request.End is { } end ? (int)Math.Max(1, Math.Ceiling((end - origin) / width)) : null;
                cell = i => new Cell(origin + (i * width), origin + ((i + 1) * width));
                lastIndex = n - 1;
                expression = Numeric(request, null, origin, width, request.End, n ?? 0);
                break;
            }

            default:
            {
                if ((request.Start ?? minimum) is null)
                {
                    return Empty(request, query);
                }

                (cell, expression, lastIndex) = DateBins(request, minimum, maximum);
                break;
            }
        }

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await summaries.StatisticsAsync(
                Derive(query, query.Statistics, [new GroupExpression("bin_index", expression)]),
                cancellation)
            .ConfigureAwait(false);

        Dictionary<long, IReadOnlyDictionary<string, object?>> byIndex = [];

        foreach (IReadOnlyDictionary<string, object?> row in rows)
        {
            if (row["bin_index"] is { } index)
            {
                byIndex[Convert.ToInt64(index, CultureInfo.InvariantCulture)] = row;
            }
        }

        // Empty bins between the first and the last are bins too — the reference's own example has one with a count of 0.
        long last = lastIndex ?? (byIndex.Count > 0 ? byIndex.Keys.Max() : -1);

        if (last < 0)
        {
            return Empty(request, query);
        }

        if (last + 1 > MostBins)
        {
            throw new BinRefusedException(
                $"These bins would number {last + 1}, and one answer holds at most {MostBins}: use a larger interval or a narrower start and end.");
        }

        double? clipLow = date && !request.FullIntervalBin ? request.Start is { } cs ? cs + request.OffsetMilliseconds : minimum : null;
        double? clipHigh = date && !request.FullIntervalBin ? request.End ?? maximum : null;

        bool whole = !date
            && request.ColumnType is FieldType.SmallInteger or FieldType.Integer or FieldType.BigInteger
            && Whole(request);

        List<string> statNames = [.. query.Statistics.Select(s => s.OutName)];
        List<Dictionary<string, object?>> features = [];

        for (long i = 0; i <= last; i++)
        {
            Cell c = cell(i);
            double lower = clipLow is { } low ? Math.Max(c.Lower, low) : c.Lower;
            double upper = clipHigh is { } high ? Math.Min(c.Upper, high) : c.Upper;
            Dictionary<string, object?> attributes = [];

            attributes[request.LowerName] = Boundary(lower, date, whole);

            if (!request.HideUpperBound)
            {
                attributes[request.UpperName] = Boundary(upper, date, whole);
            }

            byIndex.TryGetValue(i, out IReadOnlyDictionary<string, object?>? row);

            foreach (StatisticRequest statistic in query.Statistics)
            {
                attributes[statistic.OutName] = row is not null
                    ? Written(row[statistic.OutName])
                    : statistic.Kind == StatisticKind.Count ? 0L : null;
            }

            features.Add(attributes);
        }

        if (request.Descending)
        {
            features.Reverse();
        }

        List<object> fields =
        [
            new { name = request.LowerName, type = BoundaryType(date, whole), alias = request.LowerName },
        ];

        if (!request.HideUpperBound)
        {
            fields.Add(new { name = request.UpperName, type = BoundaryType(date, whole), alias = request.UpperName });
        }

        foreach (StatisticRequest statistic in query.Statistics)
        {
            object? sample = byIndex.Values.Select(r => r[statistic.OutName]).FirstOrDefault(v => v is not null);

            fields.Add(new
            {
                name = statistic.OutName,
                type = sample switch
                {
                    long or int or short => "esriFieldTypeInteger",
                    double or float or decimal => "esriFieldTypeDouble",
                    DateTime => "esriFieldTypeDate",
                    null => statistic.Kind == StatisticKind.Count ? "esriFieldTypeInteger" : "esriFieldTypeDouble",
                    _ => "esriFieldTypeString",
                },
                alias = statistic.OutName,
            });
        }

        return new
        {
            fields,
            features = features.Select(a => new { attributes = a }),
            exceededTransferLimit = false,
        };
    }

    private static object Empty(BinRequest request, FeatureQuery query)
    {
        bool date = request.Kind == BinKind.Date;
        List<object> fields = [new { name = request.LowerName, type = BoundaryType(date, false), alias = request.LowerName }];

        if (!request.HideUpperBound)
        {
            fields.Add(new { name = request.UpperName, type = BoundaryType(date, false), alias = request.UpperName });
        }

        foreach (StatisticRequest statistic in query.Statistics)
        {
            fields.Add(new
            {
                name = statistic.OutName,
                type = statistic.Kind == StatisticKind.Count ? "esriFieldTypeInteger" : "esriFieldTypeDouble",
                alias = statistic.OutName,
            });
        }

        return new { fields, features = Array.Empty<object>(), exceededTransferLimit = false };
    }

    private static string BoundaryType(bool date, bool whole) =>
        date ? "esriFieldTypeDate" : whole ? "esriFieldTypeInteger" : "esriFieldTypeDouble";

    /// <summary>A boundary as it is written: a date or a whole number is an integer, and a double carries one exactly.</summary>
    private static double Boundary(double value, bool date, bool whole) =>
        date || whole ? Math.Round(value) : value;

    private static bool Whole(BinRequest r) =>
        r.Kind switch
        {
            BinKind.FixedBoundaries => r.Boundaries.All(v => v == Math.Floor(v)),
            BinKind.FixedInterval => r.Interval == Math.Floor(r.Interval) && (r.Start ?? 0) == Math.Floor(r.Start ?? 0),
            _ => false,
        };

    private static object? Written(object? value) =>
        FeatureServerQueryWriter.EpochMilliseconds(value) is { } epoch ? epoch : value;

    private static double? AsNumber(object? value) => value switch
    {
        null => null,
        DateTime or DateTimeOffset => FeatureServerQueryWriter.EpochMilliseconds(value),
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    /// <summary>The query with other statistics and groups, the filters kept.</summary>
    private static FeatureQuery Derive(FeatureQuery from, IReadOnlyList<StatisticRequest> statistics, IReadOnlyList<GroupExpression> groups) =>
        new(
            limit: FeatureQuery.MaximumLimit,
            boundingBox: from.BoundingBox,
            includeGeometry: false,
            spatial: from.Spatial,
            identities: from.Identities,
            statistics: statistics,
            where: from.Where,
            filterSrid: from.FilterSrid)
        {
            GroupExpressions = groups,
            HistoricMoment = from.HistoricMoment,
        };

    // ---------- the index, as SQL ----------

    private static string Column(BinRequest request) => LayerDefinition.Quote(request.Column);

    /// <summary>
    /// The index of a number's bin, or null outside them. Every number is a bound parameter; the column is the layer's.
    /// </summary>
    private static BoundExpression Numeric(
        BinRequest request, IReadOnlyList<double>? boundaries, double? origin, double width, double? end, int bins)
    {
        string v = $"(({Column(request)})::double precision)";
        List<KeyValuePair<string, object?>> parameters = [];

        string Bind(double value)
        {
            string name = $"bin{parameters.Count}";
            parameters.Add(new KeyValuePair<string, object?>(name, value));
            return $"@{name}::double precision";
        }

        string sql;

        if (boundaries is not null)
        {
            string array = string.Join(", ", boundaries.Select(Bind));
            string low = Bind(boundaries[0]);
            string high = Bind(boundaries[^1]);
            sql = $"case when {v} >= {low} and {v} <= {high} then "
                + $"least(width_bucket({v}, array[{array}]) - 1, {bins - 1})::bigint end";
        }
        else if (end is { } upper && bins > 0)
        {
            string o = Bind(origin!.Value);
            string w = Bind(width);
            string e = Bind(upper);
            sql = $"case when {v} >= {o} and {v} <= {e} then least(floor(({v} - {o}) / {w}), {bins - 1})::bigint end";
        }
        else
        {
            string o = Bind(origin!.Value);
            string w = Bind(width);
            sql = $"case when {v} >= {o} then floor(({v} - {o}) / {w})::bigint end";
        }

        return new BoundExpression("bin index", sql, parameters, ExpressionKind.Integer);
    }

    private static DateTime Utc(double milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(milliseconds)).UtcDateTime;

    private static double Epoch(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static long MonthIndex(DateTime utc) => (utc.Year * 12L) + utc.Month - 1;

    private static DateTime MonthStart(long index) => new((int)(index / 12), (int)(index % 12) + 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A moment cut back to the start of its unit.</summary>
    private static DateTime Truncate(DateTime utc, string unit, int firstDayOfWeek)
    {
        switch (unit)
        {
            case "year": return new DateTime(utc.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            case "quarter": return new DateTime(utc.Year, (((utc.Month - 1) / 3) * 3) + 1, 1, 0, 0, 0, DateTimeKind.Utc);
            case "month": return new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            case "week":
            {
                int today = utc.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)utc.DayOfWeek;
                int back = (today - firstDayOfWeek + 7) % 7;
                return utc.Date.AddDays(-back);
            }

            case "day": return utc.Date;
            case "hour": return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
            case "minute": return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, DateTimeKind.Utc);
            default: return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, DateTimeKind.Utc);
        }
    }

    /// <summary>
    /// A date bin's cells, its index as SQL, and the last index the caller's <c>end</c> reaches.
    /// </summary>
    /// <remarks>
    /// <b>Boundaries are the origin plus <c>offset</c> plus whole intervals</b> (INFERRED from the reference's example: a day
    /// bin with a start of 16:00 UTC and an offset of 8 hours is a bin that starts at midnight). Without a start, the origin
    /// is the first value cut back to the start of its unit in the shifted time, so the first bin holds the first value.
    /// A month, quarter or year is a calendar span, so its origin is a month start.
    /// </remarks>
    private static (Func<long, Cell> Cell, BoundExpression Expression, long? Last) DateBins(
        BinRequest request, double? minimum, double? maximum)
    {
        double offset = request.OffsetMilliseconds;
        string ms = $"(extract(epoch from {Column(request)}) * 1000)";
        List<KeyValuePair<string, object?>> parameters = [];

        string Bind(double value)
        {
            string name = $"bin{parameters.Count}";
            parameters.Add(new KeyValuePair<string, object?>(name, value));
            return $"@{name}::double precision";
        }

        double? guard = request.Start is { } start ? start + offset : null;

        if (FixedUnits.TryGetValue(request.Unit, out long unitMs))
        {
            double width = unitMs * (double)request.Number;
            double origin;

            if (request.Start is { } given)
            {
                origin = given + offset;
            }
            else if (request.SnapToFirst)
            {
                origin = minimum!.Value;
            }
            else
            {
                origin = Epoch(Truncate(Utc(minimum!.Value - offset), request.Unit, request.FirstDayOfWeek)) + offset;
            }

            string o = Bind(origin);
            string w = Bind(width);
            string where = $"{ms} >= {o}";
            long? last = null;

            if (request.End is { } end)
            {
                where += $" and {ms} <= {Bind(end)}";
                last = (long)Math.Floor((end - origin) / width);
            }

            string sql = $"case when {where} then floor(({ms} - {o}) / {w})::bigint end";
            return (i => new Cell(origin + (i * width), origin + ((i + 1) * width)), new BoundExpression("bin index", sql, parameters, ExpressionKind.Integer), last);
        }

        int months = MonthUnits[request.Unit] * request.Number;
        double anchor = request.Start ?? minimum!.Value;
        long first = MonthIndex(Truncate(Utc(anchor - offset), request.Unit, request.FirstDayOfWeek));
        string shifted = $"(to_timestamp(({ms} - {Bind(offset)}) / 1000.0) at time zone 'UTC')";
        string month = $"(extract(year from {shifted}) * 12 + extract(month from {shifted}) - 1)";
        string startIndex = Bind(first);
        string span = Bind(months);

        string condition = guard is { } g ? $"{ms} >= {Bind(g)}" : $"{month} >= {startIndex}";
        long? lastIndex = null;

        if (request.End is { } endMs)
        {
            condition += $" and {ms} <= {Bind(endMs)}";
            lastIndex = (MonthIndex(Utc(endMs - offset)) - first) / months;
        }

        string indexSql = $"case when {condition} then floor(({month} - {startIndex}) / {span})::bigint end";

        return (
            i =>
            {
                double lower = Epoch(MonthStart(first + (i * months))) + offset;
                double upper = Epoch(MonthStart(first + ((i + 1) * months))) + offset;
                return new Cell(lower, upper);
            },
            new BoundExpression("bin index", indexSql, parameters, ExpressionKind.Integer),
            lastIndex);
    }
}
