"""Building and editing parcels — the tools of ADR-250 that change a fabric's polygons, lines, points and records.

Each function follows its tool page; what a page leaves open is INFERRED in ADR-250 §5.8 and named where it is chosen.
"""
from __future__ import annotations

import math

import shapely
from shapely.geometry import LineString, Point

from . import _parcel as pf
from ._runtime import AddMessage, AddWarning, ExecuteError


class Snap:
    """Coordinates within the tolerance of one already seen answer that one — a grid of cells the tolerance wide."""

    def __init__(self, tolerance):
        self.tolerance = max(float(tolerance), 1e-12)
        self.cells = {}

    def _cell(self, xy):
        return int(math.floor(xy[0] / self.tolerance)), int(math.floor(xy[1] / self.tolerance))

    def find(self, xy):
        cx, cy = self._cell(xy)
        best, distance = None, None
        for i in (-1, 0, 1):
            for j in (-1, 0, 1):
                for held in self.cells.get((cx + i, cy + j), ()):
                    d = math.hypot(held[0] - xy[0], held[1] - xy[1])
                    if d <= self.tolerance and (distance is None or d < distance):
                        best, distance = held, d
        return best

    def add(self, xy):
        found = self.find(xy)
        if found is not None:
            return found
        xy = (float(xy[0]), float(xy[1]))
        self.cells.setdefault(self._cell(xy), []).append(xy)
        return xy


def _key(snap, a, b):
    return frozenset((snap.add(a), snap.add(b)))


def _in_scope(feature, box, guid):
    if guid is not None and not pf.same_guid(feature.get_ci("CreatedByRecord"), guid):
        return False
    if box is not None and (feature.shape is None or not feature.shape.intersects(box)):
        return False
    return True


def _corners(ring, tolerance):
    """The vertices of a closed ring that are not collinear with their neighbours (offset beyond the tolerance)."""
    points = ring[:-1]
    out = []
    n = len(points)
    for i, p in enumerate(points):
        a, b = points[i - 1], points[(i + 1) % n]
        if pf.offset(p, a, b) > tolerance or n <= 3:
            out.append(p)
    return out


def _pieces(ring, nodes, tolerance):
    """A ring cut into two-point lines between consecutive nodes: the nodes lying on a segment are inserted, and the
    ring is walked from node to node (INFERRED, §5.8 (j): a neighbour's corner or an existing point on a polygon's
    edge splits its line, so adjacent boundaries share one line)."""
    dense = []
    for a, b in zip(ring, ring[1:]):
        dense.append(a)
        on = [n for n in nodes if pf.on_segment(n, a, b, tolerance)]
        on.sort(key=lambda n: math.hypot(n[0] - a[0], n[1] - a[1]))
        dense.extend(on)
    node_set = Snap(tolerance)
    for n in nodes:
        node_set.add(n)
    marks = [i for i, p in enumerate(dense) if node_set.find(p) is not None]
    pieces = []
    for k, start in enumerate(marks):
        a, b = dense[start], dense[marks[(k + 1) % len(marks)]]
        if len(marks) > 1 and not pf.near(a, b, tolerance):
            pieces.append((a, b))
    return pieces


def _record_priority(fab):
    """Records ranked for a shared boundary: most recent Recorded Date, then most recent creation date (editor
    tracking's created_date), as documented; equal ones by object id (the page's "randomly", made repeatable)."""
    ranked = {}
    for record in pf.records(fab):
        key = (str(record.get_ci("RecordedDate") or ""), str(record.get_ci("created_date") or ""), record.oid)
        ranked[str(record.get_ci("GlobalID") or "").upper()] = key
    return ranked


def _best_record(guids, ranked):
    known = [g for g in guids if g]
    if not known:
        return None
    return max(known, key=lambda g: ranked.get(str(g).upper(), ("", "", -1)))


def _floor(feature):
    return feature.get_ci("FloorOrder")


def _faces(lines):
    shapes = [f.shape for f in lines if f.shape is not None and not f.shape.is_empty]
    if not shapes:
        return []
    noded = shapely.node(shapely.union_all(shapes))
    return [p for p in shapely.get_parts(shapely.polygonize(list(shapely.get_parts(noded)))) if p.area > 0]


def build(fab, box, guid):
    """BuildParcelFabric: seeds built from lines, lines built from polygons, missing points created, records refreshed."""
    tolerance = fab.tolerance
    ranked = _record_priority(fab)
    built_lines = []        # (type, Feature-like dict) of every line in scope, to give points to
    touched = set()
    for each in fab.types:
        polygons = pf.read(fab.file, each.polygons)
        lines = [f for f in pf.read(fab.file, each.lines) if pf.current(f)]
        # 1. from lines and seeds
        seeds = [p for p in polygons if pf.current(p) and p.get_ci("IsSeed") == pf.YES and _in_scope(p, box, guid)]
        changes = {}
        for seed in seeds:
            group = [ln for ln in lines if not each.strata or _floor(ln) == _floor(seed)]
            faces = _faces(group)
            found = []
            for part in shapely.get_parts(seed.shape):
                inside = part.representative_point()
                face = next((f for f in faces if f.covers(inside)), None)
                if face is None:
                    found = None
                    break
                found.append(face)
            if not found:
                AddWarning(f"WARNING: the seed {seed.oid} of '{each.name}' lies in no closed loop of lines; it is not "
                           "built.")
                continue
            changes[seed.oid] = {"shape": shapely.union_all(found), "IsSeed": pf.NO}
            touched.add(seed.get_ci("CreatedByRecord"))
        pf.update(fab.file, each.polygons, changes)
        polygons = pf.read(fab.file, each.polygons)
        # 2. from polygons
        current_polygons = [p for p in polygons if pf.current(p) and p.get_ci("IsSeed") != pf.YES
                            and pf.polygon_of(p.shape) is not None]
        scope = [p for p in current_polygons if _in_scope(p, box, guid)]
        snap = Snap(tolerance)
        nodes = []
        for p in current_polygons:
            for ring in pf.rings(p.shape):
                nodes.extend(_corners(ring, tolerance))
        for point in pf.read(fab.file, fab.points):
            if pf.current(point) and point.shape is not None:
                nodes.append((point.shape.x, point.shape.y))
        for ln in lines:
            nodes.extend(pf.ends(ln.shape))
        unique = Snap(tolerance)
        nodes = [n for n in nodes if unique.find(n) is None and unique.add(n)]
        existing = {_key(snap, *pf.ends(ln.shape)) for ln in lines if ln.shape is not None}
        made = {}
        for p in scope:
            for ring in pf.rings(p.shape):
                for a, b in _pieces(ring, nodes, tolerance):
                    key = _key(snap, a, b)
                    if key in existing:
                        continue
                    made.setdefault(key, {"a": snap.add(a), "b": snap.add(b), "owners": []})["owners"].append(p)
        new_rows = []
        for held in made.values():
            owners = held["owners"]
            record = _best_record([o.get_ci("CreatedByRecord") for o in owners], ranked)
            row = {"CreatedByRecord": record, "shape": LineString([held["a"], held["b"]])}
            if each.strata:
                row["FloorOrder"] = _floor(owners[0])
            new_rows.append(row)
            touched.add(record)
        pf.insert(fab.file, each.lines, new_rows)
        if new_rows:
            AddMessage(f"{len(new_rows)} lines built in '{each.name}'.")
        for ln in pf.read(fab.file, each.lines):
            if pf.current(ln) and ln.shape is not None and _in_scope(ln, box, guid):
                built_lines.append(ln)
    for ln in pf.read(fab.file, fab.connections):
        if pf.current(ln) and ln.shape is not None and _in_scope(ln, box, guid):
            built_lines.append(ln)
    # 3. missing points
    point_snap = Snap(fab.tolerance)
    for point in pf.read(fab.file, fab.points):
        if pf.current(point) and point.shape is not None:
            point_snap.add((point.shape.x, point.shape.y))
    new_points = []
    for ln in built_lines:
        for xy in pf.ends(ln.shape):
            if point_snap.find(xy) is None:
                point_snap.add(xy)
                new_points.append({"CreatedByRecord": ln.get_ci("CreatedByRecord"), "shape": Point(xy)})
    pf.insert(fab.file, fab.points, new_points)
    if new_points:
        AddMessage(f"{len(new_points)} points built.")
    _push_cogo_accuracy(fab)
    pf.refresh_records(fab)


def _push_cogo_accuracy(fab):
    """A record's COGO Accuracy given to every line it created, when the records and the lines carry that field (it is
    optional since fabric version 3, and grapy does not add it)."""
    held = {c.name.lower() for c in pf.columns_of(fab.file, fab.records)}
    if "cogoaccuracy" not in held:
        return
    values = {str(r.get_ci("GlobalID")).upper(): r.get_ci("COGOAccuracy") for r in pf.records(fab)
              if r.get_ci("COGOAccuracy") is not None}
    for table in fab.line_classes():
        if "cogoaccuracy" not in {c.name.lower() for c in pf.columns_of(fab.file, table)}:
            continue
        name = next(c.name for c in pf.columns_of(fab.file, table) if c.name.lower() == "cogoaccuracy")
        changes = {ln.oid: {name: values[str(ln.get_ci("CreatedByRecord")).upper()]} for ln in pf.read(fab.file, table)
                   if str(ln.get_ci("CreatedByRecord")).upper() in values}
        pf.update(fab.file, table, changes)


def create_seeds(fab, record_name):
    record = pf.record_named(fab, record_name)
    if record is None:
        raise ExecuteError(f"ERROR 000800: CreateParcelSeeds: there is no record '{record_name}'.")
    guid = record.get_ci("GlobalID")
    made = 0
    for each in fab.types:
        lines = [ln for ln in pf.read(fab.file, each.lines)
                 if pf.current(ln) and pf.same_guid(ln.get_ci("CreatedByRecord"), guid)]
        polygons = [p for p in pf.read(fab.file, each.polygons) if pf.current(p) and p.shape is not None]
        floors = sorted({_floor(ln) for ln in lines}, key=lambda v: (v is None, v)) if each.strata else [None]
        rows = []
        for floor in floors:
            group = [ln for ln in lines if not each.strata or _floor(ln) == floor]
            for face in _faces(group):
                inside = face.representative_point()
                if any((not each.strata or _floor(p) == floor) and
                       (p.shape.covers(inside) or face.covers(p.shape.representative_point())) for p in polygons):
                    continue
                row = {"CreatedByRecord": guid, "IsSeed": pf.YES, "shape": pf.minimized_polygon(face)}
                if each.strata:
                    row["FloorOrder"] = floor
                rows.append(row)
        pf.insert(fab.file, each.polygons, rows)
        made += len(rows)
    AddMessage(f"{made} seeds created.")
    return made


# ---- deleting -----------------------------------------------------------------------------------------------------

_POINT_SYSTEM = {"globalid", "createdbyrecord", "retiredbyrecord", "created_user", "created_date", "last_edited_user",
                 "last_edited_date", "preserve", "isfixed", "validationstatus"}


def _covered_by(line, boundary, tolerance):
    return line.shape is not None and boundary is not None and line.shape.within(boundary.buffer(tolerance))


def delete_parcels(fab, parcel_type, polygons):
    tolerance = fab.tolerance
    gone = {p.oid for p in polygons}
    every = pf.read(fab.file, parcel_type.polygons)
    remaining = [p for p in every if p.oid not in gone and p.shape is not None]
    deleted_boundary = shapely.union_all([p.shape.boundary for p in polygons if p.shape is not None]) if polygons else None
    remaining_boundary = shapely.union_all([p.shape.boundary for p in remaining]) if remaining else None
    lines = pf.read(fab.file, parcel_type.lines)
    doomed = [ln for ln in lines if _covered_by(ln, deleted_boundary, tolerance)
              and not _covered_by(ln, remaining_boundary, tolerance)]
    guids = {p.get_ci("CreatedByRecord") for p in polygons} | {p.get_ci("RetiredByRecord") for p in polygons}
    guids |= {ln.get_ci("CreatedByRecord") for ln in doomed}
    pf.delete(fab.file, parcel_type.polygons, gone)
    pf.delete(fab.file, parcel_type.lines, [ln.oid for ln in doomed])
    # points no other parcel uses
    used = Snap(tolerance)
    for table in fab.line_classes():
        for ln in pf.read(fab.file, table):
            if ln.shape is not None:
                for xy in pf.line_coordinates(ln.shape):
                    used.add(xy)
    for each in fab.types:
        for p in pf.read(fab.file, each.polygons):
            if p.shape is not None:
                for ring in pf.rings(p.shape):
                    for xy in ring:
                        used.add(xy)
    candidates = Snap(tolerance)
    for p in polygons:
        if p.shape is not None:
            for ring in pf.rings(p.shape):
                for xy in ring:
                    candidates.add(xy)
    for ln in doomed:
        for xy in pf.line_coordinates(ln.shape):
            candidates.add(xy)
    points = []
    for point in pf.read(fab.file, fab.points):
        if point.shape is None:
            continue
        xy = (point.shape.x, point.shape.y)
        if candidates.find(xy) is None or used.find(xy) is not None:
            continue
        if point.get_ci("Preserve") == pf.YES or point.get_ci("IsFixed") == pf.YES:
            continue
        if any(v not in (None, "") for k, v in point.items() if k.lower() not in _POINT_SYSTEM):
            continue
        points.append(point.oid)
    pf.delete(fab.file, fab.points, points)
    AddMessage(f"{len(gone)} parcels, {len(doomed)} lines and {len(points)} points deleted.")
    pf.refresh_records(fab, [g for g in guids if g])


# ---- selecting and labelling ----------------------------------------------------------------------------------------


def related_lines(fab, parcel_type, polygon, lines):
    """The lines of a parcel: those of its type on its boundary, historic ones for a historic parcel where they
    cover the boundary, else current ones (INFERRED, §5.8 (k))."""
    boundary = polygon.shape.boundary
    on = [ln for ln in lines if _covered_by(ln, boundary, fab.tolerance)]
    if pf.current(polygon):
        return [ln for ln in on if pf.current(ln)]
    historic = [ln for ln in on if not pf.current(ln)]
    covered = shapely.union_all([ln.shape for ln in historic]) if historic else None
    if covered is not None and boundary.difference(covered.buffer(fab.tolerance)).length <= fab.tolerance:
        return historic
    return historic + [ln for ln in on if pf.current(ln)
                       and (covered is None or not ln.shape.within(covered.buffer(fab.tolerance)))]


def select_parcel_features(fab, parcel_type, polygons):
    lines = pf.read(fab.file, parcel_type.lines)
    chosen, historic_wanted = {}, False
    for polygon in polygons:
        if polygon.shape is None:
            continue
        historic_wanted |= not pf.current(polygon)
        for ln in related_lines(fab, parcel_type, polygon, lines):
            chosen[ln.oid] = ln
    places = Snap(fab.tolerance)
    for ln in chosen.values():
        for xy in pf.line_coordinates(ln.shape):   # endpoints and polyline inflections
            places.add(xy)
    points = [p for p in pf.read(fab.file, fab.points) if p.shape is not None and places.find((p.shape.x, p.shape.y))]
    by_place = {}
    for p in points:
        by_place.setdefault(places.find((p.shape.x, p.shape.y)), []).append(p)
    picked = set()
    for group in by_place.values():
        if historic_wanted:
            historic = [p for p in group if not pf.current(p)]
            picked.update(p.oid for p in (historic or group))
        else:
            current = [p for p in group if pf.current(p)]
            picked.update(p.oid for p in (current or group))
    return sorted(chosen), sorted(picked)


def _side(line_shape, polygon, tolerance):
    """RIGHT when the polygon lies to the right of the line's direction, else LEFT: a point a little off the middle of
    the line, to its right, is tested."""
    coordinates = pf.line_coordinates(line_shape)
    total = line_shape.length
    walked = 0.0
    for a, b in zip(coordinates, coordinates[1:]):
        length = math.hypot(b[0] - a[0], b[1] - a[1])
        if walked + length >= total / 2.0 and length > 0:
            t = (total / 2.0 - walked) / length
            middle = (a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]))
            step = max(tolerance * 10.0, length * 1e-3)
            right = (middle[0] + (b[1] - a[1]) / length * step, middle[1] - (b[0] - a[0]) / length * step)
            return pf.RIGHT if polygon.covers(Point(right)) else pf.LEFT
        walked += length
    return None


def label_positions(fab, parcel_type, lines):
    polygons = [p for p in pf.read(fab.file, parcel_type.polygons)
                if pf.current(p) and pf.polygon_of(p.shape) is not None and p.get_ci("IsSeed") != pf.YES]
    changes = {}
    for ln in lines:
        if not pf.current(ln) or ln.shape is None:
            continue
        on = [p for p in polygons if _covered_by(ln, p.shape.boundary, fab.tolerance)]
        if len(on) >= 2:
            changes[ln.oid] = {"LabelPosition": pf.CENTERED}
        elif len(on) == 1:
            side = _side(ln.shape, on[0].shape, fab.tolerance)
            if side is not None:
                changes[ln.oid] = {"LabelPosition": side}
    pf.update(fab.file, parcel_type.lines, changes)
    return len(changes)


# ---- merging collinear lines ----------------------------------------------------------------------------------------

ROUNDING = 1.0 / 3600.0   # INFERRED (§5.8 (l)): directions within one second of arc are "within numerical rounding limits"


def _centre(a, b, radius):
    """The centre of the circle of signed radius through a and b: positive radius, the centre to the right of a→b
    (a clockwise curve), negative to the left (INFERRED, §5.8 (l): the page does not sign the radius)."""
    chord = math.hypot(b[0] - a[0], b[1] - a[1])
    r = abs(radius)
    if chord == 0 or chord > 2 * r:
        return None
    middle = ((a[0] + b[0]) / 2.0, (a[1] + b[1]) / 2.0)
    h = math.sqrt(max(r * r - chord * chord / 4.0, 0.0))
    ux, uy = (b[0] - a[0]) / chord, (b[1] - a[1]) / chord
    side = 1.0 if radius > 0 else -1.0
    return middle[0] + side * uy * h, middle[1] - side * ux * h


def _angle_close(a, b):
    if a is None or b is None:
        return False
    d = abs((a - b + 180.0) % 360.0 - 180.0)
    return d <= ROUNDING


def merge_collinear(fab, table, inputs, tolerance):
    """Pairs of connected lines merged where the shared point lies within ``tolerance`` of the chord (or of the
    curve through the outer ends); repeated until no pair is left. Answers the number of merges."""
    selected = {f.oid for f in inputs if pf.current(f) and f.shape is not None}
    merges = 0
    while True:
        lines = {f.oid: f for f in pf.read(fab.file, table)}
        at = {}
        snap = Snap(fab.tolerance)
        for other in fab.line_classes():
            for ln in pf.read(fab.file, other):
                if ln.shape is None:
                    continue
                for xy in pf.ends(ln.shape):
                    at.setdefault(snap.add(xy), []).append((other, ln.oid))
        points = {}
        for p in pf.read(fab.file, fab.points):
            if p.shape is not None:
                found = snap.find((p.shape.x, p.shape.y))
                if found is not None:
                    points.setdefault(found, []).append(p)
        done = False
        for node, owners in at.items():
            if len(owners) != 2 or any(t != table for t, _ in owners):
                continue
            (_, first), (_, second) = owners
            if first == second or first not in selected or second not in selected:
                continue
            l1, l2 = lines[first], lines[second]
            if not (pf.current(l1) and pf.current(l2)):
                continue
            if not pf.same_guid(l1.get_ci("CreatedByRecord"), l2.get_ci("CreatedByRecord")) and \
                    (l1.get_ci("CreatedByRecord") or l2.get_ci("CreatedByRecord")):
                continue
            merged = _merge(l1, l2, node, snap, tolerance)
            if merged is None:
                continue
            new_oid = pf.insert(fab.file, table, [merged])[0]
            pf.delete(fab.file, table, [first, second])
            keep = [p.oid for p in points.get(node, []) if p.get_ci("Preserve") == pf.YES]
            pf.delete(fab.file, fab.points, [p.oid for p in points.get(node, []) if p.oid not in keep])
            selected -= {first, second}
            selected.add(new_oid)
            merges += 1
            done = True
            break
        if not done:
            return merges


def _oriented(line, node, snap):
    """(coordinates ending at the node, direction as stored for that orientation)."""
    coordinates = pf.line_coordinates(line.shape)
    direction = line.get_ci("Direction")
    if snap.find(coordinates[-1]) == node:
        return coordinates, direction, line.get_ci("Radius")
    radius = line.get_ci("Radius")
    return (list(reversed(coordinates)), None if direction is None else (direction + 180.0) % 360.0,
            None if radius is None else -radius)


def _merge(l1, l2, node, snap, tolerance):
    c1, d1, r1 = _oriented(l1, node, snap)
    c2, d2, r2 = _oriented(l2, node, snap)
    c2 = list(reversed(c2))
    d2 = None if d2 is None else (d2 + 180.0) % 360.0
    r2 = None if r2 is None else -r2
    a, p, b = c1[0], c1[-1], c2[-1]
    if (r1 is None) != (r2 is None):
        return None
    if r1 is None:
        if pf.offset(p, a, b) > tolerance:
            return None
    else:
        if abs(abs(r1) - abs(r2)) > tolerance or (r1 > 0) != (r2 > 0):
            return None
        centre = _centre(a, b, r1)
        if centre is None or abs(math.hypot(p[0] - centre[0], p[1] - centre[1]) - abs(r1)) > tolerance:
            return None
    longest = l1 if l1.shape.length >= l2.shape.length else l2
    row = {k: v for k, v in longest.items() if k.lower() not in ("globalid", "created_user", "created_date",
                                                                  "last_edited_user", "last_edited_date")}
    for name in ("Distance", "ArcLength"):
        v1, v2 = l1.get_ci(name), l2.get_ci(name)
        row[name] = (v1 + v2) if v1 is not None and v2 is not None else None
    direction = (d1 if longest is l1 else d2) if _angle_close(d1, d2) else None
    row["Direction"] = direction
    row["Radius"] = r1
    row["COGOType"] = pf.COMPUTED
    row["shape"] = LineString(c1 + c2[1:])
    return row


# ---- records and duplicates --------------------------------------------------------------------------------------


def create_records(fab, table, features, names):
    """Each feature given the record its name names — made when there is none; a blank name, no record — then every
    record's polygon and counts refreshed, as documented."""
    guids, changes = {}, {}
    for feature, name in zip(features, names):
        if name is None or str(name).strip() == "":
            continue
        key = str(name).strip().lower()
        if key not in guids:
            guids[key] = pf.ensure_record(fab, str(name).strip())
        changes[feature.oid] = {"CreatedByRecord": guids[key]}
    pf.update(fab.file, table, changes)
    pf.refresh_records(fab)
    AddMessage(f"{len(changes)} features associated with {len(guids)} records.")
    return len(guids)


_NOT_COPIED = {"globalid", "createdbyrecord", "retiredbyrecord", "created_user", "created_date", "last_edited_user",
               "last_edited_date", "fid"}


def _copied(source, source_columns, target_columns):
    """The source's values of the fields the target also has with the same name and type (help topic *Duplicate
    parcels*)."""
    target = {c.name.lower(): c for c in target_columns}
    out = {}
    for column in source_columns:
        found = target.get(column.name.lower())
        if found is not None and found.type == column.type and column.name.lower() not in _NOT_COPIED:
            out[found.name] = source.get(column.name)
    return out


def duplicate(fab, source_type, polygons, guid, target_type, repeat, update_field, start, increment):
    with_lines = target_type is not source_type or target_type.strata
    source_polygon_columns = pf.columns_of(fab.file, source_type.polygons)
    target_polygon_columns = pf.columns_of(fab.file, target_type.polygons)
    source_line_columns = pf.columns_of(fab.file, source_type.lines)
    target_line_columns = pf.columns_of(fab.file, target_type.lines)
    lines = pf.read(fab.file, source_type.lines) if with_lines else []
    new_polygons, new_lines = [], []
    for k in range(repeat):
        value = start + k * increment
        taken = set()
        for polygon in polygons:
            row = _copied(polygon, source_polygon_columns, target_polygon_columns)
            row.update({"CreatedByRecord": guid, "RetiredByRecord": None, "shape": polygon.shape})
            if update_field is not None:
                row[update_field] = value
            new_polygons.append(row)
            if not with_lines or polygon.shape is None:
                continue
            for ln in related_lines(fab, source_type, polygon, lines):
                if ln.oid in taken:
                    continue
                taken.add(ln.oid)
                line_row = _copied(ln, source_line_columns, target_line_columns)
                line_row.update({"CreatedByRecord": guid, "RetiredByRecord": None, "shape": ln.shape})
                if target_type.strata:
                    line_row["FloorOrder"] = value if update_field and update_field.lower() == "floororder" \
                        else row.get("FloorOrder")
                new_lines.append(line_row)
    pf.insert(fab.file, target_type.polygons, new_polygons)
    pf.insert(fab.file, target_type.lines, new_lines)
    pf.refresh_records(fab, [guid])
    AddMessage(f"{len(new_polygons)} parcels and {len(new_lines)} lines duplicated.")
    return len(new_polygons), len(new_lines)

