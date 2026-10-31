"""arcpy.ddd.SurfaceLength and SurfaceSpot (ADR-245): two legacy 3D Analyst tools that Pro's arcpy still answers.

Pro's documentation no longer has a page for either (doc.esri.com's tool-reference/3d-analyst/surface-length.html and
surface-spot.html answered 404 on 2026-10-31, and pro.arcgis.com redirects there); ArcMap's 3D Analyst reference still
documents both under the same arcpy name, arcpy.ddd.SurfaceLength and arcpy.ddd.SurfaceSpot, marked "a deprecated
tool that has been superseded by the Add Surface Information tool". They are built from those pages.
"""
from __future__ import annotations

import numpy as np
import shapely

from ._result import Result
from ._runtime import ExecuteError, NotBuiltError
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return _is_given(value)


def _field(path, name):
    from . import _gpkg
    from .analysis import _schema
    columns, kind, _, _ = _schema(path)
    found = next((c.name for c in columns if c.name.lower() == name.lower()), None)
    if found is None:
        _gpkg.add_column(path, _gpkg.Column(name, "Double"))
        found = name
    return found, kind


def _surface(in_surface, tool, method, pyramid_level_resolution):
    """('tin', Surface) for a TIN, terrain or LAS dataset; ('raster', Raster) for a raster."""
    from . import _tintools
    from .sa import as_raster
    if _tintools.is_surface(in_surface):
        if _given(method) and str(method).upper() != "LINEAR":
            _refuse(tool, "method", method, "natural neighbour interpolation over a triangulated surface is not built "
                    "(ADR-229 §5.4)")
        return "tin", _tintools.surface_of(in_surface, tool, pyramid_level_resolution)
    if _given(method) and str(method).upper() != "BILINEAR":
        _refuse(tool, "method", method, "a raster surface takes only BILINEAR, as the page says")
    return "raster", as_raster(in_surface, tool)


def _raster_z(raster, xs, ys, zf):
    from .sa import _sample
    out = []
    for x, y in zip(xs, ys):
        value = _sample(raster, float(x), float(y), True)
        out.append(np.nan if value is None else float(value) * zf)
    return np.array(out)


def SurfaceSpot(in_surface, in_feature_class, out_spot_field="Spot", z_factor=1, method=None,
                pyramid_level_resolution=0):
    """The surface's value under each point, times ``z_factor``, added to the point feature class in ``out_spot_field``
    (by default ``Spot``, as ArcMap's page says). A raster is interpolated bilinearly from the four nearest cell
    centres, as AddSurfaceInformation does; a TIN, terrain or LAS dataset linearly on its triangles.

    INFERRED: the field is a double, written over when it exists; a point off the surface or over NoData gets an empty
    value; an empty method is BILINEAR on a raster and LINEAR on a triangulated surface."""
    from . import da
    tool = "SurfaceSpot"
    kind, surface = _surface(in_surface, tool, method, pyramid_level_resolution)
    field, shape_type = _field(in_feature_class, out_spot_field if _given(out_spot_field) else "Spot")
    if shape_type != "Point":
        raise ExecuteError(f"{tool}: the input must be a point feature class; it holds {shape_type} features.")
    zf = float(z_factor) if _given(z_factor) else 1.0
    with da.UpdateCursor(in_feature_class, [field, "SHAPE@"]) as rows:
        for row in rows:
            value = None
            if row[1] is not None:
                p = row[1]._shape
                z = (surface.z_at([p.x], [p.y], zf) if kind == "tin" else _raster_z(surface, [p.x], [p.y], zf))[0]
                value = None if np.isnan(z) else float(z)
            rows.updateRow([value, row[1]])
    return Result(str(in_feature_class))


def _raster_length(shape, raster, step, zf):
    """The 3D length of a line over a raster: each segment sampled at its ends and every ``step`` along it, each
    sample's height interpolated bilinearly; None when any sample is off the surface."""
    total = 0.0
    for part in getattr(shape, "geoms", [shape]):
        coords = np.asarray(part.coords)[:, :2]
        points = [coords[0]]
        for a, b in zip(coords[:-1], coords[1:]):
            length = float(np.hypot(*(b - a)))
            n = max(1, int(np.ceil(length / step - 1e-9)))
            for k in range(1, n + 1):
                points.append(a + (b - a) * min(1.0, k * step / length) if length else b)
        points = np.array(points)
        zs = _raster_z(raster, points[:, 0], points[:, 1], zf)
        if np.isnan(zs).any():
            return None
        d = np.diff(np.column_stack([points, zs]), axis=0)
        total += float(np.sqrt((d ** 2).sum(axis=1)).sum())
    return total


def SurfaceLength(in_surface, in_feature_class, out_length_field=None, sample_distance=None, z_factor=1, method=None,
                  pyramid_level_resolution=0):
    """Each line's or polygon's length over the surface, stored in the input as an attribute, as ArcMap's page says. A
    polygon's surface length is that of its boundary. On a raster the line is sampled at every ``sample_distance``
    (by default the cell size, as documented) and at its vertices, each height interpolated bilinearly; on a TIN,
    terrain or LAS dataset it is sampled where it crosses a triangle edge (the "natural densification" the page gives
    as the default) and every ``sample_distance`` when given, as AddSurfaceInformation's SURFACE_LENGTH is.
    ``z_factor`` multiplies the heights before the length is measured.

    INFERRED (ADR-245 §5.6): the page names no default field, so it is ``SURFACE_LENGTH``, AddSurfaceInformation's
    name for the same property; the field is a double, written over when it exists; a feature any part of which is off
    the surface or over NoData gets an empty value on a raster, and is measured where it is on the data area of a
    triangulated surface, as AddSurfaceInformation does."""
    from . import da, _tintools
    tool = "SurfaceLength"
    kind, surface = _surface(in_surface, tool, method, pyramid_level_resolution)
    field, shape_type = _field(in_feature_class, out_length_field if _given(out_length_field) else "SURFACE_LENGTH")
    if shape_type not in ("Polyline", "Polygon"):
        raise ExecuteError(f"{tool}: only polyline and polygon feature classes are valid inputs, as the page says.")
    zf = float(z_factor) if _given(z_factor) else 1.0
    step = float(sample_distance) if _given(sample_distance) else None
    if kind == "raster":
        step = step or surface._grid.width
        tree = lines = None
    else:
        tree, lines = _tintools._crossings(surface)
    with da.UpdateCursor(in_feature_class, [field, "SHAPE@"]) as rows:
        for row in rows:
            value = None
            if row[1] is not None:
                shape = shapely.force_2d(row[1]._shape)
                if shape_type == "Polygon":
                    shape = shape.boundary
                if kind == "raster":
                    value = _raster_length(shape, surface, step, zf)
                else:
                    found = _tintools._line_properties(shape, surface, tree, lines, zf, step, None)
                    value = (found or {}).get("SURFACE_LENGTH")
            rows.updateRow([value, row[1]])
    return Result(str(in_feature_class))
