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
        "body::accuracyThreshold", "body::accept", "bind::esri:parameters");

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
        ["draw"] = Set("image"),
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
        "normalize-space", "contains", "starts-with", "ends-with", "uuid", "pulldata", "substring", "property",
        // Boolean
        "if", "coalesce", "once", "true", "false", "boolean", "boolean-from-string", "not", "regex", "checklist",
        "weighted-checklist",
        // Number
        "number", "random", "int", "sum", "max", "min", "round", "floor", "ceiling", "pow", "log", "log10", "abs", "sin",
        "cos", "tan", "asin", "acos", "atan", "atan2", "sqrt", "exp", "exp10", "pi",
        // Node-set
        "count", "count-non-empty", "position", "last", "current", "randomize", "instance",
        // Date and time
        "today", "now", "format-date", "format-date-time", "date", "decimal-date-time", "decimal-time", "date-time",
        // Select
        "selected", "selected-at", "count-selected", "jr:choice-name",
        // Repeat
        "indexed-repeat",
        // Geographic
        "area", "distance", "geofence", "intersects");

    /// <summary>
    /// Functions refused, with the reason. The XPath 1.0 core functions that name nodes or languages are here on purpose
    /// (ADR-287 §5.10): a form's answers are not XML nodes with names, namespaces, IDs or <c>xml:lang</c>, so there is no
    /// meaning to give them. The client's evaluator refuses the same names, and a test holds the two lists equal.
    /// </summary>
    public static readonly FrozenDictionary<string, string> RefusedFunctions = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["digest"] = "digests are not in the first version",
        ["base64-decode"] = "base64 decoding is not in the first version",
        ["jr:itext"] = "jr:itext is an internal XForms function; use a label::language column",
        ["lang"] = "lang() reads the xml:lang of a node, and a form's answers carry none; the language is the form's default_language",
        ["id"] = "id() looks elements up by an ID attribute, which an XForm does not use; a question is named by ${name}",
        ["local-name"] = "node names are not in the first version; a question is named by ${name}",
        ["namespace-uri"] = "namespaces are not in the first version; a question is named by ${name}",
        ["name"] = "node names are not in the first version; a question is named by ${name}",
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

    /// <summary>
    /// The device, user and survey properties <c>pulldata("@property", name)</c> may ask for and a browser client can
    /// answer (ADR-287 §5.20). Names are Survey123's documented ones, compared exactly as written there.
    /// </summary>
    public static readonly FrozenSet<string> Properties = Set(
        "online", "utcOffset", "timeZone", "deviceId", "userAgent",
        "email", "username", "userFullName", "userFirstName", "userLastName", "portalUrl",
        "language", "surveyName", "mode", "status");

    /// <summary>Documented properties a browser cannot answer, each with the reason the upload gives.</summary>
    public static readonly FrozenDictionary<string, string> RefusedProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["deviceModel"] = "a browser does not tell a page the model of the device, and Survey123 does not give it in its own web app either",
        ["OS"] = "a browser does not tell a page the operating system's name, and Survey123 does not give it in its own web app either",
        ["appVersion"] = "this client has no build number to give; the form's own version is the version setting",
        ["portalInfo"] = "the whole portal description is a JSON object, which no question in a form can hold",
        ["token"] = "an access token is a credential, and a form's answers are stored and shown, so a token would be written into the layer",
        ["submissionUrl"] = "it exists to point pulldata(\"@layer\") and search() at the layer, and neither is in the first version",
        ["locale"] = "Survey123 offers it only to pulldata(\"@javascript\"), which is refused",
        ["localeInfo"] = "Survey123 offers it only to pulldata(\"@javascript\"), which is refused",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The names ODK's <c>property()</c> function knows, each with the <c>pulldata("@property")</c> name it is answered from.
    /// A <c>uri:</c> prefix is accepted on any of them, as ODK's own forms write it.
    /// </summary>
    public static readonly FrozenDictionary<string, string> OdkProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["deviceid"] = "deviceId",
        ["username"] = "username",
        ["email"] = "email",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>ODK's <c>property()</c> names a browser cannot answer, each with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedOdkProperties = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["subscriberid"] = "a browser cannot read the SIM's subscriber id",
        ["simserial"] = "a browser cannot read the SIM's serial number",
        ["phonenumber"] = "a browser cannot read the device's phone number",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The parts of a geopoint answer <c>pulldata("@geopoint", ${q}, part)</c> is given, the client knows, and answers
    /// from what it recorded (§5.20): the position, the altitude and the horizontal accuracy, and the point written in
    /// degrees, minutes and seconds.
    /// </summary>
    public static readonly FrozenSet<string> GeopointParts = Set(
        "x", "y", "z", "horizontalAccuracy", "accuracy", "positionSourceType",
        "DMS", "DMS.latitudeDegrees", "DMS.latitudeMinutes", "DMS.latitudeSeconds", "DMS.latitudeHemisphere", "DMS.latitudeText",
        "DMS.longitudeDegrees", "DMS.longitudeMinutes", "DMS.longitudeSeconds", "DMS.longitudeHemisphere", "DMS.longitudeText",
        "DMS.text");

    /// <summary>
    /// Parts Survey123 documents that a browser does not record. They are accepted and answer empty, which is what
    /// Survey123 does for a value the hardware did not give (§5.20, INFERRED: its page does not say).
    /// </summary>
    public static readonly FrozenSet<string> GeopointPartsAnsweredEmpty = Set(
        "verticalAccuracy", "speed", "verticalSpeed", "direction", "magneticVariation", "fixType",
        "positionSourceInfo", "positionSourceInfo.antennaHeight", "positionSourceInfo.altitudeType",
        "positionSourceInfo.pluginName", "positionSourceInfo.deviceAddress", "positionSourceInfo.deviceName",
        "positionSourceInfo.deviceType", "positionSourceInfo.networkName", "positionSourceInfo.networkAddress",
        "positionSourceInfo.networkPort", "positionSourceInfo.geoidSeparationCustom", "geoidSeparation", "accuracyType",
        "positionAccuracy", "latitudeError", "longitudeError", "altitudeError", "hdop", "vdop", "pdop", "differentialAge",
        "referenceStationId", "satellitesVisible", "satellitesInUse");

    /// <summary>Documented parts that are refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedGeopointParts = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["UTM"] = "converting to UTM is not built; x and y are the position in degrees",
        ["UTM.easting"] = "converting to UTM is not built; x and y are the position in degrees",
        ["UTM.northing"] = "converting to UTM is not built; x and y are the position in degrees",
        ["UTM.zone"] = "converting to UTM is not built; x and y are the position in degrees",
        ["UTM.hemisphere"] = "converting to UTM is not built; x and y are the position in degrees",
        ["MGRS"] = "converting to MGRS is not built; x and y are the position in degrees",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The <c>pulldata("@…")</c> sources refused, with the reason. <c>@property</c> and <c>@geopoint</c> are the two
    /// built (§5.20); <c>@layer</c> is the one most worth adding next, and is left out for the reason given.
    /// </summary>
    public static readonly FrozenDictionary<string, string> RefusedPulldataSources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["@layer"] = "it queries a feature layer, and an offline client would need a copy of that layer on the device, which is the Inbox's problem and not built",
        ["@json"] = "reading a JSON answer is not in the first version",
        ["@javascript"] = "running a script a form carries is not in the first version, and a form shared with everyone must not carry code the page runs",
        ["@exif"] = "reading the metadata of a photo is not in the first version",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The documented values of <c>calculationMode</c> in <c>bind::esri:parameters</c> (§5.20).</summary>
    public static readonly FrozenSet<string> CalculationModes = Set("auto", "manual", "always", "whenEmpty");

    /// <summary>The question types Survey123 allows <c>calculationMode</c> on.</summary>
    public static readonly FrozenSet<string> CalculationModeTypes = Set(
        "text", "decimal", "integer", "note", "date", "time", "dateTime", "select_one", "select_multiple", "geopoint", "calculate", "hidden");

    private static FrozenSet<string> Set(params string[] items) => items.ToFrozenSet(StringComparer.Ordinal);

    private static FrozenSet<string> Columns(params string[] items) => items.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
