"""Turns in grapy's network datasets, and the solver that honours them — ADR-239 (the hook ADR-226 §5.3 left).

A **turn feature class** is a line feature class with the documented turn fields: ``Edge1End`` (``Y`` when the turn
passes through the end of its first edge, ``N`` through its beginning) and, for each edge ``#`` from 1 to its maximum
edges, ``Edge#FCID`` (the edge's source), ``Edge#FID`` (the source feature's object id) and ``Edge#Pos`` (a position
along that feature, telling apart the edges one feature is split into). INFERRED, a GeoPackage having no feature class
ids: ``Edge#FCID`` holds the network's source id of the edge's source (1, 2 … in the order the sources were given), as
Describe's ``sourceID`` numbers them.

A network's turn sources are grapy tables in its GeoPackage, beside ADR-226's:

- ``grapy_network_turn_source``: each turn feature class of a network;
- ``grapy_network_turn``: what BuildNetwork writes — each turn feature as the ordered network edges it traverses, each
  with its direction of travel;
- ``grapy_network_property``: a network's settings that are not attributes, its driving side.

Turns are directed, so a turn source's evaluators are read along the turn only, as documented: ``FIELD`` or
``CONSTANT``. A cost attribute's turn values are added to the travel through the turn; a restriction prohibits it. The
**global turns** — every transition between two edges with no two-edge turn feature on it — take a cost from a
``TURN_CATEGORY`` evaluator, as the turn category evaluator page documents: the turn is *straight* when it rotates less
than the minimum azimuth, *left* (counterclockwise) or *right* (clockwise) within the azimuth range, *reverse* beyond
the maximum or for a U-turn; defaults 30 and 150 degrees; the delays in seconds, for a time attribute only.

INFERRED where the pages are silent: the rotation is measured between the last segment of the edge arrived on and the
first segment of the edge left by; a multi-edge turn's own values are charged when its last edge is entered, and the
global turns at its transitions still apply; a stop part-way along an edge breaks no turn that runs through that edge,
but a U-turn there starts the edge sequence again.

A travel mode's U-turn policy is honoured at junctions: ``ALLOW_UTURNS``, ``NO_UTURNS``, ``ALLOW_DEAD_ENDS_ONLY`` (a
junction with one edge) and ``ALLOW_DEAD_ENDS_AND_INTERSECTIONS_ONLY`` (any junction but one where exactly two edges
meet), with their REST names. A U-turn at a stop is the stop's curb approach, not a junction's.

Solving over turns is Dijkstra's algorithm over the network's *edge-expanded* graph, each state an edge travelled and
the turn features it is part-way through — the label-setting form of Dijkstra (1959) on the line graph that
Caldwell (1961) introduced for turn penalties, with the prefix states of Gutiérrez and Medaglia (2008) for turns of more
than two edges. It answers what ``_network._Solver`` answers, so every solver uses it unchanged.
"""
from __future__ import annotations

import heapq
import json
import math

import numpy

from . import _gpkg
from ._runtime import ExecuteError

TABLES = ("grapy_network_turn_source", "grapy_network_turn", "grapy_network_property")
GLOBAL = "GLOBAL_TURNS"
MAXIMUM_EDGES = 50
CATEGORIES = ("straight", "left", "right", "reverse")
SOLVER = ("Dijkstra's shortest path over the edge-expanded network, honouring turns",
          "Dijkstra 1959; Caldwell 1961; Gutiérrez and Medaglia 2008")
_POLICIES = {"ALLOW_UTURNS": "ALLOW", "ALLOW": "ALLOW", "ALLOW_UTURNS_EVERYWHERE": "ALLOW", "ESRINFSBALLOWBACKTRACK": "ALLOW",
             "NO_UTURNS": "NO", "ESRINFSBNOBACKTRACK": "NO",
             "ALLOW_DEAD_ENDS_ONLY": "DEAD_ENDS", "ESRINFSBATDEADENDSONLY": "DEAD_ENDS",
             "ALLOW_DEAD_ENDS_AND_INTERSECTIONS_ONLY": "DEAD_ENDS_AND_INTERSECTIONS",
             "ESRINFSBATDEADENDSANDINTERSECTIONS": "DEAD_ENDS_AND_INTERSECTIONS"}


def policy(value):
    """grapy's word for a U-turn policy, from the travel mode's (Python or REST) name."""
    word = _POLICIES.get(str(value or "ALLOW_UTURNS").upper().replace(" ", ""))
    if word is None:
        raise ExecuteError(f"ERROR 030000: '{value}' is not a U-turn policy.")
    return word


def ensure(connection):
    from ._network import DEFINITION, EXTENSION
    from ._topology import register
    connection.execute("""create table if not exists grapy_network_turn_source (network text not null, class_name text
        not null, primary key (network, class_name))""")
    connection.execute("""create table if not exists grapy_network_turn (network text not null, turn_id integer not null,
        class_name text not null, source_oid integer not null, edges text not null)""")
    connection.execute("""create table if not exists grapy_network_property (network text not null, name text not null,
        value text, primary key (network, name))""")
    register(connection, TABLES, EXTENSION, DEFINITION)


# ---- turn feature classes -------------------------------------------------------------------------------------------

def turn_columns(maximum_edges):
    columns = [_gpkg.Column("Edge1End", "String")]
    for k in range(1, int(maximum_edges) + 1):
        columns += [_gpkg.Column(f"Edge{k}FCID", "Integer"), _gpkg.Column(f"Edge{k}FID", "Integer"),
                    _gpkg.Column(f"Edge{k}Pos", "Double")]
    return columns


def fields_of(path):
    connection, table = _gpkg.table_of(path)
    try:
        return [c.name for c in _gpkg.describe(connection, table)[0]]
    finally:
        connection.close()


def is_turn_class(path):
    try:
        return "edge1end" in {f.lower() for f in fields_of(path)}
    except Exception:  # noqa: BLE001 — a path that names no table is no turn class
        return False


def maximum_edges(path):
    names = {f.lower() for f in fields_of(path)}
    k = 0
    while f"edge{k + 1}fid" in names:
        k += 1
    return k


def check_maximum(value):
    count = int(value)
    if count < 2 or count > MAXIMUM_EDGES:
        raise ExecuteError(f"ERROR 030000: a turn's maximum edges is between 2 and {MAXIMUM_EDGES}.")
    return count


def increase_maximum_edges(path, value):
    """Three fields more for each edge added, as documented; the maximum only grows."""
    current, wanted = maximum_edges(path), check_maximum(value)
    if wanted <= current:
        raise ExecuteError(f"ERROR 030000: the new maximum edges must be at least one more than the current {current}.")
    for k in range(current + 1, wanted + 1):
        for column in turn_columns(k)[-3:]:
            _gpkg.add_column(path, column)


def _turn_rows(path, extra=()):
    """Each turn feature: (oid, Edge1End, [(FCID, FID, Pos) …] up to its first null FID, shape, extra values)."""
    from . import da
    count = maximum_edges(path)
    names = ["OID@", "Edge1End", *[f"Edge{k}{p}" for k in range(1, count + 1) for p in ("FCID", "FID", "Pos")],
             *extra, "SHAPE@"]
    out = []
    with da.SearchCursor(path, names) as rows:
        for row in rows:
            edges = []
            for k in range(count):
                fcid, fid, pos = row[2 + 3 * k: 5 + 3 * k]
                if fid is None:
                    break
                edges.append((fcid, fid, pos))
            shape = row[-1]._shape if row[-1] is not None else None
            out.append((row[0], row[1], edges, shape, row[2 + 3 * count:-1]))
    return out


# ---- the network's turn sources and properties -------------------------------------------------------------------

def turn_sources(connection, name):
    ensure(connection)
    return [r[0] for r in connection.execute("select class_name from grapy_network_turn_source where network = ? "
                                             "order by rowid", (name,))]


def add_turn_source(network, path):
    from . import _network
    connection, file, dataset, name = _network.open_network(network)
    try:
        ensure(connection)
        source_file, table = _gpkg.split(str(path))
        if source_file != file:
            raise ExecuteError("ERROR 030000: a turn source is a feature class of the network's own geodatabase.")
        connection.execute("insert or ignore into grapy_network_turn_source values (?, ?)", (name, table))
        _network._dirty(connection, name)
        connection.commit()
    finally:
        connection.close()


def get_property(network, key, default=None):
    from . import _network
    connection, _, _, name = _network.open_network(network)
    try:
        ensure(connection)
        row = connection.execute("select value from grapy_network_property where network = ? and name = ?",
                                 (name, key)).fetchone()
        return row[0] if row else default
    finally:
        connection.close()


def set_property(network, key, value):
    from . import _network
    connection, _, _, name = _network.open_network(network)
    try:
        ensure(connection)
        connection.execute("insert or replace into grapy_network_property values (?, ?, ?)", (name, key, value))
        connection.commit()
    finally:
        connection.close()


def driving_side(network):
    """``RightSide`` unless set: INFERRED, the side most of the world drives on, Pro's network page giving no default."""
    return get_property(network, "driving_side", "RightSide")


# ---- building ---------------------------------------------------------------------------------------------------------

def _resolve(edges_of, junction_edges, sources, fcid, fid, pos, previous_end):
    """The network edge a turn's Edge#FCID/FID/Pos names: the piece of the feature holding the position, or — with no
    position — the piece that touches the junction the turn is at."""
    if fcid is None or not (1 <= int(fcid) <= len(sources)):
        return None
    pieces = edges_of.get((sources[int(fcid) - 1], int(fid)), [])
    if not pieces:
        return None
    if pos is not None:
        inside = [e for e in pieces if e["p0"] - 1e-9 <= float(pos) <= e["p1"] + 1e-9]
        if previous_end is not None:
            touching = [e for e in inside if previous_end in (e["u"], e["v"])]
            inside = touching or inside
        return inside[0] if inside else None
    if previous_end is not None:
        touching = [e for e in pieces if previous_end in (e["u"], e["v"])]
        return touching[0] if touching else None
    return pieces[0] if len(pieces) == 1 else None


def build(network):
    """Each turn feature as the network edges it traverses, in order, with the direction of travel on each; a turn whose
    edges do not chain is left out, with a warning, as a build error is."""
    from . import _network
    from ._runtime import AddWarning
    connection, file, dataset, name = _network.open_network(network)
    try:
        ensure(connection)
        classes = turn_sources(connection, name)
        sources = [r[0] for r in connection.execute("select class_name from grapy_network_source where network = ? "
                                                    "order by rowid", (name,))]
        edges = [dict(edge_id=r[0], source=r[1], oid=r[2], u=r[3], v=r[4], p0=r[5], p1=r[6]) for r in connection.execute(
            "select edge_id, class_name, source_oid, from_junction, to_junction, from_position, to_position from "
            "grapy_network_edge where network = ? order by edge_id", (name,))]
    finally:
        connection.close()
    edges_of: dict = {}
    for edge in edges:
        edges_of.setdefault((edge["source"], edge["oid"]), []).append(edge)
    written, skipped = [], 0
    turn_id = 0
    for table in classes:
        for oid, end, refs, _, _ in _turn_rows(f"{file}/{table}"):
            chain, at = [], None
            ok = len(refs) >= 2
            for k, (fcid, fid, pos) in enumerate(refs):
                edge = _resolve(edges_of, None, sources, fcid, fid, pos, at)
                if edge is None or (k and at not in (edge["u"], edge["v"])):
                    ok = False
                    break
                if k == 0:
                    forward = str(end or "").upper().startswith("Y")
                elif edge["edge_id"] == chain[-1][0] and edge["u"] != edge["v"]:
                    forward = not chain[-1][1]             # a U-turn: back along the same edge
                else:
                    forward = edge["u"] == at
                chain.append((edge["edge_id"], forward))
                at = edge["v"] if forward else edge["u"]
            if not ok:
                skipped += 1
                continue
            turn_id += 1
            written.append((name, turn_id, table, oid, json.dumps(chain)))
    connection = _gpkg.connect(file)
    try:
        ensure(connection)
        connection.execute("delete from grapy_network_turn where network = ?", (name,))
        connection.executemany("insert into grapy_network_turn values (?, ?, ?, ?, ?)", written)
        connection.commit()
    finally:
        connection.close()
    if skipped:
        AddWarning(f"BuildNetwork: {skipped} turn feature(s) do not name a chain of connected network edges and are not "
                   "part of the network.")
    return len(written)


def turn_count(network):
    from . import _network
    connection, _, _, name = _network.open_network(network)
    try:
        ensure(connection)
        return int(connection.execute("select count(*) from grapy_network_turn where network = ?", (name,)).fetchone()[0])
    finally:
        connection.close()


# ---- editing turn features from the network ---------------------------------------------------------------------

def _network_of_turn_class(path):
    """The network a turn feature class is a turn source of."""
    from . import _network
    file, table = _gpkg.split(str(path))
    connection = _gpkg.connect(file)
    try:
        ensure(connection)
        row = connection.execute("select t.network, n.dataset from grapy_network_turn_source t join grapy_network n on "
                                 "n.name = t.network where t.class_name = ?", (table,)).fetchone()
    finally:
        connection.close()
    if row is None:
        raise ExecuteError(f"ERROR 030000: '{path}' is not a turn source of a network dataset.")
    _network.open_network(f"{file}/{row[1]}/{row[0]}")[0].close()
    return f"{file}/{row[1]}/{row[0]}"


def update_by_geometry(path):
    """Edge#FCID, Edge#FID and Edge#Pos set from each turn's shape, by the edges it runs along: the network edges the
    line overlaps, in the order it overlaps them, and Edge1End from the junction it leaves the first edge by. A turn
    whose shape follows no chain of edges keeps its values and is counted in a warning. The network must be built."""
    from . import _network, da
    from ._runtime import AddWarning
    network = _network_of_turn_class(path)
    graph = _network.Graph(network)
    count = maximum_edges(path)
    from shapely.strtree import STRtree
    shapes = [e["shape"] for e in graph.edges]
    tree = STRtree(shapes)
    size = max((s.length for s in shapes), default=1.0)
    tolerance = max(size * 1e-9, 1e-9)
    names = ["Edge1End", *[f"Edge{k}{p}" for k in range(1, count + 1) for p in ("FCID", "FID", "Pos")], "SHAPE@"]
    failed = 0
    with da.UpdateCursor(path, names) as rows:
        for row in rows:
            line = row[-1]._shape if row[-1] is not None else None
            found = _edges_along(graph, tree, line, tolerance) if line is not None else None
            if not found or len(found) < 2 or len(found) > count:
                failed += 1
                continue
            values = [found[0][1]]
            for k in range(count):
                if k < len(found):
                    i = found[k][0]
                    edge = graph.edges[i]
                    values += [graph.sources.index(edge["source"]) + 1, edge["oid"], (edge["p0"] + edge["p1"]) / 2]
                else:
                    values += [None, None, None]
            rows.updateRow([*values, row[-1]])
    if failed:
        AddWarning(f"UpdateByGeometry: {failed} turn feature(s) do not follow a chain of network edges and were not "
                   "updated.")


def _edges_along(graph, tree, line, tolerance):
    """[(edge index, 'Y'/'N' for the first)] of the edges a turn's line runs along, in order."""
    from shapely.geometry import LineString
    coordinates = list(line.coords)
    pieces = [LineString(coordinates[k:k + 2]) for k in range(len(coordinates) - 1)]
    sequence = []
    for piece in pieces:
        if piece.length == 0:
            continue
        middle = piece.interpolate(0.5, normalized=True)
        hits = [int(i) for i in tree.query(middle.buffer(tolerance * 10), predicate="intersects")]
        hits = [i for i in hits if graph.edges[i]["shape"].distance(middle) <= tolerance * 10]
        if not hits:
            return None
        i = hits[0]
        if not sequence or sequence[-1] != i:
            sequence.append(i)
    if len(sequence) == 1:
        sequence.append(sequence[0])                        # a U-turn runs out along one edge and back
    first, second = graph.edges[sequence[0]], graph.edges[sequence[1]]
    shared = {first["u"], first["v"]} & {second["u"], second["v"]}
    if not shared:
        return None
    # The junction the turn passes from its first edge to its second: the shared one the line touches.
    junction = min(shared, key=lambda j: line.distance(shapely_point(graph.junctions[j])))
    end = "Y" if junction == first["v"] else "N"
    return [(sequence[0], end)] + [(i, None) for i in sequence[1:]]


def shapely_point(xy):
    from shapely.geometry import Point
    return Point(xy[0], xy[1])


def populate_alternate_ids(network, field):
    """``AltID1`` … ``AltID<maximum edges>`` on every turn source of the network, each the edge feature's value of the
    alternate ID field, as documented. INFERRED: the AltID fields take the edge field's type."""
    from . import _network, da
    connection, file, _, name = _network.open_network(network)
    try:
        classes = turn_sources(connection, name)
        sources = [r[0] for r in connection.execute("select class_name from grapy_network_source where network = ? "
                                                    "order by rowid", (name,))]
    finally:
        connection.close()
    values, kind = _alternate_values(file, sources, field)
    for table in classes:
        path = f"{file}/{table}"
        count = maximum_edges(path)
        have = {f.lower() for f in fields_of(path)}
        for k in range(1, count + 1):
            if f"altid{k}" not in have:
                _gpkg.add_column(path, _gpkg.Column(f"AltID{k}", kind))
        names = [f"Edge{k}{p}" for k in range(1, count + 1) for p in ("FCID", "FID")] + \
                [f"AltID{k}" for k in range(1, count + 1)]
        with da.UpdateCursor(path, names) as rows:
            for row in rows:
                alternate = []
                for k in range(count):
                    fcid, fid = row[2 * k], row[2 * k + 1]
                    alternate.append(values.get((int(fcid), int(fid))) if fcid is not None and fid is not None else None)
                rows.updateRow([*row[:2 * count], *alternate])


def _alternate_values(file, sources, field):
    from . import da
    values, kind = {}, "Integer"
    for number, table in enumerate(sources, start=1):
        connection = _gpkg.connect(file)
        try:
            columns = {c.name.lower(): c for c in _gpkg.describe(connection, table)[0]}
        finally:
            connection.close()
        if str(field).lower() not in columns:
            raise ExecuteError(f"ERROR 000728: the edge source '{table}' has no field '{field}'.")
        kind = columns[str(field).lower()].type
        with da.SearchCursor(f"{file}/{table}", ["OID@", columns[str(field).lower()].name]) as rows:
            for oid, value in rows:
                values[(number, oid)] = value
    return values, kind


def update_by_alternate_ids(network, field):
    """Each turn's Edge#FID (and Edge#FCID) found again from its AltID# values, as documented. A turn whose alternate
    id matches no edge feature keeps its values and is counted in a warning."""
    from . import _network, da
    from ._runtime import AddWarning
    connection, file, _, name = _network.open_network(network)
    try:
        classes = turn_sources(connection, name)
        sources = [r[0] for r in connection.execute("select class_name from grapy_network_source where network = ? "
                                                    "order by rowid", (name,))]
    finally:
        connection.close()
    values, _ = _alternate_values(file, sources, field)
    by_value: dict = {}
    for (number, oid), value in values.items():
        if value is not None:
            by_value.setdefault(value, []).append((number, oid))
    missed = 0
    for table in classes:
        path = f"{file}/{table}"
        count = maximum_edges(path)
        have = {f.lower() for f in fields_of(path)}
        if "altid1" not in have:
            raise ExecuteError(f"ERROR 030000: '{table}' has no AltID fields: run PopulateAlternateIDFields first.")
        names = [f"Edge{k}{p}" for k in range(1, count + 1) for p in ("FCID", "FID")] + \
                [f"AltID{k}" for k in range(1, count + 1)]
        with da.UpdateCursor(path, names) as rows:
            for row in rows:
                pairs = list(row[:2 * count])
                for k in range(count):
                    alternate = row[2 * count + k]
                    if alternate is None:
                        continue
                    matches = by_value.get(alternate, [])
                    if pairs[2 * k] is not None:
                        matches = [m for m in matches if m[0] == int(pairs[2 * k])] or matches
                    if not matches:
                        missed += 1
                        continue
                    pairs[2 * k], pairs[2 * k + 1] = matches[0]
                rows.updateRow([*pairs, *row[2 * count:]])
    if missed:
        AddWarning(f"UpdateByAlternateIDFields: {missed} alternate id value(s) match no edge feature.")


# ---- the turn model a travel mode sees -----------------------------------------------------------------------------

def _heading(coordinates, start):
    """The direction (radians) of a line's first segment (``start``) or last."""
    if start:
        (x0, y0), (x1, y1) = coordinates[0][:2], coordinates[1][:2]
    else:
        (x0, y0), (x1, y1) = coordinates[-2][:2], coordinates[-1][:2]
    return math.atan2(y1 - y0, x1 - x0)


def rotation(graph, arriving, leaving):
    """The signed rotation in degrees from the edge arrived on to the edge left by: counterclockwise (left) positive,
    in (-180, 180]; a U-turn is 180."""
    (i, fi), (j, fj) = arriving, leaving
    if i == j and fi != fj:
        return 180.0
    a = list(graph.edges[i]["shape"].coords)
    b = list(graph.edges[j]["shape"].coords)
    h0 = _heading(a, False) if fi else _heading(a[::-1], False)
    h1 = _heading(b, True) if fj else _heading(b[::-1], True)
    turn = math.degrees(h1 - h0)
    while turn <= -180:
        turn += 360
    while turn > 180:
        turn -= 360
    return turn


def category(turn, minimum=30.0, maximum=150.0):
    """The turn category of a rotation, as the turn category evaluator defines it."""
    size = abs(turn)
    if size < minimum:
        return "straight"
    if size > maximum:
        return "reverse"
    return "left" if turn > 0 else "right"


class Model:
    """The turns of a built network under one travel mode."""

    def __init__(self, graph):
        from . import _network, da
        connection, file, _, name = _network.open_network(f"{graph.file}/{graph.dataset}/{graph.name}")
        try:
            ensure(connection)
            rows = connection.execute("select turn_id, class_name, source_oid, edges from grapy_network_turn where "
                                      "network = ? order by turn_id", (name,)).fetchall()
            evaluators = connection.execute("select attribute, class_name, direction, kind, value from "
                                            "grapy_network_evaluator where network = ?", (name,)).fetchall()
            self.classes = turn_sources(connection, name)
        finally:
            connection.close()
        index = {e["edge_id"]: k for k, e in enumerate(graph.edges)}
        self.graph = graph
        self.turns = []
        for turn_id, table, oid, edges in rows:
            chain = [(index[e], bool(f)) for e, f in json.loads(edges) if e in index]
            self.turns.append({"turn_id": turn_id, "class": table, "oid": oid, "edges": chain, "costs": {},
                               "restrictions": set(), "restricted_by": set()})
        # Turn source values.
        needed: dict = {}
        for attribute, table, _, kind, value in evaluators:
            if table in self.classes and kind == "FIELD":
                needed.setdefault(table, set()).add(value)
        values: dict = {}
        for table, fields in needed.items():
            with da.SearchCursor(f"{file}/{table}", ["OID@", *sorted(fields)]) as cursor:
                for row in cursor:
                    values[(table, row[0])] = dict(zip(sorted(fields), row[1:]))
        self.globals = {}
        restrictions = set(graph.mode.get("restrictions") or [])
        for attribute, table, direction, kind, value in evaluators:
            usage, units = graph.attributes.get(attribute, (None, None))
            if table == GLOBAL and kind == "TURN_CATEGORY":
                settings = json.loads(value)
                factor = _seconds_to(units)
                self.globals[attribute] = {"minimum": float(settings.get("minimum", 30)),
                                           "maximum": float(settings.get("maximum", 150)),
                                           **{c: float(settings.get(c, 0) or 0) * factor for c in CATEGORIES}}
                continue
            if table not in self.classes:
                continue
            for turn in self.turns:
                if turn["class"] != table:
                    continue
                if kind == "CONSTANT":
                    amount = _number_or_flag(value)
                else:
                    amount = values.get((table, turn["oid"]), {}).get(value)
                if usage == "Restriction":
                    if _truthy(amount):
                        turn["restricted_by"].add(attribute)
                        if attribute in restrictions:
                            turn["restrictions"].add(attribute)
                elif amount is not None:
                    turn["costs"][attribute] = turn["costs"].get(attribute, 0.0) + float(amount)
        self.policy = policy(graph.mode.get("uTurns"))
        self.degree: dict = {}
        for edge in graph.edges:
            for j in (edge["u"], edge["v"]):
                self.degree[j] = self.degree.get(j, 0) + 1
        self.active = bool(self.turns or self.globals or self.policy != "ALLOW")

    def uturn_allowed(self, junction):
        if self.policy == "ALLOW":
            return True
        if self.policy == "NO":
            return False
        degree = self.degree.get(junction, 0)
        if self.policy == "DEAD_ENDS":
            return degree == 1
        return degree != 2

    def global_cost(self, attribute, arriving, leaving):
        settings = self.globals.get(attribute)
        if settings is None:
            return 0.0
        return settings[category(rotation(self.graph, arriving, leaving), settings["minimum"], settings["maximum"])]

    def cost(self, attribute, step):
        """An attribute's cost of one transition: its global turn unless a two-edge turn feature covers it, and the
        turn features it completes."""
        arriving, leaving, completed = step
        total = 0.0
        if not any(len(self.turns[k]["edges"]) == 2 for k in completed):
            total += self.global_cost(attribute, arriving, leaving)
        for k in completed:
            total += self.turns[k]["costs"].get(attribute, 0.0)
        return total


def _seconds_to(units):
    word = str(units or "").upper()
    per = {"SECONDS": 1.0, "MINUTES": 1 / 60, "HOURS": 1 / 3600, "DAYS": 1 / 86400}.get(word)
    if per is None:
        raise ExecuteError("ERROR 030000: a turn category evaluator gives a delay in seconds, for a time attribute only.")
    return per


def _number_or_flag(value):
    if value is None:
        return None
    try:
        return float(value)
    except ValueError:
        return str(value).strip().lower() in ("true", "yes", "1")


def _truthy(value):
    if value is None:
        return False
    if isinstance(value, str):
        return value.strip().lower() not in ("", "0", "false", "no", "n", "f")
    return bool(value)


def model(graph):
    """The graph's turn model, made once."""
    found = getattr(graph, "_turn_model", None)
    if found is None:
        found = Model(graph)
        graph._turn_model = found
    return found


# ---- the solver over turns --------------------------------------------------------------------------------------------

class Step(tuple):
    """A transition between two edges inside a path: (edge arrived on, edge left by, turn features completed)."""


class Solver:
    """``_network._Solver``'s interface over the edge-expanded graph. ``run`` answers each node's cheapest cost (a
    junction's over every edge it is reached by) and the search's labels, which ``path`` and ``via`` read."""

    def __init__(self, graph, locations, reverse):
        self.graph = graph
        self.model = model(graph)
        self.reverse = reverse
        ids = sorted(graph.junctions)
        self.node = {j: k for k, j in enumerate(ids)}
        self.junction_of = {k: j for j, k in self.node.items()}
        self.location_node = {}
        for key, _, _ in locations:
            self.location_node[key] = len(self.node) + len(self.location_node)
        self.size = len(self.node) + len(self.location_node)
        on_edge: dict = {}
        for key, i, t in locations:
            on_edge.setdefault(i, []).append((t, self.location_node[key]))
        self.arcs = []          # (edge index, t0, t1, from node, to node, forward, impedance)
        self.leaving: dict = {}
        self.entering: dict = {}
        impedance = graph.impedance
        for i, edge in enumerate(graph.edges):
            stops = [(0.0, self.node[edge["u"]])] + sorted(on_edge.get(i, [])) + [(1.0, self.node[edge["v"]])]
            for (ta, na), (tb, nb) in zip(stops, stops[1:]):
                for forward in (True, False):
                    if not graph.passable(i, forward):
                        continue
                    a, b, t0, t1 = (na, nb, ta, tb) if forward else (nb, na, tb, ta)
                    cost = graph.cost(impedance, i, forward, t0, t1)
                    k = len(self.arcs)
                    self.arcs.append((i, t0, t1, a, b, forward, cost))
                    self.leaving.setdefault(a, []).append(k)
                    self.entering.setdefault(b, []).append(k)
        turns = self.model.turns
        self.sequences = [list(reversed(t["edges"])) if reverse else list(t["edges"]) for t in turns]
        self.starts: dict = {}
        for k, sequence in enumerate(self.sequences):
            if len(sequence) >= 2:
                self.starts.setdefault(sequence[0], []).append(k)

    # transitions

    def _begin(self, edge):
        return frozenset((k, 1) for k in self.starts.get(edge, ()))

    def _transition(self, active, previous_edge, next_edge, node):
        """(cost, new active set, step) of moving on from one arc's edge to the next at a node, or None when it is
        prohibited. ``previous_edge`` and ``next_edge`` are in search order."""
        junction = self.junction_of.get(node)
        if junction is None:
            # A located point part-way along an edge: going on along the same edge breaks nothing; a U-turn there
            # starts the edge sequence again and is no turn.
            if previous_edge == next_edge:
                return 0.0, active, None
            return 0.0, self._begin(next_edge), None
        arriving, leaving = (next_edge, previous_edge) if self.reverse else (previous_edge, next_edge)
        if arriving[0] == leaving[0] and arriving[1] != leaving[1] and not self.model.uturn_allowed(junction):
            return None
        completed, carried = [], set()
        for k, m in active:
            if self.sequences[k][m] == next_edge:
                if m + 1 == len(self.sequences[k]):
                    completed.append(k)
                else:
                    carried.add((k, m + 1))
        for k in completed:
            if self.model.turns[k]["restrictions"]:
                return None
        carried |= self._begin(next_edge)
        step = Step((arriving, leaving, tuple(sorted(completed))))
        return self.model.cost(self.graph.impedance, step), frozenset(carried), step

    # searching

    def run(self, key):
        source = self.location_node[key]
        best, labels = {}, {}
        heap = []
        counter = 0
        outward = self.entering if self.reverse else self.leaving
        for k in outward.get(source, ()):
            arc = self.arcs[k]
            state = (k, self._begin((arc[0], arc[5])))
            if arc[6] < best.get(state, math.inf):
                best[state] = arc[6]
                labels[state] = (None, None)
                heapq.heappush(heap, (arc[6], counter, state))
                counter += 1
        done = set()
        while heap:
            cost, _, state = heapq.heappop(heap)
            if state in done or cost > best.get(state, math.inf):
                continue
            done.add(state)
            k, active = state
            arc = self.arcs[k]
            node = arc[3] if self.reverse else arc[4]
            for j in outward.get(node, ()):
                following = self.arcs[j]
                moved = self._transition(active, (arc[0], arc[5]), (following[0], following[5]), node)
                if moved is None:
                    continue
                extra, carried, step = moved
                total = cost + extra + following[6]
                target = (j, carried)
                if total < best.get(target, math.inf) - 1e-12:
                    best[target] = total
                    labels[target] = (state, step)
                    heapq.heappush(heap, (total, counter, target))
                    counter += 1
        distances = numpy.full(self.size, numpy.inf)
        distances[source] = 0.0
        ending: dict = {}
        at: dict = {}
        for state, cost in best.items():
            arc = self.arcs[state[0]]
            node = arc[3] if self.reverse else arc[4]
            at.setdefault(node, []).append(state)
            if cost < distances[node]:
                distances[node] = cost
            if cost < ending.get(node, (math.inf, None))[0]:
                ending[node] = (cost, state)
        return distances, {"source": source, "best": best, "labels": labels, "ending": ending, "at": at}

    def path(self, search, start_key, end_key):
        """The arcs from start to end in travel order, as (edge index, t0, t1) with a ``Step`` between two edges where
        a turn is made; None when unreached."""
        start, node = self.location_node[start_key], self.location_node[end_key]
        if start == node:
            return []
        found = search["ending"].get(node)
        if found is None:
            return None
        chain = []
        state = found[1]
        while state is not None:
            arc = self.arcs[state[0]]
            previous, step = search["labels"][state]
            chain.append((arc[0], arc[1], arc[2]))
            if step is not None:
                chain.append(step)
            state = previous
        # Forward, the walk runs from the end of the journey back; reversed, from its start onward. A step sits between
        # the two arcs it joins either way, so only the order is turned.
        return chain[::-1] if not self.reverse else chain

    def via(self, distances, search, node, i, forward, t0, t1):
        """The cost at a node of going on along (forward search) or having come along (reversed) an edge piece: the
        node's cheapest label and the turn between, the start node's own zero, or infinity."""
        if node == search["source"]:
            return 0.0
        best = math.inf
        piece_edge = (i, forward)
        for state in search["at"].get(node, ()):
            cost = search["best"][state]
            arc = self.arcs[state[0]]
            moved = self._transition(state[1], (arc[0], arc[5]), piece_edge, node)
            if moved is not None and cost + moved[0] < best:
                best = cost + moved[0]
        return best

    def total(self, arcs, attribute):
        out = 0.0
        for arc in arcs:
            if isinstance(arc, Step):
                out += self.model.cost(attribute, arc)
            else:
                i, t0, t1 = arc
                out += self.graph.cost(attribute, i, t1 > t0, t0, t1)
        return out

    def shape(self, arcs):
        from ._network import _Solver
        return _Solver.shape(self, [a for a in arcs if not isinstance(a, Step)])


def turn_rows(graph, properties, attributes, wanted=None, area=None):
    """NetworkDataset.turns(): each turn feature's documented properties, then the attribute values asked for."""
    m = model(graph)
    rows = []
    sources = {table: len(graph.sources) + 1 + n for n, table in enumerate(m.classes)}
    for turn in m.turns:
        if wanted is not None and turn["turn_id"] not in wanted:
            continue
        if area is not None and not any(graph.edges[i]["shape"].intersects(area) for i, _ in turn["edges"]):
            continue
        mode = graph.mode
        values = {"EID": turn["turn_id"], "SOURCEID": sources[turn["class"]], "SOURCEOID": turn["oid"],
                  "EDGEEIDS": [graph.edges[i]["edge_id"] for i, _ in turn["edges"]],
                  "ISRESTRICTED": bool(turn["restrictions"]),
                  "COST": turn["costs"].get(graph.impedance, 0.0),
                  "TIME": turn["costs"].get(mode.get("timeAttributeName"), 0.0) if mode.get("timeAttributeName") else None,
                  "DISTANCE": turn["costs"].get(mode.get("distanceAttributeName"), 0.0)
                  if mode.get("distanceAttributeName") else None}
        row = [values[str(p).upper()] for p in properties]
        for attribute in attributes:
            usage = graph.attributes[attribute][0]
            row.append(attribute in turn["restricted_by"] if usage == "Restriction" else turn["costs"].get(attribute, 0.0))
        rows.append(tuple(row))
    return rows
