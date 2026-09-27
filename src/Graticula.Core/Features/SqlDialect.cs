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
        bool arcGisGuidText = false)
    {
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
            GuidColumns, ArcGisGuidText);

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
            arcGisText);

    /// <summary>Whether a column holds UUIDs.</summary>
    internal bool IsGuid(string? column) => column is not null && GuidColumns.Contains(column);

    /// <summary>A UUID column as text, in the spelling of the face that asked.</summary>
    internal string GuidText(string quoted) =>
        ArcGisGuidText ? $"('{{' || upper({quoted}::text) || '}}')" : $"({quoted}::text)";
}
