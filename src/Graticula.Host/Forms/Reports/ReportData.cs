using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Graticula.Host.Forms.Reports;

/// <summary>A file attached to a record, as the layer's attachments keep it: its bytes read only when a template uses them.</summary>
internal sealed record ReportFile(int Id, string Name, string ContentType, long Size, string Keywords, Guid? GlobalId, byte[]? Content);

/// <summary>
/// One record a report is made from — a row of the form's layer or of one of its repeats' tables, read through the layer's
/// own query as the caller (ADR-301 §5.5) — with its repeats' rows and its attachments.
/// </summary>
internal sealed class ReportRecord
{
    /// <summary>The repeat whose table holds it; empty for the form's top layer.</summary>
    public string Table { get; init; } = string.Empty;

    /// <summary>Its object id.</summary>
    public long ObjectId { get; init; }

    /// <summary>The answers by question name, and the layer's own fields (<c>objectid</c>, <c>created_user</c>, …) by column.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Its point, longitude and latitude, when the table has a location and the row has one.</summary>
    public (double X, double Y)? Point { get; init; }

    /// <summary>Its geometry in EPSG:4326 (longitude, latitude), as its layer keeps it: what a map of it draws.</summary>
    public Graticula.Geometries.Geometry? Shape { get; init; }

    /// <summary>The geometry a map draws: its shape, else its point.</summary>
    public Graticula.Geometries.Geometry? Drawn => Shape ?? (Point is { } p ? new Graticula.Geometries.Point(p.X, p.Y) : null);

    /// <summary>Each repeat's rows that belong to this record, by repeat name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<ReportRecord>> Repeats { get; init; } =
        new Dictionary<string, IReadOnlyList<ReportRecord>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Its attachments, in the order the layer lists them.</summary>
    public IReadOnlyList<ReportFile> Files { get; init; } = [];
}

/// <summary>What a template is checked and filled against: the published form's tables, questions and choice labels.</summary>
internal sealed class ReportSchema
{
    /// <summary>Fields every form layer has beside its questions, which a template may name (ADR-301 §5.2, INFERRED).</summary>
    public static readonly FrozenSet<string> LayerFields = new[]
    {
        "objectid", "globalid", "parentglobalid", "created_user", "created_date", "last_edited_user", "last_edited_date",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The layer fields that hold a date.</summary>
    public static readonly FrozenSet<string> DateFields = new[] { "created_date", "last_edited_date" }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public ReportSchema(FormDefinition definition, string topName)
    {
        ArgumentNullException.ThrowIfNull(definition);
        Definition = definition;
        TopName = topName ?? string.Empty;
        Language = definition.Settings.DefaultLanguage is { Length: > 0 } chosen && chosen != "default"
            ? chosen
            : (definition.Languages.Count > 0 ? definition.Languages[0] : string.Empty);
    }

    public FormDefinition Definition { get; }

    /// <summary>The name a template may give the top layer, as in <c>${mainLayer.field}</c>: the layer's name.</summary>
    public string TopName { get; }

    /// <summary>The language labels are written in: the form's default language, as its layer's aliases are.</summary>
    public string Language { get; }

    public FormTable Root => Definition.Root;

    public static FormQuestion? Question(FormTable table, string name) =>
        table.Questions.FirstOrDefault(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase));

    public static FormTable? Repeat(FormTable table, string name) =>
        table.Repeats.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A label in the form's language, then the unlabelled column, then any language, then the fallback.</summary>
    public string Label(IReadOnlyDictionary<string, string>? labels, string fallback)
    {
        if (labels is null || labels.Count == 0)
        {
            return fallback;
        }

        return labels.TryGetValue(Language, out string? own) && own.Length > 0 ? own
            : labels.TryGetValue(string.Empty, out string? plain) && plain.Length > 0 ? plain
            : labels.Values.FirstOrDefault(v => v.Length > 0) ?? fallback;
    }

    /// <summary>A choice's label, or its name when the list does not have it.</summary>
    public string ChoiceLabel(FormQuestion question, string name)
    {
        if (question.Search is null && question.Argument is { } list
            && Definition.ChoiceLists.TryGetValue(list, out IReadOnlyList<FormChoice>? choices)
            && choices.FirstOrDefault(c => c.Name == name) is { } choice)
        {
            return Label(choice.Labels, name);
        }

        return name;
    }

    public static bool IsSelect(FormQuestion? q) => q is not null && q.Type is "select_one" or "select_multiple";

    public static bool IsFiles(FormQuestion? q) => q is not null && FormCatalog.AttachmentTypes.Contains(q.Type);

    public static bool IsDate(FormQuestion? q) => q is not null && q.Type is "date" or "dateTime" or "datetime" or "start" or "end" or "today";

    /// <summary>The names a select_multiple's answer holds: stored joined by commas (ADR-287 §5.10), or by spaces.</summary>
    public static string[] Selected(object? raw) =>
        raw is null ? [] : Convert.ToString(raw, CultureInfo.InvariantCulture)!.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Where a placeholder is read: a table and, when filling, a record of it, its position, and a file.</summary>
internal sealed class ReportScope
{
    public ReportScope(ReportScope? parent, FormTable table, string name, ReportRecord? record, int position, ReportFile? file, bool inFiles)
    {
        Parent = parent;
        Table = table;
        Name = name;
        Record = record;
        Position = position;
        File = file;
        InFiles = inFiles;
    }

    public ReportScope? Parent { get; }

    public FormTable Table { get; }

    /// <summary>The table's name as a template writes it: the repeat's, or the top layer's.</summary>
    public string Name { get; }

    /// <summary>The record, when filling; null when checking.</summary>
    public ReportRecord? Record { get; }

    /// <summary>Its 1-based position among the rows or files of the section it is in.</summary>
    public int Position { get; }

    /// <summary>The file of a <c>${#image}</c> or <c>${#$attachment}</c> section, when filling.</summary>
    public ReportFile? File { get; }

    /// <summary>Whether this scope is a section over files, where <c>$file</c> may be named.</summary>
    public bool InFiles { get; }

    /// <summary>
    /// For a summary section (<c>$&lt;$summary&gt;</c>), the records the report is made of; empty when checking, null for any
    /// other scope.
    /// </summary>
    public IReadOnlyList<ReportRecord>? Inputs { get; init; }

    /// <summary>A row of statistics or of distinct values that a section repeats over, when filling.</summary>
    public IReadOnlyDictionary<string, object?>? Row { get; init; }

    /// <summary>The names such a row holds — the statistics' output names and the fields grouped by — known when checking.</summary>
    public IReadOnlySet<string>? RowNames { get; init; }

    /// <summary>Whether this is a summary section's scope.</summary>
    public bool IsSummary => Inputs is not null;

    /// <summary>Whether a record is being written, rather than the template checked.</summary>
    public bool Filling => Chain().Any(s => s.Record is not null || s.Inputs is { Count: > 0 } || s.Row is not null);

    public IEnumerable<ReportScope> Chain()
    {
        for (ReportScope? s = this; s is not null; s = s.Parent)
        {
            yield return s;
        }
    }
}

/// <summary>What a placeholder's name stands for.</summary>
internal enum ReportValueKind
{
    Plain,
    Question,
    Field,
    Files,
    File,
    Feature,
    Now,

    /// <summary>A record's geometry, <c>$shape</c>, or a map of no question, <c>$map</c>: drawn as a map.</summary>
    Shape,

    /// <summary>A layer or table of the form — its name, a repeat's, or <c>$layers[…]</c> — summarised with <c>| stats</c>.</summary>
    Layer,
}

/// <summary>A value as a placeholder's expression gives it, with what it came from.</summary>
internal readonly record struct ReportValue(ReportValueKind Kind, object? Raw, FormQuestion? Question = null, IReadOnlyList<ReportFile>? Files = null, int Position = 0, string? Field = null)
{
    public static ReportValue Of(object? raw) => new(ReportValueKind.Plain, raw);

    public static double? AsNumber(object? raw) => raw switch
    {
        null => null,
        double d => d,
        float f => f,
        decimal m => (double)m,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        bool b => b ? 1 : 0,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) => p,
        _ => null,
    };

    public static DateTimeOffset? AsDate(object? raw) => raw switch
    {
        DateTimeOffset o => o,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, d.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : d.Kind)),
        DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        long ms => DateTimeOffset.FromUnixTimeMilliseconds(ms),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset p) => p,
        _ => null,
    };

    public static string AsText(object? raw) => raw switch
    {
        null => string.Empty,
        string s => s,
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###############", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("0.###############", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTimeOffset o => o.ToString("yyyy-MM-dd'T'HH:mm:ssK", CultureInfo.InvariantCulture),
        DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        Guid g => g.ToString("B").ToUpperInvariant(),
        _ => Convert.ToString(raw, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    public static bool Truthy(object? raw) => raw switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        _ when AsNumber(raw) is { } n => n != 0 && !double.IsNaN(n),
        _ => true,
    };
}
