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

from ._raster import Raster, _Bands, _combine, _Grid, _open_bands, _refuse_environment, as_raster
from ._raster import NumPyArrayToRaster, RasterToNumPyArray  # noqa: F401 — arcpy.sa answers these too
from ._runtime import NotBuiltError

# ---- one-input mathematics


def _unary(name, function, keep_integer=False):
    def tool(in_raster_or_constant):
        _refuse_environment(f"arcpy.sa.{name}")
        return _combine([as_raster(in_raster_or_constant, name, bands=True)], function, keep_integer=keep_integer, tool=name)
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
        values = [as_raster(v, name, bands=True) if isinstance(v, str) else v for v in (in_raster_or_constant1, in_raster_or_constant2)]
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
    values = [as_raster(v, "Divide", bands=True) if isinstance(v, str) else v for v in (in_raster_or_constant1, in_raster_or_constant2)]
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
    """A where clause on the cell value, ``VALUE > 5``, as a 0/1 array. A clause that names other fields is SQL on the
    raster's attribute table, as documented: the cells whose Value's row satisfies it.

    INFERRED: the clause is run by SQLite on a copy of the table, so its SQL dialect is SQLite's."""
    if raster._rat is not None and not all(_WHERE.match(part) or part.upper() in ("AND", "OR") for part in
                                           re.split(r"\s+(AND|OR)\s+", where_clause.strip(), flags=re.IGNORECASE)):
        return _where_on_table(raster, where_clause, tool)
    clauses = re.split(r"\s+(AND|OR)\s+", where_clause.strip(), flags=re.IGNORECASE)
    result, joiner = None, None
    for part in clauses:
        if part.upper() in ("AND", "OR"):
            joiner = part.upper()
            continue
        found = _WHERE.match(part)
        if not found:
            raise NotBuiltError(f"{tool}: the where clause '{where_clause}' is not built in grapy yet; "
                                "only comparisons of VALUE with a number, joined by AND or OR, are, unless the raster has "
                                "an attribute table for the clause to run on.")
        op, number = found.group(1), float(found.group(2))
        a = raster._array
        test = {"<": a < number, ">": a > number, "<=": a <= number, ">=": a >= number, "=": a == number,
                "==": a == number, "<>": a != number, "!=": a != number}[op]
        result = test if result is None else (result & test if joiner == "AND" else result | test)
    return result


def _where_on_table(raster, where_clause, tool):
    import sqlite3
    rat = raster._rat
    kinds = {"Integer": "INTEGER", "Double": "REAL", "String": "TEXT"}
    connection = sqlite3.connect(":memory:")
    try:
        connection.execute("create table rat (" + ", ".join(f'"{n}" {kinds[k]}' for n, k, _ in rat.fields) + ")")
        connection.executemany(f"insert into rat values ({', '.join('?' for _ in rat.fields)})", rat.rows)
        value = rat.fields[rat.index("Value")][0]
        try:
            chosen = [row[0] for row in connection.execute(f'select "{value}" from rat where {where_clause}')]
        except sqlite3.Error as error:
            raise ValueError(f"{tool}: the where clause '{where_clause}' is not valid on the attribute table: {error}.") from None
    finally:
        connection.close()
    return np.isin(raster._array, np.array(chosen, dtype=np.float64))


def _band_wise(values, function, tool):
    from ._raster import _band_wise as _wise
    opened = [as_raster(v, tool, bands=True) if isinstance(v, (str, Raster)) else v for v in values]
    return _wise(opened, function, tool)


def Con(in_conditional_raster, in_true_raster_or_constant, in_false_raster_or_constant=None, where_clause=None):
    """The true value where the condition holds (non-zero, or the where clause is true), the false value elsewhere, and
    NoData where there is no false value. Multiband inputs are processed band by band, as documented: all of them
    multiband with the same number of bands, or a multiband condition with constants."""
    _refuse_environment("arcpy.sa.Con")
    multiband = _band_wise([in_conditional_raster, in_true_raster_or_constant, in_false_raster_or_constant],
                           lambda c, t, f: Con(c, t, f, where_clause), "Con")
    if multiband is not None:
        return multiband
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
    """NoData where the condition holds, the false value elsewhere; band by band for multiband inputs, as Con."""
    _refuse_environment("arcpy.sa.SetNull")
    multiband = _band_wise([in_conditional_raster, in_false_raster_or_constant],
                           lambda c, f: SetNull(c, f, where_clause), "SetNull")
    if multiband is not None:
        return multiband
    condition = as_raster(in_conditional_raster, "SetNull")
    test = _where(condition, where_clause, "SetNull") if where_clause else condition._array != 0
    flipped = Raster._of((~test).astype(np.int32), condition._mask.copy(), condition._grid)
    return Con(flipped, in_false_raster_or_constant)


def IsNull(in_raster):
    """1 where the input is NoData, 0 elsewhere; never NoData itself. Band by band for a multiband input."""
    multiband = _band_wise([in_raster], IsNull, "IsNull")
    if multiband is not None:
        return multiband
    raster = as_raster(in_raster, "IsNull")
    return Raster._of(raster._mask.astype(np.int32), np.zeros(raster._mask.shape, bool), raster._grid)


def Test(in_raster, where_clause):
    """1 where the where clause is true, 0 where it is false, NoData where the input is. Band by band for a multiband
    input."""
    multiband = _band_wise([in_raster], lambda r: Test(r, where_clause), "Test")
    if multiband is not None:
        return multiband
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
    """A statistic of the inputs, cell by cell. ``DATA`` leaves NoData inputs out of each cell's statistic.

    Multiband inputs as documented: under ``SINGLE_BAND`` (the default) each band of a multiband input is an input of
    its own and the output has one band; under ``MULTI_BAND`` the statistic is taken band by band over the inputs'
    corresponding bands — every raster input multiband with the same number of bands, or multiband with constants —
    and the output has as many bands. A multiband raster with a single-band one under MULTI_BAND is refused (the page
    does not say how the single band pairs with the bands)."""
    _refuse_environment("arcpy.sa.CellStatistics")
    mode = str(process_as_multiband or "SINGLE_BAND").upper()
    if mode in ("TRUE", "MULTI_BAND"):
        mode = "MULTI_BAND"
    elif mode in ("FALSE", "SINGLE_BAND"):
        mode = "SINGLE_BAND"
    else:
        _refuse("CellStatistics", "process_as_multiband", process_as_multiband)
    opened = [as_raster(v, "CellStatistics", bands=True) if isinstance(v, (str, Raster)) else v for v in in_rasters_or_constants]
    if any(isinstance(v, Raster) and v.bandCount > 1 for v in opened):
        if mode == "SINGLE_BAND":
            expanded = []
            for v in opened:
                expanded.extend([v._band(b) for b in range(1, v.bandCount + 1)] if isinstance(v, Raster) else [v])
            return CellStatistics(expanded, statistics_type, ignore_nodata)
        return _band_wise(opened, lambda *band: CellStatistics(list(band), statistics_type, ignore_nodata),
                          "CellStatistics")
    inputs = opened
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
    raster = as_raster(in_raster, "Reclassify")
    if isinstance(remap, str):
        remap = _parse_remap(remap)
    if str(reclass_field).upper() not in ("VALUE", "VALUES"):
        return _reclassify_by_field(raster, reclass_field, remap, missing_values)
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
        from ._raster import _align
        (z, v), (zm, vm), grid = _align([zones, value], tool)
        if str(zone_field).upper() not in ("VALUE", "VALUES"):
            # A field of the zone raster's attribute table: each cell's zone is its row's value in that field; text
            # zones are coded 1, 2 … in sorted order and named back, as a feature zone's text field is.
            looked, missing = _rat_values(zones, zone_field, tool, z)
            present = [v for v in looked.ravel()[~(missing | zm).ravel()]]
            if all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in present):
                return np.where(missing, 0, looked).astype(np.float64), zm | missing, {}, (v, vm, grid)
            codes = {value_: i + 1 for i, value_ in enumerate(sorted({str(p) for p in present}))}
            coded = np.array([codes.get(str(x), 0) for x in looked.ravel()], dtype=np.float64).reshape(z.shape)
            return coded, zm | missing, {c: n for n, c in codes.items()}, (v, vm, grid)
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
    """The points copied with ``RASTERVALU``, the cell value under each; -9999 where it is NoData, as documented. With
    ``ALL`` every field of the raster's attribute table except Count is added too, the row of the cell each point is in
    (the cell's own value, not an interpolated one)."""
    from . import _gpkg, da
    from .analysis import _out, _schema
    from ._result import Result
    every = str(add_attributes or "VALUE_ONLY").upper() == "ALL"
    raster = as_raster(in_raster, "ExtractValuesToPoints")
    if every and raster._rat is None:
        raise ValueError("ExtractValuesToPoints: ALL needs the raster's attribute table; build one first.")
    columns, shape, srid, _ = _schema(in_point_features)
    names = [c.name for c in columns]
    if "rastervalu" in [n.lower() for n in names]:
        raise ValueError("ExtractValuesToPoints: the input already has a RASTERVALU field, which the tool adds.")
    # ALL: every field of the attribute table but Count, as documented, after RASTERVALU (INFERRED: a name the input
    # already has is given _1, _2 …).
    extra, taken = [], {n.lower() for n in names} | {"rastervalu"}
    if every:
        for name, kind, _ in raster._rat.fields:
            if name.lower() == "count":
                continue
            label, i = name, 0
            while label.lower() in taken:
                i += 1
                label = f"{name}_{i}"
            taken.add(label.lower())
            extra.append((name, label, {"Integer": "Integer", "Double": "Double"}.get(kind, "String")))
    out = _out(out_point_features)
    _gpkg.create(out, shape, srid, [*columns, _gpkg.Column("RASTERVALU", "Double"),
                                    *[_gpkg.Column(label, kind) for _, label, kind in extra]])
    bilinear = str(interpolate_values).upper() == "INTERPOLATE"
    rows_by_value = {}
    if every:
        at = raster._rat.index("Value")
        rows_by_value = {row[at]: row for row in raster._rat.rows}
    with da.SearchCursor(in_point_features, [*names, "SHAPE@"]) as rows, \
            da.InsertCursor(out, [*names, "RASTERVALU", *[label for _, label, _ in extra], "SHAPE@"]) as written:
        for row in rows:
            point = row[-1]._shape if row[-1] is not None else None
            value = _sample(raster, point.x, point.y, bilinear) if point is not None else None
            attributes = []
            if every:
                cell = _sample(raster, point.x, point.y, False) if point is not None else None
                match = rows_by_value.get(cell)
                attributes = [match[raster._rat.index(name)] if match is not None else None for name, _, _ in extra]
            written.insertRow((*row[:-1], -9999.0 if value is None else float(value), *attributes, row[-1]))
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

    A field other than ``VALUE`` is read from the raster's attribute table: each cell takes its row's value.
    INFERRED: NoData in any input is NoData, arcpy's general rule; the page is silent."""
    _refuse_environment("arcpy.sa.WeightedSum")
    rows = in_rasters.weightedSumTable if isinstance(in_rasters, WSTable) else in_rasters
    if not rows:
        raise ValueError("WeightedSum needs at least one row.")
    rasters = []
    for row in rows:
        raster = as_raster(row[0], "WeightedSum") if not isinstance(row[0], Raster) else row[0]
        if str(row[1]).upper() not in ("VALUE", "VALUES"):
            looked, missing = _rat_values(raster, row[1], "WeightedSum")
            values = np.array([0.0 if v is None else float(v) for v in looked.ravel()]).reshape(looked.shape)
            raster = Raster._of(values, raster._mask | missing, raster._grid)
        rasters.append(raster)
    arrays, mask, grid = _many(rasters, "WeightedSum")
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

    The output's attribute table has Value and Count, and under ``ADD_LINK`` (the default; ignored for CROSS, as
    documented) a LINK field holding the value of the input zone each region came from.

    INFERRED: excluded cells (0) have no row in the table."""
    from scipy.ndimage import label
    _refuse_environment("arcpy.sa.RegionGroup")
    link = str(add_link or "ADD_LINK").upper() == "ADD_LINK"
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
    result = Raster._of(out, mask.copy(), raster._grid)
    from ._raster import _Rat
    cross = str(zone_connectivity or "WITHIN").upper() == "CROSS"
    numbers, counts = np.unique(out[~mask & (out > 0)], return_counts=True)
    fields = [("Value", "Integer", 5), ("Count", "Integer", 1)]
    rows = [[int(n), int(c)] for n, c in zip(numbers, counts)]
    if link and not cross:
        fields.append(("LINK", "Integer", 0))
        first = {}
        flat_out, flat_in = out.ravel(), a.ravel()
        for flat in np.flatnonzero(flat_out > 0):
            first.setdefault(int(flat_out[flat]), int(flat_in[flat]))
        for row in rows:
            row.append(first[row[0]])
    result._rat = _Rat(fields, rows)
    return result


# ---- density


def _area_unit(unit, srid, tool, lines=False):
    """What a density in data units is multiplied by for ``area_unit_scale_factor``: square map units leave it, and
    for data in metres the square units convert area and, for lines, length too — kilometres per square kilometre. The
    imperial units are the US survey ones, ``ACRES`` too, as Pro's PointDensity measures them (D-323)."""
    from ._reference import SpatialReference
    from ._units import DENSITY_AREA as square
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


# ---- path distance, distance accumulation and least-cost paths (ADR-241), built in _pathdist
from . import _pathdist  # noqa: E402
from ._runtime import equivalent as _equivalent_distance  # noqa: E402

HfBinary = _pathdist.HfBinary
HfForward = _pathdist.HfForward
HfLinear = _pathdist.HfLinear
HfInverseLinear = _pathdist.HfInverseLinear
HfTable = _pathdist.HfTable
VfBinary = _pathdist.VfBinary
VfLinear = _pathdist.VfLinear
VfSymLinear = _pathdist.VfSymLinear
VfInverseLinear = _pathdist.VfInverseLinear
VfSymInverseLinear = _pathdist.VfSymInverseLinear
VfCos = _pathdist.VfCos
VfSec = _pathdist.VfSec
VfCosSec = _pathdist.VfCosSec
VfSecCos = _pathdist.VfSecCos
VfHikingTime = _pathdist.VfHikingTime
VfBidirHikingTime = _pathdist.VfBidirHikingTime
VfTable = _pathdist.VfTable
PathDistance = _pathdist.PathDistance
PathAllocation = _pathdist.PathAllocation
PathBackLink = _pathdist.PathBackLink
CostPath = _pathdist.CostPath
CostPathAsPolyline = _pathdist.CostPathAsPolyline
_SURFACE_RECONSTRUCTION = (
    "fast marching surface reconstruction, eight network estimates and four Eikonal steps per cell, the "
    "direction-dependent slope solved again along each step's own gradient",
    "Sethian 1999, Level Set Methods and Fast Marching Methods, as Distance accumulation algorithm cites it")
DistanceAccumulation = _equivalent_distance(*_SURFACE_RECONSTRUCTION)(_pathdist.DistanceAccumulation)
DistanceAllocation = _equivalent_distance(*_SURFACE_RECONSTRUCTION)(_pathdist.DistanceAllocation)
_SLOPELINE = ("slopeline tracing down the back-direction raster across the lattice through the cell centres, as "
              "Distance accumulation algorithm draws it",
              "Douglas 1994, Least-cost Path in GIS Using an Accumulated Cost Surface and Slopelines")
OptimalPathAsRaster = _equivalent_distance(*_SLOPELINE)(_pathdist.OptimalPathAsRaster)
OptimalPathAsLine = _equivalent_distance(*_SLOPELINE)(_pathdist.OptimalPathAsLine)


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

    ``DINF`` is an EQUIVALENT: Tarboton's (1997) D-infinity, see ``_flow_direction_dinf``; it says so in its run.

    Flats (a steepest drop of zero) are an EQUIVALENT: Garbrecht and Martz (1997) as Barnes et al. (2014) make it
    exact, see ``_resolve_flats``; it says so in its run.

    Refused rather than guessed (D8): a cell whose positive steepest drop is shared by several neighbours — the
    documentation resolves it with a lookup table (Greenlee 1987) it does not publish; a surface with NoData;
    MFD and the geodesic method; and ``out_drop_raster``.

    INFERRED: outward from an edge is straight across it (64 on the top row, 4 bottom, 16 left, 1 right) and
    diagonal at a corner (32 top left, 128 top right, 8 bottom left, 2 bottom right); a diagonal step is
    √(width² + height²) long."""
    _refuse_environment("arcpy.sa.FlowDirection")
    dinf = str(flow_direction_type or "D8").upper() == "DINF"
    if str(flow_direction_type or "D8").upper() not in ("D8", "DINF"):
        _refuse("FlowDirection", "flow_direction_type", flow_direction_type)
    if str(method or "PLANAR").upper() != "PLANAR":
        _refuse("FlowDirection", "method", method)
    if _given(out_drop_raster):
        _refuse("FlowDirection", "out_drop_raster", out_drop_raster, "its value across flats and at outward edges is not specified")
    raster = as_raster(in_surface_raster, "FlowDirection")
    if raster._mask.any():
        _refuse("FlowDirection", "in_surface_raster", in_surface_raster, "how NoData neighbours are treated is not documented")
    force = str(force_flow or "NORMAL").upper() == "FORCE"
    if dinf:
        return _flow_direction_dinf(raster, force)
    z = raster._array.astype(np.float64)
    rows, columns = z.shape
    g = raster._grid
    out = np.zeros(z.shape, np.int32)
    flats = {}
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
                # A flat: left for the flat resolution below, with its tied directions kept for the undefined case.
                flats[(r, k)] = sum(code for d, code, _ in drops if d == 0)
                continue
            if len(winners) != 1:
                raise NotBuiltError(f"arcpy.sa.FlowDirection: the cell at row {r}, column {k} has no single steepest "
                                    "descent; the documentation resolves it with an unpublished lookup table (Greenlee "
                                    "1987), so grapy does not guess. Fill the surface or break the tie.")
            out[r, k] = winners[0]
    if flats:
        out = _resolve_flats(z, out, flats)
    return Raster._of(out, np.zeros(z.shape, bool), g)


def _flats_tool():
    @_equivalent_early("flat resolution by gradients towards lower and away from higher terrain",
                       "Garbrecht and Martz 1997, Journal of Hydrology 193; as made exact by Barnes, Lehman and Mulla "
                       "2014, Computers & Geosciences 62")
    def FlowDirection(z, out, flats):
        """EQUIVALENT: D8 across flats as Garbrecht and Martz (1997) define it, in Barnes, Lehman and Mulla's (2014)
        exact formulation: a flat's cells that border lower terrain through a draining cell (low edges) and those that
        border higher terrain (high edges) are found; each flat draining somewhere is labelled from its low edges; a
        gradient away from higher terrain (steps from the high edges) and one towards lower terrain (steps from the low
        edges, doubled) are combined as Barnes et al. combine them; and each flat cell flows to the neighbour of its
        flat with the least combined value. A flat that drains nowhere keeps the sum of its tied directions, the value
        Esri documents for an undefined cell.

        INFERRED: of neighbours with equal least values, the first in the code order 1, 2, 4 … 128 wins."""
        from collections import deque
        rows, columns = z.shape
        noflow = np.zeros(z.shape, bool)
        for (r, k) in flats:
            noflow[r, k] = True

        def neighbours(r, k):
            for code, (dr, dk) in _D8.items():
                nr, nk = r + dr, k + dk
                if 0 <= nr < rows and 0 <= nk < columns:
                    yield code, nr, nk

        low, high = [], []
        for r in range(rows):
            for k in range(columns):
                for _, nr, nk in neighbours(r, k):
                    if not noflow[r, k] and noflow[nr, nk] and z[nr, nk] == z[r, k]:
                        low.append((r, k))
                        break
                for _, nr, nk in neighbours(r, k):
                    if noflow[r, k] and z[nr, nk] > z[r, k]:
                        high.append((r, k))
                        break
        labels = np.zeros(z.shape, np.int64)
        label = 0
        for start in low:
            if labels[start]:
                continue
            label += 1
            queue = deque([start])
            labels[start] = label
            while queue:
                r, k = queue.popleft()
                for _, nr, nk in neighbours(r, k):
                    if not labels[nr, nk] and z[nr, nk] == z[start]:
                        labels[nr, nk] = label
                        queue.append((nr, nk))
        high = [c for c in high if labels[c]]
        mask = np.zeros(z.shape, np.int64)
        height = {}
        front, loops = list(high), 1
        while front:
            following = []
            for c in front:
                if mask[c] > 0:
                    continue
                mask[c] = loops
                height[labels[c]] = loops
                for _, nr, nk in neighbours(*c):
                    if labels[nr, nk] == labels[c] and noflow[nr, nk] and mask[nr, nk] == 0:
                        following.append((nr, nk))
            front, loops = following, loops + 1
        mask = -mask
        front, loops = list(low), 1
        while front:
            following = []
            for c in front:
                if mask[c] > 0:
                    continue
                mask[c] = height.get(labels[c], 0) + mask[c] + 2 * loops if mask[c] < 0 else 2 * loops
                for _, nr, nk in neighbours(*c):
                    if labels[nr, nk] == labels[c] and noflow[nr, nk] and mask[nr, nk] <= 0:
                        following.append((nr, nk))
            front, loops = following, loops + 1
        result = out.copy()
        for (r, k), tied in flats.items():
            if not labels[r, k]:
                result[r, k] = tied
                continue
            least, direction = mask[r, k], 0
            for code, nr, nk in neighbours(r, k):
                if labels[nr, nk] == labels[r, k] and mask[nr, nk] < least:
                    least, direction = mask[nr, nk], code
            result[r, k] = direction if direction else tied
        return result
    return FlowDirection


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
    if str(z_field).upper() in ("SHAPE", "SHAPE@Z", "SHAPE.Z"):
        # The points' own z, as documented ("the Shape field if the points contain z-values").
        import shapely
        xs, ys, zs = [], [], []
        with da.SearchCursor(in_point_features, ["SHAPE@"]) as rows:
            for (shape,) in rows:
                if shape is None:
                    continue
                if not shapely.has_z(shape._shape):
                    _refuse(tool, "z_field", z_field, "the points have no z-values")
                for x, y, z in shapely.get_coordinates(shape._shape, include_z=True):
                    xs.append(float(x))
                    ys.append(float(y))
                    zs.append(float(z))
        if not xs:
            raise ValueError(f"{tool} found no points with a value.")
        return np.array(xs), np.array(ys), np.array(zs)
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

    The profile and plan outputs are an EQUIVALENT (see ``_profile_and_plan``): Moore et al.'s (1991) formulas on the
    page's D, E, F, G and H, signed as the page documents; they say so in the run.

    INFERRED: L is the cell width across a row and the cell height down a column; a neighbour that is NoData or off the
    grid takes the centre cell's value, as Slope does by Esri's documentation of that tool.
    """
    _refuse_environment("arcpy.sa.Curvature")
    raster = as_raster(in_raster, "Curvature")
    n = _neighbours(raster)
    zf = float(z_factor)
    w, h = raster._grid.width, raster._grid.height
    d = ((n["d"] + n["f"]) / 2 - n["e"]) * zf / (w * w)
    e = ((n["b"] + n["h"]) / 2 - n["e"]) * zf / (h * h)
    curvature = -2 * (d + e) * 100
    if _given(out_profile_curve_raster) or _given(out_plan_curve_raster):
        profile, plan = _profile_and_plan(n, zf, w, h, d, e)
        for path, value in ((out_profile_curve_raster, profile), (out_plan_curve_raster, plan)):
            if _given(path):
                Raster._of(value.astype(np.float32), raster._mask.copy(), raster._grid).save(path)
    return Raster._of(curvature.astype(np.float32), raster._mask.copy(), raster._grid)


from ._runtime import equivalent as _equivalent_early  # noqa: E402


def _curvature_tool():
    @_equivalent_early("profile and plan curvature", "Moore, Grayson and Ladson 1991, Hydrological Processes 5(1), "
                       "on the coefficients of Zevenbergen and Thorne 1987")
    def Curvature(n, zf, w, h, d, e):
        """EQUIVALENT: profile and plan curvature by Moore et al. (1991) on Zevenbergen and Thorne's (1987)
        coefficients — the page's D and E, with F = (−Z1 + Z3 + Z7 − Z9)/(4L²), G = (−Z4 + Z6)/(2L) and
        H = (Z2 − Z8)/(2L) in Zevenbergen and Thorne's numbering (Z1 top left, row by row): profile
        ``2(DG² + EH² + FGH)/(G² + H²) · 100`` and plan ``−2(DH² + EG² − FGH)/(G² + H²) · 100``. These are Moore's
        expressions with the signs the page documents: profile negative where the surface is upwardly convex, plan
        positive where it is laterally convex. A flat cell (G = H = 0) is 0.

        INFERRED: the flat case; L as in Curvature."""
        f = (-n["a"] + n["c"] + n["g"] - n["i"]) * zf / (4 * w * h)
        g = (-n["d"] + n["f"]) * zf / (2 * w)
        hh = (n["b"] - n["h"]) * zf / (2 * h)
        slope2 = g * g + hh * hh
        safe = np.where(slope2 == 0, 1.0, slope2)
        profile = np.where(slope2 == 0, 0.0, 2 * (d * g * g + e * hh * hh + f * g * hh) / safe * 100)
        plan = np.where(slope2 == 0, 0.0, -2 * (d * hh * hh + e * g * g - f * g * hh) / safe * 100)
        return profile, plan
    return Curvature


_profile_and_plan = _curvature_tool()
_resolve_flats = _flats_tool()


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

    Built: point features on a raster surface, property ``Z``. The other properties on a raster are refused: the
    documentation says a raster is first turned into an intermediate TIN, and does not say how. On a TIN, terrain or LAS
    dataset every property is built (ADR-229, ADR-234): see ``_tintools.add_surface_information``.

    INFERRED: the field is named after the property, ``Z``, a double (an existing field of that name in any case is
    written); a point over NoData or off the raster gets an empty value; at the outermost half cell, where four centres
    do not surround the point, the cell's own value is used (see ``_sample``).
    """
    from . import _gpkg, da
    from .analysis import _schema
    from .management import _list
    from ._result import Result
    from . import _tintools
    if _tintools.is_surface(in_surface):
        return _tintools.add_surface_information(in_feature_class, in_surface, out_property, method, sample_distance,
                                                 z_factor, pyramid_level_resolution, noise_filtering)
    properties = [p.upper() for p in _list(out_property)]
    if properties != ["Z"]:
        _refuse("AddSurfaceInformation", "out_property", out_property, "on a raster only Z at points is built")
    if str(method or "BILINEAR").upper() != "BILINEAR":
        _refuse("AddSurfaceInformation", "method", method, "a raster surface takes only BILINEAR")
    if isinstance(in_surface, str) and not in_surface.lower().endswith((".tif", ".tiff")):
        _refuse("AddSurfaceInformation", "in_surface", in_surface,
                "it is not a raster, TIN, terrain or LAS dataset grapy holds")
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


# ---- image tools that arcpy.sa and arcpy.ia share (ADR-224); arcpy.ia re-exports them

# ---- band indexes: the formulas of each function's page, which are those of the Band Arithmetic raster function


def _band_values(raster, ids, tool):
    """The bands asked for, one-based, as floating point with NoData as NaN, and their grid."""
    bands = _open_bands(raster, tool)
    out = []
    for number in ids:
        if number is None:
            _refuse(tool, "band id", "(empty)", "its default is not documented; pass it")
        band = bands._band(int(number))
        out.append(np.where(band._mask, np.nan, band._array.astype(np.float64)))
    return out, bands._grid


def _index_raster(values, grid):
    """An index's answer: floating point, NoData where any band used was NoData or the formula has no value (a zero
    denominator). INFERRED: 32-bit floating point, as arcpy's float rasters are; the pages give no pixel type."""
    with np.errstate(all="ignore"):
        values = np.asarray(values, dtype=np.float64)
    mask = ~np.isfinite(values)
    return Raster._of(np.where(mask, 0, values).astype(np.float32), mask, grid)


def _index(name, band_names, defaults, formula, doc, constants=()):
    """An index function: ``name(raster, <band ids…>, <constants…>)`` with the documented defaults."""
    parameters = [*band_names, *[c for c, _ in constants]]

    def tool(raster, *args, **kwargs):
        given = dict(zip(parameters, args))
        given.update(kwargs)
        unknown = set(given) - set(parameters)
        if unknown:
            raise TypeError(f"{name}() got an unexpected argument {sorted(unknown)[0]!r}")
        ids = [given.get(b, defaults.get(b)) for b in band_names]
        values, grid = _band_values(raster, ids, name)
        numbers = [float(given.get(c, d)) for c, d in constants]
        with np.errstate(all="ignore"):
            return _index_raster(formula(*values, *numbers), grid)

    tool.__name__ = tool.__qualname__ = name
    shown = [f"{b}={defaults.get(b)}" for b in band_names] + [f"{c}={d}" for c, d in constants]
    tool.__doc__ = f"arcpy.ia.{name}(raster, {', '.join(shown)}): {doc} Band ids are one-based."
    return tool


NDVI = _index("NDVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
              lambda nir, red: (nir - red) / (nir + red), "(NIR − Red) / (NIR + Red), from −1 to 1.")
SAVI = _index("SAVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 7, "red_band_id": 6},
              lambda nir, red, l: (nir - red) / (nir + red + l) * (1 + l),
              "((NIR − Red) / (NIR + Red + L)) × (1 + L). The defaults are the page's: bands 7 and 6, L 0.33.",
              [("l", 0.33)])
EVI = _index("EVI", ["nir_band_id", "red_band_id", "blue_band_id"], {"nir_band_id": 5, "red_band_id": 4, "blue_band_id": 2},
             lambda nir, red, blue: 2.5 * (nir - red) / (nir + 6 * red - 7.5 * blue + 1),
             "2.5 × (NIR − Red) / (NIR + 6 × Red − 7.5 × Blue + 1).")
MSAVI = _index("MSAVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
               lambda nir, red: 0.5 * (2 * (nir + 1) - np.sqrt((2 * nir + 1) ** 2 - 8 * (nir - red))),
               "MSAVI2 = ½ (2(NIR + 1) − √((2 NIR + 1)² − 8(NIR − Red))).")
NDWI = _index("NDWI", ["nir_band_id", "green_band_id"], {"nir_band_id": 5, "green_band_id": 3},
              lambda nir, green: (green - nir) / (green + nir), "(Green − NIR) / (Green + NIR).")
NBR = _index("NBR", ["swir_band_id", "nir_band_id"], {"swir_band_id": 7, "nir_band_id": 5},
             lambda swir, nir: (nir - swir) / (nir + swir), "(NIR − SWIR) / (NIR + SWIR).")
BAI = _index("BAI", ["red_band_id", "nir_band_id"], {"red_band_id": 3, "nir_band_id": 4},
             lambda red, nir: 1 / ((0.1 - red) ** 2 + (0.06 - nir) ** 2), "1 / ((0.1 − Red)² + (0.06 − NIR)²).")
CIg = _index("CIg", ["nir_band_id", "green_band_id"], {"nir_band_id": 7, "green_band_id": 3},
             lambda nir, green: nir / green - 1, "(NIR / Green) − 1.")
CIre = _index("CIre", ["nir_band_id", "redEdge_band_id"], {"nir_band_id": 7, "redEdge_band_id": 6},
              lambda nir, edge: nir / edge - 1, "(NIR / RedEdge) − 1.")
ClayMinerals = _index("ClayMinerals", ["swir1_band_id", "swir2_band_id"], {"swir1_band_id": 6, "swir2_band_id": 7},
                      lambda a, b: a / b, "SWIR1 / SWIR2.")
FerrousMinerals = _index("FerrousMinerals", ["swir_band_id", "nir_band_id"], {"swir_band_id": 6},
                         lambda swir, nir: swir / nir,
                         "SWIR / NIR. The page gives no default for nir_band_id, so it must be passed.")
GEMI = _index("GEMI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
              lambda nir, red: (lambda eta: eta * (1 - 0.25 * eta) - (red - 0.125) / (1 - red))(
                  (2 * (nir ** 2 - red ** 2) + 1.5 * nir + 0.5 * red) / (nir + red + 0.5)),
              "η(1 − 0.25η) − (Red − 0.125)/(1 − Red), η = (2(NIR² − Red²) + 1.5 NIR + 0.5 Red)/(NIR + Red + 0.5).")
IronOxide = _index("IronOxide", ["red_band_id", "blue_band_id"], {"red_band_id": 4, "blue_band_id": 2},
                   lambda red, blue: red / blue, "Red / Blue.")
NDBI = _index("NDBI", ["swir_band_id", "nir_band_id"], {"swir_band_id": 6, "nir_band_id": 5},
              lambda swir, nir: (swir - nir) / (swir + nir), "(SWIR − NIR) / (SWIR + NIR).")
NDMI = _index("NDMI", ["nir_band_id", "swir1_band_id"], {"nir_band_id": 5, "swir1_band_id": 6},
              lambda nir, swir: (nir - swir) / (nir + swir), "(NIR − SWIR1) / (NIR + SWIR1).")
NDSI = _index("NDSI", ["green_band_id", "swir_band_id"], {"green_band_id": 4, "swir_band_id": 6},
              lambda green, swir: (green - swir) / (green + swir), "(Green − SWIR) / (Green + SWIR).")
NDVIre = _index("NDVIre", ["nir_band_id", "redEdge_band_id"], {"nir_band_id": 7, "redEdge_band_id": 6},
                lambda nir, edge: (nir - edge) / (nir + edge), "(NIR − RedEdge) / (NIR + RedEdge).")
PVI = _index("PVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
             lambda nir, red, a, b: (nir - a * red - b) / np.sqrt(1 + a * a), "(NIR − a·Red − b) / √(1 + a²).",
             [("a", 0.3), ("b", 0.5)])
RTVICore = _index("RTVICore", ["nir_band_id", "redEdge_band_id", "green_band_id"],
                  {"nir_band_id": 7, "redEdge_band_id": 6, "green_band_id": 3},
                  lambda nir, edge, green: 100 * (nir - edge) - 10 * (nir - green), "100(NIR − RedEdge) − 10(NIR − Green).")
SR = _index("SR", ["nir_band_id", "red_band_id"], {"nir_band_id": 5, "red_band_id": 4}, lambda nir, red: nir / red,
            "NIR / Red.")
SRre = _index("SRre", ["nir_band_id", "redEdge_band_id"], {"nir_band_id": 7, "redEdge_band_id": 6},
              lambda nir, edge: nir / edge, "NIR / RedEdge.")
TSAVI = _index("TSAVI", ["nir_band_id", "red_band_id"], {"nir_band_id": 4, "red_band_id": 3},
               lambda nir, red, s, a, x: s * (nir - s * red - a) / (a * nir + red - a * s + x * (1 + s * s)),
               "s(NIR − s·Red − a) / (a·NIR + Red − a·s + X(1 + s²)).", [("s", 0.33), ("a", 0.5), ("X", 1.5)])
VARI = _index("VARI", ["red_band_id", "green_band_id", "blue_band_id"], {"red_band_id": 3, "green_band_id": 2, "blue_band_id": 1},
              lambda red, green, blue: (green - red) / (green + red - blue), "(Green − Red) / (Green + Red − Blue).")


# BandArithmetic's method codes and, for each, the formula over the bands named in band_ids and the constants that
# follow them in the same string, as the Band Arithmetic raster function documents.
_METHODS = {
    1: ("NDVI", 2, lambda nir, red: (nir - red) / (nir + red)),
    2: ("SAVI", 3, lambda nir, red, l: (nir - red) / (nir + red + l) * (1 + l)),
    3: ("TSAVI", 5, lambda nir, red, s, a, x: s * (nir - s * red - a) / (a * nir + red - a * s + x * (1 + s * s))),
    4: ("MSAVI", 2, lambda nir, red: 0.5 * (2 * (nir + 1) - np.sqrt((2 * nir + 1) ** 2 - 8 * (nir - red)))),
    5: ("GEMI", 2, lambda nir, red: (lambda eta: eta * (1 - 0.25 * eta) - (red - 0.125) / (1 - red))(
        (2 * (nir ** 2 - red ** 2) + 1.5 * nir + 0.5 * red) / (nir + red + 0.5))),
    6: ("PVI", 4, lambda nir, red, a, b: (nir - a * red - b) / np.sqrt(1 + a * a)),
    7: ("GVITM", 6, lambda b1, b2, b3, b4, b5, b7: -0.2848 * b1 - 0.2435 * b2 - 0.5436 * b3 + 0.7243 * b4 + 0.0840 * b5
        - 1.1800 * b7),
    9: ("VARI", 3, lambda red, green, blue: (green - red) / (green + red - blue)),
    10: ("GNDVI", 2, lambda nir, green: (nir - green) / (nir + green)),
    11: ("SR", 2, lambda nir, red: nir / red),
    12: ("NDVIre", 2, lambda nir, edge: (nir - edge) / (nir + edge)),
    13: ("SRre", 2, lambda nir, edge: nir / edge),
    15: ("RTVICore", 3, lambda nir, edge, green: 100 * (nir - edge) - 10 * (nir - green)),
    16: ("CIre", 2, lambda nir, edge: nir / edge - 1),
    17: ("CIg", 2, lambda nir, green: nir / green - 1),
    19: ("EVI", 3, lambda nir, red, blue: 2.5 * (nir - red) / (nir + 6 * red - 7.5 * blue + 1)),
    20: ("Iron oxide", 2, lambda red, blue: red / blue),
    21: ("Ferrous minerals", 2, lambda swir, nir: swir / nir),
    22: ("Clay minerals", 2, lambda a, b: a / b),
    23: ("WNDWI", 4, lambda green, nir, swir, alpha: (green - alpha * nir - (1 - alpha) * swir)
         / (green + alpha * nir + (1 - alpha) * swir)),
}
# How many of each method's values are band ids; the rest are constants written in the same string.
_BANDS_IN = {2: 2, 3: 2, 6: 2, 23: 3}


def BandArithmetic(raster, band_ids, method=1):
    """One of the Band Arithmetic raster function's indexes by its method code, over the bands ``band_ids`` names —
    ``"5 4"`` — in the order the method documents, constants following the bands (``"4 3 0.5"`` for SAVI).

    Refused: 0, a user-defined expression, whose syntax the page shows only by one example; 8, Sultan's formula, which
    answers three bands; 14, MTVI2, whose formula the page's text renders garbled; 18, NDWI, whose band order the page
    gives as NIR then Green while the formula reads Green first.

    INFERRED: GVITM's six ids are Bands 1, 2, 3, 4, 5 and 7 of the formula, in that order; WNDWI's α defaults to 0.5,
    as the page says, when only three ids are given."""
    code = int(method)
    if code not in _METHODS:
        reasons = {0: "a user-defined expression's syntax is shown only by one example",
                   8: "Sultan's formula answers three bands", 14: "the page's text renders the formula garbled",
                   18: "the page's band order and formula disagree"}
        _refuse("BandArithmetic", "method", method, reasons.get(code, "it is not one of the documented codes"))
    name, count, formula = _METHODS[code]
    parts = [p for p in str(band_ids).replace(",", " ").split() if p]
    if code == 23 and len(parts) == 3:
        parts.append("0.5")
    if len(parts) != count:
        raise ValueError(f"BandArithmetic {name} takes {count} values in band_ids; {len(parts)} were given.")
    bands_in = _BANDS_IN.get(code, count)
    values, grid = _band_values(raster, [int(float(p)) for p in parts[:bands_in]], "BandArithmetic")
    with np.errstate(all="ignore"):
        return _index_raster(formula(*values, *[float(p) for p in parts[bands_in:]]), grid)


# ---- bands


def CompositeBand(raster):
    """The rasters' bands, in order, as one raster of several bands; every input must lie on one grid (cells of the same
    size, lined up and covering the same extent), since resampling is not built."""
    inputs = raster if isinstance(raster, (list, tuple)) else [raster]
    bands = [_open_bands(r, "CompositeBand") for r in inputs]
    grid = bands[0]._grid
    for b in bands[1:]:
        if not b._grid.same(grid):
            _refuse("CompositeBand", "raster", b, "rasters on different grids need resampling")
    return _Bands([a for b in bands for a in b._arrays], [m for b in bands for m in b._masks], grid)


def ExtractBand(raster, band_ids=None, band_names=None, band_wavelengths=None, missing_band_action=None,
                wavelength_match_tolerance=None):
    """The bands ``band_ids`` names, in that order: one band answers a Raster, several a raster of several bands.

    ``band_names`` names bands by the raster's bandNames, in that order.

    Refused: ``band_wavelengths``, which needs band metadata grapy's rasters do not carry; a band id or name the raster
    does not have, since BestMatch's choice of a substitute is not specified.

    INFERRED: band ids are one-based, as every other arcpy.ia band id is; the page's example passes [1, 2].
    ``band_names`` is a list of names, or one name (the page gives no format); names match without regard to case; the
    output keeps the names of a raster whose bands are named."""
    if _given(band_names) and _given(band_ids):
        raise ValueError("ExtractBand takes band_ids or band_names, not both.")
    if _given(band_names):
        from ._raster import _band_number
        source = _open_bands(raster, "ExtractBand")
        names = band_names if isinstance(band_names, (list, tuple)) else [band_names]
        try:
            band_ids = [_band_number(str(n), source.bandNames, "ExtractBand's raster") for n in names]
        except ValueError as error:
            _refuse("ExtractBand", "band_names", band_names, f"{error} BestMatch's substitute is not specified")
        raster = source
    if _given(band_wavelengths):
        _refuse("ExtractBand", "band_wavelengths", band_wavelengths, "wavelengths are metadata grapy's rasters do not carry")
    bands = _open_bands(raster, "ExtractBand")
    ids = band_ids if isinstance(band_ids, (list, tuple)) else [band_ids]
    if not ids or ids == [None]:
        raise ValueError("ExtractBand needs band_ids.")
    for i in ids:
        if not 1 <= int(i) <= bands.bandCount:
            _refuse("ExtractBand", "band_ids", i, f"the raster has {bands.bandCount} bands, and BestMatch's substitute is "
                    "not specified")
    if len(ids) == 1:
        return bands._band(int(ids[0]))
    return _Bands([bands._arrays[int(i) - 1] for i in ids], [bands._masks[int(i) - 1] for i in ids], bands._grid,
                  [bands.bandNames[int(i) - 1] for i in ids] if bands._names else None)


# ---- local statistics across rasters, answered by arcpy.sa.CellStatistics


def _same_grid_or_intersection(rasters, extent_type, cellsize_type, tool):
    """The extent and cell size choices: the same answer whatever they are when every input lies on one grid; otherwise
    only IntersectionOf over cells of one size, which is how grapy aligns rasters."""
    opened = [as_raster(r, tool) if isinstance(r, str) else r for r in rasters]
    grids = [r._grid for r in opened if isinstance(r, Raster)]
    if all(grids[0].same(g) for g in grids[1:]):
        return opened
    if str(extent_type or "FirstOf") != "IntersectionOf":
        _refuse(tool, "extent_type", extent_type, "rasters on different grids are built only for IntersectionOf")
    widths = {round(g.width, 9) for g in grids}
    if len(widths) != 1:
        _refuse(tool, "cellsize_type", cellsize_type, "rasters of different cell sizes need resampling")
    return opened


def _local(name, statistic, doc):
    def tool(rasters, extent_type="FirstOf", cellsize_type="FirstOf", ignore_nodata=False, process_as_multiband=False):
        if process_as_multiband:
            _refuse(name, "process_as_multiband", process_as_multiband)
        opened = _same_grid_or_intersection(list(rasters), extent_type, cellsize_type, name)
        return CellStatistics(opened, statistic, "DATA" if ignore_nodata else "NODATA")
    tool.__name__ = tool.__qualname__ = name
    tool.__doc__ = (f"arcpy.ia.{name}: {doc}, cell by cell — arcpy.sa.CellStatistics' {statistic}. NoData anywhere makes "
                    "the cell NoData unless ``ignore_nodata`` is True.")
    return tool


Max = _local("Max", "MAXIMUM", "the largest value across the rasters")
Min = _local("Min", "MINIMUM", "the smallest value across the rasters")
Mean = _local("Mean", "MEAN", "the mean across the rasters")
Median = _local("Median", "MEDIAN", "the median across the rasters")
Sum = _local("Sum", "SUM", "the sum across the rasters")
Range = _local("Range", "RANGE", "the largest less the smallest value across the rasters")
StdDev = _local("StdDev", "STD", "the standard deviation across the rasters")
Majority = _local("Majority", "MAJORITY", "the most frequent value across the rasters")
Minority = _local("Minority", "MINORITY", "the least frequent value across the rasters")
Variety = _local("Variety", "VARIETY", "the number of distinct values across the rasters")


def Arithmetic(raster1, raster2, operation_type="Plus", extent_type="FirstOf", cellsize_type="FirstOf"):
    """Two rasters (or a raster and a number) combined cell by cell: Plus, Minus, Multiply, Divide or Power — arcpy.sa's
    Plus, Minus, Times, Divide and Power.

    Refused: Mode, whose answer for two different values is not specified.

    INFERRED: NoData in either input is NoData, arcpy's general rule; Divide divides two integers as arcpy.sa.Divide
    documents (an integer division)."""
    operations = {"PLUS": Plus, "MINUS": Minus, "MULTIPLY": Times, "DIVIDE": Divide, "POWER": Power}
    kind = str(operation_type or "Plus").upper()
    if kind not in operations:
        _refuse("Arithmetic", "operation_type", operation_type, "the answer of Mode for two different values is not specified")
    rasters = [r for r in (raster1, raster2) if isinstance(r, (Raster, str))]
    _same_grid_or_intersection(rasters, extent_type, cellsize_type, "Arithmetic")
    return operations[kind](raster1, raster2)


def Remap(raster, input_ranges=None, output_values=None, no_data_ranges=None, allow_unmatched=True, replacement_value=None):
    """Values remapped by ranges, each from (inclusive) to (exclusive): ``input_ranges`` ``[from, to, from, to, …]`` with
    one output value each; values in ``no_data_ranges`` become NoData; unmatched values keep their value, or — with
    ``allow_unmatched`` False — become ``replacement_value``, or NoData when there is none.

    INFERRED: a value in a NoData range is NoData even if an input range also holds it; of overlapping input ranges the
    first listed wins; the output is integer when the input and every output and replacement value are integers."""
    from ._raster import as_raster
    source = as_raster(raster, "Remap")
    ranges = list(input_ranges or [])
    outputs = list(output_values or [])
    if len(ranges) % 2 or len(ranges) // 2 != len(outputs):
        raise ValueError("Remap: input_ranges are pairs, one output value for each pair.")
    a = source._array.astype(np.float64)
    out = np.full(a.shape, np.nan)
    done = np.zeros(a.shape, bool)
    for (low, high), value in zip(zip(ranges[0::2], ranges[1::2]), outputs):
        hit = (a >= float(low)) & (a < float(high)) & ~done
        out[hit] = float(value)
        done |= hit
    nodata = source._mask.copy()
    empty = list(no_data_ranges or [])
    for low, high in zip(empty[0::2], empty[1::2]):
        nodata |= (a >= float(low)) & (a < float(high))
    if allow_unmatched:
        out = np.where(done, out, a)
    elif _given(replacement_value):
        out = np.where(done, out, float(replacement_value))
    else:
        nodata |= ~done
    values = [*outputs, *([replacement_value] if _given(replacement_value) else [])]
    integer = source.isInteger and all(float(v).is_integer() for v in values)
    out = np.where(nodata, 0, out)
    return Raster._of(out.astype(np.int32) if integer else out, nodata, source._grid)


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
})


# ---- the image tools' refusals, shared with arcpy.ia

_IA_GROUP_3 = "it needs a pretrained model or a deep learning framework, which a user tool on this server does not have " \
           "(ADR-224 group 3)"
_IA_GROUP_2 = "it needs a data model this server does not hold yet (ADR-224 group 2)"
_IA_UNSPECIFIED = "its algorithm is named or described but not specified closely enough to reproduce"
_IMAGE_REFUSED = {name: _IA_GROUP_3 for name in (
    "ClassifyObjectsUsingDeepLearning", "ClassifyPixelsUsingDeepLearning", "ComputeAccuracyForObjectDetection",
    "ComputeAccuracyForPixelClassification", "DeepLearningModelToEcd", "DetectChangeUsingDeepLearning",
    "DetectObjectsUsingDeepLearning", "DetectObjectsUsingText", "ExportTrainingDataForDeepLearning",
    "ExtractFeaturesUsingAIModels", "NonMaximumSuppression", "TrainDeepLearningModel", "TrainUsingAutoDL",
    "TranslatePixelsUsingDeepLearning",
)}
_IMAGE_REFUSED.update({name: _IA_GROUP_2 for name in (
    "AggregateMultidimensionalRaster", "AnalyzeChangesUsingCCDC", "AnalyzeChangesUsingLandTrendr", "Anomaly",
    "DimensionalMovingStatistics", "FindArgumentStatistics", "ArgStatistics", "GenerateMultidimensionalAnomaly",
    "GenerateTrend", "GenerateTrendRaster", "InterpolateRasterByDimension", "MultidimensionalPrincipalComponents",
    "MultidimensionalRasterCorrelation", "PredictUsingTrend", "PredictUsingTrendRaster", "RasterToXarray",
    "XarrayToRaster", "TrendToRGB", "ComputeChange", "ComputeChangeRaster", "DetectChangeUsingChangeAnalysis",
    "DetectChangeUsingChangeAnalysisRaster", "InterpolateFromSpatiotemporalPoints",
    "ApplyCoregistration", "ApplyGeometricTerrainCorrection", "ApplyOrbitCorrection", "ApplyRadiometricCalibration",
    "ApplyRadiometricTerrainFlattening", "ApplyComplexDataFilter", "ComputeCoherence", "ComputeSARIndices",
    "ConvertSARUnits", "Deburst", "Despeckle", "DetectBrightOceanObjects", "DetectDarkOceanAreas", "DownloadOrbitFile",
    "ExtractOceanWinds", "GenerateInterferogram", "GenerateMaskedSARRaster", "GenerateMultitemporalCoherence",
    "GenerateRadiometricTerrainCorrectedData", "Multilook", "RadarCalibration", "RemoveThermalNoise",
    "S1RadiometricCalibration", "S1ThermalNoiseRemoval", "UnwrapPhase",
    "ConvertVideoMetadata", "ExtractVideoFramesToImages", "GenerateVideoMetadataStationary", "VideoMetadataToFeatureClass",
    "VideoMultiplexer", "RasterCollection",
)})
_IMAGE_REFUSED.update({name: _IA_UNSPECIFIED for name in (
    "TrainIsoClusterClassifier", "TrainKNearestNeighborClassifier", "TrainMaximumLikelihoodClassifier",
    "TrainRandomTreesClassifier", "TrainRandomTreesRegressionModel", "TrainSupportVectorMachineClassifier",
    "ClassifyRaster", "Classify", "MLClassify", "PredictUsingRegressionModel", "SegmentMeanShift", "SegMeanShift",
    "Threshold", "Pansharpen", "TasseledCap", "RegionGrow",
)})
_IMAGE_REFUSED.update({
    "MTVI2": "the formula on its page is rendered garbled in the text, and grapy does not guess where the root falls",
    "Stretch": "its pages describe MinMax, StdDev and PercentClip without giving their formulas",
    "Convolution": "the page maps only two of its 22 kernel codes to kernels, and edge handling is not documented",
    "Percentile": "which interpolation AUTO_DETECT's NEAREST and LINEAR use between two values is not specified",
    "Sultan": "it answers three bands from an ordering of five band ids the page does not spell out",
    "GNDVI": "its arcpy.ia page could not be read (404 on 2026-10-29), so its parameter names and defaults are unknown",
    "MNDWI": "its arcpy.ia page could not be read (404 on 2026-10-29), so its parameter names and defaults are unknown",
    "FloatDivide": "its reference page could not be read (404 on 2026-10-29)",
    "FloorDivide": "its reference page could not be read (404 on 2026-10-29)",
    "InList": "its reference page could not be read (404 on 2026-10-29)",
})
_REFUSED.update({k: v for k, v in _IMAGE_REFUSED.items() if k not in _REFUSED})


# ---- round four (ADR-224): the remaining names classified, and the few found cleanly documented built

from ._geometry import Point as _GeometryPoint  # noqa: E402
from ._reference import Extent as _ReferenceExtent  # noqa: E402

Raster = Raster  # noqa: PLW0127 — arcpy.sa.Raster is arcpy.Raster
Point = _GeometryPoint  # arcpy.sa.Point is arcpy.Point
Extent = _ReferenceExtent  # arcpy.sa.Extent is arcpy.Extent


def Corridor(in_distance_raster1, in_distance_raster2):
    """The sum of two accumulative cost rasters, as the tool's summary states; floating point.

    INFERRED: NoData in either input is NoData, arcpy's general rule; the page is silent."""
    _refuse_environment("arcpy.sa.Corridor")
    a, b = as_raster(in_distance_raster1, "Corridor"), as_raster(in_distance_raster2, "Corridor")
    return _combine([a, b], lambda x, y: np.asarray(x, dtype=np.float64) + y, keep_integer=False, tool="Corridor")


def FuzzyOverlay(in_rasters, overlay_type=None, gamma=0.9):
    """Fuzzy memberships combined cell by cell: ``AND`` takes the minimum, ``OR`` the maximum, as documented.

    Refused: ``PRODUCT``, ``SUM`` and ``GAMMA``, which the page describes only in words (*a decrease function*, *an
    increase function*); and an omitted ``overlay_type``, whose default the page does not state.

    INFERRED: NoData in any input is NoData."""
    _refuse_environment("arcpy.sa.FuzzyOverlay")
    kind = str(overlay_type).upper() if _given(overlay_type) else None
    if kind not in ("AND", "OR"):
        _refuse("FuzzyOverlay", "overlay_type", overlay_type or "(empty)",
                "only AND and OR are given exactly; the default is not documented")
    arrays, mask, grid = _many(list(in_rasters), "FuzzyOverlay")
    stack = np.stack([a.astype(np.float64) for a in arrays])
    value = stack.min(axis=0) if kind == "AND" else stack.max(axis=0)
    return Raster._of(np.where(mask, 0, value), mask, grid)


def TransposeBits(raster, input_bit_positions=(12, 13), output_bit_positions=(0, 1), constant_fill_value=0,
                  fill_raster=None):
    """Bits moved: output bit ``output_bit_positions[k]`` takes input bit ``input_bit_positions[k]``; every other output
    bit comes from ``constant_fill_value``, or from ``fill_raster`` when one is given.

    INFERRED: bits are numbered from 0, the least significant; ``fill_raster`` takes precedence over the constant when
    both are given (the page does not say); the output is a 32-bit integer; a floating-point input is refused."""
    _refuse_environment("arcpy.sa.TransposeBits")
    source = as_raster(raster, "TransposeBits")
    if not source.isInteger:
        _refuse("TransposeBits", "raster", raster, "the bits of a floating-point raster are not defined")
    ins, outs = [int(i) for i in input_bit_positions], [int(o) for o in output_bit_positions]
    if len(ins) != len(outs):
        raise ValueError("TransposeBits: input_bit_positions and output_bit_positions pair up one to one.")
    a = source._array.astype(np.int64)
    mask = source._mask.copy()
    if _given(fill_raster):
        fill = as_raster(fill_raster, "TransposeBits")
        if not fill._grid.same(source._grid):
            _refuse("TransposeBits", "fill_raster", fill_raster, "a fill raster on another grid needs resampling")
        base = fill._array.astype(np.int64)
        mask |= fill._mask
    else:
        base = np.full(a.shape, int(constant_fill_value or 0), np.int64)
    cleared = 0
    for o in outs:
        cleared |= 1 << o
    out = base & ~cleared
    for i, o in zip(ins, outs):
        out |= ((a >> i) & 1) << o
    return Raster._of(_wrap32(out).astype(np.int32), mask, source._grid)


_POINT_STATS = ("MEAN", "MAJORITY", "MAXIMUM", "MEDIAN", "MINIMUM", "MINORITY", "RANGE", "STD", "SUM", "VARIETY")


def _statistic_of(values, kind):
    """A statistic of a list of values as Point Statistics documents them: majority ties go to the lower value, the
    median of an even count is the lower of the two middle values. INFERRED: minority ties also go to the lower value;
    STD is the population standard deviation."""
    v = np.sort(np.asarray(values, dtype=np.float64))
    if kind == "MEAN":
        return v.mean()
    if kind in ("MAJORITY", "MINORITY"):
        uniques, counts = np.unique(v, return_counts=True)
        target = counts.max() if kind == "MAJORITY" else counts.min()
        return uniques[counts == target].min()
    if kind == "MAXIMUM":
        return v.max()
    if kind == "MEDIAN":
        return v[(len(v) - 1) // 2]
    if kind == "MINIMUM":
        return v.min()
    if kind == "RANGE":
        return v.max() - v.min()
    if kind == "STD":
        return v.std()
    if kind == "SUM":
        return v.sum()
    return float(len(np.unique(v)))


def PointStatistics(in_point_features, field, cell_size=None, neighborhood=None, statistics_type="MEAN"):
    """For each cell, a statistic of the field over the points within a neighbourhood of the cell's centre — by default
    a 3 × 3-cell rectangle; NoData where none falls within.

    INFERRED: a point exactly on the neighbourhood's edge is within; the grid's top-left corner is the points' extent's;
    see ``_statistic_of`` for ties and STD."""
    _refuse_environment("arcpy.sa.PointStatistics")
    kind = str(statistics_type or "MEAN").upper()
    if kind not in _POINT_STATS:
        _refuse("PointStatistics", "statistics_type", statistics_type)
    grid, _, _ = _density_grid(in_point_features, cell_size, "PointStatistics")
    points = [(s, w) for s, w in _population(in_point_features, field, "PointStatistics")]
    xs = np.array([p.x for p, _ in points])
    ys = np.array([p.y for p, _ in points])
    values = np.array([w for _, w in points])
    neighborhood = neighborhood if neighborhood is not None else NbrRectangle(3, 3, "CELL")
    in_cells = str(getattr(neighborhood, "units", "CELL")).upper() == "CELL"
    cx, cy = grid.centres()
    out = np.zeros((grid.rows, grid.columns))
    empty = np.ones((grid.rows, grid.columns), bool)
    for i, y in enumerate(cy):
        for j, x in enumerate(cx):
            dx, dy = xs - x, ys - y
            if isinstance(neighborhood, NbrRectangle):
                w = float(neighborhood.width) * (grid.width if in_cells else 1.0)
                h = float(neighborhood.height) * (grid.height if in_cells else 1.0)
                within = (np.abs(dx) <= w / 2) & (np.abs(dy) <= h / 2)
            elif isinstance(neighborhood, NbrCircle):
                r = float(neighborhood.radius) * (grid.width if in_cells else 1.0)
                within = dx * dx + dy * dy <= r * r
            else:
                _refuse("PointStatistics", "neighborhood", type(neighborhood).__name__,
                        "only NbrRectangle and NbrCircle are built")
            if within.any():
                out[i, j] = _statistic_of(values[within], kind)
                empty[i, j] = False
    return Raster._of(np.where(empty, 0, out), empty, grid)


def LineStatistics(in_polyline_features, field, cell_size=None, search_radius=None, statistics_type="MEAN"):
    """For each cell, a statistic of the field over the lines within a circle round the cell's centre — five cells by
    default. ``MEAN`` weights each line by its length inside the circle; ``LENGTH`` is that length; ``MAXIMUM``,
    ``MINIMUM``, ``RANGE`` and ``VARIETY`` take every line with any length inside it. NoData where none does.

    Refused: ``MAJORITY``, ``MEDIAN`` and ``MINORITY``, whose length weighting the page does not define.

    INFERRED: a line that only touches the circle has no length inside it and does not count."""
    _refuse_environment("arcpy.sa.LineStatistics")
    kind = str(statistics_type or "MEAN").upper()
    if kind not in ("MEAN", "LENGTH", "MAXIMUM", "MINIMUM", "RANGE", "VARIETY"):
        _refuse("LineStatistics", "statistics_type", statistics_type, "its length weighting is not defined")
    import shapely
    from shapely.strtree import STRtree
    grid, _, _ = _density_grid(in_polyline_features, cell_size, "LineStatistics")
    lines = _population(in_polyline_features, field, "LineStatistics")
    r = float(search_radius) if _given(search_radius) else 5 * grid.width
    tree = STRtree([s for s, _ in lines])
    cx, cy = grid.centres()
    out = np.zeros((grid.rows, grid.columns))
    empty = np.ones((grid.rows, grid.columns), bool)
    for i, y in enumerate(cy):
        for j, x in enumerate(cx):
            found = []
            for k in tree.query(shapely.box(x - r, y - r, x + r, y + r)):
                shape, value = lines[int(k)]
                length = _length_in_circle(shape, x, y, r)
                if length > 0:
                    found.append((length, value))
            if not found:
                continue
            empty[i, j] = False
            lengths = np.array([f[0] for f in found])
            values = np.array([f[1] for f in found])
            out[i, j] = {"MEAN": lambda: (lengths * values).sum() / lengths.sum(), "LENGTH": lambda: lengths.sum(),
                         "MAXIMUM": lambda: values.max(), "MINIMUM": lambda: values.min(),
                         "RANGE": lambda: values.max() - values.min(),
                         "VARIETY": lambda: float(len(np.unique(values)))}[kind]()
    return Raster._of(np.where(empty, 0, out), empty, grid)


# ---- every remaining name, refused with its specific reason (checked 2026-10-29 against Esri's pages)

_RESCALE = ("it is a transformation function of RescaleByFunction, which is refused: the formulas are published, "
            "but which values the rescaling's minimum and maximum are taken over, and how shape parameters are fitted "
            "when not given, are not specified")
_FUZZY = ("the Fuzzy Membership pages give the membership functions only as images (checked: the tool page and "
          "FuzzyGaussian's class page)")
_TOPO = "it is an input class of TopoToRaster, which is refused (its algorithm is not specified)"
_404 = "its reference page could not be read (404 on 2026-10-29)"
_SEGMENT = "it works on the segmented rasters of SegmentMeanShift, which is refused (its algorithm is not specified)"
_REFUSED.update({name: _RESCALE for name in (
    "RescaleByFunction", "TfExponential", "TfGaussian", "TfLarge", "TfLinear", "TfLogarithm", "TfLogisticDecay",
    "TfLogisticGrowth", "TfMSLarge", "TfMSSmall", "TfNear", "TfPower", "TfSmall", "TfSymmetricLinear")})
_REFUSED.update({name: _FUZZY for name in (
    "FuzzyMembership", "FuzzyGaussian", "FuzzyLarge", "FuzzyLinear", "FuzzyMSLarge", "FuzzyMSSmall", "FuzzyNear",
    "FuzzySmall")})
_REFUSED.update({name: _TOPO for name in (
    "TopoBoundary", "TopoCliff", "TopoCoast", "TopoContour", "TopoExclusion", "TopoLake", "TopoPointElevation",
    "TopoSink", "TopoStream")})
_REFUSED.update({name: _SEGMENT for name in ("ComputeSegmentAttributes", "RemoveRasterSegmentTilingArtifacts")})
_REFUSED.update({name: _404 for name in ("HeatIndex", "WindChill", "UnitConversion", "Resample", "Speckle")})
_REFUSED.update({
    "KernelDensity": "its page gives the density formula and the default search radius only as images — alt text "
                     "'Predicted density formula' and 'Formula for calculating the default search radius for kernel "
                     "density', with no LaTeX or MathML — and its text scales the result by the number of points, which "
                     "cannot be reconciled with a population inside the kernel without the formula itself",
    "CalculateKernelDensityRatio": "it divides two kernel densities, and KernelDensity is refused (formula published "
                                   "only as an image)",
    "SpaceTimeKernelDensity": "it extends kernel density, whose formula Esri publishes only as an image",
    "KrigingModelOrdinary": "it is a model class of Kriging, which is refused (its algorithm is not specified)",
    "KrigingModelUniversal": "it is a model class of Kriging, which is refused (its algorithm is not specified)",
    "WeightedOverlay": "its page shows the rounding to integer only by one example (2.25 to 2) and does not explain the "
                       "evaluation scale's three values",
    "WOTable": "it holds WeightedOverlay's criteria, and WeightedOverlay is refused (rounding shown only by example)",
    "PrincipalComponents": "the page does not say whether the covariance or correlation matrix is used, how components "
                           "are scaled, or how eigenvector signs are chosen",
    "Filter": "the page does not give the LOW and HIGH kernels' coefficients or edge handling",
    "ZonalHistogram": "the page does not name the output table's fields",
    "ZonalGeometryAsTable": "the page does not name its fields beyond ORIENTATION or define PERIMETER and THICKNESS",
    "TopographicPositionIndex": "the page does not say whether the centre cell is in the mean, nor how edges and NoData "
                                "are treated",
    "SurfaceAreaRatio": "the page does not give the 3D surface area computation; it defers to another topic",
    "StreamToFeature": "the page does not name the output fields, number the nodes, or say how lines are traced",
    "FocalFlow": "the page does not say which neighbour maps to which bit, and its two threshold statements disagree",
    "RasterToWeightedPoints": "its default point placement is stated two ways that conflict, and distance_from_cell_center "
                              "has no default",
    "CombinatorialAnd": "the page does not say how the unique combination values are numbered, nor how NoData is treated",
    "CombinatorialOr": "Combinatorial And's page, its sibling, does not say how combination values are numbered",
    "CombinatorialXOr": "Combinatorial And's page, its sibling, does not say how combination values are numbered",
    "ComputeConfusionMatrix": "the output table's fields are shown only by example, and the IoU it mentions is not in it",
    "CreateRandomRaster": "its generator (ACM algorithm 599) is named but not specified, and the page says even a fixed "
                          "seed does not reproduce a raster",
    "CutFill": _NO_RAT + " — the VOLUME and AREA of each region",
})


GVITM = _index("GVITM", ["band1_id", "band2_id", "band3_id", "band4_id", "band5_id", "band7_id"],
               {"band1_id": 1, "band2_id": 2, "band3_id": 3, "band4_id": 4, "band5_id": 5, "band7_id": 7},
               lambda b1, b2, b3, b4, b5, b7: -0.2848 * b1 - 0.2435 * b2 - 0.5436 * b3 + 0.7243 * b4 + 0.084 * b5 - 1.18 * b7,
               "Landsat TM's green vegetation index: −0.2848 B1 − 0.2435 B2 − 0.5436 B3 + 0.7243 B4 + 0.084 B5 − 1.18 B7.")


def SubsetBands(raster, method="BY_IDS", bands=None, missing_band_action="BestMatch", exclude_bad_bands=False):
    """The bands named by their one-based ids — ``"1 3"``, or ranges such as ``"2-4 6"`` — kept in the input's band
    order, as the page says the output keeps it.

    Refused: ``BY_NAMES``, whose way of writing several names in ``bands`` the page does not give; ``BY_WAVELENGTHS``
    and ``exclude_bad_bands`` (band metadata grapy's rasters do not carry);
    an id the raster does not have (BestMatch's substitute is chosen by wavelength, which is not held); and an empty
    ``bands``, whose meaning the page does not give.

    INFERRED: a range includes both its ends."""
    if str(method or "BY_IDS").upper() != "BY_IDS":
        _refuse("SubsetBands", "method", method, "how several names are written in bands is not documented, and "
                "wavelengths are metadata grapy's rasters do not carry")
    if exclude_bad_bands:
        _refuse("SubsetBands", "exclude_bad_bands", exclude_bad_bands, "bad-band flags are metadata grapy's rasters do not carry")
    if not _given(bands):
        _refuse("SubsetBands", "bands", "(empty)", "the page does not say what an empty selection answers")
    source = _open_bands(raster, "SubsetBands")
    wanted = set()
    for part in str(bands).split():
        low, _, high = part.partition("-")
        wanted.update(range(int(low), int(high or low) + 1))
    for number in wanted:
        if not 1 <= number <= source.bandCount:
            _refuse("SubsetBands", "bands", number, f"the raster has {source.bandCount} bands, and BestMatch needs wavelengths")
    chosen = sorted(wanted)
    if len(chosen) == 1:
        return source._band(chosen[0])
    return _Bands([source._arrays[i - 1] for i in chosen], [source._masks[i - 1] for i in chosen], source._grid,
                  [source.bandNames[i - 1] for i in chosen] if source._names else None)


_REFUSED.update({
    "EucBackDirection": "the page says each value points to the next cell on the path back to the source and does not "
                        "say whether that is one of the eight neighbours or the bearing to the source",
    "Statistics": "its page states the defaults of stat_type and fill_no_data_only two ways that disagree, and is silent "
                  "on edges",
    "Clip": "the page does not say whether cells are kept by their centres or what happens outside the area of interest",
    "Grayscale": "the page does not give the formula or say how the weights are normalised",
    "Mask": "the page does not say whether included_ranges include their ends or how they combine with no_data_values",
})


_REFUSED.update({
    "FlowDistance": "the page names the vertical and horizontal distances and the MFD statistics without giving their "
                    "formulas",
    "LocateDepressions": "the page does not give its fill or thresholds and does not name the depression id field",
    "DeriveContinuousFlow": "the page does not say how flats and spurious sinks are routed, nor give MFD's partition "
                            "function",
    "StorageCapacity": "the page does not say how area and volume are computed (planar or surface area) nor give the "
                       "default increment",
    "TopHatTransform": "the page does not name the morphological operation, its structuring element or edge handling",
    "GeomorphonLandforms": "the page does not give the ternary pattern computation or the mapping of its 498 patterns "
                           "to landforms",
})

# Names examined and not refused, but not built in this round, each with why.
_NOT_YET = {name: "the solar radiation model is published as text (How solar radiation is calculated), but its "
                  "per-direction horizon sampling was not built in this round" for name in (
    "AreaSolarRadiation", "PointsSolarRadiation", "RasterSolarRadiation", "FeatureSolarRadiation",
    "SolarRadiationGraphics", "TimeMultipleDays", "TimeSpecialDays", "TimeWholeYear", "TimeWithinDay")}
_NOT_YET.update({name: "FocalStatistics builds rectangle, circle and annulus neighbourhoods; this one was not built in "
                       "this round" for name in ("NbrIrregular", "NbrWedge", "NbrWeight")})


# ---- round five (ADR-224): the last names read and classified


def Buffered(raster):
    """The raster itself: Buffered only keeps a raster's pixel block in memory so an expression that reads it several
    times is faster, and changes no value, so on grapy's in-memory rasters it answers the same raster."""
    return as_raster(raster, "Buffered")


class NbrIrregular:
    """arcpy.sa.NbrIrregular: a neighbourhood read from a kernel file — a first line of width and height, then rows of
    values in the neighbourhood's layout, where 0 excludes a cell and any other value includes it.

    Refused: a kernel of even width or height, since the page does not say which cell is the centre."""

    def __init__(self, inKernelFile):
        self.inKernelFile = inKernelFile

    def _footprint(self, grid):
        with open(self.inKernelFile, encoding="utf-8") as kernel:
            numbers = kernel.read().split()
        width, height = int(float(numbers[0])), int(float(numbers[1]))
        values = [float(v) for v in numbers[2:2 + width * height]]
        if len(values) != width * height:
            raise ValueError(f"NbrIrregular: the kernel file holds {len(values)} values, not {width} x {height}.")
        if width % 2 == 0 or height % 2 == 0:
            _refuse("NbrIrregular", "inKernelFile", self.inKernelFile, "the centre of an even-sized kernel is not documented")
        return np.array(values).reshape(height, width) != 0


_DISPLAY = "it makes a display rendering, and its page does not define the output values (it defers to a raster function topic)"
_UNSPEC = "its page describes the algorithm in words and defers the method to another topic or a citation"
_NOT_FOUND = "its reference page could not be read (404 on 2026-10-29)"
_SIGNATURE_FILE = ("it reads or writes the ASCII signature (.gsg) file, whose layout is not documented (checked on Iso "
                   "Cluster's page)")
_CLASSIFIER_FILE = "it reads a classifier definition (.ecd) from the train classifier tools, which are refused"
_SPECTRAL = "it reads or writes spectral libraries (.esl, .sli) whose layout the page does not give"
_SOLAR = ("How solar radiation is calculated gives the direct and diffuse formulas as text, but the horizon "
          "(hemispherical viewshed) algorithm, the sun-position equations and the sky map's zenith divisions are "
          "described in words or cited (Rich et al. 1994; Fu and Rich 2000)")
_REFUSED.update({name: _DISPLAY for name in (
    "Colormap", "Render", "ShadedRelief", "AspectSlope", "ContrastBrightness", "ColorspaceConversion")})
_REFUSED.update({name: _NOT_FOUND for name in (
    "ColormapToRGB", "VectorField", "VectorFieldRenderer", "RasterInfo", "ZonalRemap", "SpectralConversion",
    "ApplyEnvironment", "PixelBlock", "PixelBlockCollection", "Mensuration", "LinearUnmixing", "LabelBadBands",
    "ExportRasterFunctionTemplate", "ElevationVoidFill")})
_REFUSED.update({name: _SIGNATURE_FILE for name in ("CreateSignatures", "EditSignatures", "Dendrogram", "ClassProbability")})
_REFUSED.update({name: _SPECTRAL for name in ("ExtractSpectraFromImage", "ResampleLibrarySpectra")})
_REFUSED.update({name: _SOLAR for name in (
    "AreaSolarRadiation", "PointsSolarRadiation", "RasterSolarRadiation", "FeatureSolarRadiation",
    "SolarRadiationGraphics", "TimeMultipleDays", "TimeSpecialDays", "TimeWholeYear", "TimeWithinDay")})
_REFUSED.update({
    "NbrWedge": "the page does not say where the wedge's apex is, whether its boundary angles are inclusive, or how a "
                "start angle above the end angle wraps",
    "NbrWeight": "the page does not say how Focal Statistics combines the weighted values (weighted sum or mean)",
    "RasterCellIterator": "the page does not give the iteration order, edge behaviour or integer NoData, and new output "
                          "rasters come from RasterInfo, whose page could not be read (404)",
    "Apply": "it runs Pro's raster function engine by function name or raster function template (.rft.xml)",
    "Geometric": "it approximates the sensor model by a Polynomial or Projective transform within a pixel tolerance, "
                 "and the page does not say how that approximation is fitted; arcpy.management."
                 "CreateOrthoCorrectedRasterDataset orthorectifies an RPC raster or Frame Camera items by their exact "
                 "model (ADR-234 §5.5)",
    "DetectControlPoints": "it finds ground control point markers with two deep learning models trained by Esri, as its "
                           "page says, and grapy carries no deep learning model (ADR-224 group 3; ADR-234 §5.5)",
    "TransformImageShapes": "its input is the iShape field the deep learning tools write in image space, whose encoding "
                            "and image identifier the page does not document; grapy carries no deep learning tool to "
                            "write one (ADR-224 group 3; ADR-234 §5.5)",
    "ApparentReflectance": "it needs sensor metadata and the page defers its formula to a raster function topic",
    "StatisticsHistogram": "it attaches statistics and histogram metadata, which grapy's rasters do not carry",
    "Merge": "the page does not give the output extent and cell size rules or how NoData enters MEAN and SUM",
    "Reproject": "no resampling method is specified",
    "Gradient": "the forward differences are given, but not what the last row, column or slice gets",
    "GeometricMedian": "the page names stopping rules but not the algorithm, and states two defaults for cellsize_type",
    "RegionPixelCount": "the page does not say what each pixel gets, how maximum_region_size applies or how NoData is treated",
    "CreateBinaryMask": "the page does not define the flood fill, the expansion metric or the region connectivity",
    "CalculateCutFillVolume": "the page does not give the volume integration or how SURFACE's base is interpolated",
    "OptimalInterpolation": _UNSPEC,
    "ExtractWater": "it labels radar backscatter as water or land by a classifier its page does not name — no threshold, "
                    "statistic or training is given — though grapy holds SAR data (ADR-237)",
    "DetectImageAnomalies": "RXD and UTD are given, but not how the mean and covariance are estimated nor the scaling "
                            "to 0-1, and KMEANS is not defined",
    "ClassifyRasterUsingSpectra": "its methods (SAM, SID, SIDSAM, NS3) are described without formulas",
    "DetectTargetUsingSpectra": "its methods (SAM, SID, ACE, CEM, MF and others) are described without formulas",
    "ReduceSpectralBands": "MNF's noise covariance estimate and the decomposition are not specified",
    "LinearSpectralUnmixing": "the page does not name the solver or how its constraints are enforced",
    "InspectTrainingSamples": _CLASSIFIER_FILE,
    "GenerateTrainingSamplesFromSeedPoints": "the page does not give the region grow's connectivity or order",
    "IsoCluster": "its isodata algorithm (initial means, splits, merges) and signature file layout are not specified",
    "IsoClusterUnsupervisedClassification": "it runs Iso Cluster, whose isodata algorithm is not specified",
    "CreateAccuracyAssessmentPoints": "its random sampling has no documented seed and its output fields are not named",
    "UpdateAccuracyAssessmentPoints": "it updates Create Accuracy Assessment Points' output, whose fields are not named",
    "CreateNormalRaster": "it is seeded from the system clock with no documented seed or generator",
    "BandCollectionStats": "the page does not say whether covariance divides by n or n - 1",
    "RasterizeFeatures": "the page does not say whether a cell is assigned by its centre or by any feature it touches",
    "ValuePercentileContours": "the percentile definition and the output fields are not documented",
    "VolumePercentileContours": "the output polygons' fields are not documented",
    "ZonalCharacterization": "its output field types and computation details are deferred to How zonal statistics "
                             "tools work",
    "GenerateWeightedVoronoi": "the influence formulas are given but the output fields are not named",
    "MultiscaleSurfaceDeviation": "the page does not say whether the deviation is signed, whether the centre is in the "
                                  "mean, how scales follow from nonlinearity, or how edges are handled",
    "MultiscaleSurfaceDifference": "the page does not say whether the difference is signed, whether the centre is in "
                                   "the mean, how distances become cells, or how edges are handled",
    "MultiscaleSurfacePercentile": "the percentile definition, scale sequence, ties and edges are not specified",
    "DarcyFlow": "the page gives no Darcy velocity, head gradient or volume balance formula",
    "DarcyVelocity": "the page gives no formulas; it defers to How Darcy Flow and Darcy Velocity work",
    "ParticleTrack": "the page names no integration scheme and no velocity interpolation between cell centres",
    "PorousPuff": "the page gives the puff only by reference; its one equation is an image",
    "FeaturePreservingSmoothing": "the page does not say how the new value or the surface normals are computed",
    "GenerateBreachLines": "its cost and path search are only cited (Lindsay and Dhun 2015)",
    "GeodesicFlowDirection": "it needs the Greenlee lookup table and geodesic distance, neither published",
    "ValidateFlowDirection": "the page does not say how LoopID values are numbered",
    "AdjustRasterToStream": "the interpolation, V-shaped channel geometry and cell selection are not specified",
    "AdjustStreamToRaster": "the page describes the adjusted path but not how cells are selected or flow assigned",
    "DeriveStreamAsRaster": "its least-steep-uphill routing is not formally defined (neighbours, ties)",
    "DeriveStreamAsLine": "its routing is not formally defined and the output fields are not named",
    "LocateRegions": "its region growth, seed placement and selection are described in words only",
    "LeastCostCorridor": "the back-direction connection step and NoData are not specified",
    "CostConnectivity": "the spanning tree's construction, neighbours and PATHCOST are not specified",
    "OptimalRegionConnections": "the cost distance, spanning tree and entry points are not specified",
    "OptimalCorridorConnections": "the corridor construction and the shortest-path algorithm are not specified",
    "MulticriteriaOverlay": "its weighted overlay rescaling, quantifier-guided OWA and TOPSIS closeness are not given",
    "CreateColorComposite": "its channel expressions' NoData, division by zero and output type are not documented",
})
for _name in list(_NOT_YET):
    if _name in _REFUSED or _name == "NbrIrregular":
        _NOT_YET.pop(_name)


# ---- round six (ADR-224 §5.6): equivalents — the standard method of the literature where Esri's documentation does
# not define the answer. Each says so in its docstring, its run and its mark.

from ._runtime import equivalent as _equivalent  # noqa: E402


@_equivalent("the quartic (biweight) kernel density estimate with Silverman's rule-of-thumb bandwidth",
             "Silverman 1986, Density Estimation for Statistics and Data Analysis, eq. 4.5 and §3.4.2")
def KernelDensity(in_features, population_field, cell_size=None, search_radius=None, area_unit_scale_factor=None,
                  out_cell_values="DENSITIES", method="PLANAR", in_barriers=None):
    """EQUIVALENT: Silverman's (1986) quartic kernel estimate, eq. 4.5 — each point spreads its population over a disc
    of ``search_radius`` as ``3/(π r²) · (1 − (d/r)²)²``, so the volume under each point's kernel is its population, as
    Esri's page says; a cell's density is the sum over the points within the radius of its centre (NoData where there
    is none), times ``area_unit_scale_factor``. ``EXPECTED_COUNTS`` multiplies the density by the cell's area.

    The default radius is Silverman's rule of thumb in two dimensions, built from the quantities Esri's page names:
    ``0.9 · min(SD, Dm · √(1/ln 2)) · n^(−0.2)``, where SD is the standard distance from the mean centre, Dm the median
    distance to it, and n the number of points (or the sum of the population). Dm · √(1/ln 2) equals SD for points
    normally distributed in the plane (the distance from the centre then follows a Rayleigh law, whose median is
    σ√(2 ln 2) while SD is σ√2), which is how a median stands in for SD in Silverman's robust rule. With a population,
    the centre, SD and median are weighted by it.

    Refused: lines (Esri's line kernel is its own), barriers and the geodesic method.

    INFERRED: the grid's top-left corner is the points' extent's; a point exactly at the radius contributes nothing.
    """
    _refuse_environment("arcpy.sa.KernelDensity")
    if str(method or "PLANAR").upper() != "PLANAR":
        _refuse("KernelDensity", "method", method, "the geodesic kernel is not built")
    if _given(in_barriers):
        _refuse("KernelDensity", "in_barriers", in_barriers, "barriers are not built")
    from .analysis import _schema
    if _schema(in_features)[1] != "Point":
        _refuse("KernelDensity", "in_features", in_features, "only points are built; Esri's line kernel is its own")
    kind = str(out_cell_values or "DENSITIES").upper()
    if kind not in ("DENSITIES", "EXPECTED_COUNTS"):
        _refuse("KernelDensity", "out_cell_values", out_cell_values)
    grid, srid, _ = _density_grid(in_features, cell_size, "KernelDensity")
    points = _population(in_features, population_field, "KernelDensity")
    xs = np.array([p.x for p, _ in points])
    ys = np.array([p.y for p, _ in points])
    weights = np.array([w for _, w in points])
    r = float(search_radius) if _given(search_radius) else _silverman_radius(xs, ys, weights)
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx, cy)
    d2 = (gx[..., None] - xs) ** 2 + (gy[..., None] - ys) ** 2
    inside = d2 < r * r
    kernel = np.where(inside, 3.0 / (math.pi * r * r) * (1 - d2 / (r * r)) ** 2, 0.0)
    density = (kernel * weights).sum(axis=-1)
    if kind == "EXPECTED_COUNTS":
        density = density * grid.width * grid.height
    else:
        density = density * _area_unit(area_unit_scale_factor, srid, "KernelDensity")
    empty = ~inside.any(axis=-1)
    return Raster._of(np.where(empty, 0, density), empty, grid)


def _silverman_radius(xs, ys, weights):
    total = weights.sum()
    if total <= 0:
        raise ValueError("KernelDensity: the population sums to zero, so no default radius can be found.")
    mx, my = (weights * xs).sum() / total, (weights * ys).sum() / total
    d = np.hypot(xs - mx, ys - my)
    sd = math.sqrt((weights * d * d).sum() / total)
    order = np.argsort(d, kind="stable")
    cumulative = np.cumsum(weights[order])
    median = float(d[order][np.searchsorted(cumulative, total / 2.0)])
    spread = min(sd, median * math.sqrt(1 / math.log(2)))
    if spread <= 0:
        raise ValueError("KernelDensity: the points have no spread, so no default radius can be found.")
    return 0.9 * spread * total ** -0.2


@_equivalent("Otsu's threshold on a 256-bin histogram", "Otsu 1979, IEEE Trans. Systems, Man and Cybernetics 9(1)")
def Threshold(raster):
    """EQUIVALENT: Otsu's (1979) method, which Esri's Binary Thresholding page names: the cells' values are counted in 256
    equal bins over their range, and the threshold is the bin edge that maximises the between-class variance. Cells
    above it are 1, the rest 0; NoData stays NoData.

    INFERRED: of equal maxima, the lowest edge is taken; a constant raster is all 0."""
    _refuse_environment("arcpy.sa.Threshold")
    source = as_raster(raster, "Threshold")
    values = source._values().astype(np.float64)
    low, high = (values.min(), values.max()) if values.size else (0.0, 0.0)
    if high <= low:
        return Raster._of(np.zeros(source._array.shape, np.int32), source._mask.copy(), source._grid)
    counts, edges = np.histogram(values, bins=256, range=(low, high))
    centres = (edges[:-1] + edges[1:]) / 2
    weight0 = np.cumsum(counts)
    weight1 = weight0[-1] - weight0
    sum0 = np.cumsum(counts * centres)
    total = sum0[-1]
    with np.errstate(all="ignore"):
        mean0 = sum0 / weight0
        mean1 = (total - sum0) / weight1
        between = weight0 * weight1 * (mean0 - mean1) ** 2
    between = np.where(np.isfinite(between), between, -1.0)
    cut = edges[int(np.argmax(between)) + 1]
    out = (source._array.astype(np.float64) > cut).astype(np.int32)
    return Raster._of(np.where(source._mask, 0, out).astype(np.int32), source._mask.copy(), source._grid)


@_equivalent("a linear contrast stretch to 0–255", "Richards and Jia, Remote Sensing Digital Image Analysis, 4th ed., §4.3")
def Stretch(raster, stretch_type=0, min=None, max=None, num_stddev=None, statistics=None, dra=None, min_percent=None,
            max_percent=None, gamma=None, compute_gamma=None, sigmoid_strength_level=None):
    """EQUIVALENT: the linear contrast stretch (Richards and Jia, §4.3): a lower and an upper value are mapped to 0 and
    255, values between them linearly, and values beyond them clipped. ``MinMax`` takes them from ``min`` and ``max``
    (or the statistics); ``StdDev`` from the mean ∓ ``num_stddev`` standard deviations; ``PercentClip`` from the
    ``min_percent`` and ``max_percent`` percentiles of the values. ``None`` (0, the default) leaves the raster as it
    is, as documented.

    Refused: Histogram and Sigmoid, ``dra``, ``gamma`` and ``compute_gamma``; a StdDev or PercentClip stretch without
    its number, whose defaults the page does not give; rasters of several bands.

    INFERRED: the output is 8-bit unsigned, rounded to the nearest integer; the statistics are those of the raster's
    cells (population standard deviation) unless ``statistics`` gives ``[[min, max, mean, std]]``; percentiles are
    numpy's linear ones, the lower percentile cutting ``min_percent`` from the bottom."""
    _refuse_environment("arcpy.sa.Stretch")
    kind = str(stretch_type).upper() if stretch_type not in (None, 0, "0", "") else "NONE"
    source = as_raster(raster, "Stretch")
    if kind == "NONE":
        return source
    for name, value in (("dra", dra), ("gamma", gamma), ("compute_gamma", compute_gamma)):
        if value:
            _refuse("Stretch", name, value)
    values = source._values().astype(np.float64)
    stats = statistics[0] if statistics else [values.min(), values.max(), values.mean(), values.std()]
    if kind == "MINMAX":
        low = float(min) if min is not None else float(stats[0])
        high = float(max) if max is not None else float(stats[1])
    elif kind == "STDDEV":
        if num_stddev is None:
            _refuse("Stretch", "num_stddev", "(empty)", "its default is not documented")
        low, high = stats[2] - float(num_stddev) * stats[3], stats[2] + float(num_stddev) * stats[3]
    elif kind == "PERCENTCLIP":
        if min_percent is None or max_percent is None:
            _refuse("Stretch", "min_percent/max_percent", "(empty)", "their defaults are not documented")
        low, high = np.percentile(values, float(min_percent)), np.percentile(values, 100 - float(max_percent))
    else:
        _refuse("Stretch", "stretch_type", stretch_type, "only MinMax, StdDev and PercentClip are built")
    if high <= low:
        raise ValueError("Stretch: the upper value must be above the lower.")
    a = source._array.astype(np.float64)
    out = np.clip(np.floor((a - low) / (high - low) * 255 + 0.5), 0, 255)
    return Raster._of(np.where(source._mask, 0, out).astype(np.uint8), source._mask.copy(), source._grid)


@_equivalent("principal components of the band covariance matrix", "Jolliffe 2002, Principal Component Analysis, ch. 1")
def PrincipalComponents(in_raster_bands, number_components=None, out_data_file=None):
    """EQUIVALENT: principal components analysis of the bands' covariance matrix (Jolliffe 2002, ch. 1): each component
    is an eigenvector, ordered by decreasing eigenvalue, and a cell's component value is the eigenvector's dot product
    with the cell's band values less the band means. A cell with NoData in any band is NoData and left out of the
    covariance. The sign of each eigenvector is fixed so that its largest-magnitude loading is positive.

    Refused: ``out_data_file``, whose layout the page does not give.

    INFERRED: the covariance divides by n − 1; the output is floating point; one component answers a Raster, several a
    raster of several bands."""
    _refuse_environment("arcpy.sa.PrincipalComponents")
    if _given(out_data_file):
        _refuse("PrincipalComponents", "out_data_file", out_data_file, "its layout is not documented")
    inputs = in_raster_bands if isinstance(in_raster_bands, (list, tuple)) else [in_raster_bands]
    bands = [_open_bands(r, "PrincipalComponents") for r in inputs]
    grid = bands[0]._grid
    arrays = [a for b in bands for a in b._arrays]
    masks = [m for b in bands for m in b._masks]
    for b in bands[1:]:
        if not b._grid.same(grid):
            _refuse("PrincipalComponents", "in_raster_bands", b, "bands on different grids need resampling")
    if len(arrays) < 2:
        raise ValueError("PrincipalComponents needs at least two bands.")
    mask = np.logical_or.reduce(masks)
    data = np.stack([a.astype(np.float64)[~mask] for a in arrays], axis=1)
    means = data.mean(axis=0)
    values, vectors = np.linalg.eigh(np.cov(data, rowvar=False))
    order = np.argsort(values)[::-1]
    vectors = vectors[:, order]
    for k in range(vectors.shape[1]):
        if vectors[np.argmax(np.abs(vectors[:, k])), k] < 0:
            vectors[:, k] = -vectors[:, k]
    n = int(number_components) if _given(number_components) else len(arrays)
    full = np.stack([a.astype(np.float64) for a in arrays], axis=-1) - means
    scores = full @ vectors[:, :n]
    outputs = [np.where(mask, 0, scores[..., k]) for k in range(n)]
    if n == 1:
        return Raster._of(outputs[0], mask.copy(), grid)
    return _Bands(outputs, [mask.copy() for _ in outputs], grid)


@_equivalent("Sibson's natural neighbour interpolation", "Sibson 1981, in Interpreting Multivariate Data, pp. 21–36")
def NaturalNeighbor(in_point_features, z_field, cell_size=None):
    """EQUIVALENT: Sibson's (1981) natural neighbour interpolation: each cell centre is inserted into the points'
    Voronoi diagram, and its value is the points' values weighted by the area its new Voronoi cell takes from each of
    theirs. A cell whose centre is outside the points' convex hull is NoData, as Esri's page says; one on a point takes
    the point's value.

    INFERRED: a centre exactly on the hull's boundary is NoData (its Voronoi cell is unbounded); the grid is as Idw's;
    points at one place are refused."""
    import shapely
    from shapely.geometry import MultiPoint, Point as _P, box
    xs, ys, zs = _points(in_point_features, z_field, "NaturalNeighbor")
    if len({(x, y) for x, y in zip(xs, ys)}) != len(xs):
        _refuse("NaturalNeighbor", "in_point_features", in_point_features, "coincident points are not built")
    grid = _point_grid(in_point_features, cell_size, xs, ys, "NaturalNeighbor")
    sites = [(float(x), float(y)) for x, y in zip(xs, ys)]
    hull = MultiPoint(sites).convex_hull
    span = max(xs.max() - xs.min(), ys.max() - ys.min(), grid.width) * 10
    frame = box(xs.min() - span, ys.min() - span, xs.max() + span, ys.max() + span)

    def cells_of(points):
        diagram = shapely.voronoi_polygons(MultiPoint(points), extend_to=frame)
        found = {}
        for cell in diagram.geoms:
            for i, p in enumerate(points):
                if i not in found and cell.contains(_P(p)):
                    found[i] = cell.intersection(frame)
                    break
        return found

    original = cells_of(sites)
    cx, cy = grid.centres()
    out = np.zeros((grid.rows, grid.columns))
    empty = np.ones((grid.rows, grid.columns), bool)
    lookup = {p: i for i, p in enumerate(sites)}
    for r, y in enumerate(cy):
        for c, x in enumerate(cx):
            q = (float(x), float(y))
            if q in lookup:
                out[r, c], empty[r, c] = zs[lookup[q]], False
                continue
            if not hull.contains(_P(q)):
                continue
            new = cells_of(sites + [q])[len(sites)]
            stolen = np.array([new.intersection(original[i]).area for i in range(len(sites))])
            out[r, c] = (stolen * zs).sum() / stolen.sum()
            empty[r, c] = False
    return Raster._of(np.where(empty, 0, out).astype(np.float32), empty, grid)


# ---- Kriging


class KrigingModelOrdinary:
    """arcpy.sa.KrigingModelOrdinary: the semivariogram model of ordinary kriging, by its documented parameters."""

    def __init__(self, semivariogramType="SPHERICAL", lagSize=None, majorRange=None, partialSill=None, nugget=None):
        self.semivariogramType, self.lagSize = semivariogramType, lagSize
        self.majorRange, self.partialSill, self.nugget = majorRange, partialSill, nugget


class KrigingModelUniversal(KrigingModelOrdinary):
    """arcpy.sa.KrigingModelUniversal: LINEARDRIFT or QUADRATICDRIFT, and the same lag, range, sill and nugget."""

    def __init__(self, semivariogramType="LINEARDRIFT", lagSize=None, majorRange=None, partialSill=None, nugget=None):
        super().__init__(semivariogramType, lagSize, majorRange, partialSill, nugget)


def _semivariogram(kind, h, a, c, c0):
    """The semivariogram models of Johnston et al. (2001), Using ArcGIS Geostatistical Analyst, Appendix A: γ(0) = 0,
    and for h > 0 the nugget c0 plus the partial sill c times the model's shape at range a."""
    h = np.asarray(h, dtype=np.float64)
    t = h / a
    if kind == "SPHERICAL":
        shape = np.where(t < 1, 1.5 * t - 0.5 * t ** 3, 1.0)
    elif kind == "CIRCULAR":
        tc = np.clip(t, 0, 1)
        shape = np.where(t < 1, 1 - (2 / math.pi) * np.arccos(tc) + (2 / math.pi) * tc * np.sqrt(1 - tc * tc), 1.0)
    elif kind == "EXPONENTIAL":
        shape = 1 - np.exp(-3 * t)
    elif kind == "GAUSSIAN":
        shape = 1 - np.exp(-3 * t * t)
    elif kind in ("LINEAR", "LINEARDRIFT", "QUADRATICDRIFT"):
        shape = np.where(t < 1, t, 1.0)
    else:
        raise NotBuiltError(f"arcpy.sa.Kriging: semivariogramType '{kind}' is not built in grapy.")
    return np.where(h > 0, c0 + c * shape, 0.0)


def _fit_semivariogram(kind, xs, ys, zs, lag, fixed):
    """The model's range, partial sill and nugget fitted to the empirical semivariogram by Cressie's (1985) weighted
    least squares — minimising Σ N(h)·(γ̂(h)/γ(h) − 1)² over 12 lags of ``lag`` — any parameter given held fixed."""
    from scipy.optimize import minimize
    dx = xs[:, None] - xs
    dy = ys[:, None] - ys
    d = np.hypot(dx, dy)[np.triu_indices(len(xs), 1)]
    g = (0.5 * (zs[:, None] - zs) ** 2)[np.triu_indices(len(xs), 1)]
    lags, gammas, counts = [], [], []
    for k in range(12):
        hit = (d > k * lag) & (d <= (k + 1) * lag)
        if hit.any():
            lags.append(d[hit].mean())
            gammas.append(g[hit].mean())
            counts.append(hit.sum())
    if len(lags) < 2:
        raise ValueError("Kriging: fewer than two lags hold pairs of points; give a larger lagSize or the model's values.")
    lags, gammas, counts = map(np.array, (lags, gammas, counts))
    names = ["majorRange", "partialSill", "nugget"]
    start = {"majorRange": lags.max() / 2, "partialSill": max(gammas.max(), 1e-12), "nugget": 0.0}
    free = [n for n in names if fixed.get(n) is None]

    def model(values):
        p = {**start, **{k: v for k, v in fixed.items() if v is not None}, **dict(zip(free, values))}
        return p

    def cost(values):
        p = model(values)
        if p["majorRange"] <= 0 or p["partialSill"] < 0 or p["nugget"] < 0:
            return 1e30
        fitted = _semivariogram(kind, lags, p["majorRange"], p["partialSill"], p["nugget"])
        if np.any(fitted <= 0):
            return 1e30
        return float((counts * (gammas / fitted - 1) ** 2).sum())

    if free:
        best = minimize(cost, [start[n] for n in free], method="Nelder-Mead",
                        options={"xatol": 1e-10, "fatol": 1e-12, "maxiter": 20000})
        return model(best.x)
    return model([])


def _krige(xs, ys, zs, qx, qy, kind, a, c, c0, drift):
    """Ordinary (drift None) or universal kriging at one place from the points given: the kriging system of Cressie
    (1993), Statistics for Spatial Data, §3.2 and §3.4, solved in semivariogram form. Answers the estimate and the
    kriging variance."""
    n = len(xs)
    terms = {None: [], "LINEARDRIFT": [lambda x, y: x, lambda x, y: y],
             "QUADRATICDRIFT": [lambda x, y: x, lambda x, y: y, lambda x, y: x * x, lambda x, y: x * y,
                                lambda x, y: y * y]}[drift]
    # Drift terms on coordinates relative to the place estimated, for conditioning; the system is unchanged.
    rx, ry = xs - qx, ys - qy
    m = 1 + len(terms)
    big = np.zeros((n + m, n + m))
    big[:n, :n] = _semivariogram(kind, np.hypot(rx[:, None] - rx, ry[:, None] - ry), a, c, c0)
    big[:n, n] = big[n, :n] = 1.0
    for k, f in enumerate(terms, 1):
        big[:n, n + k] = big[n + k, :n] = f(rx, ry)
    right = np.zeros(n + m)
    right[:n] = _semivariogram(kind, np.hypot(rx, ry), a, c, c0)
    right[n] = 1.0
    for k, f in enumerate(terms, 1):
        right[n + k] = f(0.0, 0.0)
    solution = np.linalg.solve(big, right)
    weights = solution[:n]
    return float(weights @ zs), float(solution @ right)


@_equivalent("ordinary and universal kriging with a semivariogram fitted by weighted least squares",
             "Cressie 1985, Mathematical Geology 17(5); Cressie 1993, Statistics for Spatial Data, §3.2–3.4; models "
             "of Johnston et al. 2001, Using ArcGIS Geostatistical Analyst, Appendix A")
def Kriging(in_point_features, z_field, semiVariogram_props=None, cell_size=None, search_radius=None,
            out_variance_prediction_raster=None):
    """EQUIVALENT: kriging as Cressie (1993) gives it. Ordinary kriging (spherical, circular, exponential, Gaussian or
    linear semivariogram) or universal kriging with linear or quadratic drift, at each cell centre from the points the
    search radius finds — the nearest 12 by default (RadiusVariable), as documented. A range, partial sill or nugget
    not given is fitted by Cressie's (1985) weighted least squares to the empirical semivariogram over 12 lags of
    ``lagSize`` (by default the cell size, as documented); 12 lags is Geostatistical Analyst's published default
    (Johnston et al. 2001). Universal kriging fits its semivariogram to the residuals of a least-squares drift.
    ``out_variance_prediction_raster`` receives the kriging variance.

    Refused: a model string or class other than these; coincident points (the system is then singular).

    INFERRED: universal kriging's residual semivariogram is the linear model with a sill (the page names only the drift); a lag holds the pairs with (k−1)L < d ≤ kL and stands at their mean distance; the fit is Nelder–Mead from
    a start of half the largest lag, the largest semivariance and no nugget; the grid is as Idw's."""
    model = semiVariogram_props or KrigingModelOrdinary()
    if isinstance(model, str):
        parts = [p.strip() for p in model.replace(";", ",").split(",")]
        kind = parts[0].upper()
        values = [float(p) if p not in ("", "#") else None for p in parts[1:]] + [None] * 4
        cls = KrigingModelUniversal if kind in ("LINEARDRIFT", "QUADRATICDRIFT") else KrigingModelOrdinary
        model = cls(kind, *values[:4])
    kind = str(model.semivariogramType).upper()
    drift = kind if kind in ("LINEARDRIFT", "QUADRATICDRIFT") else None
    if drift is None and kind not in ("SPHERICAL", "CIRCULAR", "EXPONENTIAL", "GAUSSIAN", "LINEAR"):
        _refuse("Kriging", "semivariogramType", model.semivariogramType)
    xs, ys, zs = _points(in_point_features, z_field, "Kriging")
    if len({(x, y) for x, y in zip(xs, ys)}) != len(xs):
        _refuse("Kriging", "in_point_features", in_point_features, "coincident points make the kriging system singular")
    grid = _point_grid(in_point_features, cell_size, xs, ys, "Kriging")
    lag = float(model.lagSize) if _given(model.lagSize) else grid.width
    fixed = {"majorRange": model.majorRange, "partialSill": model.partialSill, "nugget": model.nugget}
    residual = zs
    if drift is not None:
        x0, y0, s = xs.mean(), ys.mean(), max(np.ptp(xs), np.ptp(ys)) or 1.0
        u, v = (xs - x0) / s, (ys - y0) / s
        cols = [np.ones_like(u), u, v] + ([u * u, u * v, v * v] if drift == "QUADRATICDRIFT" else [])
        design = np.column_stack(cols)
        coefficients, *_ = np.linalg.lstsq(design, zs, rcond=None)
        residual = zs - design @ coefficients
    if all(v is not None for v in fixed.values()):
        p = {k: float(v) for k, v in fixed.items()}
    else:
        p = _fit_semivariogram("LINEAR" if drift else kind, xs, ys, residual, lag,
                               {k: (float(v) if v is not None else None) for k, v in fixed.items()})
    shape = "LINEAR" if drift else kind
    mode, first, second = _radius(search_radius)
    cx, cy = grid.centres()
    estimate = np.zeros((grid.rows, grid.columns))
    variance = np.zeros((grid.rows, grid.columns))
    empty = np.zeros((grid.rows, grid.columns), bool)
    for r, y in enumerate(cy):
        for c, x in enumerate(cx):
            d = np.hypot(xs - x, ys - y)
            order = np.argsort(d, kind="stable")
            if mode == "VARIABLE":
                limit = float(second) if _given(second) else math.hypot(grid.right - grid.left, grid.top - grid.bottom)
                chosen = [i for i in order[:first] if d[i] <= limit]
            else:
                radius = float(first) if _given(first) else 5 * grid.width
                chosen = [i for k, i in enumerate(order) if d[i] <= radius or k < second]
            needed = 1 + (0 if drift is None else (2 if drift == "LINEARDRIFT" else 5))
            if len(chosen) < needed:
                empty[r, c] = True
                continue
            idx = np.array(chosen)
            estimate[r, c], variance[r, c] = _krige(xs[idx], ys[idx], zs[idx], x, y, shape, p["majorRange"],
                                                    p["partialSill"], p["nugget"], drift)
    if _given(out_variance_prediction_raster):
        Raster._of(np.where(empty, 0, variance).astype(np.float32), empty.copy(), grid).save(out_variance_prediction_raster)
    return Raster._of(np.where(empty, 0, estimate).astype(np.float32), empty, grid)


def _dinf_tool():
  @_equivalent("D-infinity flow direction", "Tarboton 1997, Water Resources Research 33(2)")
  def FlowDirection(raster, force):
      """EQUIVALENT: Tarboton's (1997) D-infinity: the steepest downslope direction over the eight triangular facets of
      each 3 × 3 window, as an angle in degrees counterclockwise from east (0–360), as Esri's page states the output.
      A facet joins a cardinal neighbour e1 and the diagonal e2 beside it; s1 = (e0 − e1)/d, s2 = (e1 − e2)/d,
      r = atan(s2/s1), held within 0 … 45° (on the bounds, the slope is that of the edge taken). A cell with no positive
      slope is −1, the undefined value Esri's Flow Accumulation page gives for D-infinity.

      INFERRED: an edge cell flows outward under FORCE, and under NORMAL when no facet inside the raster falls, at the
      angle of the D8 outward direction (90 north, 0 east, …); of equal slopes the first facet counterclockwise from
      in the order east–north-east, east–south-east, north–north-west, north–north-east, … wins."""
      z = raster._array.astype(np.float64)
      g = raster._grid
      if not math.isclose(g.width, g.height):
          _refuse("FlowDirection", "in_surface_raster", raster, "D-infinity is defined on square cells")
      dcell = g.width
      rows, columns = z.shape
      # (cardinal offset, angle), then the diagonal on each side: +45° (counterclockwise) and −45°.
      cardinals = [((0, 1), 0.0), ((-1, 0), 90.0), ((0, -1), 180.0), ((1, 0), 270.0)]
      out = np.zeros(z.shape, np.float64)
      outward = {(True, False, False, False): 90.0, (False, True, False, False): 270.0,
                 (False, False, True, False): 180.0, (False, False, False, True): 0.0,
                 (True, False, True, False): 135.0, (True, False, False, True): 45.0,
                 (False, True, True, False): 225.0, (False, True, False, True): 315.0}
      for r in range(rows):
          for k in range(columns):
              edge = (r == 0, r == rows - 1, k == 0, k == columns - 1)
              if any(edge) and force:
                  out[r, k] = outward[edge]
                  continue
              best, angle = 0.0, -1.0
              for (dr, dk), base in cardinals:
                  for side in (1, -1):
                      # The diagonal beside the cardinal, counterclockwise (+) or clockwise (−).
                      rot = math.radians(base + side * 45)
                      ddr, ddk = -int(round(math.sin(rot) * math.sqrt(2))), int(round(math.cos(rot) * math.sqrt(2)))
                      r1, k1, r2, k2 = r + dr, k + dk, r + ddr, k + ddk
                      if not (0 <= r1 < rows and 0 <= k1 < columns and 0 <= r2 < rows and 0 <= k2 < columns):
                          continue
                      s1 = (z[r, k] - z[r1, k1]) / dcell
                      s2 = (z[r1, k1] - z[r2, k2]) / dcell
                      t = math.atan2(s2, s1)
                      if t < 0:
                          t, s = 0.0, s1
                      elif t > math.pi / 4:
                          t, s = math.pi / 4, (z[r, k] - z[r2, k2]) / (dcell * math.sqrt(2))
                      else:
                          s = math.hypot(s1, s2)
                      if s > best:
                          best, angle = s, (base + side * math.degrees(t)) % 360.0
              if angle < 0 and any(edge):
                  angle = outward[edge]
              out[r, k] = angle
      return Raster._of(out.astype(np.float32), np.zeros(z.shape, bool), g)

  return FlowDirection


_flow_direction_dinf = _dinf_tool()


for _built in ("KernelDensity", "Threshold", "Stretch", "PrincipalComponents", "NaturalNeighbor", "Kriging",
               "KrigingModelOrdinary", "KrigingModelUniversal"):
    _REFUSED.pop(_built, None)


# ---- round seven (ADR-224 §5.6): visibility, generalization, filters and merge as equivalents

_EARTH_DIAMETER = 12740000.0  # metres, as How Viewshed works gives it


def _observers(in_observer_features, raster, zf, values, tool):
    """The observers — each point, or each vertex of a line — with their parameters: a number, or a field read from
    the feature, each defaulting to the documented field (SPOT, OFFSETA, OFFSETB …) and then to the documented value."""
    from . import da
    from .analysis import _schema
    columns = {c.name.upper(): c.name for c in _schema(in_observer_features)[0]}
    fields = {}
    for key, (default_field, default_value, given) in values.items():
        if isinstance(given, str) and given.upper() in columns:
            fields[key] = columns[given.upper()]
        elif _given(given):
            fields[key] = float(given)
        elif default_field in columns:
            fields[key] = columns[default_field]
        else:
            fields[key] = default_value
    names = sorted({v for v in fields.values() if isinstance(v, str)})
    out = []
    with da.SearchCursor(in_observer_features, [*names, "SHAPE@"]) as rows:
        for row in rows:
            shape = row[-1]._shape if row[-1] is not None else None
            if shape is None:
                continue
            record = dict(zip(names, row[:-1]))
            params = {k: (float(record[v]) if isinstance(v, str) and record[v] is not None else
                          (None if isinstance(v, str) else v)) for k, v in fields.items()}
            for part in getattr(shape, "geoms", [shape]):
                coordinates = [part.coords[0]] if part.geom_type == "Point" else list(part.coords)
                for x, y in coordinates:
                    p = dict(params)
                    if p.get("elevation") is None:
                        sampled = _sample(raster, x, y, True)
                        if sampled is None:
                            continue
                        p["elevation"] = sampled * zf
                    out.append((x, y, p))
    return out


def _line_of_sight(z, mask, grid, ox, oy, oz, curved, refraction):
    """For one observer, each cell's visibility and the height it would need to be seen: R3 line of sight (Franklin and
    Ray 1994) — the line to each cell centre is sampled where it crosses the row and column centre lines between, each
    sample linearly interpolated between the two cells it falls between; a cell is visible when its elevation angle is
    at least the largest angle along the way. NoData cells are not visible and block nothing, as documented."""
    rows, columns = z.shape
    xs, ys = grid.centres()
    visible = np.zeros(z.shape, bool)
    needed = np.zeros(z.shape)

    def corrected(height, distance):
        if not curved:
            return height
        return height - distance * distance / _EARTH_DIAMETER + refraction * distance * distance / _EARTH_DIAMETER

    oc = (ox - grid.left) / grid.width - 0.5
    orow = (grid.top - oy) / grid.height - 0.5
    for r in range(rows):
        for c in range(columns):
            if mask[r, c]:
                continue
            dx, dy = xs[c] - ox, ys[r] - oy
            distance = math.hypot(dx, dy)
            if distance == 0:
                visible[r, c] = True
                continue
            target = (corrected(z[r, c], distance) - oz) / distance
            steepest = -np.inf
            # Crossings of column centre lines and row centre lines strictly between observer and target.
            samples = []
            for k in range(int(math.floor(min(oc, c))) + 1, int(math.ceil(max(oc, c)))):
                t = (k - oc) / (c - oc)
                if 0 < t < 1:
                    fr = orow + t * (r - orow)
                    r0 = int(math.floor(fr))
                    samples.append((t, [(r0, k, 1 - (fr - r0)), (r0 + 1, k, fr - r0)]))
            for k in range(int(math.floor(min(orow, r))) + 1, int(math.ceil(max(orow, r)))):
                t = (k - orow) / (r - orow)
                if 0 < t < 1:
                    fc = oc + t * (c - oc)
                    c0 = int(math.floor(fc))
                    samples.append((t, [(k, c0, 1 - (fc - c0)), (k, c0 + 1, fc - c0)]))
            for t, parts in samples:
                parts = [(pr, pc, w) for pr, pc, w in parts if w > 0]
                if any(not (0 <= pr < rows and 0 <= pc < columns) or mask[pr, pc] for pr, pc, _ in parts):
                    continue
                height = sum(z[pr, pc] * w for pr, pc, w in parts) / sum(w for _, _, w in parts)
                angle = (corrected(height, t * distance) - oz) / (t * distance)
                steepest = max(steepest, angle)
            visible[r, c] = target >= steepest
            if not visible[r, c]:
                needed[r, c] = oz + steepest * distance - corrected(z[r, c], distance)
    return visible, needed


def _visibility(tool, in_raster, in_observer_features, z_factor, curvature_correction, refractivity_coefficient,
                values, limits=None):
    _refuse_environment(f"arcpy.sa.{tool}")
    raster = as_raster(in_raster, tool)
    zf = float(z_factor if _given(z_factor) else 1)
    curved = str(curvature_correction or "FLAT_EARTH").upper() == "CURVED_EARTH"
    if curved:
        from ._reference import SpatialReference
        g = raster._grid
        if not g.srid or SpatialReference(g.srid).type != "Projected" or SpatialReference(g.srid).metersPerUnit != 1:
            _refuse(tool, "curvature_correction", curvature_correction, "the correction is built for data in metres")
    refraction = float(refractivity_coefficient if _given(refractivity_coefficient) else 0.13)
    z = raster._array.astype(np.float64) * zf
    count = np.zeros(z.shape, np.int32)
    agl = np.full(z.shape, np.inf)
    xs, ys = raster._grid.centres()
    gx, gy = np.meshgrid(xs, ys)
    for ox, oy, p in _observers(in_observer_features, raster, zf, values, tool):
        oz = p["elevation"] + p["observer_offset"]
        target = z + p["surface_offset"]
        visible, needed = _line_of_sight(target, raster._mask, raster._grid, ox, oy, oz, curved, refraction)
        if limits is not None:
            visible &= limits(p, gx - ox, gy - oy, target - oz)
        count += visible.astype(np.int32)
        agl = np.minimum(agl, np.where(visible, 0.0, needed))
    return raster, count, np.where(np.isfinite(agl), agl, 0.0)


_VIEWSHED_SOURCE = ("R3 line of sight with linear interpolation between cells (Franklin and Ray 1994, Higher isn't "
                    "necessarily better), with the curvature and refraction correction and observer fields of How "
                    "Viewshed works")


@_equivalent("R3 line-of-sight viewshed", _VIEWSHED_SOURCE)
def Viewshed(in_raster, in_observer_features, z_factor=1, curvature_correction="FLAT_EARTH", refractivity_coefficient=0.13,
             out_agl_raster=None):
    """EQUIVALENT: the R3 line-of-sight viewshed (Franklin and Ray 1994) — each cell centre is visible from an observer
    when its elevation angle is at least the largest along the line between, sampled where the line crosses row and
    column centre lines and interpolated linearly between the two cells there. As documented: the output counts the
    observers (points, or line vertices) that see each cell; an observer stands at SPOT, or the surface bilinearly
    interpolated, plus OFFSETA (default 1); each cell is raised by OFFSETB (default 0); NoData cells are not visible and
    block nothing; ``CURVED_EARTH`` applies Z − D²/12,740,000 + R·D²/12,740,000 in metres. ``out_agl_raster`` receives
    the least height a cell would need to be seen, 0 where it is.

    Refused: observers carrying AZIMUTH, VERT or RADIUS fields (use Visibility, which honours them).

    INFERRED: the observer's own cell is visible; the AGL is measured from the cell's surface raised by OFFSETB."""
    from .analysis import _schema
    names = {c.name.upper() for c in _schema(in_observer_features)[0]}
    for field in ("AZIMUTH1", "AZIMUTH2", "VERT1", "VERT2", "RADIUS1", "RADIUS2"):
        if field in names:
            _refuse("Viewshed", "in_observer_features", field, "observer limits are honoured by Visibility")
    values = {"elevation": ("SPOT", None, None), "observer_offset": ("OFFSETA", 1.0, None),
              "surface_offset": ("OFFSETB", 0.0, None)}
    raster, count, agl = _visibility("Viewshed", in_raster, in_observer_features, z_factor, curvature_correction,
                                     refractivity_coefficient, values)
    if _given(out_agl_raster):
        Raster._of(np.where(raster._mask, 0, agl).astype(np.float32), raster._mask.copy(), raster._grid).save(out_agl_raster)
    return Raster._of(np.where(raster._mask, 0, count), raster._mask.copy(), raster._grid)


@_equivalent("R3 line-of-sight visibility", _VIEWSHED_SOURCE)
def Visibility(in_raster, in_observer_features, out_agl_raster=None, analysis_type="FREQUENCY",
               nonvisible_cell_value="ZERO", z_factor=1, curvature_correction="FLAT_EARTH", refractivity_coefficient=0.13,
               surface_offset=None, observer_elevation=None, observer_offset=None, inner_radius=None, outer_radius=None,
               horizontal_start_angle=None, horizontal_end_angle=None, vertical_upper_angle=None,
               vertical_lower_angle=None):
    """EQUIVALENT: Viewshed's R3 line of sight, with the observer parameters Visibility documents — each a number or a
    field, defaulting to OFFSETB, SPOT, OFFSETA, RADIUS1, RADIUS2, AZIMUTH1, AZIMUTH2, VERT1, VERT2 and then to 0,
    the bilinear surface, 1, 0, infinity, 0, 360, 90 and −90. A cell counts for an observer when it is in sight, within
    the radii (a negative radius measured flat, a positive one in three dimensions, as documented), within the
    horizontal angles (clockwise from north) and within the vertical angles. ``nonvisible_cell_value`` ``NODATA``
    writes NoData where no observer sees.

    Refused: ``OBSERVERS``, whose encoding the page does not give.

    INFERRED: a horizontal range whose start exceeds its end wraps through north; the vertical angle is the target's
    elevation angle from the observer's eye."""
    if str(analysis_type or "FREQUENCY").upper() != "FREQUENCY":
        _refuse("Visibility", "analysis_type", analysis_type, "the OBSERVERS encoding is not documented")
    values = {"surface_offset": ("OFFSETB", 0.0, surface_offset), "elevation": ("SPOT", None, observer_elevation),
              "observer_offset": ("OFFSETA", 1.0, observer_offset), "inner": ("RADIUS1", 0.0, inner_radius),
              "outer": ("RADIUS2", math.inf, outer_radius), "start": ("AZIMUTH1", 0.0, horizontal_start_angle),
              "end": ("AZIMUTH2", 360.0, horizontal_end_angle), "upper": ("VERT1", 90.0, vertical_upper_angle),
              "lower": ("VERT2", -90.0, vertical_lower_angle)}

    def limits(p, dx, dy, dz):
        flat = np.hypot(dx, dy)
        def within(radius, below):
            if radius is None or not np.isfinite(radius):
                return np.ones(flat.shape, bool)
            measure = flat if radius < 0 else np.hypot(flat, dz)
            return measure <= abs(radius) if below else measure >= abs(radius)
        keep = within(p["outer"], True) & within(p["inner"], False)
        azimuth = np.degrees(np.arctan2(dx, dy)) % 360
        start, end = p["start"] % 360, p["end"] % 360 if p["end"] != 360 else 360
        keep &= ((azimuth >= start) & (azimuth <= end)) if start <= end else ((azimuth >= start) | (azimuth <= end))
        angle = np.degrees(np.arctan2(dz, flat))
        keep &= (angle <= p["upper"]) & (angle >= p["lower"])
        keep |= (flat == 0)
        return keep

    raster, count, agl = _visibility("Visibility", in_raster, in_observer_features, z_factor, curvature_correction,
                                     refractivity_coefficient, values, limits)
    mask = raster._mask.copy()
    if str(nonvisible_cell_value or "ZERO").upper() == "NODATA":
        mask |= count == 0
    if _given(out_agl_raster):
        Raster._of(np.where(raster._mask, 0, agl).astype(np.float32), raster._mask.copy(), raster._grid).save(out_agl_raster)
    return Raster._of(np.where(mask, 0, count), mask, raster._grid)


# ---- generalization


_EIGHT = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]


@_equivalent("zone growth by morphological dilation, or by Euclidean distance",
             "Serra 1982, Image Analysis and Mathematical Morphology, ch. 2; Danielsson 1980, Computer Graphics and "
             "Image Processing 14")
def Expand(in_raster, number_cells, zone_values, expand_method="MORPHOLOGICAL"):
    """EQUIVALENT: the zones listed grow into the other cells. ``MORPHOLOGICAL``: ``number_cells`` steps of dilation by
    the 3 × 3 square (Serra 1982); a background cell reached by several zones takes the one most frequent among its
    eight neighbours, as documented, the lower value on a tie. ``DISTANCE``: every background cell within
    ``number_cells`` cell widths of a zone takes the nearest zone by exact Euclidean distance (Danielsson 1980).
    NoData is background and never expands, as documented.

    INFERRED: a DISTANCE tie goes to the zone SciPy's exact transform reports."""
    _refuse_environment("arcpy.sa.Expand")
    source = as_raster(in_raster, "Expand")
    if not source.isInteger:
        raise ValueError("Expand takes an integer raster.")
    zones = {int(v) for v in (zone_values if isinstance(zone_values, (list, tuple)) else [zone_values])}
    a = source._array.astype(np.int64).copy()
    mask = source._mask.copy()
    n = int(number_cells)
    if str(expand_method or "MORPHOLOGICAL").upper() == "DISTANCE":
        from scipy.ndimage import distance_transform_edt
        foreground = np.isin(a, list(zones)) & ~mask
        distance, (ri, ci) = distance_transform_edt(~foreground, return_indices=True)
        reach = (distance <= n) & ~foreground
        a = np.where(reach, a[ri, ci], a)
        return Raster._of(a.astype(np.int32), mask & ~reach, source._grid)
    if str(expand_method).upper() != "MORPHOLOGICAL":
        _refuse("Expand", "expand_method", expand_method)
    rows, columns = a.shape
    for _ in range(n):
        foreground = np.isin(a, list(zones)) & ~mask
        new_a, new_mask = a.copy(), mask.copy()
        for r in range(rows):
            for c in range(columns):
                if foreground[r, c]:
                    continue
                near = [a[r + dr, c + dc] for dr, dc in _EIGHT
                        if 0 <= r + dr < rows and 0 <= c + dc < columns and foreground[r + dr, c + dc]]
                if near:
                    values, counts = np.unique(near, return_counts=True)
                    new_a[r, c] = values[counts == counts.max()].min()
                    new_mask[r, c] = False
        a, mask = new_a, new_mask
    return Raster._of(np.where(mask, 0, a).astype(np.int32), mask, source._grid)


@_equivalent("zone shrinking by morphological erosion", "Serra 1982, Image Analysis and Mathematical Morphology, ch. 2")
def Shrink(in_raster, number_cells, zone_values, shrink_method="MORPHOLOGICAL"):
    """EQUIVALENT: the zones listed lose ``number_cells`` layers by erosion with the 3 × 3 square (Serra 1982): at each
    step, a cell of a listed zone with a neighbour outside the listed zones takes the value most frequent among its
    neighbours outside its own zone, as documented — NoData competing as any value does — the lower value on a tie
    (NoData last). Where two listed zones meet, nothing changes at their boundary, as documented.

    Refused: ``DISTANCE``, whose rule for the replacing value the page does not give."""
    _refuse_environment("arcpy.sa.Shrink")
    if str(shrink_method or "MORPHOLOGICAL").upper() != "MORPHOLOGICAL":
        _refuse("Shrink", "shrink_method", shrink_method, "its replacing value is not documented")
    source = as_raster(in_raster, "Shrink")
    if not source.isInteger:
        raise ValueError("Shrink takes an integer raster.")
    zones = {int(v) for v in (zone_values if isinstance(zone_values, (list, tuple)) else [zone_values])}
    a = source._array.astype(np.int64).copy()
    mask = source._mask.copy()
    rows, columns = a.shape
    for _ in range(int(number_cells)):
        selected = np.isin(a, list(zones)) & ~mask
        new_a, new_mask = a.copy(), mask.copy()
        for r in range(rows):
            for c in range(columns):
                if not selected[r, c]:
                    continue
                outside = [(None if mask[r + dr, c + dc] else int(a[r + dr, c + dc])) for dr, dc in _EIGHT
                           if 0 <= r + dr < rows and 0 <= c + dc < columns and not selected[r + dr, c + dc]]
                if not outside:
                    continue
                keys = sorted(set(outside), key=lambda v: (v is None, v if v is not None else 0))
                best = max(keys, key=lambda v: (outside.count(v), -keys.index(v)))
                if best is None:
                    new_mask[r, c] = True
                else:
                    new_a[r, c] = best
        a, mask = new_a, new_mask
    return Raster._of(np.where(mask, 0, a).astype(np.int32), mask, source._grid)


@_equivalent("nearest-neighbour replacement by exact Euclidean distance", "Danielsson 1980, Computer Graphics and "
             "Image Processing 14")
def Nibble(in_raster, in_mask_raster, nibble_values="ALL_VALUES", nibble_nodata="PRESERVE_NODATA", in_zone_raster=None):
    """EQUIVALENT: each cell that is NoData in the mask, as documented, takes the value of the nearest cell outside the
    mask by exact Euclidean distance (Danielsson 1980). ``DATA_ONLY`` lets only data replace; ``ALL_VALUES`` lets
    NoData replace too. ``PRESERVE_NODATA`` keeps the input's NoData inside the mask.

    Refused: ``in_zone_raster``.

    INFERRED: a tie goes to the cell SciPy's exact transform reports."""
    from scipy.ndimage import distance_transform_edt
    _refuse_environment("arcpy.sa.Nibble")
    if _given(in_zone_raster):
        _refuse("Nibble", "in_zone_raster", in_zone_raster)
    source, mask_raster = as_raster(in_raster, "Nibble"), as_raster(in_mask_raster, "Nibble")
    if not mask_raster._grid.same(source._grid):
        _refuse("Nibble", "in_mask_raster", in_mask_raster, "a mask on another grid needs resampling")
    masked = mask_raster._mask
    donors = ~masked & (~source._mask if str(nibble_values or "ALL_VALUES").upper() == "DATA_ONLY" else True)
    if not donors.any():
        return Raster._of(source._array.copy(), source._mask | masked, source._grid)
    _, (ri, ci) = distance_transform_edt(~donors, return_indices=True)
    a = np.where(masked, source._array[ri, ci], source._array)
    out_mask = np.where(masked, source._mask[ri, ci], source._mask)
    if str(nibble_nodata or "PRESERVE_NODATA").upper() == "PRESERVE_NODATA":
        out_mask |= masked & source._mask
    return Raster._of(np.where(out_mask, 0, a), out_mask, source._grid)


@_equivalent("3 × 3 low-pass (mean) and high-pass (Laplacian, centre 8) filters",
             "Gonzalez and Woods, Digital Image Processing, 3rd ed., §3.5–3.6")
def Filter(in_raster, filter_type="LOW", ignore_nodata="DATA"):
    """EQUIVALENT: ``LOW`` is the 3 × 3 mean, the averaging filter the page names; ``HIGH`` is the 3 × 3 Laplacian high
    pass with centre 8 and −1 around it (Gonzalez and Woods §3.6). With ``DATA`` only valid cells take part, as
    documented — the mean of those there, and the high pass with each missing neighbour left out (the centre weight
    then the number of valid neighbours); with ``NODATA`` any NoData in the window makes the cell NoData. Off the raster
    counts as NoData. The output is floating point.

    INFERRED: a NoData centre with valid neighbours gets the low-pass mean under DATA, as the page says such cells can
    receive values; under HIGH it stays NoData, having no centre value."""
    _refuse_environment("arcpy.sa.Filter")
    kind = str(filter_type or "LOW").upper()
    if kind not in ("LOW", "HIGH"):
        _refuse("Filter", "filter_type", filter_type)
    source = as_raster(in_raster, "Filter")
    z = np.where(source._mask, np.nan, source._array.astype(np.float64))
    padded = np.pad(z, 1, constant_values=np.nan)
    rows, columns = z.shape
    stack = np.stack([padded[1 + dr:1 + dr + rows, 1 + dc:1 + dc + columns] for dr, dc in _EIGHT])
    valid = ~np.isnan(stack)
    strict = str(ignore_nodata or "DATA").upper() == "NODATA"
    if kind == "LOW":
        everything = np.concatenate([stack, z[None]])
        count = (~np.isnan(everything)).sum(axis=0)
        value = np.nansum(everything, axis=0) / np.where(count == 0, 1, count)
        mask = count == 0
        if strict:
            mask |= np.isnan(everything).any(axis=0)
    else:
        value = valid.sum(axis=0) * np.nan_to_num(z) - np.nansum(stack, axis=0)
        mask = np.isnan(z)
        if strict:
            mask |= (~valid).any(axis=0)
    return Raster._of(np.where(mask, 0, value), mask, source._grid)


@_equivalent("mosaic of rasters by local operations over the valid inputs",
             "Tomlin 1990, Geographic Information Systems and Cartographic Modeling, ch. 3 (local operations)")
def Merge(rasters, resolve_overlap="FIRST"):
    """EQUIVALENT: rasters on one grid merged cell by cell over the inputs that have data there (Tomlin's local
    operations): FIRST and LAST take the first or last such input, MIN, MAX, MEAN and SUM their statistic. A cell with
    no data in any input is NoData.

    Refused: rasters on different grids (the page does not give the output extent and cell size rules)."""
    kind = str(resolve_overlap or "FIRST").upper()
    if kind not in ("FIRST", "LAST", "MIN", "MAX", "MEAN", "SUM"):
        _refuse("Merge", "resolve_overlap", resolve_overlap)
    opened = [as_raster(r, "Merge") for r in rasters]
    grid = opened[0]._grid
    for r in opened[1:]:
        if not r._grid.same(grid):
            _refuse("Merge", "rasters", r, "the output extent and cell size rules for different grids are not documented")
    stack = np.stack([np.where(r._mask, np.nan, r._array.astype(np.float64)) for r in opened])
    empty = np.isnan(stack).all(axis=0)
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        if kind in ("FIRST", "LAST"):
            order = stack if kind == "FIRST" else stack[::-1]
            first = np.argmax(~np.isnan(order), axis=0)
            value = np.take_along_axis(order, first[None], axis=0)[0]
        else:
            value = {"MIN": np.nanmin, "MAX": np.nanmax, "MEAN": np.nanmean, "SUM": np.nansum}[kind](stack, axis=0)
    integer = all(r.isInteger for r in opened) and kind != "MEAN"
    out = np.where(empty, 0, value)
    return Raster._of(out.astype(np.int32) if integer else out, empty, grid)


for _built in ("Viewshed", "Visibility", "Expand", "Shrink", "Nibble", "Filter", "Merge"):
    _REFUSED.pop(_built, None)


# ---- round eight (ADR-224 §5.6): fuzzy membership


class _Fuzzy:
    def __init__(self, *values):
        for name, value in zip(self._names, values):
            setattr(self, name, value)


class FuzzyGaussian(_Fuzzy):
    """arcpy.sa.FuzzyGaussian(midpoint, spread): midpoint defaults to the middle of the input's range, spread to 0.1."""
    _names = ("midpoint", "spread")

    def __init__(self, midpoint=None, spread=0.1):
        super().__init__(midpoint, spread)


class FuzzyLarge(_Fuzzy):
    """arcpy.sa.FuzzyLarge(midpoint, spread): midpoint defaults to the middle of the input's range, spread to 5."""
    _names = ("midpoint", "spread")

    def __init__(self, midpoint=None, spread=5):
        super().__init__(midpoint, spread)


class FuzzySmall(FuzzyLarge):
    """arcpy.sa.FuzzySmall(midpoint, spread): midpoint defaults to the middle of the input's range, spread to 5."""


class FuzzyNear(FuzzyGaussian):
    """arcpy.sa.FuzzyNear(midpoint, spread): midpoint defaults to the middle of the input's range, spread to 0.1."""


class FuzzyLinear(_Fuzzy):
    """arcpy.sa.FuzzyLinear(minimum, maximum): 1 and 2 by default, as documented."""
    _names = ("minimum", "maximum")

    def __init__(self, minimum=1, maximum=2):
        super().__init__(minimum, maximum)


class FuzzyMSLarge(_Fuzzy):
    """arcpy.sa.FuzzyMSLarge(meanMultiplier, STDMultiplier): 1 and 2 by default, as documented."""
    _names = ("meanMultiplier", "STDMultiplier")

    def __init__(self, meanMultiplier=1, STDMultiplier=2):
        super().__init__(meanMultiplier, STDMultiplier)


class FuzzyMSSmall(FuzzyMSLarge):
    """arcpy.sa.FuzzyMSSmall(meanMultiplier, STDMultiplier): 1 and 2 by default, as documented."""


@_equivalent("the fuzzy membership functions in the forms of Esri's transformation-function formulas",
             "Esri, Formulas for the transformation functions (Spatial Analyst reference), which give the same shapes "
             "as text; hedges after Zadeh 1972, A fuzzy-set-theoretic interpretation of linguistic hedges")
def FuzzyMembership(in_raster, fuzzy_function=None, hedge="NONE"):
    """EQUIVALENT: the membership functions in the forms Esri's *Formulas for the transformation functions* page gives as
    text for the same shapes — Gaussian exp(−s·(x − m)²); Near 1/(1 + s·(x − m)²); Large 1/(1 + (x/m)^(−s));
    Small 1/(1 + (x/m)^s); Linear (x − min)/(max − min) held to 0 … 1; MSLarge 1 − b·σ/(x − a·μ + b·σ) above a·μ and 0
    below; MSSmall b·σ/(x − a·μ + b·σ) above a·μ and 1 below — with μ and σ the input's mean and standard deviation and
    the documented defaults. ``VERY`` squares the membership and ``SOMEWHAT`` takes its square root, as documented
    (Zadeh's concentration and dilation).

    Refused: Small and Large on negative values, which the page says they do not accept.

    INFERRED: σ is the population standard deviation; the default midpoint is (min + max)/2 of the cells."""
    _refuse_environment("arcpy.sa.FuzzyMembership")
    source = as_raster(in_raster, "FuzzyMembership")
    f = fuzzy_function if fuzzy_function is not None else FuzzyMSSmall()
    values = source._values().astype(np.float64)
    x = source._array.astype(np.float64)
    middle = (values.min() + values.max()) / 2 if values.size else 0.0
    with np.errstate(all="ignore"):
        if isinstance(f, (FuzzyGaussian, FuzzyLarge)):
            m = float(f.midpoint) if f.midpoint is not None else middle
            s = float(f.spread)
            if isinstance(f, FuzzyNear):
                out = 1 / (1 + s * (x - m) ** 2)
            elif isinstance(f, FuzzyGaussian):
                out = np.exp(-s * (x - m) ** 2)
            else:
                if (values < 0).any():
                    _refuse("FuzzyMembership", "in_raster", in_raster, "Small and Large do not accept negative values")
                out = 1 / (1 + (x / m) ** (s if isinstance(f, FuzzySmall) else -s))
        elif isinstance(f, FuzzyLinear):
            low, high = float(f.minimum), float(f.maximum)
            if low == high:
                raise ValueError("FuzzyLinear: the minimum and maximum must differ.")
            out = np.clip((x - low) / (high - low), 0, 1)
        elif isinstance(f, FuzzyMSLarge):
            mean, std = values.mean(), values.std()
            a, b = float(f.meanMultiplier) * mean, float(f.STDMultiplier) * std
            if isinstance(f, FuzzyMSSmall):
                out = np.where(x > a, b / (x - a + b), 1.0)
            else:
                out = np.where(x > a, 1 - b / (x - a + b), 0.0)
        else:
            _refuse("FuzzyMembership", "fuzzy_function", type(f).__name__)
    kind = str(hedge or "NONE").upper()
    if kind == "VERY":
        out = out ** 2
    elif kind == "SOMEWHAT":
        out = np.sqrt(out)
    elif kind != "NONE":
        _refuse("FuzzyMembership", "hedge", hedge)
    mask = source._mask | ~np.isfinite(out)
    return Raster._of(np.where(mask, 0, out), mask, source._grid)


for _built in ("FuzzyMembership", "FuzzyGaussian", "FuzzyLarge", "FuzzyLinear", "FuzzyMSLarge", "FuzzyMSSmall",
               "FuzzyNear", "FuzzySmall"):
    _REFUSED.pop(_built, None)


# ---- round nine (ADR-224 §5.6): Spline

_EULER = 0.577215


def _spline_basis(kind, r, weight):
    """R(r) of the regularized spline (Mitas and Mitasova 1988, with τ² = weight) or of the spline with tension (the
    page's own text, with φ² = weight). R(0) is the limit, 0 for both."""
    from scipy.special import k0
    r = np.asarray(r, dtype=np.float64)
    safe = np.where(r > 0, r, 1.0)
    if kind == "REGULARIZED":
        tau = math.sqrt(weight)
        value = (safe * safe / 4 * (np.log(safe / (2 * tau)) + _EULER - 1)
                 + tau * tau * (k0(safe / tau) + _EULER + np.log(safe / (2 * tau)))) / (2 * math.pi)
    else:
        phi = math.sqrt(weight)
        value = -1 / (2 * math.pi * phi * phi) * (np.log(safe * phi / 2) + _EULER + k0(safe * phi))
    return np.where(r > 0, value, 0.0)


@_equivalent("the regularized spline and the spline with tension", "Mitas and Mitasova 1988, Mathematical Geology "
             "20(8); Franke 1982, Computers and Mathematics with Applications 8(4); the page's own tension basis")
def Spline(in_point_features, z_field, cell_size=None, spline_type="REGULARIZED", weight=0.1, number_points=12):
    """EQUIVALENT: S(x, y) = T(x, y) + Σ λⱼ R(rⱼ), as the page writes it, solved exactly through the points: for
    ``REGULARIZED``, T = a₁ + a₂x + a₃y and Mitas and Mitasova's (1988) basis
    R(r) = (1/2π)·{r²/4·[ln(r/2τ) + c − 1] + τ²·[K₀(r/τ) + c + ln(r/2τ)]} with τ² = ``weight``; for ``TENSION``,
    T = a₁ and the page's R(r) = −1/(2πφ²)·[ln(rφ/2) + c + K₀(rφ)] with φ² = ``weight``; c = 0.577215. The
    regularized basis is the published one; Esri's page shows it only as an image (where its last logarithm reads
    ln(r/2π), which differs by a constant that the condition Σλⱼ = 0 cancels).

    ``number_points`` divides the output into regions of that many points, as documented; the equivalent builds the
    case of one region — as many points as ``number_points`` or fewer, the default 12 — and refuses more, since how a
    region is widened to eight points is not documented.

    INFERRED: the default type is REGULARIZED (the page's examples pass it); the grid is as Idw's; coincident points
    are refused."""
    kind = str(spline_type or "REGULARIZED").upper()
    if kind not in ("REGULARIZED", "TENSION"):
        _refuse("Spline", "spline_type", spline_type)
    w = float(weight if weight is not None else 0.1)
    if w <= 0:
        raise ValueError("Spline: the weight must be positive.")
    xs, ys, zs = _points(in_point_features, z_field, "Spline")
    if len({(x, y) for x, y in zip(xs, ys)}) != len(xs):
        _refuse("Spline", "in_point_features", in_point_features, "coincident points make the system singular")
    if len(xs) > int(number_points or 12):
        _refuse("Spline", "number_points", number_points, f"{len(xs)} points make more than one region, and how a region "
                "is widened to eight points is not documented")
    grid = _point_grid(in_point_features, cell_size, xs, ys, "Spline")
    n = len(xs)
    trend = [lambda x, y: np.ones_like(np.asarray(x, dtype=np.float64))]
    if kind == "REGULARIZED":
        trend += [lambda x, y: np.asarray(x, dtype=np.float64), lambda x, y: np.asarray(y, dtype=np.float64)]
    m = len(trend)
    x0, y0 = xs.mean(), ys.mean()
    u, v = xs - x0, ys - y0
    system = np.zeros((n + m, n + m))
    system[:n, :n] = _spline_basis(kind, np.hypot(u[:, None] - u, v[:, None] - v), w)
    for k, f in enumerate(trend):
        system[:n, n + k] = system[n + k, :n] = f(u, v)
    right = np.concatenate([zs, np.zeros(m)])
    solution = np.linalg.solve(system, right)
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx - x0, cy - y0)
    surface = (_spline_basis(kind, np.hypot(gx[..., None] - u, gy[..., None] - v), w) * solution[:n]).sum(axis=-1)
    for k, f in enumerate(trend):
        surface = surface + solution[n + k] * f(gx, gy)
    return Raster._of(surface.astype(np.float32), np.zeros(surface.shape, bool), grid)


_REFUSED.pop("Spline", None)


# ---- round ten (ADR-224 §5.6): Percentile across rasters


@_equivalent("sample quantiles: nearest rank (type 1) and linear interpolation (type 7)",
             "Hyndman and Fan 1996, The American Statistician 50(4)")
def Percentile(rasters, percentile_value=90, percentile_interpolation_type="AUTO_DETECT", extent_type="FirstOf",
               cellsize_type="FirstOf", ignore_nodata=False, process_as_multiband=False):
    """EQUIVALENT: the percentile of the rasters' values in each cell, as one of Hyndman and Fan's (1996) sample
    quantiles: ``NEAREST`` is their type 1, the value at rank ⌈p·n/100⌉ (an actual input value, keeping the input's
    pixel type, as documented); ``LINEAR`` is their type 7, interpolating between the two values around position
    1 + (n − 1)·p/100 (floating point, as documented). ``AUTO_DETECT`` takes NEAREST for integer inputs and LINEAR for
    floating point, as documented. NoData in any input makes the cell NoData unless ``ignore_nodata`` is True.

    Refused: ``process_as_multiband``; rasters on different grids except under IntersectionOf."""
    if process_as_multiband:
        _refuse("Percentile", "process_as_multiband", process_as_multiband)
    opened = _same_grid_or_intersection(list(rasters), extent_type, cellsize_type, "Percentile")
    arrays, mask, grid = _many(opened, "Percentile")
    integer = all(isinstance(r, Raster) and r.isInteger for r in opened)
    kind = str(percentile_interpolation_type or "AUTO_DETECT").upper()
    if kind == "AUTO_DETECT":
        kind = "NEAREST" if integer else "LINEAR"
    if kind not in ("NEAREST", "LINEAR"):
        _refuse("Percentile", "percentile_interpolation_type", percentile_interpolation_type)
    from ._raster import _align
    raw, masks, _ = _align([r for r in opened if isinstance(r, Raster)], "Percentile")
    stack = np.stack([np.where(m, np.nan, a.astype(np.float64)) for a, m in zip(raw, masks)])
    p = float(percentile_value)
    method = "inverted_cdf" if kind == "NEAREST" else "linear"
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        value = np.nanpercentile(stack, p, axis=0, method=method)
    empty = np.isnan(value)
    if not ignore_nodata:
        empty |= np.isnan(stack).any(axis=0)
    out = np.where(empty, 0, value)
    return Raster._of(out.astype(np.int64) if kind == "NEAREST" and integer else out, empty, grid)


_REFUSED.pop("Percentile", None)


# ---- round eleven (ADR-224 §5.6): RescaleByFunction and its transformation functions


class _Tf:
    """A transformation function's parameters, by the names and order of its arcpy class."""
    _shape = ()
    _defaults = {}

    def __init__(self, *args, **kwargs):
        names = [*self._shape, "lowerThreshold", "valueBelowThreshold", "upperThreshold", "valueAboveThreshold"]
        values = {**{n: self._defaults.get(n) for n in names}, **dict(zip(names, args)), **kwargs}
        for name in names:
            setattr(self, name, values.get(name))


def _tf_class(name, shape, defaults=None, doc=""):
    cls = type(name, (_Tf,), {"_shape": shape, "_defaults": defaults or {},
                              "__doc__": f"arcpy.sa.{name}({', '.join(shape)}, lowerThreshold, valueBelowThreshold, "
                                         f"upperThreshold, valueAboveThreshold){': ' + doc if doc else ''}"})
    cls.__module__ = __name__
    return cls


TfExponential = _tf_class("TfExponential", ("shift", "baseFactor"))
TfGaussian = _tf_class("TfGaussian", ("midpoint", "spread"))
TfLarge = _tf_class("TfLarge", ("midpoint", "spread"), {"spread": 5})
TfLinear = _tf_class("TfLinear", ("minimum", "maximum"))
TfLogarithm = _tf_class("TfLogarithm", ("shift", "factor"))
TfLogisticDecay = _tf_class("TfLogisticDecay", ("minimum", "maximum", "yInterceptPercent"), {"yInterceptPercent": 99.0})
TfLogisticGrowth = _tf_class("TfLogisticGrowth", ("minimum", "maximum", "yInterceptPercent"), {"yInterceptPercent": 1.0})
TfMSLarge = _tf_class("TfMSLarge", ("meanMultiplier", "STDMultiplier"), {"meanMultiplier": 1.0, "STDMultiplier": 1.0})
TfMSSmall = _tf_class("TfMSSmall", ("meanMultiplier", "STDMultiplier"), {"meanMultiplier": 1.0, "STDMultiplier": 1.0})
TfNear = _tf_class("TfNear", ("midpoint", "spread"))
TfPower = _tf_class("TfPower", ("shift", "exponent"))
TfSmall = _tf_class("TfSmall", ("midpoint", "spread"), {"spread": 5})
TfSymmetricLinear = _tf_class("TfSymmetricLinear", ("minimum", "maximum"))


def _transform(f, x, low, high, from_scale, to_scale, everything):
    """f(x) by Esri's *Formulas for the transformation functions*, each default fitted to ``low`` and ``high`` (the
    thresholds, or the input's minimum and maximum) as that page gives it."""
    ln, ex = math.log, math.exp
    name = type(f).__name__
    rising = to_scale > from_scale
    mid = f.midpoint if getattr(f, "midpoint", None) is not None else (high + low) / 2 if hasattr(f, "midpoint") else None
    if name == "TfExponential":
        shift = f.shift if f.shift is not None else (
            (low * ln(to_scale) - high * ln(from_scale)) / (ln(to_scale) - ln(from_scale)) if rising else
            (low * ln(from_scale) - high * ln(to_scale)) / (ln(from_scale) - ln(to_scale)))
        base = f.baseFactor if f.baseFactor is not None else (
            (ln(to_scale) - ln(from_scale)) / (high - low) if rising else (ln(from_scale) - ln(to_scale)) / (high - low))
        return np.exp((x - shift) * base)
    if name == "TfLogarithm":
        shift = f.shift if f.shift is not None else (
            (low * ex(to_scale) - high * ex(from_scale)) / (ex(to_scale) - ex(from_scale)) if rising else
            (low * ex(from_scale) - high * ex(to_scale)) / (ex(from_scale) - ex(to_scale)))
        factor = f.factor if f.factor is not None else (
            (ex(to_scale) - ex(from_scale)) / (high - low) if rising else (ex(from_scale) - ex(to_scale)) / (high - low))
        with np.errstate(all="ignore"):
            return np.log((x - shift) * factor)
    if name == "TfGaussian":
        spread = f.spread if f.spread is not None else ln(10) * 4 / (mid - low) ** 2
        return np.exp(-spread * (x - mid) ** 2)
    if name == "TfNear":
        spread = f.spread if f.spread is not None else 36 / (mid - low) ** 2
        return 1.0 / (1.0 + spread * (x - mid) ** 2)
    if name in ("TfLarge", "TfSmall"):
        with np.errstate(all="ignore"):
            return 1.0 / (1.0 + np.power(x / mid, -f.spread if name == "TfLarge" else f.spread))
    lo = f.minimum if getattr(f, "minimum", None) is not None else low
    hi = f.maximum if getattr(f, "maximum", None) is not None else high
    if name == "TfLinear":
        diff = hi - lo
        if lo < hi:
            return np.where(x < lo, 0.0, np.where(x > hi, 1.0, (x - lo) / diff))
        return np.where(x > lo, 0.0, np.where(x < hi, 1.0, (x - lo) / diff))
    if name == "TfSymmetricLinear":
        hdiff = 0.5 * (hi - lo)
        midp = lo + hdiff
        if lo < hi:
            return np.where(x < lo, 0.0, np.where(x < midp, (x - lo) / hdiff, np.where(x > hi, 0.0, (hi - x) / hdiff)))
        return np.where(x < hi, 1.0, np.where(x < midp, (x - midp) / hdiff, np.where(x > lo, 1.0, (midp - x) / hdiff)))
    if name in ("TfLogisticGrowth", "TfLogisticDecay"):
        c = 100.0
        a = c / float(f.yInterceptPercent) - 1
        b = ln(a) / (0.5 * (hi + lo) - lo)
        sign = -1 if name == "TfLogisticGrowth" else 1
        return c / (1 + a * np.exp(sign * (x - lo) * b))
    if name in ("TfMSLarge", "TfMSSmall"):
        n_means = float(f.meanMultiplier) * everything.mean()
        n_std = float(f.STDMultiplier) * everything.std()
        with np.errstate(all="ignore"):
            ratio = n_std / (x - n_means + n_std)
        return np.where(x > n_means, 1 - ratio, 0.0) if name == "TfMSLarge" else np.where(x > n_means, ratio, 1.0)
    if name == "TfPower":
        shift = f.shift if f.shift is not None else (
            (low - 1 if from_scale == 1 else low) if rising else (low - 1 if to_scale == 1 else low))
        if f.exponent is not None:
            exponent = f.exponent
        elif rising:
            exponent = (1 if to_scale <= 1 else ln(to_scale) / (high - shift)) if from_scale == 0 else \
                (ln(to_scale) / ln(high - shift) if from_scale == 1 else 2)
        else:
            exponent = (1 if from_scale <= 1 else ln(from_scale) / (high - shift)) if to_scale == 0 else \
                (ln(from_scale) / ln(high - shift) if to_scale == 1 else 2)
        with np.errstate(all="ignore"):
            return np.power(x - shift, exponent)
    _refuse("RescaleByFunction", "transformation_function", name)


@_equivalent("linear rescaling of the transformed values onto the evaluation scale",
             "the min-max normalisation of the transformed cells; the functions and their fitted defaults are Esri's "
             "Formulas for the transformation functions, given as text")
def RescaleByFunction(in_raster, transformation_function=None, from_scale=1, to_scale=10):
    """EQUIVALENT: each cell between the lower and upper thresholds (by default the input's minimum and maximum) is
    transformed by the function as Esri's *Formulas for the transformation functions* gives it — with every default
    shape parameter fitted to the thresholds as that page writes — and the transformed values are rescaled linearly so
    that the smallest of them over those cells becomes ``from_scale`` and the largest ``to_scale``, as *How Rescale by
    Function works* describes. Cells below the lower threshold take ``valueBelowThreshold`` (by default
    ``from_scale``), cells above the upper one ``valueAboveThreshold`` (by default ``to_scale``); ``"NODATA"`` makes
    them NoData. The default function is TfMSSmall; the output is floating point.

    The equivalent part is the rescaling's endpoints: the page names the minimum and maximum transformed values without
    saying over which cells; here they are those of the cells transformed.

    INFERRED: a cell where the function has no value (the logarithm of a negative number) is NoData; MSSmall's and
    MSLarge's mean and standard deviation are of every cell, the standard deviation the population one."""
    _refuse_environment("arcpy.sa.RescaleByFunction")
    source = as_raster(in_raster, "RescaleByFunction")
    f = transformation_function if transformation_function is not None else TfMSSmall()
    if not isinstance(f, _Tf):
        _refuse("RescaleByFunction", "transformation_function", f, "only the Tf classes are built")
    lo_s, hi_s = float(from_scale), float(to_scale)
    if lo_s == hi_s or lo_s < 0 or hi_s < 0:
        raise ValueError("RescaleByFunction: from_scale and to_scale must differ and not be negative.")
    values = source._values().astype(np.float64)
    low = float(f.lowerThreshold) if f.lowerThreshold is not None else values.min()
    high = float(f.upperThreshold) if f.upperThreshold is not None else values.max()
    x = source._array.astype(np.float64)
    within = (x >= low) & (x <= high) & ~source._mask
    with np.errstate(all="ignore"):
        transformed = np.asarray(_transform(f, x, low, high, lo_s, hi_s, values), dtype=np.float64)
    usable = within & np.isfinite(transformed)
    out = np.zeros(x.shape)
    mask = source._mask | (within & ~np.isfinite(transformed))
    if usable.any():
        fmin, fmax = transformed[usable].min(), transformed[usable].max()
        scaled = lo_s + (transformed - fmin) / ((fmax - fmin) or 1.0) * (hi_s - lo_s) if fmax > fmin else \
            np.full(x.shape, lo_s)
        out = np.where(usable, scaled, out)
    for side, value, default in ((x < low, f.valueBelowThreshold, lo_s), (x > high, f.valueAboveThreshold, hi_s)):
        side = side & ~source._mask
        if isinstance(value, str) and value.upper() == "NODATA":
            mask |= side
        else:
            out = np.where(side, float(value) if value is not None else default, out)
    return Raster._of(np.where(mask, 0, out), mask, source._grid)


for _built in ("RescaleByFunction", "TfExponential", "TfGaussian", "TfLarge", "TfLinear", "TfLogarithm",
               "TfLogisticDecay", "TfLogisticGrowth", "TfMSLarge", "TfMSSmall", "TfNear", "TfPower", "TfSmall",
               "TfSymmetricLinear"):
    _REFUSED.pop(_built, None)


# ---- round twelve (ADR-224 §5.6): Darcy flow


def _darcy(in_head_raster, in_porosity_raster, in_thickness_raster, in_transmissivity_raster, tool):
    """Face fluxes U = −T ∇h with T the harmonic mean of the two cells' transmissivities and ∇h the difference of heads
    over the cell spacing (as How Darcy Flow and Darcy Velocity work writes Darcy's law and the face gradient), the
    cell-centre seepage velocity as the mean of the two faces' fluxes divided by the cell's porosity and thickness, and
    the volume balance residual as the net discharge Q = U·(face width) out of each cell."""
    rasters = [as_raster(r, tool) for r in (in_head_raster, in_porosity_raster, in_thickness_raster, in_transmissivity_raster)]
    from ._raster import _align
    (h, n, b, t), masks, grid = _align(rasters, tool)
    mask = np.logical_or.reduce(masks)
    if mask.any():
        _refuse(tool, "in_head_raster", in_head_raster, "how NoData cells enter the fluxes is not documented")
    if not math.isclose(grid.width, grid.height):
        _refuse(tool, "in_head_raster", in_head_raster, "the scheme is built for square cells")
    h, n, b, t = (np.asarray(a, dtype=np.float64) for a in (h, n, b, t))
    d = grid.width
    rows, columns = h.shape
    if rows < 3 or columns < 3:
        raise ValueError(f"{tool} needs at least 3 rows and 3 columns.")
    harmonic = lambda a, c: 2 * a * c / (a + c)  # noqa: E731
    # Faces between columns (x, east positive) and between rows (y, north positive: row r−1 is north of r).
    ux = -harmonic(t[:, :-1], t[:, 1:]) * (h[:, 1:] - h[:, :-1]) / d          # face between column k and k+1
    uy = -harmonic(t[1:, :], t[:-1, :]) * (h[:-1, :] - h[1:, :]) / d          # face between row r (south) and r−1
    vx = np.zeros(h.shape)
    vy = np.zeros(h.shape)
    vx[:, 1:-1] = (ux[:, :-1] + ux[:, 1:]) / 2
    vy[1:-1, :] = (uy[:-1, :] + uy[1:, :]) / 2
    vx, vy = vx / (n * b), vy / (n * b)
    residual = np.zeros(h.shape)
    residual[:, :-1] += ux * d          # out through the east face
    residual[:, 1:] -= ux * d           # in through the west face of the next cell
    residual[1:, :] += uy * d           # out through the north face (row r to r−1)
    residual[:-1, :] -= uy * d
    # Bounding cells copy the nearest interior cell's velocity, as documented.
    for a in (vx, vy, residual):
        a[0, :], a[-1, :] = a[1, :], a[-2, :]
        a[:, 0], a[:, -1] = a[:, 1], a[:, -2]
    direction = np.degrees(np.arctan2(vx, vy)) % 360.0
    magnitude = np.hypot(vx, vy)
    return residual, direction, magnitude, grid


_DARCY_SOURCE = ("Darcy's law on a finite-difference grid with harmonic-mean face transmissivity, as How Darcy Flow and "
                 "Darcy Velocity work writes it; Anderson, Woessner and Hunt 2015, Applied Groundwater Modeling, ch. 3")


@_equivalent("finite-difference Darcy flux and volume balance", _DARCY_SOURCE)
def DarcyFlow(in_head_raster, in_porosity_raster, in_thickness_raster, in_transmissivity_raster, out_direction_raster=None,
              out_magnitude_raster=None):
    """EQUIVALENT: the volume balance residual of steady flow — each cell's net discharge out through its four faces,
    with the face flux U = −T·Δh/Δx from the harmonic mean of the two cells' transmissivities, as the page writes
    Darcy's law, the face gradient and the harmonic average. ``out_direction_raster`` receives the seepage velocity's
    direction in compass degrees clockwise from north, ``out_magnitude_raster`` its magnitude; the velocity at a cell
    centre is the mean of its two faces' fluxes in each axis divided by the cell's porosity and thickness, as the page
    says. Bounding cells copy the nearest interior cell, as documented.

    Refused: NoData in any input; rectangular cells.

    INFERRED: the residual is outflow minus inflow (the page shows it only as an image); the bounding cells' residual
    is copied as their velocity is."""
    _refuse_environment("arcpy.sa.DarcyFlow")
    residual, direction, magnitude, grid = _darcy(in_head_raster, in_porosity_raster, in_thickness_raster,
                                                  in_transmissivity_raster, "DarcyFlow")
    blank = np.zeros(residual.shape, bool)
    if _given(out_direction_raster):
        Raster._of(direction.astype(np.float32), blank.copy(), grid).save(out_direction_raster)
    if _given(out_magnitude_raster):
        Raster._of(magnitude.astype(np.float32), blank.copy(), grid).save(out_magnitude_raster)
    return Raster._of(residual.astype(np.float32), blank, grid)


@_equivalent("finite-difference Darcy seepage velocity", _DARCY_SOURCE)
def DarcyVelocity(in_head_raster, in_porosity_raster, in_thickness_raster, in_transmissivity_raster,
                  out_magnitude_raster=None):
    """EQUIVALENT: the seepage velocity's direction, in compass degrees clockwise from north (answered), and its
    magnitude (``out_magnitude_raster``), computed as DarcyFlow computes them."""
    _refuse_environment("arcpy.sa.DarcyVelocity")
    _, direction, magnitude, grid = _darcy(in_head_raster, in_porosity_raster, in_thickness_raster,
                                           in_transmissivity_raster, "DarcyVelocity")
    blank = np.zeros(direction.shape, bool)
    if _given(out_magnitude_raster):
        Raster._of(magnitude.astype(np.float32), blank.copy(), grid).save(out_magnitude_raster)
    return Raster._of(direction.astype(np.float32), blank, grid)


for _built in ("DarcyFlow", "DarcyVelocity"):
    _REFUSED.pop(_built, None)


# ---- round thirteen (ADR-224 §5.6): MajorityFilter

_RING = [(-1, 0), (-1, 1), (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1)]  # N, NE, E, SE, S, SW, W, NW
_CROSS = [(-1, 0), (0, 1), (1, 0), (0, -1)]                                       # N, E, S, W


def _contiguous(positions, eight):
    """Whether the neighbour positions form one group: on the eight-cell ring two cells join when they share an edge;
    of the four orthogonal neighbours two join when they share a corner (are not opposite), as the page defines it."""
    positions = list(positions)
    if len(positions) <= 1:
        return True
    seen, todo = {positions[0]}, [positions[0]]
    while todo:
        a = todo.pop()
        for b in positions:
            if b in seen:
                continue
            touching = (abs(a[0] - b[0]) + abs(a[1] - b[1]) == 1) if eight else (a[0] != -b[0] or a[1] != -b[1])
            if touching:
                seen.add(b)
                todo.append(b)
    return len(seen) == len(positions)


@_equivalent("majority filtering with a contiguity test", "the page's own replacement rule, with contiguity as "
             "connectedness in the neighbour ring (Rosenfeld 1970, Journal of the ACM 17(1))")
def MajorityFilter(in_raster, number_neighbors="FOUR", majority_definition="MAJORITY"):
    """EQUIVALENT: a cell is replaced by a value held by enough of its neighbours, contiguous about it, as the page
    documents: MAJORITY needs 3 of 4 or 5 of 8, HALF 2 of 4 or 4 of 8; with EIGHT neighbours contiguous means sharing an
    edge, with FOUR sharing a corner; under HALF, two equal halves leave the cell as it is when it holds one of them. On
    the raster's edge, with FOUR neighbours a cell needs two matching neighbours; with EIGHT a corner needs all three and
    an edge cell three contiguous ones including one along the edge, as documented. The equivalent part is the
    contiguity test: connectedness of the matching cells in the neighbour ring (Rosenfeld 1970).

    INFERRED: the processing cell itself does not count; NoData neighbours match nothing and a NoData cell stays NoData;
    two equal halves of which the cell holds neither leave it unchanged; edge cells use the same rule under HALF."""
    _refuse_environment("arcpy.sa.MajorityFilter")
    source = as_raster(in_raster, "MajorityFilter")
    if not source.isInteger:
        raise ValueError("MajorityFilter takes an integer raster.")
    eight = str(number_neighbors or "FOUR").upper() == "EIGHT"
    half = str(majority_definition or "MAJORITY").upper() == "HALF"
    a, mask = source._array, source._mask
    rows, columns = a.shape
    offsets = _RING if eight else _CROSS
    out = a.copy()
    for r in range(rows):
        for c in range(columns):
            if mask[r, c]:
                continue
            inside = [(dr, dc) for dr, dc in offsets if 0 <= r + dr < rows and 0 <= c + dc < columns]
            edge = len(inside) < len(offsets)
            groups = {}
            for dr, dc in inside:
                if not mask[r + dr, c + dc]:
                    groups.setdefault(int(a[r + dr, c + dc]), []).append((dr, dc))
            if edge:
                need = 3 if eight else 2
            else:
                need = (4 if half else 5) if eight else (2 if half else 3)
            qualifying = [v for v, cells_ in groups.items() if len(cells_) >= need and _contiguous(cells_, eight)]
            if eight and edge and len(inside) == 5:
                along = {(0, 1), (0, -1)} if r in (0, rows - 1) else {(1, 0), (-1, 0)}
                qualifying = [v for v in qualifying if along & set(groups[v])]
            if not qualifying:
                continue
            if len(qualifying) > 1:
                continue  # two equal halves: unchanged whether or not the cell holds one of them
            if qualifying[0] != int(a[r, c]):
                out[r, c] = qualifying[0]
    return Raster._of(out, mask.copy(), source._grid)


_REFUSED.pop("MajorityFilter", None)


# ---- round fourteen (ADR-224 §5.6): what stays refused after the equivalents, and why no standard method fits

_REFUSED.update({
    "BoundaryClean": "its smoothing expands and shrinks zones in an order set by sort_type and number_of_runs, which "
                     "the page describes but no standard morphological operation defines; an opening or closing would "
                     "not honour sort_type",
    "Thin": "a standard skeletonisation (Zhang and Suen 1984) cannot honour its corners (ROUND or SHARP), filter and "
            "maximum_thickness parameters, which the page does not define",
})
_REFUSED.update({name: ("Fu and Rich's (2000) model is given only in part: the direct and diffuse formulas are text, "
                        "but the sun map and sky map are discretised by sky_size and the zenith and azimuth divisions "
                        "in ways defined only in Fu 2000 (a thesis) and the hemispherical viewshed in Rich et al. 1994; "
                        "an equivalent evaluating the sun continuously would leave sky_size and the divisions unhonoured")
                 for name in ("AreaSolarRadiation", "PointsSolarRadiation", "RasterSolarRadiation",
                              "FeatureSolarRadiation", "SolarRadiationGraphics", "TimeMultipleDays", "TimeSpecialDays",
                              "TimeWholeYear", "TimeWithinDay")})
_REFUSED["GeodesicFlowDirection"] = ("its D8 on the ellipsoid ties with Greenlee's unpublished lookup table, and no "
                                     "published source defines Esri's geodesic flat and tie handling")


def InterpolateShape(in_surface, in_feature_class, out_feature_class, sample_distance=None, z_factor=1, method="BILINEAR",
                     vertices_only="DENSIFY", pyramid_level_resolution=0, preserve_features="EXCLUDE",
                     interpolate_m_value="NO_INTERPOLATE_M_VALUE"):
    """arcpy.sa.InterpolateShape: the same tool as arcpy.ddd.InterpolateShape, which answers it; see its docstring."""
    from . import ddd
    return ddd.InterpolateShape(in_surface, in_feature_class, out_feature_class, sample_distance, z_factor, method,
                                vertices_only, pyramid_level_resolution, preserve_features, interpolate_m_value)


_REFUSED.pop("InterpolateShape", None)


# ---- raster attribute tables (ADR-224): the tools that read or write them


def _rat_values(raster, field, tool, array=None):
    """Each cell's value in a field of the raster's attribute table, looked up by the cell's value in the table's Value
    field, as an object array, and a mask of the cells with no row or no value there."""
    if raster._rat is None:
        raise ValueError(f"{tool}: '{field}' is read from the raster's attribute table, and the raster has none; build "
                         "one first (management.BuildRasterAttributeTable).")
    try:
        at, value_at = raster._rat.index(field), raster._rat.index("Value")
    except KeyError:
        raise ValueError(f"{tool}: the raster's attribute table has no field '{field}' (or no Value field).") from None
    lookup = {row[value_at]: row[at] for row in raster._rat.rows}
    cells = np.asarray(raster._array if array is None else array)
    flat = [lookup.get(int(v)) if float(v).is_integer() else None for v in cells.ravel()]
    looked = np.empty(cells.size, dtype=object)
    looked[:] = flat
    looked = looked.reshape(cells.shape)
    missing = np.array([v is None for v in flat], dtype=bool).reshape(cells.shape)
    return looked, missing


def _reclassify_by_field(raster, reclass_field, remap, missing_values):
    """Reclassify on a field of the attribute table: each cell's row's value in that field is matched against the
    remap — a RemapValue's old values may be text — and the cell takes the new value. A cell whose field value the
    remap does not name keeps its own value under DATA and is NoData under NODATA, as for the Value field.

    INFERRED: a cell with no row in the table, or no value in the field, counts as unmapped."""
    looked, _ = _rat_values(raster, reclass_field, "Reclassify")
    keep = str(missing_values or "DATA").upper() == "DATA"
    out = np.where(raster._mask, 0, raster._array).astype(np.float64)
    mask = raster._mask.copy()
    done = np.zeros(out.shape, bool)
    cells = looked.ravel()
    ranged = isinstance(remap, RemapRange)
    for row in remap.remapTable:
        new = row[-1]
        if ranged:
            low, high = sorted((float(row[0]), float(row[1])))
            hit = [v is not None and not isinstance(v, str) and low <= float(v) <= high for v in cells]
        else:
            old = row[0]
            hit = [v is not None and (str(v) == str(old) if isinstance(v, str) or isinstance(old, str)
                                      else float(v) == float(old)) for v in cells]
        hit = np.array(hit, dtype=bool).reshape(out.shape) & ~raster._mask & ~done
        if str(new).upper() == "NODATA":
            mask |= hit
        else:
            out[hit] = float(new)
        done |= hit
    if not keep:
        mask |= ~done
    integer = raster.isInteger and all(str(r[-1]).upper() == "NODATA" or float(r[-1]).is_integer()
                                       for r in remap.remapTable)
    return Raster._of(np.where(mask, 0, out).astype(np.int32 if integer else np.float64), mask, raster._grid)


def Lookup(in_raster, lookup_field):
    """A raster of the values of a field of the input's attribute table. As documented: an integer field gives an
    integer raster, with a table of its values; another numeric field gives a floating-point raster; a text field
    keeps the input's values and carries the field into the output's table, beside Value and Count.

    INFERRED: a cell with no row, or no value in the field, is NoData; the output table's Count counts the cells of
    each value."""
    from ._raster import _Rat, _value_count
    _refuse_environment("arcpy.sa.Lookup")
    raster = as_raster(in_raster, "Lookup")
    looked, missing = _rat_values(raster, lookup_field, "Lookup")
    name, kind, _ = raster._rat.fields[raster._rat.index(lookup_field)]
    mask = raster._mask | missing
    if kind == "String":
        counts = _value_count(raster._array, mask)
        texts = {v: t for v, t in zip(raster._rat.column("Value"), raster._rat.column(name))}
        result = Raster._of(np.where(mask, 0, raster._array), mask, raster._grid)
        result._rat = _Rat([*counts.fields, (name, "String", 0)], [[v, c, texts.get(v)] for v, c in counts.rows])
        return result
    values = np.array([0.0 if v is None else float(v) for v in looked.ravel()]).reshape(looked.shape)
    if kind == "Integer":
        out = np.where(mask, 0, values).astype(np.int32)
        result = Raster._of(out, mask, raster._grid)
        result._rat = _value_count(out, mask)
        return result
    return Raster._of(np.where(mask, 0, values), mask, raster._grid)


def _dataset_name(raster, i, taken):
    """A raster's name as Combine names its field: its dataset's base name, characters other than letters, digits and
    the underscore replaced by underscores, a name already taken given an index.

    INFERRED: a temporary raster, which has no name, is called raster1, raster2 … by its position; the index is
    appended as _1, _2 …"""
    import os
    base = os.path.splitext(os.path.basename(raster.catalogPath))[0] if raster.catalogPath else f"raster{i + 1}"
    name = re.sub(r"[^0-9A-Za-z_]", "_", base)
    label, k = name, 0
    while label.lower() in taken:
        k += 1
        label = f"{name}_{k}"
    taken.add(label.lower())
    return label


@_equivalent("unique combinations numbered from 1 in the order a row-major scan first meets them",
             "the scan order the Region Group page documents for numbering its regions")
def Combine(in_rasters):
    """EQUIVALENT: unique combinations numbered in scan order (the method), after Region Group's documented numbering.

    Each unique combination of the inputs' values, cell by cell, gets a unique integer; the output's attribute table
    has Value, Count and a field per input holding that input's value, named after the input as documented.
    Floating-point inputs are truncated to integers, as documented. The page does not say which combination gets which
    number; here they are numbered from 1 in the order a scan, left to right and top to bottom, first meets them.

    INFERRED: a cell NoData in any input is NoData."""
    from ._raster import _Rat, _align
    _refuse_environment("arcpy.sa.Combine")
    rasters = in_rasters if isinstance(in_rasters, (list, tuple)) else [in_rasters]
    opened = [as_raster(r, "Combine") if not isinstance(r, Raster) else r for r in rasters]
    arrays, masks, grid = _align(opened, "Combine")
    mask = np.logical_or.reduce(masks)
    stack = np.stack([np.trunc(np.asarray(a, dtype=np.float64)).astype(np.int64) for a in arrays], axis=-1)
    numbers, counts = {}, {}
    out = np.zeros(mask.shape, np.int32)
    for r in range(mask.shape[0]):
        for c in range(mask.shape[1]):
            if mask[r, c]:
                continue
            key = tuple(int(v) for v in stack[r, c])
            if key not in numbers:
                numbers[key] = len(numbers) + 1
            out[r, c] = numbers[key]
            counts[key] = counts.get(key, 0) + 1
    taken = {"value", "count"}
    names = [_dataset_name(raster, i, taken) for i, raster in enumerate(opened)]
    result = Raster._of(out, mask, grid)
    result._rat = _Rat([("Value", "Integer", 5), ("Count", "Integer", 1), *[(n, "Integer", 0) for n in names]],
                       [[number, counts[key], *key] for key, number in numbers.items()])
    return result


def CutFill(in_before_surface, in_after_surface, z_factor=1):
    """The regions of cut, fill and no change between two surfaces, as *How Cut Fill works* documents. The change is
    Z before less Z after, times ``z_factor``: positive where material was cut, negative where it was filled, zero
    where nothing changed. Each edge-connected area of one kind is a region with its own value, and the output's
    attribute table holds Value, Count, Volume — the change times the cell area, summed over the region — and Area,
    the region's cell count times the cell area.

    INFERRED: edge-connected is four-connected; regions are numbered from 1 in scan order, left to right and top to
    bottom, as Region Group documents; a cell NoData in either surface is NoData."""
    from scipy.ndimage import label
    from ._raster import _Rat, _align
    _refuse_environment("arcpy.sa.CutFill")
    before = in_before_surface if isinstance(in_before_surface, Raster) else as_raster(in_before_surface, "CutFill")
    after = in_after_surface if isinstance(in_after_surface, Raster) else as_raster(in_after_surface, "CutFill")
    (b, a), (bm, am), grid = _align([before, after], "CutFill")
    mask = bm | am
    change = (b.astype(np.float64) - a.astype(np.float64)) * float(z_factor)
    kind = np.sign(change)
    four = np.array([[0, 1, 0], [1, 1, 1], [0, 1, 0]])
    pieces = np.zeros(change.shape, np.int64)
    offset = 0
    for k in (-1.0, 0.0, 1.0):
        labelled, count = label((kind == k) & ~mask, four)
        pieces = np.where(labelled > 0, labelled + offset, pieces)
        offset += count
    order = {}
    flat_pieces = pieces.ravel()
    for flat in np.flatnonzero(flat_pieces):
        order.setdefault(int(flat_pieces[flat]), len(order) + 1)
    out = np.zeros(change.shape, np.int32)
    area = abs(grid.width * grid.height)
    rows = []
    for key, number in order.items():
        inside = pieces == key
        out[inside] = number
        count = int(inside.sum())
        rows.append([number, count, float(change[inside].sum() * area), float(count * area)])
    result = Raster._of(out, mask, grid)
    result._rat = _Rat([("Value", "Integer", 5), ("Count", "Integer", 1), ("Volume", "Double", 0),
                        ("Area", "Double", 0)], rows)
    return result


for _built in ("Lookup", "Combine", "CutFill"):
    _REFUSED.pop(_built, None)
    _NOT_YET.pop(_built, None)


# ---- multidimensional rasters (ADR-236): built in _mdimtools and _change, answered here and by arcpy.ia under
# arcpy's names; the reasons for the one still refused replace group 2's.

from . import _mdimtools as _md_tools  # noqa: E402

AggregateMultidimensionalRaster = _md_tools.AggregateMultidimensionalRaster
Anomaly = _md_tools.Anomaly
ArgStatistics = _md_tools.ArgStatistics
DimensionalMovingStatistics = _md_tools.DimensionalMovingStatistics
GenerateMultidimensionalAnomaly = _md_tools.GenerateMultidimensionalAnomaly
for _built in ("AggregateMultidimensionalRaster", "AnalyzeChangesUsingCCDC", "AnalyzeChangesUsingLandTrendr", "Anomaly",
               "ArgStatistics", "ComputeChange", "ComputeChangeRaster", "DetectChangeUsingChangeAnalysis",
               "DetectChangeUsingChangeAnalysisRaster", "DimensionalMovingStatistics", "FindArgumentStatistics",
               "GenerateMultidimensionalAnomaly", "GenerateTrend", "GenerateTrendRaster",
               "InterpolateFromSpatiotemporalPoints", "MultidimensionalPrincipalComponents",
               "MultidimensionalRasterCorrelation", "PredictUsingTrend", "PredictUsingTrendRaster", "RasterCollection",
               "RasterToXarray", "TrendToRGB", "XarrayToRaster"):
    _REFUSED.pop(_built, None)
    _NOT_YET.pop(_built, None)
# Subset and Foreach (D-329's sweep): built on ADR-236's model, in _mdmore.
from . import _mdmore as _md_more  # noqa: E402

Subset = _md_more.Subset
Foreach = _md_more.Foreach
Complex = _md_more.Complex  # on ADR-237's complex rasters
for _built in ("Subset", "Foreach", "SummarizeCategoricalRaster", "Complex"):
    _REFUSED.pop(_built, None)
    _NOT_YET.pop(_built, None)
# ---- Topo to Raster, Segment Mean Shift and the combinatorial tools (ADR-242)
from . import _combinatorial, _segment, _topo  # noqa: E402

CombinatorialAnd = _combinatorial.CombinatorialAnd
CombinatorialOr = _combinatorial.CombinatorialOr
CombinatorialXOr = _combinatorial.CombinatorialXOr
SegmentMeanShift = _equivalent(*_segment.SEGMENT_MEAN_SHIFT)(_segment.SegmentMeanShift)
SegMeanShift = _equivalent(*_segment.SEGMENT_MEAN_SHIFT)(_segment.SegMeanShift)
ComputeSegmentAttributes = _equivalent(*_segment.SEGMENT_ATTRIBUTES)(_segment.ComputeSegmentAttributes)
RemoveRasterSegmentTilingArtifacts = _equivalent(*_segment.TILING_ARTIFACTS)(
    _segment.RemoveRasterSegmentTilingArtifacts)
TopoPointElevation = _topo.TopoPointElevation
TopoContour = _topo.TopoContour
TopoStream = _topo.TopoStream
TopoSink = _topo.TopoSink
TopoBoundary = _topo.TopoBoundary
TopoLake = _topo.TopoLake
TopoCliff = _topo.TopoCliff
TopoExclusion = _topo.TopoExclusion
TopoCoast = _topo.TopoCoast


@_equivalent(*_topo.METHOD)
def TopoToRaster(in_topo_features, cell_size=None, extent=None, margin=20, minimum_z_value=None, maximum_z_value=None,
                 enforce="ENFORCE", data_type="CONTOUR", maximum_iterations=20, roughness_penalty=None,
                 discrete_error_factor=1, vertical_standard_error=0, tolerance_1=None, tolerance_2=None,
                 out_stream_features=None, out_sink_features=None, out_diagnostic_file=None, out_parameter_file=None,
                 profile_penalty=0, out_residual_feature=None, out_stream_cliff_error_feature=None,
                 out_contour_error_feature=None):
    """EQUIVALENT: ANUDEM after Hutchinson's published descriptions (1989; 2009; 2011); see grapy._topo's docstring
    for the method. Every parameter is the page's, with its defaults: margin 20 cells; z limits 20 percent below and
    above the data; ENFORCE; CONTOUR; 20 iterations; roughness 0 for CONTOUR and 0.5 for SPOT; discretisation error
    factor 1; vertical standard error 0; tolerances 2.5 and 100 for CONTOUR, 0 and 200 for SPOT; profile penalty 0.
    The cell size, when not given, is the extent's shorter side over 250.

    The outputs: the stream polylines with ``Code`` 1 (input stream not over a cliff), 2 (over a cliff, one cell
    long) and 3 (a chain that cleared a sink), as the page numbers them; the remaining sinks with their
    ``Elevation``; the diagnostic file (grapy's own layout: inputs, parameters, and per resolution and iteration the
    sinks found, cleared and remaining); the parameter file, in Topo to Raster by File's documented syntax; the
    residuals with ``Elevation``, ``Residual`` and ``Scaled`` (by the local discretisation error); stream and cliff
    errors with ``Code`` 1, 4, 5 and 8; contour errors with ``ErrorValue`` 1 (contours of different heights meeting,
    as documented) and 2 with ``Bias`` (a contour biased by more than five standard deviations).

    INFERRED (ADR-242 §5.4): the roughness functional's form and the λ rule; the resolutions; the first grid's slopes;
    the chain's shape; data in lakes, exclusions and outside the z limits unused; the special coast value, the z limit
    less 1; the output field names; residuals written when their scaled value exceeds 1; codes 4, 5, 6, 7, 9 and
    10–13 of the polylines and points never written, the equivalent not deriving lines from contour corners nor
    meeting those conditions; no failure at 8,000 sinks or at more points than cells, which are Pro's limits."""
    plan = _topo.plan_of(in_topo_features, cell_size, extent, margin, minimum_z_value, maximum_z_value, enforce,
                         data_type, maximum_iterations, roughness_penalty, discrete_error_factor,
                         vertical_standard_error, tolerance_1, tolerance_2, out_diagnostic_file, out_parameter_file,
                         profile_penalty)
    return _topo.run(plan, out_stream_features, out_sink_features, out_residual_feature,
                     out_stream_cliff_error_feature, out_contour_error_feature)


@_equivalent(*_topo.METHOD)
def TopoToRasterByFile(in_parameter_file, out_stream_features=None, out_sink_features=None, out_residual_feature=None,
                       out_stream_cliff_error_feature=None, out_contour_error_feature=None):
    """EQUIVALENT: TopoToRaster run from a parameter file in the syntax the page documents (``Contour <Path> <Item>``,
    ``ENFORCE ON``, ``TOLERANCES 2.5 100.0`` …); see TopoToRaster.

    INFERRED: a coastal polygon's line is ``Coast <Path>`` (the page's table writes ``Boundary <Path>`` for it, which
    would read back as a boundary); a path with spaces in double quotes; output keywords without paths ignored."""
    plan = _topo.read_parameter_file(in_parameter_file)
    return _topo.run(plan, out_stream_features, out_sink_features, out_residual_feature,
                     out_stream_cliff_error_feature, out_contour_error_feature, tool="TopoToRasterByFile")


for _built in ("CombinatorialAnd", "CombinatorialOr", "CombinatorialXOr", "SegmentMeanShift", "SegMeanShift",
               "ComputeSegmentAttributes", "RemoveRasterSegmentTilingArtifacts", "TopoToRaster", "TopoToRasterByFile",
               "TopoPointElevation", "TopoContour", "TopoStream", "TopoSink", "TopoBoundary", "TopoLake", "TopoCliff",
               "TopoExclusion", "TopoCoast"):
    _REFUSED.pop(_built, None)
    _NOT_YET.pop(_built, None)
_REFUSED["InterpolateRasterByDimension"] = (
    "arcpy documents no Python signature for it: there is no arcpy reference page, and the raster function page gives "
    "only its display labels, so its parameter names and order are unknown (ADR-236)")


# ---- ADR-245: the pages that answered 404 on 2026-10-29, read again at their new addresses on 2026-10-31
from . import _imagefns as _image_fns  # noqa: E402
from ._raster import RasterInfo as _RasterInfo  # noqa: E402

RasterInfo = _RasterInfo  # arcpy.sa.RasterInfo is arcpy.RasterInfo (arcpy/classes/rasterinfo-class.html)
UnitConversion = _image_fns.UnitConversion
HeatIndex = _image_fns.HeatIndex
WindChill = _image_fns.WindChill
SpectralConversion = _image_fns.SpectralConversion
VectorField = _image_fns.VectorField
InList = _image_fns.InList
ZonalRemap = _image_fns.ZonalRemap
ColormapToRGB = _image_fns.ColormapToRGB
ApplyEnvironment = _image_fns.ApplyEnvironment
RasterCellIterator = _image_fns.RasterCellIterator
ColorspaceConversion = _equivalent(*_image_fns.HSV)(_image_fns.ColorspaceConversion)
Speckle = _equivalent(*_image_fns.SPECKLE)(_image_fns.Speckle)
ElevationVoidFill = _equivalent(*_image_fns.VOID_FILL)(_image_fns.ElevationVoidFill)
LinearUnmixing = _equivalent(*_image_fns.UNMIXING)(_image_fns.LinearUnmixing)
Resample = _equivalent(*_image_fns.RESAMPLE)(_image_fns.Resample)
GNDVI = _index("GNDVI", ["nir_band_id", "green_band_id"], {"nir_band_id": 5, "green_band_id": 2},
               lambda nir, green: (nir - green) / (nir + green),
               "(NIR − Green) / (NIR + Green); the defaults are the page's (arcpy/image-analyst/gnvdi.html), bands 5 and 2.")
for _built in ("RasterInfo", "UnitConversion", "HeatIndex", "WindChill", "SpectralConversion", "VectorField", "InList",
               "ZonalRemap", "ColormapToRGB", "ApplyEnvironment", "RasterCellIterator", "ColorspaceConversion",
               "Speckle", "ElevationVoidFill", "LinearUnmixing", "Resample", "GNDVI"):
    _REFUSED.pop(_built, None)
    _NOT_YET.pop(_built, None)

_REFUSED.update({
    "MNDWI": "it has no reference page: arcpy/image-analyst/mndwi.html and arcpy/spatial-analyst/mndwi.html answered 404 "
             "at doc.esri.com and pro.arcgis.com (latest and 3.3), and neither arcpy's table of contents nor the "
             "Band Arithmetic raster function names an arcpy MNDWI function (checked 2026-10-31), so its parameter "
             "names, order and default band ids are unknown",
    "FloatDivide": "it has no reference page: tool-reference/spatial-analyst/float-divide and arcpy/spatial-analyst/"
                   "floatdivide answered 404 at doc.esri.com, at pro.arcgis.com for 2.9 to 3.5 and latest, and at "
                   "desktop.arcgis.com, and no table of contents names it (checked 2026-10-31); arcpy documents the / "
                   "(Division) operator, which grapy's Raster answers",
    "FloorDivide": "it has no reference page: tool-reference/spatial-analyst/floor-divide answered 404 at doc.esri.com, "
                   "at pro.arcgis.com for 2.9 to 3.5 and latest, and at desktop.arcgis.com, and no table of contents "
                   "names it (checked 2026-10-31); arcpy documents the // (Integer Division) operator, which grapy's "
                   "Raster answers",
    "VectorFieldRenderer": "its output is a raster \"displayed according to your rendering parameters\" — arrows, wind "
                           "barbs or ocean-current symbols drawn over thinned tiles — and its page "
                           "(arcpy/image-analyst/vector-field-renderer.html) does not say what pixel values that "
                           "rendering holds; VectorField builds the U-V and magnitude-direction bands",
    "ExportRasterFunctionTemplate": "it exports the raster function chain a Raster carries in its functions property, "
                                    "and grapy's rasters are computed when they are made and carry no function chain; "
                                    "the .rft.xml layout it can write is not published",
    "LabelBadBands": "its only effect is the IsBadBand band property (arcpy/image-analyst/labelbadbands.html), and "
                     "grapy's rasters carry no band properties to hold it — SubsetBands refuses exclude_bad_bands for "
                     "the same reason — while how ArcGIS stores the property in a saved raster is not documented",
    "Colormap": "its answer is the input's values with a colour map attached for display, and grapy's rasters carry no "
                "colour map (BuildRasterAttributeTable's convert_colormap is refused for the same reason); its named "
                "maps — Elevation, NDVI, NDVI2, NDVI3 — are described only in words, and its colour ramps by Pro's "
                "names (arcpy/image-analyst/colormap.html, help/analysis/raster-functions/colormap-function.html, "
                "read 2026-10-31)",
    "Render": "it attaches a rendering rule or colour map to a raster for a notebook's display; its page "
              "(arcpy/image-analyst/render.html) does not say what pixel values a saved rendered raster holds — "
              "whether stretched to 0–255, how gamma is applied — and its colour schemes are given by name in a PDF of "
              "swatches",
    "ShadedRelief": "it merges an elevation-coloured image with a hillshade into RGB, and neither its page nor the Shaded "
                    "Relief raster function says how the two are merged, nor gives the default colour ramp (Elevation #1) "
                    "or the SCALED z-factor equation that ps_power and psz_factor enter (read 2026-10-31)",
    "AspectSlope": "the Aspect-Slope raster function gives only the output's classes — below 20 flat, 21–30, 31–40, 41 "
                   "and above for low, moderate and high slope — and not how aspect and slope are coded into one value "
                   "nor the slope breaks (read 2026-10-31)",
    "ContrastBrightness": "its page and the Contrast And Brightness raster function (Pro's and ArcMap's, read "
                          "2026-10-31) give the offsets' range, −100 to 100, and no formula mapping an offset to pixel "
                          "values",
})


def __getattr__(name):
    if name.startswith("_"):
        raise AttributeError(name)
    why = _REFUSED.get(name)
    later = _NOT_YET.get(name)

    def missing(*_, **__):
        if why:
            raise NotBuiltError(f"arcpy.sa.{name} is not built in grapy: {why}.")
        raise NotBuiltError(f"arcpy.sa.{name} is not built in grapy yet" + (f": {later}." if later else "."))

    return missing
