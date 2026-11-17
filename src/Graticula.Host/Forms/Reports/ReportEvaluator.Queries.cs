using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Host.Forms.Reports;

/// <summary>
/// Maps, summary sections and queries — ADR-301 §5.10 and §5.11: what a placeholder reads beyond the record in front of it,
/// asked of <see cref="Lookups"/> so the engine itself makes no request.
/// </summary>
internal sealed partial class ReportEvaluator
{
    // ------------------------------------------------------------------------------------------------- piped values

    /// <summary>
    /// <c>(question | getValue:"size")</c> in an expression, or <c>${if question | selected:"name"}</c>: the value the
    /// methods give — a choice selected or not, else what the placeholder would write, read as a number when it is one.
    /// When checking, the methods are checked and nothing is written.
    /// </summary>
    private ReportValue Piped(ReportPiped piped, ReportScope scope)
    {
        ReportTag tag = new(ReportTagKind.Value, string.Empty, piped.Inner, piped.Methods);

        if (piped.Methods.FirstOrDefault(m => m.Name == "selected") is { } selected)
        {
            ReportValue value = Evaluate(piped.Inner, scope);

            if (piped.Methods.Count > 1 || !ReportSchema.IsSelect(value.Question) || selected.Text(0) is not { Length: > 0 } name)
            {
                throw new ReportSyntaxException("'| selected' names a choice of a select_one or select_multiple question, alone: ${if question | selected:\"name\"}.");
            }

            string[] chosen = value.Question!.Type == "select_multiple" ? ReportSchema.Selected(value.Raw)
                : value.Raw is null ? [] : [ReportValue.AsText(value.Raw)];
            return ReportValue.Of(chosen.Contains(name.Trim(), StringComparer.Ordinal));
        }

        if (!scope.Filling)
        {
            Check(tag, scope);
            return ReportValue.Of(null);
        }

        return Write(tag, scope) switch
        {
            ReportText text => ReportValue.Of(ReportValue.AsNumber(text.Text) is { } n ? n : text.Text.Length == 0 ? null : text.Text),
            ReportBullets bullets => ReportValue.Of(string.Join(", ", bullets.Items)),
            _ => ReportValue.Of(null),
        };
    }

    /// <summary>
    /// ArcGIS's editor-tracking field names, which templates written for a hosted layer use, read as this server's layer
    /// fields (INFERRED): CreationDate, Creator, EditDate, Editor.
    /// </summary>
    internal static string LayerFieldOf(string name) => name.ToLowerInvariant() switch
    {
        "creationdate" => "created_date",
        "creator" => "created_user",
        "editdate" => "last_edited_date",
        "editor" => "last_edited_user",
        _ => name,
    };

    // ------------------------------------------------------------------------------------------------------- tables

    /// <summary>
    /// The table a placeholder names as a layer — the form's layer by its name, a repeat by its name, or
    /// <c>$layers[…]</c> — or null when it names something else.
    /// </summary>
    public FormTable? TableOf(ReportNode? node, ReportScope scope)
    {
        switch (node)
        {
            case ReportLayerRef layer:
                List<FormTable> tables = [.. _schema.Root.AllTables];

                if (layer.Index is { } index)
                {
                    // The position publishing gives the form's tables: the form's layer first, then its repeats (INFERRED).
                    return index < tables.Count ? tables[index]
                        : throw new ReportSyntaxException($"{layer.Written} names no layer: the form's service has {tables.Count} (from $layers[0], the form's layer, to $layers[{tables.Count - 1}]).");
                }

                return string.Equals(layer.Name, _schema.TopName, StringComparison.OrdinalIgnoreCase) ? _schema.Root
                    : tables.Skip(1).FirstOrDefault(t => string.Equals(t.Name, layer.Name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ReportSyntaxException($"{layer.Written} names no layer of this form: its layers are {_schema.TopName}{string.Concat(tables.Skip(1).Select(t => ", " + t.Name))}.");
            case ReportName { Path.Count: 1 } name when !name.Path[0].StartsWith('$'):
                string first = name.Path[0];

                if (ReportSchema.Question(scope.Table, first) is not null || scope.RowNames?.Contains(first) == true)
                {
                    return null;
                }

                if (string.Equals(first, _schema.TopName, StringComparison.OrdinalIgnoreCase) && ReportSchema.Question(_schema.Root, first) is null)
                {
                    return _schema.Root;
                }

                // A repeat of the table in front of the placeholder, or of the form's layer in a summary section.
                return scope.Chain().Where(s => !s.InFiles).Select(s => ReportSchema.Repeat(s.Table, first)).FirstOrDefault(t => t is not null);
            default:
                return null;
        }
    }

    private static string TableWritten(ReportNode node) => node switch
    {
        ReportLayerRef layer => layer.Written,
        ReportName name => name.Dotted,
        _ => "the layer",
    };

    /// <summary>The name a table's rows are written in a scope by: the form's layer's, or the repeat's.</summary>
    private string ScopeName(FormTable table) => table == _schema.Root ? _schema.TopName : table.Name;

    /// <summary>The table that holds a repeat; null for the form's layer.</summary>
    private FormTable? ParentOf(FormTable table) => _schema.Root.AllTables.FirstOrDefault(t => t.Repeats.Contains(table));

    private static Guid? GlobalOf(ReportRecord record) =>
        record.Values.GetValueOrDefault("globalid") switch
        {
            Guid g => g,
            { } other when Guid.TryParse(Convert.ToString(other, CultureInfo.InvariantCulture), out Guid parsed) => parsed,
            _ => null,
        };

    /// <summary>The summary section a scope is in, with no record of the form's layer between them; null outside one.</summary>
    private static ReportScope? SummaryOf(ReportScope scope) =>
        scope.Chain().Where(s => !s.InFiles).TakeWhile(s => s.Record is null && s.Row is null && s.RowNames is null).FirstOrDefault(s => s.IsSummary);

    /// <summary>
    /// The records a query of <paramref name="table"/> is kept to: Survey123's page says a filter reads the records the
    /// report is printed for — the record in front of it, or a summary's records — unless <c>!important</c> is written.
    /// </summary>
    private (IReadOnlyList<long>? Ids, IReadOnlyList<Guid>? Parents) Within(FormTable table, ReportScope scope)
    {
        if (table == _schema.Root)
        {
            foreach (ReportScope s in scope.Chain().Where(s => !s.InFiles))
            {
                if (s.Record is { } record && s.Table == _schema.Root)
                {
                    return ([record.ObjectId], null);
                }

                if (s.IsSummary)
                {
                    return ([.. s.Inputs!.Select(r => r.ObjectId)], null);
                }
            }

            return ([], null);
        }

        FormTable? parent = ParentOf(table);

        foreach (ReportScope s in scope.Chain().Where(s => !s.InFiles))
        {
            if (s.Record is { } record && s.Table == parent)
            {
                return (null, GlobalOf(record) is { } g ? [g] : []);
            }

            if (s.IsSummary && parent == _schema.Root)
            {
                return (null, [.. s.Inputs!.Select(GlobalOf).OfType<Guid>()]);
            }
        }

        return (null, []);
    }

    /// <summary>Whether a field a query names is one of the table's: a question that keeps a value, or a layer field.</summary>
    private static bool IsField(FormTable table, string name) =>
        ReportSchema.LayerFields.Contains(name)
        || (ReportSchema.Question(table, name) is { } q && !ReportSchema.IsFiles(q) && !FormCatalog.LocationTypes.Contains(q.Type));

    private static string[] FieldList(string? text) =>
        (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Checks a query's methods on a table; throws the refusal.</summary>
    private void CheckQuery(FormTable table, IReadOnlyList<ReportMethod> methods, ReportScope scope, IReadOnlySet<string> allowed, bool numberMethods)
    {
        IReadOnlyList<ReportStatistic>? statistics = null;

        foreach (ReportMethod m in methods)
        {
            if (RefusedMethods.TryGetValue(m.Name, out string? why))
            {
                throw new ReportSyntaxException($"'| {m.Name}' is not supported: {why}.");
            }

            if (!allowed.Contains(m.Name))
            {
                if (numberMethods && m.Name is "round" or "toFixed" or "format" or "useGrouping" or "locale")
                {
                    continue;
                }

                throw new ReportSyntaxException($"'| {m.Name}' is not read on a layer here: a layer takes {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
            }

            if (m.Arguments.Any(a => a is string s && s.Contains("$inputParams", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ReportSyntaxException($"$inputParams is not supported: {RefusedKeywords["$inputParams"]}.");
            }

            switch (m.Name)
            {
                case "where":
                    if (m.Expression(0) is { } built)
                    {
                        Evaluate(built, scope);
                    }
                    else if (m.Text(0) is not { Length: > 0 })
                    {
                        throw new ReportSyntaxException("'| where' takes a where clause in quotes, as where:\"status='broken'\".");
                    }

                    break;
                case "stats":
                    statistics = ReportQueries.Statistics(m.Text(0));

                    foreach (ReportStatistic s in statistics)
                    {
                        if (!(s.Field == "*" && s.Type == "count") && !IsField(table, s.Field))
                        {
                            throw new ReportSyntaxException($"'{s.Field}' in '| stats' is not a question of {(table == _schema.Root ? "the form's layer" : $"the repeat '{table.Name}'")} that keeps a value.");
                        }

                        if (s.Type is "sum" or "avg" or "stddev" or "var" or "range" or "median"
                            && ReportSchema.Question(table, s.Field) is { } q && q.Type is not ("integer" or "decimal" or "range" or "calculate" or "hidden"))
                        {
                            throw new ReportSyntaxException($"'{s.Type}' is a statistic of numbers, and '{s.Field}' is a {q.Type} question.");
                        }
                    }

                    if (FieldList(m.Text(1)).Length > 0 && statistics.Any(s => s.Type is "first" or "last"))
                    {
                        throw new ReportSyntaxException("'first' and 'last' are read over all the rows here, not per group: write them in a '| stats' without groupByFieldsForStatistics.");
                    }

                    foreach (string g in FieldList(m.Text(1)))
                    {
                        if (!IsField(table, g))
                        {
                            throw new ReportSyntaxException($"'{g}', grouped by in '| stats', is not a question of {(table == _schema.Root ? "the form's layer" : $"the repeat '{table.Name}'")}.");
                        }
                    }

                    break;
                case "orderByFields":
                    foreach (string order in FieldList(m.Text(0)))
                    {
                        string[] words = order.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        bool statistic = statistics?.Any(s => string.Equals(s.OutName, words[0], StringComparison.OrdinalIgnoreCase)) == true;

                        if (words.Length > 2 || (words.Length == 2 && !(words[1].Equals("ASC", StringComparison.OrdinalIgnoreCase) || words[1].Equals("DESC", StringComparison.OrdinalIgnoreCase)))
                            || (!statistic && !IsField(table, words[0])))
                        {
                            throw new ReportSyntaxException($"'{order}' in '| orderByFields' is a question of the table, or a statistic's name, then ASC or DESC.");
                        }
                    }

                    break;
                case "resultRecordCount":
                    if (m.Number(0) is not { } count || count < 1 || count > FormEndpoints.MaximumReportRows || count != Math.Floor(count))
                    {
                        throw new ReportSyntaxException($"'| resultRecordCount' takes a number of rows from 1 to {FormEndpoints.MaximumReportRows}.");
                    }

                    break;
                case "outFields":
                    foreach (string f in FieldList(m.Text(0)))
                    {
                        if (!IsField(table, f))
                        {
                            throw new ReportSyntaxException($"'{f}' in '| outFields' is not a question of the table that keeps a value.");
                        }
                    }

                    break;
                case "returnDistinctValues":
                    if (m.Arguments.Count != 1 || m.Arguments[0] is not bool)
                    {
                        throw new ReportSyntaxException("'| returnDistinctValues' takes true or false.");
                    }

                    break;
                default:
                    break;
            }
        }

        if (methods.Any(m => m.Name == "returnDistinctValues") && !methods.Any(m => m.Name is "outFields" or "stats"))
        {
            throw new ReportSyntaxException("'| returnDistinctValues' is read with '| outFields', which names the fields whose values are distinct.");
        }
    }

    /// <summary>The query a table placeholder's methods ask, in the scope it is filled in.</summary>
    private ReportQueryRequest QueryOf(FormTable table, IReadOnlyList<ReportMethod> methods, ReportScope scope)
    {
        string? where = null;
        bool important = false;
        IReadOnlyList<ReportStatistic> statistics = [];
        string? groupBy = null, having = null, orderBy = null, outFields = null;
        int? count = null;
        bool distinct = false;

        foreach (ReportMethod m in methods)
        {
            switch (m.Name)
            {
                case "where":
                    string text = m.Expression(0) is { } built ? ReportValue.AsText(Comparable(Evaluate(built, scope))) : m.Text(0) ?? string.Empty;
                    (where, important) = ReportQueries.Important(text);
                    break;
                case "stats":
                    statistics = ReportQueries.Statistics(m.Text(0));
                    groupBy = m.Text(1) is { Length: > 0 } g ? g : null;
                    having = m.Text(2) is { Length: > 0 } h ? h : null;
                    break;
                case "orderByFields":
                    orderBy = m.Text(0);
                    break;
                case "resultRecordCount":
                    count = (int)(m.Number(0) ?? 1);
                    break;
                case "outFields":
                    outFields = m.Text(0);
                    break;
                case "returnDistinctValues":
                    distinct = m.Arguments.Count > 0 && m.Arguments[0] is true;
                    break;
                default:
                    break;
            }
        }

        (IReadOnlyList<long>? ids, IReadOnlyList<Guid>? parents) = important ? (null, null) : Within(table, scope);
        return new ReportQueryRequest(table == _schema.Root ? string.Empty : table.Name, where is { Length: > 0 } ? where : null, important,
            ids, parents, statistics, groupBy, having, orderBy, count, outFields, distinct);
    }

    /// <summary>The value of <c>${layer | stats:"…"}</c>: its first statistic in its first row; null while the query is not answered.</summary>
    private ReportValue? StatisticValue(FormTable table, ReportTag tag, ReportScope scope)
    {
        ReportQueryRequest request = QueryOf(table, tag.Methods, scope);

        if (Lookups?.Query(request) is not { } result)
        {
            return null;
        }

        return ReportValue.Of(result.Rows.Count > 0 ? result.Rows[0].GetValueOrDefault(request.Statistics[0].OutName) : null);
    }

    // ----------------------------------------------------------------------------------------------------- sections

    /// <summary>The names a row of statistics or distinct values holds.</summary>
    private static HashSet<string>? RowNamesOf(IReadOnlyList<ReportMethod> methods)
    {
        if (methods.FirstOrDefault(m => m.Name == "stats") is { } stats)
        {
            HashSet<string> names = new(ReportQueries.Statistics(stats.Text(0)).Select(s => s.OutName), StringComparer.OrdinalIgnoreCase);
            names.UnionWith(FieldList(stats.Text(1)));
            return names;
        }

        return methods.Any(m => m.Name == "returnDistinctValues" && m.Arguments.Count > 0 && m.Arguments[0] is true)
            && methods.FirstOrDefault(m => m.Name == "outFields") is { } fields
            ? new HashSet<string>(FieldList(fields.Text(0)), StringComparer.OrdinalIgnoreCase)
            : null;
    }

    /// <summary>
    /// Checks a section's opening placeholder and gives the scope its body is checked in: <c>${if}</c>'s own, a repeat's or
    /// a layer's row, a row of statistics, a file, or a summary section's.
    /// </summary>
    public ReportScope CheckSection(ReportTag tag, ReportScope scope)
    {
        if (tag.Kind == ReportTagKind.If)
        {
            Evaluate(tag.Expression!, scope);
            return scope;
        }

        if (tag.Kind == ReportTagKind.Summary)
        {
            if (scope.Parent is not null || scope.IsSummary)
            {
                throw new ReportSyntaxException("A summary section stands at the start or the end of the document, never inside another section.");
            }

            return new ReportScope(null, _schema.Root, _schema.TopName, null, 1, null, false) { Inputs = [] };
        }

        if (tag.Expression is ReportName { Path.Count: 1 } named && !named.Path[0].StartsWith('$')
            && ReportSchema.Question(scope.Table, named.Path[0]) is null
            && !scope.Chain().Any(s => !s.InFiles && ReportSchema.Repeat(s.Table, named.Path[0]) is not null)
            && !string.Equals(named.Path[0], _schema.TopName, StringComparison.OrdinalIgnoreCase)
            && _schema.Root.AllTables.Skip(1).FirstOrDefault(t => string.Equals(t.Name, named.Path[0], StringComparison.OrdinalIgnoreCase)) is { } deeper)
        {
            throw new ReportSyntaxException($"'{deeper.Name}' is a repeat inside another repeat: write its section inside the section of the repeat that holds it.");
        }

        if (TableOf(tag.Expression, scope) is { } table)
        {
            CheckQuery(table, tag.Methods, scope, ReportQueries.QueryMethods, numberMethods: false);
            return new ReportScope(scope, table, ScopeName(table), null, 0, null, false) { RowNames = RowNamesOf(tag.Methods) };
        }

        if (tag.Methods.Count > 0)
        {
            ReportMethod m = tag.Methods[0];
            throw new ReportSyntaxException(RefusedMethods.TryGetValue(m.Name, out string? why)
                ? $"'| {m.Name}' is not supported: {why}."
                : $"A section over files takes no method; '| {m.Name}' is not read.");
        }

        ReportName name = (ReportName)tag.Expression!;
        ReportValue value = Resolve(name, scope);

        if (value.Kind == ReportValueKind.Files)
        {
            return new ReportScope(scope, scope.Table, scope.Name, scope.Record, 0, null, true);
        }

        throw new ReportSyntaxException($"${{#{name.Dotted}}} repeats over a repeat, the form's layer, $layers[…], an image or file question, or $attachment; '{name.Dotted}' is none of them.");
    }

    /// <summary>The scopes a section's body is written in, one per row, file or statistic; none for an unmet <c>${if}</c>.</summary>
    public IEnumerable<ReportScope> Sections(ReportTag tag, ReportScope scope)
    {
        if (tag.Kind == ReportTagKind.If)
        {
            if (TruthOf(Evaluate(tag.Expression!, scope)))
            {
                yield return scope;
            }

            yield break;
        }

        if (tag.Kind == ReportTagKind.Summary)
        {
            yield break;
        }

        if (TableOf(tag.Expression, scope) is { } table)
        {
            string name = ScopeName(table);

            if (tag.Methods.Any(m => ReportQueries.QueryMethods.Contains(m.Name)))
            {
                ReportQueryRequest request = QueryOf(table, tag.Methods, scope);

                if (Lookups?.Query(request) is not { } result)
                {
                    yield break;
                }

                if (request.AnswersRows)
                {
                    HashSet<string>? names = RowNamesOf(tag.Methods);

                    for (int i = 0; i < result.Rows.Count; i++)
                    {
                        yield return new ReportScope(scope, table, name, null, i + 1, null, false) { Row = result.Rows[i], RowNames = names };
                    }

                    yield break;
                }

                for (int i = 0; i < result.Records.Count; i++)
                {
                    yield return new ReportScope(scope, table, name, result.Records[i], i + 1, null, false);
                }

                yield break;
            }

            IReadOnlyList<ReportRecord> rows = DefaultRows(table, scope);

            for (int i = 0; i < rows.Count; i++)
            {
                yield return new ReportScope(scope, table, name, rows[i], i + 1, null, false);
            }

            yield break;
        }

        ReportValue files = Resolve((ReportName)tag.Expression!, scope);

        for (int i = 0; i < (files.Files?.Count ?? 0); i++)
        {
            yield return new ReportScope(scope, scope.Table, scope.Name, scope.Record, i + 1, files.Files![i], true);
        }
    }

    private static bool TruthOf(ReportValue value) => value.Kind switch
    {
        ReportValueKind.Files or ReportValueKind.File => value.Files is { Count: > 0 },
        _ => ReportValue.Truthy(value.Raw),
    };

    /// <summary>
    /// A table's rows with no query: a repeat's rows of the record in front of it; in a summary section, the records the
    /// report is made of and all their rows of a repeat; the form's layer outside one, the record itself.
    /// </summary>
    private IReadOnlyList<ReportRecord> DefaultRows(FormTable table, ReportScope scope)
    {
        FormTable? parent = ParentOf(table);

        foreach (ReportScope s in scope.Chain().Where(s => !s.InFiles))
        {
            if (table == _schema.Root)
            {
                if (s.IsSummary)
                {
                    return s.Inputs!;
                }

                if (s.Record is { } own && s.Table == _schema.Root)
                {
                    return [own];
                }

                continue;
            }

            if (s.Record is { } record && s.Table == parent)
            {
                return record.Repeats.GetValueOrDefault(table.Name) ?? [];
            }

            if (s.IsSummary && parent == _schema.Root)
            {
                return [.. s.Inputs!.SelectMany(r => r.Repeats.GetValueOrDefault(table.Name) ?? [])];
            }
        }

        return [];
    }

    // --------------------------------------------------------------------------------------------------------- maps

    /// <summary>Whether a value placeholder draws a map: <c>$shape</c>, <c>$map</c>, or a location not read with getValue.</summary>
    private static bool IsMap(ReportValue value, ReportTag tag) =>
        value.Kind == ReportValueKind.Shape
        || (value.Question is { } q && FormCatalog.LocationTypes.Contains(q.Type) && !tag.Methods.Any(m => m.Name == "getValue"));

    /// <summary>The table a location question belongs to.</summary>
    private FormTable TableHolding(FormQuestion question) =>
        _schema.Root.AllTables.First(t => t.Questions.Contains(question));

    /// <summary>Checks a map's methods, as Survey123's <i>Map expressions</i> page writes them; throws the refusal.</summary>
    private void CheckMap(ReportTag tag, ReportValue value, ReportScope scope)
    {
        bool plainMap = value.Raw is "$map";
        FormTable? table = plainMap ? null : value.Question is { } q ? TableHolding(q) : _schema.Root;

        foreach (ReportMethod m in tag.Methods)
        {
            if (RefusedMethods.TryGetValue(m.Name, out string? why))
            {
                throw new ReportSyntaxException($"'| {m.Name}' is not supported: {why}.");
            }

            switch (m.Name)
            {
                case "map":
                    if (m.Text(0)?.Trim() is not { } id || (id.Length > 0 && !ItemId.IsMatch(id)))
                    {
                        throw new ReportSyntaxException("'| map' takes a web map's item id, 32 letters and digits in quotes, or \"\" for the organisation's default basemap.");
                    }

                    break;
                case "mapScale":
                    if (m.Number(0) is not { } scale || scale < 0 || scale > 1e9)
                    {
                        throw new ReportSyntaxException("'| mapScale' takes the scale's denominator, as mapScale:10000; 0 fits the map to what it shows.");
                    }

                    break;
                case "mapExtent":
                    if (m.Arguments.Count is not (4 or 5) || m.Arguments.Any(a => a is not double)
                        || m.Number(0) >= m.Number(2) || m.Number(1) >= m.Number(3))
                    {
                        throw new ReportSyntaxException("'| mapExtent' takes xmin:ymin:xmax:ymax and the wkid they are in, as mapExtent:139.7:35.6:139.9:35.8:4326.");
                    }

                    break;
                case "size":
                    if (m.Arguments.Count is < 1 or > 4 || m.Arguments.Any(a => a is not double d || d < 0 || d > ReportQueries.MaximumMapSide))
                    {
                        throw new ReportSyntaxException($"'| size' on a map takes its width and height in pixels, from 0 to {ReportQueries.MaximumMapSide}: size:400:300.");
                    }

                    break;
                case "drawingInfo":
                    if (plainMap)
                    {
                        throw new ReportSyntaxException("'| drawingInfo' draws records with their layer's symbols, and $map draws no records.");
                    }

                    if (!string.Equals(m.Text(0), "currentLayer", StringComparison.Ordinal))
                    {
                        throw new ReportSyntaxException("'| drawingInfo' here takes \"currentLayer\", the record's own layer: a layer at an address is not fetched by a report (ADR-265 §5.3).");
                    }

                    break;
                case "where" or "orderByFields" or "resultRecordCount":
                    if (plainMap)
                    {
                        throw new ReportSyntaxException($"'| {m.Name}' chooses the records a map draws, and $map draws none.");
                    }

                    CheckQuery(table!, [m], scope, ReportQueries.QueryMethods, numberMethods: false);
                    break;
                case "rotate":
                    throw new ReportSyntaxException("'| rotate' on a map is not supported: a map in a report is drawn with north up, as this server's print service draws one (ADR-265 §6).");
                default:
                    throw new ReportSyntaxException($"'| {m.Name}' is not a method a map takes: a map takes map, mapScale, mapExtent, size, drawingInfo, where, orderByFields and resultRecordCount.");
            }
        }
    }

    /// <summary>The map a placeholder draws, or nothing while it is not drawn yet, or for a record with no geometry.</summary>
    private ReportOutput WriteMap(ReportTag tag, ReportValue value, ReportScope scope)
    {
        bool plainMap = value.Raw is "$map";
        FormTable table = value.Question is { } q ? TableHolding(q) : _schema.Root;
        string? webMap = null;
        double scale = 0;
        IReadOnlyList<double>? extent = null;
        int width = ReportQueries.DefaultMapWidth, height = ReportQueries.DefaultMapHeight;
        bool own = false;

        foreach (ReportMethod m in tag.Methods)
        {
            switch (m.Name)
            {
                case "map":
                    webMap = m.Text(0)?.Trim() is { Length: > 0 } id ? id.ToLowerInvariant() : null;
                    break;
                case "mapScale":
                    scale = m.Number(0) ?? 0;
                    break;
                case "mapExtent":
                    extent = [.. Enumerable.Range(0, 4).Select(i => m.Number(i) ?? 0), m.Number(4) ?? 4326];
                    break;
                case "size":
                    width = m.Number(0) is { } w && w >= 1 ? (int)Math.Round(w) : width;
                    height = m.Number(1) is { } h && h >= 1 ? (int)Math.Round(h) : height;
                    break;
                case "drawingInfo":
                    own = true;
                    break;
                default:
                    break;
            }
        }

        IReadOnlyList<ReportRecord> records;

        if (plainMap)
        {
            records = [];
        }
        else if (tag.Methods.Any(m => m.Name is "where" or "orderByFields" or "resultRecordCount"))
        {
            if (Lookups?.Query(QueryOf(table, [.. tag.Methods.Where(m => m.Name is "where" or "orderByFields" or "resultRecordCount")], scope)) is not { } found)
            {
                return new ReportText(string.Empty);
            }

            records = found.Records;
        }
        else if (scope.Chain().Where(s => !s.InFiles).FirstOrDefault(s => s.Record is not null && s.Table == table) is { Record: { } record })
        {
            records = [record];
        }
        else
        {
            // In a summary section: every record the report is made of, or all their rows of the repeat.
            records = SummaryOf(scope) is not null ? DefaultRows(table, scope) : [];
        }

        List<Graticula.Geometries.Geometry> shapes = [.. records.Select(r => r.Drawn).OfType<Graticula.Geometries.Geometry>().Where(g => !g.IsEmpty)];

        if (!plainMap && shapes.Count == 0 && extent is null)
        {
            // A record with no location draws no map (INFERRED): a map of nothing would say the record has a place.
            return new ReportText(string.Empty);
        }

        ReportMapRequest request = new(table == _schema.Root ? string.Empty : table.Name,
            [.. records.Where(r => r.Drawn is { IsEmpty: false }).Select(r => r.ObjectId)], shapes, webMap, scale, extent,
            Math.Min(width, ReportQueries.MaximumMapSide), Math.Min(height, ReportQueries.MaximumMapSide), own);

        if (Lookups?.Map(request) is not { Png.Length: > 0 } map)
        {
            return new ReportText(string.Empty);
        }

        ReportFile file = new(0, "map.png", "image/png", map.Png.Length, string.Empty, null, map.Png);
        return new ReportImage(file, request.Width, request.Height, 0, 0, 0);
    }
}
