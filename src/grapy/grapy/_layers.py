"""Layers and table views — a name over a dataset, with a definition query and a selection (ADR-224).

arcpy's layer lives as long as the session; grapy's lives as long as this process (INFERRED). Moved here from
``management`` so every module honours one: ``_gpkg.table_of`` reads a layer's dataset, and the ``da`` cursors read only
the rows its definition query shows and, when it has one, its selection — so an analysis or conversion tool given a
layer works on its selected features, or on all it shows when none is selected, as arcpy documents.

A layer's name may not contain ``.gpkg``: a path and a name can never be mistaken for each other.
"""
from __future__ import annotations

import os


class Layer:
    def __init__(self, name, source, where, kind):
        self.name, self.source, self.where, self.kind = name, source, where, kind
        self.selection: set = set()
        self.joins: list = []

    def __str__(self):
        return self.name


class Join:
    """A join on a layer (management.AddJoin): the join table, the fields matched, and whether rows with no match are
    kept (``KEEP_ALL``, an outer join) or left out (``KEEP_COMMON``). One to first: each layer row meets at most the
    first matching join row, by the join table's object ID."""

    def __init__(self, path, name, in_field, join_field, keep_common):
        self.path, self.name, self.in_field, self.join_field, self.keep_common = path, name, in_field, join_field, keep_common


class Joined:
    """How a cursor reads a joined layer: the FROM clause, each qualified field (``<table>.<field>``) with the SQL that
    reads it and its type, and which of them belong to a join table and so cannot be written."""

    def __init__(self, from_sql, fields, oid_sql, geometry_sql, attached):
        self.from_sql, self.fields, self.oid_sql, self.geometry_sql, self.attached = from_sql, fields, oid_sql, geometry_sql, attached

    @property
    def names(self):
        return [f[0] for f in self.fields]

    def sql(self, name):
        return next(f[1] for f in self.fields if f[0] == name)

    def column(self, name):
        """The in-table column a qualified field names, or None for a join table's field."""
        return next((f[3] for f in self.fields if f[0] == name), None)


def joined(layer, connection, table, oid, geometry):
    """The join of a layer, its join tables attached to ``connection``. A one-to-first join takes, for each row, the
    matching join row of lowest object ID; text matches without regard to case, as the AddJoin page says one-to-first
    joins do."""
    from . import _gpkg
    own = _gpkg.describe(connection, table)[0]
    fields = [(f"{table}.{c.name}", f'"{table}"."{c.name}"', c.type, c.name) for c in own]
    clause = f'"{table}"'
    attached = []
    by_name = {c.name.lower(): c for c in own}
    for i, join in enumerate(layer.joins):
        file, _ = _gpkg.split(join.path)
        schema = f"grapy_join{i}"
        connection.execute("attach database ? as " + schema, (file,))
        attached.append(schema)
        their_connection, their_table = _gpkg.table_of(join.path)
        try:
            columns, _, _, their_geometry = _gpkg.describe(their_connection, their_table)
            their_oid = _gpkg.oid_column(their_connection, their_table)
        finally:
            their_connection.close()
        alias = join.name
        theirs = {c.name.lower(): c for c in columns}
        mine, match = by_name[join.in_field.lower()], theirs[join.join_field.lower()]
        collate = " collate nocase" if mine.type == "String" and match.type == "String" else ""
        first = (f'(select min(x."{their_oid}") from {schema}."{their_table}" as x where x."{match.name}"{collate} = '
                 f'"{table}"."{mine.name}")')
        clause += (f' {"" if join.keep_common else "left "}join {schema}."{their_table}" as "{alias}" '
                   f'on "{alias}"."{their_oid}" = {first}')
        fields.append((f"{alias}.{their_oid}", f'"{alias}"."{their_oid}"', "Integer", None))
        fields.extend((f"{alias}.{c.name}", f'"{alias}"."{c.name}"', c.type, None) for c in columns)
    return Joined(clause, fields, f'"{table}"."{oid}"', f'"{table}"."{geometry}"', attached)


LAYERS: dict = {}


def text(value):
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


def find(value):
    """The layer a value names, or None for a dataset path."""
    if isinstance(value, Layer):
        return value
    return LAYERS.get(text(value))


def filter_of(layer, oid, quoted=False):
    """The where clause that shows a layer's rows: its definition query and its selection, by the dataset's id column
    (``quoted``: the column is given as SQL already, as a joined layer's ``"table"."fid"``)."""
    clauses = [f"({layer.where})"] if layer.where else []
    if layer.selection:
        column = oid if quoted else f'"{oid}"'
        clauses.append(f'{column} in ({", ".join(str(int(i)) for i in sorted(layer.selection))})')
    return " and ".join(clauses) or None


def combined(layer, oid, where_clause, quoted=False):
    """A cursor's own where clause joined with ``AND`` to the layer's."""
    shown = filter_of(layer, oid, quoted) if layer is not None else None
    if shown and where_clause:
        return f"({shown}) and ({where_clause})"
    return shown or where_clause
