"""arcpy.cartography — the generalization tools grapy builds, by their documented names and parameters (ADR-224).

Only what "How Simplify Line and Simplify Polygon work" defines well enough to reproduce is built. Esri's tools keep
topology while they simplify — where a simplification would make a line cross itself or another, they "first try to
split the geometry into two parts, and simplify them independently", recursively, then put the feature "in waiting".
That procedure is described, not specified, so grapy does not imitate it: it simplifies, checks that no relationship
between the lines changed, and refuses the run by name when one did. An answer is either the documented algorithm's or
a refusal, never a topology the tool would not have written.

Refused, with the reason, by ``__getattr__``: the smoothing tools (PAEK's kernel and Bezier interpolation's curves are
described by their papers, not specified, and a GeoPackage cannot hold a Bezier curve), and the conflict and
aggregation tools whose procedures are described only in outline.
"""
from __future__ import annotations

import os

from . import _gpkg, da
from ._result import Result
from ._runtime import NotBuiltError


def _out(path):
    from .analysis import _out as out
    return out(path)


def _schema(path):
    from .analysis import _schema as schema
    return schema(path)


def _list(value):
    from .management import _list as listed
    return listed(value)


def _refuse(tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.cartography.{tool}: {parameter} '{value}' is not built in grapy — {why}.")


def _given(value):
    if isinstance(value, (list, tuple)):
        return any(_given(v) for v in value)
    return value not in (None, "", "#")


def _beside(path, suffix):
    """The derived feature class next to ``path`` with ``suffix`` on its name: ``roads.gpkg`` → ``roads_Pnt.gpkg``,
    ``data.gpkg/roads`` → ``data.gpkg/roads_Pnt``."""
    file, table = _gpkg.split(path)
    if table:
        return f"{file}/{table}{suffix}"
    stem, extension = os.path.splitext(file)
    return f"{stem}{suffix}{extension}"


# ---- the algorithms -----------------------------------------------------------------------------------------------

def _segment_distance(p, a, b):
    ax, ay = a
    bx, by = b
    px, py = p
    dx, dy = bx - ax, by - ay
    length = dx * dx + dy * dy
    if length == 0:
        return ((px - ax) ** 2 + (py - ay) ** 2) ** 0.5
    t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / length))
    return ((px - ax - t * dx) ** 2 + (py - ay - t * dy) ** 2) ** 0.5


def _douglas_peucker(points, tolerance):
    """The vertices Douglas and Peucker (1973) keep: the endpoints, then — between two kept vertices — the one farthest
    from the line joining them, as long as it is not closer than the tolerance."""
    keep = [False] * len(points)
    keep[0] = keep[-1] = True
    stack = [(0, len(points) - 1)]
    while stack:
        first, last = stack.pop()
        far, at = -1.0, None
        for k in range(first + 1, last):
            d = _segment_distance(points[k], points[first], points[last])
            if d > far:
                far, at = d, k
        if at is not None and far >= tolerance:
            keep[at] = True
            stack.append((first, at))
            stack.append((at, last))
    return [p for p, kept in zip(points, keep) if kept]


def _triangle(a, b, c):
    return abs((b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])) / 2


def _visvalingam_whyatt(points, tolerance):
    """Visvalingam and Whyatt (1992): the interior vertex with the smallest triangle with its two neighbours is removed
    while that triangle is smaller than the tolerance squared, and its neighbours' triangles are recomputed."""
    alive = list(range(len(points)))
    threshold = tolerance * tolerance
    while len(alive) > 2:
        areas = [(_triangle(points[alive[k - 1]], points[alive[k]], points[alive[k + 1]]), k)
                 for k in range(1, len(alive) - 1)]
        area, k = min(areas)
        if area >= threshold:
            break
        del alive[k]
    return [points[i] for i in alive]


_ALGORITHMS = {"POINT_REMOVE": _douglas_peucker, "EFFECTIVE_AREA": _visvalingam_whyatt}


def _algorithm(tool, value):
    key = str(value).upper()
    if key in ("BEND_SIMPLIFY", "WEIGHTED_AREA"):
        _refuse(tool, "algorithm", key, "Wang-Müller's bend measures and Zhou-Jones's weights are described, not "
                "specified, in the documentation")
    if key not in _ALGORITHMS:
        raise ValueError(f"arcpy.cartography.{tool}: algorithm '{value}' is not one of POINT_REMOVE, BEND_SIMPLIFY, "
                         "WEIGHTED_AREA, EFFECTIVE_AREA.")
    return _ALGORITHMS[key]


def _vertices_of(shape):
    """Each vertex of a line or multiline, as (x, y) — the set an intersection point is checked against."""
    parts = getattr(shape, "geoms", [shape])
    return {tuple(c[:2]) for part in parts for c in part.coords}


def _input_errors(lines):
    """SimLnFlag for each input line: 1 when it crosses itself, or meets another line along a stretch or at a point
    that is not a vertex of both."""
    from shapely.strtree import STRtree
    flags = [0 if line.is_simple else 1 for line in lines]
    tree = STRtree(lines)
    for i, line in enumerate(lines):
        for j in tree.query(line, predicate="intersects"):
            j = int(j)
            if j == i:
                continue
            met = line.intersection(lines[j])
            pieces = getattr(met, "geoms", [met])
            if any(p.geom_type != "Point" for p in pieces) or any(
                    (p.x, p.y) not in _vertices_of(line) or (p.x, p.y) not in _vertices_of(lines[j]) for p in pieces):
                flags[i] = 1
    return flags


def _topology_kept(before, after):
    """Whether simplifying changed no relationship: every line as simple as it was, and every pair meeting where it
    met before and nowhere else."""
    from shapely.strtree import STRtree
    for b, a in zip(before, after):
        if a is None:
            continue
        if b.is_simple and not a.is_simple:
            return False
        if not b.is_simple and not a.equals(b):
            return False
    kept = [(i, a) for i, a in enumerate(after) if a is not None]
    shapes = [a for _, a in kept]
    tree_after = STRtree(shapes) if shapes else None
    tree_before = STRtree(before) if before else None
    pairs = set()
    for i, shape in enumerate(before):
        pairs.update((min(i, int(j)), max(i, int(j))) for j in tree_before.query(shape, predicate="intersects") if int(j) != i)
    for k, (i, shape) in enumerate(kept):
        pairs.update((min(i, kept[int(j)][0]), max(i, kept[int(j)][0]))
                     for j in tree_after.query(shape, predicate="intersects") if int(j) != k)
    for i, j in pairs:
        if after[i] is None or after[j] is None:
            if before[i].intersects(before[j]):
                return False
            continue
        old, new = before[i].intersection(before[j]), after[i].intersection(after[j])
        if not (old.is_empty and new.is_empty) and not old.equals(new):
            return False
    return True


# ---- tools ------------------------------------------------------------------------------------------------------

def SimplifyLine(in_features, out_feature_class, algorithm="POINT_REMOVE", tolerance=None, error_resolving_option=None,
                 collapsed_point_option="KEEP_COLLAPSED_POINTS", error_checking_option=None, in_barriers=None,
                 error_option="NO_CHECK"):
    """Lines with their insignificant vertices removed, from "How Simplify Line and Simplify Polygon work".

    POINT_REMOVE is Douglas-Peucker: "the tolerance is the maximum allowable perpendicular distance between each vertex
    and the new line created", and "vertices closer to the line than the tolerance are eliminated". EFFECTIVE_AREA is
    Visvalingam-Whyatt: "the square of the tolerance is the area of a significant triangle", and the triangles are
    recomputed after each removal. BEND_SIMPLIFY and WEIGHTED_AREA are refused, as are barriers. Each part of a
    multipart line is simplified on its own; endpoints are kept.

    The output has the input's fields, then InLine_FID, SimLnFlag, MinSimpTol and MaxSimpTol (both the tolerance). A
    line simplified to zero length is left out, and with KEEP_COLLAPSED_POINTS its endpoint is written to the
    ``_Pnt`` feature class beside the output. Outputs, in order: the output, the ``_Pnt`` feature class (or empty).

    A run whose simplification would change how two lines meet, or make a line cross itself, is refused: the tool
    keeps topology by a recursive splitting the documentation does not specify. RESOLVE_ERRORS is refused when the
    input has topological errors (it inserts vertices at them); otherwise the three error options answer alike,
    because the tool "no longer introduce[s] topological errors".

    INFERRED: the distance is to the segment joining the kept vertices ("the new line created"), and a vertex exactly
    at the tolerance is kept. INFERRED: among equal smallest triangles the earlier vertex goes first. INFERRED:
    SimLnFlag is 1 for a line that crosses itself or meets another along a stretch or at a point that is not a vertex
    of both. INFERRED: the ``_Pnt`` feature class carries InLine_FID, and is written (perhaps empty) whenever asked for.
    """
    import shapely
    from shapely.geometry import LineString, MultiLineString
    from .analysis import _distance
    if _given(in_barriers):
        _refuse("SimplifyLine", "in_barriers", in_barriers, "keeping lines off barriers needs the topology-keeping "
                "procedure the documentation does not specify")
    simplify = _algorithm("SimplifyLine", algorithm)
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Polyline":
        raise ValueError("arcpy.cartography.SimplifyLine needs line features.")
    if not _given(tolerance):
        raise ValueError("arcpy.cartography.SimplifyLine needs a tolerance.")
    limit = _distance(tolerance, srid)
    names = [c.name for c in columns]
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows:
        features = [(r[0], list(r[1:-1]), r[-1]._shape) for r in rows if r[-1] is not None]
    before = [f[2] for f in features]
    flags = _input_errors(before) if before else []
    if str(error_option).upper() == "RESOLVE_ERRORS" and any(flags):
        _refuse("SimplifyLine", "error_option", "RESOLVE_ERRORS", "the input has topological errors, and how vertices are "
                "inserted at them is not documented")
    after = []
    for _, _, shape in features:
        parts = []
        for part in getattr(shape, "geoms", [shape]):
            simplified = simplify([tuple(c[:2]) for c in part.coords], limit)
            parts.append(simplified)
        if all(LineString(p).length == 0 for p in parts):
            after.append(None)
            continue
        kept = [LineString(p) for p in parts if LineString(p).length > 0]
        after.append(kept[0] if len(kept) == 1 and shape.geom_type == "LineString" else MultiLineString(kept))
    if not _topology_kept(before, after):
        _refuse("SimplifyLine", "tolerance", tolerance, "at this tolerance the simplified lines would meet differently "
                "or cross themselves, and the tool's topology-keeping procedure is not specified")
    out = _out(out_feature_class)
    extra = [_gpkg.Column("InLine_FID", "Integer"), _gpkg.Column("SimLnFlag", "SmallInteger"),
             _gpkg.Column("MinSimpTol", "Double"), _gpkg.Column("MaxSimpTol", "Double")]
    _gpkg.create(out, "Polyline", srid, [*columns, *extra])
    collapsed = []
    with da.InsertCursor(out, [*names, *[c.name for c in extra], "SHAPE@"]) as written:
        for (oid, values, shape), simplified, flag in zip(features, after, flags):
            if simplified is None:
                ends = {tuple(shape.coords[0][:2]) if shape.geom_type == "LineString" else tuple(shape.geoms[0].coords[0][:2])}
                last = shape.coords[-1] if shape.geom_type == "LineString" else shape.geoms[-1].coords[-1]
                ends.add(tuple(last[:2]))
                collapsed.extend((oid, point) for point in sorted(ends))
                continue
            written.insertRow((*values, oid, flag, limit, limit, simplified))
    points = ""
    if str(collapsed_point_option).upper() in ("KEEP_COLLAPSED_POINTS", "TRUE"):
        points = _beside(out, "_Pnt")
        _gpkg.create(points, "Point", srid, [_gpkg.Column("InLine_FID", "Integer")])
        with da.InsertCursor(points, ["InLine_FID", "SHAPE@"]) as written:
            for oid, point in collapsed:
                written.insertRow((oid, shapely.Point(point)))
    return Result(out, points)


from ._runtime import equivalent as _equivalent  # noqa: E402


@_equivalent("Douglas–Peucker or Visvalingam–Whyatt on each ring, the ring opened at its first stored vertex",
             "Douglas and Peucker 1973; Visvalingam and Whyatt 1992")
def SimplifyPolygon(in_features, out_feature_class, algorithm="POINT_REMOVE", tolerance=None, minimum_area=0,
                    error_option="NO_CHECK", collapsed_point_option="KEEP_COLLAPSED_POINTS", in_barriers=None):
    """EQUIVALENT: each ring simplified as SimplifyLine simplifies a line — POINT_REMOVE Douglas and Peucker (1973),
    EFFECTIVE_AREA Visvalingam and Whyatt (1992) — the ring opened at its first stored vertex, which is kept. Where a
    ring's simplification starts is not documented, and the answer depends on it: that is what makes this an
    equivalent. "Multipart polygons are simplified as individual parts." A hole simplified to less than a triangle is
    dropped; a polygon whose outer ring is, or whose area falls below minimum_area, is removed, and with
    KEEP_COLLAPSED_POINTS its centroid goes to the ``_Pnt`` feature class with InPoly_FID. INFERRED: the minimum area
    applies to each polygon on its own — polygons that share edges are refused below, so no group of adjacent polygons
    reaches the rule that applies the area to the group.

    The output has the input's fields, InPoly_FID, SimPgnFlag (1 for an input polygon that is not valid), MinSimpTol
    and MaxSimpTol. As SimplifyLine, a run that would change how outlines meet — polygons that share an edge or touch,
    whose simplified outlines would no longer coincide — or make one cross itself is refused, since Esri's
    topology-keeping procedure is not specified. Refused: BEND_SIMPLIFY, WEIGHTED_AREA, barriers, RESOLVE_ERRORS on an
    input with errors.
    """
    import shapely
    from shapely.geometry import MultiPolygon, Polygon
    from .analysis import _distance
    tool = "SimplifyPolygon"
    if _given(in_barriers):
        _refuse(tool, "in_barriers", in_barriers, "keeping outlines off barriers needs the topology-keeping procedure the "
                "documentation does not specify")
    simplify = _algorithm(tool, algorithm)
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Polygon":
        raise ValueError(f"arcpy.cartography.{tool} needs polygon features.")
    if not _given(tolerance):
        raise ValueError(f"arcpy.cartography.{tool} needs a tolerance.")
    limit = _distance(tolerance, srid)
    smallest = _area(minimum_area, srid) if _given(minimum_area) else 0.0
    names = [c.name for c in columns]
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows:
        features = [(r[0], list(r[1:-1]), r[-1]._shape) for r in rows if r[-1] is not None]
    flags = [0 if f[2].is_valid else 1 for f in features]
    if str(error_option).upper() == "RESOLVE_ERRORS" and any(flags):
        _refuse(tool, "error_option", "RESOLVE_ERRORS", "the input has topological errors, and how they are resolved is "
                "not documented")

    def ring(coordinates):
        kept = simplify([tuple(c[:2]) for c in coordinates], limit)
        return kept if len(kept) >= 4 and Polygon(kept).area > 0 else None

    after = []
    for _, _, shape in features:
        parts = []
        for part in getattr(shape, "geoms", [shape]):
            outer = ring(part.exterior.coords)
            if outer is None:
                continue
            holes = [h for h in (ring(r.coords) for r in part.interiors) if h is not None]
            parts.append(Polygon(outer, holes))
        kept = [p for p in parts if p.area > 0]
        result = MultiPolygon(kept) if kept else None
        if result is not None and smallest > 0 and result.area < smallest:
            result = None
        after.append(result)
    before_lines = [f[2].boundary for f in features]
    after_lines = [a.boundary if a is not None else None for a in after]
    if not _topology_kept(before_lines, after_lines) or any(a is not None and not a.is_valid for a in after):
        _refuse(tool, "tolerance", tolerance, "at this tolerance the simplified outlines would meet differently or cross "
                "themselves, and the tool's topology-keeping procedure is not specified")
    out = _out(out_feature_class)
    extra = [_gpkg.Column("InPoly_FID", "Integer"), _gpkg.Column("SimPgnFlag", "SmallInteger"),
             _gpkg.Column("MinSimpTol", "Double"), _gpkg.Column("MaxSimpTol", "Double")]
    _gpkg.create(out, "Polygon", srid, [*columns, *extra])
    collapsed = []
    with da.InsertCursor(out, [*names, *[c.name for c in extra], "SHAPE@"]) as written:
        for (oid, values, shape), simplified, flag in zip(features, after, flags):
            if simplified is None:
                collapsed.append((oid, shape.centroid))
                continue
            written.insertRow((*values, oid, flag, limit, limit, simplified))
    points = ""
    if str(collapsed_point_option).upper() in ("KEEP_COLLAPSED_POINTS", "TRUE"):
        points = _beside(out, "_Pnt")
        _gpkg.create(points, "Point", srid, [_gpkg.Column("InPoly_FID", "Integer")])
        with da.InsertCursor(points, ["InPoly_FID", "SHAPE@"]) as written:
            for oid, point in collapsed:
                written.insertRow((oid, point))
    return Result(out, points)


def _area(value, srid):
    """An areal unit such as ``"100 SquareMeters"`` in the data's units squared, or a number as it is."""
    from ._geometry import _AREAL, _unit
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    number = float(parts[0])
    if len(parts) == 1:
        return number
    from ._reference import SpatialReference
    per = SpatialReference(srid).metersPerUnit if srid > 0 else 1.0
    return number * _unit(_AREAL, parts[1], "cartography.SimplifyPolygon") / per ** 2


_REFUSED = {
    "SmoothLine": "PAEK's kernel is described by its paper, not specified, and Bezier interpolation writes true curves a "
                  "GeoPackage cannot hold",
    "SmoothPolygon": "PAEK's kernel is described by its paper, not specified, and Bezier interpolation writes true curves "
                     "a GeoPackage cannot hold",
    "SmoothSharedEdges": "PAEK's kernel is described by its paper, not specified",
    "SimplifySharedEdges": "it keeps shared edges by the topology procedure the documentation does not specify",
    "SimplifyBuilding": "its building-simplification procedure is described only in outline",
    "AggregatePolygons": "how gaps are bridged and orthogonal shapes kept is described only in outline",
    "AggregatePoints": "the shape of the aggregation polygon is described only in outline",
    "CollapseDualLinesToCenterline": "how the centerline is derived is described only in outline",
    "DelineateBuiltUpAreas": "its grouping procedure is described only in outline",
    "CalculatePolygonMainAngle": "what makes segments' orientations 'similar' is not defined",
}
_MAP = "it works on symbolized layers, labels or annotation in an ArcGIS Pro map — symbols at a reference scale — " \
       "which a run on this server does not have"
_OUTLINE = "its procedure is described only in outline, not specified closely enough to reproduce"
_PE_STRING = "it writes an Esri projection-engine string whose exact text grapy cannot reproduce, from the map's " \
             "coordinate system, which a server run does not have"
_REFUSED.update({name: _MAP for name in (
    "AnnotateSelectedFeatures", "ContourAnnotation", "ConvertLabelsToAnnotation", "ConvertLabelsToGraphics",
    "TiledLabelsToAnnotation", "UpdateAnnotationReferenceScale", "AlignMarkerToStrokeOrFill", "CalculateLineCaps",
    "ConvertMarkerPlacementToPoints", "CreateOverpass", "CreateUnderpass", "DisperseMarkers", "DetectGraphicConflict",
    "ResolveBuildingConflicts", "ResolveRoadConflicts", "PropagateDisplacement", "CulDeSacMasks", "FeatureOutlineMasks",
    "IntersectingLayersMasks")})
_REFUSED.update({name: "control points are vertex flags of a geodatabase geometry, which a GeoPackage geometry does "
                       "not carry" for name in ("ConvertControlPointsToVertices", "SetControlPointAtIntersect",
                                                "SetControlPointByAngle")})
_REFUSED.update({name: _OUTLINE for name in (
    "CollapseHydroPolygon", "CollapseRoadDetail", "MergeDividedRoads", "ThinRoadNetwork", "CreateCartographicPartitions",
    "GenerateContiguousCartogram", "GenerateHachuresForDefinedSlopes", "CalculateColorTheoremField",
    "StripMapIndexFeatures")})
_REFUSED.update({name: _PE_STRING for name in ("CalculateUTMZone", "CalculateCentralMeridianAndParallels")})
_REFUSED.update({
    "CalculateGridConvergenceAngle": "with no coordinate system field it reads the Cartographic Coordinate System "
                                     "environment of an ArcGIS Pro session, and a field holds an Esri projection-engine "
                                     "string grapy does not parse",
    "CalculateAdjacentFields": "how a neighbouring page's direction is decided is not documented",
    "GridIndexFeatures": "its PageName labelling scheme is not documented, and page units need a map frame",
    "MapServerCacheTilingSchemeToPolygons": "its first parameter is the current ArcGIS Pro map, whose extent it uses, "
                                            "and a run on this server has no map",
})


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        why = _REFUSED.get(name)
        raise NotBuiltError(f"arcpy.cartography.{name} is not built in grapy" + (f" — {why}." if why else " yet."))

    return missing
