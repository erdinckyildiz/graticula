"""arcpy.sa — Spatial Analyst's tools grapy builds, by their documented names and parameters (ADR-224).

Each tool takes rasters (Raster objects or GeoTIFF paths) and answers a temporary Raster, which ``save`` writes, as
arcpy's do. NoData follows arcpy's rule: NoData in, NoData out, unless a tool documents otherwise (IsNull, Con's false
branch, CellStatistics with ``DATA``).

The surface tools use the 3×3 neighbourhood and the formulas Esri publishes in *How Slope works*, *How Aspect works*
and *How Hillshade works* (Horn's method). Where a neighbour is NoData or off the grid, it takes the centre cell's
value, which is what those pages document.
"""
from __future__ import annotations

import math
import re

import numpy as np

from ._raster import Raster, _combine, _Grid, _refuse_environment, as_raster
from ._raster import NumPyArrayToRaster, RasterToNumPyArray  # noqa: F401 — arcpy.sa answers these too
from ._runtime import NotBuiltError

# ---- one-input mathematics


def _unary(name, function, keep_integer=False):
    def tool(in_raster_or_constant):
        _refuse_environment(f"arcpy.sa.{name}")
        return _combine([as_raster(in_raster_or_constant, name)], function, keep_integer=keep_integer, tool=name)
    tool.__name__ = name
    tool.__doc__ = f"arcpy.sa.{name}, cell by cell."
    return tool


Abs = _unary("Abs", np.abs, keep_integer=True)
Exp = _unary("Exp", np.exp)
Exp2 = _unary("Exp2", np.exp2)
Exp10 = _unary("Exp10", lambda a: np.power(10.0, a))
Ln = _unary("Ln", lambda a: np.where(np.asarray(a) > 0, np.log(np.where(np.asarray(a) > 0, a, 1)), np.nan))
Log2 = _unary("Log2", lambda a: np.where(np.asarray(a) > 0, np.log2(np.where(np.asarray(a) > 0, a, 1)), np.nan))
Log10 = _unary("Log10", lambda a: np.where(np.asarray(a) > 0, np.log10(np.where(np.asarray(a) > 0, a, 1)), np.nan))
Sqrt = _unary("Sqrt", lambda a: np.where(np.asarray(a) >= 0, np.sqrt(np.abs(a)), np.nan))
Square = _unary("Square", lambda a: np.square(a), keep_integer=True)
Negate = _unary("Negate", np.negative, keep_integer=True)
def _int(a):
    """Truncated toward zero; a value outside the 32-bit range is NoData, as Int's page documents."""
    truncated = np.trunc(np.asarray(a, dtype=float))
    outside = (truncated < np.iinfo(np.int32).min) | (truncated > np.iinfo(np.int32).max) | ~np.isfinite(truncated)
    return np.where(outside, 0, truncated).astype(np.int32), outside


Int = _unary("Int", _int, keep_integer=True)
Float = _unary("Float", lambda a: np.asarray(a, dtype=np.float64))
RoundUp = _unary("RoundUp", np.ceil)
RoundDown = _unary("RoundDown", np.floor)
Sin = _unary("Sin", np.sin)
Cos = _unary("Cos", np.cos)
Tan = _unary("Tan", np.tan)
ASin = _unary("ASin", lambda a: np.where(np.abs(a) <= 1, np.arcsin(np.clip(a, -1, 1)), np.nan))
ACos = _unary("ACos", lambda a: np.where(np.abs(a) <= 1, np.arccos(np.clip(a, -1, 1)), np.nan))
ATan = _unary("ATan", np.arctan)
SinH = _unary("SinH", np.sinh)
CosH = _unary("CosH", np.cosh)
TanH = _unary("TanH", np.tanh)
BooleanNot = _unary("BooleanNot", lambda a: (np.asarray(a) == 0).astype(np.int32), keep_integer=True)


# ---- two-input mathematics, comparison and logic


def _binary(name, function, keep_integer=True):
    def tool(in_raster_or_constant1, in_raster_or_constant2):
        _refuse_environment(f"arcpy.sa.{name}")
        values = [as_raster(v, name) if isinstance(v, str) else v for v in (in_raster_or_constant1, in_raster_or_constant2)]
        return _combine(values, function, keep_integer=keep_integer, tool=name)
    tool.__name__ = name
    tool.__doc__ = f"arcpy.sa.{name}, cell by cell; NoData where either input is."
    return tool


def _zero_is_nodata(function):
    def safe(x, y):
        y = np.asarray(y)
        zero = y == 0
        return function(x, np.where(zero, 1, y)), zero
    return safe


Plus = _binary("Plus", np.add)
Minus = _binary("Minus", np.subtract)
Times = _binary("Times", np.multiply)
def Divide(in_raster_or_constant1, in_raster_or_constant2):
    """arcpy.sa.Divide: integer division when both inputs are integers, as the tool's page documents; NoData where the
    divisor is zero."""
    from ._raster import _divide
    _refuse_environment("arcpy.sa.Divide")
    values = [as_raster(v, "Divide") if isinstance(v, str) else v for v in (in_raster_or_constant1, in_raster_or_constant2)]
    return _divide(*values)
Mod = _binary("Mod", _zero_is_nodata(np.fmod))
Power = _binary("Power", lambda x, y: np.power(np.asarray(x, float), y), keep_integer=False)
ATan2 = _binary("ATan2", lambda y, x: np.arctan2(y, x), keep_integer=False)
GreaterThan = _binary("GreaterThan", lambda x, y: np.greater(x, y).astype(np.int32))
GreaterThanEqual = _binary("GreaterThanEqual", lambda x, y: np.greater_equal(x, y).astype(np.int32))
LessThan = _binary("LessThan", lambda x, y: np.less(x, y).astype(np.int32))
LessThanEqual = _binary("LessThanEqual", lambda x, y: np.less_equal(x, y).astype(np.int32))
EqualTo = _binary("EqualTo", lambda x, y: np.equal(x, y).astype(np.int32))
NotEqual = _binary("NotEqual", lambda x, y: np.not_equal(x, y).astype(np.int32))
BooleanAnd = _binary("BooleanAnd", lambda x, y: np.logical_and(np.asarray(x) != 0, np.asarray(y) != 0).astype(np.int32))
BooleanOr = _binary("BooleanOr", lambda x, y: np.logical_or(np.asarray(x) != 0, np.asarray(y) != 0).astype(np.int32))
BooleanXOr = _binary("BooleanXOr", lambda x, y: np.logical_xor(np.asarray(x) != 0, np.asarray(y) != 0).astype(np.int32))
Diff = _binary("Diff", lambda x, y: np.where(np.not_equal(x, y), x, 0))


# ---- conditions


_WHERE = re.compile(r"^\s*value\s*(<=|>=|<>|!=|=|==|<|>)\s*(-?\d+(?:\.\d+)?(?:e-?\d+)?)\s*$", re.IGNORECASE)


def _where(raster, where_clause, tool):
    """A where clause on the cell value, ``VALUE > 5``, as a 0/1 array. Clauses on other fields need an attribute
    table, which grapy's rasters do not carry, and are refused."""
    clauses = re.split(r"\s+(AND|OR)\s+", where_clause.strip(), flags=re.IGNORECASE)
    result, joiner = None, None
    for part in clauses:
        if part.upper() in ("AND", "OR"):
            joiner = part.upper()
            continue
        found = _WHERE.match(part)
        if not found:
            raise NotBuiltError(f"{tool}: the where clause '{where_clause}' is not built in grapy yet; "
                                "only comparisons of VALUE with a number, joined by AND or OR, are.")
        op, number = found.group(1), float(found.group(2))
        a = raster._array
        test = {"<": a < number, ">": a > number, "<=": a <= number, ">=": a >= number, "=": a == number,
                "==": a == number, "<>": a != number, "!=": a != number}[op]
        result = test if result is None else (result & test if joiner == "AND" else result | test)
    return result


def Con(in_conditional_raster, in_true_raster_or_constant, in_false_raster_or_constant=None, where_clause=None):
    """The true value where the condition holds (non-zero, or the where clause is true), the false value elsewhere, and
    NoData where there is no false value."""
    _refuse_environment("arcpy.sa.Con")
    condition = as_raster(in_conditional_raster, "Con")
    if where_clause:
        test = _where(condition, where_clause, "Con").astype(np.int32)
        condition = Raster._of(test, condition._mask.copy(), condition._grid)
    no_false = in_false_raster_or_constant is None
    values = [condition, in_true_raster_or_constant, 0 if no_false else in_false_raster_or_constant]
    values = [as_raster(v, "Con") if isinstance(v, str) else v for v in values]
    rasters = [v for v in values if isinstance(v, Raster)]
    from ._raster import _align
    arrays, masks, grid = _align(rasters, "Con")
    it = iter(zip(arrays, masks))
    resolved = []
    for v in values:
        if isinstance(v, Raster):
            resolved.append(next(it))
        else:
            resolved.append((np.asarray(v), np.zeros(arrays[0].shape, bool)))
    (c, cm), (t, tm), (f, fm) = resolved
    chosen = c != 0
    array = np.where(chosen, t, f)
    mask = cm | np.where(chosen, tm, fm)
    if no_false:
        mask = mask | ~chosen
    return Raster._of(array, mask, grid)


def SetNull(in_conditional_raster, in_false_raster_or_constant, where_clause=None):
    """NoData where the condition holds, the false value elsewhere."""
    _refuse_environment("arcpy.sa.SetNull")
    condition = as_raster(in_conditional_raster, "SetNull")
    test = _where(condition, where_clause, "SetNull") if where_clause else condition._array != 0
    flipped = Raster._of((~test).astype(np.int32), condition._mask.copy(), condition._grid)
    return Con(flipped, in_false_raster_or_constant)


def IsNull(in_raster):
    """1 where the input is NoData, 0 elsewhere; never NoData itself."""
    raster = as_raster(in_raster, "IsNull")
    return Raster._of(raster._mask.astype(np.int32), np.zeros(raster._mask.shape, bool), raster._grid)


def Test(in_raster, where_clause):
    """1 where the where clause is true, 0 where it is false, NoData where the input is."""
    raster = as_raster(in_raster, "Test")
    return Raster._of(_where(raster, where_clause, "Test").astype(np.int32), raster._mask.copy(), raster._grid)


def Pick(in_position_raster, in_rasters_or_constants):
    """The value of the input the position raster names, counting from 1; NoData where it names none."""
    position = as_raster(in_position_raster, "Pick")
    inputs = [as_raster(v, "Pick") if isinstance(v, str) else v for v in in_rasters_or_constants]
    rasters = [position, *[v for v in inputs if isinstance(v, Raster)]]
    from ._raster import _align
    arrays, masks, grid = _align(rasters, "Pick")
    p, pm = arrays[0], masks[0]
    it = iter(zip(arrays[1:], masks[1:]))
    layers = [next(it) if isinstance(v, Raster) else (np.full(p.shape, v), np.zeros(p.shape, bool)) for v in inputs]
    out = np.zeros(p.shape, dtype=np.result_type(*[a.dtype for a, _ in layers]))
    mask = pm.copy() | (p < 1) | (p > len(layers))
    for i, (a, m) in enumerate(layers, start=1):
        here = p == i
        out = np.where(here, a, out)
        mask |= here & m
    return Raster._of(out, mask, grid)


# ---- surface


def _neighbours(raster):
    """The nine 3×3 neighbours of every cell as arrays a…i (a top-left, e the centre), NoData and the edge taking the
    centre's value."""
    z = raster._array.astype(np.float64)
    z = np.where(raster._mask, np.nan, z)
    padded = np.pad(z, 1, constant_values=np.nan)
    rows, columns = z.shape
    view = {}
    for k, (dr, dc) in zip("abcdefghi", [(r, c) for r in range(3) for c in range(3)]):
        cell = padded[dr:dr + rows, dc:dc + columns]
        view[k] = np.where(np.isnan(cell), z, cell)
    return view


def _gradients(raster, z_factor):
    n = _neighbours(raster)
    w, h = raster._grid.width, raster._grid.height
    dx = ((n["c"] + 2 * n["f"] + n["i"]) - (n["a"] + 2 * n["d"] + n["g"])) / (8 * w) * z_factor
    dy = ((n["g"] + 2 * n["h"] + n["i"]) - (n["a"] + 2 * n["b"] + n["c"])) / (8 * h) * z_factor
    return dx, dy


def Slope(in_raster, output_measurement="DEGREE", z_factor=1, method="PLANAR", z_unit="METER", analysis_target_device=None):
    """Steepest downhill slope of each cell by Horn's method: degrees, or ``PERCENT_RISE``."""
    _refuse_environment("arcpy.sa.Slope")
    raster = as_raster(in_raster, "Slope")
    if str(method).upper() != "PLANAR":
        raise NotBuiltError(f"Slope: method '{method}' is not built in grapy yet; PLANAR is.")
    dx, dy = _gradients(raster, float(z_factor))
    rise = np.sqrt(dx * dx + dy * dy)
    kind = str(output_measurement).upper()
    if kind == "DEGREE":
        value = np.degrees(np.arctan(rise))
    elif kind == "PERCENT_RISE":
        value = rise * 100
    else:
        raise NotBuiltError(f"Slope: output_measurement '{output_measurement}' is not built in grapy yet.")
    return Raster._of(value.astype(np.float32), raster._mask.copy(), raster._grid)


def _aspect_degrees(dx, dy):
    """Esri's documented conversion: degrees clockwise from north, -1 where the cell is flat."""
    aspect = np.degrees(np.arctan2(dy, -dx))
    cell = np.where(aspect < 0, 90.0 - aspect, np.where(aspect > 90.0, 360.0 - aspect + 90.0, 90.0 - aspect))
    return np.where((dx == 0) & (dy == 0), -1.0, cell)


def Aspect(in_raster, method="PLANAR", z_unit="METER", analysis_target_device=None):
    """Downslope direction of each cell, in degrees clockwise from north; -1 where flat."""
    _refuse_environment("arcpy.sa.Aspect")
    raster = as_raster(in_raster, "Aspect")
    if str(method).upper() != "PLANAR":
        raise NotBuiltError(f"Aspect: method '{method}' is not built in grapy yet; PLANAR is.")
    dx, dy = _gradients(raster, 1.0)
    return Raster._of(_aspect_degrees(dx, dy).astype(np.float32), raster._mask.copy(), raster._grid)


def Hillshade(in_raster, azimuth=315, altitude=45, model_shadows="NO_SHADOWS", z_factor=1):
    """Illumination 0–255 from a light source at ``azimuth`` and ``altitude``, by Esri's published formula."""
    _refuse_environment("arcpy.sa.Hillshade")
    if str(model_shadows).upper() != "NO_SHADOWS":
        raise NotBuiltError("Hillshade: model_shadows 'SHADOWS' is not built in grapy yet.")
    raster = as_raster(in_raster, "Hillshade")
    dx, dy = _gradients(raster, float(z_factor))
    zenith = math.radians(90.0 - float(altitude))
    azimuth_math = 360.0 - float(azimuth) + 90.0
    if azimuth_math >= 360.0:
        azimuth_math -= 360.0
    azimuth_rad = math.radians(azimuth_math)
    slope = np.arctan(np.sqrt(dx * dx + dy * dy))
    aspect = np.arctan2(dy, -dx)
    aspect = np.where(aspect < 0, aspect + 2 * math.pi, aspect)
    aspect = np.where(dx == 0, np.where(dy > 0, math.pi / 2, np.where(dy < 0, 2 * math.pi - math.pi / 2, aspect)), aspect)
    shade = 255.0 * (math.cos(zenith) * np.cos(slope) + math.sin(zenith) * np.sin(slope) * np.cos(azimuth_rad - aspect))
    shade = np.clip(np.floor(shade + 0.5), 0, 255).astype(np.uint8)
    return Raster._of(shade, raster._mask.copy(), raster._grid)


# ---- neighbourhoods


class NbrRectangle:
    def __init__(self, width=3, height=3, units="CELL"):
        self.width, self.height, self.units = width, height, units

    def _footprint(self, grid):
        if str(self.units).upper() != "CELL":
            raise NotBuiltError("NbrRectangle: units 'MAP' are not built in grapy yet; CELL is.")
        if int(self.width) % 2 == 0 or int(self.height) % 2 == 0:
            raise NotBuiltError("NbrRectangle: an even width or height is not built in grapy yet.")
        return np.ones((int(self.height), int(self.width)), bool)


class NbrCircle:
    def __init__(self, radius=3, units="CELL"):
        self.radius, self.units = radius, units

    def _footprint(self, grid):
        if str(self.units).upper() != "CELL":
            raise NotBuiltError("NbrCircle: units 'MAP' are not built in grapy yet; CELL is.")
        r = float(self.radius)
        n = int(math.floor(r))
        y, x = np.mgrid[-n:n + 1, -n:n + 1]
        # INFERRED: a cell is in the circle when its centre is within the radius of the processing cell's centre.
        return (x * x + y * y) <= r * r


class NbrAnnulus:
    def __init__(self, innerRadius=1, outerRadius=3, units="CELL"):
        self.innerRadius, self.outerRadius, self.units = innerRadius, outerRadius, units

    def _footprint(self, grid):
        if str(self.units).upper() != "CELL":
            raise NotBuiltError("NbrAnnulus: units 'MAP' are not built in grapy yet; CELL is.")
        n = int(math.floor(float(self.outerRadius)))
        y, x = np.mgrid[-n:n + 1, -n:n + 1]
        d2 = x * x + y * y
        return (d2 <= float(self.outerRadius) ** 2) & (d2 > float(self.innerRadius) ** 2)


def _majority(values, minority=False):
    out = np.full(values.shape[:-1], np.nan)
    flat = values.reshape(-1, values.shape[-1])
    result = out.reshape(-1)
    for i, row in enumerate(flat):
        row = row[~np.isnan(row)]
        if row.size == 0:
            continue
        kinds, counts = np.unique(row, return_counts=True)
        best = counts.min() if minority else counts.max()
        winners = kinds[counts == best]
        # INFERRED: a tie answers NoData, which is what Esri documents for MAJORITY in Focal Statistics.
        result[i] = winners[0] if len(winners) == 1 else np.nan
    return result.reshape(values.shape[:-1])


_FOCAL = {
    "MEAN": np.nanmean, "SUM": np.nansum, "MINIMUM": np.nanmin, "MAXIMUM": np.nanmax,
    "RANGE": lambda v, axis: np.nanmax(v, axis=axis) - np.nanmin(v, axis=axis),
    "STD": np.nanstd, "MEDIAN": np.nanmedian,
    "VARIETY": lambda v, axis: np.apply_along_axis(lambda r: len(np.unique(r[~np.isnan(r)])), axis, v).astype(float),
    "MAJORITY": lambda v, axis: _majority(v), "MINORITY": lambda v, axis: _majority(v, minority=True),
}


def FocalStatistics(in_raster, neighborhood=None, statistics_type="MEAN", ignore_nodata="DATA", percentile_value=90):
    """A statistic of each cell's neighbourhood. With ``DATA`` NoData neighbours are left out; with ``NODATA`` any NoData
    neighbour makes the cell NoData."""
    _refuse_environment("arcpy.sa.FocalStatistics")
    from numpy.lib.stride_tricks import sliding_window_view
    raster = as_raster(in_raster, "FocalStatistics")
    neighborhood = neighborhood or NbrRectangle(3, 3, "CELL")
    if isinstance(neighborhood, str):
        neighborhood = _parse_neighborhood(neighborhood)
    footprint = neighborhood._footprint(raster._grid)
    kind = str(statistics_type).upper()
    if kind == "PERCENTILE":
        function = lambda v, axis: np.nanpercentile(v, float(percentile_value), axis=axis)  # noqa: E731
    elif kind in _FOCAL:
        function = _FOCAL[kind]
    else:
        raise NotBuiltError(f"FocalStatistics: statistics_type '{statistics_type}' is not built in grapy yet.")
    z = np.where(raster._mask, np.nan, raster._array.astype(np.float64))
    ph, pw = footprint.shape[0] // 2, footprint.shape[1] // 2
    padded = np.pad(z, ((ph, ph), (pw, pw)), constant_values=np.nan)
    windows = sliding_window_view(padded, footprint.shape)[..., footprint]
    with np.errstate(all="ignore"):
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", RuntimeWarning)
            value = function(windows, axis=-1)
            if kind == "SUM":
                value = np.where(np.all(np.isnan(windows), axis=-1), np.nan, value)
    mask = raster._mask | np.isnan(value)
    if str(ignore_nodata).upper() == "NODATA":
        inside = sliding_window_view(np.pad(raster._mask, ((ph, ph), (pw, pw)), constant_values=True), footprint.shape)[..., footprint]
        mask |= inside.any(axis=-1)
    integer = raster.isInteger and kind in ("SUM", "MINIMUM", "MAXIMUM", "RANGE", "MAJORITY", "MINORITY", "VARIETY")
    out = np.where(mask, 0, value)
    return Raster._of(out.astype(np.int64) if integer else out.astype(np.float64), mask, raster._grid)


def _parse_neighborhood(text):
    parts = text.split()
    kind = parts[0].upper()
    if kind == "RECTANGLE":
        return NbrRectangle(float(parts[1]), float(parts[2]), parts[3] if len(parts) > 3 else "CELL")
    if kind == "CIRCLE":
        return NbrCircle(float(parts[1]), parts[2] if len(parts) > 2 else "CELL")
    if kind == "ANNULUS":
        return NbrAnnulus(float(parts[1]), float(parts[2]), parts[3] if len(parts) > 3 else "CELL")
    raise NotBuiltError(f"FocalStatistics: the neighbourhood '{text}' is not built in grapy yet.")


def BlockStatistics(in_raster, neighborhood=None, statistics_type="MEAN", ignore_nodata="DATA"):
    """The statistic of each non-overlapping block, written to every cell of the block."""
    raster = as_raster(in_raster, "BlockStatistics")
    neighborhood = neighborhood or NbrRectangle(3, 3, "CELL")
    if not isinstance(neighborhood, NbrRectangle):
        raise NotBuiltError("BlockStatistics: only a rectangle neighbourhood is built in grapy yet.")
    if str(neighborhood.units).upper() != "CELL":
        raise NotBuiltError("BlockStatistics: units 'MAP' are not built in grapy yet; CELL is.")
    kind = str(statistics_type).upper()
    if kind not in _FOCAL:
        raise NotBuiltError(f"BlockStatistics: statistics_type '{statistics_type}' is not built in grapy yet.")
    bw, bh = int(neighborhood.width), int(neighborhood.height)
    z = np.where(raster._mask, np.nan, raster._array.astype(np.float64))
    out = np.full(z.shape, np.nan)
    for r in range(0, z.shape[0], bh):
        for c in range(0, z.shape[1], bw):
            block = z[r:r + bh, c:c + bw]
            if np.all(np.isnan(block)) or (str(ignore_nodata).upper() == "NODATA" and np.any(np.isnan(block))):
                continue
            out[r:r + bh, c:c + bw] = _FOCAL[kind](block.reshape(1, -1), axis=-1)[0]
    mask = np.isnan(out)
    return Raster._of(np.where(mask, 0, out), mask, raster._grid)


# ---- local, across rasters


_CELL = {"MEAN": np.nanmean, "SUM": np.nansum, "MINIMUM": np.nanmin, "MAXIMUM": np.nanmax,
         "RANGE": lambda v, axis: np.nanmax(v, axis=axis) - np.nanmin(v, axis=axis), "STD": np.nanstd,
         "MEDIAN": np.nanmedian, "MAJORITY": lambda v, axis: _majority(np.moveaxis(v, 0, -1)),
         "MINORITY": lambda v, axis: _majority(np.moveaxis(v, 0, -1), minority=True),
         "VARIETY": lambda v, axis: np.apply_along_axis(lambda r: len(np.unique(r[~np.isnan(r)])), axis, v).astype(float)}


def CellStatistics(in_rasters_or_constants, statistics_type="MEAN", ignore_nodata="DATA", process_as_multiband=None,
                   percentile_value=90, percentile_interpolation_type="AUTO_DETECT"):
    """A statistic of the inputs, cell by cell. ``DATA`` leaves NoData inputs out of each cell's statistic."""
    _refuse_environment("arcpy.sa.CellStatistics")
    inputs = [as_raster(v, "CellStatistics") if isinstance(v, str) else v for v in in_rasters_or_constants]
    rasters = [v for v in inputs if isinstance(v, Raster)]
    from ._raster import _align
    arrays, masks, grid = _align(rasters, "CellStatistics")
    it = iter(zip(arrays, masks))
    stack = []
    for v in inputs:
        if isinstance(v, Raster):
            a, m = next(it)
            stack.append(np.where(m, np.nan, a.astype(np.float64)))
        else:
            stack.append(np.full(arrays[0].shape, float(v)))
    stack = np.stack(stack)
    kind = str(statistics_type).upper()
    if kind not in _CELL:
        raise NotBuiltError(f"CellStatistics: statistics_type '{statistics_type}' is not built in grapy yet.")
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        value = _CELL[kind](stack, axis=0)
    mask = np.all(np.isnan(stack), axis=0) | np.isnan(value)
    if str(ignore_nodata).upper() == "NODATA":
        mask |= np.any(np.isnan(stack), axis=0)
    integer = all(r.isInteger for r in rasters) and kind in ("SUM", "MINIMUM", "MAXIMUM", "RANGE", "MAJORITY", "MINORITY", "VARIETY")
    out = np.where(mask, 0, value)
    return Raster._of(out.astype(np.int64) if integer else out, mask, grid)


def HighestPosition(in_rasters_or_constants):
    """Which input, counting from 1, holds the highest value of each cell."""
    return _position(in_rasters_or_constants, np.argmax, "HighestPosition")


def LowestPosition(in_rasters_or_constants):
    return _position(in_rasters_or_constants, np.argmin, "LowestPosition")


def _position(values, choose, tool):
    inputs = [as_raster(v, tool) if isinstance(v, str) else v for v in values]
    rasters = [v for v in inputs if isinstance(v, Raster)]
    from ._raster import _align
    arrays, masks, grid = _align(rasters, tool)
    it = iter(zip(arrays, masks))
    stack, mask = [], np.zeros(arrays[0].shape, bool)
    for v in inputs:
        if isinstance(v, Raster):
            a, m = next(it)
            stack.append(a.astype(np.float64))
            mask |= m
        else:
            stack.append(np.full(arrays[0].shape, float(v)))
    return Raster._of((choose(np.stack(stack), axis=0) + 1).astype(np.int32), mask, grid)


# ---- reclassification


class RemapValue:
    def __init__(self, remapTable=None):
        self.remapTable = remapTable or []


class RemapRange:
    def __init__(self, remapTable=None):
        self.remapTable = remapTable or []


def Reclassify(in_raster, reclass_field, remap, missing_values="DATA"):
    """New values by a RemapValue (``[[old, new]]``) or RemapRange (``[[from, to, new]]``) table. A value no row names
    keeps its value with ``DATA`` and becomes NoData with ``NODATA``. ``"NODATA"`` may stand for old or new values.

    INFERRED: in a RemapRange a range includes both ends, and a value on the boundary of two ranges takes the
    earlier row — the order the table is written in."""
    _refuse_environment("arcpy.sa.Reclassify")
    if str(reclass_field).upper() not in ("VALUE", "VALUES"):
        raise NotBuiltError(f"Reclassify: reclass_field '{reclass_field}' needs an attribute table, which is not built in grapy yet.")
    raster = as_raster(in_raster, "Reclassify")
    if isinstance(remap, str):
        remap = _parse_remap(remap)
    a = raster._array.astype(np.float64)
    out = np.full(a.shape, np.nan)
    done = np.zeros(a.shape, bool)
    set_nodata = np.zeros(a.shape, bool)
    for row in remap.remapTable:
        new = row[-1]
        if isinstance(remap, RemapRange) and str(row[0]).upper() == "NODATA":
            hit = raster._mask & ~done
        elif isinstance(remap, RemapRange):
            low, high = float(row[0]), float(row[1])
            hit = (a >= min(low, high)) & (a <= max(low, high)) & ~raster._mask & ~done
        else:
            old = row[0]
            hit = raster._mask & ~done if str(old).upper() == "NODATA" else (a == float(old)) & ~raster._mask & ~done
        if str(new).upper() == "NODATA":
            set_nodata |= hit
        else:
            out[hit] = float(new)
        done |= hit
    keep = str(missing_values).upper() == "DATA"
    out = np.where(~done & keep, a, out)
    mask = set_nodata | (~done & (raster._mask | (not keep)))
    integer = all(str(r[-1]).upper() == "NODATA" or float(r[-1]).is_integer() for r in remap.remapTable) and (raster.isInteger or not keep)
    out = np.where(mask, 0, out)
    return Raster._of(out.astype(np.int32) if integer else out, mask, raster._grid)


def _parse_remap(text):
    rows = [r.split() for r in text.split(";") if r.strip()]
    if rows and len(rows[0]) == 3:
        return RemapRange([[r[0], r[1], r[2]] for r in rows])
    return RemapValue([[r[0], r[1]] for r in rows])


def Slice(in_raster, number_zones, slice_type="EQUAL_INTERVAL", base_output_zone=1, nodata_to_value=None,
          class_interval_size=None):
    """Zones of equal width (``EQUAL_INTERVAL``) or equal count (``EQUAL_AREA``) over the input's range."""
    for name, value in (("nodata_to_value", nodata_to_value), ("class_interval_size", class_interval_size)):
        if value not in (None, "", "#"):
            raise NotBuiltError(f"Slice: {name} is not built in grapy yet.")
    raster = as_raster(in_raster, "Slice")
    values = raster._values().astype(np.float64)
    n = int(number_zones)
    kind = str(slice_type).upper()
    a = raster._array.astype(np.float64)
    if kind == "EQUAL_INTERVAL":
        low, high = values.min(), values.max()
        width = (high - low) / n
        zone = np.floor((a - low) / width) if width else np.zeros(a.shape)
        zone = np.clip(zone, 0, n - 1)
    elif kind == "EQUAL_AREA":
        ranks = np.argsort(np.argsort(a[~raster._mask], kind="stable"), kind="stable")
        zone = np.zeros(a.shape)
        zone[~raster._mask] = np.floor(ranks * n / ranks.size)
    else:
        raise NotBuiltError(f"Slice: slice_type '{slice_type}' is not built in grapy yet.")
    return Raster._of((zone + int(base_output_zone)).astype(np.int32), raster._mask.copy(), raster._grid)


# ---- zones


def _rasterize(features, field, grid, tool):
    """Polygon features burnt onto a grid by cell centre, each cell given the field's value — NoData where none."""
    from rasterio.features import rasterize
    from . import da
    shapes = []
    with da.SearchCursor(features, [field, "SHAPE@"] if field and field.upper() not in ("OID", "OBJECTID") else ["OID@", "SHAPE@"]) as rows:
        for value, shape in rows:
            if shape is not None and value is not None:
                shapes.append((shape._shape, value))
    values = [v for _, v in shapes]
    numeric = all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in values)
    codes = {}
    if not numeric:
        codes = {v: i + 1 for i, v in enumerate(sorted(set(values), key=str))}
        shapes = [(s, codes[v]) for s, v in shapes]
    burnt = rasterize(shapes, out_shape=(grid.rows, grid.columns), transform=grid.transform(), fill=0,
                      dtype="float64", all_touched=False) if shapes else np.zeros((grid.rows, grid.columns))
    covered = rasterize([(s, 1) for s, _ in shapes], out_shape=(grid.rows, grid.columns), transform=grid.transform(),
                        fill=0, dtype="uint8") if shapes else np.zeros((grid.rows, grid.columns), np.uint8)
    return burnt, covered == 0, {v: k for k, v in codes.items()}


def _zones(in_zone_data, zone_field, value, tool):
    """The zone of each cell of the value raster, a NoData mask and the names of coded text zones."""
    if isinstance(in_zone_data, Raster) or str(in_zone_data).lower().endswith((".tif", ".tiff")):
        zones = as_raster(in_zone_data, tool)
        if str(zone_field).upper() not in ("VALUE", "VALUES"):
            raise NotBuiltError(f"{tool}: zone_field '{zone_field}' needs an attribute table, which is not built in grapy yet.")
        from ._raster import _align
        (z, v), (zm, vm), grid = _align([zones, value], tool)
        return z.astype(np.float64), zm, {}, (v, vm, grid)
    burnt, empty, names = _rasterize(in_zone_data, zone_field, value._grid, tool)
    return burnt, empty, names, (value._array, value._mask, value._grid)


_ZONAL = {"MEAN": np.mean, "MAJORITY": None, "MAXIMUM": np.max, "MEDIAN": np.median, "MINIMUM": np.min, "MINORITY": None,
          "RANGE": lambda v: v.max() - v.min(), "STD": np.std, "SUM": np.sum, "VARIETY": lambda v: len(np.unique(v))}


def _zonal_value(kind, values):
    if kind in ("MAJORITY", "MINORITY"):
        return _majority(values.reshape(1, -1).astype(float), minority=kind == "MINORITY")[0]
    return _ZONAL[kind](values)


def ZonalStatistics(in_zone_data, zone_field, in_value_raster, statistics_type="MEAN", ignore_nodata="DATA",
                    process_as_multidimensional=None, percentile_value=90, percentile_interpolation_type="AUTO_DETECT",
                    circular_calculation=None, circular_wrap_value=None):
    """Each cell given the statistic of the value raster over its zone."""
    _refuse_environment("arcpy.sa.ZonalStatistics")
    value = as_raster(in_value_raster, "ZonalStatistics")
    kind = str(statistics_type).upper()
    if kind not in _ZONAL:
        raise NotBuiltError(f"ZonalStatistics: statistics_type '{statistics_type}' is not built in grapy yet.")
    z, zm, _, (v, vm, grid) = _zones(in_zone_data, zone_field, value, "ZonalStatistics")
    out = np.full(z.shape, np.nan)
    for zone in np.unique(z[~zm]):
        inside = (z == zone) & ~zm
        data = inside & ~vm
        if not data.any() or (str(ignore_nodata).upper() == "NODATA" and (inside & vm).any()):
            continue
        out[inside] = _zonal_value(kind, v[data])
    mask = np.isnan(out)
    integer = np.issubdtype(v.dtype, np.integer) and kind in ("MAXIMUM", "MINIMUM", "RANGE", "SUM", "MAJORITY", "MINORITY", "VARIETY")
    out = np.where(mask, 0, out)
    return Raster._of(out.astype(np.int64) if integer else out, mask, grid)


def ZonalStatisticsAsTable(in_zone_data, zone_field, in_value_raster, out_table, ignore_nodata="DATA",
                           statistics_type="ALL", *_, **__):
    """A table with a row per zone: COUNT, AREA and the statistics asked (``ALL``: MIN, MAX, RANGE, MEAN, STD, SUM)."""
    from . import _gpkg, da
    from .analysis import _out
    from ._result import Result
    value = as_raster(in_value_raster, "ZonalStatisticsAsTable")
    z, zm, names, (v, vm, grid) = _zones(in_zone_data, zone_field, value, "ZonalStatisticsAsTable")
    groups = {"ALL": ["MIN", "MAX", "RANGE", "MEAN", "STD", "SUM"], "MEAN_STD": ["MEAN", "STD"],
              "MIN_MAX": ["MIN", "MAX"], "MIN_MAX_MEAN": ["MIN", "MAX", "MEAN"]}
    kind = str(statistics_type).upper()
    single = {"MEAN": "MEAN", "MAXIMUM": "MAX", "MINIMUM": "MIN", "RANGE": "RANGE", "STD": "STD", "SUM": "SUM",
              "MEDIAN": "MEDIAN", "MAJORITY": "MAJORITY", "MINORITY": "MINORITY", "VARIETY": "VARIETY"}
    wanted = groups.get(kind) or ([single[kind]] if kind in single else None)
    if wanted is None:
        raise NotBuiltError(f"ZonalStatisticsAsTable: statistics_type '{statistics_type}' is not built in grapy yet.")
    function = {"MIN": "MINIMUM", "MAX": "MAXIMUM"}
    zone_name = zone_field if zone_field else "Value"
    text_zones = bool(names)
    columns = [_gpkg.Column(zone_name, "String" if text_zones else "Integer"), _gpkg.Column("COUNT", "Integer"),
               _gpkg.Column("AREA", "Double"), *[_gpkg.Column(w, "Double") for w in wanted]]
    out = _out(out_table)
    _gpkg.create(out, "Geometry", 0, columns)
    with da.InsertCursor(out, [*[c.name for c in columns], "SHAPE@"]) as written:
        for zone in np.unique(z[~zm]):
            inside = (z == zone) & ~zm
            data = inside & ~vm
            if not data.any() or (str(ignore_nodata).upper() == "NODATA" and (inside & vm).any()):
                continue
            label = names.get(zone, zone) if text_zones else int(zone)
            stats = [float(_zonal_value(function.get(w, w), v[data])) for w in wanted]
            written.insertRow((label, int(data.sum()), float(data.sum() * grid.width * grid.height), *stats, None))
    return Result(out)


# ---- distance


def EucDistance(in_source_data, maximum_distance=None, cell_size=None, out_direction_raster=None,
                distance_method="PLANAR", in_barrier_data=None, out_back_direction_raster=None):
    """The straight-line distance from each cell's centre to the nearest source cell's centre. A raster source's
    sources are its cells that are not NoData; features are burnt onto a grid of ``cell_size`` over their extent."""
    _refuse_environment("arcpy.sa.EucDistance")
    from scipy.ndimage import distance_transform_edt
    if str(distance_method).upper() != "PLANAR":
        raise NotBuiltError(f"EucDistance: distance_method '{distance_method}' is not built in grapy yet.")
    for name, value in (("out_direction_raster", out_direction_raster), ("in_barrier_data", in_barrier_data),
                        ("out_back_direction_raster", out_back_direction_raster)):
        if value:
            raise NotBuiltError(f"EucDistance: {name} is not built in grapy yet.")
    if isinstance(in_source_data, Raster) or str(in_source_data).lower().endswith((".tif", ".tiff")):
        source = as_raster(in_source_data, "EucDistance")
        grid, sources = source._grid, ~source._mask
    else:
        if cell_size is None:
            raise NotBuiltError("EucDistance: features without a cell_size need the default cell size rule, which is not built in grapy yet.")
        grid = _grid_over(in_source_data, float(cell_size))
        _, empty, _ = _rasterize_any(in_source_data, grid)
        sources = ~empty
    distance = distance_transform_edt(~sources, sampling=(grid.height, grid.width))
    mask = np.zeros(distance.shape, bool)
    if maximum_distance not in (None, "", "#"):
        mask = distance > float(maximum_distance)
    return Raster._of(np.where(mask, 0, distance).astype(np.float32), mask, grid)


def _grid_over(features, cell):
    """A grid of square cells over a feature class's extent, its top-left corner at the extent's (INFERRED)."""
    from .analysis import _schema
    from . import da
    import shapely
    srid = _schema(features)[2]
    with da.SearchCursor(features, ["SHAPE@"]) as rows:
        shapes = [r[0]._shape for r in rows if r[0] is not None]
    left, bottom, right, top = shapely.GeometryCollection(shapes).bounds
    columns = max(1, int(math.ceil((right - left) / cell)))
    rows_ = max(1, int(math.ceil((top - bottom) / cell)))
    return _Grid(left, top, cell, cell, rows_, columns, srid)


def _rasterize_any(features, grid, field=None):
    """Any shape type burnt by cell centre, lines and points marking every cell they touch."""
    from rasterio.features import rasterize
    from . import da
    pairs = []
    with da.SearchCursor(features, [field or "OID@", "SHAPE@"]) as rows:
        for value, shape in rows:
            if shape is not None:
                pairs.append((shape._shape, value))
    polygons = [(s, v) for s, v in pairs if s.geom_type.endswith("Polygon")]
    others = [(s, v) for s, v in pairs if not s.geom_type.endswith("Polygon")]
    shape = (grid.rows, grid.columns)
    burnt = np.zeros(shape)
    covered = np.zeros(shape, bool)
    for group, touched in ((polygons, False), (others, True)):
        if group:
            burnt = np.where(covered, burnt, rasterize([(s, float(v) if v is not None else 0) for s, v in group], out_shape=shape,
                                                       transform=grid.transform(), fill=0, dtype="float64", all_touched=touched))
            covered |= rasterize([(s, 1) for s, _ in group], out_shape=shape, transform=grid.transform(), fill=0,
                                 dtype="uint8", all_touched=touched).astype(bool)
    return burnt, ~covered, {}


# ---- extraction


def ExtractByMask(in_raster, in_mask_data, extraction_area="INSIDE", analysis_extent=None):
    """The input's cells inside (or outside) the mask; the rest NoData. A mask raster's NoData is outside."""
    _refuse_environment("arcpy.sa.ExtractByMask")
    raster = as_raster(in_raster, "ExtractByMask")
    if isinstance(in_mask_data, Raster) or str(in_mask_data).lower().endswith((".tif", ".tiff")):
        mask_raster = as_raster(in_mask_data, "ExtractByMask")
        from ._raster import _align
        (a, _), (am, mm), grid = _align([raster, mask_raster], "ExtractByMask")
        outside = mm
    else:
        a, am, grid = raster._array, raster._mask, raster._grid
        _, outside, _ = _rasterize_any(in_mask_data, grid)
    if str(extraction_area).upper() == "OUTSIDE":
        outside = ~outside
    return Raster._of(a.copy(), am | outside, grid)


def ExtractByAttributes(in_raster, where_clause):
    """The cells the where clause chooses; the rest NoData."""
    raster = as_raster(in_raster, "ExtractByAttributes")
    keep = _where(raster, where_clause, "ExtractByAttributes")
    return Raster._of(raster._array.copy(), raster._mask | ~keep, raster._grid)


def ExtractByRectangle(in_raster, extent, extraction_area="INSIDE"):
    raster = as_raster(in_raster, "ExtractByRectangle")
    if isinstance(extent, str):
        xmin, ymin, xmax, ymax = (float(v) for v in extent.split()[:4])
    else:
        xmin, ymin, xmax, ymax = extent.XMin, extent.YMin, extent.XMax, extent.YMax
    xs, ys = raster._grid.centres()
    inside = (ys[:, None] >= ymin) & (ys[:, None] <= ymax) & (xs[None, :] >= xmin) & (xs[None, :] <= xmax)
    if str(extraction_area).upper() == "OUTSIDE":
        inside = ~inside
    return Raster._of(raster._array.copy(), raster._mask | ~inside, raster._grid)


def _sample(raster, x, y, bilinear):
    g = raster._grid
    if not bilinear:
        cell = g.cell_of(x, y)
        if cell is None or raster._mask[cell]:
            return None
        return raster._array[cell].item()
    # Bilinear over the four nearest cell centres; NoData if any of them is NoData or off the grid (INFERRED).
    fx = (x - g.left) / g.width - 0.5
    fy = (g.top - y) / g.height - 0.5
    c0, r0 = int(math.floor(fx)), int(math.floor(fy))
    if c0 < 0 or r0 < 0 or c0 + 1 >= g.columns or r0 + 1 >= g.rows:
        return _sample(raster, x, y, False)
    block = raster._array[r0:r0 + 2, c0:c0 + 2].astype(float)
    if raster._mask[r0:r0 + 2, c0:c0 + 2].any():
        return None
    tx, ty = fx - c0, fy - r0
    top = block[0, 0] * (1 - tx) + block[0, 1] * tx
    bottom = block[1, 0] * (1 - tx) + block[1, 1] * tx
    return float(top * (1 - ty) + bottom * ty)


def ExtractValuesToPoints(in_point_features, in_raster, out_point_features, interpolate_values="NONE",
                          add_attributes="VALUE_ONLY"):
    """The points copied with ``RASTERVALU``, the cell value under each; -9999 where it is NoData, as documented."""
    from . import _gpkg, da
    from .analysis import _out, _schema
    from ._result import Result
    if str(add_attributes).upper() != "VALUE_ONLY":
        raise NotBuiltError("ExtractValuesToPoints: add_attributes 'ALL' needs an attribute table, which is not built in grapy yet.")
    raster = as_raster(in_raster, "ExtractValuesToPoints")
    columns, shape, srid, _ = _schema(in_point_features)
    out = _out(out_point_features)
    _gpkg.create(out, shape, srid, [*columns, _gpkg.Column("RASTERVALU", "Double")])
    names = [c.name for c in columns]
    bilinear = str(interpolate_values).upper() == "INTERPOLATE"
    with da.SearchCursor(in_point_features, [*names, "SHAPE@"]) as rows, da.InsertCursor(out, [*names, "RASTERVALU", "SHAPE@"]) as written:
        for row in rows:
            point = row[-1]._shape if row[-1] is not None else None
            value = _sample(raster, point.x, point.y, bilinear) if point is not None else None
            written.insertRow((*row[:-1], -9999.0 if value is None else float(value), row[-1]))
    return Result(out)


def ExtractMultiValuesToPoints(in_point_features, in_rasters, bilinear_interpolate_values="NONE"):
    """A field per raster added to the points, holding the cell value under each point; null where it is NoData."""
    from . import _gpkg, da
    from ._result import Result
    pairs = []
    for item in (in_rasters if isinstance(in_rasters, (list, tuple)) else [r.split() for r in str(in_rasters).split(";")]):
        if isinstance(item, (list, tuple)):
            source, field = item[0], item[1] if len(item) > 1 else None
        else:
            source, field = item, None
        raster = as_raster(source, "ExtractMultiValuesToPoints")
        if not field:
            import os
            field = os.path.splitext(os.path.basename(str(source)))[0]
        pairs.append((raster, field))
    from .analysis import _schema
    existing = {c.name.lower() for c in _schema(in_point_features)[0]}
    for _, field in pairs:
        if field.lower() not in existing:
            _gpkg.add_column(in_point_features, _gpkg.Column(field, "Double"))
    bilinear = str(bilinear_interpolate_values).upper() == "BILINEAR"
    with da.UpdateCursor(in_point_features, [*[f for _, f in pairs], "SHAPE@"]) as rows:
        for row in rows:
            point = row[-1]._shape if row[-1] is not None else None
            values = [(_sample(r, point.x, point.y, bilinear) if point is not None else None) for r, _ in pairs]
            rows.updateRow([*[None if v is None else float(v) for v in values], row[-1]])
    return Result(in_point_features)


# ---- creating rasters


def CreateConstantRaster(constant_value, data_type="INTEGER", cell_size=None, extent=None):
    """A raster of one value over an extent at a cell size."""
    from ._runtime import env
    if cell_size is None or extent is None:
        raise NotBuiltError("CreateConstantRaster: cell_size and extent from the environment are not built in grapy yet; pass both.")
    if isinstance(extent, str):
        xmin, ymin, xmax, ymax = (float(v) for v in extent.split()[:4])
        srid = 0
    else:
        xmin, ymin, xmax, ymax = extent.XMin, extent.YMin, extent.XMax, extent.YMax
        srid = getattr(getattr(extent, "spatialReference", None), "factoryCode", 0) or 0
    srid = srid or getattr(getattr(env, "outputCoordinateSystem", None), "factoryCode", 0) or 0
    cell = float(cell_size)
    columns, rows = max(1, int(math.ceil((xmax - xmin) / cell))), max(1, int(math.ceil((ymax - ymin) / cell)))
    dtype = np.int32 if str(data_type).upper() == "INTEGER" else np.float32
    grid = _Grid(xmin, ymax, cell, cell, rows, columns, srid)
    return Raster._of(np.full((rows, columns), constant_value, dtype=dtype), np.zeros((rows, columns), bool), grid)


def RasterCalculator(rasters, input_names, expression, extent_type="FirstOf", cellsize_type="FirstOf"):
    """A map algebra expression over named rasters, ``RasterCalculator([r1, r2], ["a", "b"], "a + b * 2")``."""
    if str(extent_type) not in ("FirstOf", "IntersectionOf") or str(cellsize_type) != "FirstOf":
        raise NotBuiltError("RasterCalculator: only the default extent and cell size types are built in grapy yet.")
    scope = {name: as_raster(r, "RasterCalculator") for r, name in zip(rasters, input_names)}
    scope.update({k: v for k, v in globals().items() if not k.startswith("_")})
    return eval(expression, {"__builtins__": {}}, scope)  # noqa: S307 — the tool's own expression, in its own fenced run


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.sa.{name} is not built in grapy yet.")

    return missing
