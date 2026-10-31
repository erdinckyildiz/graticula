"""arcpy.topographic's shape tools: centrelines, narrow polygons, hydrology thinning and the sheet diagrams (ADR-251).

The centreline is the medial axis (Blum 1967) approximated by the Voronoi diagram of the densified boundary — the
Voronoi edges lying wholly inside the polygon — and pruned to its main path. Narrow parts are found by morphological
opening (Serra 1982): what an opening by a disc of the minimum width does not keep is narrower than that width.
"""
from __future__ import annotations

import heapq
import math

import shapely
from shapely.geometry import LineString, MultiLineString, Point, Polygon, box
from shapely.ops import linemerge, unary_union

from . import _topo_prod as tp
from ._result import Result
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError


# ---- the skeleton ----------------------------------------------------------------------------------------------------

def skeleton(polygon, spacing=None):
    """The Voronoi skeleton of a polygon: {node: {node: length}} over its edges, with node coordinates as keys."""
    if spacing is None:
        spacing = max(polygon.length / 600.0, math.sqrt(polygon.area) / 200.0, 1e-9)
    dense = shapely.segmentize(polygon, spacing)
    points = shapely.MultiPoint(shapely.get_coordinates(dense.boundary))
    edges = shapely.voronoi_polygons(points, only_edges=True)
    graph = {}
    for edge in tp.parts(edges):
        coords = list(edge.coords)
        for a, b in zip(coords, coords[1:]):
            segment = LineString([a, b])
            if segment.length == 0 or not polygon.contains(segment):
                continue
            ka, kb = tp.key(a, 7), tp.key(b, 7)
            graph.setdefault(ka, {})[kb] = segment.length
            graph.setdefault(kb, {})[ka] = segment.length
    return graph


def prune(graph, polygon, factor=1.5):
    """The skeleton without its spurs: a branch from a leaf to the first junction is dropped when it is no longer than
    ``factor`` times the junction's distance from the boundary — the branches the medial axis sends into every convex
    corner (a rectangle's corner branch is √2 times that distance)."""
    boundary = polygon.boundary
    graph = {n: dict(e) for n, e in graph.items()}
    leaves = [n for n, e in graph.items() if len(e) == 1]
    for leaf in leaves:
        if leaf not in graph or len(graph[leaf]) != 1:
            continue
        branch, length, node, previous = [leaf], 0.0, leaf, None
        while True:
            onward = [m for m in graph[node] if m != previous]
            if len(graph[node]) >= 3 or not onward:
                break
            length += graph[node][onward[0]]
            previous, node = node, onward[0]
            branch.append(node)
        if len(graph.get(node, {})) < 3:
            continue   # a path with no junction is the whole skeleton, kept
        if length <= factor * boundary.distance(Point(node)):
            for a, b in zip(branch, branch[1:]):
                graph[a].pop(b, None)
                graph[b].pop(a, None)
            for n in branch[:-1]:
                if not graph.get(n):
                    graph.pop(n, None)
    return graph


def _farthest(graph, start):
    distance, previous = {start: 0.0}, {start: None}
    queue = [(0.0, start)]
    while queue:
        d, node = heapq.heappop(queue)
        if d > distance[node]:
            continue
        for other, length in graph[node].items():
            if d + length < distance.get(other, float("inf")):
                distance[other] = d + length
                previous[other] = node
                heapq.heappush(queue, (d + length, other))
    return distance, previous


def _path(previous, end):
    out = []
    while end is not None:
        out.append(end)
        end = previous[end]
    return out[::-1]


def main_path(graph):
    """The longest of the skeleton's shortest paths (its diameter, found by two farthest-node searches)."""
    if not graph:
        return None
    best = None
    seen = set()
    for start in graph:
        if start in seen:
            continue
        distance, _ = _farthest(graph, start)
        seen.update(distance)
        a = max(distance, key=distance.get)
        far, previous = _farthest(graph, a)
        b = max(far, key=far.get)
        if best is None or far[b] > best[0]:
            best = (far[b], _path(previous, b))
    nodes = best[1]
    return LineString(nodes) if len(nodes) >= 2 else None


def centreline(polygon, touches=()):
    """The centreline of a polygon: the skeleton's main path; with touch points of connecting features, the skeleton
    paths joining them, each extended to its touch point."""
    graph = prune(skeleton(polygon), polygon)
    if not graph:
        return []
    if len(touches) < 2:
        line = main_path(graph)
        if line is None:
            return []
        if len(touches) == 1:
            t = touches[0]
            nearest = min((line.coords[0], line.coords[-1]), key=lambda c: math.dist(c, t))
            coords = list(line.coords)
            coords = coords + [t] if nearest == coords[-1] else [t] + coords
            line = LineString(coords)
        return [line]
    anchors = [min(graph, key=lambda n: math.dist(n, t)) for t in touches]
    distance, previous = _farthest(graph, anchors[0])
    segments = []
    for anchor, touch in zip(anchors, touches):
        if anchor != anchors[0] and anchor in previous:
            segments.append(LineString(_path(previous, anchor)))
        if math.dist(anchor, touch) > 0:
            segments.append(LineString([anchor, touch]))
    merged = unary_union(segments)
    return split_at_junctions(merged)


def split_at_junctions(lines):
    """Lines merged where two meet and split where three or more do."""
    merged = linemerge(unary_union(lines))
    return [p for p in tp.parts(merged) if p.geom_type == "LineString"]


# ---- PolygonToCenterline ---------------------------------------------------------------------------------------------

def _class_name(path):
    return path.replace("\\", "/").rstrip("/").rsplit("/", 1)[-1].split(".gpkg")[0] or "input"


def _touches(polygon, connectors):
    points = []
    for oid, shape in connectors:
        meet = polygon.boundary.intersection(shape)
        if meet.is_empty:
            continue
        points.append((oid, meet.representative_point().coords[0] if meet.geom_type != "Point" else meet.coords[0]))
    return points


def polygon_to_centerline(in_features, out_feature_class, connecting_features):
    from .analysis import _out
    tool = "PolygonToCenterline"
    path = tp.dataset(in_features)
    srid = tp.srid_of(path)
    out = _out(tp.text(out_feature_class))
    own = f"FID_{_class_name(path)}"
    connectors = []
    for each in tp.listed(connecting_features):
        name = f"FID_{_class_name(tp.dataset(each))}"
        connectors.append((name, [(r.oid, r.shape) for r in tp.read(each, fields=[]) if r.shape is not None]))
    columns = [("FID", "Integer"), (own, "Integer")] + [(n, "Integer") for n, _ in connectors]
    rows = [r for r in tp.read(in_features, fields=[]) if r.shape is not None]
    made = []
    for row in rows:
        for polygon in tp.polygons(row.shape):
            touches = [(n, oid, at) for n, found in connectors for oid, at in _touches(polygon, found)]
            for line in centreline(polygon, [t[2] for t in touches]):
                values = {"FID": row.oid, "SHAPE@": MultiLineString([line])}
                ends = [Point(line.coords[0]), Point(line.coords[-1])]
                neighbour = next((o.oid for o in rows if o.oid != row.oid
                                  and any(o.shape.distance(e) < 1e-6 for e in ends)), None)
                values[own] = neighbour
                for name, found in connectors:
                    values[name] = next((oid for oid, s in found if any(s.distance(e) < 1e-6 for e in ends)), None)
                made.append(values)
    tp.create_with_objectid(out, "Polyline", srid, columns)
    tp.insert(out, made)
    AddMessage(f"{tool}: {len(made)} centrelines written.")
    return Result(out)


# ---- IdentifyNarrowPolygons ------------------------------------------------------------------------------------------

def narrow_split(polygon, width):
    """(narrow parts, wide parts) of a polygon at a width: the wide parts are its opening by a disc of that diameter;
    slivers the opening cuts from convex corners (smaller than a quarter of the width squared) stay with the wide."""
    opened = polygon.buffer(-width / 2, join_style="round").buffer(width / 2, join_style="round").intersection(polygon)
    rest = polygon.difference(opened)
    narrow = [p for p in tp.polygons(rest) if p.area >= 0.25 * width * width]
    wide = polygon.difference(unary_union(narrow)) if narrow else polygon
    return narrow, [p for p in tp.polygons(wide) if p.area > 1e-12]


def path_length(polygon):
    line = main_path(skeleton(polygon))
    return line.length if line is not None else 0.0


def identify_narrow_polygons(in_features, out_feature_class, min_width, min_length, taper_length, connecting_features):
    from .analysis import _out
    tool = "IdentifyNarrowPolygons"
    path = tp.dataset(in_features)
    srid = tp.srid_of(path)
    tp.projected(tool, srid)
    width = tp.linear(tool, min_width, srid)
    length = tp.linear(tool, min_length, srid)
    taper = tp.linear(tool, taper_length, srid, 0.0)
    connectors = unary_union([r.shape for each in tp.listed(connecting_features) for r in tp.read(each, fields=[])
                              if r.shape is not None]) if tp.given(connecting_features) else None
    out = _out(tp.text(out_feature_class))
    made = []
    for row in tp.read(in_features, fields=[]):
        for polygon in tp.polygons(row.shape):
            narrow, wide = narrow_split(polygon, width)
            tapers = []
            if taper and narrow:
                for i, piece in enumerate(wide):
                    for n in narrow:
                        joint = piece.boundary.intersection(n.boundary)
                        if joint.length <= 0:
                            continue
                        a, b = shapely.get_coordinates(joint)[[0, -1]]
                        m = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
                        if connectors is not None and not connectors.intersection(piece.boundary).is_empty:
                            target = connectors.intersection(piece.boundary).representative_point()
                        else:
                            target = piece.representative_point()
                        dx, dy = target.x - m[0], target.y - m[1]
                        d = math.hypot(dx, dy) or 1.0
                        apex = (m[0] + dx / d * taper, m[1] + dy / d * taper)
                        cone = Polygon([tuple(a), tuple(b), apex]).buffer(0).intersection(piece)
                        if cone.area > 1e-12:
                            tapers.append(cone)
                            wide[i] = piece = piece.difference(cone)
            for piece in narrow:
                long_enough = path_length(piece) >= length
                made.append({"split_type": 0 if long_enough else 2, "split_length": path_length(piece), "FID": row.oid,
                             "SHAPE@": tp.as_multi(piece, "Polygon")})
            for piece in wide:
                for part in tp.polygons(piece):
                    measured = path_length(part)
                    made.append({"split_type": 1 if measured >= length else 3, "split_length": measured,
                                 "FID": row.oid, "SHAPE@": tp.as_multi(part, "Polygon")})
            for cone in tapers:
                made.append({"split_type": 4, "split_length": path_length(cone), "FID": row.oid,
                             "SHAPE@": tp.as_multi(cone, "Polygon")})
    tp.create_with_objectid(out, "Polygon", srid, [("split_type", "SmallInteger"), ("split_length", "Double"),
                                                   ("FID", "Integer")])
    tp.insert(out, made)
    AddMessage(f"{tool}: {len(made)} polygons written.")
    return Result(out)


# ---- ThinHydrologyLines ----------------------------------------------------------------------------------------------

def _heading(line, at_start):
    coords = list(line.coords)
    a, b = (coords[0], coords[1]) if at_start else (coords[-2], coords[-1])
    return math.atan2(b[1] - a[1], b[0] - a[0])


def thin(lines, min_length, min_spacing, hierarchy, guards, unsplit, use_angles):
    """{oid: 1 hidden or 0 visible} for a directed hydrographic network (digitised direction = flow).

    Strokes are built from each outlet upstream (Thomson & Richardson 1999, *good continuation*): at a confluence the
    stroke continues up the most important branch — lowest hierarchy, then the longest upstream length or, with
    angles, the straightest — and the other branches begin strokes of their own. A stroke shorter than the minimum
    length is hidden, unless one of its lines has hierarchy 0 or touches an intersecting feature; of two tributaries
    joining one stroke closer than the minimum spacing, the less important is hidden; a hidden stroke hides every
    stroke upstream of it."""
    # Segments: lines merged through two-line nodes when unsplit.
    starts, ends = {}, {}
    for oid, line in lines.items():
        starts.setdefault(tp.key(line.coords[0]), []).append(oid)
        ends.setdefault(tp.key(line.coords[-1]), []).append(oid)
    if unsplit:
        groups, used = [], set()
        for oid in lines:
            if oid in used:
                continue
            chain = [oid]
            used.add(oid)
            head = oid
            while True:   # walk downstream through nodes with one line in and one out
                node = tp.key(lines[head].coords[-1])
                if len(ends.get(node, [])) == 1 and len(starts.get(node, [])) == 1 and starts[node][0] not in used:
                    head = starts[node][0]
                    chain.append(head)
                    used.add(head)
                else:
                    break
            tail = oid
            while True:   # and upstream
                node = tp.key(lines[tail].coords[0])
                if len(starts.get(node, [])) == 1 and len(ends.get(node, [])) == 1 and ends[node][0] not in used:
                    tail = ends[node][0]
                    chain.insert(0, tail)
                    used.add(tail)
                else:
                    break
            groups.append(chain)
    else:
        groups = [[oid] for oid in lines]
    segments = []
    for chain in groups:
        coords = []
        for oid in chain:
            part = list(lines[oid].coords)
            coords += part if not coords else part[1:]
        segments.append({"members": chain, "line": LineString(coords),
                         "rank": min((hierarchy.get(o) if hierarchy.get(o) is not None else 99) for o in chain),
                         "locked": any(hierarchy.get(o) == 0 for o in chain),
                         "guarded": guards is not None and any(lines[o].distance(guards) < 1e-9 for o in chain)})
    into = {}
    for i, s in enumerate(segments):
        into.setdefault(tp.key(s["line"].coords[-1]), []).append(i)
    out_of = {}
    for i, s in enumerate(segments):
        out_of.setdefault(tp.key(s["line"].coords[0]), []).append(i)
    upstream_length = {}

    def up(i, seen=()):
        if i in upstream_length:
            return upstream_length[i]
        if i in seen:
            return 0.0
        s = segments[i]
        best = max((up(j, seen + (i,)) for j in into.get(tp.key(s["line"].coords[0]), [])), default=0.0)
        upstream_length[i] = s["line"].length + best
        return upstream_length[i]

    outlets = [i for i, s in enumerate(segments) if not out_of.get(tp.key(s["line"].coords[-1]))]
    stroke_of, strokes, parent = {}, [], {}
    pending = [(i, None, None) for i in outlets]
    while pending:
        start, parent_stroke, joint = pending.pop()
        if start in stroke_of:
            continue
        number = len(strokes)
        members, i = [], start
        while i is not None and i not in stroke_of:
            stroke_of[i] = number
            members.append(i)
            node = tp.key(segments[i]["line"].coords[0])
            branches = [j for j in into.get(node, []) if j not in stroke_of]
            if not branches:
                break
            here = _heading(segments[i]["line"], True)

            def importance(j):
                rank = segments[j]["rank"]
                if use_angles:
                    turn = abs((_heading(segments[j]["line"], False) - here + math.pi) % (2 * math.pi) - math.pi)
                    return (rank, turn)
                return (rank, -up(j))

            branches.sort(key=importance)
            for other in branches[1:]:
                pending.append((other, number, segments[other]["line"].coords[-1]))
            i = branches[0]
        strokes.append({"members": members, "parent": parent_stroke, "joint": joint})
        parent[number] = parent_stroke
    hidden = set()
    for n, stroke in enumerate(strokes):
        total = sum(segments[i]["line"].length for i in stroke["members"])
        keep = any(segments[i]["locked"] or segments[i]["guarded"] for i in stroke["members"])
        if total < min_length and not keep and stroke["parent"] is not None:
            hidden.add(n)
    if min_spacing:
        by_parent = {}
        for n, stroke in enumerate(strokes):
            if stroke["parent"] is not None and n not in hidden:
                by_parent.setdefault(stroke["parent"], []).append(n)
        for p, children in by_parent.items():
            main = MultiLineString([segments[i]["line"] for i in strokes[p]["members"]])
            line = linemerge(main) if len(strokes[p]["members"]) > 1 else segments[strokes[p]["members"][0]]["line"]
            if line.geom_type != "LineString":
                continue
            placed = sorted(children, key=lambda n: line.project(Point(strokes[n]["joint"])))
            for a, b in zip(placed, placed[1:]):
                if a in hidden or b in hidden:
                    continue
                gap = abs(line.project(Point(strokes[b]["joint"])) - line.project(Point(strokes[a]["joint"])))
                if gap < min_spacing:
                    def weight(n):
                        first = strokes[n]["members"][0]
                        locked = any(segments[i]["locked"] or segments[i]["guarded"] for i in strokes[n]["members"])
                        return (not locked, segments[first]["rank"], -sum(segments[i]["line"].length
                                                                             for i in strokes[n]["members"]))
                    hidden.add(max((a, b), key=weight))
    changed = True
    while changed:   # a stroke flowing into a hidden one is hidden too
        changed = False
        for n, stroke in enumerate(strokes):
            if n not in hidden and stroke["parent"] in hidden:
                hidden.add(n)
                changed = True
    result = {}
    for n, stroke in enumerate(strokes):
        for i in stroke["members"]:
            for oid in segments[i]["members"]:
                result[oid] = 1 if n in hidden else 0
    for oid in lines:
        result.setdefault(oid, 0)
    return result


def thin_hydrology_lines(in_features, invisibility_field, min_length, min_spacing, hierarchy_field,
                         intersecting_features, unsplit_lines, use_angles):
    tool = "ThinHydrologyLines"
    path = tp.dataset(in_features)
    srid = tp.srid_of(path)
    tp.projected(tool, srid)
    hidden = tp.require_field(tool, path, invisibility_field)
    rank = tp.require_field(tool, path, hierarchy_field) if tp.given(hierarchy_field) else None
    shortest = tp.linear(tool, min_length, srid)
    spacing = tp.linear(tool, min_spacing, srid, 0.0)
    unsplit = tp.flag(tool, unsplit_lines, "UNSPLIT_LINES", "NO_UNSPLIT_LINES", "UNSPLIT_LINES")
    angles = tp.flag(tool, use_angles, "USE_ANGLES", "NO_USE_ANGLES", "NO_USE_ANGLES")
    guards = None
    if tp.given(intersecting_features):
        found = []
        for each in tp.listed(intersecting_features):
            if tp.dataset(each).lower() == path.lower():
                AddWarning(f"{tool}: the input cannot also be an intersecting feature; it is left out.")
                continue
            found += [r.shape for r in tp.read(each, fields=[]) if r.shape is not None]
        guards = unary_union(found) if found else None
    rows = tp.read(in_features, fields=[rank] if rank else [])
    lines = {r.oid: tp.single_line(r.shape) for r in rows if tp.single_line(r.shape) is not None}
    hierarchy = {r.oid: r[rank] for r in rows} if rank else {}
    result = thin(lines, shortest, spacing, hierarchy, guards, unsplit, angles)
    tp.update(path, {oid: {hidden: v} for oid, v in result.items()})
    AddMessage(f"{tool}: {sum(result.values())} of {len(result)} lines hidden.")
    return Result(tp.text(in_features))


# ---- the sheet diagrams ----------------------------------------------------------------------------------------------

def _copy_class(target, srid, kind, columns, rows):
    tp.create(target, kind, srid, columns)
    tp.insert(target, rows)


def _columns_of(path):
    return [(c.name, c.type) for c in tp.schema(path)[0]]


def generate_adjoining_sheets_features(in_feature_dataset, area_of_interest, land_features, scale, clip_aoi_to_sheets,
                                       buffer_size):
    tool = "GenerateAdjoiningSheetsFeatures"
    chosen = str(scale).strip() if tp.given(scale) else "1:50000"
    if chosen.upper() != "NONE":
        raise NotBuiltError(f"arcpy.topographic.{tool}: scale '{chosen}' is not built in grapy — the area each "
                            "specification (MIL-T-89301A, MIL-T-89306) adds around the sheet is not on the page; give "
                            "scale 'None' and a buffer_size.")
    if not tp.given(buffer_size):
        raise ExecuteError(f"ERROR 000735: {tool}: buffer_size is required when scale is None.")
    clip = tp.flag(tool, clip_aoi_to_sheets, "CLIP_AOI", "DONT_CLIP_AOI", "CLIP_AOI")
    from ._topo_prod_relief import _one_area as one_area
    aoi = one_area(tool, area_of_interest)
    sheets_path = tp.dataset(area_of_interest)
    srid = tp.srid_of(sheets_path)
    reach = aoi.shape.buffer(tp.linear(tool, buffer_size, srid), join_style="mitre")
    columns = _columns_of(sheets_path)
    names = [n for n, _ in columns]
    sheets = [r for r in tp.read(sheets_path) if r.shape is not None and r.shape.intersects(reach)
              and r.shape.intersection(reach).area > 1e-12]
    area = reach.intersection(unary_union([r.shape for r in sheets])) if clip and sheets else reach
    ds = tp.text(in_feature_dataset)
    _copy_class(tp.in_dataset(ds, "ASG_SHEETS_A"), srid, "Polygon", columns,
                [{**{n: r[n] for n in names}, "SHAPE@": tp.as_multi(r.shape, "Polygon")} for r in sheets])
    _copy_class(tp.in_dataset(ds, "ASG_SHEETS_L"), srid, "Polyline", columns,
                [{**{n: r[n] for n in names}, "SHAPE@": tp.as_multi(r.shape.boundary, "Polyline")} for r in sheets])
    _copy_class(tp.in_dataset(ds, "ASG_AOI"), srid, "Polygon", columns,
                [{**{n: aoi[n] for n in names if n in aoi}, "SHAPE@": tp.as_multi(area, "Polygon")}])
    land = unary_union([r.shape for each in tp.listed(land_features) for r in tp.read(each, fields=[])
                        if r.shape is not None]) if tp.given(land_features) else None
    made = ["ASG_SHEETS_A", "ASG_SHEETS_L", "ASG_AOI"]
    if land is not None:
        water = area.difference(land)
        if water.area > 1e-12:   # the page: the coast classes only where land does not cover the area
            _copy_class(tp.in_dataset(ds, "ASG_COAST_A"), srid, "Polygon", [],
                        [{"SHAPE@": tp.as_multi(p, "Polygon")} for p in tp.polygons(water)])
            coast = land.boundary.intersection(area)
            _copy_class(tp.in_dataset(ds, "ASG_COAST_L"), srid, "Polyline", [],
                        [{"SHAPE@": MultiLineString([p])} for p in tp.parts(coast) if p.geom_type == "LineString"])
            made += ["ASG_COAST_A", "ASG_COAST_L"]
    AddMessage(f"{tool}: {len(sheets)} sheets; {', '.join(made)} written.")
    return Result(ds)


def _clipped_copy(target, source, frame, kind):
    path = tp.dataset(source)
    columns = _columns_of(path)
    rows = []
    for r in tp.read(source):
        if r.shape is None or not r.shape.intersects(frame):
            continue
        piece = r.shape.intersection(frame)
        if kind == "Polygon" and piece.area <= 1e-12:
            continue
        rows.append({**{n: r[n] for n, _ in columns}, "SHAPE@": tp.as_multi(piece, kind)})
    _copy_class(target, tp.srid_of(path), kind, columns, rows)


def generate_location_diagram_features(in_feature_dataset, area_of_interest, sheet_id_field, wac_features,
                                       onc_features, land_features, specification, tpc_features, smooth_tolerance,
                                       hydro_line_features, hydro_area_features, hydroline_minlength,
                                       hydroline_minspacing, hydroarea_minlength, hydroarea_minwidth, min_island_area):
    tool = "GenerateLocationDiagramFeatures"
    spec = tp.word(specification, "JOG")
    if spec not in ("JOG", "ONC", "TPC"):
        raise ExecuteError(f"ERROR 000800: {tool}: specification is JOG, ONC or TPC.")
    if tp.given(tpc_features):
        raise NotBuiltError(f"arcpy.topographic.{tool}: tpc_features is not built in grapy — the page says only that "
                            "they are used for the location diagram, not which output class they become.")
    if spec == "JOG" and any(tp.given(v) for v in (smooth_tolerance, hydro_line_features, hydro_area_features)):
        raise ExecuteError(f"ERROR 000800: {tool}: smoothing and hydrography apply to ONC and TPC only.")
    from ._topo_prod_relief import _one_area as one_area
    aoi = one_area(tool, area_of_interest)
    sheets_path = tp.dataset(area_of_interest)
    srid = tp.srid_of(sheets_path)
    if tp.given(sheet_id_field):
        tp.require_field(tool, sheets_path, sheet_id_field)
    x0, y0, x1, y1 = aoi.shape.bounds
    w, h = x1 - x0, y1 - y0
    frame = box(x0 - 2 * w, y0 - 2 * h, x1 + 2 * w, y1 + 2 * h)   # five sheets by five, the area of interest central
    ds = tp.text(in_feature_dataset)
    columns = _columns_of(sheets_path)
    names = [n for n, _ in columns]
    _copy_class(tp.in_dataset(ds, "LD_AOI"), srid, "Polygon", [], [{"SHAPE@": tp.as_multi(frame, "Polygon")}])
    _copy_class(tp.in_dataset(ds, "JOG_AOI"), srid, "Polygon", columns,
                [{**{n: aoi[n] for n in names if n in aoi}, "SHAPE@": tp.as_multi(aoi.shape, "Polygon")}])
    _clipped_copy(tp.in_dataset(ds, "JOG_SHEETS_A"), sheets_path, frame, "Polygon")
    sheets = [r for r in tp.read(sheets_path) if r.shape is not None and r.shape.intersection(frame).area > 1e-12]
    _copy_class(tp.in_dataset(ds, "JOG_SHEETS_L"), srid, "Polyline", columns,
                [{**{n: r[n] for n in names}, "SHAPE@": tp.as_multi(r.shape.intersection(frame).boundary, "Polyline")}
                 for r in sheets])
    made = ["LD_AOI", "JOG_AOI", "JOG_SHEETS_A", "JOG_SHEETS_L"]
    if tp.given(wac_features):
        _clipped_copy(tp.in_dataset(ds, "JOG_WAC"), wac_features, frame, "Polygon")
        made.append("JOG_WAC")
    if tp.given(onc_features):
        _clipped_copy(tp.in_dataset(ds, "JOG_ONC"), onc_features, frame, "Polygon")
        made.append("JOG_ONC")
    smoothing = tp.linear(tool, smooth_tolerance, srid, tp.linear(tool, "0.002 DecimalDegrees", srid)) \
        if spec != "JOG" else 0.0
    islands = tp.areal(tool, min_island_area, srid, 0.0)
    if tp.given(land_features):
        land = unary_union([r.shape for each in tp.listed(land_features) for r in tp.read(each, fields=[])
                            if r.shape is not None]).intersection(frame)
        if spec != "JOG" and tp.given(hydro_area_features):
            water = _thinned_areas(tool, hydro_area_features, srid, hydroarea_minlength, hydroarea_minwidth)
            land = land.difference(water)
        land = tp.smooth(land, smoothing).intersection(frame) if smoothing else land
        land = _without_small(land, islands)
        _copy_class(tp.in_dataset(ds, "JOG_COAST_A"), srid, "Polygon", [],
                    [{"SHAPE@": tp.as_multi(p, "Polygon")} for p in tp.polygons(land)])
        coast = [p for p in tp.parts(land.boundary.difference(frame.boundary)) if p.geom_type == "LineString"]
        if spec != "JOG" and tp.given(hydro_line_features):
            coast += _thinned_lines(tool, hydro_line_features, srid, hydroline_minlength, hydroline_minspacing, frame)
        _copy_class(tp.in_dataset(ds, "JOG_COAST_L"), srid, "Polyline", [],
                    [{"SHAPE@": MultiLineString([p])} for p in coast])
        made += ["JOG_COAST_A", "JOG_COAST_L"]
    AddMessage(f"{tool}: {', '.join(made)} written.")
    return Result(ds)


def _without_small(shape, limit):
    if not limit:
        return shape
    out = []
    for p in tp.polygons(shape):
        if p.area >= limit:
            out.append(Polygon(p.exterior, [r for r in p.interiors if Polygon(r).area >= limit]))
    return unary_union(out)


def _thinned_lines(tool, features, srid, min_length, min_spacing, frame):
    """Hydrographic lines inside a frame, the hidden ones of ThinHydrologyLines' rule left out."""
    lines = {}
    for each in tp.listed(features):
        for r in tp.read(each, fields=[]):
            line = tp.single_line(r.shape)
            if line is not None:
                lines[(each, r.oid)] = line
    if not lines:
        return []
    keyed = {i: line for i, line in enumerate(lines.values())}
    hidden = thin(keyed, tp.linear(tool, min_length, srid, 0.0), tp.linear(tool, min_spacing, srid, 0.0), {}, None,
                  True, False)
    return [p for i, line in keyed.items() if not hidden[i]
            for p in tp.parts(line.intersection(frame)) if p.geom_type == "LineString"]


def _thinned_areas(tool, features, srid, min_length, min_width):
    """Hydrographic areas with their narrow parts (narrower than the minimum width) and short pieces (centreline
    shorter than the minimum length) left out."""
    width = tp.linear(tool, min_width, srid, 0.0)
    length = tp.linear(tool, min_length, srid, 0.0)
    kept = []
    for each in tp.listed(features):
        for r in tp.read(each, fields=[]):
            for polygon in tp.polygons(r.shape):
                if width:
                    _, wide = narrow_split(polygon, width)
                else:
                    wide = [polygon]
                kept += [p for p in wide if not length or path_length(p) >= length]
    return unary_union(kept) if kept else Polygon()


def generate_elevation_guide_features(in_feature_dataset, area_of_interest, in_rasters, hydro_exclusion_features,
                                      spot_height_features, hydro_line_features, hydro_area_features, contour_interval,
                                      bands_minarea, smooth_tolerance, number_of_bands, height_field, search_distance,
                                      hydroline_minlength, hydroline_minspacing, hydroarea_minlength, hydroarea_minwidth,
                                      min_island_area):
    from . import _topo_prod_relief as relief
    tool = "GenerateElevationGuideFeatures"
    from ._topo_prod_relief import _one_area as one_area
    aoi = one_area(tool, area_of_interest).shape
    srid = tp.srid_of(area_of_interest)
    raster = relief.raster_of(tool, in_rasters)
    interval = float(contour_interval) if tp.given(contour_interval) else 20.0
    area = tp.areal(tool, bands_minarea, srid, tp.areal(tool, "0.00016 SquareDecimalDegrees", srid))
    smoothing = tp.linear(tool, smooth_tolerance, srid, tp.linear(tool, "0.002 DecimalDegrees", srid))
    islands = tp.areal(tool, min_island_area, srid, None)
    exclusion = unary_union([r.shape for each in tp.listed(hydro_exclusion_features) for r in tp.read(each, fields=[])
                             if r.shape is not None]) if tp.given(hydro_exclusion_features) else None
    bands = int(number_of_bands) if tp.given(number_of_bands) else None
    found = relief.bands_from_raster(tool, raster, aoi, interval, area, smoothing, exclusion, bands, srid, islands)
    ds = tp.text(in_feature_dataset)
    relief.write_bands(tp.in_dataset(ds, "EGB_BANDS_A"), srid, found)
    edges = unary_union([shape.boundary for *_, shape in found]).difference(aoi.boundary) if found else None
    _copy_class(tp.in_dataset(ds, "EGB_BANDS_L"), srid, "Polyline", [],
                [{"SHAPE@": MultiLineString([p])} for p in (tp.parts(edges) if edges is not None else [])
                 if p.geom_type == "LineString"])
    lines = _thinned_lines(tool, hydro_line_features, srid, hydroline_minlength, hydroline_minspacing, aoi) \
        if tp.given(hydro_line_features) else []
    areas = _thinned_areas(tool, hydro_area_features, srid, hydroarea_minlength, hydroarea_minwidth).intersection(aoi) \
        if tp.given(hydro_area_features) else Polygon()
    _copy_class(tp.in_dataset(ds, "EGB_HYDRO_L"), srid, "Polyline", [],
                [{"SHAPE@": MultiLineString([p])} for p in lines])
    _copy_class(tp.in_dataset(ds, "EGB_HYDRO_A"), srid, "Polygon", [],
                [{"SHAPE@": tp.as_multi(p, "Polygon")} for p in tp.polygons(areas)])
    made = ["EGB_BANDS_A", "EGB_BANDS_L", "EGB_HYDRO_A", "EGB_HYDRO_L"]
    if tp.given(spot_height_features):
        if not tp.given(height_field):
            raise ExecuteError(f"ERROR 000735: {tool}: height_field is required with spot heights.")
        field = tp.require_field(tool, tp.dataset(spot_height_features), height_field)
        distance = tp.linear(tool, search_distance, srid, 0.0)
        spots = [r for r in tp.read(spot_height_features, fields=[field]) if r.shape is not None
                 and r[field] is not None and aoi.covers(r.shape)]
        kept = []
        for r in sorted(spots, key=lambda r: (-float(r[field]), r.oid)):
            if all(r.shape.distance(k.shape) >= distance for k in kept) or not distance:
                kept.append(r)
        _copy_class(tp.in_dataset(ds, "EGB_SPOTS_P"), srid, "Point", [(field, "Double")],
                    [{field: r[field], "SHAPE@": r.shape} for r in kept])
        made.append("EGB_SPOTS_P")
    if exclusion is not None:
        coast = exclusion.boundary.intersection(aoi)
        _copy_class(tp.in_dataset(ds, "EGB_COAST_L"), srid, "Polyline", [],
                    [{"SHAPE@": MultiLineString([p])} for p in tp.parts(coast) if p.geom_type == "LineString"])
        made.append("EGB_COAST_L")
    AddMessage(f"{tool}: {', '.join(made)} written.")
    return Result(ds)
