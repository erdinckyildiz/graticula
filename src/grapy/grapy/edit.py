"""arcpy.edit's tools grapy builds, by their documented names and parameters — ADR-223 §5.2, ADR-224.

Every tool here changes its input in place, as arcpy.edit's do, and answers a Result whose first output is the input.
A feature class holds no true curves (ADR-223 §5.5), so the parameters that act only on curve segments change nothing
here; a parameter whose effect grapy cannot reproduce as documented is refused by name.

Not built, each refused by name with its reason in ``__getattr__`` below: the conflation tools, whose matching is
documented only in words; the tools whose output is true circular arcs; and COGO, which needs a data model.
"""
from __future__ import annotations

import math
import os

import numpy
import shapely
from shapely.geometry import LineString, MultiLineString, MultiPoint, MultiPolygon, Point, Polygon

from . import _gpkg, da
from ._result import Result
from ._runtime import AddMessage, ExecuteError, NotBuiltError
from .analysis import _distance
from .management import _list, _pairs, _schema


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.edit.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _single_line(shape, tool):
    """A line as one LineString; a line of several parts is refused, since the tools that need ends do not say which."""
    if shape.geom_type == "LineString":
        return shape
    if shape.geom_type == "MultiLineString" and len(shape.geoms) == 1:
        return shape.geoms[0]
    raise NotBuiltError(f"arcpy.edit.{tool} on a multipart line is not built in grapy: the documentation does not say "
                        "which part's ends are dangles.")


def _length(value, srid, tool, parameter):
    if value in (None, "", "#"):
        return None
    try:
        return _distance(value, srid)
    except ValueError as error:
        _refuse(tool, parameter, value, str(error))


def _same(a, b):
    return os.path.normcase(os.path.abspath(os.fspath(a))) == os.path.normcase(os.path.abspath(os.fspath(b)))


# ---- Densify, Generalize, FlipLine


def Densify(in_features, densification_method="DISTANCE", distance=None, max_deviation=None, max_angle=None,
            max_vertex_per_segment=None):
    """Vertices added to every segment of the lines or polygons so that none is longer than ``distance``; the original
    vertices are kept.

    INFERRED: the documentation says only that the interval *does not exceed* the distance and *may not be* exact.
    grapy divides each segment into the fewest equal pieces no longer than the distance — a 100-long segment at a
    distance of 30 becomes four of 25. ``OFFSET`` and ``ANGLE`` act only on curves, which a feature class here does
    not hold, so straight segments are densified by distance under every method, as the documentation says they are.
    """
    method = str(densification_method or "DISTANCE").upper()
    if method not in ("DISTANCE", "OFFSET", "ANGLE"):
        _refuse("Densify", "densification_method", densification_method)
    _, kind, srid, _ = _schema(in_features)
    if kind not in ("Polyline", "Polygon"):
        raise ExecuteError(f"ERROR 000365: Densify takes lines or polygons; {in_features} holds {kind}.")
    step = _length(distance, srid, "Densify", "distance")
    if step is None:
        _refuse("Densify", "distance", "(empty)", "its default is a function of an x,y tolerance grapy does not hold")
    if step <= 0:
        raise ExecuteError("ERROR 000859: Densify's distance must be greater than zero.")
    if max_vertex_per_segment not in (None, "", "#") and int(max_vertex_per_segment) > 0:
        _refuse("Densify", "max_vertex_per_segment", max_vertex_per_segment,
                "the documentation allows the count to be met within one either way and does not say how")
    with da.UpdateCursor(in_features, ["SHAPE@"]) as rows:
        for row in rows:
            if row[0] is not None:
                rows.updateRow([shapely.segmentize(row[0]._shape, step)])
    return Result(in_features)


def _douglas_peucker(points, tolerance):
    """The indices Douglas-Peucker keeps: a vertex stays when it lies farther than the tolerance from the segment
    between the vertices kept either side of it."""
    keep = {0, len(points) - 1}
    stack = [(0, len(points) - 1)]
    while stack:
        first, last = stack.pop()
        if last - first < 2:
            continue
        chord = LineString([points[first], points[last]]) if points[first] != points[last] else Point(points[first])
        far, at = -1.0, None
        for i in range(first + 1, last):
            gap = chord.distance(Point(points[i]))
            if gap > far:
                far, at = gap, i
        if far > tolerance:
            keep.add(at)
            stack += [(first, at), (at, last)]
    return sorted(keep)


def _generalized_ring(ring, tolerance):
    points = list(ring.coords)
    kept = [points[i] for i in _douglas_peucker(points, tolerance)]
    if len(kept) >= 4:
        return kept
    # Fewer than three distinct vertices: the documentation says a polygon is reduced to three, not removed. The
    # first vertex, the one farthest from it, and the one farthest from the segment between them.
    first = points[0]
    far = max(points[:-1], key=lambda p: Point(p).distance(Point(first)))
    chord = LineString([first, far])
    third = max((p for p in points[:-1] if p not in (first, far)), key=lambda p: chord.distance(Point(p)))
    order = sorted([first, far, third], key=points.index)
    return [*order, order[0]]


def Generalize(in_features, tolerance=None):
    """Lines and polygons simplified by Douglas-Peucker with ``tolerance`` as the largest offset, record by record.

    INFERRED: the distance from a vertex to the segment between the vertices kept either side (not to that segment's
    infinite line), and a vertex exactly at the tolerance is removed. A ring that would keep fewer than three vertices
    keeps three — its first, the one farthest from the first, and the one farthest from the segment between those two
    — because the documentation says a polygon is reduced to three vertices rather than deleted. Each part of a
    multipart feature is generalized on its own.
    """
    _, kind, srid, _ = _schema(in_features)
    if kind not in ("Polyline", "Polygon"):
        raise ExecuteError(f"ERROR 000365: Generalize takes lines or polygons; {in_features} holds {kind}.")
    offset = _length(tolerance, srid, "Generalize", "tolerance")
    if offset is None:
        _refuse("Generalize", "tolerance", "(empty)", "its default is not documented")

    def simplified(shape):
        if isinstance(shape, LineString):
            points = list(shape.coords)
            return LineString([points[i] for i in _douglas_peucker(points, offset)])
        if isinstance(shape, Polygon):
            return Polygon(_generalized_ring(shape.exterior, offset), [_generalized_ring(r, offset) for r in shape.interiors])
        if isinstance(shape, MultiLineString):
            return MultiLineString([simplified(p) for p in shape.geoms])
        if isinstance(shape, MultiPolygon):
            return MultiPolygon([simplified(p) for p in shape.geoms])
        return shape

    with da.UpdateCursor(in_features, ["SHAPE@"]) as rows:
        for row in rows:
            if row[0] is not None:
                rows.updateRow([simplified(row[0]._shape)])
    return Result(in_features)


def FlipLine(in_features):
    """Each line's direction reversed: its vertices in the opposite order. Attributes are not changed.

    INFERRED: in a multipart line each part is reversed and the parts keep their order; the documentation does not
    speak of parts.
    """
    _, kind, _, _ = _schema(in_features)
    if kind != "Polyline":
        raise ExecuteError(f"ERROR 000365: FlipLine takes lines; {in_features} holds {kind}.")
    with da.UpdateCursor(in_features, ["SHAPE@"]) as rows:
        for row in rows:
            if row[0] is None:
                continue
            shape = row[0]._shape
            if isinstance(shape, MultiLineString):
                flipped = MultiLineString([list(p.coords)[::-1] for p in shape.geoms])
            else:
                flipped = LineString(list(shape.coords)[::-1])
            rows.updateRow([flipped])
    return Result(in_features)


# ---- ErasePoint


def ErasePoint(in_features, remove_features, operation_type="INSIDE"):
    """Points deleted from the input: those inside or on the boundary of the remove polygons (``INSIDE``), or those
    outside them (``OUTSIDE``). Of a multipoint, only the points that qualify are removed.

    INFERRED: the default is ``INSIDE``; the documentation lists the two values without marking one. A multipoint left
    with no points is deleted.
    """
    operation = str(operation_type or "INSIDE").upper()
    if operation not in ("INSIDE", "OUTSIDE"):
        _refuse("ErasePoint", "operation_type", operation_type)
    _, kind, srid, _ = _schema(in_features)
    if kind not in ("Point", "Multipoint"):
        raise ExecuteError(f"ERROR 000365: ErasePoint takes points; {in_features} holds {kind}.")
    from .analysis import _read
    mask = shapely.union_all([s for _, _, s in _read(remove_features, srid)])

    def erased(point):
        inside = mask.intersects(point)
        return inside if operation == "INSIDE" else not inside

    with da.UpdateCursor(in_features, ["SHAPE@"]) as rows:
        for row in rows:
            if row[0] is None:
                continue
            shape = row[0]._shape
            if isinstance(shape, MultiPoint):
                kept = [p for p in shape.geoms if not erased(p)]
                if not kept:
                    rows.deleteRow()
                elif len(kept) != len(shape.geoms):
                    rows.updateRow([MultiPoint(kept)])
            elif erased(shape):
                rows.deleteRow()
    return Result(in_features)


# ---- TrimLine and ExtendLine


def _lines(in_features, tool):
    _, kind, srid, _ = _schema(in_features)
    if kind != "Polyline":
        raise ExecuteError(f"ERROR 000365: {tool} takes lines; {in_features} holds {kind}.")
    with da.SearchCursor(in_features, ["OID@", "SHAPE@"]) as rows:
        features = [(r[0], _single_line(r[1]._shape, tool)) for r in rows if r[1] is not None]
    return features, srid


def _positions(line, others):
    """Where along ``line`` the other lines touch or cross it, as distances from its start."""
    if others.is_empty:
        return []
    met = line.intersection(others)
    points = []
    for part in getattr(met, "geoms", [met]):
        if part.is_empty:
            continue
        if part.geom_type == "Point":
            points.append(part)
        else:
            points += [Point(c) for c in part.coords]
    return sorted(line.project(p) for p in points)


def TrimLine(in_features, dangle_length=None, delete_shorts="DELETE_SHORT"):
    """Dangles cut back: the part of a line between its free end and the nearest place it touches or crosses another
    line, when that part is no longer than ``dangle_length`` (every dangle when no length is given). A line that touches
    no other line is deleted under ``DELETE_SHORT`` when its whole length is within the dangle length.

    INFERRED, where the documentation is silent: a dangle's length is measured along the line from its free end; a
    line dangling at both ends is trimmed at both; every dangle is judged on the lines as they were before any was
    trimmed; a line crossing itself does not count as touching another; a length equal to the dangle length is
    trimmed (the usage note's *less than or equal to*); and with no dangle length, a free-standing line is kept, since
    there is no length for it to be short against; and a line that touches others at one place only, with both of
    its dangles within the length, is left whole, since trimming both would leave nothing and the documentation says
    the tool deletes only short free-standing lines.
    """
    shorts = str(delete_shorts or "DELETE_SHORT").upper()
    if shorts not in ("DELETE_SHORT", "KEEP_SHORT"):
        _refuse("TrimLine", "delete_shorts", delete_shorts)
    features, srid = _lines(in_features, "TrimLine")
    limit = _length(dangle_length, srid, "TrimLine", "dangle_length")
    from shapely.ops import substring
    answers = {}
    for oid, line in features:
        others = shapely.union_all([s for o, s in features if o != oid]) if len(features) > 1 else LineString()
        at = _positions(line, others)
        if not at:
            if shorts == "DELETE_SHORT" and limit is not None and line.length <= limit:
                answers[oid] = None
            continue
        start, end = 0.0, line.length
        if limit is None or at[0] <= limit:
            start = at[0]
        if limit is None or line.length - at[-1] <= limit:
            end = at[-1]
        if start >= end:
            continue
        if (start, end) != (0.0, line.length):
            answers[oid] = substring(line, start, end)
    with da.UpdateCursor(in_features, ["OID@", "SHAPE@"]) as rows:
        for row in rows:
            if row[0] not in answers:
                continue
            if answers[row[0]] is None:
                rows.deleteRow()
            else:
                rows.updateRow([row[0], answers[row[0]]])
    return Result(in_features)


def ExtendLine(in_features, length=None, extend_to="EXTENSION"):
    """Dangling ends lengthened, along the direction of their last segment, to the first line they would meet within
    ``length`` (at any distance when no length is given). An end with nothing to meet within the length is left.

    Only ``FEATURE`` — extending to existing lines — is built. ``EXTENSION``, the default, also lets an extension stop
    at another line's extension, and the documentation does not say which of two meeting extensions is lengthened or in
    what order they are judged; it is refused rather than guessed.

    INFERRED: an end is dangling when it touches no other line; the direction is that of the end's last segment; the
    line the extension stops at is the one it meets nearest the end, which may be the line itself (as the documentation
    allows); and every extension is judged against the lines as they were before any was extended.
    """
    mode = str(extend_to or "EXTENSION").upper()
    if mode != "FEATURE":
        _refuse("ExtendLine", "extend_to", extend_to,
                "the documentation does not say how two extensions that meet are resolved; FEATURE is built")
    features, srid = _lines(in_features, "ExtendLine")
    limit = _length(length, srid, "ExtendLine", "length")
    if not features:
        return Result(in_features)
    everything = shapely.union_all([s for _, s in features])
    x0, y0, x1, y1 = everything.bounds
    reach = limit if limit is not None else 2 * ((x1 - x0) ** 2 + (y1 - y0) ** 2) ** 0.5 + 1

    def extended(oid, line, at_start):
        points = list(line.coords)
        if at_start:
            points = points[::-1]
        end, before = numpy.array(points[-1]), numpy.array(points[-2])
        others = shapely.union_all([s for o, s in features if o != oid]) if len(features) > 1 else LineString()
        if not others.is_empty and others.distance(Point(end)) == 0:
            return None
        direction = end - before
        norm = numpy.hypot(*direction)
        if norm == 0:
            return None
        ray = LineString([end, end + direction / norm * reach])
        nearest = None
        for _, target in features:
            met = ray.intersection(target)
            for part in getattr(met, "geoms", [met]):
                if part.is_empty:
                    continue
                for c in (part.coords if part.geom_type != "Point" else [part.coords[0]]):
                    gap = float(numpy.hypot(c[0] - end[0], c[1] - end[1]))
                    if gap > 0 and (limit is None or gap <= limit) and (nearest is None or gap < nearest[0]):
                        nearest = (gap, c)
        return nearest[1] if nearest else None

    answers = {}
    for oid, line in features:
        head, tail = extended(oid, line, True), extended(oid, line, False)
        if head is None and tail is None:
            continue
        points = list(line.coords)
        answers[oid] = LineString([*([head] if head else []), *points, *([tail] if tail else [])])
    with da.UpdateCursor(in_features, ["OID@", "SHAPE@"]) as rows:
        for row in rows:
            if row[0] in answers:
                rows.updateRow([row[0], answers[row[0]]])
    return Result(in_features)


# ---- Snap


def _snap_targets(shape, kind):
    """The places a vertex may snap to on a target: its ends, its vertices, or its edges (as a geometry)."""
    if kind == "EDGE":
        return shape if shape.geom_type not in ("Point", "MultiPoint") else None
    parts = list(getattr(shape, "geoms", [shape]))
    if kind == "END":
        if any(p.geom_type == "Polygon" for p in parts):
            raise NotBuiltError("arcpy.edit.Snap: type 'END' on polygon targets is not built in grapy: the documentation "
                                "does not say where a polygon's ends are.")
        points = []
        for p in parts:
            points += [p.coords[0]] if p.geom_type == "Point" else [p.coords[0], p.coords[-1]]
        return MultiPoint(points)
    from .management import _coordinates
    return MultiPoint(list(_coordinates(shape)))


def Snap(in_features, snap_environment):
    """Each vertex of the input moved to the nearest end, vertex or edge of the snap features within a distance.
    ``snap_environment`` is ``[[features, "END"|"VERTEX"|"EDGE", distance], …]``; the rules are tried in order.

    INFERRED: the first rule that finds something within its distance decides, and a later rule is not tried for that
    vertex; snapping to an edge moves the vertex onto it and adds no vertex to the target; targets are read as they were
    before any vertex moved; a feature never snaps to itself; and when the snap features are the input itself a feature
    snaps only to features of a higher ObjectID, as the documentation says it typically does.
    """
    _, kind, srid, _ = _schema(in_features)
    rules = []
    for rule in _pairs(snap_environment):
        if len(rule) < 3:
            raise ExecuteError("ERROR 000800: each snap rule is [features, type, distance].")
        features, snap_type, distance = rule[0], str(rule[1]).upper(), " ".join(str(v) for v in rule[2:])
        if snap_type not in ("END", "VERTEX", "EDGE"):
            _refuse("Snap", "type", rule[1])
        from .analysis import _read
        own = _same(features, in_features)
        targets = [(oid, _snap_targets(shape, snap_type)) for oid, _, shape in _read(features, srid)]
        rules.append((own, [(o, t) for o, t in targets if t is not None], _length(distance, srid, "Snap", "distance")))

    def moved(xy, oid):
        here = Point(xy)
        for own, targets, distance in rules:
            best = None
            for target_oid, target in targets:
                if own and target_oid <= oid:
                    continue
                gap = target.distance(here)
                if gap <= distance and (best is None or gap < best[0]):
                    best = (gap, target)
            if best is not None:
                from shapely.ops import nearest_points
                to = nearest_points(best[1], here)[0]
                return (to.x, to.y)
        return tuple(xy)

    def snapped(shape, oid):
        if isinstance(shape, Point):
            return Point(moved(shape.coords[0], oid))
        if isinstance(shape, LineString):
            return LineString([moved(c, oid) for c in shape.coords])
        if isinstance(shape, Polygon):
            def ring(r):
                points = [moved(c, oid) for c in list(r.coords)[:-1]]
                return [*points, points[0]]
            return Polygon(ring(shape.exterior), [ring(r) for r in shape.interiors])
        return type(shape)([snapped(p, oid) for p in shape.geoms])

    with da.UpdateCursor(in_features, ["OID@", "SHAPE@"]) as rows:
        for row in rows:
            if row[1] is not None:
                rows.updateRow([row[0], snapped(row[1]._shape, row[0])])
    return Result(in_features)


# ---- TransformFeatures and CalculateTransformationErrors

_MINIMUM_LINKS = {"AFFINE": 3, "PROJECTIVE": 4, "SIMILARITY": 2}


def _links(in_link_features, srid=None):
    _, kind, own, _ = _schema(in_link_features)
    if kind != "Polyline":
        raise ExecuteError(f"ERROR 000365: links are lines; {in_link_features} holds {kind}.")
    if srid is not None and own != srid:
        raise ExecuteError("ERROR 001007: the input features and the links must share one coordinate system.")
    with da.SearchCursor(in_link_features, ["OID@", "SHAPE@"]) as rows:
        return [(r[0], r[1]._shape.coords[0] if r[1]._shape.geom_type == "LineString" else r[1]._shape.geoms[0].coords[0],
                 r[1]._shape.coords[-1] if r[1]._shape.geom_type == "LineString" else r[1]._shape.geoms[-1].coords[-1])
                for r in rows if r[1] is not None]


def _fit(links, method, tool):
    """The transformation that best carries each link's start onto its end, as a function of an (n, 2) array."""
    method = str(method or "AFFINE").upper()
    if method not in _MINIMUM_LINKS:
        _refuse(tool, "method", method)
    if len(links) < _MINIMUM_LINKS[method]:
        raise ExecuteError(f"ERROR 001011: {method} needs at least {_MINIMUM_LINKS[method]} links; {len(links)} were given.")
    source = numpy.array([s for _, s, _ in links], dtype=float)[:, :2]
    target = numpy.array([t for _, _, t in links], dtype=float)[:, :2]
    n = len(links)
    if method == "AFFINE":
        design = numpy.column_stack([source, numpy.ones(n)])
        x, *_ = numpy.linalg.lstsq(design, target[:, 0], rcond=None)
        y, *_ = numpy.linalg.lstsq(design, target[:, 1], rcond=None)
        return lambda p: numpy.column_stack([p @ x[:2] + x[2], p @ y[:2] + y[2]])
    if method == "SIMILARITY":
        design = numpy.zeros((2 * n, 4))
        design[0::2] = numpy.column_stack([source[:, 0], -source[:, 1], numpy.ones(n), numpy.zeros(n)])
        design[1::2] = numpy.column_stack([source[:, 1], source[:, 0], numpy.zeros(n), numpy.ones(n)])
        (a, b, c, d), *_ = numpy.linalg.lstsq(design, target.reshape(-1), rcond=None)
        return lambda p: numpy.column_stack([a * p[:, 0] - b * p[:, 1] + c, b * p[:, 0] + a * p[:, 1] + d])
    if n != 4:
        _refuse(tool, "method", "PROJECTIVE",
                f"with {n} links; the documentation does not say how a projective fit over more than four is solved, "
                "and four links are fitted exactly")
    design = numpy.zeros((8, 8))
    for i, ((x, y), (u, v)) in enumerate(zip(source, target)):
        design[2 * i] = [x, y, 1, 0, 0, 0, -u * x, -u * y]
        design[2 * i + 1] = [0, 0, 0, x, y, 1, -v * x, -v * y]
    a, b, c, d, e, f, g, h = numpy.linalg.solve(design, target.reshape(-1))

    def projective(p):
        w = g * p[:, 0] + h * p[:, 1] + 1
        return numpy.column_stack([(a * p[:, 0] + b * p[:, 1] + c) / w, (d * p[:, 0] + e * p[:, 1] + f) / w])
    return projective


def _errors(links, transform, out_link_table):
    """Each link's residual — how far its transformed start falls from its end — and their root mean square. The
    table is written when asked: ``Orig_FID``, ``X_Source``, ``Y_Source``, ``X_Destination``, ``Y_Destination``,
    ``Residual_Error``."""
    source = numpy.array([s for _, s, _ in links], dtype=float)[:, :2]
    target = numpy.array([t for _, _, t in links], dtype=float)[:, :2]
    residuals = numpy.hypot(*(transform(source) - target).T)
    rmse = float(numpy.sqrt(numpy.mean(residuals ** 2)))
    if out_link_table not in (None, "", "#"):
        from .analysis import _out
        out = _out(out_link_table)
        columns = [_gpkg.Column("Orig_FID", "Integer"), *[_gpkg.Column(n, "Double") for n in
                   ("X_Source", "Y_Source", "X_Destination", "Y_Destination", "Residual_Error")]]
        _gpkg.create(out, "Geometry", 0, columns)
        with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
            for (oid, s, t), residual in zip(links, residuals):
                written.insertRow((oid, s[0], s[1], t[0], t[1], float(residual), None))
        out_link_table = out
    AddMessage(f"RMS Error: {rmse}")
    return rmse, out_link_table


def CalculateTransformationErrors(in_link_features, out_link_table=None, method="AFFINE"):
    """The residual of each link and the root mean square error of the transformation the links define, without
    changing anything. Its outputs are the link table and the RMSE.

    The fit is least squares: AFFINE ``x' = ax + by + c, y' = dx + ey + f``; SIMILARITY ``x' = ax − by + c,
    y' = bx + ay + d``; PROJECTIVE ``x' = (ax + by + c)/(gx + hy + 1)`` and likewise for y', from exactly four links.

    INFERRED: the documentation names the transformations and says the parameters are a best fit, leaving the fit to a
    topic it links. grapy fits by ordinary least squares on the coordinates; a residual is the straight-line distance
    from the transformed start to the link's end; the RMSE is the square root of the mean of the squared residuals;
    the outputs are ordered link table, then RMSE.
    """
    links = _links(in_link_features)
    rmse, table = _errors(links, _fit(links, method, "CalculateTransformationErrors"), out_link_table)
    return Result(table or "", rmse)


def TransformFeatures(in_features, in_link_features, method="AFFINE", out_link_table=None):
    """Every vertex of the input moved by the transformation the links define — each link runs from a source control
    point to its destination — fitted as CalculateTransformationErrors fits it.

    INFERRED: the outputs are ordered as the other edit tools' are, the updated input first, then the link table and
    the RMSE.
    """
    _, _, srid, _ = _schema(in_features)
    links = _links(in_link_features, srid)
    transform = _fit(links, method, "TransformFeatures")
    rmse, table = _errors(links, transform, out_link_table)
    with da.UpdateCursor(in_features, ["SHAPE@"]) as rows:
        for row in rows:
            if row[0] is not None:
                rows.updateRow([shapely.transform(row[0]._shape, transform)])
    return Result(in_features, table or "", rmse)


from ._runtime import equivalent as _equivalent  # noqa: E402


@_equivalent("piecewise-linear rubber-sheeting on a Delaunay triangulation of the links' sources",
             "Saalfeld 1985, A fast rubber-sheeting transformation using simplicial coordinates, The American Cartographer 12(2)")
def RubbersheetFeatures(in_features, in_link_features, in_identity_links=None, method="LINEAR"):
    """EQUIVALENT: LINEAR rubber-sheeting as Saalfeld (1985) defines it. The links' starts — and the identity links'
    points, which stay where they are — are triangulated (Delaunay); within each triangle a vertex moves by the affine
    map that carries the triangle's corners onto the links' ends, so its new place is the barycentric mean of those
    ends. INFERRED: a vertex outside the triangulation does not move — the page builds "temporary TINs" and does not
    say what lies outside them. NATURAL_NEIGHBOR is refused (not in this equivalent).
    """
    from scipy.spatial import Delaunay
    tool = "RubbersheetFeatures"
    if str(method or "LINEAR").upper() != "LINEAR":
        _refuse(tool, "method", method, "natural neighbour rubber-sheeting is not in this equivalent")
    _, _, srid, _ = _schema(in_features)
    links = _links(in_link_features, srid)
    source = [tuple(s[:2]) for _, s, _ in links]
    target = [tuple(t[:2]) for _, _, t in links]
    if in_identity_links not in (None, "", "#"):
        with da.SearchCursor(in_identity_links, ["SHAPE@XY"]) as rows:
            for (xy,) in rows:
                source.append(tuple(xy))
                target.append(tuple(xy))
    if len(source) < 3:
        raise ExecuteError("ERROR 001011: rubber-sheeting needs at least three links or identity points.")
    source_array = numpy.array(source, dtype=float)
    target_array = numpy.array(target, dtype=float)
    triangulation = Delaunay(source_array)

    def move(points):
        found = triangulation.find_simplex(points)
        out = points.copy()
        for i, simplex in enumerate(found):
            if simplex < 0:
                continue
            corners = triangulation.simplices[simplex]
            transform = triangulation.transform[simplex]
            b = transform[:2] @ (points[i] - transform[2])
            weights = numpy.array([b[0], b[1], 1 - b.sum()])
            out[i] = weights @ target_array[corners]
        return out

    with da.UpdateCursor(in_features, ["SHAPE@"]) as rows:
        for row in rows:
            if row[0] is not None:
                rows.updateRow([shapely.transform(row[0]._shape, move)])
    return Result(in_features)


_MATCHING = "INFERRED construction: features match when the Hausdorff distance between them is within the search distance"


def _features_of(path, fields=()):
    """[(oid, {field: value}, shape)] of a feature class."""
    names = list(fields)
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"]) as rows:
        return [(r[0], dict(zip(names, r[1:-1])), r[-1]._shape) for r in rows if r[-1] is not None]


def _match_pairs(match_fields):
    pairs = _pairs(match_fields) if match_fields not in (None, "", "#") else []
    return [(p[0], p[1] if len(p) > 1 else p[0]) for p in pairs]


def _directed(shape, other, spacing):
    """The largest distance from a point of ``shape`` (its vertices, densified to ``spacing``) to ``other``."""
    import shapely
    return max(other.distance(shapely.Point(c)) for c in shapely.segmentize(shape, spacing).coords)


def _best(feature, candidates, distance, pairs, directed=False):
    """The candidate nearest ``feature`` by Hausdorff distance within ``distance`` — with ``directed``, the distance from
    the feature to the candidate only — those agreeing on every match field preferred when any do — ties by object id;
    and how many candidates there were."""
    measure = ((lambda a, b: _directed(a, b, distance / 10)) if directed else (lambda a, b: a.hausdorff_distance(b)))
    near = [(measure(feature[2], c[2]), c[0], c) for c in candidates
            if feature[2].distance(c[2]) <= distance and measure(feature[2], c[2]) <= distance]
    if pairs:
        agreeing = [n for n in near if all(feature[1].get(a) == n[2][1].get(b) for a, b in pairs)]
        near = agreeing or near
    if not near:
        return None, 0
    near.sort(key=lambda n: (n[0], n[1]))
    return near[0][2], len(near)


def _ends(shape):
    line = _single_line(shape, "edit")
    return tuple(line.coords[0][:2]), tuple(line.coords[-1][:2])


def _link_output(out_feature_class, srid, fields):
    from .analysis import _out
    out = _out(out_feature_class)
    _gpkg.create(out, "Polyline", srid, [_gpkg.Column(f, kind) for f, kind in fields])
    return out


@_equivalent("source and target lines matched by least Hausdorff distance within the search distance; links from each "
             "source end to the matched target's corresponding end", "INFERRED construction; Hausdorff distance as in "
             "Alt and Guibas 2000")
def GenerateRubbersheetLinks(source_features, target_features, out_feature_class, search_distance, match_fields=None,
                             out_match_table=None):
    """EQUIVALENT: each source line is matched to the target line nearest it by Hausdorff distance within the search
    distance — those agreeing on the match fields preferred — ties by object id (the page's matching "by line topology,
    patterns and geometric characteristics" is not specified). Each matched pair gives a link from each source end to
    the target end nearer it. A link whose ends coincide is an identity link, written to the ``_pnt`` point feature
    class beside the output (INFERRED name). SRC_FID and TGT_FID are −1 where a location is shared by more than one
    source or target feature, as documented; one link per source location, the lowest source id's. Refused:
    out_match_table."""
    tool = "GenerateRubbersheetLinks"
    if out_match_table not in (None, "", "#"):
        _refuse(tool, "out_match_table", out_match_table, "its match groups and confidence levels are not specified")
    _, _, srid, _ = _schema(source_features)
    distance = _length(search_distance, srid, tool, "search_distance")
    pairs = _match_pairs(match_fields)
    sources = _features_of(source_features, [a for a, _ in pairs])
    targets = _features_of(target_features, [b for _, b in pairs])
    source_ends = {}
    for oid, _, shape in sources:
        for end in _ends(shape):
            source_ends.setdefault(end, set()).add(oid)
    target_ends = {}
    for oid, _, shape in targets:
        for end in _ends(shape):
            target_ends.setdefault(end, set()).add(oid)
    links = {}
    for feature in sorted(sources, key=lambda f: f[0]):
        target, _ = _best(feature, targets, distance, pairs)
        if target is None:
            continue
        (s0, s1), (t0, t1) = _ends(feature[2]), _ends(target[2])
        near0 = t0 if math.dist(s0, t0) <= math.dist(s0, t1) else t1
        near1 = t1 if near0 == t0 else t0
        for source_end, target_end in ((s0, near0), (s1, near1)):
            links.setdefault(source_end, target_end)
    out = _link_output(out_feature_class, srid, [("SRC_FID", "Integer"), ("TGT_FID", "Integer")])
    from .analysis import _out
    identity = _out(out[:-5] + "_pnt.gpkg") if out.lower().endswith(".gpkg") else out + "_pnt"
    _gpkg.create(identity, "Point", srid, [_gpkg.Column("SRC_FID", "Integer"), _gpkg.Column("TGT_FID", "Integer")])
    fid = (lambda table, location: next(iter(table[location])) if len(table.get(location, ())) == 1 else -1)
    with da.InsertCursor(out, ["SRC_FID", "TGT_FID", "SHAPE@"]) as written, \
            da.InsertCursor(identity, ["SRC_FID", "TGT_FID", "SHAPE@"]) as same:
        for source_end, target_end in links.items():
            values = (fid(source_ends, source_end), fid(target_ends, target_end))
            if source_end == target_end:
                same.insertRow((*values, Point(source_end)))
            else:
                written.insertRow((*values, LineString([source_end, target_end])))
    return Result(out, identity)


@_equivalent("each source dangle linked to the nearest adjacent dangle within the search distance",
             "INFERRED construction")
def GenerateEdgematchLinks(source_features, adjacent_features, out_feature_class, search_distance, match_fields=None):
    """EQUIVALENT: a dangle is a line end no other line of its own dataset shares. Each source dangle, in object id
    order, is linked to the nearest unused adjacent dangle within the search distance — dangles of lines agreeing on the
    match fields preferred — ties by object id; "a line … is generated from the end of the source line to the end of
    the matched adjacent line", as documented. EM_CONF is 100 divided by the number of candidate dangles (INFERRED; the
    page names what lowers it without a formula)."""
    tool = "GenerateEdgematchLinks"
    _, _, srid, _ = _schema(source_features)
    distance = _length(search_distance, srid, tool, "search_distance")
    pairs = _match_pairs(match_fields)
    sources = _features_of(source_features, [a for a, _ in pairs])
    adjacent = _features_of(adjacent_features, [b for _, b in pairs])

    def dangles(features):
        count = {}
        for _, _, shape in features:
            for end in _ends(shape):
                count[end] = count.get(end, 0) + 1
        return [(oid, values, end) for oid, values, shape in features for end in _ends(shape) if count[end] == 1]

    source_dangles, adjacent_dangles = dangles(sources), dangles(adjacent)
    used = set()
    out = _link_output(out_feature_class, srid, [("SRC_FID", "Integer"), ("ADJ_FID", "Integer"), ("EM_CONF", "Double")])
    with da.InsertCursor(out, ["SRC_FID", "ADJ_FID", "EM_CONF", "SHAPE@"]) as written:
        for oid, values, end in sorted(source_dangles, key=lambda d: (d[0], d[2])):
            near = [(math.dist(end, a[2]), a[0], a) for a in adjacent_dangles
                    if (a[0], a[2]) not in used and math.dist(end, a[2]) <= distance]
            if pairs:
                agreeing = [n for n in near if all(values.get(x) == n[2][1].get(y) for x, y in pairs)]
                near = agreeing or near
            if not near:
                continue
            near.sort(key=lambda n: (n[0], n[1]))
            chosen = near[0][2]
            used.add((chosen[0], chosen[2]))
            written.insertRow((oid, chosen[0], 100.0 / len(near), LineString([end, chosen[2]])))
    return Result(out)


@_equivalent("line ends moved to the documented connecting locations; ADJUST_VERTICES spreads the move along the line in "
             "proportion to the distance from the far end", "INFERRED construction for ADJUST_VERTICES")
def EdgematchFeatures(in_features, in_link_features, method="MOVE_ENDPOINT", adjacent_features=None, border_features=None):
    """EQUIVALENT: the connecting location of each link is, as documented, its end (input features only), its midpoint
    (with adjacent features), or the point on the border features nearest its midpoint (with border features). The line
    whose id is the link's SRC_FID — and with adjacent features the line ADJ_FID names — has its end nearest the link
    moved there: MOVE_ENDPOINT moves the end vertex; ADD_SEGMENT adds a straight segment to it; ADJUST_VERTICES moves
    every vertex by the end's shift times (its distance along the line from the far end)/(the line's length) — the
    page says only that the change is "gradually reduced toward the opposite end"."""
    import shapely
    from shapely.ops import nearest_points
    tool = "EdgematchFeatures"
    method = str(method or "MOVE_ENDPOINT").upper()
    if method not in ("MOVE_ENDPOINT", "ADD_SEGMENT", "ADJUST_VERTICES"):
        raise ValueError(f"arcpy.edit.{tool}: method is MOVE_ENDPOINT, ADD_SEGMENT or ADJUST_VERTICES.")
    with da.SearchCursor(in_link_features, ["SRC_FID", "ADJ_FID", "SHAPE@"]) as rows:
        links = [(r[0], r[1], _ends(r[2]._shape)) for r in rows if r[2] is not None]
    border = None
    if border_features not in (None, "", "#"):
        border = shapely.union_all([f[2] for f in _features_of(border_features)])
    moves_in, moves_adjacent = {}, {}
    for source, adjacent, (start, end) in links:
        if border is not None:
            middle = Point((start[0] + end[0]) / 2, (start[1] + end[1]) / 2)
            target = tuple(nearest_points(border, middle)[0].coords[0][:2])
        elif adjacent_features not in (None, "", "#"):
            target = ((start[0] + end[0]) / 2, (start[1] + end[1]) / 2)
        else:
            target = end
        moves_in.setdefault(source, []).append((start, target))
        if adjacent_features not in (None, "", "#"):
            moves_adjacent.setdefault(adjacent, []).append((end, target))

    def adjust(path, moves):
        with da.UpdateCursor(path, ["OID@", "SHAPE@"]) as rows:
            for oid, shape in rows:
                if oid not in moves or shape is None:
                    continue
                coordinates = [tuple(c[:2]) for c in _single_line(shape._shape, tool).coords]
                for near, target in moves[oid]:
                    at_start = math.dist(coordinates[0], near) <= math.dist(coordinates[-1], near)
                    if not at_start:
                        coordinates.reverse()
                    if method == "MOVE_ENDPOINT":
                        coordinates[0] = target
                    elif method == "ADD_SEGMENT":
                        coordinates.insert(0, target)
                    else:
                        line = LineString(coordinates)
                        dx, dy = target[0] - coordinates[0][0], target[1] - coordinates[0][1]
                        total = line.length
                        moved, run = [], 0.0
                        for k, c in enumerate(coordinates):
                            if k:
                                run += math.dist(coordinates[k - 1], c)
                            share = (total - run) / total if total else 1.0
                            moved.append((c[0] + dx * share, c[1] + dy * share))
                        coordinates = moved
                    if not at_start:
                        coordinates.reverse()
                rows.updateRow([oid, LineString(coordinates)])

    adjust(in_features, moves_in)
    if adjacent_features not in (None, "", "#"):
        adjust(adjacent_features, moves_adjacent)
    return Result(in_features)


@_equivalent("input lines matched to target lines by Hausdorff distance within the search distance and replaced by "
             "the target's stretch between their ends' projections", "INFERRED construction")
def AlignFeatures(in_features, target_features, search_distance, match_fields=None):
    """EQUIVALENT: an input line lying wholly within the search distance of a target line (the directed Hausdorff
    distance from the input to the target, its vertices densified to a tenth of the search distance), the
    nearest such target preferred — those agreeing on the match fields first — ties by object id, is replaced by the
    target's stretch between the points nearest its two ends; any other input line ending where it ended is moved with
    it, so connections stay, as documented. AF_CONF is 100 divided by the number of candidates, −1 for unmodified
    features (INFERRED formula; the page names what lowers it). Polygons are refused in this equivalent."""
    from shapely.ops import substring
    tool = "AlignFeatures"
    _, kind, srid, _ = _schema(in_features)
    if kind != "Polyline":
        _refuse(tool, "in_features", in_features, "polygon alignment is not in this equivalent")
    distance = _length(search_distance, srid, tool, "search_distance")
    pairs = _match_pairs(match_fields)
    inputs = _features_of(in_features, [a for a, _ in pairs])
    targets = _features_of(target_features, [b for _, b in pairs])
    new_shapes, confidence, moved_ends = {}, {}, {}
    for feature in inputs:
        target, count = _best(feature, targets, distance, pairs, directed=True)
        if target is None:
            continue
        line = _single_line(target[2], tool)
        (s0, s1) = _ends(feature[2])
        a, b = line.project(Point(s0)), line.project(Point(s1))
        piece = substring(line, a, b)
        if piece.geom_type != "LineString" or piece.length == 0:
            continue
        new_shapes[feature[0]] = piece
        confidence[feature[0]] = 100.0 / count
        moved_ends[s0], moved_ends[s1] = tuple(piece.coords[0][:2]), tuple(piece.coords[-1][:2])
    names = {c.name.lower() for c in _schema(in_features)[0]}
    if "af_conf" not in names:
        _gpkg.add_column(in_features, _gpkg.Column("AF_CONF", "Double"))
    with da.UpdateCursor(in_features, ["OID@", "AF_CONF", "SHAPE@"]) as rows:
        for oid, _, shape in rows:
            if oid in new_shapes:
                rows.updateRow([oid, confidence[oid], new_shapes[oid]])
            elif shape is not None:
                coordinates = [tuple(c[:2]) for c in _single_line(shape._shape, tool).coords]
                ends = [moved_ends.get(coordinates[0]), moved_ends.get(coordinates[-1])]
                if ends[0]:
                    coordinates[0] = ends[0]
                if ends[1]:
                    coordinates[-1] = ends[1]
                rows.updateRow([oid, -1.0, LineString(coordinates)])
    return Result(in_features)


@_equivalent("target lines matched to source lines by Hausdorff distance within the search distance; an m:n match "
             "takes the longest source, as documented", "INFERRED construction for the matching")
def TransferAttributes(source_features, target_features, transfer_fields, search_distance, match_fields=None,
                       out_match_table=None, transfer_rule_fields=None):
    """EQUIVALENT: each target line takes the transfer fields of the source lines within the search distance of it
    (Hausdorff distance), those agreeing on the match fields preferred; when several match, "attributes will be
    transferred from the longest of the matched source features", as documented, ties by object id. A transfer field
    with a target field's name is written with ``_1`` (``_2`` …), as documented; a missing source value transfers
    nothing. Refused: out_match_table and transfer_rule_fields."""
    tool = "TransferAttributes"
    if out_match_table not in (None, "", "#"):
        _refuse(tool, "out_match_table", out_match_table, "its match groups and confidence levels are not specified")
    if transfer_rule_fields not in (None, "", "#"):
        _refuse(tool, "transfer_rule_fields", transfer_rule_fields, "its rule evaluation is not in this equivalent")
    _, _, srid, _ = _schema(target_features)
    distance = _length(search_distance, srid, tool, "search_distance")
    pairs = _match_pairs(match_fields)
    fields = _list(transfer_fields)
    source_columns = {c.name.lower(): c for c in _schema(source_features)[0]}
    sources = _features_of(source_features, [*fields, *[a for a, _ in pairs]])
    targets = _features_of(target_features, [b for _, b in pairs])
    existing = {c.name.lower() for c in _schema(target_features)[0]}
    written_names = []
    for field in fields:
        name, k = field, 0
        while name.lower() in existing:
            k += 1
            name = f"{field}_{k}"
        existing.add(name.lower())
        written_names.append(name)
        _gpkg.add_column(target_features, _gpkg.Column(name, source_columns[field.lower()].type))
    chosen = {}
    for target in targets:
        near = [s for s in sources if target[2].distance(s[2]) <= distance and
                target[2].hausdorff_distance(s[2]) <= distance]
        if pairs:
            agreeing = [s for s in near if all(s[1].get(a) == target[1].get(b) for a, b in pairs)]
            near = agreeing or near
        if near:
            chosen[target[0]] = sorted(near, key=lambda s: (-s[2].length, s[0]))[0]
    with da.UpdateCursor(target_features, ["OID@", *written_names]) as rows:
        for row in rows:
            source = chosen.get(row[0])
            if source is None:
                continue
            values = [source[1].get(f) for f in fields]
            if any(v is None for v in values):
                continue
            rows.updateRow([row[0], *values])
    return Result(target_features)


_REFUSED = {
    "SplitLineByMatch": "its input is a match table, which grapy's matching tools do not write — their match groups "
                        "and confidence levels are not specified",
    "SimplifyByStraightLinesAndCircularArcs": "it writes true circular arcs, which a GeoPackage feature class here does "
                                              "not hold, and its fitting is not specified",
    "SimplifyByTangentSegments": "it writes true curves, which a GeoPackage feature class here does not hold, and its "
                                 "fitting is not specified",
}


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.edit.{name} is not built in grapy" + (f": {why}." if why else " yet."))

    return missing


# ---- UpdateCOGO on ADR-240's COGO-enabled lines (D-329's sweep), built in _cogo
from . import _cogo  # noqa: E402

UpdateCOGO = _cogo.UpdateCOGO
