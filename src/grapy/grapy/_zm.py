"""Z and m through the general tools, as ArcGIS Pro 3.7 carries them (D-412).

Measured 2026-11-20 with ``tools/pro-compare/sweep/cases/zm.py`` (inputs PolylineZM, PolygonZM and PointZM, each tool
run under Pro's arcpy and under grapy):

- An output has z when an input has z and m when an input has m — the ``outputZFlag`` and ``outputMFlag``
  environments' default, *Same As Input* — and ``Enabled`` or ``Disabled`` in either decides instead
  (Clip with ``outputZFlag = "Disabled"`` writes a PolylineM). Buffer and PairwiseBuffer write 2D polygons whatever
  their input carries.
- A tool that cuts an input's shapes (Clip, Erase, Intersect, Union, Identity, SymDiff, Update, Split Line, Split Line
  At Point, Feature To Line, Feature To Polygon, Dissolve, Densify …) keeps each vertex's own z and m, and gives a new
  vertex lying on an input's line or ring the z and m interpolated along that segment: Clip at x = 150 of a segment
  from (0, z 100, m 0) to (200, z 120, m 3) writes z 115, m 2.25. Where two lines cross, each piece takes its own
  line's values (20 on one, 15 on the other at the same x and y).
- A vertex on no input with z and m — a corner of a 2D clip polygon inside a Union piece — has z 0 and m NaN, and so
  does every vertex of a shape a tool makes rather than cuts (Feature To Point's centre, Minimum Bounding Geometry's
  envelope), even where it falls on an input's vertex.
"""
from __future__ import annotations

import math

import numpy as np
import shapely

from . import _gpkg

_FLAGS = {"enabled": True, "disabled": False}


def of(path) -> tuple[bool, bool]:
    """Whether a feature class's shapes carry z and m (its GeoPackage geometry-column flags); (False, False) for a
    table or a path grapy cannot open as one."""
    try:
        connection, table = _gpkg.table_of(str(path))
    except (FileNotFoundError, ValueError):
        return False, False
    try:
        return _gpkg.dimensions(connection, table)
    finally:
        connection.close()


def _flag(setting, same_as_input: bool) -> bool:
    """An ``outputZFlag``/``outputMFlag`` value: ``Enabled`` or ``Disabled`` (any case) decide; anything else — the
    default ``Same As Input``, ``SAME_AS_INPUT`` or a word Pro also accepts without complaint — follows the inputs."""
    return _FLAGS.get(str(setting or "").strip().lower(), same_as_input)


def output(*paths) -> tuple[bool, bool]:
    """The z and m an output written from ``paths`` carries: either if any input carries it, unless the environment
    says otherwise."""
    from ._runtime import env
    zs, ms = zip(*(of(p) for p in paths if p not in (None, ""))) if any(p not in (None, "") for p in paths) else ((), ())
    return _flag(env.outputZFlag, any(zs)), _flag(env.outputMFlag, any(ms))


def _candidates(value):
    """The feature-class paths a parameter value names: a path into a GeoPackage, a layer's source, or each of a list's
    or a ``;``-separated string's values."""
    from . import _layers
    if value is None or isinstance(value, (bool, int, float)):
        return []
    if isinstance(value, (list, tuple)):
        return [p for v in value for p in _candidates(v)]
    layer = _layers.find(value)
    if layer is not None:
        return [layer.source]
    if not isinstance(value, str):
        value = getattr(value, "dataSource", None) or getattr(value, "catalogPath", None)
        if not isinstance(value, str):
            return []
    if ";" in value:
        return [p for v in value.split(";") for p in _candidates(v.strip().strip("'\""))]
    return [value] if ".gpkg" in value.lower() else []


def from_tool() -> tuple[bool, bool]:
    """The z and m of an output the running tool writes, by the output Z and M environments: under *Same As Input*,
    whether any feature class among the tool's input parameters carries them (outside a tool, neither)."""
    from ._runtime import tool_inputs
    _, values = tool_inputs()
    paths = [p for v in values for p in _candidates(v)]
    zs, ms = [], []
    for path in paths:
        try:
            z, m = of(path)
        except Exception:  # noqa: BLE001 — a value that only looks like a path (a file that is not a GeoPackage)
            continue
        zs.append(z)
        ms.append(m)
    from ._runtime import env
    return _flag(env.outputZFlag, any(zs)), _flag(env.outputMFlag, any(ms))


# ---- restoring z and m on a cut shape --------------------------------------------------------------------------


def _linear(shape):
    """The shape's lines, rings and points as arrays of (x, y, z, m), NaN for a dimension it lacks."""
    hz, hm = bool(shapely.has_z(shape)), bool(shapely.has_m(shape))
    out = []
    for part in shapely.get_parts(shape):
        kind = part.geom_type
        if kind == "Polygon":
            pieces = [part.exterior, *part.interiors]
        elif kind in ("LineString", "LinearRing", "Point"):
            pieces = [part]
        else:
            pieces = list(shapely.get_parts(part))
        for piece in pieces:
            raw = shapely.get_coordinates(piece, include_z=hz, include_m=hm)
            full = np.full((len(raw), 4), np.nan)
            full[:, :2] = raw[:, :2]
            if hz:
                full[:, 2] = raw[:, 2]
            if hm:
                full[:, 3] = raw[:, -1]
            out.append(full)
    return out


class Sources:
    """The input shapes a cut shape's vertices take their z and m from, searched in the order given."""

    def __init__(self, shapes):
        self.shapes = [s for s in shapes if s is not None and not s.is_empty]
        # A 2D source gives nothing: a vertex on both a 2D clip polygon and a 3D input takes the 3D input's values
        # (Pro 3.7, Union of PolygonZM and a 2D polygon, measured 2026-11-20).
        self.carries = [bool(shapely.has_z(s)) or bool(shapely.has_m(s)) for s in self.shapes]
        self.arrays = [_linear(s) if c else [] for s, c in zip(self.shapes, self.carries)]
        self.tree = shapely.STRtree(self.shapes) if self.shapes else None

    def along(self, point, tolerance: float) -> list[int]:
        """The indices of the sources with z or m that pass within ``tolerance`` of ``point``."""
        if self.tree is None:
            return []
        hits = sorted(int(i) for i in self.tree.query(point, predicate="dwithin", distance=tolerance))
        return [i for i in hits if self.carries[i]]

    def value(self, x: float, y: float, tolerance: float, prefer=()) -> tuple[float, float]:
        """The z and m at (x, y) on the first source (``prefer``'s indices first, then the order given) whose line,
        ring or point lies within ``tolerance``: a vertex's own values, or the segment's interpolated linearly by 2D
        distance along it. (0, NaN) where none does."""
        if self.tree is None:
            return 0.0, math.nan
        hits = self.tree.query(shapely.box(x - tolerance, y - tolerance, x + tolerance, y + tolerance))
        order = [i for i in prefer if i in set(int(h) for h in hits)] + sorted(int(h) for h in hits if int(h) not in prefer)
        for i in order:
            if not self.carries[i]:
                continue
            best = None
            for a in self.arrays[i]:
                if len(a) == 1:
                    d = math.hypot(a[0, 0] - x, a[0, 1] - y)
                    if d <= tolerance and (best is None or d < best[0]):
                        best = (d, a[0, 2], a[0, 3])
                    continue
                p0, p1 = a[:-1], a[1:]
                d = p1[:, :2] - p0[:, :2]
                length2 = (d * d).sum(axis=1)
                with np.errstate(invalid="ignore", divide="ignore"):
                    t = np.where(length2 > 0, ((x - p0[:, 0]) * d[:, 0] + (y - p0[:, 1]) * d[:, 1]) / length2, 0.0)
                t = np.clip(t, 0.0, 1.0)
                px, py = p0[:, 0] + t * d[:, 0], p0[:, 1] + t * d[:, 1]
                dist = np.hypot(px - x, py - y)
                k = int(np.argmin(dist))
                if dist[k] <= tolerance and (best is None or dist[k] < best[0]):
                    tk = t[k]
                    z = p0[k, 2] if tk == 0 else (p1[k, 2] if tk == 1 else p0[k, 2] + tk * (p1[k, 2] - p0[k, 2]))
                    m = p0[k, 3] if tk == 0 else (p1[k, 3] if tk == 1 else p0[k, 3] + tk * (p1[k, 3] - p0[k, 3]))
                    best = (float(dist[k]), float(z), float(m))
            if best is not None:
                z = 0.0 if best[1] != best[1] else best[1]
                return z, best[2]
        return 0.0, math.nan


def tolerance_for(shape) -> float:
    """How far a cut vertex may lie from its input's segment and still be on it: a ten-billionth of the largest
    coordinate (under half a millimetre at UTM northings), far above what floating-point intersection leaves and
    under Pro's default XY tolerance of a millimetre."""
    if shape is None or shape.is_empty:
        return 1e-10
    x0, y0, x1, y1 = shape.bounds
    return 1e-10 * max(1.0, abs(x0), abs(y0), abs(x1), abs(y1))


def _number(value: float) -> str:
    return "NaN" if value != value else repr(float(value))


def carry(shape, sources, dimensions, prefer=()):
    """``restore`` for a tool's output of ``dimensions`` (has_z, has_m): ``shape`` as it is when the output is 2D,
    else with its vertices' z and m from ``sources`` (a Sources, or the shapes to make one of)."""
    has_z, has_m = dimensions
    if shape is None or not (has_z or has_m):
        return shape
    return restore(shape, sources if isinstance(sources, Sources) else Sources(sources), has_z, has_m, prefer)


def restore(shape, sources: Sources, has_z: bool, has_m: bool, prefer=()):
    """``shape`` (any dimensions; what GEOS answered for a cut) with exactly ``has_z`` and ``has_m``, each vertex's z
    and m taken from ``sources`` as the module's note says. Untouched when neither is asked."""
    if shape is None or shape.is_empty or not (has_z or has_m):
        return _gpkg.conform(shape, has_z, has_m) if shape is not None and not shape.is_empty else shape
    tolerance = tolerance_for(shape)
    tag = ("ZM" if has_m else "Z") if has_z else "M"

    def coordinates(part):
        xy = shapely.get_coordinates(part)
        order = list(prefer)
        if part.geom_type == "LineString" and len(xy) > 1:
            # A line's vertices prefer the sources the line runs along — its middle lies on them — so where two
            # input lines cross, each piece keeps its own line's z and m (Pro 3.7: Dissolve and Feature To Line of
            # two crossing PolylineZM, measured 2026-11-20).
            middle = shapely.line_interpolate_point(shapely.LineString(xy), 0.5, normalized=True)
            order += [i for i in sources.along(middle, tolerance) if i not in order]
        cells = []
        for x, y in xy:
            z, m = sources.value(float(x), float(y), tolerance, order)
            cells.append(" ".join([repr(float(x)), repr(float(y))] + ([_number(z)] if has_z else []) +
                                  ([_number(m)] if has_m else [])))
        return ", ".join(cells)

    def body(part):
        kind = part.geom_type
        if kind in ("Point", "LineString", "LinearRing"):
            return f"({coordinates(part)})"
        if kind == "Polygon":
            return "(" + ", ".join(f"({coordinates(r)})" for r in [part.exterior, *part.interiors]) + ")"
        return "(" + ", ".join(body(g) for g in part.geoms) + ")"

    kind = shape.geom_type.upper()
    if kind == "GEOMETRYCOLLECTION":
        inner = ", ".join(shapely.to_wkt(restore(g, sources, has_z, has_m, prefer)) for g in shape.geoms)
        return shapely.from_wkt(f"GEOMETRYCOLLECTION {tag} ({inner})")
    return shapely.from_wkt(f"{kind} {tag} {body(shape)}")
