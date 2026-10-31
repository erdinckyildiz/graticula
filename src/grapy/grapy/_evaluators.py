"""Custom evaluators on grapy's network datasets, and the network query they read — ADR-246 (reverses ADR-226 §5.1's
refusal of script evaluators for the custom evaluator API).

A custom evaluator is a subclass of ``arcpy.nax.AttributeEvaluator`` that gives a network attribute's value for each
edge, junction or turn of a solve, as the *custom evaluators* topic and the AttributeEvaluator page document. grapy's
solvers are Python, so the evaluator runs inside them:

- **Temporary**: a list given to ``NetworkDataset.customEvaluators``; a nax solver made from that NetworkDataset
  object (``arcpy.nax.Route(network_dataset)``) uses them, as the topic's workflow writes it.
- **Persisted**: ``NetworkDataset.updateNetworkDatasetSchema(custom_evaluators={attribute: {"class":
  "package.module.Class", "sourceNames": [...]}})`` stores a reference to the class in the network (grapy's
  ``grapy_network_property``, key ``custom_evaluators``); every solve on the network imports it. A class that does not
  load is left out with a warning and the solve goes on, as documented.

Each solve builds the travel mode's graph (``_network.Graph``), and the graph attaches each evaluator as the topic
orders it: the attribute is looked up by name (an evaluator whose attribute does not exist is not attached);
``attach(network_query)`` decides (False detaches it); ``refresh()`` runs before any element is evaluated; then each
value method the class overrides is called for every element of its sources — ``edgeValue`` for each edge in each
direction, ``junctionValue`` for each junction, ``turnValue`` for each turn feature — and its answer replaces the
attribute's base value for the rest of the solve. The ``…AtTime`` methods are not called: grapy's solves are time
neutral (time of day needs traffic, ADR-226 §5.3), and the topic calls them only in time-enabled solves.

INFERRED where the pages are silent or answered 404 (ADR-246 §5.6, Q-216): an Edge is one direction of travel along a
network edge (``eid`` the edge's EID, ``direction`` True along its digitised direction); ``None`` from a value method
keeps the base value; a junction cost is charged when a route passes through the junction, and a restricted junction
cannot be passed through; a junction's base value is 0 (or False), grapy's junctions carrying no attribute values;
an Attribute holds its ``name`` and its ``index`` in the network's attribute order; enumeration values number their
members in the order the pages list them, ``NetworkTimeUsage.Unused`` (named only by NetworkQuery's page) first;
``AdjacentEdge`` is an Edge as reached from a junction, what ``NetworkQuery.edges(junction)`` answers.
"""
from __future__ import annotations

import enum
import importlib
import json
import math

from ._runtime import ExecuteError


class NetworkTimeUsage(enum.Enum):
    Unused = 0
    BeforeTraversal = 1
    AfterTraversal = 2


class AttributeUsage(enum.Enum):
    Cost = 0
    Descriptor = 1
    Hierarchy = 2
    Restriction = 3


class AttributeParameterUsage(enum.Enum):
    General = 0
    Restriction = 1


# ---- network elements ---------------------------------------------------------------------------------------------

class NetworkElement:
    """A network element of the network dataset: a junction, a turn or an edge."""

    _kind = ""

    def __init__(self, eid):
        self.eid = int(eid)

    @property
    def isEdge(self):
        return self._kind == "edge"

    @property
    def isJunction(self):
        return self._kind == "junction"

    @property
    def isTurn(self):
        return self._kind == "turn"

    def _key(self):
        return (self._kind, self.eid)

    def __eq__(self, other):
        return isinstance(other, NetworkElement) and self._key() == other._key()

    def __hash__(self):
        return hash(self._key())

    def __repr__(self):
        return f"<{type(self).__name__} {self.eid}>"


class Edge(NetworkElement):
    """One direction of travel along a network edge (INFERRED, the class page answering 404)."""

    _kind = "edge"

    def __init__(self, eid, direction=True):
        super().__init__(eid)
        self.direction = bool(direction)

    def _key(self):
        return (self._kind, self.eid, self.direction)


class AdjacentEdge(Edge):
    """An edge as reached from a junction: what ``NetworkQuery.edges(junction)`` answers (INFERRED)."""


class Junction(NetworkElement):
    _kind = "junction"


class Turn(NetworkElement):
    """A turn feature of the network, by its EID (the order BuildNetwork numbered it, as ``NetworkDataset.turns()``
    reads it)."""

    _kind = "turn"


class Attribute:
    """The identifier of a network attribute: its name and its index in the network's attribute order."""

    def __init__(self, index, name):
        self.index, self.name = int(index), str(name)

    def __eq__(self, other):
        return isinstance(other, Attribute) and (self.index, self.name) == (other.index, other.name)

    def __hash__(self):
        return hash((self.index, self.name))

    def __int__(self):
        return self.index

    def __repr__(self):
        return f"<Attribute {self.index} {self.name}>"


class AttributeParameter:
    """A parameter of a network attribute: its name, usage and the value the solve uses."""

    def __init__(self, name, usage, value):
        self._name, self._usage, self._value = name, usage, value

    name = property(lambda self: self._name)
    usage = property(lambda self: self._usage)
    value = property(lambda self: self._value)

    def __repr__(self):
        return f"<AttributeParameter {self._name}={self._value!r}>"


# ---- the evaluator base class ---------------------------------------------------------------------------------------

class AttributeEvaluator:
    """The base class of a custom evaluator: subclass it and override at least one value method."""

    def __init__(self, attribute_name, specific_sources=None):
        self._attributeName = str(attribute_name)
        self._sourceNames = None if specific_sources is None else [s for s in specific_sources]
        self._attribute = None
        self._networkQuery = None
        self._attached = False

    attached = property(lambda self: self._attached)
    attribute = property(lambda self: self._attribute)
    attributeName = property(lambda self: self._attributeName)
    networkQuery = property(lambda self: self._networkQuery)
    sourceNames = property(lambda self: self._sourceNames)

    def attach(self, network_query):
        return True

    def refresh(self):
        return None

    def edgeValue(self, edge):
        return None

    def edgeValueAtTime(self, edge, time, time_usage):
        return None

    def junctionValue(self, junction):
        return None

    def junctionValueAtTime(self, junction, time, time_usage):
        return None

    def turnValue(self, turn):
        return None

    def turnValueAtTime(self, turn, time, time_usage):
        return None


def _overrides(evaluator, method):
    return getattr(type(evaluator), method) is not getattr(AttributeEvaluator, method)


# ---- the network query ------------------------------------------------------------------------------------------------

class NetworkQuery:
    """The network's properties, attributes, base attribute values and elements, as an evaluator reads them during a
    solve. Values are the base values: what the network's own evaluators give, before any custom evaluator."""

    def __init__(self, graph):
        self._graph = graph
        self._model = None

    @property
    def networkName(self):
        return self._graph.name

    @property
    def sourceNames(self):
        from . import _turns
        sources = [s.rsplit("/", 1)[-1] for s in self._graph.sources] + [f"{self._graph.name}_Junctions"]
        return sources + [t.rsplit("/", 1)[-1] for t in self._turn_model().classes] if _has_turns(self._graph) else sources

    def _turn_model(self):
        if self._model is not None:
            return self._model
        from . import _turns
        return _turns.model(self._graph)

    def attribute(self, attribute_name):
        names = list(self._graph.attributes)
        return Attribute(names.index(attribute_name), attribute_name) if attribute_name in names else None

    def _name(self, attribute):
        if isinstance(attribute, Attribute):
            return attribute.name
        if isinstance(attribute, int):
            return list(self._graph.attributes)[attribute]
        return str(attribute)

    def attributeUsage(self, attribute):
        return AttributeUsage[self._graph.attributes[self._name(attribute)][0]]

    def attributeParameters(self, attribute):
        name = self._name(attribute)
        return [AttributeParameter(p, AttributeParameterUsage[spec["usage"]], spec["value"])
                for (a, p), spec in self._graph.parameters.items() if a == name]

    def _edge(self, edge):
        return self._graph.edges[self._graph.edge_index[edge.eid]]

    def fromJunction(self, edge):
        e = self._edge(edge)
        return Junction(e["u"] if edge.direction else e["v"])

    def toJunction(self, edge):
        e = self._edge(edge)
        return Junction(e["v"] if edge.direction else e["u"])

    def _turn(self, turn):
        model = self._turn_model()
        found = next((t for t in model.turns if t["turn_id"] == turn.eid), None)
        if found is None:
            raise ValueError(f"The network has no turn {turn.eid}.")
        return found

    def atJunction(self, turn):
        if not isinstance(turn, Turn):
            first, second = list(turn)[:2]
            ends = {self.toJunction(first).eid} & {self.fromJunction(second).eid}
            return Junction(ends.pop()) if ends else None
        (i, forward) = self._turn(turn)["edges"][0]
        e = self._graph.edges[i]
        return Junction(e["v"] if forward else e["u"])

    def turns(self, junction):
        model = self._turn_model()
        out = []
        for t in model.turns:
            i, forward = t["edges"][0]
            e = self._graph.edges[i]
            if (e["v"] if forward else e["u"]) == junction.eid:
                out.append(Turn(t["turn_id"]))
        return out

    def edges(self, element):
        graph = self._graph
        if isinstance(element, Turn):
            return [Edge(graph.edges[i]["edge_id"], forward) for i, forward in self._turn(element)["edges"]]
        out = []
        for e in graph.edges:
            if e["u"] == element.eid:
                out.append(AdjacentEdge(e["edge_id"], True))
            if e["v"] == element.eid:
                out.append(AdjacentEdge(e["edge_id"], False))
        return out

    def edgePositions(self, edge):
        e = self._edge(edge)
        return (float(e["p0"]), float(e["p1"]))

    def edgeAzimuths(self, edge):
        """The azimuths, degrees clockwise from north, of the edge's first and last segments in its direction of travel
        (INFERRED)."""
        coordinates = [c[:2] for c in self._edge(edge)["shape"].coords]
        if not edge.direction:
            coordinates.reverse()
        return (_azimuth(coordinates[0], coordinates[1]), _azimuth(coordinates[-2], coordinates[-1]))

    def sourceInfo(self, element):
        graph = self._graph
        if isinstance(element, Edge):
            e = self._edge(element)
            return (graph.sources.index(e["source"]) + 1, int(e["oid"]))
        if isinstance(element, Junction):
            return (len(graph.sources) + 1, element.eid)
        t = self._turn(element)
        model = self._turn_model()
        return (len(graph.sources) + 2 + model.classes.index(t["class"]), int(t["oid"]))

    def attributeValue(self, element, attribute, time_usage=NetworkTimeUsage.Unused, local_time=None):
        graph = self._graph
        name = self._name(attribute)
        usage = graph.attributes[name][0]
        if isinstance(element, Edge):
            i = graph.edge_index[element.eid]
            if usage == "Restriction":
                return bool(graph.base_restrictions.get((name, i, element.direction), False))
            return float(graph.base_costs.get((name, i, element.direction), 0.0))
        if isinstance(element, Junction):
            return False if usage == "Restriction" else 0.0
        t = self._turn(element)
        if usage == "Restriction":
            return name in t.get("base_restricted", t["restricted_by"])
        return float(t.get("base_costs", t["costs"]).get(name, 0.0))


def _azimuth(a, b):
    angle = math.degrees(math.atan2(b[0] - a[0], b[1] - a[1]))
    return angle + 360.0 if angle < 0 else angle


def _has_turns(graph):
    from . import _gpkg
    connection = _gpkg.connect(graph.file)
    try:
        return bool(connection.execute("select 1 from sqlite_master where name = 'grapy_network_turn_source'").fetchone())
    finally:
        connection.close()


# ---- persisted evaluators and parameters ---------------------------------------------------------------------------

def persisted(network):
    """The persisted evaluators' references: {attribute: {"class": dotted path, "sourceNames": [...]}}."""
    from . import _turns
    text = _turns.get_property(network, "custom_evaluators")
    return json.loads(text) if text else {}


def persist(network, custom_evaluators):
    """Each attribute's evaluator reference stored, as updateNetworkDatasetSchema documents: a dictionary of attribute
    → {"class": "folder.module.Class", "sourceNames": [...]}. INFERRED: an attribute given ``None`` loses its
    evaluator; others already stored are kept."""
    from . import _network, _turns
    known = _network.attributes(network)
    stored = persisted(network)
    for attribute, spec in dict(custom_evaluators).items():
        if attribute not in known:
            raise ExecuteError(f"ERROR 030000: the network has no attribute '{attribute}'.")
        if spec is None:
            stored.pop(attribute, None)
            continue
        if not isinstance(spec, dict) or not str(spec.get("class") or "").count("."):
            raise ValueError("A custom evaluator is given as {'class': 'folder.module.ClassName', 'sourceNames': [...]}.")
        stored[attribute] = {"class": str(spec["class"]), "sourceNames": spec.get("sourceNames")}
    _turns.set_property(network, "custom_evaluators", json.dumps(stored))


def _load(attribute, spec):
    from ._runtime import AddWarning
    path = spec["class"]
    module, _, name = path.rpartition(".")
    try:
        cls = getattr(importlib.import_module(module), name)
        return cls(attribute, spec.get("sourceNames"))
    except Exception as failed:  # noqa: BLE001 — the solve goes on without it, as documented
        AddWarning(f"The custom evaluator '{path}' for '{attribute}' could not be loaded and is not used: {failed}")
        return None


def parameters(network):
    """The network's attribute parameters: {(attribute, parameter): {"usage", "value"}} (grapy_network_property,
    key ``attribute_parameters``)."""
    from . import _turns
    text = _turns.get_property(network, "attribute_parameters")
    stored = json.loads(text) if text else {}
    return {(a, p): dict(spec) for a, by_name in stored.items() for p, spec in by_name.items()}


def add_parameter(network, attribute, name, default_value=None, usage="General"):
    from . import _network, _turns
    if attribute not in _network.attributes(network):
        raise ExecuteError(f"ERROR 030000: the network has no attribute '{attribute}'.")
    word = str(usage or "General").title()
    if word not in AttributeParameterUsage.__members__:
        raise ExecuteError(f"ERROR 030000: '{usage}' is not an attribute parameter usage (General, Restriction).")
    text = _turns.get_property(network, "attribute_parameters")
    stored = json.loads(text) if text else {}
    stored.setdefault(attribute, {})[str(name)] = {"usage": word, "value": default_value}
    _turns.set_property(network, "attribute_parameters", json.dumps(stored))


def parameter_values(value):
    """A dictionary of (attribute, parameter) → value, from tuple keys, ``"attribute:parameter"`` keys, or the REST
    list of {attributeName, parameterName, value}."""
    if not value:
        return {}
    if isinstance(value, list):
        return {(v["attributeName"], v["parameterName"]): v.get("value") for v in value}
    out = {}
    for key, v in dict(value).items():
        if isinstance(key, str):
            attribute, _, parameter = key.partition(":")
            key = (attribute, parameter)
        out[tuple(key)] = v
    return out


# ---- attaching evaluators to a solve's graph --------------------------------------------------------------------------

def prepare(graph, evaluators=None, values=None):
    """The graph's parameters, and its custom evaluators attached and applied to its edges and junctions; turn
    evaluators are kept for ``_turns.Model``."""
    graph.base_costs = dict(graph.costs)
    graph.base_restrictions = dict(graph.restriction_values)
    graph.junction_costs, graph.blocked_junctions, graph.turn_evaluators = {}, set(), []
    graph.edge_index = {e["edge_id"]: k for k, e in enumerate(graph.edges)}
    network = f"{graph.file}/{graph.dataset}/{graph.name}"
    graph.parameters = parameters(network)
    for given in (graph.mode.get("attributeParameters"), values):
        for key, value in parameter_values(given).items():
            if key not in graph.parameters:
                raise ExecuteError(f"ERROR 030000: the network attribute '{key[0]}' has no parameter '{key[1]}'.")
            graph.parameters[key] = {**graph.parameters[key], "value": value}
    chosen = [e for e in (evaluators or [])]
    taken = {e.attributeName for e in chosen if isinstance(e, AttributeEvaluator)}
    for attribute, spec in persisted(network).items():
        if attribute not in taken:                    # a temporary evaluator replaces the persisted one (INFERRED)
            loaded = _load(attribute, spec)
            if loaded is not None:
                chosen.append(loaded)
    seen = set()
    for evaluator in chosen:
        if not isinstance(evaluator, AttributeEvaluator):
            raise TypeError("A custom evaluator is an instance of a subclass of arcpy.nax.AttributeEvaluator.")
        if evaluator.attributeName in seen:
            raise ValueError(f"The attribute '{evaluator.attributeName}' has more than one custom evaluator; an "
                             "attribute takes one.")
        seen.add(evaluator.attributeName)
        _apply(graph, evaluator)


def _sources(evaluator):
    return None if not evaluator.sourceNames else {str(s).rsplit("/", 1)[-1] for s in evaluator.sourceNames if s}


def _apply(graph, evaluator):
    from ._network import _truthy
    evaluator._attached, evaluator._networkQuery = False, None
    query = self_query = NetworkQuery(graph)
    attribute = query.attribute(evaluator.attributeName)
    if attribute is None:
        return
    evaluator._attribute = attribute
    if not evaluator.attach(query):
        return
    evaluator._networkQuery, evaluator._attached = self_query, True
    evaluator.refresh()
    name = attribute.name
    usage = graph.attributes[name][0]
    restricted = name in (graph.mode.get("restrictions") or [])
    sources = _sources(evaluator)
    if _overrides(evaluator, "edgeValue"):
        for i, edge in enumerate(graph.edges):
            if sources is not None and edge["source"].rsplit("/", 1)[-1] not in sources:
                continue
            for forward in (True, False):
                value = evaluator.edgeValue(Edge(edge["edge_id"], forward))
                if value is None:
                    continue
                if usage == "Restriction":
                    flag = _truthy(value)
                    graph.restriction_values[(name, i, forward)] = flag
                    if restricted:
                        (graph.blocked.add if flag else graph.blocked.discard)((i, forward))
                else:
                    graph.costs[(name, i, forward)] = float(value)
                    if name == graph.impedance:
                        if float(value) < 0:
                            graph.blocked.add((i, forward))
                        elif not any(graph.restriction_values.get((r, i, forward)) for r in graph.mode["restrictions"]):
                            graph.blocked.discard((i, forward))
    if _overrides(evaluator, "junctionValue") and (sources is None or f"{graph.name}_Junctions" in sources):
        for junction in sorted(graph.junctions):
            value = evaluator.junctionValue(Junction(junction))
            if value is None:
                continue
            if usage == "Restriction":
                if restricted and _truthy(value):
                    graph.blocked_junctions.add(junction)
            else:
                graph.junction_costs[(name, junction)] = float(value)
    if _overrides(evaluator, "turnValue"):
        graph.turn_evaluators.append(evaluator)


def apply_to_turns(model, graph):
    """Each turn evaluator's values on the model's turn features (called by ``_turns.Model``)."""
    from ._network import _truthy
    for turn in model.turns:
        turn["base_costs"] = dict(turn["costs"])
        turn["base_restricted"] = set(turn["restricted_by"])
    restrictions = set(graph.mode.get("restrictions") or [])
    for evaluator in getattr(graph, "turn_evaluators", []):
        evaluator.networkQuery._model = model
        name = evaluator.attributeName
        usage = graph.attributes[name][0]
        sources = _sources(evaluator)
        for turn in model.turns:
            if sources is not None and turn["class"].rsplit("/", 1)[-1] not in sources:
                continue
            value = evaluator.turnValue(Turn(turn["turn_id"]))
            if value is None:
                continue
            if usage == "Restriction":
                if _truthy(value):
                    turn["restricted_by"].add(name)
                    if name in restrictions:
                        turn["restrictions"].add(name)
                else:
                    turn["restricted_by"].discard(name)
                    turn["restrictions"].discard(name)
            else:
                turn["costs"][name] = float(value)
        evaluator.networkQuery._model = None
