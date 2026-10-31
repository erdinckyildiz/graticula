"""Surface Parameters — slope, aspect and the curvatures of Minár et al. (2020) from a local surface fitted by least
squares (ADR-243). How Surface Parameters works describes the fit, the window the neighbourhood distance makes and
the adaptive neighbourhood; it computes in geocentric coordinates, which this equivalent does not: the fit is made in
the raster's own planar coordinates, so a raster in a geographic coordinate system is refused.

``slope_aspect`` is the same fit for other tools (Raster Solar Radiation's slope and aspect).
"""
from __future__ import annotations

import math

import numpy as np

from ._raster import Raster, _refuse_environment, as_raster
from ._runtime import NotBuiltError


def _given(value):
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


METHOD = ("slope, aspect and curvatures of a local quadratic (Evans 1972; Wood 1996) or biquadratic (Zevenbergen "
          "and Thorne 1987) surface fitted by least squares in the raster's planar coordinates",
          "Wood 1996, The Geomorphological Characterisation of Digital Elevation Models, PhD thesis, ch. 4; "
          "Zevenbergen and Thorne 1987, Earth Surface Processes and Landforms 12; Minár, Evans and Jenčo 2020, "
          "Earth-Science Reviews 211, 103414 (the curvature formulas); Wilson and Gallant 2000, Terrain Analysis (DEV)")

_Z_UNITS = {"INCH": 0.0254, "FOOT": 0.3048, "FOOT_US": 1200 / 3937, "YARD": 0.9144, "MILE_US": 6336000 / 3937,
            "NAUTICAL_MILE": 1852.0, "MILLIMETER": 0.001, "CENTIMETER": 0.01, "METER": 1.0, "KILOMETER": 1000.0,
            "DECIMETER": 0.1}
_TYPES = ("SLOPE", "ASPECT", "MEAN_CURVATURE", "TANGENTIAL_CURVATURE", "PROFILE_CURVATURE", "CONTOUR_CURVATURE",
          "CONTOUR_GEODESIC_TORSION", "GAUSSIAN_CURVATURE", "CASORATI_CURVATURE")


def _refuse(parameter, value, why=""):
    raise NotBuiltError(f"SurfaceParameters: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def half_width(cell_size, neighborhood_distance):
    """The window's half width in cells, as How Surface Parameters works sets it: the distance rounded up to a whole
    number of cells, at least 1 and at most 7 (a 15 × 15 window)."""
    if not _given(neighborhood_distance):
        return 1
    distance = float(str(neighborhood_distance).split()[0])
    if distance < cell_size * (1 - 1e-9):
        raise ValueError("The neighbourhood distance cannot be less than the cell size.")
    return int(min(7, max(1, math.ceil(distance / cell_size - 1e-9))))


def _terms(x, y, biquadratic):
    if biquadratic:
        return np.stack([x * x * y * y, x * x * y, x * y * y, x * x, y * y, x * y, x, y, np.ones_like(x)], axis=-1)
    return np.stack([x * x, y * y, x * y, x, y, np.ones_like(x)], axis=-1)


def _derivatives(coefficients, biquadratic):
    """p, q, r, s, t — z_x, z_y, z_xx, z_xy, z_yy at the centre — from the fitted coefficients."""
    c = coefficients
    if biquadratic:
        return c[..., 6], c[..., 7], 2 * c[..., 3], c[..., 5], 2 * c[..., 4]
    return c[..., 3], c[..., 4], 2 * c[..., 0], c[..., 2], 2 * c[..., 1]


def _fit(z, mask, cx, cy, offsets, biquadratic):
    """For every cell, the derivatives of the surface fitted to the cells at ``offsets`` (row, column) around it; NaN
    where any of them is NoData or off the raster."""
    rows, columns = z.shape
    dr = np.array([o[0] for o in offsets])
    dc = np.array([o[1] for o in offsets])
    design = _terms(dc * cx, -dr * cy, biquadratic)
    solve = np.linalg.pinv(design)
    reach = int(max(np.abs(dr).max(), np.abs(dc).max()))
    padded = np.pad(np.where(mask, np.nan, z.astype(np.float64)), reach, constant_values=np.nan)
    samples = np.stack([padded[reach + r:reach + r + rows, reach + c:reach + c + columns] for r, c in offsets], axis=-1)
    coefficients = samples @ solve.T
    return _derivatives(coefficients, biquadratic)


def _ring(n):
    """The nine cells the adaptive neighbourhood uses: the centre and the outer orthogonal and diagonal cells."""
    return [(dr * n, dc * n) for dr in (-1, 0, 1) for dc in (-1, 0, 1)]


def derivatives(values, nodata_mask, cell_size, neighborhood_distance=None, adaptive=False, biquadratic=False,
                cell_height=None):
    """p, q, r, s, t of the fitted surface at every cell (NaN where it cannot be fitted), x east and y north.

    With a fixed neighbourhood every cell of the (2n + 1)² window is fitted; an adaptive one fits the nine cells of the
    window chosen for each cell, from 3 × 3 up to the neighbourhood distance's — the largest whose deviation from the
    mean, DEV = (z − mean) / standard deviation over the window (Wilson and Gallant 2000), lies within ±1 (INFERRED:
    How Surface Parameters works says the largest window that keeps the variability low, without the threshold)."""
    z = np.asarray(values, dtype=np.float64)
    mask = np.asarray(nodata_mask, bool) | ~np.isfinite(z)
    cx = float(cell_size)
    cy = float(cell_height if cell_height is not None else cell_size)
    n = half_width(cx, neighborhood_distance)
    if not adaptive:
        offsets = [(r, c) for r in range(-n, n + 1) for c in range(-n, n + 1)]
        return _fit(z, mask, cx, cy, offsets, biquadratic)
    found = [_fit(z, mask, cx, cy, _ring(k), biquadratic) for k in range(1, n + 1)]
    chosen = np.ones(z.shape, np.int64)
    rows, columns = z.shape
    for k in range(2, n + 1):
        padded = np.pad(np.where(mask, np.nan, z), k, constant_values=np.nan)
        window = np.stack([padded[k + r:k + r + rows, k + c:k + c + columns]
                           for r in range(-k, k + 1) for c in range(-k, k + 1)], axis=-1)
        with np.errstate(all="ignore"):
            mean = window.mean(axis=-1)
            spread = window.std(axis=-1)
            dev = np.where(spread > 0, (z - mean) / spread, 0.0)
        ok = np.isfinite(dev) & (np.abs(dev) <= 1.0) & np.isfinite(found[k - 1][0])
        chosen = np.where(ok, k, chosen)
    out = []
    for part in range(5):
        stack = np.stack([found[k][part] for k in range(n)], axis=-1)
        out.append(np.take_along_axis(stack, (chosen - 1)[..., None], axis=-1)[..., 0])
    return tuple(out)


def _slope_aspect(p, q):
    with np.errstate(all="ignore"):
        slope = np.degrees(np.arctan(np.hypot(p, q)))
        aspect = np.degrees(np.arctan2(-p, -q)) % 360.0
    # Flat where the gradient is zero up to the fit's rounding (INFERRED: 1e-10 in z per xy unit).
    aspect = np.where(np.hypot(p, q) < 1e-10, -1.0, aspect)
    return slope, aspect


def slope_aspect(values, nodata_mask, cell_size, neighborhood_distance=None, adaptive=False):
    """Slope in degrees and aspect in degrees clockwise from north (the downslope direction; −1 where flat) of the
    quadratic surface fitted around each cell, as Surface Parameters fits it; NaN where the fit cannot be made (NoData
    in the window, or the window off the raster). ``values`` is a 2-D array of elevations in the units of
    ``cell_size``, its first row the northern one."""
    p, q, _, _, _ = derivatives(values, nodata_mask, cell_size, neighborhood_distance, adaptive)
    slope, aspect = _slope_aspect(p, q)
    bad = ~np.isfinite(p) | ~np.isfinite(q)
    return np.where(bad, np.nan, slope), np.where(bad, np.nan, aspect)


def curvatures(p, q, r, s, t):
    """Minár, Evans and Jenčo's (2020) curvatures, convex positive: profile (normal slope line), tangential (normal
    contour), plan (projected contour), contour geodesic torsion, mean, Gaussian and Casorati. Where the slope is 0,
    the directional ones are 0 (INFERRED: their slope line is undefined there)."""
    w = p * p + q * q
    big = 1 + w
    flat = w < 1e-20
    safe = np.where(flat, 1.0, w)
    with np.errstate(all="ignore"):
        profile = np.where(flat, 0.0, -(p * p * r + 2 * p * q * s + q * q * t) / (safe * big ** 1.5))
        tangential = np.where(flat, 0.0, -(q * q * r - 2 * p * q * s + p * p * t) / (safe * np.sqrt(big)))
        plan = np.where(flat, 0.0, -(q * q * r - 2 * p * q * s + p * p * t) / safe ** 1.5)
        torsion = np.where(flat, 0.0, (p * q * (r - t) - (p * p - q * q) * s) / (safe * big))
        mean = -((1 + q * q) * r - 2 * p * q * s + (1 + p * p) * t) / (2 * big ** 1.5)
        gaussian = (r * t - s * s) / big ** 2
        casorati = np.sqrt(np.maximum(2 * mean * mean - gaussian, 0.0))
    return {"PROFILE_CURVATURE": profile, "TANGENTIAL_CURVATURE": tangential, "CONTOUR_CURVATURE": plan,
            "CONTOUR_GEODESIC_TORSION": torsion, "MEAN_CURVATURE": mean, "GAUSSIAN_CURVATURE": gaussian,
            "CASORATI_CURVATURE": casorati}


def SurfaceParameters(in_raster, parameter_type="SLOPE", local_surface_type="QUADRATIC", neighborhood_distance=None,
                      use_adaptive_neighborhood="FIXED_NEIGHBORHOOD", z_unit="METER",
                      output_slope_measurement="DEGREE", project_geodesic_azimuths="GEODESIC_AZIMUTHS",
                      use_equatorial_aspect="NORTH_POLE_ASPECT", in_analysis_mask=None):
    """EQUIVALENT: as How Surface Parameters works describes — a quadratic surface (z = ax² + by² + cxy + dx + ey + f,
    Evans 1972, Wood 1996) or a biquadratic one (the nine terms of Zevenbergen and Thorne 1987) fitted by least
    squares to the window around each cell, the window (2n + 1) cells wide where n is the neighbourhood distance in
    cells, rounded up, from 1 to 7; with an adaptive neighbourhood only the centre and the window's outer orthogonal
    and diagonal cells are fitted, the window shrinking where the terrain varies (see ``derivatives``). From the fit:
    slope (degrees or percent rise), aspect (downslope, clockwise from north, −1 where flat) and Minár et al.'s (2020)
    curvatures, convex positive. Cells within n of the edge (1 when adaptive) or with NoData in the window are NoData,
    as documented. ``z_unit`` converts the elevations to the raster's planar unit.

    Refused: a raster in a geographic coordinate system (the fit is planar), projected geodesic azimuths and the
    equatorial aspect, which belong to the geodesic computation this equivalent does not make.

    INFERRED: the planar fit stands in for the geocentric one; the adaptive window's DEV threshold; the directional
    curvatures are 0 where the slope is 0; the output is 32-bit floating point."""
    _refuse_environment("arcpy.sa.SurfaceParameters")
    raster = as_raster(in_raster, "SurfaceParameters")
    kind = str(parameter_type or "SLOPE").upper()
    if kind not in _TYPES:
        _refuse("parameter_type", parameter_type)
    surface = str(local_surface_type or "QUADRATIC").upper()
    if surface not in ("QUADRATIC", "BIQUADRATIC"):
        _refuse("local_surface_type", local_surface_type)
    if str(project_geodesic_azimuths or "GEODESIC_AZIMUTHS").upper() not in ("GEODESIC_AZIMUTHS", "FALSE"):
        _refuse("project_geodesic_azimuths", project_geodesic_azimuths, "the fit is planar, not geodesic")
    if str(use_equatorial_aspect or "NORTH_POLE_ASPECT").upper() not in ("NORTH_POLE_ASPECT", "FALSE"):
        _refuse("use_equatorial_aspect", use_equatorial_aspect, "the fit is planar, not geodesic")
    grid = raster._grid
    from ._reference import SpatialReference
    planar_metres = 1.0
    if grid.srid:
        reference = SpatialReference(grid.srid)
        if reference.type != "Projected":
            _refuse("in_raster", in_raster, "a geographic raster needs the geocentric fit, which is not built")
        planar_metres = float(reference.metersPerUnit or 1.0)
    unit = str(z_unit or "METER").upper()
    if unit not in _Z_UNITS:
        _refuse("z_unit", z_unit)
    z = np.asarray(raster._array, np.float64) * (_Z_UNITS[unit] / planar_metres)
    mask = raster._mask.copy()
    distance = neighborhood_distance
    if _given(distance):
        from .analysis import _distance
        distance = _distance(distance, grid.srid)
    adaptive = str(use_adaptive_neighborhood or "FIXED_NEIGHBORHOOD").upper() in ("ADAPTIVE_NEIGHBORHOOD", "TRUE")
    p, q, r, s, t = derivatives(z, mask, grid.width, distance, adaptive, surface == "BIQUADRATIC", grid.height)
    if kind == "SLOPE":
        slope, _ = _slope_aspect(p, q)
        measure = str(output_slope_measurement or "DEGREE").upper()
        if measure == "PERCENT_RISE":
            out = np.hypot(p, q) * 100.0
        elif measure == "DEGREE":
            out = slope
        else:
            _refuse("output_slope_measurement", output_slope_measurement)
    elif kind == "ASPECT":
        out = _slope_aspect(p, q)[1]
    else:
        out = curvatures(p, q, r, s, t)[kind]
    bad = mask | ~np.isfinite(p) | ~np.isfinite(out)
    if _given(in_analysis_mask):
        bad |= _outside_mask(in_analysis_mask, raster)
    return Raster._of(np.where(bad, 0, out).astype(np.float32), bad, grid)


def _outside_mask(in_analysis_mask, raster):
    if isinstance(in_analysis_mask, Raster) or str(in_analysis_mask).lower().endswith((".tif", ".tiff")):
        mask_raster = as_raster(in_analysis_mask, "SurfaceParameters")
        if not mask_raster._grid.same(raster._grid):
            _refuse("in_analysis_mask", in_analysis_mask, "a mask on another grid would need resampling")
        return mask_raster._mask.copy()
    from .sa import _rasterize_any
    return _rasterize_any(in_analysis_mask, raster._grid)[1]


from ._runtime import equivalent as _equivalent  # noqa: E402

SurfaceParameters = _equivalent(*METHOD)(SurfaceParameters)
