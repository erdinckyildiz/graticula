using System;
using System.Collections.Generic;
using System.Linq;
using Graticula.Api.ArcGis.Offline;

namespace Graticula.Offline.Sqlite;

/// <summary>
/// The statements that make one table of a mobile geodatabase: its columns with the type checks the file declares, the
/// archive columns, views and triggers of a registered replica, and the R*Tree spatial index — ADR-288, research note
/// §1 and §4.
/// </summary>
/// <remarks>
/// Written from what the server's files hold, column for column; <c>experiments/mobile-geodatabase/writer_proto.py</c>
/// is the specification ArcGIS Pro was measured against.
/// </remarks>
internal static class TableStatements
{
    /// <summary>The ArcGIS field types a table may hold, and the geodatabase's <c>sde_type</c> for each.</summary>
    public static int SdeType(string esriType) => esriType switch
    {
        "esriFieldTypeSmallInteger" => 1,
        "esriFieldTypeInteger" or "esriFieldTypeOID" => 2,
        "esriFieldTypeSingle" => 3,
        "esriFieldTypeDouble" => 4,
        "esriFieldTypeString" => 5,
        "esriFieldTypeBlob" => 6,
        "esriFieldTypeDate" => 7,
        "esriFieldTypeGUID" or "esriFieldTypeGlobalID" => 12,
        _ => throw Unsupported(esriType),
    };

    /// <summary>One column's definition.</summary>
    /// <param name="field">The field.</param>
    /// <param name="archived">
    /// Whether the table is archived; its object id is then an ordinary column and <c>gdb_archive_oid</c> is the key.
    /// </param>
    public static string Column(OfflineField field, bool archived)
    {
        string n = FileValues.ColumnName(field.Name);
        bool nullable = field.Nullable;

        string Typed(string declared, string type, string range) => nullable
            ? $"{n} {declared} check((typeof({n}) = '{type}' or typeof({n}) = 'null'){range})"
            : $"{n} {declared} check(typeof({n}) = '{type}'{range}) not null";

        switch (field.EsriType)
        {
            case "esriFieldTypeOID":
                return archived
                    ? $"{n} int32 check(typeof({n}) = 'integer' and {n} >= -2147483648 and {n} <= 2147483647) not null"
                    : $"{n} integer primary key not null";
            case "esriFieldTypeGlobalID":
                return $"{n} uuidtext collate nocase check(typeof({n}) = 'text' and length({n}) = 38) not null";
            case "esriFieldTypeGUID":
                return nullable
                    ? $"{n} uuidtext collate nocase check((typeof({n}) = 'text' or typeof({n}) = 'null') and length({n}) = 38)"
                    : $"{n} uuidtext collate nocase check(typeof({n}) = 'text' and length({n}) = 38) not null";
            case "esriFieldTypeString":
                int length = StringLength(field);
                return Typed($"text({length})", "text", $" and not length({n}) > {length}");
            case "esriFieldTypeSmallInteger":
                return Typed("int16", "integer", $" and {n} >= -32768 and {n} <= 32767");
            case "esriFieldTypeInteger":
                return Typed("int32", "integer", $" and {n} >= -2147483648 and {n} <= 2147483647");
            case "esriFieldTypeDouble":
            case "esriFieldTypeSingle":
                string real = field.EsriType == "esriFieldTypeDouble" ? "float64" : "float32";
                return nullable
                    ? $"{n} {real} check(typeof({n}) = 'real' or typeof({n}) = 'null')"
                    : $"{n} {real} check(typeof({n}) = 'real') not null";
            case "esriFieldTypeDate":
                return Typed("realdate", "real", $" and {n} >= 0.0");
            case "esriFieldTypeBlob":
                return $"{n} blob check(typeof({n}) = 'blob' or typeof({n}) = 'null')";
            default:
                throw Unsupported(field.EsriType);
        }
    }

    /// <summary>The geometry column's definition: a blob that begins with the format's magic byte, or null.</summary>
    public static string Shape(string column) =>
        $"{column} geometryblob check((typeof({column}) = 'blob' and length({column}) >= 18 and cast(hex(substr({column},1,1)) as integer) = 64) or typeof({column}) = 'null')";

    /// <summary>The archive columns a registered replica's tables end with (research note §4).</summary>
    public const string ArchiveColumns =
        ", gdb_archive_oid integer primary key not null, gdb_from_date realdate check(typeof(gdb_from_date) = 'real' and gdb_from_date >= 0.0) not null default (gdb_transaction_time ()), "
        + "gdb_to_date realdate check(typeof(gdb_to_date) = 'real' and gdb_to_date >= 0.0) not null default (" + FileValues.OpenEndSql + ")";

    /// <summary>A string field's declared length, 255 when the service gives none.</summary>
    public static int StringLength(OfflineField field) => field.Length is > 0 ? field.Length.Value : 255;

    /// <summary>
    /// The views a registered table is edited through, and the <c>INSTEAD OF</c> triggers that turn an edit into
    /// archive rows: an insert opens a row, a delete closes it, an update closes it and opens the new version.
    /// </summary>
    /// <param name="table">The table.</param>
    /// <param name="objectId">Its object id column.</param>
    /// <param name="columns">Its columns, object id first, archive columns excluded.</param>
    public static IEnumerable<string> Archive(string table, string objectId, IReadOnlyList<string> columns)
    {
        string c = string.Join(",", columns);
        string newColumns = string.Join(",", columns.Select(static x => "NEW." + x));
        string far = FileValues.OpenEndSql;
        string current = $"gdb_to_date BETWEEN ({far} - 0.000000001) AND ({far} + 0.000000001)";
        string[] views = ["_evw", "_evw2"];

        yield return $"CREATE VIEW {table}_evw AS SELECT {c} FROM {table} WHERE {current}";
        yield return $"CREATE VIEW {table}_evw2 AS SELECT {c} FROM {table} WHERE gdb_from_date <=  (gdb_undo_start_moment()) AND gdb_to_date > (gdb_undo_start_moment())";

        string insertValues = string.Join(
            ",",
            new[] { $"IFNULL (NEW.{objectId},Next_RowID (NULL,'{table}'))" }.Concat(columns.Skip(1).Select(static x => "NEW." + x)));
        foreach (string v in views)
        {
            yield return $"CREATE TRIGGER {table}{v}_insert INSTEAD OF INSERT ON {table}{v} BEGIN INSERT INTO {table} ({c},gdb_from_date,gdb_to_date) VALUES ({insertValues},gdb_transaction_time (),{far}); END";
        }

        foreach (string v in views)
        {
            yield return $"CREATE TRIGGER {table}{v}_delete INSTEAD OF DELETE ON {table}{v} BEGIN DELETE FROM {table} WHERE {objectId} = OLD.{objectId} AND gdb_from_date BETWEEN (gdb_transaction_time () - 0.000000001) AND (gdb_transaction_time () + 0.000000001); DELETE FROM {table} WHERE {objectId} = OLD.{objectId} AND gdb_to_date BETWEEN (gdb_transaction_time () - 0.000000001) AND (gdb_transaction_time () + 0.000000001); UPDATE {table} SET gdb_to_date = gdb_transaction_time () WHERE {objectId} = OLD.{objectId} AND {current}; END";
        }

        foreach (string v in views)
        {
            yield return $"CREATE TRIGGER {table}{v}_update INSTEAD OF UPDATE ON {table}{v} BEGIN UPDATE OR IGNORE {table} SET gdb_to_date = gdb_transaction_time () WHERE {objectId} = OLD.{objectId} AND {current};DELETE FROM {table} WHERE {objectId} = OLD.{objectId} AND {current};DELETE FROM GDB_TriggerWork;INSERT INTO GDB_TriggerWork SELECT IFNULL ((SELECT MAX (gdb_to_date) FROM {table} WHERE {objectId} = OLD.{objectId} AND gdb_to_date < {far}),gdb_transaction_time ());DELETE FROM {table} WHERE {objectId} = OLD.{objectId} AND gdb_from_date = (SELECT gdb_to_date FROM GDB_TriggerWork);INSERT INTO {table} ({c},gdb_from_date,gdb_to_date) VALUES ({newColumns},(SELECT gdb_to_date FROM GDB_TriggerWork),{far}); END";
        }
    }

    /// <summary>The name of a table's R*Tree spatial index.</summary>
    public static string SpatialIndexName(string table, string column) => $"st_spindex__{table}_{column}";

    /// <summary>The R*Tree that indexes a table's geometry column.</summary>
    public static string SpatialIndex(string table, string column) =>
        $"CREATE VIRTUAL TABLE [{SpatialIndexName(table, column)}] USING RTREE (pkid,minx,maxx,miny,maxy)";

    /// <summary>
    /// The triggers that keep the spatial index on the device. They call Esri's functions, which only the device has, so
    /// a writer loads rows and fills the index itself first and creates these last (research note §1).
    /// </summary>
    public static IEnumerable<string> SpatialIndexTriggers(string table, string column)
    {
        string index = SpatialIndexName(table, column);
        yield return $"CREATE TRIGGER [st_insert_trigger_{table}_{column}] AFTER INSERT ON [{table}] FOR EACH ROW BEGIN SELECT InsertIndexEntry ('{index}',NEW.{column},NEW._ROWID_,2); END";
        yield return $"CREATE TRIGGER [st_delete_trigger_{table}_{column}] AFTER DELETE ON [{table}] FOR EACH ROW BEGIN DELETE FROM {index} WHERE pkid = OLD._ROWID_; END";
        yield return $"CREATE TRIGGER [st_update_trigger_{table}_{column}] AFTER UPDATE OF {column} ON [{table}] WHEN OLD._ROWID_ = NEW._ROWID_ BEGIN SELECT UpdateIndexEntry ('{index}',NEW.{column},NEW._ROWID_,2); END";
        yield return $"CREATE TRIGGER [st_update1_trigger_{table}_{column}] AFTER UPDATE ON [{table}] WHEN OLD._ROWID_ != NEW._ROWID_ BEGIN DELETE FROM [{index}] WHERE pkid = OLD._ROWID_; SELECT UpdateIndexEntry ('{index}',NEW.{column},NEW._ROWID_,2); END";
    }

    /// <summary>
    /// The value of <c>GDB_Layers.eflags</c> for a geometry type: the low byte by shape, with bits for Z, M and more than
    /// one point.
    /// </summary>
    public static int LayerFlags(string geometryType, bool hasZ, bool hasM)
    {
        int low = geometryType switch
        {
            "esriGeometryPoint" or "esriGeometryMultipoint" => 0x83,
            "esriGeometryPolyline" => 0x8D,
            "esriGeometryPolygon" => 0x91,
            _ => throw Unsupported(geometryType),
        };
        return 0x4400000 | (geometryType == "esriGeometryPoint" ? 0 : 0x40000) | (hasZ ? 0x10000 : 0) | (hasM ? 0x80000 : 0) | low;
    }

    /// <summary>The value of <c>st_geometry_columns.geometry_type</c> before Z and M are added.</summary>
    public static int ColumnType(string geometryType) => geometryType switch
    {
        "esriGeometryPoint" => 1,
        "esriGeometryMultipoint" => 4,
        "esriGeometryPolyline" => 5,
        "esriGeometryPolygon" => 6,
        _ => throw Unsupported(geometryType),
    };

    /// <summary>The value of <c>DatasetSubtype2</c> on a feature class item.</summary>
    public static int ItemShapeType(string geometryType) => geometryType switch
    {
        "esriGeometryPoint" => 1,
        "esriGeometryMultipoint" => 2,
        "esriGeometryPolyline" => 3,
        "esriGeometryPolygon" => 4,
        _ => throw Unsupported(geometryType),
    };

    private static NotSupportedException Unsupported(string what) =>
        new($"'{what}' cannot be written to an offline copy; ADR-288 covers the field and geometry types research note §3 observed.");
}
