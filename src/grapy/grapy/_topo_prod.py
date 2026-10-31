"""arcpy.topographic's shared machinery and its editing tools (ADR-251).

Every tool here reads and writes through ``arcpy.da``'s cursors, so a layer's definition query and selection are
honoured as arcpy honours them: a tool given a layer with a selection works on the selected features, and on all the
layer shows when none is selected. Tools that *modify the input* (as nearly all of this toolbox's pages warn) change
the dataset under the layer in place.
"""
from __future__ import annotations

import math
import os

import shapely
from shapely.geometry import LineString, MultiLineString, MultiPolygon, Point, Polygon
from shapely.ops import linemerge, unary_union

from . import _gpkg
from ._result import Result
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError

TOLERANCE = 1e-9


# ---- parameters ------------------------------------------------------------------------------------------------------

def text(value):
    from . import _layers
    if isinstance(value, _layers.Layer):
        return value.name
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


def given(value):
    if isinstance(value, (list, tuple)):
        return any(given(v) for v in value)
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


def word(value, default):
    return str(value).strip().upper() if given(value) else default


def flag(tool, value, yes, no, default):
    """A Boolean parameter given as its keyword or as True/False."""
    if value is True:
        return True
    if value is False:
        return False
    chosen = word(value, default)
    if chosen in (yes, "TRUE"):
        return True
    if chosen in (no, "FALSE"):
        return False
    raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not {yes} or {no}.")


def listed(value):
    """A multivalue parameter: a list, or text with ``;`` between the values."""
    if value is None:
        return []
    if isinstance(value, (list, tuple)):
        return [v for v in value if given(v)]
    from .management import _list
    return [v for v in _list(value) if given(v)]


def table_rows(value):
    """A value table: a list of lists, or text with ``;`` between rows and spaces (or quotes) between cells."""
    import shlex
    out = []
    for row in listed(value) if not isinstance(value, str) else _split(value):
        if isinstance(row, (list, tuple)):
            out.append(list(row))
        else:
            lexer = shlex.shlex(str(row), posix=True)
            lexer.whitespace_split, lexer.escape = True, ""   # a Windows path keeps its backslashes
            out.append(list(lexer))
    return out


def _split(value):
    rows, current, quote = [], "", None
    for c in str(value):
        if quote:
            current += c
            if c == quote:
                quote = None
        elif c in "'\"":
            quote = c
            current += c
        elif c == ";":
            rows.append(current)
            current = ""
        else:
            current += c
    rows.append(current)
    return [r.strip() for r in rows if r.strip()]


def number(value, default=None):
    if not given(value):
        return default
    if isinstance(value, (int, float)):
        return float(value)
    return float(str(value).split()[0])


# ---- datasets --------------------------------------------------------------------------------------------------------

def source(value):
    """(dataset path, where clause) a value shows: a layer's dataset with its query and selection, or a path."""
    from .management import _source
    return _source(value)


def dataset(value):
    return source(value)[0]


def schema(path):
    from .management import _schema
    return _schema(path)


def oid_of(path):
    from .management import _oid_of
    return _oid_of(path)


def srid_of(value):
    return schema(dataset(value))[2]


def exists(path):
    try:
        connection, table = _gpkg.table_of(path)
    except Exception:  # noqa: BLE001 — a missing file or table
        return False
    try:
        return connection.execute("select 1 from sqlite_master where name = ?", (table,)).fetchone() is not None
    finally:
        connection.close()


def field_names(path):
    return [c.name for c in schema(path)[0]]


def find_field(path, name):
    """The field of a dataset whose name is ``name`` without regard to case, or None."""
    for column in schema(path)[0]:
        if column.name.lower() == str(name).lower():
            return column.name
    return None


def require_field(tool, path, name):
    found = find_field(path, name)
    if found is None:
        raise ExecuteError(f"ERROR 000728: {tool}: the field '{name}' does not exist in '{path}'.")
    return found


class Row(dict):
    """A feature: its object ID (``oid``), shape (``shape``, shapely) and field values by name."""


def read(value, fields=None, where=None, shapes=True):
    """The rows a value shows, each a ``Row`` of every field (or those named), its OID and its shape."""
    from . import da
    path, shown = source(value)
    columns, kind, _, _ = schema(path)
    names = [c.name for c in columns] if fields is None else list(fields)
    with_shape = shapes and kind != "Geometry"
    clauses = [f"({c})" for c in (shown, where) if c]
    tokens = ["OID@", *names] + (["SHAPE@WKB"] if with_shape else [])
    out = []
    with da.SearchCursor(path, tokens, " and ".join(clauses) or None) as rows:
        for r in rows:
            row = Row(zip(names, r[1:1 + len(names)]))
            row.oid = r[0]
            row.shape = shapely.from_wkb(bytes(r[-1])) if with_shape and r[-1] is not None else None
            out.append(row)
    return out


def update(path, changes):
    """``changes``: {oid: {field: value, or "SHAPE@": shapely}} written to the dataset."""
    from . import da
    if not changes:
        return
    oid = oid_of(path)
    fields = sorted({f for c in changes.values() for f in c})
    ids = ", ".join(str(int(i)) for i in changes)
    with da.UpdateCursor(path, ["OID@", *fields], f'"{oid}" in ({ids})') as rows:
        for row in rows:
            change = changes.get(row[0])
            if change is None:
                continue
            rows.updateRow([row[0], *[change.get(f, row[1 + i]) for i, f in enumerate(fields)]])


def delete(path, oids):
    from . import da
    oids = set(oids)
    if not oids:
        return
    oid = oid_of(path)
    with da.UpdateCursor(path, ["OID@"], f'"{oid}" in ({", ".join(str(int(i)) for i in oids)})') as rows:
        for _ in rows:
            rows.deleteRow()


def insert(path, rows):
    """Rows (dicts of field values, the shape under ``SHAPE@``) appended; the new object IDs answered."""
    from . import da
    if not rows:
        return []
    fields = sorted({f for r in rows for f in r})
    with da.InsertCursor(path, fields) as cursor:
        return [cursor.insertRow([r.get(f) for f in fields]) for r in rows]


def create(path, shape, srid, columns, has_z=False):
    """A new feature class (or table, shape ``Geometry`` and no reference) replacing any of that name."""
    _gpkg.create(path, shape, srid, [_gpkg.Column(n, t) for n, t in columns], has_z)
    return path


def create_with_objectid(path, shape, srid, columns):
    """A new feature class whose object ID column is ``OBJECTID``, for an output whose page names a field ``FID``:
    SQLite compares column names without case, so a field ``FID`` cannot sit beside grapy's usual ``fid`` key."""
    _gpkg.create(path, shape, srid, [], False)
    connection, table = _gpkg.table_of(path)
    try:
        connection.execute(f'alter table "{table}" rename column "{_gpkg.OID}" to "OBJECTID"')
        for name, kind in columns:
            connection.execute(f'alter table "{table}" add column "{name}" {_gpkg._TYPES.get(kind.upper(), "TEXT")}')
        connection.commit()
    finally:
        connection.close()
    return path


def in_dataset(feature_dataset, name):
    return f"{text(feature_dataset).rstrip('/')}/{name}"


# ---- units -------------------------------------------------------------------------------------------------------------

_DEGREE_METRES = 111319.490793  # one degree of the WGS 84 equator (2πa/360), for a decimal-degree tolerance on projected data


def per_unit(srid):
    """Metres in one unit of a reference; None for a geographic or unknown one."""
    from .management import _per_unit
    return _per_unit(srid)


def linear(tool, value, srid, default=None):
    """A linear unit (``"20 Meters"``, ``"0.002 DecimalDegrees"``) in the data's units; a bare number is in them."""
    if not given(value):
        return default
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    if len(parts) == 1 or parts[1].upper() == "UNKNOWN":
        return float(parts[0])
    unit = parts[1].upper()
    per = per_unit(srid)
    if unit in ("DECIMALDEGREES", "DEGREES"):
        return float(parts[0]) if per is None else float(parts[0]) * _DEGREE_METRES / per
    from . import _units
    metres = _units.linear(unit)
    if metres is None:
        raise NotBuiltError(f"arcpy.topographic.{tool}: the linear unit '{parts[1]}' is not a unit grapy reads.")
    if per is None:
        return float(parts[0]) * metres / _DEGREE_METRES
    return float(parts[0]) * metres / per


def areal(tool, value, srid, default=None):
    """An areal unit (``"500 SquareMeters"``) in the data's square units; a bare number is in them."""
    if not given(value):
        return default
    if isinstance(value, (int, float)):
        return float(value)
    parts = str(value).split()
    if len(parts) == 1 or parts[1].upper() == "UNKNOWN":
        return float(parts[0])
    if parts[1].upper() in ("SQUAREDECIMALDEGREES", "DECIMALDEGREES"):
        per = per_unit(srid)
        return float(parts[0]) if per is None else float(parts[0]) * (_DEGREE_METRES / per) ** 2
    from . import _units
    square = _units.areal(parts[1])
    per = per_unit(srid)
    if square is None:
        raise NotBuiltError(f"arcpy.topographic.{tool}: the areal unit '{parts[1]}' is not a unit grapy reads.")
    return float(parts[0]) * square / ((per or _DEGREE_METRES) ** 2)


def projected(tool, srid):
    """The page's rule: a missing or unknown reference fails, a geographic one is warned about."""
    if srid <= 0:
        raise ExecuteError(f"ERROR 000622: {tool}: the input has no coordinate system.")
    if per_unit(srid) is None:
        AddWarning(f"{tool}: the input is not projected; distances are in degrees.")


# ---- geometry ----------------------------------------------------------------------------------------------------------

def parts(shape):
    if shape is None or shape.is_empty:
        return []
    return list(shape.geoms) if hasattr(shape, "geoms") else [shape]


def polygons(shape):
    return [p for p in parts(shape) if p.geom_type == "Polygon"]


def as_multi(shape, kind):
    """A shape as its class stores it."""
    if shape is None or shape.is_empty:
        return shape
    if kind == "Polygon":
        found = [p for p in parts(shape) if p.geom_type == "Polygon"]
        return MultiPolygon(found) if found else None
    if kind == "Polyline":
        found = [p for p in parts(shape) if p.geom_type == "LineString"]
        return MultiLineString(found) if found else None
    return shape


def single_line(shape):
    """The one LineString of a single-part line, or None."""
    found = parts(shape)
    return found[0] if len(found) == 1 and found[0].geom_type == "LineString" else None


def key(point, digits=9):
    return (round(point[0], digits), round(point[1], digits))


def shared_length(a, b):
    """The length of boundary two areas share."""
    found = a.boundary.intersection(b.boundary)
    return sum(p.length for p in parts(found) if p.geom_type in ("LineString", "LinearRing"))


def angle_at(prev, at, nxt):
    """The angle (0–180°) a vertex makes with its two neighbours."""
    ax, ay = prev[0] - at[0], prev[1] - at[1]
    bx, by = nxt[0] - at[0], nxt[1] - at[1]
    la, lb = math.hypot(ax, ay), math.hypot(bx, by)
    if la == 0 or lb == 0:
        return None
    cosine = max(-1.0, min(1.0, (ax * bx + ay * by) / (la * lb)))
    return math.degrees(math.acos(cosine))


def chaikin(coords, closed, rounds=2):
    """Chaikin's corner cutting (Chaikin 1974), each round replacing every corner by points ¼ and ¾ along its two
    edges; a line keeps its end points."""
    points = [tuple(c[:2]) for c in coords]
    for _ in range(rounds):
        if closed:
            ring = points[:-1]
            out = []
            for i in range(len(ring)):
                p, q = ring[i], ring[(i + 1) % len(ring)]
                out += [(0.75 * p[0] + 0.25 * q[0], 0.75 * p[1] + 0.25 * q[1]),
                        (0.25 * p[0] + 0.75 * q[0], 0.25 * p[1] + 0.75 * q[1])]
            points = out + [out[0]]
        else:
            out = [points[0]]
            for p, q in zip(points, points[1:]):
                out += [(0.75 * p[0] + 0.25 * q[0], 0.75 * p[1] + 0.25 * q[1]),
                        (0.25 * p[0] + 0.75 * q[0], 0.25 * p[1] + 0.75 * q[1])]
            points = out + [points[-1]]
    return points


def smooth(shape, tolerance):
    """A shape simplified by Douglas–Peucker at ``tolerance`` and then smoothed by Chaikin's corner cutting; nothing
    is done at a tolerance of 0."""
    if not tolerance or shape is None or shape.is_empty:
        return shape
    shape = shape.simplify(tolerance, preserve_topology=True)
    if shape.geom_type in ("Polygon", "MultiPolygon"):
        out = []
        for p in polygons(shape):
            shell = chaikin(p.exterior.coords, True)
            holes = [chaikin(r.coords, True) for r in p.interiors]
            made = Polygon(shell, holes)
            out.append(made if made.is_valid else made.buffer(0))
        return unary_union(out)
    if shape.geom_type in ("LineString", "MultiLineString"):
        return unary_union([LineString(chaikin(p.coords, p.is_closed)) for p in parts(shape)])
    return shape


# ---- ApplyFeatureLevelMetadata, CalculateDefaultValues -----------------------------------------------------------------

_TOKENS = ("USER", "UTC_DATE", "LOCAL_DATE")


def apply_feature_level_metadata(in_features, in_metadata_table, metadata_favorite):
    import datetime
    import json
    tool = "ApplyFeatureLevelMetadata"
    table = dataset(in_metadata_table)
    name_field = require_field(tool, table, "MetadataFavoriteName")
    token_field = require_field(tool, table, "MetadataTokens")
    favourites = [r for r in read(table, shapes=False) if r[name_field] == str(metadata_favorite)]
    if not favourites:
        raise ExecuteError(f"ERROR 000800: {tool}: the favorite '{metadata_favorite}' is not in the metadata table.")
    favourite = favourites[0]
    tokens = {}
    if favourite[token_field]:
        try:
            document = json.loads(favourite[token_field])
        except ValueError as said:
            raise ExecuteError(f"ERROR 000800: {tool}: MetadataTokens is not JSON: {said}.") from None
        for entry in document if isinstance(document, list) else [document]:
            for field, token in entry.items():
                if str(token).upper() not in _TOKENS:
                    raise ExecuteError(f"ERROR 000800: {tool}: '{token}' is not one of {', '.join(_TOKENS)}.")
                if find_field(table, field) is None:
                    raise ExecuteError(f"ERROR 000728: {tool}: the token field '{field}' is not in the metadata table.")
                tokens[field.lower()] = str(token).upper()
    from ._behaviours import user
    now = {"USER": user(), "UTC_DATE": datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None),
           "LOCAL_DATE": datetime.datetime.now()}
    table_types = {c.name.lower(): c for c in schema(table)[0]}
    skip = {name_field.lower(), token_field.lower()}
    inputs = listed(in_features)
    plans = []
    for each in inputs:
        path = dataset(each)
        mine = {c.name.lower(): c for c in schema(path)[0]}
        common = [n for n in table_types if n in mine and n not in skip]
        for n in common:   # the page: field types must match, or the tool stops before it changes anything
            if mine[n].type != table_types[n].type:
                raise ExecuteError(f"ERROR 000800: {tool}: '{mine[n].name}' is {mine[n].type} in '{path}' but "
                                   f"{table_types[n].type} in the metadata table.")
        plans.append((each, path, {mine[n].name: (now[tokens[n]] if n in tokens else favourite[table_types[n].name])
                                   for n in common}))
    for each, path, values in plans:
        changes = {r.oid: dict(values) for r in read(each, fields=[], shapes=False)}
        update(path, changes)
        AddMessage(f"{tool}: {len(changes)} features of '{path}' took the favorite '{metadata_favorite}'.")
    return Result(*[text(i) for i in inputs])


def calculate_default_values(in_datasets):
    from . import _subtypes
    out = []
    for each in listed(in_datasets):
        path = dataset(each)
        connection, table = _gpkg.table_of(path)
        try:
            defaults = _subtypes.defaults(connection, table)
            subtype = _subtypes.field(connection, table)
        finally:
            connection.close()
        out.append(text(each))
        if not defaults:
            continue
        names = field_names(path)
        changes = {}
        for row in read(each, shapes=False):
            code = row.get(subtype[0]) if subtype else None
            change = {}
            for name in names:
                if row[name] is not None:
                    continue
                value = defaults.get((name.lower(), code), defaults.get((name.lower(), None)))
                if code is not None and (name.lower(), code) not in defaults:
                    value = defaults.get((name.lower(), None))
                if value is not None:
                    change[name] = value
            if change:
                changes[row.oid] = change
        update(path, changes)
        AddMessage(f"CalculateDefaultValues: {len(changes)} rows of '{path}' took default values.")
    return Result(*out)


# ---- EliminatePolygon -------------------------------------------------------------------------------------------------

def eliminate_polygon(in_features, surrounding_features, min_area):
    tool = "EliminatePolygon"
    path = dataset(in_features)
    srid = srid_of(path)
    projected(tool, srid)
    limit = areal(tool, min_area, srid)
    candidates = [r for r in read(in_features, fields=[]) if r.shape is not None
                  and (limit is None or r.shape.area < limit)]
    taken = {r.oid for r in candidates}
    neighbours = []  # [layer order, path, oid, shape]
    for order, each in enumerate(listed(surrounding_features)):
        other = dataset(each)
        if srid_of(other) != srid:
            raise ExecuteError(f"ERROR 000622: {tool}: '{other}' has another spatial reference than the input.")
        same = os.path.normcase(other) == os.path.normcase(path)
        for row in read(other, fields=[]):
            if row.shape is None or (same and row.oid in taken):
                continue
            neighbours.append([order, other, row.oid, row.shape])
    changed = {}
    for candidate in sorted(candidates, key=lambda r: r.oid):
        best, length = None, 0.0
        for entry in neighbours:
            shared = shared_length(candidate.shape, entry[3])
            if shared > length + TOLERANCE:
                best, length = entry, shared
        if best is not None:
            best[3] = unary_union([best[3], candidate.shape])
            changed[(best[1], best[2])] = best[3]
    by_path = {}
    for (other, oid), shape in changed.items():
        by_path.setdefault(other, {})[oid] = {"SHAPE@": as_multi(shape, "Polygon")}
    for other, changes in by_path.items():
        update(other, changes)
    delete(path, taken)
    AddMessage(f"{tool}: {len(taken)} polygons eliminated, {len(changed)} surrounding polygons grown.")
    return Result(text(in_features))


# ---- FillGaps ---------------------------------------------------------------------------------------------------------

def fill_gaps(input_features, max_gap_area, fill_option, fill_unenclosed_gaps, max_gap_distance):
    tool = "FillGaps"
    inputs = listed(input_features)
    by_order = word(fill_option, "FILL_BY_LENGTH")
    if by_order not in ("FILL_BY_LENGTH", "FILL_BY_ORDER"):
        raise ExecuteError(f"ERROR 000800: {tool}: fill_option is FILL_BY_LENGTH or FILL_BY_ORDER.")
    fill_all = flag(tool, fill_unenclosed_gaps, "FILL_ALL", "SKIP_UNENCLOSED_GAPS", "SKIP_UNENCLOSED_GAPS")
    srid = srid_of(inputs[0])
    limit = areal(tool, max_gap_area, srid)
    features = []  # [order, path, oid, shape]
    for order, each in enumerate(inputs):
        path = dataset(each)
        for row in read(each, fields=[]):
            if row.shape is not None and not row.shape.is_empty:
                features.append([order, path, row.oid, row.shape])
    changed = set()

    def assign(gap):
        touching = [(f, shared_length(gap, f[3])) for f in features]
        touching = [(f, s) for f, s in touching if s > TOLERANCE]
        if not touching:
            return False
        if len(touching) == 1:
            f = touching[0][0]
            # A hole of a single feature is filled; a gap only between the parts of one multipart feature is not.
            own_hole = any(Polygon(r).equals_exact(gap, 1e-7) or Polygon(r).symmetric_difference(gap).area < 1e-9
                           for p in polygons(f[3]) for r in p.interiors)
            if not own_hole:
                return False
            chosen = f
        elif by_order == "FILL_BY_ORDER":
            first = min(f[0] for f, _ in touching)
            chosen = max(((f, s) for f, s in touching if f[0] == first), key=lambda t: (t[1], -t[0][2]))[0]
        else:
            chosen = max(touching, key=lambda t: (t[1], -t[0][0], -t[0][2]))[0]
        chosen[3] = unary_union([chosen[3], gap])
        changed.add(id(chosen))
        return True

    filled = 0
    if fill_all:
        distance = linear(tool, max_gap_distance, srid)
        if distance is None or distance <= 0:
            raise ExecuteError(f"ERROR 000735: {tool}: max_gap_distance is required with FILL_ALL.")
        union = unary_union([f[3] for f in features])
        holes = unary_union([Polygon(r) for p in polygons(union) for r in p.interiors]) if polygons(union) else Polygon()
        closed = union.buffer(distance / 2, join_style="mitre").buffer(-distance / 2, join_style="mitre")
        extra = closed.difference(union).difference(holes)
        for gap in polygons(extra):
            if gap.area > TOLERANCE and gap.area <= limit and assign(gap):
                filled += 1
    elif given(max_gap_distance):
        raise ExecuteError(f"ERROR 000800: {tool}: max_gap_distance is taken only with FILL_ALL.")
    union = unary_union([f[3] for f in features])
    for p in polygons(union):
        for ring in p.interiors:
            gap = Polygon(ring)
            if gap.area <= limit and assign(gap):
                filled += 1
    by_path = {}
    for f in features:
        if id(f) in changed:
            by_path.setdefault(f[1], {})[f[2]] = {"SHAPE@": as_multi(f[3], "Polygon")}
    for path, changes in by_path.items():
        update(path, changes)
    AddMessage(f"{tool}: {filled} gaps filled.")
    return Result(*[text(i) for i in inputs])


# ---- MergeLinesByPseudoNode -------------------------------------------------------------------------------------------

def merge_lines_by_pseudo_node(input_features, merge_fields, aggregate_operations, merge_feature_rule):
    from .management import _STATISTICS
    tool = "MergeLinesByPseudoNode"
    path = dataset(input_features)
    rule = word(merge_feature_rule, "FIRST")
    if rule not in ("FIRST", "LAST", "BY_LENGTH", "USE_DEFAULTS"):
        raise ExecuteError(f"ERROR 000800: {tool}: merge_feature_rule is FIRST, LAST, BY_LENGTH or USE_DEFAULTS.")
    keys = [require_field(tool, path, f) for f in listed(merge_fields)]
    operations = []
    for row in table_rows(aggregate_operations):
        field, op = require_field(tool, path, row[0]), str(row[1]).upper()
        if op not in _STATISTICS:
            raise ExecuteError(f"ERROR 000800: {tool}: '{op}' is not an aggregate operation.")
        operations.append((field, op))
    types = {c.name: c.type for c in schema(path)[0]}
    for field, op in operations:
        if types[field] in ("String", "Date") and op not in ("MIN", "MAX", "FIRST", "LAST"):
            raise ExecuteError(f"ERROR 000800: {tool}: a text or date field takes only MIN, MAX, FIRST or LAST.")
    rows = read(input_features)
    lines = {r.oid: r for r in rows if single_line(r.shape) is not None}
    ends = {}
    for oid, r in lines.items():
        line = single_line(r.shape)
        if line.is_closed:
            continue
        for at in (line.coords[0], line.coords[-1]):
            ends.setdefault(key(at), []).append(oid)
    all_ends = {}
    for r in rows:
        for line in parts(r.shape):
            if line.geom_type == "LineString":
                for at in (line.coords[0], line.coords[-1]):
                    all_ends[key(at)] = all_ends.get(key(at), 0) + 1
    parent = {oid: oid for oid in lines}

    def root(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for at, oids in ends.items():
        if len(oids) == 2 and all_ends.get(at) == 2 and oids[0] != oids[1]:
            a, b = lines[oids[0]], lines[oids[1]]
            if all(a[f] == b[f] for f in keys):
                parent[root(oids[0])] = root(oids[1])
    groups = {}
    for oid in lines:
        groups.setdefault(root(oid), []).append(oid)
    defaults = {}
    if rule == "USE_DEFAULTS":
        from . import _subtypes
        connection, table = _gpkg.table_of(path)
        try:
            defaults = _subtypes.defaults(connection, table)
        finally:
            connection.close()
    changes, gone = {}, set()
    for members in groups.values():
        if len(members) < 2:
            continue
        members.sort()
        merged = linemerge([single_line(lines[m].shape) for m in members])
        if merged.geom_type != "LineString":
            continue
        if rule == "LAST":
            kept = members[-1]
        elif rule == "BY_LENGTH":
            kept = max(members, key=lambda m: (lines[m].shape.length, -m))
        else:
            kept = members[0]
        own = single_line(lines[kept].shape)
        # The kept feature's direction is kept: the merged line runs the way it did.
        a, b = own.coords[0], own.coords[-1]
        coords = list(merged.coords)
        i, j = _index_of(coords, a), _index_of(coords, b)
        if i is not None and j is not None and i > j:
            merged = LineString(coords[::-1])
        change = {"SHAPE@": merged}
        if rule == "USE_DEFAULTS":
            for name in types:
                if name not in keys:
                    change[name] = defaults.get((name.lower(), None))
        for field, op in operations:
            values = [lines[m][field] for m in members if lines[m][field] is not None]
            change[field] = _STATISTICS[op](values) if values else None
        changes[kept] = change
        gone.update(m for m in members if m != kept)
    update(path, changes)
    delete(path, gone)
    AddMessage(f"{tool}: {len(gone)} lines merged into {len(changes)}.")
    return Result(text(input_features))


def _index_of(coords, point):
    for i, c in enumerate(coords):
        if abs(c[0] - point[0]) < 1e-9 and abs(c[1] - point[1]) < 1e-9:
            return i
    return None


# ---- RemoveCutbackVertices --------------------------------------------------------------------------------------------

def _cut(coords, closed, minimum, sequential, skip_coincident):
    pts = list(coords[:-1]) if closed else list(coords)
    least = 3 if closed else 2
    removed = 0

    def same(a, b):
        return abs(a[0] - b[0]) < 1e-12 and abs(a[1] - b[1]) < 1e-12

    if not skip_coincident:
        out = []
        for p in pts:
            if out and same(out[-1], p):
                removed += 1
                continue
            out.append(p)
        if closed and len(out) > 1 and same(out[0], out[-1]):
            out.pop()
            removed += 1
        pts = out

    def angle(i):
        n = len(pts)
        if not closed and (i == 0 or i == n - 1):
            return None
        return angle_at(pts[(i - 1) % n], pts[i], pts[(i + 1) % n])

    if sequential:
        i = 0 if closed else 1
        while len(pts) > least and i < len(pts):
            a = angle(i)
            if a is not None and a < minimum:
                del pts[i]
                removed += 1
                i = max(0 if closed else 1, i - 1)
                continue
            i += 1
    else:
        flagged = [i for i in range(len(pts)) if (a := angle(i)) is not None and a < minimum]
        keep = [p for i, p in enumerate(pts) if i not in set(flagged)]
        if len(keep) >= least:
            removed += len(pts) - len(keep)
            pts = keep
    return (pts + [pts[0]] if closed else pts), removed


def remove_cutback_vertices(in_features, minimum_angle, removal_method, skip_coincident_vertices):
    tool = "RemoveCutbackVertices"
    minimum = float(minimum_angle)
    if not 0 <= minimum <= 180:
        raise ExecuteError(f"ERROR 000800: {tool}: minimum_angle is between 0 and 180.")
    method = word(removal_method, "SEQUENTIAL")
    if method not in ("SEQUENTIAL", "ALL"):
        raise ExecuteError(f"ERROR 000800: {tool}: removal_method is SEQUENTIAL or ALL.")
    skip = flag(tool, skip_coincident_vertices, "SKIP_COINCIDENT", "REMOVE_COINCIDENT", "REMOVE_COINCIDENT")
    path = dataset(in_features)
    kind = schema(path)[1]
    changes, total = {}, 0
    for row in read(in_features, fields=[]):
        if row.shape is None:
            continue
        made, count = [], 0
        for part in parts(row.shape):
            if part.geom_type == "LineString":
                coords, n = _cut(list(part.coords), False, minimum, method == "SEQUENTIAL", skip)
                made.append(LineString(coords))
            elif part.geom_type == "Polygon":
                shell, n = _cut(list(part.exterior.coords), True, minimum, method == "SEQUENTIAL", skip)
                holes = []
                for ring in part.interiors:
                    hole, k = _cut(list(ring.coords), True, minimum, method == "SEQUENTIAL", skip)
                    holes.append(hole)
                    n += k
                made.append(Polygon(shell, holes))
            else:
                made.append(part)
                n = 0
            count += n
        if count:
            total += count
            changes[row.oid] = {"SHAPE@": as_multi(unary_union(made) if kind != "Polyline" else MultiLineString(made),
                                                   kind)}
    update(path, changes)
    AddMessage(f"{tool}: {total} vertices removed from {len(changes)} features.")
    return Result(text(in_features))


# ---- RepairSelfIntersection ---------------------------------------------------------------------------------------------

def _crossing(p, q, r, s):
    """Where segment pq properly crosses segment rs (their interiors meet at one point), with the fractions along each."""
    d = (q[0] - p[0]) * (s[1] - r[1]) - (q[1] - p[1]) * (s[0] - r[0])
    if abs(d) < 1e-15:
        return None
    t = ((r[0] - p[0]) * (s[1] - r[1]) - (r[1] - p[1]) * (s[0] - r[0])) / d
    u = ((r[0] - p[0]) * (q[1] - p[1]) - (r[1] - p[1]) * (q[0] - p[0])) / d
    if 1e-12 < t < 1 - 1e-12 and 1e-12 < u < 1 - 1e-12:
        return (p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1])), t, u
    return None


def _z_at(a, b, t):
    if len(a) > 2 and len(b) > 2:
        return a[2] + t * (b[2] - a[2])
    return None


def _first_loop(coords, limit):
    """(i, j, point) of the first crossing of segment i by a later segment j whose loop is shorter than ``limit``."""
    n = len(coords)
    for i in range(n - 1):
        for j in range(i + 2, n - 1):
            hit = _crossing(coords[i], coords[i + 1], coords[j], coords[j + 1])
            if hit is None:
                continue
            point, t, u = hit
            z1, z2 = _z_at(coords[i], coords[i + 1], t), _z_at(coords[j], coords[j + 1], u)
            if z1 is not None and abs(z1 - z2) > 1e-9:
                continue  # the page: only where the z of both segments is the same
            at = (*point, z1) if z1 is not None else point
            loop = [at, *coords[i + 1:j + 1], at]
            if limit is None or LineString([c[:2] for c in loop]).length < limit:
                return i, j, at, loop
    return None


def _end_loop(coords, limit):
    """A loop made by an end point lying on the line's own interior (a cul-de-sac snapped back): (which end, segment,
    loop)."""
    n = len(coords)
    for which, end in (("end", coords[-1]), ("start", coords[0])):
        for k in range(n - 1):
            if which == "end" and k >= n - 2 or which == "start" and k == 0:
                continue
            a, b = coords[k], coords[k + 1]
            segment = LineString([a[:2], b[:2]])
            if segment.distance(Point(end[:2])) < 1e-9 and Point(end[:2]).distance(Point(a[:2])) > 1e-9 \
                    and Point(end[:2]).distance(Point(b[:2])) > 1e-9:
                loop = [end, *coords[k + 1:]] if which == "end" else [*coords[:k + 1], end]
                if limit is None or LineString([c[:2] for c in loop]).length < limit:
                    return which, k, loop
    return None


def repair_self_intersection(in_features, repair_type, max_length, repair_at_end_point):
    tool = "RepairSelfIntersection"
    split = word(repair_type, "DELETE")
    if split not in ("DELETE", "SPLIT"):
        raise ExecuteError(f"ERROR 000800: {tool}: repair_type is DELETE or SPLIT.")
    ends = flag(tool, repair_at_end_point, "REPAIR_ENDS", "IGNORE_ENDS", "IGNORE_ENDS")
    path = dataset(in_features)
    _, kind, srid, _ = schema(path)
    limit = linear(tool, max_length, srid)
    names = field_names(path)
    changes, new_rows, repaired = {}, [], 0
    for row in read(in_features):
        if row.shape is None:
            continue
        kept, pieces = [], []
        for part in parts(row.shape):
            if part.geom_type == "LineString":
                coords = [tuple(c) for c in part.coords]
                if part.is_closed:
                    kept.append(part)
                    continue
                while True:
                    found = _first_loop(coords, limit)
                    if found is not None:
                        i, j, at, loop = found
                        coords = [*coords[:i + 1], at, *coords[j + 1:]]
                    elif ends and (found := _end_loop(coords, limit)) is not None:
                        which, k, loop = found
                        coords = coords[:k + 1] + [loop[0]] if which == "end" else [loop[-1], *coords[k + 1:]]
                    else:
                        break
                    repaired += 1
                    if split == "SPLIT":
                        pieces.append(LineString(loop))
                kept.append(LineString(coords))
            elif part.geom_type == "Polygon":
                ring = [tuple(c) for c in part.exterior.coords]
                found = _first_loop(ring, None)
                if found is None:
                    kept.append(part)
                    continue
                i, j, at, loop = found
                one = Polygon([c[:2] for c in loop])
                other = Polygon([c[:2] for c in [at, *ring[j + 1:], *ring[1:i + 1], at]])
                small, large = sorted((one, other), key=lambda p: p.length)
                if limit is not None and small.length >= limit:
                    kept.append(part)
                    continue
                repaired += 1
                kept.append(Polygon(large.exterior.coords, [r.coords for r in part.interiors]))
                if split == "SPLIT":
                    pieces.append(small)
            else:
                kept.append(part)
        if pieces or len(kept) != len(parts(row.shape)) or any(not k.equals(p) for k, p in zip(kept, parts(row.shape))):
            shape = MultiLineString(kept) if kind == "Polyline" else unary_union(kept)
            changes[row.oid] = {"SHAPE@": as_multi(shape, kind)}
            for piece in pieces:
                values = {n: row[n] for n in names}
                values["SHAPE@"] = as_multi(piece, kind)
                new_rows.append(values)
    update(path, changes)
    insert(path, new_rows)
    AddMessage(f"{tool}: {repaired} self-intersections repaired.")
    return Result(text(in_features))


# ---- SplitFeatures ----------------------------------------------------------------------------------------------------

def _with_vertices(line, points, target_z):
    """A line with a vertex added at each point lying on it; ``target_z`` gives each new vertex's z, else it is
    interpolated along the line."""
    coords = [tuple(c) for c in line.coords]
    has_z = len(coords[0]) > 2
    for point in points:
        best = None
        for i in range(len(coords) - 1):
            segment = LineString([coords[i][:2], coords[i + 1][:2]])
            d = segment.distance(Point(point[:2]))
            if d < 1e-7 and (best is None or d < best[1]):
                best = (i, d)
        if best is None:
            continue
        i = best[0]
        a, b = coords[i], coords[i + 1]
        if any(abs(c[0] - point[0]) < 1e-9 and abs(c[1] - point[1]) < 1e-9 for c in (a, b)):
            continue
        if has_z:
            length = math.dist(a[:2], b[:2])
            t = math.dist(a[:2], point[:2]) / length if length else 0
            z = target_z.get(key(point)) if target_z is not None else None
            coords.insert(i + 1, (point[0], point[1], a[2] + t * (b[2] - a[2]) if z is None else z))
        else:
            coords.insert(i + 1, (point[0], point[1]))
    return LineString(coords)


def _z_on(shape, point):
    """The z a shape has at a point on one of its edges, interpolated along that edge."""
    for part in parts(shape):
        rings = [part] if part.geom_type == "LineString" else [part.exterior, *part.interiors]
        for ring in rings:
            coords = list(ring.coords)
            for a, b in zip(coords, coords[1:]):
                if len(a) < 3:
                    return None
                if LineString([a[:2], b[:2]]).distance(Point(point)) < 1e-7:
                    length = math.dist(a[:2], b[:2])
                    t = math.dist(a[:2], point) / length if length else 0
                    return a[2] + t * (b[2] - a[2])
    return None


def split_features(cutting_features, target_features, use_target_z):
    from . import _layers
    from shapely.ops import split
    tool = "SplitFeatures"
    with_target_z = flag(tool, use_target_z, "USE_TARGET_Z", "DONT_USE_TARGET_Z", "DONT_USE_TARGET_Z")
    layer = _layers.find(cutting_features)
    if layer is None or not layer.selection:
        AddWarning(f"{tool}: no cutting feature is selected; nothing was split.")
        return Result(text(target_features))
    cutters = read(cutting_features, fields=[])
    cut_path = dataset(cutting_features)
    cut_kind = schema(cut_path)[1]
    blades = unary_union([p if p.geom_type == "LineString" else p.boundary
                          for c in cutters for p in parts(c.shape)])
    path = dataset(target_features)
    kind = schema(path)[1]
    names = field_names(path)
    changes, new_rows, crossings = {}, [], {}
    target_z = {}
    for row in read(target_features):
        if row.shape is None or not row.shape.intersects(blades):
            continue
        hits = row.shape.boundary.intersection(blades) if kind == "Polygon" else row.shape.intersection(blades)
        for point in parts(hits):
            if point.geom_type == "Point":
                crossings[key(point.coords[0])] = point.coords[0]
                if with_target_z:
                    z = _z_on(row.shape, point.coords[0][:2])
                    if z is not None:
                        target_z[key(point.coords[0])] = z
        pieces = [g for g in parts(split(row.shape, blades)) if not g.is_empty]
        if len(pieces) <= 1:
            continue
        pieces.sort(key=lambda g: (-g.length if kind == "Polyline" else -g.area))
        changes[row.oid] = {"SHAPE@": as_multi(pieces[0], kind)}
        for piece in pieces[1:]:
            values = {n: row[n] for n in names}
            values["SHAPE@"] = as_multi(piece, kind)
            new_rows.append(values)
    update(path, changes)
    insert(path, new_rows)
    # The page: vertices are added to the cutting features where they cross the targets.
    cut_changes = {}
    for cutter in cutters:
        made = []
        for part in parts(cutter.shape):
            if part.geom_type == "LineString":
                made.append(_with_vertices(part, list(crossings.values()), target_z if with_target_z else None))
            elif part.geom_type == "Polygon":
                shell = _with_vertices(part.exterior, list(crossings.values()), target_z if with_target_z else None)
                made.append(Polygon(shell.coords, [r.coords for r in part.interiors]))
        cut_changes[cutter.oid] = {"SHAPE@": as_multi(unary_union(made) if cut_kind == "Polygon"
                                                      else MultiLineString(made), cut_kind)}
    update(cut_path, cut_changes)
    AddMessage(f"{tool}: {len(changes)} features split into {len(changes) + len(new_rows)}.")
    return Result(text(target_features))


# ---- RemoveSmallLines ------------------------------------------------------------------------------------------------

def _direction_from(line, at):
    """The unit direction a line leaves the end point ``at``."""
    coords = list(line.coords)
    if key(coords[-1]) == key(at):
        coords = coords[::-1]
    for c in coords[1:]:
        dx, dy = c[0] - coords[0][0], c[1] - coords[0][1]
        n = math.hypot(dx, dy)
        if n > 0:
            return dx / n, dy / n
    return 0.0, 0.0


def remove_small_lines(in_features, minimum_length, maximum_angle, in_intersecting_features, recursive,
                       split_input_lines):
    tool = "RemoveSmallLines"
    path = dataset(in_features)
    _, kind, srid, _ = schema(path)
    projected(tool, srid)
    minimum = linear(tool, minimum_length, srid)
    limit = number(maximum_angle)
    again = flag(tool, recursive, "RECURSIVE", "NON_RECURSIVE", "NON_RECURSIVE")
    split_first = flag(tool, split_input_lines, "SPLIT", "NO_SPLIT", "SPLIT")
    guards = unary_union([r.shape for each in listed(in_intersecting_features) for r in read(each, fields=[])
                          if r.shape is not None]) if given(in_intersecting_features) else None
    names = field_names(path)
    rows = read(in_features)
    # Each piece: [source oid, LineString]; split at every intersection with the other lines when SPLIT.
    pieces = []
    if split_first:
        for r in rows:
            for part in parts(r.shape):
                if part.geom_type != "LineString":
                    continue
                cut = shapely.ops.split(part, _nodes_on(part, rows, r.oid))
                pieces += [[r.oid, g] for g in parts(cut) if g.geom_type == "LineString" and g.length > 0]
    else:
        pieces = [[r.oid, p] for r in rows for p in parts(r.shape) if p.geom_type == "LineString"]
    original = [(o, g) for o, g in pieces]
    removed_any = True
    while removed_any:
        removed_any = False
        degree = {}
        for _, g in pieces:
            for at in (g.coords[0], g.coords[-1]):
                degree[key(at)] = degree.get(key(at), 0) + 1

        def small(g):
            if g.length >= minimum:
                return False
            if guards is not None and g.distance(guards) < 1e-9:
                return False
            return degree[key(g.coords[0])] == 1 or degree[key(g.coords[-1])] == 1

        doomed = {i for i, (_, g) in enumerate(pieces) if small(g)}
        if limit is not None and doomed:
            # Small lines within the angle of the main path's continuation: the one of least declination at each
            # junction is kept, then dropped after all if, with the lines it continues up to the first intersection,
            # it is still shorter than the minimum.
            at_junction = {}
            for i in doomed:
                g = pieces[i][1]
                joint = g.coords[0] if degree[key(g.coords[-1])] == 1 else g.coords[-1]
                if degree[key(joint)] == 1:
                    continue
                s = _direction_from(g, joint)
                best = None
                for j, (_, h) in enumerate(pieces):
                    if j == i or key(joint) not in (key(h.coords[0]), key(h.coords[-1])):
                        continue
                    d = _direction_from(h, joint)
                    declination = math.degrees(math.acos(max(-1.0, min(1.0, -(s[0] * d[0] + s[1] * d[1])))))
                    if best is None or declination < best[0]:
                        best = (declination, j)
                if best is not None and best[0] <= limit:
                    at_junction.setdefault(key(joint), []).append((best[0], i, best[1]))
            for joint, found in at_junction.items():
                declination, i, j = min(found)
                combined = pieces[i][1].length + _chain_length(pieces, j, joint, degree)
                if combined >= minimum:
                    doomed.discard(i)
        if doomed:
            pieces = [p for i, p in enumerate(pieces) if i not in doomed]
            removed_any = again
    # Written back: every source feature as its kept pieces, the first piece in place and the rest as new features.
    kept = {}
    for oid, g in pieces:
        kept.setdefault(oid, []).append(g)
    before = {}
    for oid, g in original:
        before.setdefault(oid, []).append(g)
    changes, new_rows, gone = {}, [], set()
    by_oid = {r.oid: r for r in rows}
    for oid in before:
        mine = kept.get(oid, [])
        if not mine:
            gone.add(oid)
            continue
        if len(mine) == len(before[oid]) and len(mine) == 1 and mine[0].equals(single_line(by_oid[oid].shape) or Point()):
            continue
        changes[oid] = {"SHAPE@": as_multi(mine[0], kind)}
        for g in mine[1:]:
            values = {n: by_oid[oid][n] for n in names}
            values["SHAPE@"] = as_multi(g, kind)
            new_rows.append(values)
    update(path, changes)
    insert(path, new_rows)
    delete(path, gone)
    AddMessage(f"{tool}: {len(original) - len(pieces)} small lines removed; {len(changes)} features modified.")
    return Result(text(in_features))


def _nodes_on(line, rows, oid):
    """The points where other lines (or the line itself) meet ``line``, as a MultiPoint to split it at."""
    points = []
    for r in rows:
        for part in parts(r.shape):
            if part.geom_type != "LineString" or (r.oid == oid and part.equals(line)):
                continue
            for p in parts(line.intersection(part)):
                if p.geom_type == "Point":
                    points.append(p)
                elif p.geom_type == "LineString":
                    points += [Point(p.coords[0]), Point(p.coords[-1])]
    inner = [p for p in points if p.distance(Point(line.coords[0])) > 1e-9 and p.distance(Point(line.coords[-1])) > 1e-9]
    return shapely.MultiPoint(inner) if inner else shapely.MultiPoint()


def _chain_length(pieces, j, joint, degree):
    """The length of piece ``j`` and the pieces it continues through two-line nodes, walking away from ``joint``."""
    total, seen = 0.0, set()
    at = joint
    while j is not None and j not in seen:
        seen.add(j)
        g = pieces[j][1]
        total += g.length
        far = g.coords[-1] if key(g.coords[0]) == key(at) else g.coords[0]
        if degree.get(key(far)) != 2:
            break
        at = far
        j = next((k for k, (_, h) in enumerate(pieces) if k not in seen
                  and key(far) in (key(h.coords[0]), key(h.coords[-1]))), None)
    return total
