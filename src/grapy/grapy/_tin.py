"""TINs held in a GeoPackage, triangulated with Delaunay conforming to their breaklines — ADR-229.

A TIN in ArcGIS is a folder of Esri's binary files, whose format is not published. grapy's TIN is its own tables in a
GeoPackage, registered in ``gpkg_extensions`` as the ``grapy_tin`` extension (INFERRED, ADR-229 §5.2):

- ``grapy_tin``: name, spatial reference, whether it is constrained (built CONSTRAINED_DELAUNAY, since 2026-10-31), the data
  area delineation last applied, and when it was built.
- ``grapy_tin_input``: each surface feature added by CreateTin, EditTin or LasDatasetToTin, in order: its surface
  feature type, tag, constant height, and its geometry with z resolved. The triangulation is derived from these, so
  EditTin re-triangulates them all.
- ``grapy_tin_node``: the nodes: x, y, z, tag, and origin (0 from the input, 1 a densification point added to keep a
  breakline conforming Delaunay).
- ``grapy_tin_triangle``: three nodes, a tag, and whether the triangle is in the data area (interpolation zone).
- ``grapy_tin_edge``: the enforced edges, the pieces of breaklines and polygon boundaries: 1 soft, 2 hard.

A path names a TIN two ways: ``<file>.gpkg/<name>``, or a folder path as a script written for ArcGIS Pro gives one, in
which case the TIN is ``<folder>/tin.gpkg`` and named after the folder (INFERRED).

Triangulation is Delaunay (``scipy.spatial.Delaunay``, Qhull). Breaklines are made conforming as the documentation
describes — "Steiner, or densification, points are added as needed to maintain conformance to Delaunay criteria" —
by splitting a segment that is not an edge of the triangulation at its midpoint and triangulating again (INFERRED: the
documentation does not say where a densification point goes). CONSTRAINED_DELAUNAY is grapy's own implementation of
Sloan's (1993) edge-swapping algorithm over the same Qhull triangulation (``_constrain``; ADR-229 condition 6): each
breakline segment becomes one edge and no point is added.
"""
from __future__ import annotations

import datetime
import math
import os

import numpy as np
import shapely

from . import _gpkg
from ._runtime import AddWarning, ExecuteError, NotBuiltError, env
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

EXTENSION = "grapy_tin"
DEFINITION = "Graticula ADR-229: grapy's TINs"
TABLES = ("grapy_tin", "grapy_tin_input", "grapy_tin_node", "grapy_tin_triangle", "grapy_tin_edge")
LAYERS: dict = {}   # MakeTinLayer: layer name -> TIN path

_TYPES = {"masspoints": (None, 0), "hardline": ("line", 2), "softline": ("line", 1), "hardclip": ("clip", 2),
          "softclip": ("clip", 1), "harderase": ("erase", 2), "softerase": ("erase", 1), "hardreplace": ("replace", 2),
          "softreplace": ("replace", 1), "hardvaluefill": ("valuefill", 2), "softvaluefill": ("valuefill", 1),
          "anchorpoints": (None, 0), "extentclip": ("clip", 0)}   # extentclip: LasDatasetToTin's CLIP, not enforced
_MAX_ROUNDS = 200


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


# ---- where a TIN is


def locate(path):
    """(file, name) of a TIN path or a MakeTinLayer layer name."""
    path = LAYERS.get(str(path), path)
    text = os.fspath(path) if isinstance(path, os.PathLike) else str(path)
    if ".gpkg" in text.lower():
        file, table = _gpkg.split(text)
        if not table:
            raise ExecuteError(f"ERROR 000732: '{text}' names no TIN: write <file>.gpkg/<tin> or a folder.")
        return file, table
    if not os.path.isabs(text):
        text = os.path.join(env.workspace, text)
    return os.path.join(text, "tin.gpkg"), os.path.basename(os.path.normpath(text))


def ensure(connection):
    from ._topology import register
    connection.execute("""create table if not exists grapy_tin (name text primary key, srid integer not null,
        constrained integer not null default 0, delineation text, built_at text)""")
    connection.execute("""create table if not exists grapy_tin_input (tin text not null, seq integer not null,
        sf_type text not null, tag integer not null default 0, height double, pending integer not null default 0,
        shape blob not null)""")
    connection.execute("""create table if not exists grapy_tin_node (tin text not null, node_id integer not null,
        x double not null, y double not null, z double not null, tag integer not null default 0,
        origin integer not null default 0)""")
    connection.execute("""create table if not exists grapy_tin_triangle (tin text not null, triangle_id integer not null,
        n1 integer not null, n2 integer not null, n3 integer not null, tag integer not null default 0,
        inside integer not null default 1)""")
    connection.execute("""create table if not exists grapy_tin_edge (tin text not null, n1 integer not null,
        n2 integer not null, type integer not null)""")
    register(connection, TABLES, EXTENSION, DEFINITION)


def exists(path):
    try:
        file, name = locate(path)
    except (ExecuteError, ValueError):
        return False
    if not os.path.exists(file):
        return False
    connection = _gpkg.connect(file)
    try:
        if not connection.execute("select 1 from sqlite_master where name = 'grapy_tin'").fetchone():
            return False
        return connection.execute("select 1 from grapy_tin where name = ?", (name,)).fetchone() is not None
    finally:
        connection.close()


# ---- the surface: nodes, triangles, data area


class Surface:
    """A triangulated surface: nodes (n, 3), triangles (k, 3) of node indexes, the data area, tags and enforced edges."""

    def __init__(self, nodes, triangles, inside=None, triangle_tags=None, node_tags=None, origin=None, edges=None,
                 srid=0, constrained=False):
        self.nodes = np.asarray(nodes, dtype=np.float64).reshape(-1, 3)
        self.triangles = np.asarray(triangles, dtype=np.int64).reshape(-1, 3)
        k, n = len(self.triangles), len(self.nodes)
        self.inside = np.ones(k, bool) if inside is None else np.asarray(inside, bool)
        self.triangle_tags = np.zeros(k, np.int64) if triangle_tags is None else np.asarray(triangle_tags, np.int64)
        self.node_tags = np.zeros(n, np.int64) if node_tags is None else np.asarray(node_tags, np.int64)
        self.origin = np.zeros(n, np.int64) if origin is None else np.asarray(origin, np.int64)
        self.edges = dict(edges or {})   # (i, j) with i < j -> 1 soft, 2 hard
        self.srid = int(srid or 0)
        self.constrained = bool(constrained)   # built CONSTRAINED_DELAUNAY: not Delaunay across an enforced edge
        self._tree = None

    # geometry of the triangles

    def corners(self, which=None):
        t = self.triangles if which is None else self.triangles[which]
        return self.nodes[t[:, 0]], self.nodes[t[:, 1]], self.nodes[t[:, 2]]

    def planes(self, z_factor=1.0):
        """(a, b, c) of each triangle's plane z = a x + b y + c, z scaled by ``z_factor``."""
        p, q, r = self.corners()
        zp, zq, zr = p[:, 2] * z_factor, q[:, 2] * z_factor, r[:, 2] * z_factor
        det = (q[:, 0] - p[:, 0]) * (r[:, 1] - p[:, 1]) - (r[:, 0] - p[:, 0]) * (q[:, 1] - p[:, 1])
        det = np.where(det == 0, np.nan, det)
        a = ((zq - zp) * (r[:, 1] - p[:, 1]) - (zr - zp) * (q[:, 1] - p[:, 1])) / det
        b = ((q[:, 0] - p[:, 0]) * (zr - zp) - (r[:, 0] - p[:, 0]) * (zq - zp)) / det
        c = zp - a * p[:, 0] - b * p[:, 1]
        return a, b, c

    def polygons(self, which=None):
        p, q, r = self.corners(which)
        rings = np.stack([p[:, :2], q[:, :2], r[:, :2], p[:, :2]], axis=1)
        return shapely.polygons(rings)

    def tree(self):
        if self._tree is None:
            self._tree = shapely.STRtree(self.polygons())
        return self._tree

    def locate(self, xs, ys):
        """The data-area triangle holding each point, or −1."""
        xs, ys = np.asarray(xs, float).ravel(), np.asarray(ys, float).ravel()
        found = np.full(len(xs), -1, np.int64)
        if not len(self.triangles) or not len(xs):
            return found
        points = shapely.points(xs, ys)
        pairs = self.tree().query(points, predicate="intersects")
        keep = self.inside[pairs[1]]
        point_of, triangle_of = pairs[0][keep], pairs[1][keep]
        # Of several triangles sharing an edge or node, the first found answers: their planes agree there.
        order = np.argsort(point_of, kind="stable")
        point_of, triangle_of = point_of[order], triangle_of[order]
        first = np.unique(point_of, return_index=True)[1]
        found[point_of[first]] = triangle_of[first]
        return found

    def z_at(self, xs, ys, z_factor=1.0):
        """Linear interpolation on the triangle's plane ("Linear interpolation views TIN triangles as planes"); NaN off
        the data area."""
        xs, ys = np.asarray(xs, float).ravel(), np.asarray(ys, float).ravel()
        which = self.locate(xs, ys)
        out = np.full(len(xs), np.nan)
        hit = which >= 0
        if hit.any():
            a, b, c = self.planes(z_factor)
            out[hit] = a[which[hit]] * xs[hit] + b[which[hit]] * ys[hit] + c[which[hit]]
        return out

    def all_edges(self):
        """{(i, j): [triangles]} of every edge."""
        found: dict = {}
        for t, (p, q, r) in enumerate(self.triangles):
            for i, j in ((p, q), (q, r), (r, p)):
                found.setdefault((min(i, j), max(i, j)), []).append(t)
        return found

    def domain(self):
        """The data area as a (multi)polygon."""
        if not self.inside.any():
            return shapely.Polygon()
        return shapely.union_all(self.polygons(np.nonzero(self.inside)[0]))

    def extent(self):
        used = np.unique(self.triangles) if len(self.triangles) else np.arange(len(self.nodes))
        xy = self.nodes[used, :2] if len(used) else np.zeros((0, 2))
        if not len(xy):
            return None
        return float(xy[:, 0].min()), float(xy[:, 1].min()), float(xy[:, 0].max()), float(xy[:, 1].max())


# ---- triangulation conforming to breaklines


def _delaunay(xy):
    from scipy.spatial import Delaunay, QhullError
    if len(xy) < 3:
        return np.zeros((0, 3), np.int64)
    try:
        # From the points' own corner: Qhull on projected coordinates (5e5, 4.4e6) drops points as coplanar — 21 of 2,500
        # on a 0.01 m grid, measured against ArcGIS Pro 2026-10-31 — and the triangulation loses them silently.
        found = Delaunay(xy - xy.min(axis=0))
    except QhullError:  # every point on one line: no triangle
        return np.zeros((0, 3), np.int64)
    return found.simplices.astype(np.int64)


def _constrain(xy, triangles, segments):
    """A constrained Delaunay triangulation: ``triangles`` (a Delaunay triangulation of ``xy``) with every segment
    ``(i, j)`` made an edge, by Sloan's edge-swapping algorithm — S. W. Sloan, *A fast algorithm for generating
    constrained Delaunay triangulations*, Computers & Structures 47 (3), 1993, pp. 441–450 — and the Delaunay criterion
    then restored by Lawson's swaps everywhere except across a constrained edge. No point is added: segments must
    already be split where they cross and where a node lies on them (``_split_segments``), and a node found on a
    segment's open interior splits it here. Returns the triangles, each anticlockwise."""
    from collections import deque
    xy = np.asarray(xy, np.float64) - np.asarray(xy, np.float64).min(axis=0)
    span = float(max(np.ptp(xy[:, 0]), np.ptp(xy[:, 1]), 1.0))
    eps = span * span * 1e-14

    def orient(a, b, c):
        return (xy[b, 0] - xy[a, 0]) * (xy[c, 1] - xy[a, 1]) - (xy[b, 1] - xy[a, 1]) * (xy[c, 0] - xy[a, 0])

    tris = []
    for a, b, c in np.asarray(triangles, np.int64).tolist():
        tris.append([a, b, c] if orient(a, b, c) > 0 else [a, c, b])
    owner = {}   # directed edge (a, b) -> the triangle in which a -> b runs anticlockwise
    for t, (a, b, c) in enumerate(tris):
        owner[(a, b)], owner[(b, c)], owner[(c, a)] = t, t, t

    def third(t, a, b):
        return next(v for v in tris[t] if v != a and v != b)

    def crosses(a, b, u, v):
        """Do the open segments ab and uv cross at one interior point?"""
        if len({a, b, u, v}) < 4:
            return False
        return (orient(u, v, a) * orient(u, v, b) < -eps * eps) and (orient(a, b, u) * orient(a, b, v) < -eps * eps)

    def flip(a, b):
        t1, t2 = owner.pop((a, b)), owner.pop((b, a))
        c, d = third(t1, a, b), third(t2, b, a)
        tris[t1], tris[t2] = [a, d, c], [d, b, c]
        for t in (t1, t2):
            p, q, r = tris[t]
            owner[(p, q)], owner[(q, r)], owner[(r, p)] = t, t, t
        return c, d

    locked = set()
    work = deque((int(i), int(j)) for i, j in segments if i != j)
    while work:
        u, v = work.popleft()
        if (u, v) in owner or (v, u) in owner:
            locked.add((min(u, v), max(u, v)))
            continue
        # A node on the segment's open interior splits it.
        d = xy[v] - xy[u]
        length2 = float(d @ d)
        rel = xy - xy[u]
        along = rel @ d
        off = rel[:, 0] * d[1] - rel[:, 1] * d[0]
        on = np.nonzero((np.abs(off) <= 1e-9 * length2) & (along > 1e-9 * length2) & (along < length2 * (1 - 1e-9)))[0]
        if len(on):
            chain = [u, *on[np.argsort(along[on])].tolist(), v]
            work.extendleft(reversed(list(zip(chain[:-1], chain[1:]))))
            continue
        crossing = deque((a, b) for (a, b) in owner if a < b and (b, a) in owner and crosses(a, b, u, v))
        rounds, limit = 0, 50 * (len(crossing) + 1) ** 2
        while crossing:
            rounds += 1
            if rounds > limit:
                raise ExecuteError("ERROR: a breakline could not be inserted into the constrained Delaunay triangulation.")
            a, b = crossing.popleft()
            if (a, b) not in owner:
                a, b = b, a
            c, dd = third(owner[(a, b)], a, b), third(owner[(b, a)], b, a)
            # Swappable only when the quadrilateral a, d, b, c is strictly convex.
            if not (orient(c, dd, a) * orient(c, dd, b) < 0):
                crossing.append((a, b))
                continue
            c, dd = flip(a, b)
            if crosses(c, dd, u, v):
                crossing.append((c, dd))
        locked.add((min(u, v), max(u, v)))
    # Lawson's swaps restore the Delaunay criterion everywhere a constrained edge does not forbid it.
    queue = deque((a, b) for (a, b) in list(owner) if a < b)
    guard = 0
    while queue:
        guard += 1
        if guard > 100 * (len(owner) + 10):
            break
        a, b = queue.popleft()
        if (a, b) not in owner or (b, a) not in owner or (min(a, b), max(a, b)) in locked:
            continue
        c, dd = third(owner[(a, b)], a, b), third(owner[(b, a)], b, a)
        ax, ay = xy[a] - xy[dd]
        bx, by = xy[b] - xy[dd]
        cx, cy = xy[c] - xy[dd]
        incircle = ((ax * ax + ay * ay) * (bx * cy - cx * by) - (bx * bx + by * by) * (ax * cy - cx * ay)
                    + (cx * cx + cy * cy) * (ax * by - bx * ay))
        if incircle > eps * span * span and orient(c, dd, a) * orient(c, dd, b) < 0:
            flip(a, b)
            queue.extend([(a, dd), (dd, b), (b, c), (c, a)])
    return np.array(tris, np.int64).reshape(-1, 3), locked


def _split_segments(points, segments, tolerance):
    """Segments split where they cross each other and where a node lies on them, so that every crossing is a node."""
    from shapely import STRtree
    changed = True
    rounds = 0
    while changed and rounds < 10:
        changed, rounds = False, rounds + 1
        xy = np.array([p[:2] for p in points]) if points else np.zeros((0, 2))
        lines = shapely.linestrings([[points[a][:2], points[b][:2]] for a, b, _ in segments]) if segments else []
        cuts: dict = {}
        if len(lines):
            tree = STRtree(lines)
            # nodes on a segment's interior
            node_pairs = tree.query(shapely.points(xy), predicate="dwithin", distance=tolerance)
            for n, s in zip(*node_pairs):
                a, b, _ = segments[s]
                if n in (a, b):
                    continue
                pa, pb = np.array(points[a][:2]), np.array(points[b][:2])
                d = pb - pa
                t = float(np.dot(xy[n] - pa, d) / np.dot(d, d))
                if 0 < t < 1:
                    cuts.setdefault(s, []).append((t, n))
            # crossings of two segments
            left, right = tree.query(lines, predicate="intersects")
            for s1, s2 in zip(left, right):
                if s1 >= s2:
                    continue
                a, b, _ = segments[s1]
                c, d, _ = segments[s2]
                if len({a, b, c, d}) < 4:
                    continue
                p, r = np.array(points[a][:2]), np.array(points[b][:2]) - np.array(points[a][:2])
                q, s = np.array(points[c][:2]), np.array(points[d][:2]) - np.array(points[c][:2])
                cross = r[0] * s[1] - r[1] * s[0]
                if cross == 0:
                    continue
                t = ((q - p)[0] * s[1] - (q - p)[1] * s[0]) / cross
                u = ((q - p)[0] * r[1] - (q - p)[1] * r[0]) / cross
                if 0 < t < 1 and 0 < u < 1:
                    # INFERRED: the crossing takes the z of the segment given first (hard lines are given before soft).
                    za, zb = points[a][2], points[b][2]
                    x, y = p + t * r
                    points.append((float(x), float(y), float(za + t * (zb - za))))
                    n = len(points) - 1
                    cuts.setdefault(s1, []).append((t, n))
                    cuts.setdefault(s2, []).append((u, n))
        if cuts:
            changed = True
            out = []
            for s, (a, b, kind) in enumerate(segments):
                chain = [a, *[n for _, n in sorted(cuts.get(s, []))], b]
                out.extend((p, q, kind) for p, q in zip(chain[:-1], chain[1:]) if p != q)
            segments[:] = out
    return segments


def triangulate(points, segments, polygons=(), srid=0, delineation=None, constrained=False):
    """A Surface from mass points ``[(x, y, z)]``, breakline segments ``[((x, y, z), (x, y, z), 1|2)]`` and polygons
    ``[(kind, shapely polygon, tag, height)]`` (kind ``clip``, ``erase``, ``replace`` or ``valuefill``), their boundaries
    already among the segments. ``constrained``: CONSTRAINED_DELAUNAY — each segment one edge, no densification point
    added (``_constrain``); otherwise Delaunay conforming to the segments by densification."""
    nodes: list = []
    index: dict = {}
    origin: list = []

    def add(p, from_input=True):
        key = (float(p[0]), float(p[1]))
        if key in index:
            # INFERRED: of two inputs at the same x,y the later one's z is kept.
            nodes[index[key]] = (key[0], key[1], float(p[2]))
            return index[key]
        nodes.append((key[0], key[1], float(p[2])))
        origin.append(0 if from_input else 1)
        index[key] = len(nodes) - 1
        return index[key]

    for p in points:
        add(p)
    pieces = []
    for p, q, kind in segments:
        a, b = add(p), add(q)
        if a != b:
            pieces.append((a, b, kind))
    if not nodes:
        return Surface(np.zeros((0, 3)), np.zeros((0, 3)), srid=srid)
    xy = np.array([n[:2] for n in nodes])
    span = float(max(np.ptp(xy[:, 0]), np.ptp(xy[:, 1]), 1.0))
    before = len(nodes)
    pieces = _split_segments(nodes, pieces, span * 1e-9)
    origin.extend([1] * (len(nodes) - before))
    for i in range(before, len(nodes)):
        index[(nodes[i][0], nodes[i][1])] = i
    if constrained:
        triangles, _ = _constrain(np.array([n[:2] for n in nodes]), _delaunay(np.array([n[:2] for n in nodes])),
                                  [(a, b) for a, b, _ in pieces])
        surface = Surface(nodes, triangles, origin=origin, srid=srid, constrained=True)
        for a, b, kind in pieces:
            key = (min(a, b), max(a, b))
            surface.edges[key] = max(kind, surface.edges.get(key, 0))
        _apply_polygons(surface, polygons)
        if delineation:
            delineate(surface, *delineation)
        return surface
    limit = max(10 * len(nodes), 1000)
    for _ in range(_MAX_ROUNDS):
        triangles = _delaunay(np.array([n[:2] for n in nodes]))
        have = {(min(i, j), max(i, j)) for t in triangles for i, j in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0]))}
        missing = [s for s in pieces if (min(s[0], s[1]), max(s[0], s[1])) not in have]
        if not missing:
            break
        if len(nodes) > limit:
            raise ExecuteError("ERROR: the breaklines could not be made conforming Delaunay without more densification "
                               "points than grapy allows (segments meeting at very small angles); CONSTRAINED_DELAUNAY "
                               "adds each segment as one edge without them.")
        keep = [s for s in pieces if s not in missing]
        for a, b, kind in missing:
            pa, pb = nodes[a], nodes[b]
            m = add(((pa[0] + pb[0]) / 2, (pa[1] + pb[1]) / 2, (pa[2] + pb[2]) / 2), from_input=False)
            keep += [(a, m, kind), (m, b, kind)]
        pieces = keep
    else:
        raise ExecuteError("ERROR: the breaklines could not be made conforming Delaunay (ADR-229 §5.3).")
    surface = Surface(nodes, triangles, origin=origin, srid=srid)
    for a, b, kind in pieces:
        key = (min(a, b), max(a, b))
        surface.edges[key] = max(kind, surface.edges.get(key, 0))
    _apply_polygons(surface, polygons)
    if delineation:
        delineate(surface, *delineation)
    return surface


def _apply_polygons(surface, polygons):
    """Clip, erase, replace and value fill, as the documentation defines them: triangles outside the clip polygons and
    inside the erase polygons are masked; nodes inside a replace polygon take its constant height; triangles and nodes
    inside a value fill polygon take its tag. INFERRED: a triangle is inside a polygon when its centroid is; all clip
    polygons are applied before any erase polygon; replace and value fill apply in the order given, later ones winning."""
    if not len(surface.triangles) or not polygons:
        return
    p, q, r = surface.corners()
    centroids = shapely.points((p[:, 0] + q[:, 0] + r[:, 0]) / 3, (p[:, 1] + q[:, 1] + r[:, 1]) / 3)
    clips = [g for kind, g, _, _ in polygons if kind == "clip"]
    if clips:
        surface.inside &= shapely.within(centroids, shapely.union_all(clips))
    for kind, g, _, _ in polygons:
        if kind == "erase":
            surface.inside &= ~shapely.within(centroids, g)
    node_points = shapely.points(surface.nodes[:, 0], surface.nodes[:, 1])
    for kind, g, tag, height in polygons:
        if kind == "replace" and height is not None:
            surface.nodes[shapely.covers(g, node_points), 2] = float(height)
        elif kind == "valuefill":
            surface.triangle_tags[shapely.within(centroids, g)] = int(tag)
            surface.node_tags[shapely.covers(g, node_points)] = int(tag)


def delineate(surface, max_edge_length, method="PERIMETER_ONLY"):
    """DelineateTinDataArea: every triangle back in the data area, then, with ALL, each triangle with an edge longer than
    the maximum masked; with PERIMETER_ONLY, triangles masked from the outside inward while a boundary edge exceeds it,
    as documented. "Exceed" is read as strictly longer (INFERRED)."""
    limit = float(max_edge_length)
    surface.inside[:] = True
    if not len(surface.triangles):
        return surface
    p, q, r = surface.corners()

    def lengths(u, v):
        return np.hypot(u[:, 0] - v[:, 0], u[:, 1] - v[:, 1])
    if str(method or "PERIMETER_ONLY").upper() == "ALL":
        surface.inside &= ~((lengths(p, q) > limit) | (lengths(q, r) > limit) | (lengths(r, p) > limit))
        return surface
    edges = surface.all_edges()
    xy = surface.nodes[:, :2]
    while True:
        masked = set()
        for (i, j), ts in edges.items():
            live = [t for t in ts if surface.inside[t]]
            if len(live) == 1:
                if math.hypot(*(xy[i] - xy[j])) > limit:
                    masked.add(live[0])
        if not masked:
            return surface
        surface.inside[list(masked)] = False


# ---- inputs: feature classes as surface features


def rows_of(value, columns):
    """A value table parameter as rows of ``columns`` strings: a ValueTable, a list of lists or strings, or a string
    with ``;`` between rows and spaces between values."""
    from ._parameter import ValueTable, _tokens
    if not _is_given(value):
        return []
    if isinstance(value, ValueTable):
        value = [[value.getValue(r, c) for c in range(value.columnCount)] for r in range(value.rowCount)]
    if isinstance(value, str):
        value = [v for v in value.split(";") if v.strip()]
    out = []
    for row in value:
        cells = list(row) if isinstance(row, (list, tuple)) else _tokens(str(row).strip())
        cells = [("" if c is None else (os.fspath(c) if isinstance(c, os.PathLike) else c)) for c in cells]
        out.append(cells + [""] * (columns - len(cells)))
    return out


def sf_type(value, tool):
    key = str(value or "masspoints").lower().replace("_", "").replace(" ", "")
    if key not in _TYPES:
        _refuse(tool, "SF_type", value)
    return key


def read_features(path, height_field, sf, tag_field, tool, use_z=True):
    """[(shape with z, pending, height, tag)] of a feature class added as surface feature type ``sf``. ``pending`` is
    True when the height is ``<None>``: interpolated from the surface built without it, as documented."""
    from . import da
    from .analysis import _schema
    columns, kind, srid, _ = _schema(path)
    names = {c.name.lower(): c.name for c in columns}
    connection, table = _gpkg.table_of(path)
    try:
        has_z, has_m = _gpkg.dimensions(connection, table)
    finally:
        connection.close()
    field = str(height_field or "").strip()
    if field.upper() in ("", "#"):
        field = "Shape.Z" if has_z else "<None>"
    if not use_z and field.upper() == "SHAPE.Z":
        field = "Shape.M"
    wanted = []
    if field.upper() not in ("SHAPE.Z", "SHAPE.M", "<NONE>", "NONE"):
        if field.lower() not in names:
            raise ExecuteError(f"ERROR 000728: '{height_field}' is not a field of {path}.")
        wanted.append(names[field.lower()])
    tag_name = None
    if tag_field not in (None, "", "#", "<None>", "<NONE>") and not str(tag_field).lstrip("-").isdigit():
        if str(tag_field).lower() not in names:
            raise ExecuteError(f"ERROR 000728: '{tag_field}' is not a field of {path}.")
        tag_name = names[str(tag_field).lower()]
        wanted.append(tag_name)
    if field.upper() == "SHAPE.Z" and not has_z:
        raise ExecuteError(f"ERROR: {path} has no z-values for its height field Shape.Z.")
    if field.upper() == "SHAPE.M" and not has_m:
        raise ExecuteError(f"ERROR: {path} has no m-values for its height field Shape.M.")
    out = []
    with da.SearchCursor(path, [*wanted, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None:
                continue
            shape = row[-1]._shape
            values = dict(zip(wanted, row[:-1]))
            tag = int(values[tag_name] or 0) if tag_name else (int(tag_field) if str(tag_field).lstrip("-").isdigit()
                                                                else 0)
            pending, height = False, None
            if field.upper() == "SHAPE.Z":
                shaped = shape
            elif field.upper() == "SHAPE.M":
                shaped = _with_m_as_z(shape)
            elif field.upper() in ("<NONE>", "NONE"):
                shaped, pending = _gpkg.rebuild(shape, True, False), True
            else:
                if values[wanted[0]] is None:
                    continue
                height = float(values[wanted[0]])
                shaped = _gpkg.rebuild(shape, True, False, None, lambda z, xy, h=height: np.full(len(xy), h))
            out.append((shaped, pending, height, tag))
    return out, srid, kind


def _with_m_as_z(shape):
    """The shape with each vertex's m as its z."""
    from shapely import wkt
    text = shapely.to_wkt(_gpkg.rebuild(shape, False, True), rounding_precision=-1)
    return wkt.loads(text.replace(" M ", " Z ").replace(" M(", " Z(").replace("M (", "Z ("))


def _parts(shape):
    """Each part: ('point', xyz) / ('line', (n, 3)) / ('polygon', shapely polygon, [rings (n, 3)])."""
    out = []
    for part in getattr(shape, "geoms", [shape]):
        kind = part.geom_type
        if kind == "Point":
            out.append(("point", shapely.get_coordinates(part, include_z=True)))
        elif kind in ("LineString", "LinearRing"):
            out.append(("line", shapely.get_coordinates(part, include_z=True)))
        elif kind == "Polygon":
            rings = [shapely.get_coordinates(r, include_z=True) for r in (part.exterior, *part.interiors)]
            out.append(("polygon", shapely.force_2d(part), rings))
        else:
            out.extend(_parts(part))
    return out


def surface_inputs(inputs):
    """(points, segments, polygons) of stored inputs ``[(sf_type, shape, tag, height)]``."""
    points, segments, polygons = [], [], []
    for sf, shape, tag, height in inputs:
        role, hardness = _TYPES[sf]
        for part in _parts(shape):
            if part[0] == "point":
                points.extend(map(tuple, part[1]))
                continue
            rings = [part[1]] if part[0] == "line" else part[2]
            if role is None:  # mass points: "lines and polygons just add their vertices"
                for ring in rings:
                    points.extend(map(tuple, ring))
                continue
            if role == "line" or (part[0] == "polygon" and hardness):
                for ring in rings:
                    points.extend(map(tuple, ring))
                    segments.extend((tuple(a), tuple(b), hardness) for a, b in zip(ring[:-1], ring[1:]))
            if part[0] == "polygon" and role in ("clip", "erase", "replace", "valuefill"):
                if role == "replace" and height is None:
                    height = float(rings[0][0][2])
                polygons.append((role, part[1], tag, height))
    return points, segments, polygons


# ---- stored TINs


def load(path):
    """The Surface of a stored TIN."""
    file, name = locate(path)
    if not exists(path):
        raise ExecuteError(f"ERROR 000732: Input TIN: Dataset {path} does not exist or is not supported.")
    connection = _gpkg.connect(file)
    try:
        srid, constrained = connection.execute("select srid, constrained from grapy_tin where name = ?",
                                               (name,)).fetchone()
        nodes = connection.execute("select x, y, z, tag, origin from grapy_tin_node where tin = ? order by node_id",
                                   (name,)).fetchall()
        triangles = connection.execute("select n1, n2, n3, tag, inside from grapy_tin_triangle where tin = ? order by "
                                       "triangle_id", (name,)).fetchall()
        edges = {(a, b): t for a, b, t in connection.execute("select n1, n2, type from grapy_tin_edge where tin = ?",
                                                             (name,))}
    finally:
        connection.close()
    n = np.array(nodes, dtype=np.float64).reshape(-1, 5)
    t = np.array(triangles, dtype=np.int64).reshape(-1, 5)
    return Surface(n[:, :3], t[:, :3], t[:, 4].astype(bool), t[:, 3], n[:, 3].astype(np.int64),
                   n[:, 4].astype(np.int64), edges, srid, bool(constrained))


def inputs_of(path):
    file, name = locate(path)
    connection = _gpkg.connect(file)
    try:
        rows = connection.execute("select sf_type, shape, tag, height, pending from grapy_tin_input where tin = ? "
                                  "order by seq", (name,)).fetchall()
        delineation = connection.execute("select delineation from grapy_tin where name = ?", (name,)).fetchone()[0]
    finally:
        connection.close()
    return [(sf, shapely.from_wkb(shape), tag, height, bool(pending)) for sf, shape, tag, height, pending in rows], \
        (delineation.split(" ", 1) if delineation else None)


def store(path, srid, inputs, delineation=None, surface=None, constrained=False):
    """Writes a TIN: its inputs and the surface triangulated from them (or ``surface`` as given), CONSTRAINED_DELAUNAY
    when ``constrained`` (or when the given surface was built so)."""
    file, name = locate(path)
    os.makedirs(os.path.dirname(os.path.abspath(file)), exist_ok=True)
    if surface is None:
        surface = build(inputs, srid, delineation, constrained)
    connection = _gpkg.connect(file)
    try:
        connection.execute("pragma application_id = 1196444487")
        ensure(connection)
        for table in TABLES[1:]:
            connection.execute(f"delete from {table} where tin = ?", (name,))
        connection.execute("delete from grapy_tin where name = ?", (name,))
        connection.execute("insert into grapy_tin (name, srid, constrained, delineation, built_at) values (?, ?, ?, ?, ?)",
                           (name, int(srid or 0), int(surface.constrained), " ".join(map(str, delineation)) if delineation else None,
                            datetime.datetime.now(datetime.timezone.utc).isoformat()))
        connection.executemany("insert into grapy_tin_input values (?, ?, ?, ?, ?, ?, ?)",
                               [(name, i, sf, int(tag), height, int(pending), shapely.to_wkb(shape, flavor="iso"))
                                for i, (sf, shape, tag, height, pending) in enumerate(inputs)])
        connection.executemany("insert into grapy_tin_node values (?, ?, ?, ?, ?, ?, ?)",
                               [(name, i, float(x), float(y), float(z), int(t), int(o)) for i, ((x, y, z), t, o) in
                                enumerate(zip(surface.nodes, surface.node_tags, surface.origin))])
        connection.executemany("insert into grapy_tin_triangle values (?, ?, ?, ?, ?, ?, ?)",
                               [(name, i, int(a), int(b), int(c), int(t), int(s)) for i, ((a, b, c), t, s) in
                                enumerate(zip(surface.triangles, surface.triangle_tags, surface.inside))])
        connection.executemany("insert into grapy_tin_edge values (?, ?, ?, ?)",
                               [(name, int(a), int(b), int(t)) for (a, b), t in surface.edges.items()])
        connection.commit()
    finally:
        connection.close()
    return surface


def build(inputs, srid, delineation=None, constrained=False):
    """The Surface of stored inputs ``[(sf, shape, tag, height, pending)]``: first without the ``<None>`` inputs, whose
    heights are then interpolated from it, as documented ("This requires that some features be added to the TIN without
    the None option"). INFERRED: a ``<None>`` feature with a vertex off the data area is left out, with a warning."""
    ready = [(sf, s, t, h) for sf, s, t, h, pending in inputs if not pending]
    surface = triangulate(*surface_inputs(ready), srid=srid, delineation=delineation, constrained=constrained)
    waiting = [(sf, s, t, h) for sf, s, t, h, pending in inputs if pending]
    if not waiting:
        return surface
    if not len(surface.triangles):
        raise ExecuteError("ERROR: features with the height <None> need a surface to take their heights from; add some "
                           "features with heights first.")
    resolved = []
    for sf, shape, tag, height in waiting:
        xy = shapely.get_coordinates(shape)
        zs = surface.z_at(xy[:, 0], xy[:, 1])
        if np.isnan(zs).any():
            AddWarning(f"A {sf} feature with the height <None> lies partly off the TIN's data area and was not added.")
            continue
        it = iter(zs)
        resolved.append((sf, _gpkg.rebuild(shape, True, False, None, lambda z, xy, it=it: np.array([next(it) for _ in xy])),
                         tag, height))
    return triangulate(*surface_inputs(ready + resolved), srid=srid, delineation=delineation, constrained=constrained)


def gather(in_features, tool, edit=False):
    """Stored inputs from CreateTin's or EditTin's ``in_features`` value table, and the reference of the first."""
    out, srid = [], 0
    for row in rows_of(in_features, 5 if edit else 4):
        if edit:
            path, height, tag, sf, use_z = row[:5]
            use_z = str(use_z or "true").lower() not in ("false", "0", "no")
        else:
            path, height, sf, tag = row[:4]
            use_z = True
        if not path:
            continue
        kind = sf_type(sf, tool)
        if kind == "anchorpoints":
            _refuse(tool, "SF_type", sf, "anchor points are a LAS dataset's surface constraint")
        features, own, shape_type = read_features(path, height, kind, tag, tool, use_z)
        role = _TYPES[kind][0]
        if role in ("clip", "erase", "replace", "valuefill") and shape_type != "Polygon":
            raise ExecuteError(f"ERROR: {path} is {shape_type}; the {sf} type takes polygons, as documented.")
        if role == "line" and shape_type not in ("Polyline", "Polygon"):
            raise ExecuteError(f"ERROR: {path} is {shape_type}; the {sf} type takes lines and polygons, as documented.")
        srid = srid or own
        out.extend((kind, shape, t, h, pending) for shape, pending, h, t in features)
    return out, srid


# ---- describe


class Described:
    """What Describe answers for a TIN: dataType ``Tin``, the TIN properties (fields, hasEdgeTagValues,
    hasNodeTagValues, hasTriangleTagValues, isDelaunay, ZFactor) and the Dataset group's names. INFERRED: ``fields`` is
    empty, a TIN having no attribute table of its own; edges carry no tags in grapy, so hasEdgeTagValues is False."""

    def __init__(self, path, file, name, surface):
        from ._reference import Extent, SpatialReference
        self.catalogPath = str(path)
        self.name = self.baseName = name
        self.path = os.path.dirname(file) if os.path.basename(file) == "tin.gpkg" else file
        self.dataType = self.datasetType = "Tin"
        self.fields = []
        self.hasEdgeTagValues = False
        self.hasNodeTagValues = bool((surface.node_tags != 0).any())
        self.hasTriangleTagValues = bool((surface.triangle_tags != 0).any())
        # "Indicates whether the TIN conforms to the Delaunay criterion": a constrained TIN does not across its
        # enforced edges.
        self.isDelaunay = not surface.constrained
        self.ZFactor = 1
        self.spatialReference = SpatialReference(surface.srid) if surface.srid > 0 else None
        box = surface.extent()
        self.extent = Extent(*box, self.spatialReference) if box else None
        self.nodeCount = len(surface.nodes)
        self.triangleCount = len(surface.triangles)


def describe(path):
    try:
        if not exists(path):
            return None
        file, name = locate(path)
    except (ExecuteError, ValueError, TypeError):
        return None
    return Described(path, file, name, load(path))
