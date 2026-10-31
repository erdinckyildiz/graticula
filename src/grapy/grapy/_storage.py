"""Storage properties of geodatabases read again against a GeoPackage — ADR-247 §5.1.

ADR-224 refused thirteen ``arcpy.management`` names together as *"a storage property of file or mobile geodatabases
with no counterpart in grapy's GeoPackages"*. Read one by one, four have a counterpart:

- **AddFullTextIndex** — its page names SQLite and mobile geodatabases among its inputs, and SQLite's FTS5 is a
  full-text index. It is kept as an FTS5 *external content* table over the indexed fields, filled with ``rebuild`` and
  kept in step by the three triggers SQLite's FTS5 documentation gives for external content tables (§4.4.3), so every
  edit — grapy's cursors or any SQLite client — reaches it. It is read with SQLite's ``MATCH``, in a where clause:
  ``fid in (select rowid from "grapy_fts_<table>" where "grapy_fts_<table>" match 'term')``.
- **CalculateDefaultClusterTolerance** — the page (ArcMap's; Pro's reference no longer has one, and Pro's arcpy answers
  the name) says the answer is the feature class's XY tolerance, or its topology's cluster tolerance, and for data that
  keeps none the default, *"equivalent to 0.001 meter"*. A GeoPackage keeps no XY tolerance, so the answer is the
  cluster tolerance of a grapy topology the class takes part in (ADR-225), else that default in the data's units —
  ``management._default_tolerance``, the value CreateTopology already uses.
- **MigrateObjectIDTo64Bit** — a GeoPackage feature or attribute table's key is an ``INTEGER PRIMARY KEY``, and SQLite
  keeps every such key as a 64-bit signed integer (*"Rowid Tables"*, sqlite.org). The tool checks what its page says
  it does not take and answers the datasets: their object IDs are already 64-bit.
- **MigrateDateFieldToHighPrecision** — a GeoPackage ``DATETIME`` is ISO 8601 text with fractional seconds (GeoPackage
  1.3 §1.1.1.1.1, *"YYYY-MM-DDTHH:MM:SS.SSSZ"*), and grapy writes Python's ``isoformat`` with them, so a date field
  already keeps milliseconds. The tool checks each field is a date and answers the table.

The other nine stay refused, each by its own reason, in ``REFUSED``.
"""
from __future__ import annotations

import sqlite3

from ._runtime import ExecuteError, NotBuiltError


def _m():
    from . import management
    return management


def _fts_name(table):
    return f"grapy_fts_{table}"


def _has_fts5(connection):
    try:
        connection.execute("create virtual table temp.grapy_fts5_probe using fts5(x)")
        connection.execute("drop table temp.grapy_fts5_probe")
        return True
    except sqlite3.OperationalError:
        return False


def _is_view(connection, table):
    return bool(connection.execute("select 1 from sqlite_master where name = ? and type = 'view'", (table,)).fetchone())


def AddFullTextIndex(in_table, fields, index_name=None, catalog_name=None, sync_on_commit=None):
    """A full-text index on the text fields named, as an SQLite FTS5 external content table (see the module's note).
    As documented for SQLite and mobile geodatabases, the index's name is the database's to give and ``index_name`` is
    ignored; ``catalog_name`` is SQL Server's and ``sync_on_commit`` Oracle's, and both are ignored here — an FTS5
    index is in step with every commit. A view is refused, as documented. INFERRED: the index is named
    ``grapy_fts_<table>``, several fields make one index, and a table takes one full-text index."""
    m = _m()
    del index_name, catalog_name, sync_on_commit
    path = m._plain("AddFullTextIndex", in_table)
    connection, table = m._gpkg.table_of(path)
    try:
        if _is_view(connection, table):
            raise ExecuteError(f"ERROR 000800: '{table}' is a view, and a full-text index is not added to a view.")
        if not _has_fts5(connection):
            raise NotBuiltError("arcpy.management.AddFullTextIndex: this SQLite was built without FTS5, the full-text "
                                "index grapy keeps.")
        declared = {name.lower(): (name, (kind or "").split("(")[0].upper())
                    for _, name, kind, *_ in connection.execute(f'pragma table_info("{table}")')}
        names = []
        for field in m._list(fields):
            found = declared.get(str(field).lower())
            if found is None:
                raise ExecuteError(f"ERROR 000728: the field {field} does not exist in {table}.")
            if found[1] not in ("TEXT", "VARCHAR", "CHAR", "CLOB", ""):
                raise ExecuteError(f"ERROR 000800: the field {found[0]} is not a text field; a full-text index is on "
                                   "text fields.")
            names.append(found[0])
        if not names:
            raise ExecuteError("ERROR 000735: Fields To Index: a value is required.")
        index = _fts_name(table)
        if connection.execute("select 1 from sqlite_master where name = ?", (index,)).fetchone():
            if connection.execute("select 1 from sqlite_master where type = 'trigger' and name = ?",
                                  (f"{index}_ai",)).fetchone():
                raise ExecuteError(f"ERROR 000800: '{table}' has a full-text index already.")
            # The index of a table that was deleted: its triggers went with the table, and the index is stale.
            connection.execute(f'drop table "{index}"')
        key = m._gpkg.oid_column(connection, table)
        quoted = ", ".join(f'"{n}"' for n in names)
        new = ", ".join(f'new."{n}"' for n in names)
        old = ", ".join(f'old."{n}"' for n in names)
        connection.execute(f'create virtual table "{index}" using fts5({quoted}, content="{table}", '
                           f'content_rowid="{key}")')
        connection.execute(f'create trigger "{index}_ai" after insert on "{table}" begin '
                           f'insert into "{index}"(rowid, {quoted}) values (new."{key}", {new}); end')
        connection.execute(f'create trigger "{index}_ad" after delete on "{table}" begin '
                           f'insert into "{index}"("{index}", rowid, {quoted}) values (\'delete\', old."{key}", {old}); '
                           'end')
        connection.execute(f'create trigger "{index}_au" after update on "{table}" begin '
                           f'insert into "{index}"("{index}", rowid, {quoted}) values (\'delete\', old."{key}", {old}); '
                           f'insert into "{index}"(rowid, {quoted}) values (new."{key}", {new}); end')
        connection.execute(f'insert into "{index}"("{index}") values (\'rebuild\')')
        connection.commit()
    finally:
        connection.close()
    return m.Result(m._text(in_table))


def CalculateDefaultClusterTolerance(in_features):
    """The XY tolerance of a feature class, as a number in its units: the cluster tolerance of a topology it takes part
    in, else the default xy tolerance — 0.001 m in the data's units, or its equivalent in degrees on geographic data
    (see the module's note). The data are not changed, as documented. INFERRED: the answer is the Result's output, as a
    string, which is where a script reads it."""
    m = _m()
    path = m._plain("CalculateDefaultClusterTolerance", in_features)
    connection, table = m._gpkg.table_of(path)
    try:
        srid = m._gpkg.describe(connection, table)[2]
        tolerance = None
        if connection.execute("select 1 from sqlite_master where name = 'grapy_topology_class'").fetchone():
            row = connection.execute("select t.cluster_tolerance from grapy_topology t join grapy_topology_class c on "
                                     "c.topology = t.name where c.class_name = ? order by t.name", (table,)).fetchone()
            tolerance = row[0] if row else None
    finally:
        connection.close()
    if tolerance is None:
        tolerance = m._default_tolerance(srid)
    return m.Result(repr(float(tolerance)))


def MigrateObjectIDTo64Bit(in_datasets):
    """The datasets' object IDs as 64-bit integers, which a GeoPackage's already are (see the module's note): each
    dataset is checked — a table, feature class or feature dataset grapy holds, not a layer with a join, as
    documented — and the datasets are answered."""
    m = _m()
    named = m._list(in_datasets)
    if not named:
        raise ExecuteError("ERROR 000735: Input Datasets: a value is required.")
    for value in named:
        if m._joined_layer(value) is not None:
            raise ExecuteError(f"ERROR 000800: '{value}' is a layer with a join, which this tool does not take.")
        file, tables = m._versioned_tables("MigrateObjectIDTo64Bit", value)
        connection = m._gpkg.connect(file)
        try:
            for table in tables:
                if _is_view(connection, table):
                    raise ExecuteError(f"ERROR 000800: '{table}' is a view; its object IDs are the tables' it reads.")
        finally:
            connection.close()
    return m.Result(";".join(m._text(v) for v in named))


def MigrateDateFieldToHighPrecision(in_table, date_fields):
    """The date fields named, as high precision date fields — which a GeoPackage ``DATETIME`` already is (see the
    module's note): each field is checked to be a date with a time, and the table is answered. INFERRED: a column
    declared ``DATE``, GeoPackage's date without a time, is not a date field to migrate."""
    m = _m()
    path = m._plain("MigrateDateFieldToHighPrecision", in_table)
    connection, table = m._gpkg.table_of(path)
    try:
        declared = {name.lower(): (name, (kind or "").split("(")[0].upper())
                    for _, name, kind, *_ in connection.execute(f'pragma table_info("{table}")')}
    finally:
        connection.close()
    named = m._list(date_fields)
    if not named:
        raise ExecuteError("ERROR 000735: Date Fields: a value is required.")
    for field in named:
        found = declared.get(str(field).lower())
        if found is None:
            raise ExecuteError(f"ERROR 000728: the field {field} does not exist in {table}.")
        if found[1] != "DATETIME":
            raise ExecuteError(f"ERROR 000800: the field {found[0]} is not a date field (it is {found[1] or 'untyped'}).")
    return m.Result(m._text(in_table))


REFUSED = {
    "AlterGeometryIndexPreference": "chooses between SAP HANA's two spatial index kinds, rectangle dictionary and "
                                    "R-tree, for a table in SAP HANA, the only input its page names; a fenced tool "
                                    "reaches no SAP HANA database (ADR-223 §5.3), and a GeoPackage has one spatial "
                                    "index kind, the R-tree extension, so there is no preference to alter",
    "CalculateDefaultGridIndex": "answers the three grid sizes of a file geodatabase's spatial grid index, from "
                                 "*\"the spatial reference, average feature size, and number of features\"* by a rule "
                                 "its page does not give; a GeoPackage's spatial index is the R-tree extension, which "
                                 "has no grid sizes, so there is no counterpart to compute even as an equivalent",
    "CompressFileGeodatabaseData": "turns a file geodatabase's feature classes and tables into Esri's compressed, "
                                   "read-only storage, whose layout is not published; a GeoPackage has no compressed "
                                   "read-only form of a table",
    "UncompressFileGeodatabaseData": "turns a file geodatabase's compressed, read-only data back into its read-write "
                                     "form; that compressed storage is Esri's and not published, and a GeoPackage "
                                     "table is never compressed, so there is nothing to uncompress",
    "RecoverFileGDB": "reads a damaged file geodatabase's .gdbtable files, header by header, into a new file "
                      "geodatabase; grapy holds its workspaces as GeoPackages (CreateFileGDB makes one) and reads no "
                      "file geodatabase, whose table format Esri does not publish",
    "GenerateFgdbLicense": "writes a .sdlic licence file for a licensed file geodatabase; the licence file and the "
                           "protection it unlocks are Esri's licensing, which is not published",
    "GenerateLicensedFgdb": "writes a licensed, display-restricted copy of a file geodatabase and its .licdef licence "
                            "definition; the protected format and the licence definition are Esri's licensing, which "
                            "is not published",
    "UpgradeDataset": "moves a dataset's schema from an earlier ArcGIS release to the current one; grapy keeps the "
                      "datasets it holds (mosaic and network datasets, utility networks, multipatch classes) in its "
                      "own tables, which have one form and no ArcGIS release, so there is no earlier schema to "
                      "upgrade from",
    "MigrateTextField": "turns Unicode text fields (NVARCHAR2, NCLOB, NCHAR) of a table in an Oracle enterprise "
                        "geodatabase into non-Unicode ones, the only input its page names; a fenced tool reaches no "
                        "enterprise geodatabase (ADR-223 §5.3), and SQLite's TEXT has no Unicode and non-Unicode kinds",
}
