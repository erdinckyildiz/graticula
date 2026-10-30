"""Database sequences held in the GeoPackage a tool works on — ADR-235 §5.5.

CreateDatabaseSequence and DeleteDatabaseSequence make and remove them, arcpy.da.ListDatabaseSequences lists them,
and Arcade's NextSequenceValue draws from them. A GeoPackage (SQLite) has no sequence object, so they are rows of
grapy's own ``grapy_sequence`` table, registered in ``gpkg_extensions`` as the ``grapy_sequences`` extension
(INFERRED, ADR-235 §5.8): its name, start value, increment and the last value drawn (null before the first).
"""
from __future__ import annotations

from ._topology import register

EXTENSION = "grapy_sequences"
DEFINITION = "Graticula ADR-235: grapy's database sequences"
TABLE = "grapy_sequence"


def ensure(connection):
    connection.execute(f"""create table if not exists {TABLE} (name text primary key collate nocase,
        start_value integer not null, increment integer not null, last_value integer)""")
    register(connection, (TABLE,), EXTENSION, DEFINITION)


def _held(connection):
    return connection.execute("select 1 from sqlite_master where name = ?", (TABLE,)).fetchone() is not None


def listed(connection):
    """[(name, start, increment, current)] — current is the last value drawn, or the start before any."""
    if not _held(connection):
        return []
    return [(n, s, i, s if v is None else v) for n, s, i, v in connection.execute(
        f"select name, start_value, increment, last_value from {TABLE} order by name collate nocase")]


def exists(connection, name):
    return _held(connection) and connection.execute(f"select 1 from {TABLE} where name = ?", (name,)).fetchone() is not None


def create(connection, name, start, increment):
    ensure(connection)
    connection.execute(f"insert into {TABLE} values (?, ?, ?, null)", (name, int(start), int(increment)))


def delete(connection, name):
    if _held(connection):
        connection.execute(f"delete from {TABLE} where name = ?", (name,))


def next_value(connection, name):
    """The sequence's next value, drawn (the start first, then each increment); None when it does not exist."""
    if not _held(connection):
        return None
    row = connection.execute(f"select start_value, increment, last_value from {TABLE} where name = ?", (name,)).fetchone()
    if row is None:
        return None
    start, increment, last = row
    value = start if last is None else last + increment
    connection.execute(f"update {TABLE} set last_value = ? where name = ?", (value, name))
    return value
