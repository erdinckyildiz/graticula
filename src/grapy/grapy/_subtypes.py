"""Subtypes and field default values held in the GeoPackage a tool works on — ADR-228.

The GeoPackage has no subtypes and no column default that can be changed after a table is made (SQLite's ALTER TABLE
cannot alter a default), and no public GeoPackage or GDAL convention stores either. They are grapy's own tables,
registered in ``gpkg_extensions`` as the ``grapy_subtypes`` extension (INFERRED, ADR-228 §5.2):

- ``grapy_subtype_field``: a table's subtype field and its default subtype code.
- ``grapy_subtype``: each subtype's code and name.
- ``grapy_field_default``: a field's default value, for the field itself (``subtype_code`` null) or for one subtype.
- ``grapy_subtype_domain``: the attribute domain a field takes in one subtype. A field's own domain stays where
  AssignDomainToField has always put it, the GeoPackage schema extension's ``gpkg_data_columns``.

INFERRED: a subtype that sets no default or domain for a field takes the field's own (read-time fallback), so a subtype
added after a field's default was set answers that default. A new row takes the default subtype code when it names no
subtype, and then, for each field it does not name, its subtype's default or else the field's.
"""
from __future__ import annotations

from ._topology import register

EXTENSION = "grapy_subtypes"
DEFINITION = "Graticula ADR-228: grapy's subtypes and field default values"
TABLES = ("grapy_subtype_field", "grapy_subtype", "grapy_field_default", "grapy_subtype_domain")
INTEGER_TYPES = ("SmallInteger", "Integer", "BigInteger")


def ensure(connection):
    connection.execute("""create table if not exists grapy_subtype_field (table_name text primary key,
        field_name text not null, default_code integer not null default 0)""")
    connection.execute("""create table if not exists grapy_subtype (table_name text not null, code integer not null,
        name text not null, primary key (table_name, code))""")
    # ``value`` has no declared type, so SQLite keeps an integer, a real and a text default each as it was given.
    connection.execute("""create table if not exists grapy_field_default (table_name text not null,
        field_name text not null, subtype_code integer, value)""")
    connection.execute("""create table if not exists grapy_subtype_domain (table_name text not null,
        subtype_code integer not null, field_name text not null, domain_name text not null,
        primary key (table_name, subtype_code, field_name))""")
    register(connection, TABLES, EXTENSION, DEFINITION)


def _held(connection):
    return connection.execute("select 1 from sqlite_master where name = 'grapy_subtype_field'").fetchone() is not None


def field(connection, table):
    """(subtype field, default subtype code) of a table, or None when it has no subtype field."""
    if not _held(connection):
        return None
    row = connection.execute("select field_name, default_code from grapy_subtype_field where table_name = ?",
                             (table,)).fetchone()
    return (row[0], int(row[1])) if row else None


def codes(connection, table):
    """{code: name} of a table's subtypes, in order of code."""
    if not _held(connection):
        return {}
    return {int(c): n for c, n in connection.execute("select code, name from grapy_subtype where table_name = ? "
                                                    "order by code", (table,))}


def defaults(connection, table):
    """{(field lower case, subtype code or None): value} of a table's default values."""
    if not _held(connection):
        return {}
    return {(f.lower(), None if c is None else int(c)): v for f, c, v in connection.execute(
        "select field_name, subtype_code, value from grapy_field_default where table_name = ?", (table,))}


def domains(connection, table):
    """{(field lower case, subtype code): domain name} of a table's per-subtype domains."""
    if not _held(connection):
        return {}
    return {(f.lower(), int(c)): d for f, c, d in connection.execute(
        "select field_name, subtype_code, domain_name from grapy_subtype_domain where table_name = ?", (table,))}


def set_default(connection, table, field_name, code, value):
    ensure(connection)
    connection.execute("delete from grapy_field_default where table_name = ? and lower(field_name) = lower(?) and "
                       "subtype_code is ?", (table, field_name, code))
    if value is not None:
        connection.execute("insert into grapy_field_default values (?, ?, ?, ?)", (table, field_name, code, value))


def drop(connection, table):
    """Everything kept for a table, gone with it (Delete) or with its subtype field (SetSubtypeField)."""
    if not _held(connection):
        return
    for name in TABLES:
        connection.execute(f"delete from {name} where table_name = ?", (table,))


def drop_subtypes(connection, table):
    """The subtype field, its subtypes and their own defaults and domains; the fields' own defaults stay."""
    if not _held(connection):
        return
    for name in ("grapy_subtype_field", "grapy_subtype", "grapy_subtype_domain"):
        connection.execute(f"delete from {name} where table_name = ?", (table,))
    connection.execute("delete from grapy_field_default where table_name = ? and subtype_code is not null", (table,))


def drop_field(connection, table, field_name):
    """A deleted field's defaults and per-subtype domains."""
    if not _held(connection):
        return
    for name in ("grapy_field_default", "grapy_subtype_domain"):
        connection.execute(f"delete from {name} where table_name = ? and lower(field_name) = lower(?)", (table, field_name))


def rename(connection, old, new):
    if not _held(connection):
        return
    for name in TABLES:
        connection.execute(f"update {name} set table_name = ? where table_name = ?", (new, old))


class InsertPlan:
    """What a new row of one table takes that it does not name: a GlobalID (ADR-228 §5.3), its default subtype code,
    and default values. Built once per cursor."""

    def __init__(self, connection, table):
        from . import _attachments
        self.global_id = _attachments.global_id_field(connection, table)
        self.subtype = field(connection, table)
        self.defaults = defaults(connection, table)
        self.names = {}
        for (name, _), _ in self.defaults.items():
            self.names[name] = name
        if self.defaults:
            for _, column, *_ in connection.execute(f'pragma table_info("{table}")'):
                if column.lower() in self.names:
                    self.names[column.lower()] = column

    def __bool__(self):
        return bool(self.global_id or self.subtype or self.defaults)

    def apply(self, values):
        """``values`` ({column: value} of what the row names) with what it does not name added."""
        from . import _attachments
        given = {k.lower(): v for k, v in values.items()}
        out = dict(values)
        if self.global_id and self.global_id.lower() not in given:
            out[self.global_id] = _attachments.new_guid()
        code = None
        if self.subtype is not None:
            name, default_code = self.subtype
            if name.lower() in given:
                code = given[name.lower()]
                try:
                    code = int(code) if code is not None else None
                except (TypeError, ValueError):
                    pass
            else:
                out[name] = code = default_code
        for lower, column in self.names.items():
            if lower in given:
                continue
            if code is not None and (lower, code) in self.defaults:
                out[column] = self.defaults[(lower, code)]
            elif (lower, None) in self.defaults:
                out[column] = self.defaults[(lower, None)]
        return out
