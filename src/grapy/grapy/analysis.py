"""arcpy.analysis's tools grapy builds, by their documented names and parameters — ADR-223 §5.2.

Each reads and writes feature classes through the cursors, so a tool's output is written exactly as a toolbox's own
InsertCursor would write it. Distances are in the data's units, as arcpy's are for a projected reference; a geographic
reference is buffered in metres on the ground, as arcpy's planar tools are not — said in the name of the parameter
that takes a linear unit (``"100 Meters"``).
"""
from __future__ import annotations

from . import _gpkg, da
from ._result import Result
from ._runtime import NotBuiltError

_METRES = {"METERS": 1, "KILOMETERS": 1000, "FEET": 0.3048, "MILES": 1609.344, "YARDS": 0.9144, "NAUTICALMILES": 1852,
           "DECIMETERS": 0.1, "CENTIMETERS": 0.01, "MILLIMETERS": 0.001, "INCHES": 0.0254}


def _schema(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.describe(connection, table)
    finally:
        connection.close()


def _distance(value, srid):
    """A linear unit such as ``"100 Meters"`` in the data's units, or a number as it is."""
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    number = float(parts[0])
    if len(parts) == 1:
        return number
    metres = number * _METRES.get(parts[1].upper().rstrip("S") + "S", _METRES.get(parts[1].upper(), 1))
    from ._reference import SpatialReference
    if srid > 0 and SpatialReference(srid).type == "Geographic":
        raise ValueError("A buffer of a geographic reference in metres needs the data projected first (management.Project).")
    return metres


def Buffer(in_features, out_feature_class, buffer_distance_or_field, line_side="FULL", line_end_type="ROUND",
           dissolve_option="NONE", dissolve_field=None, method="PLANAR", *_, **__):
    columns, _, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    by_field = isinstance(buffer_distance_or_field, str) and buffer_distance_or_field in names
    _gpkg.create(out_feature_class if ".gpkg" in str(out_feature_class).lower() else out_feature_class + ".gpkg",
                 "Polygon", srid, [] if dissolve_option.upper() == "ALL" else columns)
    out = out_feature_class if ".gpkg" in str(out_feature_class).lower() else out_feature_class + ".gpkg"
    shapes = []
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None:
                continue
            distance = float(row[names.index(buffer_distance_or_field)]) if by_field else _distance(buffer_distance_or_field, srid)
            kwargs = {"cap_style": "flat" if line_end_type.upper() == "FLAT" else "round"}
            if line_side.upper() in ("LEFT", "RIGHT"):
                kwargs["single_sided"] = True
                distance = distance if line_side.upper() == "LEFT" else -distance
            shape = row[-1]._shape.buffer(distance, **kwargs)
            if line_side.upper() == "OUTSIDE_ONLY":
                shape = shape.difference(row[-1]._shape)
            shapes.append((row[:-1], shape))
    with da.InsertCursor(out, [*([] if dissolve_option.upper() == "ALL" else names), "SHAPE@"]) as written:
        if dissolve_option.upper() == "ALL":
            from shapely import union_all
            written.insertRow((union_all([s for _, s in shapes]),))
        else:
            for values, shape in shapes:
                written.insertRow((*values, shape))
    return Result(out)


def _overlay(in_features, other, out_feature_class, operation):
    columns, shape, srid, _ = _schema(in_features)
    names = [c.name for c in columns]
    out = out_feature_class if ".gpkg" in str(out_feature_class).lower() else out_feature_class + ".gpkg"
    with da.SearchCursor(other, ["SHAPE@"]) as rows:
        from shapely import union_all
        mask = union_all([r[0]._shape for r in rows if r[0] is not None])
    _gpkg.create(out, shape, srid, columns)
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows, da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for row in rows:
            if row[-1] is None:
                continue
            cut = operation(row[-1]._shape, mask)
            if not cut.is_empty:
                written.insertRow((*row[:-1], cut))
    return Result(out)


def Clip(in_features, clip_features, out_feature_class, cluster_tolerance=None):
    return _overlay(in_features, clip_features, out_feature_class, lambda a, b: a.intersection(b))


def Erase(in_features, erase_features, out_feature_class, cluster_tolerance=None):
    return _overlay(in_features, erase_features, out_feature_class, lambda a, b: a.difference(b))


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.analysis.{name} is not built in grapy yet.")

    return missing
