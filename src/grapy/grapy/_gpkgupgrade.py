"""A GeoPackage an earlier grapy wrote, found and brought to the form grapy writes now — D-340.

Until 2026-10-31 grapy wrote GeoPackages that ArcGIS Pro opened wrongly: a feature class's geometry column was declared
``BLOB``, where GeoPackage 1.4 Requirement 31 (§2.1.6.1.1) asks for the geometry type name, so Pro read every class as
a table with a ``geom`` blob field; a table was a ``GEOMETRY`` feature table with an empty ``geom`` column, where §2.4
makes it an ``attributes`` table; and the core tables lacked Annex C's constraints. grapy has written the standard form
since then (``_gpkg``), but a file written before keeps the old one.

``findings`` lists what in a file is in the old form. ``upgrade`` rebuilds each such table in the standard form, rows
kept, by SQLite's documented procedure for a change ALTER TABLE cannot make (create the new table, copy, drop, rename;
*sqlite.org/lang_altertable.html*, "Making Other Kinds Of Table Schema Changes"), inside one transaction, after a copy
of the file is kept beside it. ``management.UpgradeGDB`` runs it under arcpy's name, and a grapy tool that writes into
such a file says once that it can (``offer``).
"""
from __future__ import annotations

import os
import re
import shutil
import sqlite3

from . import _gpkg

_SAID: set = set()


def _quote(name):
    return '"' + str(name).replace('"', '""') + '"'


def _declared(connection, table):
    return {r[1].lower(): (r[2] or "").upper() for r in connection.execute(f"pragma table_info({_quote(table)})")}


def findings(connection):
    """[(kind, table, detail)] of what a GeoPackage holds in grapy's pre-2026-10-31 form:
    ``geometry type`` — a feature table whose geometry column is declared other than its geometry type name;
    ``attributes`` — a GEOMETRY feature table with no reference, no z or m, and no shape in any row, which grapy writes
    now as an attributes table; ``core table`` — gpkg_contents or gpkg_geometry_columns without Annex C's constraints;
    ``definition`` — a spatial reference whose ``definition`` holds WKT 2; ``version`` — a ``user_version`` of 0."""
    found = []
    names = {r[0] for r in connection.execute("select name from sqlite_master where type = 'table'")}
    if not {"gpkg_contents", "gpkg_geometry_columns"} <= names:
        return found
    if connection.execute("pragma user_version").fetchone()[0] == 0:
        found.append(("version", None, "user_version is 0; GeoPackage 1.4 asks for 10400"))
    for table, column, kind, srid, z, m in connection.execute(
            "select table_name, column_name, geometry_type_name, srs_id, z, m from gpkg_geometry_columns").fetchall():
        if table not in names:
            continue
        declared = _declared(connection, table).get(str(column).lower(), "")
        if declared == str(kind).upper():
            continue
        empty = connection.execute(f"select not exists (select 1 from {_quote(table)} where {_quote(column)} is not "
                                   "null)").fetchone()[0] == 1
        if str(kind).upper() == "GEOMETRY" and not srid and not z and not m and empty and declared in ("BLOB", ""):
            found.append(("attributes", table, f"a table written as a GEOMETRY feature table with an empty "
                                               f"'{column}' column"))
        else:
            found.append(("geometry type", table, f"'{column}' is declared {declared or 'without a type'}, not "
                                                  f"{str(kind).upper()}"))
    for table, marker in (("gpkg_contents", "fk_gc_r_srs_id"), ("gpkg_geometry_columns", "uk_gc_table_name")):
        sql = connection.execute("select sql from sqlite_master where name = ?", (table,)).fetchone()
        if sql and marker not in (sql[0] or "").lower():
            found.append(("core table", table, "without GeoPackage 1.4 Annex C's constraints"))
    if "gpkg_spatial_ref_sys" in names:
        for code, definition in connection.execute(
                "select srs_id, definition from gpkg_spatial_ref_sys where upper(organization) = 'EPSG'").fetchall():
            if str(definition or "").lstrip().upper().startswith(_gpkg._WKT2):
                found.append(("definition", f"EPSG:{code}", "WKT 2 in definition, where §1.1.2.1.1 asks for WKT 1"))
    return found


def offer(file, connection):
    """Says once per file and process that a file is in grapy's old form and that UpgradeGDB brings it up to date."""
    from ._runtime import AddWarning
    key = os.path.normcase(os.path.abspath(file))
    if key in _SAID:
        return
    _SAID.add(key)
    kinds = {k for k, _, _ in findings(connection) if k in ("geometry type", "attributes", "core table")}
    if kinds:
        AddWarning(f"{file} was written by grapy before 2026-10-31 and ArcGIS Pro reads some of its classes as tables "
                   f"({', '.join(sorted(kinds))}); arcpy.management.UpgradeGDB rebuilds them in the standard form, "
                   "keeping a copy of the file first (D-340).")


def _rebuild(connection, table, sql):
    """``table`` recreated from ``sql`` (its CREATE TABLE with the new definition, naming ``table``), rows copied by
    the columns both share, its indexes and triggers made again."""
    keep = [r[0] for r in connection.execute("select sql from sqlite_master where tbl_name = ? and type in "
                                             "('index', 'trigger') and sql is not null", (table,))]
    old = list(_declared(connection, table))
    temporary = f"__grapy_upgrade_{abs(hash(table)) % 10 ** 8}"
    connection.execute(re.sub(r'(?is)^\s*create\s+table\s+(if\s+not\s+exists\s+)?("[^"]*"|\S+)',
                              f"create table {_quote(temporary)}", sql, count=1))
    new = list(_declared(connection, temporary))
    shared = [c for c in new if c in old]
    columns = ", ".join(_quote(c) for c in shared)
    connection.execute(f"insert into {_quote(temporary)} ({columns}) select {columns} from {_quote(table)}")
    connection.execute(f"drop table {_quote(table)}")
    connection.execute(f"alter table {_quote(temporary)} rename to {_quote(table)}")
    for statement in keep:
        connection.execute(statement)


def _table_sql(connection, table):
    return connection.execute("select sql from sqlite_master where type = 'table' and name = ?", (table,)).fetchone()[0]


def upgrade(file, backup=True):
    """The file brought to the form grapy writes now; answers the findings it repaired. A copy of the file is kept
    beside it first as ``<file>.before-upgrade`` (INFERRED, D-340: the rebuild cannot be undone otherwise, and the
    upgrade page asks for a backup before an enterprise upgrade)."""
    connection = _gpkg.connect(file)
    try:
        found = findings(connection)
    finally:
        connection.close()
    if not found:
        return []
    if backup:
        target = file + ".before-upgrade"
        n = 1
        while os.path.exists(target):
            n += 1
            target = f"{file}.before-upgrade-{n}"
        shutil.copy2(file, target)
    connection = sqlite3.connect(file, isolation_level=None)
    try:
        _gpkg.functions(connection)
        connection.execute("pragma foreign_keys = off")
        connection.execute("begin")
        for kind, table, _ in found:
            if kind == "geometry type":
                column, name = connection.execute("select column_name, geometry_type_name from gpkg_geometry_columns "
                                                  "where table_name = ?", (table,)).fetchone()
                # The column's type in the column list, after the table name's opening parenthesis.
                head, _, body = _table_sql(connection, table).partition("(")
                body = re.sub(rf'(?i)(^|,)(\s*)("{re.escape(column)}"|{re.escape(column)})\s+[A-Za-z_]*',
                              lambda m: f"{m.group(1)}{m.group(2)}{m.group(3)} {str(name).upper()}", body, count=1)
                sql = head + "(" + body
                _rebuild(connection, table, sql)
            elif kind == "attributes":
                column, = connection.execute("select column_name from gpkg_geometry_columns where table_name = ?",
                                             (table,)).fetchone()
                fields = [r for r in connection.execute(f"pragma table_info({_quote(table)})") if r[1] != column]
                parts = []
                for _, name, declared, notnull, default, pk in fields:
                    if pk:
                        parts.append(f"{_quote(name)} integer primary key autoincrement not null")
                        continue
                    parts.append(f"{_quote(name)} {declared or ''}{' not null' if notnull else ''}"
                                 f"{' default ' + default if default is not None else ''}")
                _rebuild(connection, table, f"create table {_quote(table)} ({', '.join(parts)})")
                connection.execute("delete from gpkg_geometry_columns where table_name = ?", (table,))
                connection.execute("update gpkg_contents set data_type = 'attributes', srs_id = null, min_x = null, "
                                   "min_y = null, max_x = null, max_y = null where table_name = ?", (table,))
        for kind, table, _ in found:
            if kind == "core table":
                statement = next(s for s in _gpkg._CORE_TABLES if f"exists {table} " in s)
                _rebuild(connection, table, statement)
        _gpkg._ensure_metadata(connection, 0)   # the version, Requirement 11's rows and WKT 1 definitions
        connection.execute("commit")
        problems = connection.execute("pragma foreign_key_check").fetchall()
    except Exception:
        if connection.in_transaction:
            connection.execute("rollback")
        raise
    finally:
        connection.close()
    if problems:
        from ._runtime import AddWarning
        AddWarning(f"{file}: {len(problems)} rows refer to a spatial reference or table the file does not hold; the "
                   "upgrade kept them as they were.")
    return found


def UpgradeGDB(input_workspace, input_prerequisite_check="PREREQUISITE_CHECK", input_upgradegdb_check="UPGRADE"):
    """A GeoPackage an earlier grapy wrote brought to the form grapy writes now, so that ArcGIS Pro reads its feature
    classes as feature classes (D-340): each class's geometry column declared with its geometry type name, each table
    an attributes table, the core tables with GeoPackage 1.4 Annex C's constraints, WKT 1 definitions and the version
    10400 — rows kept, a copy of the file kept first as ``<file>.before-upgrade``. The page's two switches, at least one
    of which must be on: PREREQUISITE_CHECK verifies the file can be written and lists in the messages what is in the
    old form; UPGRADE repairs it. A file already in the current form is left as it is, which the messages say.

    INFERRED (D-340): a GeoPackage's "release" is the form grapy writes it in; the prerequisite check is that the file
    exists, is a GeoPackage and is not read-only, the file-geodatabase checks of the page that a file has (no other
    users, no edit session and the system tables opening have no counterpart beyond SQLite opening it)."""
    from ._result import Result
    from ._runtime import AddMessage, ExecuteError
    check = str(input_prerequisite_check or "PREREQUISITE_CHECK").upper() in ("PREREQUISITE_CHECK", "TRUE")
    act = str(input_upgradegdb_check or "UPGRADE").upper() in ("UPGRADE", "TRUE")
    if not check and not act:
        raise ExecuteError("ERROR 000800: enable the prerequisite check, the upgrade, or both, as UpgradeGDB's page "
                           "requires.")
    path = os.fspath(input_workspace)
    file = path if path.lower().endswith(".gpkg") else _gpkg.split(path)[0]
    if not os.path.isfile(file):
        raise ExecuteError(f"ERROR 000732: Input Geodatabase: {input_workspace} does not exist or is not a GeoPackage.")
    connection = _gpkg.connect(file)
    try:
        if connection.execute("select count(*) from sqlite_master where name = 'gpkg_contents'").fetchone()[0] == 0:
            raise ExecuteError(f"ERROR 000732: {input_workspace} is not a GeoPackage.")
        found = findings(connection)
    finally:
        connection.close()
    if check:
        if not os.access(file, os.W_OK):
            raise ExecuteError(f"ERROR: the prerequisite check failed: {file} is read-only.")
        AddMessage(f"Prerequisite check passed for {file}.")
        for kind, table, detail in found:
            AddMessage(f"Old form ({kind}){' in ' + table if table else ''}: {detail}.")
    if not found:
        AddMessage(f"{file} is already in the form grapy writes; nothing to upgrade.")
    elif act:
        upgrade(file)
        AddMessage(f"{file} upgraded: {len(found)} repairs; the file as it was is kept beside it.")
    return Result(file)
