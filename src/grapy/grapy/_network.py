"""Network datasets held in the GeoPackage a tool works on, and the shortest-path solvers over them — ADR-226.

A network dataset lives in a feature dataset (management.CreateFeatureDataset), next to its line sources. Its state is
grapy's own tables, registered in ``gpkg_extensions`` as the ``grapy_network`` extension (INFERRED, ADR-226 §5.6):

- ``grapy_network``: name, feature dataset, elevation model, whether it is built.
- ``grapy_network_source``: each edge source and its connectivity policy, ``END_POINT`` or ``ANY_VERTEX``.
- ``grapy_network_attribute``: cost and restriction attributes, their units and use by default.
- ``grapy_network_evaluator``: how an attribute is read for a source and direction: the shape's ``LENGTH`` in the
  attribute's units, a ``FIELD``'s value, or a ``CONSTANT``. Script evaluators are refused.
- ``grapy_network_travel_mode``: travel modes: an impedance, time and distance attributes, the restrictions used.
- ``grapy_network_edge`` and ``grapy_network_junction``: what BuildNetwork writes.

INFERRED defaults, CreateNetworkDataset's page giving none: each line source connects at its end points; a cost
attribute ``Length`` reads each edge's length in the data's linear unit, and a travel mode ``Length`` uses it.
Connectivity, attributes and travel modes are changed with ``set_connectivity``, ``add_attribute`` and
``add_travel_mode`` (ArcGIS Pro sets them in a dialog, not with a documented tool).

Solving is Dijkstra's shortest path (Dijkstra 1959, ``scipy.sparse.csgraph``) over the travel mode's impedance, an
*equivalent* under ADR-224 §5.6: Pro's own solver, its hierarchy and its tie-breaking are not published.
"""
from __future__ import annotations

import json
import os

import numpy
import shapely
from shapely.geometry import LineString, Point as ShapelyPoint
from shapely.ops import substring

from . import _gpkg
from ._runtime import ExecuteError, NotBuiltError
from ._topology import register

EXTENSION = "grapy_network"
DEFINITION = "Graticula ADR-226: grapy's network datasets"
TABLES = ("grapy_network", "grapy_network_source", "grapy_network_attribute", "grapy_network_evaluator",
          "grapy_network_travel_mode", "grapy_network_edge", "grapy_network_junction")
DIJKSTRA = ("Dijkstra's shortest path over the network's impedance", "Dijkstra 1959")

# arcpy.nax's DistanceUnits and a network attribute's units: the imperial ones US survey units, as Pro measures them
# (D-323).
from ._units import NETWORK as _LINEAR  # noqa: E402

_TIME = {"SECONDS", "MINUTES", "HOURS", "DAYS"}


def ensure(connection):
    connection.execute("""create table if not exists grapy_network (name text primary key, dataset text not null,
        elevation_model text not null, built integer not null default 0)""")
    connection.execute("""create table if not exists grapy_network_source (network text not null, class_name text not null,
        connectivity text not null, primary key (network, class_name))""")
    connection.execute("""create table if not exists grapy_network_attribute (network text not null, name text not null,
        usage text not null, units text, use_by_default integer not null default 0, primary key (network, name))""")
    connection.execute("""create table if not exists grapy_network_evaluator (network text not null, attribute text not null,
        class_name text not null, direction text not null, kind text not null, value text)""")
    connection.execute("""create table if not exists grapy_network_travel_mode (network text not null, name text not null,
        impedance text not null, time_attribute text, distance_attribute text, restrictions text not null,
        primary key (network, name))""")
    connection.execute("""create table if not exists grapy_network_edge (network text not null, edge_id integer not null,
        class_name text not null, source_oid integer not null, from_junction integer not null, to_junction integer not null,
        from_position double not null, to_position double not null, shape blob not null)""")
    connection.execute("""create table if not exists grapy_network_junction (network text not null, junction_id integer
        not null, x double not null, y double not null)""")
    if "built_at" not in [r[1] for r in connection.execute("pragma table_info(grapy_network)")]:
        connection.execute("alter table grapy_network add column built_at text")
    register(connection, TABLES, EXTENSION, DEFINITION)


def locate(path):
    """(file, dataset, name) of a network dataset path ``<file>.gpkg/<dataset>/<network>``, or of a layer naming one."""
    path = str(LAYERS.get(str(path), path))
    file, table = _gpkg.split(path)
    if not table or "/" not in table:
        raise ExecuteError(f"ERROR 000732: '{path}' is no network dataset: write <file>.gpkg/<feature dataset>/<network>.")
    dataset, name = table.rsplit("/", 1)
    return file, dataset, name


def open_network(path):
    file, dataset, name = locate(path)
    connection = _gpkg.connect(file)
    ensure(connection)
    if not connection.execute("select 1 from grapy_network where name = ? and dataset = ?", (name, dataset)).fetchone():
        connection.close()
        raise ExecuteError(f"ERROR 000732: there is no network dataset '{name}' in '{dataset}'.")
    return connection, file, dataset, name


# Network dataset layers (MakeNetworkDatasetLayer): a name for a network dataset's path, for this process.
LAYERS: dict = {}


def _units_name(srid):
    from ._reference import SpatialReference
    if srid <= 0:
        return "Unknown"
    reference = SpatialReference(srid)
    if reference.type == "Geographic":
        return "DecimalDegrees"
    per = reference.metersPerUnit
    for name, metres in _LINEAR.items():
        if abs(metres - per) < 1e-12:
            return name.title().replace("Nauticalmiles", "NauticalMiles")
    return "Unknown"


def create(file, dataset, name, sources, elevation_model, srid):
    connection = _gpkg.connect(file)
    try:
        ensure(connection)
        if connection.execute("select 1 from grapy_network where name = ?", (name,)).fetchone():
            raise ExecuteError(f"ERROR 000258: a network dataset named '{name}' already exists.")
        connection.execute("insert into grapy_network (name, dataset, elevation_model, built) values (?, ?, ?, 0)",
                           (name, dataset, elevation_model))
        for table in sources:
            connection.execute("insert into grapy_network_source values (?, ?, 'END_POINT')", (name, table))
            connection.execute("insert into grapy_network_evaluator values (?, 'Length', ?, 'BOTH', 'LENGTH', null)",
                               (name, table))
        connection.execute("insert into grapy_network_attribute values (?, 'Length', 'Cost', ?, 0)", (name, _units_name(srid)))
        connection.execute("insert into grapy_network_travel_mode values (?, 'Length', 'Length', null, 'Length', '[]')", (name,))
        connection.commit()
    finally:
        connection.close()


def _dirty(connection, name):
    connection.execute("update grapy_network set built = 0 where name = ?", (name,))


def set_connectivity(network, source, policy):
    """A source's connectivity policy, ``END_POINT`` or ``ANY_VERTEX``. The network must be built again."""
    policy = str(policy).upper()
    if policy not in ("END_POINT", "ANY_VERTEX"):
        raise NotBuiltError(f"Connectivity policy '{policy}' is not built in grapy; END_POINT and ANY_VERTEX are.")
    connection, _, dataset, name = open_network(network)
    try:
        table = _source_name(connection, name, dataset, source)
        connection.execute("update grapy_network_source set connectivity = ? where network = ? and class_name = ?",
                           (policy, name, table))
        _dirty(connection, name)
        connection.commit()
    finally:
        connection.close()


def _source_name(connection, name, dataset, source):
    candidates = {r[0] for r in connection.execute("select class_name from grapy_network_source where network = ?", (name,))}
    for candidate in (str(source), f"{dataset}/{source}", _gpkg.split(str(source))[1] if ".gpkg" in str(source).lower() else None):
        if candidate in candidates:
            return candidate
    raise ExecuteError(f"ERROR 000732: '{source}' is not a source of the network.")


def add_attribute(network, attribute, usage, units=None, evaluators=(), use_by_default=False):
    """A cost or restriction attribute with its evaluators — ``(source, direction, kind, value)``, direction
    ``FROM_TO``, ``TO_FROM`` or ``BOTH``, kind ``LENGTH`` (costs in a linear unit), ``FIELD`` (a field's value; a
    restriction is restricted where it is true) or ``CONSTANT``. A script evaluator is refused. The network must be
    built again."""
    usage = str(usage).title()
    if usage not in ("Cost", "Restriction"):
        raise NotBuiltError(f"A network attribute of usage '{usage}' is not built in grapy; Cost and Restriction are.")
    connection, _, dataset, name = open_network(network)
    try:
        connection.execute("delete from grapy_network_attribute where network = ? and name = ?", (name, attribute))
        connection.execute("delete from grapy_network_evaluator where network = ? and attribute = ?", (name, attribute))
        connection.execute("insert into grapy_network_attribute values (?, ?, ?, ?, ?)",
                           (name, attribute, usage, units, int(bool(use_by_default))))
        for source, direction, kind, value in evaluators:
            kind = str(kind).upper()
            if kind not in ("LENGTH", "FIELD", "CONSTANT"):
                raise NotBuiltError(f"A network evaluator of kind '{kind}' is not built in grapy: script evaluators are "
                                    "refused (ADR-226 §5.1).")
            if kind == "LENGTH" and (usage != "Cost" or str(units).upper().replace(" ", "") not in _LINEAR):
                raise ExecuteError("ERROR 030000: a LENGTH evaluator gives a cost in a linear unit.")
            connection.execute("insert into grapy_network_evaluator values (?, ?, ?, ?, ?, ?)",
                               (name, attribute, _source_name(connection, name, dataset, source), str(direction).upper(),
                                kind, None if value is None else str(value)))
        _dirty(connection, name)
        connection.commit()
    finally:
        connection.close()


def add_travel_mode(network, mode, impedance, restrictions=(), time_attribute=None, distance_attribute=None):
    """A travel mode: the cost attribute minimised and the restrictions honoured."""
    connection, _, _, name = open_network(network)
    try:
        attributes = {r[0]: r[1] for r in connection.execute("select name, usage from grapy_network_attribute where "
                                                             "network = ?", (name,))}
        if attributes.get(impedance) != "Cost" or any(attributes.get(r) != "Restriction" for r in restrictions):
            raise ExecuteError("ERROR 030000: a travel mode's impedance is a cost attribute and its restrictions are "
                               "restriction attributes of the network.")
        connection.execute("insert or replace into grapy_network_travel_mode values (?, ?, ?, ?, ?, ?)",
                           (name, mode, impedance, time_attribute, distance_attribute, json.dumps(list(restrictions))))
        connection.commit()
    finally:
        connection.close()


def travel_modes(network):
    connection, _, _, name = open_network(network)
    try:
        return {r[0]: {"name": r[0], "impedance": r[1], "timeAttributeName": r[2], "distanceAttributeName": r[3],
                       "restrictions": json.loads(r[4])}
                for r in connection.execute("select name, impedance, time_attribute, distance_attribute, restrictions "
                                            "from grapy_network_travel_mode where network = ? order by rowid", (name,))}
    finally:
        connection.close()


def attributes(network):
    connection, _, _, name = open_network(network)
    try:
        return {r[0]: {"usage": r[1], "units": r[2], "use_by_default": bool(r[3])}
                for r in connection.execute("select name, usage, units, use_by_default from grapy_network_attribute "
                                            "where network = ?", (name,))}
    finally:
        connection.close()


# ---- building ---------------------------------------------------------------------------------------------------------

def _parts(shape):
    return list(shape.geoms) if hasattr(shape, "geoms") else [shape]


def build(network):
    """Edges and junctions from the sources, by their connectivity policies: every line end is a junction; a vertex of an
    ``ANY_VERTEX`` source is one where it meets another feature's connection point (an end, or any vertex of an
    ``ANY_VERTEX`` source); each part is cut at its own connection points that are junctions. INFERRED where the page is
    silent: a multipart feature's parts are edges of the same source feature, its positions running over its whole
    length."""
    from . import da
    connection, file, dataset, name = open_network(network)
    try:
        sources = connection.execute("select class_name, connectivity from grapy_network_source where network = ? "
                                     "order by rowid", (name,)).fetchall()
    finally:
        connection.close()
    features = []
    for table, policy in sources:
        with da.SearchCursor(f"{file}/{table}", ["OID@", "SHAPE@"]) as rows:
            for oid, shape in rows:
                if shape is not None and not shape._shape.is_empty:
                    features.append((table, oid, policy, shape._shape))
    owners: dict = {}
    for index, (_, _, policy, shape) in enumerate(features):
        for part in _parts(shape):
            coordinates = [tuple(c[:2]) for c in part.coords]
            points = coordinates if policy == "ANY_VERTEX" else [coordinates[0], coordinates[-1]]
            for xy in set(points):
                owners.setdefault(xy, set()).add(index)
    junctions = {}
    for index, (_, _, policy, shape) in enumerate(features):
        for part in _parts(shape):
            coordinates = [tuple(c[:2]) for c in part.coords]
            for xy in (coordinates[0], coordinates[-1]):
                junctions.setdefault(xy, len(junctions) + 1)
    for xy, indices in owners.items():
        if len(indices) > 1:
            junctions.setdefault(xy, len(junctions) + 1)
    edges = []
    for index, (table, oid, policy, shape) in enumerate(features):
        total = shape.length
        travelled = 0.0
        for part in _parts(shape):
            coordinates = [tuple(c[:2]) for c in part.coords]
            cuts = [0] + [i for i, xy in enumerate(coordinates[1:-1], start=1)
                          if policy == "ANY_VERTEX" and xy in junctions] + [len(coordinates) - 1]
            for a, b in zip(cuts, cuts[1:]):
                piece = LineString(coordinates[a:b + 1])
                start = travelled + LineString(coordinates[:a + 1]).length if a else travelled
                edges.append((len(edges) + 1, table, oid, junctions[coordinates[a]], junctions[coordinates[b]],
                              start / total if total else 0.0, (start + piece.length) / total if total else 0.0, piece))
            travelled += part.length
    srid = _source_srid(file, sources[0][0]) if sources else 0
    connection = _gpkg.connect(file)
    try:
        connection.execute("delete from grapy_network_edge where network = ?", (name,))
        connection.execute("delete from grapy_network_junction where network = ?", (name,))
        connection.executemany("insert into grapy_network_edge values (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                               [(name, e[0], e[1], e[2], e[3], e[4], e[5], e[6], _gpkg.encode(e[7], srid)) for e in edges])
        connection.executemany("insert into grapy_network_junction values (?, ?, ?, ?)",
                               [(name, j, xy[0], xy[1]) for xy, j in junctions.items()])
        connection.execute("update grapy_network set built = 1, built_at = strftime('%Y-%m-%dT%H:%M:%f','now') where name = ?",
                           (name,))
        connection.commit()
    finally:
        connection.close()
    return len(edges), len(junctions)


# ---- the graph a travel mode sees ------------------------------------------------------------------------------------

class Graph:
    """The built network under one travel mode: each edge's cost along and against its digitised direction in every
    cost attribute, and whether it is traversable each way."""

    def __init__(self, network, travel_mode=None):
        from . import da
        connection, file, dataset, name = open_network(network)
        try:
            built = connection.execute("select built from grapy_network where name = ?", (name,)).fetchone()[0]
            if not built:
                raise ExecuteError("ERROR 030024: the network dataset is not built; run BuildNetwork.")
            self.edges = [dict(edge_id=r[0], source=r[1], oid=r[2], u=r[3], v=r[4], p0=r[5], p1=r[6],
                               shape=_gpkg.decode(r[7]))
                          for r in connection.execute("select edge_id, class_name, source_oid, from_junction, to_junction, "
                                                      "from_position, to_position, shape from grapy_network_edge where "
                                                      "network = ? order by edge_id", (name,))]
            self.junctions = {r[0]: (r[1], r[2]) for r in connection.execute(
                "select junction_id, x, y from grapy_network_junction where network = ?", (name,))}
            self.sources = [r[0] for r in connection.execute("select class_name from grapy_network_source where "
                                                             "network = ? order by rowid", (name,))]
            evaluators = connection.execute("select attribute, class_name, direction, kind, value from "
                                            "grapy_network_evaluator where network = ?", (name,)).fetchall()
            self.attributes = {r[0]: (r[1], r[2]) for r in connection.execute(
                "select name, usage, units from grapy_network_attribute where network = ?", (name,))}
        finally:
            connection.close()
        self.file, self.dataset, self.name = file, dataset, name
        self.srid = _source_srid(file, self.sources[0]) if self.sources else 0
        modes = travel_modes(network)
        if travel_mode is None or travel_mode == "":
            mode = next(iter(modes.values()))
        elif isinstance(travel_mode, dict):
            mode = travel_mode
        elif hasattr(travel_mode, "impedance"):
            mode = {"name": travel_mode.name, "impedance": travel_mode.impedance,
                    "restrictions": list(travel_mode.restrictions), "timeAttributeName": travel_mode.timeAttributeName,
                    "distanceAttributeName": travel_mode.distanceAttributeName}
        elif str(travel_mode).strip().startswith("{"):
            mode = json.loads(str(travel_mode))
        elif str(travel_mode) in modes:
            mode = modes[str(travel_mode)]
        else:
            raise ExecuteError(f"ERROR 030000: the network has no travel mode '{travel_mode}'.")
        self.mode = mode
        self.impedance = mode["impedance"]
        fields_needed: dict = {}
        for attribute, table, direction, kind, value in evaluators:
            if kind == "FIELD":
                fields_needed.setdefault(table, set()).add(value)
        values = {}
        for table, needed in fields_needed.items():
            with da.SearchCursor(f"{file}/{table}", ["OID@", *sorted(needed)]) as rows:
                for row in rows:
                    values[(table, row[0])] = dict(zip(sorted(needed), row[1:]))
        lengths = {}
        for table in self.sources:
            with da.SearchCursor(f"{file}/{table}", ["OID@", "SHAPE@LENGTH"]) as rows:
                for oid, length in rows:
                    lengths[(table, oid)] = length
        per = _per_unit(self.srid)
        self.costs = {}       # (attribute, edge index, forward) -> cost
        self.blocked = set()  # (edge index, forward)
        self.restriction_values = {}  # (attribute, edge index, forward) -> bool
        for i, edge in enumerate(self.edges):
            share = edge["shape"].length / lengths[(edge["source"], edge["oid"])] if lengths[(edge["source"], edge["oid"])] else 0
            for attribute, table, direction, kind, value in evaluators:
                if table != edge["source"]:
                    continue
                usage, units = self.attributes[attribute]
                for forward in (True, False):
                    if direction not in ("BOTH", "FROM_TO" if forward else "TO_FROM"):
                        continue
                    if kind == "LENGTH":
                        if per is None:
                            raise NotBuiltError("A LENGTH evaluator on data with a geographic or unknown reference is not "
                                                "built in grapy; project the sources first.")
                        amount = edge["shape"].length * per / _LINEAR[str(units).upper().replace(" ", "")]
                    elif kind == "CONSTANT":
                        amount = _value(value)
                    else:
                        raw = values[(table, edge["oid"])][value]
                        amount = raw if usage == "Restriction" else (float(raw) * share if raw is not None else 0.0)
                    if usage == "Restriction":
                        self.restriction_values[(attribute, i, forward)] = _truthy(amount)
                        if attribute in mode["restrictions"] and _truthy(amount):
                            self.blocked.add((i, forward))
                    else:
                        self.costs[(attribute, i, forward)] = float(amount)
        for (attribute, i, forward), amount in self.costs.items():
            if attribute == self.impedance and amount < 0:
                self.blocked.add((i, forward))

    def cost(self, attribute, i, forward, t0=0.0, t1=1.0):
        """The cost of travelling an edge, or the part of it between fractions t0 and t1 (INFERRED: in proportion)."""
        return self.costs.get((attribute, i, forward), 0.0) * abs(t1 - t0)

    def passable(self, i, forward):
        return (i, forward) not in self.blocked

    # ---- locating

    def locate(self, point, tolerance):
        """(edge index, fraction along it, snapped point, distance) of the nearest traversable edge within the
        tolerance, or None."""
        best = None
        for i, edge in enumerate(self.edges):
            if not (self.passable(i, True) or self.passable(i, False)):
                continue
            distance = edge["shape"].distance(point)
            if distance <= tolerance and (best is None or distance < best[3]):
                t = edge["shape"].project(point, normalized=True)
                best = (i, t, edge["shape"].interpolate(t, normalized=True), distance)
        return best

    def location_fields(self, located, point):
        """The documented network location fields of a located point. INFERRED: SourceID numbers the sources from 1 in
        the order they were given; SideOfEdge is 1 (right) or 2 (left) of the digitised direction."""
        if located is None:
            return {"SourceID": None, "SourceOID": None, "PosAlong": None, "SideOfEdge": None, "SnapX": None,
                    "SnapY": None, "DistanceToNetworkInMeters": None}
        i, t, snapped, distance = located
        edge = self.edges[i]
        coordinates = list(edge["shape"].coords)
        ahead = edge["shape"].interpolate(min(1.0, t + 1e-6), normalized=True)
        behind = edge["shape"].interpolate(max(0.0, t - 1e-6), normalized=True)
        cross = (ahead.x - behind.x) * (point.y - behind.y) - (ahead.y - behind.y) * (point.x - behind.x)
        per = _per_unit(self.srid) or 1.0
        return {"SourceID": self.sources.index(edge["source"]) + 1, "SourceOID": edge["oid"],
                "PosAlong": edge["p0"] + (edge["p1"] - edge["p0"]) * t, "SideOfEdge": 2 if cross > 0 else 1,
                "SnapX": snapped.x, "SnapY": snapped.y, "DistanceToNetworkInMeters": distance * per,
                "_edge": i, "_t": t, "_coords": coordinates}

    # ---- solving

    def solver(self, locations, reverse=False):
        """A Dijkstra solver over the junctions and the located points (``[(key, edge index, t)]``)."""
        return _Solver(self, locations, reverse)


def _value(text):
    if text is None:
        return 0.0
    try:
        return float(text)
    except ValueError:
        return str(text).strip().lower() in ("true", "yes", "1")


def _truthy(value):
    if value is None:
        return False
    if isinstance(value, str):
        return value.strip().lower() not in ("", "0", "false", "no", "n", "f")
    return bool(value)


def _per_unit(srid):
    from ._reference import SpatialReference
    if srid <= 0:
        return 1.0
    reference = SpatialReference(srid)
    return None if reference.type == "Geographic" else reference.metersPerUnit


def _source_srid(file, table):
    connection = _gpkg.connect(file)
    try:
        return int(_gpkg.describe(connection, table)[2])
    finally:
        connection.close()


class _Solver:
    """The sparse graph of one travel mode with located points added as nodes; arcs keep which edge piece they are."""

    def __init__(self, graph, locations, reverse):
        from scipy.sparse import csr_matrix
        self.graph = graph
        ids = sorted(graph.junctions)
        self.node = {j: k for k, j in enumerate(ids)}
        self.location_node = {}
        for key, _, _ in locations:
            self.location_node[key] = len(self.node) + len(self.location_node)
        arcs = {}

        def add(a, b, i, forward, t0, t1):
            cost = graph.cost(graph.impedance, i, forward, t0, t1)
            pair = (b, a) if reverse else (a, b)
            if pair not in arcs or cost < arcs[pair][0]:
                arcs[pair] = (cost, i, t0, t1)

        on_edge: dict = {}
        for key, i, t in locations:
            on_edge.setdefault(i, []).append((t, self.location_node[key]))
        for i, edge in enumerate(graph.edges):
            u, v = self.node[edge["u"]], self.node[edge["v"]]
            stops = [(0.0, u)] + sorted(on_edge.get(i, [])) + [(1.0, v)]
            for a in range(len(stops)):
                for b in range(a + 1, len(stops)):
                    (ta, na), (tb, nb) = stops[a], stops[b]
                    if graph.passable(i, True):
                        add(na, nb, i, True, ta, tb)
                    if graph.passable(i, False):
                        add(nb, na, i, False, tb, ta)
        size = len(self.node) + len(self.location_node)
        rows, cols, data = [], [], []
        self.arcs = {}
        for (a, b), (cost, i, t0, t1) in arcs.items():
            if a == b:
                continue
            rows.append(a)
            cols.append(b)
            data.append(cost if cost > 0 else 1e-300)
            self.arcs[(a, b)] = (i, t0, t1)
        self.matrix = csr_matrix((data, (rows, cols)), shape=(size, size))
        self.reverse = reverse

    def run(self, key):
        from scipy.sparse.csgraph import dijkstra
        distances, predecessors = dijkstra(self.matrix, indices=self.location_node[key], return_predecessors=True)
        distances = numpy.where(distances < 1e-200, 0.0, distances)
        return distances, predecessors

    def path(self, predecessors, start_key, end_key):
        """The arcs from start to end, in travel order, as (edge index, t0, t1); None when unreached."""
        start, node = self.location_node[start_key], self.location_node[end_key]
        if start == node:
            return []
        chain = []
        while node != start:
            previous = predecessors[node]
            if previous < 0:
                return None
            chain.append((previous, node))
            node = previous
        chain.reverse()
        arcs = [self.arcs[(a, b)] for a, b in chain]
        # On the reversed graph the chain runs from the end of the journey back to its start; each arc already holds
        # its edge piece in the direction of travel.
        return list(reversed(arcs)) if self.reverse else arcs

    def total(self, arcs, attribute):
        return sum(self.graph.cost(attribute, i, t1 > t0, t0, t1) for i, t0, t1 in arcs)

    def shape(self, arcs):
        """The path's line along the network."""
        pieces = []
        for i, t0, t1 in arcs:
            line = self.graph.edges[i]["shape"]
            piece = substring(line, min(t0, t1), max(t0, t1), normalized=True)
            coordinates = list(piece.coords) if piece.geom_type == "LineString" else [piece.coords[0]] * 2
            pieces.append(coordinates if t1 >= t0 else coordinates[::-1])
        merged = []
        for coordinates in pieces:
            merged.extend(coordinates if not merged or merged[-1] != coordinates[0] else coordinates[1:])
        return LineString(merged) if len(merged) >= 2 else None


# ---- the four solvers --------------------------------------------------------------------------------------------------
#
# Each takes located inputs — dictionaries with ``key``, ``oid``, ``name``, ``point`` (shapely), ``located``
# (Graph.locate's answer or None) and the input's own fields — and answers output rows as dictionaries, the shape under
# ``SHAPE``.

def _status(item):
    return 0 if item["located"] is not None else 1


def _nodes(items):
    return [(item["key"], item["located"][0], item["located"][1]) for item in items if item["located"] is not None]


def _line(solver, arcs, points, line_shape):
    shape = str(line_shape or "ALONG_NETWORK").upper()
    if shape in ("NO_LINES", "NOLINES", "NONE"):
        return None
    if shape in ("STRAIGHT_LINES", "STRAIGHTLINE", "STRAIGHT"):
        return LineString([(p.x, p.y) for p in points])
    return solver.shape(arcs)


def solve_route(graph, stops, accumulate, line_shape):
    """Routes through stops in sequence: a route per ``RouteName`` (case-insensitive, as documented), or one through
    all when no stop has one; stops in ``Sequence`` order, then object id. INFERRED: stops without a route name, when
    others have one, take no part; a route with an unlocated or unreached stop is not written, and its stops' Status
    says why (1 not located, 5 not reached)."""
    solver = graph.solver(_nodes(stops))
    named = any(s.get("RouteName") for s in stops)
    groups: dict = {}
    for stop in stops:
        if named and not stop.get("RouteName"):
            continue
        groups.setdefault(str(stop.get("RouteName") or "").lower(), []).append(stop)
    routes, results = [], {}
    impedance = graph.impedance
    for members in groups.values():
        members.sort(key=lambda s: (s.get("Sequence") is None, s.get("Sequence") or 0, s["oid"]))
        costs = [a for a, (usage, _) in graph.attributes.items() if usage == "Cost"]
        for n, stop in enumerate(members, start=1):
            results[stop["key"]] = {"Sequence": n, "Status": _status(stop), f"Cumul_{impedance}": None, "_cumul": {}}
        if any(s["located"] is None for s in members) or len(members) < 2:
            continue
        all_arcs, cumulative, reached = [], 0.0, True
        results[members[0]["key"]][f"Cumul_{impedance}"] = 0.0
        results[members[0]["key"]]["_cumul"] = {a: 0.0 for a in costs}
        for a, b in zip(members, members[1:]):
            _, predecessors = solver.run(a["key"])
            arcs = solver.path(predecessors, a["key"], b["key"])
            if arcs is None:
                results[b["key"]]["Status"] = 5
                reached = False
                break
            cumulative += solver.total(arcs, impedance)
            results[b["key"]][f"Cumul_{impedance}"] = cumulative
            all_arcs.extend(arcs)
            results[b["key"]]["_cumul"] = {a: solver.total(all_arcs, a) for a in costs}
        if not reached:
            continue
        row = {"Name": members[0].get("RouteName") or f"{members[0]['name']} - {members[-1]['name']}",
               "FirstStopID": members[0]["oid"], "LastStopID": members[-1]["oid"], "StopCount": len(members),
               f"Total_{impedance}": cumulative}
        for attribute in accumulate:
            row[f"Total_{attribute}"] = solver.total(all_arcs, attribute)
        row["SHAPE"] = _line(solver, all_arcs, [m["point"] for m in members], line_shape)
        row["_totals"] = {a: solver.total(all_arcs, a) for a in costs}
        routes.append(row)
    return routes, results


def _ranked(solver, distances, items, cutoff):
    found = []
    for item in items:
        if item["located"] is None:
            continue
        cost = distances[solver.location_node[item["key"]]]
        if numpy.isfinite(cost) and (cutoff is None or cost <= cutoff):
            found.append((float(cost), item["oid"], item))
    return sorted(found, key=lambda f: (f[0], f[1]))


def solve_closest_facility(graph, facilities, incidents, direction, cutoff, count, accumulate, line_shape):
    """For each incident the ``count`` nearest facilities (its ``TargetFacilityCount`` when set) within the cutoff,
    travelling toward the facilities or away from them, ranked by cost; the route named facility first when travel is
    away from it, as documented. INFERRED: ties go to the lower facility object id; names are joined with " - "."""
    away = str(direction or "TO_FACILITIES").upper() == "FROM_FACILITIES"
    solver = graph.solver(_nodes(facilities) + _nodes(incidents), reverse=away)
    impedance = graph.impedance
    routes = []
    for incident in incidents:
        if incident["located"] is None:
            continue
        distances, predecessors = solver.run(incident["key"])
        wanted = int(incident.get("TargetFacilityCount") or count or 1)
        for rank, (cost, _, facility) in enumerate(_ranked(solver, distances, facilities, cutoff)[:wanted], start=1):
            arcs = solver.path(predecessors, incident["key"], facility["key"])
            ends = [facility["point"], incident["point"]] if away else [incident["point"], facility["point"]]
            row = {"Name": f"{facility['name']} - {incident['name']}" if away else f"{incident['name']} - {facility['name']}",
                   "FacilityID": facility["oid"], "FacilityRank": rank, "IncidentID": incident["oid"],
                   f"Total_{impedance}": cost}
            for attribute in accumulate:
                row[f"Total_{attribute}"] = solver.total(arcs, attribute)
            row["SHAPE"] = _line(solver, arcs, ends, line_shape)
            row["_totals"] = {a: solver.total(arcs, a) for a, (usage, _) in graph.attributes.items() if usage == "Cost"}
            routes.append(row)
    return routes


def solve_od_cost_matrix(graph, origins, destinations, cutoff, count, accumulate, line_shape):
    """A line from each origin to each destination reached within the cutoff — the ``count`` nearest (its
    ``TargetDestinationCount`` when set), all when none is given — ranked by cost. INFERRED: ties go to the lower
    destination object id; a line's name joins its origin's and destination's with " - "."""
    solver = graph.solver(_nodes(origins) + _nodes(destinations))
    impedance = graph.impedance
    lines = []
    for origin in origins:
        if origin["located"] is None:
            continue
        distances, predecessors = solver.run(origin["key"])
        found = _ranked(solver, distances, destinations, cutoff)
        wanted = origin.get("TargetDestinationCount") or count
        for rank, (cost, _, destination) in enumerate(found[:int(wanted)] if wanted else found, start=1):
            row = {"Name": f"{origin['name']} - {destination['name']}", "OriginID": origin["oid"],
                   "DestinationID": destination["oid"], "DestinationRank": rank, f"Total_{impedance}": cost}
            arcs = solver.path(predecessors, origin["key"], destination["key"])
            for attribute in accumulate:
                row[f"Total_{attribute}"] = solver.total(arcs, attribute)
            row["_totals"] = {a: solver.total(arcs, a) for a, (usage, _) in graph.attributes.items() if usage == "Cost"}
            row["SHAPE"] = None if str(line_shape or "STRAIGHT_LINES").upper() == "NO_LINES" else \
                LineString([(origin["point"].x, origin["point"].y), (destination["point"].x, destination["point"].y)])
            lines.append(row)
    return lines


def _cheaper(da_, ca, a_forward, db_, cb, b_forward):
    """A piece of edge reached from both its ends: each point takes the cheaper end. ``da_`` and ``ca`` are the cost at
    the piece's start and of crossing it from there (cost da + ca·x at fraction x), ``db_`` and ``cb`` the same from its
    end (db + cb·(1 − x)); ``*_forward`` is whether travel then runs with the piece. Answers (start, end, cost at start,
    cost at end, travel runs with the piece), start < end."""
    a_ok = ca is not None and numpy.isfinite(da_)
    b_ok = cb is not None and numpy.isfinite(db_)
    if a_ok and b_ok:
        meet = (db_ + cb - da_) / (ca + cb) if (ca + cb) > 0 else 0.5
        meet = min(1.0, max(0.0, meet))
        parts = []
        if meet > 0:
            parts.append((0.0, meet, da_, da_ + ca * meet, a_forward))
        if meet < 1:
            parts.append((meet, 1.0, db_ + cb * (1 - meet), db_, b_forward))
        return parts
    if a_ok:
        return [(0.0, 1.0, da_, da_ + ca, a_forward)]
    if b_ok:
        return [(0.0, 1.0, db_ + cb, db_, b_forward)]
    return []


def solve_service_area(graph, facilities, breaks, direction, rings, trim):
    """For each facility, the network it reaches within each break, as lines and polygons. Each point of each piece of
    edge (between junctions and facilities) is reached through whichever end is cheaper; the piece is cut where the two
    meet and at each break. Lines run in the direction of travel with ``FromCumul_`` and ``ToCumul_`` the costs at
    their start and end. EQUIVALENT: a polygon is the reached lines, and the facility's point, buffered by the trim
    distance — with ``RINGS`` less the polygon of the break before — Pro's polygon generation not being published.
    INFERRED: a polygon's name is "<facility> : <from break> - <to break>", as the page's example writes it."""
    away = str(direction or "FROM_FACILITIES").upper() == "FROM_FACILITIES"
    solver = graph.solver(_nodes(facilities), reverse=not away)
    impedance = graph.impedance
    breaks = sorted(float(b) for b in breaks)
    on_edge: dict = {}
    for facility in facilities:
        if facility["located"] is not None:
            on_edge.setdefault(facility["located"][0], []).append(
                (facility["located"][1], solver.location_node[facility["key"]]))
    lines, polygons = [], []
    for facility in facilities:
        if facility["located"] is None:
            continue
        distances, _ = solver.run(facility["key"])
        parts = []
        for i, edge in enumerate(graph.edges):
            stops = [(0.0, solver.node[edge["u"]])] + sorted(on_edge.get(i, [])) + [(1.0, solver.node[edge["v"]])]
            for (ta, na), (tb, nb) in zip(stops, stops[1:]):
                if tb <= ta:
                    continue
                forward = graph.cost(impedance, i, True, ta, tb) if graph.passable(i, True) else None
                backward = graph.cost(impedance, i, False, tb, ta) if graph.passable(i, False) else None
                if away:
                    pieces = _cheaper(distances[na], forward, True, distances[nb], backward, False)
                else:
                    pieces = _cheaper(distances[na], backward, False, distances[nb], forward, True)
                for s, e, c0, c1, runs in pieces:
                    parts.append((i, ta + (tb - ta) * s, ta + (tb - ta) * e, c0, c1, runs))
        mine = []
        for k, cutoff in enumerate(breaks):
            low = breaks[k - 1] if k else 0.0
            for i, p0, p1, c0, c1, runs in parts:
                if c1 == c0:
                    s0, s1 = (0.0, 1.0) if (low < c0 <= cutoff or (k == 0 and c0 == 0)) else (1.0, 0.0)
                else:
                    sa, sb = (low - c0) / (c1 - c0), (cutoff - c0) / (c1 - c0)
                    s0, s1 = max(0.0, min(sa, sb)), min(1.0, max(sa, sb))
                if s1 <= s0:
                    continue
                q0, q1 = p0 + (p1 - p0) * s0, p0 + (p1 - p0) * s1
                piece = substring(graph.edges[i]["shape"], q0, q1, normalized=True)
                if piece.geom_type != "LineString" or piece.length == 0:
                    continue
                k0, k1 = c0 + (c1 - c0) * s0, c0 + (c1 - c0) * s1
                edge = graph.edges[i]
                position = lambda q: edge["p0"] + (edge["p1"] - edge["p0"]) * q  # noqa: E731
                if not runs:
                    piece, q0, q1, k0, k1 = LineString(list(piece.coords)[::-1]), q1, q0, k1, k0
                row = {"FacilityID": facility["oid"], f"FromCumul_{impedance}": k0, f"ToCumul_{impedance}": k1,
                       "SourceID": graph.sources.index(edge["source"]) + 1, "SourceOID": edge["oid"],
                       "FromPosition": position(q0), "ToPosition": position(q1), "SHAPE": piece}
                lines.append(row)
                mine.append((k, piece))
        previous = None
        for k, cutoff in enumerate(breaks):
            disk = shapely.union_all([*(s.buffer(trim) for b, s in mine if b <= k), facility["point"].buffer(trim)])
            low = breaks[k - 1] if (k and rings) else 0.0
            polygons.append({"Name": f"{facility['name']} : {float(low)} - {float(cutoff)}", "FacilityID": facility["oid"],
                             "FromBreak": float(low), "ToBreak": float(cutoff),
                             "SHAPE": disk.difference(previous) if rings and previous is not None else disk})
            previous = disk
    return lines, polygons


# ---- Describe ----------------------------------------------------------------------------------------------------------

class _Object:
    def __init__(self, **values):
        self.__dict__.update(values)

    def __repr__(self):
        return f"<{type(self).__name__} {getattr(self, 'name', '')}>"


class Described:
    """What Describe answers for a network dataset: the documented properties and the Dataset group's names. INFERRED,
    the pages of the objects the properties answer not being read: an attribute has ``name``, ``usageType``
    (``Cost``/``Restriction``), ``units``, ``dataType`` (``Double``/``Boolean``) and ``useByDefault``; a source has
    ``name``, ``sourceID`` (from 1, in the order given), ``sourceType`` (``EdgeFeature``, ``SystemJunction``) and
    ``elementType`` (``Edge``, ``Junction``); the system junction source is ``<network>_Junctions`` and numbers after the
    edge sources. ``elevationModel`` is ``None`` unless elevation fields are named — grapy names none."""

    def __init__(self, path, file, dataset, name, sources, attributes, modes, srid):
        from ._reference import SpatialReference
        self.catalogPath = str(path)
        self.name = self.baseName = name
        self.path = f"{file}/{dataset}"
        self.dataType = self.datasetType = "NetworkDataset"
        self.attributes = [_Object(name=a, usageType=u, units=units or "Unknown",
                                   dataType="Boolean" if u == "Restriction" else "Double", useByDefault=bool(d))
                           for a, u, units, d in attributes]
        self.edgeSources = [_Object(name=s.rsplit("/", 1)[1], sourceID=i, sourceType="EdgeFeature", elementType="Edge",
                                    connectivityPolicy=policy)
                            for i, (s, policy) in enumerate(sources, start=1)]
        self.junctionSources = []
        self.turnSources = []
        self.systemJunctionSource = _Object(name=f"{name}_Junctions", sourceID=len(sources) + 1,
                                            sourceType="SystemJunction", elementType="Junction")
        self.sources = [*self.edgeSources, self.systemJunctionSource]
        self.defaultTravelModeName = modes[0] if modes else ""
        self.elevationModel = "None"
        self.isBuildable = True
        self.networkType = "Geodatabase"
        self.optimizations = []
        self.supportsDirections = False
        self.supportsHistoricalTrafficData = False
        self.supportsLiveTrafficData = False
        self.supportsTurns = False
        self.timeZoneAttributeName = ""
        self.timeZoneTableName = ""
        self.trafficSupportType = "NONE"
        self.spatialReference = SpatialReference(srid) if srid > 0 else None


def describe(path):
    """A Described for a network dataset path or layer name, or None when it names none."""
    try:
        file, dataset, name = locate(path)
    except (ExecuteError, ValueError):
        return None
    if not os.path.exists(file):
        return None
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_network'").fetchone():
            return None
        if not connection.execute("select 1 from grapy_network where name = ? and dataset = ?", (name, dataset)).fetchone():
            return None
        sources = connection.execute("select class_name, connectivity from grapy_network_source where network = ? "
                                     "order by rowid", (name,)).fetchall()
        attributes = connection.execute("select name, usage, units, use_by_default from grapy_network_attribute where "
                                        "network = ? order by rowid", (name,)).fetchall()
        modes = [r[0] for r in connection.execute("select name from grapy_network_travel_mode where network = ? "
                                                  "order by rowid", (name,))]
    finally:
        connection.close()
    srid = _source_srid(file, sources[0][0]) if sources else 0
    return Described(path, file, dataset, name, sources, attributes, modes, srid)
