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


@_equivalent("morphological closing by the half aggregation distance, less each polygon's own closing",
             "Serra, Image Analysis and Mathematical Morphology, 1982")
def AggregatePolygons(in_features, out_feature_class, aggregation_distance, minimum_area=0, minimum_hole_size=0,
                      orthogonality_option="NON_ORTHOGONAL", barrier_features=None, out_table=None, aggregate_field=None):
    """EQUIVALENT: polygons whose boundaries lie within ``aggregation_distance`` of each other are joined by the
    morphological closing of their union (Serra 1982) — dilated by half the distance, eroded by it again — so a gap
    narrower than the distance is bridged. "There will be no self-aggregation": what a polygon's own closing would fill
    (a notch narrower than the distance) is taken out again. Each connected result is an output polygon; holes smaller
    than minimum_hole_size are filled and polygons smaller than minimum_area dropped. out_table links each output to the
    inputs it holds (OUTPUT_FID, INPUT_FID); with aggregate_field only features sharing a value aggregate, and the
    value is written. INFERRED: the dilation is round (shapely's 16 segments a quarter circle).

    Refused: ORTHOGONAL (orthogonal bridging is not in this equivalent) and barrier features.
    """
    import shapely
    from shapely.geometry import MultiPolygon, Polygon
    from .analysis import _distance
    tool = "AggregatePolygons"
    if str(orthogonality_option or "NON_ORTHOGONAL").upper() in ("ORTHOGONAL", "TRUE"):
        _refuse(tool, "orthogonality_option", "ORTHOGONAL", "orthogonal bridging is not in this equivalent")
    if _given(barrier_features):
        _refuse(tool, "barrier_features", barrier_features, "barriers are not in this equivalent")
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Polygon":
        raise ValueError(f"arcpy.cartography.{tool} needs polygon features.")
    half = _distance(aggregation_distance, srid) / 2
    if half <= 0:
        raise ValueError(f"arcpy.cartography.{tool}: the aggregation distance must be greater than zero.")
    smallest = _area(minimum_area, srid) if _given(minimum_area) else 0.0
    smallest_hole = _area(minimum_hole_size, srid) if _given(minimum_hole_size) else 0.0
    field = None
    if _given(aggregate_field):
        field = {c.name.lower(): c for c in columns}[str(aggregate_field).lower()]
    with da.SearchCursor(in_features, ["OID@", field.name if field else "OID@", "SHAPE@"]) as rows:
        features = [(r[0], r[1] if field else None, r[2]._shape) for r in rows if r[2] is not None]
    groups: dict = {}
    for oid, value, shape in features:
        groups.setdefault(value, []).append((oid, shape))
    results = []
    for value, members in sorted(groups.items(), key=lambda g: (g[0] is None, g[0])):
        shapes = [s for _, s in members]
        union = shapely.union_all(shapes)
        closed = union.buffer(half).buffer(-half)
        own = shapely.union_all([s.buffer(half).buffer(-half) for s in shapes])
        bridges = closed.difference(own)
        merged = shapely.union_all([union, bridges]).buffer(0)
        for part in getattr(merged, "geoms", [merged]):
            if part.is_empty or part.geom_type != "Polygon":
                continue
            holes = [h for h in part.interiors if Polygon(h).area >= smallest_hole]
            part = Polygon(part.exterior, holes)
            if part.area < smallest:
                continue
            inside = [oid for oid, s in members if s.intersects(part) and s.intersection(part).area > 0]
            results.append((value, part, inside))
    out = _out(out_feature_class)
    _gpkg.create(out, "Polygon", srid, [field] if field else [])
    links = []
    with da.InsertCursor(out, [*([field.name] if field else []), "SHAPE@"]) as written:
        for value, part, inside in results:
            new = written.insertRow((*([value] if field else []), MultiPolygon([part])))
            links += [(new, oid) for oid in inside]
    table = ""
    if _given(out_table):
        table = _out(out_table)
        _gpkg.create(table, "Geometry", 0, [_gpkg.Column("OUTPUT_FID", "Integer"), _gpkg.Column("INPUT_FID", "Integer")])
        with da.InsertCursor(table, ["OUTPUT_FID", "INPUT_FID", "SHAPE@"]) as written:
            for new, oid in links:
                written.insertRow((new, oid, None))
    return Result(out, table)


@_equivalent("each building's ring by Douglas–Peucker at the tolerance, or its minimum-area rectangle when that lies within "
             "the tolerance of it", "Douglas and Peucker 1973; Freeman and Shapira 1975 (minimum-area rectangle)")
def SimplifyBuilding(in_features, out_feature_class, simplification_tolerance, minimum_area=0, conflict_option="NO_CHECK",
                     in_barriers=None, collapsed_point_option="NO_KEEP"):
    """EQUIVALENT (INFERRED construction): for each building part — smaller than the tolerance squared, its minimum-area
    rectangle (BLD_STATUS 3); otherwise its minimum-area rectangle when every point of the building lies within the
    tolerance of it, and the rectangle's within the tolerance of the building (Hausdorff distance ≤ tolerance;
    BLD_STATUS 2); otherwise its outline by Douglas–Peucker at the tolerance (BLD_STATUS 1), or unchanged when that
    removes no vertex (5) — the codes the page documents. Buildings smaller than minimum_area afterwards are removed,
    their centroids to ``_Pnt`` with KEEP_COLLAPSED_POINTS. CHECK_CONFLICTS adds SimBldFlag, 1 where a simplified
    building overlaps or touches another. Refused: barriers.
    """
    import shapely
    from shapely.geometry import MultiPolygon, Polygon
    from .analysis import _distance
    tool = "SimplifyBuilding"
    if _given(in_barriers):
        _refuse(tool, "in_barriers", in_barriers, "barriers are not in this equivalent")
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Polygon":
        raise ValueError(f"arcpy.cartography.{tool} needs polygon features.")
    tolerance = _distance(simplification_tolerance, srid)
    if tolerance <= 0:
        raise ValueError(f"arcpy.cartography.{tool}: the tolerance must be greater than zero.")
    smallest = _area(minimum_area, srid) if _given(minimum_area) else 0.0
    names = [c.name for c in columns]
    with da.SearchCursor(in_features, ["OID@", *names, "SHAPE@"]) as rows:
        features = [(r[0], list(r[1:-1]), r[-1]._shape) for r in rows if r[-1] is not None]
    results = []
    for oid, values, shape in features:
        parts, statuses = [], []
        for part in getattr(shape, "geoms", [shape]):
            rectangle = shapely.minimum_rotated_rectangle(part)
            if part.area < tolerance * tolerance:
                parts.append(rectangle)
                statuses.append(3)
            elif part.hausdorff_distance(rectangle) <= tolerance:
                parts.append(rectangle)
                statuses.append(2)
            else:
                outer = _douglas_peucker([tuple(c[:2]) for c in part.exterior.coords], tolerance)
                holes = [_douglas_peucker([tuple(c[:2]) for c in r.coords], tolerance) for r in part.interiors]
                simplified = Polygon(outer, [h for h in holes if len(h) >= 4]) if len(outer) >= 4 else rectangle
                unchanged = len(outer) == len(part.exterior.coords) and all(
                    len(h) == len(r.coords) for h, r in zip(holes, part.interiors))
                parts.append(part if unchanged else simplified.buffer(0))
                statuses.append(5 if unchanged else 1)
        merged = shapely.union_all(parts)
        status = min(statuses)
        results.append((oid, values, merged if merged.area >= smallest else None, status, shape))
    check = str(conflict_option or "NO_CHECK").upper() in ("CHECK_CONFLICTS", "TRUE")
    kept = [r for r in results if r[2] is not None]
    flags = {}
    if check:
        from shapely.strtree import STRtree
        shapes = [r[2] for r in kept]
        tree = STRtree(shapes)
        for i, shape in enumerate(shapes):
            flags[kept[i][0]] = int(any(int(j) != i for j in tree.query(shape, predicate="intersects")))
    extra = [_gpkg.Column("BLD_STATUS", "SmallInteger")] + ([_gpkg.Column("SimBldFlag", "SmallInteger")] if check else [])
    plain = [c for c in columns if c.name.upper() not in ("BLD_STATUS", "SIMBLDFLAG")]
    out = _out(out_feature_class)
    _gpkg.create(out, "Polygon", srid, [*plain, *extra])
    keep_index = [names.index(c.name) for c in plain]
    with da.InsertCursor(out, [*[c.name for c in plain], *[c.name for c in extra], "SHAPE@"]) as written:
        for oid, values, shape, status, _ in kept:
            row = [values[i] for i in keep_index] + [status] + ([flags[oid]] if check else [])
            written.insertRow((*row, shape if shape.geom_type == "MultiPolygon" else MultiPolygon([shape])))
    points = ""
    if str(collapsed_point_option or "NO_KEEP").upper() in ("KEEP_COLLAPSED_POINTS", "TRUE"):
        points = _beside(out, "_Pnt")
        _gpkg.create(points, "Point", srid, [_gpkg.Column("InBld_FID", "Integer")])
        with da.InsertCursor(points, ["InBld_FID", "SHAPE@"]) as written:
            for oid, _, shape, _, original in results:
                if shape is None:
                    written.insertRow((oid, original.centroid))
    return Result(out, points)


@_equivalent("the medial axis between a pair of casings: the Voronoi diagram of their densified vertices, its edges "
             "between one casing's points and the other's, inside the pair's polygon",
             "Lee 1982, Medial axis transformation of a planar shape; Voronoi by Qhull")
def CollapseDualLinesToCenterline(in_features, out_feature_class, maximum_width, minimum_width=0):
    """EQUIVALENT (INFERRED construction): each open line is paired with the nearest other open line within
    maximum_width (ties by object id; each line in one pair). The pair's polygon is closed by joining their ends; their
    vertices are densified to a tenth of maximum_width; the Voronoi edges whose two generating points lie one on each
    casing, and which fall inside the polygon, are the medial axis (Lee 1982). Edges whose casings are closer than
    minimum_width or farther than maximum_width apart are dropped. The pieces are merged into the centerline, oriented
    as the first casing runs, with LnType 1 and the casings' ids in LeftLn_FID and RightLn_FID (left of the
    centerline's direction). Lines left unpaired, and closed lines, are copied with LnType 2 and ids 0, as the page
    says for "broken or unpaired lines". LnType 3 (unclear intersections) is not produced.
    """
    import numpy as np
    import shapely
    from scipy.spatial import Voronoi
    from shapely.geometry import LineString, Polygon
    from shapely.ops import linemerge
    from .analysis import _distance
    tool = "CollapseDualLinesToCenterline"
    columns, kind, srid, _ = _schema(in_features)
    if kind != "Polyline":
        raise ValueError(f"arcpy.cartography.{tool} needs line features.")
    widest = _distance(maximum_width, srid)
    narrowest = _distance(minimum_width, srid) if _given(minimum_width) else 0.0
    if widest <= 0 or narrowest < 0 or narrowest >= widest:
        raise ValueError(f"arcpy.cartography.{tool}: 0 ≤ minimum_width < maximum_width, and maximum_width > 0.")
    with da.SearchCursor(in_features, ["OID@", "SHAPE@"]) as rows:
        lines = [(r[0], linemerge(r[1]._shape) if r[1]._shape.geom_type == "MultiLineString" else r[1]._shape)
                 for r in rows if r[1] is not None]
    open_lines = [(oid, s) for oid, s in lines if s.geom_type == "LineString" and not s.is_closed]
    paired, used = [], set()
    for oid, shape in sorted(open_lines):
        if oid in used:
            continue
        candidates = sorted((shape.distance(other), other_oid, other) for other_oid, other in open_lines
                            if other_oid != oid and other_oid not in used and shape.distance(other) <= widest)
        if candidates:
            _, other_oid, other = candidates[0]
            used.update({oid, other_oid})
            paired.append((oid, shape, other_oid, other))
    out = _out(out_feature_class)
    fields = [_gpkg.Column("LnType", "SmallInteger"), _gpkg.Column("LeftLn_FID", "Integer"),
              _gpkg.Column("RightLn_FID", "Integer")]
    _gpkg.create(out, "Polyline", srid, fields)
    step = widest / 10
    with da.InsertCursor(out, ["LnType", "LeftLn_FID", "RightLn_FID", "SHAPE@"]) as written:
        for a_oid, a, b_oid, b in paired:
            other = b if a.distance(shapely.Point(b.coords[0])) <= a.distance(shapely.Point(b.coords[-1])) else shapely.reverse(b)
            polygon = Polygon([*a.coords, *reversed(other.coords)]).buffer(0)
            a_points = np.array(shapely.segmentize(a, step).coords)[:, :2]
            b_points = np.array(shapely.segmentize(b, step).coords)[:, :2]
            points = np.vstack([a_points, b_points])
            side = np.array([0] * len(a_points) + [1] * len(b_points))
            diagram = Voronoi(points)
            pieces = []
            for (p, q), ridge in zip(diagram.ridge_points, diagram.ridge_vertices):
                if side[p] == side[q] or -1 in ridge:
                    continue
                width = float(np.hypot(*(points[p] - points[q])))
                if width < narrowest or width > widest:
                    continue
                edge = LineString(diagram.vertices[ridge])
                if polygon.contains(edge.centroid):
                    pieces.append(edge)
            if not pieces:
                continue
            axis = linemerge(shapely.union_all(pieces))
            for line in getattr(axis, "geoms", [axis]):
                start, end = a.project(shapely.Point(line.coords[0])), a.project(shapely.Point(line.coords[-1]))
                if end < start:
                    line = shapely.reverse(line)
                (x0, y0), (x1, y1) = line.coords[0][:2], line.coords[-1][:2]
                mid = b.interpolate(0.5, normalized=True)
                left_is_b = (x1 - x0) * (mid.y - y0) - (y1 - y0) * (mid.x - x0) > 0
                written.insertRow((1, b_oid if left_is_b else a_oid, a_oid if left_is_b else b_oid, line))
        for oid, shape in lines:
            if oid not in used:
                written.insertRow((2, 0, 0, shape))
    return Result(out)


_REFUSED = {
    "SmoothLine": "PAEK's kernel is described by its paper, not specified, and Bezier interpolation writes true curves a "
                  "GeoPackage cannot hold",
    "SmoothPolygon": "PAEK's kernel is described by its paper, not specified, and Bezier interpolation writes true curves "
                     "a GeoPackage cannot hold",
    "SmoothSharedEdges": "PAEK's kernel is described by its paper, not specified",
    "SimplifySharedEdges": "it keeps shared edges by the topology procedure the documentation does not specify",
    "AggregatePoints": "the shape of the aggregation polygon is described only in outline",
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
