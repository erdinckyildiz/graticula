"""The diagram rules a template runs when a diagram is built or updated — ADR-232 §5.4.

Each rule does what its Add … Rule page says; where a page says what but not how, the choice is written beside it and
in ADR-232 §5.9 (c) (INFERRED). A rule is a step of the template, ``{"kind": "rule", "rule": <name>, "active": bool,
"params": {...}}``; its sources are kept as class names (``WaterDevice``, ``SystemJunctions``) so a template's
definitions move between networks with the same classes (Export and Import Diagram Template Definitions).

- **Trace**: the trace runs from the diagram's features — or the starting points a Set Starting Point rule chose, or the
  trace locations the diagram was created from — and what it returns becomes the diagram's content.
- **Set starting point, set root junction, diagram feature capability**: flags later rules and layouts read.
- **Spatial query**: features of a class are added where they meet, as the overlap type says, features of another class
  already in the diagram.
- **Remove feature**: features removed by source, attribute or category, junctions optionally only when unconnected or
  connected to a single junction; a removed junction takes its edges with it (INFERRED).
- **Reduce junction**: a junction with no connection removed; with one, reduced onto that junction with its edge; with
  two, it and its two edges replaced by a reduction edge between the other two; with three or more, reduced onto the
  one junction upstream of it — by digitized direction, or toward the subnetwork controllers (INFERRED: the neighbour
  fewest hops from a controller, when there is exactly one).
- **Reduce edge**: a line or edge object and its connection points merged into one reduction junction; edges met there
  are reconnected to it, or replaced by reduction edges, one per junction they lead to.
- **Collapse and expand container**: a container's content aggregated into it, edges from the content reconnected to
  it; or its content added from its containment associations, the container kept visible or hidden.
- **Connectivity associations and structural attachments**: straight edges between junctions already in the diagram.
- **Start and stop iteration**: the rules between them run again while they change the diagram.
"""
from __future__ import annotations

from collections import deque

import shapely

from . import _untrace, _utility
from ._diagram import ASSOCIATION_SOURCE, MAX_ITERATIONS
from ._runtime import ExecuteError

CAPABILITIES = {"PREVENT_TO_COLLAPSE_CONTAINER": ("collapse", False), "ALLOW_TO_COLLAPSE_CONTAINER": ("collapse", True),
                "PREVENT_TO_REDUCE_JUNCTION": ("reduce", False), "ALLOW_TO_REDUCE_JUNCTION": ("reduce", True),
                "SAVE_EMPTY_CONTAINER_AS_POLYGON_IF_UNCONNECTED": ("polygon", True),
                "IGNORED_STARTING_POINT_FOR_UPSTREAM_TRACE": ("ignore_up", True),
                "IGNORED_STARTING_POINT_FOR_DOWNSTREAM_TRACE": ("ignore_down", True)}


class Context:
    def __init__(self, trace_locations):
        self.trace_locations = trace_locations
        self.locations_used = False
        self.hops = None


def run(builder, d, found, trace_locations=None):
    steps = [s for s in found["steps"] if s["kind"] == "rule"]
    ctx = Context(trace_locations)
    i = 0
    while i < len(steps):
        step = steps[i]
        if step["rule"] == "StartIteration":
            j = next((k for k in range(i + 1, len(steps)) if steps[k]["rule"] == "StopIteration"), len(steps))
            body = [s for s in steps[i + 1:j] if s["active"]]
            if step["active"]:
                for _ in range(MAX_ITERATIONS):
                    before = _snapshot(d)
                    for s in body:
                        apply(builder, d, s, ctx)
                    if _snapshot(d) == before:
                        break
            else:
                for s in body:
                    apply(builder, d, s, ctx)
            i = j + 1
            continue
        if step["active"]:
            apply(builder, d, step, ctx)
        i += 1


def _snapshot(d):
    return tuple(sorted((f.id, f.from_id, f.to_id, f.container) for f in d.features.values()))


def apply(builder, d, step, ctx):
    rule, p = step["rule"], step["params"]
    if rule in ("StartIteration", "StopIteration"):
        return
    if rule == "StructuralAttachments":
        _associations(builder, d, p, ctx, kind=3)
        return
    RULES[rule](builder, d, p, ctx)


# ---- selecting candidates ------------------------------------------------------------------------------------------

def candidates(builder, d, selector, kinds):
    """The features a rule acts on, in the order it acts on them: by listed sources (in list order when included, as
    the reduce page gives them priority), by a source and a where clause, or by network categories."""
    pool = [f for f in sorted(d.features.values(), key=lambda f: f.id) if f.kind in kinds and not f.association]
    by = selector.get("by", "source")
    if by == "attribute":
        source = builder.source_ids([selector["source"]])[0]
        if source == 0:
            return [f for f in pool if f.source == 0]
        chosen = builder.matching(source, selector.get("where"))
        return [f for f in pool if f.source == source and not f.reduction and builder.gkey(f.key) in chosen]
    if by == "category":
        wanted = [c for c in selector.get("categories", [])]
        inside = [f for f in pool if f.key and not f.reduction and builder.has_category(f, wanted)]
        if selector.get("include", True):
            return inside
        ids = {f.id for f in inside}
        return [f for f in pool if f.id not in ids]
    listed = builder.source_ids(selector.get("sources", []))
    if selector.get("include", True):
        out = []
        for s in listed:
            out += [f for f in pool if f.source == s and not f.reduction]
        return out
    return [f for f in pool if f.source not in listed or f.reduction]


def _junction_kinds(container_type="JUNCTIONS"):
    return {"JUNCTIONS": ("J", "C"), "EDGES": ("E",), "BOTH": ("J", "C", "E")}[container_type]


# ---- flags ---------------------------------------------------------------------------------------------------------

def _set_root(builder, d, p, ctx):
    for f in candidates(builder, d, {"by": "attribute", "source": p["source"], "where": p.get("where")}, ("J", "C")):
        f.root = True


def _set_starting_point(builder, d, p, ctx):
    terminals = [int(t) for t in p.get("terminals") or []]
    for f in candidates(builder, d, {"by": "attribute", "source": p["source"], "where": p.get("where")},
                        ("J", "C", "E")):
        f.starting = terminals


def _capability(builder, d, p, ctx):
    flag, value = CAPABILITIES[p["capability"]]
    valences = p.get("valences", [True, True, True, True])
    for f in candidates(builder, d, p["selector"], ("J", "C", "E")):
        if f.kind != "E":
            n = len(d.neighbours(f.id))
            if not valences[min(n, 3)]:
                continue
        f.flags[flag] = value


# ---- the trace rule ------------------------------------------------------------------------------------------------

def _rows_of(builder, d, features, terminals=None):
    rows = []
    for f in features:
        if not f.key or f.association:
            continue
        if f.kind == "E":
            if f.span:
                rows.append((f.globalid, None, (f.span[0] + f.span[1]) / 2))
            continue
        chosen = f.starting if f.starting else (terminals or [])
        if chosen:
            rows += [(f.globalid, t, None) for t in chosen]
        else:
            rows.append((f.globalid, None, None))
    return rows


def _trace(builder, d, p, ctx):
    kind = p["trace_type"]
    stops = []
    barriers = filter_locations = []
    if ctx.trace_locations and not ctx.locations_used:
        ctx.locations_used = True
        starts = list(ctx.trace_locations.get("starts", []))
        barriers = [r for r in ctx.trace_locations.get("barriers", []) if not r[3]]
        filter_locations = [r for r in ctx.trace_locations.get("barriers", []) if r[3]]
        stops = list(ctx.trace_locations.get("stops", []))
    else:
        chosen = [f for f in d.features.values() if f.starting is not None]
        pool = chosen or list(d.features.values())
        if kind == "UPSTREAM":
            pool = [f for f in pool if not f.flags.get("ignore_up")]
        if kind == "DOWNSTREAM":
            pool = [f for f in pool if not f.flags.get("ignore_down")]
        starts = _rows_of(builder, d, sorted(pool, key=lambda f: f.id))
        for f in chosen:
            f.starting = None
    if kind == "PATH" and not stops:
        raise ExecuteError("ERROR 002078: a path trace rule needs stopping points, which only a diagram created from "
                           "trace locations gives (Add Trace Rule).")
    if not starts:
        return
    request = _untrace.Request(trace_type=kind, starts=starts, barriers=barriers, filter_locations=filter_locations,
                               stops=stops, report=False, **p["request"])
    try:
        outcome = _untrace.run(builder.network, builder.graph, request)
    except ExecuteError:
        raise
    graph = builder.graph
    ids = _untrace.filter_output(builder.network, graph, outcome.ids, p.get("output_assettypes", []),
                                 p.get("output_conditions", []))
    keys = _untrace.features(graph, ids)
    keys = _untrace.related(graph, keys, False, False, p.get("include_structures", False))
    d.features.clear()
    builder.add_elements(d, ids)
    bare = {k for k in keys if not graph.by_feature.get(k)}
    wanted = set(bare)
    for k in list(keys):
        holder = builder.container_of.get(k)
        while holder is not None and holder not in wanted:
            wanted.add(holder)
            holder = builder.container_of.get(holder)
    builder.add_features(d, wanted)
    builder.link_containers(d)


# ---- adding --------------------------------------------------------------------------------------------------------

def _relates(kind, added, existing, distance):
    def outline(s):
        return s.boundary if s.geom_type in ("Polygon", "MultiPolygon") else s

    if kind == "INTERSECT":
        return added.distance(existing) <= distance if distance else added.intersects(existing)
    if kind == "WITHIN_A_DISTANCE":
        return added.distance(existing) <= (distance or 0.0)
    grown = existing.buffer(distance) if distance else existing
    if kind == "CONTAINS":
        return grown.contains(added) or added.contains(existing)
    if kind == "WITHIN":
        return added.within(grown)
    if kind == "BOUNDARY_TOUCHES":
        return added.touches(existing)
    if kind == "SHARE_A_LINE_SEGMENT_WITH":
        shared = outline(added).intersection(outline(existing))
        return not shared.is_empty and shared.length > 0
    if kind == "CROSSED_BY_THE_OUTLINE_OF":
        shared = outline(existing).intersection(outline(added))
        return not shared.is_empty and shared.length == 0
    raise ExecuteError(f"ERROR 000800: '{kind}' is not an overlap type.")


def _spatial_query(builder, d, p, ctx):
    existing_source = builder.source_ids([p["existing"]])[0]
    added_source = builder.source_ids([p["added"]])[0]
    allowed = builder.matching(existing_source, p.get("existing_where"))
    shapes = []
    for f in d.features.values():
        if f.source == existing_source and not f.reduction and builder.gkey(f.key) in allowed:
            s = builder.network_shape(f)
            if s is not None:
                shapes.append(s)
    if not shapes:
        return
    wanted = set()
    for g in sorted(builder.matching(added_source, p.get("added_where"))):
        wkt = builder.graph.features.get(g, (None, {}))[1].get("wkt")
        if not wkt:
            continue
        shape = shapely.from_wkt(wkt)
        if any(_relates(p["overlap"], shape, s, p.get("distance")) for s in shapes):
            wanted.add(g)
    if wanted:
        builder.add_features(d, wanted)
        builder.link_containers(d)


def _associations(builder, d, p, ctx, kind=1):
    """Connectivity associations (or structural attachments) between two junctions already in the diagram, drawn as
    straight edges from the association's from feature to its to feature."""
    _, by_key = builder.index(d)
    drawn = {f.association for f in d.features.values() if f.association}
    for row in builder.graph.associations:
        if int(row["type"]) != kind or str(row["globalid"]).upper() in drawn:
            continue
        a = by_key.get((_source_of(builder, row["from_globalid"]), str(row["from_globalid"]).upper()))
        b = by_key.get((_source_of(builder, row["to_globalid"]), str(row["to_globalid"]).upper()))
        a = [i for i in a or [] if d.features[i].kind in ("J", "C")]
        b = [i for i in b or [] if d.features[i].kind in ("J", "C")]
        if not a or not b:
            continue
        e = d.add("E", source=ASSOCIATION_SOURCE, globalid=str(row["globalid"]).upper())
        e.association = str(row["globalid"]).upper()
        e.from_id, e.to_id = a[0], b[0]


def _source_of(builder, globalid):
    g = builder.by_globalid.get(str(globalid).upper())
    return g[0] if g else None


# ---- removing and reducing -----------------------------------------------------------------------------------------

def _remove(builder, d, p, ctx):
    kinds = _junction_kinds(p.get("source_type", "BOTH"))
    unconnected, single = p.get("unconnected", False), p.get("single", False)
    constrained = p.get("source_type") == "JUNCTIONS" and (unconnected or single)
    for f in candidates(builder, d, p["selector"], kinds):
        if f.id not in d.features:
            continue
        if constrained and f.kind != "E":
            n = len(d.neighbours(f.id))
            if not ((unconnected and n == 0) or (single and n == 1)):
                continue
        d.remove(f.id)


def _edge_value(builder, d, e, name):
    """An edge's value of a network attribute: its own element's, or what the edges it aggregates have in common."""
    keys = ([e.key] if e.key and not e.association and not e.reduction else []) + [tuple(a) for a in e.aggregated]
    found = set()
    for k in keys:
        g = builder.gkey(k)
        if g is None:
            continue
        edges = [el for el in builder.graph.elements_of(g) if el.kind == "E"]
        if edges:
            found.add(builder.values.attribute(edges[0], name))
    return found.pop() if len(found) == 1 else None


def _same_attributes(builder, d, f, names):
    for name in names:
        values = {_edge_value(builder, d, e, name) for e in d.incident(f.id)}
        if len(values) > 1:
            return False
    return True


def _hops(builder):
    """Hops of each graph element from the subnetwork controllers, over the network's traversability."""
    graph = builder.graph
    seeds = [e.id for e in graph.elements if builder.values.controller(e)]
    arcs = graph.arcs()
    hops = {s: 0 for s in seeds}
    queue = deque(seeds)
    while queue:
        a = queue.popleft()
        for b in sorted(arcs.get(a, ())):
            if b not in hops:
                hops[b] = hops[a] + 1
                queue.append(b)
    return hops


def _upstream(builder, d, f, digitized, ctx):
    """The single junction a junction of three or more connections is reduced onto: the one at the from end of its
    only incoming edge by digitized direction, else its one neighbour nearest the subnetwork controllers (INFERRED)."""
    if digitized:
        froms = {e.from_id for e in d.incident(f.id) if e.to_id == f.id and e.from_id != f.id}
        return froms.pop() if len(froms) == 1 else None
    if ctx.hops is None:
        ctx.hops = _hops(builder)

    def hop(feature):
        found = [ctx.hops[i] for i in feature.elements if i in ctx.hops]
        return min(found) if found else None

    own = hop(f)
    ranked = sorted((h, n) for n in d.neighbours(f.id) for h in [hop(d.features[n])] if h is not None)
    if not ranked or own is None or ranked[0][0] >= own or (len(ranked) > 1 and ranked[1][0] == ranked[0][0]):
        return None
    return ranked[0][1]


def _reduce_junction(builder, d, p, ctx):
    for f in candidates(builder, d, p["selector"], ("J",)):
        if f.id not in d.features or f.flags.get("reduce") is False or d.contents(f.id) or f.reduction:
            continue
        if f.container is not None:   # content stays in its container: Pro 3.7 kept a contained valve (2026-10-31)
            continue
        if p.get("edge_attributes") and not _same_attributes(builder, d, f, p["edge_attributes"]):
            continue
        near = d.neighbours(f.id)
        incident = sorted(d.incident(f.id), key=lambda e: e.id)
        if p.get("connectivity", "MAX_2_CONNECTED_JUNCTIONS") == "MAX_2_CONNECTED_JUNCTIONS":
            if not near and not incident and p.get("unconnected"):
                d.features.pop(f.id)
            elif len(near) == 1 and p.get("one", False):
                target = d.features[next(iter(near))]
                d.aggregate(target, f, *incident)
                for e in incident:
                    d.features.pop(e.id)
                _drop(d, f)
            elif len(near) == 2 and p.get("two", True):
                first = incident[0]
                n1 = first.to_id if first.from_id == f.id else first.from_id
                n2 = next(iter(near - {n1}))
                a, b = (n1, n2) if first.to_id == f.id else (n2, n1)
                r = d.add("E")
                r.reduction, r.from_id, r.to_id = True, a, b
                d.aggregate(r, *incident, f)
                for e in incident:
                    d.features.pop(e.id)
                _drop(d, f)
        elif len(near) >= 3:
            target = _upstream(builder, d, f, p.get("digitized", False), ctx)
            if target is None:
                continue
            for e in incident:
                other = e.to_id if e.from_id == f.id else e.from_id
                if other == target:
                    d.aggregate(d.features[target], e)
                    d.features.pop(e.id)
                elif e.from_id == f.id:
                    e.from_id = target
                else:
                    e.to_id = target
            d.aggregate(d.features[target], f)
            _drop(d, f)


def _drop(d, f):
    for c in d.contents(f.id):
        c.container = f.container
    d.features.pop(f.id, None)


def _regroup(d, hub, edges, aggregate_them):
    """Edges reconnected to ``hub``: kept, or replaced by one reduction edge per junction they lead to."""
    if not aggregate_them:
        return
    groups = {}
    for e in sorted(edges, key=lambda e: e.id):
        if e.id not in d.features:
            continue
        other = e.to_id if e.from_id == hub.id else e.from_id
        groups.setdefault(other, []).append(e)
    for other, group in groups.items():
        first = group[0]
        r = d.add("E")
        r.reduction = True
        r.from_id, r.to_id = (hub.id, other) if first.from_id == hub.id else (other, hub.id)
        d.aggregate(r, *group)
        for e in group:
            d.features.pop(e.id)


def _reduce_edge(builder, d, p, ctx):
    chosen = candidates(builder, d, {"by": "attribute", "source": p["source"], "where": p.get("where")}, ("E",))
    by_line = {}
    for e in chosen:
        by_line.setdefault(e.key, []).append(e)
    for key, edges in by_line.items():
        edges = [e for e in edges if e.id in d.features]
        if not edges:
            continue
        points = sorted({i for e in edges for i in (e.from_id, e.to_id)
                         if i in d.features and not d.features[i].reduction})
        r = d.add("J", source=key[0], globalid=key[1])
        r.reduction = True
        d.aggregate(r, *edges, *[d.features[i] for i in points])
        gone = {e.id for e in edges} | set(points)
        moved = []
        for e in list(d.edges()):
            if e.id in gone:
                continue
            touched = False
            if e.from_id in gone:
                e.from_id, touched = r.id, True
            if e.to_id in gone:
                e.to_id, touched = r.id, True
            if touched:
                moved.append(e)
        for i in gone:
            for c in d.contents(i):
                c.container = None
            d.features.pop(i, None)
        _regroup(d, r, [e for e in moved if e.from_id != e.to_id], p.get("aggregate", True))


def _descendants(d, fid):
    out, stack = [], [fid]
    while stack:
        for c in d.contents(stack.pop()):
            out.append(c.id)
            stack.append(c.id)
    return out


def _collapse(builder, d, p, ctx):
    kinds = _junction_kinds(p.get("container_type", "BOTH"))
    for f in candidates(builder, d, p["selector"], kinds):
        if f.id not in d.features or f.flags.get("collapse") is False:
            continue
        inside = set(_descendants(d, f.id))
        if not inside:
            continue
        moved = []
        for e in list(d.edges()):
            if e.id in inside:
                continue
            a_in, b_in = e.from_id in inside, e.to_id in inside
            if not (a_in or b_in):
                continue
            if f.kind == "E" or (a_in and b_in) or (a_in and e.to_id == f.id) or (b_in and e.from_id == f.id):
                d.aggregate(f, e)
                d.features.pop(e.id)
                continue
            if a_in:
                e.from_id = f.id
            else:
                e.to_id = f.id
            moved.append(e)
        for i in sorted(inside):
            if i in d.features:
                d.aggregate(f, d.features[i])
                d.features.pop(i)
        if f.kind != "E":
            _regroup(d, f, moved, p.get("aggregate", True))


def _expand(builder, d, p, ctx):
    kinds = _junction_kinds(p.get("container_type", "BOTH"))
    for f in candidates(builder, d, p["selector"], kinds):
        if f.id not in d.features or f.reduction:
            continue
        g = builder.gkey(f.key)
        content = builder.contents_of.get(g, [])
        if not content:
            continue
        builder.add_features(d, set(content))
        builder.link_containers(d)
        if p.get("hide", False):
            _drop(d, f)


RULES = {"Trace": _trace, "SetStartingPoint": _set_starting_point, "SetRootJunction": _set_root,
         "Capability": _capability, "SpatialQuery": _spatial_query, "RemoveFeature": _remove,
         "ReduceJunction": _reduce_junction, "ReduceEdge": _reduce_edge, "CollapseContainer": _collapse,
         "ExpandContainer": _expand, "ConnectivityAssociations": _associations}
