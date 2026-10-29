"""Branch versions and archives held in the GeoPackage — ADR-227.

A registered class keeps its *current default state* in its own table, so every reader — any grapy tool, and any
GeoPackage reader that is not grapy — sees exactly what it saw before. Its history lives beside it, in
``<table>__history``, with the branch model's documented columns: ``gdb_objectid``, ``gdb_from_date`` (a moment),
``gdb_branch_id`` (0 for DEFAULT), ``gdb_is_delete`` and ``gdb_deleted_by``. Triggers on the table append a history
row for every insert, update and delete made to DEFAULT, by whatever writes it. An edit on a named version is written
to the history only, under that version's branch.

**This differs from ADR-227 §5.1 as written** ("keeps every edit as a new row" in the table itself): kept in the
table, history would be counted by every grapy tool that reads the table with SQL rather than through a cursor
(GetCount, TruncateTable …) and by every GeoPackage reader — the counterargument §3 names. The columns are the
documented ones; only where they live differs. INFERRED, for confirmation with §5.5.

Moments are a counter (``grapy_moments``), each with the UTC time it was taken (ADR-227 §7). Versions are rows of
``grapy_versions``: DEFAULT is branch 0; a named version is a branch off DEFAULT, seeing DEFAULT as of its
``base_moment`` (its creation or last reconcile) plus its own edits.
"""
from __future__ import annotations

import datetime

VERSIONS, REGISTERED, MOMENTS = "grapy_versions", "grapy_versioned", "grapy_moments"
SYSTEM = ("gdb_archive_oid", "gdb_objectid", "gdb_from_date", "gdb_branch_id", "gdb_is_delete", "gdb_deleted_by")
DEFAULT = "DEFAULT"
_NOW_SQL = "strftime('%Y-%m-%dT%H:%M:%f','now')"


def history(table):
    return f"{table}__history"


def ensure(connection):
    """The three tables of grapy's versioning, and the DEFAULT version."""
    connection.execute(f"create table if not exists {MOMENTS} (moment integer primary key autoincrement, at text not null)")
    connection.execute(f"""create table if not exists {VERSIONS} (name text primary key, branch_id integer unique not null,
        parent text, description text, access text, owner text, created text, modified text, base_moment integer not null)""")
    connection.execute(f"create table if not exists {REGISTERED} (table_name text primary key, kind text not null)")
    if connection.execute(f"select 1 from {VERSIONS} where branch_id = 0").fetchone() is None:
        now = now_text()
        connection.execute(f"insert into {VERSIONS} values (?, 0, null, '', 'Public', '', ?, ?, 0)", (DEFAULT, now, now))


def now_text():
    return datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]


def has_tables(connection):
    return connection.execute(f"select 1 from sqlite_master where name = '{REGISTERED}'").fetchone() is not None


def kind(connection, table):
    """'branch', 'archive' or None."""
    if not has_tables(connection):
        return None
    row = connection.execute(f"select kind from {REGISTERED} where table_name = ?", (table,)).fetchone()
    return row[0] if row else None


def new_moment(connection):
    return connection.execute(f"insert into {MOMENTS} (at) values ({_NOW_SQL})").lastrowid


def current_moment(connection):
    row = connection.execute(f"select max(moment) from {MOMENTS}").fetchone()
    return int(row[0] or 0)


def moment_at(connection, when):
    """The last moment taken at or before ``when`` (a UTC datetime or its ISO text); 0 before any."""
    text = when if isinstance(when, str) else when.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3]
    row = connection.execute(f"select max(moment) from {MOMENTS} where at <= ?", (text,)).fetchone()
    return int(row[0] or 0)


def version(connection, name):
    """(name, branch_id, base_moment) of a version, DEFAULT answering to its common aliases."""
    ensure(connection)
    wanted = str(name)
    if wanted.split(".")[-1].upper() == DEFAULT:
        wanted = DEFAULT
    row = connection.execute(f"select name, branch_id, base_moment from {VERSIONS} where name = ?", (wanted,)).fetchone()
    if row is None:
        from ._runtime import ExecuteError
        raise ExecuteError(f"ERROR 000800: the version '{name}' does not exist.")
    return row


def columns(connection, table):
    """The table's own columns, its id and geometry column."""
    from . import _gpkg
    described, _, _, geometry = _gpkg.describe(connection, table)
    return [c.name for c in described], _gpkg.oid_column(connection, table), geometry


def register(connection, table, kind_):
    """The history table, the existing rows copied into it at one new moment, and the triggers that keep it."""
    from . import _gpkg
    ensure(connection)
    names, oid, geometry = columns(connection, table)
    described = {c.name: c for c in _gpkg.describe(connection, table)[0]}
    defined = "".join(f', "{n}" {_gpkg._TYPES.get(described[n].type.upper(), "TEXT")}' for n in names)
    connection.execute(f'''create table "{history(table)}" (gdb_archive_oid integer primary key autoincrement,
        gdb_objectid integer not null, gdb_from_date integer not null, gdb_branch_id integer not null,
        gdb_is_delete integer not null, gdb_deleted_by text, "{geometry}" BLOB{defined})''')
    connection.execute(f'create index "{history(table)}_object" on "{history(table)}" (gdb_objectid, gdb_branch_id)')
    moment = new_moment(connection)
    listed = ", ".join(f'"{n}"' for n in [geometry, *names])
    connection.execute(f'insert into "{history(table)}" (gdb_objectid, gdb_from_date, gdb_branch_id, gdb_is_delete, '
                       f'{listed}) select "{oid}", ?, 0, 0, {listed} from "{table}"', (moment,))
    for event, row, deleted in (("insert", "new", 0), ("update", "new", 0), ("delete", "old", 1)):
        values = ", ".join(f'{row}."{n}"' for n in [geometry, *names])
        connection.execute(f'''create trigger "{table}__grapy_{event}" after {event} on "{table}" begin
            insert into {MOMENTS} (at) values ({_NOW_SQL});
            insert into "{history(table)}" (gdb_objectid, gdb_from_date, gdb_branch_id, gdb_is_delete, {listed})
            values ({row}."{oid}", (select max(moment) from {MOMENTS}), 0, {deleted}, {values});
        end''')
    connection.execute(f"insert into {REGISTERED} values (?, ?)", (table, kind_))


def unregister(connection, table, keep_history_as=None):
    """The triggers and the history removed — or the history kept as a table of its own name."""
    for event in ("insert", "update", "delete"):
        connection.execute(f'drop trigger if exists "{table}__grapy_{event}"')
    if keep_history_as:
        connection.execute(f'alter table "{history(table)}" rename to "{keep_history_as}"')
    else:
        connection.execute(f'drop table if exists "{history(table)}"')
    connection.execute(f"delete from {REGISTERED} where table_name = ?", (table,))


def state_sql(connection, table, branch, base_moment, until=None):
    """A FROM clause reading the table as a version sees it, or as DEFAULT was at the moment ``until``: for each object,
    its latest row on the branch, else DEFAULT's latest row at the version's base moment; deleted objects left out."""
    names, oid, geometry = columns(connection, table)
    top = until if until is not None else 2 ** 62
    default_top = min(base_moment, top) if branch else top
    listed = ", ".join(f'h."{n}"' for n in [geometry, *names])
    chosen = (f'select gdb_archive_oid from (select gdb_archive_oid, gdb_is_delete, row_number() over ('
              f'partition by gdb_objectid order by (gdb_branch_id = {int(branch)}) desc, gdb_from_date desc, '
              f'gdb_archive_oid desc) as rank from "{history(table)}" where (gdb_branch_id = {int(branch)} and '
              f'gdb_from_date <= {int(top)}) or (gdb_branch_id = 0 and gdb_from_date <= {int(default_top)})) '
              f'where rank = 1 and gdb_is_delete = 0')
    return (f'(select h.gdb_objectid as "{oid}", {listed} from "{history(table)}" h where h.gdb_archive_oid in '
            f'({chosen})) as "{table}"')


def current_row(connection, table, branch, base_moment, object_id):
    """{column: value} of an object as a version sees it now, the geometry under its column name; None if absent."""
    names, oid, geometry = columns(connection, table)
    sql = f'select * from {state_sql(connection, table, branch, base_moment)} where "{oid}" = ?'
    cursor = connection.execute(sql, (object_id,))
    row = cursor.fetchone()
    if row is None:
        return None
    return dict(zip([d[0] for d in cursor.description], row))


def write(connection, table, branch, object_id, values, deleted=False):
    """A history row for an edit on a named version: the object's whole row (``values``, the geometry under its column
    name) at a new moment, under the version's branch."""
    names, oid, geometry = columns(connection, table)
    moment = new_moment(connection)
    listed = [geometry, *names]
    connection.execute(f'insert into "{history(table)}" (gdb_objectid, gdb_from_date, gdb_branch_id, gdb_is_delete, '
                       f'{", ".join(chr(34) + n + chr(34) for n in listed)}) values (?, ?, ?, ?, '
                       f'{", ".join("?" for _ in listed)})',
                       (object_id, moment, branch, 1 if deleted else 0, *[values.get(n) for n in listed]))
    connection.execute(f"update {VERSIONS} set modified = ? where branch_id = ?", (now_text(), branch))
    return moment


def reserve_id(connection, table):
    """An object id the table will not give to another row: its autoincrement sequence moved past it."""
    row = connection.execute("select seq from sqlite_sequence where name = ?", (table,)).fetchone()
    high = connection.execute(f'select max(gdb_objectid) from "{history(table)}"').fetchone()[0] or 0
    next_id = max(int(row[0]) if row else 0, int(high)) + 1
    if row is None:
        connection.execute("insert into sqlite_sequence (name, seq) values (?, ?)", (table, next_id))
    else:
        connection.execute("update sqlite_sequence set seq = ? where name = ?", (next_id, table))
    return next_id


def registered(connection, wanted=None):
    if not has_tables(connection):
        return []
    rows = connection.execute(f"select table_name from {REGISTERED} where kind = ?" if wanted else
                              f"select table_name from {REGISTERED}", (wanted,) if wanted else ()).fetchall()
    return [r[0] for r in rows]
