"""The utility network traces grapy builds — ADR-231 §5.6 and its second batch.

Every trace type of a traditional domain network — Connected, Subnetwork, Subnetwork Controllers, Upstream, Downstream,
Isolation, Loops, Shortest Path and Path — over the topology ``_utility.build`` makes from the record. This module is
the one place where a trace's flow is decided: UpdateSubnetwork, ExportSubnetwork and Validate's discovered subnetworks
call ``run`` too. What each does is what the "Utility network trace types", "Connectivity and traversability",
"Barriers", "Configure a trace" and "Attribute propagation" pages say; where they say what but not how, the rule chosen
is written beside it and in ADR-231 §5.9 (f) and (j), and what ArcGIS Pro 3.7 was measured doing on 2026-10-30 is
marked so there (condition 5):

- a connected trace treats a device with terminals as one feature and spans every link until a barrier;
- the subnetwork controllers of a starting point are those from which the resource can reach it — a search against
  the flow, through a directional device only from its downstream terminal to its upstream one (Scenario 1 of the
  page), stopping at the first controller of the tier;
- a subnetwork is everything the resource reaches from its controllers, passing through controllers of other tiers in a
  hierarchical domain network and stopping at them in a partitioned one; controllers of the tier with another
  subnetwork name are the documented error;
- upstream and downstream follow the flow found from the controllers (``_utility.flow_arcs``, an equivalent) with the
  trace's feature and condition barriers closed and its filter barriers open; an edge the resource enters from both
  ends is indeterminate and passable both ways, or, when indeterminate flow is ignored, not entered — and a trace
  starting on one returns nothing; a target tier lets them pass the controllers of every tier ranked between the two,
  the flow found from the highest of them (rank 1 is the highest);
- an isolation trace spans out from the starting points to the filter barriers; those the resource can reach without
  passing the area are the isolating features, returned whether or not barriers are included, and the isolated
  features are what loses its supply once they close; an area holding a subnetwork controller returns nothing;
- a loops trace returns the elements on a cycle of what is connected to the starting points: the biconnected
  components of three or more nodes (Hopcroft and Tarjan 1973), a device's terminals counted as one node;
- a shortest path is Dijkstra's (1959) over the terminal paths both ways, an element costing its attribute's value on
  entry; a path trace returns the ``num_paths`` paths of fewest hops (Yen 1971) of at most ``max_hops``, or the
  cheapest one when a cost attribute is named and one path is asked for;
- function barriers keep a running total in breadth-first order (global) or along each branch (local values);
  propagators carry a controller's value with the flow and make a barrier of what fails their condition; a bitset
  filter carries the starting point's bits and stops where none remains.
"""
from __future__ import annotations

import heapq
import itertools
from collections import defaultdict

from . import _utility
from ._runtime import AddMessage, ExecuteError

FUNCTIONS = ("MIN", "MAX", "AVERAGE", "COUNT", "ADD", "SUBTRACT")
PROPAGATORS = {"PROPAGATED_BITWISE_AND": "bitwiseAnd", "PROPAGATED_MIN": "min", "PROPAGATED_MAX": "max"}
SUBNETWORK_BASED = ("SUBNETWORK", "SUBNETWORK_CONTROLLERS", "UPSTREAM", "DOWNSTREAM", "ISOLATION")
_DEFAULTS = dict(starts=(), barriers=(), stops=(), filter_locations=(), domain_network=None, tier=None, target_tier=None,
                 subnetwork_name=None, condition_barriers=(), filter_barriers=(), function_barriers=(),
                 filter_function_barriers=(), functions=(), propagators=(), traversability_scope="BOTH_JUNCTIONS_AND_EDGES",
                 filter_scope="BOTH_JUNCTIONS_AND_EDGES", include_barriers=True, include_isolated_features=False,
                 ignore_barriers_at_starting_points=False, allow_indeterminate_flow=True, shortest_path_attribute=None,
                 num_paths=None, max_hops=None, filter_bitset=None, nearest=None, digitized=False, report=True)


class Request:
    """A trace's parameters, as ``un.Trace`` (or a subnetwork tool) read them; what is not given takes its default."""

    def __init__(self, **kw):
        self.__dict__.update(_DEFAULTS)
        self.__dict__.update(kw)


class Outcome:
    """What a trace found: its element ids, the flow it followed (arcs u→v, or None for a trace without flow), the values
    its propagators carried ({element: {attribute: value}}), its function results and its warnings."""

    def __init__(self, ids, flow=None, propagated=None, functions=(), knn_found=None):
        self.ids, self.flow, self.propagated = set(ids), flow, propagated or {}
        self.functions, self.knn_found = list(functions), knn_found


def location_groups(graph, locations, what):
    """For location rows (FEATUREGLOBALID, TERMINALID, PERCENTALONG) one list of element ids per row, as the Starting
    points page says: a junction at its terminal (every terminal when none is named), an edge at PERCENTALONG — and an
    edge row without one is ignored, "it is assumed the starting point is to be placed on a junction"."""
    by_globalid = {g: k for k, (g, _) in graph.features.items()}
    out = []
    for row in locations:
        globalid, terminal, percent = row[0], row[1], row[2]
        if not globalid:
            continue
        key = by_globalid.get(str(globalid).upper())
        if key is None:
            raise ExecuteError(f"ERROR 002079: the {what} on {globalid} is not on a feature in the network topology; "
                               "validate the network topology first.")
        elements = graph.elements_of(key)
        junctions = [e for e in elements if e.kind == "J"]
        if junctions:
            chosen = [e for e in junctions if terminal is None or e.terminal == int(terminal) or e.terminal is None]
            if terminal is not None and not chosen:
                raise ExecuteError(f"ERROR 002079: {globalid} has no terminal {terminal}.")
            out.append([e.id for e in chosen])
            continue
        if percent is None:
            continue
        p = float(percent)
        hit = next((e for e in sorted(elements, key=lambda e: e.start) if e.start <= p <= e.end), None)
        if hit is not None:
            out.append([hit.id])
    return out


def elements_of_locations(graph, locations, what):
    return [i for group in location_groups(graph, locations, what) for i in group]


# ---- gates: function barriers, propagators, bitset filters ---------------------------------------------------------

class Accumulator:
    """A function's running value — MIN, MAX, AVERAGE, COUNT, ADD or SUBTRACT, the functions the pages list — over the
    values given; nulls are skipped, as the Attribute propagation page says of a nullable attribute, so COUNT of Shape
    length counts edge elements. SUBTRACT is the first value less the others (INFERRED, §5.9 (j))."""

    def __init__(self, function, state=None):
        self.function = function
        self.n, self.total, self.first, self.low, self.high = state or (0, 0.0, None, None, None)

    def add(self, value):
        if value is None:
            return self
        value = float(value)
        out = Accumulator(self.function, (self.n + 1, self.total + value, value if self.first is None else self.first,
                                          value if self.low is None else min(self.low, value),
                                          value if self.high is None else max(self.high, value)))
        return out

    @property
    def value(self):
        f = self.function
        if f == "COUNT":
            return self.n
        if self.n == 0:
            return None
        return {"MIN": self.low, "MAX": self.high, "ADD": self.total, "AVERAGE": self.total / self.n,
                "SUBTRACT": self.first - (self.total - self.first)}[f]


def _function_word(text, kind=None):
    word = str(text or "").strip().upper()
    if word not in FUNCTIONS:
        raise ExecuteError(f"ERROR 000800: '{text}' is not a function: MIN, MAX, AVERAGE, COUNT, ADD or SUBTRACT.")
    if word == "SUBTRACT" and kind in ("SUBNETWORK_CONTROLLERS", "LOOPS"):
        raise ExecuteError(f"ERROR 000800: the subtract function is not supported for a {kind.lower()} trace.")
    return word


def _truthy(value):
    return value is True or str(value).strip().upper() in ("TRUE", "1", "YES", "USE_LOCAL_VALUES")


class FunctionGate:
    """Function barriers (Function, Attribute, Operator, Value, Use Local Values): the element at which a function's
    total meets its condition stops the trace. Global values pool the total over every branch, taken in breadth-first
    order; local values keep one total per branch, along the path the trace first reached each element by, and allow one
    starting point only (the Barriers page). The element meeting the condition is the barrier, whole: grapy does not
    apportion a midspan stop (INFERRED, §5.9 (j))."""

    def __init__(self, rows, values, scope, kind, starts):
        self.values, self.scope = values, _utility.Condition([], values, scope)
        self.rows = []
        for row in rows:
            function = _function_word(row[0], kind)
            _attribute(values.graph.network, row[1])
            self.rows.append((function, row[1], str(row[2]).upper(), row[3], _truthy(row[4])))
        if any(r[4] for r in self.rows) and len(starts) > 1:
            raise ExecuteError("ERROR 002089: function barriers with local values allow one starting point only.")
        self.global_state = [Accumulator(r[0]) for r in self.rows]
        self.local = {}

    def __bool__(self):
        return bool(self.rows)

    def __call__(self, parent, child):
        element = self.values.graph.elements[child]
        mine, stop = [], False
        for k, (function, attribute, operator, value, local) in enumerate(self.rows):
            if local:
                before = self.local.get(parent, [Accumulator(r[0]) for r in self.rows])[k]
            else:
                before = self.global_state[k]
            after = before.add(self.values.attribute(element, attribute))
            if not local:
                self.global_state[k] = after
            mine.append(after)
            if self.scope.applies(element) and _utility.compare(after.value, operator, value):
                stop = True
        self.local[child] = mine
        return stop


class BitsetGate:
    """Filter by bitset network attribute (upstream and downstream): the starting point's bits are carried along, each
    element keeping only the bits it has, and the trace stops where none is left — the path no longer "reflects
    real-world conditions" (the Barriers page's phase example). A null passes the bits on (INFERRED, §5.9 (j))."""

    def __init__(self, attribute, values):
        _attribute(values.graph.network, attribute)
        self.attribute, self.values, self.carried = attribute, values, {}

    def __call__(self, parent, child):
        own = self.values.attribute(self.values.graph.elements[child], self.attribute)
        before = self.carried.get(parent) if parent is not None else None
        if own is None:
            now = before
        else:
            now = int(own) if before is None else before & int(own)
        self.carried[child] = now
        return now == 0


class Propagation:
    """Propagators (Attribute, Substitution Attribute, Function, Operator, Value[, Propagated Attribute]) as the
    Attribute propagation and Attribute substitution pages say: a controller's value is carried with the flow, each
    element combining it with its own — bitwise AND, MIN or MAX, nulls skipped; an element of the Propagator Resetter
    category starts again from its own value; one of the Attribute Substitution category passes on its value remapped by
    its substitution value, bit ``i·n + j`` mapping input bit ``i`` to output bit ``j`` (n = 3, or 4 when a substitution
    value has a bit above the ninth); an element whose value fails the condition is a barrier and is left out. An
    element reached by two paths keeps the value of the first, in breadth-first order (INFERRED, §5.9 (j)); a Subnetwork
    Tap is substituted like any other."""

    def __init__(self, rows, values):
        self.values, self.rows, self.value, self.failed = values, [], defaultdict(dict), set()
        for row in rows:
            function = str(row[2] or "").strip().upper()
            if function not in PROPAGATORS:
                raise ExecuteError(f"ERROR 000800: '{row[2]}' is not a propagator function: PROPAGATED_BITWISE_AND, "
                                   "PROPAGATED_MIN or PROPAGATED_MAX.")
            _attribute(values.graph.network, row[0])
            if row[1]:
                _attribute(values.graph.network, row[1])
            self.rows.append((row[0], row[1], function, str(row[3]).upper(), row[4], row[5] if len(row) > 5 else None))
        self.passed = {}

    def __bool__(self):
        return bool(self.rows)

    def __call__(self, parent, child):
        element = self.values.graph.elements[child]
        resetter = self.values.has_category(element, "Propagator Resetter")
        substituting = self.values.has_category(element, "Attribute Substitution")
        fails = False
        for attribute, substitution, function, operator, target, _ in self.rows:
            own = self.values.attribute(element, attribute)
            before = self.passed.get((parent, attribute)) if parent is not None else None
            if parent is None or resetter or before is None:
                now = own if own is not None else before
            elif own is None:
                now = before
            elif function == "PROPAGATED_BITWISE_AND":
                now = int(before) & int(own)
            elif function == "PROPAGATED_MIN":
                now = min(float(before), float(own))
            else:
                now = max(float(before), float(own))
            self.value[child][attribute] = now
            onward = now
            if substituting and substitution and now is not None:
                code = self.values.attribute(element, substitution)
                if code is not None:
                    onward = substitute(int(now), int(code))
            self.passed[(child, attribute)] = onward
            if now is not None and not _utility.compare(now, operator, target):
                fails = True
        if fails:
            self.failed.add(child)
        return fails


def substitute(value, code):
    """A bitset value remapped by a substitution value (the Attribute substitution page's encoding)."""
    n = 4 if code >= 512 else 3
    out = 0
    for i in range(n):
        if value & (1 << i):
            for j in range(n):
                if code & (1 << (i * n + j)):
                    out |= 1 << j
    return out


def _attribute(network, name):
    row = network.q("select name from grapy_un_attribute where network = ? and lower(name) = lower(?)", network.name,
                    str(name)).fetchone()
    if row is None:
        raise ExecuteError(f"ERROR 000732: '{name}' is not a network attribute of '{network.name}'.")
    return row[0]


def _gates(*gates):
    live = [g for g in gates if g]
    if not live:
        return None
    return lambda parent, child: any([g(parent, child) for g in live])


# ---- the trace -----------------------------------------------------------------------------------------------------

def run(network, graph, request):
    """A trace: its ``Outcome``."""
    values = _utility.Values(graph)
    element = graph.elements
    kind = request.trace_type
    start_groups = location_groups(graph, request.starts, "starting point")
    starts = [i for g in start_groups for i in g]
    feature_barriers = set(elements_of_locations(graph, request.barriers, "barrier"))
    filter_locations = set(elements_of_locations(graph, request.filter_locations, "filter barrier"))
    condition = _utility.Condition(request.condition_barriers, values, request.traversability_scope)
    filtered = _utility.Condition(request.filter_barriers, values, request.filter_scope)
    functions_gate = FunctionGate(request.function_barriers, values, request.traversability_scope, kind, start_groups)
    filter_gate = FunctionGate(request.filter_function_barriers, values, request.filter_scope, kind, start_groups)
    dynamic_free = set(starts) if request.ignore_barriers_at_starting_points else set()

    def isolator(i):
        return i in filter_locations or filtered(element[i])

    def dynamic(i):
        return i not in dynamic_free and (condition(element[i]) or (kind != "ISOLATION" and isolator(i)))

    def barrier(i):
        return i in feature_barriers or dynamic(i)

    def finish(ids, flow=None, propagated=None):
        return _finish(network, graph, values, request, starts, ids, flow, propagated)

    if kind in ("CONNECTED", "LOOPS"):
        if not starts:
            raise ExecuteError(f"ERROR 002078: a {kind.lower()} trace needs one or more starting points.")
        gate = _gates(functions_gate, filter_gate)
        passed, stopped = _utility.reach(starts, graph.undirected(), barrier, starts_free=False, gate=gate)
        if kind == "LOOPS":
            return finish(loops(graph, values, passed, request.filter_bitset))
        return finish(passed | (stopped if request.include_barriers else set()))

    if kind in ("SHORTEST_PATH", "PATH"):
        return finish(paths(graph, values, request, start_groups, barrier, dynamic_free | set(starts)))

    if kind in ("UPSTREAM", "DOWNSTREAM") and request.digitized:
        if int(network.version) < 7:
            raise ExecuteError("ERROR 002090: tracing by digitized direction needs utility network version 7 or later.")
        if not starts:
            raise ExecuteError(f"ERROR 002078: a {kind.lower()} trace needs starting points.")
        forward = graph.digitized_arcs()
        if kind == "UPSTREAM":
            forward = _utility.reverse(forward)
        bits = BitsetGate(request.filter_bitset, values) if request.filter_bitset else None
        traced, hit = _utility.reach(starts, forward, lambda i: barrier(i) and i not in starts, starts_free=True,
                                     gate=_gates(functions_gate, filter_gate, bits))
        kept = traced | (hit - _invalid(bits) if request.include_barriers else set())
        return finish(kept, graph.digitized_arcs())

    return _subnetwork_based(network, graph, request, values, starts, feature_barriers, condition, isolator, barrier,
                             dynamic_free, functions_gate, filter_gate, finish)


def _subnetwork_based(network, graph, request, values, starts, feature_barriers, condition, isolator, barrier,
                      dynamic_free, functions_gate, filter_gate, finish):
    element = graph.elements
    kind = request.trace_type
    if not request.domain_network or not request.tier:
        raise ExecuteError(f"ERROR 000735: a {kind.lower()} trace needs the domain network and tier parameters.")
    domain = network.domain(request.domain_network)
    if domain[2] != "Traditional":
        raise ExecuteError(f"ERROR 000732: '{request.domain_network}' is not a traditional domain network.")
    tier = network.tier(request.tier)
    if tier[0] != domain[0]:
        raise ExecuteError(f"ERROR 000732: the tier '{request.tier}' is not in '{domain[0]}'.")
    partitioned = domain[3] == "PARTITIONED"
    flow = graph.arcs(domain[0])
    controlled = flow if domain[4] == "SOURCE" else _utility.reverse(flow)

    # The tiers whose controllers the trace passes: the tier itself, or with a target tier every tier ranked between the
    # two; the flow is found from the highest of them (rank 1 is the highest, INFERRED §5.9 (j)).
    spelled = {name.lower(): name for (name,) in network.q("select name from grapy_un_tier where network = ? and "
                                                           "domain = ?", network.name, domain[0])}
    ranks = {name.lower(): rank for name, rank in network.q(
        "select name, rank from grapy_un_tier where network = ? and domain = ?", network.name, domain[0])}
    in_range = {tier[1].lower()}
    if request.target_tier and kind in ("UPSTREAM", "DOWNSTREAM"):
        target = network.tier(request.target_tier)
        if target[0] != domain[0]:
            raise ExecuteError(f"ERROR 000732: the target tier '{request.target_tier}' is not in '{domain[0]}'.")
        low, high = sorted((tier[2], target[2]))
        in_range = {n for n, r in ranks.items() if low <= r <= high}
    top = min(in_range, key=lambda n: (ranks[n], n))

    own, passing, other = {}, set(), set()
    for e in element:
        found = values.controller(e)
        if found is None or graph.sources[e.source][1] != domain[0]:
            continue
        name = found[1].lower()
        if name == top:
            own[e.id] = found[2]
        elif name in in_range:
            passing.add(e.id)
        else:
            other.add(e.id)

    def boundary(i):
        return partitioned and i in other

    if request.subnetwork_name and kind == "SUBNETWORK":
        seeds = {i for i, name in own.items() if name.lower() == str(request.subnetwork_name).lower()}
        if not seeds:
            raise ExecuteError(f"ERROR 002080: the subnetwork '{request.subnetwork_name}' has no controller in the tier "
                               f"'{tier[1]}'.")
        found_names = {own[i] for i in seeds}
    else:
        if not starts:
            raise ExecuteError(f"ERROR 002078: a {kind.lower()} trace needs starting points"
                               + (" or a subnetwork name." if kind == "SUBNETWORK" else "."))
        if kind in ("SUBNETWORK", "SUBNETWORK_CONTROLLERS"):
            user = barrier
        elif kind == "ISOLATION":
            def user(i):
                return i in feature_barriers or (i not in dynamic_free and condition(element[i]))
        else:
            def user(i):
                return False
        back = _utility.reverse(controlled)
        passed, stopped = _utility.reach(starts, back, lambda i: user(i) or boundary(i) or (
            i in own and i not in starts), starts_free=True)
        found = {i for i in passed | stopped if i in own}
        found_names = {own[i] for i in found}
        if not found:
            raise ExecuteError(f"ERROR 002080: no subnetwork controller of the tier '{spelled[top]}' is traversable "
                               "from the starting points.")
        if len({n.lower() for n in found_names}) > 1:
            raise ExecuteError("ERROR 002081: two or more subnetworks are traversable from the starting points: "
                               + ", ".join(sorted(found_names)) + ".")
        if kind == "SUBNETWORK_CONTROLLERS":
            return finish(found)
        seeds = {i for i, name in own.items() if name.lower() in {n.lower() for n in found_names}} | found
    names = {n.lower() for n in found_names}

    def user_barrier(i):
        if kind == "ISOLATION":
            return i in feature_barriers or (i not in dynamic_free and condition(element[i]))
        return barrier(i)

    # Upstream and downstream find the subnetwork's flow with the trace's traversability barriers closed — feature and
    # condition barriers — and not its filter barriers, at which they only stop: measured in Pro 3.7 on 2026-10-30
    # (ADR-231 §5.9 (f)), a closed valve as a condition or feature barrier changes which way the mesh around it flows,
    # and the same valve as a filter barrier does not. Propagators travel with that span from the controllers; what
    # fails them is a barrier every trace type keeps.
    directional = kind in ("UPSTREAM", "DOWNSTREAM")

    def traversability(i):
        return i in feature_barriers or (i not in dynamic_free and condition(element[i]))

    def closed(i):
        return i not in seeds and (traversability(i) if directional else user_barrier(i))

    propagation = Propagation(request.propagators, values)
    span_gate = _gates(propagation, None if directional else functions_gate, None if directional else filter_gate)
    passed, stopped = _utility.reach(seeds, controlled, lambda i: closed(i) or boundary(i), gate=span_gate)
    for i in passed | stopped:
        if i in own and own[i].lower() not in names:
            raise ExecuteError("ERROR 002081: two or more subnetworks are traversable: "
                               + ", ".join(sorted(found_names | {own[i]})) + ".")
    failed = propagation.failed
    boundaries = {i for i in stopped if boundary(i)}
    region = passed | stopped
    propagated = dict(propagation.value)

    def resource(arcs):
        """Arcs in the direction the resource flows: away from a source's controllers, toward a sink's."""
        return arcs if domain[4] == "SOURCE" else _utility.reverse(arcs)

    if kind == "SUBNETWORK":
        kept = passed | boundaries
        kept |= (stopped - failed) if request.include_barriers else set()
        return finish(kept - failed, resource(_flow(graph, region - failed, controlled, seeds, boundary)[0]),
                      propagated)

    if directional:
        arcs, indeterminate = _flow(graph, region, controlled, seeds, lambda i: boundary(i) or traversability(i))
        if not request.allow_indeterminate_flow:
            # Ignored, an indeterminate edge is not entered, and a trace starting on one returns nothing (measured in
            # Pro 3.7, 2026-10-30).
            arcs = {a: {b for b in bs if a not in arcs.get(b, ()) and b not in indeterminate}
                    for a, bs in arcs.items() if a not in indeterminate}
        forward = arcs if domain[4] == "SOURCE" else _utility.reverse(arcs)
        if kind == "UPSTREAM":
            forward = _utility.reverse(forward)
        inside = [s for s in starts if s in region and (request.allow_indeterminate_flow or s not in indeterminate)]
        bits = BitsetGate(request.filter_bitset, values) if request.filter_bitset else None

        def stop_here(i):
            return (user_barrier(i) or i in failed or boundary(i)) and i not in inside

        traced, hit = _utility.reach(inside, forward, stop_here, starts_free=True,
                                     gate=_gates(functions_gate, filter_gate, bits))
        # Pro 3.7 refuses an upstream trace whose barriers leave it no controller to reach — a feature barrier
        # cutting the start off its subnetwork, or a function barrier stopping it short (measured 2026-10-31,
        # ADR-231 condition 5) — and answers nothing, without an error, where indeterminate flow is what ends it.
        if kind == "UPSTREAM" and starts and not (traced | hit) & set(own) and (
                any(user_barrier(i) for i in hit) or bool(request.function_barriers)
                or not any(s in region for s in starts)):
            raise ExecuteError("ERROR 001797: No valid subnetwork controllers found.")
        kept = traced | {i for i in hit if boundary(i)}
        kept |= (hit - failed - _invalid(bits)) if request.include_barriers else set()
        return finish(kept, resource(arcs), propagated)

    # ISOLATION
    if not request.filter_barriers and not request.filter_locations and not request.filter_function_barriers:
        raise ExecuteError("ERROR 002082: an isolation trace requires a filter barrier.")
    within = defaultdict(set)
    for a, bs in controlled.items():
        if a in region:
            for b in bs:
                if b in region:
                    within[a].add(b)
                    within[b].add(a)
    zone, edge = _utility.reach(starts, within, lambda i: user_barrier(i) or i in failed or isolator(i),
                                starts_free=True, gate=_gates(filter_gate))
    flow = resource(_flow(graph, region, controlled, seeds, boundary)[0])
    if zone & seeds:
        # A subnetwork controller inside the leak's own area: no valve can isolate it, and Pro 3.7 returns nothing,
        # isolated features or not (measured 2026-10-30, ADR-231 §5.9 (f)).
        return finish(set(), flow, propagated)
    candidates = {i for i in edge if isolator(i) or (filter_gate and i not in failed and not user_barrier(i))}
    local = {a: {b for b in bs if b in region} for a, bs in controlled.items() if a in region}
    fed, _ = _utility.reach(seeds, local, lambda i: user_barrier(i) or i in failed or i in zone)
    isolating = {b for b in candidates if b in fed and any(z in zone for z in local.get(b, ()))}
    # The isolating features are the answer, not barriers the trace met: Pro 3.7 returns them with EXCLUDE_BARRIERS too
    # (measured 2026-10-30).
    kept = set(isolating)
    if request.include_isolated_features:
        before, _ = _utility.reach(seeds, local, lambda i: user_barrier(i) or i in failed)
        after, _ = _utility.reach(seeds, local, lambda i: user_barrier(i) or i in failed or i in isolating)
        kept |= (before - after - isolating) | zone
    return finish(kept, flow, propagated)


def _flow(graph, region, controlled, seeds, barrier):
    """The subnetwork's flow (``_utility.flow_arcs``) and its indeterminate edges: an edge element the resource can
    enter from both ends. Such an edge is passable both ways between everything it touches — measured in Pro 3.7 on
    2026-10-30, downstream of B1 with VB closed reaches J1 back through A2, which the dominator rule alone forbids
    (ADR-231 §5.9 (f))."""
    arcs = _utility.flow_arcs(region, controlled, seeds, barrier)
    into = _utility.reverse(arcs)
    indeterminate = {a for a, bs in arcs.items() if graph.elements[a].kind == "E" and any(a in arcs.get(b, ())
                                                                                           for b in bs)}
    out = defaultdict(set, {a: set(bs) for a, bs in arcs.items()})
    for e in indeterminate:
        for n in set(arcs.get(e, ())) | set(into.get(e, ())):
            out[e].add(n)
            out[n].add(e)
    return out, indeterminate


def _invalid(bits):
    """What a bitset filter stopped: no bit was left, so the element is not on a valid path and is not returned."""
    return {i for i, carried in bits.carried.items() if carried == 0} if bits else set()


def _finish(network, graph, values, request, starts, ids, flow, propagated):
    """The nearest filter, then the functions over what was traced."""
    found = None
    if request.nearest:
        ids, found = nearest(graph, values, request.nearest, starts, ids)
    results = functions(network, graph, values, request.functions, ids, request.trace_type, request.report)
    return Outcome(ids, flow, propagated, results, found)


# ---- loops, shortest path and path ---------------------------------------------------------------------------------

def loops(graph, values, reached, bitset=None):
    """The elements on a cycle of ``reached``: biconnected components of three or more nodes (Hopcroft and Tarjan 1973),
    found depth first over the connectivity with a device's terminals as one node. With a bitset attribute, a loop is
    kept only when some bit runs all the way round it (the Barriers page's phase example; INFERRED, §5.9 (j))."""
    node_of = {}
    for i in reached:
        e = graph.elements[i]
        node_of[i] = ("F", e.feature) if e.kind == "J" and e.feature is not None else ("E", i)
    adjacency = defaultdict(set)
    connectivity = graph.undirected()
    for a in reached:
        for b in connectivity.get(a, ()):
            if b in reached and node_of[a] != node_of[b]:
                adjacency[node_of[a]].add(node_of[b])
                adjacency[node_of[b]].add(node_of[a])
    members = defaultdict(list)
    for i, n in node_of.items():
        members[n].append(i)
    order, low, out = {}, {}, set()
    counter = itertools.count()
    for root in sorted(adjacency):
        if root in order:
            continue
        order[root] = low[root] = next(counter)
        stack, edges = [(root, None, iter(sorted(adjacency[root])))], []
        while stack:
            node, parent, children = stack[-1]
            advanced = False
            for child in children:
                if child == parent:
                    continue
                if child not in order:
                    order[child] = low[child] = next(counter)
                    edges.append((node, child))
                    stack.append((child, node, iter(sorted(adjacency[child]))))
                    advanced = True
                    break
                if order[child] < order[node]:
                    low[node] = min(low[node], order[child])
                    edges.append((node, child))
            if advanced:
                continue
            stack.pop()
            if parent is None:
                continue
            low[parent] = min(low[parent], low[node])
            if low[node] >= order[parent]:
                component = set()
                while edges:
                    a, b = edges.pop()
                    component |= {a, b}
                    if (a, b) == (parent, node):
                        break
                if len(component) >= 3:
                    ids = {i for n in component for i in members[n]}
                    if bitset is None or _bits_round(graph, values, ids, bitset):
                        out |= ids
    return out


def _bits_round(graph, values, ids, attribute):
    _attribute(graph.network, attribute)
    carried = None
    for i in sorted(ids):
        own = values.attribute(graph.elements[i], attribute)
        if own is not None:
            carried = int(own) if carried is None else carried & int(own)
    return carried is None or carried != 0


def _hops(graph, a, b):
    """A path trace's hops on entering ``b`` from ``a``, as the Trace page counts them: an edge 1, a junction-junction
    connection 1, a device's terminal path 2; the starting point is not counted."""
    eb = graph.elements[b]
    if eb.kind == "E":
        return 1
    ea = graph.elements[a]
    if ea.kind == "J" and ea.feature is not None and ea.feature == eb.feature:
        return 2
    if ea.kind == "J":
        return 1
    return 0


def _dijkstra(arcs, weight, source, target, blocked_nodes=frozenset(), blocked_arcs=frozenset()):
    """The cheapest path from ``source`` to ``target`` (Dijkstra 1959), ties to the lower node id; or None."""
    best, previous, heap, done = {source: 0.0}, {}, [(0.0, source)], set()
    while heap:
        cost, node = heapq.heappop(heap)
        if node in done:
            continue
        done.add(node)
        if node == target:
            path = [node]
            while path[-1] != source:
                path.append(previous[path[-1]])
            return cost, path[::-1]
        for nxt in sorted(arcs.get(node, ())):
            if nxt in blocked_nodes or (node, nxt) in blocked_arcs or nxt in done:
                continue
            new = cost + weight(node, nxt)
            if nxt not in best or new < best[nxt]:
                best[nxt], previous[nxt] = new, node
                heapq.heappush(heap, (new, nxt))
    return None


def k_shortest(arcs, weight, source, target, k):
    """Up to ``k`` loopless paths from ``source`` to ``target`` in order of cost (Yen 1971)."""
    first = _dijkstra(arcs, weight, source, target)
    if first is None:
        return []
    found, candidates = [first], []
    while len(found) < k:
        _, last = found[-1]
        for i in range(len(last) - 1):
            spur, root = last[i], last[:i + 1]
            blocked_arcs = {(p[i], p[i + 1]) for _, p in found if p[:i + 1] == root and len(p) > i + 1}
            spur_path = _dijkstra(arcs, weight, spur, target, frozenset(root[:-1]), frozenset(blocked_arcs))
            if spur_path is None:
                continue
            path = root[:-1] + spur_path[1]
            cost = sum(weight(a, b) for a, b in zip(path, path[1:]))
            if all(path != p for _, p in found) and all(path != p for _, p in candidates):
                heapq.heappush(candidates, (cost, path))
        if not candidates:
            break
        found.append(heapq.heappop(candidates))
    return found


def paths(graph, values, request, start_groups, barrier, free):
    """SHORTEST_PATH between two starting points, or PATH from the starting points to the stopping points — else to the
    unterminated ends (a line or edge object ending at a system junction of valence 1) — over the terminal paths both
    ways, a barrier never entered unless it is a starting or stopping point."""
    kind = request.trace_type
    arcs = graph.path_arcs()
    attribute = request.shortest_path_attribute
    if attribute:
        _attribute(graph.network, attribute)
    if kind == "SHORTEST_PATH":
        if not attribute:
            raise ExecuteError("ERROR 000735: a shortest path trace needs shortest_path_network_attribute_name.")
        if len(start_groups) != 2:
            raise ExecuteError("ERROR 002078: a shortest path trace needs exactly two starting points.")
        sources, targets = start_groups
    else:
        if not start_groups:
            raise ExecuteError("ERROR 002078: a path trace needs one or more starting points.")
        sources = [i for g in start_groups for i in g]
        stop_groups = location_groups(graph, request.stops, "stopping point")
        targets = [i for g in stop_groups for i in g]
        if not request.stops:
            degree = defaultdict(int)
            for a, b in graph.links:
                degree[a] += 1
                degree[b] += 1
            targets = [e.id for e in graph.elements if e.kind == "J" and e.feature is None and degree[e.id] == 1
                       and e.id not in sources]
        if not targets:
            raise ExecuteError("ERROR 002078: a path trace needs stopping points, or an unterminated line to stop at.")
    ends = set(sources) | set(targets) | free
    source, target = -1, -2
    local = defaultdict(set)
    for a, bs in arcs.items():
        if barrier(a) and a not in ends:
            continue
        local[a] = {b for b in bs if not barrier(b) or b in ends}
    local[source] = set(sources)
    for t in targets:
        local[t] = set(local.get(t, ())) | {target}

    def cost_of(b):
        if b < 0:
            return 0.0
        value = values.attribute(graph.elements[b], attribute) if attribute else None
        return float(value) if value is not None else 0.0

    def hops(a, b):
        return 0 if a < 0 or b < 0 else _hops(graph, a, b)

    count = 1 if kind == "SHORTEST_PATH" else int(request.num_paths or 1)
    limit = None if kind == "SHORTEST_PATH" else int(request.max_hops if request.max_hops is not None else 100)
    if attribute and count == 1:
        found = k_shortest(local, lambda a, b: cost_of(b) if a >= 0 else 0.0, source, target, 1 if limit is None else 50)
        found = [(c, p) for c, p in found if limit is None or sum(hops(a, b) for a, b in zip(p, p[1:])) <= limit][:1]
    else:
        found = k_shortest(local, hops, source, target, count)
        found = [(c, p) for c, p in found if c <= limit]
    if not found and kind == "SHORTEST_PATH":
        # Pro 3.7 succeeds with an empty result when barriers leave no path (measured 2026-10-31, ADR-231 condition 5).
        AddMessage("No shortest path was found between the locations; the result is empty.")
        return set()
    if not found:
        raise ExecuteError(f"ERROR 002078: no {kind.lower().replace('_', ' ')} was found between the locations.")
    return {i for _, p in found for i in p if i >= 0}


# ---- nearest and functions -----------------------------------------------------------------------------------------

def nearest(graph, values, settings, starts, ids):
    """Filter by nearest: of the traced features of the categories or asset types named, the ``count`` least costly to
    reach from the starting points by the cost attribute (Dijkstra 1959 over what was traced), ties to the lower
    feature; the trace returns those features. A count of -1 keeps them all (INFERRED, §5.9 (j))."""
    count, attribute, categories, assets = settings
    _attribute(graph.network, attribute)
    wanted = set()
    for each in assets or ():
        parts = [p.strip() for p in str(each).split("/")]
        if len(parts) != 3:
            raise ExecuteError(f"ERROR 000800: a nearest asset '{each}' is written Class/AssetGroup/AssetType.")
        network = graph.network
        class_name = network.table(parts[0])
        group = network.asset_group(class_name, parts[1])
        wanted.add((class_name, group, network.asset_type(class_name, group, parts[2])))
    arcs = graph.path_arcs()
    best = {}
    heap = [(0.0, s) for s in sorted(set(starts) & set(ids))]
    for _, s in heap:
        best[s] = 0.0
    heapq.heapify(heap)
    done = set()
    while heap:
        cost, node = heapq.heappop(heap)
        if node in done:
            continue
        done.add(node)
        for nxt in sorted(arcs.get(node, ())):
            if nxt not in ids or nxt in done:
                continue
            value = values.attribute(graph.elements[nxt], attribute)
            new = cost + (float(value) if value is not None else 0.0)
            if nxt not in best or new < best[nxt]:
                best[nxt] = new
                heapq.heappush(heap, (new, nxt))
    by_feature = {}
    for i, cost in best.items():
        e = graph.elements[i]
        if e.feature is None:
            continue
        match = any(values.has_category(e, c) for c in categories or ()) or values.asset(e) in wanted
        if match and (e.feature not in by_feature or cost < by_feature[e.feature]):
            by_feature[e.feature] = cost
    ranked = sorted(by_feature, key=lambda f: (by_feature[f], f))
    count = int(count) if count is not None else -1
    chosen = set(ranked if count < 0 else ranked[:count])
    return {i for i in ids if graph.elements[i].feature in chosen}, count < 0 or len(ranked) >= count


def functions(network, graph, values, rows, ids, kind=None, report=True):
    """Functions (Function, Attribute, Filter Name, Filter Operator, Filter Type, Filter Value, Function Name) over what
    was traced: an apportionable attribute over edge elements, any other over features, each counted once (the
    Configure a trace page); a filter keeps the elements it passes. Each result is written as a message (INFERRED: the
    page names no output) and returned as the JSON's globalFunctionResults."""
    out = []
    for row in rows or ():
        row = list(row) + [None] * (7 - len(row))
        function = _function_word(row[0], kind)
        attribute = _attribute(network, row[1])
        apportionable = network.q("select apportionable from grapy_un_attribute where network = ? and name = ?",
                                  network.name, attribute).fetchone()[0]
        condition = _utility.Condition([[row[2], row[3], row[4], row[5], None]], values) if row[2] else None
        acc, seen = Accumulator(function), set()
        for i in sorted(ids):
            e = graph.elements[i]
            if e.feature is None and not (apportionable and e.kind == "E"):
                continue
            if condition is not None and not condition(e):
                continue
            if not apportionable:
                if e.feature in seen:
                    continue
                seen.add(e.feature)
            acc = acc.add(values.attribute(e, attribute))
        result = {"functionType": function.lower(), "networkAttributeName": attribute, "result": acc.value}
        if row[6]:
            result["functionName"] = str(row[6])
        if report:
            AddMessage(f"{row[6] or function.title() + ' of ' + attribute}: {acc.value}")
        out.append(result)
    return out


# ---- results -------------------------------------------------------------------------------------------------------

def features(graph, ids):
    return {graph.elements[i].feature for i in ids if graph.elements[i].feature is not None}


def filter_output(network, graph, ids, assettypes, conditions):
    """Results kept by Output Asset Types (``Class/AssetGroup/AssetType``) and Output Conditions: the elements that pass
    them, and so the features with an element that passes — measured in Pro 3.7, 2026-10-30, whose ELEMENTS result of
    ``Terminal ID = 2`` lists a check valve's Source terminal and not its Load (ADR-231 §5.9 (f))."""
    if not assettypes and not conditions:
        return ids
    wanted = set()
    for each in assettypes or []:
        parts = [p.strip() for p in str(each).split("/")]
        if len(parts) != 3:
            raise ExecuteError(f"ERROR 000800: output asset type '{each}' is written Class/AssetGroup/AssetType.")
        class_name = network.table(parts[0])
        group = network.asset_group(class_name, parts[1])
        wanted.add((class_name, group, network.asset_type(class_name, group, parts[2])))
    values = _utility.Values(graph)
    condition = _utility.Condition(conditions, values)
    keep = set()
    for i in ids:
        e = graph.elements[i]
        if wanted and values.asset(e) not in wanted:
            continue
        if conditions and not condition(e):
            continue
        keep.add(i)
    return keep


def related(graph, keys, containers, content, structures, first_spatial=False):
    """The features the Include Containers, Include Content and Include Structures options add, through the recorded
    containment and structural attachment associations; containment followed through every level (nesting), or with
    ``first_spatial`` up to and including the first container that has a shape."""
    by_globalid = {g: k for k, (g, _) in graph.features.items()}
    up, down, attached_to = defaultdict(set), defaultdict(set), defaultdict(set)
    for row in graph.associations:
        a = by_globalid.get(str(row["from_globalid"]).upper())
        b = by_globalid.get(str(row["to_globalid"]).upper())
        if a is None or b is None:
            continue
        if int(row["type"]) == 2:
            up[b].add(a)
            down[a].add(b)
        elif int(row["type"]) == 3:
            attached_to[b].add(a)
    out = set(keys)

    def follow(start, links, stop=lambda k: False):
        found, stack = set(), list(start)
        while stack:
            k = stack.pop()
            for n in links.get(k, ()):
                if n not in found:
                    found.add(n)
                    if not stop(n):
                        stack.append(n)
        return found

    if containers:
        spatial = (lambda k: bool(graph.features.get(k, (None, {}))[1].get("wkt"))) if first_spatial else \
            (lambda k: False)
        out |= follow(keys, up, spatial)
    if content:
        out |= follow(keys, down)
    if structures:
        out |= {s for k in keys for s in attached_to.get(k, ())}
    return out


def unlocatable(graph, keys):
    """The junction and edge objects among ``keys`` with no containment or attachment association to locate them by
    (Validate Locatability)."""
    located = set()
    by_globalid = {g: k for k, (g, _) in graph.features.items()}
    for row in graph.associations:
        if int(row["type"]) in (2, 3):
            found = by_globalid.get(str(row["to_globalid"]).upper())
            if found is not None:
                located.add(found)
    return {k for k in keys if graph.sources[k[0]][2] in _utility.OBJECT_ROLES and k not in located}


def flow_direction(graph, flow, edge):
    """An edge element's flow against its digitized direction — ``withDigitized``, ``againstDigitized`` or
    ``indeterminate`` (the REST trace page's values) — from the arcs a trace followed."""
    if flow is None or edge not in graph.ends:
        return "indeterminate"
    starts, ends = graph.ends[edge]
    along = any(edge in flow.get(j, ()) for j in starts) and any(j in flow.get(edge, ()) for j in ends)
    against = any(edge in flow.get(j, ()) for j in ends) and any(j in flow.get(edge, ()) for j in starts)
    if along and not against:
        return "withDigitized"
    if against and not along:
        return "againstDigitized"
    return "indeterminate"
