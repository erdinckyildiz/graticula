"""arcpy.ddd — 3D Analyst's tools grapy builds, by their documented names and parameters (ADR-224).

3D Analyst's raster tools are geoprocessing tools: each writes its output to a path and answers a Result, where
Spatial Analyst's answer a Raster. Where a ddd tool is the same computation as an ``arcpy.sa`` function, it calls that
function and saves what it answers; the parameter order and defaults are the ddd page's.

Not built, each refused by name with its reason in ``__getattr__`` below:

- TIN, terrain, LAS dataset, point cloud and multipatch tools: data models this server does not hold yet (ADR-224
  group 2).
- Tools whose answer is a Z value on a feature, or which read one: grapy's feature classes are two-dimensional today.
- Tools whose algorithm the documentation describes without specifying (Kriging, Spline, Natural Neighbor, Topo to
  Raster, Viewshed, Visibility …), and tools whose answer lives in a raster attribute table, which grapy's rasters do
  not carry.
"""
from __future__ import annotations

import math

import numpy as np

from . import sa
from ._raster import Raster, _combine, _refuse_environment, as_raster
from ._result import Result
from ._runtime import ExecuteError, NotBuiltError


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.ddd.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _saved(raster, out_raster):
    return Result(raster.save(out_raster))


def _integer_output(raster, tool):
    """The tools whose output the documentation says is always integer: a kept floating-point value is refused,
    since the documentation does not say how it would become an integer."""
    if not raster.isInteger:
        raise NotBuiltError(f"arcpy.ddd.{tool}: the output is always integer, and the documentation does not say how a "
                            "floating-point value kept by missing_values 'DATA' becomes one; map every value, or use "
                            "'NODATA'.")
    return raster


# ---- the same computation as arcpy.sa: called and saved


def Slope(in_raster, out_raster, output_measurement="DEGREE", z_factor=1, method="PLANAR", z_unit="METER",
          analysis_target_device="GPU_THEN_CPU"):
    """arcpy.sa.Slope, saved. INFERRED: the default measurement is ``DEGREE``; the ddd page lists the two values without
    marking one, and the sa page's default is ``DEGREE``."""
    return _saved(sa.Slope(in_raster, output_measurement or "DEGREE", z_factor, method, z_unit), out_raster)


def Aspect(in_raster, out_raster, method="PLANAR", z_unit="METER", project_geodesic_azimuths="GEODESIC_AZIMUTHS",
           analysis_target_device="GPU_THEN_CPU"):
    """arcpy.sa.Aspect, saved. ``project_geodesic_azimuths`` applies only to the geodesic method, which is refused."""
    return _saved(sa.Aspect(in_raster, method, z_unit), out_raster)


def HillShade(in_raster, out_raster, azimuth=315, altitude=45, model_shadows="NO_SHADOWS", z_factor=1):
    """arcpy.sa.Hillshade, saved."""
    return _saved(sa.Hillshade(in_raster, azimuth, altitude, model_shadows, z_factor), out_raster)


def Reclassify(in_raster, reclass_field, remap, out_raster, missing_values="DATA"):
    """arcpy.sa.Reclassify, saved. The ddd output is always integer."""
    return _saved(_integer_output(sa.Reclassify(in_raster, reclass_field, remap, missing_values), "Reclassify"), out_raster)


def ReclassByTable(in_raster, in_remap_table, from_value_field, to_value_field, output_value_field, out_raster,
                   missing_values="DATA"):
    """arcpy.sa.Reclassify with the remap read from a table: each row's from and to values make a range, and its output
    value the new value. A row whose from and to are equal reclassifies that single value.

    INFERRED, as for arcpy.sa.Reclassify's RemapRange: a range includes both its ends, and a value on the boundary of
    two ranges takes the earlier row; the documentation says ranges should not overlap.
    """
    from . import da
    with da.SearchCursor(in_remap_table, [from_value_field, to_value_field, output_value_field]) as rows:
        table = [[low, high, new] for low, high, new in rows]
    for _, _, new in table:
        if new is not None and not float(new).is_integer():
            raise ExecuteError(f"ERROR 000989: ReclassByTable's output values must be integers; {new} is not.")
    remap = sa.RemapRange([[low, high, "NODATA" if new is None else int(new)] for low, high, new in table])
    return _saved(_integer_output(sa.Reclassify(in_raster, "VALUE", remap, missing_values), "ReclassByTable"), out_raster)


def Slice(in_raster, out_raster, number_zones=None, slice_type="EQUAL_INTERVAL", base_output_zone=1, nodata_to_value=None,
          class_interval_size=None):
    """arcpy.sa.Slice, saved; a ``nodata_to_value`` writes that integer where the input is NoData."""
    kind = str(slice_type or "EQUAL_INTERVAL").upper()
    if kind not in ("EQUAL_INTERVAL", "EQUAL_AREA"):
        _refuse("Slice", "slice_type", slice_type)
    if number_zones in (None, "", "#"):
        raise ExecuteError(f"ERROR 000735: Slice needs number_zones for {kind}.")
    sliced = sa.Slice(in_raster, number_zones, kind, base_output_zone)
    if nodata_to_value not in (None, "", "#"):
        array = np.where(sliced._mask, int(nodata_to_value), sliced._array).astype(sliced._array.dtype)
        sliced = Raster._of(array, np.zeros(array.shape, bool), sliced._grid)
    return _saved(sliced, out_raster)


def Plus(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """arcpy.sa.Plus, saved: integer when both inputs are."""
    return _saved(sa.Plus(in_raster_or_constant1, in_raster_or_constant2), out_raster)


def Minus(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """arcpy.sa.Minus, saved."""
    return _saved(sa.Minus(in_raster_or_constant1, in_raster_or_constant2), out_raster)


def Times(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """arcpy.sa.Times, saved."""
    return _saved(sa.Times(in_raster_or_constant1, in_raster_or_constant2), out_raster)


def Float(in_raster_or_constant, out_raster):
    """arcpy.sa.Float, saved."""
    return _saved(sa.Float(in_raster_or_constant), out_raster)


def Int(in_raster_or_constant, out_raster):
    """arcpy.sa.Int, saved: the values truncated."""
    return _saved(sa.Int(in_raster_or_constant), out_raster)


def Divide(in_raster_or_constant1, in_raster_or_constant2, out_raster):
    """The first input divided by the second, cell by cell; NoData where the divisor is zero. As the ddd page documents,
    two integer inputs give an integer division (3 / 2 is 1) and an integer raster; otherwise the output is floating
    point. This is not arcpy.sa.Divide as grapy builds it, which is always floating point, so it is computed here.

    INFERRED: the integer division truncates toward zero (−3 / 2 is −1); the page's only example is positive.
    """
    _refuse_environment("arcpy.ddd.Divide")
    values = [as_raster(v, "Divide") if isinstance(v, str) else v for v in (in_raster_or_constant1, in_raster_or_constant2)]

    def integer(v):
        return v.isInteger if isinstance(v, Raster) else isinstance(v, (int, np.integer)) and not isinstance(v, bool)

    if all(integer(v) for v in values):
        def operation(x, y):
            y = np.asarray(y)
            zero = y == 0
            safe = np.where(zero, 1, y)
            return (np.trunc(np.asarray(x, float) / safe)).astype(np.int32), zero
        return _saved(_combine(values, operation, keep_integer=True, tool="Divide"), out_raster)
    return _saved(sa.Divide(*values), out_raster)


# ---- surfaces from points


def _cell_size(cell_size, tool):
    if cell_size in (None, "", "#"):
        return None
    if isinstance(cell_size, Raster):
        return float(cell_size.meanCellWidth)
    try:
        return float(cell_size)
    except (TypeError, ValueError):
        return float(as_raster(cell_size, tool).meanCellWidth)


def _points(in_point_features, z_field, tool):
    """The points' coordinates and z values, and the grid they are interpolated on."""
    from . import da
    from .analysis import _schema
    columns, kind, _, _ = _schema(in_point_features)
    if kind not in ("Point", "Multipoint"):
        raise ExecuteError(f"ERROR 000365: {tool} takes points.")
    if str(z_field).upper() in ("SHAPE", "SHAPE@Z"):
        _refuse(tool, "z_field", z_field, "grapy's feature classes hold no z values yet; name a numeric field")
    names = {c.name.lower(): c for c in columns}
    if str(z_field).lower() not in names or names[str(z_field).lower()].type == "String":
        raise ExecuteError(f"ERROR 000728: '{z_field}' is not a numeric field of {in_point_features}.")
    xs, ys, zs = [], [], []
    with da.SearchCursor(in_point_features, [names[str(z_field).lower()].name, "SHAPE@"]) as rows:
        for value, shape in rows:
            if shape is None or value is None:
                continue
            for p in getattr(shape._shape, "geoms", [shape._shape]):
                xs.append(p.x)
                ys.append(p.y)
                zs.append(float(value))
    if not xs:
        raise ExecuteError(f"ERROR 010151: {tool} found no points with a value.")
    return np.array(xs), np.array(ys), np.array(zs)


def _point_grid(in_point_features, cell_size, xs, ys, tool):
    """The output grid: over the points' extent, its top-left corner at the extent's, at the cell size given or, by
    the documented rule, the shorter side of the extent divided by 250."""
    _refuse_environment(f"arcpy.ddd.{tool}")
    cell = _cell_size(cell_size, tool)
    if cell is None:
        shorter = min(xs.max() - xs.min(), ys.max() - ys.min())
        if shorter <= 0:
            _refuse(tool, "cell_size", "(empty)", "the points' extent has no width or height to divide by 250")
        cell = shorter / 250.0
    return sa._grid_over(in_point_features, cell)


def _radius(search_radius):
    """A search radius as (kind, first, second): Variable with a count and maximum distance, or Fixed with a distance
    and minimum count. Accepts arcpy's RadiusVariable/RadiusFixed objects or their text."""
    if search_radius in (None, "", "#"):
        return "VARIABLE", 12, None
    if hasattr(search_radius, "numberofPoints"):
        return "VARIABLE", int(search_radius.numberofPoints), getattr(search_radius, "maxDistance", None)
    if hasattr(search_radius, "distance"):
        return "FIXED", getattr(search_radius, "distance", None), int(getattr(search_radius, "minNumberofPoints", 0) or 0)
    parts = [p for p in str(search_radius).replace(",", " ").split() if p]
    kind = parts[0].upper()
    if kind == "VARIABLE":
        return kind, int(float(parts[1])) if len(parts) > 1 else 12, float(parts[2]) if len(parts) > 2 else None
    if kind == "FIXED":
        return kind, float(parts[1]) if len(parts) > 1 else None, int(float(parts[2])) if len(parts) > 2 else 0
    raise ExecuteError(f"ERROR 000800: '{search_radius}' is not a Variable or Fixed search radius.")


def Idw(in_point_features, z_field, out_raster, cell_size=None, power=2, search_radius=None,
        in_barrier_polyline_features=None):
    """Inverse distance weighted interpolation: each cell centre is the average of the points found for it, each
    weighted by one over its distance to the centre raised to ``power``. A Variable radius takes the nearest
    ``number_of_points`` (12) within an optional maximum distance; a Fixed radius takes every point within the
    distance (five cells by default), widening to the nearest ``minimum_number_of_points`` when fewer are found. A cell
    that finds no point is NoData. The output is floating point.

    Barriers are refused: the documentation says only points on the cell's side of a barrier count, and does not say how
    a side is decided where a barrier ends.

    INFERRED: the weight is 1/dᵖ, normalised to sum to 1 — the documentation names IDW's linear weighted combination
    and its power without writing the formula; a point at a cell centre gives that cell its value; the maximum distance,
    when not given, is the output extent's diagonal; points found at equal distance in the last place are taken in the
    order they are stored; the grid's top-left corner is the points' extent's; points at one place are refused, since the
    documentation says the first or the last of them is used, depending on the case.
    """
    if in_barrier_polyline_features not in (None, "", "#"):
        _refuse("Idw", "in_barrier_polyline_features", in_barrier_polyline_features)
    p = float(power)
    if p <= 0:
        raise ExecuteError("ERROR 000859: Idw's power must be greater than zero.")
    xs, ys, zs = _points(in_point_features, z_field, "Idw")
    if len({(x, y) for x, y in zip(xs, ys)}) != len(xs):
        _refuse("Idw", "in_point_features", in_point_features, "coincident points are used first or last depending on "
                "the case, and the documentation does not say which case is which")
    grid = _point_grid(in_point_features, cell_size, xs, ys, "Idw")
    kind, first, second = _radius(search_radius)
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx, cy)
    distance = np.hypot(gx[..., None] - xs, gy[..., None] - ys)
    order = np.argsort(distance, axis=-1, kind="stable")
    ranked = np.take_along_axis(distance, order, axis=-1)
    values = zs[order]
    if kind == "VARIABLE":
        limit = float(second) if second not in (None, "") else math.hypot(grid.right - grid.left, grid.top - grid.bottom)
        used = (np.arange(len(xs)) < first) & (ranked <= limit)
    else:
        radius = float(first) if first not in (None, "") else 5 * grid.width
        used = (ranked <= radius) | (np.arange(len(xs)) < second)
    exact = ranked[..., 0] == 0
    with np.errstate(divide="ignore"):
        weights = np.where(used, 1.0 / np.where(ranked == 0, 1.0, ranked) ** p, 0.0)
    total = weights.sum(axis=-1)
    with np.errstate(invalid="ignore", divide="ignore"):
        surface = (weights * values).sum(axis=-1) / total
    surface = np.where(exact, values[..., 0], surface)
    mask = (total == 0) & ~exact
    return _saved(Raster._of(np.where(mask, 0, surface).astype(np.float32), mask, grid), out_raster)


def Trend(in_point_features, z_field, out_raster, cell_size=None, order=1, regression_type="LINEAR", out_rms_file=None):
    """A polynomial surface fitted to the points by least squares and evaluated at each cell centre. Order n uses every
    term xⁱyʲ with i + j ≤ n — for order 3: 1, x, y, x², xy, y², x³, x²y, xy², y³, as *How Trend works* writes it.

    ``LOGISTIC`` is refused (its maximum likelihood fit is not specified), and so is ``out_rms_file``, whose layout the
    documentation shows only by example.

    INFERRED: the default regression is ``LINEAR`` (the page marks none); the grid is as Idw's. The fit is computed on
    coordinates shifted and scaled for numerical accuracy, which changes nothing in a least-squares polynomial's values.
    """
    if str(regression_type or "LINEAR").upper() != "LINEAR":
        _refuse("Trend", "regression_type", regression_type, "the logistic fit is not specified")
    if out_rms_file not in (None, "", "#"):
        _refuse("Trend", "out_rms_file", out_rms_file, "its layout is shown only by example")
    n = int(order)
    if not 1 <= n <= 12:
        raise ExecuteError("ERROR 000854: Trend's order is from 1 to 12.")
    xs, ys, zs = _points(in_point_features, z_field, "Trend")
    grid = _point_grid(in_point_features, cell_size, xs, ys, "Trend")
    x0, y0 = xs.mean(), ys.mean()
    scale = max(np.ptp(xs), np.ptp(ys)) or 1.0
    terms = [(d - j, j) for d in range(n + 1) for j in range(d + 1)]

    def design(x, y):
        u, v = (x - x0) / scale, (y - y0) / scale
        return np.column_stack([u ** i * v ** j for i, j in terms])

    if len(xs) < len(terms):
        raise ExecuteError(f"ERROR 010151: Trend of order {n} needs at least {len(terms)} points.")
    coefficients, *_ = np.linalg.lstsq(design(xs, ys), zs, rcond=None)
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx, cy)
    surface = (design(gx.ravel(), gy.ravel()) @ coefficients).reshape(gx.shape)
    return _saved(Raster._of(surface.astype(np.float32), np.zeros(surface.shape, bool), grid), out_raster)


# ---- surface analysis


def Curvature(in_raster, out_curvature_raster, z_factor=1, out_profile_curve_raster=None, out_plan_curve_raster=None):
    """The curvature of each cell from its 3×3 neighbourhood, as *How Curvature works* documents:
    ``−2(D + E) · 100`` with ``D = ((Z4 + Z6)/2 − Z5)/L²`` and ``E = ((Z2 + Z8)/2 − Z5)/L²`` — the second differences
    across the row and the column through the centre cell. Positive is convex; the unit is one hundredth of a z unit.

    The profile and plan outputs are refused: their formulas are not written in the documentation's text.

    INFERRED: L is the cell width across a row and the cell height down a column; a neighbour that is NoData or off the
    grid takes the centre cell's value, as grapy's Slope does by Esri's documentation of that tool.
    """
    for name, value in (("out_profile_curve_raster", out_profile_curve_raster), ("out_plan_curve_raster", out_plan_curve_raster)):
        if value not in (None, "", "#"):
            _refuse("Curvature", name, value, "its formula is not written in the documentation's text")
    _refuse_environment("arcpy.ddd.Curvature")
    raster = as_raster(in_raster, "Curvature")
    n = sa._neighbours(raster)
    zf = float(z_factor)
    w, h = raster._grid.width, raster._grid.height
    d = ((n["d"] + n["f"]) / 2 - n["e"]) * zf / (w * w)
    e = ((n["b"] + n["h"]) / 2 - n["e"]) * zf / (h * h)
    curvature = -2 * (d + e) * 100
    return _saved(Raster._of(curvature.astype(np.float32), raster._mask.copy(), raster._grid), out_curvature_raster)


def _contour_lines(raster, levels, z_factor=1.0):
    """Contours by the method *How contouring works* describes: each 2×2 block of cell centres gets a centre value, the
    mean of its four (rounded when the raster is integer, as the page's example rounds 813.5 to 814), and its four
    triangles — two corners and that centre — are contoured by linear interpolation along their edges. A block with a
    NoData corner gives no contour, and nothing is extrapolated past the outer cell centres.

    INFERRED: a corner exactly at a contour value counts as above it, so a contour through cell values that equal it
    runs once, not twice; each connected line is one feature."""
    from shapely.geometry import LineString, MultiLineString
    from shapely.ops import linemerge
    z = raster._array.astype(np.float64) * z_factor
    mask = raster._mask
    xs, ys = raster._grid.centres()
    rows, columns = z.shape
    segments = {level: [] for level in levels}
    for r in range(rows - 1):
        for c in range(columns - 1):
            if mask[r:r + 2, c:c + 2].any():
                continue
            corners = [(xs[c], ys[r], z[r, c]), (xs[c + 1], ys[r], z[r, c + 1]),
                       (xs[c + 1], ys[r + 1], z[r + 1, c + 1]), (xs[c], ys[r + 1], z[r + 1, c])]
            middle = sum(k[2] for k in corners) / 4
            if raster.isInteger:
                middle = math.floor(middle + 0.5)
            centre = ((xs[c] + xs[c + 1]) / 2, (ys[r] + ys[r + 1]) / 2, middle)
            for i in range(4):
                triangle = (corners[i], corners[(i + 1) % 4], centre)
                for level in levels:
                    cut = []
                    for a, b in ((0, 1), (1, 2), (2, 0)):
                        p, q = sorted((triangle[a], triangle[b]))
                        if (p[2] >= level) != (q[2] >= level):
                            t = (level - p[2]) / (q[2] - p[2])
                            point = (p[0] + t * (q[0] - p[0]), p[1] + t * (q[1] - p[1]))
                            if point not in cut:
                                cut.append(point)
                    if len(cut) == 2 and cut[0] != cut[1]:
                        segments[level].append(tuple(sorted(cut)))
    lines = []
    for level in levels:
        unique = sorted(set(segments[level]))
        if not unique:
            continue
        merged = linemerge(MultiLineString([list(s) for s in unique]))
        for part in getattr(merged, "geoms", [merged]):
            lines.append((level, LineString(part)))
    return lines


def _write_contours(out_polyline_features, raster, lines):
    from . import _gpkg, da
    from .analysis import _out
    out = _out(out_polyline_features)
    _gpkg.create(out, "Polyline", raster._grid.srid, [_gpkg.Column("Contour", "Double")])
    with da.InsertCursor(out, ["Contour", "SHAPE@"]) as written:
        for level, line in lines:
            written.insertRow((float(level), line))
    return Result(out)


def Contour(in_raster, out_polyline_features, contour_interval, base_contour=0, z_factor=1, contour_type="CONTOUR",
            max_vertices_per_feature=None):
    """Contour lines at ``base_contour`` plus every multiple of ``contour_interval``, above and below it, across the
    raster's range of values (after ``z_factor``), each with its value in ``Contour``.

    Only ``CONTOUR`` lines are built; the polygon types and ``max_vertices_per_feature`` are refused.

    INFERRED: the value field is named ``Contour``; the documentation says only that each line carries its elevation.
    See the method in ``_contour_lines``.
    """
    if str(contour_type or "CONTOUR").upper() != "CONTOUR":
        _refuse("Contour", "contour_type", contour_type, "only CONTOUR lines are built")
    if max_vertices_per_feature not in (None, "", "#", 0):
        _refuse("Contour", "max_vertices_per_feature", max_vertices_per_feature)
    interval = float(contour_interval)
    if interval <= 0:
        raise ExecuteError("ERROR 000859: Contour's interval must be positive.")
    _refuse_environment("arcpy.ddd.Contour")
    raster = as_raster(in_raster, "Contour")
    zf, base = float(z_factor), float(base_contour or 0)
    values = raster._values().astype(np.float64) * zf
    if not values.size:
        return _write_contours(out_polyline_features, raster, [])
    low = math.ceil((values.min() - base) / interval)
    high = math.floor((values.max() - base) / interval)
    levels = [base + k * interval for k in range(low, high + 1)]
    return _write_contours(out_polyline_features, raster, _contour_lines(raster, levels, zf))


def ContourList(in_raster, out_polyline_features, contour_values, contour_type="CONTOUR"):
    """Contour lines at each of the values given, in any order, made as Contour makes them."""
    from .management import _list
    if str(contour_type or "CONTOUR").upper() != "CONTOUR":
        _refuse("ContourList", "contour_type", contour_type, "only CONTOUR lines are built")
    _refuse_environment("arcpy.ddd.ContourList")
    raster = as_raster(in_raster, "ContourList")
    levels = sorted({float(v) for v in _list(contour_values)})
    return _write_contours(out_polyline_features, raster, _contour_lines(raster, levels))


def AddSurfaceInformation(in_feature_class, in_surface, out_property, method="BILINEAR", sample_distance=None, z_factor=1,
                          pyramid_level_resolution=0, noise_filtering=None):
    """The surface's height under each point written to a ``Z`` field: the raster value at the point by bilinear
    interpolation from the four nearest cell centres, times ``z_factor``.

    Built: point features on a raster surface, property ``Z``. The other properties are refused: the documentation says
    a raster is first turned into an intermediate TIN, and does not say how. TIN, terrain and LAS surfaces need data
    models this server does not hold yet (ADR-224 group 2).

    INFERRED: the field is named after the property, ``Z``, a double; a point over NoData or off the raster gets an
    empty value; at the outermost half cell, where four centres do not surround the point, grapy's raster sampling
    answers the cell's own value (see ``arcpy.sa``'s ``_sample``).
    """
    from . import _gpkg, da
    from .analysis import _schema
    from .management import _list
    properties = [p.upper() for p in _list(out_property)]
    if properties != ["Z"]:
        _refuse("AddSurfaceInformation", "out_property", out_property, "on a raster only Z at points is built")
    if str(method or "BILINEAR").upper() != "BILINEAR":
        _refuse("AddSurfaceInformation", "method", method, "a raster surface takes only BILINEAR")
    if isinstance(in_surface, str) and not in_surface.lower().endswith((".tif", ".tiff")):
        _refuse("AddSurfaceInformation", "in_surface", in_surface,
                "only a raster surface is built; TIN, terrain and LAS need data models this server does not hold yet "
                "(ADR-224 group 2)")
    raster = as_raster(in_surface, "AddSurfaceInformation")
    columns, kind, _, _ = _schema(in_feature_class)
    if kind != "Point":
        _refuse("AddSurfaceInformation", "in_feature_class", kind, "only points are built on a raster surface")
    field = next((c.name for c in columns if c.name.lower() == "z"), None)
    if field is None:
        field = "Z"
        _gpkg.add_column(in_feature_class, _gpkg.Column(field, "Double"))
    zf = float(z_factor)
    with da.UpdateCursor(in_feature_class, [field, "SHAPE@"]) as rows:
        for row in rows:
            point = row[1]._shape if row[1] is not None else None
            value = sa._sample(raster, point.x, point.y, True) if point is not None else None
            rows.updateRow([None if value is None else float(value) * zf, row[1]])
    return Result(in_feature_class)


# ---- refused, by name, with the reason

_GROUP_2 = "needs a data model this server does not hold yet (ADR-224 group 2)"
_NO_Z = "it reads or writes z values on features, and grapy's feature classes are two-dimensional today"
_UNSPECIFIED = "its algorithm is described in the documentation but not specified closely enough to reproduce"
_NO_RAT = "its answer is held in a raster attribute table, which grapy's rasters do not carry yet"

_REFUSED = {name: _GROUP_2 for name in (
    "AddFeatureClassToTerrain", "AddTerrainPoints", "AddTerrainPyramidLevel", "AppendTerrainPoints", "BuildTerrain",
    "ChangeTerrainReferenceScale", "ChangeTerrainResolutionBounds", "CreateTerrain", "DeleteTerrainPoints",
    "RemoveFeatureClassFromTerrain", "RemoveTerrainPoints", "RemoveTerrainPyramidLevel", "ReplaceTerrainPoints",
    "TerrainToPoints", "TerrainToRaster", "TerrainToTin", "BIMToTerrain",
    "CopyTin", "CreateTin", "DecimateTinNodes", "DelineateTinDataArea", "EditTin", "LandXMLToTin", "RasterTin",
    "TINToMultipatch", "TinAspect", "TinContour", "TinDifference", "TinDomain", "TinEdge", "TinLine", "TinNode",
    "TinPolygonTag", "TinPolygonVolume", "TinRaster", "TinSlope", "TinTriangle",
    "ChangeLasClassCodes", "ClassifyLasBuilding", "ClassifyLasByHeight", "ClassifyLasGround", "ClassifyLasNoise",
    "ClassifyLasOverlap", "ColorizeLas", "ExtractLas", "LASToMultipoint", "LasBuildingMultipatch", "LasDatasetToTin",
    "LasHeightMetrics", "LasPointStatsByArea", "LocateLasPointsByProximity", "SetLasClassCodesUsingFeatures",
    "SetLasClassCodesUsingRaster", "ThinLas", "TileLas", "PointFileInformation",
    "ClassifyPointCloudUsingTrainedModel", "DetectObjectsFromPointCloudUsingTrainedModel",
    "EvaluatePointCloudClassificationModel", "ExtractObjectsFromPointCloud", "ExtractPowerLinesFromPointCloud",
    "ExtractRailsFromPointCloud", "ExtractMeshFeaturesUsingPointCloud", "PreparePointCloudObjectDetectionTrainingData",
    "PreparePointCloudTrainingData", "TrainPointCloudClassificationModel", "TrainPointCloudObjectDetectionModel",
    "ExtractLOD2Buildings", "ExtractMultipatchFromMesh",
    "EncloseMultiPatch", "Import3DFiles", "InterpolatePolyToPatch", "Intersect3DLineWithMultiPatch", "MultiPatchFootprint",
    "IsClosed3D", "Inside3D", "Intersect3D", "Union3D", "Difference3D", "ExtrudeBetween", "SunShadowVolume",
    "SurfaceAspect", "SurfaceSlope", "SurfaceContour", "SurfaceDifference", "LocateOutliers",
)}
_REFUSED.update({name: _NO_Z for name in (
    "AddZInformation", "CalculateMissingZValues", "FeatureTo3DByAttribute", "UpdateFeatureZ", "Simplify3DLine",
    "GeneratePointsAlong3DLines", "Buffer3D", "Near3D", "Intersect3DLines", "Intersect3DLineWithSurface", "LineOfSight",
    "ConstructSightLines", "Intervisibility", "InterpolateShape", "RasterDomain", "RasterToMultipoint",
    "FeatureClassZToASCII", "ASCII3DToFeatureClass", "MinimumBoundingVolume", "Layer3DToFeatureClass",
)})
_REFUSED.update({name: _UNSPECIFIED for name in (
    "Kriging", "NaturalNeighbor", "Spline", "SplineWithBarriers", "TopoToRaster", "TopoToRasterByFile", "Viewshed",
    "Viewshed2", "Visibility", "ObserverPoints", "SurfaceVolume", "SurfaceParameters", "StackProfile",
    "ContourWithBarriers",
)})
_REFUSED.update({
    "CutFill": _NO_RAT + " — the VOLUME and AREA of each region",
    "Lookup": _NO_RAT,
    "ReclassByASCIIFile": "the documentation does not specify the remap file's format",
    "LayerToKML": "it reads map layers, which a user tool on the server does not have",
    "MapToKML": "it reads a map, which a user tool on the server does not have",
    "Skyline": _NO_Z, "SkylineBarrier": _NO_Z, "SkylineGraph": _NO_Z,
})


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.ddd.{name} is not built in grapy" + (f": {why}." if why else " yet."))

    return missing
