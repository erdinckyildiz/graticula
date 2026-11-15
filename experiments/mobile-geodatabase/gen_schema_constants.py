"""Disposable (ADR-288): writes src/Graticula.Offline.Sqlite/MobileGeodatabaseSchema.cs, the format constants the
production writer carries, from a captured registered replica and a captured download delta.

What it takes is format, not data: the system tables' CREATE statements and their indexes, the GDB_Items spatial-index
triggers, the two Esri-module virtual table statements, the GDB_ItemTypes and GDB_ItemRelationshipTypes rows, the
system rows of GDB_ColumnRegistry, and the five delta tables' CREATE statements (from the delta, which is the only
file that has them). No row of anybody's data is read. Research note §0 is the disclosure.

Usage: gen_schema_constants.py <replica.geodatabase> <delta.geodatabase> <out.cs>
"""
import sqlite3, sys

SYSTEM = ["GDB_Version", "st_aux_spatial_reference_systems", "st_geometry_columns", "GDB_Layers", "GDB_Metadata",
          "GDB_TableRegistry", "GDB_Locators", "GDB_ColumnRegistry", "GDB_TablesModified", "GDB_RowidGenerators",
          "GDB_Tables_Last_Modified", "GDB_Items", "GDB_ItemRelationships", "GDB_ItemTypes", "GDB_ItemRelationshipTypes",
          "GDB_ReplicaLog", "GDB_EditingTemplates", "GDB_EditingTemplateRelationships", "GDB_TriggerWork", "GDB_ServiceItems"]
DELTA = ["GDB_DataChanges", "GDB_DataChangesDatasets", "GDB_DataChangesDeltas", "GDB_DataChangesTopologies", "GDB_RelatedDatasets"]
TRIGGERS = ["st_insert_trigger_GDB_Items_Shape", "st_delete_trigger_GDB_Items_Shape", "st_update_trigger_GDB_Items_Shape",
            "st_update1_trigger_GDB_Items_Shape"]
MODULES = ["st_spatial_reference_systems", "st_vtspindex_interface"]


def open_ro(path):
    c = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
    c.create_collation("ICUFOLDCASE", lambda a, b: (a.casefold() > b.casefold()) - (a.casefold() < b.casefold()))
    return c


def s(v):
    if v is None:
        return "null"
    if isinstance(v, int):
        return str(v)
    return '"' + str(v).replace("\\", "\\\\").replace('"', '\\"') + '"'


def v(text):
    return '@"' + text.replace('"', '""') + '"'


def main():
    k, d, out = open_ro(sys.argv[1]), open_ro(sys.argv[2]), sys.argv[3]
    sql = {n: q for n, q in k.execute("select name, sql from sqlite_master where sql is not null")}
    dsql = {n: q for n, q in d.execute("select name, sql from sqlite_master where sql is not null")}
    L = []
    w = L.append
    w("// Written by experiments/mobile-geodatabase/gen_schema_constants.py. Regenerate rather than edit.")
    w("//")
    w("// FORMAT CONSTANTS, OBSERVED — ADR-288 and docs/research/mobile-geodatabase-format.md §0.")
    w("// Esri does not publish the mobile geodatabase. These statements and rows are the format's fixed part as read")
    w("// from a registered replica and a download delta ArcGIS Server 12.1 wrote for a service this project created and")
    w("// filled (owner's permission, 2026-11-10). They are schema and type catalogue, not data: no row of any service is")
    w("// here, and the captured files stay outside the repository.")
    w("")
    w("namespace Graticula.Offline.Sqlite;")
    w("")
    w("/// <summary>The fixed part of a mobile geodatabase: system tables, item types, system column registry.</summary>")
    w("internal static class MobileGeodatabaseSchema")
    w("{")
    w("    /// <summary>The system tables, in the order a file creates them.</summary>")
    w("    public static readonly (string Name, string Sql)[] SystemTables =")
    w("    [")
    for n in SYSTEM:
        w(f"        ({s(n)}, {v(sql[n])}),")
    w("    ];")
    w("")
    w("    /// <summary>The indexes on system tables, each with the table it is on.</summary>")
    w("    public static readonly (string Table, string Sql)[] SystemIndexes =")
    w("    [")
    for n, t, q in k.execute("select name, tbl_name, sql from sqlite_master where type='index' and sql is not null order by rootpage"):
        if t in SYSTEM:
            w(f"        ({s(t)}, {v(q)}),")
    w("    ];")
    w("")
    w("    /// <summary>The R*Tree index of <c>GDB_Items.Shape</c>.</summary>")
    w("    public const string ItemsSpatialIndex = " + v(sql["st_spindex__GDB_Items_Shape"]) + ";")
    w("")
    w("    /// <summary>The triggers that keep <c>GDB_Items</c>' spatial index; created last, as they call Esri functions.</summary>")
    w("    public static readonly string[] ItemsSpatialIndexTriggers =")
    w("    [")
    for n in TRIGGERS:
        w(f"        {v(sql[n])},")
    w("    ];")
    w("")
    w("    /// <summary>")
    w("    /// The two virtual tables whose modules only Esri's library has. Plain SQLite cannot execute these; the writer")
    w("    /// inserts them as rows of <c>sqlite_master</c>, as research note §1 describes.")
    w("    /// </summary>")
    w("    public static readonly (string Name, string Sql)[] EsriModuleTables =")
    w("    [")
    for n in MODULES:
        w(f"        ({s(n)}, {v(sql[n])}),")
    w("    ];")
    w("")
    w("    /// <summary>The tables a delta file adds to the system set (research note §5).</summary>")
    w("    public static readonly (string Name, string Sql)[] DeltaTables =")
    w("    [")
    for n in DELTA:
        w(f"        ({s(n)}, {v(dsql[n])}),")
    w("    ];")
    w("")
    w("    /// <summary>The rows of <c>GDB_ItemTypes</c>: object id, UUID, name, parent type UUID.</summary>")
    w("    public static readonly (int ObjectId, string Uuid, string Name, string ParentTypeId)[] ItemTypes =")
    w("    [")
    for r in k.execute("select ObjectID, UUID, Name, ParentTypeID from GDB_ItemTypes order by ObjectID"):
        w("        (" + ", ".join(s(x) for x in r) + "),")
    w("    ];")
    w("")
    w("    /// <summary>The rows of <c>GDB_ItemRelationshipTypes</c>, every column in table order.</summary>")
    w("    public static readonly (int ObjectId, string Uuid, string Name, string? ForwardLabel, string? BackwardLabel, string OrigItemTypeId, string DestItemTypeId, int? IsContainment)[] ItemRelationshipTypes =")
    w("    [")
    for r in k.execute("select ObjectID, UUID, Name, ForwardLabel, BackwardLabel, OrigItemTypeID, DestItemTypeID, IsContainment from GDB_ItemRelationshipTypes order by ObjectID"):
        w("        (" + ", ".join(s(x) for x in r) + "),")
    w("    ];")
    w("")
    w("    /// <summary>The rows of <c>GDB_ColumnRegistry</c> for the system tables, every column in table order.</summary>")
    w("    public static readonly (string Table, string Column, int SdeType, int? Size, int? DecimalDigits, string? Description, int Flags, int? Flags2, int? ObjectId, string? Comment)[] SystemColumnRegistry =")
    w("    [")
    for r in k.execute("select * from GDB_ColumnRegistry where table_name like 'GDB\\_%' escape '\\' order by rowid"):
        w("        (" + ", ".join(s(x) for x in r) + "),")
    w("    ];")
    w("}")
    open(out, "w", encoding="utf-8", newline="\n").write("\n".join(L) + "\n")
    print("wrote", out)


main()
