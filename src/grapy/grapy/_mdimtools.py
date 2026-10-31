"""The multidimensional tools of arcpy.ia and arcpy.sa, on grapy's multidimensional raster (ADR-236).

Each is answered under arcpy's own name by ``ia.py`` and ``sa.py``. Where Esri's page gives the answer, the tool is
built to it; where the page names a method and leaves the formula out (the trend models are drawn as images), the tool
is built as the standard method of the literature, marked ``_runtime.equivalent`` (ADR-224 §5.6).

Shared choices (ADR-236 §5.3–5.5, INFERRED, listed in Q-204):

- **A time held in a cell** — ArgStatistics' and FindArgumentStatistics' dimension value, a change date — is a
  decimal year (2015-07-02T12:00 is 2015.5); a non-time dimension's value is itself.
- **A band index held in a cell** is one-based, as HighestPosition's is.
- **An aggregated slice's dimension value** is the start of its interval: the first instant of the hour, day, week
  (ISO, Monday), dekad, pentad, month, quarter or year, an INTERVAL_VALUE bin's start, an interval range's minimum; a
  recurring interval's (every January …) is its first occurrence in the series.
- **A moving window** at either end of the dimension takes the slices there are.
- **The standard deviation** divides by n (the population's), as CellStatistics' does.
"""
from __future__ import annotations

import math
import os

import numpy as np

from . import _mdim
from ._mdim import TIME, Cube, Variable
from ._runtime import NotBuiltError, equivalent

_ = math


def _refuse(tool, parameter, value, why=""):
    raise NotBuiltError(f"{tool}: {parameter} '{value}' is not built in grapy" + (f": {why}." if why else "."))


def _given(value):
    """A parameter given: not None, "" or "#" (compared by type, as a Raster's == is map algebra)."""
    return value is not None and not (isinstance(value, str) and value in ("", "#"))


def _ignore(value, default=True):
    """``ignore_nodata``: True, "DATA" or "IGNORE" ignore NoData; False, "NODATA" do not."""
    if value is None or value == "":
        return default
    if isinstance(value, str):
        return value.upper() in ("DATA", "TRUE", "IGNORE")
    return bool(value)


def _flag(value, on, default=False):
    if value is None or value == "":
        return default
    if isinstance(value, str):
        return value.upper() == on
    return bool(value)


def _upper(value, default):
    return str(value).upper() if _given(value) else default


def _dimension(cube, dimension, variables=None, tool="arcpy.ia"):
    if _given(dimension):
        return str(dimension)
    v = cube.chosen(variables)[0]
    return v.dims[0]


# ---- statistics over the first axis of (n, bands, rows, columns)


def _majority(stack, valid, minority=False):
    """The most (or least) frequent valid value along axis 0; ties take the lowest value, as documented for
    Dimensional Moving Statistics' Majority."""
    n = stack.shape[0]
    best = np.zeros(stack.shape[1:])
    best_count = np.full(stack.shape[1:], -1 if not minority else n + 1)
    for i in range(n):
        same = (stack == stack[i]) & valid & valid[i]
        count = same.sum(axis=0)
        here = valid[i]
        better = (count > best_count) if not minority else (count < best_count)
        tie = (count == best_count) & (stack[i] < best)
        take = here & (better | tie)
        best = np.where(take, stack[i], best)
        best_count = np.where(take, count, best_count)
    return best


def reduce(stack, mask, method, ignore_nodata=True, percentile=90.0, interpolation="LINEAR", wrap=360.0):
    """One statistic of each cell's values along the first axis, and its NoData mask."""
    method = method.upper()
    valid = ~mask
    values = np.where(valid, stack.astype(np.float64), np.nan)
    count = valid.sum(axis=0)
    empty = count == 0 if ignore_nodata else mask.any(axis=0)
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        if method == "MEAN":
            out = np.nanmean(values, axis=0)
        elif method in ("MAXIMUM", "MAX"):
            out = np.nanmax(values, axis=0)
        elif method in ("MINIMUM", "MIN"):
            out = np.nanmin(values, axis=0)
        elif method == "SUM":
            out = np.nansum(values, axis=0)
        elif method == "RANGE":
            out = np.nanmax(values, axis=0) - np.nanmin(values, axis=0)
        elif method in ("STD", "STANDARD_DEVIATION"):
            out = np.nanstd(values, axis=0)
        elif method == "MEDIAN":
            out = np.nanpercentile(values, 50, axis=0, method="inverted_cdf" if interpolation == "NEAREST" else "linear")
        elif method == "PERCENTILE":
            out = np.nanpercentile(values, float(percentile), axis=0,
                                   method="inverted_cdf" if interpolation == "NEAREST" else "linear")
        elif method in ("MAJORITY", "MINORITY"):
            out = _majority(np.where(valid, stack, 0).astype(np.float64), valid, method == "MINORITY")
        elif method == "VARIETY":
            ordered = np.sort(values, axis=0)
            out = (np.isfinite(ordered[:1])).sum(axis=0) + (np.diff(ordered, axis=0) > 0).sum(axis=0)
            out = out.astype(np.float64)
        elif method == "CIRCULAR_MEAN":
            angle = values * (2 * np.pi / float(wrap))
            s, c = np.nanmean(np.sin(angle), axis=0), np.nanmean(np.cos(angle), axis=0)
            out = np.mod(np.arctan2(s, c), 2 * np.pi) * float(wrap) / (2 * np.pi)
        else:
            raise ValueError(f"'{method}' is not a statistic grapy knows.")
    empty = empty | ~np.isfinite(out)
    return np.where(empty, 0, out), empty


def _interp(value, integer):
    kind = _upper(value, "AUTO_DETECT")
    if kind == "AUTO_DETECT":
        return "NEAREST" if integer else "LINEAR"
    if kind not in ("NEAREST", "LINEAR"):
        raise ValueError(f"'{value}' is not NEAREST, LINEAR or AUTO_DETECT.")
    return kind


def _integer(variable):
    values = variable.data[~variable.mask]
    return bool(values.size) and bool(np.all(values == np.round(values)))


# ---- intervals of time


_KEYWORDS = ("HOURLY", "DAILY", "WEEKLY", "DEKADLY", "PENTADLY", "MONTHLY", "QUARTERLY", "YEARLY", "RECURRING_DAILY",
             "RECURRING_WEEKLY", "RECURRING_MONTHLY", "RECURRING_QUARTERLY")


def period(value, keyword):
    """The interval a time falls in, as a key, and the interval's start."""
    import datetime as dt
    y, mo, d, h, yday, week = _mdim.parts(value)
    t = np.datetime64(value, "s").astype(dt.datetime)
    if keyword == "HOURLY":
        start = dt.datetime(y, mo, d, h)
    elif keyword == "DAILY":
        start = dt.datetime(y, mo, d)
    elif keyword == "WEEKLY":
        start = dt.datetime(y, mo, d) - dt.timedelta(days=t.weekday())
    elif keyword == "DEKADLY":
        start = dt.datetime(y, mo, 1 + 10 * min(2, (d - 1) // 10))
    elif keyword == "PENTADLY":
        start = dt.datetime(y, mo, 1 + 5 * min(5, (d - 1) // 5))
    elif keyword == "MONTHLY":
        start = dt.datetime(y, mo, 1)
    elif keyword == "QUARTERLY":
        start = dt.datetime(y, 3 * ((mo - 1) // 3) + 1, 1)
    elif keyword == "YEARLY":
        start = dt.datetime(y, 1, 1)
    elif keyword == "RECURRING_DAILY":
        return ("day", yday), None
    elif keyword == "RECURRING_WEEKLY":
        return ("week", week), None
    elif keyword == "RECURRING_MONTHLY":
        return ("month", mo), None
    elif keyword == "RECURRING_QUARTERLY":
        return ("quarter", (mo - 1) // 3 + 1), None
    else:
        raise ValueError(f"'{keyword}' is not one of {', '.join(_KEYWORDS)}.")
    start = np.datetime64(start, "s")
    return ("at", _mdim.days(start)), start


def _recurring_start(value, keyword):
    """A recurring interval's dimension value: its first occurrence's start."""
    import datetime as dt
    y, mo, d, _, _, _ = _mdim.parts(value)
    t = np.datetime64(value, "s").astype(dt.datetime)
    if keyword == "RECURRING_DAILY":
        return np.datetime64(dt.datetime(y, mo, d), "s")
    if keyword == "RECURRING_WEEKLY":
        return np.datetime64(dt.datetime(y, mo, d) - dt.timedelta(days=t.weekday()), "s")
    if keyword == "RECURRING_MONTHLY":
        return np.datetime64(dt.datetime(y, mo, 1), "s")
    return np.datetime64(dt.datetime(y, 3 * ((mo - 1) // 3) + 1, 1), "s")


def _bins(values, dim, definition, keyword, interval_value, interval_unit, interval_ranges, tool):
    """[(output dimension value, [indexes into values])] for an aggregation definition."""
    definition = _upper(definition, "ALL")
    if definition == "ALL":
        return [(values[0], list(range(len(values))))]
    if definition == "INTERVAL_KEYWORD":
        if dim != TIME:
            _refuse(tool, "aggregation_def", definition, "an interval keyword is for the time dimension")
        keyword = _upper(keyword, None)
        if keyword not in _KEYWORDS:
            raise ValueError(f"{tool}: interval_keyword must be one of {', '.join(_KEYWORDS)}.")
        groups = {}
        for i, v in enumerate(values):
            key, start = period(v, keyword)
            if key not in groups:
                groups[key] = (start if start is not None else _recurring_start(v, keyword), [])
            groups[key][1].append(i)
        out = list(groups.values())
        return sorted(out, key=lambda g: _mdim._key(g[0]))
    if definition == "INTERVAL_VALUE":
        if not _given(interval_value) or float(interval_value) <= 0:
            raise ValueError(f"{tool}: INTERVAL_VALUE needs a positive interval_value.")
        out = []
        if dim == TIME:
            if not _given(interval_unit):
                raise ValueError(f"{tool}: INTERVAL_VALUE on time needs an interval_unit.")
            start = values[0]
            i = 0
            while i < len(values):
                end = _mdim.step(start, interval_value, interval_unit)
                inside = [k for k in range(i, len(values)) if values[k] < end]
                if inside:
                    out.append((start, inside))
                    i = inside[-1] + 1
                start = end
            return out
        if _given(interval_unit):
            _refuse(tool, "interval_unit", interval_unit, "a unit is for the time dimension")
        width = float(interval_value)
        groups = {}
        for i, v in enumerate(values):
            k = math.floor((v - values[0]) / width + 1e-9)
            groups.setdefault(k, (values[0] + k * width, []))[1].append(i)
        return [groups[k] for k in sorted(groups)]
    if definition in ("INTERVAL_RANGES", "INTERVAL_RANGE"):
        rows = interval_ranges
        if isinstance(rows, str):
            rows = [r.split() for r in rows.split(";") if r.strip()]
        if not rows:
            raise ValueError(f"{tool}: INTERVAL_RANGES needs interval_ranges, pairs of minimum and maximum.")
        out = []
        for low, high in rows:
            low, high = _mdim.dim_value(dim, low), _mdim.dim_value(dim, high)
            inside = [i for i, v in enumerate(values) if _mdim._key(low) <= _mdim._key(v) <= _mdim._key(high)]
            if inside:
                out.append((low, inside))
        return out
    raise ValueError(f"{tool}: aggregation_def must be ALL, INTERVAL_KEYWORD, INTERVAL_VALUE or INTERVAL_RANGES.")


def _result(variables, grid):
    return _mdim.raster_of(Cube(grid, variables))


# ---- Aggregate Multidimensional Raster


def AggregateMultidimensionalRaster(in_multidimensional_raster, dimension, aggregation_method="MEAN", variables=None,
                                    aggregation_def="ALL", interval_keyword=None, interval_value=None,
                                    interval_unit=None, interval_ranges=None, aggregation_function=None,
                                    ignore_nodata="DATA", dimensionless="DIMENSIONS", percentile_value=90,
                                    percentile_interpolation_type="NEAREST"):
    """The slices of each variable combined along ``dimension`` by the aggregation method, over all slices or over
    each interval, as documented. Each aggregated slice's dimension value is the start of its interval (ADR-236 §5.4,
    INFERRED). ``NO_DIMENSIONS`` drops the dimension when one slice is left along it.

    Refused: ``CUSTOM``, whose aggregation function is a raster function template or a Python raster function run by
    Pro's raster function engine.

    INFERRED: an interval range holds the values from its minimum to its maximum, both included; PERCENTILE's
    interpolation, whose default the page does not state, is NEAREST."""
    tool = "AggregateMultidimensionalRaster"
    method = _upper(aggregation_method, "MEAN")
    if method == "CUSTOM":
        _refuse(tool, "aggregation_method", method, "its function is a raster function template or Python raster "
                "function run by Pro's raster function engine")
    if method not in ("MEAN", "MAXIMUM", "MAJORITY", "MINIMUM", "MINORITY", "MEDIAN", "PERCENTILE", "RANGE", "STD",
                      "SUM", "VARIETY"):
        raise ValueError(f"{tool}: '{aggregation_method}' is not an aggregation method.")
    if _given(aggregation_function):
        _refuse(tool, "aggregation_function", aggregation_function, "it is used by CUSTOM only")
    cube = _mdim.cube_of(in_multidimensional_raster, tool)
    dim = str(dimension)
    ignore = _ignore(ignore_nodata)
    interpolation = _upper(percentile_interpolation_type, "NEAREST")
    out = []
    for v in cube.chosen(variables, dim):
        coords, data, mask = [], [], []
        for other, indexes in v.along(dim):
            values = [v.coords[i][dim] for i in indexes]
            for start, members in _bins(values, dim, aggregation_def, interval_keyword, interval_value, interval_unit,
                                        interval_ranges, tool):
                chosen = [indexes[k] for k in members]
                value, empty = reduce(v.data[chosen], v.mask[chosen], method, ignore, percentile_value, interpolation)
                coords.append({**other, dim: start})
                data.append(value)
                mask.append(empty)
        dims = list(v.dims)
        if _upper(dimensionless, "DIMENSIONS") == "NO_DIMENSIONS" and len({_mdim._key(c[dim]) for c in coords}) == 1:
            dims = [d for d in dims if d != dim]
            coords = [{d: c[d] for d in dims} for c in coords]
        out.append(Variable(v.name, dims, coords, np.stack(data), np.stack(mask), v.attrs, v.band_names, v.dim_attrs))
    return _result(out, cube.grid)


# ---- anomalies


_ANOMALY = ("DIFFERENCE_FROM_MEAN", "PERCENT_DIFFERENCE_FROM_MEAN", "PERCENT_OF_MEAN", "Z_SCORE",
            "DIFFERENCE_FROM_MEDIAN", "PERCENT_DIFFERENCE_FROM_MEDIAN", "PERCENT_OF_MEDIAN")


def _anomaly_of(x, centre, spread, method):
    with np.errstate(all="ignore"):
        if method.startswith("DIFFERENCE_FROM"):
            return x - centre
        if method.startswith("PERCENT_DIFFERENCE"):
            return np.abs(x - centre) / ((x + centre) / 2)
        if method.startswith("PERCENT_OF"):
            return x / centre
        return (x - centre) / spread


def _anomaly(variable, dim, key, method, ignore, reference=None):
    """Each slice's anomaly against the mean (or median) of the slices that share its interval key."""
    data = np.zeros(variable.data.shape)
    mask = np.zeros(variable.data.shape, bool)
    median = "MEDIAN" in method
    for other, indexes in variable.along(dim):
        groups = {}
        for i in indexes:
            groups.setdefault(key(variable.coords[i][dim]), []).append(i)
        for members in groups.values():
            if reference is not None:
                for i in members:
                    centre, centre_mask = reference(variable, i)
                    value = _anomaly_of(variable.data[i], centre, None, method)
                    data[i] = value
                    mask[i] = variable.mask[i] | centre_mask | ~np.isfinite(value)
                continue
            stack, smask = variable.data[members], variable.mask[members]
            centre, cmask = reduce(stack, smask, "MEDIAN" if median else "MEAN", ignore)
            spread, smask2 = reduce(stack, smask, "STD", ignore)
            for i in members:
                value = _anomaly_of(variable.data[i], centre, spread, method)
                bad = variable.mask[i] | cmask | ~np.isfinite(value)
                if method == "Z_SCORE":
                    bad |= smask2
                data[i] = np.where(bad, 0, value)
                mask[i] = bad
    out = variable.copy()
    out.data, out.mask = np.where(mask, 0, data), mask
    return out


def Anomaly(in_raster, dimension_name="StdTime", anomaly_type="DIFFERENCE_FROM_MEAN", temporal_interval=None,
            ignore_nodata=True):
    """Each slice's anomaly from the mean of its pixel along the dimension, as documented: x − µ, |x − µ| / ((x + µ)/2)
    or x / µ, with µ over all slices or over each HOURLY, DAILY, WEEKLY, MONTHLY or YEARLY period. Every variable with
    the dimension is computed.

    INFERRED: a period is a calendar period of the series (each month of each year), not a recurring one; a week is an
    ISO week, from Monday."""
    tool = "Anomaly"
    kind = _upper(anomaly_type, "DIFFERENCE_FROM_MEAN")
    if kind not in _ANOMALY[:3]:
        raise ValueError(f"{tool}: anomaly_type must be DIFFERENCE_FROM_MEAN, PERCENT_DIFFERENCE_FROM_MEAN or "
                         "PERCENT_OF_MEAN.")
    cube = _mdim.cube_of(in_raster, tool)
    dim = str(dimension_name or TIME)
    interval = _upper(temporal_interval, None)
    if interval in (None, "NONE"):
        key = lambda _v: 0  # noqa: E731
    else:
        if interval not in ("HOURLY", "DAILY", "WEEKLY", "MONTHLY", "YEARLY"):
            raise ValueError(f"{tool}: temporal_interval must be HOURLY, DAILY, WEEKLY, MONTHLY or YEARLY.")
        if dim != TIME:
            raise ValueError(f"{tool}: a temporal interval needs the time dimension, StdTime.")
        key = lambda v: period(v, interval)[0]  # noqa: E731
    out = [_anomaly(v, dim, key, kind, _ignore(ignore_nodata)) for v in cube.chosen(None, dim)]
    return _result(out, cube.grid)


def GenerateMultidimensionalAnomaly(in_multidimensional_raster, variables=None, method="DIFFERENCE_FROM_MEAN",
                                    calculation_interval="ALL", ignore_nodata="DATA", reference_mean_raster=None):
    """Each slice's anomaly along time, by the page's formulas: x − µ, |x − µ| / ((x + µ)/2), x / µ, (x − µ)/S, and
    the same three about the median ß. The mean (median, standard deviation) is each pixel's over every slice (ALL),
    over each year (YEARLY), over each recurring month, ISO week or day of the year, or over each hour (HOURLY); or it
    is read from ``reference_mean_raster`` (EXTERNAL_RASTER): one raster for every slice, or a multidimensional one
    whose matching slices are used. A non-time dimension is computed at each of its steps, as documented.

    INFERRED: S divides by n; HOURLY is each hour of each day, as YEARLY is each year; the calculation interval's
    default, which the page does not state, is ALL."""
    tool = "GenerateMultidimensionalAnomaly"
    kind = _upper(method, "DIFFERENCE_FROM_MEAN")
    if kind not in _ANOMALY:
        raise ValueError(f"{tool}: method must be one of {', '.join(_ANOMALY)}.")
    interval = _upper(calculation_interval, "ALL")
    keys = {"ALL": lambda _v: 0, "YEARLY": lambda v: _mdim.parts(v)[0],
            "RECURRING_MONTHLY": lambda v: _mdim.parts(v)[1], "RECURRING_WEEKLY": lambda v: _mdim.parts(v)[5],
            "RECURRING_DAILY": lambda v: _mdim.parts(v)[4], "HOURLY": lambda v: period(v, "HOURLY")[0],
            "EXTERNAL_RASTER": lambda _v: 0}
    if interval not in keys:
        raise ValueError(f"{tool}: calculation_interval must be one of {', '.join(keys)}.")
    cube = _mdim.cube_of(in_multidimensional_raster, tool)
    reference = None
    if interval == "EXTERNAL_RASTER":
        if kind == "Z_SCORE":
            raise ValueError(f"{tool}: Z_SCORE is not supported with an external raster, as documented.")
        if not _given(reference_mean_raster):
            raise ValueError(f"{tool}: EXTERNAL_RASTER needs a reference_mean_raster.")
        reference = _reference(reference_mean_raster, cube, tool)
    elif _given(reference_mean_raster):
        raise ValueError(f"{tool}: reference_mean_raster is used with EXTERNAL_RASTER only.")
    chosen = cube.chosen(variables, TIME)
    out = [_anomaly(v, TIME, keys[interval], kind, _ignore(ignore_nodata), reference) for v in chosen]
    return _result(out, cube.grid)


def _reference(value, cube, tool):
    """The mean (or median) for each slice from an external raster: one raster for every slice, or the matching slice
    of a multidimensional one (the same variable and dimension values)."""
    from ._raster import Raster, as_raster
    if _mdim.is_multidimensional_path(value) or isinstance(value, _mdim.bind()):
        other = _mdim.cube_of(value, tool)
        if not other.grid.same(cube.grid):
            raise ValueError(f"{tool}: the reference raster must have the input's extent, cell size and reference.")

        def find(variable, i):
            ref = other.variable(variable.name)
            for k, c in enumerate(ref.coords):
                if all(d in c and _mdim._same(c[d], variable.coords[i][d]) for d in ref.dims):
                    return ref.data[k], ref.mask[k]
            raise ValueError(f"{tool}: the reference raster has no slice of '{variable.name}' at "
                             f"{variable.coords[i]}.")
        return find
    single = value if isinstance(value, Raster) else as_raster(value, tool)
    if not single._grid.same(cube.grid):
        raise ValueError(f"{tool}: the reference raster must have the input's extent, cell size and reference.")
    array, mask = single._array.astype(np.float64)[None], single._mask[None]
    return lambda _v, _i: (array, mask)


# ---- argument statistics


def _arg(values, mask, coords, stat, low, high, multiple, ignore, value=None, comparison="EQUAL_TO",
         occurrence="FIRST_OCCURRENCE"):
    """The coordinate (as a number) at which each cell reaches a statistic, along the first axis of (n, rows, cols)."""
    n = values.shape[0]
    valid = ~mask
    empty = ~valid.any(axis=0) if ignore else mask.any(axis=0)
    filled = np.where(valid, values, np.nan)
    coords = np.asarray(coords, float)
    out = np.zeros(values.shape[1:])
    if stat in ("MAX", "MIN", "MEDIAN"):
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", RuntimeWarning)
            if stat == "MAX":
                target = np.nanmax(filled, axis=0)
            elif stat == "MIN":
                target = np.nanmin(filled, axis=0)
            else:
                target = np.nanpercentile(filled, 50, axis=0, method="inverted_cdf")
        hits = valid & (filled == target)
        count = hits.sum(axis=0)
        first = np.argmax(hits, axis=0)
        last = n - 1 - np.argmax(hits[::-1], axis=0)
        index = last if occurrence == "LAST_OCCURRENCE" else first
        out = coords[index]
        if multiple is not None:
            out = np.where(count > 1, float(multiple), out)
        empty = empty | (count == 0)
    elif stat == "DURATION":
        inside = valid & (filled >= low) & (filled <= high)
        run = np.zeros(values.shape[1:])
        best = np.zeros(values.shape[1:])
        for i in range(n):
            if ignore:
                run = np.where(inside[i], run + 1, np.where(valid[i], 0, run))
            else:
                run = np.where(inside[i], run + 1, 0)
            best = np.maximum(best, run)
        out = best
    elif stat == "VALUE":
        compare = {"EQUAL_TO": np.equal, "GREATER_THAN": np.greater, "SMALLER_THAN": np.less}[comparison]
        hits = valid & compare(filled, float(value))
        count = hits.sum(axis=0)
        first = np.argmax(hits, axis=0)
        last = n - 1 - np.argmax(hits[::-1], axis=0)
        out = coords[last if occurrence == "LAST_OCCURRENCE" else first]
        if multiple is not None:
            out = np.where(count > 1, float(multiple), out)
        empty = empty | (count == 0)
    return np.where(empty, 0, out), empty


def _numbers(dim, values):
    return [_mdim.decimal_year(v) if dim == TIME else float(v) for v in values]


def _arg_over(source, dim, stat, low, high, multiple, ignore, value=None, comparison="EQUAL_TO",
              occurrence="FIRST_OCCURRENCE", variables=None, tool="ArgStatistics"):
    """Argument statistics on a multidimensional raster (dimension values) or a multiband one (band indexes)."""
    from ._raster import Raster, _open_bands
    if isinstance(source, (list, tuple)):
        rasters = [_open_bands(r, tool) for r in source]
        if any(r.isMultidimensional for r in rasters):
            source = rasters[0] if len(rasters) == 1 else None
            if source is None:
                _refuse(tool, "rasters", "several multidimensional rasters", "the page does not say how they combine")
        else:
            arrays = [a for r in rasters for a in r._arrays]
            masks = [m for r in rasters for m in r._masks]
            return _arg_bands(arrays, masks, rasters[0]._grid, stat, low, high, multiple, ignore, value, comparison,
                              occurrence, rasters, tool)
    if isinstance(source, Raster) and not source.isMultidimensional:
        return _arg_bands(source._arrays, source._masks, source._grid, stat, low, high, multiple, ignore, value,
                          comparison, occurrence, [source], tool)
    if isinstance(source, (str, os.PathLike)) and not _mdim.is_multidimensional_path(source):
        r = _open_bands(source, tool)
        return _arg_bands(r._arrays, r._masks, r._grid, stat, low, high, multiple, ignore, value, comparison,
                          occurrence, [r], tool)
    cube = _mdim.cube_of(source, tool)
    out = []
    for v in cube.chosen(variables, dim if _given(dim) else None):
        d = dim if _given(dim) else v.dims[0]
        if v.bands != 1:
            _refuse(tool, "in_raster", v.name, "its slices have several bands, and the page does not say which is read")
        coords, data, mask = [], [], []
        for other, indexes in v.along(d):
            numbers = _numbers(d, [v.coords[i][d] for i in indexes])
            value_, empty = _arg(v.data[indexes, 0], v.mask[indexes, 0], numbers, stat, low, high, multiple, ignore,
                                 value, comparison, occurrence)
            coords.append(other)
            data.append(value_)
            mask.append(empty)
        dims = [x for x in v.dims if x != d]
        out.append(Variable(v.name, dims, coords, np.stack(data), np.stack(mask), {}, None,
                            {k: a for k, a in v.dim_attrs.items() if k != d}))
    if len(out) == 1 and not out[0].dims:
        return Raster._of(out[0].data[0, 0], out[0].mask[0, 0], cube.grid)
    return _result(out, cube.grid)


def _arg_bands(arrays, masks, grid, stat, low, high, multiple, ignore, value, comparison, occurrence, rasters, tool):
    from ._raster import Raster
    if len(arrays) < 2:
        raise ValueError(f"{tool} needs a multidimensional raster, a multiband raster or several rasters.")
    for r in rasters[1:]:
        if not r._grid.same(grid):
            _refuse(tool, "rasters", r, "rasters on different grids need resampling")
    value_, empty = _arg(np.stack([a.astype(np.float64) for a in arrays]), np.stack(masks),
                         list(range(1, len(arrays) + 1)), stat, low, high, multiple, ignore, value, comparison,
                         occurrence)
    return Raster._of(value_, empty, grid)


def ArgStatistics(rasters, stat_type=None, min_value=None, max_value=None, multiple_occurrence_value=100,
                  ignore_nodata=True):
    """Where each pixel reaches its maximum, minimum or median: the one-based band index of a multiband raster or a
    list of rasters, or the dimension value of a multidimensional raster (along each variable's first dimension); or
    DURATION, the longest run of consecutive slices whose values lie between ``min_value`` and ``max_value``. Where the
    maximum or minimum is reached more than once, the cell is ``multiple_occurrence_value``, as documented.

    INFERRED (ADR-236 §5.3): band indexes count from one, as HighestPosition's do; a time is a decimal year; DURATION
    counts slices, and with ``ignore_nodata`` a NoData slice neither extends nor ends a run; MEDIAN of an even count is
    the lower of the two middle values, reached at its first slice; one variable of one dimension answers a plain
    raster, and anything more a multidimensional one."""
    stat = _upper(stat_type, None)
    if stat not in ("MAX", "MIN", "MEDIAN", "DURATION"):
        raise ValueError("ArgStatistics: stat_type must be MAX, MIN, MEDIAN or DURATION.")
    low = high = None
    if stat == "DURATION":
        if not _given(min_value) or not _given(max_value):
            raise ValueError("ArgStatistics: DURATION needs min_value and max_value.")
        low, high = float(min_value), float(max_value)
    multiple = None if stat == "MEDIAN" or not _given(multiple_occurrence_value) else float(multiple_occurrence_value)
    return _arg_over(rasters, None, stat, low, high, multiple, _ignore(ignore_nodata), tool="ArgStatistics")


def FindArgumentStatistics(in_raster, dimension=None, dimension_def="ALL", interval_keyword=None, variables=None,
                           statistics_type="ARGUMENT_MIN", min=None, max=None, multiple_occurrence=None,  # noqa: A002
                           ignore_nodata="DATA", value=None, comparison="EQUAL_TO", occurrence="FIRST_OCCURRENCE"):
    """The dimension value (or band index) where each pixel reaches its minimum, maximum or median, where a value is
    reached (equal to, greater than or smaller than ``value``), or the longest run between ``min`` and ``max``, along
    the dimension over every slice (``ALL``). The dimensions not analysed are kept, as documented. Where the statistic
    is reached more than once, ``multiple_occurrence`` is written when given, and otherwise the first or last
    occurrence's value (``occurrence``).

    Refused: ``INTERVAL_KEYWORD``, whose page says both that the statistic is extracted across each interval and, in its
    example, that the cell holds the month in which it was reached; the two are different answers.

    INFERRED: as ArgStatistics (ADR-236 §5.3)."""
    tool = "FindArgumentStatistics"
    if _upper(dimension_def, "ALL") != "ALL":
        _refuse(tool, "dimension_def", dimension_def, "the page describes an interval keyword both as grouping the "
                "slices and, in its example, as reporting the calendar part of the argument")
    stat = {"ARGUMENT_MIN": "MIN", "ARGUMENT_MAX": "MAX", "ARGUMENT_MEDIAN": "MEDIAN", "DURATION": "DURATION",
            "ARGUMENT_VALUE": "VALUE"}.get(_upper(statistics_type, "ARGUMENT_MIN"))
    if stat is None:
        raise ValueError(f"{tool}: statistics_type must be ARGUMENT_MIN, ARGUMENT_MAX, ARGUMENT_MEDIAN, DURATION or "
                         "ARGUMENT_VALUE.")
    low = high = None
    if stat == "DURATION":
        if not _given(min) or not _given(max):
            raise ValueError(f"{tool}: DURATION needs min and max.")
        low, high = float(min), float(max)
    if stat == "VALUE" and not _given(value):
        raise ValueError(f"{tool}: ARGUMENT_VALUE needs a value.")
    compare = _upper(comparison, "EQUAL_TO")
    if compare not in ("EQUAL_TO", "GREATER_THAN", "SMALLER_THAN"):
        raise ValueError(f"{tool}: comparison must be EQUAL_TO, GREATER_THAN or SMALLER_THAN.")
    which = _upper(occurrence, "FIRST_OCCURRENCE")
    if which not in ("FIRST_OCCURRENCE", "LAST_OCCURRENCE"):
        raise ValueError(f"{tool}: occurrence must be FIRST_OCCURRENCE or LAST_OCCURRENCE.")
    multiple = float(multiple_occurrence) if _given(multiple_occurrence) else None
    return _arg_over(in_raster, dimension, stat, low, high, multiple, _ignore(ignore_nodata), value, compare, which,
                     variables, tool)


# ---- Dimensional Moving Statistics


def DimensionalMovingStatistics(in_raster, dimension=None, backward_window=1, forward_window=1,
                                nodata_handling="DATA", statistics_type="MEAN", percentile_value=90,
                                percentile_interpolation_type="AUTO_DETECT", circular_wrap_value=360):
    """A moving-window statistic along one dimension: each slice's value is the statistic of the slices from
    ``backward_window`` before it to ``forward_window`` after it. ``DATA`` ignores NoData in the window, ``NODATA``
    makes the cell NoData when the window holds any, and ``FILL_NODATA`` keeps valid cells and fills NoData ones with
    the statistic, as documented. Majority's ties take the lowest value, as documented; the circular mean wraps at
    ``circular_wrap_value`` (the mean direction of Fisher 1993, Statistical Analysis of Circular Data, §2.2).
    Majority on floating-point input is refused, as documented.

    INFERRED (ADR-236 §5.4): at either end the window takes the slices there are; AUTO_DETECT is NEAREST for integer
    input and LINEAR for floating point, as Percentile's."""
    tool = "DimensionalMovingStatistics"
    back, forward = int(backward_window), int(forward_window)
    if not (1 <= back <= 100 and 1 <= forward <= 100):
        raise ValueError(f"{tool}: the windows range from 1 to 100 slices.")
    handling = _upper(nodata_handling, "DATA")
    if handling not in ("DATA", "NODATA", "FILL_NODATA"):
        raise ValueError(f"{tool}: nodata_handling must be DATA, NODATA or FILL_NODATA.")
    stat = _upper(statistics_type, "MEAN")
    if stat not in ("MEAN", "CIRCULAR_MEAN", "MAJORITY", "MAXIMUM", "MEDIAN", "MINIMUM", "PERCENTILE"):
        raise ValueError(f"{tool}: '{statistics_type}' is not one of its statistics types.")
    if float(circular_wrap_value) <= 0:
        raise ValueError(f"{tool}: circular_wrap_value must be positive.")
    cube = _mdim.cube_of(in_raster, tool)
    dim = _dimension(cube, dimension)
    out = []
    for v in cube.chosen(None, dim):
        integer = _integer(v)
        if stat == "MAJORITY" and not integer:
            raise ValueError(f"{tool}: Majority is not available for floating-point input, as documented.")
        interpolation = _interp(percentile_interpolation_type, integer)
        data, mask = np.zeros(v.data.shape), np.zeros(v.data.shape, bool)
        for _other, indexes in v.along(dim):
            for k, i in enumerate(indexes):
                window = indexes[max(0, k - back):k + forward + 1]
                value, empty = reduce(v.data[window], v.mask[window], stat, handling != "NODATA", percentile_value,
                                      interpolation, circular_wrap_value)
                if handling == "FILL_NODATA":
                    value = np.where(v.mask[i], value, v.data[i])
                    empty = np.where(v.mask[i], empty, False)
                data[i], mask[i] = value, empty
        if integer and stat in ("MAJORITY", "MAXIMUM", "MINIMUM"):
            data = np.round(data)
        new = v.copy()
        new.data, new.mask = np.where(mask, 0, data), mask
        out.append(new)
    return _result(out, cube.grid)


# ---- trends (equivalents: the page draws the models as images)


_TREND_SOURCE = ("ordinary least squares (Draper and Smith 1998, Applied Regression Analysis, ch. 5); harmonic "
                 "regression after Zhu and Woodcock 2014, Remote Sensing of Environment 144; Mann 1945, Econometrica "
                 "13(3); Kendall 1975, Rank Correlation Methods; Sen 1968, JASA 63(324); Hirsch, Slack and Smith 1982, "
                 "Water Resources Research 18(1)")
_TREND_METHOD = "least-squares linear, polynomial and harmonic trends, and the Mann-Kendall and Seasonal-Kendall tests"


def _x_of(dim, values):
    """The regression's x: days since the first slice for time, the value less the first otherwise (ADR-236 §5.5)."""
    if dim == TIME:
        first = _mdim.days(values[0])
        return np.array([_mdim.days(v) - first for v in values])
    return np.array([float(v) - float(values[0]) for v in values])


def _design(kind, x, order, frequency, period):
    if kind == "LINEAR":
        return np.column_stack([x, np.ones_like(x)])
    if kind == "POLYNOMIAL":
        return np.column_stack([x ** p for p in range(order, -1, -1)])
    columns = [x, np.ones_like(x)]
    for i in range(1, frequency + 1):
        w = 2 * np.pi * i / period
        columns += [np.sin(w * x), np.cos(w * x)]
    return np.column_stack(columns)


def _band_names(kind, order, frequency, rmse, r2, pvalue):
    if kind == "LINEAR":
        names = ["Slope", "Intercept"]
    elif kind == "POLYNOMIAL":
        names = [f"Polynomial_{p}" for p in range(order, -1, -1)]
    elif kind == "HARMONIC":
        names = ["Slope", "Intercept"]
        for i in range(1, frequency + 1):
            names += [f"Harmonic_sin{i}", f"Harmonic_cos{i}"]
    else:
        return ["Sens_Slope", "P_Value", "MK_Score", "S_Variance", "Z_Score"]
    return names + ["RMSE"] * rmse + ["R2"] * r2 + ["Slope_P_Value"] * pvalue


def _fit(y, valid, x, kind, order, frequency, period, rmse, r2, pvalue):
    """Least-squares coefficients and statistics for each pixel, grouping pixels by which slices they have."""
    from scipy import stats
    n, rows, cols = y.shape
    names = _band_names(kind, order, frequency, rmse, r2, pvalue)
    out = np.zeros((len(names), rows, cols))
    empty = np.ones((rows, cols), bool)
    flat_valid = valid.reshape(n, -1)
    flat_y = y.reshape(n, -1)
    patterns, inverse = np.unique(flat_valid.T, axis=0, return_inverse=True)
    full = _design(kind, x, order, frequency, period)
    p = full.shape[1]
    slope = 0 if kind in ("LINEAR", "HARMONIC") else order - 1
    for k, pattern in enumerate(patterns):
        pixels = np.flatnonzero(inverse.ravel() == k)
        m = int(pattern.sum())
        if m < p:
            continue
        a = full[pattern]
        b = flat_y[pattern][:, pixels]
        coef, *_ = np.linalg.lstsq(a, b, rcond=None)
        fitted = a @ coef
        sse = ((b - fitted) ** 2).sum(axis=0)
        columns = [coef[j] for j in range(p)]
        if rmse:
            columns.append(np.sqrt(sse / m))
        if r2:
            sst = ((b - b.mean(axis=0)) ** 2).sum(axis=0)
            with np.errstate(all="ignore"):
                columns.append(np.where(sst > 0, 1 - sse / np.where(sst > 0, sst, 1), np.nan))
        if pvalue:
            if m > p:
                inverse_xtx = np.linalg.pinv(a.T @ a)
                se = np.sqrt(sse / (m - p) * inverse_xtx[slope, slope])
                with np.errstate(all="ignore"):
                    t = coef[slope] / se
                columns.append(np.where(se > 0, 2 * stats.t.sf(np.abs(t), m - p), np.where(coef[slope] != 0, 0.0, 1.0)))
            else:
                columns.append(np.full(pixels.size, np.nan))
        values = np.stack(columns)
        r, c = np.unravel_index(pixels, (rows, cols))
        out[:, r, c] = values
        empty[r, c] = ~np.isfinite(values).all(axis=0)
    return names, np.where(empty, 0, out), empty


def _kendall(y, valid, x, seasons=None):
    """Mann-Kendall's S, its variance with the tie correction, Z with the continuity correction, the two-sided p-value
    and Sen's slope (the median of the pairwise slopes); with seasons, Hirsch, Slack and Smith's sums over seasons and
    the median of the within-season slopes."""
    from scipy import stats
    n, rows, cols = y.shape
    groups = [np.arange(n)] if seasons is None else [np.flatnonzero(seasons == s) for s in np.unique(seasons)]
    S = np.zeros((rows, cols))
    V = np.zeros((rows, cols))
    slopes_all = []
    counts = np.zeros((rows, cols))
    for g in groups:
        yg, vg, xg = y[g], valid[g], x[g]
        m = len(g)
        for i in range(m):
            for j in range(i + 1, m):
                both = vg[i] & vg[j]
                S += np.where(both, np.sign(yg[j] - yg[i]), 0)
                if xg[j] != xg[i]:
                    slopes_all.append(np.where(both, (yg[j] - yg[i]) / (xg[j] - xg[i]), np.nan))
        nv = vg.sum(axis=0)
        counts += nv
        # ties: Σ t(t − 1)(2t + 5) over groups of equal valid values
        ordered = np.sort(np.where(vg, yg, np.inf), axis=0)
        ties = np.zeros((rows, cols))
        run = np.ones((rows, cols))
        for i in range(1, m):
            same = (ordered[i] == ordered[i - 1]) & np.isfinite(ordered[i])
            ending = ~same
            ties += np.where(ending & (run > 1), run * (run - 1) * (2 * run + 5), 0)
            run = np.where(same, run + 1, 1)
        ties += np.where(run > 1, run * (run - 1) * (2 * run + 5), 0)
        V += (nv * (nv - 1) * (2 * nv + 5) - ties) / 18.0
    with np.errstate(all="ignore"):
        Z = np.where(S > 0, (S - 1) / np.sqrt(V), np.where(S < 0, (S + 1) / np.sqrt(V), 0.0))
    Z = np.where(V > 0, Z, 0.0)
    P = 2 * stats.norm.sf(np.abs(Z))
    import warnings
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", RuntimeWarning)
        sen = np.nanmedian(np.stack(slopes_all), axis=0) if slopes_all else np.full((rows, cols), np.nan)
    empty = (counts < 3) | ~np.isfinite(sen)
    out = np.stack([sen, P, S, V, Z])
    return _band_names("MANN-KENDALL", 0, 0, 0, 0, 0), np.where(empty, 0, out), empty


def _trend(cube, dim, variables, kind, order, frequency, cycle_length, cycle_unit, ignore, rmse, r2, pvalue,
           seasonal_period, tool):
    if kind not in ("LINEAR", "POLYNOMIAL", "HARMONIC", "MANN-KENDALL", "SEASONAL-KENDALL"):
        raise ValueError(f"{tool}: the trend type must be LINEAR, POLYNOMIAL, HARMONIC, MANN-KENDALL or "
                         "SEASONAL-KENDALL.")
    if kind in ("HARMONIC", "SEASONAL-KENDALL") and dim != TIME:
        raise ValueError(f"{tool}: {kind} needs the time dimension, StdTime.")
    if kind == "POLYNOMIAL" and order < 1:
        raise ValueError(f"{tool}: the polynomial order must be at least 1.")
    if kind == "HARMONIC" and frequency < 1:
        raise ValueError(f"{tool}: the harmonic frequency must be at least 1.")
    unit = _upper(cycle_unit, "YEARS")
    if unit not in ("DAYS", "YEARS"):
        raise ValueError(f"{tool}: cycle_unit must be DAYS or YEARS.")
    period_days = float(cycle_length) * (365.25 if unit == "YEARS" else 1.0)
    out = []
    for v in cube.chosen(variables, dim):
        if v.bands != 1:
            _refuse(tool, "variables", v.name, "its slices have several bands")
        coords, data, mask = [], [], []
        for other, indexes in v.along(dim):
            values = [v.coords[i][dim] for i in indexes]
            x = _x_of(dim, values)
            y = v.data[indexes, 0].astype(np.float64)
            valid = ~v.mask[indexes, 0]
            if kind in ("MANN-KENDALL", "SEASONAL-KENDALL"):
                seasons = None
                if kind == "SEASONAL-KENDALL":
                    season = _upper(seasonal_period, "DAYS")
                    if season not in ("DAYS", "MONTHS"):
                        raise ValueError(f"{tool}: seasonal_period must be DAYS or MONTHS.")
                    seasons = np.array([_mdim.parts(t)[4 if season == "DAYS" else 1] for t in values])
                names, value, empty = _kendall(y, valid, x, seasons)
                if not ignore:
                    empty = empty | (~valid).any(axis=0)
            else:
                names, value, empty = _fit(y, valid, x, kind, order, frequency, period_days, rmse, r2, pvalue)
                if not ignore:
                    empty = empty | (~valid).any(axis=0)
            coords.append({**other, dim: values[0]})
            data.append(np.where(empty, 0, value))
            mask.append(np.broadcast_to(empty, value.shape).copy())
        attrs = {"grapy_trend": kind, "grapy_trend_dimension": dim, "grapy_trend_x": "days" if dim == TIME else "value",
                 "grapy_trend_order": order, "grapy_trend_frequency": frequency, "grapy_trend_period_days": period_days}
        out.append(Variable(v.name, v.dims, coords, np.stack(data), np.stack(mask), attrs, names, v.dim_attrs))
    return _result(out, cube.grid)


@equivalent(_TREND_METHOD, _TREND_SOURCE)
def GenerateTrend(raster, dimension_name, regression_type="LINEAR", cycle_length=1, cycle_unit="YEARS",
                  harmonic_frequency=1, polynomial_order=2, ignore_nodata=True, rmse=True, r2=False,
                  slope_p_value=False, seasonal_period="DAYS"):
    """EQUIVALENT: each pixel's trend along a dimension, for every variable that has it. The page draws the models as
    images; grapy fits them by ordinary least squares (Draper and Smith 1998):

    - LINEAR ``y = β0 + β1·x``, bands Slope, Intercept;
    - POLYNOMIAL ``y = Σ βk·xᵏ``, bands Polynomial_n … Polynomial_0;
    - HARMONIC ``y = β0 + β1·x + Σᵢ (αᵢ·sin(2πi·x/T) + γᵢ·cos(2πi·x/T))``, the form of Zhu and Woodcock 2014, bands
      Slope, Intercept, Harmonic_sin1, Harmonic_cos1 …, T the cycle length in days (a year is 365.25 days);

    then RMSE, R2 and Slope_P_Value when asked, in the band order the page gives for the first. MANN-KENDALL and
    SEASONAL-KENDALL give Sen's slope, the p-value, S, S's variance and Z (Mann 1945, Kendall 1975, Sen 1968; the
    seasonal test of Hirsch, Slack and Smith 1982 with seasons by day of the year or month).

    The output is one slice per combination of the other dimensions, at the analysed dimension's first value.

    INFERRED (ADR-236 §5.5): x is days since the first slice for time and the value less the first otherwise, so the
    intercept is the fitted value at the first slice (as Trend to RGB's page describes it); RMSE divides by n; R2 and
    Slope_P_Value follow RMSE in that order; the slope p-value is the two-sided t-test of the linear term (Polynomial_1
    for a polynomial); RMSE, R2 and the p-value are not added to the five Kendall bands."""
    cube = _mdim.cube_of(raster, "GenerateTrend")
    return _trend(cube, str(dimension_name), None, _upper(regression_type, "LINEAR"), int(polynomial_order),
                  int(harmonic_frequency), cycle_length if _given(cycle_length) else 1, cycle_unit,
                  _ignore(ignore_nodata), _flag(rmse, "RMSE", True), _flag(r2, "R2"), _flag(slope_p_value, "TRUE"),
                  seasonal_period, "GenerateTrend")


@equivalent(_TREND_METHOD, _TREND_SOURCE)
def GenerateTrendRaster(in_multidimensional_raster, dimension, variables=None, line_type="LINEAR", frequency=None,
                        ignore_nodata="DATA", cycle_length=None, cycle_unit="YEARS", rmse="RMSE", r2="NO_R2",
                        slope_p_value="NO_SLOPEPVALUE", seasonal_period="DAYS"):
    """EQUIVALENT: GenerateTrend as a tool: the variables named (the first when none is), ``frequency`` the polynomial
    order (2 by default) or the number of harmonics (1), ``cycle_length`` a year by default. See GenerateTrend."""
    tool = "GenerateTrendRaster"
    cube = _mdim.cube_of(in_multidimensional_raster, tool)
    kind = _upper(line_type, "LINEAR")
    order = int(frequency) if _given(frequency) and kind == "POLYNOMIAL" else 2
    harmonics = int(frequency) if _given(frequency) and kind == "HARMONIC" else 1
    chosen = variables if _given(variables) else [cube.variable().name]
    length = float(cycle_length) if _given(cycle_length) else (1.0 if _upper(cycle_unit, "YEARS") == "YEARS" else 365.25)
    return _trend(cube, str(dimension), chosen, kind, order, harmonics, length, cycle_unit, _ignore(ignore_nodata),
                  _flag(rmse, "RMSE", True), _flag(r2, "R2"), _flag(slope_p_value, "SLOPEPVALUE"), seasonal_period, tool)


def _predict(source, variables, definition, values, start, end, interval_value, interval_unit, tool):
    cube = _mdim.cube_of(source, tool)
    out = []
    for v in cube.chosen(variables):
        kind = v.attrs.get("grapy_trend")
        if not kind:
            raise NotBuiltError(f"{tool}: '{v.name}' is not a trend raster grapy made; a trend raster from elsewhere "
                                "carries no description of its model that grapy can read.")
        if kind in ("MANN-KENDALL", "SEASONAL-KENDALL"):
            raise ValueError(f"{tool}: '{v.name}' holds a {kind} test, which is not a model to predict from.")
        dim = v.attrs["grapy_trend_dimension"]
        order, frequency = int(v.attrs["grapy_trend_order"]), int(v.attrs["grapy_trend_frequency"])
        period_days = float(v.attrs["grapy_trend_period_days"])
        targets = _targets(dim, definition, values, start, end, interval_value, interval_unit, tool)
        names = v.names_of_bands()
        p = len(_band_names(kind, order, frequency, 0, 0, 0))
        coords, data, mask = [], [], []
        for i, c in enumerate(v.coords):
            origin = c[dim]
            x = _x_of(dim, [origin] + targets)[1:]
            design = _design(kind, x, order, frequency, period_days)
            coef = v.data[i, :p]
            empty = v.mask[i, :p].any(axis=0)
            for k, t in enumerate(targets):
                value = np.tensordot(design[k], coef, axes=1)
                coords.append({**{d: c[d] for d in v.dims if d != dim}, dim: t})
                data.append(np.where(empty, 0, value)[None])
                mask.append(empty[None])
        _ = names
        out.append(Variable(v.name, v.dims, coords, np.stack(data), np.stack(mask), {}, None, v.dim_attrs))
    return _result(out, cube.grid)


def _targets(dim, definition, values, start, end, interval_value, interval_unit, tool):
    definition = _upper(definition, "BY_VALUE")
    if definition == "BY_VALUE":
        listed = values if isinstance(values, (list, tuple)) else [p for p in str(values or "").split(";") if p.strip()]
        if not listed:
            raise ValueError(f"{tool}: BY_VALUE needs dimension_values.")
        return [_mdim.dim_value(dim, x) for x in listed]
    if definition != "BY_INTERVAL":
        raise ValueError(f"{tool}: the dimension definition must be BY_VALUE or BY_INTERVAL.")
    if not _given(start) or not _given(end):
        raise ValueError(f"{tool}: BY_INTERVAL needs start and end.")
    step = float(interval_value) if _given(interval_value) else 1.0
    if step <= 0:
        raise ValueError(f"{tool}: interval_value must be positive.")
    a, b = _mdim.dim_value(dim, start), _mdim.dim_value(dim, end)
    out = []
    if dim == TIME:
        unit = _upper(interval_unit, "HOURS")
        t, k = a, 0
        while t <= b:
            out.append(t)
            k += 1
            t = _mdim.step(a, step * k, unit)
        return out
    if _given(interval_unit):
        _refuse(tool, "interval_unit", interval_unit, "a unit is for the time dimension")
    k = 0
    while a + k * step <= b + 1e-9:
        out.append(a + k * step)
        k += 1
    return out


@equivalent("the fitted least-squares trend evaluated at the new dimension values", _TREND_SOURCE)
def PredictUsingTrend(raster, dimension_definition_type="BY_VALUE", dimension_values=None, start=None, end=None,
                      interval_value=1, interval_unit="HOURS"):
    """EQUIVALENT: GenerateTrend's model evaluated at each dimension value asked: the values listed (BY_VALUE), or
    from ``start`` to ``end`` in steps of ``interval_value`` ``interval_unit`` (BY_INTERVAL), for every variable.
    A Mann-Kendall raster is a test, not a model, and is refused; so is a trend raster grapy did not make, whose model
    grapy cannot read."""
    return _predict(raster, None, dimension_definition_type, dimension_values, start, end, interval_value,
                    interval_unit, "PredictUsingTrend")


@equivalent("the fitted least-squares trend evaluated at the new dimension values", _TREND_SOURCE)
def PredictUsingTrendRaster(in_multidimensional_raster, variables=None, dimension_def="BY_VALUE",
                            dimension_values=None, start=None, end=None, interval_value=1, interval_unit=None):
    """EQUIVALENT: PredictUsingTrend as a tool, for the variables named (every one when none is). See
    PredictUsingTrend. INFERRED: the interval unit's default on time is HOURS, as the function's."""
    return _predict(in_multidimensional_raster, variables, dimension_def, dimension_values, start, end,
                    interval_value, interval_unit, "PredictUsingTrendRaster")


@equivalent("slope and intercept, or harmonic amplitude and phase through HSV, stretched linearly to 0–255",
            "Smith 1978, Color gamut transform pairs, SIGGRAPH '78; the Trend to RGB function's description")
def TrendToRGB(raster, model_type="LINEAR"):
    """EQUIVALENT: a three-band 8-bit raster of a trend raster, as the Trend to RGB function page describes it and
    with the scaling it leaves out filled in: LINEAR puts positive slopes in red and negative slopes in blue, each
    stretched linearly from 0 to the largest magnitude onto 0–255, and the intercept in green, stretched linearly
    from its minimum to its maximum. HARMONIC takes the first harmonic's amplitude √(sin1² + cos1²) as saturation (over
    the largest amplitude) and its phase atan2(sin1, cos1) as hue, at a constant value of 1, converted to RGB by Smith's
    (1978) hexcone transform.

    Refused: a change analysis raster (Analyze Changes Using CCDC), whose harmonic coefficients grapy's change raster
    holds per band of the input and not as one model.

    INFERRED: the stretches and the constant value; a cell NoData in any coefficient is NoData."""
    tool = "TrendToRGB"
    kind = _upper(model_type, "LINEAR")
    if kind not in ("LINEAR", "HARMONIC"):
        raise ValueError(f"{tool}: model_type must be LINEAR or HARMONIC.")
    cube = _mdim.cube_of(raster, tool)
    v = cube.variable()
    if v.attrs.get("grapy_change"):
        _refuse(tool, "raster", raster, "grapy's change analysis raster holds a model per input band, not one trend")
    names = [n.lower() for n in v.names_of_bands()]
    from ._raster import Raster

    def band(name):
        if name.lower() not in names:
            raise ValueError(f"{tool}: the raster has no {name} band; it needs a {kind.lower()} trend raster.")
        return v.data[0, names.index(name.lower())], v.mask[0, names.index(name.lower())]

    if kind == "LINEAR":
        (slope, m1), (intercept, m2) = band("Slope"), band("Intercept")
        empty = m1 | m2
        pos = np.where(slope > 0, slope, 0)
        neg = np.where(slope < 0, -slope, 0)
        stretch = lambda a, top: np.where(top > 0, a / (top if top > 0 else 1) * 255, 0)  # noqa: E731
        red = stretch(pos, pos[~empty].max() if (~empty).any() else 0)
        blue = stretch(neg, neg[~empty].max() if (~empty).any() else 0)
        lo = intercept[~empty].min() if (~empty).any() else 0
        hi = intercept[~empty].max() if (~empty).any() else 0
        green = (intercept - lo) / (hi - lo) * 255 if hi > lo else np.zeros_like(intercept)
    else:
        (s, m1), (c, m2) = band("Harmonic_sin1"), band("Harmonic_cos1")
        empty = m1 | m2
        amplitude = np.hypot(s, c)
        top = amplitude[~empty].max() if (~empty).any() else 0
        sat = amplitude / top if top > 0 else np.zeros_like(amplitude)
        hue = np.mod(np.arctan2(s, c), 2 * np.pi) / (2 * np.pi) * 6
        i = np.floor(hue).astype(int) % 6
        f = hue - np.floor(hue)
        p, q, t = 1 - sat, 1 - sat * f, 1 - sat * (1 - f)
        one = np.ones_like(sat)
        red = np.choose(i, [one, q, p, p, t, one]) * 255
        green = np.choose(i, [t, one, one, q, p, p]) * 255
        blue = np.choose(i, [p, p, t, one, one, q]) * 255
    bands = [np.where(empty, 0, np.clip(np.round(b), 0, 255)).astype(np.uint8) for b in (red, green, blue)]
    return Raster._of_bands(bands, [empty.copy() for _ in bands], cube.grid)


# ---- principal components and correlation


def _write_table(path, columns, rows, tool):
    """A table as CSV (a .csv path) or in a GeoPackage, as grapy's other tables are."""
    path = os.fspath(path)
    if not os.path.isabs(path):
        from ._runtime import env
        path = os.path.join(env.workspace, path)
    if path.lower().endswith((".csv", ".txt")):
        import csv
        with open(path, "w", newline="", encoding="utf-8") as f:
            w = csv.writer(f)
            w.writerow([c for c, _ in columns])
            w.writerows(rows)
        return path
    from . import _gpkg, da
    from .analysis import _out
    out = _out(path)
    _gpkg.create(out, "Geometry", 0, [_gpkg.Column(name, kind) for name, kind in columns])
    with da.InsertCursor(out, [c for c, _ in columns] + ["SHAPE@"]) as cursor:
        for row in rows:
            cursor.insertRow(tuple(row) + (None,))
    return out


@equivalent("principal components of the slices' covariance matrix", "Jolliffe 2002, Principal Component Analysis, ch. 1")
def MultidimensionalPrincipalComponents(in_multidimensional_raster, mode, dimension, out_pc, out_loadings,
                                        out_eigenvalues=None, variable=None, number_of_pc=None):
    """EQUIVALENT: principal components of a variable's slices along a dimension (DIMENSION_REDUCTION), each slice an
    image and each pixel an observation: the components are the eigenvectors of the slices' covariance matrix in order
    of decreasing eigenvalue (Jolliffe 2002), as sa.PrincipalComponents computes them. ``out_pc`` is a raster of a band
    per component, ``out_loadings`` a table of each slice's weight in each component, and ``out_eigenvalues`` a table
    of each component's eigenvalue and share of the variance. ``number_of_pc`` is a count or a percentage of the
    variance (``"90%"``).

    Refused: ``SPATIAL_REDUCTION``, which the page says reduces the computation by a random reprojection to a larger
    cell it does not specify.

    INFERRED: the covariance divides by n − 1 and centres each slice on its mean; a pixel NoData in any slice is left
    out and NoData in the output; the loadings table's fields are Slice (the dimension value) and PC_1 … PC_k, the
    eigenvalue table's PC, Eigenvalue, Percent and Cumulative; a variable of several dimensions uses the first value
    of the others, as documented."""
    tool = "MultidimensionalPrincipalComponents"
    from ._raster import Raster
    from ._result import Result
    if _upper(mode, "DIMENSION_REDUCTION") != "DIMENSION_REDUCTION":
        _refuse(tool, "mode", mode, "its random reprojection to a larger cell is not specified")
    cube = _mdim.cube_of(in_multidimensional_raster, tool)
    v = cube.variable(variable if _given(variable) else None)
    dim = str(dimension)
    other, indexes = v.along(dim)[0]
    _ = other
    if len(indexes) < 2:
        raise ValueError(f"{tool} needs at least two slices.")
    stack = v.data[indexes, 0].astype(np.float64)
    mask = v.mask[indexes, 0].any(axis=0)
    data = stack[:, ~mask].T
    values, vectors = np.linalg.eigh(np.cov(data, rowvar=False))
    order = np.argsort(values)[::-1]
    values, vectors = values[order], vectors[:, order]
    for k in range(vectors.shape[1]):
        if vectors[np.argmax(np.abs(vectors[:, k])), k] < 0:
            vectors[:, k] = -vectors[:, k]
    total = values.sum()
    if _given(number_of_pc) and str(number_of_pc).strip().endswith("%"):
        share = float(str(number_of_pc).strip()[:-1]) / 100.0
        n = int(np.searchsorted(np.cumsum(values) / total, share - 1e-12) + 1)
    else:
        n = int(number_of_pc) if _given(number_of_pc) else len(indexes)
    n = max(1, min(n, len(indexes)))
    means = data.mean(axis=0)
    full = np.moveaxis(stack, 0, -1) - means
    scores = full @ vectors[:, :n]
    bands = [np.where(mask, 0, scores[..., k]) for k in range(n)]
    pc = Raster._of_bands(bands, [mask.copy() for _ in bands], cube.grid, [f"PC_{k}" for k in range(1, n + 1)]) \
        if n > 1 else Raster._of(bands[0], mask.copy(), cube.grid)
    pc_path = pc.save(out_pc) if not _mdim.is_multidimensional_path(out_pc) else _mdim.write(
        Cube(cube.grid, [Variable("PC", ["Band"], [{"Band": 1.0}], np.stack(bands)[None], np.stack(
            [mask] * n)[None], {}, [f"PC_{k}" for k in range(1, n + 1)])]), out_pc)
    labels = [str(_mdim.shown(dim, v.coords[i][dim])) for i in indexes]
    loadings = _write_table(out_loadings, [("Slice", "String")] + [(f"PC_{k}", "Double") for k in range(1, n + 1)],
                            [[labels[i]] + [float(vectors[i, k]) for k in range(n)] for i in range(len(indexes))], tool)
    outputs = [pc_path, loadings]
    if _given(out_eigenvalues):
        cumulative = np.cumsum(values) / total * 100
        outputs.append(_write_table(out_eigenvalues, [("PC", "Integer"), ("Eigenvalue", "Double"), ("Percent", "Double"),
                                                      ("Cumulative", "Double")],
                                    [[k + 1, float(values[k]), float(values[k] / total * 100), float(cumulative[k])]
                                     for k in range(len(values))], tool))
    return Result(*outputs)


def _correlation(x, y, method):
    """A correlation and its two-sided p-value: Pearson's r with the t-test on n − 2 degrees of freedom, Spearman's
    rho (Pearson's r of the average ranks) with the same test, and Kendall's tau-b with its normal approximation."""
    from scipy import stats
    n = x.size
    if n < 3 or np.all(x == x[0]) or np.all(y == y[0]):
        return np.nan, np.nan
    if method == "KENDALL":
        result = stats.kendalltau(x, y, method="asymptotic")
        return float(result.statistic), float(result.pvalue)
    if method == "SPEARMAN":
        x, y = stats.rankdata(x), stats.rankdata(y)
    r = float(np.corrcoef(x, y)[0, 1])
    r = max(-1.0, min(1.0, r))
    if abs(r) == 1:
        return r, 0.0
    t = r * math.sqrt((n - 2) / (1 - r * r))
    return r, float(2 * stats.t.sf(abs(t), n - 2))


@equivalent("Pearson, Spearman and Kendall correlations with their standard significance tests",
            "Pearson 1896; Spearman 1904; Kendall 1938, Biometrika 30; Sheskin 2011, Handbook of Parametric and "
            "Nonparametric Statistical Procedures, tests 28 and 30")
def MultidimensionalRasterCorrelation(in_mdim_raster1, in_mdim_raster2, dimension1=None, variable1=None,
                                      dimension2=None, variable2=None, corr_method="PEARSON", lag=0,
                                      calculate_xcorr="NO_CROSS_CORRELATION", calculate_pvalue="NO_P_VALUE",
                                      out_max_corr_raster=None):
    """EQUIVALENT: each pixel's correlation between the two rasters' arrays along their dimensions, as documented:
    the longer array's tail is trimmed, a positive lag compares the first array with the second shifted forward and
    a negative lag the first shifted back (the page's example: arrays 0–3 and 4–9 compare 0–3 with 6–9 at lag 2 and
    1–3 with 4–6 at lag −1). ``ALL_CROSS_CORRELATION`` gives a band per lag from −lag to +lag; ``CALCULATE_P_VALUE``
    adds a p-value band after each. ``out_max_corr_raster`` holds the largest correlation and the lag it is at. The
    page does not name the test behind its p-value; grapy uses each coefficient's standard one.

    INFERRED: a pair with NoData on either side is left out of that pixel's correlation; bands run from −lag to +lag
    with the p-values after the correlations; fewer than three pairs give NoData."""
    tool = "MultidimensionalRasterCorrelation"
    from ._raster import Raster
    method = _upper(corr_method, "PEARSON")
    if method not in ("PEARSON", "SPEARMAN", "KENDALL"):
        raise ValueError(f"{tool}: corr_method must be PEARSON, SPEARMAN or KENDALL.")
    first, second = _mdim.cube_of(in_mdim_raster1, tool), _mdim.cube_of(in_mdim_raster2, tool)
    if not first.grid.same(second.grid):
        raise ValueError(f"{tool}: the two rasters must share cell size, extent and spatial reference.")
    v1 = first.variable(variable1 if _given(variable1) else None)
    v2 = second.variable(variable2 if _given(variable2) else None)

    def series(v, dim):
        d = str(dim) if _given(dim) else v.dims[0]
        if len(v.dims) > 1 and not _given(dim):
            raise ValueError(f"{tool}: '{v.name}' has two dimensions; name the one to correlate along.")
        _, indexes = v.along(d)[0]
        if len(indexes) <= 2:
            raise ValueError(f"{tool}: the dimension must be longer than 2.")
        return v.data[indexes, 0].astype(np.float64), ~v.mask[indexes, 0]

    a, va = series(v1, dimension1)
    b, vb = series(v2, dimension2)
    k = int(lag or 0)
    limit = min(a.shape[0], b.shape[0]) // 2
    if abs(k) > limit:
        raise ValueError(f"{tool}: the lag ranges from 0 to half the dimension ({limit}).")
    lags = list(range(-abs(k), abs(k) + 1)) if _flag(calculate_xcorr, "ALL_CROSS_CORRELATION") else [k]
    want_p = _flag(calculate_pvalue, "CALCULATE_P_VALUE")
    rows, cols = a.shape[1:]
    corr = np.full((len(lags), rows, cols), np.nan)
    pval = np.full((len(lags), rows, cols), np.nan)
    for li, shift in enumerate(lags):
        x, vx, y, vy = (a, va, b[shift:], vb[shift:]) if shift >= 0 else (a[-shift:], va[-shift:], b, vb)
        n = min(x.shape[0], y.shape[0])
        x, vx, y, vy = x[:n], vx[:n], y[:n], vy[:n]
        for r in range(rows):
            for c in range(cols):
                both = vx[:, r, c] & vy[:, r, c]
                corr[li, r, c], pval[li, r, c] = _correlation(x[both, r, c], y[both, r, c], method)
    bands = list(corr) + (list(pval) if want_p else [])
    masks = [~np.isfinite(bd) for bd in bands]
    names = [f"Lag_{s}" for s in lags] + ([f"P_Value_Lag_{s}" for s in lags] if want_p else [])
    out = Raster._of_bands([np.where(m, 0, bd) for bd, m in zip(bands, masks)], masks, first.grid, names) \
        if len(bands) > 1 else Raster._of(np.where(masks[0], 0, bands[0]), masks[0], first.grid)
    if _given(out_max_corr_raster):
        if not _flag(calculate_xcorr, "ALL_CROSS_CORRELATION"):
            raise ValueError(f"{tool}: out_max_corr_raster is made only with ALL_CROSS_CORRELATION.")
        filled = np.where(np.isfinite(corr), corr, -np.inf)
        best = np.argmax(filled, axis=0)
        top = np.take_along_axis(filled, best[None], 0)[0]
        empty = ~np.isfinite(top)
        at = np.array(lags, float)[best]
        Raster._of_bands([np.where(empty, 0, top), np.where(empty, 0, at)], [empty, empty.copy()], first.grid,
                         ["Max_Correlation", "Lag"]).save(out_max_corr_raster)
    return out


# ---- RasterToXarray / XarrayToRaster


def RasterToXarray(in_raster):
    """A multidimensional raster as an ``xarray.Dataset``: a data variable per variable over its dimensions, then
    ``y`` and ``x`` (and ``band`` before them when its slices have several bands), with coordinates at the cell centres
    and StdTime as ``datetime64``. The page documents only that the result is a Dataset of labelled arrays.

    INFERRED: the dimension names are arcpy's (StdTime …), as the page's sample resamples ``StdTime``; NoData is NaN;
    the Dataset's attributes hold ``crs`` (``EPSG:<code>``) and ``transform`` (the GDAL geotransform), and each data
    variable's its own attributes and band names. A variable whose slices do not fill every combination of its
    dimension values cannot be an array and is refused."""
    import xarray as xr
    cube = _mdim.cube_of(in_raster, "RasterToXarray")
    g = cube.grid
    xs, ys = g.centres()
    data_vars = {}
    for v in cube.variables.values():
        axes = [v.values(d) for d in v.dims]
        if int(np.prod([len(a) for a in axes])) != len(v.coords):
            raise NotBuiltError(f"RasterToXarray: '{v.name}' does not have a slice for every combination of its "
                                "dimension values, so it is not an array.")
        full = np.full([len(a) for a in axes] + [v.bands, g.rows, g.columns], np.nan)
        for i, c in enumerate(v.coords):
            index = tuple(next(k for k, f in enumerate(axis) if _mdim._same(c[d], f)) for d, axis in zip(v.dims, axes))
            full[index] = np.where(v.mask[i], np.nan, v.data[i])
        coords = {d: (np.array(axis, "datetime64[ns]") if d == TIME else np.array(axis, float))
                  for d, axis in zip(v.dims, axes)}
        dims = list(v.dims)
        attrs = {k: a for k, a in v.attrs.items() if isinstance(a, (str, int, float))}
        if v.bands > 1:
            dims.append("band")
            coords["band"] = v.names_of_bands()
        else:
            full = full[..., 0, :, :]
        data_vars[v.name] = xr.DataArray(full, dims=dims + ["y", "x"], coords={**coords, "y": ys, "x": xs},
                                         attrs=attrs, name=v.name)
    attrs = {"transform": [g.left, g.width, 0.0, g.top, 0.0, -g.height]}
    if g.srid:
        attrs["crs"] = f"EPSG:{g.srid}"
    return xr.Dataset(data_vars, attrs=attrs)


def XarrayToRaster(in_xarray):
    """An ``xarray.Dataset`` (or DataArray) as a multidimensional raster: each data variable over ``y`` and ``x``
    (or ``lat`` and ``lon``) is a variable, its other dimensions its dimensions, a ``datetime64`` one (or one named
    ``time``) called StdTime and a ``band`` one its slices' bands.

    INFERRED: the reference is read from the Dataset's ``crs`` attribute (``EPSG:<code>`` or WKT), from a ``spatial_ref``
    or ``crs`` variable's ``spatial_ref`` or ``crs_wkt`` attribute, or is WGS 84 for ``lat``/``lon``; NaN is NoData;
    the coordinates must be evenly spaced cell centres."""
    import xarray as xr
    tool = "XarrayToRaster"
    ds = in_xarray.to_dataset() if isinstance(in_xarray, xr.DataArray) else in_xarray
    if not isinstance(ds, xr.Dataset):
        raise TypeError(f"{tool}: expected an xarray.Dataset, got {type(in_xarray).__name__}.")
    xname = next((n for n in ("x", "lon", "longitude") if n in ds.dims), None)
    yname = next((n for n in ("y", "lat", "latitude") if n in ds.dims), None)
    if xname is None or yname is None:
        raise ValueError(f"{tool}: the Dataset needs x and y (or lon and lat) dimensions.")
    srid = 0
    text = ds.attrs.get("crs")
    for name in ("spatial_ref", "crs"):
        if not text and name in ds.variables:
            text = ds[name].attrs.get("spatial_ref") or ds[name].attrs.get("crs_wkt")
    if text:
        srid = _mdim._srid_of(text)
    elif xname != "x":
        srid = 4326
    xs, ys = np.asarray(ds[xname].values, float), np.asarray(ds[yname].values, float)
    transform = ds.attrs.get("transform")
    if transform is not None and len(transform) == 6 and transform[2] == 0 and transform[4] == 0 and transform[5] < 0:
        from ._raster import _Grid
        grid = _Grid(transform[0], transform[3], transform[1], -transform[5], ys.size, xs.size, srid)
    else:
        grid = _mdim._grid_from(xs, ys, srid)
    flip = ys.size > 1 and ys[0] < ys[-1]
    variables = []
    for name, da_ in ds.data_vars.items():
        if xname not in da_.dims or yname not in da_.dims:
            continue
        outer = [d for d in da_.dims if d not in (xname, yname, "band")]
        order = outer + (["band"] if "band" in da_.dims else []) + [yname, xname]
        arr = da_.transpose(*order)
        values = np.asarray(arr.values, np.float64)
        if "band" not in da_.dims:
            values = values[..., None, :, :]
        if flip:
            values = values[..., ::-1, :]
        dims, axes = [], []
        for d in outer:
            coord = np.asarray(ds[d].values) if d in ds.coords else np.arange(ds.sizes[d], dtype=float)
            if np.issubdtype(coord.dtype, np.datetime64) or str(d).lower() in ("time", "stdtime"):
                dims.append(TIME)
                axes.append([_mdim.to_time(np.datetime64(t, "s")) for t in coord.astype("datetime64[s]")])
            else:
                dims.append(str(d))
                axes.append([float(t) for t in coord])
        count = int(np.prod([len(a) for a in axes])) if axes else 1
        values = values.reshape((count,) + values.shape[-3:])
        mask = ~np.isfinite(values)
        coords = [dict(zip(dims, combo)) for combo in _mdim._product(axes)] if axes else [{"Band": 1.0}]
        band_names = [str(b) for b in ds["band"].values] if "band" in da_.dims and "band" in ds.coords else None
        attrs = {k: a for k, a in da_.attrs.items() if isinstance(a, (str, int, float))}
        variables.append(Variable(str(name), dims or ["Band"], coords, np.where(mask, 0, values), mask, attrs,
                                  band_names))
    if not variables:
        raise ValueError(f"{tool}: the Dataset has no data variable over its x and y dimensions.")
    return _result(variables, grid)


# ---- Interpolate From Spatiotemporal Points


def _sibson(xs, ys, zs, grid):
    """Sibson's natural neighbour interpolation at the grid's cell centres (as sa.NaturalNeighbor computes it)."""
    import shapely
    from shapely.geometry import MultiPoint, Point as _P, box
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
            out[r, c], empty[r, c] = (stolen * zs).sum() / stolen.sum(), False
    return out, empty


def _interpolate(method, xs, ys, zs, grid):
    cx, cy = grid.centres()
    gx, gy = np.meshgrid(cx, cy)
    if method == "IDW":
        distance = np.hypot(gx[..., None] - xs, gy[..., None] - ys)
        radius = 20 * grid.width
        near = distance <= radius
        with np.errstate(all="ignore"):
            weight = np.where(near, 1.0 / distance ** 2, 0.0)
        exact = near & (distance == 0)
        weight = np.where(exact.any(axis=-1)[..., None], exact.astype(float), weight)
        total = weight.sum(axis=-1)
        empty = total == 0
        with np.errstate(all="ignore"):
            value = (weight * zs).sum(axis=-1) / np.where(empty, 1, total)
        return value, empty
    if method == "NEAREST_NEIGHBOR":
        distance = np.hypot(gx[..., None] - xs, gy[..., None] - ys)
        return zs[np.argmin(distance, axis=-1)], np.zeros(gx.shape, bool)
    if method in ("MEAN", "MEDIAN"):
        out = np.zeros(gx.shape)
        empty = np.ones(gx.shape, bool)
        cells = {}
        for x, y, z in zip(xs, ys, zs):
            at = grid.cell_of(x, y)
            if at is None and np.isclose(x, grid.right):
                at = grid.cell_of(x - grid.width / 2, y)
            if at is None and np.isclose(y, grid.bottom):
                at = grid.cell_of(x, y + grid.height / 2)
            if at is not None:
                cells.setdefault(at, []).append(z)
        for (r, c), values in cells.items():
            out[r, c] = np.mean(values) if method == "MEAN" else np.median(values)
            empty[r, c] = False
        return out, empty
    if method == "TRIANGULATION":
        from scipy.interpolate import LinearNDInterpolator
        value = LinearNDInterpolator(np.column_stack([xs, ys]), zs)(gx, gy)
        return np.nan_to_num(value), ~np.isfinite(value)
    return _sibson(xs, ys, zs, grid)


@equivalent("inverse distance, nearest neighbour, cell mean and median, linear interpolation on a Delaunay "
            "triangulation and Sibson's natural neighbours, for each period's points",
            "Shepard 1968, Proc. 23rd ACM National Conference; Delaunay 1934; Sibson 1981, in Interpreting "
            "Multivariate Data, pp. 21-36")
def InterpolateFromSpatiotemporalPoints(in_dataset, out_raster, variable_field, time_field,
                                        temporal_aggregation="DAILY", cell_size=None, interpolation_method=None):
    """EQUIVALENT: points grouped by period (DAILY, WEEKLY, MONTHLY, QUARTERLY or YEARLY) and each period's points
    interpolated into a slice of a multidimensional raster written to ``out_raster``, as documented. IDW is the
    average weighted by inverse distance squared within 20 cells, as documented (Shepard 1968); NEAREST_NEIGHBOR the
    point nearest the cell centre; MEAN and MEDIAN of the points; TRIANGULATION linear on the Delaunay triangulation
    and NATURAL_NEIGHBOR Sibson's (1981) method, both over a TIN of the points, as documented.

    Refused: QUADRATIC, whose function of x, y and t the page gives only as an image and whose iterative least squares
    it does not specify; a trajectory layer or dataset, a data model grapy does not hold (ADR-224 group 2).

    INFERRED (ADR-236 §5.9): MEAN and MEDIAN take the points that fall in each cell; a cell with no point within IDW's
    radius, or outside the triangulation, is NoData; a slice's StdTime is its period's start; the grid covers every
    point, its top-left corner at their extent's, its cell the shorter side over 250 by default, as documented; the
    variable is named after the variable field."""
    tool = "InterpolateFromSpatiotemporalPoints"
    from . import da
    from ._raster import _Grid
    from ._result import Result
    from ._mosaic import exists as _is_mosaic
    from .analysis import _schema
    method = _upper(interpolation_method, None)
    if method is None:
        raise ValueError(f"{tool}: interpolation_method is required.")
    if method == "QUADRATIC":
        _refuse(tool, "interpolation_method", method, "its function is given only as an image and its iterative least "
                "squares is not specified")
    if method not in ("IDW", "TRIANGULATION", "MEAN", "MEDIAN", "NATURAL_NEIGHBOR", "NEAREST_NEIGHBOR"):
        raise ValueError(f"{tool}: '{interpolation_method}' is not one of its interpolation methods.")
    keyword = _upper(temporal_aggregation, "DAILY")
    if keyword not in ("DAILY", "WEEKLY", "MONTHLY", "QUARTERLY", "YEARLY"):
        raise ValueError(f"{tool}: temporal_aggregation must be DAILY, WEEKLY, MONTHLY, QUARTERLY or YEARLY.")
    if isinstance(in_dataset, (str, os.PathLike)) and _is_mosaic(in_dataset):
        _refuse(tool, "in_dataset", in_dataset, "a mosaic dataset of trajectories is a trajectory dataset, a data model "
                "grapy does not hold (ADR-224 group 2)")
    xs, ys, zs, ts = [], [], [], []
    with da.SearchCursor(in_dataset, [variable_field, time_field, "SHAPE@XY"]) as rows:
        for value, when, xy in rows:
            if value is None or when is None or xy is None:
                continue
            xs.append(float(xy[0]))
            ys.append(float(xy[1]))
            zs.append(float(value))
            ts.append(_mdim.to_time(when))
    if not xs:
        raise ValueError(f"{tool}: the input has no point with a value and a time.")
    xs, ys, zs = np.array(xs), np.array(ys), np.array(zs)
    if _given(cell_size):
        cell = float(cell_size)
    else:
        shorter = min(xs.max() - xs.min(), ys.max() - ys.min())
        if shorter <= 0:
            raise ValueError(f"{tool}: the points' extent has no width or height to divide by 250; give a cell_size.")
        cell = shorter / 250.0
    columns = max(1, int(math.floor((xs.max() - xs.min()) / cell)) + 1)
    rows_ = max(1, int(math.floor((ys.max() - ys.min()) / cell)) + 1)
    srid = _schema(in_dataset)[2] or 0
    grid = _Grid(xs.min(), ys.max(), cell, cell, rows_, columns, srid)
    groups = {}
    for k, t in enumerate(ts):
        key, start = period(t, keyword)
        groups.setdefault(key, (start, []))[1].append(k)
    coords, data, mask = [], [], []
    for start, members in sorted(groups.values(), key=lambda g: _mdim._key(g[0])):
        m = np.array(members)
        if method in ("TRIANGULATION", "NATURAL_NEIGHBOR"):
            if len({(x, y) for x, y in zip(xs[m], ys[m])}) != len(m):
                _refuse(tool, "in_dataset", in_dataset, "coincident points in one period cannot both be TIN nodes")
            if len(m) < 3:
                value, empty = np.zeros((grid.rows, grid.columns)), np.ones((grid.rows, grid.columns), bool)
            else:
                value, empty = _interpolate(method, xs[m], ys[m], zs[m], grid)
        else:
            value, empty = _interpolate(method, xs[m], ys[m], zs[m], grid)
        coords.append({TIME: start})
        data.append(np.where(empty, 0, value)[None])
        mask.append(empty[None])
    cube = Cube(grid, [Variable(str(variable_field), [TIME], coords, np.stack(data), np.stack(mask))])
    return Result(_mdim.write(cube, out_raster))
