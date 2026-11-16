using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Graticula.Catalog;
using Graticula.Features;
using Graticula.Providers.PostGis;

namespace Graticula.Host.Forms;

/// <summary>
/// What publishing a form makes — ADR-287 §5.2 and §5.10's last table — worked out before anything is made.
/// </summary>
/// <remarks>
/// <para>
/// <b>A plan, then the work.</b> Everything that can be refused is refused here, from the definition alone: two
/// questions that would become one column, a numeric field whose choices are not numbers. The publisher then makes what
/// the plan says, so a refusal never leaves half a form's layers behind.
/// </para>
/// <para>
/// <b>Names.</b> A question keeps its name in the form; its column is the name with letters such as <c>ş</c>,
/// <c>ı</c> and <c>ü</c> written as the Latin letters they carry (<c>s</c>, <c>i</c>, <c>u</c>) and then the importer's
/// one rule applied (<see cref="PostGisImporter.ColumnNameFor"/>). Without the first step a Turkish name became a run
/// of underscores. The client is sent the question-to-column map, so it never has to know the rule.
/// </para>
/// </remarks>
public sealed record FormLayerPlan(
    string ServiceName,
    IReadOnlyList<PlannedLayer> Layers,
    IReadOnlyList<PlannedDomain> Domains,
    IReadOnlyList<FormIssue> Refusals)
{
    /// <summary>The spatial reference a form's layers are stored in: WGS 84, what a device's position is given in. INFERRED.</summary>
    public const int Srid = 4326;

    /// <summary>The longest a text field is when the form does not say: Survey123's default, kept for §5.9 (d).</summary>
    public const int DefaultTextLength = 255;

    /// <summary>The column a related table's rows name their parent by.</summary>
    public const string ParentColumn = "parentglobalid";

    /// <summary>Whether the plan can be carried out.</summary>
    public bool Accepted => Refusals.Count == 0;

    /// <summary>Plans a checked form's layers.</summary>
    /// <param name="definition">The form, already checked.</param>
    /// <param name="fallbackName">The name to use when the settings give no <c>form_id</c>: the item's title.</param>
    public static FormLayerPlan For(FormDefinition definition, string fallbackName)
    {
        ArgumentNullException.ThrowIfNull(definition);

        List<FormIssue> refusals = [];
        string service = Identifier(definition.Settings.FormId ?? fallbackName);

        if (service.Length == 0)
        {
            refusals.Add(new FormIssue("settings", 2, "form_id", "The form needs a form_id or a title its layer can be named after."));
            service = "form";
        }

        string language = definition.Settings.DefaultLanguage is { Length: > 0 } chosen && chosen != "default"
            ? chosen
            : (definition.Languages.Count > 0 ? definition.Languages[0] : string.Empty);

        Dictionary<string, PlannedDomain> domains = new(StringComparer.Ordinal);
        List<PlannedLayer> layers = [];

        foreach ((FormTable table, string? parent) in Walk(definition.Root, null))
        {
            string layerName = table.Name.Length == 0 ? service : $"{service}_{Identifier(table.Name)}";
            Dictionary<string, FormQuestion> columns = new(StringComparer.OrdinalIgnoreCase)
            {
                ["objectid"] = null!,
                ["geom"] = null!,
                ["globalid"] = null!,
                [ParentColumn] = null!,
            };
            List<PlannedField> fields = [];

            foreach (FormQuestion question in table.Questions)
            {
                if (Stored(question) is not { } type)
                {
                    continue;
                }

                string column = Column(question.Name);

                if (columns.TryGetValue(column, out FormQuestion? taken))
                {
                    refusals.Add(new FormIssue("survey", question.Row, "name", taken is null
                        ? $"'{question.Name}' becomes the column '{column}', which the layer keeps for itself."
                        : $"'{question.Name}' and '{taken.Name}' (row {taken.Row}) both become the column '{column}'; rename one."));
                    continue;
                }

                columns[column] = question;
                PlannedDomain? domain = null;

                // A search() list's one row names columns, not answers (§5.23), so it makes no domain.
                if (question.Type == "select_one" && question.Search is null && question.Argument is { } list && definition.ChoiceLists.TryGetValue(list, out IReadOnlyList<FormChoice>? choices))
                {
                    if (!domains.TryGetValue(list, out domain))
                    {
                        domain = Domain(service, list, choices, language);
                        domains[list] = domain;
                    }

                    if (type != FieldType.Text && choices.FirstOrDefault(c => !decimal.TryParse(c.Name, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) is { } word)
                    {
                        refusals.Add(new FormIssue("survey", question.Row, "bind::esri:fieldType",
                            $"'{question.Name}' is stored as a number, and the choice '{word.Name}' of '{list}' (choices row {word.Row}) is not one."));
                    }
                }

                int? length = type == FieldType.Text ? question.FieldLength ?? DefaultTextLength : null;
                string alias = question.FieldAlias ?? Label(question.Labels, language) ?? question.Name;
                fields.Add(new PlannedField(question.Name, column, type, length, alias, domain));
            }

            if (fields.Count > 250)
            {
                refusals.Add(new FormIssue("survey", table.Row, null,
                    $"{(table.Name.Length == 0 ? "The form's top level" : $"The repeat '{table.Name}'")} would have {fields.Count} fields, and a layer has at most 250."));
            }

            FormQuestion? location = table.Location;
            layers.Add(new PlannedLayer(
                layerName,
                table.Name,
                location is null ? null : GeometryOf(location.Type),
                location?.Name,
                fields,
                table.Questions.Where(q => FormCatalog.AttachmentTypes.Contains(q.Type)).Select(q => q.Name).ToList(),
                parent));
        }

        HashSet<string> layerNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (PlannedLayer layer in layers)
        {
            if (!layerNames.Add(layer.Name))
            {
                refusals.Add(new FormIssue("survey", 0, null, $"Two of the form's tables would both be named '{layer.Name}'; rename a repeat."));
            }
        }

        return new FormLayerPlan(service, layers, [.. domains.Values], refusals);
    }

    /// <summary>
    /// The column a question's answer is stored in: Latin letters for the accented ones, then the importer's rule.
    /// </summary>
    public static string Column(string question) => PostGisImporter.ColumnNameFor(Latin(question));

    /// <summary>A name made into an identifier: Latin letters, lower case, digits and underscores.</summary>
    public static string Identifier(string text)
    {
        StringBuilder safe = new();

        foreach (char c in Latin(text.Trim()).ToLowerInvariant())
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        }

        string collapsed = string.Join('_', safe.ToString().Split('_', StringSplitOptions.RemoveEmptyEntries));

        if (collapsed.Length > 48)
        {
            collapsed = collapsed[..48].TrimEnd('_');
        }

        return collapsed.Length > 0 && char.IsAsciiDigit(collapsed[0]) ? "f_" + collapsed : collapsed;
    }

    /// <summary>Accented letters as the Latin letters they carry, by a table.</summary>
    /// <remarks>
    /// <b>A table and not Unicode decomposition</b>, because this repository runs with invariant globalization, where
    /// <c>Normalize(FormD)</c> leaves <c>ş</c> as it is: measured, the first version of this method turned
    /// <c>şehir</c> into <c>_ehir</c>. The table covers Turkish and the Latin-1 and Latin Extended-A letters; anything
    /// else falls to the importer's rule, which writes an underscore.
    /// </remarks>
    public static string Latin(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        StringBuilder plain = new(text.Length);

        foreach (char c in text)
        {
            int at = Accented.IndexOf(c, StringComparison.Ordinal);
            plain.Append(at >= 0 ? Plain[at].ToString() : c switch
            {
                'ß' => "ss",
                'æ' => "ae",
                'Æ' => "AE",
                'œ' => "oe",
                'Œ' => "OE",
                'þ' => "th",
                'Þ' => "TH",
                _ => c.ToString(),
            });
        }

        return plain.ToString();
    }

    private const string Accented =
        "çÇğĞıİöÖşŞüÜâÂîÎûÛ"
        + "àáãäåÀÁÃÄÅèéêëÈÉÊËìíïÌÍÏòóôõøÒÓÔÕØùúÙÚýÿÝñÑ"
        + "āĀăĂąĄćĆĉĈċĊčČďĎđĐēĒĕĔėĖęĘěĚĝĜġĠģĢĥĤħĦĩĨīĪĭĬįĮĵĴķĶĺĹļĻľĽŀĿłŁńŃņŅňŇōŌŏŎőŐŕŔŗŖřŘśŚŝŜšŠţŢťŤŧŦũŨūŪŭŬůŮűŰųŲŵŴŷŶźŹżŻžŽ";

    private const string Plain =
        "cCgGiIoOsSuUaAiIuUaaaaaAAAAAeeeeEEEEiiiIIIoooooOOOOOuuUUyyYnNaAaAaAcCcCcCcCdDdDeEeEeEeEeEgGgGgGhHhHiIiIiIiIjJkKlLlLlLlLlLnNnNnNoOoOoOrRrRrRsSsSsStTtTtTuUuUuUuUuUuUwWyYzZzZzZ";

    /// <summary>The field a question is stored in, or null when it is stored as something else or not at all.</summary>
    public static FieldType? Stored(FormQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (question.EsriFieldType == "null"
            || FormCatalog.LocationTypes.Contains(question.Type)
            || FormCatalog.AttachmentTypes.Contains(question.Type)
            || (question.Type == "note" && question.Calculation is null))
        {
            return null;
        }

        if (question.EsriFieldType is { } esri)
        {
            return esri switch
            {
                "esriFieldTypeInteger" => FieldType.Integer,
                "esriFieldTypeSmallInteger" => FieldType.SmallInteger,
                "esriFieldTypeBigInteger" => FieldType.BigInteger,
                "esriFieldTypeDouble" => FieldType.Double,
                "esriFieldTypeSingle" => FieldType.Single,
                "esriFieldTypeDate" => FieldType.Date,
                "esriFieldTypeGUID" => FieldType.Guid,
                _ => FieldType.Text,
            };
        }

        return question.Type switch
        {
            "integer" => FieldType.Integer,
            "decimal" => FieldType.Double,
            "range" => Whole(question) ? FieldType.Integer : FieldType.Double,
            "date" or "dateTime" or "start" or "end" or "today" => FieldType.Date,
            _ => FieldType.Text,
        };
    }

    private static bool Whole(FormQuestion question) =>
        ((string[])["start", "end", "step"]).All(k => !question.Parameters.TryGetValue(k, out string? v)
            || (decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal d) && d == decimal.Truncate(d)));

    private static string GeometryOf(string type) => type switch
    {
        "geopoint" => "Point",
        "geotrace" => "LineString",
        _ => "Polygon",
    };

    private static string? Label(IReadOnlyDictionary<string, string> labels, string language) =>
        labels.TryGetValue(language, out string? text) ? text.Trim()
        : labels.TryGetValue(string.Empty, out string? plain) ? plain.Trim()
        : labels.Values.FirstOrDefault()?.Trim();

    private static PlannedDomain Domain(string service, string list, IReadOnlyList<FormChoice> choices, string language) =>
        new($"{service}_{Identifier(list)}", list,
            choices.DistinctBy(c => c.Name).Select(c => new CodedValue(DomainValue.Of(c.Name), Label(c.Labels, language) ?? c.Name)).ToList());

    private static IEnumerable<(FormTable Table, string? Parent)> Walk(FormTable table, string? parent)
    {
        yield return (table, parent);

        foreach (FormTable repeat in table.Repeats)
        {
            foreach ((FormTable t, string? p) in Walk(repeat, table.Name))
            {
                yield return (t, p);
            }
        }
    }
}

/// <summary>One layer or table a form makes.</summary>
/// <param name="Name">Its layer name.</param>
/// <param name="Repeat">The repeat it holds, or empty for the form's top level.</param>
/// <param name="GeometryType">Point, LineString or Polygon, or null for a table.</param>
/// <param name="LocationQuestion">The question whose answer is the geometry, or null.</param>
/// <param name="Fields">Its fields, in form order.</param>
/// <param name="AttachmentQuestions">The questions whose answers are its attachments; none means attachments stay off.</param>
/// <param name="ParentRepeat">The repeat it is related to (empty for the top level), or null for the top level itself.</param>
public sealed record PlannedLayer(
    string Name,
    string Repeat,
    string? GeometryType,
    string? LocationQuestion,
    IReadOnlyList<PlannedField> Fields,
    IReadOnlyList<string> AttachmentQuestions,
    string? ParentRepeat);

/// <summary>One field: the question, its column, its type, its length for text, its alias, and its choice list.</summary>
public sealed record PlannedField(string Question, string Column, FieldType Type, int? Length, string Alias, PlannedDomain? Domain);

/// <summary>A shared coded-value domain a choice list becomes.</summary>
public sealed record PlannedDomain(string Name, string List, IReadOnlyList<CodedValue> Codes);
