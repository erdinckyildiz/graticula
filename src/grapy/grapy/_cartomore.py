"""arcpy.cartography's tools that a published method makes buildable: equivalents, ADR-224 §5.6.

* GenerateContiguousCartogram — the page names both of its methods: Gastner and Newman's diffusion (2004) and
  Gastner, Seguy and More's flow-based algorithm (2018). Both are built from those papers.
* CreateCartographicPartitions — the page says only that partitions are smaller where features are denser. A
  point-region quadtree (Samet 1984) does exactly that.
* StripMapIndexFeatures — the page names the parameters, the fields and the meaning of each field, but not where a
  page falls on the line. grapy places them by the rule written on the function, and records it as INFERRED.
"""
from __future__ import annotations

import math

import numpy as np

from ._runtime import AddWarning, equivalent as _equivalent


def _c():
    from . import cartography
    return cartography


# ---- GenerateContiguousCartogram ---------------------------------------------------------------------------------

_GRID = 128
_ITERATIONS = 30
_TOLERANCE = 0.01


def _dct_poisson_eigen(rows, cols):
    """The eigenvalues of the 5-point Laplacian with zero-flux edges, in the DCT-II basis."""
    k = 2.0 - 2.0 * np.cos(np.pi * np.arange(rows) / rows)
    m = 2.0 - 2.0 * np.cos(np.pi * np.arange(cols) / cols)
    return -(k[:, None] + m[None, :])


def _gradient(field):
    """Central differences, with the normal component at the grid's edges zero: nothing flows out of the grid."""
    gy, gx = np.gradient(field)
    gx[:, 0] = gx[:, -1] = 0.0
    gy[0, :] = gy[-1, :] = 0.0
    return gx, gy


def _sample(field, xy):
    from scipy.ndimage import map_coordinates
    return map_coordinates(field, [xy[:, 1], xy[:, 0]], order=1, mode="nearest")


def _rk2(xy, velocity, t, dt):
    vx, vy = velocity(t)
    k1 = np.column_stack([_sample(vx, xy), _sample(vy, xy)])
    mid = xy + 0.5 * dt * k1
    vx, vy = velocity(t + 0.5 * dt)
    k2 = np.column_stack([_sample(vx, mid), _sample(vy, mid)])
    return xy + dt * k2


def _flow(rho, xy):
    """Gastner, Seguy and More 2018: the density is made to change linearly in time,
    ρ(t) = (1 − t)·ρ₀ + t·ρ̄. The continuity equation then makes ρ·v the gradient of one potential Ψ, with ∇²Ψ = ρ₀ − ρ̄.
    Ψ is solved once with a cosine transform. Points move with v = ∇Ψ / ρ(t) from t = 0 to t = 1."""
    from scipy.fft import dctn, idctn
    mean = float(rho.mean())
    source = dctn(rho - mean, type=2, norm="ortho")
    eigen = _dct_poisson_eigen(*rho.shape)
    eigen[0, 0] = 1.0
    potential = source / eigen
    potential[0, 0] = 0.0
    gx, gy = _gradient(idctn(potential, type=2, norm="ortho"))

    def velocity(t):
        density = (1.0 - t) * rho + t * mean
        return gx / density, gy / density

    t = 0.0
    while t < 1.0 - 1e-12:
        vx, vy = velocity(t)
        fastest = float(np.sqrt(vx * vx + vy * vy).max()) or 1e-12
        dt = min(1.0 - t, 0.05, 0.3 / fastest)
        xy = _rk2(xy, velocity, t, dt)
        t += dt
    return xy


def _diffusion(rho, xy):
    """Gastner and Newman 2004: the density diffuses, ρ̂(k, t) = ρ̂(k, 0)·exp(λₖ·t). Points move with v = −∇ρ / ρ until
    the slowest mode has decayed to 1/1000."""
    from scipy.fft import dctn, idctn
    spectrum = dctn(rho, type=2, norm="ortho")
    eigen = _dct_poisson_eigen(*rho.shape)
    slowest = -max(eigen[0, 1] if rho.shape[1] > 1 else -1.0, eigen[1, 0] if rho.shape[0] > 1 else -1.0)
    end = math.log(1000.0) / slowest

    def velocity(t):
        density = idctn(spectrum * np.exp(eigen * t), type=2, norm="ortho")
        gx, gy = _gradient(density)
        return -gx / density, -gy / density

    t = 0.0
    while t < end - 1e-9:
        vx, vy = velocity(t)
        fastest = float(np.sqrt(vx * vx + vy * vy).max()) or 1e-12
        dt = min(end - t, 0.3 / fastest, max(1.0, 0.25 * t))
        xy = _rk2(xy, velocity, t, dt)
        t += dt
    return xy


def _rasterise(polygons, densities, sea, origin, cell, shape):
    import shapely
    rows, cols = shape
    xs = origin[0] + (np.arange(cols) + 0.5) * cell
    ys = origin[1] + (np.arange(rows) + 0.5) * cell
    grid = np.full(shape, sea, dtype=np.float64)
    for polygon, density in zip(polygons, densities):
        x0, y0, x1, y1 = polygon.bounds
        c0, c1 = max(0, int((x0 - origin[0]) / cell) - 1), min(cols, int((x1 - origin[0]) / cell) + 2)
        r0, r1 = max(0, int((y0 - origin[1]) / cell) - 1), min(rows, int((y1 - origin[1]) / cell) + 2)
        if c0 >= c1 or r0 >= r1:
            continue
        X, Y = np.meshgrid(xs[c0:c1], ys[r0:r1])
        inside = shapely.contains_xy(polygon, X, Y)
        grid[r0:r1, c0:c1][inside] = density
    return grid


def _rebuild(template, coordinates, at):
    """``template`` with its coordinates read in order from ``coordinates`` starting at ``at[0]``."""
    from shapely.geometry import MultiPolygon, Polygon
    parts = []
    for part in getattr(template, "geoms", [template]):
        rings = []
        for ring in (part.exterior, *part.interiors):
            n = len(ring.coords)
            rings.append(coordinates[at[0]:at[0] + n])
            at[0] += n
        parts.append(Polygon(rings[0], rings[1:]))
    return MultiPolygon(parts)


def _vertices(shape):
    out = []
    for part in getattr(shape, "geoms", [shape]):
        for ring in (part.exterior, *part.interiors):
            out.extend(tuple(c[:2]) for c in ring.coords)
    return out


@_equivalent("Gastner, Seguy and More's flow-based algorithm, or Gastner and Newman's diffusion, on a 128-cell grid, "
             "repeated until every area is within 1% of its share",
             "Gastner and Newman 2004, PNAS 101(20); Gastner, Seguy and More 2018, PNAS 115(10)")
def GenerateContiguousCartogram(in_features, field_name, out_features, method="FLOW-BASED"):
    """EQUIVALENT: each polygon's area made proportional to ``field_name``, its share of the field's total over the
    total area of the polygons kept. Shared boundaries stay shared, because every vertex moves by the same map. The
    page names both methods and cites both papers. ``FLOW-BASED`` (the default) is Gastner, Seguy and More 2018 and
    ``DIFFUSION`` is Gastner and Newman 2004, both on a grid of 128 cells along the longer side of the extent.
    Features with a value of zero or less are omitted, as documented. The input's fields are kept.

    INFERRED: the details that the page leaves to the papers are grapy's own choices:
    - The grid is the polygons' extent padded by half its longer side. The space outside the polygons, and the area
      of an omitted feature, has the polygons' mean density.
    - The density is blurred by a Gaussian of one cell.
    - The projection is repeated, as both papers do, until no area is more than 1% from its share of the polygons'
      current total, or at most 30 times.
    - The result is then scaled about the centre of its extent, so that the polygons' total area is the input's.
      The page says only "proportional".
    Areas are planar in the data's coordinates. The page asks for an equal-area projection; grapy warns when the
    data is geographic.
    """
    import shapely
    from scipy.ndimage import gaussian_filter
    from . import _gpkg, da
    from ._result import Result
    c = _c()
    tool = "GenerateContiguousCartogram"
    key = str(method or "FLOW-BASED").upper().replace("_", "-")
    if key not in ("FLOW-BASED", "DIFFUSION"):
        raise ValueError(f"arcpy.cartography.{tool}: method '{method}' is not one of FLOW-BASED, DIFFUSION.")
    columns, kind, srid, _ = c._schema(in_features)
    if kind != "Polygon":
        raise ValueError(f"arcpy.cartography.{tool} needs polygon features.")
    from ._reference import SpatialReference
    if srid > 0 and SpatialReference(srid).type == "Geographic":
        AddWarning(f"{tool}: the input is in a geographic coordinate system; areas are measured in its degrees. "
                   "Project it to an equal-area projection first.")
    names = [col.name for col in columns]
    if field_name not in names:
        raise ValueError(f"arcpy.cartography.{tool}: '{field_name}' is not a field of the input.")
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows:
        features = [(list(r[:-1]), r[-1]._shape) for r in rows if r[-1] is not None]
    at = names.index(field_name)
    kept = [(values, shape) for values, shape in features
            if values[at] is not None and float(values[at]) > 0 and shape.area > 0]
    if not kept:
        raise ValueError(f"arcpy.cartography.{tool}: no feature has a value above zero.")
    land = sum(shape.area for _, shape in kept)
    total = sum(float(values[at]) for values, _ in kept)
    targets = np.array([float(values[at]) / total * land for values, _ in kept])

    everything = [shape for _, shape in features]
    x0, y0, x1, y1 = shapely.total_bounds(everything)
    longest = max(x1 - x0, y1 - y0)
    pad = 0.5 * longest
    origin = (x0 - pad, y0 - pad)
    cell = (longest + 2 * pad) / _GRID
    shape = (max(2, int(math.ceil((y1 - y0 + 2 * pad) / cell))), max(2, int(math.ceil((x1 - x0 + 2 * pad) / cell))))

    templates = [s for _, s in kept]
    points = np.array([v for s in templates for v in _vertices(s)], dtype=np.float64)
    grid_xy = (points - np.array(origin)) / cell - 0.5
    move = _flow if key == "FLOW-BASED" else _diffusion
    polygons = templates
    shares = targets / land
    for _ in range(_ITERATIONS):
        areas = np.array([p.area for p in polygons])
        wanted = shares * areas.sum()
        if np.max(np.abs(areas / wanted - 1.0)) <= _TOLERANCE:
            break
        rho = _rasterise(polygons, wanted / areas, 1.0, origin, cell, shape)
        rho = gaussian_filter(rho, 1.0, mode="nearest")
        grid_xy = move(rho, grid_xy)
        coordinates = [tuple(p) for p in (grid_xy + 0.5) * cell + np.array(origin)]
        position = [0]
        polygons = [_rebuild(t, coordinates, position) for t in templates]

    from shapely import affinity
    now = sum(p.area for p in polygons)
    if now > 0:
        factor = math.sqrt(land / now)
        bx0, by0, bx1, by1 = shapely.total_bounds(polygons)
        centre = ((bx0 + bx1) / 2, (by0 + by1) / 2)
        polygons = [affinity.scale(p, factor, factor, origin=centre) for p in polygons]

    out = c._out(out_features)
    _gpkg.create(out, "Polygon", srid, list(columns))
    with da.InsertCursor(out, [*names, "SHAPE@"]) as written:
        for (values, _), polygon in zip(kept, polygons):
            written.insertRow((*values, polygon))
    return Result(out)


# ---- CreateCartographicPartitions --------------------------------------------------------------------------------

@_equivalent("a point-region quadtree: a cell holding more than feature_count features or vertices is split in four",
             "Samet 1984, ACM Computing Surveys 16(2)")
def CreateCartographicPartitions(in_features, out_features, feature_count=50000, partition_method="FEATURES"):
    """EQUIVALENT: polygons that cover the inputs' extent, each holding no more than ``feature_count`` features
    (``FEATURES``, the default) or vertices (``VERTICES``). They are smaller where the inputs are denser, as the page
    says. A cell holding more is split into four equal quadrants, recursively: a point-region quadtree (Samet 1984).
    The page gives no layout, and this is grapy's. Empty cells are kept, so the mesh covers the extent. The minimum
    count is 500, as documented.

    INFERRED: these choices are grapy's own.
    - A feature counts at one point, its ``point_on_surface``, so it falls in one partition.
    - A point on a split line goes to the upper or right cell.
    - A cell is not split more than 24 times, so coincident points cannot split it without end.
    - The output carries no fields of its own: the page names none.
    - Its coordinate system is the first input's.
    """
    import shapely
    from shapely.geometry import box
    from . import _gpkg, da
    from ._result import Result
    c = _c()
    tool = "CreateCartographicPartitions"
    limit = int(feature_count)
    if limit < 500:
        raise ValueError(f"arcpy.cartography.{tool}: feature_count must be at least 500.")
    key = str(partition_method or "FEATURES").upper()
    if key not in ("FEATURES", "VERTICES"):
        raise ValueError(f"arcpy.cartography.{tool}: partition_method '{partition_method}' is not one of FEATURES, "
                         "VERTICES.")
    inputs = c._list(in_features)
    srid = None
    points = []
    for path in inputs:
        _, _, own, _ = c._schema(path)
        srid = own if srid is None else srid
        from ._reference import SpatialReference
        target = SpatialReference(srid) if own > 0 and srid > 0 and own != srid else None
        with da.SearchCursor(path, ["SHAPE@"]) as rows:
            for (geometry,) in rows:
                if geometry is None:
                    continue
                shape = (geometry.projectAs(target) if target else geometry)._shape
                if shape.is_empty:
                    continue
                if key == "FEATURES":
                    points.append(shapely.point_on_surface(shape).coords[0][:2])
                else:
                    points.extend(tuple(xy[:2]) for xy in shapely.get_coordinates(shape))
    if not points:
        raise ValueError(f"arcpy.cartography.{tool}: the inputs hold no features.")
    xy = np.array(points, dtype=np.float64)
    x0, y0 = xy.min(axis=0)
    x1, y1 = xy.max(axis=0)
    if x1 == x0:
        x1 = x0 + 1.0
    if y1 == y0:
        y1 = y0 + 1.0
    cells = []

    def split(bounds, members, depth):
        if len(members) <= limit or depth >= 24:
            cells.append(bounds)
            return
        bx0, by0, bx1, by1 = bounds
        mx, my = (bx0 + bx1) / 2, (by0 + by1) / 2
        px, py = xy[members, 0], xy[members, 1]
        east, north = px >= mx, py >= my
        for quadrant, wanted in (((bx0, by0, mx, my), ~east & ~north), ((mx, by0, bx1, my), east & ~north),
                                 ((bx0, my, mx, by1), ~east & north), ((mx, my, bx1, by1), east & north)):
            split(quadrant, members[wanted], depth + 1)

    split((x0, y0, x1, y1), np.arange(len(xy)), 0)
    out = c._out(out_features)
    _gpkg.create(out, "Polygon", srid or 0, [])
    with da.InsertCursor(out, ["SHAPE@"]) as written:
        for bounds in cells:
            written.insertRow((box(*bounds),))
    return Result(out)


# ---- StripMapIndexFeatures ---------------------------------------------------------------------------------------

def _length(value, srid, scale, page_units):
    from .analysis import _distance
    from . import _units
    if not page_units:
        return _distance(value, srid)
    parts = str(value).split()
    number = float(parts[0])
    inches = number if len(parts) == 1 else number * (_units.linear(parts[1]) or 0.0254) / 0.0254
    metres = inches * 0.0254 * float(scale or 1)
    from ._reference import SpatialReference
    per = SpatialReference(srid).metersPerUnit if srid > 0 else 1.0
    return metres / (per or 1.0)


def _arc_at_distance(line, start, distance):
    """The first arc position after ``start`` whose point is at least ``distance`` from the point at ``start``, in a
    straight line, or the line's end when none is."""
    from shapely.geometry import Point
    origin = line.interpolate(start)
    coordinates = list(line.coords)
    walked = 0.0
    for a, b in zip(coordinates, coordinates[1:]):
        seg = math.dist(a[:2], b[:2])
        if walked + seg <= start or seg == 0:
            walked += seg
            continue
        lo = max(0.0, start - walked)
        if Point(b[:2]).distance(origin) >= distance:
            # the circle of radius ``distance`` round ``origin`` is crossed on this segment: solve for it
            ax, ay = a[0] + (b[0] - a[0]) * lo / seg, a[1] + (b[1] - a[1]) * lo / seg
            dx, dy = (b[0] - a[0]) / seg, (b[1] - a[1]) / seg
            fx, fy = ax - origin.x, ay - origin.y
            bq = fx * dx + fy * dy
            cq = fx * fx + fy * fy - distance * distance
            root = -bq + math.sqrt(max(0.0, bq * bq - cq))
            return walked + lo + max(0.0, root)
        walked += seg
    return line.length


def _normalise(angle):
    """An angle folded into (−90, 90] by turning the page half round: north stays toward the top."""
    angle = (angle + 180.0) % 360.0 - 180.0
    if angle > 90.0:
        angle -= 180.0
    elif angle <= -90.0:
        angle += 180.0
    return angle


@_equivalent("pages laid along each connected chain: each starts on the line and points at the line's first point "
             "one page-length away; the next starts where the line is (1 − overlap) of a page-length away",
             "INFERRED placement; the page documents the parameters and fields, not the placement")
def StripMapIndexFeatures(in_features, out_feature_class, use_page_unit="NO_USEPAGEUNIT", scale=None,
                          length_along_line=None, length_perpendicular_to_line=None, page_orientation="HORIZONTAL",
                          overlap_percentage=10, starting_page_number=1, direction_type="WE_NS"):
    """EQUIVALENT: rectangular index polygons along the input lines, with the documented fields: PageNumber, GroupId,
    SeqId, PrevPage, NextPage, LeftPage, RightPage, TopPage, BottomPage and Angle. Lines that do not connect are
    separate groups. Pages are numbered from ``starting_page_number`` through every group in turn. Defaults follow
    the page:
    - the length along the line is 1/100 of the input extent's width;
    - the width across the line is half of that;
    - the overlap is 10%;
    - page units, when used, are inches times ``scale``, which defaults to 1.

    INFERRED, where the page is silent:
    - Placement. The input lines are merged into chains (``line_merge``). Each chain is a group, whose GroupId is
      the ObjectID of the input line at the chain's start. A page starts on the line. Its long axis points at the
      first point on the line one page-length away in a straight line, or at the line's end. It is centred across
      that axis. The next page starts at the first point on the line ``(1 − overlap)`` of a page-length from this
      page's start. The last page is the one whose axis reaches the line's end.
    - Direction. A chain whose ends lie further apart east–west than north–south starts at its west end (``WE_*``)
      or its east end (``EW_*``). Any other chain starts at its north end (``*_NS``) or its south end (``*_SN``).
    - Angle. Angle is the azimuth of the page's top, from north, folded into (−90°, 90°] so that north stays toward
      the top. For ``HORIZONTAL``, the line runs across the page; for ``VERTICAL``, it runs down the page.
    - Neighbours. LeftPage, RightPage, TopPage and BottomPage name the previous and next pages, by the side of the
      rotated page that each one's centre lies toward. The field name NextPage is grapy's: the page says "Next".
    """
    import shapely
    from shapely.geometry import LineString, Point, Polygon
    from . import _gpkg, da
    from ._result import Result
    c = _c()
    tool = "StripMapIndexFeatures"
    columns, kind, srid, _ = c._schema(in_features)
    if kind != "Polyline":
        raise ValueError(f"arcpy.cartography.{tool} needs line features.")
    page_units = str(use_page_unit).upper() in ("USEPAGEUNIT", "TRUE")
    orientation = str(page_orientation or "HORIZONTAL").upper()
    if orientation not in ("HORIZONTAL", "VERTICAL"):
        raise ValueError(f"arcpy.cartography.{tool}: page_orientation '{page_orientation}' is not one of HORIZONTAL, "
                         "VERTICAL.")
    direction = str(direction_type or "WE_NS").upper()
    if direction not in ("WE_NS", "WE_SN", "EW_NS", "EW_SN"):
        raise ValueError(f"arcpy.cartography.{tool}: direction_type '{direction_type}' is not one of WE_NS, WE_SN, "
                         "EW_NS, EW_SN.")
    with da.SearchCursor(in_features, ["OID@", "SHAPE@"]) as rows:
        lines = [(r[0], r[1]._shape) for r in rows if r[1] is not None and not r[1]._shape.is_empty]
    if not lines:
        raise ValueError(f"arcpy.cartography.{tool}: the input holds no lines.")
    x0, _, x1, _ = shapely.total_bounds([s for _, s in lines])
    along = (_length(length_along_line, srid, scale, page_units) if c._given(length_along_line)
             else (x1 - x0) / 100.0)
    across = (_length(length_perpendicular_to_line, srid, scale, page_units) if c._given(length_perpendicular_to_line)
              else along / 2.0)
    if along <= 0 or across <= 0:
        raise ValueError(f"arcpy.cartography.{tool}: the page lengths must be greater than zero.")
    overlap = float(overlap_percentage if c._given(overlap_percentage) else 10) / 100.0
    if not 0 <= overlap < 1:
        raise ValueError(f"arcpy.cartography.{tool}: overlap_percentage must be at least 0 and below 100.")
    parts = [LineString(p.coords) for _, s in lines for p in getattr(s, "geoms", [s])]
    merged = shapely.line_merge(shapely.union_all(parts))
    chains = list(getattr(merged, "geoms", [merged]))

    groups = []
    for chain in chains:
        a, b = chain.coords[0], chain.coords[-1]
        if abs(b[0] - a[0]) >= abs(b[1] - a[1]):
            start_west = direction.startswith("WE")
            reverse = (a[0] > b[0]) if start_west else (a[0] < b[0])
        else:
            start_north = direction.endswith("NS")
            reverse = (a[1] < b[1]) if start_north else (a[1] > b[1])
        if reverse:
            chain = LineString(list(chain.coords)[::-1])
        head = Point(chain.coords[0][:2])
        group_id = min((oid for oid, s in lines if s.distance(head) <= 1e-9), default=lines[0][0])
        groups.append((group_id, chain))
    groups.sort(key=lambda g: g[0])

    pages = []
    number = int(starting_page_number if c._given(starting_page_number) else 1)
    for group_id, chain in groups:
        start, seq, previous_u = 0.0, 1, (1.0, 0.0)
        first = len(pages)
        while True:
            end = _arc_at_distance(chain, start, along)
            p, q = chain.interpolate(start), chain.interpolate(end)
            dx, dy = q.x - p.x, q.y - p.y
            norm = math.hypot(dx, dy)
            u = (dx / norm, dy / norm) if norm > 0 else previous_u
            previous_u = u
            n = (-u[1], u[0])
            h = across / 2.0
            corners = [(p.x + n[0] * h, p.y + n[1] * h), (p.x - n[0] * h, p.y - n[1] * h),
                       (p.x + u[0] * along - n[0] * h, p.y + u[1] * along - n[1] * h),
                       (p.x + u[0] * along + n[0] * h, p.y + u[1] * along + n[1] * h)]
            azimuth = math.degrees(math.atan2(u[0], u[1]))
            angle = _normalise(azimuth - 90.0 if orientation == "HORIZONTAL" else azimuth)
            pages.append({"PageNumber": number, "GroupId": group_id, "SeqId": seq, "Angle": angle,
                          "shape": Polygon(corners)})
            number += 1
            seq += 1
            if end >= chain.length - 1e-9:
                break
            following = _arc_at_distance(chain, start, (1.0 - overlap) * along)
            if following <= start + 1e-12:
                break
            start = following
        run = pages[first:]
        for k, page in enumerate(run):
            page["PrevPage"] = run[k - 1]["PageNumber"] if k > 0 else None
            page["NextPage"] = run[k + 1]["PageNumber"] if k + 1 < len(run) else None
            for side in ("LeftPage", "RightPage", "TopPage", "BottomPage"):
                page[side] = None
            here = page["shape"].centroid
            up = math.radians(page["Angle"])
            ux, uy = math.sin(up), math.cos(up)  # the page's top, as a map direction
            rx, ry = uy, -ux                      # the page's right
            for other in (run[k - 1] if k > 0 else None, run[k + 1] if k + 1 < len(run) else None):
                if other is None:
                    continue
                there = other["shape"].centroid
                vx, vy = there.x - here.x, there.y - here.y
                right, top = vx * rx + vy * ry, vx * ux + vy * uy
                if abs(right) >= abs(top):
                    page["RightPage" if right > 0 else "LeftPage"] = other["PageNumber"]
                else:
                    page["TopPage" if top > 0 else "BottomPage"] = other["PageNumber"]

    fields = ["PageNumber", "GroupId", "SeqId", "PrevPage", "NextPage", "LeftPage", "RightPage", "TopPage",
              "BottomPage", "Angle"]
    out = c._out(out_feature_class)
    _gpkg.create(out, "Polygon", srid, [_gpkg.Column(f, "Double" if f == "Angle" else "Integer") for f in fields])
    with da.InsertCursor(out, [*fields, "SHAPE@"]) as written:
        for page in pages:
            written.insertRow((*[page[f] for f in fields], page["shape"]))
    return Result(out)
