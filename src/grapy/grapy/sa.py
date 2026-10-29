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


# =====================================================================================================================
# Added for ADR-224's second sa batch. Each tool refuses by name the parameter values it does not build.

def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"arcpy.sa.{tool}: {parameter} '{value}' is not built in grapy{': ' + why if why else ''}.")


def _given(value):
    return value not in (None, "", "#")


def _is_raster(value):
    return isinstance(value, Raster) or str(value).lower().endswith((".tif", ".tiff"))


# ---- more mathematics


def _domain(function, valid, safe):
    def guarded(a):
        a = np.asarray(a, dtype=float)
        ok = valid(a)
        return np.where(ok, function(np.where(ok, a, safe)), 0.0), ~ok
    return guarded


ACosH = _unary("ACosH", _domain(np.arccosh, lambda a: a >= 1, 1.0))
ASinH = _unary("ASinH", np.arcsinh)
ATanH = _unary("ATanH", _domain(np.arctanh, lambda a: (a > -1) & (a < 1), 0.0))
ACosH.__doc__ = "arcpy.sa.ACosH: floating point; NoData outside its domain 1 ≤ x, as documented."
ATanH.__doc__ = "arcpy.sa.ATanH: floating point; NoData outside its domain −1 < x < 1, as documented."


def _int32(a):
    """A value as the 32-bit integer the bitwise tools work on: floating point truncated, as documented. INFERRED: a
    value outside the 32-bit range is NoData."""
    a = np.trunc(np.asarray(a, dtype=float))
    outside = (a < np.iinfo(np.int32).min) | (a > np.iinfo(np.int32).max) | ~np.isfinite(a)
    return np.where(outside, 0, a).astype(np.int64), outside


def _bitwise(operation):
    def tool(x, y):
        (a, ma), (b, mb) = _int32(x), _int32(y)
        return operation(a, b), ma | mb
    return tool


def _wrap32(a):
    """The low 32 bits as a signed integer: bits shifted past the left are dropped and the sign bit is part of the
    value, as the documentation says."""
    return ((np.asarray(a, dtype=np.int64) & 0xFFFFFFFF) ^ 0x80000000).astype(np.int64) - 0x80000000


def _left(a, s):
    negative = s < 0
    shifted = np.where(s >= 32, 0, np.left_shift(a, np.clip(s, 0, 31)))
    return _wrap32(shifted).astype(np.int32), negative


def _right(a, s):
    negative = s < 0
    return np.right_shift(a, np.clip(s, 0, 63)).astype(np.int32), negative


BitwiseAnd = _binary("BitwiseAnd", _bitwise(lambda a, b: (a & b).astype(np.int32)))
BitwiseOr = _binary("BitwiseOr", _bitwise(lambda a, b: (a | b).astype(np.int32)))
BitwiseXOr = _binary("BitwiseXOr", _bitwise(lambda a, b: (a ^ b).astype(np.int32)))
BitwiseLeftShift = _binary("BitwiseLeftShift", lambda x, y: (lambda p, q: (_left(p[0], q[0])[0], p[1] | q[1] | (q[0] < 0)))(
    _int32(x), _int32(y)))
BitwiseRightShift = _binary("BitwiseRightShift", lambda x, y: (lambda p, q: (_right(p[0], q[0])[0], p[1] | q[1] | (q[0] < 0)))(
    _int32(x), _int32(y)))
BitwiseNot = _unary("BitwiseNot", lambda x: (lambda p: ((~p[0]).astype(np.int32), p[1]))(_int32(x)), keep_integer=True)
for _tool in (BitwiseAnd, BitwiseOr, BitwiseXOr, BitwiseLeftShift, BitwiseRightShift, BitwiseNot):
    _tool.__doc__ = (f"arcpy.sa.{_tool.__name__} on 32-bit integers, floating point truncated first, as documented. "
                     "INFERRED: a value outside the 32-bit range, or a negative shift, is NoData; a right shift is "
                     "arithmetic (it keeps the sign bit).")
Over = _binary("Over", lambda x, y: np.where(np.asarray(x) != 0, x, y))
Over.__doc__ = ("arcpy.sa.Over: the first input where it is not 0, the second where it is. INFERRED: NoData in either "
                "input is NoData, arcpy's general rule; the page is silent.")


# ---- local: many rasters cell by cell


def _many(values, tool):
    """Rasters and constants on one grid: their arrays, the union of their NoData, and the grid."""
    from ._raster import _align
    values = [as_raster(v, tool) if isinstance(v, str) else v for v in values]
    rasters = [v for v in values if isinstance(v, Raster)]
    if not rasters:
        raise TypeError(f"{tool}: at least one input must be a raster.")
    arrays, masks, grid = _align(rasters, tool)
    it = iter(arrays)
    out = [next(it) if isinstance(v, Raster) else np.full((grid.rows, grid.columns), v) for v in values]
    return out, np.logical_or.reduce(masks), grid


def _frequency(name, compare):
    def tool(in_value_raster, in_rasters, process_as_multiband=None):
        _refuse_environment(f"arcpy.sa.{name}")
        rasters = in_rasters if isinstance(in_rasters, (list, tuple)) else [in_rasters]
        arrays, mask, grid = _many([in_value_raster, *rasters], name)
        count = sum(compare(a, arrays[0]).astype(np.int32) for a in arrays[1:])
        return Raster._of(np.where(mask, 0, count).astype(np.int32), mask, grid)
    tool.__name__ = name
    return tool


EqualToFrequency = _frequency("EqualToFrequency", np.equal)
GreaterThanFrequency = _frequency("GreaterThanFrequency", np.greater)
LessThanFrequency = _frequency("LessThanFrequency", np.less)
EqualToFrequency.__doc__ = "How many of the rasters equal the value raster, cell by cell; NoData where any input is."
GreaterThanFrequency.__doc__ = ("How many of the rasters are greater than the value raster, cell by cell; NoData where any "
                                "input is. INFERRED: strictly greater; the page says *greater than*.")
LessThanFrequency.__doc__ = ("How many of the rasters are less than the value raster, cell by cell; NoData where any input "
                             "is. INFERRED: strictly less.")


def _stack(position, in_rasters, tool):
    rasters = in_rasters if isinstance(in_rasters, (list, tuple)) else [in_rasters]
    arrays, mask, grid = _many([position, *rasters], tool)
    stack = np.stack(arrays[1:])
    floating = any(np.issubdtype(a.dtype, np.floating) for a in arrays[1:])
    return arrays[0], stack, mask, grid, floating


def Popularity(in_popularity_raster_or_constant, in_rasters, process_as_multiband=None):
    """The value at the nth level of popularity among the rasters, cell by cell: the most frequent value for 1, the
    next for 2. NoData where no single value holds that level (a tie), where n is 0 or more than the number of rasters,
    or where any input is NoData; where every input has one value, that value. Integer unless an input is floating
    point."""
    _refuse_environment("arcpy.sa.Popularity")
    positions, stack, mask, grid, floating = _stack(in_popularity_raster_or_constant, in_rasters, "Popularity")
    n_rasters = stack.shape[0]
    out = np.zeros(mask.shape, np.float64)
    empty = mask.copy()
    for r in range(mask.shape[0]):
        for c in range(mask.shape[1]):
            if mask[r, c]:
                continue
            n = int(positions[r, c])
            values, counts = np.unique(stack[:, r, c], return_counts=True)
            if n < 1 or n > n_rasters:
                empty[r, c] = True
            elif len(values) == 1:
                out[r, c] = values[0]
            else:
                order = sorted(zip(counts, values), key=lambda p: -p[0])
                if n > len(order) or sum(1 for k, _ in order if k == order[n - 1][0]) > 1:
                    empty[r, c] = True
                else:
                    out[r, c] = order[n - 1][1]
    return Raster._of(out if floating else out.astype(np.int32), empty, grid)


def Rank(in_rank_raster_or_constant, in_rasters, process_as_multiband=None):
    """The value at a rank among the rasters sorted ascending, cell by cell (values 17, 8, 11 at rank 3 give 17).
    NoData where the rank is beyond the number of rasters or any input is NoData.

    INFERRED: a rank below 1 is NoData too, and a fractional rank is truncated."""
    _refuse_environment("arcpy.sa.Rank")
    ranks, stack, mask, grid, floating = _stack(in_rank_raster_or_constant, in_rasters, "Rank")
    ordered = np.sort(stack, axis=0)
    k = np.trunc(np.asarray(ranks, dtype=float)).astype(np.int64)
    bad = (k < 1) | (k > stack.shape[0])
    picked = np.take_along_axis(ordered, np.clip(k - 1, 0, stack.shape[0] - 1)[None], axis=0)[0]
    out_mask = mask | bad
    return Raster._of(np.where(out_mask, 0, picked).astype(np.float64 if floating else np.int32), out_mask, grid)


class WSTable:
    """arcpy.sa.WSTable: ``[[raster, field, weight], …]`` for WeightedSum."""

    def __init__(self, weightedSumTable=None):
        self.weightedSumTable = weightedSumTable or []


def WeightedSum(in_rasters):
    """Each raster times its weight, summed, as a floating-point raster.

    The field must be ``VALUE``: another field needs an attribute table, which grapy's rasters do not carry.
    INFERRED: NoData in any input is NoData, arcpy's general rule; the page is silent."""
    _refuse_environment("arcpy.sa.WeightedSum")
    rows = in_rasters.weightedSumTable if isinstance(in_rasters, WSTable) else in_rasters
    if not rows:
        raise ValueError("WeightedSum needs at least one row.")
    for row in rows:
        if str(row[1]).upper() not in ("VALUE", "VALUES"):
            _refuse("WeightedSum", "field", row[1], "it needs a raster attribute table")
    arrays, mask, grid = _many([row[0] for row in rows], "WeightedSum")
    total = sum(a.astype(np.float64) * float(row[2]) for a, row in zip(arrays, rows))
    return Raster._of(np.where(mask, 0, total), mask, grid)


# ---- extraction


def _extract(raster, inside, extraction_area, tool):
    area = str(extraction_area or "INSIDE").upper()
    if area not in ("INSIDE", "OUTSIDE"):
        _refuse(tool, "extraction_area", extraction_area)
    keep = inside if area == "INSIDE" else ~inside
    return Raster._of(raster._array.copy(), raster._mask | ~keep, raster._grid)


def ExtractByCircle(in_raster, center_point, radius, extraction_area="INSIDE"):
    """The cells whose centres are within the circle (``INSIDE``) or beyond it (``OUTSIDE``); the rest NoData.

    INFERRED: a centre exactly on the circle is inside; the default is ``INSIDE``, which the page does not mark."""
    _refuse_environment("arcpy.sa.ExtractByCircle")
    raster = as_raster(in_raster, "ExtractByCircle")
    xs, ys = raster._grid.centres()
    inside = (xs[None, :] - center_point.X) ** 2 + (ys[:, None] - center_point.Y) ** 2 <= float(radius) ** 2
    return _extract(raster, inside, extraction_area, "ExtractByCircle")


def _points_of(value):
    return [(p.X, p.Y) if hasattr(p, "X") else (float(p[0]), float(p[1])) for p in value]


def ExtractByPolygon(in_raster, polygon, extraction_area="INSIDE"):
    """The cells whose centres are inside the polygon — a list of Points, or a list of such lists for several parts —
    or outside it; the rest NoData.

    INFERRED: a centre on the boundary is inside; the default is ``INSIDE``."""
    import shapely
    from shapely.geometry import Polygon as Ring
    _refuse_environment("arcpy.sa.ExtractByPolygon")
    raster = as_raster(in_raster, "ExtractByPolygon")
    parts = polygon if polygon and isinstance(polygon[0], (list, tuple)) and not hasattr(polygon[0], "X") \
        and len(polygon[0]) and not isinstance(polygon[0][0], (int, float)) else [polygon]
    shape = shapely.union_all([Ring(_points_of(part)) for part in parts])
    xs, ys = raster._grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    inside = shapely.intersects_xy(shape, gx, gy)
    return _extract(raster, inside, extraction_area, "ExtractByPolygon")


def ExtractByPoints(in_raster, points, extraction_area="INSIDE"):
    """The cells the points fall in (``INSIDE``), or every other cell (``OUTSIDE``); the rest NoData.

    INFERRED: the default is ``INSIDE``; a point exactly on a cell's edge falls in the cell to its east or south, as
    grapy's ``cell_of`` places it."""
    _refuse_environment("arcpy.sa.ExtractByPoints")
    raster = as_raster(in_raster, "ExtractByPoints")
    inside = np.zeros(raster._mask.shape, bool)
    for x, y in _points_of(points):
        cell = raster._grid.cell_of(x, y)
        if cell is not None:
            inside[cell] = True
    return _extract(raster, inside, extraction_area, "ExtractByPoints")


# ---- generalization


def Aggregate(in_raster, cell_factor, aggregation_type="SUM", extent_handling="EXPAND", ignore_nodata="DATA"):
    """A coarser raster whose cells are ``cell_factor`` input cells on a side, each the SUM, MAXIMUM, MEAN, MEDIAN or
    MINIMUM of the cells it covers. Where the rows or columns are not a multiple of the factor, ``EXPAND`` extends the
    top and right edges with NoData and ``TRUNCATE`` drops the cells left over there — so the blocks are counted from
    the bottom-left corner, as the documentation's *top and right* implies. ``DATA`` ignores NoData; ``NODATA`` makes a
    block with any NoData NoData. SUM, MAXIMUM and MINIMUM keep the input's type; MEAN and MEDIAN are floating point.

    INFERRED: a block with no data at all is NoData under ``DATA`` too."""
    _refuse_environment("arcpy.sa.Aggregate")
    raster = as_raster(in_raster, "Aggregate")
    f = int(cell_factor)
    if f < 2:
        raise ValueError("Aggregate's cell_factor must be an integer greater than 1.")
    kind = str(aggregation_type or "SUM").upper()
    functions = {"SUM": np.nansum, "MAXIMUM": np.nanmax, "MEAN": np.nanmean, "MEDIAN": np.nanmedian, "MINIMUM": np.nanmin}
    if kind not in functions:
        _refuse("Aggregate", "aggregation_type", aggregation_type)
    expand = str(extent_handling or "EXPAND").upper() == "EXPAND"
    strict = str(ignore_nodata or "DATA").upper() == "NODATA"
    g = raster._grid
    rows_out = -(-g.rows // f) if expand else g.rows // f
    cols_out = -(-g.columns // f) if expand else g.columns // f
    if rows_out == 0 or cols_out == 0:
        raise ValueError("Aggregate: the raster is smaller than one output cell.")
    values = np.where(raster._mask, np.nan, raster._array.astype(np.float64))
    # Rows are counted from the bottom: pad or cut at the top, and at the right.
    top_rows = rows_out * f - g.rows
    if top_rows >= 0:
        values = np.vstack([np.full((top_rows, g.columns), np.nan), values])
    else:
        values = values[-top_rows:]
    right = cols_out * f - g.columns
    values = np.hstack([values, np.full((values.shape[0], right), np.nan)]) if right >= 0 else values[:, :cols_out * f]
    blocks = values.reshape(rows_out, f, cols_out, f).transpose(0, 2, 1, 3).reshape(rows_out, cols_out, f * f)
    empty = np.isnan(blocks).all(axis=-1)
    if strict:
        empty |= np.isnan(blocks).any(axis=-1)
    with np.errstate(all="ignore"):
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", RuntimeWarning)
            out = functions[kind](blocks, axis=-1)
    out = np.where(empty, 0, out)
    if kind in ("SUM", "MAXIMUM", "MINIMUM") and raster.isInteger:
        out = out.astype(raster._array.dtype if raster._array.dtype != np.bool_ else np.int32)
    grid = _Grid(g.left, g.bottom + rows_out * f * g.height, g.width * f, g.height * f, rows_out, cols_out, g.srid)
    return Raster._of(out, empty, grid)


def RegionGroup(in_raster, number_neighbors="FOUR", zone_connectivity="WITHIN", add_link="ADD_LINK", excluded_value=None):
    """Each connected region a unique value, numbered from 1 in the order the scan — left to right, top to bottom —
    first meets it. ``WITHIN`` connects only cells of one value; ``CROSS`` connects any cells that have data. Cells of
    ``excluded_value`` are 0 and take part in no region; NoData stays NoData.

    ``ADD_LINK`` — the default — is refused: it writes the original value into a LINK field of the raster attribute
    table, which grapy's rasters do not carry. Pass ``NO_LINK``."""
    from scipy.ndimage import label
    _refuse_environment("arcpy.sa.RegionGroup")
    if str(add_link or "ADD_LINK").upper() != "NO_LINK":
        _refuse("RegionGroup", "add_link", add_link, "the LINK field belongs to a raster attribute table, which grapy's "
                "rasters do not carry; pass NO_LINK")
    raster = as_raster(in_raster, "RegionGroup")
    if not raster.isInteger:
        raise ValueError("RegionGroup takes an integer raster.")
    eight = str(number_neighbors or "FOUR").upper() == "EIGHT"
    structure = np.ones((3, 3), int) if eight else np.array([[0, 1, 0], [1, 1, 1], [0, 1, 0]])
    a, mask = raster._array, raster._mask
    excluded = (a == int(excluded_value)) & ~mask if _given(excluded_value) else np.zeros(a.shape, bool)
    active = ~mask & ~excluded
    pieces = np.zeros(a.shape, np.int64)
    if str(zone_connectivity or "WITHIN").upper() == "CROSS":
        pieces, _ = label(active, structure)
    else:
        offset = 0
        for value in np.unique(a[active]):
            labelled, count = label(active & (a == value), structure)
            pieces = np.where(labelled > 0, labelled + offset, pieces)
            offset += count
    # Renumber in scan order.
    order, out = {}, np.zeros(a.shape, np.int32)
    for flat in np.flatnonzero(pieces.ravel()):
        key = pieces.ravel()[flat]
        if key not in order:
            order[key] = len(order) + 1
    for key, number in order.items():
        out[pieces == key] = number
    return Raster._of(out, mask.copy(), raster._grid)


# ---- density


def _area_unit(unit, srid, tool, lines=False):
    """What a density in data units is multiplied by for ``area_unit_scale_factor``: square map units leave it, and
    for data in metres the square units convert area and, for lines, length too — kilometres per square kilometre."""
    from ._reference import SpatialReference
    square = {"SQUARE_KILOMETERS": 1e6, "SQUARE_METERS": 1.0, "SQUARE_CENTIMETERS": 1e-4, "SQUARE_MILLIMETERS": 1e-6,
              "SQUARE_MILES": 2589988.110336, "SQUARE_YARDS": 0.83612736, "SQUARE_FEET": 0.09290304,
              "SQUARE_INCHES": 0.00064516, "HECTARES": 1e4, "ACRES": 4046.8564224}
    metres = srid > 0 and SpatialReference(srid).type == "Projected" and abs(SpatialReference(srid).metersPerUnit - 1) < 1e-12
    name = str(unit).upper() if _given(unit) else ("SQUARE_KILOMETERS" if metres else "SQUARE_MAP_UNITS")
    if name == "SQUARE_MAP_UNITS":
        return 1.0
    if not metres:
        _refuse(tool, "area_unit_scale_factor", name, "a unit conversion is built only for data in metres")
    if name not in square:
        _refuse(tool, "area_unit_scale_factor", name)
    if lines:
        if name in ("HECTARES", "ACRES"):
            _refuse(tool, "area_unit_scale_factor", name, "the documentation does not say which length unit goes with it")
        return square[name] / math.sqrt(square[name])
    return square[name]


def _density_grid(features, cell_size, tool):
    from .analysis import _schema
    from . import da
    import shapely
    srid = _schema(features)[2]
    with da.SearchCursor(features, ["SHAPE@"]) as rows:
        shapes = [r[0]._shape for r in rows if r[0] is not None]
    x0, y0, x1, y1 = shapely.GeometryCollection(shapes).bounds
    cell = _cell_size(cell_size, tool)
    if cell is None:
        shorter = min(x1 - x0, y1 - y0)
        if shorter <= 0:
            _refuse(tool, "cell_size", "(empty)", "the features' extent has no width or height to divide by 250")
        cell = shorter / 250.0
    return _grid_over(features, cell), srid, (x0, y0, x1, y1)


def _population(features, population_field, tool):
    from . import da
    field = None if str(population_field or "NONE").upper() == "NONE" else population_field
    with da.SearchCursor(features, [field or "OID@", "SHAPE@"]) as rows:
        return [(r[1]._shape, 1.0 if field is None else (0.0 if r[0] is None else float(r[0]))) for r in rows if r[1] is not None]


def PointDensity(in_point_features, population_field, cell_size=None, neighborhood=None, area_unit_scale_factor=None):
    """The population of the points within a neighbourhood of each cell centre, divided by the neighbourhood's area;
    NoData where none falls within. The neighbourhood is an ``NbrCircle`` or ``NbrRectangle`` in cells or map units, by
    default a circle whose radius is the shorter side of the output extent divided by 30.

    INFERRED: the area divided by is the shape's own (πr², or width × height), not a count of cells; a point exactly
    on the edge is within; the output grid's top-left corner is the points' extent's."""
    _refuse_environment("arcpy.sa.PointDensity")
    grid, srid, (x0, y0, x1, y1) = _density_grid(in_point_features, cell_size, "PointDensity")
    points = _population(in_point_features, population_field, "PointDensity")
    xs = np.array([p.x for p, _ in points])
    ys = np.array([p.y for p, _ in points])
    weights = np.array([w for _, w in points])
    if neighborhood is None:
        neighborhood = NbrCircle(min(grid.right - grid.left, grid.top - grid.bottom) / 30.0, "MAP")
    in_cells = str(getattr(neighborhood, "units", "CELL")).upper() == "CELL"
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx, cy)
    dx, dy = gx[..., None] - xs, gy[..., None] - ys
    if isinstance(neighborhood, NbrCircle):
        r = float(neighborhood.radius) * (grid.width if in_cells else 1.0)
        within = dx * dx + dy * dy <= r * r
        area = math.pi * r * r
    elif isinstance(neighborhood, NbrRectangle):
        w = float(neighborhood.width) * (grid.width if in_cells else 1.0)
        h = float(neighborhood.height) * (grid.height if in_cells else 1.0)
        within = (np.abs(dx) <= w / 2) & (np.abs(dy) <= h / 2)
        area = w * h
    else:
        _refuse("PointDensity", "neighborhood", type(neighborhood).__name__, "only NbrCircle and NbrRectangle are built")
    total = (within * weights).sum(axis=-1)
    empty = ~within.any(axis=-1)
    density = total / area * _area_unit(area_unit_scale_factor, srid, "PointDensity")
    return Raster._of(np.where(empty, 0, density), empty, grid)


def _length_in_circle(shape, x, y, r):
    """The exact length of a line inside a circle, segment by segment: where |p0 + t(p1 − p0) − c| = r."""
    total = 0.0
    for part in getattr(shape, "geoms", [shape]):
        points = np.asarray(part.coords, dtype=float)[:, :2]
        for p0, p1 in zip(points[:-1], points[1:]):
            d = p1 - p0
            f = p0 - (x, y)
            a = d @ d
            if a == 0:
                continue
            b = 2 * (f @ d)
            c = f @ f - r * r
            disc = b * b - 4 * a * c
            if disc <= 0:
                continue
            root = math.sqrt(disc)
            t0, t1 = max((-b - root) / (2 * a), 0.0), min((-b + root) / (2 * a), 1.0)
            if t1 > t0:
                total += (t1 - t0) * math.sqrt(a)
    return total


def LineDensity(in_polyline_features, population_field, cell_size=None, search_radius=None, area_unit_scale_factor=None):
    """For each cell centre, the length of each line inside a circle of ``search_radius`` times its population, summed
    and divided by the circle's area, as *How Line Density works* documents; NoData where no line falls within. The
    default radius is the shorter side of the output extent divided by 30. For data in metres the default unit is
    kilometres per square kilometre.

    INFERRED: the output grid's top-left corner is the lines' extent's."""
    import shapely
    _refuse_environment("arcpy.sa.LineDensity")
    grid, srid, _ = _density_grid(in_polyline_features, cell_size, "LineDensity")
    lines = _population(in_polyline_features, population_field, "LineDensity")
    r = float(search_radius) if _given(search_radius) else min(grid.right - grid.left, grid.top - grid.bottom) / 30.0
    cx, cy = grid.centres()
    total = np.zeros((grid.rows, grid.columns))
    empty = np.ones((grid.rows, grid.columns), bool)
    from shapely.strtree import STRtree
    tree = STRtree([s for s, _ in lines])
    for i, y in enumerate(cy):
        for j, x in enumerate(cx):
            for k in tree.query(shapely.box(x - r, y - r, x + r, y + r)):
                shape, weight = lines[int(k)]
                length = _length_in_circle(shape, x, y, r)
                if length > 0:
                    total[i, j] += length * weight
                    empty[i, j] = False
    density = total / (math.pi * r * r) * _area_unit(area_unit_scale_factor, srid, "LineDensity", lines=True)
    return Raster._of(np.where(empty, 0, density), empty, grid)


# ---- distance


def _sources(in_source_data, grid_or_none, cell_size, tool, field=None):
    """Source cells and their values, on the source raster's grid or a grid over the features."""
    if _is_raster(in_source_data):
        source = as_raster(in_source_data, tool)
        return source._grid, ~source._mask, source._array, source
    if grid_or_none is None:
        if not _given(cell_size):
            _refuse(tool, "cell_size", "(empty)", "features need a cell size")
        grid_or_none = _grid_over(in_source_data, float(cell_size))
    burnt, empty, _ = _rasterize_any(in_source_data, grid_or_none, field)
    return grid_or_none, ~empty, burnt, None


def _nearest(sources, grid):
    from scipy.ndimage import distance_transform_edt
    distance, (ri, ci) = distance_transform_edt(~sources, sampling=(grid.height, grid.width), return_indices=True)
    return distance, ri, ci


def EucDirection(in_source_data, maximum_distance=None, cell_size=None, out_distance_raster=None, distance_method="PLANAR",
                 in_barrier_data=None, out_back_direction_raster=None):
    """The direction, in degrees clockwise from north (90 east … 360 north), in which each cell's centre lies from the
    nearest source cell's centre; 0 on the source cells; NoData beyond ``maximum_distance``.

    INFERRED: of sources at equal distance, the one SciPy's exact Euclidean transform reports — the documentation does
    not say."""
    _refuse_environment("arcpy.sa.EucDirection")
    if str(distance_method or "PLANAR").upper() != "PLANAR":
        _refuse("EucDirection", "distance_method", distance_method)
    for name, value in (("in_barrier_data", in_barrier_data), ("out_back_direction_raster", out_back_direction_raster)):
        if _given(value):
            _refuse("EucDirection", name, value)
    grid, sources, _, _ = _sources(in_source_data, None, cell_size, "EucDirection")
    distance, ri, ci = _nearest(sources, grid)
    rows, columns = np.indices(sources.shape)
    dx = (columns - ci) * grid.width
    dy = (ri - rows) * grid.height
    bearing = np.degrees(np.arctan2(dx, dy)) % 360.0
    bearing = np.where(bearing == 0, 360.0, bearing)
    bearing = np.where(sources, 0.0, bearing)
    mask = distance > float(maximum_distance) if _given(maximum_distance) else np.zeros(sources.shape, bool)
    if _given(out_distance_raster):
        Raster._of(np.where(mask, 0, distance).astype(np.float32), mask.copy(), grid).save(out_distance_raster)
    return Raster._of(np.where(mask, 0, bearing).astype(np.float32), mask, grid)


def EucAllocation(in_source_data, maximum_distance=None, in_value_raster=None, cell_size=None, source_field=None,
                  out_distance_raster=None, out_direction_raster=None, distance_method="PLANAR", in_barrier_data=None,
                  out_back_direction_raster=None):
    """Each cell given the value of the source cell nearest its centre — a raster source's own integer value, a
    feature's ``source_field``, or the value raster's value at the source — and NoData beyond ``maximum_distance``.

    INFERRED: of sources at equal distance, the one SciPy's exact Euclidean transform reports."""
    _refuse_environment("arcpy.sa.EucAllocation")
    if str(distance_method or "PLANAR").upper() != "PLANAR":
        _refuse("EucAllocation", "distance_method", distance_method)
    for name, value in (("in_barrier_data", in_barrier_data), ("out_back_direction_raster", out_back_direction_raster)):
        if _given(value):
            _refuse("EucAllocation", name, value)
    if not _is_raster(in_source_data) and not _given(source_field):
        _refuse("EucAllocation", "source_field", "(empty)", "the default field of features is not built; name an integer field")
    grid, sources, values, raster = _sources(in_source_data, None, cell_size, "EucAllocation",
                                              None if _is_raster(in_source_data) else source_field)
    if _given(in_value_raster):
        value_raster = as_raster(in_value_raster, "EucAllocation")
        if not value_raster._grid.same(grid):
            _refuse("EucAllocation", "in_value_raster", in_value_raster, "a value raster on another grid needs resampling")
        values = value_raster._array
    elif raster is not None and not raster.isInteger:
        raise ValueError("EucAllocation: a floating-point source needs an integer value raster.")
    distance, ri, ci = _nearest(sources, grid)
    mask = distance > float(maximum_distance) if _given(maximum_distance) else np.zeros(sources.shape, bool)
    allocated = np.asarray(values)[ri, ci].astype(np.int32)
    if _given(out_distance_raster):
        Raster._of(np.where(mask, 0, distance).astype(np.float32), mask.copy(), grid).save(out_distance_raster)
    if _given(out_direction_raster):
        EucDirection(in_source_data, maximum_distance, cell_size).save(out_direction_raster)
    return Raster._of(np.where(mask, 0, allocated), mask, grid)


_BACKLINK = {(0, 1): 1, (1, 1): 2, (1, 0): 3, (1, -1): 4, (0, -1): 5, (-1, -1): 6, (-1, 0): 7, (-1, 1): 8}


def _cost(in_source_data, in_cost_raster, tool, extra):
    """Least accumulated cost from the sources over the cost raster, as *How the cost distance tools work* documents:
    a step between neighbours costs the mean of their two costs times the cell size, and 1.414214 times that on a
    diagonal; the cheapest cell on the active list is settled next. Answers the cost, the source each cell is reached
    from, and the neighbour it is reached through."""
    import heapq
    extra = dict(extra)
    field = extra.pop("__field__", None)
    for name, value in extra.items():
        if _given(value):
            _refuse(tool, name, value, "source characteristics are not built")
    cost = as_raster(in_cost_raster, tool)
    g = cost._grid
    if not math.isclose(g.width, g.height):
        _refuse(tool, "in_cost_raster", in_cost_raster, "the diagonal step is documented for square cells")
    if _is_raster(in_source_data):
        from ._raster import _align
        source = as_raster(in_source_data, tool)
        if not source._grid.same(g):
            _refuse(tool, "in_source_data", in_source_data, "a source raster on another grid needs resampling")
        sources, values = ~source._mask, source._array
    else:
        burnt, empty, _ = _rasterize_any(in_source_data, g, field)
        sources, values = ~empty, burnt
    c = cost._array.astype(np.float64)
    barrier = cost._mask
    rows, columns = c.shape
    total = np.full(c.shape, np.inf)
    origin = np.full(c.shape, -1, np.int64)
    back = np.zeros(c.shape, np.int32)
    heap = []
    for r, k in zip(*np.nonzero(sources & ~barrier)):
        total[r, k] = 0.0
        origin[r, k] = r * columns + k
        heapq.heappush(heap, (0.0, r * columns + k, r, k))
    done = np.zeros(c.shape, bool)
    while heap:
        value, _, r, k = heapq.heappop(heap)
        if done[r, k]:
            continue
        done[r, k] = True
        for (dr, dk), _code in _BACKLINK.items():
            nr, nk = r + dr, k + dk
            if not (0 <= nr < rows and 0 <= nk < columns) or barrier[nr, nk] or done[nr, nk]:
                continue
            step = (c[r, k] + c[nr, nk]) / 2 * g.width * (1.414214 if dr and dk else 1.0)
            if value + step < total[nr, nk]:
                total[nr, nk] = value + step
                origin[nr, nk] = origin[r, k]
                back[nr, nk] = _BACKLINK[(-dr, -dk)]
                heapq.heappush(heap, (value + step, nr * columns + nk, nr, nk))
    unreached = ~np.isfinite(total) | barrier
    return total, origin, back, unreached, values, g


_SOURCE_PARAMETERS = ("source_cost_multiplier", "source_start_cost", "source_resistance_rate", "source_capacity",
                      "source_direction")


def _source_extra(**kwargs):
    extra = {k: v for k, v in kwargs.items() if k in _SOURCE_PARAMETERS}
    if str(extra.get("source_direction") or "").upper() == "FROM_SOURCE":
        extra["source_direction"] = None
    return extra


def CostDistance(in_source_data, in_cost_raster, maximum_distance=None, out_backlink_raster=None,
                 source_cost_multiplier=None, source_start_cost=None, source_resistance_rate=None, source_capacity=None,
                 source_direction=None):
    """The least accumulated cost of travel from each cell to the nearest source over the cost raster: 0 on the
    sources, NoData on the cost raster's NoData (which travel cannot cross) and beyond ``maximum_distance``.

    INFERRED: of two equally cheap routes, the one settled first wins — the documentation does not say; a source on a
    NoData cost cell is not a source."""
    _refuse_environment("arcpy.sa.CostDistance")
    total, _, back, unreached, _, g = _cost(in_source_data, in_cost_raster, "CostDistance", _source_extra(
        source_cost_multiplier=source_cost_multiplier, source_start_cost=source_start_cost,
        source_resistance_rate=source_resistance_rate, source_capacity=source_capacity, source_direction=source_direction))
    mask = unreached | (total > float(maximum_distance) if _given(maximum_distance) else unreached)
    if _given(out_backlink_raster):
        Raster._of(np.where(mask, 0, back), mask.copy(), g).save(out_backlink_raster)
    return Raster._of(np.where(mask, 0, total).astype(np.float32), mask, g)


def CostBackLink(in_source_data, in_cost_raster, maximum_distance=None, out_distance_raster=None,
                 source_cost_multiplier=None, source_start_cost=None, source_resistance_rate=None, source_capacity=None,
                 source_direction=None):
    """The neighbour each cell's least-cost path back to its source passes through first: 1 to the right, 2 lower
    right, and on clockwise to 8 upper right; 0 on the sources. Fitted as CostDistance is."""
    _refuse_environment("arcpy.sa.CostBackLink")
    total, _, back, unreached, _, g = _cost(in_source_data, in_cost_raster, "CostBackLink", _source_extra(
        source_cost_multiplier=source_cost_multiplier, source_start_cost=source_start_cost,
        source_resistance_rate=source_resistance_rate, source_capacity=source_capacity, source_direction=source_direction))
    mask = unreached | (total > float(maximum_distance) if _given(maximum_distance) else unreached)
    if _given(out_distance_raster):
        Raster._of(np.where(mask, 0, total).astype(np.float32), mask.copy(), g).save(out_distance_raster)
    return Raster._of(np.where(mask, 0, back), mask, g)


def CostAllocation(in_source_data, in_cost_raster, maximum_distance=None, in_value_raster=None, source_field=None,
                   out_distance_raster=None, out_backlink_raster=None, source_cost_multiplier=None,
                   source_start_cost=None, source_resistance_rate=None, source_capacity=None, source_direction=None):
    """Each cell given the value of the source it is reached most cheaply from, as CostDistance finds it: the source's
    integer value, a feature's ``source_field``, or the value raster's value at the source."""
    _refuse_environment("arcpy.sa.CostAllocation")
    extra = _source_extra(source_cost_multiplier=source_cost_multiplier, source_start_cost=source_start_cost,
                          source_resistance_rate=source_resistance_rate, source_capacity=source_capacity,
                          source_direction=source_direction)
    if not _is_raster(in_source_data):
        if not _given(source_field):
            _refuse("CostAllocation", "source_field", "(empty)", "the default field of features is not built; name an integer field")
        extra["__field__"] = source_field
    total, origin, back, unreached, values, g = _cost(in_source_data, in_cost_raster, "CostAllocation", extra)
    if _given(in_value_raster):
        value_raster = as_raster(in_value_raster, "CostAllocation")
        if not value_raster._grid.same(g):
            _refuse("CostAllocation", "in_value_raster", in_value_raster, "a value raster on another grid needs resampling")
        values = value_raster._array
    mask = unreached | (total > float(maximum_distance) if _given(maximum_distance) else unreached)
    flat = np.asarray(values).ravel()
    allocated = np.where(origin >= 0, flat[np.clip(origin, 0, None)], 0).astype(np.int32)
    if _given(out_distance_raster):
        Raster._of(np.where(mask, 0, total).astype(np.float32), mask.copy(), g).save(out_distance_raster)
    if _given(out_backlink_raster):
        Raster._of(np.where(mask, 0, back), mask.copy(), g).save(out_backlink_raster)
    return Raster._of(np.where(mask, 0, allocated), mask, g)


# ---- hydrology (D8)

_D8 = {1: (0, 1), 2: (1, 1), 4: (1, 0), 8: (1, -1), 16: (0, -1), 32: (-1, -1), 64: (-1, 0), 128: (-1, 1)}


def _d8(in_flow_direction_raster, tool):
    raster = as_raster(in_flow_direction_raster, tool)
    if not raster.isInteger:
        raise ValueError(f"{tool} takes a D8 flow direction raster, which is integer.")
    return raster


def _downstream(raster):
    """Each cell's downstream cell as a flat index: -1 where the flow leaves the raster or reaches NoData, -2 where the
    direction is undefined (not one of the eight codes) or the cell is NoData."""
    a, mask = raster._array, raster._mask
    rows, columns = a.shape
    down = np.full(a.size, -2, np.int64)
    for code, (dr, dk) in _D8.items():
        r, k = np.nonzero((a == code) & ~mask)
        nr, nk = r + dr, k + dk
        inside = (nr >= 0) & (nr < rows) & (nk >= 0) & (nk < columns)
        target = np.where(inside, nr * columns + np.clip(nk, 0, columns - 1), -1)
        target = np.where(inside & ~mask[np.clip(nr, 0, rows - 1), np.clip(nk, 0, columns - 1)], target, -1)
        down[r * columns + k] = target
    return down


def _upstream_order(down, active):
    """The cells in an order where every cell comes after all the cells that flow into it; a loop is an error, as the
    documentation warns a direction raster not made by FlowDirection may hold."""
    inflow = np.zeros(down.size, np.int64)
    for d in down[active & (down >= 0)]:
        inflow[d] += 1
    queue = [int(i) for i in np.flatnonzero(active & (inflow == 0))]
    order, head = [], 0
    while head < len(queue):
        i = queue[head]
        head += 1
        order.append(i)
        d = down[i]
        if d >= 0 and active[d]:
            inflow[d] -= 1
            if inflow[d] == 0:
                queue.append(int(d))
    if len(order) != int(active.sum()):
        raise ValueError("The flow direction raster holds a loop; it cannot be accumulated.")
    return order


def FlowDirection(in_surface_raster, force_flow="NORMAL", out_drop_raster=None, flow_direction_type="D8", method="PLANAR"):
    """The D8 direction of steepest descent from each cell to one of its eight neighbours, coded 1 east, 2 south-east,
    4 south, 8 south-west, 16 west, 32 north-west, 64 north, 128 north-east. The drop is the change in z over the
    distance between cell centres. A cell lower than all its neighbours is treated as noise, filled to its lowest
    neighbour and pointed at it, as *How Flow Direction works* documents. An edge cell flows outward under ``FORCE``,
    and under ``NORMAL`` when its steepest drop inside the raster is not positive.

    Refused rather than guessed: a cell whose steepest drop is shared by several neighbours, or is zero (a flat) — the
    documentation resolves those with a lookup table (Greenlee 1987) it does not publish; a surface with NoData; MFD,
    DINF and the geodesic method; and ``out_drop_raster``.

    INFERRED: outward from an edge is straight across it (64 on the top row, 4 bottom, 16 left, 1 right) and
    diagonal at a corner (32 top left, 128 top right, 8 bottom left, 2 bottom right); a diagonal step is
    √(width² + height²) long."""
    _refuse_environment("arcpy.sa.FlowDirection")
    if str(flow_direction_type or "D8").upper() != "D8":
        _refuse("FlowDirection", "flow_direction_type", flow_direction_type)
    if str(method or "PLANAR").upper() != "PLANAR":
        _refuse("FlowDirection", "method", method)
    if _given(out_drop_raster):
        _refuse("FlowDirection", "out_drop_raster", out_drop_raster, "its value across flats and at outward edges is not specified")
    raster = as_raster(in_surface_raster, "FlowDirection")
    if raster._mask.any():
        _refuse("FlowDirection", "in_surface_raster", in_surface_raster, "how NoData neighbours are treated is not documented")
    force = str(force_flow or "NORMAL").upper() == "FORCE"
    z = raster._array.astype(np.float64)
    rows, columns = z.shape
    g = raster._grid
    out = np.zeros(z.shape, np.int32)
    for r in range(rows):
        for k in range(columns):
            edge = r in (0, rows - 1) or k in (0, columns - 1)
            outward = 0
            if edge:
                vertical = 64 if r == 0 else (4 if r == rows - 1 else 0)
                horizontal = 16 if k == 0 else (1 if k == columns - 1 else 0)
                outward = {(64, 16): 32, (64, 1): 128, (4, 16): 8, (4, 1): 2}.get((vertical, horizontal), vertical or horizontal)
            if edge and force:
                out[r, k] = outward
                continue
            drops = []
            for code, (dr, dk) in _D8.items():
                nr, nk = r + dr, k + dk
                if 0 <= nr < rows and 0 <= nk < columns:
                    distance = math.hypot(dr * g.height, dk * g.width)
                    drops.append(((z[r, k] - z[nr, nk]) / distance, code, z[nr, nk]))
            best = max(d for d, _, _ in drops)
            if edge and best <= 0:
                out[r, k] = outward
                continue
            if best > 0:
                winners = [code for d, code, _ in drops if d == best]
            elif best < 0:
                lowest = min(v for _, _, v in drops)
                winners = [code for _, code, v in drops if v == lowest]
            else:
                winners = []
            if len(winners) != 1:
                raise NotBuiltError(f"arcpy.sa.FlowDirection: the cell at row {r}, column {k} has no single steepest "
                                    "descent; the documentation resolves it with an unpublished lookup table (Greenlee "
                                    "1987), so grapy does not guess. Fill the surface or break the tie.")
            out[r, k] = winners[0]
    return Raster._of(out, np.zeros(z.shape, bool), g)


def Fill(in_surface_raster, z_limit=None):
    """Sinks filled to the height of their pour points: each cell raised to the lowest level at which water from it
    could leave the raster across its edge, moving between the eight neighbours. Integer stays integer.

    Refused: ``z_limit`` (the documentation does not say how it applies to nested sinks) and a surface with NoData
    (its handling is not documented).

    INFERRED: the edge of the raster is where water leaves; *filled to its pour point* is read as the lowest such
    level, the standard depression-filling result."""
    import heapq
    _refuse_environment("arcpy.sa.Fill")
    if _given(z_limit):
        _refuse("Fill", "z_limit", z_limit, "how it applies to nested sinks is not documented")
    raster = as_raster(in_surface_raster, "Fill")
    if raster._mask.any():
        _refuse("Fill", "in_surface_raster", in_surface_raster, "how NoData is treated is not documented")
    z = raster._array.astype(np.float64)
    rows, columns = z.shape
    filled = z.copy()
    done = np.zeros(z.shape, bool)
    heap = []
    for r in range(rows):
        for k in range(columns):
            if r in (0, rows - 1) or k in (0, columns - 1):
                heapq.heappush(heap, (z[r, k], r, k))
                done[r, k] = True
    while heap:
        level, r, k = heapq.heappop(heap)
        for dr, dk in _D8.values():
            nr, nk = r + dr, k + dk
            if 0 <= nr < rows and 0 <= nk < columns and not done[nr, nk]:
                done[nr, nk] = True
                filled[nr, nk] = max(z[nr, nk], level)
                heapq.heappush(heap, (filled[nr, nk], nr, nk))
    return Raster._of(filled.astype(raster._array.dtype), np.zeros(z.shape, bool), raster._grid)


def FlowAccumulation(in_flow_direction_raster, in_weight_raster=None, data_type="FLOAT", flow_direction_type="D8"):
    """For each cell, the number of cells — or the sum of their weights — that flow into it, the cell itself not
    counted. A cell whose direction is not one of the eight codes receives flow but passes none on.

    INFERRED: a NoData direction is NoData in the output and passes nothing on; a cell whose weight is NoData passes on
    nothing of its own."""
    _refuse_environment("arcpy.sa.FlowAccumulation")
    if str(flow_direction_type or "D8").upper() != "D8":
        _refuse("FlowAccumulation", "flow_direction_type", flow_direction_type)
    kinds = {"FLOAT": np.float32, "INTEGER": np.int32, "DOUBLE": np.float64}
    if str(data_type or "FLOAT").upper() not in kinds:
        _refuse("FlowAccumulation", "data_type", data_type)
    raster = _d8(in_flow_direction_raster, "FlowAccumulation")
    down = _downstream(raster)
    active = ~raster._mask.ravel()
    weights = np.ones(down.size)
    if _given(in_weight_raster):
        weight = as_raster(in_weight_raster, "FlowAccumulation")
        if not weight._grid.same(raster._grid):
            _refuse("FlowAccumulation", "in_weight_raster", in_weight_raster, "a weight raster on another grid needs resampling")
        weights = np.where(weight._mask, 0.0, weight._array.astype(np.float64)).ravel()
    total = np.zeros(down.size)
    for i in _upstream_order(down, active):
        d = down[i]
        if d >= 0 and active[d]:
            total[d] += total[i] + weights[i]
    kind = kinds[str(data_type or "FLOAT").upper()]
    return Raster._of(total.reshape(raster._array.shape).astype(kind), raster._mask.copy(), raster._grid)


def _scan_labels(groups, mask):
    """Groups numbered 1, 2 … in the order a left-to-right, top-to-bottom scan first meets them."""
    numbers, out = {}, np.zeros(groups.shape, np.int32)
    for flat in np.flatnonzero(~mask.ravel()):
        key = groups.ravel()[flat]
        numbers.setdefault(key, len(numbers) + 1)
        out.ravel()[flat] = numbers[key]
    return out


def Sink(in_flow_direction_raster, analysis_target_device=None):
    """Each sink — a cell, or a set of connected cells, whose flow direction is not one of the eight codes — given a
    unique value from 1; every other cell NoData.

    INFERRED: cells are connected through their eight neighbours; sinks are numbered in scan order, left to right, top
    to bottom — the page says only that they are numbered from 1."""
    from scipy.ndimage import label
    _refuse_environment("arcpy.sa.Sink")
    raster = _d8(in_flow_direction_raster, "Sink")
    undefined = ~raster._mask & ~np.isin(raster._array, list(_D8))
    groups, _ = label(undefined, np.ones((3, 3), int))
    return Raster._of(_scan_labels(groups, ~undefined), ~undefined, raster._grid)


def _pour_cells(in_pour_point_data, pour_point_field, grid, tool):
    """Pour points as {flat cell: value}: a raster's cells that are not NoData, or the cell each point falls in, with
    the field's value truncated to an integer (the first numeric field by default, else the ObjectID)."""
    if _is_raster(in_pour_point_data):
        pour = as_raster(in_pour_point_data, tool)
        if not pour._grid.same(grid):
            _refuse(tool, "in_pour_point_data", in_pour_point_data, "a pour point raster on another grid needs resampling")
        return {int(i): int(pour._array.ravel()[i]) for i in np.flatnonzero(~pour._mask.ravel())}
    from . import da
    from .analysis import _schema
    columns = _schema(in_pour_point_data)[0]
    field = pour_point_field if _given(pour_point_field) else next(
        (c.name for c in columns if c.type in ("Integer", "SmallInteger", "Double", "Single")), "OID@")
    cells = {}
    with da.SearchCursor(in_pour_point_data, [field, "SHAPE@"]) as rows:
        for value, shape in rows:
            if shape is None or value is None:
                continue
            cell = grid.cell_of(shape._shape.x, shape._shape.y)
            if cell is not None:
                cells[cell[0] * grid.columns + cell[1]] = int(value)
    return cells


def Watershed(in_flow_direction_raster, in_pour_point_data, pour_point_field=None):
    """The area draining to each pour point, given the pour point's value; every other cell NoData.

    INFERRED: a cell belongs to the first pour point its flow path reaches, so a pour point upstream of another takes
    its own area out of the other's; a point feature marks the cell it falls in, and of two in one cell the later
    counts."""
    _refuse_environment("arcpy.sa.Watershed")
    raster = _d8(in_flow_direction_raster, "Watershed")
    down = _downstream(raster)
    pours = _pour_cells(in_pour_point_data, pour_point_field, raster._grid, "Watershed")
    out = np.full(down.size, -1, np.int64)
    known = np.zeros(down.size, bool)
    for start in range(down.size):
        path, i = [], start
        while i >= 0 and not known[i]:
            if i in pours:
                out[i], known[i] = pours[i], True
                break
            path.append(i)
            if len(path) > down.size:
                raise ValueError("The flow direction raster holds a loop.")
            i = down[i]
        value = out[i] if i >= 0 and known[i] else -1
        for p in path:
            out[p], known[p] = value, True
    mask = (out < 0).reshape(raster._array.shape) | raster._mask
    return Raster._of(np.where(mask, 0, out.reshape(mask.shape)).astype(np.int32), mask, raster._grid)


def Basin(in_flow_direction_raster, analysis_target_device=None):
    """Every cell's drainage basin: the cells whose flow ends at the same place — a cell that flows off the raster,
    into NoData, or nowhere (a sink) — share a value. Every cell with a direction belongs to a basin.

    INFERRED: basins are numbered from 1 in the scan order of their first cell; the page does not say how."""
    _refuse_environment("arcpy.sa.Basin")
    raster = _d8(in_flow_direction_raster, "Basin")
    down = _downstream(raster)
    end = np.full(down.size, -1, np.int64)
    for start in range(down.size):
        if raster._mask.ravel()[start]:
            continue
        path, i = [], start
        while end[i] < 0:
            path.append(i)
            if down[i] < 0 or len(path) > down.size:
                end[i] = i
                break
            i = down[i]
        for p in path:
            end[p] = end[i]
    return Raster._of(_scan_labels(end.reshape(raster._array.shape), raster._mask), raster._mask.copy(), raster._grid)


def _stream(in_stream_raster, in_flow_direction_raster, tool):
    stream = as_raster(in_stream_raster, tool)
    direction = _d8(in_flow_direction_raster, tool)
    if not stream._grid.same(direction._grid):
        _refuse(tool, "in_stream_raster", in_stream_raster, "a stream raster on another grid needs resampling")
    down = _downstream(direction)
    cells = ~stream._mask.ravel() & ~direction._mask.ravel()
    inflows = {}
    for i in np.flatnonzero(cells):
        d = down[i]
        if d >= 0 and cells[d]:
            inflows.setdefault(int(d), []).append(int(i))
    return stream, down, cells, inflows


def StreamOrder(in_stream_raster, in_flow_direction_raster, order_method="STRAHLER"):
    """The order of each stream cell. ``STRAHLER``: a stream with no tributaries is 1, and the order rises by one only
    where two streams of the same, highest order meet. ``SHREVE``: a stream with no tributaries is 1, and magnitudes add
    where streams meet. Cells off the stream network are NoData.

    INFERRED: a stream cell is one that is not NoData in the stream raster; streams meet where two or more stream cells
    flow into one."""
    _refuse_environment("arcpy.sa.StreamOrder")
    method = str(order_method or "STRAHLER").upper()
    if method not in ("STRAHLER", "SHREVE"):
        _refuse("StreamOrder", "order_method", order_method)
    stream, down, cells, inflows = _stream(in_stream_raster, in_flow_direction_raster, "StreamOrder")
    order = np.zeros(down.size, np.int64)
    for i in _upstream_order(np.where(cells[np.clip(down, 0, None)] & (down >= 0), down, -1), cells):
        above = [order[u] for u in inflows.get(i, [])]
        if not above:
            order[i] = 1
        elif method == "SHREVE":
            order[i] = sum(above)
        else:
            top = max(above)
            order[i] = top + 1 if above.count(top) > 1 else top
    mask = ~cells.reshape(stream._array.shape)
    return Raster._of(np.where(mask, 0, order.reshape(mask.shape)).astype(np.int32), mask, stream._grid)


def StreamLink(in_stream_raster, in_flow_direction_raster):
    """A unique value for each link — the stretch of stream between a source or junction and the next junction or
    outlet. A link begins at a stream cell with no stream flowing into it, or with two or more.

    INFERRED: links are numbered from 1 in the scan order of their first cell; the page does not say how."""
    _refuse_environment("arcpy.sa.StreamLink")
    stream, down, cells, inflows = _stream(in_stream_raster, in_flow_direction_raster, "StreamLink")
    start = np.full(down.size, -1, np.int64)
    for i in _upstream_order(np.where(cells[np.clip(down, 0, None)] & (down >= 0), down, -1), cells):
        above = inflows.get(i, [])
        start[i] = start[above[0]] if len(above) == 1 else i
    mask = ~cells.reshape(stream._array.shape)
    return Raster._of(_number_by_first(start, mask), mask, stream._grid)


def _number_by_first(start, mask):
    """Links numbered in the scan order of their first cell."""
    firsts = sorted({int(s) for s in start.ravel()[~mask.ravel()]})
    number = {s: n + 1 for n, s in enumerate(firsts)}
    out = np.zeros(mask.shape, np.int32)
    for flat in np.flatnonzero(~mask.ravel()):
        out.ravel()[flat] = number[int(start[flat])]
    return out


def SnapPourPoint(in_pour_point_data, in_accumulation_raster, snap_distance, pour_point_field=None):
    """Each pour point moved to the cell of highest accumulated flow within ``snap_distance``, keeping its value; every
    other cell NoData. Of pour points that snap to one cell, the last is kept.

    INFERRED: the search covers the cells whose centres are within the distance of the pour point's cell centre;
    among equal accumulations the first in scan order wins; a point feature marks the cell it falls in."""
    _refuse_environment("arcpy.sa.SnapPourPoint")
    accumulation = as_raster(in_accumulation_raster, "SnapPourPoint")
    g = accumulation._grid
    pours = _pour_cells(in_pour_point_data, pour_point_field, g, "SnapPourPoint")
    xs, ys = g.centres()
    gx, gy = np.meshgrid(xs, ys)
    a = np.where(accumulation._mask, -np.inf, accumulation._array.astype(np.float64))
    out = np.zeros(a.shape, np.int32)
    taken = np.zeros(a.shape, bool)
    radius = float(snap_distance)
    for flat, value in pours.items():
        r, k = divmod(flat, g.columns)
        near = (gx - xs[k]) ** 2 + (gy - ys[r]) ** 2 <= radius * radius
        candidates = np.where(near, a, -np.inf)
        best = int(np.argmax(candidates))
        out.ravel()[best] = value
        taken.ravel()[best] = True
    return Raster._of(out, ~taken, g)


def FlowLength(in_flow_direction_raster, direction_measurement="DOWNSTREAM", in_weight_raster=None):
    """The distance along the flow path: ``DOWNSTREAM`` from each cell to where its flow ends (an outlet on the edge or
    a sink); ``UPSTREAM`` the longest path from each cell up to the top of its drainage. Floating point.

    Refused: ``in_weight_raster``, since the documentation does not say which cell's weight a step takes.

    INFERRED: a step is the distance between cell centres — the cell width or height, or the diagonal; a path ends at
    the centre of its last cell, so a cell flowing off the raster is 0 downstream; the default is ``DOWNSTREAM``, which
    the page does not mark."""
    _refuse_environment("arcpy.sa.FlowLength")
    if _given(in_weight_raster):
        _refuse("FlowLength", "in_weight_raster", in_weight_raster, "which cell's weight a step takes is not documented")
    kind = str(direction_measurement or "DOWNSTREAM").upper()
    if kind not in ("DOWNSTREAM", "UPSTREAM"):
        _refuse("FlowLength", "direction_measurement", direction_measurement)
    raster = _d8(in_flow_direction_raster, "FlowLength")
    g = raster._grid
    down = _downstream(raster)
    active = ~raster._mask.ravel()
    columns = g.columns

    def step(i, d):
        (r, k), (nr, nk) = divmod(i, columns), divmod(d, columns)
        return math.hypot((nr - r) * g.height, (nk - k) * g.width)

    order = _upstream_order(np.where(active[np.clip(down, 0, None)] & (down >= 0), down, -1), active)
    length = np.zeros(down.size)
    if kind == "DOWNSTREAM":
        for i in reversed(order):
            d = down[i]
            length[i] = length[d] + step(i, d) if d >= 0 and active[d] else 0.0
    else:
        for i in order:
            d = down[i]
            if d >= 0 and active[d]:
                length[d] = max(length[d], length[i] + step(i, d))
    return Raster._of(length.reshape(raster._array.shape).astype(np.float32), raster._mask.copy(), g)


# ---- surfaces from points (moved here from ddd so 3D Analyst calls them, ADR-224)


def _cell_size(cell_size, tool):
    if not _given(cell_size):
        return None
    if isinstance(cell_size, Raster):
        return float(cell_size.meanCellWidth)
    try:
        return float(cell_size)
    except (TypeError, ValueError):
        return float(as_raster(cell_size, tool).meanCellWidth)


def _points(in_point_features, z_field, tool):
    """The points' coordinates and z values."""
    from . import da
    from .analysis import _schema
    columns, kind, _, _ = _schema(in_point_features)
    if kind not in ("Point", "Multipoint"):
        raise ValueError(f"{tool} takes points.")
    if str(z_field).upper() in ("SHAPE", "SHAPE@Z"):
        _refuse(tool, "z_field", z_field, "grapy's feature classes hold no z values yet; name a numeric field")
    names = {c.name.lower(): c for c in columns}
    if str(z_field).lower() not in names or names[str(z_field).lower()].type == "String":
        raise ValueError(f"'{z_field}' is not a numeric field of {in_point_features}.")
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
        raise ValueError(f"{tool} found no points with a value.")
    return np.array(xs), np.array(ys), np.array(zs)


def _point_grid(in_point_features, cell_size, xs, ys, tool):
    """The output grid: over the points' extent, its top-left corner at the extent's, at the cell size given or, by
    the documented rule, the shorter side of the extent divided by 250."""
    _refuse_environment(f"arcpy.sa.{tool}")
    cell = _cell_size(cell_size, tool)
    if cell is None:
        shorter = min(xs.max() - xs.min(), ys.max() - ys.min())
        if shorter <= 0:
            _refuse(tool, "cell_size", "(empty)", "the points' extent has no width or height to divide by 250")
        cell = shorter / 250.0
    return _grid_over(in_point_features, cell)


class RadiusVariable:
    """arcpy.sa.RadiusVariable: the nearest ``numberofPoints`` points, within ``maxDistance`` when given."""

    def __init__(self, numberofPoints=12, maxDistance=None):
        self.numberofPoints, self.maxDistance = numberofPoints, maxDistance


class RadiusFixed:
    """arcpy.sa.RadiusFixed: every point within ``distance``, widening to ``minNumberofPoints``."""

    def __init__(self, distance=None, minNumberofPoints=0):
        self.distance, self.minNumberofPoints = distance, minNumberofPoints


def _radius(search_radius):
    if not _given(search_radius):
        return "VARIABLE", 12, None
    if hasattr(search_radius, "numberofPoints"):
        return "VARIABLE", int(search_radius.numberofPoints), search_radius.maxDistance
    if hasattr(search_radius, "minNumberofPoints"):
        return "FIXED", search_radius.distance, int(search_radius.minNumberofPoints or 0)
    parts = [p for p in str(search_radius).replace(",", " ").split() if p]
    kind = parts[0].upper()
    if kind == "VARIABLE":
        return kind, int(float(parts[1])) if len(parts) > 1 else 12, float(parts[2]) if len(parts) > 2 else None
    if kind == "FIXED":
        return kind, float(parts[1]) if len(parts) > 1 else None, int(float(parts[2])) if len(parts) > 2 else 0
    raise ValueError(f"'{search_radius}' is not a Variable or Fixed search radius.")


def Idw(in_point_features, z_field, cell_size=None, power=2, search_radius=None, in_barrier_polyline_features=None):
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
    if _given(in_barrier_polyline_features):
        _refuse("Idw", "in_barrier_polyline_features", in_barrier_polyline_features)
    p = float(power)
    if p <= 0:
        raise ValueError("Idw's power must be greater than zero.")
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
        limit = float(second) if _given(second) else math.hypot(grid.right - grid.left, grid.top - grid.bottom)
        used = (np.arange(len(xs)) < first) & (ranked <= limit)
    else:
        radius = float(first) if _given(first) else 5 * grid.width
        used = (ranked <= radius) | (np.arange(len(xs)) < second)
    exact = ranked[..., 0] == 0
    with np.errstate(divide="ignore"):
        weights = np.where(used, 1.0 / np.where(ranked == 0, 1.0, ranked) ** p, 0.0)
    total = weights.sum(axis=-1)
    with np.errstate(invalid="ignore", divide="ignore"):
        surface = (weights * values).sum(axis=-1) / total
    surface = np.where(exact, values[..., 0], surface)
    mask = (total == 0) & ~exact
    return Raster._of(np.where(mask, 0, surface).astype(np.float32), mask, grid)


def Trend(in_point_features, z_field, cell_size=None, order=1, regression_type="LINEAR", out_rms_file=None):
    """A polynomial surface fitted to the points by least squares and evaluated at each cell centre. Order n uses every
    term xⁱyʲ with i + j ≤ n — for order 3: 1, x, y, x², xy, y², x³, x²y, xy², y³, as *How Trend works* writes it.

    ``LOGISTIC`` is refused (its maximum likelihood fit is not specified), and so is ``out_rms_file``, whose layout the
    documentation shows only by example.

    INFERRED: the default regression is ``LINEAR`` (the page marks none); the grid is as Idw's. The fit is computed on
    coordinates shifted and scaled for numerical accuracy, which changes nothing in a least-squares polynomial's values.
    """
    if str(regression_type or "LINEAR").upper() != "LINEAR":
        _refuse("Trend", "regression_type", regression_type, "the logistic fit is not specified")
    if _given(out_rms_file):
        _refuse("Trend", "out_rms_file", out_rms_file, "its layout is shown only by example")
    n = int(order)
    if not 1 <= n <= 12:
        raise ValueError("Trend's order is from 1 to 12.")
    xs, ys, zs = _points(in_point_features, z_field, "Trend")
    grid = _point_grid(in_point_features, cell_size, xs, ys, "Trend")
    x0, y0 = xs.mean(), ys.mean()
    scale = max(np.ptp(xs), np.ptp(ys)) or 1.0
    terms = [(d - j, j) for d in range(n + 1) for j in range(d + 1)]

    def design(x, y):
        u, v = (x - x0) / scale, (y - y0) / scale
        return np.column_stack([u ** i * v ** j for i, j in terms])

    if len(xs) < len(terms):
        raise ValueError(f"Trend of order {n} needs at least {len(terms)} points.")
    coefficients, *_ = np.linalg.lstsq(design(xs, ys), zs, rcond=None)
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx, cy)
    surface = (design(gx.ravel(), gy.ravel()) @ coefficients).reshape(gx.shape)
    return Raster._of(surface.astype(np.float32), np.zeros(surface.shape, bool), grid)


def Curvature(in_raster, z_factor=1, out_profile_curve_raster=None, out_plan_curve_raster=None):
    """The curvature of each cell from its 3×3 neighbourhood, as *How Curvature works* documents:
    ``−2(D + E) · 100`` with ``D = ((Z4 + Z6)/2 − Z5)/L²`` and ``E = ((Z2 + Z8)/2 − Z5)/L²`` — the second differences
    across the row and the column through the centre cell. Positive is convex; the unit is one hundredth of a z unit.

    The profile and plan outputs are refused: their formulas are not written in the documentation's text.

    INFERRED: L is the cell width across a row and the cell height down a column; a neighbour that is NoData or off the
    grid takes the centre cell's value, as Slope does by Esri's documentation of that tool.
    """
    for name, value in (("out_profile_curve_raster", out_profile_curve_raster), ("out_plan_curve_raster", out_plan_curve_raster)):
        if _given(value):
            _refuse("Curvature", name, value, "its formula is not written in the documentation's text")
    _refuse_environment("arcpy.sa.Curvature")
    raster = as_raster(in_raster, "Curvature")
    n = _neighbours(raster)
    zf = float(z_factor)
    w, h = raster._grid.width, raster._grid.height
    d = ((n["d"] + n["f"]) / 2 - n["e"]) * zf / (w * w)
    e = ((n["b"] + n["h"]) / 2 - n["e"]) * zf / (h * h)
    curvature = -2 * (d + e) * 100
    return Raster._of(curvature.astype(np.float32), raster._mask.copy(), raster._grid)


def ReclassByTable(in_raster, in_remap_table, from_value_field, to_value_field, output_value_field, missing_values="DATA"):
    """Reclassify with the remap read from a table: each row's from and to values make a range, and its output value
    the new value; a row whose from and to are equal reclassifies that single value. The output is always integer, so
    a floating-point value kept by ``DATA`` is refused.

    INFERRED, as for Reclassify's RemapRange: a range includes both its ends, and a value on the boundary of two ranges
    takes the earlier row; the documentation says ranges should not overlap."""
    from . import da
    with da.SearchCursor(in_remap_table, [from_value_field, to_value_field, output_value_field]) as rows:
        table = [[low, high, new] for low, high, new in rows]
    for _, _, new in table:
        if new is not None and not float(new).is_integer():
            raise ValueError(f"ReclassByTable's output values must be integers; {new} is not.")
    remap = RemapRange([[low, high, "NODATA" if new is None else int(new)] for low, high, new in table])
    result = Reclassify(in_raster, "VALUE", remap, missing_values)
    if not result.isInteger:
        _refuse("ReclassByTable", "missing_values", missing_values, "a kept floating-point value cannot be written to the "
                "always-integer output; map every value or use NODATA")
    return result


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
    from ._result import Result
    out = _out(out_polyline_features)
    _gpkg.create(out, "Polyline", raster._grid.srid, [_gpkg.Column("Contour", "Double")])
    with da.InsertCursor(out, ["Contour", "SHAPE@"]) as written:
        for level, line in lines:
            written.insertRow((float(level), line))
    return Result(out)


def Contour(in_raster, out_polyline_features, contour_interval, base_contour=0, z_factor=1, contour_type="CONTOUR",
            max_vertices_per_feature=None):
    """Contour lines at ``base_contour`` plus every multiple of ``contour_interval``, above and below it, across the
    raster's range of values (after ``z_factor``), each with its value in ``Contour``; written to the feature class.

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
        raise ValueError("Contour's interval must be positive.")
    _refuse_environment("arcpy.sa.Contour")
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
    _refuse_environment("arcpy.sa.ContourList")
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

    INFERRED: the field is named after the property, ``Z``, a double (an existing field of that name in any case is
    written); a point over NoData or off the raster gets an empty value; at the outermost half cell, where four centres
    do not surround the point, the cell's own value is used (see ``_sample``).
    """
    from . import _gpkg, da
    from .analysis import _schema
    from .management import _list
    from ._result import Result
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
            value = _sample(raster, point.x, point.y, True) if point is not None else None
            rows.updateRow([None if value is None else float(value) * zf, row[1]])
    return Result(in_feature_class)


SquareRoot = _unary("SquareRoot", lambda a: np.where(np.asarray(a) >= 0, np.sqrt(np.abs(a)), np.nan))
SquareRoot.__doc__ = "arcpy.sa.SquareRoot: floating point; a value below 0 is NoData, as the tool's page documents."


# ---- refused by name, with the reason

_UNSPECIFIED = "its algorithm is described in the documentation but not specified closely enough to reproduce"
_NO_RAT = "it needs a raster attribute table, which grapy's rasters do not carry yet"
_REFUSED = {name: _UNSPECIFIED for name in (
    "Kriging", "NaturalNeighbor", "Spline", "SplineWithBarriers", "TopoToRaster", "TopoToRasterByFile", "Viewshed",
    "Viewshed2", "Visibility", "ObserverPoints", "SurfaceParameters", "Expand", "Shrink", "Nibble", "MajorityFilter",
    "BoundaryClean", "Thin", "ZonalFill", "ContourWithBarriers",
)}
_REFUSED.update({
    "KernelDensity": "its formula and default search radius are shown only as images, and the text's sentence on "
                     "normalising by the number of points cannot be reconciled with the population inside the kernel",
    "Combine": _NO_RAT + " — its values mean nothing without the table of combinations",
    "Lookup": _NO_RAT, "ZonalGeometry": "how PERIMETER and THICKNESS treat cell edges and the raster's edge is not documented",
    "TabulateArea": "the documentation does not name the fields it writes for each class",
    "Sample": "the documentation does not name the fields it writes for each raster",
    "ReclassByASCIIFile": "the documentation does not specify the remap file's format",
    "CutFill": _NO_RAT + " — the VOLUME and AREA of each region",
    "InterpolateShape": "it writes z values on features, and grapy's feature classes are two-dimensional today",
})


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)

    def missing(*_, **__):
        raise NotBuiltError(f"arcpy.sa.{name} is not built in grapy" + (f": {why}." if why else " yet."))

    return missing
