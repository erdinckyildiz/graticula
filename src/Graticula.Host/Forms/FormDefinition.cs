using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Graticula.Host.Forms;

/// <summary>
/// What a form model means, and whether this server can honour it — ADR-287 §5.3 and §5.10.
/// </summary>
/// <remarks>
/// <para>
/// <b>The rule that sorts every cell</b> (§5.10): what changes what is stored, or which answer is valid, is supported
/// or refused with its row and column; what changes only how a question looks may be drawn the default way, and is then
/// named in a notice. Nothing is accepted and quietly ignored. <see cref="Check"/> reports every refusal it finds, not
/// only the first, so a publisher fixes a workbook once.
/// </para>
/// <para>
/// <b>Run on every save</b> (§5.3): an upload, a designer save and an API write all come through here, so the designer
/// cannot reach a state an upload would refuse.
/// </para>
/// </remarks>
public sealed partial class FormDefinition
{
    /// <summary>Field names the layer keeps for itself; a question may not take one.</summary>
    public static readonly IReadOnlySet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "objectid", "globalid", "parentglobalid", "shape", "created_user", "created_date", "last_edited_user",
        "last_edited_date",
    };

    private FormDefinition(
        FormTable root,
        IReadOnlyDictionary<string, IReadOnlyList<FormChoice>> choiceLists,
        IReadOnlyList<string> languages,
        FormSettings settings)
    {
        Root = root;
        ChoiceLists = choiceLists;
        Languages = languages;
        Settings = settings;
    }

    /// <summary>The form's top level: the layer. Its repeats are the related tables.</summary>
    public FormTable Root { get; }

    /// <summary>The choice lists, by list name.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<FormChoice>> ChoiceLists { get; }

    /// <summary>The languages the labels are given in, in column order; empty when labels have no language.</summary>
    public IReadOnlyList<string> Languages { get; }

    /// <summary>The settings sheet.</summary>
    public FormSettings Settings { get; }

    /// <summary>
    /// The layer addresses the form's search() appearances and pulldata("@layer") calls write out in full, with where; each
    /// is checked at publish to be a layer of this server the publisher can read (§5.23).
    /// </summary>
    public IReadOnlyList<QueriedLayer> QueriedLayers { get; private init; } = [];

    /// <summary>Every question, in form order, at every depth.</summary>
    public IEnumerable<FormQuestion> AllQuestions => Root.AllTables.SelectMany(t => t.Questions);

    /// <summary>Checks a model and, when nothing is refused, gives its definition.</summary>
    public static FormCheck Check(FormModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Checker checker = new(model);
        return checker.Run();
    }

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_.\-]*$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^(attributes\.[^\s.]+|geometry(\.[A-Za-z]+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"\(tr\)|türkçe|turkish", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TurkishLanguage();

    /// <summary>The name of the choice <c>or_other</c> adds, as XLSForm names it.</summary>
    public const string AutomaticOther = "other";

    private sealed class Checker(FormModel model)
    {
        private readonly List<FormIssue> refusals = [];
        private readonly List<FormIssue> notices = [];
        private readonly Dictionary<string, FormQuestion> names = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Sheet, int Row, string Column, string Expression)> expressions = [];
        private readonly Dictionary<int, FormSearch> searches = [];
        private readonly List<QueriedLayer> queried = [];
        private bool pagesStyle;
        private bool gridStyle;

        public FormCheck Run()
        {
            ModelSheet? survey = model.Sheet("survey");

            if (survey is null)
            {
                Refuse("survey", 0, null, "A form needs a sheet named 'survey'.");
                return new FormCheck(null, refusals, notices);
            }

            List<string> languages = [];
            Dictionary<string, IReadOnlyList<FormChoice>> lists = Choices(model.Sheet("choices"), languages);
            FormSettings settings = ReadSettings(model.Sheet("settings"));
            pagesStyle = settings.Pages;
            gridStyle = settings.Grid is not null;
            FormTable root = Survey(survey, lists, languages);
            DuplicateChoices(lists, root, languages);

            foreach ((string sheet, int row, string column, string expression) in expressions)
            {
                Expression(sheet, row, column, expression, lists);
            }

            if (settings.DefaultLanguage is { Length: > 0 } named && languages.Count > 0 && !languages.Contains(named, StringComparer.Ordinal)
                && languages.Where(l => l.EndsWith(')') && l.Contains(" (", StringComparison.Ordinal) && l[..l.LastIndexOf(" (", StringComparison.Ordinal)].Trim() == named.Trim()).ToList() is [var full])
            {
                // ADR-287 §5.26, INFERRED: 'English' where the label columns say 'English (en)' names one language without
                // its code; it is read as that language, and the upload says so.
                Notice("settings", 2, "default_language", $"default_language '{named}' is read as '{full}', the label language it names without its code.");
                settings = settings with { DefaultLanguage = full };
            }

            if (settings.DefaultLanguage is { Length: > 0 } language && languages.Count > 0
                && !languages.Contains(language, StringComparer.Ordinal) && language != "default")
            {
                Refuse("settings", 2, "default_language",
                    $"default_language '{language}' is not one of the label languages: {string.Join(", ", languages)}.");
            }

            FormDefinition? definition = refusals.Count == 0 ? new FormDefinition(root, lists, languages, settings) { QueriedLayers = queried } : null;
            return new FormCheck(definition, refusals, notices);
        }

        private FormTable Survey(ModelSheet sheet, Dictionary<string, IReadOnlyList<FormChoice>> lists, List<string> languages)
        {
            Columns columns = new(sheet);
            ClassifyColumns(sheet, columns, "survey", languages, SurveyColumn);

            int typeColumn = sheet.ColumnIndex("type");
            int nameColumn = sheet.ColumnIndex("name");

            if (typeColumn < 0 || nameColumn < 0)
            {
                Refuse("survey", 1, null, "The survey sheet needs a 'type' and a 'name' column.");
                return new FormTable("", null, 0, null, [], [], [], null, []);
            }

            Stack<Frame> open = new();
            open.Push(new Frame(FrameKind.Root, "", 0, [], [], null, null, [], null, []));
            Dictionary<Frame, int> locations = new(ReferenceEqualityComparer.Instance);

            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                IReadOnlyList<string> row = sheet.Rows[r];
                int at = r + 2;
                string rawType = ModelSheet.Cell(row, typeColumn).Trim();

                if (row.All(c => c.Trim().Length == 0))
                {
                    continue;
                }

                if (rawType.Length == 0)
                {
                    Refuse("survey", at, "type", "This row has cells but no type.");
                    continue;
                }

                bool phoneNumber = false;

                // ADR-287 §5.26: pyxform's other spellings of a type (string, int, trigger, imei, phone number…) are read as
                // the type they spell, and the upload says so.
                if (FormCatalog.TypeSpellings.TryGetValue(string.Join(' ', rawType.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant(), out string? respelled))
                {
                    phoneNumber = rawType.Trim().Equals("phone number", StringComparison.OrdinalIgnoreCase);

                    if (!FormCatalog.RefusedTypes.ContainsKey(respelled))
                    {
                        Notice("survey", at, "type", phoneNumber
                            ? "'phone number' is pyxform's text question that takes digits only; it is read as text with that constraint, unless the row has its own."
                            : $"'{rawType.Trim()}' is pyxform's spelling of '{respelled}' and is read as that.");
                    }

                    rawType = respelled;
                }

                (string type, string? argument, string? problem, bool orOther) = ParseTypeFull(rawType);

                if (problem is not null)
                {
                    Refuse("survey", at, "type", problem);
                    continue;
                }

                if (type is "begin group" or "begin repeat" && argument is not null)
                {
                    // ADR-287 §5.26: pyxform reads a word after 'begin group' or 'begin repeat' as a list name, which only
                    // 'begin loop' uses; on a group or a repeat it is dropped. A real Survey123 form writes 'begin group pressure'.
                    Notice("survey", at, "type", $"'{rawType.Trim()}' is read as '{type}': the word after it names a list, which only a loop uses, and it is ignored as pyxform ignores it.");
                    argument = null;
                }

                if (type is "end group" or "end repeat")
                {
                    FrameKind expected = type == "end group" ? FrameKind.Group : FrameKind.Repeat;

                    if (open.Peek().Kind != expected)
                    {
                        Refuse("survey", at, "type", open.Peek().Kind == FrameKind.Root
                            ? $"'{type}' closes nothing."
                            : $"'{type}' closes the {open.Peek().Kind.ToString().ToLowerInvariant()} '{open.Peek().Name}' opened on row {open.Peek().Row}.");
                        continue;
                    }

                    Frame closed = open.Pop();

                    if (closed.Kind == FrameKind.Repeat)
                    {
                        FormTable repeat = new(closed.Name, closed.Label, closed.Row, closed.RepeatCount,
                            closed.Questions, closed.Repeats, closed.Body, closed.Relevant, closed.Appearance)
                        {
                            AllowAdds = closed.AllowAdds,
                            AllowUpdates = closed.AllowUpdates,
                        };
                        open.Peek().Repeats.Add(repeat);
                        open.Peek().Body.Add(repeat);
                    }
                    else
                    {
                        if (closed.Appearance.Contains("table-list", StringComparer.Ordinal))
                        {
                            TableList(closed);
                        }

                        // A group draws a screen; its questions belong to the table around it, and the body keeps the
                        // group so the client can hide it as one and give it a page.
                        open.Peek().Questions.AddRange(closed.Questions);
                        open.Peek().Repeats.AddRange(closed.Repeats);
                        open.Peek().Body.Add(new FormGroup(closed.Name, closed.Label ?? new Dictionary<string, string>(), closed.Row,
                            closed.Relevant, closed.Appearance, closed.Body));
                    }

                    continue;
                }

                string name = ModelSheet.Cell(row, nameColumn).Trim();
                Dictionary<string, string> labels = columns.Translations(row, "label");

                // ADR-287 §5.21 (INFERRED): a note with no name and no calculation stores nothing, and Esri's own published
                // samples carry such rows, so it is given a name from its row instead of refused. The workbook and the model
                // keep the row as written; only the definition holds the name. A note that calculates makes a field, and a
                // field needs a name its publisher chose, so that one is still refused.
                // §5.26 widens it to a note that calculates: pyxform names every unnamed note (generated_note_name_…) and
                // Esri's Calculate-location-from-CSV sample carries one, so it is named the same way and stores nothing — a
                // field needs a name its publisher chose. One that asks for a field (bind::esri:fieldType) is still refused.
                bool unnamedNote = name.Length == 0 && type == "note"
                    && (columns.Value(row, "bind::esri:fieldType") is null || columns.Value(row, "bind::esri:fieldType")!.Equals("null", StringComparison.OrdinalIgnoreCase));

                if (unnamedNote)
                {
                    name = $"note_row_{at}";

                    while (names.ContainsKey(name) || frameNames.Contains(name))
                    {
                        name = "_" + name;
                    }

                    Notice("survey", at, "name", $"This note has no name; it is shown, stores nothing, and is called '{name}' here.");
                }

                if (!Named(name, at))
                {
                    continue;
                }

                Appearance(row, columns, type, at);

                if (type is "begin group" or "begin repeat")
                {
                    string? count = columns.Value(row, "repeat_count");

                    if (type == "begin group" && count is not null)
                    {
                        Refuse("survey", at, "repeat_count", "A group does not repeat; repeat_count belongs on 'begin repeat'.");
                    }

                    if (count is not null && !int.TryParse(count, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    {
                        expressions.Add(("survey", at, "repeat_count", count));
                    }

                    if (columns.Translations(row, "media::image").Count + columns.Translations(row, "image").Count > 0)
                    {
                        Refuse("survey", at, "media::image", $"A picture belongs on a question or a choice; the {(type == "begin group" ? "group" : "repeat")} '{name}' is drawn as a heading and cannot carry one.");
                    }

                    if (columns.Translations(row, "media::audio").Count + columns.Translations(row, "audio").Count > 0)
                    {
                        Refuse("survey", at, "media::audio", $"A recording belongs on a question or a choice; the {(type == "begin group" ? "group" : "repeat")} '{name}' is drawn as a heading and cannot carry one.");
                    }

                    if (columns.Value(row, "bind::esri:warning") is not null)
                    {
                        Refuse("survey", at, "bind::esri:warning", "A warning is about an answer; a group or a repeat has none.");
                    }

                    if (columns.Translations(row, "guidance_hint").Count > 0)
                    {
                        Refuse("survey", at, "guidance_hint", $"A guidance_hint belongs on a question; the {(type == "begin group" ? "group" : "repeat")} '{name}' has no hint to expand.");
                    }

                    Relevance(row, columns, at);
                    (bool allowAdds, bool allowUpdates) = type == "begin repeat" ? RepeatParameters(row, columns, at) : (true, false);
                    open.Push(new Frame(type == "begin group" ? FrameKind.Group : FrameKind.Repeat, name, at, [], [],
                        labels.Count > 0 ? labels : null, count, [], columns.Value(row, "relevant"),
                        (columns.Value(row, "appearance") ?? string.Empty).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
                        allowAdds, allowUpdates));
                    continue;
                }

                if (FormCatalog.RefusedTypes.TryGetValue(type, out string? why))
                {
                    Refuse("survey", at, "type", $"'{type}' is refused: {why}.");
                    continue;
                }

                bool select = type is "select_one" or "select_multiple";
                bool fromFile = type is "select_one_from_file" or "select_multiple_from_file";
                bool known = select || fromFile || FormCatalog.InputTypes.Contains(type)
                    || FormCatalog.ValueTypes.Contains(type) || FormCatalog.MetadataTypes.Contains(type);

                if (!known)
                {
                    Refuse("survey", at, "type", $"'{rawType}' is not a question type this server knows.");
                    continue;
                }

                if (argument is not null && !select && !fromFile)
                {
                    Refuse("survey", at, "type", $"'{type}' takes nothing after it, and this row says '{rawType}'.");
                    continue;
                }

                if (select && (argument is null || !lists.ContainsKey(argument)))
                {
                    Refuse("survey", at, "type", argument is null
                        ? $"'{type}' needs a choice list name after it."
                        : $"The choice list '{argument}' is not on the choices sheet.");
                }

                if (fromFile && (argument is null || !argument.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                {
                    Refuse("survey", at, "type", argument is null
                        ? $"'{type}' needs a file name after it."
                        : $"'{argument}' is refused: choices from a file are read from a CSV in the first version, not from XML or GeoJSON.");
                }

                bool needsLabel = select || fromFile || FormCatalog.InputTypes.Contains(type);

                bool hintIsLabel = false;

                if (needsLabel && labels.Count == 0)
                {
                    // ADR-287 §5.26: pyxform refuses an element only when it has neither a label nor a hint ("has no label or
                    // hint"); with a hint alone the hint is what is shown, so here it becomes the label, and the upload says so.
                    if (columns.Translations(row, "hint").Count > 0)
                    {
                        hintIsLabel = true;
                        labels = columns.Translations(row, "hint");
                        Notice("survey", at, "label", $"The question '{name}' has no label; its hint is shown as its label.");
                    }
                    else
                    {
                        Refuse("survey", at, "label", $"The question '{name}' has no label or hint.");
                    }
                }

                if (FormCatalog.LocationTypes.Contains(type))
                {
                    // One location per table (owner decision 2026-11-09): the top level's is the layer's geometry, a
                    // repeat's makes that repeat a related layer rather than a table. A group belongs to the table
                    // around it, so the table is the nearest repeat, or the top level.
                    Frame table = open.First(f => f.Kind != FrameKind.Group);
                    locations.TryGetValue(table, out int seen);
                    locations[table] = seen + 1;

                    if (seen > 0)
                    {
                        Refuse("survey", at, "type", table.Kind == FrameKind.Root
                            ? $"'{name}' is a second location question at the top level; it has at most one, which becomes the layer's geometry."
                            : $"'{name}' is a second location question in the repeat '{table.Name}'; a repeat has at most one, which becomes its geometry.");
                    }
                }

                if (type is "select_one" or "select_multiple" && argument is not null
                    && lists.TryGetValue(argument, out IReadOnlyList<FormChoice>? offered) && offered.Any(c => c.Images.Count > 0)
                    && (columns.Value(row, "appearance") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(a => a.Equals("minimal", StringComparison.OrdinalIgnoreCase) || a.Equals("autocomplete", StringComparison.OrdinalIgnoreCase)) is { } listAppearance)
                {
                    Refuse("survey", at, "appearance", listAppearance.Equals("minimal", StringComparison.OrdinalIgnoreCase)
                        ? $"The choices of '{argument}' have pictures, and a drop-down (minimal) cannot show them; take minimal off '{name}'."
                        : $"The choices of '{argument}' have pictures, and a type-ahead list (autocomplete) cannot show them; take autocomplete off '{name}'.");
                }

                if (orOther && argument is not null && lists.ContainsKey(argument))
                {
                    AddOther(lists, argument, languages, at);
                }

                FormQuestion question = Question(row, columns, type, argument, name, labels, at);

                if (hintIsLabel)
                {
                    question = question with { Hints = new Dictionary<string, string>(StringComparer.Ordinal) };
                }

                if (unnamedNote)
                {
                    question = question with { EsriFieldType = "null" };
                }

                if (phoneNumber)
                {
                    // pyxform's 'phone number': a text input bound with regex(., '^\d*$') and the hint "Enter numbers only.";
                    // the row's own constraint and hint win, as a row's columns win over pyxform's defaults.
                    question = question with
                    {
                        Constraint = question.Constraint ?? @"regex(., '^\d*$')",
                        Hints = question.Hints.Count > 0 ? question.Hints : new Dictionary<string, string>(StringComparer.Ordinal) { [string.Empty] = "Enter numbers only." },
                    };
                }

                names[name] = question;

                if (question.Search is not null)
                {
                    SearchList(question, lists, at);
                }
                open.Peek().Questions.Add(question);
                open.Peek().Body.Add(question);

                if (orOther && argument is not null)
                {
                    FormQuestion other = SpecifyOther(name, at, languages);

                    if (names.ContainsKey(other.Name) || frameNames.Contains(other.Name))
                    {
                        Refuse("survey", at, "type", $"'or_other' adds a text question named '{other.Name}', and that name is already taken.");
                    }
                    else
                    {
                        names[other.Name] = other;
                        open.Peek().Questions.Add(other);
                        open.Peek().Body.Add(other);
                        Notice("survey", at, "type", $"or_other adds the choice 'other' to the list and a text question '{other.Name}' that is asked when it is chosen.");
                    }
                }
            }

            while (open.Count > 1)
            {
                Frame left = open.Pop();
                Refuse("survey", left.Row, "type", $"The {left.Kind.ToString().ToLowerInvariant()} '{left.Name}' opened on row {left.Row} is never closed.");
            }

            Frame top = open.Pop();
            return new FormTable("", null, 0, null, top.Questions, top.Repeats, top.Body, null, []);
        }

        /// <summary>
        /// <c>select_one list or_other</c> and <c>select_multiple list or_other</c> (XLSForm's, and Survey123's): the list
        /// gains a choice named <c>other</c>, and a text question <c>{name}_other</c> follows, asked when it is chosen.
        /// The choice is added to the list itself, so every question over the list offers it. That is the one choice
        /// that keeps republishing working: a layer's coded-value domain is the list's, republishing adds the new code to
        /// the domain that exists, and a copy of the list under another name would be a second domain that the field
        /// already made does not follow. A list that already has a choice named <c>other</c> is left as it is.
        /// INFERRED (ADR-287 §5.19): that this is what XLSForm's own tools do with a shared list, which was not read.
        /// </summary>
        private static void AddOther(Dictionary<string, IReadOnlyList<FormChoice>> lists, string list, List<string> languages, int at)
        {
            IReadOnlyList<FormChoice> choices = lists[list];

            if (choices.Any(c => c.Name == AutomaticOther))
            {
                return;
            }

            Dictionary<string, string> filters = new(StringComparer.OrdinalIgnoreCase);

            foreach (string column in choices.SelectMany(c => c.Filters.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                filters[column] = string.Empty;
            }

            lists[list] = [.. choices, new FormChoice(AutomaticOther, Words(languages, "Other", "Diğer"), filters, at) { Automatic = true }];
        }

        private static FormQuestion SpecifyOther(string name, int at, List<string> languages) =>
            new($"{name}_other", "text", null, Words(languages, "Specify other", "Diğerini belirtin"), at,
                Required: null,
                Relevant: $"selected(${{{name}}}, '{AutomaticOther}')",
                Constraint: null, Calculation: null, Default: null, ReadOnly: null, ChoiceFilter: null,
                Appearance: [], Parameters: new Dictionary<string, string>(), EsriFieldType: null, FieldLength: null, FieldAlias: null)
            {
                Automatic = true,
            };

        /// <summary>A label in every language the form has: Turkish for a language that says so, English for the rest.</summary>
        private static Dictionary<string, string> Words(List<string> languages, string english, string turkish)
        {
            Dictionary<string, string> words = new(StringComparer.Ordinal) { [string.Empty] = english };

            foreach (string language in languages)
            {
                words[language] = TurkishLanguage().IsMatch(language) ? turkish : english;
            }

            return words;
        }

        /// <summary>
        /// Duplicate choice names in one list (§5.19 (4)). XLSForm's own tools refuse them unless the settings say
        /// <c>allow_choice_duplicates</c>; Survey123 does not, and its published documentation (the page on multiple
        /// choice) says duplicates are supported for <c>select_one</c> and not for <c>select_multiple</c> or for a
        /// multilingual survey. So a duplicate is accepted, with a notice that says what becomes of it, when every use of
        /// the list is a <c>select_one</c> in a form of one language, or when the settings say so; and refused with that
        /// reason otherwise. The first choice with a name is the one whose label the layer's domain keeps.
        /// </summary>
        private void DuplicateChoices(Dictionary<string, IReadOnlyList<FormChoice>> lists, FormTable root, List<string> languages)
        {
            bool allowed = string.Equals(
                FirstSetting(model.Sheet("settings"), "allow_choice_duplicates"), "yes", StringComparison.OrdinalIgnoreCase);
            List<FormQuestion> selects = [.. root.AllTables.SelectMany(t => t.Questions).Where(q => q.Type is "select_one" or "select_multiple")];

            foreach ((string list, IReadOnlyList<FormChoice> choices) in lists)
            {
                FormChoice? second = choices.Where((c, i) => choices.Take(i).Any(e => e.Name == c.Name)).FirstOrDefault();

                if (second is null)
                {
                    continue;
                }

                FormChoice first = choices.First(c => c.Name == second.Name);
                string where = $"'{second.Name}' is a second choice of that name in the list '{list}' (the first is on row {first.Row})";
                FormQuestion? multiple = selects.FirstOrDefault(q => q.Argument == list && q.Type == "select_multiple");

                if (!allowed && multiple is not null)
                {
                    Refuse("choices", second.Row, "name", $"{where}, and the list is used by the select_multiple '{multiple.Name}' (row {multiple.Row}); Survey123 does not support duplicate choice names there. Rename one, or set allow_choice_duplicates to yes if that is meant.");
                }
                else if (!allowed && languages.Count > 0)
                {
                    Refuse("choices", second.Row, "name", $"{where}, and the form has labels in more than one language; Survey123 does not support duplicate choice names in a multilingual survey. Rename one, or set allow_choice_duplicates to yes if that is meant.");
                }
                else
                {
                    Notice("choices", second.Row, "name", $"{where}. Both are offered, they are stored as one answer, and the layer's domain keeps the label of the first.");
                }
            }
        }

        private bool Named(string name, int at)
        {
            if (name.Length == 0)
            {
                Refuse("survey", at, "name", "This row has no name.");
                return false;
            }

            if (!NamePattern().IsMatch(name))
            {
                Refuse("survey", at, "name", $"'{name}' is not a valid name: it starts with a letter or '_' and holds letters, digits, '_', '-' and '.'.");
                return false;
            }

            if (ReservedNames.Contains(name))
            {
                Refuse("survey", at, "name", $"'{name}' is a field the layer keeps for itself.");
                return false;
            }

            if (names.TryGetValue(name, out FormQuestion? first) || frameNames.Contains(name))
            {
                Refuse("survey", at, "name", first is not null
                    ? $"'{name}' is already the name of row {first.Row}; names are compared ignoring case."
                    : $"'{name}' is already the name of a group or repeat.");
                return false;
            }

            frameNames.Add(name);
            return true;
        }

        private readonly HashSet<string> frameNames = new(StringComparer.OrdinalIgnoreCase);

        private FormQuestion Question(IReadOnlyList<string> row, Columns columns, string type, string? argument, string name,
            Dictionary<string, string> labels, int at)
        {
            string? required = columns.Value(row, "required");
            string? readOnly = columns.Value(row, "read_only") ?? columns.Value(row, "readonly");

            Flag(required, "required", at);
            Flag(readOnly, columns.Value(row, "read_only") is null ? "readonly" : "read_only", at);

            foreach (string column in (string[])["constraint", "calculation", "choice_filter", "bind::esri:warning"])
            {
                if (columns.Value(row, column) is { } expression)
                {
                    expressions.Add(("survey", at, column, expression));
                }
            }

            Relevance(row, columns, at);

            if (columns.Value(row, "default") is { } fallback && IsFormula(fallback))
            {
                expressions.Add(("survey", at, "default", fallback));
            }

            // Survey123's media page: on draw and annotate a default is a picture from the media folder, drawn over. That
            // is not built (ADR-287 §5.24), and the client would have answered with the picture's name and no file.
            if (type == "image" && columns.Value(row, "default") is not null)
            {
                Refuse("survey", at, "default", "A default picture on an image question is refused: drawing or annotating over a picture from the form's media is not built, and the answer would be a file name with no file.");
            }

            if (type == "calculate" && columns.Value(row, "calculation") is null)
            {
                Refuse("survey", at, "calculation", $"The calculate '{name}' has no calculation.");
            }

            if (columns.Value(row, "choice_filter") is not null && type is not ("select_one" or "select_multiple" or "select_one_from_file" or "select_multiple_from_file"))
            {
                Refuse("survey", at, "choice_filter", "A choice_filter belongs on a select question.");
            }

            if (columns.Translations(row, "bind::esri:warning_message").Count > 0 && columns.Value(row, "bind::esri:warning") is null)
            {
                Refuse("survey", at, "bind::esri:warning_message", "A warning message is shown when the bind::esri:warning expression is false, and this row has none.");
            }

            if (type == "note" && columns.Value(row, "bind::esri:warning") is not null)
            {
                Refuse("survey", at, "bind::esri:warning", "A warning is about an answer, and a note has none to warn about.");
            }
            else if ((FormCatalog.ValueTypes.Contains(type) || FormCatalog.MetadataTypes.Contains(type)) && columns.Value(row, "bind::esri:warning") is not null)
            {
                Refuse("survey", at, "bind::esri:warning", $"A warning is shown beside a question, and a '{type}' is not shown; put it on the question the value comes from.");
            }

            Dictionary<string, string> parameters = Parameters(columns.Value(row, "parameters"), type, at);
            (string? fieldType, int? length, string? alias) = Esri(row, columns, type, at);
            fieldType = BindType(row, columns, type, fieldType, at);
            (string? calculationMode, int? maxDuration) = EsriParameters(row, columns, type, at);
            string? inputMask = InputMask(row, columns, type, length, at);

            return new FormQuestion(
                name, type, argument, labels, at,
                Required: required,
                Relevant: columns.Value(row, "relevant"),
                Constraint: columns.Value(row, "constraint"),
                Calculation: columns.Value(row, "calculation"),
                Default: columns.Value(row, "default"),
                ReadOnly: readOnly,
                ChoiceFilter: columns.Value(row, "choice_filter"),
                Appearance: WithoutSearch(columns.Value(row, "appearance")).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Parameters: parameters,
                EsriFieldType: fieldType,
                FieldLength: length,
                FieldAlias: alias)
            {
                Hints = columns.Translations(row, "hint"),
                GuidanceHints = columns.Translations(row, "guidance_hint"),
                Images = Pictures(columns, row, "survey", at),
                RequiredMessages = columns.Translations(row, "required_message"),
                ConstraintMessages = columns.Translations(row, "constraint_message"),
                Visible = columns.Value(row, "body::esri:visible"),
                AccuracyThreshold = columns.Value(row, "body::accuracyThreshold"),
                Accept = columns.Value(row, "body::accept"),
                CalculationMode = calculationMode,
                InputMask = inputMask,
                Search = searches.GetValueOrDefault(at),
                Warning = columns.Value(row, "bind::esri:warning"),
                WarningMessages = columns.Translations(row, "bind::esri:warning_message"),
                Audio = Sounds(columns, row, "survey", at),
                MaxDuration = maxDuration ?? (type == "audio" ? FormCatalog.DefaultMaxDurationSeconds : null),
            };
        }

        /// <summary>
        /// <c>bind::type</c> (ADR-287 §5.26): the XForms data type pyxform writes through to the bind, which Survey123's older
        /// template carried as a column. ODK's XForms specification lists the types. On a calculate or a hidden question it
        /// says what the value is, so where no <c>bind::esri:fieldType</c> is given it chooses the field (int an integer,
        /// decimal a double, string text); where one is given the two must agree. On any other question it must be the type
        /// the question already has; anything else would change what is stored and is refused.
        /// </summary>
        private string? BindType(IReadOnlyList<string> row, Columns columns, string type, string? fieldType, int at)
        {
            if (columns.Value(row, "bind::type") is not { } written)
            {
                return fieldType;
            }

            string bind = written.Trim().ToLowerInvariant() switch
            {
                "integer" => "int",
                "datetime" => "datetime",
                var other => other,
            };

            if (type is "calculate" or "hidden")
            {
                (string? esri, string[] agreeing) = bind switch
                {
                    "int" => ("esriFieldTypeInteger", new[] { "esriFieldTypeInteger", "esriFieldTypeSmallInteger", "esriFieldTypeBigInteger" }),
                    "decimal" => ("esriFieldTypeDouble", new[] { "esriFieldTypeDouble", "esriFieldTypeSingle" }),
                    "string" => ("esriFieldTypeString", new[] { "esriFieldTypeString", "esriFieldTypeGUID" }),
                    _ => ((string?)null, Array.Empty<string>()),
                };

                if (esri is null)
                {
                    Refuse("survey", at, "bind::type", $"bind::type '{written}' is refused on a '{type}': int, decimal and string are the types a calculated value is stored as here.");
                    return fieldType;
                }

                if (fieldType is null)
                {
                    Notice("survey", at, "bind::type", $"bind::type '{written}' makes the field {esri}, as bind::esri:fieldType would.");
                    return esri;
                }

                if (fieldType != "null" && !agreeing.Contains(fieldType, StringComparer.OrdinalIgnoreCase))
                {
                    Refuse("survey", at, "bind::type", $"bind::type '{written}' and bind::esri:fieldType '{fieldType}' disagree about what the value is.");
                }

                return fieldType;
            }

            string? own = type switch
            {
                "text" or "barcode" or "select_one" or "select_one_from_file" or "acknowledge" => "string",
                "select_multiple" or "select_multiple_from_file" => "string",
                "integer" => "int",
                "decimal" => "decimal",
                "date" => "date",
                "time" => "time",
                "dateTime" => "datetime",
                "geopoint" or "geotrace" or "geoshape" => type,
                "image" or "audio" or "video" or "file" => "binary",
                "note" => "string",
                "range" => bind is "int" or "decimal" ? bind : "decimal",
                _ => null,
            };

            if (own == bind || (type is "select_one" or "select_one_from_file" && bind == "select1") || (type.StartsWith("select_multiple", StringComparison.Ordinal) && bind == "select"))
            {
                Notice("survey", at, "bind::type", $"bind::type '{written}' is the type a '{type}' question already has, and changes nothing.");
            }
            else
            {
                Refuse("survey", at, "bind::type", $"bind::type '{written}' is refused on a '{type}' question: it would store the answer as another type than the question asks for.");
            }

            return fieldType;
        }

        /// <summary>A row's recordings per language, from <c>media::audio</c> and its older spelling <c>audio</c> (§5.26).</summary>
        private Dictionary<string, string> Sounds(Columns columns, IReadOnlyList<string> row, string sheet, int at)
        {
            Dictionary<string, string> sounds = columns.Translations(row, "media::audio");

            foreach ((string language, string name) in columns.Translations(row, "audio"))
            {
                sounds.TryAdd(language, name);
            }

            foreach ((string language, string name) in sounds)
            {
                if (SoundProblem(name.Trim()) is { } problem)
                {
                    Refuse(sheet, at, language.Length == 0 ? "media::audio" : $"media::audio::{language}", $"'{name.Trim()}': {problem}");
                }
            }

            return sounds.ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.Ordinal);
        }

        /// <summary>An appearance cell with its search(…) taken out, which is the question's search and not an appearance.</summary>
        private static string WithoutSearch(string? appearance) =>
            appearance is null ? string.Empty : FormLayerQuery.TryExtract(appearance, out _, out string rest, out _) ? rest : appearance;

        /// <summary>
        /// <c>bind::esri:parameters</c> on a <c>begin repeat</c> row: Survey123's <c>allowAdds</c> and <c>allowUpdates</c>,
        /// which say whether a record opened from the Inbox may gain new rows of the repeat and change the ones it has —
        /// ADR-300 §5.7. <c>true</c> or <c>false</c>; absent, adds are allowed and updates are not, Survey123's defaults.
        /// <c>query</c>, which filters the rows downloaded with a record, is refused with its reason; any other key by name.
        /// </summary>
        private (bool AllowAdds, bool AllowUpdates) RepeatParameters(IReadOnlyList<string> row, Columns columns, int at)
        {
            bool adds = true, updates = false;

            if (columns.Value(row, "bind::esri:parameters") is not { } written)
            {
                return (adds, updates);
            }

            foreach (string pair in written.Split([' ', ',', ';', '&'], StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                string key = equals < 0 ? pair : pair[..equals].Trim();
                string setting = equals < 0 ? string.Empty : pair[(equals + 1)..].Trim();

                if (key.Equals("query", StringComparison.OrdinalIgnoreCase))
                {
                    Refuse("survey", at, "bind::esri:parameters",
                        "query is refused: it filters the repeat's rows downloaded with an Inbox record, and the Inbox downloads them all.");
                    continue;
                }

                if (!key.Equals("allowAdds", StringComparison.OrdinalIgnoreCase) && !key.Equals("allowUpdates", StringComparison.OrdinalIgnoreCase))
                {
                    Refuse("survey", at, "bind::esri:parameters", $"The parameter '{key}' is refused: allowAdds and allowUpdates are the ones a repeat takes.");
                    continue;
                }

                if (!bool.TryParse(setting, out bool value))
                {
                    Refuse("survey", at, "bind::esri:parameters", $"{key} is true or false, not '{setting}'.");
                    continue;
                }

                if (key.Equals("allowAdds", StringComparison.OrdinalIgnoreCase))
                {
                    adds = value;
                }
                else
                {
                    updates = value;
                }
            }

            return (adds, updates);
        }

        /// <summary>
        /// <c>bind::esri:parameters</c> on a question (§5.20, §5.26): <c>calculationMode</c> — <c>auto</c>, <c>manual</c>,
        /// <c>always</c> or <c>whenEmpty</c>, on a question that has a calculation and is one of the types Survey123 allows
        /// it on; <c>max-duration</c> on an <c>audio</c> question, in seconds; and the keys Survey123's parameters page lists
        /// for its field app alone (an image's watermarks, smart attributes and redaction; an audio's codec), accepted with a
        /// notice that they are not applied here, as they are not in Survey123's own web app. Any other key is refused by
        /// name. A value may be written in double quotes, and a watermark's is: its spaces and ampersands are its own.
        /// </summary>
        private (string? Mode, int? MaxDuration) EsriParameters(IReadOnlyList<string> row, Columns columns, string type, int at)
        {
            if (columns.Value(row, "bind::esri:parameters") is not { } written)
            {
                return (null, null);
            }

            string? mode = null;
            int? maxDuration = null;
            List<string> fieldApp = [];

            foreach ((string key, string setting) in ParameterPairs(written))
            {
                if (FormCatalog.FieldAppParameters.TryGetValue(type, out var onlyThere) && onlyThere.Contains(key))
                {
                    fieldApp.Add(key);
                    continue;
                }

                if (type == "audio" && key.Equals("max-duration", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(setting, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) || seconds < 1 || seconds > 86_400)
                    {
                        Refuse("survey", at, "bind::esri:parameters", $"max-duration is a number of seconds from 1 to 86400, not '{setting}'.");
                    }
                    else
                    {
                        maxDuration = seconds;
                    }

                    continue;
                }

                if (!key.Equals("calculationMode", StringComparison.OrdinalIgnoreCase))
                {
                    Refuse("survey", at, "bind::esri:parameters", type == "audio"
                        ? $"The parameter '{key}' is refused: on an audio question calculationMode is not allowed, max-duration is built and codec is accepted."
                        : $"The parameter '{key}' is refused: calculationMode is the only one Survey123 documents for this column{(type == "image" ? " on an image here, beside its field-app parameters (watermarks, smartAttributes, redaction)" : string.Empty)}.");
                    continue;
                }

                string? canonical = FormCatalog.CalculationModes.FirstOrDefault(m => m.Equals(setting, StringComparison.OrdinalIgnoreCase));

                if (canonical is null)
                {
                    Refuse("survey", at, "bind::esri:parameters", $"'{setting}' is not a calculationMode. The documented ones are {string.Join(", ", FormCatalog.CalculationModes.Order(StringComparer.Ordinal))}.");
                    continue;
                }

                mode = canonical;
            }

            if (fieldApp.Count > 0)
            {
                Notice("survey", at, "bind::esri:parameters", $"{string.Join(", ", fieldApp)} {(fieldApp.Count == 1 ? "is" : "are")} applied by Survey123's field app alone, not by its web app, and not here: the {(type == "image" ? "photo is kept as it was taken" : "recording is kept in the format the browser makes")}.");
            }

            if (mode is null)
            {
                return (null, maxDuration);
            }

            if (columns.Value(row, "calculation") is null)
            {
                Refuse("survey", at, "bind::esri:parameters", "calculationMode says when a calculation runs, and this question has none.");
            }
            else if (!FormCatalog.CalculationModeTypes.Contains(type))
            {
                Refuse("survey", at, "bind::esri:parameters", $"calculationMode is allowed on {string.Join(", ", FormCatalog.CalculationModeTypes.Order(StringComparer.Ordinal))}, not on '{type}'.");
            }
            else if (mode == "manual" && (type is "calculate" or "hidden"
                || (columns.Value(row, "appearance") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("hidden", StringComparer.OrdinalIgnoreCase)))
            {
                Refuse("survey", at, "bind::esri:parameters", "calculationMode=manual is run by a Calculate button on the question, and a question the person does not see has none; it would never be calculated.");
            }

            return (mode, maxDuration);
        }

        /// <summary>
        /// <c>body::esri:inputMask</c> (§5.19 (5)): on a <c>text</c> question alone, as Survey123's documentation says, and
        /// one the client can draw. The longest answer the mask allows must fit the field it is stored in.
        /// </summary>
        private string? InputMask(IReadOnlyList<string> row, Columns columns, string type, int? fieldLength, int at)
        {
            if (columns.Value(row, "body::esri:inputMask") is not { } text)
            {
                return null;
            }

            if (type != "text")
            {
                Refuse("survey", at, "body::esri:inputMask", $"An input mask belongs on a text question; on a '{type}' use a constraint.");
                return null;
            }

            if (!FormInputMask.TryParse(text, out FormInputMask? mask, out string? problem))
            {
                Refuse("survey", at, "body::esri:inputMask", $"'{text}' is not an input mask: {problem}");
                return null;
            }

            int holds = fieldLength ?? FormLayerPlan.DefaultTextLength;

            if (mask!.MaxLength > holds)
            {
                Refuse("survey", at, "body::esri:inputMask", $"The mask allows an answer of {mask.MaxLength} characters and the field holds {holds}; give bind::esri:fieldLength a larger number.");
                return null;
            }

            return text;
        }

        /// <summary>
        /// A <c>table-list</c> group draws its questions as the rows of one table whose columns are a choice list's
        /// choices (§5.10), so every row must be a select over that one list, whole and unfiltered.
        /// </summary>
        private void TableList(Frame group)
        {
            string? list = null;

            if (group.Body.Count == 0)
            {
                Refuse("survey", group.Row, "appearance", $"The table-list group '{group.Name}' has no questions to make rows of.");
                return;
            }

            foreach (object node in group.Body)
            {
                if (node is not FormQuestion { Type: "select_one" or "select_multiple" } q)
                {
                    string what = node switch
                    {
                        FormQuestion other => $"the {other.Type} question '{other.Name}'",
                        FormGroup inner => $"the group '{inner.Name}'",
                        FormTable repeat => $"the repeat '{repeat.Name}'",
                        _ => "a row",
                    };
                    Refuse("survey", group.Row, "appearance", $"A table-list group holds only select_one and select_multiple questions, and {what} is in '{group.Name}'.");
                    return;
                }

                if (q.ChoiceFilter is not null)
                {
                    Refuse("survey", q.Row, "choice_filter", $"'{q.Name}' sits in a table-list, whose columns are the same for every row, so it cannot filter its choices.");
                }

                list ??= q.Argument;

                if (!string.Equals(q.Argument, list, StringComparison.Ordinal))
                {
                    Refuse("survey", q.Row, "type", $"Every question in a table-list shares one choice list; '{q.Name}' uses '{q.Argument}' and the first row uses '{list}'.");
                }
            }
        }

        /// <summary>
        /// A row's pictures per language, from <c>media::image</c> and its older spelling <c>image</c>, each checked: a
        /// picture is a file uploaded with the form, named without a folder or a web address (§5.10).
        /// </summary>
        private Dictionary<string, string> Pictures(Columns columns, IReadOnlyList<string> row, string sheet, int at)
        {
            Dictionary<string, string> pictures = columns.Translations(row, "media::image");

            foreach ((string language, string name) in columns.Translations(row, "image"))
            {
                pictures.TryAdd(language, name);
            }

            foreach ((string language, string name) in pictures)
            {
                if (MediaProblem(name.Trim()) is { } problem)
                {
                    Refuse(sheet, at, language.Length == 0 ? "media::image" : $"media::image::{language}", $"'{name.Trim()}': {problem}");
                }
            }

            return pictures.ToDictionary(p => p.Key, p => p.Value.Trim(), StringComparer.Ordinal);
        }

        /// <summary>
        /// Whether a <c>default</c> is a formula rather than a literal. A literal default such as an HTML fragment or a
        /// phone number is text (corpus: three Survey123 forms had HTML defaults that were scanned as XPath and failed).
        /// A formula has a question reference, or starts with a function call. INFERRED, recorded in ADR-287 §5.10.
        /// </summary>
        private static bool IsFormula(string text) =>
            text.Contains("${", StringComparison.Ordinal) || FormulaStart(text);

        private static readonly Regex FormulaStartPattern = new(@"^\s*[A-Za-z_][A-Za-z0-9_:.\-]*\s*\(", RegexOptions.CultureInvariant);

        private static bool FormulaStart(string text) => FormulaStartPattern.IsMatch(text);

        private void Relevance(IReadOnlyList<string> row, Columns columns, int at)
        {
            if (columns.Value(row, "relevant") is { } relevant)
            {
                expressions.Add(("survey", at, "relevant", relevant));
            }

            if (columns.Value(row, "body::esri:visible") is { } visible)
            {
                expressions.Add(("survey", at, "body::esri:visible", visible));
            }
        }

        private void Flag(string? value, string column, int at)
        {
            if (value is null)
            {
                return;
            }

            if (value.ToLowerInvariant() is "yes" or "no" or "true" or "false" or "true()" or "false()" or "1" or "0")
            {
                return;
            }

            expressions.Add(("survey", at, column, value));
        }

        private void Appearance(IReadOnlyList<string> row, Columns columns, string type, int at)
        {
            if (columns.Value(row, "appearance") is not { } appearance)
            {
                return;
            }

            // search() (§5.23): choices from a CSV uploaded with the form, or from a layer of this server.
            if (FormLayerQuery.TryExtract(appearance, out string inner, out string rest, out string? malformed))
            {
                appearance = rest;
                Search(inner, malformed, type, at);
            }

            foreach (string token in appearance.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (FormCatalog.SupportedAppearances.TryGetValue(token, out var types))
                {
                    if (!types.Contains("*") && !types.Contains(type))
                    {
                        if (token == "annotate")
                        {
                            // Owner decision 2026-11-17 (ADR-287 §5.24): no public page says what Survey123 does with
                            // annotate off an image, and a real form carries it on a select_one; it is ignored, in words.
                            Notice("survey", at, "appearance", $"The appearance 'annotate' draws over a photo, and a '{type}' question has none, so it is ignored here.");
                        }
                        else if (FormCatalog.PresentationOnOtherTypes.Contains(token))
                        {
                            Notice("survey", at, "appearance", $"The appearance '{token}' means nothing on a '{type}' question (Survey123 ignores it there) and is drawn the default way.");
                        }
                        else if (token == "autocomplete" && type == "select_multiple")
                        {
                            Refuse("survey", at, "appearance", "The appearance 'autocomplete' is refused on select_multiple: Survey123 supports it there in its Mobile app only, not in the field app or the web form; use select_one, or take it off.");
                        }
                        else
                        {
                            Refuse("survey", at, "appearance", $"The appearance '{token}' applies to {string.Join(", ", types.Order(StringComparer.Ordinal))}, not to '{type}'.");
                        }
                    }
                    else if (token == "field-list" && !pagesStyle)
                    {
                        Notice("survey", at, "appearance", "Without the pages style the whole form is one page, so field-list changes nothing here: this group is already drawn together with everything else.");
                    }
                }
                else if (WidthPattern().IsMatch(token))
                {
                    // ADR-287 §5.26: under a grid style a width is built (a group's columns, a question's span); without
                    // one it does nothing in Survey123 either, and the upload says so.
                    if (!gridStyle)
                    {
                        Notice("survey", at, "appearance", $"The width '{token}' lays a question out in a grid, and the form has no grid style (fixed-grid or dynamic-grid in the settings' style), so it changes nothing.");
                    }
                }
                else if (token == "grid")
                {
                    // ADR-287 §5.26, INFERRED: Survey123's pages set a grid by the settings' style, not by an appearance; a
                    // real form writes 'grid w2' on a group. It is ignored in words.
                    Notice("survey", at, "appearance", "'grid' is not an appearance: a grid is set by the settings' style (fixed-grid or dynamic-grid) and a group's width (w2, w3…). It is ignored.");
                }
                else if (FormCatalog.PresentationAppearances.Contains(token) || CompactColumnsPattern().IsMatch(token))
                {
                    Notice("survey", at, "appearance", $"The appearance '{token}' changes only how the question looks and is drawn the default way.");
                }
                else if (FormCatalog.RefusedAppearances.TryGetValue(token, out string? why))
                {
                    Refuse("survey", at, "appearance", $"The appearance '{token}' is refused: {why}.");
                }
                else
                {
                    Refuse("survey", at, "appearance", $"'{token}' is not an appearance this server knows.");
                }
            }

            AnnotateCombination(appearance, type, at);
        }

        // annotate (ADR-287 §5.24) draws over one photo. Survey123's media page allows `multiline annotate`; this client
        // annotates a single photo per question, so the combination is refused in words rather than half-built. Beside
        // `draw` or `signature` the question would be two different controls at once.
        private static readonly string[] AnnotateExclusive = ["draw", "signature"];

        private void AnnotateCombination(string appearance, string type, int at)
        {
            if (type != "image")
            {
                return;
            }

            string[] tokens = appearance.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (!tokens.Contains("annotate"))
            {
                return;
            }

            if (tokens.Contains("multiline"))
            {
                Refuse("survey", at, "appearance", "The appearances 'multiline' and 'annotate' together are refused: this client annotates one photo per question; use several image questions with 'annotate'.");
            }

            foreach (string other in AnnotateExclusive.Where(tokens.Contains))
            {
                Refuse("survey", at, "appearance", $"The appearances 'annotate' and '{other}' together are refused: a question is either a drawing over a photo or a {(other == "draw" ? "drawing on a blank canvas" : "signature")}, not both.");
            }
        }

        /// <summary>The search() of a select's appearance: read, checked, and kept for the question (§5.23).</summary>
        private void Search(string inner, string? malformed, string type, int at)
        {
            if (malformed is not null)
            {
                Refuse("survey", at, "appearance", malformed);
                return;
            }

            if (type is not ("select_one" or "select_multiple"))
            {
                Refuse("survey", at, "appearance", type is "select_one_from_file" or "select_multiple_from_file"
                    ? $"search() fills a select_one or select_multiple from a CSV or a layer; a '{type}' already takes its choices from its file. Use one or the other."
                    : $"search() fills the choices of a select_one or select_multiple, and a '{type}' has none.");
                return;
            }

            if (!FormLayerQuery.TryParseSearch(inner, out FormSearch? search, out string? why))
            {
                Refuse("survey", at, "appearance", why!);
                return;
            }

            searches[at] = search!;

            if (search!.SearchText is { } text)
            {
                expressions.Add(("survey", at, "appearance", text));
            }

            if (search.FilterText is { } filter)
            {
                expressions.Add(("survey", at, "appearance", filter));
            }

            if (search.Url is { } url && FormLayerQuery.TryLayer(url, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), FormLayerQuery.SearchParameters, out SubmissionAddress? address, out _))
            {
                queried.Add(new QueriedLayer("survey", at, "appearance", url, address!));
            }
        }

        /// <summary>
        /// A search()'s choice list: its first row's <c>name</c> is the column (or field) the answer is taken from and its
        /// <c>label</c> the one shown, as xlsform.org and Survey123 both say. Further rows are not offered, and that is said.
        /// </summary>
        private void SearchList(FormQuestion question, Dictionary<string, IReadOnlyList<FormChoice>> lists, int at)
        {
            if (question.Argument is not { } list || !lists.TryGetValue(list, out IReadOnlyList<FormChoice>? rows))
            {
                return;
            }

            List<FormChoice> written = [.. rows.Where(c => !c.Automatic)];

            if (written.Count == 0)
            {
                Refuse("survey", at, "type", $"search() needs a row of '{list}' on the choices sheet: its name is the column the answer is taken from and its label the column shown.");
            }
            else if (written.Count > 1)
            {
                Notice("survey", at, "appearance", $"search() reads the first row of '{list}' (choices row {written[0].Row}) to know which columns hold the answer and the label; the list's other rows are not offered.");
            }

            if (rows.Any(c => c.Automatic))
            {
                Refuse("survey", at, "type", "or_other adds a choice to a list, and a search() list's choices come from its CSV or layer; take or_other off.");
            }
        }

        private Dictionary<string, string> Parameters(string? value, string type, int at)
        {
            Dictionary<string, string> parameters = new(StringComparer.OrdinalIgnoreCase);

            if (value is null)
            {
                return parameters;
            }

            HashSet<string> allowed = type switch
            {
                "range" => new(StringComparer.OrdinalIgnoreCase) { "start", "end", "step" },
                "image" => new(StringComparer.OrdinalIgnoreCase) { "max-pixels" },
                _ => [],
            };

            foreach (string pair in value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                string key = equals < 0 ? pair : pair[..equals].Trim();
                string setting = equals < 0 ? string.Empty : pair[(equals + 1)..].Trim();

                if (type is "integer" or "decimal" && key.ToLowerInvariant() is "start" or "end" or "step")
                {
                    // ADR-287 §5.26: start, end and step are a range's (Survey123's parameters page lists them for range
                    // alone), and on a number they bound nothing in Survey123 either. A real form writes them on an integer;
                    // they are ignored in words, and a constraint is what bounds a number.
                    Notice("survey", at, "parameters", $"'{key}' belongs to a range question and does nothing on '{type}'; it is ignored. A constraint such as . >= 1 and . <= 50 bounds a number, and a range question draws a slider.");
                    continue;
                }

                if (!allowed.Contains(key))
                {
                    Refuse("survey", at, "parameters", allowed.Count == 0
                        ? $"The parameter '{key}' is refused: '{type}' takes no parameters in the first version."
                        : $"The parameter '{key}' is refused: '{type}' takes {string.Join(", ", allowed.Order(StringComparer.Ordinal))}.");
                    continue;
                }

                if (!double.TryParse(setting, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
                    || (key.Equals("max-pixels", StringComparison.OrdinalIgnoreCase) && number <= 0))
                {
                    Refuse("survey", at, "parameters", $"The parameter '{key}' needs a number, not '{setting}'.");
                    continue;
                }

                parameters[key] = setting;
            }

            if (type == "range" && parameters.TryGetValue("step", out string? step)
                && double.Parse(step, CultureInfo.InvariantCulture) <= 0)
            {
                Refuse("survey", at, "parameters", "A range's step is greater than zero.");
            }

            return parameters;
        }

        private (string? Type, int? Length, string? Alias) Esri(IReadOnlyList<string> row, Columns columns, string type, int at)
        {
            string? fieldType = columns.Value(row, "bind::esri:fieldType");
            string? stored = null;

            if (fieldType is not null)
            {
                if (fieldType.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    stored = "null";
                }
                else if (FormCatalog.EsriFieldTypes.TryGetValue(fieldType, out string? field))
                {
                    stored = field;

                    if (fieldType.Equals("esriFieldTypeDateOnly", StringComparison.OrdinalIgnoreCase))
                    {
                        Notice("survey", at, "bind::esri:fieldType", "esriFieldTypeDateOnly is stored as a date at midnight, which keeps the whole value.");
                    }
                }
                else if (FormCatalog.RefusedEsriFieldTypes.TryGetValue(fieldType, out string? why))
                {
                    Refuse("survey", at, "bind::esri:fieldType", $"'{fieldType}' is refused: {why}.");
                }
                else
                {
                    Refuse("survey", at, "bind::esri:fieldType", $"'{fieldType}' is not a field type this server can store.");
                }

                if (type == "barcode" && stored is not null && !FormCatalog.BarcodeFieldTypes.Contains(fieldType))
                {
                    // ADR-287 §5.22, INFERRED: Survey123's pages do not say; a code is text, and a number would drop its leading zeros.
                    Refuse("survey", at, "bind::esri:fieldType", $"A barcode is stored as text (esriFieldTypeString) or not at all; '{fieldType}' would change a scanned code, such as the leading zeros of an EAN.");
                }

                if (FormCatalog.AttachmentTypes.Contains(type) && stored is "null" or "esriFieldTypeString")
                {
                    // ADR-287 §5.26, INFERRED: Survey123's media page names no field type for an attachment question, and
                    // two real forms give a photo esriFieldTypeString. A photo is an attachment whatever the cell says; no
                    // field is made, and the upload says so rather than refusing the form.
                    if (stored == "esriFieldTypeString")
                    {
                        Notice("survey", at, "bind::esri:fieldType", $"A '{type}' question is stored as an attachment; no text field is made for it, and esriFieldTypeString is ignored.");
                    }
                }
                else if (FormCatalog.AttachmentTypes.Contains(type) || FormCatalog.LocationTypes.Contains(type))
                {
                    Refuse("survey", at, "bind::esri:fieldType", $"A '{type}' question is stored as {(FormCatalog.LocationTypes.Contains(type) ? "the layer's geometry" : "an attachment")}, and its field type cannot be changed.");
                }
            }

            int? length = null;

            if (columns.Value(row, "bind::esri:fieldLength") is { } text)
            {
                if (text.Equals("null", StringComparison.OrdinalIgnoreCase))
                {
                    // ADR-287 §5.26, INFERRED: Esri's own question-types sample writes 'null' here. It is not a length; the
                    // field keeps the length it would have had, and the upload says what 'null' would mean in the type column.
                    Notice("survey", at, "bind::esri:fieldLength", "'null' is not a length, so the field gets its usual one. To store nothing for a question, write null in bind::esri:fieldType.");
                }
                else if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 10_000)
                {
                    Refuse("survey", at, "bind::esri:fieldLength", $"A field length is a whole number from 1 to 10000, not '{text}'.");
                }
                else
                {
                    length = n;
                }
            }

            if (columns.Value(row, "body::accuracyThreshold") is { } threshold
                && (!FormCatalog.LocationTypes.Contains(type)
                    || !double.TryParse(threshold, NumberStyles.Float, CultureInfo.InvariantCulture, out double metres) || metres <= 0))
            {
                Refuse("survey", at, "body::accuracyThreshold", FormCatalog.LocationTypes.Contains(type)
                    ? $"An accuracy threshold is a number of metres above zero, not '{threshold}'."
                    : "An accuracy threshold belongs on a geopoint, geotrace or geoshape.");
            }

            if (columns.Value(row, "body::accept") is not null && !FormCatalog.AttachmentTypes.Contains(type))
            {
                Refuse("survey", at, "body::accept", "Accepted file types belong on an image, audio, video or file question.");
            }

            return (stored, length, columns.Value(row, "bind::esri:fieldAlias"));
        }

        private void Expression(string sheet, int row, string column, string expression,
            Dictionary<string, IReadOnlyList<FormChoice>> lists)
        {
            ExpressionScan scan = FormExpressions.Scan(expression);

            if (scan.Problem is not null)
            {
                Refuse(sheet, row, column, $"The expression is not well formed: {scan.Problem}.");
                return;
            }

            foreach (FunctionCall call in scan.Calls)
            {
                if (call.Name == "pulldata" && call.FirstLiteral is { } source && source.StartsWith('@'))
                {
                    PulldataSource(sheet, row, column, call, source);
                }
                else if (call.Name == "property")
                {
                    OdkProperty(sheet, row, column, call);
                }
                else if (call.Name == "instance" && (call.FirstLiteral is null || !lists.ContainsKey(call.FirstLiteral)))
                {
                    Refuse(sheet, row, column, $"instance(…) is supported for a choice list only, and '{call.FirstLiteral ?? "(not a name)"}' is not one.");
                }
                else if (FormCatalog.RefusedFunctions.TryGetValue(call.Name, out string? why))
                {
                    Refuse(sheet, row, column, $"{call.Name}() is refused: {why}.");
                }
                else if (!FormCatalog.Functions.Contains(call.Name))
                {
                    Refuse(sheet, row, column, $"{call.Name}() is not a function the form client has.");
                }
            }

            foreach (string reference in scan.References)
            {
                if (!names.ContainsKey(reference) && !frameNames.Contains(reference))
                {
                    Refuse(sheet, row, column, $"${{{reference}}} names no question in this form.");
                }
            }
        }

        /// <summary><c>pulldata("@property", name)</c> and <c>pulldata("@geopoint", ${q}, part)</c> are built; the rest are refused (§5.20).</summary>
        private void PulldataSource(string sheet, int row, string column, FunctionCall call, string source)
        {
            if (source == "@property")
            {
                if (call.Count != 2)
                {
                    Refuse(sheet, row, column, "pulldata(\"@property\", …) takes the name of one property: pulldata(\"@property\", 'username').");
                }
                else if (call.Literal(1) is { } name)
                {
                    if (FormCatalog.RefusedProperties.TryGetValue(name, out string? why))
                    {
                        Refuse(sheet, row, column, $"The property '{name}' is refused: {why}.");
                    }
                    else if (!FormCatalog.Properties.Contains(name))
                    {
                        Refuse(sheet, row, column, $"'{name}' is not a property Survey123 documents. The ones a browser can answer are {string.Join(", ", FormCatalog.Properties.Order(StringComparer.Ordinal))}.");
                    }
                }
            }
            else if (source == "@geopoint")
            {
                if (call.Count != 3)
                {
                    Refuse(sheet, row, column, "pulldata(\"@geopoint\", …) takes a geopoint question and the name of a part: pulldata(\"@geopoint\", ${location}, \"y\").");
                }
                else if (call.Literal(2) is { } part)
                {
                    if (FormCatalog.RefusedGeopointParts.TryGetValue(part, out string? why))
                    {
                        Refuse(sheet, row, column, $"The geopoint part '{part}' is refused: {why}.");
                    }
                    else if (!FormCatalog.GeopointParts.Contains(part) && !FormCatalog.GeopointPartsAnsweredEmpty.Contains(part))
                    {
                        Refuse(sheet, row, column, $"'{part}' is not a part of a geopoint Survey123 documents.");
                    }
                }
            }
            else if (source == "@layer")
            {
                LayerSource(sheet, row, column, call);
            }
            else if (FormCatalog.RefusedPulldataSources.TryGetValue(source, out string? why))
            {
                Refuse(sheet, row, column, $"pulldata(\"{source}\", …) is refused: {why}.");
            }
            else
            {
                Refuse(sheet, row, column, $"pulldata(\"{source}\", …) is refused: '{source}' is not a source Survey123 documents.");
            }
        }

        /// <summary>
        /// <c>pulldata("@layer", operation, …)</c> as Survey123's <i>Query a feature layer</i> writes it (§5.23):
        /// <c>getRecord(url[, where])</c>, <c>getValue(property, url[, where])</c>, <c>getRecordAt(url, location[, where])</c>
        /// and <c>getValueAt(property, url, location[, where])</c>. The location is a geopoint question, as the page says. An
        /// address written out is checked here for its shape and at publish for being this server's; one worked out at run
        /// time (<c>concat(…)</c> of a written address and parameters is read for the address) is checked by the client.
        /// </summary>
        private void LayerSource(string sheet, int row, string column, FunctionCall call)
        {
            const string Shapes = "pulldata(\"@layer\", \"getValue\", \"attributes.NAME\", <layer address>[, <where>]), \"getRecord\" without the property, "
                + "and \"getValueAt\" / \"getRecordAt\" with a geopoint question after the address";

            if (call.Count < 2 || call.Literal(1) is not { } operation)
            {
                Refuse(sheet, row, column, $"pulldata(\"@layer\", …) names its operation in quotes as its second argument: {Shapes}.");
                return;
            }

            if (!FormLayerQuery.Operations.TryGetValue(operation, out (int Fewest, int Most) count))
            {
                Refuse(sheet, row, column, $"'{operation}' is not an operation of pulldata(\"@layer\"): it is getValue, getRecord, getValueAt or getRecordAt.");
                return;
            }

            if (call.Count < count.Fewest || call.Count > count.Most)
            {
                Refuse(sheet, row, column, $"pulldata(\"@layer\", \"{operation}\", …) takes {count.Fewest - 2} to {count.Most - 2} arguments after the operation, not {call.Count - 2}: {Shapes}.");
                return;
            }

            bool value = operation.StartsWith("getValue", StringComparison.Ordinal);
            int at = value ? 3 : 2;

            if (value && call.Literal(2) is { } property && !PropertyPattern().IsMatch(property))
            {
                Refuse(sheet, row, column, $"'{property}' is not a property of a feature: it is attributes.<field>, or geometry, as in \"attributes.NAME\".");
            }

            if (operation.EndsWith("At", StringComparison.Ordinal) && call.Written is { } written)
            {
                string location = written[at + 1];
                string? name = location.StartsWith("${", StringComparison.Ordinal) && location.EndsWith('}') ? location[2..^1].Trim() : null;

                if (name is null)
                {
                    Refuse(sheet, row, column, $"pulldata(\"@layer\", \"{operation}\", …) queries at a location, and Survey123 asks for a geopoint question there: ${{location}}, not '{location}'.");
                }
                else if (names.TryGetValue(name, out FormQuestion? point) && point.Type != "geopoint")
                {
                    Refuse(sheet, row, column, $"pulldata(\"@layer\", \"{operation}\", …) queries at a location, and ${{{name}}} is a '{point.Type}', not a geopoint.");
                }
            }

            if (call.Written is not { } arguments)
            {
                return;
            }

            string url = arguments[at];

            if (FormLayerQuery.Literal(url) is { } literal)
            {
                Address(literal, url: literal);
            }
            else if (url.StartsWith("concat", StringComparison.Ordinal) && url.IndexOf('(', StringComparison.Ordinal) is var open and > 0
                && FormLayerQuery.Arguments(url[(open + 1)..url.LastIndexOf(')')]) is [var first, ..] && FormLayerQuery.Literal(first) is { } start)
            {
                // concat("https://…/FeatureServer/1", '?outStatistics=…') — Survey123's own example: the written part is
                // the address, and the parameters after it are the client's to read at run time.
                Address(start, url: start);
            }

            void Address(string text, string url)
            {
                Dictionary<string, string> query = new(StringComparer.OrdinalIgnoreCase);

                if (!FormLayerQuery.TryLayer(text, query, FormLayerQuery.PulldataParameters, out SubmissionAddress? address, out string? why))
                {
                    Refuse(sheet, row, column, why!);
                    return;
                }

                queried.Add(new QueriedLayer(sheet, row, column, url, address!));
            }
        }

        /// <summary>
        /// <c>property('username')</c>: ODK's three a browser can answer, and the SIM's three refused by name; and, as a Survey123
        /// form in the corpus writes it, any of <c>pulldata("@property")</c>'s names (§5.20, INFERRED).
        /// </summary>
        private void OdkProperty(string sheet, int row, string column, FunctionCall call)
        {
            if (call.Count != 1)
            {
                Refuse(sheet, row, column, "property() takes the name of one property: property('username').");
                return;
            }

            if (call.Literal(0) is not { } written)
            {
                return;
            }

            string name = written.StartsWith("uri:", StringComparison.Ordinal) ? written[4..] : written;

            if (FormCatalog.RefusedOdkProperties.TryGetValue(name, out string? why)
                || FormCatalog.RefusedProperties.TryGetValue(name, out why))
            {
                Refuse(sheet, row, column, $"The property '{written}' is refused: {why}.");
            }
            else if (!FormCatalog.OdkProperties.ContainsKey(name) && !FormCatalog.Properties.Contains(name))
            {
                Refuse(sheet, row, column, $"'{written}' is not a property ODK or Survey123 documents. The ones a browser can answer are {string.Join(", ", FormCatalog.OdkProperties.Keys.Concat(FormCatalog.Properties).Order(StringComparer.Ordinal))}.");
            }
        }

        private Dictionary<string, IReadOnlyList<FormChoice>> Choices(ModelSheet? sheet, List<string> languages)
        {
            Dictionary<string, List<FormChoice>> lists = new(StringComparer.Ordinal);

            if (sheet is null)
            {
                return [];
            }

            Columns columns = new(sheet);
            ClassifyColumns(sheet, columns, "choices", languages, ChoiceColumn);
            int listColumn = sheet.ColumnIndex("list_name") is >= 0 and var l ? l : sheet.ColumnIndex("list name");
            int nameColumn = sheet.ColumnIndex("name");

            if (listColumn < 0 || nameColumn < 0)
            {
                if (sheet.Rows.Count > 0)
                {
                    Refuse("choices", 1, null, "The choices sheet needs a 'list_name' and a 'name' column.");
                }

                return [];
            }

            HashSet<string> filterColumns = new(sheet.Columns.Where((c, i) => i != listColumn && i != nameColumn
                && !FormCatalog.ChoiceColumns.Contains(BaseName(c).Base) && c.Trim().Length > 0).Select(c => c.Trim()),
                StringComparer.OrdinalIgnoreCase);

            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                IReadOnlyList<string> row = sheet.Rows[r];
                int at = r + 2;
                string list = ModelSheet.Cell(row, listColumn).Trim();
                string name = ModelSheet.Cell(row, nameColumn).Trim();

                if (row.All(c => c.Trim().Length == 0))
                {
                    continue;
                }

                if (list.Length == 0)
                {
                    // ADR-287 §5.26: pyxform groups the choices sheet by list_name and passes over a row without one; a
                    // real Survey123 form keeps stray pictures in such rows. The row belongs to no list, so no question
                    // can offer it, and the upload says it is left out.
                    Notice("choices", at, "list_name", "This row has no list_name, so it belongs to no list and is left out, as pyxform leaves it out.");
                    continue;
                }

                if (name.Length == 0)
                {
                    Refuse("choices", at, "name", "A choice needs a list_name and a name.");
                    continue;
                }

                if (!lists.TryGetValue(list, out List<FormChoice>? choices))
                {
                    lists[list] = choices = [];
                }

                Dictionary<string, string> filters = new(StringComparer.OrdinalIgnoreCase);

                foreach (string column in filterColumns)
                {
                    filters[column] = ModelSheet.Cell(row, sheet.ColumnIndex(column));
                }

                choices.Add(new FormChoice(name, columns.Translations(row, "label"), filters, at)
                {
                    Images = Pictures(columns, row, "choices", at),
                    Audio = Sounds(columns, row, "choices", at),
                });
            }

            return lists.ToDictionary(p => p.Key, p => (IReadOnlyList<FormChoice>)p.Value, StringComparer.Ordinal);
        }

        private FormSettings ReadSettings(ModelSheet? sheet)
        {
            if (sheet is null)
            {
                return new FormSettings(null, null, null, null, null, false, null);
            }

            Columns columns = new(sheet);
            ClassifyColumns(sheet, columns, "settings", [], SettingsColumn);
            IReadOnlyList<string> row = sheet.Rows.Count > 0 ? sheet.Rows[0] : [];

            if (sheet.Rows.Skip(1).Any(r => r.Any(c => c.Trim().Length > 0)))
            {
                Refuse("settings", 3, null, "The settings sheet holds one row of values, under its header.");
            }

            bool pages = false;
            string? grid = null;

            if (columns.Value(row, "style") is { } style)
            {
                foreach (string token in style.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Equals("pages", StringComparison.OrdinalIgnoreCase))
                    {
                        pages = true;
                    }
                    else if (FormCatalog.GridStyles.Contains(token))
                    {
                        // ADR-287 §5.26: Survey123's groups-and-pages page — a group's width (w4) divides it into columns
                        // and a question's width spans them. theme-grid, the older name, is read as dynamic-grid (INFERRED).
                        grid = token.Equals("fixed-grid", StringComparison.OrdinalIgnoreCase) ? "fixed" : "dynamic";
                    }
                    else
                    {
                        Notice("settings", 2, "style", $"The style '{token}' changes only how the form looks and is drawn the default way.");
                    }
                }
            }

            if (columns.Value(row, "instance_name") is { } instanceName)
            {
                expressions.Add(("settings", 2, "instance_name", instanceName));
            }

            string? basemap = null;

            if (columns.Value(row, "graticula:basemap") is { } ground)
            {
                basemap = ground.ToLowerInvariant();

                if (!FormCatalog.Basemaps.Contains(basemap))
                {
                    Refuse("settings", 2, "graticula:basemap", $"'{ground}' is not a basemap this server offers: {string.Join(", ", FormCatalog.Basemaps.Order(StringComparer.Ordinal))}.");
                    basemap = null;
                }
            }

            string? redirect = columns.Value(row, "graticula:redirect");

            if (redirect is not null && !IsWebAddress(redirect))
            {
                Refuse("settings", 2, "graticula:redirect", $"'{redirect}' is not an http or https address; a form is sent on to one of those after it is submitted, and to nothing else.");
                redirect = null;
            }

            string? thanks = columns.Value(row, "graticula:thank_you");

            if (thanks is { Length: > 2000 })
            {
                Refuse("settings", 2, "graticula:thank_you", "The thank-you message is longer than 2,000 characters.");
                thanks = null;
            }

            // ADR-287 §5.17: an address that is not a feature service's is refused wherever the form is checked; whether
            // it is this server's, and whether the layer fits the form, can only be known when it is published.
            if (columns.Value(row, "submission_url") is { } submission
                && !SubmissionTarget.TryParse(submission, out _, out string? notAnAddress))
            {
                Refuse("settings", 2, "submission_url", notAnAddress!);
            }

            return new FormSettings(
                columns.Value(row, "form_title"),
                columns.Value(row, "form_id"),
                columns.Value(row, "version"),
                columns.Value(row, "default_language"),
                columns.Value(row, "instance_name"),
                pages,
                columns.Value(row, "submission_url"))
            {
                Basemap = basemap,
                ThankYou = thanks,
                Redirect = redirect,
                Grid = grid,
            };
        }

        private static string? FirstSetting(ModelSheet? settings, string column)
        {
            if (settings is null || settings.Rows.Count == 0)
            {
                return null;
            }

            string value = ModelSheet.Cell(settings.Rows[0], settings.ColumnIndex(column)).Trim();
            return value.Length == 0 ? null : value;
        }

        private ColumnKind SurveyColumn(string baseName) =>
            FormCatalog.SurveyColumns.Contains(baseName) ? ColumnKind.Supported
            : FormCatalog.PresentationSurveyColumns.Contains(baseName) ? ColumnKind.Presentation
            : FormCatalog.RefusedSurveyColumns.ContainsKey(baseName) ? ColumnKind.Refused
            : ColumnKind.Unknown;

        private ColumnKind ChoiceColumn(string baseName) =>
            baseName.Equals("geometry", StringComparison.OrdinalIgnoreCase) ? ColumnKind.Refused
            : ColumnKind.Supported;

        private ColumnKind SettingsColumn(string baseName) =>
            FormCatalog.SettingsColumns.Contains(baseName) ? ColumnKind.Supported
            : FormCatalog.InertSettingsColumns.Contains(baseName) ? ColumnKind.Inert
            : FormCatalog.RefusedSettingsColumns.ContainsKey(baseName) ? ColumnKind.Refused
            : ColumnKind.Unknown;

        private void ClassifyColumns(ModelSheet sheet, Columns columns, string sheetName, List<string> languages,
            Func<string, ColumnKind> kindOf)
        {
            int width = Math.Max(sheet.Columns.Count, sheet.Rows.Count == 0 ? 0 : sheet.Rows.Max(r => r.Count));

            for (int c = 0; c < width; c++)
            {
                string header = c < sheet.Columns.Count ? sheet.Columns[c].Trim() : string.Empty;
                int firstRow = FirstValueRow(sheet, c);

                if (firstRow < 0)
                {
                    continue;
                }

                string letter = FormWorkbook.ColumnLetters(c);

                if (header.Length == 0)
                {
                    Notice(sheetName, firstRow, letter, $"Column {letter} has values but no header; it is kept in the form and has no effect.");
                    continue;
                }

                (string baseName, string? language) = BaseName(header);
                ColumnKind kind = kindOf(baseName);

                if (language is not null && kind == ColumnKind.Supported)
                {
                    if (!FormCatalog.TranslatableColumns.Contains(baseName))
                    {
                        Refuse(sheetName, 1, header, $"'{baseName}' cannot be given per language.");
                    }
                    else if (baseName.Equals("label", StringComparison.OrdinalIgnoreCase) && !languages.Contains(language, StringComparer.Ordinal))
                    {
                        languages.Add(language);
                    }
                }

                switch (kind)
                {
                    case ColumnKind.Presentation:
                        Notice(sheetName, firstRow, header, $"'{header}' changes only how questions look and is drawn the default way.");
                        break;
                    case ColumnKind.Inert:
                        Notice(sheetName, firstRow, header, $"'{header}' is accepted and has no effect on this server.");
                        break;
                    case ColumnKind.Refused:
                        string why = (sheetName switch
                        {
                            "survey" => FormCatalog.RefusedSurveyColumns,
                            "settings" => FormCatalog.RefusedSettingsColumns,
                            _ => null,
                        })?.GetValueOrDefault(baseName) ?? "choosing on a map is not in the first version";
                        Refuse(sheetName, firstRow, header, $"'{header}' is refused: {why}.");
                        break;
                    case ColumnKind.Unknown when Prefixed(baseName):
                        Refuse(sheetName, firstRow, header, $"'{header}' is not a column this server knows, and a bind:: or body:: column may change what a form stores.");
                        break;
                    case ColumnKind.Unknown:
                        Notice(sheetName, firstRow, header, $"'{header}' is not an XLSForm column; it is kept in the form and has no effect.");
                        break;
                    default:
                        break;
                }
            }
        }

        private static bool Prefixed(string baseName) =>
            baseName.StartsWith("bind::", StringComparison.OrdinalIgnoreCase)
            || baseName.StartsWith("body::", StringComparison.OrdinalIgnoreCase)
            || baseName.StartsWith("instance::", StringComparison.OrdinalIgnoreCase)
            || baseName.StartsWith("attribute::", StringComparison.OrdinalIgnoreCase);

        private static int FirstValueRow(ModelSheet sheet, int column)
        {
            for (int r = 0; r < sheet.Rows.Count; r++)
            {
                if (ModelSheet.Cell(sheet.Rows[r], column).Trim().Length > 0)
                {
                    return r + 2;
                }
            }

            return -1;
        }

        private void Refuse(string sheet, int row, string? column, string message) => refusals.Add(new FormIssue(sheet, row, column, message));

        private void Notice(string sheet, int row, string? column, string message) => notices.Add(new FormIssue(sheet, row, column, message));
    }

    /// <summary>A header's base name and language: <c>label::Türkçe (tr)</c> is <c>label</c> and <c>Türkçe (tr)</c>.</summary>
    /// <summary>Whether a text is an absolute http or https address with no credentials in it.</summary>
    /// <param name="text">The text.</param>
    public static bool IsWebAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.Host.Length > 0;

    // .bmp since ADR-287 §5.26: every browser shows one, and a real Survey123 form names a .bmp choice picture.
    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".svg", ".bmp"];

    /// <summary>
    /// The key=value pairs of a <c>bind::esri:parameters</c> cell. They are separated by spaces, commas, semicolons or
    /// ampersands, except inside double quotes, which a value may be written in and which are taken off it.
    /// </summary>
    internal static List<(string Key, string Value)> ParameterPairs(string written)
    {
        List<string> tokens = [];
        System.Text.StringBuilder token = new();
        bool quoted = false;

        foreach (char c in written)
        {
            if (c == '"')
            {
                quoted = !quoted;
                token.Append(c);
            }
            else if (!quoted && (char.IsWhiteSpace(c) || c is ',' or ';' or '&'))
            {
                if (token.Length > 0)
                {
                    tokens.Add(token.ToString());
                    token.Clear();
                }
            }
            else
            {
                token.Append(c);
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return [.. tokens.Select(pair =>
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? pair : pair[..equals].Trim();
            string value = equals < 0 ? string.Empty : pair[(equals + 1)..].Trim();
            return (key, value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value);
        })];
    }

    // ADR-287 §5.26: recordings a browser's audio element plays. Survey123's media page lists more (AIF, MID, RA, WMA…)
    // for a file question and warns that a browser may not play every codec; a label's recording is there to be played.
    private static readonly string[] SoundExtensions = [".mp3", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".webm", ".flac"];

    /// <summary>Why a recording's name cannot be served from the form's files and played, or null when it can (§5.26).</summary>
    /// <param name="name">The cell's text.</param>
    public static string? SoundProblem(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal) || name.Any(char.IsControl))
        {
            return "a recording is a file uploaded with the form and is named by that file alone, with no folder and no web address.";
        }

        return SoundExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"a recording is {string.Join(", ", SoundExtensions)}, which a browser plays — '{name}' is none of them.";
    }

    /// <summary>Why a picture's name cannot be served from the form's files, or null when it can.</summary>
    /// <param name="name">The cell's text.</param>
    public static string? MediaProblem(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Contains('/', StringComparison.Ordinal) || name.Contains('\\', StringComparison.Ordinal) || name.Contains("..", StringComparison.Ordinal)
            || name.Contains(':', StringComparison.Ordinal) || name.Any(char.IsControl))
        {
            return "a picture is a file uploaded with the form and is named by that file alone, with no folder and no web address.";
        }

        return PictureExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            ? null
            : $"a picture is {string.Join(", ", PictureExtensions)} — '{name}' is none of them.";
    }

    /// <summary>Every picture and recording the form names, with the sheet, row and column it is named in.</summary>
    public IEnumerable<(string Sheet, int Row, string Column, string Name)> MediaReferences()
    {
        foreach (FormQuestion q in AllQuestions)
        {
            foreach ((string language, string name) in q.Images)
            {
                yield return ("survey", q.Row, language.Length == 0 ? "media::image" : $"media::image::{language}", name);
            }

            foreach ((string language, string name) in q.Audio)
            {
                yield return ("survey", q.Row, language.Length == 0 ? "media::audio" : $"media::audio::{language}", name);
            }
        }

        foreach (IReadOnlyList<FormChoice> list in ChoiceLists.Values)
        {
            foreach (FormChoice c in list)
            {
                foreach ((string language, string name) in c.Images)
                {
                    yield return ("choices", c.Row, language.Length == 0 ? "media::image" : $"media::image::{language}", name);
                }

                foreach ((string language, string name) in c.Audio)
                {
                    yield return ("choices", c.Row, language.Length == 0 ? "media::audio" : $"media::audio::{language}", name);
                }
            }
        }
    }

    internal static (string Base, string? Language) BaseName(string header)
    {
        string[] parts = header.Split("::");

        if (parts.Length == 1)
        {
            // pyxform's own fixtures write a label's language with one colon, label:English (en). It is read as the
            // double-colon form; any other single colon is part of the name.
            int colon = header.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0 && header[..colon].Trim().ToLowerInvariant() is "label" or "hint" or "guidance_hint"
                or "required_message" or "constraint_message" && header[(colon + 1)..].Trim().Length > 0)
            {
                return (header[..colon].Trim(), header[(colon + 1)..].Trim());
            }

            return (header.Trim(), null);
        }

        bool prefixed = parts[0].Trim().ToLowerInvariant() is "bind" or "body" or "instance" or "media" or "attribute";
        string baseName = prefixed ? $"{parts[0].Trim()}::{parts[1].Trim()}" : parts[0].Trim();
        string[] rest = parts.Skip(prefixed ? 2 : 1).ToArray();

        return (baseName, rest.Length == 0 ? null : string.Join("::", rest).Trim());
    }

    /// <summary>A type cell as a type and its argument: <c>select_one hasar</c> is <c>select_one</c> and <c>hasar</c>.</summary>
    internal static (string Type, string? Argument, string? Problem) ParseType(string raw)
    {
        (string type, string? argument, string? problem, _) = ParseTypeFull(raw);
        return (type, argument, problem);
    }

    /// <summary>
    /// A type cell read in full: its type, its argument, why it cannot be read, and whether it ends in <c>or_other</c>
    /// (also written <c>or other</c>), which XLSForm allows on <c>select_one</c> and <c>select_multiple</c> alone.
    /// </summary>
    internal static (string Type, string? Argument, string? Problem, bool OrOther) ParseTypeFull(string raw)
    {
        string text = string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string lower = text.ToLowerInvariant();
        bool orOther = false;

        if (lower.EndsWith(" or_other", StringComparison.Ordinal) || lower.EndsWith(" or other", StringComparison.Ordinal)
            || lower.EndsWith(" or specify other", StringComparison.Ordinal))
        {
            orOther = true;
            // "or specify other" is pyxform's third spelling (ADR-287 §5.26).
            text = text[..^(lower.EndsWith(" or specify other", StringComparison.Ordinal) ? " or specify other".Length : " or_other".Length)].TrimEnd();
            lower = text.ToLowerInvariant();

            bool select = lower.StartsWith("select_one ", StringComparison.Ordinal) || lower.StartsWith("select one ", StringComparison.Ordinal)
                || lower.StartsWith("select_multiple ", StringComparison.Ordinal) || lower.StartsWith("select multiple ", StringComparison.Ordinal)
                || lower.StartsWith("select all that apply ", StringComparison.Ordinal);

            if (!select)
            {
                return (text, null, "'or_other' belongs after the choice list of a select_one or select_multiple, and nowhere else.", false);
            }
        }

        (string kind, string? list, string? trouble) = ParseBase(text, lower);
        return (kind, list, trouble, orOther && trouble is null);
    }

    private static (string Type, string? Argument, string? Problem) ParseBase(string text, string lower)
    {
        foreach ((string alias, string type) in (ReadOnlySpan<(string, string)>)[
            ("select_one_from_file ", "select_one_from_file"), ("select_multiple_from_file ", "select_multiple_from_file"),
            // pyxform's own spellings (its question_type_dictionary), ADR-287 §5.26; before the shorter ones they begin with.
            ("select one from ", "select_one"), ("select all that apply from ", "select_multiple"), ("select multiple from ", "select_multiple"),
            ("select_one ", "select_one"), ("select one ", "select_one"),
            ("select_multiple ", "select_multiple"), ("select multiple ", "select_multiple"),
            ("select all that apply ", "select_multiple")])
        {
            if (lower.StartsWith(alias, StringComparison.Ordinal))
            {
                string argument = text[alias.Length..].Trim();
                return argument.Contains(' ', StringComparison.Ordinal)
                    ? (type, null, $"'{text}' has more than one word after '{type}'.")
                    : (type, argument, null);
            }
        }

        string canonical = lower switch
        {
            "begin group" or "begin_group" => "begin group",
            "end group" or "end_group" => "end group",
            "begin repeat" or "begin_repeat" => "begin repeat",
            "end repeat" or "end_repeat" => "end repeat",
            "select_one" or "select_multiple" or "select_one_from_file" or "select_multiple_from_file" => lower,
            "datetime" => "dateTime",
            _ => lower,
        };

        if (canonical is "select_one" or "select_multiple" or "select_one_from_file" or "select_multiple_from_file")
        {
            return (canonical, null, null);
        }

        // "begin group pressure": pyxform's begin pattern takes one word after begin group / begin repeat (a list name, used
        // by loops alone). It is given back as the argument, and the checker says it is ignored (ADR-287 §5.26).
        if (BeginWithWord().Match(lower) is { Success: true } begin && begin.Groups[2].Value != "over")
        {
            return (begin.Groups[1].Value == "group" ? "begin group" : "begin repeat", text.Split(' ')[^1], null);
        }

        if (canonical.StartsWith("begin ", StringComparison.Ordinal) || canonical.StartsWith("end ", StringComparison.Ordinal)
            || !canonical.Contains(' ', StringComparison.Ordinal))
        {
            return (canonical, null, null);
        }

        // "rank colours", "select_one_external places": the first word is the type, so a refused type is refused with
        // its own reason rather than as an unknown spelling.
        int space = canonical.IndexOf(' ', StringComparison.Ordinal);
        string first = canonical[..space] == "datetime" ? "dateTime" : canonical[..space];
        return (first, text[(space + 1)..].Trim(), null);
    }

    [GeneratedRegex(@"^w\d+$")]
    private static partial Regex WidthPattern();

    [GeneratedRegex(@"^begin[ _](group|repeat) (\S+)$")]
    private static partial Regex BeginWithWord();

    [GeneratedRegex(@"^compact-\d+$")]
    private static partial Regex CompactColumnsPattern();

    private enum ColumnKind
    {
        Supported,
        Presentation,
        Inert,
        Refused,
        Unknown,
    }

    private enum FrameKind
    {
        Root,
        Group,
        Repeat,
    }

    private sealed record Frame(FrameKind Kind, string Name, int Row, List<FormQuestion> Questions, List<FormTable> Repeats,
        Dictionary<string, string>? Label, string? RepeatCount, List<object> Body, string? Relevant, IReadOnlyList<string> Appearance,
        bool AllowAdds = true, bool AllowUpdates = false);

    /// <summary>A sheet's columns by base name and language.</summary>
    private sealed class Columns
    {
        private readonly Dictionary<string, int> plain = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(string Language, int Index)>> translated = new(StringComparer.OrdinalIgnoreCase);

        public Columns(ModelSheet sheet)
        {
            for (int i = 0; i < sheet.Columns.Count; i++)
            {
                string header = sheet.Columns[i].Trim();

                if (header.Length == 0)
                {
                    continue;
                }

                (string baseName, string? language) = BaseName(header);

                if (language is null)
                {
                    plain.TryAdd(baseName, i);
                }
                else
                {
                    if (!translated.TryGetValue(baseName, out var list))
                    {
                        translated[baseName] = list = [];
                    }

                    list.Add((language, i));
                }
            }
        }

        /// <summary>A plain column's value in a row, trimmed, or null when empty or absent.</summary>
        public string? Value(IReadOnlyList<string> row, string column)
        {
            if (!plain.TryGetValue(column, out int index))
            {
                return null;
            }

            string value = ModelSheet.Cell(row, index).Trim();
            return value.Length == 0 ? null : value;
        }

        /// <summary>A column's text per language; the plain column is the empty language.</summary>
        public Dictionary<string, string> Translations(IReadOnlyList<string> row, string column)
        {
            Dictionary<string, string> texts = new(StringComparer.Ordinal);

            if (plain.TryGetValue(column, out int index) && ModelSheet.Cell(row, index) is { Length: > 0 } text && text.Trim().Length > 0)
            {
                texts[string.Empty] = text;
            }

            if (translated.TryGetValue(column, out var languages))
            {
                foreach ((string language, int i) in languages)
                {
                    if (ModelSheet.Cell(row, i) is { Length: > 0 } t && t.Trim().Length > 0)
                    {
                        texts[language] = t;
                    }
                }
            }

            return texts;
        }
    }
}

/// <summary>A table of the form: the top level is the layer, each repeat a related table.</summary>
/// <remarks>
/// <see cref="Questions"/> and <see cref="Repeats"/> are what is stored; <see cref="Body"/> is what is drawn, in form
/// order: <see cref="FormQuestion"/>s, <see cref="FormGroup"/>s and repeats (<see cref="FormTable"/>s).
/// </remarks>
public sealed record FormTable(
    string Name,
    IReadOnlyDictionary<string, string>? Label,
    int Row,
    string? RepeatCount,
    IReadOnlyList<FormQuestion> Questions,
    IReadOnlyList<FormTable> Repeats,
    IReadOnlyList<object> Body,
    string? Relevant,
    IReadOnlyList<string> Appearance)
{
    /// <summary>Whether a record opened from the Inbox may gain new rows of this repeat — <c>allowAdds</c>, ADR-300 §5.7.</summary>
    public bool AllowAdds { get; init; } = true;

    /// <summary>Whether a record opened from the Inbox may change the rows of this repeat it has — <c>allowUpdates</c>.</summary>
    public bool AllowUpdates { get; init; }

    /// <summary>This table and every table under it, parents before children.</summary>
    public IEnumerable<FormTable> AllTables => new[] { this }.Concat(Repeats.SelectMany(r => r.AllTables));

    /// <summary>
    /// The table's location question, when it has one: the layer's geometry at the top level, and for a repeat the
    /// geometry that makes it a related layer rather than a table (owner decision 2026-11-09).
    /// </summary>
    public FormQuestion? Location => Questions.FirstOrDefault(q => FormCatalog.LocationTypes.Contains(q.Type));
}

/// <summary>A group: questions drawn together, hidden together by its relevant, and a page when the style is <c>pages</c>.</summary>
public sealed record FormGroup(
    string Name,
    IReadOnlyDictionary<string, string> Labels,
    int Row,
    string? Relevant,
    IReadOnlyList<string> Appearance,
    IReadOnlyList<object> Body);

/// <summary>One question, as the form states it.</summary>
public sealed record FormQuestion(
    string Name,
    string Type,
    string? Argument,
    IReadOnlyDictionary<string, string> Labels,
    int Row,
    string? Required,
    string? Relevant,
    string? Constraint,
    string? Calculation,
    string? Default,
    string? ReadOnly,
    string? ChoiceFilter,
    IReadOnlyList<string> Appearance,
    IReadOnlyDictionary<string, string> Parameters,
    string? EsriFieldType,
    int? FieldLength,
    string? FieldAlias)
{
    /// <summary>The hint per language.</summary>
    public IReadOnlyDictionary<string, string> Hints { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// <c>guidance_hint</c> per language: longer help, shown when the person asks for it (Survey123's own reading of the
    /// column, from its public documentation).
    /// </summary>
    public IReadOnlyDictionary<string, string> GuidanceHints { get; init; } = new Dictionary<string, string>();

    /// <summary>The picture shown with the question: a file uploaded with the form, per language (<c>media::image</c>).</summary>
    public IReadOnlyDictionary<string, string> Images { get; init; } = new Dictionary<string, string>();

    /// <summary>The message shown when a required question is left empty, per language.</summary>
    public IReadOnlyDictionary<string, string> RequiredMessages { get; init; } = new Dictionary<string, string>();

    /// <summary>The message shown when the constraint fails, per language.</summary>
    public IReadOnlyDictionary<string, string> ConstraintMessages { get; init; } = new Dictionary<string, string>();

    /// <summary><c>body::esri:visible</c>: hidden when false, and still stored.</summary>
    public string? Visible { get; init; }

    /// <summary><c>body::accuracyThreshold</c>, in metres.</summary>
    public string? AccuracyThreshold { get; init; }

    /// <summary><c>body::accept</c>: the file types an attachment question takes.</summary>
    public string? Accept { get; init; }

    /// <summary>
    /// <c>calculationMode</c> from <c>bind::esri:parameters</c> — <c>auto</c>, <c>manual</c>, <c>always</c> or
    /// <c>whenEmpty</c> — or null when the question does not say, which is <c>auto</c> (ADR-287 §5.20).
    /// </summary>
    public string? CalculationMode { get; init; }
    /// <summary><c>body::esri:inputMask</c>, as written; checked to be a mask this server honours (ADR-287 §5.19).</summary>
    public string? InputMask { get; init; }

    /// <summary>True for a question the checker added: the <c>{name}_other</c> text that <c>or_other</c> makes.</summary>
    public bool Automatic { get; init; }

    /// <summary>The search() its appearance holds: its choices come from a CSV or a layer (ADR-287 §5.23).</summary>
    public FormSearch? Search { get; init; }

    /// <summary>
    /// <c>bind::esri:warning</c>: an expression that, when false, shows a warning and lets the answer go — Survey123's soft
    /// constraint (ADR-287 §5.26).
    /// </summary>
    public string? Warning { get; init; }

    /// <summary><c>bind::esri:warning_message</c> per language: what the warning says.</summary>
    public IReadOnlyDictionary<string, string> WarningMessages { get; init; } = new Dictionary<string, string>();

    /// <summary>The recording played with the question: a file uploaded with the form, per language (<c>media::audio</c>, §5.26).</summary>
    public IReadOnlyDictionary<string, string> Audio { get; init; } = new Dictionary<string, string>();

    /// <summary>The longest recording an <c>audio</c> question takes, in seconds (<c>max-duration</c>, §5.26); null on other types.</summary>
    public int? MaxDuration { get; init; }
}

/// <summary>One choice of a list: its name, its labels per language, and the values a choice_filter may read.</summary>
public sealed record FormChoice(string Name, IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Filters, int Row)
{
    /// <summary>The picture shown beside the choice: a file uploaded with the form, per language (<c>media::image</c>).</summary>
    public IReadOnlyDictionary<string, string> Images { get; init; } = new Dictionary<string, string>();

    /// <summary>The recording played beside the choice, per language (<c>media::audio</c>, ADR-287 §5.26).</summary>
    public IReadOnlyDictionary<string, string> Audio { get; init; } = new Dictionary<string, string>();

    /// <summary>True for the choice <c>or_other</c> adds: it is offered whatever a <c>choice_filter</c> says.</summary>
    public bool Automatic { get; init; }
}

/// <summary>The settings sheet's values.</summary>
public sealed record FormSettings(
    string? Title,
    string? FormId,
    string? Version,
    string? DefaultLanguage,
    string? InstanceName,
    bool Pages,
    string? SubmissionUrl)
{
    /// <summary>The ground under a location question's map: <c>osm</c> (the default), <c>topo</c> or <c>none</c> (§5.15).</summary>
    public string? Basemap { get; init; }

    /// <summary>What the confirmation says after an answer was sent; the page's own words when null.</summary>
    public string? ThankYou { get; init; }

    /// <summary>An http or https address the person is sent on to after an answer was sent.</summary>
    public string? Redirect { get; init; }

    /// <summary>
    /// The grid the settings' style asks for — <c>fixed</c> (a hidden question keeps its place) or <c>dynamic</c> (the
    /// rest move up) — or null for none (ADR-287 §5.26).
    /// </summary>
    public string? Grid { get; init; }
}

/// <summary>One finding, at a sheet, a spreadsheet row (1 is the header; 0 the whole sheet) and a column.</summary>
public sealed record FormIssue(string Sheet, int Row, string? Column, string Message);

/// <summary>The outcome of checking a form: its definition when nothing was refused, and every refusal and notice.</summary>
public sealed record FormCheck(FormDefinition? Definition, IReadOnlyList<FormIssue> Refusals, IReadOnlyList<FormIssue> Notices)
{
    /// <summary>Whether the form can be published.</summary>
    public bool Accepted => Refusals.Count == 0;
}
