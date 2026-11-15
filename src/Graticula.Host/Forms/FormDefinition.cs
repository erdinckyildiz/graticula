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

    private sealed class Checker(FormModel model)
    {
        private readonly List<FormIssue> refusals = [];
        private readonly List<FormIssue> notices = [];
        private readonly Dictionary<string, FormQuestion> names = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Sheet, int Row, string Column, string Expression)> expressions = [];

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
            FormTable root = Survey(survey, lists, languages);

            foreach ((string sheet, int row, string column, string expression) in expressions)
            {
                Expression(sheet, row, column, expression, lists);
            }

            if (settings.DefaultLanguage is { Length: > 0 } language && languages.Count > 0
                && !languages.Contains(language, StringComparer.Ordinal) && language != "default")
            {
                Refuse("settings", 2, "default_language",
                    $"default_language '{language}' is not one of the label languages: {string.Join(", ", languages)}.");
            }

            FormDefinition? definition = refusals.Count == 0 ? new FormDefinition(root, lists, languages, settings) : null;
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

                (string type, string? argument, string? problem) = ParseType(rawType);

                if (problem is not null)
                {
                    Refuse("survey", at, "type", problem);
                    continue;
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
                            closed.Questions, closed.Repeats, closed.Body, closed.Relevant, closed.Appearance);
                        open.Peek().Repeats.Add(repeat);
                        open.Peek().Body.Add(repeat);
                    }
                    else
                    {
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

                    Relevance(row, columns, at);
                    open.Push(new Frame(type == "begin group" ? FrameKind.Group : FrameKind.Repeat, name, at, [], [],
                        labels.Count > 0 ? labels : null, count, [], columns.Value(row, "relevant"),
                        (columns.Value(row, "appearance") ?? string.Empty).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)));
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

                if (needsLabel && labels.Count == 0)
                {
                    Refuse("survey", at, "label", $"The question '{name}' has no label.");
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

                FormQuestion question = Question(row, columns, type, argument, name, labels, at);
                names[name] = question;
                open.Peek().Questions.Add(question);
                open.Peek().Body.Add(question);
            }

            while (open.Count > 1)
            {
                Frame left = open.Pop();
                Refuse("survey", left.Row, "type", $"The {left.Kind.ToString().ToLowerInvariant()} '{left.Name}' opened on row {left.Row} is never closed.");
            }

            Frame top = open.Pop();
            return new FormTable("", null, 0, null, top.Questions, top.Repeats, top.Body, null, []);
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

            foreach (string column in (string[])["constraint", "calculation", "choice_filter"])
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

            if (type == "calculate" && columns.Value(row, "calculation") is null)
            {
                Refuse("survey", at, "calculation", $"The calculate '{name}' has no calculation.");
            }

            if (columns.Value(row, "choice_filter") is not null && type is not ("select_one" or "select_multiple" or "select_one_from_file" or "select_multiple_from_file"))
            {
                Refuse("survey", at, "choice_filter", "A choice_filter belongs on a select question.");
            }

            Dictionary<string, string> parameters = Parameters(columns.Value(row, "parameters"), type, at);
            (string? fieldType, int? length, string? alias) = Esri(row, columns, type, at);

            return new FormQuestion(
                name, type, argument, labels, at,
                Required: required,
                Relevant: columns.Value(row, "relevant"),
                Constraint: columns.Value(row, "constraint"),
                Calculation: columns.Value(row, "calculation"),
                Default: columns.Value(row, "default"),
                ReadOnly: readOnly,
                ChoiceFilter: columns.Value(row, "choice_filter"),
                Appearance: (columns.Value(row, "appearance") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                Parameters: parameters,
                EsriFieldType: fieldType,
                FieldLength: length,
                FieldAlias: alias)
            {
                Hints = columns.Translations(row, "hint"),
                RequiredMessages = columns.Translations(row, "required_message"),
                ConstraintMessages = columns.Translations(row, "constraint_message"),
                Visible = columns.Value(row, "body::esri:visible"),
                AccuracyThreshold = columns.Value(row, "body::accuracyThreshold"),
                Accept = columns.Value(row, "body::accept"),
            };
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

            if (appearance.Contains("search(", StringComparison.OrdinalIgnoreCase))
            {
                Refuse("survey", at, "appearance", "search() is refused: dynamic choices from a CSV are not in the first version; use select_one_from_file.");
                return;
            }

            foreach (string token in appearance.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (FormCatalog.SupportedAppearances.TryGetValue(token, out var types))
                {
                    if (!types.Contains("*") && !types.Contains(type))
                    {
                        Refuse("survey", at, "appearance", $"The appearance '{token}' applies to {string.Join(", ", types.Order(StringComparer.Ordinal))}, not to '{type}'.");
                    }
                }
                else if (FormCatalog.PresentationAppearances.Contains(token) || WidthPattern().IsMatch(token))
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

                if (FormCatalog.AttachmentTypes.Contains(type) || FormCatalog.LocationTypes.Contains(type))
                {
                    Refuse("survey", at, "bind::esri:fieldType", $"A '{type}' question is stored as {(FormCatalog.LocationTypes.Contains(type) ? "the layer's geometry" : "an attachment")}, and its field type cannot be changed.");
                }
            }

            int? length = null;

            if (columns.Value(row, "bind::esri:fieldLength") is { } text)
            {
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 10_000)
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
                    Refuse(sheet, row, column, $"pulldata(\"{source}\", …) is refused: Survey123's pulldata(\"@…\") forms are not in the first version; pulldata from a CSV is.");
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

            bool duplicatesAllowed = string.Equals(
                FirstSetting(model.Sheet("settings"), "allow_choice_duplicates"), "yes", StringComparison.OrdinalIgnoreCase);
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

                if (list.Length == 0 || name.Length == 0)
                {
                    Refuse("choices", at, list.Length == 0 ? "list_name" : "name", "A choice needs a list_name and a name.");
                    continue;
                }

                if (!lists.TryGetValue(list, out List<FormChoice>? choices))
                {
                    lists[list] = choices = [];
                }

                if (!duplicatesAllowed && choices.Any(c => c.Name == name))
                {
                    Refuse("choices", at, "name", $"'{name}' is already a choice in the list '{list}'; set allow_choice_duplicates to yes if that is meant.");
                    continue;
                }

                Dictionary<string, string> filters = new(StringComparer.OrdinalIgnoreCase);

                foreach (string column in filterColumns)
                {
                    filters[column] = ModelSheet.Cell(row, sheet.ColumnIndex(column));
                }

                choices.Add(new FormChoice(name, columns.Translations(row, "label"), filters, at));
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

            if (columns.Value(row, "style") is { } style)
            {
                foreach (string token in style.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Equals("pages", StringComparison.OrdinalIgnoreCase))
                    {
                        pages = true;
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

            return new FormSettings(
                columns.Value(row, "form_title"),
                columns.Value(row, "form_id"),
                columns.Value(row, "version"),
                columns.Value(row, "default_language"),
                columns.Value(row, "instance_name"),
                pages,
                columns.Value(row, "submission_url"));
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
        string text = string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        string lower = text.ToLowerInvariant();

        if (lower.EndsWith(" or_other", StringComparison.Ordinal) || lower.EndsWith(" or other", StringComparison.Ordinal))
        {
            return (text, null, "'or_other' is refused: an automatic 'other' choice is not in the first version; add a choice and a text question with relevant.");
        }

        foreach ((string alias, string type) in (ReadOnlySpan<(string, string)>)[
            ("select_one_from_file ", "select_one_from_file"), ("select_multiple_from_file ", "select_multiple_from_file"),
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
        Dictionary<string, string>? Label, string? RepeatCount, List<object> Body, string? Relevant, IReadOnlyList<string> Appearance);

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
}

/// <summary>One choice of a list: its name, its labels per language, and the values a choice_filter may read.</summary>
public sealed record FormChoice(string Name, IReadOnlyDictionary<string, string> Labels, IReadOnlyDictionary<string, string> Filters, int Row);

/// <summary>The settings sheet's values.</summary>
public sealed record FormSettings(
    string? Title,
    string? FormId,
    string? Version,
    string? DefaultLanguage,
    string? InstanceName,
    bool Pages,
    string? SubmissionUrl);

/// <summary>One finding, at a sheet, a spreadsheet row (1 is the header; 0 the whole sheet) and a column.</summary>
public sealed record FormIssue(string Sheet, int Row, string? Column, string Message);

/// <summary>The outcome of checking a form: its definition when nothing was refused, and every refusal and notice.</summary>
public sealed record FormCheck(FormDefinition? Definition, IReadOnlyList<FormIssue> Refusals, IReadOnlyList<FormIssue> Notices)
{
    /// <summary>Whether the form can be published.</summary>
    public bool Accepted => Refusals.Count == 0;
}
