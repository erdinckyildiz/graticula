"""What a solve traversed — ADR-246 (reverses ADR-226's "grapy's solvers keep routes, not their traversed elements").

grapy's solvers keep each route's path as the pieces of network edges it runs along, and — over turns — the turns
between them (``_network._Solver.path``, ``_turns.Solver.path``). This module writes them out in the documented shapes:

- the nax Route result's **RouteEdges**, **RouteJunctions** and **RouteTurns**, and the ClosestFacility result's
  RouteEdges (the *Route output data types* page's fields: ``SourceName``, ``SourceOID``, ``RouteEdgeID``,
  ``FromPosition``, ``ToPosition``, ``FromJunctionID``, ``ToJunctionID``, ``RouteID``, ``Attr_``/``Cumul_`` by unit);
- **CopyTraversedSourceFeatures**' Edges, Junctions and Turns (the *Copy Traversed Source Features tool output*
  page's fields: ``SourceName``, ``SourceOID``, ``SourceType``, ``EID``, positions, ``FromJunctionID``,
  ``ToJunctionID``, ``FromEdgeID``, ``ToEdgeID``, ``RouteID`` or ``FacilityID``, ``Attr_``/``Cumul_`` by attribute name);
- the **TraversedEdge**, **TraversedJunction** and **TraversedTurn** objects, read from those rows.

A traversal alternates junctions and edges, as the TraversedEdge page writes it: the route's first location (an
NA_CLASS junction), each edge piece, the network junction it ends at, and at the end of each leg the stop visited.
Between two edges, a turn feature completed there is a NETWORK turn and a global (turn category) turn a VIRTUAL one,
``ImplicitTurns``, as documented; restricted turns are never traversed.

INFERRED where the pages are silent (ADR-246 §5.6, Q-216): consecutive pieces of one edge travelled one way with no
turn between are one traversed edge; ``RouteEdgeID`` and ``RouteTurnID`` are the network element's EID and
``RouteJunctionID`` the junction's EID or the stop's ObjectID; a network junction's ``SourceName`` is the system
junction source, ``<network>_Junctions``; a restriction attribute's ``Attr_`` is 0, grapy's restrictions being hard
ones a route never uses; a service area keeps its lines as edges and the junctions they end at, and no turns; a time-
neutral solve's arrival and departure times are None; an edge's names and landmarks are empty, the network holding no
directions configuration.
"""
from __future__ import annotations

import enum

from shapely.ops import substring


class TraversedElementType(enum.Enum):
    """The kinds of traversed element, numbered in the order the page lists them (INFERRED: the page gives no values)."""
    Edge = 0
    Elevator = 1
    Escalator = 2
    Ferry = 3
    Hallway = 4
    HighwayRoad = 5
    Indoor = 6
    Junction = 7
    MajorRoad = 8
    MovingWalkway = 9
    PedestrianRamp = 10
    Ramp = 11
    RestBreak = 12
    Road = 13
    RoadIntersection = 14
    RoundaboutRoad = 15
    SailingLine = 16
    Stairs = 17
    Stop = 18
    Transit = 19
    Turn = 20
    TurningArc = 21
    Walkway = 22
    Waypoint = 23


# ---- building a traversal -----------------------------------------------------------------------------------------

def _step_class():
    from ._turns import Step
    return Step


class Traversal:
    """The edges, junctions and turns of one or more routes, as rows: ``_attr`` and ``_cumul`` hold each cost
    attribute's value for the element and up to and including it."""

    def __init__(self, graph, solver=None, owner="RouteID"):
        self.graph, self.solver, self.owner = graph, solver, owner
        self.edges, self.junctions, self.turns = [], [], []
        self._sequence = 0
        self._open = []
        self.costs = [a for a, (usage, _) in graph.attributes.items() if usage == "Cost"]
        self.restrictions = list(graph.mode.get("restrictions") or [])

    def _next(self):
        self._sequence += 1
        return self._sequence

    def _junction(self, owner, cumul, kind, source, oid, eid, point, attr=None):
        attr = attr or {a: 0.0 for a in self.costs}
        for a in self.costs:
            cumul[a] += attr[a]
        row = {"ObjectID": len(self.junctions) + 1, "SourceName": source, "SourceOID": oid, "SourceType": kind,
               "EID": eid, self.owner: owner, "_attr": attr, "_cumul": dict(cumul), "SHAPE": point,
               "_seq": self._next()}
        self.junctions.append(row)
        for edge in self._open:
            edge["ToJunctionID"] = row["ObjectID"]
        self._open = []
        return row

    def _network_junction(self, owner, cumul, junction):
        from shapely.geometry import Point
        graph = self.graph
        attr = {a: graph.junction_cost(a, junction) for a in self.costs}
        x, y = graph.junctions[junction]
        return self._junction(owner, cumul, "NETWORK", f"{graph.name}_Junctions", junction, junction, Point(x, y), attr)

    def add_route(self, owner, legs):
        """One route: ``legs`` are (from item, to item, arcs) in travel order, an item a dictionary with ``label`` (its
        sublayer: Stops, Facilities …), ``oid`` and ``point``."""
        if not legs:
            return
        Step = _step_class()
        graph = self.graph
        cumul = {a: 0.0 for a in self.costs}
        self._open = []
        first = legs[0][0]
        last_junction = self._junction(owner, cumul, "NA_CLASS", first["label"], first["oid"], -1, first.get("point"))
        previous = None                 # the edge row just written, while no junction or turn has followed it
        last_edge = None                # the last edge row written, for a turn's FromEdgeID
        for _, target, arcs in legs:
            steps = []
            for arc in arcs or []:
                if isinstance(arc, Step):
                    steps.append(arc)
                    continue
                i, t0, t1 = arc
                if t0 == t1:
                    continue
                forward = t1 > t0
                if previous is not None and not steps and previous["_edge"] == (i, forward) and \
                        abs(previous["_t"][1] - t0) < 1e-12:
                    attr = {a: graph.cost(a, i, forward, t0, t1) for a in self.costs}
                    for a in self.costs:
                        previous["_attr"][a] += attr[a]
                        cumul[a] += attr[a]
                    previous["_cumul"] = dict(cumul)
                    previous["_t"] = (previous["_t"][0], t1)
                    self._finish_edge(previous)
                else:
                    for step in steps:
                        self._turn_rows(owner, cumul, step, last_edge, len(self.edges) + 1)
                    attr = {a: graph.cost(a, i, forward, t0, t1) for a in self.costs}
                    for a in self.costs:
                        cumul[a] += attr[a]
                    edge = graph.edges[i]
                    row = {"ObjectID": len(self.edges) + 1, "SourceName": edge["source"].rsplit("/", 1)[-1],
                           "SourceOID": edge["oid"], "SourceType": "NETWORK", "EID": edge["edge_id"],
                           "FromJunctionID": last_junction["ObjectID"], "ToJunctionID": None, self.owner: owner,
                           "_attr": attr, "_cumul": dict(cumul), "_edge": (i, forward), "_t": (t0, t1),
                           "_seq": self._next()}
                    self._finish_edge(row)
                    self.edges.append(row)
                    self._open.append(row)
                    previous = last_edge = row
                steps = []
                head = graph.head_junction(i, t0, t1)
                if head is not None:
                    last_junction = self._network_junction(owner, cumul, head)
                    previous = None
            last_junction = self._junction(owner, cumul, "NA_CLASS", target["label"], target["oid"], -1,
                                           target.get("point"))
            previous = None

    def _finish_edge(self, row):
        edge = self.graph.edges[row["_edge"][0]]
        t0, t1 = row["_t"]
        row["FromPosition"] = edge["p0"] + (edge["p1"] - edge["p0"]) * t0
        row["ToPosition"] = edge["p0"] + (edge["p1"] - edge["p0"]) * t1
        piece = substring(edge["shape"], min(t0, t1), max(t0, t1), normalized=True)
        if piece.geom_type == "LineString" and t1 < t0:
            from shapely.geometry import LineString
            piece = LineString(list(piece.coords)[::-1])
        row["SHAPE"] = piece if piece.geom_type == "LineString" else None

    def _turn_rows(self, owner, cumul, step, from_edge, to_edge_oid):
        from . import _turns
        model = _turns.model(self.graph)
        arriving, leaving, completed = step
        rows = []
        for k in completed:
            turn = model.turns[k]
            rows.append(("NETWORK", turn["class"].rsplit("/", 1)[-1], turn["oid"], turn["turn_id"],
                         {a: turn["costs"].get(a, 0.0) for a in self.costs}))
        if model.globals and not any(len(model.turns[k]["edges"]) == 2 for k in completed):
            rows.append(("VIRTUAL", "ImplicitTurns", -1, -1,
                         {a: model.global_cost(a, arriving, leaving) for a in self.costs}))
        for kind, source, oid, eid, attr in rows:
            for a in self.costs:
                cumul[a] += attr[a]
            self.turns.append({"ObjectID": len(self.turns) + 1, "SourceName": source, "SourceOID": oid,
                               "SourceType": kind, "EID": eid,
                               "FromEdgeID": from_edge["ObjectID"] if from_edge else None, "ToEdgeID": to_edge_oid,
                               self.owner: owner, "_attr": attr, "_cumul": dict(cumul), "_seq": self._next()})


def service_area(graph, facilities, lines):
    """A service area's traversal: each facility's lines as edges (``_edge`` and ``_t`` as solve_service_area keeps
    them), the network junctions they start and end at, and the facility itself; no turns."""
    from shapely.geometry import Point
    out = Traversal(graph, owner="FacilityID")
    impedance = graph.impedance
    for facility in facilities:
        if facility.get("located") is None:
            continue
        out._open = []
        cumul = {a: 0.0 for a in out.costs}
        out._junction(facility["oid"], cumul, "NA_CLASS", "Facilities", facility["oid"], -1, facility["point"])
        reached = {}
        for line in lines:
            if line.get("FacilityID") != facility["oid"] or "_edge" not in line:
                continue
            i = line["_edge"]
            q0, q1 = line["_t"]
            forward = q1 > q0
            ends = []
            for q in (q0, q1):
                edge = graph.edges[i]
                junction = edge["u"] if q <= 1e-12 else edge["v"] if q >= 1 - 1e-12 else None
                if junction is not None and junction not in reached:
                    x, y = graph.junctions[junction]
                    row = {"ObjectID": len(out.junctions) + 1, "SourceName": f"{graph.name}_Junctions",
                           "SourceOID": junction, "SourceType": "NETWORK", "EID": junction,
                           "FacilityID": facility["oid"],
                           "_attr": {a: graph.junction_cost(a, junction) for a in out.costs},
                           "_cumul": {a: (line[f"FromCumul_{impedance}"] if q == q0 else line[f"ToCumul_{impedance}"])
                                      if a == impedance else None for a in out.costs}, "SHAPE": Point(x, y),
                           "_seq": out._next()}
                    out.junctions.append(row)
                    reached[junction] = row["ObjectID"]
                ends.append(reached.get(junction) if junction is not None else None)
            attr = {a: graph.cost(a, i, forward, q0, q1) for a in out.costs}
            attr[impedance] = line[f"ToCumul_{impedance}"] - line[f"FromCumul_{impedance}"]
            row = {"ObjectID": len(out.edges) + 1, "SourceName": graph.edges[i]["source"].rsplit("/", 1)[-1],
                   "SourceOID": graph.edges[i]["oid"], "SourceType": "NETWORK", "EID": graph.edges[i]["edge_id"],
                   "FromJunctionID": ends[0], "ToJunctionID": ends[1], "FacilityID": facility["oid"],
                   "_attr": attr, "_cumul": {a: line[f"ToCumul_{impedance}"] if a == impedance else None
                                             for a in out.costs},
                   "_edge": (i, forward), "_t": (q0, q1), "_seq": out._next()}
            out._finish_edge(row)
            out.edges.append(row)
    return out


def _item(value):
    return {"label": value.get("label") or value["key"][0], "oid": value["oid"], "point": value.get("point")}


def of_routes(graph, rows, owner="RouteID"):
    """The traversal of a route or closest facility solve's rows, each with the ``_legs`` its solver kept; a row's
    ObjectID (or its place, from 1) is its RouteID."""
    out = Traversal(graph, owner=owner)
    for n, row in enumerate(rows, start=1):
        legs = [(_item(a), _item(b), arcs) for a, b, arcs in row.get("_legs") or []]
        out.add_route(row.get("ObjectID", n), legs)
    return out


def of_fleet(fleet, rows):
    """The traversal of a vehicle routing or last mile delivery solve: each route's legs between the places it
    visits, along the paths its travel-time matrix found."""
    m = fleet.matrix
    out = Traversal(fleet.graph)

    def item(place):
        kind, oid = m.places[place][0][:2]
        return {"label": kind, "oid": oid, "point": fleet.place_point(place)}

    for row in rows:
        legs = [(item(a), item(b), m.solver.path(m.searches[a], m.places[a][0], m.places[b][0]))
                for a, b in row.get("_legs") or [] if a != b]
        out.add_route(row["ObjectID"], legs)
    return out


# ---- writing the rows -------------------------------------------------------------------------------------------------

def copy_rows(traversal):
    """Rows for CopyTraversedSourceFeatures: ``Attr_<attribute>`` and ``Cumul_<attribute>`` for each cost attribute,
    ``Attr_<restriction>`` 0 for each restriction of the travel mode."""
    out = {}
    for kind in ("edges", "junctions", "turns"):
        rows = []
        for row in getattr(traversal, kind):
            written = {k: v for k, v in row.items() if not k.startswith("_")}
            for a in traversal.costs:
                written[f"Attr_{a}"] = row["_attr"].get(a)
                written[f"Cumul_{a}"] = row["_cumul"].get(a)
            for r in traversal.restrictions:
                written[f"Attr_{r}"] = 0
            rows.append(written)
        out[kind] = rows
    return out


def result_rows(traversal, totals):
    """Rows for a nax result's RouteEdges, RouteJunctions and RouteTurns: the documented ``Attr_`` and ``Cumul_``
    fields by unit (``totals(graph, values, prefix)`` is the solver's own unit conversion)."""
    graph = traversal.graph
    ids = {"edges": "RouteEdgeID", "junctions": "RouteJunctionID", "turns": "RouteTurnID"}
    out = {}
    for kind in ("edges", "junctions", "turns"):
        rows = []
        for row in getattr(traversal, kind):
            written = {k: v for k, v in row.items() if not k.startswith("_") and k not in ("SourceType", "EID")}
            written[ids[kind]] = row["SourceOID"] if kind == "junctions" and row["SourceType"] == "NA_CLASS" \
                else row["EID"]
            for prefix, values in (("Attr", row["_attr"]), ("Cumul", row["_cumul"])):
                converted = totals(graph, {a: v or 0.0 for a, v in values.items()}, "Total")
                written.update({f"{prefix}_{k[len('Total_'):]}": v for k, v in converted.items()})
            for r in traversal.restrictions:
                written[f"Attr_{r.replace(' ', '_')}"] = 0
            written["_traversed"] = row
            rows.append(written)
        out[kind] = rows
    return out


# ---- the Traversed* objects ---------------------------------------------------------------------------------------

class TraversedElement:
    """One traversed element: its accumulated impedance, length and time, its row and its network source."""

    def __init__(self, row, graph, element_type):
        mode = graph.mode
        cumul = row["_cumul"]
        self._row, self._graph = row, graph
        self.accumulatedImpedance = cumul.get(graph.impedance)
        self.accumulatedLength = cumul.get(mode.get("distanceAttributeName")) if mode.get("distanceAttributeName") \
            else None
        self.accumulatedTime = cumul.get(mode.get("timeAttributeName")) if mode.get("timeAttributeName") else None
        self.rowID = row["ObjectID"]
        source = row["SourceName"]
        sources = [s.rsplit("/", 1)[-1] for s in graph.sources]
        self.sourceID = sources.index(source) + 1 if source in sources else \
            len(sources) + 1 if source == f"{graph.name}_Junctions" else -1
        self.sourceObjectID = row["SourceOID"]
        self.type = element_type
        self.violatedRestrictions = []

    def __repr__(self):
        return f"<{type(self).__name__} {self.rowID}>"


class _Named:
    """The names and landmarks a directions configuration would give; grapy's networks hold none."""

    names = ()
    referenceLandmarks = ()
    spatialLandmarks = ()

    def getName(self, index=0, mapping=None, fallback=""):
        return fallback

    def getNames(self, mapping=None, fallback=""):
        return []


class TraversedEdge(TraversedElement, _Named):
    def __init__(self, row, graph):
        from ._evaluators import Edge
        from . import _turns
        super().__init__(row, graph, TraversedElementType.Edge)
        i, forward = row["_edge"]
        t0, t1 = row["_t"]
        self.element = Edge(graph.edges[i]["edge_id"], forward)
        self.fromPosition = t0
        self.toPosition = self.toPostition = t1             # the page spells it toPostition
        coordinates = [c[:2] for c in row["SHAPE"].coords] if row.get("SHAPE") is not None else []
        from ._evaluators import _azimuth
        self.immediateAzimuths = (_azimuth(coordinates[0], coordinates[1]), _azimuth(coordinates[-2], coordinates[-1])) \
            if len(coordinates) >= 2 else (None, None)
        self.generalizedAzimuths = (_azimuth(coordinates[0], coordinates[-1]),) * 2 if len(coordinates) >= 2 \
            else (None, None)
        self.arrivalTime = self.departureTime = None
        self.administrativeArea = self.floorName = ""
        self.fromLevel = self.toLevel = None
        self.timezoneID = None
        self.directionPoints = []
        from ._evaluators import NetworkQuery  # noqa: F401 — the element reads through the same model
        self.drivingSide = _turns.driving_side(f"{graph.file}/{graph.dataset}/{graph.name}")


class TraversedJunction(TraversedElement, _Named):
    def __init__(self, row, graph):
        from ._evaluators import Junction
        stop = row["SourceType"] == "NA_CLASS"
        super().__init__(row, graph, TraversedElementType.Stop if stop else TraversedElementType.Junction)
        self.element = None if stop else Junction(row["EID"])
        self.arrivalTime = self.departureTime = None
        self.timeWindowStart = self.timeWindowEnd = None
        self.arriveCurbApproach = None
        self.waitTime = self.violationTime = 0.0
        self.level = None
        self.roadSplitID = self.signpostID = None
        self.referenceLandmark = None
        self.directionPoints = []


class TraversedTurn(TraversedElement):
    def __init__(self, row, graph):
        from ._evaluators import Turn
        super().__init__(row, graph, TraversedElementType.Turn)
        self.element = Turn(row["EID"]) if row["EID"] != -1 else None


def objects(traversal, owner=None):
    """The traversal's elements as Traversed* objects in travel order: by route, junctions and edges alternating, a
    turn before the edge it turns onto."""
    graph = traversal.graph
    out = []
    by_route = {}
    for kind, rows in (("junctions", traversal.junctions), ("edges", traversal.edges), ("turns", traversal.turns)):
        for row in rows:
            if owner is not None and row[traversal.owner] != owner:
                continue
            by_route.setdefault(row[traversal.owner], []).append((kind, row))
    for route in sorted(by_route, key=lambda r: (r is None, r)):
        for kind, row in sorted(by_route[route], key=lambda item: item[1]["_seq"]):
            cls = {"junctions": TraversedJunction, "edges": TraversedEdge, "turns": TraversedTurn}[kind]
            out.append(cls(row, graph))
    return out


def elements(result, route_id=None):
    traversal = getattr(result, "_traversal", None)
    if traversal is None:
        from ._runtime import ExecuteError
        raise ExecuteError("ERROR 030000: the result keeps no traversal: give a route or closest facility result solved "
                           "with returnRouteEdges.")
    return objects(traversal, None if route_id is None else int(route_id))
