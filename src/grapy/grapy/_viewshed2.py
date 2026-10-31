"""Observer Points and Viewshed 2 (ADR-243), on Viewshed's R3 line of sight (Franklin and Ray 1994), which grapy.sa
holds as an equivalent (ADR-224 §5.6)."""
from __future__ import annotations

import math

import numpy as np

from ._raster import Raster, _refuse_environment, as_raster
from ._runtime import NotBuiltError, equivalent


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.sa.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


def _metric(raster, tool, parameter="in_raster"):
    from ._reference import SpatialReference
    g = raster._grid
    if not g.srid or SpatialReference(g.srid).type != "Projected" or SpatialReference(g.srid).metersPerUnit != 1:
        _refuse(tool, parameter, "(its coordinate system)", "the earth's curvature is corrected for data in metres")


def ObserverPoints(in_raster, in_observer_point_features, z_factor=1, curvature_correction="FLAT_EARTH",
                   refractivity_coefficient=0.13, out_agl_raster=None):
    """EQUIVALENT: Viewshed's R3 line of sight (see arcpy.sa.Viewshed), observer by observer, as Using Viewshed and
    Observer Points describes: each cell's value encodes which observers see it — observer n contributes 2ⁿ⁻¹ — and
    the output's attribute table holds Value, Count and OBS1 … OBSn, 1 where observer n sees the cells of that value
    and 0 where it does not. At most 16 points, as documented; SPOT, OFFSETA (default 1) and OFFSETB (default 0) are
    read as Viewshed reads them; ``out_agl_raster`` holds the least height that makes a cell visible to at least one
    observer, 0 where one sees it, as documented.

    INFERRED: observer n is the n-th point the cursor reads; the bit order of the binary encoding (the page says only
    that the value is binary-encoded, and the OBSn fields decode it)."""
    tool = "ObserverPoints"
    from . import sa
    from ._raster import _Rat
    from .analysis import _schema
    _refuse_environment(f"arcpy.sa.{tool}")
    if _schema(in_observer_point_features)[1] not in ("Point",):
        _refuse(tool, "in_observer_point_features", in_observer_point_features,
                "it takes points, as documented (Feature Vertices To Points converts lines)")
    raster = as_raster(in_raster, tool)
    zf = float(z_factor if _given(z_factor) else 1)
    curved = str(curvature_correction or "FLAT_EARTH").upper() == "CURVED_EARTH"
    if curved:
        _metric(raster, tool, "curvature_correction")
    refraction = float(refractivity_coefficient if _given(refractivity_coefficient) else 0.13)
    values = {"elevation": ("SPOT", None, None), "observer_offset": ("OFFSETA", 1.0, None),
              "surface_offset": ("OFFSETB", 0.0, None)}
    observers = sa._observers(in_observer_point_features, raster, zf, values, tool)
    if len(observers) > 16:
        raise ValueError(f"{tool}: at most 16 observer points are allowed, as documented; {len(observers)} were given.")
    z = raster._array.astype(np.float64) * zf
    code = np.zeros(z.shape, np.int64)
    agl = np.full(z.shape, np.inf)
    for n, (ox, oy, p) in enumerate(observers):
        target = z + p["surface_offset"]
        visible, needed = sa._line_of_sight(target, raster._mask, raster._grid, ox, oy,
                                            p["elevation"] + p["observer_offset"], curved, refraction)
        code |= visible.astype(np.int64) << n
        agl = np.minimum(agl, np.where(visible, 0.0, needed))
    mask = raster._mask.copy()
    if _given(out_agl_raster):
        agl = np.where(np.isfinite(agl), agl, 0.0)
        Raster._of(np.where(mask, 0, agl).astype(np.float32), mask.copy(), raster._grid).save(out_agl_raster)
    out = np.where(mask, 0, code).astype(np.int32)
    result = Raster._of(out, mask, raster._grid)
    present, counts = np.unique(out[~mask], return_counts=True)
    names = [f"OBS{n + 1}" for n in range(len(observers))]
    result._rat = _Rat([("Value", "Integer", 5), ("Count", "Integer", 1), *[(name, "Integer", 0) for name in names]],
                       [[int(v), int(c), *[(int(v) >> n) & 1 for n in range(len(observers))]]
                        for v, c in zip(present, counts)])
    return result


def _linear(value, raster, columns):
    """A field's name as it is; a number, or a linear unit such as '6 Feet', in the raster's units (a bare number, or
    'Unknown', in metres — the tool's default unit — as the page's table of strings says)."""
    if not _given(value):
        return None
    if isinstance(value, str) and value.strip().upper() in columns:
        return value.strip()
    from .analysis import _distance
    from ._reference import SpatialReference
    metres = _distance(value if not isinstance(value, str) else value.strip(), raster._grid.srid)
    per = SpatialReference(raster._grid.srid).metersPerUnit if raster._grid.srid else 1.0
    return metres / float(per or 1.0)


def _angle(value, columns):
    if not _given(value):
        return None
    if isinstance(value, str) and value.strip().upper() in columns:
        return value.strip()
    return float(value)


def Viewshed2(in_raster, in_observer_features, out_agl_raster=None, analysis_type="FREQUENCY", vertical_error=None,
              out_observer_region_relationship_table=None, refractivity_coefficient=0.13, surface_offset="0 Meters",
              observer_elevation=None, observer_offset="1 Meters", inner_radius=None, inner_radius_is_3d="GROUND",
              outer_radius=None, outer_radius_is_3d="GROUND", horizontal_start_angle=0, horizontal_end_angle=360,
              vertical_upper_angle=90, vertical_lower_angle=-90, analysis_method="ALL_SIGHTLINES",
              analysis_target_device="GPU_THEN_CPU"):
    """EQUIVALENT: Viewshed's R3 line of sight (see arcpy.sa.Viewshed) with Viewshed 2's observer parameters — each a
    number, a linear unit ('6 Feet', converted to the raster's units) or a field: surface offset (0), observer
    elevation (the surface, bilinearly interpolated), observer offset (1 metre), inner and outer radius (0 and
    unlimited, on the ground or in 3D), horizontal angles (0 to 360, clockwise from north) and vertical angles (−90 to
    90). A cell nearer than the inner radius is not visible but still blocks, as documented; the earth's curvature is
    always corrected, with the refractivity coefficient (Z − D²/12,740,000 + R·D²/12,740,000 in metres, How Viewshed
    works) — standing in for the page's geocentric transformation. FREQUENCY counts the observers that see each
    cell; ``out_agl_raster`` holds the least height that makes a cell visible, 0 where it is.

    Refused: OBSERVERS, whose region numbering and relationship table's fields the page does not give; a vertical
    error, whose probabilities the page does not define; a raster not in a projected coordinate system in metres.

    INFERRED: the curvature correction for the geocentric transformation; z in the raster's xy units; PERIMETER_SIGHTLINES
    answers as ALL_SIGHTLINES, which the page calls the more accurate; the analysis device is the CPU."""
    tool = "Viewshed2"
    from . import sa
    from .analysis import _schema
    if str(analysis_type or "FREQUENCY").upper() != "FREQUENCY":
        _refuse(tool, "analysis_type", analysis_type, "the region numbering and the relationship table's fields are "
                                                      "not documented")
    if _given(out_observer_region_relationship_table):
        _refuse(tool, "out_observer_region_relationship_table", out_observer_region_relationship_table,
                "its fields are not documented")
    if _given(vertical_error) and float(str(vertical_error).split()[0]) > 0:
        _refuse(tool, "vertical_error", vertical_error, "how the probabilities are computed is not documented")
    if str(analysis_method or "ALL_SIGHTLINES").upper() not in ("ALL_SIGHTLINES", "PERIMETER_SIGHTLINES"):
        _refuse(tool, "analysis_method", analysis_method)
    raster = as_raster(in_raster, tool)
    _metric(raster, tool)
    columns = {c.name.upper() for c in _schema(in_observer_features)[0]}
    three_d = {"3D", "TRUE"}
    inner_3d = str(inner_radius_is_3d).upper() in three_d or inner_radius_is_3d is True
    outer_3d = str(outer_radius_is_3d).upper() in three_d or outer_radius_is_3d is True
    values = {"surface_offset": (None, 0.0, _linear(surface_offset, raster, columns)),
              "elevation": (None, None, _linear(observer_elevation, raster, columns)),
              "observer_offset": (None, 1.0, _linear(observer_offset, raster, columns)),
              "inner": (None, 0.0, _linear(inner_radius, raster, columns)),
              "outer": (None, math.inf, _linear(outer_radius, raster, columns)),
              "start": (None, 0.0, _angle(horizontal_start_angle, columns)),
              "end": (None, 360.0, _angle(horizontal_end_angle, columns)),
              "upper": (None, 90.0, _angle(vertical_upper_angle, columns)),
              "lower": (None, -90.0, _angle(vertical_lower_angle, columns))}

    def limits(p, dx, dy, dz):
        flat = np.hypot(dx, dy)
        solid = np.hypot(flat, dz)
        keep = np.ones(flat.shape, bool)
        if p["outer"] is not None and np.isfinite(p["outer"]):
            keep &= (solid if outer_3d else flat) <= p["outer"]
        if p["inner"]:
            keep &= (solid if inner_3d else flat) >= p["inner"]
        azimuth = np.degrees(np.arctan2(dx, dy)) % 360
        start, end = p["start"] % 360, p["end"] % 360 if p["end"] != 360 else 360
        keep &= ((azimuth >= start) & (azimuth <= end)) if start <= end else ((azimuth >= start) | (azimuth <= end))
        angle = np.degrees(np.arctan2(dz, flat))
        keep &= (angle <= p["upper"]) & (angle >= p["lower"])
        keep |= flat == 0
        return keep

    raster, count, agl = sa._visibility(tool, raster, in_observer_features, 1, "CURVED_EARTH",
                                        refractivity_coefficient, values, limits)
    mask = raster._mask.copy()
    if _given(out_agl_raster):
        Raster._of(np.where(mask, 0, agl).astype(np.float32), mask.copy(), raster._grid).save(out_agl_raster)
    return Raster._of(np.where(mask, 0, count).astype(np.int32), mask, raster._grid)


SOURCE = ("R3 line of sight with linear interpolation between cells (Franklin and Ray 1994, Higher isn't necessarily "
          "better), with the curvature and refraction correction and observer fields of How Viewshed works")
ObserverPoints = equivalent("R3 line-of-sight visibility, observer by observer, binary-encoded", SOURCE)(ObserverPoints)
Viewshed2 = equivalent("R3 line-of-sight viewshed with Viewshed 2's observer parameters", SOURCE)(Viewshed2)
