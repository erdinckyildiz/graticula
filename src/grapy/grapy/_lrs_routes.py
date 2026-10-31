"""Routes of an LRS network: loading, calibrating, generating, concurrency and dominance — ADR-252.

A route record's shape is rebuilt from the centerlines its Centerline Sequence rows name, and its measures from its
calibration points (``_lrs_measure``). Everything that changes a route that events lie on is written to the edit log,
for Apply Event Behaviors.
"""
from __future__ import annotations

import datetime
import math

import numpy as np
import shapely
from shapely.geometry import LineString, MultiLineString, Point

from . import _lrs as L
from . import _lrs_measure as M
from ._runtime import ExecuteError


# ---- the LRS's own classes ------------------------------------------------------------------------------------------

def sequence_rows(connection, lrs, network_id, route_id=None):
    rows = L.read(connection, lrs["sequence"], None, (lrs["seq_from"], lrs["seq_to"]))
    return [r for r in rows if _int(r.get(lrs["seq_network_id"])) == int(network_id)
            and (route_id is None or str(r.get(lrs["seq_route_id"])) == str(route_id))]


def centerlines(connection, lrs):
    """{CenterlineID: Row} of the LRS's centerlines."""
    return {str(r.get(lrs["centerline_id"])): r for r in L.read(connection, lrs["centerline"])
            if r.get(lrs["centerline_id"]) is not None}


def calibration_rows(connection, lrs, network_id, route_id=None):
    rows = L.read(connection, lrs["calibration"], None, (lrs["cp_from"], lrs["cp_to"]))
    return [r for r in rows if _int(r.get(lrs["cp_network_id"])) == int(network_id)
            and (route_id is None or str(r.get(lrs["cp_route_id"])) == str(route_id))]


def _int(value):
    try:
        return int(value)
    except (TypeError, ValueError):
        return None


# ---- building one route record ---------------------------------------------------------------------------------------

def build(connection, lrs, network, row, cache=None):
    """(parts, unplaced calibration points, centerline count, effective date) of a route record: the centerlines of
    its sequence rows, merged, and measured from its calibration points — the rows whose time overlaps the record's
    and that are still active at its end, so a record shows its latest state (INFERRED, ADR-252 §5.9 (e))."""
    cache = cache if cache is not None else {}
    if "centerlines" not in cache:
        cache["centerlines"] = centerlines(connection, lrs)
        cache["sequence"] = sequence_rows(connection, lrs, network["network_id"])
        cache["points"] = calibration_rows(connection, lrs, network["network_id"])
    route_id = str(L.route_id_of(network, row))
    start, end = row.get(network["from_date"]), row.get(network["to_date"])
    # the record's latest state: the rows that overlap it and are still active at its end
    last = None if end is None else end - datetime.timedelta(microseconds=1)

    def holds(s, e):
        return L.overlaps(s, e, start, end) and L.active(s, e, last) if last is not None else \
            L.overlaps(s, e, start, end) and e is None
    used = [s for s in cache["sequence"] if str(s.get(lrs["seq_route_id"])) == route_id
            and holds(s.get(lrs["seq_from"]), s.get(lrs["seq_to"]))]
    shapes = [cache["centerlines"][str(s.get(lrs["seq_centerline_id"]))].shape for s in used
              if str(s.get(lrs["seq_centerline_id"])) in cache["centerlines"]]
    points = [p for p in cache["points"] if str(p.get(lrs["cp_route_id"])) == route_id
              and holds(p.get(lrs["cp_from"]), p.get(lrs["cp_to"]))
              and p.shape is not None and p.get(lrs["cp_measure"]) is not None]
    if not shapes:
        return None, [], 0, None
    tolerance = L.tolerance(lrs)
    xym = [(p.shape.x, p.shape.y, float(p.get(lrs["cp_measure"]))) for p in points]
    pieces = M.orient(M.merge(shapes), xym, tolerance)
    placed, unplaced = M.place(pieces, xym, tolerance)
    dates = [d for d in [s.get(lrs["seq_from"]) for s in used] + [p.get(lrs["cp_from"]) for p in points] if d]
    effective = max(dates) if dates else None
    return M.calibrate(pieces, placed), unplaced, len(shapes), effective


def classify(before, after, tolerance):
    """The activity a change of a route's shape or measures is (INFERRED, ADR-252 §5.9 (i)): None when nothing changed;
    *calibrate* when the shape is the same and the measures are not; *reverse* when the same shape runs the other way;
    *extend* when the new shape holds the old and more; *carto_realign* when the shape moved but the measure range is
    the old one; *realign* otherwise."""
    old, new = M.parts(before), M.parts(after)
    if not old or not new or M.extent(old) is None:
        return None
    flat_old = shapely.force_2d(before)
    flat_new = shapely.force_2d(after)
    same_shape = flat_old.difference(flat_new.buffer(tolerance)).length <= tolerance * 4 and \
        flat_new.difference(flat_old.buffer(tolerance)).length <= tolerance * 4
    if same_shape:
        if all(len(a) == len(b) and np.allclose(a[:, :2], b[:, :2], atol=tolerance)
               and np.allclose(a[:, 3], b[:, 3], atol=tolerance, equal_nan=True) for a, b in zip(old, new)) \
                and len(old) == len(new):
            return None
        start_old, start_new = old[0][0, :2], new[0][0, :2]
        end_old = old[-1][-1, :2]
        if np.allclose(start_new, end_old, atol=tolerance) and not np.allclose(start_new, start_old, atol=tolerance):
            return "reverse"
        return "calibrate"
    if flat_old.difference(flat_new.buffer(tolerance)).length <= tolerance * 4:
        return "extend"
    e_old, e_new = M.extent(old), M.extent(new)
    if e_new and abs(e_old[0] - e_new[0]) <= tolerance and abs(e_old[1] - e_new[1]) <= tolerance:
        return "carto_realign"
    return "realign"


def generate(file, lrs, network, where=None, today=None):
    """Every route record of the network (within ``where``) rebuilt; answers (details lines, records changed)."""
    tolerance = L.tolerance(lrs)
    connection = L.connect(file)
    lines, changes = [], []
    try:
        cache = {}
        for row in L.route_rows(connection, network, where):
            pieces, unplaced, found, effective = build(connection, lrs, network, row, cache)
            route_id = L.route_id_of(network, row)
            if found == 0:
                lines.append(f"Route {route_id}: no centerline in the Centerline Sequence for its time; shape kept.")
                continue
            for x, y, m in unplaced:
                lines.append(f"Route {route_id}: the calibration point at ({x}, {y}) measure {m} is not on the route.")
            if M.extent(pieces) is None:
                lines.append(f"Route {route_id}: no calibration point; measures are not set.")
            shape = M.line(pieces)
            activity = classify(row.shape, shape, tolerance)
            start = row.get(network["from_date"])
            when = effective if effective and (start is None or effective > start) else start
            when = when or today or datetime.datetime.combine(datetime.date.today(), datetime.time())
            changes.append((row.oid, shape, route_id, activity, when, row.shape))
        connection.commit()
    finally:
        connection.close()
    for oid, shape, route_id, activity, when, before in changes:
        L.update(file, network["table"], oid, {"SHAPE@": shape})
    connection = L.connect(file)
    try:
        for oid, shape, route_id, activity, when, before in changes:
            if activity:
                L.log(connection, network["table"], route_id, activity, when, before, shape)
                lines.append(f"Route {route_id}: {activity} on {when.date().isoformat()} logged for Apply Event "
                             "Behaviors.")
        connection.commit()
    finally:
        connection.close()
    return lines, len(changes)


def generate_derived(file, lrs, line_network):
    """A line network's derived network rebuilt: each line's current routes in LineOrder, joined, measured from 0 by
    length in the derived network's unit (INFERRED, ADR-252 §5.9 (j); the pages say only that a derived route starts
    at zero). The derived route's ID is the LineID and its name the LineName."""
    derived_table = line_network.get("derived")
    if not derived_table:
        return 0
    connection = L.connect(file)
    try:
        derived = L.network_get(connection, derived_table)
        lines = {}
        for row in L.routes_active(connection, line_network, None):
            line_id = row.get(line_network["line_id"])
            if line_id is None or row.shape is None:
                continue
            lines.setdefault(str(line_id), []).append(row)
        old = [r.oid for r in L.read(connection, derived_table)]
        connection.commit()
    finally:
        connection.close()
    L.delete(file, derived_table, old)
    ratio = L.unit_ratio(lrs["srid"], derived.get("unit", "METERS"))
    records = []
    for line_id, rows in sorted(lines.items()):
        rows.sort(key=lambda r: (r.get(line_network["line_order"]) is None, r.get(line_network["line_order"]) or 0))
        pieces, run = [], 0.0
        for row in rows:
            for p in M.parts(row.shape):
                q = p.copy()
                d = M.cumulative(p)
                q[:, 3] = (run + d) * ratio
                run += d[-1]
                pieces.append(q)
        starts = [r.get(line_network["from_date"]) for r in rows if r.get(line_network["from_date"])]
        record = {derived["route_id"]: line_id, derived["from_date"]: max(starts) if starts else None,
                  derived["to_date"]: None, "SHAPE@": M.line(pieces)}
        if derived.get("route_name") and line_network.get("line_name"):
            record[derived["route_name"]] = rows[0].get(line_network["line_name"])
        records.append(record)
    L.insert(file, derived_table, records)
    return len(records)


# ---- route ID configuration ------------------------------------------------------------------------------------------

def compose_route_id(network, values, lookups_ok=None):
    """A multi-field route ID: each part padded as ModifyRouteIdPadding configured it, then joined. INFERRED (§5.9
    (g)): LEFT_AND_RIGHT puts the odd pad character on the right."""
    out = []
    padding = network.get("padding") or {}
    for name in network.get("individual_fields") or []:
        value = values.get(name.lower()) if isinstance(values, dict) else values.get(name)
        rule = padding.get(name.lower())
        if lookups_ok is not None:
            lookups_ok(name, value)
        if value is None or str(value) == "":
            if rule and rule["allow_null"]:
                out.append(rule["character"] * rule["length"] if rule["pad_if_null"] else "")
                continue
            if rule is None:
                out.append("")
                continue
            raise ExecuteError(f"ERROR 000800: the route ID field '{name}' is null, which its padding does not allow.")
        text = str(value)
        if rule:
            if len(text) > rule["length"]:
                raise ExecuteError(f"ERROR 000800: '{text}' is longer than the {rule['length']} characters of '{name}'.")
            if rule["enabled"] and not rule["variable"]:
                extra = rule["length"] - len(text)
                if rule["location"] == "LEFT":
                    text = rule["character"] * extra + text
                elif rule["location"] == "RIGHT":
                    text = text + rule["character"] * extra
                else:
                    text = rule["character"] * (extra // 2) + text + rule["character"] * (extra - extra // 2)
        out.append(text)
    return "".join(out)


# ---- concurrency and dominance ---------------------------------------------------------------------------------------

def concurrencies(file, lrs, network, moment, all_time):
    """[(section, centerline shape, [(route row, from measure, to measure)], from date, to date)]: one section for each
    centerline that two or more routes of the network share at the same time (only shared centerlines count, as the
    page says). INFERRED (§5.9 (h)): a section is one centerline, not a run of them."""
    connection = L.connect(file)
    try:
        lines = centerlines(connection, lrs)
        sequence = sequence_rows(connection, lrs, network["network_id"])
        routes = L.route_rows(connection, network)
    finally:
        connection.close()
    tolerance = L.tolerance(lrs)
    by_centerline = {}
    for s in sequence:
        by_centerline.setdefault(str(s.get(lrs["seq_centerline_id"])), []).append(s)
    out = []
    for cid, rows in sorted(by_centerline.items()):
        if cid not in lines:
            continue
        pairs = []
        for s in rows:
            for r in routes:
                if str(L.route_id_of(network, r)) != str(s.get(lrs["seq_route_id"])):
                    continue
                start = _later(r.get(network["from_date"]), s.get(lrs["seq_from"]))
                end = _earlier(r.get(network["to_date"]), s.get(lrs["seq_to"]))
                if start is not None and end is not None and start >= end:
                    continue
                if not all_time and not L.active(start, end, moment):
                    continue
                pairs.append((r, start, end))
        ids = {str(L.route_id_of(network, r)) for r, _, _ in pairs}
        if len(ids) < 2:
            continue
        shape = lines[cid].shape
        # one section per time window shared by all its routes (INFERRED): grouped by the window's bounds
        start = max((p[1] for p in pairs if p[1]), default=None)
        ends = [p[2] for p in pairs if p[2]]
        end = min(ends) if ends else None
        members = []
        ends_xy = M.parts(shape)
        first, last = ends_xy[0][0], ends_xy[-1][-1]
        for r, _, _ in pairs:
            pieces = M.parts(r.shape)
            a = M.locate(pieces, first[0], first[1], tolerance)
            b = M.locate(pieces, last[0], last[1], tolerance)
            ma = a[0][0] if a else math.nan
            mb = b[0][0] if b else math.nan
            members.append((r, min(ma, mb) if not (math.isnan(ma) or math.isnan(mb)) else math.nan,
                            max(ma, mb) if not (math.isnan(ma) or math.isnan(mb)) else math.nan))
        out.append((L.guid(), shape, members, start, end))
    return out


def _later(a, b):
    return b if a is None else a if b is None else max(a, b)


def _earlier(a, b):
    return b if a is None else a if b is None else min(a, b)


def dominance(file, network, members):
    """{route oid: (flag, error)} for the routes of one section, by the network's dominance rules in their configured
    order, a later rule breaking an earlier one's tie (INFERRED, §5.9 (h)). Errors as the page numbers them: 0 none,
    1 a tie (grapy then picks the smallest route ID, where Pro picks at random), 2 a null or missing value, 3 more than
    one event value on the section (INFERRED meaning), 4 not calibrated on the section."""
    rules = network.get("dominance") or []
    candidates = list(members)
    errors = {r.oid: 0 for r, _, _ in members}
    for r, a, b in members:
        if math.isnan(a) or math.isnan(b):
            errors[r.oid] = 4
    candidates = [m for m in candidates if errors[m[0].oid] == 0] or candidates
    for rule in rules:
        if len(candidates) < 2:
            break
        values = {}
        for r, a, b in candidates:
            value, error = _rule_value(file, network, rule, r, a, b)
            if error:
                errors[r.oid] = error
            values[r.oid] = value
        exceptions = rule.get("exceptions") or []
        chosen = None
        for exception in exceptions:
            hit = [m for m in candidates if values[m[0].oid] is not None and str(values[m[0].oid]) == str(exception)]
            if hit:
                chosen = hit
                break
        if chosen is None:
            valued = [m for m in candidates if values[m[0].oid] is not None]
            if not valued:
                continue
            key = (lambda v: float(v)) if rule.get("order_type") == "NUMERIC" else (lambda v: str(v))
            try:
                ordered = sorted(valued, key=lambda m: key(values[m[0].oid]))
            except ValueError:
                for m in valued:
                    errors[m[0].oid] = 2
                continue
            best = key(values[(ordered[-1] if rule.get("order_method") == "GREATER" else ordered[0])[0].oid])
            chosen = [m for m in valued if key(values[m[0].oid]) == best]
        candidates = chosen
    flags = {r.oid: 0 for r, _, _ in members}
    candidates.sort(key=lambda m: str(L.route_id_of(network, m[0])))
    if candidates:
        flags[candidates[0][0].oid] = 1
        if len(candidates) > 1:
            for m in candidates:
                if errors[m[0].oid] == 0:
                    errors[m[0].oid] = 1
    return {k: (flags[k], errors[k]) for k in flags}


def _rule_value(file, network, rule, route, a, b):
    """A route's value for a rule on one section: the rule's fields concatenated in their order, read from the route
    record or from the source event that covers the section's middle on that route."""
    fields = rule.get("fields") or []
    if not rule.get("source") or rule["source"] == network["table"]:
        record = route
    else:
        connection = L.connect(file)
        try:
            event = L.event_get(connection, rule["source"])
            rows = L.read(connection, rule["source"], None, (event["from_date"], event["to_date"]))
        finally:
            connection.close()
        middle = (a + b) / 2 if not (math.isnan(a) or math.isnan(b)) else math.nan
        hits = [e for e in rows if str(e.get(event["route_id"])) == str(L.route_id_of(network, route))
                and e.get(event["to_date"]) is None and e.get(event["measure"]) is not None
                and e.get(event["to_measure"]) is not None
                and min(e.get(event["measure"]), e.get(event["to_measure"])) <= middle
                <= max(e.get(event["measure"]), e.get(event["to_measure"]))]
        if not hits:
            return None, 2
        if len(hits) > 1:
            return None, 3
        record = hits[0]
    parts = [record.get(f) for f in fields]
    if not parts or any(p is None for p in parts):
        return None, 2
    return "".join(str(int(p)) if isinstance(p, float) and p.is_integer() else str(p) for p in parts), 0


# ---- planar overlay of centerlines -----------------------------------------------------------------------------------

def overlay_centerlines(shapes, tolerance):
    """The centerlines' 2D overlay: [(piece, {indices of the centerlines covering it})], pieces of one cover merged.
    The overlay is shapely's noding union (GEOS), standing in for the Intersect and Simplify the page names."""
    flat = [shapely.force_2d(s) for s in shapes]
    noded = shapely.unary_union(flat)
    segments = list(noded.geoms) if hasattr(noded, "geoms") else [noded]
    groups = {}
    for seg in segments:
        if seg.length <= tolerance:
            continue
        middle = seg.interpolate(0.5, normalized=True)
        cover = frozenset(i for i, f in enumerate(flat) if f.distance(middle) <= tolerance)
        groups.setdefault(cover, []).append(seg)
    out = []
    for cover, segs in groups.items():
        merged = shapely.line_merge(shapely.union_all(segs)) if len(segs) > 1 else segs[0]
        out.append((merged, set(cover)))
    return out


def with_z(piece, source):
    """A 2D piece given z from the centerline it came from, interpolated along it."""
    src = M.parts(source)
    if not src:
        return piece
    pieces = list(piece.geoms) if hasattr(piece, "geoms") else [piece]
    rows = []
    for g in pieces:
        coords = []
        for x, y in g.coords:
            z = 0.0
            for p in src:
                near = M.candidates([p], x, y, math.inf)
                if near:
                    _, _, _, i, t = min(near, key=lambda c: c[2])
                    z = float(p[i, 2] + t * (p[i + 1, 2] - p[i, 2]))
                    break
            coords.append((x, y, z))
        rows.append(LineString(coords))
    return MultiLineString(rows)


def point_on(pieces, m):
    found = M.at(pieces, m)
    return None if found is None else Point(found)
