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

    def __str__(self):
        return self.name


LAYERS: dict = {}


def text(value):
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


def find(value):
    """The layer a value names, or None for a dataset path."""
    if isinstance(value, Layer):
        return value
    return LAYERS.get(text(value))


def filter_of(layer, oid):
    """The where clause that shows a layer's rows: its definition query and its selection, by the dataset's id column."""
    clauses = [f"({layer.where})"] if layer.where else []
    if layer.selection:
        clauses.append(f'"{oid}" in ({", ".join(str(int(i)) for i in sorted(layer.selection))})')
    return " and ".join(clauses) or None


def combined(layer, oid, where_clause):
    """A cursor's own where clause joined with ``AND`` to the layer's."""
    shown = filter_of(layer, oid) if layer is not None else None
    if shown and where_clause:
        return f"({shown}) and ({where_clause})"
    return shown or where_clause
