"""The diagram layouts — ADR-232 §5.5.

Each layout moves a diagram's junctions and reshapes its edges. Where its page specifies the arrangement it is built
to the page (geo positions, grid, linear dispatch, angle directed, partial overlapping edges, reshape edges), the gaps
it leaves written beside it and in ADR-232 §5.9 (e) (INFERRED). Where the page only names the arrangement, the layout
is an *equivalent* (ADR-224 §5.6) — the standard method of the literature, named where it is used:

- smart tree, mainline tree branches and main ring trees: the tidy tree drawing of Reingold and Tilford (1981),
  subtrees pushed apart along their contours and a parent centred over its first and last child;
- radial tree: concentric levels with angular wedges in proportion to each subtree's leaves (Eades 1992);
- main ring: the longest cycle by depth-first search, its junctions spaced evenly round the ring;
- force directed: Fruchterman and Reingold (1991), from the current positions, with a linearly cooling temperature;
- spatial dispatch: the force-scan of Misue, Eades, Lai and Sugiyama (1995), pushing near junctions apart;
- compression: an orthogonal-order-preserving compaction (Misue et al. 1995) of the junction groups.

A layout works on the diagram's *view*: its junctions — with PRESERVE_CONTAINERS only the top graph, a container and
its content moving as one (the pages' "top graph") — and its edges between them; a diagram feature selection limits
the view to the features chosen. Proportional distances are in units of ``_diagram.JUNCTION_SIZE``.
"""
from __future__ import annotations

import math
from collections import defaultdict, deque

import shapely
import shapely.affinity
import shapely.ops
from shapely.geometry import LineString, Point

from . import _diagram
from ._diagram import JUNCTION_SIZE, containers, point_of, reconnect
from ._runtime import AddWarning, ExecuteError

TIDY = ("the tidy tree drawing: each subtree pushed against its left neighbour along their contours, a parent centred "
        "over its first and last child", "Reingold and Tilford 1981, Tidier Drawings of Trees")
RADIAL = ("a radial tree: one concentric circle per level, each subtree given an angular wedge in proportion to its "
          "leaves", "Eades 1992, Drawing free trees")
RING = ("the main ring as the longest cycle found by depth-first search, junctions evenly round it, the rest as tidy "
        "trees outward", "Reingold and Tilford 1981; Tarjan 1972, Depth-first search and linear graph algorithms")
FORCE = ("force-directed placement: repulsion k²/d between junctions, attraction d²/k along edges, a linearly cooling "
         "temperature", "Fruchterman and Reingold 1991, Graph Drawing by Force-directed Placement")
DISPATCH = ("the force-scan: junctions nearer than the separation pushed apart along the line joining them",
            "Misue, Eades, Lai and Sugiyama 1995, Layout Adjustment and the Mental Map")
COMPRESS = ("orthogonal-order-preserving compaction of the junction groups: each gap between successive group "
            "positions on x and on y shortened to the grouping distance", "Misue, Eades, Lai and Sugiyama 1995, Layout "
            "Adjustment and the Mental Map")


# ---- the view ------------------------------------------------------------------------------------------------------

class View:
    def __init__(self, d, preserve=False, selected=None):
        self.d = d
        self.preserve = preserve
        self.top = {}
        for f in d.features.values():
            t = f
            seen = set()
            while t.container is not None and t.container in d.features and t.id not in seen:
                seen.add(t.id)
                t = d.features[t.container]
            self.top[f.id] = t.id
        if preserve:
            nodes = [f.id for f in d.junctions() if self.top[f.id] == f.id]
        else:
            nodes = [f.id for f in d.junctions() if not d.contents(f.id)]
        if selected is not None:
            nodes = [n for n in nodes if n in selected]
        self.nodes = sorted(nodes)
        self.node_set = set(self.nodes)
        self.pos = {n: point_of(d.features[n].shape).coords[0] for n in self.nodes}
        self.edges = []
        for e in sorted(d.edges(), key=lambda e: e.id):
            a, b = self.map(e.from_id), self.map(e.to_id)
            if a in self.node_set and b in self.node_set:
                self.edges.append((e.id, a, b))
        self.adjacent = defaultdict(set)
        for _, a, b in self.edges:
            if a != b:
                self.adjacent[a].add(b)
                self.adjacent[b].add(a)

    def map(self, fid):
        return self.top.get(fid, fid) if self.preserve else fid

    def components(self):
        seen, out = set(), []
        for n in self.nodes:
            if n in seen:
                continue
            comp, queue = [], deque([n])
            seen.add(n)
            while queue:
                a = queue.popleft()
                comp.append(a)
                for b in sorted(self.adjacent[a]):
                    if b not in seen:
                        seen.add(b)
                        queue.append(b)
            out.append(sorted(comp))
        return out

    def commit(self, new, shapes=None, mode="keep", margin=None):
        """New positions written; content moved with its top container; edges reshaped: ``shapes`` {edge id: line}
        given by the layout, the rest kept with their ends on their junctions (``keep``) or made straight."""
        d = self.d
        for n, xy in new.items():
            old = self.pos[n]
            dx, dy = xy[0] - old[0], xy[1] - old[1]
            if dx == 0 and dy == 0:
                continue
            f = d.features[n]
            f.shape = shapely.affinity.translate(f.shape, dx, dy)
            if self.preserve:
                for g in d.features.values():
                    if g.id != n and self.top.get(g.id) == n and g.shape is not None:
                        g.shape = shapely.affinity.translate(g.shape, dx, dy)
        for e in d.edges():
            if shapes and e.id in shapes:
                e.shape = shapes[e.id]
            elif mode == "straight" and any(e.id == x[0] for x in self.edges):
                a, b = point_of(d.features[e.from_id].shape), point_of(d.features[e.to_id].shape)
                e.shape = LineString([a.coords[0], b.coords[0]]) if e.from_id != e.to_id else e.shape
        reconnect(d)
        containers(d, margin if margin is not None else _diagram.DEFAULT_MARGIN)


# ---- running layouts -----------------------------------------------------------------------------------------------

def template_layouts(builder, d, found):
    """The template's active layouts in order, each limited by the diagram feature selections just before it."""
    selection = None
    for step in found["steps"]:
        if not step["active"]:
            continue
        if step["kind"] == "selection":
            chosen = select(builder, d, step["params"])
            selection = chosen if selection is None else selection | chosen
        elif step["kind"] == "layout":
            run(builder, d, step["layout"], step["params"], found, selection)
            selection = None


def select(builder, d, p):
    """Add Diagram Feature Selection To Layout: by attributes, category or source, inverted, and limited to content."""
    from . import _ndrules
    pool = list(d.features.values())
    kind = p.get("selection_type", "SELECT_BY_ATTRIBUTES")
    if kind == "SELECT_BY_ATTRIBUTES":
        chosen = _ndrules.candidates(builder, d, {"by": "attribute", "source": p["source"], "where": p.get("where")},
                                     ("J", "C", "E"))
    elif kind == "SELECT_BY_CATEGORY":
        chosen = _ndrules.candidates(builder, d, {"by": "category", "categories": p["categories"]}, ("J", "C", "E"))
    else:
        chosen = _ndrules.candidates(builder, d, {"by": "source", "sources": p["sources"]}, ("J", "C", "E"))
    ids = {f.id for f in chosen}
    if p.get("invert"):
        ids = {f.id for f in pool} - ids
    if p.get("contained"):
        ids = {i for i in ids if d.features[i].container is not None}
    return ids


def run(builder, d, name, p, found, selected=None):
    margin = found["container_margin"]
    if name == "GeoPositions":
        return geo_positions(builder, d, p, margin, selected)
    view = View(d, p.get("preserve", False), selected)
    if not view.nodes:
        return None
    if name == "SmartTree" and p.get("order_by_attribute") and p.get("order_attribute"):
        view.values = order_values(builder, d, p)
    {"Grid": grid, "LinearDispatch": linear_dispatch, "SpatialDispatch": spatial_dispatch, "Compression": compression,
     "AngleDirected": angle_directed, "ForceDirected": force_directed, "SmartTree": smart_tree,
     "RadialTree": radial_tree, "MainlineTree": mainline_tree, "MainRing": main_ring,
     "PartialOverlappingEdges": partial_overlapping_edges, "ReshapeEdges": reshape_edges}[name](view, p, margin)
    return None


def order_values(builder, d, p):
    """Smart Tree's order attribute: a field of the network feature (by name or alias) or a network attribute, read on
    each junction or, with EDGE, on the edge leading to it, keyed (parent, child) (INFERRED, §5.9 (e))."""
    from . import _gpkg
    name = str(p["order_attribute"])
    out = {}
    cache = {}

    def value(f):
        if f is None or not f.key:
            return None
        g = builder.gkey(f.key)
        if g is None:
            return None
        class_name = builder.sources[g[0]][0]
        if class_name not in cache:
            fields = {c.name.lower(): c.name for c in _gpkg.describe(builder.network.connection, class_name)[0]}
            cache[class_name] = fields
        column = cache[class_name].get(name.lower())
        if column:
            row = builder.network.q(f'select "{column}" from "{class_name}" where "{builder.network.oid(class_name)}" = ?',
                                    g[1]).fetchone()
            return row[0] if row else None
        elements = [builder.graph.elements[i] for i in f.elements]
        return builder.values.attribute(elements[0], name) if elements else None

    if p.get("order_on_edge"):
        for e in d.edges():
            v = value(e)
            out[(e.from_id, e.to_id)] = out[(e.to_id, e.from_id)] = v
    else:
        for f in d.junctions():
            out[f.id] = value(f)
    return out


# ---- geographic, grid and dispatch ---------------------------------------------------------------------------------

def geo_positions(builder, d, p, margin, selected):
    """Apply Geo Positions Layout: every junction back at its network feature's position; edges along their lines when
    RESTORE_EDGES_GEO_POSITIONS and they represent one piece of a line, else straight; reduced edges always straight."""
    view = View(d, False, selected)
    placed = {}
    new = {}
    for n in sorted(view.nodes, key=lambda n: (bool(d.features[n].reduction), n)):
        placed[n] = builder.junction_position(d, d.features[n], placed)
        new[n] = placed[n].coords[0]
    shapes = {}
    restore = p.get("restore_edges", True)
    for e in d.edges():
        if e.from_id not in view.node_set or e.to_id not in view.node_set:
            continue
        a, b = new[e.from_id], new[e.to_id]
        piece = builder.edge_piece(e) if restore and not e.reduction and not e.association else None
        if piece is not None and len(piece.coords) > 2:
            shapes[e.id] = LineString([a] + list(piece.coords)[1:-1] + [b])
        elif e.from_id != e.to_id:
            shapes[e.id] = LineString([a, b])
    view.commit(new, shapes, margin=margin)


def grid(view, p, margin):
    """Apply Grid Layout: each junction to the nearest node of a grid of the cell width and height; a node already
    taken sends the next junction (in feature id order) to the nearest free one (INFERRED, §5.9 (e))."""
    w, h = float(p.get("cell_width", 2.0)), float(p.get("cell_height", 2.0))
    if w <= 0 or h <= 0:
        raise ExecuteError("ERROR 000800: a grid cell's width and height are greater than 0.")
    taken, new = set(), {}
    for n in view.nodes:
        x, y = view.pos[n]
        i0, j0 = round(x / w), round(y / h)
        best, ring = None, 0
        while best is None:
            cells = [(i, j) for i in range(i0 - ring, i0 + ring + 1) for j in range(j0 - ring, j0 + ring + 1)
                     if max(abs(i - i0), abs(j - j0)) == ring and (i, j) not in taken]
            if cells:
                best = min(cells, key=lambda c: (math.hypot(c[0] * w - x, c[1] * h - y), c[1], c[0]))
            ring += 1
        taken.add(best)
        new[n] = (best[0] * w, best[1] * h)
    view.commit(new, margin=margin)


def _chains(view):
    """Chains of two-edge junctions (connected to exactly two others by one edge each) between two other junctions:
    (anchor, [junctions in order], anchor, [edges in order])."""
    edges_of = defaultdict(list)
    for eid, a, b in view.edges:
        if a != b:
            edges_of[a].append((eid, b))
            edges_of[b].append((eid, a))

    def two(n):
        return len(edges_of[n]) == 2 and len({o for _, o in edges_of[n]}) == 2

    done, out = set(), []
    for n in view.nodes:
        if not two(n) or n in done:
            continue
        chain, edges = [n], []
        done.add(n)
        ends = []
        for eid, other in sorted(edges_of[n]):
            part, part_edges, prev, cur, cur_edge = [], [eid], n, other, eid
            while two(cur) and cur not in done:
                done.add(cur)
                part.append(cur)
                nxt = [(e, o) for e, o in edges_of[cur] if e != cur_edge][0]
                prev, cur, cur_edge = cur, nxt[1], nxt[0]
                part_edges.append(cur_edge)
            ends.append((part, part_edges, cur))
        (left, left_edges, a), (right, right_edges, b) = ends
        nodes = list(reversed(left)) + chain + right
        edges = list(reversed(left_edges)) + right_edges
        if a == b and a in nodes:
            continue
        out.append((a, nodes, b, edges))
    return out


def _path_line(view, a, nodes, b, edges):
    d = view.d
    points = [view.pos[a]]
    seq = [a] + nodes + [b]
    for k, eid in enumerate(edges):
        coords = list(d.features[eid].shape.coords)
        if d.features[eid].from_id != view.map(seq[k]) and view.map(d.features[eid].to_id) == seq[k]:
            coords.reverse()
        points += coords[1:-1] + [view.pos[seq[k + 1]]]
    return LineString(points)


def linear_dispatch(view, p, margin):
    """Apply Linear Dispatch Layout. Two-edge junctions are moved along the path their chain's edges make between the
    junctions at its ends: EQUAL_DISTANCE spaces them evenly; USER_DEFINE_DISTANCE keeps them unless nearer than the
    minimum shift to the next, and spaces them evenly when the minimum cannot be kept; ITERATIVE_DISTANCE moves each
    toward its even place by an equal share of the maximum shift per iteration (INFERRED, §5.9 (e)). PRESERVE_PATH
    cuts the edges from the path, IGNORE_PATH draws them straight. A leaf is moved with the junction it hangs from
    (MOVE_LEAVES) and pushed out to the expand shift (EXPAND_LEAVES), INFERRED."""
    kind = p.get("placement", "EQUAL_DISTANCE")
    new, shapes = {}, {}
    moved_by = {}
    for a, nodes, b, edges in _chains(view):
        line = _path_line(view, a, nodes, b, edges)
        total = line.length
        if total == 0:
            continue
        here = [line.project(Point(view.pos[n])) for n in nodes]
        k = len(nodes)
        even = [total * (i + 1) / (k + 1) for i in range(k)]
        if kind == "EQUAL_DISTANCE":
            at = even
        elif kind == "USER_DEFINE_DISTANCE":
            m = float(p.get("minimum_shift", 2.0))
            if (k + 1) * m > total:
                at = even
            else:
                at = list(here)
                for i in range(k):
                    at[i] = max(at[i], (at[i - 1] if i else 0.0) + m)
                for i in reversed(range(k)):
                    at[i] = min(at[i], (at[i + 1] if i < k - 1 else total) - m)
        else:
            steps = max(1, int(p.get("iterations", 5)))
            limit = float(p.get("maximum_shift", 2.0))
            at = list(here)
            for _ in range(steps):
                at = [s + max(-limit / steps, min(limit / steps, t - s)) for s, t in zip(at, even)]
        for n, s in zip(nodes, at):
            pt = line.interpolate(s)
            new[n] = (pt.x, pt.y)
            moved_by[n] = (pt.x - view.pos[n][0], pt.y - view.pos[n][1])
        cuts = [0.0] + at + [total]
        seq = [a] + nodes + [b]
        for i, eid in enumerate(edges):
            e = view.d.features[eid]
            if p.get("preserve_path", True):
                piece = shapely.ops.substring(line, cuts[i], cuts[i + 1])
                coords = list(piece.coords)
            else:
                coords = [line.interpolate(cuts[i]).coords[0], line.interpolate(cuts[i + 1]).coords[0]]
            if view.map(e.from_id) != seq[i]:
                coords.reverse()
            shapes[eid] = LineString(coords)
    for n in view.nodes:
        others = list(view.adjacent[n])
        if len(others) != 1:
            continue
        hub = others[0]
        x, y = new.get(n, view.pos[n])
        if p.get("move_leaves") and hub in moved_by:
            x, y = x + moved_by[hub][0], y + moved_by[hub][1]
        if p.get("expand_leaves"):
            hx, hy = new.get(hub, view.pos[hub])
            dist = math.hypot(x - hx, y - hy)
            want = float(p.get("expand_shift", 2.0))
            if 0 < dist < want:
                x, y = hx + (x - hx) * want / dist, hy + (y - hy) * want / dist
        if (x, y) != tuple(view.pos[n]):
            new[n] = (x, y)
    view.commit(new, shapes, margin=margin)


def spatial_dispatch(view, p, margin):
    """EQUIVALENT (force-scan): junctions nearer than the separation — the junction size times the maximum shift factor
    (INFERRED) — are pushed apart along the line joining them, half the shortfall each, once per iteration."""
    sep = JUNCTION_SIZE * float(p.get("maximum_shift_factor", 2.0))
    pos = {n: list(view.pos[n]) for n in view.nodes}
    for _ in range(max(1, int(p.get("iterations", 5)))):
        for i, a in enumerate(view.nodes):
            for b in view.nodes[i + 1:]:
                dx, dy = pos[b][0] - pos[a][0], pos[b][1] - pos[a][1]
                dist = math.hypot(dx, dy)
                if dist >= sep:
                    continue
                ux, uy = (dx / dist, dy / dist) if dist > 0 else (1.0, 0.0)
                push = (sep - dist) / 2
                pos[a][0] -= ux * push
                pos[a][1] -= uy * push
                pos[b][0] += ux * push
                pos[b][1] += uy * push
    view.commit({n: tuple(v) for n, v in pos.items()}, margin=margin)


def compression(view, p, margin):
    """EQUIVALENT: junction groups — junctions joined by an edge no longer than the grouping distance — each moved as
    one, the gaps between successive group positions on x and on y shortened to the grouping distance, the whole kept
    about its centre. ALL removes every edge vertex; OUTER keeps those of edges inside a group; OUTER_EXCEPT_FIRST also
    keeps the first vertex of an edge leaving a group (INFERRED)."""
    limit = float(p.get("grouping_distance", 20.0))
    parent = {n: n for n in view.nodes}

    def root(n):
        while parent[n] != n:
            parent[n] = parent[parent[n]]
            n = parent[n]
        return n

    for _, a, b in view.edges:
        if a != b and math.dist(view.pos[a], view.pos[b]) <= limit:
            parent[root(a)] = root(b)
    groups = defaultdict(list)
    for n in view.nodes:
        groups[root(n)].append(n)
    centre = {g: (sum(view.pos[n][0] for n in m) / len(m), sum(view.pos[n][1] for n in m) / len(m))
              for g, m in groups.items()}

    def squeeze(values):
        ordered = sorted(set(values))
        out, last, acc = {}, None, None
        for v in ordered:
            acc = v if last is None else acc + min(v - last, limit)
            out[v] = acc
            last = v
        shift = (ordered[0] + ordered[-1]) / 2 - (out[ordered[0]] + out[ordered[-1]]) / 2
        return {v: w + shift for v, w in out.items()}

    xs = squeeze([c[0] for c in centre.values()])
    ys = squeeze([c[1] for c in centre.values()])
    new = {}
    for g, members in groups.items():
        dx, dy = xs[centre[g][0]] - centre[g][0], ys[centre[g][1]] - centre[g][1]
        for n in members:
            new[n] = (view.pos[n][0] + dx, view.pos[n][1] + dy)
    rule = p.get("vertices_removal", "OUTER")
    shapes = {}
    for eid, a, b in view.edges:
        e = view.d.features[eid]
        coords = list(e.shape.coords)
        start, end = new[view.map(e.from_id)], new[view.map(e.to_id)]
        same = root(a) == root(b)
        if rule != "ALL" and same:
            dx, dy = start[0] - view.pos[a][0], start[1] - view.pos[a][1]
            shapes[eid] = LineString([start] + [(x + dx, y + dy) for x, y in coords[1:-1]] + [end])
        elif rule == "OUTER_EXCEPT_FIRST" and len(coords) > 2:
            dx, dy = start[0] - view.pos[a][0], start[1] - view.pos[a][1]
            shapes[eid] = LineString([start, (coords[1][0] + dx, coords[1][1] + dy), end])
        elif a != b:
            shapes[eid] = LineString([start, end])
    view.commit(new, shapes, margin=margin)


def angle_directed(view, p, margin):
    """Apply Angle Directed Layout: each edge, in feature id order, turned to the nearest of the allowed directions
    measured from its origin junction, its length kept, by moving its to junction (INFERRED, §5.9 (e)); a tie goes to
    the smaller angle. Repeated for each iteration; the edges are drawn straight."""
    count = {"TWELVE_DIRECTIONS": 12, "EIGHT_DIRECTIONS": 8, "FOUR_DIRECTIONS": 4}[p.get("directions",
                                                                                          "EIGHT_DIRECTIONS")]
    step = 2 * math.pi / count
    pos = {n: list(view.pos[n]) for n in view.nodes}
    for _ in range(max(1, int(p.get("iterations", 1)))):
        for eid, a, b in view.edges:
            if a == b:
                continue
            dx, dy = pos[b][0] - pos[a][0], pos[b][1] - pos[a][1]
            length = math.hypot(dx, dy)
            if length == 0:
                continue
            angle = math.atan2(dy, dx) % (2 * math.pi)
            k = math.floor(angle / step + 0.5 - 1e-12)
            target = (k % count) * step
            pos[b] = [pos[a][0] + length * math.cos(target), pos[a][1] + length * math.sin(target)]
    view.commit({n: tuple(v) for n, v in pos.items()}, mode="straight", margin=margin)


def force_directed(view, p, margin):
    """EQUIVALENT (Fruchterman and Reingold): the ideal length k is the edges' mean length now; each iteration every
    pair of junctions repels with repel_factor·k²/d and every edge attracts with d²/k, and a junction moves along its
    net force by no more than the temperature, which falls linearly from LOW 0.1, MEDIUM 0.5 or HIGH 1 times k
    (INFERRED, §5.9 (e)) to nothing."""
    pos = {n: list(view.pos[n]) for n in view.nodes}
    lengths = [math.dist(pos[a], pos[b]) for _, a, b in view.edges if a != b]
    k = (sum(lengths) / len(lengths)) if lengths and sum(lengths) > 0 else 2 * JUNCTION_SIZE
    repel = float(p.get("repel_factor", 1.0))
    freedom = {"LOW": 0.1, "MEDIUM": 0.5, "HIGH": 1.0}[p.get("degree_freedom", "LOW")]
    rounds = max(1, int(p.get("iterations", 20)))
    for r in range(rounds):
        heat = freedom * k * (1 - r / rounds)
        disp = {n: [0.0, 0.0] for n in view.nodes}
        for i, a in enumerate(view.nodes):
            for b in view.nodes[i + 1:]:
                dx, dy = pos[a][0] - pos[b][0], pos[a][1] - pos[b][1]
                dist = math.hypot(dx, dy)
                if dist == 0:
                    dx, dy, dist = 1e-6, 0.0, 1e-6
                force = repel * k * k / dist
                disp[a][0] += dx / dist * force
                disp[a][1] += dy / dist * force
                disp[b][0] -= dx / dist * force
                disp[b][1] -= dy / dist * force
        for _, a, b in view.edges:
            if a == b:
                continue
            dx, dy = pos[a][0] - pos[b][0], pos[a][1] - pos[b][1]
            dist = math.hypot(dx, dy)
            if dist == 0:
                continue
            force = dist * dist / k
            disp[a][0] -= dx / dist * force
            disp[a][1] -= dy / dist * force
            disp[b][0] += dx / dist * force
            disp[b][1] += dy / dist * force
        for n in view.nodes:
            size = math.hypot(*disp[n])
            if size > 1e-12:
                move = min(size, heat)
                pos[n][0] += disp[n][0] / size * move
                pos[n][1] += disp[n][1] / size * move
    new = {n: tuple(v) for n, v in pos.items()}
    shapes = _edges(view, new, None, p.get("edge_display", "REGULAR_EDGES"), p.get("breakpoint", 30.0), 0.0)
    view.commit(new, shapes, margin=margin)


# ---- trees ---------------------------------------------------------------------------------------------------------

FRAMES = {"FROM_LEFT_TO_RIGHT": ((1, 0), (0, -1)), "FROM_RIGHT_TO_LEFT": ((-1, 0), (0, -1)),
          "FROM_TOP_TO_BOTTOM": ((0, -1), (1, 0)), "FROM_BOTTOM_TO_TOP": ((0, 1), (1, 0))}


def _forest(view, comp, roots, order):
    """BFS trees over a component from its roots, children in ``order``: (roots, {node: [children]}, {node: parent})."""
    children, parent = defaultdict(list), {}
    seen = set(roots)
    queue = deque(roots)
    for r in roots:
        parent[r] = None
    while queue:
        a = queue.popleft()
        kids = sorted((b for b in view.adjacent[a] if b not in seen), key=lambda b: order(a, b))
        for b in kids:
            seen.add(b)
            parent[b] = a
            children[a].append(b)
            queue.append(b)
    return children, parent


def tidy(root, children, sibling, subtree):
    """Reingold–Tilford: {node: perpendicular offset from the root} and {node: depth}. Siblings are kept ``sibling``
    apart, deeper levels of neighbouring subtrees ``subtree`` apart."""
    order, stack = [], [root]
    while stack:
        v = stack.pop()
        order.append(v)
        stack.extend(children.get(v, []))
    rel, contour = {}, {}
    for v in reversed(order):
        kids = children.get(v, [])
        if not kids:
            rel[v], contour[v] = {v: 0.0}, {0: (0.0, 0.0)}
            continue
        positions, acc, shifts = {}, None, []
        for c in kids:
            cc = contour[c]
            if acc is None:
                shift = 0.0
            else:
                shift = max(acc[k][1] - cc[k][0] + (sibling if k == 0 else subtree) for k in cc if k in acc)
            for n, x in rel[c].items():
                positions[n] = x + shift
            if acc is None:
                acc = {k: (lo + shift, hi + shift) for k, (lo, hi) in cc.items()}
            else:
                for k, (lo, hi) in cc.items():
                    acc[k] = (min(acc[k][0], lo + shift), max(acc[k][1], hi + shift)) if k in acc else (lo + shift,
                                                                                                         hi + shift)
            shifts.append(shift)
            del rel[c], contour[c]
        middle = (shifts[0] + shifts[-1]) / 2
        rel[v] = {n: x - middle for n, x in positions.items()}
        rel[v][v] = 0.0
        contour[v] = {0: (0.0, 0.0)}
        contour[v].update({k + 1: (lo - middle, hi - middle) for k, (lo, hi) in acc.items()})
    depth = {root: 0}
    for v in order:
        for c in children.get(v, []):
            depth[c] = depth[v] + 1
    return rel[root], depth


def _order(view, p, frame):
    """Children ordered by their current place across the tree direction, or by an attribute (Smart Tree's
    order_tree_branches)."""
    u, v = frame

    def across(n):
        x, y = view.pos[n]
        return x * v[0] + y * v[1]

    if p.get("order_by_attribute") and p.get("order_attribute"):
        values = getattr(view, "values", {})
        sign = -1 if p.get("order_descending") else 1

        def by_value(parent, child):
            value = values.get((parent, child)) if p.get("order_on_edge") else values.get(child)
            return (value is None, sign * value if isinstance(value, (int, float)) else 0,
                    str(value) if not isinstance(value, (int, float)) else "", child)

        return by_value
    return lambda parent, child: (across(child), child)


def _roots(view, comp):
    roots = [n for n in comp if view.d.features[n].root]
    return roots or [min(comp)]


def _place_components(view, laid, gap, frame):
    """Components side by side across the tree direction, ``gap`` apart; the first root keeps its position."""
    u, v = frame
    new = {}
    origin = None
    edge = None
    for coords in laid:
        across = [p for _, p in coords.values()]
        lo, hi = min(across), max(across)
        shift = 0.0 if edge is None else edge + gap - lo
        if origin is None:
            first = next(iter(coords))
            origin = view.pos[first]
        for n, (a, p) in coords.items():
            new[n] = (origin[0] + a * u[0] + (p + shift) * v[0], origin[1] + a * u[1] + (p + shift) * v[1])
        edge = hi + shift
    return new


def _edges(view, new, parent, display, breakpoint, offset, frame=None):
    """Tree edges drawn from parent to child: REGULAR_EDGES straight; ORTHOGONAL_EDGES broken at ``breakpoint`` per
    cent along the tree direction (0: at the parent's along-position, 100: no break), siblings' crossing segments
    ``offset`` apart; CURVED_EDGES a cubic Bézier through the two inflexion points at N and 100 − N per cent, drawn as
    sixteen segments (INFERRED, §5.9 (e))."""
    shapes = {}
    u, v = frame or ((1, 0), (0, -1))
    rank = defaultdict(int)
    for eid, a, b in view.edges:
        if a == b:
            continue
        if parent is not None and parent.get(a) == b:
            a, b = b, a
        pa, pb = new.get(a, view.pos[a]), new.get(b, view.pos[b])
        e = view.d.features[eid]
        forward = view.map(e.from_id) == a
        if display == "ORTHOGONAL_EDGES" and parent is not None and float(breakpoint) < 100:
            aa = pa[0] * u[0] + pa[1] * u[1]
            ab = pb[0] * u[0] + pb[1] * u[1]
            pa_v = pa[0] * v[0] + pa[1] * v[1]
            pb_v = pb[0] * v[0] + pb[1] * v[1]
            at = aa + float(breakpoint) / 100 * (ab - aa) + offset * rank[a]
            rank[a] += 1

            def xy(along, across):
                return (along * u[0] + across * v[0], along * u[1] + across * v[1])

            coords = [pa, xy(at, pa_v), xy(at, pb_v), pb]
        elif display == "CURVED_EDGES":
            n = float(breakpoint) / 100
            aa, ab = pa[0] * u[0] + pa[1] * u[1], pb[0] * u[0] + pb[1] * u[1]
            va, vb = pa[0] * v[0] + pa[1] * v[1], pb[0] * v[0] + pb[1] * v[1]

            def at(along, across):
                return (along * u[0] + across * v[0], along * u[1] + across * v[1])

            c1, c2 = at(aa + n * (ab - aa), va), at(aa + (1 - n) * (ab - aa), vb)
            coords = []
            for k in range(17):
                t = k / 16
                coords.append(tuple((1 - t) ** 3 * s0 + 3 * (1 - t) ** 2 * t * s1 + 3 * (1 - t) * t * t * s2 + t ** 3 * s3
                                    for s0, s1, s2, s3 in zip(pa, c1, c2, pb)))
        else:
            coords = [pa, pb]
        cleaned = [coords[0]]
        for c in coords[1:]:
            if math.dist(c, cleaned[-1]) > 1e-12:
                cleaned.append(c)
        if len(cleaned) < 2:
            cleaned = [pa, pb]
        if not forward:
            cleaned.reverse()
        shapes[eid] = LineString(cleaned)
    return shapes


def _spacing(p, name, default):
    return float(p.get(name, default))


def _check_offset(p, *spacings):
    offset = float(p.get("offset", 0.0))
    if offset and offset > 0.1 * min(spacings):
        raise ExecuteError("ERROR 000800: the offset cannot exceed 10 per cent of the smallest spacing.")
    return offset


def smart_tree(view, p, margin):
    """EQUIVALENT (Reingold–Tilford): from the root junctions — or, where a connected part has none, its lowest
    feature id (INFERRED for "the smallest network topology index") — each part a tidy tree in the tree direction,
    levels ``along`` apart, siblings ``perpendicular`` apart and neighbouring subtrees ``subtree`` apart; several roots
    of one part side by side as subtrees; parts ``disjoined`` apart; the whole centred where the diagram's junctions
    were, as Pro 3.7 places it (measured 2026-10-31)."""
    frame = FRAMES[p.get("direction", "FROM_LEFT_TO_RIGHT")]
    along, perp = _spacing(p, "along", 2.0), _spacing(p, "perpendicular", 2.0)
    sub = _spacing(p, "subtree", 2.0)
    offset = _check_offset(p, along, perp, sub)
    order = _order(view, p, frame)
    laid, parents = [], {}
    for comp in view.components():
        roots = _roots(view, comp)
        children, parent = _forest(view, comp, roots, order)
        parents.update(parent)
        if len(roots) > 1:
            fake = ("root",)
            children[fake] = list(roots)
            rel, depth = tidy(fake, children, sub, sub)
            coords = {n: ((depth[n] - 1) * along, rel[n]) for n in rel if n != fake}
        else:
            rel, depth = tidy(roots[0], children, perp, sub)
            coords = {n: (depth[n] * along, rel[n]) for n in rel}
        first = roots[0]
        coords = {first: coords[first], **{n: c for n, c in coords.items() if n != first}}
        laid.append(coords)
    new = _centred(view, _place_components(view, laid, _spacing(p, "disjoined", 4.0), frame))
    shapes = _edges(view, new, parents, p.get("edge_display", "REGULAR_EDGES"), p.get("breakpoint", 50.0), offset,
                    frame)
    view.commit(new, shapes, margin=margin)


def _centred(view, new):
    """``new`` moved so that its extent has the centre the same junctions had before: ArcGIS Pro 3.7 lays a smart tree
    out round the diagram's centre, measured 2026-10-31 left to right and top to bottom (ADR-232 condition 5)."""
    if not new:
        return new
    def centre(points):
        xs, ys = [x for x, _ in points], [y for _, y in points]
        return (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2
    (bx, by), (ax, ay) = centre([view.pos[n] for n in new]), centre(list(new.values()))
    return {n: (x + bx - ax, y + by - ay) for n, (x, y) in new.items()}


def radial_tree(view, p, margin):
    """EQUIVALENT (Eades): the root at the centre — at its own position — and each level on a circle, the first of the
    initial radius and each next one further by the initial radius times the radius factor to the level's power
    (INFERRED); a subtree's wedge in proportion to its leaves, a junction at the middle of its wedge, angles from the
    east counterclockwise. Several roots stand on the first circle round their centroid. Connected parts are placed
    left to right ``disjoined`` apart."""
    r0 = _spacing(p, "initial_radius", 5.0)
    factor = float(p.get("radius_factor", 1.0))
    gap = _spacing(p, "disjoined", 10.0)
    new, edge = {}, None

    def radius(level):
        return sum(r0 * factor ** i for i in range(level))

    for comp in view.components():
        roots = _roots(view, comp)
        children, _ = _forest(view, comp, roots, lambda a, b: (b,))
        leaves = {}
        order, stack = [], list(roots)
        while stack:
            v = stack.pop()
            order.append(v)
            stack.extend(children.get(v, []))
        for v in reversed(order):
            leaves[v] = sum(leaves[c] for c in children.get(v, [])) or 1
        coords = {}
        if len(roots) == 1:
            centre = view.pos[roots[0]]
            coords[roots[0]] = (0.0, 0.0)
            tops, level0 = [(roots[0], 0.0, 2 * math.pi)], 0
        else:
            centre = (sum(view.pos[r][0] for r in roots) / len(roots), sum(view.pos[r][1] for r in roots) / len(roots))
            total = sum(leaves[r] for r in roots)
            lo, tops = 0.0, []
            for r in roots:
                span = 2 * math.pi * leaves[r] / total
                mid = lo + span / 2
                coords[r] = (radius(1) * math.cos(mid), radius(1) * math.sin(mid))
                tops.append((r, lo, lo + span))
                lo += span
            level0 = 1
        queue = deque((v, lo, hi, level0) for v, lo, hi in tops)
        while queue:
            v, lo, hi, level = queue.popleft()
            kids = children.get(v, [])
            start = lo
            for c in kids:
                span = (hi - lo) * leaves[c] / leaves[v]
                mid = start + span / 2
                r = radius(level + 1)
                coords[c] = (r * math.cos(mid), r * math.sin(mid))
                queue.append((c, start, start + span, level + 1))
                start += span
        xs = [x for x, _ in coords.values()]
        shift = 0.0 if edge is None else edge + gap - (centre[0] + min(xs))
        for n, (x, y) in coords.items():
            new[n] = (centre[0] + x + shift, centre[1] + y)
        edge = centre[0] + max(xs) + shift
    view.commit(new, _edges(view, new, None, "REGULAR_EDGES", 0, 0), margin=margin)


def mainline(root, children, along, perp, placement):
    """A mainline tree relative to its root: {node: (along, across)}. The main line is the longest branch from the
    root (most edges; a tie to the lower feature id), along the direction ``along`` apart; every other child of a main
    line junction starts a branch, a tidy tree growing across it, levels ``perp`` apart and siblings ``along`` apart,
    on the left (negative across), the right, or alternately both sides starting left (INFERRED); main line junctions
    are spaced so branches on one side never overlap."""
    depth, deepest = {root: 0}, root
    queue = deque([root])
    while queue:
        a = queue.popleft()
        for c in children.get(a, []):
            depth[c] = depth[a] + 1
            queue.append(c)
            if depth[c] > depth[deepest] or (depth[c] == depth[deepest] and c < deepest):
                deepest = c
    parent = {c: a for a, cs in children.items() for c in cs}
    line = [deepest]
    while line[-1] != root:
        line.append(parent[line[-1]])
    line.reverse()
    on_line = set(line)
    side_counter = 0
    branches = []
    for m in line:
        sides = {-1: [], 1: []}
        for c in children.get(m, []):
            if c in on_line:
                continue
            if placement == "LEFT_SIDE":
                side = -1
            elif placement == "RIGHT_SIDE":
                side = 1
            else:
                side = -1 if side_counter % 2 == 0 else 1
                side_counter += 1
            sides[side].append(c)
        laid = {}
        for side, kids in sides.items():
            if not kids:
                continue
            sub = dict(children)
            sub[m] = kids
            rel, dep = tidy(m, sub, along, along)
            laid[side] = {n: (rel[n], dep[n]) for n in rel if n != m}
        branches.append(laid)
    out, s, reach = {}, 0.0, {-1: None, 1: None}
    for i, m in enumerate(line):
        laid = branches[i]
        if i:
            s += along
            for side, nodes in laid.items():
                lo = min(q for q, _ in nodes.values())
                if reach[side] is not None:
                    s = max(s, reach[side] + along - lo)
        out[m] = (s, 0.0)
        for side, nodes in laid.items():
            for n, (q, dep) in nodes.items():
                out[n] = (s + q, side * dep * perp)
            hi = max(q for q, _ in nodes.values())
            reach[side] = max(reach[side] if reach[side] is not None else -math.inf, s + hi)
    return out


def mainline_tree(view, p, margin):
    """EQUIVALENT (Reingold–Tilford for the branches): each connected part a mainline tree from its root junction —
    or its lowest feature id — in the tree direction; parts ``disjoined`` apart across it."""
    frame = FRAMES[p.get("direction", "FROM_LEFT_TO_RIGHT")]
    along, perp = _spacing(p, "along", 2.0), _spacing(p, "perpendicular", 2.0)
    offset = _check_offset(p, along, perp)
    laid, parents = [], {}
    for comp in view.components():
        roots = _roots(view, comp)
        children, parent = _forest(view, comp, roots[:1], lambda a, b: (b,))
        parents.update(parent)
        coords = mainline(roots[0], children, along, perp, p.get("placement", "BOTH_SIDES"))
        laid.append({roots[0]: coords[roots[0]], **{n: c for n, c in coords.items() if n != roots[0]}})
    new = _place_components(view, laid, _spacing(p, "disjoined", 4.0), frame)
    shapes = _edges(view, new, parents, p.get("edge_display", "REGULAR_EDGES"), p.get("breakpoint", 50.0), offset,
                    frame)
    view.commit(new, shapes, margin=margin)


def longest_cycle(view, budget=200000):
    """The cycle of most junctions, by depth-first search from each junction in id order over higher ids only (each
    cycle met once from its lowest junction); past ``budget`` steps the longest found so far (INFERRED)."""
    best, steps = [], 0
    for start in view.nodes:
        stack = [(start, [start], {start})]
        while stack:
            a, path, seen = stack.pop()
            steps += 1
            if steps > budget:
                return best
            for b in sorted(view.adjacent[a], reverse=True):
                if b == start and len(path) >= 3 and len(path) > len(best):
                    best = list(path)
                elif b > start and b not in seen:
                    stack.append((b, path + [b], seen | {b}))
    return best


def main_ring(view, p, margin):
    """EQUIVALENT: the main ring is the cycle of most edges; its junctions are spaced evenly round an ellipse or a
    rectangle of the ring width and height, centred where they were, from the east counterclockwise starting at the
    ring's lowest feature id toward its lower-id neighbour (INFERRED); every other junction hangs from the ring
    junction it is reached from, as a tidy tree (SMART_TREE) or a mainline tree (BOTH_SIDES, LEFT_SIDE, RIGHT_SIDE)
    growing outward from the centre. A diagram with no loop is left as it is, as the page says."""
    ring = longest_cycle(view)
    if not ring:
        AddWarning("ApplyMainRingLayout: the diagram has no loop; the layout has no effect.")
        return
    start = ring.index(min(ring))
    ring = ring[start:] + ring[:start]
    if len(ring) > 2 and ring[-1] < ring[1]:
        ring = [ring[0]] + list(reversed(ring[1:]))
    n = len(ring)
    width, height = _spacing(p, "ring_width", 50.0), _spacing(p, "ring_height", 20.0)
    cx = sum(view.pos[r][0] for r in ring) / n
    cy = sum(view.pos[r][1] for r in ring) / n
    new = {}
    if p.get("ring_type", "ELLIPSE") == "ELLIPSE":
        for k, r in enumerate(ring):
            t = 2 * math.pi * k / n
            new[r] = (cx + width / 2 * math.cos(t), cy + height / 2 * math.sin(t))
    else:
        w, h = width / 2, height / 2
        corners = [(w, 0), (w, h), (-w, h), (-w, -h), (w, -h), (w, 0)]
        perimeter = 2 * (width + height)
        for k, r in enumerate(ring):
            target, walked = perimeter * k / n, 0.0
            for (x0, y0), (x1, y1) in zip(corners, corners[1:]):
                seg = math.hypot(x1 - x0, y1 - y0)
                if walked + seg >= target - 1e-12:
                    t = (target - walked) / seg if seg else 0
                    new[r] = (cx + x0 + t * (x1 - x0), cy + y0 + t * (y1 - y0))
                    break
                walked += seg
    along, perp = _spacing(p, "along", 2.0), _spacing(p, "perpendicular", 2.0)
    offset = _check_offset(p, along, perp)
    on_ring = set(ring)
    parents = {}
    for r in ring:
        children, parent = defaultdict(list), {}
        queue, seen = deque([r]), set(on_ring)
        while queue:
            a = queue.popleft()
            for b in sorted(view.adjacent[a]):
                if b not in seen and b not in new:
                    seen.add(b)
                    children[a].append(b)
                    parent[b] = a
                    queue.append(b)
        if not children:
            continue
        parents.update(parent)
        ux, uy = new[r][0] - cx, new[r][1] - cy
        size = math.hypot(ux, uy) or 1.0
        u, v = (ux / size, uy / size), (uy / size, -ux / size)
        kind = p.get("tree_type", "SMART_TREE")
        if kind == "SMART_TREE":
            rel, depth = tidy(r, children, perp, perp)
            coords = {m: (depth[m] * along, rel[m]) for m in rel}
        else:
            coords = mainline(r, children, along, perp, kind)
        for m, (a, q) in coords.items():
            if m != r:
                new[m] = (new[r][0] + a * u[0] + q * v[0], new[r][1] + a * u[1] + q * v[1])
    for m in view.nodes:
        new.setdefault(m, view.pos[m])
    shapes = _edges(view, new, parents, p.get("edge_display", "REGULAR_EDGES"), p.get("breakpoint", 50.0), offset)
    view.commit(new, shapes, margin=margin)


# ---- edges ---------------------------------------------------------------------------------------------------------

def _segments(view):
    out = []
    for eid, _, _ in view.edges:
        coords = list(view.d.features[eid].shape.coords)
        for k in range(len(coords) - 1):
            if math.dist(coords[k], coords[k + 1]) > 0:
                out.append((eid, k, coords[k], coords[k + 1]))
    return out


def partial_overlapping_edges(view, p, margin):
    """Apply Partial Overlapping Edges Layout: segments of different edges that run within ``buffer_width`` of each
    other — both ends of one within it of the other's line, within 10 degrees of parallel and overlapping along it
    (INFERRED) — form a set; a set of k is spread to offsets (i − (k − 1)/2)·offset about its middle, in their present
    order across it (or, OPTIMIZE_EDGES, in the order of their edges' far ends, INFERRED). A shifted segment that
    ends on a junction keeps the junction and gains a vertex; a vertex two shifted segments share takes their mean."""
    width, offset = float(p["buffer_width"]), float(p["offset"])
    segs = _segments(view)
    parent = list(range(len(segs)))

    def root(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def near(s, t):
        (a, b), (c, d) = (s[2], s[3]), (t[2], t[3])
        u = (b[0] - a[0], b[1] - a[1])
        w = (d[0] - c[0], d[1] - c[1])
        lu, lw = math.hypot(*u), math.hypot(*w)
        cos = abs(u[0] * w[0] + u[1] * w[1]) / (lu * lw)
        if cos < math.cos(math.radians(10)):
            return False
        far = LineString([(a[0] - u[0] * 1e6, a[1] - u[1] * 1e6), (b[0] + u[0] * 1e6, b[1] + u[1] * 1e6)])
        if far.distance(Point(c)) > width or far.distance(Point(d)) > width:
            return False
        proj = sorted([((c[0] - a[0]) * u[0] + (c[1] - a[1]) * u[1]) / lu,
                       ((d[0] - a[0]) * u[0] + (d[1] - a[1]) * u[1]) / lu])
        return min(proj[1], lu) - max(proj[0], 0.0) > 1e-9

    for i in range(len(segs)):
        for j in range(i + 1, len(segs)):
            if segs[i][0] != segs[j][0] and near(segs[i], segs[j]):
                parent[root(i)] = root(j)
    groups = defaultdict(list)
    for i in range(len(segs)):
        groups[root(i)].append(i)
    shift = defaultdict(list)
    for members in groups.values():
        if len({segs[i][0] for i in members}) < 2:
            continue
        a, b = segs[members[0]][2], segs[members[0]][3]
        lu = math.dist(a, b)
        nx, ny = -(b[1] - a[1]) / lu, (b[0] - a[0]) / lu

        def across(i):
            s = segs[i]
            return ((s[2][0] + s[3][0]) / 2 - a[0]) * nx + ((s[2][1] + s[3][1]) / 2 - a[1]) * ny

        if p.get("optimize"):
            def far(i):
                e = view.d.features[segs[i][0]]
                ends = [point_of(view.d.features[e.from_id].shape), point_of(view.d.features[e.to_id].shape)]
                return sum((q.x - a[0]) * nx + (q.y - a[1]) * ny for q in ends) / 2
            ordered = sorted(members, key=lambda i: (far(i), segs[i][0]))
        else:
            ordered = sorted(members, key=lambda i: (across(i), segs[i][0]))
        middle = sum(across(i) for i in members) / len(members)
        k = len(ordered)
        for rank, i in enumerate(ordered):
            target = middle + (rank - (k - 1) / 2) * offset
            delta = target - across(i)
            shift[(segs[i][0], segs[i][1])].append((nx * delta, ny * delta))
    shapes = {}
    for eid, _, _ in view.edges:
        coords = list(view.d.features[eid].shape.coords)
        moves = defaultdict(list)
        for k in range(len(coords) - 1):
            for m in shift.get((eid, k), []):
                moves[k].append(m)
                moves[k + 1].append(m)
        if not moves:
            continue
        out = []
        last = len(coords) - 1
        for k, c in enumerate(coords):
            if k in moves:
                mx = sum(m[0] for m in moves[k]) / len(moves[k])
                my = sum(m[1] for m in moves[k]) / len(moves[k])
                moved = (c[0] + mx, c[1] + my)
                if k == 0:
                    out += [c, moved]
                elif k == last:
                    out += [moved, c]
                else:
                    out.append(moved)
            else:
                out.append(c)
        shapes[eid] = LineString(out)
    view.commit({}, shapes, margin=margin)


def reshape_edges(view, p, margin):
    """Reshape Diagram Edges Layout's operations on edge vertices, as its page lists them; the choices it leaves open
    are INFERRED (§5.9 (e)):

    - REMOVE_VERTICES: every vertex removed;
    - SQUARE_EDGES: each segment (PRESERVE_PATH) or the edge (IGNORE_PATH) made of horizontal and vertical pieces, its
      break no further than ``breakpoint`` from where it starts along its longer axis; edges leaving one junction the
      same way break ``offset_between_segment`` apart;
    - SEPARATE_OVERLAPPING_EDGES: k edges between the same two junctions on one line get a middle vertex
      (i − (k − 1)/2)·shift across it;
    - REDUCE_VERTICES_BY_ANGLE: a vertex whose two segments meet at the threshold or wider is removed;
    - MARK_CROSSING_EDGES: where a horizontal and a vertical segment cross at a right angle, a half circle of the arc
      radius is drawn round the crossing on the segment the position names (default ABOVE_HORIZONTAL_SEGMENT)."""
    kind = p.get("reshape_type", "SQUARE_EDGES")
    d = view.d
    shapes = {}
    edges = [(eid, list(d.features[eid].shape.coords)) for eid, a, b in view.edges if a != b]
    if kind == "REMOVE_VERTICES":
        shapes = {eid: LineString([c[0], c[-1]]) for eid, c in edges}
    elif kind == "SQUARE_EDGES":
        brk = float(p.get("breakpoint", 8.66))
        gap = float(p.get("offset_between_segment", 5.0))
        used = defaultdict(int)
        for eid, coords in edges:
            pieces = coords if p.get("preserve_path", True) else [coords[0], coords[-1]]
            out = [pieces[0]]
            for k in range(len(pieces) - 1):
                (x0, y0), (x1, y1) = pieces[k], pieces[k + 1]
                dx, dy = x1 - x0, y1 - y0
                if abs(dx) < 1e-12 or abs(dy) < 1e-12:
                    out.append((x1, y1))
                    continue
                horizontal = abs(dx) >= abs(dy)
                way = (round(x0, 9), round(y0, 9), horizontal, dx > 0 if horizontal else dy > 0)
                extra = gap * used[way] if k == 0 else 0.0
                if k == 0:
                    used[way] += 1
                if horizontal:
                    s = math.copysign(min(brk + extra, abs(dx)), dx)
                    out += [(x0 + s, y0), (x0 + s, y1), (x1, y1)]
                else:
                    s = math.copysign(min(brk + extra, abs(dy)), dy)
                    out += [(x0, y0 + s), (x1, y0 + s), (x1, y1)]
            cleaned = [out[0]]
            for c in out[1:]:
                if math.dist(c, cleaned[-1]) > 1e-12:
                    cleaned.append(c)
            shapes[eid] = LineString(cleaned)
    elif kind == "SEPARATE_OVERLAPPING_EDGES":
        shift = float(p.get("shift_between_edge", 0.5))
        pairs = defaultdict(list)
        for eid, coords in edges:
            e = d.features[eid]
            pairs[tuple(sorted((e.from_id, e.to_id)))].append((eid, coords))
        for group in pairs.values():
            straight = [(eid, c) for eid, c in group if len(c) == 2]
            if len(straight) < 2:
                continue
            k = len(straight)
            a, b = straight[0][1][0], straight[0][1][-1]
            for rank, (eid, c) in enumerate(sorted(straight)):
                lu = math.dist(c[0], c[-1])
                nx, ny = -(c[-1][1] - c[0][1]) / lu, (c[-1][0] - c[0][0]) / lu
                if (c[0], c[-1]) != (a, b):
                    nx, ny = -nx, -ny
                m = (rank - (k - 1) / 2) * shift
                mid = ((c[0][0] + c[-1][0]) / 2 + nx * m, (c[0][1] + c[-1][1]) / 2 + ny * m)
                shapes[eid] = LineString([c[0], mid, c[-1]] if m else [c[0], c[-1]])
    elif kind == "REDUCE_VERTICES_BY_ANGLE":
        limit = float(p.get("angle_threshold", 160.0))
        for eid, coords in edges:
            out = [coords[0]]
            for k in range(1, len(coords) - 1):
                a, v, b = out[-1], coords[k], coords[k + 1]
                u1 = (a[0] - v[0], a[1] - v[1])
                u2 = (b[0] - v[0], b[1] - v[1])
                n1, n2 = math.hypot(*u1), math.hypot(*u2)
                if n1 == 0 or n2 == 0:
                    continue
                cos = max(-1.0, min(1.0, (u1[0] * u2[0] + u1[1] * u2[1]) / (n1 * n2)))
                if math.degrees(math.acos(cos)) < limit:
                    out.append(v)
            out.append(coords[-1])
            shapes[eid] = LineString(out)
    elif kind == "MARK_CROSSING_EDGES":
        radius = float(p.get("arc_radius", 5.0))
        position = p.get("arc_position", "ABOVE_HORIZONTAL_SEGMENT")
        current = {eid: list(c) for eid, c in edges}
        horizontal_marks = position in ("ABOVE_HORIZONTAL_SEGMENT", "BELOW_HORIZONTAL_SEGMENT")
        sign = 1 if position in ("ABOVE_HORIZONTAL_SEGMENT", "RIGHT_OF_VERTICAL_SEGMENT") else -1
        segs = [(eid, k, c[k], c[k + 1]) for eid, c in edges for k in range(len(c) - 1)]
        marks = defaultdict(list)
        for eid, k, a, b in segs:
            if abs(a[1] - b[1]) > 1e-12:
                continue
            for fid, m, c, e in segs:
                if fid == eid or abs(c[0] - e[0]) > 1e-12:
                    continue
                x, y = c[0], a[1]
                if min(a[0], b[0]) < x < max(a[0], b[0]) and min(c[1], e[1]) < y < max(c[1], e[1]):
                    if horizontal_marks:
                        marks[(eid, k)].append((x, y))
                    else:
                        marks[(fid, m)].append((x, y))
        for eid, coords in current.items():
            out = [coords[0]]
            for k in range(len(coords) - 1):
                a, b = coords[k], coords[k + 1]
                found = sorted(set(marks.get((eid, k), [])), key=lambda xy: math.dist(a, xy))
                for x, y in found:
                    if horizontal_marks:
                        way = 1 if b[0] > a[0] else -1
                        arc = [(x - way * radius * math.cos(math.pi * t / 8), y + sign * radius * math.sin(math.pi * t / 8))
                               for t in range(9)]
                    else:
                        way = 1 if b[1] > a[1] else -1
                        arc = [(x + sign * radius * math.sin(math.pi * t / 8), y - way * radius * math.cos(math.pi * t / 8))
                               for t in range(9)]
                    out += arc
                out.append(b)
            if len(out) != len(coords):
                shapes[eid] = LineString(out)
    view.commit({}, shapes, margin=margin)
