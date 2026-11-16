using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Host.Forms.Reports;

/// <summary>What a placeholder writes: text, a list, a picture, or nothing.</summary>
internal abstract record ReportOutput;

/// <summary>Text; <paramref name="Multiline"/> keeps its line breaks as breaks in the document.</summary>
internal sealed record ReportText(string Text, bool Multiline = false) : ReportOutput;

/// <summary>A bulleted list, one line per item (<c>appearance:"bullets"</c>).</summary>
internal sealed record ReportBullets(IReadOnlyList<string> Items) : ReportOutput;

/// <summary>A picture: the file, and <c>size:width:height:maxWidth:maxHeight</c> and <c>rotate</c> as written.</summary>
internal sealed record ReportImage(ReportFile File, double Width, double Height, double MaxWidth, double MaxHeight, double Rotate) : ReportOutput;

/// <summary>
/// Reads names in a scope and applies a placeholder's methods — used twice, the same way: to check a template against the
/// form when it is uploaded (no record, so every refusal is found before anybody asks for a report), and to fill it.
/// ADR-301 §5.2–§5.4.
/// </summary>
internal sealed class ReportEvaluator
{
    /// <summary>The methods Survey123 documents that a report here does not do, each with the reason the refusal gives.</summary>
    public static readonly FrozenDictionary<string, string> RefusedMethods = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["map"] = "a map is not drawn in a report yet (ADR-301 §6); write the location's coordinates with getValue:\"x\" and getValue:\"y\"",
        ["mapScale"] = "a map is not drawn in a report yet (ADR-301 §6)",
        ["drawingInfo"] = "a map is not drawn in a report yet (ADR-301 §6)",
        ["where"] = "a report here does not query layers of its own; it is made of the records chosen",
        ["stats"] = "a report here does not compute statistics; a summary section is not supported yet",
        ["attachmentsDefExp"] = "attachments are not filtered by an expression here; ${#question} … ${/} takes one question's files",
        ["src"] = "a picture is not fetched from an address or from the form's media by a report (ADR-301 §5.4); a picture comes from the record's attachments",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The keywords a report here does not read, each with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedKeywords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["$shape"] = "a record's geometry is drawn as a map, which a report here does not draw yet (ADR-301 §6)",
        ["$map"] = "a map is not drawn in a report yet (ADR-301 §6)",
        ["$image"] = "a picture is not fetched from an address or from the form's media by a report (ADR-301 §5.4)",
        ["$layers"] = "a report here does not query layers of its own; it is made of the records chosen",
        ["$summary"] = "a summary section is not supported yet",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> FileProperties = new[]
    {
        "", "name", "size", "contentType", "keywords", "id", "globalId", "width", "height", "position",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> Exif = new[] { "x", "y", "date", "time", "direction" }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ReportSchema _schema;
    private readonly DateTimeOffset _now;
    private readonly TimeSpan _offset;

    /// <param name="schema">The form.</param>
    /// <param name="now">When the report is made, for <c>$date</c>.</param>
    /// <param name="offset">The offset dates are written in unless a placeholder says <c>utcOffset</c>: the asker's.</param>
    public ReportEvaluator(ReportSchema schema, DateTimeOffset now, TimeSpan offset)
    {
        _schema = schema;
        _now = now;
        _offset = offset;
    }

    public ReportSchema Schema => _schema;

    // ------------------------------------------------------------------------------------------------------- names

    /// <summary>The value a name stands for; when checking (no record), its kind and question with no value.</summary>
    /// <exception cref="ReportSyntaxException">The name is nothing this form has.</exception>
    public ReportValue Resolve(ReportName name, ReportScope scope)
    {
        IReadOnlyList<string> path = name.Path;
        string first = path[0];

        if (first.StartsWith('$'))
        {
            if (RefusedKeywords.TryGetValue(first, out string? why))
            {
                throw new ReportSyntaxException($"{first} is not supported: {why}.");
            }

            if (path.Count > 1)
            {
                throw new ReportSyntaxException($"'{name.Dotted}' reads a property with '.'; write {first} | getValue:\"{path[1]}\".");
            }

            switch (first.ToLowerInvariant())
            {
                case "$date":
                    return new ReportValue(ReportValueKind.Now, _now.ToOffset(_offset));
                case "$feature":
                    return new ReportValue(ReportValueKind.Feature, scope.Record?.ObjectId, Position: scope.Position);
                case "$file":
                    ReportScope? files = scope.Chain().FirstOrDefault(s => s.InFiles)
                        ?? throw new ReportSyntaxException("$file is named outside a section over files: put it between ${#question} or ${#$attachment} and ${/}.");
                    return new ReportValue(ReportValueKind.File, null, Files: files.File is { } f ? [f] : [], Position: files.Position);
                case "$attachment":
                    ReportScope owner = scope.Chain().First(s => !s.InFiles);
                    return new ReportValue(ReportValueKind.Files, null, Files: owner.Record?.Files ?? []);
                default:
                    throw new ReportSyntaxException($"{first} is not a keyword a report reads: the keywords are $date, $feature, $file and $attachment.");
            }
        }

        if (path.Count > 2)
        {
            throw new ReportSyntaxException($"'{name.Dotted}' has more than one '.': a name is question, or table.question.");
        }

        ReportScope? at;
        string field;

        if (path.Count == 2)
        {
            at = scope.Chain().FirstOrDefault(s => !s.InFiles && string.Equals(s.Name, first, StringComparison.OrdinalIgnoreCase));

            if (at is null)
            {
                throw new ReportSyntaxException($"'{name.Dotted}' names '{first}', which is not the form's layer ({_schema.TopName}) nor a repeat whose section this placeholder is in.");
            }

            field = path[1];
        }
        else
        {
            field = first;
            at = scope.Chain().Where(s => !s.InFiles).FirstOrDefault(s => ReportSchema.Question(s.Table, field) is not null)
                ?? scope.Chain().First(s => !s.InFiles);
        }

        if (ReportSchema.Question(at.Table, field) is { } question)
        {
            if (FormCatalog.LocationTypes.Contains(question.Type))
            {
                return new ReportValue(ReportValueKind.Question, at.Record?.Point, question);
            }

            if (ReportSchema.IsFiles(question))
            {
                IReadOnlyList<ReportFile> files = at.Record is { } r
                    ? [.. r.Files.Where(f => string.Equals(f.Keywords, question.Name, StringComparison.OrdinalIgnoreCase)
                        || (f.Keywords.Length == 0 && at.Table.Questions.Count(ReportSchema.IsFiles) == 1))]
                    : [];
                return new ReportValue(ReportValueKind.Files, null, question, files);
            }

            return new ReportValue(ReportValueKind.Question, at.Record?.Values.GetValueOrDefault(question.Name), question);
        }

        if (ReportSchema.LayerFields.Contains(field))
        {
            return new ReportValue(ReportValueKind.Field, at.Record?.Values.GetValueOrDefault(field), Field: field);
        }

        if (ReportSchema.Repeat(at.Table, field) is not null)
        {
            throw new ReportSyntaxException($"'{field}' is a repeat: its rows are written between ${{#{field}}} and ${{/}}, with its questions' placeholders between them.");
        }

        throw new ReportSyntaxException(at.Table.Name.Length == 0 && path.Count == 1
            ? $"'{field}' is not a question of this form."
            : $"'{field}' is not a question of {(at.Table.Name.Length == 0 ? "the form's top level" : $"the repeat '{at.Table.Name}'")}.");
    }

    /// <summary>Evaluates an expression: a name, a literal, or an operation on them.</summary>
    public ReportValue Evaluate(ReportNode node, ReportScope scope)
    {
        switch (node)
        {
            case ReportLiteral literal:
                return ReportValue.Of(literal.Value);
            case ReportName name:
                return Resolve(name, scope);
            case ReportUnary unary:
                ReportValue operand = Evaluate(unary.Operand, scope);
                return unary.Operator == "!"
                    ? ReportValue.Of(!ReportValue.Truthy(Comparable(operand)))
                    : ReportValue.Of(ReportValue.AsNumber(Comparable(operand)) is { } n ? -n : null);
            case ReportBinary binary:
                ReportValue left = Evaluate(binary.Left, scope);

                if (binary.Operator == "&&" && scope.Record is not null && !ReportValue.Truthy(Comparable(left)))
                {
                    return ReportValue.Of(false);
                }

                if (binary.Operator == "||" && scope.Record is not null && ReportValue.Truthy(Comparable(left)))
                {
                    return ReportValue.Of(true);
                }

                ReportValue right = Evaluate(binary.Right, scope);
                return ReportValue.Of(Operate(binary.Operator, Comparable(left), Comparable(right)));
            default:
                throw new ReportSyntaxException("The expression is not one a report reads.");
        }
    }

    /// <summary>What a value is in a comparison or a calculation: a choice's name, a number, a text, a date's milliseconds.</summary>
    private static object? Comparable(ReportValue value) => value.Kind switch
    {
        ReportValueKind.Files or ReportValueKind.File => value.Files is { Count: > 0 } f ? f[0].Name : null,
        ReportValueKind.Feature => value.Position,
        _ => value.Raw switch
        {
            DateTimeOffset or DateTime => ReportValue.AsDate(value.Raw)!.Value.ToUnixTimeMilliseconds(),
            (double X, double Y) => null,
            _ => value.Raw,
        },
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859", Justification = "An operation gives a number, a text or a truth value.")]
    private static object? Operate(string op, object? a, object? b)
    {
        double? x = ReportValue.AsNumber(a);
        double? y = ReportValue.AsNumber(b);
        bool numbers = x is not null && y is not null && a is not string || (x is not null && y is not null && b is not string);

        switch (op)
        {
            case "&&":
                return ReportValue.Truthy(a) && ReportValue.Truthy(b);
            case "||":
                return ReportValue.Truthy(a) || ReportValue.Truthy(b);
            case "==":
                return x is not null && y is not null ? x == y : string.Equals(ReportValue.AsText(a), ReportValue.AsText(b), StringComparison.Ordinal);
            case "!=":
                return x is not null && y is not null ? x != y : !string.Equals(ReportValue.AsText(a), ReportValue.AsText(b), StringComparison.Ordinal);
            case "<" or ">" or "<=" or ">=":
                int order = x is not null && y is not null ? x.Value.CompareTo(y.Value) : string.CompareOrdinal(ReportValue.AsText(a), ReportValue.AsText(b));
                return a is null || b is null ? false : op switch { "<" => order < 0, ">" => order > 0, "<=" => order <= 0, _ => order >= 0 };
            case "+":
                return numbers ? x + y : a is null && b is null ? null : ReportValue.AsText(a) + ReportValue.AsText(b);
            case "-":
                return x is not null && y is not null ? x - y : null;
            case "*":
                return x is not null && y is not null ? x * y : null;
            case "/":
                return x is not null && y is not null && y != 0 ? x / y : null;
            case "%":
                return x is not null && y is not null && y != 0 ? x % y : null;
            default:
                return null;
        }
    }

    // ----------------------------------------------------------------------------------------------------- checking

    /// <summary>Checks a value placeholder's names and methods against the form; throws the refusal.</summary>
    public void Check(ReportTag tag, ReportScope scope)
    {
        ReportValue value = Evaluate(tag.Expression!, scope);

        if (tag.Methods.Count == 0)
        {
            if (value.Kind == ReportValueKind.Feature)
            {
                throw new ReportSyntaxException("$feature is written with getValue: ${$feature | getValue:\"position\"}.");
            }

            if (value.Question is { } q && FormCatalog.LocationTypes.Contains(q.Type))
            {
                throw new ReportSyntaxException($"'{q.Name}' is a location, which Survey123 draws as a map, and a report here does not draw maps yet (ADR-301 §6). Write ${{{q.Name} | getValue:\"x\"}} and ${{{q.Name} | getValue:\"y\"}} for its coordinates.");
            }
        }

        foreach (ReportMethod method in tag.Methods)
        {
            if (CheckMethod(method, value) is { } problem)
            {
                throw new ReportSyntaxException(problem);
            }
        }
    }

    /// <summary>The refusal of a method on a value of this kind, or null.</summary>
    private static string? CheckMethod(ReportMethod method, ReportValue value)
    {
        if (RefusedMethods.TryGetValue(method.Name, out string? why))
        {
            return $"'| {method.Name}' is not supported: {why}.";
        }

        FormQuestion? q = value.Question;
        bool files = value.Kind is ReportValueKind.Files or ReportValueKind.File;
        bool location = q is not null && FormCatalog.LocationTypes.Contains(q.Type);
        string arg = method.Text(0) ?? string.Empty;

        switch (method.Name)
        {
            case "getValue":
                if (files)
                {
                    return Exif.Contains(arg) ? $"getValue:\"{arg}\" reads a photograph's EXIF, which a report here does not read."
                        : FileProperties.Contains(arg) ? null
                        : $"getValue:\"{arg}\" is not a property of a file: the properties are {string.Join(", ", FileProperties.Where(p => p.Length > 0))}.";
                }

                if (value.Kind == ReportValueKind.Feature)
                {
                    return arg == "position" ? null : $"$feature takes getValue:\"position\", not getValue:\"{arg}\".";
                }

                if (location)
                {
                    return arg is "x" or "y" ? null : $"A location takes getValue:\"x\" or getValue:\"y\", not getValue:\"{arg}\".";
                }

                return arg.Length == 0 ? null : $"getValue:\"{arg}\" is for a file, a location or $feature; for an answer write getValue:\"\" for its raw value.";
            case "format":
                return files || location ? $"'| format' is for a number or a date, and '{q?.Name ?? "this"}' is neither."
                    : q is not null && !ReportSchema.IsDate(q) && q.Type is "integer" or "decimal" or "range" && ReportFormat.NumberPatternProblem(arg) is { } bad ? bad
                    : null;
            case "round" or "toFixed":
                return method.Number(0) is { } places && places >= 0 && places <= 15 && places == Math.Floor(places)
                    ? null : $"'| {method.Name}' takes a number of decimal places from 0 to 15, as in {method.Name}:2.";
            case "useGrouping":
                return method.Arguments.Count == 0 || method.Arguments[0] is bool ? null : "'| useGrouping' takes true or false.";
            case "utcOffset":
                return ReportFormat.Offset(arg) is null ? $"utcOffset:\"{arg}\" is not an offset: write \"+01:00\", \"+0100\" or \"+01\"." : null;
            case "locale":
                return !string.Equals(arg, arg.ToLowerInvariant(), StringComparison.Ordinal) ? $"locale:\"{arg}\" is written in lower case, as \"{arg.ToLowerInvariant()}\"."
                    : ReportFormat.Culture(arg) is null ? $"locale:\"{arg}\" is not a language this server knows." : null;
            case "appearance":
                return arg switch
                {
                    "multiline" => null,
                    "bullets" when q?.Type == "select_multiple" => null,
                    "bullets" => "appearance:\"bullets\" is for a select_multiple question.",
                    _ => $"appearance:\"{arg}\" is not one a report here writes: it writes \"multiline\" and, for a select_multiple, \"bullets\".",
                };
            case "checked":
                return ReportSchema.IsSelect(q) ? (arg.Length == 0 ? "'| checked' names the choice it checks, as in checked:\"yes\"." : null)
                    : "'| checked' is for a select_one or select_multiple question.";
            case "countSelected":
                return q?.Type == "select_multiple" ? null : "'| countSelected' is for a select_multiple question.";
            case "selectedAt":
                return q?.Type != "select_multiple" ? "'| selectedAt' is for a select_multiple question."
                    : method.Number(0) is { } n && n >= 0 && n == Math.Floor(n) ? null : "'| selectedAt' takes a position counted from 0, as in selectedAt:0.";
            case "size":
                if (!files)
                {
                    return location ? "'| size' on a location sizes a map, which a report here does not draw yet (ADR-301 §6)."
                        : "'| size' is for a picture: an image question, $file or $attachment.";
                }

                return method.Arguments.Count is >= 1 and <= 4 && method.Arguments.All(a => a is double d && d >= 0 && d <= 10_000)
                    ? null : "'| size' takes width, height and, if wanted, the largest width and height, in pixels from 0 to 10000: size:300:200.";
            case "rotate":
                return !files ? "'| rotate' is for a picture."
                    : method.Number(0) is { } r && r >= 0 && r <= 360 ? null : "'| rotate' takes degrees from 0 to 360.";
            default:
                return $"'| {method.Name}' is not a method a report reads. The methods are getValue, format, round, toFixed, useGrouping, utcOffset, locale, appearance, checked, countSelected, selectedAt, size and rotate.";
        }
    }

    // ------------------------------------------------------------------------------------------------------ writing

    /// <summary>What a value placeholder writes for a record.</summary>
    public ReportOutput Write(ReportTag tag, ReportScope scope)
    {
        ReportValue value = Evaluate(tag.Expression!, scope);
        CultureInfo? culture = null;
        TimeSpan? offset = null;
        bool raw = false;
        string? property = null;
        bool multiline = false;
        bool bullets = false;
        string? numberFormat = null;
        string? dateFormat = null;
        int? round = null;
        int? fixedPlaces = null;
        bool grouping = false;
        string? checkedName = null;
        bool count = false;
        int? selectedAt = null;
        double[]? size = null;
        double rotate = 0;

        foreach (ReportMethod m in tag.Methods)
        {
            switch (m.Name)
            {
                case "locale":
                    culture = ReportFormat.Culture(m.Text(0) ?? string.Empty);
                    break;
                case "utcOffset":
                    offset = ReportFormat.Offset(m.Text(0) ?? string.Empty);
                    break;
                case "getValue":
                    raw = true;
                    property = m.Text(0) ?? string.Empty;
                    break;
                case "appearance":
                    multiline |= m.Text(0) == "multiline";
                    bullets |= m.Text(0) == "bullets";
                    break;
                case "format":
                    numberFormat = dateFormat = m.Text(0) ?? string.Empty;
                    break;
                case "round":
                    round = (int)(m.Number(0) ?? 0);
                    break;
                case "toFixed":
                    fixedPlaces = (int)(m.Number(0) ?? 0);
                    break;
                case "useGrouping":
                    grouping = m.Arguments.Count > 0 && m.Arguments[0] is true;
                    break;
                case "checked":
                    checkedName = m.Text(0);
                    break;
                case "countSelected":
                    count = true;
                    break;
                case "selectedAt":
                    selectedAt = (int)(m.Number(0) ?? 0);
                    break;
                case "size":
                    size = [.. Enumerable.Range(0, 4).Select(i => m.Number(i) ?? 0)];
                    break;
                case "rotate":
                    rotate = m.Number(0) ?? 0;
                    break;
                default:
                    break;
            }
        }

        CultureInfo writing = culture ?? CultureInfo.InvariantCulture;
        FormQuestion? q = value.Question;

        // Files: a picture, or one of its properties.
        if (value.Kind is ReportValueKind.Files or ReportValueKind.File)
        {
            ReportFile? file = value.Files is { Count: > 0 } all ? all[0] : null;

            if (file is null)
            {
                return new ReportText(string.Empty);
            }

            if (property is { Length: > 0 })
            {
                return new ReportText(property switch
                {
                    "name" => file.Name,
                    "size" => file.Size.ToString(CultureInfo.InvariantCulture),
                    "contentType" => file.ContentType,
                    "keywords" => file.Keywords,
                    "id" => file.Id.ToString(CultureInfo.InvariantCulture),
                    "globalId" => file.GlobalId is { } g ? g.ToString("B").ToUpperInvariant() : string.Empty,
                    "width" => ReportPicture.Measure(file.Content) is { } wh ? wh.Width.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    "height" => ReportPicture.Measure(file.Content) is { } hh ? hh.Height.ToString(CultureInfo.InvariantCulture) : string.Empty,
                    "position" => value.Position.ToString(CultureInfo.InvariantCulture),
                    _ => string.Empty,
                });
            }

            // A file that is not a picture Word can hold is written as its name (ADR-301 §5.4, INFERRED).
            return ReportPicture.Measure(file.Content) is null
                ? new ReportText(file.Name)
                : new ReportImage(file, size?[0] ?? 0, size?[1] ?? 0, size?[2] ?? 0, size?[3] ?? 0, rotate);
        }

        if (value.Kind == ReportValueKind.Feature)
        {
            return new ReportText(value.Position.ToString(CultureInfo.InvariantCulture));
        }

        if (q is not null && FormCatalog.LocationTypes.Contains(q.Type))
        {
            return value.Raw is (double x, double y)
                ? new ReportText(ReportFormat.Number(property == "y" ? y : x, writing, grouping: false))
                : new ReportText(string.Empty);
        }

        // Choices.
        if (ReportSchema.IsSelect(q))
        {
            string[] chosen = q!.Type == "select_multiple" ? ReportSchema.Selected(value.Raw)
                : value.Raw is null || ReportValue.AsText(value.Raw).Length == 0 ? [] : [ReportValue.AsText(value.Raw)];

            if (checkedName is not null)
            {
                return new ReportText(chosen.Contains(checkedName, StringComparer.Ordinal) ? "☒" : "☐");
            }

            if (count)
            {
                return new ReportText(chosen.Length.ToString(CultureInfo.InvariantCulture));
            }

            if (selectedAt is { } at)
            {
                return new ReportText(at < chosen.Length ? (raw ? chosen[at] : _schema.ChoiceLabel(q, chosen[at])) : string.Empty);
            }

            string[] shown = raw ? chosen : [.. chosen.Select(c => _schema.ChoiceLabel(q, c))];

            return bullets ? new ReportBullets(shown) : new ReportText(string.Join(", ", shown));
        }

        // Dates: a date question, a date field of the layer, or $date.
        bool isDate = value.Kind == ReportValueKind.Now || ReportSchema.IsDate(q)
            || (value.Field is { } f && ReportSchema.DateFields.Contains(f))
            || (q is null && value.Raw is DateTimeOffset or DateTime);

        if (isDate && ReportValue.AsDate(value.Raw) is { } when)
        {
            bool dateOnly = value.Kind == ReportValueKind.Now ? dateFormat is null : q?.Type is "date" or "today";

            // A date question has no time of day: it is the calendar date it was answered as, in no zone (INFERRED).
            DateTimeOffset shownAt = q?.Type is "date" or "today" ? when.ToOffset(TimeSpan.Zero)
                : when.ToOffset(offset ?? (value.Kind == ReportValueKind.Now ? when.Offset : _offset));

            if (dateFormat is not null)
            {
                return new ReportText(ReportFormat.Date(shownAt, dateFormat, writing));
            }

            if (culture is not null)
            {
                return new ReportText(shownAt.ToString(dateOnly ? "d" : "g", culture));
            }

            return new ReportText(ReportFormat.Date(shownAt, dateOnly ? "YYYY-MM-DD" : "YYYY-MM-DD HH:mm", CultureInfo.InvariantCulture));
        }

        // Numbers, and texts that hold one when a number's method is asked for.
        bool numeric = q?.Type is "integer" or "decimal" or "range" || value.Kind == ReportValueKind.Plain && value.Raw is double;
        bool numberMethod = round is not null || fixedPlaces is not null || numberFormat is not null || grouping;

        if ((numeric || numberMethod) && ReportValue.AsNumber(value.Raw) is { } n && value.Raw is not bool)
        {
            if (round is { } r)
            {
                n = Math.Round(n, r, MidpointRounding.AwayFromZero);
            }

            if (numberFormat is not null && ReportFormat.NumberPatternProblem(numberFormat) is null)
            {
                return new ReportText(ReportFormat.Number(n, numberFormat, writing));
            }

            if (fixedPlaces is { } places)
            {
                return new ReportText(n.ToString((grouping ? "N" : "F") + places.ToString(CultureInfo.InvariantCulture), writing));
            }

            return new ReportText(ReportFormat.Number(n, writing, grouping));
        }

        return new ReportText(ReportValue.AsText(value.Raw), multiline);
    }
}
