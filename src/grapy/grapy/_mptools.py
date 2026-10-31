"""3D Analyst's multipatch tools — ADR-238. Answered under arcpy.ddd's names in ``ddd.py``.

Every tool reads and writes multipatch feature classes as ``_multipatch`` holds them: faces of a MultiPolygon Z in a
GeoPackage feature table marked by the ``grapy_multipatch`` extension. Solids are combined by manifold3d
(Apache-2.0), which computes exact-topology booleans on closed triangle meshes; everything else is computed here.
What a page leaves open is listed as INFERRED in each docstring and in ADR-238 §5.
"""
from __future__ import annotations

import math

import numpy as np
import shapely

from . import _gpkg
from . import _multipatch as M
from ._result import Result
from ._runtime import AddMessage, AddWarning, ExecuteError, NotBuiltError, equivalent as _equivalent

given = M.given


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _list(value):
    if value is None or value == "":
        return []
    if isinstance(value, (list, tuple)):
        return [str(v) for v in value]
    return [v.strip().strip("'\"") for v in str(value).split(";") if v.strip()]


def _distance(value, srid):
    from .analysis import _distance as distance
    return distance(value, srid)


def _xyz(shape):
    """Every vertex of a shape as (n, 3), z 0 where it has none."""
    if shape is None or shape.is_empty:
        return np.zeros((0, 3))
    if shapely.has_z(shape):
        return shapely.get_coordinates(shape, include_z=True)
    xy = shapely.get_coordinates(shape)
    return np.column_stack([xy, np.zeros(len(xy))])


def _joined_columns(first, second):
    """The two inputs' fields side by side, a name the first already has taking ``_1`` (INFERRED, as Intersect)."""
    out = list(first)
    names = {c.name.lower() for c in first}
    renamed = []
    for c in second:
        name = c.name
        while name.lower() in names:
            name += "_1"
        names.add(name.lower())
        out.append(_gpkg.Column(name, c.type, True))
        renamed.append(name)
    return out


def _cross2(a, b):
    return a[..., 0] * b[..., 1] - a[..., 1] * b[..., 0]


def _bbox(tris):
    pts = np.asarray(tris).reshape(-1, 3)
    return pts.min(axis=0), pts.max(axis=0)


def _overlap(a, b, pad=0.0):
    return bool(np.all(a[0] <= b[1] + pad) and np.all(b[0] <= a[1] + pad))


# ---- surfaces: TINs and rasters


def _bilinear(raster, xs, ys):
    """Bilinear interpolation between the four cell centres around each point; NaN where one of them is NoData or
    the point is off the raster. INFERRED: between the outermost centres and the raster's edge, the edge cells' value
    is held (the interpolation is clamped)."""
    g = raster._grid
    xs, ys = np.asarray(xs, float), np.asarray(ys, float)
    out = np.full(len(xs), np.nan)
    inside = (xs >= g.left) & (xs <= g.right) & (ys >= g.bottom) & (ys <= g.top)
    fx = np.clip((xs - g.left) / g.width - 0.5, 0, g.columns - 1)
    fy = np.clip((g.top - ys) / g.height - 0.5, 0, g.rows - 1)
    c0 = np.clip(np.floor(fx).astype(int), 0, max(g.columns - 2, 0))
    r0 = np.clip(np.floor(fy).astype(int), 0, max(g.rows - 2, 0))
    c1, r1 = np.minimum(c0 + 1, g.columns - 1), np.minimum(r0 + 1, g.rows - 1)
    tx, ty = fx - c0, fy - r0
    a = np.asarray(raster._array, float)
    m = np.asarray(raster._mask, bool)
    v00, v01, v10, v11 = a[r0, c0], a[r0, c1], a[r1, c0], a[r1, c1]
    bad = m[r0, c0] | m[r0, c1] | m[r1, c0] | m[r1, c1]
    value = (v00 * (1 - tx) * (1 - ty) + v01 * tx * (1 - ty) + v10 * (1 - tx) * ty + v11 * tx * ty)
    ok = inside & ~bad
    out[ok] = value[ok]
    return out


class _Heights:
    """A surface a tool samples: a TIN, terrain or LAS dataset (linear on its triangles), or a raster (bilinear)."""

    def __init__(self, value, tool, method="LINEAR"):
        from . import _tintools
        from ._raster import as_raster
        self.value = value
        self.tin = _tintools.surface_of(value, tool)
        self.raster = None
        if self.tin is None:
            self.raster = as_raster(value, f"arcpy.ddd.{tool}")
            self.srid = self.raster._grid.srid
        else:
            self.srid = self.tin.srid
            if str(method or "LINEAR").upper() != "LINEAR":
                _refuse(tool, "method", method, "natural neighbour interpolation on a TIN is not built in grapy "
                        "(ADR-229 §5.4); LINEAR is")

    def z(self, xs, ys):
        if self.tin is not None:
            return self.tin.z_at(xs, ys)
        return _bilinear(self.raster, xs, ys)

    def cell(self):
        return None if self.raster is None else min(self.raster._grid.width, self.raster._grid.height)

    def crossings(self, line):
        """Distances along a 2D line where it crosses this TIN's edges (none for a raster)."""
        if self.tin is None:
            return []
        edges = shapely.union_all(shapely.boundary(self.tin.polygons()))
        cut = shapely.intersection(line, edges)
        return [line.project(shapely.Point(p)) for p in shapely.get_coordinates(cut)]


def _ring_points(ring, surfaces):
    """A ring's vertices with one added wherever it crosses an edge of one of the TINs, in order."""
    line = shapely.LineString(ring.coords)
    distances = {0.0, line.length}
    distances.update(line.project(shapely.Point(p)) for p in ring.coords)
    for s in surfaces:
        if s is not None:
            distances.update(s.crossings(line) if isinstance(s, _Heights) else _tin_crossings(s, line))
    xy = np.array([line.interpolate(d).coords[0] for d in sorted(distances)])
    keep = np.concatenate([[True], np.any(np.abs(np.diff(xy, axis=0)) > 1e-9, axis=1)])
    return xy[keep]


def _tin_crossings(tin, line):
    edges = shapely.union_all(shapely.boundary(tin.polygons()))
    return [line.project(shapely.Point(p)) for p in shapely.get_coordinates(shapely.intersection(line, edges))]


def _densified(polygon, surfaces):
    """A 2D polygon with a vertex wherever its boundary crosses one of the TINs' edges."""
    polygon = shapely.geometry.polygon.orient(polygon, 1.0)
    rings = [_ring_points(r, surfaces) for r in [polygon.exterior, *polygon.interiors]]
    return shapely.Polygon(rings[0], rings[1:])


def _top_faces(polygon, surface):
    """A densified 2D polygon on a surface: draped facet for facet on a TIN, or a flat roof at a constant."""
    if isinstance(surface, (int, float)):
        out = []
        for t in getattr(shapely.constrained_delaunay_triangles(polygon), "geoms", []):
            xy = np.array(t.exterior.coords)[:3]
            if shapely.Polygon(xy).exterior.is_ccw is False:
                xy = xy[::-1]
            out.append(np.column_stack([xy, np.full(3, float(surface))]))
        return np.array(out, float).reshape(-1, 3, 3)
    return M.drape(polygon, surface)


def _height_of(surface, xy):
    if isinstance(surface, (int, float)):
        return np.full(len(xy), float(surface))
    return surface.z_at(xy[:, 0], xy[:, 1])


def closed_between(polygon, upper, lower):
    """The closed solid over a 2D polygon between two surfaces (TIN Surfaces or constant heights): its roof draped on
    the upper, its floor on the lower, its walls through every boundary vertex, the boundary having a vertex wherever
    it crosses either TIN's edges. Outward-oriented triangles (k, 3, 3)."""
    tins = [s for s in (upper, lower) if not isinstance(s, (int, float))]
    polygon = _densified(polygon, tins)
    roof = _top_faces(polygon, upper)
    floor = _top_faces(polygon, lower)[:, [0, 2, 1]]
    walls = []
    for ring in [polygon.exterior, *polygon.interiors]:
        xy = np.array(ring.coords)
        top, bottom = _height_of(upper, xy), _height_of(lower, xy)
        for i in range(len(xy) - 1):
            a, b = xy[i], xy[i + 1]
            a0, b0 = (*a, bottom[i]), (*b, bottom[i + 1])
            a1, b1 = (*a, top[i]), (*b, top[i + 1])
            walls.append([a0, b0, b1])
            walls.append([a0, b1, a1])
    walls = np.array(walls, float).reshape(-1, 3, 3)
    tris = np.concatenate([roof, floor, walls])
    keep = np.all(np.isfinite(tris.reshape(len(tris), -1)), axis=1)
    return tris[keep]


# ---- IsClosed3D, MultiPatchFootprint


def IsClosed3D(in_feature_class):
    """Adds ``IsClosed`` to a multipatch class: 1 where the feature encloses a volume, 0 where it does not. INFERRED:
    the field's type and values (SHORT, 1 and 0), which the page does not give; and the test — every edge of the
    welded, triangulated faces used exactly twice, once in each direction (ADR-238 §5.2)."""
    from . import da
    M.require(in_feature_class, "ddd", "IsClosed3D", "in_feature_class")
    connection, table = _gpkg.table_of(in_feature_class)
    try:
        names = [c.name.lower() for c in _gpkg.describe(connection, table)[0]]
    finally:
        connection.close()
    if "isclosed" not in names:
        _gpkg.add_column(in_feature_class, _gpkg.Column("IsClosed", "Short"))
    closed = 0
    with da.UpdateCursor(in_feature_class, ["SHAPE@", "IsClosed"]) as rows:
        for shape, _ in rows:
            value = 1 if shape is not None and M.is_closed(shape._shape) else 0
            closed += value
            rows.updateRow([shape, value])
    AddMessage(f"{closed} closed multipatch feature(s).")
    return Result(in_feature_class)


def _footprint(tris):
    """The 2D area a set of triangles covers: the union of their projections with area; their 2D extent when they
    have none (vertical faces), as the page documents."""
    flat = tris[:, :, :2]
    area = np.abs(_cross2(flat[:, 1] - flat[:, 0], flat[:, 2] - flat[:, 0])) / 2
    polys = [shapely.Polygon(t) for t, a in zip(flat, area) if a > 1e-12]
    if polys:
        return shapely.union_all(polys)
    lo, hi = flat.reshape(-1, 2).min(axis=0), flat.reshape(-1, 2).max(axis=0)
    return shapely.box(*lo, *hi)


def MultiPatchFootprint(in_feature_class, out_feature_class, group_field=None):
    """Each multipatch's 2D footprint with the input's fields and ``Z_Min`` and ``Z_Max``; with a group field, one
    footprint per value of it. INFERRED: a grouped footprint carries the group field (and Z_Min/Z_Max) only, its
    features' other values differing."""
    M.require(in_feature_class, "ddd", "MultiPatchFootprint", "in_feature_class")
    columns, srid, rows = M.read(in_feature_class)
    extra = [_gpkg.Column("Z_Min", "Double"), _gpkg.Column("Z_Max", "Double")]
    out = []
    if given(group_field):
        column = next((c for c in columns if c.name.lower() == str(group_field).lower()), None)
        if column is None:
            raise ExecuteError(f"ERROR 000728: field {group_field} does not exist.")
        groups: dict = {}
        for _, values, shape in rows:
            tris = M.triangles(shape)
            if len(tris):
                groups.setdefault(values[column.name], []).append(tris)
        for key, parts in groups.items():
            tris = np.concatenate(parts)
            out.append(([key, float(tris[:, :, 2].min()), float(tris[:, :, 2].max())], _footprint(tris)))
        columns = [column]
    else:
        for _, values, shape in rows:
            tris = M.triangles(shape)
            if not len(tris):
                continue
            out.append(([values[c.name] for c in columns] + [float(tris[:, :, 2].min()), float(tris[:, :, 2].max())],
                        _footprint(tris)))
    path = M.write(out_feature_class, srid, [*columns, *extra], out, kind="Polygon", has_z=False)
    return Result(path)


# ---- MinimumBoundingVolume


def MinimumBoundingVolume(in_features, z_value, out_feature_class, geometry_type="CONVEX_HULL", group="NONE",
                          group_field=None, mbv_fields="NO_MBV_FIELDS"):
    """The volume around 3D features or a LAS dataset's points, as a closed multipatch: CONVEX_HULL (the 3D convex
    hull, Qhull) or ENVELOPE (the axis-aligned box). ``z_value`` is ``Shape.Z`` for the features' own z or a numeric
    field holding each feature's height. MBV_FIELDS adds ``MBV_Area`` (the surface area) and ``MBV_Volume``.

    Refused: SPHERE — the page does not say whether it is the smallest enclosing sphere, nor how a sphere is faceted,
    and MBV_Area and MBV_Volume depend on the facets; CONCAVE_HULL — no parameter sets its concavity and the method is
    not given. INFERRED: CONVEX_HULL is the default (the page lists it first and names none); points take ALL when
    no grouping is given, NONE not being available for them; a group with no volume (fewer than four points not in
    one plane, or a flat box) writes no feature and a warning; a LAS dataset is one group of every point its layer
    shows."""
    from . import _las
    tool = "MinimumBoundingVolume"
    kind = str(geometry_type or "CONVEX_HULL").upper()
    if kind == "SPHERE":
        _refuse(tool, "geometry_type", geometry_type, "the page does not say whether the sphere is the smallest "
                "enclosing one, nor how it is faceted, and MBV_Area and MBV_Volume depend on the facets")
    if kind == "CONCAVE_HULL":
        _refuse(tool, "geometry_type", geometry_type, "no parameter sets its concavity and its method is not given")
    if kind not in ("CONVEX_HULL", "ENVELOPE"):
        raise ValueError(f"arcpy.ddd.{tool}: geometry_type '{geometry_type}' is not one of CONVEX_HULL, SPHERE, "
                         "ENVELOPE, CONCAVE_HULL.")
    grouping = str(group or "NONE").upper()
    keys = _list(group_field)
    with_fields = str(mbv_fields or "").upper() == "MBV_FIELDS"
    groups: dict = {}
    srid = 0
    key_columns = []
    if _las.is_las(in_features):
        found, dataset = _las.points(in_features, tool)
        srid = dataset.srid
        groups[()] = [np.column_stack([found["x"], found["y"], found["z"]])]
        grouping = "ALL"
    else:
        columns, srid, rows = M.read(in_features)
        from .analysis import _schema
        shape_type = _schema(in_features)[1]
        if shape_type in ("Point",) and grouping == "NONE":
            grouping = "ALL"
        z_field = None if str(z_value).replace("@", ".").upper() in ("SHAPE.Z", "SHAPE_Z", "SHAPE@Z", "#", "") \
            else str(z_value)
        if grouping == "LIST":
            if not keys:
                raise ExecuteError(f"ERROR 000735: {tool} needs group_field for LIST.")
            key_columns = [next(c for c in columns if c.name.lower() == k.lower()) for k in keys]
        for oid, values, shape in rows:
            xyz = _xyz(shape)
            if z_field is not None:
                xyz[:, 2] = float(values[z_field])
            key = (oid,) if grouping == "NONE" else tuple(values[c.name] for c in key_columns) if grouping == "LIST" \
                else ()
            groups.setdefault(key, []).append(xyz)
    out_columns = list(key_columns) + ([_gpkg.Column("MBV_Area", "Double"), _gpkg.Column("MBV_Volume", "Double")]
                                       if with_fields else [])
    out = []
    for key, parts in groups.items():
        pts = np.concatenate(parts)
        if kind == "ENVELOPE":
            lo, hi = pts.min(axis=0), pts.max(axis=0)
            tris = M.box(lo, hi) if np.all(hi > lo) else None
        else:
            tris = M.hull(pts)
        if tris is None:
            AddWarning(f"{tool}: group {key or 'ALL'} spans no volume and writes no feature.")
            continue
        values = list(key) if grouping == "LIST" else []
        if with_fields:
            values += [M.surface_area(tris), M.signed_volume(tris)]
        out.append((values, M.from_triangles(tris)))
    path = M.write(out_feature_class, srid, out_columns, out)
    return Result(path)


# ---- TINToMultipatch, InterpolatePolyToPatch, ExtrudeBetween


_SIMPLIFY = ("the page gives the tolerance as the allowed deviation in height but not how points are removed to meet "
             "it, and the faces depend on which are")


def _tin_tool(value, tool):
    from . import _tintools
    surface = _tintools.surface_of(value, tool)
    if surface is None:
        raise ExecuteError(f"ERROR 000732: arcpy.ddd.{tool}: '{value}' is not a TIN.")
    return surface


def TINToMultipatch(in_tin, out_feature_class, simplification_tolerance=None, base_height=None):
    """A TIN's data-area triangles as one multipatch feature of triangles; with ``base_height``, closed by walls
    dropped from the data area's outline to that height and a floor at it, as documented. The base must be lower
    than the TIN's lowest node.

    Refused: ``simplification_tolerance`` (see the reason). INFERRED: one feature for the whole TIN; no fields."""
    tool = "TINToMultipatch"
    if given(simplification_tolerance):
        _refuse(tool, "simplification_tolerance", simplification_tolerance, _SIMPLIFY)
    surface = _tin_tool(in_tin, tool)
    which = np.nonzero(surface.inside)[0]
    tris = surface.nodes[surface.triangles[which]]
    up = np.cross(tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0])[:, 2] < 0
    tris[up] = tris[up][:, [0, 2, 1]]
    if given(base_height):
        base = _distance(base_height, surface.srid)
        if base >= float(tris[:, :, 2].min()):
            raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the base height {base} must be less than the TIN's lowest "
                               f"z, {float(tris[:, :, 2].min())}.")
        walls, floors = [], []
        domain = surface.domain()
        z_of = {(float(x), float(y)): float(z) for x, y, z in surface.nodes}
        for poly in M.faces(domain) if domain.geom_type != "Polygon" else [domain]:
            poly = shapely.geometry.polygon.orient(poly, 1.0)
            for ring in [poly.exterior, *poly.interiors]:
                xy = np.array(ring.coords)
                z = np.array([z_of.get((float(x), float(y)), np.nan) for x, y in xy])
                missing = np.isnan(z)
                if missing.any():
                    z[missing] = surface.z_at(xy[missing, 0], xy[missing, 1])
                for i in range(len(xy) - 1):
                    a0, b0 = (*xy[i], base), (*xy[i + 1], base)
                    a1, b1 = (*xy[i], z[i]), (*xy[i + 1], z[i + 1])
                    walls += [[a0, b0, b1], [a0, b1, a1]]
            floors.append(_top_faces(poly, base)[:, [0, 2, 1]])
        tris = np.concatenate([tris, np.array(walls, float).reshape(-1, 3, 3), *floors])
    path = M.write(out_feature_class, surface.srid, [], [([], M.from_triangles(tris))])
    return Result(path)


def InterpolatePolyToPatch(in_surface, in_feature_class, out_feature_class, max_strip_size=1024, z_factor=1,
                           area_field="Area", surface_area_field="SArea", pyramid_level_resolution=0):
    """Each polygon draped on a TIN or terrain as a multipatch, with the polygon's fields, its planimetric area in
    ``area_field`` and its 3D area in ``surface_area_field``. As documented, heights are linear on the surface's
    triangles, the polygon's boundary is enforced where it crosses them, and the nodes inside are kept: grapy
    intersects each data-area triangle with the polygon and triangulates the piece on that triangle's plane, which is
    the same surface facet for facet; its triangles need not be Pro's. ``max_strip_size`` (3 at least) bounds how
    many triangles a strip holds; grapy stores a strip as its triangles, so it changes nothing stored.

    INFERRED: the field names' defaults are the page's examples', ``Area`` and ``SArea``; a polygon off the data
    area writes no feature."""
    from . import _tintools
    tool = "InterpolatePolyToPatch"
    if int(max_strip_size if given(max_strip_size) else 1024) < 3:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: max_strip_size must be 3 or more.")
    surface = _tintools.surface_of(in_surface, tool, pyramid_level_resolution)
    if surface is None:
        raise ExecuteError(f"ERROR 000732: arcpy.ddd.{tool}: '{in_surface}' is not a TIN or terrain.")
    zf = float(z_factor if given(z_factor) else 1)
    columns, srid, rows = M.read(in_feature_class)
    extra = [_gpkg.Column(str(n), "Double") for n in (area_field, surface_area_field) if given(n)]
    out = []
    for _, values, shape in rows:
        if shape is None or shape.is_empty:
            continue
        tris = np.concatenate([M.drape(p, surface, zf) for p in M.faces(shapely.force_2d(shape))] or
                              [np.zeros((0, 3, 3))])
        if not len(tris):
            continue
        flat = float(np.abs(_cross2(tris[:, 1, :2] - tris[:, 0, :2], tris[:, 2, :2] - tris[:, 0, :2])).sum() / 2)
        row = [values[c.name] for c in columns]
        if given(area_field):
            row.append(float(flat))
        if given(surface_area_field):
            row.append(M.surface_area(tris))
        out.append((row, M.from_triangles(tris)))
    path = M.write(out_feature_class, srid or surface.srid, [*columns, *extra], out)
    return Result(path)


def ExtrudeBetween(in_tin1, in_tin2, in_feature_class, out_feature_class):
    """Each feature extruded between two TINs, over the part of it inside both TINs' data areas: a polygon becomes a
    closed multipatch (roof and floor draped on the two TINs, walls through every boundary vertex and every crossing
    with either TIN's edges); a line a multipatch wall between the two surfaces; a point a vertical 3D line, as
    documented. The fields are the input's.

    INFERRED: of the two TINs, the one higher on average over the feature is the roof; a line's wall is sampled at its
    vertices and its crossings with either TIN's edges."""
    from .analysis import _schema
    tool = "ExtrudeBetween"
    one, two = _tin_tool(in_tin1, tool), _tin_tool(in_tin2, tool)
    region = shapely.intersection(one.domain(), two.domain())
    columns, srid, rows = M.read(in_feature_class)
    kind = _schema(in_feature_class)[1]
    out = []
    for _, values, shape in rows:
        if shape is None or shape.is_empty:
            continue
        piece = shapely.intersection(shapely.force_2d(shape), region)
        if piece.is_empty:
            continue
        attrs = [values[c.name] for c in columns]
        if kind == "Point" or kind == "Multipoint":
            for p in shapely.get_coordinates(piece):
                z1, z2 = float(one.z_at([p[0]], [p[1]])[0]), float(two.z_at([p[0]], [p[1]])[0])
                out.append((attrs, shapely.LineString([(p[0], p[1], min(z1, z2)), (p[0], p[1], max(z1, z2))])))
            continue
        if kind == "Polyline":
            walls = []
            lines = [g for g in getattr(piece, "geoms", [piece]) if g.geom_type == "LineString"]
            for line in lines:
                d = sorted({0.0, line.length, *[line.project(shapely.Point(c)) for c in line.coords],
                            *_tin_crossings(one, line), *_tin_crossings(two, line)})
                xy = np.array([line.interpolate(v).coords[0] for v in d])
                z1, z2 = one.z_at(xy[:, 0], xy[:, 1]), two.z_at(xy[:, 0], xy[:, 1])
                lo, hi = np.minimum(z1, z2), np.maximum(z1, z2)
                for i in range(len(xy) - 1):
                    a0, b0, a1, b1 = (*xy[i], lo[i]), (*xy[i + 1], lo[i + 1]), (*xy[i], hi[i]), (*xy[i + 1], hi[i + 1])
                    walls += [[a0, b0, b1], [a0, b1, a1]]
            if walls:
                out.append((attrs, M.from_triangles(np.array(walls))))
            continue
        tris = []
        for poly in [g for g in getattr(piece, "geoms", [piece]) if g.geom_type == "Polygon"]:
            mean1 = float(np.nanmean(one.z_at(*np.array(poly.exterior.coords).T)))
            mean2 = float(np.nanmean(two.z_at(*np.array(poly.exterior.coords).T)))
            upper, lower = (one, two) if mean1 >= mean2 else (two, one)
            tris.append(closed_between(poly, upper, lower))
        if tris:
            out.append((attrs, M.from_triangles(np.concatenate(tris))))
    if kind in ("Point", "Multipoint"):
        path = M.write(out_feature_class, srid, columns, out, kind="Polyline", has_z=True)
    else:
        path = M.write(out_feature_class, srid, columns, out)
    return Result(path)


# ---- Intersect3DLineWithMultiPatch, Inside3D


def _crossings_of(line_xyz, tris):
    """Distances along a 3D polyline (n, 3) where it meets a triangle set, sorted and without repeats."""
    seg = np.diff(line_xyz, axis=0)
    lengths = np.linalg.norm(seg, axis=1)
    start = np.concatenate([[0.0], np.cumsum(lengths)])
    found = []
    for i in range(len(seg)):
        if lengths[i] == 0:
            continue
        t, _ = M.segment_triangle(line_xyz[i], line_xyz[i + 1], tris)
        found.extend(start[i] + t * lengths[i])
    found = sorted(found)
    out = []
    for d in found:
        if not out or d - out[-1] > 1e-7:
            out.append(float(d))
    return out, start


def _at(line_xyz, start, d):
    i = int(np.clip(np.searchsorted(start, d, side="right") - 1, 0, len(line_xyz) - 2))
    span = start[i + 1] - start[i]
    t = 0.0 if span == 0 else (d - start[i]) / span
    return line_xyz[i] + t * (line_xyz[i + 1] - line_xyz[i])


def _piece(line_xyz, start, d0, d1):
    inner = [line_xyz[i] for i in range(len(line_xyz)) if d0 < start[i] < d1]
    return np.array([_at(line_xyz, start, d0), *inner, _at(line_xyz, start, d1)])


def _lines_of(shape):
    if shape is None or shape.is_empty:
        return []
    return [np.asarray(_xyz(g)) for g in getattr(shape, "geoms", [shape]) if g.geom_type == "LineString"]


def Intersect3DLineWithMultiPatch(in_line_features, in_multipatch_features, join_attributes="IDS_ONLY",
                                  out_point_feature_class=None, out_line_feature_class=None):
    """Where 3D lines meet multipatches in 3D (Möller–Trumbore on each face's triangles): points with ``LINE_OID``,
    ``MPATCH_OID`` and ``DIST_3D``; and the lines split there, each with ``LINE_OID``, ``FROM_MP_ID`` and
    ``TO_MP_ID`` (−1 where an end meets none), ``DIST_3D`` (where it begins along the line) and ``LENGTH_3D`` — their
    lengths summing to the line's 3D length, a line meeting none copied whole. ALL adds both inputs' fields. The
    third output is the number of intersection points.

    INFERRED: a line touching a multipatch at one place twice (an edge shared by two faces) counts once; where one
    point meets several multipatches, the split line's FROM_MP_ID/TO_MP_ID is the lowest of their OIDs; ALL's fields
    are the line's then the multipatch's, a repeated name taking ``_1``; a multi-part line is measured part by part
    (DIST_3D from its part's start)."""
    tool = "Intersect3DLineWithMultiPatch"
    M.require(in_multipatch_features, "ddd", tool, "in_multipatch_features")
    lcols, srid, lines = M.read(in_line_features)
    mcols, _, patches = M.read(in_multipatch_features)
    if not _gpkg_has_z(in_line_features):
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: 2D line features are not supported, as documented.")
    everything = str(join_attributes or "IDS_ONLY").upper() == "ALL"
    meshes = [(oid, values, M.triangles(shape)) for oid, values, shape in patches]
    meshes = [(oid, values, t, _bbox(t)) for oid, values, t in meshes if len(t)]
    joined = _joined_columns(lcols, mcols) if everything else []
    points, pieces, count = [], [], 0
    for loid, lvalues, shape in lines:
        for part in _lines_of(shape):
            box = (part.min(axis=0), part.max(axis=0))
            meet: dict = {}
            for moid, mvalues, tris, mbox in meshes:
                if not _overlap(box, mbox, 1e-9):
                    continue
                found, start = _crossings_of(part, tris)
                for d in found:
                    p = _at(part, start, d)
                    count += 1
                    attrs = [*lvalues.values(), *mvalues.values()] if everything else []
                    points.append(([loid, moid, d, *attrs], shapely.Point(*p)))
                    meet.setdefault(round(d, 9), []).append(moid)
            _, start = _crossings_of(part, np.zeros((0, 3, 3)))
            total = float(start[-1])
            cuts = sorted({0.0, total, *meet.keys()})
            for d0, d1 in zip(cuts[:-1], cuts[1:]):
                if d1 - d0 <= 1e-9:
                    continue
                piece = _piece(part, start, d0, d1)
                a, b = meet.get(round(d0, 9)), meet.get(round(d1, 9))
                attrs = [*lvalues.values()] if everything else []
                pieces.append(([loid, min(a) if a else -1, min(b) if b else -1, d0, d1 - d0, *attrs],
                               shapely.LineString(piece)))
    outputs = []
    if given(out_point_feature_class):
        cols = [_gpkg.Column("LINE_OID", "Integer"), _gpkg.Column("MPATCH_OID", "Integer"),
                _gpkg.Column("DIST_3D", "Double"), *joined]
        outputs.append(M.write(out_point_feature_class, srid, cols, points, kind="Point", has_z=True))
    else:
        outputs.append("")
    if given(out_line_feature_class):
        cols = [_gpkg.Column("LINE_OID", "Integer"), _gpkg.Column("FROM_MP_ID", "Integer"),
                _gpkg.Column("TO_MP_ID", "Integer"), _gpkg.Column("DIST_3D", "Double"),
                _gpkg.Column("LENGTH_3D", "Double"), *joined[:len(lcols)]]
        outputs.append(M.write(out_line_feature_class, srid, cols, pieces, kind="Polyline", has_z=True))
    else:
        outputs.append("")
    AddMessage(f"{count} intersection(s).")
    return Result(*outputs, count)


def _gpkg_has_z(path):
    connection, table = _gpkg.table_of(path)
    try:
        return _gpkg.dimensions(connection, table)[0]
    finally:
        connection.close()


def Inside3D(in_target_feature_class, in_container_feature_class, out_table, complex_output="SIMPLE"):
    """Which 3D features lie inside closed multipatches: a row for each target and each container it is in or
    partly in, with ``Target_ID`` and ``Status`` and, COMPLEX, ``Contain_ID``. A point is inside when its winding
    number about the container is one (van Oosterom and Strackee 1983) or it lies on its surface; a line is cut where
    it meets the container's faces and each piece judged by its midpoint; a closed multipatch is inside when its
    intersection with the container (manifold3d) has its whole volume, partly when some.

    INFERRED: Status is ``Inside`` or ``Partially_Inside`` (the page gives no values); a point on the surface is
    inside; an open multipatch target is judged by its vertices and its triangles' centroids. A container that is not
    closed is an error, as the page requires closed ones."""
    tool = "Inside3D"
    M.require(in_container_feature_class, "ddd", tool, "in_container_feature_class")
    if not _gpkg_has_z(in_target_feature_class):
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: every input must have z, as documented.")
    _, _, containers = M.read(in_container_feature_class)
    _, _, targets = M.read(in_target_feature_class)
    target_is_mp = M.is_multipatch(in_target_feature_class)
    boxes = []
    for coid, _, shape in containers:
        if not M.is_closed(shape):
            raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: container {coid} is not a closed multipatch, which the "
                               "analysis requires.")
        tris = M.triangles(shape)
        boxes.append((coid, shape, tris, _bbox(tris)))
    rows = []
    for toid, _, shape in targets:
        if shape is None or shape.is_empty:
            continue
        pts = _xyz(shape)
        tbox = (pts.min(axis=0), pts.max(axis=0))
        for coid, cshape, tris, cbox in boxes:
            if not _overlap(tbox, cbox, 1e-9):
                continue
            status = _status(shape, target_is_mp, cshape, tris)
            if status:
                rows.append((toid, status, coid))
    complex_ = str(complex_output or "SIMPLE").upper() == "COMPLEX"
    from . import da
    from .analysis import _table
    columns = [_gpkg.Column("Target_ID", "Integer"), _gpkg.Column("Status", "String")]
    if complex_:
        columns.append(_gpkg.Column("Contain_ID", "Integer"))
    path = _table(out_table, columns)
    with da.InsertCursor(path, [c.name for c in columns]) as cursor:
        for toid, status, coid in rows:
            cursor.insertRow([toid, status, coid] if complex_ else [toid, status])
    return Result(path)


def _inside_points(points, tris):
    return M.inside_solid(points, tris) | M.on_surface(points, tris)


def _status(shape, target_is_mp, container, tris):
    if target_is_mp:
        if M.is_closed(shape):
            target = M.Solid.of(shape)
            common = (target & M.Solid.of(container)).volume()
            whole = target.volume()
            if common >= whole * (1 - 1e-9):
                return "Inside"
            return "Partially_Inside" if common > whole * 1e-12 else None
        ttris = M.triangles(shape)
        pts = np.concatenate([ttris.reshape(-1, 3), ttris.mean(axis=1)])
        inside = _inside_points(pts, tris)
    elif shape.geom_type in ("LineString", "MultiLineString"):
        mids = []
        for part in _lines_of(shape):
            found, start = _crossings_of(part, tris)
            cuts = sorted({0.0, float(start[-1]), *found})
            mids += [_at(part, start, (a + b) / 2) for a, b in zip(cuts[:-1], cuts[1:]) if b > a]
        inside = _inside_points(np.array(mids).reshape(-1, 3), tris)
    elif shape.geom_type in ("Point", "MultiPoint"):
        inside = _inside_points(_xyz(shape), tris)
    else:
        inside = _inside_points(_xyz(shape), tris)
    if not len(inside) or not inside.any():
        return None
    return "Inside" if inside.all() else "Partially_Inside"


# ---- 3D booleans: Intersect3D, Union3D, Difference3D (manifold3d)


def _solids(path, tool, parameter):
    M.require(path, "ddd", tool, parameter)
    columns, srid, rows = M.read(path)
    out = []
    for oid, values, shape in rows:
        if shape is None or not M.is_closed(shape):
            raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: feature {oid} of {parameter} is not a closed multipatch, "
                               "which the analysis requires.")
        tris = M.triangles(shape)
        out.append((oid, values, M.Solid.of(tris), _bbox(tris)))
    return columns, srid, out


def Intersect3D(in_feature_class_1, out_feature_class, in_feature_class_2=None, output_geometry_type="SOLID"):
    """The volumes closed multipatches share, as closed multipatches (SOLID): with one input, every pair of its
    features that overlap; with two, every pair of one from each, a feature's overlaps within one input ignored, as
    documented, with both features' fields concatenated.

    Refused: SURFACE — the page does not say how faces are found coincident (no tolerance); LINE — the page does not
    say how the faces' crossing segments are joined into line features. INFERRED: the second input's fields take
    ``_1`` where a name repeats; with one input, its fields twice, the second set renamed so."""
    tool = "Intersect3D"
    kind = str(output_geometry_type or "SOLID").upper()
    if kind == "SURFACE":
        _refuse(tool, "output_geometry_type", output_geometry_type, "the page does not say how faces are found "
                "coincident, nor with what tolerance")
    if kind == "LINE":
        _refuse(tool, "output_geometry_type", output_geometry_type, "the page does not say how the segments where "
                "faces cross are joined into line features")
    cols1, srid, one = _solids(in_feature_class_1, tool, "in_feature_class_1")
    if given(in_feature_class_2):
        cols2, _, two = _solids(in_feature_class_2, tool, "in_feature_class_2")
        pairs = [(a, b) for a in one for b in two]
    else:
        cols2, two = cols1, one
        pairs = [(one[i], one[j]) for i in range(len(one)) for j in range(i + 1, len(one))]
    columns = _joined_columns(cols1, cols2)
    out = []
    for a, b in pairs:
        if not _overlap(a[3], b[3]):
            continue
        common = a[2] & b[2]
        if common.empty():
            continue
        out.append(([*(a[1][c.name] for c in cols1), *(b[1][c.name] for c in cols2)], common.shape()))
    path = M.write(out_feature_class, srid, columns, out)
    return Result(path)


def _overlapping_groups(solids):
    """Connected groups of solids whose volumes overlap; ones only touching at a face, edge or vertex stay apart."""
    n = len(solids)
    parent = list(range(n))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    touching = 0
    for i in range(n):
        for j in range(i + 1, n):
            if not _overlap(solids[i][3], solids[j][3], 1e-9):
                continue
            if (solids[i][2] & solids[j][2]).empty():
                if _overlap(solids[i][3], solids[j][3]):
                    touching += 1
                continue
            parent[find(i)] = find(j)
    if touching:
        AddWarning(f"{touching} pair(s) of features overlap only at a face, edge or vertex and are not merged.")
    groups: dict = {}
    for i in range(n):
        groups.setdefault(find(i), []).append(i)
    return list(groups.values())


def Union3D(in_feature_class, out_feature_class, group_field=None, disable_optimization="ENABLE", output_all="ENABLE",
            out_table=None):
    """Closed multipatches whose volumes overlap merged into one closed multipatch each, their interiors removed
    (manifold3d's union); with a group field, a feature only meets those of its group. ``out_table`` relates each
    output to the inputs that made it. As the page documents, with optimization enabled the group field is ignored.
    ``output_all`` ENABLE writes the features that met none too; DISABLE only the merged ones.

    INFERRED: the output carries no input fields; ``out_table``'s fields are ``Output_ID`` and ``Input_ID`` (the page
    names none; Difference3D's table names its own ``Output_ID`` and ``Minuend_ID``); features meeting only at a face,
    edge or vertex are not merged, with a warning, as documented."""
    from . import da
    from .analysis import _table
    tool = "Union3D"
    columns, srid, solids = _solids(in_feature_class, tool, "in_feature_class")
    optimise = str(disable_optimization or "ENABLE").upper() != "DISABLE"
    sets: dict = {}
    for s in solids:
        key = s[1].get(group_field) if given(group_field) and not optimise else None
        if given(group_field) and not optimise and group_field not in s[1]:
            raise ExecuteError(f"ERROR 000728: field {group_field} does not exist.")
        sets.setdefault(key, []).append(s)
    if given(group_field) and optimise:
        AddWarning("Union3D: optimization is enabled, so the group field is ignored, as documented.")
    everything = str(output_all or "ENABLE").upper() != "DISABLE"
    out, related = [], []
    for members in sets.values():
        for group in _overlapping_groups(members):
            if len(group) == 1 and not everything:
                continue
            merged = members[group[0]][2]
            for i in group[1:]:
                merged = merged | members[i][2]
            out.append(([], merged.shape()))
            related.append([members[i][0] for i in group])
    path = M.write(out_feature_class, srid, [], out)
    outputs = [path]
    if given(out_table):
        cols = [_gpkg.Column("Output_ID", "Integer"), _gpkg.Column("Input_ID", "Integer")]
        table = _table(out_table, cols)
        with da.SearchCursor(path, ["OID@"]) as rows:
            oids = [r[0] for r in rows]
        with da.InsertCursor(table, ["Output_ID", "Input_ID"]) as cursor:
            for oid, inputs in zip(oids, related):
                for i in inputs:
                    cursor.insertRow([oid, i])
        outputs.append(table)
    return Result(*outputs)


def Difference3D(in_features_minuend, in_features_subtrahend, out_feature_class, out_table=None):
    """Each closed multipatch less the volumes of the subtracting ones it overlaps (manifold3d's difference); one
    wholly inside a subtracting feature removed, as documented; no input fields kept. ``out_table`` rows hold
    ``Output_ID``, ``Minuend_ID`` and ``Subtrahend``.

    INFERRED: a feature no subtracting feature overlaps is copied as it is, with one table row whose Subtrahend is −1;
    an output overlapped by several subtracting features has a row for each."""
    from . import da
    from .analysis import _table
    tool = "Difference3D"
    _, srid, minuends = _solids(in_features_minuend, tool, "in_features_minuend")
    _, _, subtrahends = _solids(in_features_subtrahend, tool, "in_features_subtrahend")
    out, related = [], []
    for oid, _, solid, box in minuends:
        cut = []
        result = solid
        for soid, _, other, obox in subtrahends:
            if not _overlap(box, obox):
                continue
            if (result & other).empty():
                continue
            result = result - other
            cut.append(soid)
        if result.empty():
            continue
        out.append(([], result.shape()))
        related.append((oid, cut or [-1]))
    path = M.write(out_feature_class, srid, [], out)
    outputs = [path]
    if given(out_table):
        cols = [_gpkg.Column("Output_ID", "Integer"), _gpkg.Column("Minuend_ID", "Integer"),
                _gpkg.Column("Subtrahend", "Integer")]
        table = _table(out_table, cols)
        with da.SearchCursor(path, ["OID@"]) as rows:
            oids = [r[0] for r in rows]
        with da.InsertCursor(table, [c.name for c in cols]) as cursor:
            for out_oid, (minuend, cuts) in zip(oids, related):
                for s in cuts:
                    cursor.insertRow([out_oid, minuend, s])
        outputs.append(table)
    return Result(*outputs)


# ---- Buffer3D


def _circle(centre, axis, radius, segments):
    axis = np.asarray(axis, float)
    axis = axis / np.linalg.norm(axis)
    helper = np.array([1.0, 0, 0]) if abs(axis[0]) < 0.9 else np.array([0, 1.0, 0])
    u = np.cross(axis, helper)
    u /= np.linalg.norm(u)
    v = np.cross(axis, u)
    angles = 2 * np.pi * np.arange(segments) / segments
    return np.asarray(centre, float) + radius * (np.outer(np.cos(angles), u) + np.outer(np.sin(angles), v))


def _sphere(centre, radius, segments):
    """A UV sphere of ``segments`` meridians and ``segments`` // 2 parallels' bands, as the hull of its vertices."""
    stacks = max(segments // 2, 3)
    pts = [np.asarray(centre, float) + [0, 0, radius], np.asarray(centre, float) - [0, 0, radius]]
    for k in range(1, stacks):
        phi = np.pi * k / stacks
        ring = np.column_stack([np.cos(2 * np.pi * np.arange(segments) / segments) * np.sin(phi),
                                np.sin(2 * np.pi * np.arange(segments) / segments) * np.sin(phi),
                                np.full(segments, np.cos(phi))])
        pts.extend(np.asarray(centre, float) + radius * ring)
    return np.array(pts)


def _hull_solid(points, origin):
    import manifold3d
    return M.Solid(manifold3d.Manifold.hull_points(np.ascontiguousarray(np.asarray(points) - origin, np.float64)),
                   origin)


def buffer_solid(line_xyz, radius, segments, round_joints):
    """A 3D buffer of a point (one row) or a polyline as a Solid: a sphere for a point; for a line, a cylinder of
    ``segments`` sides around each segment, joined at each inner vertex by a sphere (ROUND) or by the convex hull of
    the two cylinders' end circles (STRAIGHT), its ends flat (STRAIGHT) or round (ROUND)."""
    import manifold3d
    line_xyz = np.asarray(line_xyz, float).reshape(-1, 3)
    origin = line_xyz.mean(axis=0)
    if len(line_xyz) == 1:
        return _hull_solid(_sphere(line_xyz[0], radius, segments), origin)
    keep = np.concatenate([[True], np.linalg.norm(np.diff(line_xyz, axis=0), axis=1) > 0])
    line_xyz = line_xyz[keep]
    if len(line_xyz) == 1:
        return _hull_solid(_sphere(line_xyz[0], radius, segments), origin)
    pieces = []
    for a, b in zip(line_xyz[:-1], line_xyz[1:]):
        axis = b - a
        pieces.append(_hull_solid(np.concatenate([_circle(a, axis, radius, segments),
                                                  _circle(b, axis, radius, segments)]), origin).manifold)
    for i in range(1, len(line_xyz) - 1):
        if round_joints:
            pieces.append(_hull_solid(_sphere(line_xyz[i], radius, segments), origin).manifold)
        else:
            before, after = line_xyz[i] - line_xyz[i - 1], line_xyz[i + 1] - line_xyz[i]
            pieces.append(_hull_solid(np.concatenate([_circle(line_xyz[i], before, radius, segments),
                                                      _circle(line_xyz[i], after, radius, segments)]),
                                      origin).manifold)
    if round_joints:
        for end in (line_xyz[0], line_xyz[-1]):
            pieces.append(_hull_solid(_sphere(end, radius, segments), origin).manifold)
    merged = manifold3d.Manifold.batch_boolean(pieces, manifold3d.OpType.Add)
    return M.Solid(merged, origin)


def Buffer3D(in_features, out_feature_class, buffer_distance_or_field, buffer_joint_type="STRAIGHT",
             buffer_quality=20, simplification_tolerance=None):
    """Points buffered to spheres and lines to cylinders in 3D, as closed multipatches with the input's fields. The
    distance is a linear unit or a field. ``buffer_quality`` (6–60) is the number of segments around a circle; a
    line's joints are STRAIGHT (filled by the hull of the two cylinders' end circles, its ends flat) or ROUND
    (spheres at every vertex, its ends round). ``simplification_tolerance`` first simplifies the line by 3D
    Douglas–Peucker, as Simplify 3D Line does. The cylinders and spheres are unioned by manifold3d.

    INFERRED (ADR-238 §5.2): the faceting — a circle of ``buffer_quality`` sides, a sphere of as many meridians and
    half as many bands; what STRAIGHT and ROUND joints are, the page saying only that the type sets "the buffer shape
    between line vertices"; a 2D feature's z is 0; the input's fields are kept (the page names no output fields)."""
    from .ddd import _dp3
    from .analysis import _schema
    tool = "Buffer3D"
    quality = int(buffer_quality if given(buffer_quality) else 20)
    if not 6 <= quality <= 60:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: buffer_quality must be 6 to 60.")
    joint = str(buffer_joint_type or "STRAIGHT").upper()
    if joint not in ("STRAIGHT", "ROUND"):
        raise ValueError(f"arcpy.ddd.{tool}: buffer_joint_type '{buffer_joint_type}' is not STRAIGHT or ROUND.")
    columns, srid, rows = M.read(in_features)
    kind = _schema(in_features)[1]
    if kind not in ("Point", "Multipoint", "Polyline"):
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the input must be points or lines.")
    by_field = next((c.name for c in columns if c.name.lower() == str(buffer_distance_or_field).lower()), None)
    fixed = None if by_field else _distance(buffer_distance_or_field, srid)
    tolerance = _distance(simplification_tolerance, srid) if given(simplification_tolerance) else None
    out = []
    for _, values, shape in rows:
        if shape is None or shape.is_empty:
            continue
        radius = float(values[by_field]) if by_field else fixed
        if not radius or radius <= 0:
            continue
        if kind == "Polyline":
            parts = _lines_of(shape)
            if tolerance:
                parts = [p[_dp3(p, tolerance)] for p in parts]
        else:
            parts = [p.reshape(1, 3) for p in _xyz(shape)]
        solid = None
        for part in parts:
            piece = buffer_solid(part, radius, quality, joint == "ROUND")
            solid = piece if solid is None else solid | piece
        if solid is not None:
            out.append(([values[c.name] for c in columns], solid.shape()))
    path = M.write(out_feature_class, srid, columns, out)
    return Result(path)


# ---- FenceDiagram


def FenceDiagram(in_line_features, in_surface, out_feature_class, method="LINEAR", floor_height=None,
                 ceiling_height=None, sample_distance=None):
    """Vertical faces under lines between surfaces, as one multipatch per line with the line's fields: the line is
    sampled at its vertices, at every ``sample_distance`` and (on a TIN) where it crosses the TIN's edges; each
    surface's height there is bilinear on a raster, as documented, or linear on a TIN; the faces fill the space
    between each surface's profile and the next, from the floor height, when given, to the ceiling height, when
    given. The default sample distance is a raster's cell size, as documented, and on a TIN its edges' crossings.

    Refused: NATURAL_NEIGHBORS (ADR-229 §5.4). INFERRED: the profiles are ordered by their mean height; with several
    surfaces the default distance is the smallest cell size; a sample where a surface has no value leaves a gap in
    that band; one feature per line."""
    tool = "FenceDiagram"
    surfaces = [_Heights(s, tool, method) for s in _list(in_surface)]
    if not surfaces:
        raise ExecuteError(f"ERROR 000735: arcpy.ddd.{tool} needs in_surface.")
    columns, srid, rows = M.read(in_line_features)
    srid = srid or surfaces[0].srid
    if given(sample_distance):
        step = _distance(sample_distance, srid)
    else:
        cells = [s.cell() for s in surfaces if s.cell()]
        step = min(cells) if cells else None
    floor = _distance(floor_height, srid) if given(floor_height) else None
    ceiling = _distance(ceiling_height, srid) if given(ceiling_height) else None
    out = []
    for _, values, shape in rows:
        faces = []
        for part in [g for g in getattr(shapely.force_2d(shape), "geoms", [shapely.force_2d(shape)])
                     if g.geom_type == "LineString"]:
            d = {0.0, part.length, *[part.project(shapely.Point(c)) for c in part.coords]}
            if step:
                d.update(np.arange(0, part.length, step).tolist())
            for s in surfaces:
                d.update(s.crossings(part))
            xy = np.array([part.interpolate(v).coords[0] for v in sorted(d)])
            profiles = [s.z(xy[:, 0], xy[:, 1]) for s in surfaces]
            profiles.sort(key=lambda p: np.nanmean(p) if np.isfinite(p).any() else np.inf)
            if floor is not None:
                profiles.insert(0, np.full(len(xy), floor))
            if ceiling is not None:
                profiles.append(np.full(len(xy), ceiling))
            for low, high in zip(profiles[:-1], profiles[1:]):
                for i in range(len(xy) - 1):
                    z = [low[i], low[i + 1], high[i + 1], high[i]]
                    if not np.all(np.isfinite(z)):
                        continue
                    a0, b0 = (*xy[i], low[i]), (*xy[i + 1], low[i + 1])
                    a1, b1 = (*xy[i], high[i]), (*xy[i + 1], high[i + 1])
                    faces += [[a0, b0, b1], [a0, b1, a1]]
        if faces:
            out.append(([values[c.name] for c in columns], M.from_triangles(np.array(faces))))
    path = M.write(out_feature_class, srid, columns, out)
    return Result(path)


# ---- LasBuildingMultipatch, SetLasClassCodesUsingRaster


def _ground_of(ground, footprint, values, columns, tool):
    """A footprint's base: its ground field's value, or the lowest height of the ground surface on its boundary, as
    documented."""
    field = next((c.name for c in columns if c.name.lower() == str(ground).lower()), None)
    if field is not None:
        return float(values[field])
    surface = ground if isinstance(ground, _Heights) else _Heights(ground, tool)
    xy = np.concatenate([np.array(r.coords) for p in M.faces(footprint) for r in [p.exterior, *p.interiors]])
    z = surface.z(xy[:, 0], xy[:, 1])
    if not np.isfinite(z).any():
        return None
    return float(np.nanmin(z))


def LasBuildingMultipatch(in_las_dataset, in_features, ground, out_feature_class,
                          point_selection="BUILDING_CLASSIFIED_POINTS", simplification=None, sampling_resolution=None,
                          min_height_field=None, max_height_field=None):
    """Building models from lidar, as documented: for each footprint, a TIN of the LAS points over it, the footprint
    its clip polygon, turned into a multipatch closed down to the ground -- the ground a field of the footprints (in
    the LAS z unit) or a raster or TIN surface, whose lowest height on the footprint's boundary is the base. The
    points are the building class (6), the layer's filtered points, or all points; ``sampling_resolution`` keeps the
    highest point of each bin; points outside a footprint's minimum and maximum height fields are ignored.

    Refused: ``simplification`` -- the page gives a z tolerance, not how points are removed to meet it. INFERRED: the
    footprint's vertices take the height of the nearest point, so that the roof reaches the footprint's edge; bins
    start at the footprint's lower-left corner; withheld points are never used; BUILDING_CLASSIFIED_POINTS and
    ALL_POINTS ignore the layer's filters; the min and max height fields are absolute z; the footprints' fields are
    kept; a footprint over fewer than three points writes no feature and a warning."""
    from scipy.spatial import Delaunay, QhullError, cKDTree
    from . import _las
    from ._tin import Surface
    tool = "LasBuildingMultipatch"
    if given(simplification):
        _refuse(tool, "simplification", simplification, _SIMPLIFY)
    selection = str(point_selection or "BUILDING_CLASSIFIED_POINTS").upper()
    if selection not in ("BUILDING_CLASSIFIED_POINTS", "LAYER_FILTERED_POINTS", "ALL_POINTS"):
        raise ValueError(f"arcpy.ddd.{tool}: point_selection '{point_selection}' is not one of "
                         "BUILDING_CLASSIFIED_POINTS, LAYER_FILTERED_POINTS, ALL_POINTS.")
    found, dataset = _las.points(in_las_dataset, tool, filtered=selection == "LAYER_FILTERED_POINTS")
    keep = np.ones(len(found["x"]), bool)
    if selection == "BUILDING_CLASSIFIED_POINTS":
        keep &= found["classification"] == 6
    if selection != "LAYER_FILTERED_POINTS" and "withheld" in found:
        keep &= ~np.asarray(found["withheld"]).astype(bool)
    xyz = np.column_stack([found["x"], found["y"], found["z"]]).astype(float)[keep]
    columns, srid, rows = M.read(in_features)
    srid = srid or dataset.srid
    step = _distance(sampling_resolution, srid) if given(sampling_resolution) else None
    surface = None if any(c.name.lower() == str(ground).lower() for c in columns) else _Heights(ground, tool)
    located = shapely.points(xyz[:, 0], xyz[:, 1])
    out = []
    for oid, values, shape in rows:
        if shape is None or shape.is_empty:
            continue
        footprint = shapely.force_2d(shape)
        pts = xyz[shapely.covers(footprint, located)]
        if given(min_height_field):
            pts = pts[pts[:, 2] >= float(values[min_height_field])]
        if given(max_height_field):
            pts = pts[pts[:, 2] <= float(values[max_height_field])]
        if step and len(pts):
            x0, y0 = footprint.bounds[:2]
            cells = np.column_stack([np.floor((pts[:, 0] - x0) / step), np.floor((pts[:, 1] - y0) / step)])
            order = np.lexsort((-pts[:, 2], cells[:, 1], cells[:, 0]))
            _, first = np.unique(cells[order], axis=0, return_index=True)
            pts = pts[order[first]]
        if len(pts):
            xy, inverse = np.unique(pts[:, :2], axis=0, return_inverse=True)
            z = np.full(len(xy), -np.inf)
            np.maximum.at(z, inverse.ravel(), pts[:, 2])
            pts = np.column_stack([xy, z])
        if len(pts) < 3:
            AddWarning(f"{tool}: footprint {oid} covers fewer than three points and writes no feature.")
            continue
        base = _ground_of(surface if surface is not None else ground, footprint, values, columns, tool)
        if base is None:
            AddWarning(f"{tool}: footprint {oid} has no ground height and writes no feature.")
            continue
        edge = np.concatenate([np.array(r.coords)[:-1] for p in M.faces(footprint)
                               for r in [p.exterior, *p.interiors]])
        _, nearest = cKDTree(pts[:, :2]).query(edge)
        nodes = np.concatenate([pts, np.column_stack([edge, pts[nearest, 2]])])
        nodes = nodes[np.unique(np.round(nodes[:, :2], 9), axis=0, return_index=True)[1]]
        try:
            triangulation = Delaunay(nodes[:, :2])
        except QhullError:
            AddWarning(f"{tool}: footprint {oid}'s points lie on a line and write no feature.")
            continue
        roof = Surface(nodes, triangulation.simplices, srid=srid)
        tris = [closed_between(p, roof, base) for p in M.faces(footprint)]
        out.append(([values[c.name] for c in columns], M.from_triangles(np.concatenate(tris))))
    path = M.write(out_feature_class, srid, columns, out)
    return Result(path)


def SetLasClassCodesUsingRaster(in_las_dataset, in_raster, compute_stats="COMPUTE_STATS", extent=None, boundary=None,
                                process_entire_files="PROCESS_EXTENT", update_pyramid="UPDATE_PYRAMID"):
    """Each LAS point given the class code of the integer raster cell it lies in, as documented: a NoData cell
    changes nothing, and a value the file's point format cannot hold (beyond 0-31, or 0-255 for formats 6 to 10) is
    ignored; the layer's filters are honoured; an extent or boundary limits the points changed, unless
    PROCESS_ENTIRE_FILES. A raster in another coordinate system is read through the points' projection to it, as the
    page says it is projected on the fly. ``update_pyramid`` has no pyramid to update in grapy.

    INFERRED: a point on a cell's left or top edge is in that cell, as a raster's cell owns its upper-left edges."""
    from . import _las
    from ._raster import as_raster
    tool = "SetLasClassCodesUsingRaster"
    raster = as_raster(in_raster, f"arcpy.ddd.{tool}")
    if not raster.isInteger:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the raster must be integer, as documented.")
    g = raster._grid
    found, dataset = _las.points(in_las_dataset, tool)
    area = _las._region(extent, boundary, process_entire_files)
    xs, ys = np.asarray(found["x"], float), np.asarray(found["y"], float)
    if g.srid and dataset.srid and g.srid != dataset.srid:
        from pyproj import Transformer
        xs, ys = Transformer.from_crs(dataset.srid, g.srid, always_xy=True).transform(xs, ys)
    columns = np.floor((np.asarray(xs) - g.left) / g.width).astype(np.int64)
    rows = np.floor((g.top - np.asarray(ys)) / g.height).astype(np.int64)
    on = (rows >= 0) & (rows < g.rows) & (columns >= 0) & (columns < g.columns)
    if area is not None:
        on &= shapely.covers(area, shapely.points(found["x"], found["y"]))
    value = np.full(len(xs), -1, np.int64)
    r, c = rows[on], columns[on]
    good = ~np.asarray(raster._mask)[r, c]
    picked = np.nonzero(on)[0][good]
    value[picked] = np.asarray(raster._array)[r[good], c[good]].astype(np.int64)
    changed = []
    for number, path in enumerate(dataset.file_paths()):
        mine = (found["file"] == number) & (value >= 0)
        if not mine.any():
            continue
        las = _las.read_file(path)
        limit = 255 if las.header.point_format.id >= 6 else 31
        mine &= value <= limit
        if not mine.any():
            continue
        classes = np.asarray(las.classification, np.int64).copy()
        classes[found["index"][mine]] = value[mine]
        las.classification = classes.astype(np.uint8)
        _las._write_las(path, las)
        changed.append(path)
    _las._update_stats(in_las_dataset, changed, str(compute_stats or "").upper() == "COMPUTE_STATS")
    return Result(_las._lasd_path(in_las_dataset))


# ---- the sun: SunShadowVolume, SunShadowFrequency


def _times(start, end, interval, unit, tool):
    """The local times from ``start`` to ``end`` every ``interval`` ``unit``; an end given as a time alone is on the
    start's date."""
    import datetime as dt
    from . import _solar
    start = _solar.parse_time(start, tool)
    if not isinstance(start, dt.datetime):
        raise ValueError(f"arcpy.ddd.{tool}: the start needs a date.")
    if not given(end):
        return [start]
    end = _solar.parse_time(end, tool)
    if isinstance(end, dt.time):
        end = dt.datetime.combine(start.date(), end)
    if end < start:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the end is before the start.")
    if not given(interval):
        return [start, end] if end != start else [start]
    unit_word = str(unit or "DAYS").upper()
    spans = {"DAYS": dt.timedelta(days=1), "HOURS": dt.timedelta(hours=1), "MINUTES": dt.timedelta(minutes=1)}
    if unit_word not in spans:
        raise ValueError(f"arcpy.ddd.{tool}: iteration_unit '{unit}' is not DAYS, HOURS or MINUTES.")
    span = spans[unit_word] * float(interval)
    if span.total_seconds() <= 0:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the interval must be positive.")
    out, t = [], start
    while t <= end:
        out.append(t)
        t += span
    return out


@_equivalent("the swept shadow volume: the union of each face's prism along the light rays",
             "Crow 1977; sun position NOAA / Meeus 1998")
def SunShadowVolume(in_features, start_date_and_time, out_feature_class, adjusted_for_dst="NOT_ADJUSTED_FOR_DST",
                    time_zone=None, end_date_and_time=None, iteration_interval=None, iteration_unit="DAYS"):
    """EQUIVALENT: the volume each multipatch's shadow fills, as a closed multipatch, for each sun position from the
    start to the end every interval: the feature swept along the parallel light rays -- the union, for each of its
    triangles, of the prism between the triangle and its copy moved along the rays (Crow 1977's shadow volume) --
    until the shadow of its highest point reaches the height of its lowest. The sun's position is NOAA's (Meeus 1998),
    computed, as documented, at the first feature of the first class. Fields as documented: ``SOURCE``,
    ``SOURCE_ID``, ``DATE_TIME`` (local), ``AZIMUTH``, ``VERT_ANGLE``; no volume when the sun is down or overhead.

    An equivalent because the page does not say how far a shadow is extruded nor where its end plane lies. INFERRED:
    the default time zone is the system's standard offset; a feature with no height (all at one z) casts no volume and
    is warned of; with an end and no interval, the start and the end; ``HUGS_FEATR`` is never added, every shadow
    being built from the feature itself."""
    import time as _time
    import manifold3d
    from . import _solar
    tool = "SunShadowVolume"
    classes = []
    for path in _list(in_features):
        M.require(path, "ddd", tool)
        classes.append((path, *M.read(path)))
    first = next(((shape, srid) for _, _, srid, rows in classes for _, _, shape in rows if shape is not None), None)
    if first is None:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the input holds no features.")
    centre = _xyz(first[0]).mean(axis=0)
    lon, lat = _solar.place(centre[0], centre[1], first[1])
    zone = _solar.offset(time_zone, default=-_time.timezone / 3600)
    dst = str(adjusted_for_dst or "").upper() in ("ADJUSTED_FOR_DST", "ADJUST_FOR_DST", "DST", "TRUE")
    times = _times(start_date_and_time, end_date_and_time, iteration_interval, iteration_unit, tool)
    columns = [_gpkg.Column("SOURCE", "String"), _gpkg.Column("SOURCE_ID", "Integer"),
               _gpkg.Column("DATE_TIME", "Date"), _gpkg.Column("AZIMUTH", "Double"),
               _gpkg.Column("VERT_ANGLE", "Double")]
    out = []
    for local in times:
        azimuth, elevation = _solar.position(_solar.to_utc(local, zone, dst), lon, lat)
        if elevation <= 0 or elevation >= 90:
            AddWarning(f"{tool}: the sun is {'down' if elevation <= 0 else 'overhead'} at {local}; no volume.")
            continue
        ray = -np.array(_solar.towards(azimuth, elevation))
        for path, _, srid, rows in classes:
            name = _gpkg.split(path)[1] or path
            for oid, _, shape in rows:
                tris = M.triangles(shape)
                if not len(tris):
                    continue
                drop = float(tris[:, :, 2].max() - tris[:, :, 2].min())
                if drop <= 0:
                    AddWarning(f"{tool}: feature {oid} has no height and casts no volume.")
                    continue
                length = drop / math.sin(math.radians(elevation))
                origin = tris.reshape(-1, 3).mean(axis=0)
                prisms = [manifold3d.Manifold.hull_points(np.ascontiguousarray(
                    np.concatenate([t, t + length * ray]) - origin, np.float64)) for t in tris]
                solid = M.Solid(manifold3d.Manifold.batch_boolean(prisms, manifold3d.OpType.Add), origin)
                if solid.empty():
                    continue
                out.append(([name, oid, local, azimuth, elevation], solid.shape()))
    path = M.write(out_feature_class, first[1], columns, out)
    return Result(path)


def _interval(value):
    """A Time Unit ("15 Minutes") as a timedelta."""
    import datetime as dt
    parts = str(value).split()
    number = float(parts[0])
    unit = (parts[1] if len(parts) > 1 else "Minutes").upper().rstrip("S")
    seconds = {"SECOND": 1, "MINUTE": 60, "HOUR": 3600, "DAY": 86400, "WEEK": 604800}.get(unit)
    if seconds is None:
        raise ValueError(f"'{value}' is not a time unit grapy reads (Seconds, Minutes, Hours, Days, Weeks).")
    return dt.timedelta(seconds=number * seconds)


def SunShadowFrequency(in_features, ground, out_raster, cell_size=None, start_time=None, end_time=None,
                       time_interval=None, time_zone=None, dst="NO_DST", max_shadow_length=None):
    """How many times each ground cell is in the shadow of multipatch obstructions, as an integer raster: for each
    day from the start date to the end date, from the start time to the end time every interval, the sun's position
    (NOAA's, Meeus 1998) is computed at the ground's centre, times the sun is below the horizon are skipped, and a
    cell is in shadow when the straight line from its centre on the ground towards the sun meets a face of an
    obstruction (Moller and Trumbore 1997) -- within ``max_shadow_length`` of the cell horizontally, when given.

    INFERRED: without an interval, the start time alone on each day; without an end, the start alone; the time zone
    is UTC when none is given (the page gives no default); DST reads the times as one hour ahead of standard; a cell
    size other than the ground's makes a grid over the ground's extent whose cells take the ground's bilinear height
    at their centres; the ground's NoData cells are NoData."""
    import datetime as dt
    from . import _solar
    from ._raster import Raster, _Grid, as_raster
    tool = "SunShadowFrequency"
    tris = []
    for path in _list(in_features):
        M.require(path, "ddd", tool)
        for _, _, shape in M.read(path)[2]:
            t = M.triangles(shape)
            if len(t):
                tris.append(t)
    tris = np.concatenate(tris) if tris else np.zeros((0, 3, 3))
    surface = as_raster(ground, f"arcpy.ddd.{tool}")
    g = surface._grid
    size = _distance(cell_size, g.srid) if given(cell_size) else g.width
    if given(cell_size) and abs(size - g.width) > 1e-12:
        columns, rows = int(math.ceil((g.right - g.left) / size)), int(math.ceil((g.top - g.bottom) / size))
        grid = _Grid(g.left, g.top, size, size, rows, columns, g.srid)
        xs, ys = grid.centres()
        gx, gy = np.meshgrid(xs, ys)
        z = _bilinear(surface, gx.ravel(), gy.ravel()).reshape(gx.shape)
    else:
        grid = g
        xs, ys = grid.centres()
        gx, gy = np.meshgrid(xs, ys)
        z = np.where(np.asarray(surface._mask), np.nan, np.asarray(surface._array, float))
    lon, lat = _solar.place((g.left + g.right) / 2, (g.top + g.bottom) / 2, g.srid)
    zone = _solar.offset(time_zone, default=0.0)
    daylight = str(dst or "NO_DST").upper() == "DST"
    start = _solar.parse_time(start_time, tool) if given(start_time) else dt.datetime.now().replace(microsecond=0)
    end = _solar.parse_time(end_time, tool) if given(end_time) else start
    if end < start:
        raise ExecuteError(f"ERROR: arcpy.ddd.{tool}: the end time is before the start time.")
    step = _interval(time_interval) if given(time_interval) else None
    times = []
    day = start.date()
    while day <= end.date():
        t = dt.datetime.combine(day, start.time())
        stop = dt.datetime.combine(day, end.time())
        while t <= stop:
            times.append(t)
            if step is None:
                break
            t += step
        day += dt.timedelta(days=1)
    limit = _distance(max_shadow_length, g.srid) if given(max_shadow_length) else None
    valid = np.isfinite(z)
    origins = np.column_stack([gx.ravel(), gy.ravel(), np.nan_to_num(z.ravel())])[valid.ravel()]
    counts = np.zeros(len(origins), np.int32)
    top = float(tris[:, :, 2].max()) if len(tris) else 0.0
    up = 0
    for local in times:
        azimuth, elevation = _solar.position(_solar.to_utc(local, zone, daylight), lon, lat)
        if elevation <= 0:
            continue
        up += 1
        if not len(tris) or not len(origins):
            continue
        sun = np.array(_solar.towards(azimuth, elevation))
        reach = limit / math.cos(math.radians(elevation)) if limit else \
            max(top - float(origins[:, 2].min()), 0.0) / math.sin(math.radians(elevation)) + 1.0
        counts += _shadowed(origins, sun, reach, tris).astype(np.int32)
    out = np.zeros(z.shape, np.int32)
    out[valid] = counts
    AddMessage(f"{up} sun position(s) above the horizon.")
    raster = Raster._of(out, ~valid, grid)
    return Result(raster.save(out_raster))


def _shadowed(origins, sun, reach, tris, chunk=1024):
    """Whether the segment from each origin towards the sun, ``reach`` long, meets a triangle (Moller-Trumbore)."""
    e1, e2 = tris[:, 1] - tris[:, 0], tris[:, 2] - tris[:, 0]
    h = np.cross(sun, e2)
    a = np.einsum("ij,ij->i", e1, h)
    ok = np.abs(a) > 1e-12
    f = np.where(ok, 1.0 / np.where(ok, a, 1.0), 0.0)
    out = np.zeros(len(origins), bool)
    for start in range(0, len(origins), chunk):
        o = origins[start:start + chunk][:, None, :]
        s = o - tris[None, :, 0]
        u = f[None] * np.einsum("ijk,jk->ij", s, h)
        q = np.cross(s, e1[None])
        v = f[None] * (q @ sun)
        t = f[None] * np.einsum("jk,ijk->ij", e2, q)
        hit = ok[None] & (u >= 0) & (v >= 0) & (u + v <= 1) & (t > 1e-6) & (t <= reach)
        out[start:start + chunk] = hit.any(axis=1)
    return out
