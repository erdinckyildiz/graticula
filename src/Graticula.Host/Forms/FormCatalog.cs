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
        "image", "audio", "video", "file", "acknowledge", "barcode");

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
        ["background-audio"] = "recording in the background is not in the first version",
        ["xml-external"] = "external XML data is not in the first version",
        ["csv-external"] = "external CSV data through instance() is not in the first version; use pulldata() or select_one_from_file",
        ["select_one_external"] = "external choices are not in the first version; use select_one_from_file with a CSV",
        ["select_multiple_external"] = "external choices are not in the first version; use select_multiple_from_file with a CSV",
        ["phonenumber"] = "a browser cannot read the device's phone number",
        ["subscriberid"] = "a browser cannot read the SIM's subscriber id",
        ["simserial"] = "a browser cannot read the SIM's serial number",
        ["audit"] = "audit logs are not in the first version",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Survey columns supported, by their base name (before any <c>::language</c>).</summary>
    public static readonly FrozenSet<string> SurveyColumns = Columns(
        "type", "name", "label", "hint", "guidance_hint", "required", "required_message", "constraint",
        "constraint_message", "relevant", "default", "read_only", "readonly", "calculation", "choice_filter",
        "appearance", "parameters", "repeat_count", "image", "media::image",
        "bind::esri:fieldType", "bind::esri:fieldLength", "bind::esri:fieldAlias", "body::esri:visible",
        "body::accuracyThreshold", "body::accept", "bind::esri:parameters", "body::esri:inputMask",
        // ADR-287 §5.26: audio in a label, a warning that lets the answer go, and the XForms bind type pyxform writes through.
        "audio", "media::audio", "bind::esri:warning", "bind::esri:warning_message", "bind::type");

    /// <summary>Survey columns that change only how a question looks; drawn the default way, with a notice.</summary>
    public static readonly FrozenSet<string> PresentationSurveyColumns = Columns("body::esri:style", "bind::saveIncomplete");

    /// <summary>Survey columns refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedSurveyColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["trigger"] = "triggered calculations are not in the first version",
        ["video"] = "video in a label is not in the first version",
        ["media::video"] = "video in a label is not in the first version",
        ["bind::esri:workflow"] = "rangefinder workflows are not in the first version",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Columns that may carry a <c>::language</c> suffix.</summary>
    public static readonly FrozenSet<string> TranslatableColumns = Columns(
        "label", "hint", "guidance_hint", "required_message", "constraint_message", "image", "media::image",
        "audio", "media::audio", "bind::esri:warning_message");

    /// <summary>Choices columns supported. Any other column is a column a <c>choice_filter</c> may read.</summary>
    public static readonly FrozenSet<string> ChoiceColumns = Columns("list_name", "list name", "name", "label", "image", "media::image", "audio", "media::audio");

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
        ["minimal"] = Set("select_one", "select_multiple", "select_one_from_file", "select_multiple_from_file", "begin group", "begin repeat", "barcode"),
        ["compact"] = Set("begin group", "begin repeat"),
        ["autocomplete"] = Set("select_one", "select_one_from_file"),
        ["spinner"] = Set("integer", "decimal"),
        ["week-number"] = Set("date"),
        ["likert"] = Set("select_one", "select_one_from_file"),
        ["rating"] = Set("range"),
        ["hidden"] = Set("*"),
        ["draw"] = Set("image"),
        ["annotate"] = Set("image"),
        ["field-list"] = Set("begin group", "begin repeat"),
        ["table-list"] = Set("begin group"),
        ["hidden-answer"] = Set("barcode"),
        // ADR-287 §5.26.
        ["distress"] = Set("integer"),
        ["new"] = Set("image", "audio", "video"),
        ["new-front"] = Set("image"),
        ["new-rear"] = Set("image"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Appearances supported on some types and drawn the default way on the rest (<c>compact</c> collapses a group and
    /// is only a layout of choices on a select; <c>minimal</c> is a drop-down on a select, collapses a group or a repeat,
    /// and means nothing on any other type, where Survey123 ignores it). ADR-287 §5.19.
    /// </summary>
    public static readonly FrozenSet<string> PresentationOnOtherTypes = Set("compact", "minimal");

    /// <summary>Appearances that change only how a question looks; drawn the default way, with a notice.</summary>
    public static readonly FrozenSet<string> PresentationAppearances = Set(
        "horizontal", "horizontal-compact", "compact", "quick", "quickcompact", "label", "list-nolabel", "counter",
        "no-calendar", "predictivetext", "nopredictivetext", "calculator", "numbers", "press-to-locate", "hide-input",
        "tabular", "columns", "columns-pack", "no-buttons", "thousands-sep", "no-ticks");

    /// <summary>Appearances refused, with the reason.</summary>
    public static readonly FrozenDictionary<string, string> RefusedAppearances = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["map"] = "choosing on a map is not in the first version",
        ["quick map"] = "choosing on a map is not in the first version",
        ["geocode"] = "geocoding a text answer is not in the first version",
        ["spike"] = "Spike devices are not supported",
        ["spike-full-measure"] = "Spike devices are not supported",
        ["spike-point-to-point"] = "Spike devices are not supported",
        ["rangefinder"] = "rangefinders are not supported",
        ["image-map"] = "image maps are not in the first version",
        ["inframarker"] = "inframarkers are not supported",
        ["getinframarkerid"] = "inframarkers are not supported",
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

    /// <summary>
    /// The field types a <c>barcode</c> answer may be stored as (ADR-287 §5.22): text, or not stored. A scanned code is
    /// text — an EAN or a UPC may begin with zeros a number would drop, and a QR code holds anything.
    /// </summary>
    public static readonly FrozenSet<string> BarcodeFieldTypes = Columns("esriFieldTypeString", "null");

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
        ["submissionUrl"] = "the address answers go to here is a view made for the form when it is shared, not the layer itself, so a query written with it would read the view; write the address of the layer in the call (§5.23)",
        ["locale"] = "Survey123 offers it to pulldata(\"@javascript\") scripts, and a script here is given only the values the form passes it (§5.27); pass pulldata(\"@property\", 'language') to the script instead",
        ["localeInfo"] = "Survey123 offers it to pulldata(\"@javascript\") scripts as an object no question can hold, and a script here is given only the values the form passes it (§5.27); pass pulldata(\"@property\", 'language') to the script instead",
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
    /// The <c>pulldata("@…")</c> sources refused, with the reason. <c>@property</c> and <c>@geopoint</c> are built (§5.20),
    /// <c>@layer</c> since §5.23, and <c>@javascript</c> since §5.27 — run in a sandbox the page cannot be reached from,
    /// and never on a form shared with everyone.
    /// </summary>
    public static readonly FrozenDictionary<string, string> RefusedPulldataSources = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["@json"] = "reading a JSON answer is not in the first version",
        ["@exif"] = "reading the metadata of a photo is not in the first version",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The folder a form's scripts are uploaded to among the item's files, as Survey123 keeps them in the survey's
    /// <c>scripts</c> folder (§5.27). A script is also found at its bare name, as a CSV and a picture are.
    /// </summary>
    public const string ScriptFolder = "scripts/";

    /// <summary>The largest script a form may carry, in bytes: the sandbox is handed the whole file (§5.27).</summary>
    public const int MaximumScriptBytes = 256 * 1024;

    /// <summary>The documented values of <c>calculationMode</c> in <c>bind::esri:parameters</c> (§5.20).</summary>
    public static readonly FrozenSet<string> CalculationModes = Set("auto", "manual", "always", "whenEmpty");

    /// <summary>The question types Survey123 allows <c>calculationMode</c> on.</summary>
    public static readonly FrozenSet<string> CalculationModeTypes = Set(
        "text", "decimal", "integer", "note", "date", "time", "dateTime", "select_one", "select_multiple", "geopoint", "calculate", "hidden");

    /// <summary>
    /// Type spellings pyxform reads as another type (its <c>question_type_dictionary</c> and <c>aliases</c>), each with the
    /// type it is read as here — ADR-287 §5.26. The type cell is compared lower-cased.
    /// </summary>
    public static readonly FrozenDictionary<string, string> TypeSpellings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["string"] = "text",
        ["int"] = "integer",
        ["trigger"] = "acknowledge",
        ["phone number"] = "text",
        ["imei"] = "deviceid",
        ["device id"] = "deviceid",
        ["get device id"] = "deviceid",
        ["uri:deviceid"] = "deviceid",
        ["uri:username"] = "username",
        ["uri:email"] = "email",
        ["subscriber id"] = "subscriberid",
        ["get subscriber id"] = "subscriberid",
        ["uri:subscriberid"] = "subscriberid",
        ["sim id"] = "simserial",
        ["get sim id"] = "simserial",
        ["uri:simserial"] = "simserial",
        ["get phone number"] = "phonenumber",
        ["uri:phonenumber"] = "phonenumber",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// The settings <c>style</c> values that lay groups out as a grid (ADR-287 §5.26): Survey123's two, and the older
    /// <c>theme-grid</c> its forms still carry.
    /// </summary>
    public static readonly FrozenSet<string> GridStyles = Columns("fixed-grid", "dynamic-grid", "theme-grid");

    /// <summary>The keys of <c>bind::esri:parameters</c> Survey123 documents for its field app only, by question type (§5.26).</summary>
    public static readonly FrozenDictionary<string, FrozenSet<string>> FieldAppParameters = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
    {
        ["image"] = Columns("watermark", "topLeftWatermark", "topWatermark", "topRightWatermark", "leftWatermark", "centerWatermark",
            "rightWatermark", "bottomLeftWatermark", "bottomWatermark", "bottomRightWatermark", "smartAttributes", "redaction",
            "smartAnnotation"),
        ["audio"] = Columns("codec"),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The longest recording an <c>audio</c> question takes when it does not say: Survey123's web app's ten minutes (§5.26).</summary>
    public const int DefaultMaxDurationSeconds = 600;

    private static FrozenSet<string> Set(params string[] items) => items.ToFrozenSet(StringComparer.Ordinal);

    private static FrozenSet<string> Columns(params string[] items) => items.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
