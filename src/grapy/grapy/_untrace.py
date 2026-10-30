"""The utility network traces grapy builds — ADR-231 §5.6.

Connected, Subnetwork, Subnetwork Controllers, Upstream, Downstream and Isolation, over the topology ``_utility.build``
makes from the record. What each does is what the "Utility network trace types" and "Connectivity and traversability"
pages say; where they say what but not how, the rule chosen is written beside it and in ADR-231 §5.9 (f):

- a connected trace treats a device with terminals as one feature and spans every link until a barrier;
- the subnetwork controllers of a starting point are those from which the resource can reach it — a search against
  the flow, through a directional device only from its downstream terminal to its upstream one (Scenario 1 of the
  page), stopping at the first controller of the tier;
- a subnetwork is everything the resource reaches from its controllers, passing through controllers of other tiers in a
  hierarchical domain network and stopping at them in a partitioned one; controllers of the tier with another
  subnetwork name are the documented error;
- upstream and downstream follow the flow found from the controllers (``_utility.flow_arcs``, an equivalent); a loop has
  no single direction, so it is traced whole unless indeterminate flow is ignored;
- an isolation trace spans out from the starting points to the filter barriers; those the resource can reach without
  passing the area are the isolating features, and the isolated features are what loses its supply once they close.
"""
from __future__ import annotations

from collections import defaultdict

from . import _utility
from ._runtime import ExecuteError


class Request:
    """A trace's parameters, as ``un.Trace`` read them."""

    def __init__(self, **kw):
        self.__dict__.update(kw)


def elements_of_locations(graph, locations, what):
    """Element ids for location rows (FEATUREGLOBALID, TERMINALID, PERCENTALONG), as the Starting points page says: a
    junction at its terminal (every terminal when none is named), an edge at PERCENTALONG — and an edge row without
    one is ignored, "it is assumed the starting point is to be placed on a junction"."""
    by_globalid = {g: k for k, (g, _) in graph.features.items()}
    out = []
    for globalid, terminal, percent in locations:
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
            out += [e.id for e in chosen]
            continue
        if percent is None:
            continue
        p = float(percent)
        hit = next((e for e in sorted(elements, key=lambda e: e.start) if e.start <= p <= e.end), None)
        if hit is not None:
            out.append(hit.id)
    return out


def run(network, graph, request):
    """(result element ids, kept for aggregated geometry) of a trace."""
    values = _utility.Values(graph)
    element = graph.elements
    starts = elements_of_locations(graph, request.starts, "starting point")
    feature_barriers = set(elements_of_locations(graph, request.barriers, "barrier"))
    condition = _utility.Condition(request.condition_barriers, values, request.traversability_scope)
    filtered = _utility.Condition(request.filter_barriers, values, request.filter_scope)
    kind = request.trace_type
    dynamic_free = set(starts) if request.ignore_barriers_at_starting_points else set()

    def dynamic(i):
        return i not in dynamic_free and (condition(element[i]) or (kind != "ISOLATION" and filtered(element[i])))

    def barrier(i):
        return i in feature_barriers or dynamic(i)

    if kind == "CONNECTED":
        if not starts:
            raise ExecuteError("ERROR 002078: a connected trace needs one or more starting points.")
        passed, stopped = _utility.reach(starts, graph.undirected(), barrier, starts_free=False)
        return passed | (stopped if request.include_barriers else set())

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

    own, other = {}, set()
    for e in element:
        found = values.controller(e)
        if found is None or graph.sources[e.source][1] != domain[0]:
            continue
        if found[1].lower() == tier[1].lower():
            own[e.id] = found[2]
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
        found = seeds
    else:
        if not starts:
            raise ExecuteError(f"ERROR 002078: a {kind.lower()} trace needs starting points"
                               + (" or a subnetwork name." if kind == "SUBNETWORK" else "."))
        user = barrier if kind in ("SUBNETWORK", "SUBNETWORK_CONTROLLERS") else (
            lambda i: i in feature_barriers or (i not in dynamic_free and condition(element[i])))             if kind == "ISOLATION" else (lambda i: False)
        back = _utility.reverse(controlled)
        passed, stopped = _utility.reach(starts, back, lambda i: user(i) or boundary(i) or (
            i in own and i not in starts), starts_free=True)
        found = {i for i in passed | stopped if i in own}
        found_names = {own[i] for i in found}
        if not found:
            raise ExecuteError(f"ERROR 002080: no subnetwork controller of the tier '{tier[1]}' is traversable from the "
                               "starting points.")
        if len({n.lower() for n in found_names}) > 1:
            raise ExecuteError("ERROR 002081: two or more subnetworks are traversable from the starting points: "
                               + ", ".join(sorted(found_names)) + ".")
        if kind == "SUBNETWORK_CONTROLLERS":
            return found
        seeds = {i for i, name in own.items() if name.lower() in {n.lower() for n in found_names}} | found
    names = {n.lower() for n in found_names}
    user_barrier = barrier if kind != "ISOLATION" else (
        lambda i: i in feature_barriers or (i not in dynamic_free and condition(element[i])))

    # Flow is a property of the network, not of one trace's barriers (INFERRED, ADR-231 §5.9 (f)): upstream and
    # downstream find their controllers and the subnetwork's flow without them, and stop at them when they trace.
    directional = kind in ("UPSTREAM", "DOWNSTREAM")
    passed, stopped = _utility.reach(seeds, controlled, lambda i: (not directional and user_barrier(i) and
                                                                    i not in seeds) or boundary(i))
    for i in passed | stopped:
        if i in own and own[i].lower() not in names:
            raise ExecuteError("ERROR 002081: two or more subnetworks are traversable: "
                               + ", ".join(sorted(found_names | {own[i]})) + ".")
    boundaries = {i for i in stopped if boundary(i)}
    region = passed | stopped
    if kind == "SUBNETWORK":
        kept = passed | boundaries
        return kept | ({i for i in stopped if user_barrier(i)} if request.include_barriers else set())

    if kind in ("UPSTREAM", "DOWNSTREAM"):
        arcs = _utility.flow_arcs(region, controlled, seeds, boundary)
        if not request.allow_indeterminate_flow:
            arcs = {a: {b for b in bs if a not in arcs.get(b, ())} for a, bs in arcs.items()}
        forward = arcs if domain[4] == "SOURCE" else _utility.reverse(arcs)
        if kind == "UPSTREAM":
            forward = _utility.reverse(forward)
        inside = [s for s in starts if s in region]
        traced, hit = _utility.reach(inside, forward, lambda i: (user_barrier(i) and i not in inside) or
                                     (boundary(i) and i not in inside), starts_free=True)
        kept = traced | {i for i in hit if boundary(i)}
        return kept | ({i for i in hit if user_barrier(i)} if request.include_barriers else set())

    # ISOLATION
    if not request.filter_barriers:
        raise ExecuteError("ERROR 002082: an isolation trace requires a filter barrier.")
    within = defaultdict(set)
    for a, bs in controlled.items():
        if a in region:
            for b in bs:
                if b in region:
                    within[a].add(b)
                    within[b].add(a)
    isolator = (lambda i: filtered(element[i]))
    zone, edge = _utility.reach(starts, within, lambda i: user_barrier(i) or isolator(i), starts_free=True)
    candidates = {i for i in edge if isolator(i)}
    local = {a: {b for b in bs if b in region} for a, bs in controlled.items() if a in region}
    fed, _ = _utility.reach(seeds, local, lambda i: user_barrier(i) or i in zone)
    isolating = {b for b in candidates if b in fed and any(z in zone for z in local.get(b, ()))}
    kept = set(isolating) if request.include_barriers else set()
    if request.include_isolated_features:
        before, _ = _utility.reach(seeds, local, user_barrier)
        after, _ = _utility.reach(seeds, local, lambda i: user_barrier(i) or i in isolating)
        kept |= (before - after - isolating) | zone
    return kept


def features(graph, ids):
    return {graph.elements[i].feature for i in ids if graph.elements[i].feature is not None}


def filter_output(network, graph, ids, assettypes, conditions):
    """Results kept by Output Asset Types (``Class/AssetGroup/AssetType``) and Output Conditions: a feature stays when
    one of its traced elements passes (INFERRED, ADR-231 §5.9 (f))."""
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
    by_feature = defaultdict(set)
    for i in ids:
        by_feature[graph.elements[i].feature].add(i)
    return {i for f, members in by_feature.items() if members & keep for i in members}


def related(graph, keys, containers, content, structures):
    """The features the Include Containers, Include Content and Include Structures options add, through the recorded
    containment and structural attachment associations; containment followed through every level (nesting)."""
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

    def follow(start, links):
        found, stack = set(), list(start)
        while stack:
            k = stack.pop()
            for n in links.get(k, ()):
                if n not in found:
                    found.add(n)
                    stack.append(n)
        return found

    if containers:
        out |= follow(keys, up)
    if content:
        out |= follow(keys, down)
    if structures:
        out |= {s for k in keys for s in attached_to.get(k, ())}
    return out
