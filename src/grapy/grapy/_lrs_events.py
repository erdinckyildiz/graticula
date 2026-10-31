"""Events on LRS routes: their shapes, behaviours after route edits, overlay and translation — ADR-252.

An event's shape is the route between its measures (Make Route Event Layer's dynamic segmentation), on the route
record of its RouteID that holds its From Date. A spanning event runs from its route to its To Route along their line,
in LineOrder. The LocError words are INFERRED (§5.9 (f)): "No Error", "Route Not Found", "No Match" and
"Partial Match", the words the Retire and Translate Event Measures pages show.
"""
from __future__ import annotations

import math

import shapely

from . import _lrs as L
from . import _lrs_measure as M

NO_ERROR, ROUTE_NOT_FOUND, NO_MATCH, PARTIAL = "No Error", "Route Not Found", "No Match", "Partial Match"


def is_line(event):
    return event.get("kind") == "LINE"


def event_dates(event):
    return (event["from_date"], event["to_date"])


def event_rows(connection, event, where=None):
    return L.read(connection, event["table"], where, event_dates(event))


def line_routes(connection, network, first, last, start, end):
    """The route records from ``first`` to ``last`` along their line, in LineOrder; None when they are not on one
    line."""
    if not network.get("line"):
        return None
    a = L.route_for(connection, network, first, start, end)
    b = L.route_for(connection, network, last, start, end)
    if a is None or b is None:
        return None
    line = a.get(network["line_id"])
    if line is None or str(line) != str(b.get(network["line_id"])):
        return None
    oa, ob = a.get(network["line_order"]) or 0, b.get(network["line_order"]) or 0
    rows = [r for r in L.route_rows(connection, network) if str(r.get(network["line_id"])) == str(line)
            and L.overlaps(r.get(network["from_date"]), r.get(network["to_date"]), start, end)]
    lo, hi = min(oa, ob), max(oa, ob)
    rows = [r for r in rows if lo <= (r.get(network["line_order"]) or 0) <= hi]
    rows.sort(key=lambda r: r.get(network["line_order"]) or 0, reverse=oa > ob)
    unique, seen = [], set()
    for r in rows:
        key = str(L.route_id_of(network, r))
        if key not in seen:
            seen.add(key)
            unique.append(r)
    return unique


def shape_of(connection, network, event, row):
    """(shape, LocError) of one event record from its route and measures."""
    route_id = row.get(event["route_id"])
    start, end = row.get(event["from_date"]), row.get(event["to_date"])
    if route_id is None:
        return None, ROUTE_NOT_FOUND
    if not is_line(event):
        m = row.get(event["measure"])
        record = L.route_for(connection, network, route_id, start, end)
        if record is None:
            return None, ROUTE_NOT_FOUND
        if m is None:
            return None, NO_MATCH
        found = M.at(M.parts(record.shape), float(m))
        return (None, NO_MATCH) if found is None else (M.point(*found, float(m)), NO_ERROR)
    a, b = row.get(event["measure"]), row.get(event["to_measure"])
    if a is None or b is None:
        return None, NO_MATCH
    a, b = float(a), float(b)
    to_route = row.get(event.get("to_route_id")) if event.get("spans") else None
    if to_route is not None and str(to_route) != str(route_id):
        records = line_routes(connection, network, route_id, to_route, start, end)
        if not records:
            return None, ROUTE_NOT_FOUND
        pieces, partial = [], False
        for i, record in enumerate(records):
            parts = M.parts(record.shape)
            span = M.extent(parts)
            if span is None:
                partial = True
                continue
            lo = a if i == 0 else span[0]
            hi = b if i == len(records) - 1 else span[1]
            if not M.covered(parts, lo, hi):
                partial = True
            pieces += M.between(parts, lo, hi)
        if not pieces:
            return None, NO_MATCH
        return M.line(pieces), PARTIAL if partial else NO_ERROR
    record = L.route_for(connection, network, route_id, start, end)
    if record is None:
        return None, ROUTE_NOT_FOUND
    parts = M.parts(record.shape)
    pieces = M.between(parts, a, b)
    if not pieces:
        return None, NO_MATCH
    return M.line(pieces), NO_ERROR if M.covered(parts, a, b) else PARTIAL


def generate(file, event, network, bypass=False, where=None):
    """Every record's shape and LocError regenerated; answers the details lines."""
    connection = L.connect(file)
    changes, lines = [], []
    try:
        for row in event_rows(connection, event, where):
            if bypass and (row.get(event["route_id"]) is None or row.get(event["measure"]) is None or
                           (is_line(event) and row.get(event["to_measure"]) is None)):
                lines.append(f"Event {row.get(event['event_id'])}: a null LRS field; bypassed.")
                continue
            shape, error = shape_of(connection, network, event, row)
            changes.append((row.oid, shape, error))
            if error != NO_ERROR:
                lines.append(f"Event {row.get(event['event_id'])}: {error}.")
    finally:
        connection.close()
    for oid, shape, error in changes:
        values = {"SHAPE@": shape}
        if event.get("loc_error"):
            values[event["loc_error"]] = error
        L.update(file, event["table"], oid, values)
    return lines


# ---- locating shapes on routes --------------------------------------------------------------------------------------

def locate_line(parts, shape, tolerance):
    """[(from, to)] measure ranges of a line shape's pieces that lie on a route (within the tolerance)."""
    route = M.line(parts)
    if route is None or shape is None:
        return []
    flat = shapely.force_2d(shape).intersection(shapely.force_2d(route).buffer(tolerance))
    flat = shapely.line_merge(flat) if flat.geom_type in ("MultiLineString", "GeometryCollection") and \
        not flat.is_empty and all(g.geom_type == "LineString" for g in getattr(flat, "geoms", [flat])) else flat
    out = []
    for g in (getattr(flat, "geoms", None) or [flat]):
        if g.is_empty or g.geom_type != "LineString" or g.length <= tolerance:
            continue
        ms = []
        for x, y in (g.coords[0], g.coords[-1]):
            found = M.locate(parts, x, y, tolerance * 2)
            if found:
                ms.append(found[0][0])
        if len(ms) == 2:
            out.append((min(ms), max(ms)))
    return sorted(out)


def event_geometry(parts, event, row):
    """An event's shape on a given route (by its measures), as parts or a point."""
    if is_line(event):
        a, b = row.get(event["measure"]), row.get(event["to_measure"])
        if a is None or b is None:
            return None
        return M.line(M.between(parts, float(a), float(b)))
    m = row.get(event["measure"])
    found = M.at(parts, float(m)) if m is not None else None
    return None if found is None else shapely.Point(found)


# ---- Apply Event Behaviors ------------------------------------------------------------------------------------------

def apply(file, lrs, network, event, entries):
    """The event behaviours of one event class applied for the edit log's entries (in log order). Answers the
    details lines."""
    tolerance = L.tolerance(lrs)
    lines = []
    for entry in entries:
        rule = (event.get("rules") or L.DEFAULT_RULES).get(entry["activity"], L.DEFAULT_RULES[entry["activity"]])
        lines += _apply_one(file, lrs, network, event, entry, rule, tolerance)
    return lines


def _apply_one(file, lrs, network, event, entry, rule, tolerance):
    effective = entry["effective"]
    old_parts = M.parts(entry["before"])
    connection = L.connect(file)
    try:
        current = L.routes_active(connection, network, effective, entry["route_id"])
        new_parts = M.parts(current[0].shape) if current else []
        rows = [r for r in event_rows(connection, event) if str(r.get(event["route_id"])) == entry["route_id"]
                and L.active(r.get(event["from_date"]), r.get(event["to_date"]), effective)]
        others = [r for r in L.routes_active(connection, network, effective)
                  if str(L.route_id_of(network, r)) != entry["route_id"]]
    finally:
        connection.close()
    lines = []
    if rule in ("MOVE", "HONOR_ROUTE_MEASURE"):
        return [f"{entry['activity']} on route {entry['route_id']}: {rule}, measures kept."]
    for row in rows:
        old_shape = event_geometry(old_parts, event, row)
        outcome = _stay_put(old_shape, event, row, new_parts, tolerance)
        if rule == "HONOR_REFERENT_LOCATION":
            lines += _referent(file, lrs, network, event, row, new_parts, tolerance)
            continue
        unchanged = outcome == [_measures(event, row)]
        if rule == "RETIRE":
            if not unchanged:
                _close(file, event, row, effective, [])
                lines.append(f"Event {row.get(event['event_id'])}: retired ({entry['activity']}).")
            continue
        if rule == "SNAP":
            outcome = outcome + _snap(old_shape, event, new_parts, others, network, tolerance)
        if rule == "COVER":
            outcome = _cover(outcome, old_parts, new_parts, tolerance)
        if unchanged and rule != "COVER":
            continue
        if outcome == [_measures(event, row)]:
            continue
        _close(file, event, row, effective, outcome)
        lines.append(f"Event {row.get(event['event_id'])}: {rule} ({entry['activity']}) -> "
                     + ("retired" if not outcome else "; ".join(_describe(o) for o in outcome)))
    return lines


def _measures(event, row):
    if is_line(event):
        a, b = row.get(event["measure"]), row.get(event["to_measure"])
        return (None, round(float(a), 9), round(float(b), 9)) if a is not None and b is not None else None
    m = row.get(event["measure"])
    return (None, round(float(m), 9), None) if m is not None else None


def _describe(outcome):
    route, a, b = outcome
    where = f" on {route}" if route else ""
    return f"{a}" + (f"-{b}" if b is not None else "") + where


def _stay_put(old_shape, event, row, new_parts, tolerance):
    """[(other route or None, from, to)] where the event's old location lies on the new route; empty when it is off."""
    if old_shape is None or not new_parts:
        return []
    if not is_line(event):
        found = M.locate(new_parts, old_shape.x, old_shape.y, tolerance)
        return [(None, round(found[0][0], 9), None)] if found else []
    return [(None, round(a, 9), round(b, 9)) for a, b in locate_line(new_parts, old_shape, tolerance)]


def _snap(old_shape, event, new_parts, others, network, tolerance):
    """The parts of the event's old location that are off its route, onto another route there (a concurrent one)."""
    if old_shape is None:
        return []
    rest = shapely.force_2d(old_shape)
    if new_parts:
        rest = rest.difference(shapely.force_2d(M.line(new_parts)).buffer(tolerance))
    if rest.is_empty:
        return []
    out = []
    for other in others:
        parts = M.parts(other.shape)
        if not is_line(event):
            found = M.locate(parts, old_shape.x, old_shape.y, tolerance)
            if found:
                return [(str(L.route_id_of(network, other)), round(found[0][0], 9), None)]
            continue
        for a, b in locate_line(parts, rest, tolerance):
            out.append((str(L.route_id_of(network, other)), round(a, 9), round(b, 9)))
    return out


def _cover(outcome, old_parts, new_parts, tolerance):
    """Stay Put, then the new section of the route — where the old route was not — taken by the event that touches it
    (INFERRED, §5.9 (l))."""
    if not new_parts or not outcome or outcome[0][2] is None:
        return outcome
    old = M.line(old_parts)
    new = M.line(new_parts)
    added = shapely.force_2d(new) if old is None else shapely.force_2d(new).difference(
        shapely.force_2d(old).buffer(tolerance))
    sections = locate_line(new_parts, added, tolerance) if not added.is_empty else []
    out = []
    for route, a, b in outcome:
        for lo, hi in sections:
            if route is None and (abs(b - lo) <= tolerance * 2 or abs(a - hi) <= tolerance * 2 or
                                  (a <= lo and hi <= b)):
                a, b = min(a, lo), max(b, hi)
        out.append((route, round(a, 9), round(b, 9)))
    return out


def _close(file, event, row, effective, outcome):
    """The record closed on the effective date (deleted when it began then or later) and the outcome's records opened
    on that date: the first on the event's own route keeps its EventID, any other gets a new one."""
    start = row.get(event["from_date"])
    in_place = effective is None or (start is not None and start >= effective)
    template = {k: row.get(k) for k in row}
    if in_place:
        L.delete(file, event["table"], [row.oid])
    else:
        L.update(file, event["table"], row.oid, {event["to_date"]: effective})
    records = []
    kept = False
    for route, a, b in outcome:
        record = {k: v for k, v in template.items()}
        connection = L.connect(file)
        try:
            names = {k: c.name for k, c in L.columns(connection, event["table"]).items()}
            real = {names[k]: v for k, v in record.items() if k in names}
        finally:
            connection.close()
        real[event["from_date"]] = start if in_place else effective
        real[event["to_date"]] = row.get(event["to_date"])
        if route is not None:
            real[event["route_id"]] = route
        if kept or route is not None:
            real[event["event_id"]] = L.guid()
        else:
            kept = True
        real[event["measure"]] = a
        if is_line(event):
            real[event["to_measure"]] = b
        records.append(real)
    L.insert(file, event["table"], records)


def _referent(file, lrs, network, event, row, new_parts, tolerance):
    """Honor Referent Location: the event keeps its offset from its referent. INFERRED (§5.9 (l)): the referent
    location holds an IntersectionId of an intersection class on the network, and the offset is in the referent's
    offset units."""
    referent = event.get("referent")
    if not referent:
        return [f"Event {row.get(event['event_id'])}: no referent fields enabled; measures kept."]
    location = row.get(referent["from_location"])
    offset = row.get(referent["from_offset"])
    if location is None or offset is None:
        return [f"Event {row.get(event['event_id'])}: no referent location; measures kept."]
    connection = L.connect(file)
    try:
        found = None
        for inter in L.intersections(connection, network["table"]):
            for r in L.read(connection, inter["table"]):
                if str(r.get(inter["intersection_id"])) == str(location) and r.shape is not None:
                    found = r.shape
                    break
    finally:
        connection.close()
    if found is None:
        return [f"Event {row.get(event['event_id'])}: referent {location} not found; measures kept."]
    at = M.locate(new_parts, found.x, found.y, tolerance)
    if not at:
        return [f"Event {row.get(event['event_id'])}: referent {location} is off the route; measures kept."]
    ratio = L.UNITS[referent.get("units", "MILES")] / L.UNITS[network.get("unit", "METERS")]
    measure = at[0][0] + float(offset) * ratio
    values = {event["measure"]: measure}
    if is_line(event) and row.get(event["to_measure"]) is not None:
        values[event["to_measure"]] = measure + float(row.get(event["to_measure"])) - float(row.get(event["measure"]))
    L.update(file, event["table"], row.oid, values)
    return [f"Event {row.get(event['event_id'])}: HONOR_REFERENT_LOCATION -> {measure}."]


def locate_on_network(connection, network, shape, moment, tolerance, kind):
    """[(route row, from, to)] of a shape on a network's routes active on ``moment``: a point's measure (to None), or a
    line's measure range where it lies on the route."""
    out = []
    for r in L.routes_active(connection, network, moment):
        parts = M.parts(r.shape)
        if kind == "POINT":
            p = shape if shape.geom_type == "Point" else shape.representative_point()
            found = M.locate(parts, p.x, p.y, tolerance)
            if found:
                out.append((r, found[0][0], None))
        else:
            for a, b in locate_line(parts, shape, tolerance):
                out.append((r, a, b))
    return out


def math_isnan(value):
    return value is None or (isinstance(value, float) and math.isnan(value))
