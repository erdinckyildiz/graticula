"""Linear referencing systems held in the GeoPackage a tool works on — ADR-252.

An LRS lives in a feature dataset (management.CreateFeatureDataset), as ArcGIS's does. Its user classes are ordinary
grapy feature classes and tables under the names and with the fields the data model pages document: Centerline
(polyline, z), Centerline Sequence (table), Calibration Point (point, z), Redline (polyline, z), each LRS Network
(polyline, z and m), event classes (point or line, z and m) and intersection classes (point, z and m).

Esri's own registration tables (``Lrs_Metadata``, ``Lrs_Edit_Log``, ``Lrs_Locks``) are named on the pages but their
layout is not published, so the registration is grapy's own tables, registered in ``gpkg_extensions`` as
``grapy_linear_referencing`` (INFERRED, ADR-252 §5.9 (a)): ``grapy_lrs`` (an LRS and the field each role is mapped to),
``grapy_lrs_network``, ``grapy_lrs_event``, ``grapy_lrs_intersection`` (each class's settings as JSON) and
``grapy_lrs_edit_log`` (the route edits grapy's own tools made, which Apply Event Behaviors reads). Conflict prevention
locks are an enterprise geodatabase's (ModifyLRS: they apply "only when editing or geoprocessing branch-versioned data
published as a feature service"), so no ``Lrs_Locks`` is held.
"""
from __future__ import annotations

import datetime
import json
import os
import re
import uuid

from . import _gpkg, _layers
from . import _lrs_measure as M
from ._runtime import ExecuteError, NotBuiltError
from ._topology import register

EXTENSION = "grapy_linear_referencing"
DEFINITION = "Graticula ADR-252: grapy's linear referencing systems"
TABLES = ("grapy_lrs", "grapy_lrs_network", "grapy_lrs_event", "grapy_lrs_intersection", "grapy_lrs_edit_log")

# INFERRED (ADR-252 §5.9 (b)): a GeoPackage keeps no tolerance, so an LRS made without one has ArcGIS's default for a
# metre-based reference, a millimetre, and a resolution of a tenth of that.
XY_TOLERANCE, XY_RESOLUTION = 0.001, 0.0001

# The measure units' keywords (CreateLRSNetwork, EnableReferentFields, EnableStationingFields) in metres. MILES and FEET
# are US survey units, as the page says of the default ("Miles (US Survey)") and as INTMILES and INTFEET stand apart from
# them; INCHES and YARDS follow FEET (INFERRED, §5.9 (b)).
from . import _units as _u  # noqa: E402

UNITS = {"MILES": _u.MILE_US, "INCHES": _u.INCH_US, "FEET": _u.FOOT_US, "YARDS": _u.YARD_US,
         "NAUTICAL_MILES": _u.NAUTICAL_MILE_INT, "INTFEET": _u.FOOT_INT, "INTMILES": _u.MILE_INT,
         "MILLIMETERS": _u.MILLIMETRE, "CENTIMETERS": _u.CENTIMETRE, "METERS": _u.METRE, "KILOMETERS": _u.KILOMETRE,
         "DECIMETERS": _u.DECIMETRE}

ACTIVITIES = ("calibrate", "retire", "extend", "reassign", "realign", "reverse", "carto_realign")
RULES = {"calibrate": ("STAY_PUT", "RETIRE", "MOVE"), "retire": ("STAY_PUT", "RETIRE", "MOVE", "SNAP"),
         "extend": ("STAY_PUT", "RETIRE", "MOVE", "COVER"), "reassign": ("STAY_PUT", "RETIRE", "MOVE", "SNAP"),
         "realign": ("STAY_PUT", "RETIRE", "MOVE", "SNAP", "COVER"), "reverse": ("STAY_PUT", "RETIRE", "MOVE"),
         "carto_realign": ("HONOR_ROUTE_MEASURE", "HONOR_REFERENT_LOCATION")}
DEFAULT_RULES = {a: ("HONOR_ROUTE_MEASURE" if a == "carto_realign" else "STAY_PUT") for a in ACTIVITIES}


def ensure(connection):
    connection.execute("""create table if not exists grapy_lrs (name text primary key, dataset text not null,
        srs_id integer not null, settings text not null)""")
    connection.execute("""create table if not exists grapy_lrs_network (table_name text primary key, lrs text not null,
        network_id integer not null, settings text not null)""")
    connection.execute("""create table if not exists grapy_lrs_event (table_name text primary key,
        network text not null, settings text not null)""")
    connection.execute("""create table if not exists grapy_lrs_intersection (table_name text primary key,
        network text not null, settings text not null)""")
    connection.execute("""create table if not exists grapy_lrs_edit_log (id integer primary key autoincrement,
        network text not null, route_id text not null, activity text not null, effective_date text,
        before_shape blob, after_shape blob, processed integer not null default 0, details text)""")
    register(connection, TABLES, EXTENSION, DEFINITION)


# ---- values ----------------------------------------------------------------------------------------------------------

def text(value):
    return os.fspath(value) if isinstance(value, os.PathLike) else str(value)


def given(value):
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


def word(value, default):
    return str(value).strip().upper() if given(value) else default


def choose(tool, name, value, allowed, default):
    found = word(value, default)
    if found not in allowed:
        raise ExecuteError(f"ERROR 000800: {tool}: {name} '{value}' is not one of {', '.join(allowed)}.")
    return found


def guid():
    return "{" + str(uuid.uuid4()).upper() + "}"


def when(value):
    """A date as a naive datetime; None for none."""
    if value is None or (isinstance(value, str) and not value.strip()):
        return None
    if isinstance(value, datetime.datetime):
        return value.replace(tzinfo=None)
    if isinstance(value, datetime.date):
        return datetime.datetime(value.year, value.month, value.day)
    text_value = str(value).strip()
    try:
        return datetime.datetime.fromisoformat(text_value).replace(tzinfo=None)
    except ValueError:
        pass
    for pattern in ("%m/%d/%Y", "%m/%d/%Y %H:%M:%S", "%m/%d/%Y %I:%M:%S %p", "%Y/%m/%d"):
        try:
            return datetime.datetime.strptime(text_value, pattern)
        except ValueError:
            continue
    raise ExecuteError(f"ERROR 000800: '{value}' is not a date.")


def active(start, end, moment):
    """Whether [start, end) holds ``moment``; a null start is the beginning of time and a null end its end (*Time
    awareness*: a null To Date "goes into the future"). INFERRED (§5.9 (e)): a slice is half-open."""
    if moment is None:
        return end is None
    return (start is None or start <= moment) and (end is None or moment < end)


def overlaps(s1, e1, s2, e2):
    return (s1 is None or e2 is None or s1 < e2) and (s2 is None or e1 is None or s2 < e1)


def unit_ratio(srid, unit):
    """Measure units in one unit of the reference's length (z counted in the same unit)."""
    metres = 1.0
    if srid and int(srid) > 0:
        from ._reference import SpatialReference
        reference = SpatialReference(int(srid))
        if reference.type == "Geographic":
            raise ExecuteError("ERROR 000800: a route's length needs a projected coordinate system; the LRS is "
                               f"in {reference.name}.")
        metres = reference.metersPerUnit
    return metres / UNITS[unit]


# ---- datasets --------------------------------------------------------------------------------------------------------

def resolve(value):
    """(file, table, where clause) of a feature class path or layer; the layer's query and selection make the clause."""
    layer = _layers.find(value)
    source = layer.source if layer is not None else text(value)
    if source.lower().startswith(("http://", "https://")):
        raise NotBuiltError("arcpy.locref: a feature service input is not built in grapy; a user tool runs fenced, "
                            "with no network (ADR-223 §5.3).")
    try:
        file, table = _gpkg.split(source)
    except ValueError as said:
        raise ExecuteError(f"ERROR 000732: {said}") from None
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: '{file}' does not exist.")
    if table is None:
        connection, table = _gpkg.table_of(file)
        connection.close()
    where = None
    if layer is not None:
        connection = _gpkg.connect(file)
        try:
            where = _layers.filter_of(layer, _gpkg.oid_column(connection, table))
        finally:
            connection.close()
    return file, table, where


def selected(value):
    """Whether a value is a layer with a selection."""
    layer = _layers.find(value)
    return layer is not None and bool(layer.selection)


def full(file, table):
    return f"{file}/{table}"


def connect(file):
    connection = _gpkg.connect(file)
    ensure(connection)
    connection.commit()  # ensure's writes would otherwise hold the file's write lock against the cursors
    return connection


def exists(connection, table):
    return connection.execute("select 1 from sqlite_master where name = ? and type in ('table', 'view')",
                              (table,)).fetchone() is not None


def columns(connection, table):
    """{lower-case name: Column} of a table."""
    return {c.name.lower(): c for c in _gpkg.describe(connection, table)[0]}


def field(connection, table, name, kinds=None, role=None):
    """A field's own name, checked to exist and, given ``kinds``, to be one of those types."""
    column = columns(connection, table).get(str(name).lower())
    if column is None:
        raise ExecuteError(f"ERROR 000728: the field '{name}' does not exist in '{table}'.")
    if kinds and column.type not in kinds:
        raise ExecuteError(f"ERROR 000800: the {role or 'field'} '{column.name}' of '{table}' is {column.type}; it must be "
                           f"{' or '.join(kinds)}.")
    return column.name


def count(connection, table):
    return connection.execute(f'select count(*) from "{table}"').fetchone()[0]


def shape_info(connection, table):
    """(shape kind, srid, has z, has m) of a feature table; (None, 0, False, False) of a table."""
    row = connection.execute("select geometry_type_name, srs_id, z, m from gpkg_geometry_columns where table_name = ?",
                             (table,)).fetchone()
    if row is None:
        return None, 0, False, False
    kind = {"POINT": "Point", "MULTIPOINT": "Multipoint", "LINESTRING": "Polyline", "MULTILINESTRING": "Polyline",
            "POLYGON": "Polygon", "MULTIPOLYGON": "Polygon"}.get(str(row[0]).upper(), "Geometry")
    return kind, int(row[1] or 0), bool(row[2]), bool(row[3])


class Row(dict):
    """A row read by ``read``: its fields by name without regard to case, ``oid`` and ``shape`` (shapely or None)."""

    def __init__(self, values, oid, shape):
        super().__init__({k.lower(): v for k, v in values.items()})
        self.oid, self.shape = oid, shape

    def get(self, name, default=None):
        return super().get(str(name).lower(), default) if name else default

    def __getitem__(self, name):
        return super().__getitem__(str(name).lower())


def read(connection, table, where=None, dates=()):
    """Every row of a table (within ``where``); the named date fields read as datetimes."""
    oid = _gpkg.oid_column(connection, table)
    geometry = connection.execute("select column_name from gpkg_geometry_columns where table_name = ?",
                                  (table,)).fetchone()
    geometry = geometry[0] if geometry else None
    names = [c.name for c in _gpkg.describe(connection, table)[0]]
    select = ", ".join([f'"{oid}"'] + [f'"{n}"' for n in names] + ([f'"{geometry}"'] if geometry else []))
    sql = f'select {select} from "{table}"' + (f" where {where}" if where else "") + f' order by "{oid}"'
    wanted = {d.lower() for d in dates if d}
    out = []
    for record in connection.execute(sql):
        values = dict(zip(names, record[1:1 + len(names)]))
        for name in names:
            if name.lower() in wanted:
                values[name] = when(values[name])
        out.append(Row(values, record[0], _gpkg.decode(record[-1]) if geometry else None))
    return out


def insert(file, table, records):
    """Rows written through arcpy.da.InsertCursor (so editor tracking and attribute rules apply); each record a dict
    of field values with the shape, if any, under ``SHAPE@``. Answers the new object IDs."""
    from . import da
    ids = []
    for record in records:
        names = list(record)
        with da.InsertCursor(full(file, table), names) as cursor:
            ids.append(cursor.insertRow([record[n] for n in names]))
    return ids


def update(file, table, oid, values):
    """One row's fields (and ``SHAPE@``) changed through arcpy.da.UpdateCursor."""
    from . import da
    connection = _gpkg.connect(file)
    try:
        key = _gpkg.oid_column(connection, table)
    finally:
        connection.close()
    names = list(values)
    with da.UpdateCursor(full(file, table), names, f'"{key}" = {int(oid)}') as cursor:
        for _ in cursor:
            cursor.updateRow([values[n] for n in names])


def delete(file, table, oids):
    from . import da
    oids = sorted({int(o) for o in oids})
    if not oids:
        return
    connection = _gpkg.connect(file)
    try:
        key = _gpkg.oid_column(connection, table)
    finally:
        connection.close()
    with da.UpdateCursor(full(file, table), ["OID@"], f'"{key}" in ({", ".join(map(str, oids))})') as cursor:
        for _ in cursor:
            cursor.deleteRow()


# ---- the registry ----------------------------------------------------------------------------------------------------

def lrs_get(connection, name):
    row = connection.execute("select dataset, srs_id, settings from grapy_lrs where name = ?", (str(name),)).fetchone()
    if row is None:
        raise ExecuteError(f"ERROR 000732: there is no LRS named '{name}' in this geodatabase.")
    settings = json.loads(row[2])
    settings.update(name=str(name), dataset=row[0], srid=int(row[1]))
    return settings


def lrs_put(connection, settings):
    kept = {k: v for k, v in settings.items() if k not in ("name", "dataset", "srid")}
    connection.execute("insert or replace into grapy_lrs values (?, ?, ?, ?)",
                       (settings["name"], settings["dataset"], int(settings["srid"]), json.dumps(kept)))


def lrs_names(connection):
    return [r[0] for r in connection.execute("select name from grapy_lrs order by name")]


ROLES = ("centerline", "sequence", "calibration", "redline")


def lrs_of_class(connection, table):
    """The LRS a class plays a role in (centerline, sequence, calibration point, redline, network, event or
    intersection), or None."""
    for name in lrs_names(connection):
        settings = lrs_get(connection, name)
        if table in [settings.get(r) for r in ROLES]:
            return settings
    network = network_get(connection, table)
    if network is None:
        found = event_get(connection, table) or intersection_get(connection, table)
        network = network_get(connection, found["network"]) if found else None
    return lrs_get(connection, network["lrs"]) if network else None


def network_get(connection, table):
    row = connection.execute("select lrs, network_id, settings from grapy_lrs_network where table_name = ?",
                             (table,)).fetchone()
    if row is None:
        return None
    settings = json.loads(row[2])
    settings.update(table=table, lrs=row[0], network_id=int(row[1]))
    return settings


def network_put(connection, settings):
    kept = {k: v for k, v in settings.items() if k not in ("table", "lrs", "network_id")}
    connection.execute("insert or replace into grapy_lrs_network values (?, ?, ?, ?)",
                       (settings["table"], settings["lrs"], int(settings["network_id"]), json.dumps(kept)))


def networks(connection, lrs):
    return [network_get(connection, r[0]) for r in
            connection.execute("select table_name from grapy_lrs_network where lrs = ? order by network_id", (lrs,))]


def network_required(connection, table, tool):
    found = network_get(connection, table)
    if found is None:
        raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is not a registered LRS Network.")
    return found


def _get(kind, connection, table):
    row = connection.execute(f"select network, settings from grapy_lrs_{kind} where table_name = ?", (table,)).fetchone()
    if row is None:
        return None
    settings = json.loads(row[1])
    settings.update(table=table, network=row[0])
    return settings


def _put(kind, connection, settings):
    kept = {k: v for k, v in settings.items() if k not in ("table", "network")}
    connection.execute(f"insert or replace into grapy_lrs_{kind} values (?, ?, ?)",
                       (settings["table"], settings["network"], json.dumps(kept)))


def event_get(connection, table):
    return _get("event", connection, table)


def event_put(connection, settings):
    _put("event", connection, settings)


def events(connection, network):
    return [event_get(connection, r[0]) for r in
            connection.execute("select table_name from grapy_lrs_event where network = ? order by table_name", (network,))]


def event_required(connection, table, tool):
    found = event_get(connection, table)
    if found is None:
        raise ExecuteError(f"ERROR 000800: {tool}: '{table}' is not a registered LRS event feature class.")
    return found


def intersection_get(connection, table):
    return _get("intersection", connection, table)


def intersection_put(connection, settings):
    _put("intersection", connection, settings)


def intersections(connection, network):
    return [intersection_get(connection, r[0]) for r in connection.execute(
        "select table_name from grapy_lrs_intersection where network = ? order by table_name", (network,))]


def registered_anywhere(connection, table):
    """Whether a class is already registered in any LRS role."""
    return lrs_of_class(connection, table) is not None


def name_check(name, tool, limit=26):
    if not re.fullmatch(r"[A-Za-z][A-Za-z0-9_]*", str(name)) or len(str(name)) > limit:
        raise ExecuteError(f"ERROR 000800: {tool}: '{name}' must begin with a letter, hold only letters, digits and "
                           f"underscores, and be at most {limit} characters.")


# ---- the edit log ----------------------------------------------------------------------------------------------------

def log(connection, network, route_id, activity, effective, before, after, details=None):
    connection.execute("insert into grapy_lrs_edit_log (network, route_id, activity, effective_date, before_shape, "
                       "after_shape, details) values (?, ?, ?, ?, ?, ?, ?)",
                       (network, str(route_id), activity, effective.isoformat(" ") if effective else None,
                        _gpkg.encode(before, 0) if before is not None else None,
                        _gpkg.encode(after, 0) if after is not None else None, json.dumps(details or {})))


def pending(connection, network):
    out = []
    for i, route, activity, effective, before, after, details in connection.execute(
            "select id, route_id, activity, effective_date, before_shape, after_shape, details from grapy_lrs_edit_log "
            "where network = ? and processed = 0 order by id", (network,)):
        out.append({"id": i, "route_id": route, "activity": activity, "effective": when(effective),
                    "before": _gpkg.decode(before), "after": _gpkg.decode(after), "details": json.loads(details or "{}")})
    return out


# ---- details files ---------------------------------------------------------------------------------------------------

def details_file(tool, lines):
    """The tool's Output Details File: a text file in the scratch folder, one line per issue or change. INFERRED
    (§5.9 (k)): the pages do not give its layout."""
    from ._runtime import env
    folder = env.scratchFolder or os.getcwd()
    os.makedirs(folder, exist_ok=True)
    stamp = datetime.datetime.now().strftime("%Y%m%d%H%M%S%f")
    path = os.path.join(folder, f"{tool}_{stamp}.txt")
    with open(path, "w", encoding="utf-8") as out:
        out.write(f"{tool}\n")
        for line in lines:
            out.write(f"{line}\n")
    return path


# ---- routes ----------------------------------------------------------------------------------------------------------

def route_rows(connection, network, where=None):
    return read(connection, network["table"], where, (network["from_date"], network["to_date"]))


def route_id_of(network, row):
    return row.get(network["route_id"])


def route_parts(row):
    return M.parts(row.shape)


def routes_active(connection, network, moment, route_id=None):
    """The routes of a network on ``moment`` (None: the current ones)."""
    out = []
    for row in route_rows(connection, network):
        if route_id is not None and str(route_id_of(network, row)) != str(route_id):
            continue
        if active(row.get(network["from_date"]), row.get(network["to_date"]), moment):
            out.append(row)
    return out


def route_for(connection, network, route_id, start, end):
    """The route record of ``route_id`` for an event's [start, end): the one holding ``start``, else the first that
    overlaps (INFERRED, §5.9 (e))."""
    rows = [r for r in route_rows(connection, network) if str(route_id_of(network, r)) == str(route_id)]
    for row in rows:
        if active(row.get(network["from_date"]), row.get(network["to_date"]), start) and (
                start is not None or row.get(network["from_date"]) is None):
            return row
    for row in rows:
        if overlaps(row.get(network["from_date"]), row.get(network["to_date"]), start, end):
            return row
    return None


def tolerance(lrs):
    return float(lrs.get("xy_tolerance") or XY_TOLERANCE)
