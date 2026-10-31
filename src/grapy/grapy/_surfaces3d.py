"""The arcpy.sa and arcpy.ddd tools whose pages describe an algorithm without specifying it, built as equivalents from
the published methods those pages name or describe — ADR-224 §5.6, ADR-244.

* ObserverPoints — Viewshed's R3 line of sight per observer; each cell's value encodes which observers see it, the
  way *How Geodesic Viewshed works* encodes them, and the attribute table carries OBS1 … OBSn as documented.
* Viewshed2 — the geodesic sight line of *How Geodesic Viewshed works*: surface and observers in 3D geocentric
  coordinates, the ground path divided into cell-size steps, the surface by distance weighting of the four centres
  around each step (bilinear, as Pro 3.7 measures nearest).
* ZonalFill — each zone filled with the lowest weight on its boundary: the pour point of Jenson and Domingue (1988).
* SurfaceParameters — a quadratic or biquadratic surface fitted by least squares over the neighbourhood (Evans 1980;
  Wood 1996), its derivatives turned into slope, aspect and the curvatures of Minár, Evans and Jenčo (2020).
* SplineWithBarriers — minimum curvature (Briggs 1974), solved directly, with the barriers cutting the stencil as
  Terzopoulos (1988) treats discontinuities.
* ContourWithBarriers — How contouring works' contours, drawn separately on each side of the barriers.
* RegularizeBuildingFootprint and RegularizeAdjacentBuildingFootprint — Douglas–Peucker simplification (1973), the
  edges turned to the footprint's dominant orientation (Duchêne et al. 2003), or a circle fitted by Kåsa (1976).
* StackProfile — each line sampled where it crosses the surface's cells or triangles.
"""
from __future__ import annotations

import math
import os

import numpy as np

from ._runtime import NotBuiltError, equivalent as _equivalent
from ._runtime import _is_given  # noqa: E402 — a Raster is never compared with ==


def _sa():
    from . import sa
    return sa


def _refuse(module, tool, parameter, value, why):
    raise NotBuiltError(f"arcpy.{module}.{tool}: {parameter} '{value}' is not built in grapy: {why}.")


def _given(value):
    return _is_given(value)


# =====================================================================================================================
# Visibility: ObserverPoints and Viewshed2


def _observer_rows(in_observer_features, raster, values, tool, points_only=False):
    """(oid, x, y, parameters) for each observer — a point, or each vertex of a line. A parameter is a number, or a
    field of the observer features; ``values`` maps it to (fixed field name or None, default)."""
    from . import da
    from .analysis import _schema
    sa = _sa()
    columns, kind, _, _ = _schema(in_observer_features)
    if points_only and kind not in ("Point", "Multipoint"):
        raise ValueError(f"{tool}: the observers must be points.")
    names = {c.name.upper(): c.name for c in columns}
    fields, constants = {}, {}
    for key, (fixed, default, given) in values.items():
        if isinstance(given, str) and given.upper() in names:
            fields[key] = names[given.upper()]
        elif _given(given):
            constants[key] = float(given)
        elif fixed and fixed in names:
            fields[key] = names[fixed]
        else:
            constants[key] = default
    read = sorted(set(fields.values()))
    out = []
    with da.SearchCursor(in_observer_features, ["OID@", *read, "SHAPE@"]) as rows:
        for row in rows:
            if row[-1] is None:
                continue
            record = dict(zip(read, row[1:-1]))
            p = dict(constants)
            for key, name in fields.items():
                p[key] = None if record[name] is None else float(record[name])
            shape = row[-1]._shape
            for part in getattr(shape, "geoms", [shape]):
                for x, y in ([part.coords[0]] if part.geom_type == "Point" else list(part.coords)):
                    q = dict(p)
                    if q.get("elevation") is None:
                        sampled = sa._sample(raster, x, y, True)
                        if sampled is None:
                            continue
                        q["elevation"], q["sampled"] = sampled, True
                    out.append((int(row[0]), float(x), float(y), q))
    return out


def _table(out_table, columns, rows):
    from . import _gpkg, da
    from .analysis import _out
    out = _out(out_table)
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column(n, k) for n, k in columns])
    with da.InsertCursor(out, [n for n, _ in columns] + ["SHAPE@"]) as written:
        for row in rows:
            written.insertRow((*row, None))
    return out


@_equivalent("Viewshed's R3 line of sight for each observer, its visibility encoded as the bitwise OR of each "
             "observer's bit", "Franklin and Ray 1994; the observer bits of How Geodesic Viewshed works; the OBSn "
             "fields of Using Viewshed and Observer Points for visibility")
def ObserverPoints(in_raster, in_observer_point_features, z_factor=1, curvature_correction="FLAT_EARTH",
                   refractivity_coefficient=0.13, out_agl_raster=None):
    """EQUIVALENT: which of up to 16 observers see each cell. Visibility is Viewshed's (R3 line of sight, the observer
    fields SPOT, OFFSETA and OFFSETB, the curvature and refraction correction). As *Using Viewshed and Observer Points
    for visibility* documents, the output's attribute table has Value, Count and one field OBS1 … OBSn per observer,
    1 where that observer sees the cells of the row and 0 where it does not; NoData stays NoData.
    ``out_agl_raster`` receives, as documented, the least height a cell needs to be seen by at least one observer,
    0 where it is seen already.

    INFERRED: the page says only that Value is "binary-encoded". grapy encodes it as *How Geodesic Viewshed works*
    encodes its regions: the observers in ObjectID order, observer i contributing 2^(i−1), a cell's value the bitwise
    OR over the observers that see it. A cell no observer sees is 0 and has its row. Each vertex of a line observer
    counts as an observer, as Viewshed counts them."""
    from ._raster import Raster, _Rat, as_raster
    sa = _sa()
    tool = "ObserverPoints"
    sa._refuse_environment(f"arcpy.sa.{tool}")
    raster = as_raster(in_raster, tool)
    zf = float(z_factor if _given(z_factor) else 1)
    curved, refraction = _curvature(raster, curvature_correction, refractivity_coefficient, "sa", tool)
    values = {"elevation": ("SPOT", None, None), "observer_offset": ("OFFSETA", 1.0, None),
              "surface_offset": ("OFFSETB", 0.0, None)}
    observers = _observer_rows(in_observer_point_features, raster, values, tool)
    if len(observers) > 16:
        raise ValueError(f"{tool}: at most 16 observers are allowed, as documented; there are {len(observers)}.")
    z = raster._array.astype(np.float64) * zf
    code = np.zeros(z.shape, np.int64)
    agl = np.full(z.shape, np.inf)
    for i, (_, ox, oy, p) in enumerate(observers):
        oz = p["elevation"] * (zf if p.get("sampled") else 1.0) + p["observer_offset"]
        visible, needed = sa._line_of_sight(z + p["surface_offset"], raster._mask, raster._grid, ox, oy, oz, curved,
                                            refraction)
        code |= np.where(visible, 1 << i, 0)
        agl = np.minimum(agl, np.where(visible, 0.0, needed))
    mask = raster._mask.copy()
    if _given(out_agl_raster):
        Raster._of(np.where(mask, 0, np.where(np.isfinite(agl), agl, 0.0)).astype(np.float32), mask.copy(),
                   raster._grid).save(out_agl_raster)
    out = Raster._of(np.where(mask, 0, code).astype(np.int32), mask, raster._grid)
    found, counts = np.unique(code[~mask], return_counts=True)
    fields = [("Value", "Integer", 5), ("Count", "Integer", 1)] + [(f"OBS{i + 1}", "Integer", 0)
                                                                     for i in range(len(observers))]
    rows = [[int(v), int(c)] + [int((int(v) >> i) & 1) for i in range(len(observers))] for v, c in zip(found, counts)]
    out._rat = _Rat(fields, rows)
    return out


def _curvature(raster, curvature_correction, refractivity_coefficient, module, tool):
    curved = str(curvature_correction or "FLAT_EARTH").upper() == "CURVED_EARTH"
    if curved:
        from ._reference import SpatialReference
        g = raster._grid
        if not g.srid or SpatialReference(g.srid).type != "Projected" or SpatialReference(g.srid).metersPerUnit != 1:
            _refuse(module, tool, "curvature_correction", curvature_correction,
                    "the correction is built for data in metres")
    refraction = float(refractivity_coefficient if _given(refractivity_coefficient) else 0.13)
    return curved, refraction


def _ellipsoid(srid, tool):
    from pyproj import CRS, Transformer
    if not srid:
        raise ValueError(f"{tool}: the surface needs a coordinate system to be placed on the ellipsoid.")
    crs = CRS.from_epsg(int(srid))
    geodetic = crs.geodetic_crs
    a, b = crs.ellipsoid.semi_major_metre, crs.ellipsoid.semi_minor_metre
    to_geodetic = Transformer.from_crs(crs, geodetic, always_xy=True)
    to_grid = Transformer.from_crs(geodetic, crs, always_xy=True)
    return a, b, to_geodetic, to_grid


def _to_ecef(lon, lat, h, a, b):
    """The page's own conversion: X = (N + h)·cosφ·cosλ, Y = (N + h)·cosφ·sinλ, Z = (b²/a²·N + h)·sinφ,
    N = a² / √(a²cos²φ + b²sin²φ)."""
    phi, lam = np.radians(lat), np.radians(lon)
    n = a * a / np.sqrt(a * a * np.cos(phi) ** 2 + b * b * np.sin(phi) ** 2)
    return np.stack([(n + h) * np.cos(phi) * np.cos(lam), (n + h) * np.cos(phi) * np.sin(lam),
                     (b * b / (a * a) * n + h) * np.sin(phi)], axis=-1)


def _from_ecef(xyz, a, b):
    """Longitude, latitude and ellipsoidal height by Bowring's iteration (Bowring 1976, Survey Review 23)."""
    x, y, z = xyz[..., 0], xyz[..., 1], xyz[..., 2]
    e2 = 1 - b * b / (a * a)
    p = np.hypot(x, y)
    lon = np.degrees(np.arctan2(y, x))
    lat = np.arctan2(z, p * (1 - e2))
    h = np.zeros_like(p)
    for _ in range(6):
        n = a / np.sqrt(1 - e2 * np.sin(lat) ** 2)
        h = p / np.cos(lat) - n
        lat = np.arctan2(z, p * (1 - e2 * n / (n + h)))
    return lon, np.degrees(lat), h


def _idw(raster, x, y):
    """The surface at each point from the four cell centres around it, each weighted by its nearness along both axes —
    (1 − dx)(1 − dy) in cell units, the bilinear form of distance weighting. How Geodesic Viewshed works says inverse
    distance weighting without the weights; the INFERRED 1/d first built hid 700 of 3,600 cells on
    tools/pro-compare/adr244-observers.py's DEM where Pro 3.7 hides 453, and these weights come within 17 cells
    (1/d² 39, 1/d³ 24, the nearest centre 344; measured 2026-10-31, ADR-244 §5.4 b). The weights of NoData centres are
    dropped and the rest renormalised; NaN where all four are NoData or off the raster."""
    g = raster._grid
    fx = (np.asarray(x) - g.left) / g.width - 0.5
    fy = (g.top - np.asarray(y)) / g.height - 0.5
    c0, r0 = np.floor(fx).astype(np.int64), np.floor(fy).astype(np.int64)
    tx, ty = fx - c0, fy - r0
    total = np.zeros(fx.shape)
    weight = np.zeros(fx.shape)
    z, mask = raster._array.astype(np.float64), raster._mask
    for dr in (0, 1):
        for dc in (0, 1):
            r, c = r0 + dr, c0 + dc
            ok = (r >= 0) & (r < g.rows) & (c >= 0) & (c < g.columns)
            rr, cc = np.where(ok, r, 0), np.where(ok, c, 0)
            ok &= ~mask[rr, cc]
            w = np.where(ok, (tx if dc else 1 - tx) * (ty if dr else 1 - ty), 0.0)
            total += w * z[rr, cc]
            weight += w
    return np.where(weight > 0, total / np.where(weight > 0, weight, 1.0), np.nan)


_EARTH_RADIUS = 6370000.0  # metres: half the 12,740,000 m diameter How Viewshed works gives


@_equivalent("the geodesic sight line: surface and observers in 3D geocentric coordinates, the ground path divided "
             "into cell-size steps, the surface by bilinear distance weighting of the four centres around each step",
             "How Geodesic Viewshed works; Bowring 1976, Survey Review 23; the refraction term of How Viewshed works")
def Viewshed2(in_raster, in_observer_features, out_agl_raster=None, analysis_type="FREQUENCY", vertical_error=0,
              out_observer_region_relationship_table=None, refractivity_coefficient=0.13, surface_offset=0,
              observer_elevation=None, observer_offset=1, inner_radius=0, inner_radius_is_3d="GROUND",
              outer_radius=None, outer_radius_is_3d="GROUND", horizontal_start_angle=0, horizontal_end_angle=360,
              vertical_upper_angle=90, vertical_lower_angle=-90, analysis_method="ALL_SIGHTLINES",
              analysis_target_device="GPU_THEN_CPU"):
    """EQUIVALENT: geodesic viewshed by *How Geodesic Viewshed works*. The surface cells and the observers are put in
    3D geocentric coordinates with the page's own formulas, on the ellipsoid of the raster's coordinate system. A sight
    line runs straight from the observer's eye to the target cell centre. Its ground path is divided into cell-size
    steps, and at each step d is the line's height above the ellipsoid less the surface there, interpolated by
    distance weighting from the four cell centres around it (bilinear weights, nearest Pro 3.7's answer; see
    ``_idw``). A target is visible when d is positive at every step.

    Every observer parameter is a number or a field of the observer features, as documented: no fixed field names are
    read. Defaults as documented: surface offset 0, observer elevation from the surface by bilinear interpolation,
    observer offset 1, inner radius 0, no outer radius, horizontal angles 0 to 360, vertical angles −90 to 90. A
    radius is measured along the ground (the geodesic on the ellipsoid) or, with ``3D``, as the straight distance.
    ``FREQUENCY`` counts the observers that see each cell. ``OBSERVERS`` (at most 32 observers) gives each cell the
    bitwise OR of the bits of the observers that see it, observer i in ObjectID order having bit 2^(i−1), as the page
    documents, and the optional table has an Observer and a Region field.

    Refused: ``vertical_error`` above 0 — the page adds a random draw to each step's clearance but does not say how
    its range follows from the error given or how many sight lines each cell gets.

    INFERRED: refraction lifts the sight line by k·t·(1 − t)·L²/(2R) at fraction t of a line of length L, R = 6,370,000
    m — the correction How Viewshed works applies, written for a straight line; the surface's values are heights above
    the ellipsoid in metres; ``PERIMETER_SIGHTLINES`` gives ALL_SIGHTLINES' answer, of which it is the documented
    faster approximation; ``analysis_target_device`` is accepted and runs on the CPU; the number of steps is the
    sight line's length in cells, rounded up; NoData cells, and cells no observer sees, are NoData in the output (Pro
    3.7's output, measured 2026-10-31); the steps over NoData block nothing; the
    AGL is the largest −d/t over the steps, how far the target must rise for the line to clear each; the table has a
    row for each pair of a region and an observer that sees it; a horizontal range whose start exceeds its end wraps
    through north."""
    from pyproj import Geod
    from ._raster import Raster, as_raster
    sa = _sa()
    tool = "Viewshed2"
    sa._refuse_environment(f"arcpy.sa.{tool}")
    kind = str(analysis_type or "FREQUENCY").upper()
    if kind not in ("FREQUENCY", "OBSERVERS"):
        raise ValueError(f"{tool}: analysis_type '{analysis_type}' is not FREQUENCY or OBSERVERS.")
    if _given(vertical_error) and float(str(vertical_error).split()[0]) > 0:
        _refuse("sa", tool, "vertical_error", vertical_error, "the page adds a random draw to each step's clearance "
                "but does not say how its range follows from the error or how many sight lines a cell gets")
    raster = as_raster(in_raster, tool)
    g = raster._grid
    a, b, to_geodetic, to_grid = _ellipsoid(g.srid, tool)
    geod = Geod(a=a, b=b)
    refraction = float(refractivity_coefficient if _given(refractivity_coefficient) else 0.13)

    def number(value):
        return float(str(value).split()[0]) if _given(value) and not isinstance(value, (int, float)) and \
            str(value).split()[0].replace(".", "", 1).replace("-", "", 1).isdigit() else value

    values = {"surface_offset": (None, 0.0, number(surface_offset)),
              "elevation": (None, None, number(observer_elevation)),
              "observer_offset": (None, 1.0, number(observer_offset)),
              "inner": (None, 0.0, number(inner_radius)), "outer": (None, math.inf, number(outer_radius)),
              "start": (None, 0.0, number(horizontal_start_angle)), "end": (None, 360.0, number(horizontal_end_angle)),
              "upper": (None, 90.0, number(vertical_upper_angle)), "lower": (None, -90.0, number(vertical_lower_angle))}
    observers = _observer_rows(in_observer_features, raster, values, tool, points_only=kind == "OBSERVERS")
    if kind == "OBSERVERS" and len(observers) > 32:
        raise ValueError(f"{tool}: OBSERVERS allows at most 32 observers, as documented; there are {len(observers)}.")
    inner_3d = str(inner_radius_is_3d).upper() in ("3D", "TRUE")
    outer_3d = str(outer_radius_is_3d).upper() in ("3D", "TRUE")

    z = raster._array.astype(np.float64)
    mask = raster._mask
    xs, ys = g.centres()
    gx, gy = np.meshgrid(xs, ys)
    lon, lat = to_geodetic.transform(gx.ravel(), gy.ravel())
    lon, lat = np.asarray(lon), np.asarray(lat)
    count = np.zeros(z.shape, np.int64)
    code = np.zeros(z.shape, np.int64)
    agl = np.full(z.size, np.inf)
    valid = ~mask.ravel()
    rows_index, cols_index = np.divmod(np.arange(z.size), g.columns)
    for i, (_, ox, oy, p) in enumerate(observers):
        olon, olat = to_geodetic.transform(ox, oy)
        eye = _to_ecef(np.array(olon), np.array(olat), np.array(p["elevation"] + p["observer_offset"]), a, b)
        heights = z.ravel() + p["surface_offset"]
        target = _to_ecef(lon, lat, heights, a, b)
        oc, orow = (ox - g.left) / g.width - 0.5, (g.top - oy) / g.height - 0.5
        steps = np.maximum(1, np.ceil(np.hypot(cols_index - oc, rows_index - orow))).astype(np.int64)
        steps = np.where(valid, steps, 1)
        owner = np.repeat(np.arange(z.size), steps - 1)
        k = np.arange(len(owner)) - np.repeat(np.cumsum(steps - 1) - (steps - 1), steps - 1) + 1
        t = k / steps[owner]
        point = eye + t[:, None] * (target[owner] - eye)
        plon, plat, ph = _from_ecef(point, a, b)
        px, py = to_grid.transform(plon, plat)
        surface = _idw(raster, np.asarray(px), np.asarray(py))
        length = np.linalg.norm(target - eye, axis=1)
        d = ph - surface + refraction * t * (1 - t) * length[owner] ** 2 / (2 * _EARTH_RADIUS)
        blocked = ~np.isnan(surface) & (d <= 0)
        need = np.zeros(z.size)
        if len(owner):
            np.maximum.at(need, owner[blocked], (-d[blocked] / t[blocked]))
        hidden = np.zeros(z.size, bool)
        hidden[owner[blocked]] = True
        seen = valid & ~hidden
        # The observer's limits.
        up = _to_ecef(np.array(olon), np.array(olat), np.array(1.0), a, b) - _to_ecef(
            np.array(olon), np.array(olat), np.array(0.0), a, b)
        chord = target - eye
        straight = np.linalg.norm(chord, axis=1)
        azimuth, _, ground = geod.inv(np.full(lon.shape, olon), np.full(lat.shape, olat), lon, lat)
        azimuth = np.asarray(azimuth) % 360
        ground = np.asarray(ground)
        angle = np.degrees(np.arcsin(np.clip((chord @ up) / np.where(straight > 0, straight, 1), -1, 1)))
        inner = (straight if inner_3d else ground) >= p["inner"]
        outer = (straight if outer_3d else ground) <= p["outer"] if np.isfinite(p["outer"]) else True
        start, end = p["start"] % 360, (p["end"] % 360 if p["end"] != 360 else 360.0)
        across = ((azimuth >= start) & (azimuth <= end)) if start <= end else ((azimuth >= start) | (azimuth <= end))
        within = inner & outer & across & (angle <= p["upper"]) & (angle >= p["lower"])
        within |= straight == 0
        seen &= within
        count.ravel()[:] += seen
        code.ravel()[:] |= np.where(seen, 1 << i, 0)
        agl = np.minimum(agl, np.where(seen | ~within, np.where(seen, 0.0, np.inf), need))
    if _given(out_agl_raster):
        out = np.where(np.isfinite(agl), agl, 0.0).reshape(z.shape)
        Raster._of(np.where(mask, 0, out).astype(np.float32), mask.copy(), g).save(out_agl_raster)
    if kind == "OBSERVERS":
        if _given(out_observer_region_relationship_table):
            regions = sorted({int(v) for v in np.unique(code[~mask]) if v})
            _table(out_observer_region_relationship_table, [("Observer", "Integer"), ("Region", "Integer")],
                   [(observers[i][0], region) for region in regions for i in range(len(observers))
                    if region >> i & 1])
        # A cell no observer sees is NoData, as Pro 3.7 writes it (measured 2026-10-31, ADR-244 §5.4 b).
        return Raster._of(np.where(mask, 0, code).astype(np.int32), mask | (code == 0), g)
    return Raster._of(np.where(mask, 0, count).astype(np.int32), mask | (count == 0), g)


# =====================================================================================================================
# ZonalFill


@_equivalent("each zone filled with the lowest weight on its boundary, the pour point of its watershed",
             "Jenson and Domingue 1988, Photogrammetric Engineering and Remote Sensing 54(11)")
def ZonalFill(in_zone_raster, in_weight_raster):
    """EQUIVALENT: every cell of a zone takes the minimum of the weight raster over the zone's boundary cells, as the
    summary says; the output has the weight raster's type, as documented. Zones may be integer or floating point, as
    documented. Used on sinks' watersheds and a DEM, this is the pour point of Jenson and Domingue (1988): the lowest
    cell on the watershed's edge, the height its sink fills to.

    INFERRED: a zone is all cells of one value; its boundary cells are its own cells with one of their eight
    neighbours in another zone, NoData, or off the raster; weight cells that are NoData are skipped; a zone with no
    boundary weight, and every NoData zone cell, is NoData."""
    from scipy.ndimage import binary_erosion
    from ._raster import Raster, as_raster
    sa = _sa()
    sa._refuse_environment("arcpy.sa.ZonalFill")
    zones = as_raster(in_zone_raster, "ZonalFill")
    weight = as_raster(in_weight_raster, "ZonalFill")
    if not zones._grid.same(weight._grid):
        raise ValueError("ZonalFill: the zone and weight rasters must share one grid.")
    z, zm = zones._array, zones._mask
    w, wm = weight._array, weight._mask
    out = np.zeros(w.shape, dtype=w.dtype)
    empty = zm.copy()
    eight = np.ones((3, 3), bool)
    for value in np.unique(z[~zm]):
        inside = (z == value) & ~zm
        boundary = inside & ~binary_erosion(inside, eight, border_value=0)
        usable = boundary & ~wm
        if not usable.any():
            empty |= inside
            continue
        out[inside] = w[usable].min()
    return Raster._of(np.where(empty, 0, out).astype(w.dtype), empty, zones._grid)


# =====================================================================================================================
# SurfaceParameters

_Z_UNITS = {"INCH": 0.0254, "FOOT": 0.3048, "FOOT_US": 1200 / 3937, "YARD": 0.9144, "MILE_US": 6336000 / 3937,
            "NAUTICAL_MILE": 1852.0, "MILLIMETER": 0.001, "CENTIMETER": 0.01, "METER": 1.0, "KILOMETER": 1000.0,
            "DECIMETER": 0.1}
_PARAMETERS = ("SLOPE", "ASPECT", "MEAN_CURVATURE", "TANGENTIAL_CURVATURE", "PROFILE_CURVATURE", "CONTOUR_CURVATURE",
               "CONTOUR_GEODESIC_TORSION", "GAUSSIAN_CURVATURE", "CASORATI_CURVATURE")


def _design(dx, dy, biquadratic):
    if biquadratic:
        return np.column_stack([dx * dx * dy * dy, dx * dx * dy, dx * dy * dy, dx * dx, dy * dy, dx * dy, dx, dy,
                                np.ones_like(dx)])
    return np.column_stack([dx * dx, dy * dy, dx * dy, dx, dy, np.ones_like(dx)])


def _derivatives(coefficients, biquadratic):
    """p = ∂z/∂x, q = ∂z/∂y, r = ∂²z/∂x², s = ∂²z/∂x∂y, t = ∂²z/∂y² at the centre."""
    c = coefficients
    if biquadratic:
        return c[6], c[7], 2 * c[3], c[5], 2 * c[4]
    return c[3], c[4], 2 * c[0], c[2], 2 * c[1]


def _curvatures(p, q, r, s, t):
    """Minár, Evans and Jenčo (2020), Table 3 and §4: the normal slope line (profile), normal contour (tangential) and
    projected contour curvatures, the contour geodesic torsion, and mean, Gaussian and Casorati curvature; convex
    positive. Where the surface is flat the curvatures that follow the slope line are 0 (INFERRED)."""
    g2 = p * p + q * q
    w = 1 + g2
    flat = g2 == 0
    safe = np.where(flat, 1.0, g2)
    profile = np.where(flat, 0.0, -(r * p * p + 2 * s * p * q + t * q * q) / (safe * np.sqrt(w ** 3)))
    tangential = np.where(flat, 0.0, -(r * q * q - 2 * s * p * q + t * p * p) / (safe * np.sqrt(w)))
    contour = np.where(flat, 0.0, -(r * q * q - 2 * s * p * q + t * p * p) / np.sqrt(safe ** 3))
    torsion = np.where(flat, 0.0, (p * q * (r - t) - s * (p * p - q * q)) / (safe * w))
    mean = -((1 + q * q) * r - 2 * p * q * s + (1 + p * p) * t) / (2 * np.sqrt(w ** 3))
    gaussian = (r * t - s * s) / (w * w)
    casorati = np.sqrt(np.maximum(2 * mean * mean - gaussian, 0.0))
    return {"PROFILE_CURVATURE": profile, "TANGENTIAL_CURVATURE": tangential, "CONTOUR_CURVATURE": contour,
            "CONTOUR_GEODESIC_TORSION": torsion, "MEAN_CURVATURE": mean, "GAUSSIAN_CURVATURE": gaussian,
            "CASORATI_CURVATURE": casorati}


def _north_offset(grid):
    """For each cell, the angle of true north clockwise from grid north, in degrees; 0 without a coordinate system."""
    if not grid.srid:
        return 0.0
    from pyproj import CRS, Transformer
    crs = CRS.from_epsg(grid.srid)
    if crs.is_geographic:
        return 0.0
    there = Transformer.from_crs(crs, crs.geodetic_crs, always_xy=True)
    back = Transformer.from_crs(crs.geodetic_crs, crs, always_xy=True)
    xs, ys = grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    lon, lat = there.transform(gx, gy)
    nx, ny = back.transform(lon, np.asarray(lat) + 1e-5)
    return np.degrees(np.arctan2(np.asarray(nx) - gx, np.asarray(ny) - gy))


@_equivalent("a quadratic (Evans 1980; Wood 1996) or biquadratic (Zevenbergen and Thorne 1987) surface fitted by least "
             "squares over the neighbourhood, with slope, aspect and the curvatures of Minár, Evans and Jenčo 2020",
             "Wood 1996, PhD thesis, University of Leicester; Zevenbergen and Thorne 1987, Earth Surface Processes and "
             "Landforms 12; Minár, Evans and Jenčo 2020, Earth-Science Reviews 211, 103414")
def SurfaceParameters(in_raster, parameter_type="SLOPE", local_surface_type="QUADRATIC", neighborhood_distance=None,
                      use_adaptive_neighborhood="FIXED_NEIGHBORHOOD", z_unit="METER", output_slope_measurement="DEGREE",
                      project_geodesic_azimuths="GEODESIC_AZIMUTHS", use_equatorial_aspect="NORTH_POLE_ASPECT",
                      in_analysis_mask=None):
    """EQUIVALENT: for each cell a local surface is fitted to the cells within ``neighborhood_distance`` (default the
    cell size, a 3 × 3 window; a distance between odd multiples rounds up and seven cells is the largest, a 15 × 15
    window, as *How Surface Parameters works* documents): ``QUADRATIC`` z = ax² + by² + cxy + dx + ey + f by least
    squares (Wood 1996), ``BIQUADRATIC`` the nine-term surface of Zevenbergen and Thorne (1987), exact on a 3 × 3
    window and least squares on a larger one, as documented. From its derivatives at the cell centre:

    - SLOPE: arctan √(p² + q²) in degrees, or 100·√(p² + q²) for ``PERCENT_RISE``;
    - ASPECT: the downslope direction clockwise from north, 0–360; with ``GEODESIC_AZIMUTHS`` (the default) from true
      north, with ``PROJECT_GEODESIC_AZIMUTHS`` from grid north;
    - the curvatures by Minár, Evans and Jenčo (2020), which the page cites, convex positive, in the reciprocal of the
      x,y unit (its square for Gaussian): profile (normal slope line), tangential (normal contour), contour
      (projected), contour geodesic torsion, mean, Gaussian and Casorati.

    ``z_unit`` converts the values to the x,y unit. ``in_analysis_mask`` (a raster, or features) makes cells outside it
    NoData.

    Refused: ``ADAPTIVE_NEIGHBORHOOD``, whose rule for choosing the window from DEV the page does not give;
    ``EQUATORIAL_ASPECT``, whose reference direction the page does not define; data in a geographic coordinate system,
    since the fit here is planar.

    INFERRED: the fit is planar in the raster's projected coordinates, where the page fits in geocentric ones; true
    north is found by projecting a step north from each cell centre; a cell whose window holds NoData or runs off the
    raster is fitted to the cells it has, if there are at least as many as the surface has terms, and is otherwise
    NoData; a flat cell's aspect is −1 and its slope-line curvatures are 0."""
    from scipy.ndimage import correlate
    from ._raster import Raster, as_raster
    from ._reference import SpatialReference
    sa = _sa()
    tool = "SurfaceParameters"
    sa._refuse_environment(f"arcpy.sa.{tool}")
    kind = str(parameter_type or "SLOPE").upper()
    if kind not in _PARAMETERS:
        raise ValueError(f"{tool}: parameter_type '{parameter_type}' is not one of {', '.join(_PARAMETERS)}.")
    if str(use_adaptive_neighborhood or "FIXED_NEIGHBORHOOD").upper() in ("ADAPTIVE_NEIGHBORHOOD", "TRUE"):
        _refuse("sa", tool, "use_adaptive_neighborhood", use_adaptive_neighborhood,
                "the page names the deviation from mean elevation (DEV) but not the rule that picks the window from it")
    if str(use_equatorial_aspect or "NORTH_POLE_ASPECT").upper() in ("EQUATORIAL_ASPECT", "USE_EQUATORIAL_ASPECT",
                                                                      "TRUE"):
        _refuse("sa", tool, "use_equatorial_aspect", use_equatorial_aspect,
                "the page does not define the direction an equatorial aspect is measured from")
    raster = as_raster(in_raster, tool)
    g = raster._grid
    metres = 1.0
    if g.srid:
        reference = SpatialReference(g.srid)
        if reference.type == "Geographic":
            _refuse("sa", tool, "in_raster", in_raster, "the surface is fitted in planar coordinates, and this raster "
                    "is in a geographic coordinate system; project it first")
        metres = reference.metersPerUnit or 1.0
    unit = str(z_unit or "METER").upper()
    if unit not in _Z_UNITS:
        raise ValueError(f"{tool}: z_unit '{z_unit}' is not a unit the page lists.")
    z = raster._array.astype(np.float64) * _Z_UNITS[unit] / metres
    cell = (g.width + g.height) / 2
    if _given(neighborhood_distance):
        distance = float(str(neighborhood_distance).split()[0])
        if distance < cell * (1 - 1e-9):
            raise ValueError(f"{tool}: the neighbourhood distance cannot be less than the cell size.")
        half = min(7, int(math.ceil(distance / cell - 1e-9)))
    else:
        half = 1
    biquadratic = str(local_surface_type or "QUADRATIC").upper() == "BIQUADRATIC"
    offsets = [(i, j) for i in range(-half, half + 1) for j in range(-half, half + 1)]
    dx = np.array([j * g.width for _, j in offsets])
    dy = np.array([-i * g.height for i, _ in offsets])
    design = _design(dx, dy, biquadratic)
    pinv = np.linalg.pinv(design)
    rows, columns = z.shape
    filled = np.where(raster._mask, 0.0, z)
    coefficients = np.stack([correlate(filled, row.reshape(2 * half + 1, 2 * half + 1), mode="constant", cval=0.0)
                             for row in pinv])
    # Cells whose window is incomplete: fitted to the cells they have.
    whole = np.zeros(z.shape, bool)
    valid = ~raster._mask
    whole[half:rows - half, half:columns - half] = True
    if half:
        from scipy.ndimage import minimum_filter
        whole &= minimum_filter(valid.astype(np.uint8), size=2 * half + 1, mode="constant", cval=0).astype(bool)
    out_mask = raster._mask.copy()
    for r, c in zip(*np.nonzero(valid & ~whole)):
        picked = [(k, r + i, c + j) for k, (i, j) in enumerate(offsets)
                  if 0 <= r + i < rows and 0 <= c + j < columns and valid[r + i, c + j]]
        if len(picked) < design.shape[1]:
            out_mask[r, c] = True
            continue
        a = design[[k for k, _, _ in picked]]
        if np.linalg.matrix_rank(a) < design.shape[1]:
            out_mask[r, c] = True
            continue
        solution = np.linalg.lstsq(a, np.array([z[rr, cc] for _, rr, cc in picked]), rcond=None)[0]
        coefficients[:, r, c] = solution
    p, q, r_, s, t = _derivatives(coefficients, biquadratic)
    gradient = np.hypot(p, q)
    if kind == "SLOPE":
        percent = str(output_slope_measurement or "DEGREE").upper() == "PERCENT_RISE"
        out = 100 * gradient if percent else np.degrees(np.arctan(gradient))
    elif kind == "ASPECT":
        out = np.degrees(np.arctan2(-p, -q)) % 360
        if str(project_geodesic_azimuths or "GEODESIC_AZIMUTHS").upper() not in ("PROJECT_GEODESIC_AZIMUTHS", "TRUE"):
            out = (out - _north_offset(g)) % 360
        out = np.where(gradient == 0, -1.0, out)
    else:
        out = _curvatures(p, q, r_, s, t)[kind]
    result = Raster._of(np.where(out_mask, 0, out).astype(np.float32), out_mask, g)
    if _given(in_analysis_mask):
        result = sa.ExtractByMask(result, in_analysis_mask)
    return result


# =====================================================================================================================
# Barriers: SplineWithBarriers and ContourWithBarriers


def _barrier_lines(in_barrier_features):
    """The barriers as linework: lines as they are, polygons by their boundaries."""
    import shapely
    from . import da
    if not _given(in_barrier_features):
        return None
    shapes = []
    with da.SearchCursor(in_barrier_features, ["SHAPE@"]) as rows:
        for (shape,) in rows:
            if shape is None:
                continue
            geometry = shape._shape
            shapes.append(geometry.boundary if geometry.geom_type in ("Polygon", "MultiPolygon") else geometry)
    return shapely.union_all(shapes) if shapes else None


def _links(grid, barrier):
    """Which neighbouring cell centres are joined: east[r, c] joins (r, c) to (r, c + 1), south[r, c] joins (r, c) to
    (r + 1, c); a join is cut where the segment between the two centres meets a barrier."""
    import shapely
    xs, ys = grid.centres()
    rows, columns = grid.rows, grid.columns
    east = np.ones((rows, max(columns - 1, 0)), bool)
    south = np.ones((max(rows - 1, 0), columns), bool)
    if barrier is None or barrier.is_empty:
        return east, south
    shapely.prepare(barrier)
    if east.size:
        gx0, gy = np.meshgrid(xs[:-1], ys)
        segments = shapely.linestrings(np.stack([np.stack([gx0, gy], -1), np.stack([gx0 + grid.width, gy], -1)], -2)
                                       .reshape(-1, 2, 2))
        east = ~shapely.intersects(barrier, segments).reshape(east.shape)
    if south.size:
        gx, gy0 = np.meshgrid(xs, ys[:-1])
        segments = shapely.linestrings(np.stack([np.stack([gx, gy0], -1), np.stack([gx, gy0 - grid.height], -1)], -2)
                                       .reshape(-1, 2, 2))
        south = ~shapely.intersects(barrier, segments).reshape(south.shape)
    return east, south


def _pairs(east, south, valid):
    """The joined pairs of flat cell indexes whose two cells both count."""
    rows, columns = valid.shape
    index = np.arange(rows * columns).reshape(rows, columns)
    pairs = []
    if east.size:
        keep = east & valid[:, :-1] & valid[:, 1:]
        pairs.append(np.stack([index[:, :-1][keep], index[:, 1:][keep]], 1))
    if south.size:
        keep = south & valid[:-1, :] & valid[1:, :]
        pairs.append(np.stack([index[:-1, :][keep], index[1:, :][keep]], 1))
    return np.concatenate(pairs) if pairs else np.zeros((0, 2), np.int64)


@_equivalent("minimum curvature — the surface minimising the summed squared discrete Laplacian, solved directly — with "
             "the barriers cutting the stencil", "Briggs 1974, Geophysics 39; Terzopoulos 1988, IEEE Transactions on "
             "Pattern Analysis and Machine Intelligence 10(4)")
def SplineWithBarriers(Input_point_features, Z_value_field, Input_barrier_features=None, Output_cell_size=None,
                       Smoothing_Factor=0.0):
    """EQUIVALENT: the minimum curvature surface of Briggs (1974), which *How Spline with Barriers works* names: the
    grid surface z minimising Σ(∇²z)², the discrete Laplacian over each cell's four neighbours, with the cells holding
    points tied to their values. A barrier (a line, or a polygon's boundary) cuts the link between two neighbouring
    cells whose centres it separates, so the surface is continuous within each side and free across it, the
    discontinuity Terzopoulos (1988) builds into the membrane. ``Smoothing_Factor`` 0 (the default) honours the points
    exactly; above 0 it minimises s·Σ(∇²z)² + (1 − s)·Σ(z − zᵢ)², and 1 gives the flattest surface. Coincident points
    are averaged, as documented. The cell size is the one given or, as documented, the shorter side of the points'
    extent divided by 250.

    INFERRED: the converged surface is solved directly rather than by the page's coarse-to-fine iteration, whose
    stopping rule is not given; a point ties the cell it falls in, the points in one cell averaged; at the grid's
    edge and along a barrier the Laplacian sums only the links a cell has; a part of the grid cut off from every point
    by barriers is NoData; at a smoothing factor of 1 the data weight is 10⁻⁹ rather than 0, so the surface stays
    tied to the points' mean."""
    from scipy.sparse import coo_matrix, csr_matrix, diags
    from scipy.sparse.csgraph import connected_components
    from scipy.sparse.linalg import spsolve
    from ._raster import Raster
    sa = _sa()
    tool = "SplineWithBarriers"
    s = float(Smoothing_Factor if _given(Smoothing_Factor) else 0.0)
    if not 0 <= s <= 1:
        raise ValueError(f"{tool}: the smoothing factor runs from 0 to 1.")
    xs, ys, zs = sa._points(Input_point_features, Z_value_field, tool)
    grid = sa._point_grid(Input_point_features, Output_cell_size, xs, ys, tool)
    rows, columns = grid.rows, grid.columns
    n = rows * columns
    col = np.clip(np.floor((xs - grid.left) / grid.width).astype(np.int64), 0, columns - 1)
    row = np.clip(np.floor((grid.top - ys) / grid.height).astype(np.int64), 0, rows - 1)
    flat = row * columns + col
    sums = np.bincount(flat, weights=zs, minlength=n)
    counts = np.bincount(flat, minlength=n)
    data = counts > 0
    value = np.where(data, sums / np.maximum(counts, 1), 0.0)
    east, south = _links(grid, _barrier_lines(Input_barrier_features))
    pairs = _pairs(east, south, np.ones((rows, columns), bool))
    adjacency = coo_matrix((np.ones(len(pairs)), (pairs[:, 0], pairs[:, 1])), shape=(n, n)).tocsr()
    adjacency = adjacency + adjacency.T
    _, label = connected_components(adjacency, directed=False)
    reached = np.isin(label, np.unique(label[data]))
    laplacian = csr_matrix(adjacency - diags(np.asarray(adjacency.sum(axis=1)).ravel()))
    keep = np.nonzero(reached)[0]
    lap = laplacian[keep][:, keep]
    held = data[keep]
    surface = np.zeros(n)
    if s == 0:
        free = np.nonzero(~held)[0]
        fixed = np.nonzero(held)[0]
        known = value[keep][fixed]
        solved = np.zeros(len(keep))
        solved[fixed] = known
        if len(free):
            lf, ld = lap[:, free], lap[:, fixed]
            solved[free] = spsolve((lf.T @ lf).tocsc(), -(lf.T @ (ld @ known)))
    else:
        weight = max(1.0 - s, 1e-9)
        system = s * (lap.T @ lap) + weight * diags(held.astype(float))
        solved = spsolve(system.tocsc(), weight * np.where(held, value[keep], 0.0))
    surface[keep] = solved
    mask = ~reached.reshape(rows, columns)
    return Raster._of(np.where(mask, 0, surface.reshape(rows, columns)).astype(np.float32), mask, grid)


def _contour_levels(values, base, interval, indexed, explicit, explicit_only):
    """(level, Type) pairs over the values' range: 1 a contour, 2 an indexed contour, 3 an explicit value."""
    low, high = float(values.min()), float(values.max())
    found = {}
    if not explicit_only:
        for step, kind in ((interval, 1), (indexed, 2)):
            if step:
                for k in range(math.ceil((low - base) / step), math.floor((high - base) / step) + 1):
                    level = round(base + k * step, 10)
                    found[level] = max(found.get(level, 0), kind)
    for level in explicit:
        if low <= level <= high:
            found[float(level)] = 3
    return sorted(found.items())


def _values_file(path):
    """The documented file: a line starting with a non-numeric character is a comment; one value is an explicit
    contour; three are base, interval and indexed interval; four are from, to, by and indexed interval."""
    base, interval, indexed, explicit, ranges = 0.0, None, None, [], []
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            text = line.strip()
            if not text or not (text[0].isdigit() or text[0] in "-+."):
                continue
            numbers = [float(v) for v in text.replace(",", " ").split()]
            if len(numbers) == 1:
                explicit.append(numbers[0])
            elif len(numbers) == 3:
                base, interval, indexed = numbers
            elif len(numbers) == 4:
                ranges.append(numbers)
            else:
                raise ValueError(f"ContourWithBarriers: the line '{text}' has {len(numbers)} values; 1, 3 or 4 are "
                                 "documented.")
    return base, interval, indexed, explicit, ranges


@_equivalent("How contouring works' contours drawn on each side of the barriers apart",
             "Esri's How contouring works; the barriers as Terzopoulos 1988 treats discontinuities")
def ContourWithBarriers(in_raster, out_contour_feature_class, in_barrier_features=None, in_contour_type="POLYLINES",
                        in_contour_values_file=None, explicit_only="NO_EXPLICIT_VALUES_ONLY", in_base_contour=0,
                        in_contour_interval=None, in_indexed_contour_interval=None, in_contour_list=None,
                        in_z_factor=1):
    """EQUIVALENT: contour lines made as Contour makes them (*How contouring works*), on each side of the barriers
    apart, as documented: the cells are split into the groups a barrier — a line, or a polygon's boundary — does not
    separate, and a 2 × 2 block of cells is contoured only when its four cells are in one group, so no contour runs
    across a barrier and the cells inside a barrier polygon are contoured on their own, as the page says. Levels
    follow the page: ``in_base_contour`` (default 0) plus every multiple of ``in_contour_interval`` above and below it
    over the raster's range; indexed contours at ``in_indexed_contour_interval``; explicit values from
    ``in_contour_list``; with ``EXPLICIT_VALUES_ONLY`` the explicit values alone; or all of them from
    ``in_contour_values_file`` in its documented format. ``Type`` is 1 for a contour, 2 for an indexed contour and 3
    for an explicit one, as documented. ``in_z_factor`` multiplies the values.

    Refused: ``POLYGONS``, since the page does not say how a contour polygon is closed at a barrier.

    INFERRED: the value field is ``Contour``, as Contour's; a level that is both a contour and an indexed contour is
    written once, as indexed, and an explicit value wins over both; a values file replaces the base, interval,
    indexed interval and list parameters; a join between two cells is cut where the segment between their centres
    meets a barrier."""
    from scipy.sparse import coo_matrix
    from scipy.sparse.csgraph import connected_components
    from ._raster import Raster, as_raster
    sa = _sa()
    tool = "ContourWithBarriers"
    if str(in_contour_type or "POLYLINES").upper() != "POLYLINES":
        _refuse("sa", tool, "in_contour_type", in_contour_type,
                "the page does not say how a contour polygon is closed at a barrier")
    sa._refuse_environment(f"arcpy.sa.{tool}")
    raster = as_raster(in_raster, tool)
    zf = float(in_z_factor if _given(in_z_factor) else 1)
    only = str(explicit_only or "NO_EXPLICIT_VALUES_ONLY").upper() in ("EXPLICIT_VALUES_ONLY", "TRUE")
    explicit = [float(v) for v in str(in_contour_list).replace(";", " ").split()] if _given(in_contour_list) and \
        not isinstance(in_contour_list, (list, tuple)) else [float(v) for v in (in_contour_list or [])]
    base = float(in_base_contour if _given(in_base_contour) else 0)
    interval = float(in_contour_interval) if _given(in_contour_interval) else None
    indexed = float(in_indexed_contour_interval) if _given(in_indexed_contour_interval) else None
    ranges = []
    if _given(in_contour_values_file):
        base, interval, indexed, explicit, ranges = _values_file(in_contour_values_file)
        base = base or 0.0
    values = raster._values().astype(np.float64) * zf
    if not values.size:
        return sa._write_contours(out_contour_feature_class, raster, [])
    if not only and not interval and not ranges:
        raise ValueError(f"{tool}: a contour interval is needed unless EXPLICIT_VALUES_ONLY is set.")
    for v in (interval, indexed):
        if v is not None and v <= 0:
            raise ValueError(f"{tool}: intervals must be positive.")
    levels = dict(_contour_levels(values, base, interval, indexed, explicit, only))
    for start, stop, by, every in ranges:
        k = 0
        while start + k * by <= stop + 1e-9 and by > 0:
            level = round(start + k * by, 10)
            if values.min() <= level <= values.max():
                kind = 2 if every and abs((level - start) / every - round((level - start) / every)) < 1e-9 else 1
                levels[level] = max(levels.get(level, 0), kind)
            k += 1
    g = raster._grid
    east, south = _links(g, _barrier_lines(in_barrier_features))
    valid = ~raster._mask
    pairs = _pairs(east, south, valid)
    n = g.rows * g.columns
    _, label = connected_components(coo_matrix((np.ones(len(pairs)), (pairs[:, 0], pairs[:, 1])), shape=(n, n)),
                                    directed=False)
    label = label.reshape(g.rows, g.columns)
    lines = []
    for group in np.unique(label[valid]):
        alone = Raster._of(raster._array, raster._mask | (label != group), g)
        lines += sa._contour_lines(alone, sorted(levels), zf)
    return _write_typed_contours(out_contour_feature_class, raster, lines, levels)


def _write_typed_contours(out, raster, lines, levels):
    from . import _gpkg, da
    from .analysis import _out
    from ._result import Result
    path = _out(out)
    _gpkg.create(path, "Polyline", raster._grid.srid, [_gpkg.Column("Contour", "Double"), _gpkg.Column("Type", "Integer")])
    with da.InsertCursor(path, ["Contour", "Type", "SHAPE@"]) as written:
        for level, line in lines:
            written.insertRow((float(level), int(levels[level]), line))
    return Result(path)


# =====================================================================================================================
# Building footprints


def _key(point):
    return round(point[0], 9), round(point[1], 9)


def _douglas_peucker(points, tolerance):
    """Douglas and Peucker (1973) on an open chain: its ends are kept."""
    if len(points) < 3:
        return list(points)
    a, b = np.array(points[0]), np.array(points[-1])
    middle = np.array(points[1:-1])
    ab = b - a
    length = np.hypot(*ab)
    if length == 0:
        distance = np.hypot(*(middle - a).T)
    else:
        distance = np.abs(ab[0] * (middle[:, 1] - a[1]) - ab[1] * (middle[:, 0] - a[0])) / length
    i = int(np.argmax(distance))
    if distance[i] <= tolerance:
        return [points[0], points[-1]]
    left = _douglas_peucker(points[:i + 2], tolerance)
    return left[:-1] + _douglas_peucker(points[i + 1:], tolerance)


def _rings(polygon):
    return [list(polygon.exterior.coords)[:-1]] + [list(r.coords)[:-1] for r in polygon.interiors]


def _noded(polygons):
    """Each polygon's rings with a vertex added wherever another polygon's vertex lies on one of its edges, so that
    shared boundaries share their vertices."""
    import shapely
    everything = {_key(p): p for poly in polygons for ring in _rings(poly) for p in ring}
    points = list(everything.values())
    tree = shapely.STRtree(shapely.points(points)) if points else None
    out = []
    for poly in polygons:
        rings = []
        for ring in _rings(poly):
            built = []
            for i, p in enumerate(ring):
                q = ring[(i + 1) % len(ring)]
                built.append(p)
                if tree is None:
                    continue
                edge = shapely.LineString([p, q])
                found = []
                for j in tree.query(edge, predicate="dwithin", distance=1e-9):
                    v = points[j]
                    if _key(v) in (_key(p), _key(q)):
                        continue
                    found.append((edge.project(shapely.Point(v)), v))
                built += [v for _, v in sorted(found)]
            rings.append(built)
        out.append(rings)
    return out


def _simplified(rings_of, tolerance):
    """Every ring simplified arc by arc: a node is a vertex met by more than two distinct neighbours, or used by more
    than one polygon with different neighbours; an arc between two nodes is simplified once, so polygons sharing it
    keep the same vertices. A ring with no node is split at its first vertex and the vertex farthest from it."""
    neighbours: dict = {}
    for rings in rings_of:
        for ring in rings:
            for i, p in enumerate(ring):
                k = _key(p)
                neighbours.setdefault(k, set()).update({_key(ring[i - 1]), _key(ring[(i + 1) % len(ring)])})
    nodes = {k for k, near in neighbours.items() if len(near) != 2}
    cache: dict = {}
    out = []
    for rings in rings_of:
        done = []
        for ring in rings:
            keys = [_key(p) for p in ring]
            at = [i for i, k in enumerate(keys) if k in nodes]
            if not at:
                far = int(np.argmax([math.dist(ring[0], p) for p in ring]))
                at = [0, far] if far else [0]
            kept = []
            for n, start in enumerate(at):
                stop = at[(n + 1) % len(at)]
                chain = [ring[(start + j) % len(ring)] for j in range(((stop - start) % len(ring)) or len(ring))]
                chain.append(ring[stop])
                ident = tuple(_key(p) for p in chain)
                if ident[::-1] in cache:
                    simple = cache[ident[::-1]][::-1]
                else:
                    simple = cache.setdefault(ident, _douglas_peucker(chain, tolerance))
                kept += simple[:-1]
            done.append(kept)
        out.append(done)
    return out, nodes


def _dominant(rings_of):
    """The footprints' dominant orientation in [0°, 90°): the length-weighted circular mean of the edges' directions
    modulo 90°, the wall-orientation statistic of Duchêne et al. (2003)."""
    sx = sy = 0.0
    for rings in rings_of:
        for ring in rings:
            for i, p in enumerate(ring):
                q = ring[(i + 1) % len(ring)]
                dx, dy = q[0] - p[0], q[1] - p[1]
                length = math.hypot(dx, dy)
                angle = math.atan2(dy, dx)
                sx += length * math.cos(4 * angle)
                sy += length * math.sin(4 * angle)
    return (math.degrees(math.atan2(sy, sx)) / 4) % 90


def _deviation(angle, base, step):
    """How far a direction (degrees, undirected) is from the nearest of base + k·step, and that direction."""
    k = round((angle - base) / step)
    target = base + k * step
    return abs(angle - target), target


def _direction(p, q, base, method, tolerance, penalty, angular_limit):
    length = math.dist(p, q)
    angle = math.degrees(math.atan2(q[1] - p[1], q[0] - p[0]))
    right, at_right = _deviation(angle, base, 90.0)
    diagonal, at_diagonal = _deviation(angle, base + 45.0, 90.0)

    def fits(deviation, weight=1.0):
        return length * math.sin(math.radians(deviation)) / 2 * weight <= tolerance

    if method == "RIGHT_ANGLES":
        return at_right
    if method == "RIGHT_ANGLES_AND_DIAGONALS":
        if angular_limit is not None:
            return at_diagonal if diagonal < right and diagonal <= angular_limit else at_right
        return at_diagonal if diagonal * penalty < right else at_right
    # ANY_ANGLE: a preferred direction where turning the edge to it stays within the tolerance.
    if not penalty:
        return angle
    if fits(right):
        return at_right
    if fits(diagonal, penalty):
        return at_diagonal
    return angle


def _line(p, q, direction):
    """A line through the edge's midpoint at ``direction``: (point, unit direction)."""
    mid = ((p[0] + q[0]) / 2, (p[1] + q[1]) / 2)
    r = math.radians(direction)
    return np.array(mid), np.array([math.cos(r), math.sin(r)])


def _meet(a, b, near, reach):
    """Where two lines cross, or — parallel, or crossing beyond ``reach`` of ``near`` — the foot of ``near`` on each,
    a step between them."""
    (pa, da), (pb, db) = a, b
    cross = da[0] * db[1] - da[1] * db[0]
    v = np.array(near)
    if abs(cross) > 1e-9:
        t = ((pb - pa)[0] * db[1] - (pb - pa)[1] * db[0]) / cross
        point = pa + t * da
        if np.hypot(*(point - v)) <= reach:
            return [tuple(point)]
    foot_a = pa + np.dot(v - pa, da) * da
    foot_b = pb + np.dot(v - pb, db) * db
    if np.hypot(*(foot_a - foot_b)) < 1e-9:
        return []
    return [tuple(foot_a), tuple(foot_b)]


def _least_squares(lines, near):
    """The point nearest, in least squares, to several lines, held to ``near`` by a vanishing weight."""
    m = np.eye(2) * 1e-9
    v = np.array(near) * 1e-9
    for p, d in lines:
        n = np.array([-d[1], d[0]])
        m += np.outer(n, n)
        v += n * np.dot(n, p)
    return tuple(np.linalg.solve(m, v))


def _clean(ring):
    out = []
    for p in ring:
        if not out or math.dist(out[-1], p) > 1e-9:
            out.append(p)
    while len(out) > 1 and math.dist(out[0], out[-1]) <= 1e-9:
        out.pop()
    changed = True
    while changed and len(out) > 3:
        changed = False
        for i in range(len(out)):
            a, b, c = out[i - 1], out[i], out[(i + 1) % len(out)]
            if abs((b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])) <= 1e-9 * max(1.0, math.dist(a, c)):
                out.pop(i)
                changed = True
                break
    return out


def _regularize_group(polygons, method, tolerance, densification, precision, penalty, angular_limit, base=None):
    """The group's footprints regularized together; None for a footprint that could not be."""
    import shapely
    if densification:
        polygons = [shapely.segmentize(p, densification) for p in polygons]
    rings_of, nodes = _simplified(_noded(polygons), tolerance)
    if base is None:
        base = _dominant(rings_of)
    lines: dict = {}
    incident: dict = {}
    for rings in rings_of:
        for ring in rings:
            for i, p in enumerate(ring):
                q = ring[(i + 1) % len(ring)]
                edge = frozenset((_key(p), _key(q)))
                if edge not in lines:
                    lines[edge] = _line(p, q, _direction(p, q, base, method, tolerance, penalty, angular_limit))
                incident.setdefault(_key(p), set()).add(edge)
                incident.setdefault(_key(q), set()).add(edge)
    reach = 2 * tolerance + 1e-9
    junction = {k: _least_squares([lines[e] for e in edges], k) for k, edges in incident.items() if len(edges) > 2}
    grid = (precision or 0) * tolerance
    out = []
    for poly, rings in zip(polygons, rings_of):
        built = []
        for ring in rings:
            placed = []
            for i, p in enumerate(ring):
                k = _key(p)
                if k in junction:
                    placed.append(junction[k])
                    continue
                before = lines[frozenset((_key(ring[i - 1]), k))]
                after = lines[frozenset((k, _key(ring[(i + 1) % len(ring)])))]
                placed += _meet(before, after, p, reach)
            if grid:
                placed = [(round(x / grid) * grid, round(y / grid) * grid) for x, y in placed]
            placed = _clean(placed)
            if len(placed) < 3:
                built = None
                break
            built.append(placed)
        if built is None:
            out.append(None)
            continue
        candidate = shapely.Polygon(built[0], built[1:])
        if not candidate.is_valid or candidate.is_empty or candidate.area <= 0 or \
                candidate.hausdorff_distance(poly) > 2 * tolerance:
            out.append(None)
        else:
            out.append(candidate)
    return out


def _circle(polygon, tolerance, tolerance_type, min_radius, max_radius, densification):
    """Kåsa's (1976) least-squares circle through the boundary, or None when it breaks a limit."""
    import shapely
    boundary = shapely.segmentize(polygon.exterior, densification) if densification else polygon.exterior
    xy = np.array(boundary.coords)[:-1]
    a = np.column_stack([2 * xy[:, 0], 2 * xy[:, 1], np.ones(len(xy))])
    cx, cy, c = np.linalg.lstsq(a, (xy ** 2).sum(axis=1), rcond=None)[0]
    radius = math.sqrt(max(c + cx * cx + cy * cy, 0.0))
    if radius <= 0 or (min_radius and radius < min_radius) or (max_radius and radius > max_radius):
        return None
    circle = shapely.Point(cx, cy).buffer(radius, quad_segs=16)
    if str(tolerance_type or "DISTANCE").upper() == "AREA_RATIO":
        if circle.symmetric_difference(polygon).area / circle.area > tolerance:
            return None
    elif polygon.exterior.hausdorff_distance(circle.exterior) > tolerance:
        return None
    return circle


def _write_footprints(in_features, out_feature_class, results):
    from . import _gpkg, da
    from .analysis import _out, _schema
    from ._result import Result
    columns, kind, srid, _ = _schema(in_features)
    out = _out(out_feature_class)
    names = [c.name for c in columns]
    kept = [c for c in columns if c.name.upper() != "STATUS"]
    _gpkg.create(out, "Polygon", srid, [*kept, _gpkg.Column("STATUS", "Integer")])
    with da.InsertCursor(out, [c.name for c in kept] + ["STATUS", "SHAPE@"]) as written:
        for values, original, regular in results:
            record = dict(zip(names, values))
            written.insertRow((*[record[c.name] for c in kept], 0 if regular is not None else 1,
                               regular if regular is not None else original))
    return Result(out)


def _read_footprints(in_features, tool):
    from . import da
    from .analysis import _schema
    columns, kind, _, _ = _schema(in_features)
    if kind != "Polygon":
        raise ValueError(f"arcpy.ddd.{tool} takes polygon features.")
    names = [c.name for c in columns]
    with da.SearchCursor(in_features, [*names, "SHAPE@"]) as rows:
        return names, [(list(r[:-1]), r[-1]._shape) for r in rows if r[-1] is not None]


def _length(value):
    return float(str(value).split()[0]) if _given(value) else 0.0


_METHODS = ("RIGHT_ANGLES", "RIGHT_ANGLES_AND_DIAGONALS", "ANY_ANGLE", "CIRCLE")
_FOOTPRINT_SOURCE = ("Douglas and Peucker 1973, The Canadian Cartographer 10(2); Duchêne, Bard, Barillot, Ruas, "
                     "Trévisan and Holzapfel 2003, 5th ICA Workshop on Progress in Automated Map Generalisation; "
                     "Kåsa 1976, IEEE Transactions on Instrumentation and Measurement 25(1)")


def _alignment_angle(polygon, alignment, distance):
    """The direction of the nearest alignment line's nearest segment within ``distance``, or None."""
    import shapely
    best = None
    for line in alignment:
        d = line.distance(polygon)
        if d <= distance and (best is None or d < best[0]):
            best = (d, line)
    if best is None:
        return None
    line = best[1]
    parts = getattr(line, "geoms", [line])
    segments = [(p[i], p[i + 1]) for part in parts for p in [list(part.coords)] for i in range(len(p) - 1)]
    a, b = min(segments, key=lambda s: shapely.LineString(s).distance(polygon))
    return math.degrees(math.atan2(b[1] - a[1], b[0] - a[0])) % 90


@_equivalent("Douglas–Peucker simplification with each edge turned to the dominant orientation, its right angles or "
             "diagonals, or Kåsa's least-squares circle", _FOOTPRINT_SOURCE)
def RegularizeBuildingFootprint(in_features, out_feature_class, method, tolerance, densification=None, precision=0.25,
                                diagonal_penalty=1.5, min_radius=0.1, max_radius=1000000, alignment_feature=None,
                                alignment_tolerance=None, tolerance_type="DISTANCE"):
    """EQUIVALENT: each footprint regularized by the method asked, as the page describes them; the page names "a
    polyline compression algorithm" and links Esri's paper, and grapy builds published methods in its place.

    - ``RIGHT_ANGLES``: the boundary is simplified by Douglas–Peucker (1973) within ``tolerance``; each remaining edge
      is turned about its midpoint to the nearest of the footprint's dominant orientation and its right angles (the
      length-weighted wall orientation of Duchêne et al. 2003, or the nearest ``alignment_feature`` line within
      ``alignment_tolerance``); consecutive edges meet where their lines cross, or by a right-angled step when they
      are parallel.
    - ``RIGHT_ANGLES_AND_DIAGONALS``: an edge takes the 45° direction when its angle from it, times
      ``diagonal_penalty``, is less than its angle from the nearest right angle; so a higher penalty makes diagonals
      less likely, as documented.
    - ``ANY_ANGLE``: an edge keeps its own direction unless turning it to a right-angle direction, or a diagonal
      weighted by ``diagonal_penalty``, moves its ends no more than ``tolerance``; a penalty of 0 keeps every edge's
      own direction, the simplified irregular polygon the page describes.
    - ``CIRCLE``: Kåsa's (1976) least-squares circle through the boundary, kept when its radius is within
      ``min_radius`` and ``max_radius`` and it is within ``tolerance`` — the boundary's largest distance from it under
      ``DISTANCE``, the default, or the area outside their overlap over the circle's area under ``AREA_RATIO``.

    A footprint that cannot be regularized is copied as it is, with ``STATUS`` 1; a regularized one has 0, as
    documented. The input's fields are kept.

    INFERRED: the defaults the page does not state — densification none, precision 0.25, diagonal penalty 1.5, minimum
    radius 0.1, maximum radius 1,000,000; ``densification`` adds vertices at that spacing before simplifying;
    ``precision`` rounds the result's coordinates to a grid of precision × tolerance; a result fails, and the original
    is kept, when it is not a valid polygon or lies more than twice the tolerance from the original anywhere; a circle
    is drawn with 64 vertices; two edges whose lines cross more than twice the tolerance from their vertex are joined
    by a step instead."""
    import shapely
    tool = "RegularizeBuildingFootprint"
    key = str(method or "").upper().replace("ANY_ANGLES", "ANY_ANGLE")
    if key not in _METHODS:
        raise ValueError(f"arcpy.ddd.{tool}: method '{method}' is not one of {', '.join(_METHODS)}.")
    tol = _length(tolerance)
    if tol <= 0:
        raise ValueError(f"arcpy.ddd.{tool}: the tolerance must be positive.")
    dens = _length(densification)
    if dens and dens > tol and key != "CIRCLE":
        raise ValueError(f"arcpy.ddd.{tool}: the densification must not exceed the tolerance, as documented.")
    prec = float(precision) if _given(precision) else 0.25
    if not 0.05 <= prec <= 0.25:
        raise ValueError(f"arcpy.ddd.{tool}: the precision runs from 0.05 to 0.25, as documented.")
    penalty = float(diagonal_penalty) if _given(diagonal_penalty) else 1.5
    names, features = _read_footprints(in_features, tool)
    alignment = []
    if _given(alignment_feature):
        from . import da
        with da.SearchCursor(alignment_feature, ["SHAPE@"]) as rows:
            alignment = [r[0]._shape for r in rows if r[0] is not None]
    reach = _length(alignment_tolerance) if _given(alignment_tolerance) else math.inf
    results = []
    for values, shape in features:
        parts = list(getattr(shape, "geoms", [shape]))
        if key == "CIRCLE":
            regular = _circle(parts[0] if len(parts) == 1 else shape.convex_hull, tol, tolerance_type,
                              _length(min_radius) if _given(min_radius) else 0.1,
                              _length(max_radius) if _given(max_radius) else 1000000.0, dens)
        else:
            base = _alignment_angle(shape, alignment, reach) if alignment else None
            done = [_regularize_group([part], key, tol, dens, prec, penalty, None, base)[0] for part in parts]
            regular = None if any(d is None for d in done) else (done[0] if len(done) == 1 else
                                                                  shapely.MultiPolygon(done))
        results.append((values, shape, regular))
    return _write_footprints(in_features, out_feature_class, results)


@_equivalent("shared-arc Douglas–Peucker simplification with each edge turned to the group's dominant orientation, "
             "shared vertices placed once for every footprint that uses them", _FOOTPRINT_SOURCE)
def RegularizeAdjacentBuildingFootprint(in_features, group, out_feature_class, method, tolerance, precision=0.25,
                                        angular_limit=None):
    """EQUIVALENT: the footprints of each ``group`` value regularized together, so that, as documented, adjacent
    footprints keep a common boundary and do not overlap. The method is RegularizeBuildingFootprint's, with three
    differences that keep shared boundaries shared: the boundaries are cut into arcs at the vertices where footprints
    meet, and each arc is simplified once by Douglas–Peucker (1973), for every footprint that uses it; the dominant
    orientation is the group's; and a vertex where three or more edges meet is placed once, at the point nearest to
    all of their lines. ``RIGHT_ANGLES`` fits right angles; ``RIGHT_ANGLES_AND_DIAGONALS`` also lets an edge take the
    45° direction when that is nearer than a right angle and within ``angular_limit`` degrees of it;
    ``ANY_ANGLES`` keeps each edge's own direction unless a right angle is within the tolerance. A footprint that
    cannot be regularized is copied with ``STATUS`` 1, as documented; the input's fields are kept.

    INFERRED: features with a null group value are each a group of their own; a vertex of one footprint lying on
    another's edge is added to that edge first; the default angular limit, unstated, is 5°, the most the page
    recommends; precision as in RegularizeBuildingFootprint, default 0.25; the same failure test, which also applies
    to each footprint of a group."""
    tool = "RegularizeAdjacentBuildingFootprint"
    key = str(method or "").upper().replace("ANY_ANGLES", "ANY_ANGLE")
    if key not in _METHODS[:3]:
        raise ValueError(f"arcpy.ddd.{tool}: method '{method}' is not one of RIGHT_ANGLES, RIGHT_ANGLES_AND_DIAGONALS, "
                         "ANY_ANGLES.")
    tol = _length(tolerance)
    if tol <= 0:
        raise ValueError(f"arcpy.ddd.{tool}: the tolerance must be positive.")
    prec = float(precision) if _given(precision) else 0.25
    if not 0.05 <= prec <= 0.25:
        raise ValueError(f"arcpy.ddd.{tool}: the precision runs from 0.05 to 0.25, as documented.")
    limit = float(angular_limit) if _given(angular_limit) else 5.0
    names, features = _read_footprints(in_features, tool)
    lowered = [n.lower() for n in names]
    if str(group).lower() not in lowered:
        raise ValueError(f"arcpy.ddd.{tool}: '{group}' is not a field of the input.")
    at = lowered.index(str(group).lower())
    groups: dict = {}
    for i, (values, _) in enumerate(features):
        groups.setdefault(("one", i) if values[at] is None else ("value", values[at]), []).append(i)
    regular = [None] * len(features)
    for members in groups.values():
        shapes = [features[i][1] for i in members]
        shapes = [s.geoms[0] if s.geom_type == "MultiPolygon" and len(s.geoms) == 1 else s for s in shapes]
        if any(s.geom_type != "Polygon" for s in shapes):
            continue
        done = _regularize_group(shapes, key, tol, None, prec, 1.0, limit if key == "RIGHT_ANGLES_AND_DIAGONALS"
                                 else None)
        for i, d in zip(members, done):
            regular[i] = d
    return _write_footprints(in_features, out_feature_class,
                             [(values, shape, r) for (values, shape), r in zip(features, regular)])


# =====================================================================================================================
# StackProfile


def _profile_samples(line, target):
    """(distance along the line, z) where the line meets each cell edge or triangle edge of the target, and at its
    vertices; z is the target's surface there."""
    import shapely
    kind, surface, name = target
    if kind == "raster":
        sa = _sa()
        g = surface._grid
        xs = g.left + np.arange(g.columns + 1) * g.width
        ys = g.top - np.arange(g.rows + 1) * g.height
        edges = shapely.MultiLineString([[(x, ys[-1]), (x, ys[0])] for x in xs] +
                                        [[(xs[0], y), (xs[-1], y)] for y in ys])
    else:
        sa = None
        p, q, r = surface.corners(np.nonzero(surface.inside)[0])
        edges = shapely.MultiLineString([[tuple(a[:2]), tuple(b[:2])] for tri in zip(p, q, r)
                                         for a, b in ((tri[0], tri[1]), (tri[1], tri[2]), (tri[2], tri[0]))])
    distances = {round(line.project(shapely.Point(c)), 9) for c in line.coords}
    crossing = line.intersection(edges)
    for part in getattr(crossing, "geoms", [crossing]):
        for c in getattr(part, "coords", []):
            distances.add(round(line.project(shapely.Point(c)), 9))
    out = []
    for d in sorted(distances):
        point = line.interpolate(d)
        if kind == "raster":
            z = sa._sample(surface, point.x, point.y, True)
        else:
            z = float(surface.z_at([point.x], [point.y])[0])
            z = None if math.isnan(z) else z
        out.append((d, z))
    return out


def _profile_target(target, tool):
    from . import _tin
    from ._raster import as_raster
    text = os.fspath(target) if not hasattr(target, "_array") else None
    if text is not None and _tin.exists(text):
        return "tin", _tin.load(text), os.path.splitext(os.path.basename(text.rstrip("/\\")))[0]
    try:
        raster = as_raster(target, tool)
    except Exception:  # noqa: BLE001 — anything that is not a raster or TIN
        _refuse("ddd", tool, "profile_targets", target, "the page does not say how a line crossing a multipatch, a "
                "terrain or a LAS dataset is cut into the segments it profiles")
    name = os.path.splitext(os.path.basename(text))[0] if text else "raster"
    return "raster", raster, name


def StackProfile(in_line_features, profile_targets, out_table, out_graph=None, multipatch_granularity="FEATURE_CLASS",
                 out_legacy_table=None):
    """Each line's profile over each raster or TIN target, as documented: the line is densified and its height
    interpolated along each target it crosses, the rows written with the current schema's DISTANCE, Z, LINE_ID,
    SRC_NAME and LINE_TARG; ``out_legacy_table`` writes the legacy schema's FIRST_DIST, FIRST_Z, SEC_DIST, SEC_Z,
    LINE_ID, SRC_TYPE, SRC_ID and SRC_NAME, one row per segment between consecutive samples.

    Refused: multipatch, terrain and LAS targets, whose cutting into segments the page does not describe; ``out_graph``,
    an ArcGIS Pro chart a run on this server cannot make.

    INFERRED: a line is sampled at its vertices and wherever it crosses a cell edge of a raster, or a triangle edge of
    a TIN — the places where the surface's interpolation changes — so that a raster's height is bilinear between
    cell centres and a TIN's is exact; samples where the target has no data are left out; DISTANCE is measured in
    the line's units from its start; LINE_ID is the line's ObjectID; SRC_NAME is the target's name without its folder
    and extension (the legacy table's, its full path); LINE_TARG is "<LINE_ID> <SRC_NAME>"; SRC_TYPE is "Surface" and
    SRC_ID null; a multipart line is profiled part after part, the distance running on."""
    from . import da
    import shapely
    tool = "StackProfile"
    if _given(out_graph):
        _refuse("ddd", tool, "out_graph", out_graph, "it is an ArcGIS Pro chart, which a run on this server cannot make")
    targets = profile_targets if isinstance(profile_targets, (list, tuple)) else \
        [t for t in str(profile_targets).split(";") if t]
    opened = [(_profile_target(t, tool), t) for t in targets]
    rows, legacy = [], []
    with da.SearchCursor(in_line_features, ["OID@", "SHAPE@"]) as lines:
        features = [(int(o), s._shape) for o, s in lines if s is not None]
    for oid, shape in features:
        parts = list(getattr(shape, "geoms", [shape]))
        for (kind, surface, name), source in opened:
            run = 0.0
            for part in parts:
                samples = [(d + run, z) for d, z in _profile_samples(shapely.LineString(part.coords), (kind, surface,
                                                                                                        name))]
                run += part.length
                kept = [(d, z) for d, z in samples if z is not None]
                rows += [(d, z, oid, name, f"{oid} {name}") for d, z in kept]
                legacy += [(a[0], a[1], b[0], b[1], oid, "Surface", None, os.fspath(source) if not hasattr(
                    source, "_array") else name) for a, b in zip(kept, kept[1:])]
    _table(out_table, [("DISTANCE", "Double"), ("Z", "Double"), ("LINE_ID", "Integer"), ("SRC_NAME", "String"),
                       ("LINE_TARG", "String")], rows)
    if _given(out_legacy_table):
        _table(out_legacy_table, [("FIRST_DIST", "Double"), ("FIRST_Z", "Double"), ("SEC_DIST", "Double"),
                                  ("SEC_Z", "Double"), ("LINE_ID", "Integer"), ("SRC_TYPE", "String"),
                                  ("SRC_ID", "Integer"), ("SRC_NAME", "String")], legacy)
    from ._result import Result
    from .analysis import _out
    return Result(_out(out_table))


# =====================================================================================================================
# TopoToRaster stays refused, with its own reason.

