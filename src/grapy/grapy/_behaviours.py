"""Geodatabase editing behaviours held in the GeoPackage a tool works on — ADR-240.

Three behaviours a class can carry, each a row of grapy's own tables registered in ``gpkg_extensions`` as the
``grapy_edit_behaviours`` extension (INFERRED, ADR-240 §5.6 a):

- **Editor tracking** (``grapy_editor_tracking``): the creator, creation date, last editor and last edit date fields a
  class names, and whether its dates are UTC. Every insert and update made through ``arcpy.da``'s cursors, through an
  attribute rule's ``edit`` dictionary (ADR-235) and through AddAttachments (ADR-228) fills them: an insert all four,
  an update the last two. The user is the operating system's login name, as the help topic says of a file or mobile
  geodatabase; the date is the second the edit was made, in UTC or in the machine's local time.
- **Last edit time** (``grapy_last_edit_time``): triggers on the class keep its ``gpkg_contents.last_change`` — the
  GeoPackage standard's own *timestamp of last change to content* (OGC 12-128r18 §1.1.3.1.1) — current on every
  insert, update and delete, by whatever writes the table.
- **COGO** (``grapy_cogo``): a line class whose five COGO fields are present and which is marked COGO enabled.

And two things that are not behaviours but share the extension: ``grapy_incrementing_id`` records the
database-maintained ID fields AddIncrementingIDField adds (each kept by a trigger), and ``grapy_unregistered`` keeps
the geometry type, reference and dimensions CreateUnRegisteredFeatureclass was given until RegisterWithGeodatabase
registers the table.
"""
from __future__ import annotations

import datetime
import getpass

from ._topology import register

EXTENSION = "grapy_edit_behaviours"
DEFINITION = "Graticula ADR-240: grapy's editor tracking, last edit time, COGO and incrementing ID fields"
TRACKING, LAST_EDIT, COGO, INCREMENTING, UNREGISTERED = (
    "grapy_editor_tracking", "grapy_last_edit_time", "grapy_cogo", "grapy_incrementing_id", "grapy_unregistered")
COGO_FIELDS = ("Direction", "Distance", "ArcLength", "Radius", "Radius2")
DEFAULT_TRACKING = ("created_user", "created_date", "last_edited_user", "last_edited_date")


def _has(connection, name):
    return connection.execute("select 1 from sqlite_master where name = ?", (name,)).fetchone() is not None


def ensure(connection):
    connection.execute(f"""create table if not exists {TRACKING} (table_name text primary key, creator_field text,
        created_field text, editor_field text, edited_field text, utc integer not null)""")
    connection.execute(f"create table if not exists {LAST_EDIT} (table_name text primary key)")
    connection.execute(f"create table if not exists {COGO} (table_name text primary key)")
    connection.execute(f"""create table if not exists {INCREMENTING} (table_name text not null, field_name text not null,
        primary key (table_name, field_name))""")
    connection.execute(f"""create table if not exists {UNREGISTERED} (table_name text primary key,
        geometry_type text not null, srs_id integer not null, z integer not null, m integer not null)""")
    register(connection, (TRACKING, LAST_EDIT, COGO, INCREMENTING, UNREGISTERED), EXTENSION, DEFINITION)


# ---- editor tracking ------------------------------------------------------------------------------------------------


def tracking(connection, table):
    """(creator, created, editor, edited, utc) of a tracked class — each field name or None — or None."""
    if not _has(connection, TRACKING):
        return None
    row = connection.execute(f"select creator_field, created_field, editor_field, edited_field, utc from {TRACKING} "
                             "where table_name = ?", (table,)).fetchone()
    if row is None or not any(row[:4]):
        return None
    return row[0], row[1], row[2], row[3], bool(row[4])


def set_tracking(connection, table, fields, utc):
    """The tracked fields of a class: ``fields`` (creator, created, editor, edited), None for one not tracked; a class
    tracking none is no longer tracked."""
    ensure(connection)
    if not any(fields):
        connection.execute(f"delete from {TRACKING} where table_name = ?", (table,))
        return
    connection.execute(f"insert or replace into {TRACKING} values (?, ?, ?, ?, ?, ?)", (table, *fields, int(bool(utc))))


def user():
    """The operating system's login name — what the help topic says a file or mobile geodatabase records — or empty
    where there is none (a container without one)."""
    try:
        return getpass.getuser()
    except Exception:  # noqa: BLE001 — no login name is no user, not a failed edit
        return ""


def moment(utc):
    """Now, to the second (a *low precision* date, as the tool page requires of the date fields), in UTC or local."""
    now = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None) if utc else datetime.datetime.now()
    return now.replace(microsecond=0)


class Tracker:
    """What a tracked class's edits are stamped with."""

    def __init__(self, creator, created, editor, edited, utc):
        self.creator, self.created, self.editor, self.edited, self.utc = creator, created, editor, edited, utc

    def _stamp(self, inserting):
        who, when = user(), moment(self.utc)
        pairs = [(self.editor, who), (self.edited, when)]
        if inserting:
            pairs = [(self.creator, who), (self.created, when)] + pairs
        return [(name, value) for name, value in pairs if name]

    def insert(self, row):
        """A new row's values, with every tracked field stamped; a value the edit gave one is replaced."""
        return _put(dict(row), self._stamp(True))

    def update(self, row):
        """A changed row's values, with the last editor and last edit date stamped."""
        return _put(dict(row), self._stamp(False))

    def update_names(self, names, values):
        """``names`` and ``values`` of an UPDATE, the last editor and last edit date among them."""
        stamped = _put(dict(zip(names, values)), self._stamp(False))
        return list(stamped), list(stamped.values())


def _put(row, pairs):
    for name, value in pairs:
        for key in [k for k in row if k.lower() == name.lower()]:
            del row[key]
        row[name] = value
    return row


def tracker(connection, table):
    found = tracking(connection, table)
    return Tracker(*found) if found else None


def stamp_insert(connection, table, row):
    found = tracker(connection, table)
    return found.insert(row) if found else row


def stamp_update(connection, table, row):
    found = tracker(connection, table)
    return found.update(row) if found else row


def stamp_row(connection, table, oid_column, oid, inserting):
    """A row written without a cursor (an attachment) stamped after the fact."""
    found = tracker(connection, table)
    if not found:
        return
    pairs = found._stamp(inserting)
    connection.execute(f'update "{table}" set {", ".join(chr(34) + n + chr(34) + " = ?" for n, _ in pairs)} '
                       f'where "{oid_column}" = ?', [*(v for _, v in pairs), oid])


# ---- last edit time -------------------------------------------------------------------------------------------------


def _trigger(table, event):
    return f"grapy_last_edit_{event}_{table}".replace('"', "")


def last_edit_enabled(connection, table):
    return _has(connection, LAST_EDIT) and connection.execute(
        f"select 1 from {LAST_EDIT} where table_name = ?", (table,)).fetchone() is not None


def _last_edit_triggers(connection, table):
    quoted = table.replace("'", "''")
    for event in ("insert", "update", "delete"):
        name = _trigger(table, event)
        connection.execute(f'drop trigger if exists "{name}"')
        connection.execute(f'''create trigger "{name}" after {event} on "{table}" begin
            update gpkg_contents set last_change = strftime('%Y-%m-%dT%H:%M:%fZ', 'now') where table_name = '{quoted}';
            end''')


def enable_last_edit(connection, table):
    ensure(connection)
    connection.execute(f"insert or ignore into {LAST_EDIT} values (?)", (table,))
    _last_edit_triggers(connection, table)


def disable_last_edit(connection, table):
    for event in ("insert", "update", "delete"):
        connection.execute(f'drop trigger if exists "{_trigger(table, event)}"')
    if _has(connection, LAST_EDIT):
        connection.execute(f"delete from {LAST_EDIT} where table_name = ?", (table,))


# ---- COGO -----------------------------------------------------------------------------------------------------------


def cogo_enabled(connection, table):
    return _has(connection, COGO) and connection.execute(
        f"select 1 from {COGO} where table_name = ?", (table,)).fetchone() is not None


# ---- incrementing ID fields -----------------------------------------------------------------------------------------


def _id_trigger(table, field):
    return f"grapy_increment_{table}_{field}".replace('"', "")


def incrementing(connection, table):
    """The database-maintained ID fields of a table."""
    if not _has(connection, INCREMENTING):
        return []
    return [r[0] for r in connection.execute(f"select field_name from {INCREMENTING} where table_name = ?", (table,))]


def _id_triggers(connection, table, field):
    """An inserted row whose ID is null takes the largest ID plus one — the database keeps it, whoever inserts."""
    name = _id_trigger(table, field)
    connection.execute(f'create unique index if not exists "{name}_unique" on "{table}" ("{field}")')
    connection.execute(f'drop trigger if exists "{name}"')
    connection.execute(f'''create trigger "{name}" after insert on "{table}" when new."{field}" is null begin
        update "{table}" set "{field}" = (select coalesce(max("{field}"), 0) + 1 from "{table}") where rowid = new.rowid;
        end''')


def add_incrementing(connection, table, field):
    """An integer ID field added, numbered 1, 2 … in the table's row order, and kept by a trigger from then on."""
    ensure(connection)
    connection.execute(f'alter table "{table}" add column "{field}" INTEGER')
    for number, (rowid,) in enumerate(connection.execute(f'select rowid from "{table}" order by rowid').fetchall(), 1):
        connection.execute(f'update "{table}" set "{field}" = ? where rowid = ?', (number, rowid))
    connection.execute(f"insert into {INCREMENTING} values (?, ?)", (table, field))
    _id_triggers(connection, table, field)


# ---- what a table carries when it is deleted, renamed or rebuilt -----------------------------------------------------


def drop(connection, table):
    """A deleted table's behaviours, and its attachment table's. Its triggers and indexes go with the table."""
    for name in (TRACKING, LAST_EDIT, COGO, INCREMENTING, UNREGISTERED):
        if _has(connection, name):
            connection.execute(f"delete from {name} where table_name in (?, ?)", (table, f"{table}__ATTACH"))


def rename(connection, old, new):
    """A renamed table's behaviours, and its attachment table's, under the new names; its triggers made again."""
    for name in (TRACKING, LAST_EDIT, COGO, INCREMENTING, UNREGISTERED):
        if _has(connection, name):
            for before, after in ((old, new), (f"{old}__ATTACH", f"{new}__ATTACH")):
                connection.execute(f"update {name} set table_name = ? where table_name = ?", (after, before))
    restore_triggers(connection, new, old)


def restore_triggers(connection, table, old=None):
    """The triggers a table's behaviours keep, made (again) — after a rename, or after RegisterWithGeodatabase has
    rebuilt the table, which drops them."""
    if last_edit_enabled(connection, table):
        if old is not None:
            for event in ("insert", "update", "delete"):
                connection.execute(f'drop trigger if exists "{_trigger(old, event)}"')
        _last_edit_triggers(connection, table)
    for field in incrementing(connection, table):
        if old is not None:
            connection.execute(f'drop trigger if exists "{_id_trigger(old, field)}"')
        _id_triggers(connection, table, field)
