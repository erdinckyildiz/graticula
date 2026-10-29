"""Topology held in the GeoPackage a tool works on — ADR-225.

A topology lives in a feature dataset (management.CreateFeatureDataset: a table-name prefix, ``<dataset>/<class>``).
Its state is grapy's own tables, registered in ``gpkg_extensions`` as the ``grapy_topology`` extension so any other
reader can see they are there and leave them alone (INFERRED, ADR-225 §5.5):

- ``grapy_topology``: name, dataset, cluster tolerance.
- ``grapy_topology_class``: the feature classes in it, with their object-class id and xy and z ranks.
- ``grapy_topology_rule``: each rule, its type and its origin and destination classes.
- ``grapy_topology_error``: what the last validation found: geometry, rule, origin and destination features.

Rules are evaluated with shapely. Each rule's meaning is the one-line definition on the AddRuleToTopology page; the
shape of each error (point, line or area, and where) is INFERRED for each rule in ``_RULES`` below, the *Geodatabase
topology rules* page having answered 404 when it was checked (2026-10-30).
"""
from __future__ import annotations

import os

import shapely
from shapely.geometry import LineString, Point as ShapelyPoint

from . import _gpkg

EXTENSION = "grapy_topology"
DEFINITION = "Graticula ADR-225: grapy's topology tables"
TABLES = ("grapy_topology", "grapy_topology_class", "grapy_topology_rule", "grapy_topology_error")


def ensure(connection):
    connection.execute("""create table if not exists grapy_topology (name text primary key, dataset text not null,
        cluster_tolerance double not null)""")
    connection.execute("""create table if not exists grapy_topology_class (class_id integer primary key autoincrement,
        topology text not null, class_name text not null, xy_rank integer not null, z_rank integer not null,
        unique (topology, class_name))""")
    connection.execute("""create table if not exists grapy_topology_rule (rule_id integer primary key autoincrement,
        topology text not null, rule_type text not null, origin text not null, destination text)""")
    connection.execute("""create table if not exists grapy_topology_error (error_id integer primary key autoincrement,
        topology text not null, rule_id integer not null, kind text not null, origin_class text, origin_oid integer,
        destination_class text, destination_oid integer, rule_type text not null, description text not null,
        is_exception integer not null default 0, shape blob)""")
    register(connection, TABLES, EXTENSION, DEFINITION)


def register(connection, tables, extension, definition):
    connection.execute("""create table if not exists gpkg_extensions (table_name text, column_name text,
        extension_name text not null, definition text not null, scope text not null,
        constraint ge_tce unique (table_name, column_name, extension_name))""")
    for table in tables:
        connection.execute("insert into gpkg_extensions select ?, null, ?, ?, 'read-write' where not exists (select 1 "
                           "from gpkg_extensions where table_name = ? and extension_name = ?)",
                           (table, extension, definition, table, extension))


def locate(path):
    """(file, dataset, name) of a topology path ``<file>.gpkg/<dataset>/<topology>``."""
    file, table = _gpkg.split(os.fspath(path) if isinstance(path, os.PathLike) else str(path))
    if not table or "/" not in table:
        raise ValueError(f"'{path}' is no topology: write <file>.gpkg/<feature dataset>/<topology>.")
    dataset, name = table.rsplit("/", 1)
    return file, dataset, name


# ---- rules --------------------------------------------------------------------------------------------------------
#
# Each entry: the rule's kinds of input ("area", "line", "point"; one for a rule within a class, two for a rule between
# classes), the esriTopologyRuleType name ExportTopologyErrors writes in RuleType (INFERRED: the ArcObjects
# enumeration's names), and the function finding its errors.

def _parts(shape):
    return list(shape.geoms) if hasattr(shape, "geoms") else [shape]


def _of_dimension(shape, dimension):
    if shape is None or shape.is_empty:
        return []
    found = []
    for part in _parts(shape):
        if part.geom_type == "GeometryCollection":
            found.extend(_of_dimension(part, dimension))
        elif {"Point": 0, "MultiPoint": 0, "LineString": 1, "LinearRing": 1, "MultiLineString": 1,
              "Polygon": 2, "MultiPolygon": 2}.get(part.geom_type) == dimension:
            found.append(part)
    return found


def _ends(line):
    """The two end points of each part of a line."""
    out = []
    for part in _parts(line):
        coordinates = list(part.coords)
        out.extend([coordinates[0][:2], coordinates[-1][:2]])
    return out


def _pairs(features):
    for i, a in enumerate(features):
        for b in features[i + 1:]:
            yield a, b


def _overlaps(a, b, dimension):
    """Where two shapes share a piece of the given dimension (2: area, 1: line)."""
    return [p for p in _of_dimension(a[1].intersection(b[1]), dimension) if (p.area if dimension == 2 else p.length) > 0]


def _crossings(a, b, endpoints_of_either):
    """Points where two lines meet that are not end points — of either line (``endpoints_of_either``) or of both."""
    points = []
    ends_a, ends_b = set(_ends(a[1])), set(_ends(b[1]))
    for p in _of_dimension(a[1].intersection(b[1]), 0):
        for q in _parts(p):
            xy = (q.x, q.y)
            at_end = (xy in ends_a or xy in ends_b) if endpoints_of_either else (xy in ends_a and xy in ends_b)
            if not at_end:
                points.append(ShapelyPoint(xy))
    return points


def _no_overlap_area(features, _):
    return [(p, a[0], b[0]) for a, b in _pairs(features) for p in _overlaps(a, b, 2)]


def _no_gaps(features, _):
    """INFERRED: a line error along the outline of every void — the holes of the class's union and its outer edge, which
    ArcGIS users mark as an exception."""
    union = shapely.union_all([f[1] for f in features])
    errors = []
    for polygon in _parts(union):
        if polygon.is_empty:
            continue
        errors.append((LineString(polygon.exterior.coords), 0, 0))
        errors.extend((LineString(ring.coords), 0, 0) for ring in polygon.interiors)
    return errors


def _no_overlap_line(features, _):
    return [(p, a[0], b[0]) for a, b in _pairs(features) for p in _overlaps(a, b, 1)]


def _no_intersect_line(features, _, touch_interior=False):
    errors = []
    for a, b in _pairs(features):
        errors.extend((p, a[0], b[0]) for p in _overlaps(a, b, 1))
        errors.extend((p, a[0], b[0]) for p in _crossings(a, b, endpoints_of_either=not touch_interior))
    return errors


def _no_dangles(features, _):
    errors = []
    for oid, line in features:
        others = shapely.union_all([f[1] for f in features if f[0] != oid]) if len(features) > 1 else None
        ends = _ends(line)
        for xy in ends:
            point = ShapelyPoint(xy)
            closed = ends.count(xy) > 1
            if not closed and (others is None or not others.intersects(point)):
                errors.append((point, oid, 0))
    return errors


def _no_pseudo_nodes(features, _):
    """INFERRED: a point error where exactly two line ends meet and nothing else touches."""
    count: dict = {}
    for oid, line in features:
        for xy in _ends(line):
            count.setdefault(xy, []).append(oid)
    errors = []
    for xy, owners in count.items():
        if len(owners) != 2:
            continue
        point = ShapelyPoint(xy)
        touching = [f for f in features if f[0] not in owners and f[1].intersects(point)]
        if not touching:
            errors.append((point, owners[0], owners[1] if owners[1] != owners[0] else 0))
    return errors


def _segments(line):
    out = []
    for part in _parts(line):
        coordinates = list(part.coords)
        out.extend(LineString([a, b]) for a, b in zip(coordinates, coordinates[1:]))
    return out


def _self_overlap(features, _):
    errors = []
    for oid, line in features:
        pieces = _segments(line)
        for i, a in enumerate(pieces):
            for b in pieces[i + 1:]:
                errors.extend((p, oid, 0) for p in _of_dimension(a.intersection(b), 1) if p.length > 0)
    return errors


def _self_intersect(features, _):
    """Points where two segments of one line that are not neighbours meet, a closed line's start and end excepted."""
    errors = []
    for oid, line in features:
        for part in _parts(line):
            pieces = _segments(part)
            closed = list(part.coords)[0] == list(part.coords)[-1]
            for i, a in enumerate(pieces):
                for j in range(i + 2, len(pieces)):
                    if closed and i == 0 and j == len(pieces) - 1:
                        continue
                    errors.extend((p, oid, 0) for p in _of_dimension(a.intersection(pieces[j]), 0))
    return errors


def _single_part(features, _):
    return [(line, oid, 0) for oid, line in features if len(_parts(line)) > 1]


def _disjoint(features, _):
    return [(ShapelyPoint(a[1].coords[0]), a[0], b[0]) for a, b in _pairs(features) if a[1].equals(b[1])]


def _two(test):
    """A rule between two classes: each origin feature with the destination features."""
    def run(features, others):
        return test(features, others)
    return run


def _not_covered(features, others, dimension, cover_of):
    union = shapely.union_all([cover_of(f[1]) for f in others]) if others else None
    errors = []
    for oid, shape in features:
        rest = shape if union is None else shape.difference(union)
        errors.extend((p, oid, 0) for p in _of_dimension(rest, dimension)
                      if (p.area if dimension == 2 else p.length if dimension == 1 else 1) > 0)
    return errors


def _points_where(features, others, keep):
    return [(shape, oid, 0) for oid, shape in features if not keep(shape, others)]


def _boundary(shape):
    return shape.boundary


def _lines_and_points_ends(others):
    return {xy for _, line in others for xy in _ends(line)}


_RULES = {
    "Must Not Overlap (Area)": (("area",), "esriTRTAreaNoOverlap", _no_overlap_area),
    "Must Not Have Gaps (Area)": (("area",), "esriTRTAreaNoGaps", _no_gaps),
    "Must Not Overlap (Line)": (("line",), "esriTRTLineNoOverlap", _no_overlap_line),
    "Must Not Intersect (Line)": (("line",), "esriTRTLineNoIntersection", _no_intersect_line),
    "Must Not Intersect Or Touch Interior (Line)": (
        ("line",), "esriTRTLineNoIntersectOrInteriorTouch", lambda f, o: _no_intersect_line(f, o, True)),
    "Must Not Have Dangles (Line)": (("line",), "esriTRTLineNoDangles", _no_dangles),
    "Must Not Have Pseudo-Nodes (Line)": (("line",), "esriTRTLineNoPseudos", _no_pseudo_nodes),
    "Must Not Self-Overlap (Line)": (("line",), "esriTRTLineNoSelfOverlap", _self_overlap),
    "Must Not Self-Intersect (Line)": (("line",), "esriTRTLineNoSelfIntersect", _self_intersect),
    "Must Be Single Part (Line)": (("line",), "esriTRTLineNoMultipart", _single_part),
    "Must Be Disjoint (Point)": (("point",), "esriTRTPointDisjoint", _disjoint),
    "Must Not Overlap With (Area-Area)": (
        ("area", "area"), "esriTRTAreaNoOverlapArea",
        lambda f, o: [(p, a[0], b[0]) for a in f for b in o for p in _overlaps(a, b, 2)]),
    "Must Be Covered By Feature Class Of (Area-Area)": (
        ("area", "area"), "esriTRTAreaCoveredByAreaClass", lambda f, o: _not_covered(f, o, 2, lambda s: s)),
    "Must Cover Each Other (Area-Area)": (
        ("area", "area"), "esriTRTAreaAreaCoverEachOther",
        lambda f, o: _not_covered(f, o, 2, lambda s: s) + [(p, 0, oid) for p, oid, _ in _not_covered(o, f, 2, lambda s: s)]),
    "Must Be Covered By (Area-Area)": (
        ("area", "area"), "esriTRTAreaCoveredByArea",
        lambda f, o: [(shape, oid, 0) for oid, shape in f if not any(other.covers(shape) for _, other in o)]),
    "Boundary Must Be Covered By Boundary Of (Area-Area)": (
        ("area", "area"), "esriTRTAreaBoundaryCoveredByAreaBoundary",
        lambda f, o: _not_covered([(oid, s.boundary) for oid, s in f], o, 1, _boundary)),
    "Boundary Must Be Covered By (Area-Line)": (
        ("area", "line"), "esriTRTAreaBoundaryCoveredByLine",
        lambda f, o: _not_covered([(oid, s.boundary) for oid, s in f], o, 1, lambda s: s)),
    "Contains Point (Area-Point)": (
        ("area", "point"), "esriTRTAreaContainPoint",
        lambda f, o: [(shape, oid, 0) for oid, shape in f if not any(shape.contains(p) for _, p in o)]),
    "Contains One Point (Area-Point)": (
        ("area", "point"), "esriTRTAreaContainOnePoint",
        lambda f, o: [(shape, oid, 0) for oid, shape in f if sum(1 for _, p in o if shape.contains(p)) != 1]
        + [(p, 0, pid) for pid, p in o if sum(1 for _, s in f if s.contains(p)) != 1]),
    "Must Be Covered By Boundary Of (Line-Area)": (
        ("line", "area"), "esriTRTLineCoveredByAreaBoundary", lambda f, o: _not_covered(f, o, 1, _boundary)),
    "Must Be Inside (Line-Area)": (
        ("line", "area"), "esriTRTLineInsideArea", lambda f, o: _not_covered(f, o, 1, lambda s: s)),
    "Must Be Covered By Feature Class Of (Line-Line)": (
        ("line", "line"), "esriTRTLineCoveredByLineClass", lambda f, o: _not_covered(f, o, 1, lambda s: s)),
    "Must Not Overlap With (Line-Line)": (
        ("line", "line"), "esriTRTLineNoOverlapLine",
        lambda f, o: [(p, a[0], b[0]) for a in f for b in o for p in _overlaps(a, b, 1)]),
    "Must Not Intersect With (Line-Line)": (
        ("line", "line"), "esriTRTLineNoIntersectLine",
        lambda f, o: [(p, a[0], b[0]) for a in f for b in o for p in _overlaps(a, b, 1) + _crossings(a, b, True)]),
    "Must Not Intersect or Touch Interior With (Line-Line)": (
        ("line", "line"), "esriTRTLineNoIntersectOrInteriorTouchLine",
        lambda f, o: [(p, a[0], b[0]) for a in f for b in o for p in _overlaps(a, b, 1) + _crossings(a, b, False)]),
    "Endpoint Must Be Covered By (Line-Point)": (
        ("line", "point"), "esriTRTLineEndpointCoveredByPoint",
        lambda f, o: [(ShapelyPoint(xy), oid, 0) for oid, line in f for xy in dict.fromkeys(_ends(line))
                      if not any(p.equals(ShapelyPoint(xy)) for _, p in o)]),
    "Must Be Covered By Boundary Of (Point-Area)": (
        ("point", "area"), "esriTRTPointCoveredByAreaBoundary",
        lambda f, o: _points_where(f, o, lambda p, others: any(s.boundary.intersects(p) for _, s in others))),
    "Must Be Properly Inside (Point-Area)": (
        ("point", "area"), "esriTRTPointProperlyInsideArea",
        lambda f, o: _points_where(f, o, lambda p, others: any(s.contains(p) for _, s in others))),
    "Must Be Covered By (Point-Line)": (
        ("point", "line"), "esriTRTPointCoveredByLine",
        lambda f, o: _points_where(f, o, lambda p, others: any(s.intersects(p) for _, s in others))),
    "Must Be Covered By Endpoint Of (Point-Line)": (
        ("point", "line"), "esriTRTPointCoveredByLineEndpoint",
        lambda f, o: _points_where(f, o, lambda p, others: (p.x, p.y) in _lines_and_points_ends(others))),
    "Must Coincide With (Point-Point)": (
        ("point", "point"), "esriTRTPointCoincidePoint",
        lambda f, o: _points_where(f, o, lambda p, others: any(q.equals(p) for _, q in others))),
}

_SHAPE_KINDS = {"Polygon": "area", "Polyline": "line", "Point": "point", "Multipoint": "point"}


def rule(rule_type):
    """The rule a name names, matched without regard to case."""
    for name, entry in _RULES.items():
        if name.lower() == str(rule_type).strip().lower():
            return name, entry
    return None, None


def description(rule_type):
    """The rule's name as the error inspector shows it, without its geometry types. INFERRED."""
    return rule_type.rsplit(" (", 1)[0]


def evaluate(rule_type, features, others):
    """Errors as (shape, origin object id, destination object id)."""
    _, entry = rule(rule_type)
    return entry[2](features, others)


def kind_of(shape):
    return {0: "point", 1: "line", 2: "area"}[shapely.get_dimensions(shape)]


# ---- clustering, the equivalent of ADR-225 §5.3 -----------------------------------------------------------------------

def cluster(classes, tolerance):
    """Vertices within ``tolerance`` of a vertex of a better-ranked class (lower xy rank), or of the same rank and a lower
    (class order, object id), move onto it. ``classes`` is [(rank, [(oid, shape)])]; answers the moved features as
    {(class index, oid): new shape}. EQUIVALENT (ADR-225 §5.3): Esri describes clustering only in words."""
    import numpy
    from shapely.strtree import STRtree
    vertices = []
    for index, (rank, features) in enumerate(classes):
        for oid, shape in features:
            for xy in shapely.get_coordinates(shape).tolist():
                vertices.append(((rank, index, oid), tuple(xy)))
    if not vertices or tolerance <= 0:
        return {}
    points = [ShapelyPoint(xy) for _, xy in vertices]
    tree = STRtree(points)
    target = {}
    for i, (key, xy) in enumerate(vertices):
        near = tree.query(points[i], predicate="dwithin", distance=tolerance)
        best = min((vertices[int(j)] for j in near), key=lambda v: (v[0], v[1]))
        if best[1] != xy and best[0] < key:
            target[(key[1], key[2], xy)] = best[1]
    moved = {}
    for index, (_, features) in enumerate(classes):
        for oid, shape in features:
            if not any((index, oid, tuple(xy)) in target for xy in shapely.get_coordinates(shape).tolist()):
                continue

            def move(xy, index=index, oid=oid):
                return numpy.array([target.get((index, oid, tuple(p)), tuple(p)) for p in xy.tolist()])
            moved[(index, oid)] = shapely.transform(shape, move)
    return moved


# ---- Describe ----------------------------------------------------------------------------------------------------------

class Described:
    """What Describe answers for a topology: the documented Topology properties and the Dataset group's names.
    INFERRED: ``maximumGeneratedErrorCount`` is -1, grapy setting no cap on validation's errors; ``ZClusterTolerance``
    is 0, grapy's shapes being 2D; ``featureClassNames`` are the classes' names within their feature dataset."""

    def __init__(self, path, file, dataset, name, tolerance, classes, srid):
        from ._reference import SpatialReference
        self.catalogPath = str(path)
        self.name = self.baseName = name
        self.path = f"{file}/{dataset}"
        self.dataType = self.datasetType = "Topology"
        self.clusterTolerance = float(tolerance)
        self.featureClassNames = [c.rsplit("/", 1)[1] for c in classes]
        self.maximumGeneratedErrorCount = -1.0
        self.ZClusterTolerance = 0.0
        self.spatialReference = SpatialReference(srid) if srid > 0 else None


def describe(path):
    """A Described for a topology path, or None when the path names none."""
    try:
        file, dataset, name = locate(path)
    except ValueError:
        return None
    if not os.path.exists(file):
        return None
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_topology'").fetchone():
            return None
        row = connection.execute("select dataset, cluster_tolerance from grapy_topology where name = ?", (name,)).fetchone()
        if row is None or row[0] != dataset:
            return None
        classes = [r[0] for r in connection.execute("select class_name from grapy_topology_class where topology = ? "
                                                    "order by class_id", (name,))]
        srid = connection.execute("select srs_id from grapy_feature_datasets where name = ?", (dataset,)).fetchone() \
            if connection.execute("select 1 from sqlite_master where name = 'grapy_feature_datasets'").fetchone() else None
    finally:
        connection.close()
    return Described(path, file, dataset, name, row[1], classes, int(srid[0]) if srid else 0)
