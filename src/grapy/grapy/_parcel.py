"""Parcel fabrics held in the GeoPackage a tool works on — ADR-250.

A parcel fabric is a controller dataset in a feature dataset (management.CreateFeatureDataset: a table-name prefix,
``<dataset>/<class>``); there is one per feature dataset and it takes the dataset's spatial reference, as the help topic
*Create a parcel fabric* says. Its path is ``<file>.gpkg/<dataset>/<fabric>``, as a topology's is. What it controls is
ordinary feature classes beside it, named as INFERRED in ADR-250 §5.8 (a):

- ``<fabric>_Records`` (polygon), ``<fabric>_Points`` (point), ``<fabric>_Connections`` (COGO line);
- ``<fabric>_AdjustmentLines``, ``<fabric>_AdjustmentPoints``, ``<fabric>_AdjustmentVectors``, rewritten by each
  least-squares analysis;
- per parcel type, the polygon class ``<type>`` and the line class ``<type>_Lines`` (the pages' code samples);
- the geodatabase topology ``<fabric>_Topology`` (grapy's own, ADR-225).

Which class plays which part is grapy's own two tables, registered in ``gpkg_extensions`` as the ``grapy_parcel_fabric``
extension (ADR-250 §5.1):

- ``grapy_parcel_fabric``: name, feature dataset, topology, and whether the topology is enabled;
- ``grapy_parcel_class``: each controlled class, its role (records, points, connections, polygons, lines, the three
  adjustment roles), and for a parcel type its name and whether it is administrative or strata.

Field names are the documented physical names where a page gives one (``CreatedByRecord``, ``RetiredByRecord``,
``IsFixed``, ``Preserve``, ``FloorOrder``, ``ParcelCount``) and otherwise the alias with its spaces removed (INFERRED,
§5.8 (b)). Coded values are INFERRED where the pages give only the names (§5.8 (c)).
"""
from __future__ import annotations

import math
import os

import shapely
from shapely.geometry import LineString, Point, Polygon

from . import _gpkg
from ._runtime import ExecuteError, NotBuiltError

EXTENSION = "grapy_parcel_fabric"
DEFINITION = "Graticula ADR-250: grapy's parcel fabric tables"
TABLES = ("grapy_parcel_fabric", "grapy_parcel_class")

YES, NO = 1, 0
ENTERED, FROM_GEOMETRY, COMPUTED = 1, 2, 3
# Pro 3.7 measured 2026-10-31 (ADR-250 condition 2): PF_LabelPosition is 1 Right, 2 Left, 3 Centered.
RIGHT, LEFT, CENTERED = 1, 2, 3
XY_FREE, XYZ_CONSTRAINED = 1, 2
CREATED, RETIRED, CREATED_AND_RETIRED = 1, 2, 3
# Pro 3.7 measured: the AdjustmentLines subtypes are 1 Direction Set, 2 Distance, 3 Geodetic Latitude, 4 Geodetic
# Longitude, and no others.
DIRECTION_SET, DISTANCE, GEODETIC_LATITUDE, GEODETIC_LONGITUDE = 1, 2, 3, 4

# Pro 3.7 measured: PF_AreaUnits holds the projection engine's area unit codes, in this order and with these names.
AREA_UNIT_NAMES = [
    "Hectare", "Acre", "Acre US", "Square Meter", "Square Foot", "Square Foot US", "Square Foot Clarke",
    "Square Fathom", "Square Nautical Mile", "Square Meter German", "Square Chain US", "Square Link US",
    "Square Mile US", "Square Kilometer", "Square Yard Clarke", "Square Chain Clarke", "Square Link Clarke",
    "Square Yard Sears", "Square Foot Sears", "Square Chain Sears", "Square Link Sears", "Square Yard Benoit A",
    "Square Foot Benoit A", "Square Chain Benoit A", "Square Link Benoit A", "Square Yard Benoit B",
    "Square Foot Benoit B", "Square Chain Benoit B", "Square Link Benoit B", "Square Foot 1865", "Square Foot Indian",
    "Square Foot Indian 1937", "Square Foot Indian 1962", "Square Foot Indian 1975", "Square Yard Indian",
    "Square Yard Indian 1937", "Square Yard Indian 1962", "Square Yard Indian 1975", "Square Mile Statute",
    "Square Foot Gold Coast", "Square Foot British 1936", "Square Yard", "Square Yard US", "Square Chain",
    "Square Link", "Square Yard Sears 1922 Trunc", "Square Foot Sears 1922 Trunc", "Square Chain Sears 1922 Trunc",
    "Square Link Sears 1922 Trunc", "Square Decimeter", "Square Centimeter", "Square Millimeter", "Square Inch",
    "Square Inch US", "Square Rod", "Square Rod US", "Square Nautical Mile US", "Square Nautical Mile UK",
    "Square Smoot", "Square Km50", "Square Km150", "Square Vara TX", "Are", "Square DTP Point", "Square Micrometer",
    "Square Nanometer"]
AREA_UNIT_CODES = [(109401 + i, name) for i, name in enumerate(AREA_UNIT_NAMES)]

# The domains a new fabric holds in Pro 3.7 (measured; PF_COGOAccuracy and ExportStatus are not among them).
DOMAINS = {
    "PF_YesNo": ("Parcel Fabric Yes No", [(NO, "No"), (YES, "Yes")]),
    "PF_COGOType": ("Parcel Fabric COGO Type", [(ENTERED, "Entered"), (FROM_GEOMETRY, "From Geometry"),
                                                (COMPUTED, "Computed")]),
    "PF_LabelPosition": ("Parcel Fabric Label Position", [(RIGHT, "Right"), (LEFT, "Left"), (CENTERED, "Centered")]),
    "PF_AdjustmentConstraint": ("Parcel Fabric Adjustment Point Constraint",
                                [(XY_FREE, "XY free, Z constrained"), (XYZ_CONSTRAINED, "XYZ constrained")]),
    "PF_AreaUnits": ("Parcel Fabric Area Units", AREA_UNIT_CODES),
}
EXPORT_STATUS = ("ExportStatus", "Parcel Fabric Export Status",
                 [(CREATED, "Created By Record"), (RETIRED, "Retired By Record"),
                  (CREATED_AND_RETIRED, "Created and Retired By Record")])
MEASUREMENT_TYPES = [(DIRECTION_SET, "Direction Set"), (DISTANCE, "Distance"), (GEODETIC_LATITUDE, "Geodetic Latitude"),
                     (GEODETIC_LONGITUDE, "Geodetic Longitude")]

GUID = "Guid"
# Field names as Pro 3.7 makes them (measured): where an alias's words differ from the physical name, the physical
# name is Pro's (EllipseMajor, MeasurementStdDev, Residual, NormalStatistic, PelzerReliablity as Pro spells it), and
# VALIDATIONSTATUS is on the classes that carry a validation rule (polygons, lines, connection lines), not on points.
VALIDATION = ("VALIDATIONSTATUS", "SmallInteger")
RECORD_FIELDS = [("Name", "String"), ("RecordType", "Integer"), ("RecordedDate", "Date"), ("ParcelCount", "Integer"),
                 ("RetiredParcelCount", "Integer")]
POLYGON_FIELDS = [("Name", "String"), ("CreatedByRecord", GUID), ("RetiredByRecord", GUID), ("StatedArea", "Double"),
                  ("StatedAreaUnit", "Integer"), ("CalculatedArea", "Double"), ("MiscloseRatio", "Double"),
                  ("MiscloseDistance", "Double"), ("IsSeed", "Integer"), VALIDATION]
COGO_FIELDS = [("Direction", "Double"), ("Distance", "Double"), ("Radius", "Double"), ("ArcLength", "Double"),
               ("Radius2", "Double")]
CONNECTION_FIELDS = [("CreatedByRecord", GUID), ("RetiredByRecord", GUID), *COGO_FIELDS, ("COGOType", "Integer"),
                     ("IsCOGOGround", "Integer"), ("Rotation", "Double"), ("Scale", "Double"),
                     ("DirectionAccuracy", "Double"), ("DistanceAccuracy", "Double"), VALIDATION]
LINE_FIELDS = [*CONNECTION_FIELDS[:-3], ("ParentLineID", GUID), ("DirectionAccuracy", "Double"),
               ("DistanceAccuracy", "Double"), ("LabelPosition", "Integer"), VALIDATION]
ELLIPSE = [("EllipseMajor", "Double"), ("EllipseMinor", "Double"), ("EllipseDirection", "Double")]
POINT_FIELDS = [("Name", "String"), ("CreatedByRecord", GUID), ("RetiredByRecord", GUID), ("IsFixed", "Integer"),
                ("AdjustmentConstraint", "Integer"), ("Preserve", "Integer"), ("X", "Double"), ("Y", "Double"),
                ("Z", "Double"), ("XYAccuracy", "Double"), ("ZAccuracy", "Double"), ("XYUncertainty", "Double"),
                *ELLIPSE]
ADJUSTMENT_LINE_FIELDS = [("PointID", GUID), ("LineID", GUID), ("Source", "String"), ("MeasurementType", "Integer"),
                          ("Point1Name", "String"), ("Point2Name", "String"), ("Point3Name", "String"),
                          ("Measurement", "Double"), ("AdjustedMeasurement", "Double"),
                          ("MeasurementCorrection", "Double"), ("MeasurementStdDev", "Double"),
                          ("AdjustedStdDev", "Double"), ("Residual", "Double"), ("NormalStatistic", "Double"),
                          ("StudentStatistic", "Double"), ("PelzerReliablity", "Double"), ("Outlier", "Integer")]
ADJUSTMENT_POINT_FIELDS = [("PointID", GUID), ("Name", "String"), ("Constrained", "String"), ("X", "Double"),
                           ("Y", "Double"), ("Z", "Double"), ("AdjustedX", "Double"), ("AdjustedY", "Double"),
                           ("AdjustedZ", "Double"), ("XYAccuracy", "Double"), ("ZAccuracy", "Double"),
                           ("XYUncertainty", "Double"), ("ZUncertainty", "Double"), ("StdDevE", "Double"),
                           ("StdDevN", "Double"), ("StdDevUp", "Double"), *ELLIPSE, ("UpdateSource", "Integer")]
ADJUSTMENT_VECTOR_FIELDS = [("PointID", GUID)]

DOMAIN_OF = {"IsSeed": "PF_YesNo", "IsCOGOGround": "PF_YesNo", "IsFixed": "PF_YesNo", "Preserve": "PF_YesNo",
             "Outlier": "PF_YesNo", "UpdateSource": "PF_YesNo", "COGOType": "PF_COGOType",
             "LabelPosition": "PF_LabelPosition", "AdjustmentConstraint": "PF_AdjustmentConstraint",
             "StatedAreaUnit": "PF_AreaUnits"}
# The defaults Pro 3.7 gives its fields (measured).
DEFAULTS = {"DirectionAccuracy": 30.0, "DistanceAccuracy": 0.15, "LabelPosition": RIGHT, "IsSeed": NO,
            "AdjustmentConstraint": XY_FREE, "MeasurementType": DIRECTION_SET}

# The six system rules of *Work with the geodatabase topology* — "Lines" and "Polygons" are each parcel type's own
# classes; an administrative type's lines take the four line rules, its polygons being outside the topology, and a
# connection line takes the first rule only (INFERRED, §5.8 (e)).
LINE_RULES = ("Endpoint Must Be Covered By (Line-Point)", "Must Not Have Dangles (Line)",
              "Must Not Self-Overlap (Line)", "Must Not Self-Intersect (Line)")
LINE_AREA_RULE = "Must Be Covered By Boundary Of (Line-Area)"
AREA_LINE_RULE = "Boundary Must Be Covered By (Area-Line)"
SYSTEM_RULES = (*LINE_RULES, LINE_AREA_RULE, AREA_LINE_RULE)
# xy ranks: points are the best located, polygons follow their lines (INFERRED, §5.8 (e)).
RANKS = {"points": 1, "connections": 2, "lines": 2, "polygons": 3}


def ensure(connection):
    connection.execute("""create table if not exists grapy_parcel_fabric (name text primary key, dataset text not null
        unique, topology text not null, topology_enabled integer not null default 0)""")
    connection.execute("""create table if not exists grapy_parcel_class (class_name text primary key,
        fabric text not null, role text not null, parcel_type text, administrative integer not null default 0,
        strata integer not null default 0)""")
    from ._topology import register
    register(connection, TABLES, EXTENSION, DEFINITION)


def _text(value):
    from ._layers import text
    return text(value)


def given(value):
    from ._runtime import _is_given
    return _is_given(value)


# ---- the fabric ---------------------------------------------------------------------------------------------------


class ParcelType:
    def __init__(self, name, polygons, lines, administrative, strata):
        self.name, self.polygons, self.lines = name, polygons, lines
        self.administrative, self.strata = bool(administrative), bool(strata)


class Fabric:
    """A fabric's file, dataset, name, topology, the classes it controls, and the spatial reference and xy tolerance
    of its dataset."""

    def __init__(self, file, dataset, name, topology, enabled, classes):
        self.file, self.dataset, self.name, self.topology, self.enabled = file, dataset, name, topology, bool(enabled)
        self.roles = {}
        self.types = []
        for class_name, role, parcel_type, administrative, strata in classes:
            if role in ("polygons", "lines"):
                continue
            self.roles[role] = class_name
        by_type = {}
        for class_name, role, parcel_type, administrative, strata in classes:
            if role in ("polygons", "lines"):
                by_type.setdefault(parcel_type, {"administrative": administrative, "strata": strata})[role] = class_name
        for type_name, held in by_type.items():
            self.types.append(ParcelType(type_name, held.get("polygons"), held.get("lines"), held["administrative"],
                                         held["strata"]))
        self.records = self.roles.get("records")
        self.points = self.roles.get("points")
        self.connections = self.roles.get("connections")
        self.srid = srid_of(file, dataset)
        self.tolerance = topology_tolerance(file, topology) or default_tolerance(self.srid)

    @property
    def path(self):
        return f"{self.file}/{self.dataset}/{self.name}"

    def full(self, table):
        return f"{self.file}/{table}"

    def type_named(self, name, tool):
        for each in self.types:
            if each.name.lower() == str(name).strip().lower():
                return each
        raise ExecuteError(f"ERROR 000800: {tool}: the parcel fabric '{self.name}' has no parcel type '{name}'.")

    def type_of(self, table):
        """The parcel type whose polygon or line class a table is, or None."""
        for each in self.types:
            if table in (each.polygons, each.lines):
                return each
        return None

    def line_classes(self, connections=True):
        found = [t.lines for t in self.types]
        return found + ([self.connections] if connections else [])

    def role_of(self, table):
        if table in self.roles.values():
            return next(r for r, t in self.roles.items() if t == table)
        found = self.type_of(table)
        if found is None:
            return None
        return "polygons" if table == found.polygons else "lines"


def srid_of(file, dataset):
    connection = _gpkg.connect(file)
    try:
        row = connection.execute("select srs_id from grapy_feature_datasets where name = ?", (dataset,)).fetchone()
        return int(row[0]) if row else 0
    finally:
        connection.close()


def topology_tolerance(file, topology):
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_topology'").fetchone():
            return None
        row = connection.execute("select cluster_tolerance from grapy_topology where name = ?", (topology,)).fetchone()
        return float(row[0]) if row else None
    finally:
        connection.close()


def default_tolerance(srid):
    from .management import _default_tolerance
    return _default_tolerance(srid)


def _load(file, where, args):
    if not os.path.exists(file):
        return None
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_parcel_fabric'").fetchone():
            return None
        row = connection.execute(f"select name, dataset, topology, topology_enabled from grapy_parcel_fabric where {where}",
                                 args).fetchone()
        if row is None:
            return None
        classes = connection.execute("select class_name, role, parcel_type, administrative, strata from "
                                     "grapy_parcel_class where fabric = ? order by rowid", (row[0],)).fetchall()
    finally:
        connection.close()
    return Fabric(file, row[1], row[0], row[2], row[3], classes)


def resolve(path):
    """A class named under its fabric's path (``<dataset>/<fabric>/<class>``, as the pages' samples write it) answers
    the class's own path ``<dataset>/<class>``; ``Records``, ``Points``, ``Connections`` and the adjustment classes
    there answer ``<dataset>/<fabric>_<name>``. Any other path is answered as it is."""
    text = _text(path)
    try:
        file, table = _gpkg.split(text)
    except ValueError:
        return text
    if not table or table.count("/") < 2:
        return text
    head, name = table.rsplit("/", 1)
    dataset, fabric_name = head.rsplit("/", 1)
    found = _load(file, "name = ? and dataset = ?", (fabric_name, dataset))
    if found is None:
        return text
    for candidate in (f"{dataset}/{name}", f"{dataset}/{fabric_name}_{name}"):
        if candidate in found.roles.values() or found.type_of(candidate) is not None:
            return f"{file}/{candidate}"
    return text


def fabric_of_class(file, table):
    """The fabric that controls a class, or None."""
    if not os.path.exists(file):
        return None
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_parcel_class'").fetchone():
            return None
        row = connection.execute("select fabric from grapy_parcel_class where class_name = ?", (table,)).fetchone()
    finally:
        connection.close()
    return _load(file, "name = ?", (row[0],)) if row else None


def locate(value, tool):
    """(fabric, layer or None, table or None) of a parcel fabric parameter: the fabric's path
    ``<file>.gpkg/<dataset>/<fabric>``, or a class the fabric controls or a layer over one (INFERRED, §5.8 (f): a
    "parcel fabric layer" with a selection is a layer over one of its classes)."""
    from . import _layers
    layer = _layers.find(value)
    path = layer.source if layer is not None else _text(value)
    try:
        file, table = _gpkg.split(resolve(path))
    except ValueError:
        raise ExecuteError(f"ERROR 000732: {tool}: '{value}' is not a parcel fabric.") from None
    if table and "/" in table:
        dataset, name = table.rsplit("/", 1)
        found = _load(file, "name = ? and dataset = ?", (name, dataset))
        if found is not None:
            return found, None, None
    found = fabric_of_class(file, table) if table else None
    if found is None:
        raise ExecuteError(f"ERROR 000732: {tool}: '{value}' is not a parcel fabric or one of its classes.")
    return found, layer, table


def fabric(value, tool):
    return locate(value, tool)[0]


def load(path):
    """The fabric at ``<file>.gpkg/<dataset>/<fabric>``, read again."""
    return fabric(path, "parcel")


# ---- reading and writing features ---------------------------------------------------------------------------------


class Feature(dict):
    """A row: its fields by name, plus ``oid`` and ``shape`` (shapely, or None)."""

    oid = None
    shape = None

    def get_ci(self, name, default=None):
        for key, value in self.items():
            if key.lower() == name.lower():
                return value
        return default


def read(file, table, where=None):
    """Every row of a table (with ``where``, those it keeps) as Features."""
    connection = _gpkg.connect(file)
    try:
        columns, _, _, geometry = _gpkg.describe(connection, table)
        oid = _gpkg.oid_column(connection, table)
        names = [c.name for c in columns]
        has_geometry = any(r[1] == geometry for r in connection.execute(f'pragma table_info("{table}")'))
        select = ", ".join([f'"{oid}"'] + [f'"{n}"' for n in names] + ([f'"{geometry}"'] if has_geometry else []))
        sql = f'select {select} from "{table}"' + (f" where {where}" if where else "") + f' order by "{oid}"'
        out = []
        for row in connection.execute(sql):
            feature = Feature(zip(names, row[1:1 + len(names)]))
            feature.oid = row[0]
            feature.shape = _gpkg.decode(row[-1]) if has_geometry else None
            out.append(feature)
        return out
    finally:
        connection.close()


def read_input(value, tool):
    """(file, table, features) of a feature-layer parameter: a layer's shown and selected rows, or every row."""
    from .management import _source
    path, where = _source(value)
    try:
        file, table = _gpkg.split(resolve(path))
    except ValueError:
        raise ExecuteError(f"ERROR 000732: {tool}: '{value}' is not a feature class or layer.") from None
    if table is None:
        connection, table = _gpkg.table_of(path)
        connection.close()
    if not os.path.exists(file):
        raise ExecuteError(f"ERROR 000732: {tool}: '{value}' does not exist.")
    return file, table, read(file, table, where)


def selection_of(value):
    """The object ids a layer selects or shows (None for a dataset path, or a layer that neither selects nor filters)."""
    from . import _layers
    from .management import _source
    layer = _layers.find(value)
    if layer is None or not (layer.where or layer.selection):
        return None
    path, where = _source(value)
    file, table = _gpkg.split(resolve(path))
    return {f.oid for f in read(file, table, where)}


def columns_of(file, table):
    connection = _gpkg.connect(file)
    try:
        return _gpkg.describe(connection, table)[0]
    finally:
        connection.close()


def insert(file, table, rows):
    """Rows (dicts; ``shape`` the geometry) inserted through ``arcpy.da`` — so global IDs, editor tracking and
    attribute rules apply. Answers the new object ids."""
    from . import da
    out = []
    if not rows:
        return out
    by_fields = {}
    for row in rows:
        by_fields.setdefault(tuple(k for k in row if k != "shape"), []).append(row)
    for fields, group in by_fields.items():
        with da.InsertCursor(f"{file}/{table}", [*fields, "SHAPE@"]) as cursor:
            for row in group:
                out.append(cursor.insertRow([*(row[f] for f in fields), row.get("shape")]))
    return out


def update(file, table, changes):
    """{oid: {field: value, "shape": geometry}} written through ``arcpy.da``."""
    from . import da
    if not changes:
        return
    fields = []
    for change in changes.values():
        for key in change:
            if key not in fields:
                fields.append(key)
    names = [("SHAPE@" if f == "shape" else f) for f in fields]
    oids = ", ".join(str(int(o)) for o in changes)
    connection = _gpkg.connect(file)
    try:
        oid = _gpkg.oid_column(connection, table)
    finally:
        connection.close()
    with da.UpdateCursor(f"{file}/{table}", ["OID@", *names], f'"{oid}" in ({oids})') as rows:
        for row in rows:
            change = changes[row[0]]
            new = [row[0]]
            for f, current in zip(fields, row[1:]):
                if f in change:
                    new.append(change[f])
                elif f == "shape":
                    new.append(current)
                else:
                    new.append(current)
            rows.updateRow(new)


def delete(file, table, oids):
    from . import da
    oids = list(oids)
    if not oids:
        return
    connection = _gpkg.connect(file)
    try:
        oid = _gpkg.oid_column(connection, table)
    finally:
        connection.close()
    with da.UpdateCursor(f"{file}/{table}", ["OID@"], f'"{oid}" in ({", ".join(str(int(o)) for o in oids)})') as rows:
        for _ in rows:
            rows.deleteRow()


def same_guid(a, b):
    return a is not None and b is not None and str(a).strip("{}").upper() == str(b).strip("{}").upper()


def current(feature):
    return feature.get_ci("RetiredByRecord") in (None, "")


# ---- records ------------------------------------------------------------------------------------------------------


def records(fab):
    return read(fab.file, fab.records)


def record_named(fab, name):
    """The record whose name is ``name``, compared without case (CreateParcelSeeds: "not case sensitive"; INFERRED for
    the other tools, §5.8 (g))."""
    wanted = str(name).strip().lower()
    for row in records(fab):
        if str(row.get("Name") or "").strip().lower() == wanted:
            return row
    return None


def ensure_record(fab, name):
    """The global ID of the record named ``name``, made (with no geometry, as the help says) when there is none."""
    found = record_named(fab, name)
    if found is not None:
        return found.get_ci("GlobalID")
    oid = insert(fab.file, fab.records, [{"Name": str(name).strip(), "shape": None}])[0]
    return next(r for r in read(fab.file, fab.records, f"fid = {int(oid)}")).get_ci("GlobalID")


RECORD_POLYGON_LIMIT = 2000


def refresh_records(fab, guids=None):
    """Each record's polygon, Created Parcel Count and Retired Parcel Count from the parcels associated with it: the
    polygon is the union of the parcels it created or retired; a record with no parcels takes the envelope of the
    lines it created or retired (documented for connection lines; INFERRED for parcel lines, §5.8 (h)); no polygon is
    written for a record with more than 2,000 parcels, as documented. A seed is counted, as Pro 3.7 counts it
    (measured 2026-10-31: a record whose only parcel is a seed has a Created Parcel Count of 1), but gives the polygon
    no shape (INFERRED)."""
    polygons, lines, seeds = [], [], []
    for each in fab.types:
        for f in read(fab.file, each.polygons):
            (seeds if f.get_ci("IsSeed") == YES else polygons).append(f)
        lines.extend(read(fab.file, each.lines))
    lines.extend(read(fab.file, fab.connections))
    changes = {}
    for record in records(fab):
        guid = record.get_ci("GlobalID")
        if guids is not None and not any(same_guid(guid, g) for g in guids):
            continue
        created = [p for p in polygons if same_guid(p.get_ci("CreatedByRecord"), guid)]
        retired = [p for p in polygons if same_guid(p.get_ci("RetiredByRecord"), guid)]
        seeded = sum(1 for p in seeds if same_guid(p.get_ci("CreatedByRecord"), guid))
        change = {"ParcelCount": len(created) + seeded, "RetiredParcelCount": len(retired)}
        if len(created) + len(retired) <= RECORD_POLYGON_LIMIT:
            shapes = [p.shape for p in created + retired if p.shape is not None and not p.shape.is_empty]
            if shapes:
                change["shape"] = shapely.union_all(shapes)
            else:
                own = [f.shape for f in lines if f.shape is not None and not f.shape.is_empty
                       and (same_guid(f.get_ci("CreatedByRecord"), guid) or same_guid(f.get_ci("RetiredByRecord"), guid))]
                change["shape"] = shapely.box(*shapely.union_all(own).bounds) if own else None
        changes[record.oid] = change
    update(fab.file, fab.records, changes)


# ---- geometry helpers ----------------------------------------------------------------------------------------------


def ends(shape):
    """The first and last coordinate (x, y) of a line."""
    coordinates = shapely.get_coordinates(shape)
    return (float(coordinates[0][0]), float(coordinates[0][1])), (float(coordinates[-1][0]), float(coordinates[-1][1]))


def line_coordinates(shape):
    return [(float(x), float(y)) for x, y in shapely.get_coordinates(shape)]


def near(a, b, tolerance):
    return math.hypot(a[0] - b[0], a[1] - b[1]) <= tolerance


def rings(polygon):
    """The rings of a (multi)polygon as coordinate lists, closed."""
    out = []
    for part in getattr(polygon, "geoms", [polygon]):
        if part.is_empty:
            continue
        out.append([(float(x), float(y)) for x, y in part.exterior.coords])
        out.extend([(float(x), float(y)) for x, y in ring.coords] for ring in part.interiors)
    return out


def offset(point, a, b):
    """The distance of ``point`` from the straight line through ``a`` and ``b``."""
    length = math.hypot(b[0] - a[0], b[1] - a[1])
    if length == 0:
        return math.hypot(point[0] - a[0], point[1] - a[1])
    return abs((b[0] - a[0]) * (a[1] - point[1]) - (a[0] - point[0]) * (b[1] - a[1])) / length


def on_segment(point, a, b, tolerance):
    """Whether ``point`` lies on segment a–b within the tolerance, strictly between its ends."""
    if near(point, a, tolerance) or near(point, b, tolerance):
        return False
    return LineString([a, b]).distance(Point(point)) <= tolerance


def azimuth(a, b):
    """North azimuth in decimal degrees from a to b."""
    return math.degrees(math.atan2(b[0] - a[0], b[1] - a[1])) % 360.0


def as_line(a, b):
    return LineString([a, b])


def polygon_of(shape):
    if shape is None or shape.is_empty:
        return None
    return shape if shape.geom_type in ("Polygon", "MultiPolygon") else None


def two_point(shape):
    """A line's chord: its first to its last coordinate."""
    a, b = ends(shape)
    return LineString([a, b])


def contains_point(polygon, xy):
    return polygon.covers(Point(xy))


SEED_RADIUS = 1.0


def minimized_polygon(face):
    """A seed: a circle of radius 1 around the face's pole of inaccessibility, as Pro 3.7 makes one (measured
    2026-10-31, ADR-250 condition 2: area pi for square loops 10, 100 and 1,000 m across and a 400 by 20 m one,
    centred in each, at the centroid of the 400 by 20 m one). Pro's is a true circular arc; a GeoPackage polygon here
    has 64 sides. The centre is the loop's centroid where the circle fits inside there, else its pole of
    inaccessibility; in a loop too narrow for the circle the radius is half the distance from the pole to the loop,
    so the seed stays inside (INFERRED, §5.8 (i))."""
    from shapely.ops import polylabel
    centre = face.centroid
    room = face.boundary.distance(centre) if face.covers(centre) else 0.0
    if room <= SEED_RADIUS:
        centre = polylabel(face, tolerance=max(math.sqrt(face.area) / 1000.0, 1e-9))
        room = face.boundary.distance(centre)
    radius = SEED_RADIUS if room > SEED_RADIUS else max(room, 1e-9) / 2.0
    return centre.buffer(radius, quad_segs=16)


# ---- creating the fabric and its parcel types ---------------------------------------------------------------------


def _columns(spec):
    from ._gpkg import Column
    return [Column(name, "Guid" if kind == GUID else kind) for name, kind in spec]


def _domains(file, export=False):
    """The fabric's domains in a GeoPackage, and ``ExportStatus`` too for ExportParcelRecordFeatures' target."""
    from . import management
    connection = _gpkg.connect(file)
    try:
        held = set()
        if connection.execute("select 1 from sqlite_master where name = 'grapy_domains'").fetchone():
            held = {r[0] for r in connection.execute("select name from grapy_domains")}
    finally:
        connection.close()
    for name, (description, codes) in [*DOMAINS.items(), *([(EXPORT_STATUS[0], EXPORT_STATUS[1:])] if export else [])]:
        if name in held:
            continue
        management.CreateDomain(file, name, description, "LONG", "CODED")
        for code, text in codes:
            management.AddCodedValueToDomain(file, name, code, text)


def assign_domains(file, table, spec):
    from . import management
    for name, _ in spec:
        if name in DOMAIN_OF:
            management.AssignDomainToField(f"{file}/{table}", name, DOMAIN_OF[name])
        if name in DEFAULTS:
            management.AssignDefaultToField(f"{file}/{table}", name, DEFAULTS[name])


def make_class(file, table, shape, srid, spec, cogo=False, track=True):
    """A controlled class: its fields, global IDs, editor tracking and the domains of its fields."""
    from . import _attachments, _gdbtools
    _gpkg.create(f"{file}/{table}", shape, srid, _columns(spec))
    connection = _gpkg.connect(file)
    try:
        _attachments.add_global_ids(connection, table)
        connection.commit()
    finally:
        connection.close()
    if track:
        _gdbtools.EnableEditorTracking(f"{file}/{table}", add_fields="ADD_FIELDS")
    if cogo:
        _gdbtools.EnableCOGO(f"{file}/{table}")
    assign_domains(file, table, spec)


# The default attribute rules of *Parcel fabric attribute rules*; their scripts and error numbers are grapy's own
# Arcade for the behaviour the page states (INFERRED, §5.8 (t)).
PRESERVE = ("if ($feature.IsFixed == 1 || !IsEmpty($feature.X) || !IsEmpty($feature.Y) || !IsEmpty($feature.Z)) "
            "{ return 1 }\nreturn $feature.Preserve")
SHAPE_IS_FIXED = ("if ($originalFeature.IsFixed == 1 && !Equals(Geometry($feature), Geometry($originalFeature))) "
                  "{ return false }\nreturn true")
MUST_HAVE_A_RECORD = "return !IsEmpty($feature.CreatedByRecord)"


def add_rules(file, table, role):
    from . import _ruletools
    path = f"{file}/{table}"
    if role == "points":
        _ruletools.AddAttributeRule(path, "PRESERVE", "CALCULATION", PRESERVE, triggering_events=["INSERT", "UPDATE"],
                                    field="Preserve", description="Preserve is Yes for a fixed point or one with "
                                                                  "recorded coordinates.")
        _ruletools.AddAttributeRule(path, "SHAPE IS FIXED", "CONSTRAINT", SHAPE_IS_FIXED, triggering_events=["UPDATE"],
                                    error_number=1, error_message="The point's shape is fixed (Fixed Shape is Yes).")
    else:
        _ruletools.AddAttributeRule(path, "MUST HAVE A RECORD", "VALIDATION", MUST_HAVE_A_RECORD, error_number=2,
                                    error_message="The feature has no record (Created By Record is empty).",
                                    severity=3)


def _register(file, rows):
    connection = _gpkg.connect(file)
    try:
        ensure(connection)
        for row in rows:
            connection.execute("insert or replace into grapy_parcel_class values (?, ?, ?, ?, ?, ?)", row)
        connection.commit()
    finally:
        connection.close()


def create(file, dataset, name):
    from . import management
    srid = srid_of(file, dataset)
    connection = _gpkg.connect(file)
    try:
        ensure(connection)
        if connection.execute("select 1 from grapy_parcel_fabric where dataset = ?", (dataset,)).fetchone():
            raise ExecuteError(f"ERROR 000258: the feature dataset '{dataset}' already has a parcel fabric; there can be "
                               "only one parcel fabric in a feature dataset.")
        if connection.execute("select 1 from grapy_parcel_fabric where name = ?", (name,)).fetchone():
            raise ExecuteError(f"ERROR 000258: a parcel fabric named '{name}' already exists in the workspace.")
        taken = {r[0] for r in connection.execute("select table_name from gpkg_contents")}
        connection.commit()
    finally:
        connection.close()
    classes = {"records": f"{dataset}/{name}_Records", "points": f"{dataset}/{name}_Points",
               "connections": f"{dataset}/{name}_Connections",
               "adjustment_lines": f"{dataset}/{name}_AdjustmentLines",
               "adjustment_points": f"{dataset}/{name}_AdjustmentPoints",
               "adjustment_vectors": f"{dataset}/{name}_AdjustmentVectors"}
    for table in classes.values():
        if table in taken:
            raise ExecuteError(f"ERROR 000258: '{table}' already exists.")
    _domains(file)
    make_class(file, classes["records"], "Polygon", srid, RECORD_FIELDS)
    make_class(file, classes["points"], "Point", srid, POINT_FIELDS)
    make_class(file, classes["connections"], "Polyline", srid, CONNECTION_FIELDS, cogo=True)
    make_class(file, classes["adjustment_lines"], "Polyline", srid, ADJUSTMENT_LINE_FIELDS)
    make_class(file, classes["adjustment_points"], "Point", srid, ADJUSTMENT_POINT_FIELDS)
    make_class(file, classes["adjustment_vectors"], "Polyline", srid, ADJUSTMENT_VECTOR_FIELDS)
    add_rules(file, classes["points"], "points")
    add_rules(file, classes["connections"], "connections")
    lines = f"{file}/{classes['adjustment_lines']}"
    management.SetSubtypeField(lines, "MeasurementType")
    for code, text in MEASUREMENT_TYPES:
        management.AddSubtype(lines, code, text)
    topology = f"{name}_Topology"
    management.CreateTopology(f"{file}/{dataset}", topology)
    connection = _gpkg.connect(file)
    try:
        connection.execute("insert into grapy_parcel_fabric values (?, ?, ?, 0)", (name, dataset, topology))
        connection.commit()
    finally:
        connection.close()
    _register(file, [(table, name, role, None, 0, 0) for role, table in classes.items()])
    # A new fabric's topology starts enabled, as Pro 3.7's does (measured 2026-10-31: EnableParcelTopology on a new
    # fabric warns 002007 that the topology must be disabled).
    fab = load(f"{file}/{dataset}/{name}")
    enable_topology(fab)
    return load(f"{file}/{dataset}/{name}")


def require_topology(fab, enabled, tool):
    if fab.enabled != enabled:
        raise ExecuteError(f"ERROR 160196: {tool}: the parcel fabric topology must be "
                           f"{'enabled (EnableParcelTopology)' if enabled else 'disabled (DisableParcelTopology)'}.")


def add_type(fab, name, administrative, strata):
    # Pro 3.7 adds a parcel type with the topology enabled too (measured); the type's classes and rules then join it.
    name = str(name).strip()
    if not name or "/" in name:
        raise ExecuteError(f"ERROR 000800: AddParcelType: '{name}' is not a parcel type name.")
    if any(t.name.lower() == name.lower() for t in fab.types):
        raise ExecuteError(f"ERROR 000258: AddParcelType: the parcel type '{name}' already exists.")
    polygons, lines = f"{fab.dataset}/{name}", f"{fab.dataset}/{name}_Lines"
    connection = _gpkg.connect(fab.file)
    try:
        taken = {r[0].lower() for r in connection.execute("select table_name from gpkg_contents")}
    finally:
        connection.close()
    for table in (polygons, lines):
        if table.lower() in taken:
            raise ExecuteError(f"ERROR 000258: AddParcelType: '{table}' already exists.")
    floor = [("FloorOrder", "Integer")] if strata else []
    make_class(fab.file, polygons, "Polygon", fab.srid, POLYGON_FIELDS + floor)
    make_class(fab.file, lines, "Polyline", fab.srid, LINE_FIELDS + floor, cogo=True)
    add_rules(fab.file, polygons, "polygons")
    add_rules(fab.file, lines, "lines")
    _register(fab.file, [(polygons, fab.name, "polygons", name, int(administrative), int(strata)),
                         (lines, fab.name, "lines", name, int(administrative), int(strata))])
    if fab.enabled:
        enable_topology(load(fab.path))
    return polygons, lines


def remove_type(fab, name):
    require_topology(fab, False, "RemoveParcelType")
    found = fab.type_named(name, "RemoveParcelType")
    connection = _gpkg.connect(fab.file)
    try:
        connection.execute("delete from grapy_parcel_class where class_name in (?, ?)", (found.polygons, found.lines))
        _remove_from_topology(connection, fab, [found.polygons, found.lines], rules_only_system=False)
        connection.commit()
    finally:
        connection.close()
    return found.polygons, found.lines


# ---- the topology -------------------------------------------------------------------------------------------------


def system_rules(fab):
    """(rule, origin, destination) of each system rule of the fabric's present classes."""
    rules = [(LINE_RULES[0], fab.connections, fab.points)]
    for each in fab.types:
        rules.append((LINE_RULES[0], each.lines, fab.points))
        rules.extend((rule, each.lines, None) for rule in LINE_RULES[1:])
        if each.administrative:     # its polygons stay out of the topology, as documented; its lines do not
            continue
        rules.append((LINE_AREA_RULE, each.lines, each.polygons))
        rules.append((AREA_LINE_RULE, each.polygons, each.lines))
    return rules


def topology_classes(fab):
    found = [(fab.points, RANKS["points"]), (fab.connections, RANKS["connections"])]
    for each in fab.types:
        found.append((each.lines, RANKS["lines"]))
        if not each.administrative:
            found.append((each.polygons, RANKS["polygons"]))
    return found


def enable_topology(fab, tool=None):
    """The fabric's classes (not an administrative type's polygons) and the six system rules added to its topology.
    Run by a tool on an enabled topology, Pro 3.7's warning 002007 (measured) and the classes and rules made sure of."""
    from . import _topology
    if tool and fab.enabled:
        from ._runtime import AddWarning
        AddWarning("WARNING 002007: Parcel topology must be disabled.")
    connection = _gpkg.connect(fab.file)
    try:
        _topology.ensure(connection)
        held = {r[0] for r in connection.execute("select class_name from grapy_topology_class where topology = ?",
                                                 (fab.topology,))}
        for table, rank in topology_classes(fab):
            if table not in held:
                connection.execute("insert into grapy_topology_class (topology, class_name, xy_rank, z_rank) "
                                   "values (?, ?, ?, 1)", (fab.topology, table, rank))
        for rule, origin, destination in system_rules(fab):
            if not connection.execute("select 1 from grapy_topology_rule where topology = ? and rule_type = ? and "
                                      "origin = ? and destination is ? and origin_subtype is null and "
                                      "destination_subtype is null", (fab.topology, rule, origin, destination)).fetchone():
                connection.execute("insert into grapy_topology_rule (topology, rule_type, origin, destination) values "
                                   "(?, ?, ?, ?)", (fab.topology, rule, origin, destination))
        connection.execute("update grapy_parcel_fabric set topology_enabled = 1 where name = ?", (fab.name,))
        connection.commit()
    finally:
        connection.close()


def _remove_from_topology(connection, fab, tables, rules_only_system=True):
    from . import _topology
    _topology.ensure(connection)
    system = {(r, o, d) for r, o, d in system_rules(fab)}
    for rule_id, rule, origin, destination, os_, ds_ in connection.execute(
            "select rule_id, rule_type, origin, destination, origin_subtype, destination_subtype from grapy_topology_rule "
            "where topology = ?", (fab.topology,)).fetchall():
        is_system = (rule, origin, destination) in system and os_ is None and ds_ is None
        touches = origin in tables or destination in tables
        if (is_system and touches) or (not rules_only_system and touches):
            connection.execute("delete from grapy_topology_error where rule_id = ?", (rule_id,))
            connection.execute("delete from grapy_topology_rule where rule_id = ?", (rule_id,))
    still = {t for row in connection.execute("select origin, destination from grapy_topology_rule where topology = ?",
                                             (fab.topology,)) for t in row if t}
    for table in tables:
        if table not in still:
            connection.execute("delete from grapy_topology_class where topology = ? and class_name = ?",
                               (fab.topology, table))


def disable_topology(fab):
    """The system rules and the fabric's classes taken out of the topology; a user rule stays, and with it the
    fabric classes it names (INFERRED, §5.8 (e): a rule cannot name a class outside its topology). On a disabled
    topology, Pro 3.7's warning 002006 (measured) and nothing else."""
    if not fab.enabled:
        from ._runtime import AddWarning
        AddWarning("WARNING 002006: Parcel topology must be enabled.")
        return
    connection = _gpkg.connect(fab.file)
    try:
        _remove_from_topology(connection, fab, [t for t, _ in topology_classes(fab)])
        connection.execute("update grapy_parcel_fabric set topology_enabled = 0 where name = ?", (fab.name,))
        connection.commit()
    finally:
        connection.close()


# ---- extents --------------------------------------------------------------------------------------------------------


def extent_box(value, tool, fab=None):
    """An extent parameter as a shapely box, or None for the whole fabric (MAXOF, MINOF — one input's intersection is
    its own extent — or nothing)."""
    if not given(value) or str(value).strip().upper() in ("DEFAULT", "MAXOF", "MINOF"):
        return None
    if hasattr(value, "XMin"):
        return shapely.box(value.XMin, value.YMin, value.XMax, value.YMax)
    if str(value).strip().upper() == "DISPLAY":
        raise NotBuiltError(f"arcpy.parcel.{tool}: the extent DISPLAY is not built in grapy: a tool run on the server has "
                            "no map display; give coordinates, an Extent or a dataset.")
    parts = str(value).split()
    if len(parts) >= 4:
        try:
            return shapely.box(*(float(p) for p in parts[:4]))
        except ValueError:
            pass
    try:
        _, _, features = read_input(value, tool)
    except Exception:
        raise ExecuteError(f"ERROR 000800: {tool}: '{value}' is not an extent.") from None
    shapes = [f.shape for f in features if f.shape is not None and not f.shape.is_empty]
    if not shapes:
        raise ExecuteError(f"ERROR 000800: {tool}: '{value}' has no features to take an extent from.")
    return shapely.box(*shapely.union_all(shapes).bounds)


def linear(value, srid, default=None):
    """A linear unit parameter in the fabric's units."""
    from .analysis import _distance
    if not given(value):
        if default is None:
            return None
        value = default
    try:
        return _distance(value, srid)
    except (ValueError, TypeError):
        raise ExecuteError(f"ERROR 000800: '{value}' is not a linear unit.") from None


def word(value, default, allowed, tool, name):
    if isinstance(value, bool):
        return allowed[0] if value else allowed[1]
    text = str(value).strip().upper() if given(value) else default
    if text == "TRUE":
        return allowed[0]
    if text == "FALSE":
        return allowed[1]
    if text not in allowed:
        raise ExecuteError(f"ERROR 000800: {tool}: {name} is {' or '.join(allowed)}, not '{value}'.")
    return text
