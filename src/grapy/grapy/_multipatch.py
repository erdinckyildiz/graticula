"""Multipatch features in grapy's GeoPackages — ADR-238.

**The model, INFERRED (ADR-238 §5.2).** GeoPackage has no multipatch type. A multipatch feature class is a feature
table whose geometry column is ``MULTIPOLYGON`` with z mandatory, registered in ``gpkg_extensions`` as the
``grapy_multipatch`` extension on that table and column. Each polygon of a feature's MultiPolygon Z is one planar
face — a triangle, or a ring with its holes. GDAL, QGIS and shapely read the table as 3D polygons; grapy reads the
extension row and answers ``MultiPatch`` for its shape type.

Esri's multipatch patch types (``TriangleStrip``, ``TriangleFan``, ``Triangles``, ``Ring``, ``FirstRing``,
``OuterRing``, ``InnerRing``, documented in *The Multipatch Geometry Type*, Esri 2008, and Pro's ``PatchType``
enumeration) are how a multipatch is *built*; ``from_patches`` takes them and stores the faces they describe — a strip
or fan as its triangles, a ring group as one polygon with holes. A face is oriented counterclockwise seen from outside,
its normal by the right-hand rule pointing out of a solid, as ISO 19107 and OGC Simple Features orient a surface;
Esri's own convention is the opposite (clockwise seen from outside) and the faces are written in OGC's because the
geometry is an OGC type.

**A closed multipatch** is one whose faces, triangulated and with coincident vertices welded, use every edge exactly
twice (INFERRED, ADR-238 §5.2; IsClosed3D's page says only that a closed feature "completely encloses a volume of
space").
"""
from __future__ import annotations

import math

import numpy as np
import shapely

from . import _gpkg
from ._runtime import ExecuteError, NotBuiltError
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==

EXTENSION = "grapy_multipatch"
DEFINITION = "Graticula ADR-238: grapy's multipatch features, faces as MultiPolygon Z"
WELD = 1e-6  # INFERRED: vertices closer than this in every axis are one vertex when a mesh is made of faces


def _refuse(module, tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.{module}.{tool}: {parameter} '{value}' is not built in grapy"
                        f"{': ' + why if why else ''}.")


def given(value):
    return _is_given(value)


# ---- the model in the GeoPackage


def register(path):
    """Marks a feature class as a multipatch class."""
    connection, table = _gpkg.table_of(path)
    try:
        connection.execute("""create table if not exists gpkg_extensions (table_name text, column_name text,
            extension_name text not null, definition text not null, scope text not null,
            constraint ge_tce unique (table_name, column_name, extension_name))""")
        connection.execute("insert or ignore into gpkg_extensions values (?, ?, ?, ?, 'read-write')",
                           (table, _gpkg.GEOMETRY, EXTENSION, DEFINITION))
        connection.commit()
    finally:
        connection.close()


def create(path, srid, columns):
    """A new, empty multipatch feature class at ``path``."""
    _gpkg.create(path, "Polygon", srid, columns, True, False)
    register(path)
    return path


def is_registered(connection, table):
    try:
        return connection.execute("select 1 from gpkg_extensions where table_name = ? and extension_name = ?",
                                  (table, EXTENSION)).fetchone() is not None
    except Exception:  # noqa: BLE001 — a GeoPackage without gpkg_extensions holds no multipatch
        return False


def is_multipatch(path):
    try:
        connection, table = _gpkg.table_of(path)
    except (ValueError, FileNotFoundError):
        return False
    try:
        return is_registered(connection, table)
    finally:
        connection.close()


def require(path, module, tool, parameter="in_features"):
    if not is_multipatch(path):
        raise ExecuteError(f"ERROR 000869: arcpy.{module}.{tool}: {parameter} '{path}' is not a multipatch feature "
                           "class (ADR-238).")


def read(path):
    """(columns, srid, rows) of a feature class: each row (oid, {field: value}, shapely shape)."""
    from . import da
    from .analysis import _schema
    columns, _, srid, _ = _schema(path)
    names = [c.name for c in columns]
    out = []
    with da.SearchCursor(path, ["OID@", *names, "SHAPE@"]) as rows:
        for r in rows:
            out.append((r[0], dict(zip(names, r[1:-1])), r[-1]._shape if r[-1] is not None else None))
    return columns, srid, out


def write(path, srid, columns, rows, kind="MultiPatch", has_z=True):
    """A new feature class holding ``rows`` of (values list, shapely shape); ``kind`` MultiPatch makes a multipatch
    class, any other a plain class of that shape."""
    from . import da
    from .analysis import _out
    path = _out(path)
    if kind == "MultiPatch":
        create(path, srid, columns)
    else:
        _gpkg.create(path, kind, srid, columns, has_z, False)
    with da.InsertCursor(path, [*(c.name for c in columns), "SHAPE@"]) as cursor:
        for values, shape in rows:
            cursor.insertRow([*values, shape])
    return path


# ---- faces and triangles


def faces(shape):
    """The faces of a multipatch shape, as shapely Polygons with z."""
    if shape is None or shape.is_empty:
        return []
    if shape.geom_type == "Polygon":
        return [shape]
    out = []
    for part in getattr(shape, "geoms", []):
        out.extend(faces(part))
    return out


def _newell(ring):
    """The Newell normal (not normalised) of a closed 3D ring (n, 3)."""
    p, q = ring[:-1], ring[1:]
    return np.array([np.sum((p[:, 1] - q[:, 1]) * (p[:, 2] + q[:, 2])),
                     np.sum((p[:, 2] - q[:, 2]) * (p[:, 0] + q[:, 0])),
                     np.sum((p[:, 0] - q[:, 0]) * (p[:, 1] + q[:, 1]))])


def _ring(coords):
    a = np.asarray(coords, dtype=float)
    if a.shape[1] == 2:
        a = np.column_stack([a, np.zeros(len(a))])
    return a[:, :3]


def face_triangles(face):
    """A planar face (a Polygon with z, holes allowed) as triangles (k, 3, 3), each oriented as the face is (its
    outer ring's Newell normal). The face is projected on the coordinate plane its normal is most nearly
    perpendicular to and triangulated there by GEOS's constrained Delaunay triangulation, which adds no vertices."""
    outer = _ring(face.exterior.coords)
    if len(outer) < 4:
        return np.zeros((0, 3, 3))
    normal = _newell(outer)
    if not np.any(normal):
        return np.zeros((0, 3, 3))
    axis = int(np.argmax(np.abs(normal)))
    keep = [i for i in range(3) if i != axis]
    rings = [outer, *[_ring(r.coords) for r in face.interiors]]
    lookup = {}
    for ring in rings:
        for p in ring:
            lookup[(p[keep[0]], p[keep[1]])] = p
    flat = shapely.Polygon([tuple(p[keep]) for p in outer], [[tuple(p[keep]) for p in r] for r in rings[1:]])
    if not flat.is_valid:
        flat = shapely.make_valid(flat)
    out = []
    for triangle in getattr(shapely.constrained_delaunay_triangles(flat), "geoms", []):
        corners = [lookup.get(xy) for xy in list(triangle.exterior.coords)[:3]]
        if any(c is None for c in corners):
            continue
        t = np.array(corners)
        if np.dot(np.cross(t[1] - t[0], t[2] - t[0]), normal) < 0:
            t = t[[0, 2, 1]]
        out.append(t)
    return np.array(out).reshape(-1, 3, 3)


def triangles(shape):
    """Every face of a multipatch shape triangulated: (k, 3, 3)."""
    parts = [face_triangles(f) for f in faces(shape)]
    parts = [p for p in parts if len(p)]
    return np.concatenate(parts) if parts else np.zeros((0, 3, 3))


def from_triangles(tris):
    """Triangles (k, 3, 3) as a MultiPolygon Z of triangle faces; degenerate ones dropped."""
    tris = np.asarray(tris, dtype=float).reshape(-1, 3, 3)
    keep = np.linalg.norm(np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]), axis=1) > 0
    return shapely.MultiPolygon([shapely.Polygon([tuple(p) for p in t]) for t in tris[keep]])


def from_faces(polygons):
    """Planar 3D polygons as one multipatch shape."""
    found = []
    for p in polygons:
        found.extend(faces(p))
    return shapely.MultiPolygon(found)


PATCH_TYPES = ("TRIANGLE_STRIP", "TRIANGLE_FAN", "TRIANGLES", "OUTER_RING", "INNER_RING", "FIRST_RING", "RING")


def from_patches(patches):
    """A multipatch shape from Esri's patches, as *The Multipatch Geometry Type* (Esri 2008) describes them: each
    ``(type, points)`` with type one of PATCH_TYPES. A strip makes triangles (0, 1, 2), (2, 1, 3), (2, 3, 4) …; a fan
    (0, 1, 2), (0, 2, 3) …; TRIANGLES each three points; a FIRST_RING or OUTER_RING begins a polygon that the RING or
    INNER_RING patches after it give holes to. A ring is closed (first point repeated) as the paper requires."""
    out = []
    current = None
    grouped = False

    def close():
        nonlocal current
        if current is not None:
            out.append(shapely.Polygon(current[0], current[1:]))
            current = None

    for kind, points in patches:
        kind = str(kind).upper().replace(" ", "_")
        pts = [tuple(float(v) for v in p[:3]) for p in points]
        if kind in ("TRIANGLE_STRIP", "TRIANGLE_FAN", "TRIANGLES"):
            close()
            if kind == "TRIANGLES":
                tris = [pts[i:i + 3] for i in range(0, len(pts) - 2, 3)]
            elif kind == "TRIANGLE_FAN":
                tris = [[pts[0], pts[i], pts[i + 1]] for i in range(1, len(pts) - 1)]
            else:
                tris = [[pts[i], pts[i + 1], pts[i + 2]] if i % 2 == 0 else [pts[i + 1], pts[i], pts[i + 2]]
                        for i in range(len(pts) - 2)]
            out.extend(faces(from_triangles(np.array(tris).reshape(-1, 3, 3))))
        elif kind in ("FIRST_RING", "OUTER_RING"):
            close()
            current = [pts]
            grouped = True
        elif kind in ("RING", "INNER_RING"):
            # "A sequence of rings not preceded by a first ring is treated as a sequence of outer rings without holes."
            if current is None or (kind == "RING" and not grouped):
                close()
                current = [pts]
                grouped = False
                continue
            current.append(pts)
        else:
            raise ValueError(f"'{kind}' is not a multipatch patch type: one of {', '.join(PATCH_TYPES)}.")
    close()
    return shapely.MultiPolygon(out)


# ---- meshes: welded vertices and triangles


def mesh(tris, tolerance=WELD):
    """(vertices (n, 3), faces (k, 3)) of triangles, coincident vertices welded; degenerate triangles dropped."""
    tris = np.asarray(tris, dtype=float).reshape(-1, 3, 3)
    if not len(tris):
        return np.zeros((0, 3)), np.zeros((0, 3), np.int64)
    points = tris.reshape(-1, 3)
    keys = np.round(points / tolerance).astype(np.int64)
    _, first, inverse = np.unique(keys, axis=0, return_index=True, return_inverse=True)
    vertices = points[first]
    f = inverse.reshape(-1, 3)
    keep = (f[:, 0] != f[:, 1]) & (f[:, 1] != f[:, 2]) & (f[:, 0] != f[:, 2])
    return vertices, f[keep]


def is_closed(shape):
    """Whether a multipatch shape encloses a volume: triangulated and welded, every edge is used exactly twice, once
    in each direction (INFERRED, ADR-238 §5.2)."""
    v, f = mesh(triangles(shape))
    if len(f) < 4:
        return False
    directed = np.concatenate([f[:, [0, 1]], f[:, [1, 2]], f[:, [2, 0]]])
    undirected = np.sort(directed, axis=1)
    _, counts = np.unique(undirected, axis=0, return_counts=True)
    if not np.all(counts == 2):
        return False
    _, directed_counts = np.unique(directed, axis=0, return_counts=True)
    return bool(np.all(directed_counts == 1))


def signed_volume(tris):
    """The volume a closed, outward-oriented triangle set encloses (the divergence theorem)."""
    tris = np.asarray(tris, dtype=float).reshape(-1, 3, 3)
    if not len(tris):
        return 0.0
    o = tris.reshape(-1, 3).mean(axis=0)
    a, b, c = tris[:, 0] - o, tris[:, 1] - o, tris[:, 2] - o
    return float(np.sum(np.einsum("ij,ij->i", a, np.cross(b, c))) / 6.0)


def surface_area(tris):
    tris = np.asarray(tris, dtype=float).reshape(-1, 3, 3)
    return float(np.sum(np.linalg.norm(np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]), axis=1)) / 2.0)


# ---- solids through manifold3d (Apache-2.0): booleans on closed meshes


class Solid:
    """A closed mesh as a manifold3d Manifold, held about a local origin so that float precision does not depend on
    how far the data are from the coordinate system's origin."""

    def __init__(self, manifold, origin):
        self.manifold, self.origin = manifold, np.asarray(origin, float)

    @classmethod
    def of(cls, shape_or_tris, origin=None):
        import manifold3d
        tris = triangles(shape_or_tris) if not isinstance(shape_or_tris, np.ndarray) else shape_or_tris
        v, f = mesh(tris)
        if origin is None:
            origin = (v.min(axis=0) + v.max(axis=0)) / 2 if len(v) else np.zeros(3)
        m = manifold3d.Manifold(manifold3d.Mesh64(vert_properties=np.ascontiguousarray(v - origin, np.float64),
                                                  tri_verts=np.ascontiguousarray(f, np.uint64)))
        if m.status() != manifold3d.Error.NoError:
            raise ExecuteError(f"ERROR: the multipatch is not a closed solid manifold3d accepts ({m.status().name}).")
        return cls(m, origin)

    def moved(self, origin):
        return Solid(self.manifold.translate(tuple(self.origin - np.asarray(origin, float))), origin)

    def triangles(self):
        m = self.manifold.to_mesh64()
        v = np.asarray(m.vert_properties, float)[:, :3] + self.origin
        f = np.asarray(m.tri_verts, np.int64)
        return v[f] if len(f) else np.zeros((0, 3, 3))

    def shape(self):
        return from_triangles(self.triangles())

    def volume(self):
        return float(self.manifold.volume())

    def empty(self):
        return self.manifold.is_empty() or self.manifold.volume() <= 0

    def __and__(self, other):
        return Solid(self.manifold ^ other.moved(self.origin).manifold, self.origin)

    def __or__(self, other):
        return Solid(self.manifold + other.moved(self.origin).manifold, self.origin)

    def __sub__(self, other):
        return Solid(self.manifold - other.moved(self.origin).manifold, self.origin)


def hull(points):
    """The convex hull of 3D points as outward triangles (k, 3, 3); None when the points span no volume."""
    from scipy.spatial import ConvexHull, QhullError
    points = np.unique(np.asarray(points, float).reshape(-1, 3), axis=0)
    if len(points) < 4:
        return None
    try:
        h = ConvexHull(points)
    except QhullError:
        return None
    tris = points[h.simplices]
    centre = points[h.vertices].mean(axis=0)
    normals = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])
    flip = np.einsum("ij,ij->i", normals, tris[:, 0] - centre) < 0
    tris[flip] = tris[flip][:, [0, 2, 1]]
    return tris


def box(lo, hi):
    """An axis-aligned box as 12 outward triangles."""
    x0, y0, z0 = lo
    x1, y1, z1 = hi
    c = np.array([[x0, y0, z0], [x1, y0, z0], [x1, y1, z0], [x0, y1, z0],
                  [x0, y0, z1], [x1, y0, z1], [x1, y1, z1], [x0, y1, z1]], float)
    quads = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    return np.array([t for a, b, cc, d in quads for t in ([c[a], c[b], c[cc]], [c[a], c[cc], c[d]])])


def prism(ring_xy, bottom, top):
    """A vertical prism over a 2D polygon (shapely Polygon) between two heights, as outward triangles: its floor and
    roof triangulated, its walls as quads split in two. ``bottom`` and ``top`` are numbers or functions of (x, y)."""
    def height(h, x, y):
        return float(h(x, y)) if callable(h) else float(h)
    out = []
    for t in getattr(shapely.constrained_delaunay_triangles(ring_xy), "geoms", []):
        xy = list(t.exterior.coords)[:3]
        if shapely.Polygon(xy).exterior.is_ccw is False:
            xy = xy[::-1]
        out.append([(x, y, height(top, x, y)) for x, y in xy])
        out.append([(x, y, height(bottom, x, y)) for x, y in xy[::-1]])
    for ring in [ring_xy.exterior, *ring_xy.interiors]:
        coords = list(ring.coords)
        ccw = ring.is_ccw
        outward = ccw if ring is ring_xy.exterior else not ccw
        if not outward:
            coords = coords[::-1]
        for (ax, ay), (bx, by) in zip(coords[:-1], coords[1:]):
            a0, b0 = (ax, ay, height(bottom, ax, ay)), (bx, by, height(bottom, bx, by))
            a1, b1 = (ax, ay, height(top, ax, ay)), (bx, by, height(top, bx, by))
            out.append([a0, b0, b1])
            out.append([a0, b1, a1])
    return np.array(out, float).reshape(-1, 3, 3)


# ---- 3D segment and triangle geometry


def segment_triangle(p, q, tris, eps=1e-12):
    """Where segment p→q crosses each triangle (Möller and Trumbore 1997): the parameters t in [0, 1] along the
    segment, and the triangles' indexes."""
    tris = np.asarray(tris, float).reshape(-1, 3, 3)
    d = np.asarray(q, float) - np.asarray(p, float)
    e1, e2 = tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]
    h = np.cross(d, e2)
    a = np.einsum("ij,ij->i", e1, h)
    ok = np.abs(a) > eps
    f = np.where(ok, 1.0 / np.where(ok, a, 1.0), 0.0)
    s = np.asarray(p, float) - tris[:, 0]
    u = f * np.einsum("ij,ij->i", s, h)
    qv = np.cross(s, e1)
    v = f * (qv @ d)
    t = f * np.einsum("ij,ij->i", e2, qv)
    tol = 1e-9
    hit = ok & (u >= -tol) & (v >= -tol) & (u + v <= 1 + tol) & (t >= -tol) & (t <= 1 + tol)
    return np.clip(t[hit], 0.0, 1.0), np.nonzero(hit)[0]


def winding(points, tris):
    """The generalised winding number of each point about a triangle set (van Oosterom and Strackee 1983's solid
    angle, summed over 4π): 1 inside a closed outward surface, 0 outside."""
    points = np.asarray(points, float).reshape(-1, 3)
    tris = np.asarray(tris, float).reshape(-1, 3, 3)
    out = np.zeros(len(points))
    for start in range(0, len(points), 256):
        p = points[start:start + 256][:, None, :]
        a, b, c = tris[None, :, 0] - p, tris[None, :, 1] - p, tris[None, :, 2] - p
        la, lb, lc = (np.linalg.norm(x, axis=2) for x in (a, b, c))
        numerator = np.einsum("ijk,ijk->ij", a, np.cross(b, c))
        denominator = (la * lb * lc + np.einsum("ijk,ijk->ij", a, b) * lc + np.einsum("ijk,ijk->ij", b, c) * la
                       + np.einsum("ijk,ijk->ij", c, a) * lb)
        out[start:start + 256] = np.sum(2 * np.arctan2(numerator, denominator), axis=1) / (4 * math.pi)
    return out


def inside_solid(points, tris):
    return winding(points, tris) > 0.5


def on_surface(points, tris, tolerance=1e-7):
    """Whether each point lies on one of the triangles (within ``tolerance``)."""
    points = np.asarray(points, float).reshape(-1, 3)
    out = np.zeros(len(points), bool)
    for i, p in enumerate(points):
        out[i] = bool(np.any(_point_triangle_distance(p, tris) <= tolerance))
    return out


def _point_triangle_distance(p, tris):
    """Distance from a point to each triangle (Ericson 2005, §5.1.5, closest point on a triangle)."""
    tris = np.asarray(tris, float).reshape(-1, 3, 3)
    a, b, c = tris[:, 0], tris[:, 1], tris[:, 2]
    best = np.full(len(tris), np.inf)
    # The closest point is in the interior, or on one of the three edges.
    n = np.cross(b - a, c - a)
    nn = np.einsum("ij,ij->i", n, n)
    good = nn > 0
    dist_plane = np.where(good, np.einsum("ij,ij->i", p - a, n) / np.where(good, nn, 1), 0)
    proj = p - dist_plane[:, None] * n
    def side(u, v):
        return np.einsum("ij,ij->i", np.cross(v - u, proj - u), n)
    inside = good & (side(a, b) >= 0) & (side(b, c) >= 0) & (side(c, a) >= 0)
    best[inside] = np.abs(dist_plane[inside]) * np.sqrt(nn[inside])
    for u, v in ((a, b), (b, c), (c, a)):
        d = v - u
        dd = np.einsum("ij,ij->i", d, d)
        t = np.clip(np.einsum("ij,ij->i", p - u, d) / np.where(dd > 0, dd, 1), 0, 1)
        best = np.minimum(best, np.linalg.norm(p - (u + t[:, None] * d), axis=1))
    return best


# ---- drawing a 2D polygon on a TIN surface


def drape(polygon, surface, z_factor=1.0):
    """A 2D polygon draped on a TIN's data area as triangles (k, 3, 3) facing up: each data-area triangle's part
    inside the polygon, triangulated (GEOS constrained Delaunay) and lifted onto that triangle's plane. The result is
    the surface the polygon covers, facet for facet."""
    a, b, c = surface.planes(z_factor)
    tree = surface.tree()
    out = []
    for i in tree.query(polygon, predicate="intersects"):
        if not surface.inside[i]:
            continue
        piece = shapely.intersection(surface.polygons([i])[0], polygon)
        for part in faces(piece) if piece.geom_type != "GeometryCollection" else [g for g in piece.geoms
                                                                                  if g.geom_type == "Polygon"]:
            if part.area <= 0:
                continue
            for t in getattr(shapely.constrained_delaunay_triangles(part), "geoms", []):
                xy = np.array(t.exterior.coords)[:3]
                if shapely.Polygon(xy).exterior.is_ccw is False:
                    xy = xy[::-1]
                z = a[i] * xy[:, 0] + b[i] * xy[:, 1] + c[i]
                out.append(np.column_stack([xy, z]))
    return np.array(out, float).reshape(-1, 3, 3)


def boundary_heights(polygon, surface, z_factor=1.0):
    """The polygon's boundary with a vertex wherever it crosses a TIN edge, as {ring: [(x, y, z)…]}; z on the TIN."""
    edges = shapely.boundary(surface.polygons())
    crossing = shapely.union_all(edges)
    out = []
    for ring in [polygon.exterior, *polygon.interiors]:
        line = shapely.LineString(ring.coords)
        cuts = shapely.intersection(line, crossing)
        distances = sorted({0.0, line.length, *[line.project(shapely.Point(p)) for p in shapely.get_coordinates(cuts)],
                            *[line.project(shapely.Point(p)) for p in ring.coords]})
        xy = np.array([line.interpolate(d).coords[0] for d in distances])
        out.append((ring, xy, surface.z_at(xy[:, 0], xy[:, 1], z_factor)))
    return out
