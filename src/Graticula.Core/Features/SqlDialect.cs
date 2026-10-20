using System;
using System.Collections.Generic;
using System.Globalization;

namespace Graticula.Features;

/// <summary>
/// How one datastore spells what <see cref="PredicateSql"/> writes, where the spellings differ.
/// </summary>
/// <remarks>
/// <para>
/// <b>Became an object on 2026-09-23, when the second difference arrived</b> — the placeholder was the
/// only one until then, and <see cref="PredicateSql"/>'s own remark said it would become a dialect "when a
/// datastore needs more than that". ArcGIS's standardized functions (ADR-083) are that. Every difference
/// here was measured on PostgreSQL 16 and DuckDB 1.5.5 with the same one-row table, not read from
/// documentation:
/// </para>
/// <list type="bullet">
/// <item><c>7 / 2</c> is 3 in PostgreSQL and 3.5 in DuckDB. Both are made to truncate, the SQL
/// standard's answer and what an ArcGIS user on PostgreSQL or SQL Server already gets.</item>
/// <item><c>round(7.5::double precision, 1)</c>, <c>trunc(…, 1)</c> and <c>mod(7.5::double precision, 2)</c>
/// do not exist in PostgreSQL, which has them for <c>numeric</c> only; DuckDB has them for every number,
/// and casting to its <c>numeric</c> would round to three places.</item>
/// <item><c>cast(s as varchar(4))</c> cuts to four characters in PostgreSQL and ignores the length in
/// DuckDB — so the cut is written as a <c>substring</c>, which both do alike, in every dialect.</item>
/// </list>
/// </remarks>
public sealed class SqlDialect
{
    private SqlDialect(
        string name,
        Func<int, string> placeholder,
        bool numericForPlaces,
        string integerDivision,
        IReadOnlySet<string>? guidColumns = null,
        bool arcGisGuidText = false,
        bool oracleForms = false,
        bool mySqlForms = false)
    {
        OracleForms = oracleForms;
        MySqlForms = mySqlForms;
        Name = name;
        Placeholder = placeholder;
        NumericForPlaces = numericForPlaces;
        IntegerDivision = integerDivision;
        GuidColumns = guidColumns ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ArcGisGuidText = arcGisGuidText;
    }

    /// <summary>PostgreSQL through Npgsql: <c>@w0</c>, <c>@w1</c>.</summary>
    public static SqlDialect PostgreSql { get; } = new(
        "PostgreSQL", index => $"@w{index.ToString(CultureInfo.InvariantCulture)}", numericForPlaces: true, "/");

    /// <summary>DuckDB, with <c>$w0</c> placeholders — how the GeoParquet provider binds by name.</summary>
    public static SqlDialect DuckDb { get; } = new(
        "DuckDB", index => $"$w{index.ToString(CultureInfo.InvariantCulture)}", numericForPlaces: false, "//");

    /// <summary>
    /// Oracle, with <c>:w0</c> placeholders — ADR-205 — and <see cref="OracleForms"/> for everything Oracle spells
    /// its own way.
    /// </summary>
    public static SqlDialect Oracle { get; } = new(
        "Oracle", index => $":w{index.ToString(CultureInfo.InvariantCulture)}", numericForPlaces: false, "/",
        oracleForms: true);

    /// <summary>
    /// MySQL, with <c>@w0</c> placeholders as MySqlConnector binds them — ADR-208 — on a session in <c>ANSI</c> mode, so
    /// names are double-quoted and <c>||</c> concatenates as in PostgreSQL; <see cref="MySqlForms"/> for the rest.
    /// </summary>
    public static SqlDialect MySql { get; } = new(
        "MySQL", index => $"@w{index.ToString(CultureInfo.InvariantCulture)}", numericForPlaces: false, "div",
        mySqlForms: true);

    /// <summary>
    /// Whether MySQL's spellings are used where they differ from PostgreSQL's — ADR-208, measured against MySQL 8.4.11
    /// on 2026-10-20: no <c>ilike</c> (folded through <c>lower</c>; MySQL's default escape is already the backslash),
    /// <c>signed</c>, <c>char</c> and <c>double</c> as cast targets, <c>truncate(x, n)</c> for <c>trunc</c>, and
    /// <c>div</c> for a division of whole numbers.
    /// </summary>
    public bool MySqlForms { get; }

    /// <summary>
    /// Whether Oracle's spellings are used where they differ from PostgreSQL's and DuckDB's — ADR-205, measured
    /// against Oracle AI Database 26ai Free on 2026-10-19.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One flag, not one per difference</b>, because the differences come together and only Oracle has them:
    /// no <c>ilike</c> (folded through <c>lower</c>), no default escape in <c>like</c> (PostgreSQL's is a
    /// backslash, so patterns written for it say <c>escape</c>), <c>substr</c> and <c>instr</c> rather than
    /// <c>substring … from … for</c> and <c>position … in</c>, <c>ceil</c>, <c>length</c>, <c>log(10, x)</c>, a
    /// two-argument <c>concat</c> (so <c>||</c>), no <c>bigint</c> and no unsized <c>varchar</c> in a cast, a
    /// division of whole numbers that does not truncate (so <c>trunc</c>), and no hour in a <c>DATE</c> for
    /// <c>extract</c> (so it is cast to a timestamp first).
    /// </para>
    /// </remarks>
    public bool OracleForms { get; }

    /// <summary>The dialect's name, for messages.</summary>
    public string Name { get; }

    /// <summary>How the <c>n</c>th bound value is spelled, counted from zero.</summary>
    public Func<int, string> Placeholder { get; }

    /// <summary>
    /// Whether <c>round</c>, <c>trunc</c> and <c>mod</c> need their number cast to <c>numeric</c> first.
    /// </summary>
    public bool NumericForPlaces { get; }

    /// <summary>The operator that divides two whole numbers and truncates.</summary>
    public string IntegerDivision { get; }

    /// <summary>This dialect with another placeholder — for a caller that binds its own way.</summary>
    /// <param name="placeholder">The spelling.</param>
    /// <returns>The dialect.</returns>
    public SqlDialect WithPlaceholder(Func<int, string> placeholder) =>
        new(Name, placeholder ?? throw new ArgumentNullException(nameof(placeholder)), NumericForPlaces, IntegerDivision,
            GuidColumns, ArcGisGuidText, OracleForms, MySqlForms);

    /// <summary>
    /// The layer's columns that hold a UUID — a GlobalID — which every filter language sends as text and neither
    /// database compares with text.
    /// </summary>
    /// <remarks>
    /// <b>Found by the OGC CITE WFS suite, red every day from 2026-09-16:</b> V-17 gave hosted layers a
    /// <c>uuid</c> GlobalID column, the suite filtered on it, and every comparison and <c>like</c> reached PostgreSQL
    /// as <c>uuid = text</c> and was refused — and so was an ArcGIS client's <c>GlobalID='{…}'</c>, which is how Field
    /// Maps and related records look a feature up.
    /// </remarks>
    public IReadOnlySet<string> GuidColumns { get; }

    /// <summary>
    /// Whether a UUID is spelled as ArcGIS spells a GlobalID — <c>{1F0C…}</c>, braced and upper-case — when a
    /// pattern is matched against it; otherwise as the database does, bare and lower-case, which is how WFS and OGC
    /// API Features write it.
    /// </summary>
    public bool ArcGisGuidText { get; }

    /// <summary>This dialect, told which columns hold UUIDs and how the face that asked spells one.</summary>
    /// <param name="columns">The UUID columns.</param>
    /// <param name="arcGisText">True for an ArcGIS face.</param>
    /// <returns>The dialect.</returns>
    public SqlDialect WithGuids(IEnumerable<string> columns, bool arcGisText) =>
        new(Name, Placeholder, NumericForPlaces, IntegerDivision,
            new HashSet<string>(columns ?? throw new ArgumentNullException(nameof(columns)), StringComparer.OrdinalIgnoreCase),
            arcGisText, OracleForms, MySqlForms);

    /// <summary>Whether a column holds UUIDs.</summary>
    internal bool IsGuid(string? column) => column is not null && GuidColumns.Contains(column);

    /// <summary>A UUID column as text, in the spelling of the face that asked.</summary>
    /// <remarks>
    /// Oracle's provider hands the column over already as ArcGIS's text (<c>OracleGuids.Text</c>), since it keeps a
    /// GlobalID as text or as RAW rather than as a UUID type.
    /// </remarks>
    internal string GuidText(string quoted) =>
        OracleForms || MySqlForms
            ? ArcGisGuidText ? quoted : $"lower(substr({quoted}, 2, 36))"
            : ArcGisGuidText ? $"('{{' || upper({quoted}::text) || '}}')" : $"({quoted}::text)";
}
