"""Combinatorial And, Or and XOr (ADR-242): built to *How the combinatorial math tools work*.

That page defines what the tool pages leave out: a cell NoData in either input is NoData; the remaining cells are
scanned left to right and top to bottom, an internal table recording each unique pair of input values as it is first
met; the tool's condition is then applied to each pair — And: both inputs non-zero; Or: either non-zero; XOr: one zero
and the other not — and a false pair answers 0 while each true pair answers *"the sequential number of that condition"*,
counting only the true pairs, in the order they were first met. The page's own example (4 × 4 inputs, nine unique
pairs) is a test. The output's attribute table carries, beside Value and Count, *"two items … that show the cell values
from the input rasters"* — the parentage.

INFERRED: the two items are named after the inputs as Combine names its fields (a constant is ``constant1`` or
``constant2``); the false value 0 has a row whose two items are null, since 0 stands for every false pair at once.
"""
from __future__ import annotations

import numpy as np

from ._raster import Raster, _Grid, _refuse_environment, as_raster
from ._runtime import env

_CONDITIONS = {
    "CombinatorialAnd": lambda a, b: a != 0 and b != 0,
    "CombinatorialOr": lambda a, b: a != 0 or b != 0,
    "CombinatorialXOr": lambda a, b: (a != 0) != (b != 0),
}


def _operand(value, tool, which):
    """A raster input as a Raster, or a number as itself; a floating-point input is refused, the page requiring
    integers."""
    if isinstance(value, (int, np.integer)):
        return int(value)
    if isinstance(value, (float, np.floating)):
        if float(value).is_integer():
            return int(value)
        raise ValueError(f"{tool}: input {which} must be of integer type, as the page requires; {value} is not.")
    raster = as_raster(value, tool)
    if not raster.isInteger:
        raise ValueError(f"{tool}: input {which} must be of integer type, as the page requires; {value!r} is floating "
                         "point.")
    return raster


def _constant_grid(tool):
    """The grid two constants answer on: the page requires the cell size and extent environments."""
    from ._reference import Extent
    cell, extent = env.cellSize, env.extent
    if cell in (None, "", "MAXOF", "MINOF") or extent in (None, "", "MAXOF", "MINOF"):
        raise ValueError(f"{tool}: to give a number for both inputs, set the cell size and extent environments first, "
                         "as the page says.")
    if not isinstance(extent, Extent):
        extent = Extent(*[float(v) for v in str(extent).split()[:4]])
    size = float(cell)
    columns = max(1, int(np.ceil((extent.XMax - extent.XMin) / size)))
    rows = max(1, int(np.ceil((extent.YMax - extent.YMin) / size)))
    srid = getattr(getattr(env, "outputCoordinateSystem", None), "factoryCode", 0) or 0
    return _Grid(extent.XMin, extent.YMax, size, size, rows, columns, srid)


def _names(values):
    from .sa import _dataset_name
    taken, names = {"value", "count"}, []
    for i, value in enumerate(values):
        if isinstance(value, Raster):
            names.append(_dataset_name(value, i, taken))
        else:
            names.append(f"constant{i + 1}")
            taken.add(names[-1])
    return names


def combinatorial(tool, in_raster_or_constant1, in_raster_or_constant2):
    from ._raster import _Rat, _align
    values = [_operand(in_raster_or_constant1, tool, 1), _operand(in_raster_or_constant2, tool, 2)]
    rasters = [v for v in values if isinstance(v, Raster)]
    if rasters:
        _refuse_environment(f"arcpy.sa.{tool}")
        arrays, masks, grid = _align(rasters, tool)
        it = iter(zip(arrays, masks))
        planes, mask = [], np.zeros((grid.rows, grid.columns), bool)
        for v in values:
            if isinstance(v, Raster):
                a, m = next(it)
                planes.append(np.asarray(a).astype(np.int64))
                mask |= m
            else:
                planes.append(np.full((grid.rows, grid.columns), v, np.int64))
    else:
        grid = _constant_grid(tool)
        mask = np.zeros((grid.rows, grid.columns), bool)
        planes = [np.full(mask.shape, v, np.int64) for v in values]
    condition = _CONDITIONS[tool]
    numbers, counts, false_count = {}, {}, 0
    out = np.zeros(mask.shape, np.int32)
    true_number = 0
    for r in range(mask.shape[0]):
        for c in range(mask.shape[1]):
            if mask[r, c]:
                continue
            pair = (int(planes[0][r, c]), int(planes[1][r, c]))
            if pair not in numbers:
                if condition(*pair):
                    true_number += 1
                    numbers[pair] = true_number
                else:
                    numbers[pair] = 0
            number = numbers[pair]
            out[r, c] = number
            if number:
                counts[pair] = counts.get(pair, 0) + 1
            else:
                false_count += 1
    first, second = _names(values)
    rows = [[0, false_count, None, None]] if false_count else []
    rows += [[n, counts[p], p[0], p[1]] for p, n in numbers.items() if n]
    result = Raster._of(out, mask, grid)
    result._rat = _Rat([("Value", "Integer", 5), ("Count", "Integer", 1), (first, "Integer", 0),
                        (second, "Integer", 0)], rows)
    return result


def CombinatorialAnd(in_raster_or_constant1, in_raster_or_constant2):
    """A distinct number for each unique pair of input values where both are non-zero, 0 where either is zero; see
    the module's docstring for the numbering *How the combinatorial math tools work* defines."""
    return combinatorial("CombinatorialAnd", in_raster_or_constant1, in_raster_or_constant2)


def CombinatorialOr(in_raster_or_constant1, in_raster_or_constant2):
    """A distinct number for each unique pair of input values where either is non-zero, 0 where both are zero; see
    the module's docstring."""
    return combinatorial("CombinatorialOr", in_raster_or_constant1, in_raster_or_constant2)


def CombinatorialXOr(in_raster_or_constant1, in_raster_or_constant2):
    """A distinct number for each unique pair of input values where exactly one is non-zero, 0 elsewhere; see the
    module's docstring."""
    return combinatorial("CombinatorialXOr", in_raster_or_constant1, in_raster_or_constant2)
