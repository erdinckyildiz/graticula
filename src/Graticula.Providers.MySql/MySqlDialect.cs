using System;
using System.Globalization;
using MySqlConnector;

namespace Graticula.Providers.MySql;

/// <summary>
/// Which server a connection reached — MySQL or MariaDB — and the SQL that differs between them (ADR-209).
/// </summary>
/// <remarks>
/// <para>
/// <b>One provider, two dialects, and every difference in this one class.</b> MariaDB speaks MySQL's protocol and most
/// of its SQL; what it does not share was measured on 11.8 before anything was written (ADR-209 §1a), and each item is
/// a member here, so the reader, the writer, the history and the checks ask rather than branch.
/// </para>
/// <para>
/// <b>Told apart by what the server says it is</b>: MariaDB names itself in its version string, which MySqlConnector
/// reports once the connection is open — no extra round trip.
/// </para>
/// </remarks>
internal sealed class MySqlDialect
{
    private MySqlDialect(bool mariaDb) => IsMariaDb = mariaDb;

    /// <summary>MySQL 8.0 or later.</summary>
    public static MySqlDialect MySql { get; } = new(false);

    /// <summary>MariaDB 10.6 or later.</summary>
    public static MySqlDialect MariaDb { get; } = new(true);

    /// <summary>Whether the server is MariaDB.</summary>
    public bool IsMariaDb { get; }

    /// <summary>The server's name, for a sentence.</summary>
    public string Name => IsMariaDb ? "MariaDB" : "MySQL";

    /// <summary>The dialect of an open connection.</summary>
    public static MySqlDialect Of(MySqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.ServerVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) ? MariaDb : MySql;
    }

    /// <summary>
    /// A shape as WKB, x then y: MySQL is asked for longitude first, since it stores a geographic shape latitude first;
    /// MariaDB stores it as written and has no such option (a third argument is error 1582).
    /// </summary>
    public string Wkb(string shape) => IsMariaDb ? $"st_asbinary({shape})" : $"st_asbinary({shape}, 'axis-order=long-lat')";

    /// <summary>A shape from WKB in x-then-y order, in a reference.</summary>
    public string FromWkb(string parameter, int srid) => IsMariaDb
        ? $"st_geomfromwkb({parameter}, {Srid(srid)})"
        : $"st_geomfromwkb({parameter}, {Srid(srid)}, 'axis-order=long-lat')";

    /// <summary>A shape from WKT in x-then-y order, in a reference.</summary>
    public string FromText(string parameter, int srid) => IsMariaDb
        ? $"st_geomfromtext({parameter}, {Srid(srid)})"
        : $"st_geomfromtext({parameter}, {Srid(srid)}, 'axis-order=long-lat')";

    /// <summary>
    /// Every spatial column as <c>(table_schema, table_name, column_name, srs_id, geometry_type_name)</c>: MySQL's
    /// <c>ST_GEOMETRY_COLUMNS</c>, or MariaDB's <c>GEOMETRY_COLUMNS</c> with its numeric type named and an undeclared
    /// SRID (0) as null, as MySQL reports one.
    /// </summary>
    public string GeometryColumns => IsMariaDb
        ? """
          (select g.g_table_schema as table_schema, g.g_table_name as table_name, g.g_geometry_column as column_name,
                  nullif(g.srid, 0) as srs_id,
                  case g.geometry_type when 1 then 'point' when 2 then 'linestring' when 3 then 'polygon'
                       when 4 then 'multipoint' when 5 then 'multilinestring' when 6 then 'multipolygon'
                       when 7 then 'geomcollection' else 'geometry' end as geometry_type_name
             from information_schema.geometry_columns g)
          """
        : "(select table_schema, table_name, column_name, srs_id, geometry_type_name from information_schema.st_geometry_columns)";

    /// <summary>
    /// The extent of a table's shapes as one envelope's WKB. MySQL computes none under a geographic reference, so the
    /// shapes are collected with their SRID set to 0; MariaDB has neither <c>ST_Collect</c> nor an SRID setter, and
    /// computes on the plane — so the box is taken from each envelope's first and third vertices.
    /// </summary>
    public string Extent(string shape, string from) => IsMariaDb
        ? $"""
           select st_asbinary(st_envelope(st_geomfromtext(concat('LINESTRING(',
                    min(st_x(st_pointn(st_exteriorring(st_envelope({shape})), 1))), ' ',
                    min(st_y(st_pointn(st_exteriorring(st_envelope({shape})), 1))), ', ',
                    max(st_x(st_pointn(st_exteriorring(st_envelope({shape})), 3))), ' ',
                    max(st_y(st_pointn(st_exteriorring(st_envelope({shape})), 3))), ')')))) from {from}
           """
        : $"select st_asbinary(st_envelope(st_srid(st_collect({shape}), 0))) from {from}";

    /// <summary>A geometry column's definition with its reference: <c>SRID n</c> after, or <c>REF_SYSTEM_ID=n</c> before, <c>NOT NULL</c>.</summary>
    public string GeometryColumn(string type, int srid) => IsMariaDb
        ? $"{type} ref_system_id={Srid(srid)} not null"
        : $"{type} not null srid {Srid(srid)}";

    /// <summary>
    /// A version's recorded value read back as a type: MySQL's <c>JSON_VALUE … RETURNING</c>, or — MariaDB has no
    /// <c>RETURNING</c> clause — the text <c>JSON_VALUE</c> gives, cast.
    /// </summary>
    public string JsonAs(string json, string path, string type) => IsMariaDb
        ? $"cast(json_value({json}, {path}) as {Cast(type)})"
        : $"json_value({json}, {path} returning {type})";

    /// <summary>Whether the server can say whether a shape is valid; MariaDB has no <c>ST_IsValid</c>.</summary>
    public bool Validates => !IsMariaDb;

    /// <summary>Whether the server enforces a column's declared SRID; MariaDB stores whatever it is given.</summary>
    public bool EnforcesSrid => !IsMariaDb;

    private static string Srid(int srid) => srid.ToString(CultureInfo.InvariantCulture);

    /// <summary>MariaDB's <c>CAST</c> names: <c>double</c>, not <c>double precision</c>, and no <c>year</c>.</summary>
    private static string Cast(string type) => type switch
    {
        "year" => "signed",
        _ => type,
    };
}
