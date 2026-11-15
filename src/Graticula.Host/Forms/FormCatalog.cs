using System;
using System.Collections.Frozen;
using System.Collections.Generic;

namespace Graticula.Host.Forms;

/// <summary>
/// The first version's list, ADR-287 §5.10: what is supported, what is refused, and what is drawn the default way.
/// </summary>
/// <remarks>
/// <b>One place.</b> The checker reads these sets and the client's evaluator is tested against the same list (condition
/// 4). A name added to a set here without the behaviour behind it is a form that is accepted and then misbehaves, so a
/// change here is a change to §5.10 first.
/// </remarks>
public static class FormCatalog
{
    /// <summary>Question types the client fills in and the layer stores.</summary>
    public static readonly FrozenSet<string> InputTypes = Set(
        "integer", "decimal", "range", "text", "note", "geopoint", "geotrace", "geoshape", "date", "time", "dateTime",
        "image", "audio", "video", "file", "acknowledge");

    /// <summary>Types with no screen: a value worked out or set.</summary>
    public static readonly FrozenSet<string> ValueTypes = Set("calculate", "hidden");

    /// <summary>Metadata the client fills in by itself.</summary>
    public static readonly FrozenSet<string> MetadataTypes = Set("start", "end", "today", "username", "email", "deviceid");

    /// <summary>Location types; at most one per table (§5.2).</summary>
    public static readonly FrozenSet<string> LocationTypes = Set("geopoint", "geotrace", "geoshape");

    /// <summary>Types stored as attachments.</summary>
    public static readonly FrozenSet<string> AttachmentTypes = Set("image", "audio", "video", "file");

    /// <summary>Types refused, each with the reason the upload gives.</summary>
    public static readonly FrozenDictionary<string, string> RefusedTypes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rank"] = "ordering a list is not in the first version",
        ["barcode"] = "Safari has no barcode reader, so the question could not be answered on an iPhone",
        ["background-audio"] = "recording in the background is not in the first version",
        ["xml-external"] = "external XML data is not in the first version",
        ["csv-external"] = "external CSV data through instance() is not in the first version; use pulldata() or select_one_from_file",
        ["select_one_external"] = "external choices are not in the first version; use select_one_from_file with a CSV",
        ["select_multiple_external"] = "external choices are not in the first version; use select_multiple_from_file with a CSV",
        ["phonenumber"] = "a browser cannot read the device's phone number",
        ["audit"] = "audit logs are not in the first version",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Survey columns supported, by their base name (before any <c>::language</c>).</summary>
    public static readonly FrozenSet<string> SurveyColumns = Columns(
        "type", "name", "label", "hint", "guidance_hint", "required", "required_message", "constraint",
        "constraint_message", "relevant", "default", "read_only", "readonly", "calculation", "choice_filter",
        "appearance", "parameters", "repeat_count", "image", "media::image",
        "bind::esri:fieldType", "bind::esri:fieldLength", "bind::esri:fieldAlias", "body::esri:visible",
        "body::accuracyThreshold", "body::accept");

    /// <summary>Survey columns that change only how a question looks; drawn the default way, with a notice.</summary>
    public static readonly FrozenSet<string> PresentationSurveyColumns = Columns("body::esri:style", "bind::saveIncomplete");

    /// <summary>Survey columns refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedSurveyColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["trigger"] = "triggered calculations are not in the first version",
        ["audio"] = "audio in a label is not in the first version",
        ["media::audio"] = "audio in a label is not in the first version",
        ["video"] = "video in a label is not in the first version",
        ["media::video"] = "video in a label is not in the first version",
        ["bind::esri:parameters"] = "its calculationMode changes the values a form stores, which is not in the first version",
        ["bind::esri:warning"] = "warnings are not in the first version",
        ["bind::esri:warning_message"] = "warnings are not in the first version",
        ["body::esri:inputMask"] = "input masks are not in the first version",
        ["bind::esri:workflow"] = "rangefinder workflows are not in the first version",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Columns that may carry a <c>::language</c> suffix.</summary>
    public static readonly FrozenSet<string> TranslatableColumns = Columns(
        "label", "hint", "guidance_hint", "required_message", "constraint_message", "image", "media::image");

    /// <summary>Choices columns supported. Any other column is a column a <c>choice_filter</c> may read.</summary>
    public static readonly FrozenSet<string> ChoiceColumns = Columns("list_name", "list name", "name", "label", "image", "media::image");

    /// <summary>Settings columns supported.</summary>
    public static readonly FrozenSet<string> SettingsColumns = Columns(
        "form_title", "form_id", "version", "instance_name", "default_language", "style", "allow_choice_duplicates",
        "clean_text_values", "name", "submission_url", "graticula:basemap", "graticula:thank_you", "graticula:redirect");

    /// <summary>The grounds a publisher may put under a location question's map (§5.15, 2026-11-15).</summary>
    public static readonly FrozenSet<string> Basemaps = Set("osm", "topo", "none");

    /// <summary>Settings columns accepted with no effect, which the upload says.</summary>
    public static readonly FrozenSet<string> InertSettingsColumns = Columns("namespaces");

    /// <summary>Settings columns refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedSettingsColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["public_key"] = "encrypted submissions are not in the first version",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Appearances supported, each with the types it applies to (<c>*</c> for any).</summary>
    public static readonly FrozenDictionary<string, FrozenSet<string>> SupportedAppearances = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
    {
        ["multiline"] = Set("text", "image", "file"),
        ["signature"] = Set("image"),
        ["month-year"] = Set("date"),
        ["year"] = Set("date"),
        ["minimal"] = Set("select_one", "select_multiple", "select_one_from_file", "select_multiple_from_file"),
        ["likert"] = Set("select_one", "select_one_from_file"),
        ["rating"] = Set("range"),
        ["hidden"] = Set("*"),
        ["field-list"] = Set("begin group", "begin repeat"),
        ["table-list"] = Set("begin group"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Appearances that change only how a question looks; drawn the default way, with a notice.</summary>
    public static readonly FrozenSet<string> PresentationAppearances = Set(
        "horizontal", "horizontal-compact", "compact", "quick", "quickcompact", "label", "list-nolabel", "counter",
        "no-calendar", "predictivetext", "nopredictivetext", "calculator", "numbers", "press-to-locate", "hide-input",
        "tabular", "columns", "columns-pack", "no-buttons");

    /// <summary>Appearances refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedAppearances = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["draw"] = "drawing on a canvas is not in the first version",
        ["annotate"] = "annotating a photo is not in the first version",
        ["map"] = "choosing on a map is not in the first version",
        ["quick map"] = "choosing on a map is not in the first version",
        ["autocomplete"] = "type-ahead choice lists are not in the first version",
        ["geocode"] = "geocoding a text answer is not in the first version",
        ["distress"] = "the distress scale is a Survey123 field app control",
        ["spike"] = "Spike devices are not supported",
        ["spike-full-measure"] = "Spike devices are not supported",
        ["spike-point-to-point"] = "Spike devices are not supported",
        ["rangefinder"] = "rangefinders are not supported",
        ["image-map"] = "image maps are not in the first version",
        ["inframarker"] = "inframarkers are not supported",
        ["getinframarkerid"] = "inframarkers are not supported",
        ["new-front"] = "choosing the camera is not in the first version",
        ["new-rear"] = "choosing the camera is not in the first version",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Functions an expression may call (ADR-287 §5.10, from the ODK XForms function table).</summary>
    public static readonly FrozenSet<string> Functions = Set(
        // String
        "string", "concat", "join", "substr", "substring-before", "substring-after", "translate", "string-length",
        "normalize-space", "contains", "starts-with", "ends-with", "uuid", "pulldata",
        // Boolean
        "if", "coalesce", "once", "true", "false", "boolean", "boolean-from-string", "not", "regex", "checklist",
        "weighted-checklist",
        // Number
        "number", "random", "int", "sum", "max", "min", "round", "pow", "log", "log10", "abs", "sin", "cos", "tan",
        "asin", "acos", "atan", "atan2", "sqrt", "exp", "exp10", "pi",
        // Node-set
        "count", "count-non-empty", "position", "current", "randomize", "instance",
        // Date and time
        "today", "now", "format-date", "format-date-time", "date", "decimal-date-time", "decimal-time",
        // Select
        "selected", "selected-at", "count-selected", "jr:choice-name",
        // Repeat
        "indexed-repeat",
        // Geographic
        "area", "distance", "geofence", "intersects");

    /// <summary>Functions refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedFunctions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["digest"] = "digests are not in the first version",
        ["base64-decode"] = "base64 decoding is not in the first version",
        ["jr:itext"] = "jr:itext is an internal XForms function; use a label::language column",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary><c>bind::esri:fieldType</c> values this server can store, and the field each becomes.</summary>
    public static readonly FrozenDictionary<string, string> EsriFieldTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["esriFieldTypeInteger"] = "esriFieldTypeInteger",
        ["esriFieldTypeSmallInteger"] = "esriFieldTypeSmallInteger",
        ["esriFieldTypeBigInteger"] = "esriFieldTypeBigInteger",
        ["esriFieldTypeDouble"] = "esriFieldTypeDouble",
        ["esriFieldTypeSingle"] = "esriFieldTypeSingle",
        ["esriFieldTypeString"] = "esriFieldTypeString",
        ["esriFieldTypeDate"] = "esriFieldTypeDate",
        ["esriFieldTypeDateOnly"] = "esriFieldTypeDate",
        ["esriFieldTypeGUID"] = "esriFieldTypeGUID",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>bind::esri:fieldType</c> values refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedEsriFieldTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["esriFieldTypeTimeOnly"] = "this server has no time-only field, and storing a time as anything else would change it",
        ["esriFieldTypeTimestampOffset"] = "this server has no field that keeps a time zone offset, and dropping it would change the value",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static FrozenSet<string> Set(params string[] items) => items.ToFrozenSet(StringComparer.Ordinal);

    private static FrozenSet<string> Columns(params string[] items) => items.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
