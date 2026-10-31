"""Path distance, distance accumulation and least-cost paths (ADR-241).

The pages this follows, all read on 2026-10-31 under doc.esri.com/en/arcgis-pro/latest/tool-reference/spatial-analyst/:

* *Understanding path distance analysis* — surface distance by Pythagoras over the planimetric step, the back-link codes
  (1 the neighbour to the right, 2 lower right, 3 below, clockwise to 8 upper right, 0 on the sources);
* *How the path distance tools work* — the step cost
  ``(Cost(a)·HF(a) + Cost(b)·HF(b)) / 2 · Surf_dist(ab) · VF(ab)``, settled cheapest-first;
* *How the horizontal and vertical factors affect path distance* and *Adjust the encountered distance using a
  vertical/horizontal factor* — the HRMA and VRMA and every factor graph with its modifiers and defaults;
* *Distance accumulation algorithm* — the surface reconstruction (eight network estimates and four Eikonal steps per
  cell, Sethian 1999) and the effective slope ``C · S · M · HF · VF``;
* *Cost Path*, *Creating the least-cost path*, *Optimal Path As Raster / As Line* — path values, path types and the
  lattice tracing of a back-direction raster.

The factor classes (``HfBinary`` … ``VfTable``) are arcpy.sa's and arcpy.ia's both.
"""
from __future__ import annotations

import heapq
import math
import os

import numpy as np

from ._runtime import NotBuiltError

DIAGONAL = 1.414214
# The back-link codes 1..8, as (row step, column step): to the right, lower right, below … upper right.
NEIGHBOURS = ((0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1))
_D8 = {1: (0, 1), 2: (1, 1), 4: (1, 0), 8: (1, -1), 16: (0, -1), 32: (-1, -1), 64: (-1, 0), 128: (-1, 1)}


def _given(value):
    """Whether an optional parameter was given; a Raster is compared by identity, never by map algebra."""
    return value is not None and not (isinstance(value, str) and value in ("", "#"))


def _azimuth(dr, dk):
    """The compass bearing of a step of ``dr`` rows (down) and ``dk`` columns (right): 0 north, 90 east."""
    return math.degrees(math.atan2(dk, -dr)) % 360.0


# ---- the factor graphs


class _Factor:
    _fields = ()
    _keyword = ""

    def __repr__(self):
        return f"{type(self).__name__}({', '.join(repr(getattr(self, f)) for f in self._fields)})"

    def __str__(self):
        return " ".join([self._keyword, *(str(getattr(self, f)) for f in self._fields)])


def _read_table(path, kind):
    """The two columns of a factor table: angles in ascending order and their factors, -1 (any negative) for
    infinity."""
    if not os.path.isfile(os.fspath(path)):
        raise ValueError(f"{kind}: the table file '{path}' does not exist.")
    points = []
    with open(os.fspath(path), encoding="utf-8") as text:
        for line in text:
            parts = line.replace(",", " ").split()
            if not parts:
                continue
            if len(parts) < 2:
                raise ValueError(f"{kind}: each line of '{path}' takes an angle and a factor.")
            points.append((float(parts[0]), float(parts[1])))
    angles = [a for a, _ in points]
    if not points or any(b <= a for a, b in zip(angles, angles[1:])):
        raise ValueError(f"{kind}: the angles of '{path}' must be given in ascending order.")
    return points


def _table_value(points, angles):
    """VfTable's reading, which HfTable shares: negative factors before the first that is not are skipped, reading
    stops at the next negative one, and outside the angles read the factor is infinite."""
    start = next((i for i, (_, f) in enumerate(points) if f >= 0), None)
    angles = np.asarray(angles, dtype=float)
    if start is None:
        return np.full(angles.shape, np.inf)
    end = start
    while end + 1 < len(points) and points[end + 1][1] >= 0:
        end += 1
    xs = np.array([a for a, _ in points[start:end + 1]])
    ys = np.array([f for _, f in points[start:end + 1]])
    out = np.interp(angles, xs, ys)
    return np.where((angles < xs[0]) | (angles > xs[-1]), np.inf, out)


def _inverse(line):
    with np.errstate(divide="ignore"):
        return np.where(line > 0, 1.0 / np.where(line > 0, line, 1.0), np.inf)


class _Hf(_Factor):
    """A horizontal factor graph: the HF of a horizontal relative moving angle (HRMA, 0 to 180 degrees)."""

    def value(self, hrma):
        raise NotImplementedError

    def _cut(self, hrma, out):
        return np.where(np.asarray(hrma) > float(self.cutAngle), np.inf, out)


class HfBinary(_Hf):
    """The zero factor where the HRMA is not beyond the cut angle, infinity beyond it."""
    _fields, _keyword = ("zeroFactor", "cutAngle"), "BINARY"

    def __init__(self, zeroFactor=1.0, cutAngle=45.0):
        self.zeroFactor, self.cutAngle = float(zeroFactor), float(cutAngle)

    def value(self, hrma):
        return self._cut(hrma, np.full(np.shape(hrma), self.zeroFactor))


class HfForward(_Hf):
    """The zero factor below 45 degrees, the side value from 45 to below 90, infinity from 90."""
    _fields, _keyword = ("zeroFactor", "sideValue"), "FORWARD"

    def __init__(self, zeroFactor=0.5, sideValue=1.0):
        self.zeroFactor, self.sideValue = float(zeroFactor), float(sideValue)

    def value(self, hrma):
        hrma = np.asarray(hrma, dtype=float)
        return np.where(hrma < 45, self.zeroFactor, np.where(hrma < 90, self.sideValue, np.inf))


class HfLinear(_Hf):
    """``zeroFactor + slope · HRMA``, infinite beyond the cut angle (181, no cut, by default)."""
    _fields, _keyword = ("zeroFactor", "cutAngle", "slope"), "LINEAR"

    def __init__(self, zeroFactor=0.5, cutAngle=181.0, slope=0.011111):
        self.zeroFactor, self.cutAngle, self.slope = float(zeroFactor), float(cutAngle), float(slope)

    def value(self, hrma):
        return self._cut(hrma, np.maximum(self.zeroFactor + self.slope * np.asarray(hrma, dtype=float), 0.0))


class HfInverseLinear(_Hf):
    """The inverse of the line ``zeroFactor + slope · HRMA`` (``1 / (2 − HRMA/90)`` by default: 0.5 straight ahead,
    infinite straight back)."""
    _fields, _keyword = ("zeroFactor", "cutAngle", "slope"), "INVERSE_LINEAR"

    def __init__(self, zeroFactor=2.0, cutAngle=180.0, slope=-0.011111):
        self.zeroFactor, self.cutAngle, self.slope = float(zeroFactor), float(cutAngle), float(slope)

    def value(self, hrma):
        return self._cut(hrma, _inverse(self.zeroFactor + self.slope * np.asarray(hrma, dtype=float)))


class HfTable(_Hf):
    """A graph read from an ASCII file of HRMA–HF pairs, joined by straight segments."""
    _fields, _keyword = ("inTable",), "TABLE"

    def __init__(self, inTable):
        self.inTable = inTable
        self._points = _read_table(inTable, "HfTable")

    def value(self, hrma):
        return _table_value(self._points, hrma)


class _Vf(_Factor):
    """A vertical factor graph: the VF of a vertical relative moving angle (VRMA, −90 to 90 degrees)."""

    def value(self, vrma, meters_per_unit=1.0):
        vrma = np.asarray(vrma, dtype=float)
        out = self._curve(vrma, meters_per_unit)
        return np.where((vrma < float(self.lowCutAngle)) | (vrma > float(self.highCutAngle)), np.inf, out)

    def _curve(self, vrma, meters_per_unit):
        raise NotImplementedError


class VfBinary(_Vf):
    """The zero factor between the cut angles, infinity outside them."""
    _fields, _keyword = ("zeroFactor", "lowCutAngle", "highCutAngle"), "BINARY"

    def __init__(self, zeroFactor=1.0, lowCutAngle=-30.0, highCutAngle=30.0):
        self.zeroFactor, self.lowCutAngle, self.highCutAngle = float(zeroFactor), float(lowCutAngle), float(highCutAngle)

    def _curve(self, vrma, _):
        return np.full(vrma.shape, self.zeroFactor)


class _VfLine(_Vf):
    _fields = ("zeroFactor", "lowCutAngle", "highCutAngle", "slope")
    _symmetric = _inverted = False

    def __init__(self, zeroFactor=1.0, lowCutAngle=-90.0, highCutAngle=90.0, slope=0.011111):
        self.zeroFactor, self.lowCutAngle = float(zeroFactor), float(lowCutAngle)
        self.highCutAngle, self.slope = float(highCutAngle), float(slope)

    def _curve(self, vrma, _):
        line = self.zeroFactor + self.slope * (np.abs(vrma) if self._symmetric else vrma)
        return _inverse(line) if self._inverted else np.maximum(line, 0.0)


class VfLinear(_VfLine):
    """``zeroFactor + slope · VRMA``."""
    _keyword = "LINEAR"


class VfSymLinear(_VfLine):
    """``zeroFactor + slope · |VRMA|``: the positive side's line mirrored onto the negative side."""
    _keyword, _symmetric = "SYM_LINEAR", True


class VfInverseLinear(_VfLine):
    """``1 / (zeroFactor + slope · VRMA)``."""
    _keyword, _inverted = "INVERSE_LINEAR", True

    def __init__(self, zeroFactor=1.0, lowCutAngle=-45.0, highCutAngle=45.0, slope=-0.022222):
        super().__init__(zeroFactor, lowCutAngle, highCutAngle, slope)


class VfSymInverseLinear(_VfLine):
    """``1 / (zeroFactor + slope · |VRMA|)``."""
    _keyword, _symmetric, _inverted = "SYM_INVERSE_LINEAR", True, True

    def __init__(self, zeroFactor=1.0, lowCutAngle=-45.0, highCutAngle=45.0, slope=-0.022222):
        super().__init__(zeroFactor, lowCutAngle, highCutAngle, slope)


def _cos(vrma, power):
    return np.abs(np.cos(np.radians(vrma))) ** power


def _sec(vrma, power):
    c = np.cos(np.radians(vrma))
    with np.errstate(divide="ignore"):
        return np.where(np.abs(c) < 1e-12, np.inf, np.abs(1.0 / np.where(np.abs(c) < 1e-12, 1.0, c)) ** power)


class VfCos(_Vf):
    """``cos(VRMA) ^ cosPower``."""
    _fields, _keyword = ("lowCutAngle", "highCutAngle", "cosPower"), "COS"

    def __init__(self, lowCutAngle=-90.0, highCutAngle=90.0, cosPower=1.0):
        self.lowCutAngle, self.highCutAngle, self.cosPower = float(lowCutAngle), float(highCutAngle), float(cosPower)

    def _curve(self, vrma, _):
        return _cos(vrma, self.cosPower)


class VfSec(_Vf):
    """``sec(VRMA) ^ secPower``."""
    _fields, _keyword = ("lowCutAngle", "highCutAngle", "secPower"), "SEC"

    def __init__(self, lowCutAngle=-90.0, highCutAngle=90.0, secPower=1.0):
        self.lowCutAngle, self.highCutAngle, self.secPower = float(lowCutAngle), float(highCutAngle), float(secPower)

    def _curve(self, vrma, _):
        return _sec(vrma, self.secPower)


class VfCosSec(_Vf):
    """``cos(VRMA) ^ cosPower`` on a negative VRMA, ``sec(VRMA) ^ secPower`` on one that is not."""
    _fields, _keyword = ("lowCutAngle", "highCutAngle", "cosPower", "secPower"), "COS_SEC"

    def __init__(self, lowCutAngle=-90.0, highCutAngle=90.0, cosPower=1.0, secPower=1.0):
        self.lowCutAngle, self.highCutAngle = float(lowCutAngle), float(highCutAngle)
        self.cosPower, self.secPower = float(cosPower), float(secPower)

    def _curve(self, vrma, _):
        return np.where(vrma < 0, _cos(vrma, self.cosPower), _sec(vrma, self.secPower))


class VfSecCos(_Vf):
    """``sec(VRMA) ^ secPower`` on a negative VRMA, ``cos(VRMA) ^ cosPower`` on one that is not."""
    _fields, _keyword = ("lowCutAngle", "highCutAngle", "secPower", "cosPower"), "SEC_COS"

    def __init__(self, lowCutAngle=-90.0, highCutAngle=90.0, secPower=1.0, cosPower=1.0):
        self.lowCutAngle, self.highCutAngle = float(lowCutAngle), float(highCutAngle)
        self.secPower, self.cosPower = float(secPower), float(cosPower)

    def _curve(self, vrma, _):
        return np.where(vrma < 0, _sec(vrma, self.secPower), _cos(vrma, self.cosPower))


def _pace(vrma, meters_per_unit):
    """The reciprocal of Tobler's (1993) hiking function ``W = 6 · exp(−3.5 · |tan d + 0.05|)`` km/h, in hours per
    map unit: hours per metre (``1 / (1000 · W)``, 0.000198541 on the flat, as the page's table gives) times the
    metres in a map unit."""
    speed = 6000.0 * np.exp(-3.5 * np.abs(np.tan(np.radians(vrma)) + 0.05))
    return meters_per_unit / speed


class VfHikingTime(_Vf):
    """The reciprocal of Tobler's hiking function: the pace in hours per map unit."""
    _fields, _keyword = ("lowCutAngle", "highCutAngle"), "HIKING_TIME"

    def __init__(self, lowCutAngle=-70.0, highCutAngle=70.0):
        self.lowCutAngle, self.highCutAngle = float(lowCutAngle), float(highCutAngle)

    def _curve(self, vrma, meters_per_unit):
        return _pace(vrma, meters_per_unit)


class VfBidirHikingTime(VfHikingTime):
    """The mean of the hiking-time pace at the VRMA and at its negative: one way of an out-and-back trail."""
    _keyword = "BIDIR_HIKING_TIME"

    def _curve(self, vrma, meters_per_unit):
        return (_pace(vrma, meters_per_unit) + _pace(-vrma, meters_per_unit)) / 2.0


class VfTable(_Vf):
    """A graph read from an ASCII file of VRMA–VF pairs, joined by straight segments."""
    _fields, _keyword = ("inTable",), "TABLE"
    lowCutAngle, highCutAngle = -90.0, 90.0

    def __init__(self, inTable):
        self.inTable = inTable
        self._points = _read_table(inTable, "VfTable")

    def _curve(self, vrma, _):
        return _table_value(self._points, vrma)


_HF = {c._keyword: c for c in (HfBinary, HfForward, HfLinear, HfInverseLinear, HfTable)}
_VF = {c._keyword: c for c in (VfBinary, VfLinear, VfSymLinear, VfInverseLinear, VfSymInverseLinear, VfCos, VfSec,
                               VfCosSec, VfSecCos, VfHikingTime, VfBidirHikingTime, VfTable)}


def _factor(value, kind, tool):
    """A factor object, or one built from a keyword string (``"BINARY 1 45"``, ``"TABLE c:/hf.txt"``); with the
    raster given and the factor not, Binary with its defaults (INFERRED, ADR-241 §5.2)."""
    classes, base = (_HF, _Hf) if kind == "horizontal" else (_VF, _Vf)
    if not _given(value):
        return classes["BINARY"]()
    if isinstance(value, base):
        return value
    if isinstance(value, str):
        parts = value.split()
        cls = classes.get(parts[0].upper())
        if cls is not None:
            if cls in (HfTable, VfTable):
                return cls(value.split(None, 1)[1] if len(parts) > 1 else "")
            return cls(*[float(p) for p in parts[1:]])
    raise ValueError(f"{tool}: '{value}' is not a {kind} factor.")


# ---- inputs


def _sa():
    from . import sa
    return sa


def _optional_raster(value, tool):
    from ._raster import as_raster
    return as_raster(value, tool) if _given(value) else None


def _feature_grid(features, divisor, longer):
    """A grid over the features' extent, its cell the shorter (or longer) side divided by ``divisor``, as the tool
    pages give the default cell size."""
    import shapely
    from . import da
    from .analysis import _schema
    from ._raster import _Grid
    srid = _schema(features)[2]
    with da.SearchCursor(features, ["SHAPE@"]) as rows:
        shapes = [r[0]._shape for r in rows if r[0] is not None]
    left, bottom, right, top = shapely.GeometryCollection(shapes).bounds
    sides = [s for s in (right - left, top - bottom) if s > 0]
    if not sides:
        raise ValueError("the features' extent has no width or height to take a default cell size from.")
    cell = (max(sides) if longer else min(sides)) / divisor
    return _Grid(left, top, cell, cell, max(1, math.ceil((top - bottom) / cell)), max(1, math.ceil((right - left) / cell)),
                 srid)


def _meters_per_unit(grid):
    if not grid.srid:
        return 1.0  # a raster without a coordinate system is taken to be in metres (INFERRED)
    from pyproj import CRS
    crs = CRS.from_epsg(grid.srid)
    if crs.is_geographic:
        raise NotBuiltError("a hiking-time vertical factor on a geographic raster needs a linear unit; project it first.")
    return float(crs.axis_info[0].unit_conversion_factor)


def _place(rasters, in_source_data, tool, divisor, longer, barrier=None):
    """The analysis grid — the first raster given, in the page's order of precedence, or a default grid over the
    features — and the source cells on it."""
    sa = _sa()
    grid = next((r._grid for r in rasters if r is not None), None)
    if grid is None and sa._is_raster(in_source_data):
        grid = _optional_raster(in_source_data, tool)._grid
    if grid is None and barrier is not None and sa._is_raster(barrier):
        grid = _optional_raster(barrier, tool)._grid
    if grid is None:
        grid = _feature_grid(in_source_data, divisor, longer)
    for raster in rasters:
        if raster is not None and not raster._grid.same(grid):
            sa._refuse(tool, "input raster", raster, "the input rasters must share one grid; resampling is not built")
    if not math.isclose(grid.width, grid.height):
        sa._refuse(tool, "input raster", "(rectangular cells)", "the diagonal step is documented for square cells")
    return grid


def _source_cells(in_source_data, grid, tool, field=None):
    sa = _sa()
    if sa._is_raster(in_source_data):
        source = _optional_raster(in_source_data, tool)
        if not source._grid.same(grid):
            sa._refuse(tool, "in_source_data", in_source_data, "a source raster on another grid needs resampling")
        return ~source._mask, source._array
    burnt, empty, _ = sa._rasterize_any(in_source_data, grid, field)
    return ~empty, burnt


def _per_source(value, in_source_data, grid, tool, name, default):
    """A source characteristic: one number for every source, or a field of the source features read source by
    source."""
    sa = _sa()
    if not _given(value):
        return np.full((grid.rows, grid.columns), float(default))
    try:
        return np.full((grid.rows, grid.columns), float(value))
    except (TypeError, ValueError):
        pass
    if sa._is_raster(in_source_data):
        sa._refuse(tool, name, value, "a field is read from source features; a raster source has none")
    burnt, _, _ = sa._rasterize_any(in_source_data, grid, value)
    return burnt.astype(float)


# ---- the legacy network: Path Distance


def _shift(array, dr, dk, fill):
    """``array`` read at (r + dr, k + dk) for every (r, k); ``fill`` off the grid."""
    rows, columns = array.shape
    out = np.full(array.shape, fill, dtype=np.result_type(array.dtype, type(fill)))
    if abs(dr) < rows and abs(dk) < columns:
        out[max(0, -dr):rows - max(0, dr), max(0, -dk):columns - max(0, dk)] = \
            array[max(0, dr):rows + min(0, dr), max(0, dk):columns + min(0, dk)]
    return out


def _hrma(azimuth, direction):
    difference = np.abs((azimuth - direction) % 360.0)
    return np.minimum(difference, 360.0 - difference)


def _horizontal(hf, h, azimuth):
    """The HF of moving on ``azimuth`` through cells whose horizontal direction is ``h``; a flat cell (−1) puts up
    no horizontal friction (INFERRED, ADR-241 §5.2)."""
    return np.where(h < 0, 1.0, hf.value(_hrma(azimuth, h)))


def _steps(cost, surface, vertical, vf, horizontal, hf, to_source, cell, meters):
    """The cost of the step from each cell to each of its eight neighbours, as *How the path distance tools work*
    gives it: ``(Cost(a)·HF(a) + Cost(b)·HF(b)) / 2 · Surf_dist(ab) · VF(ab)``."""
    steps = []
    for dr, dk in NEIGHBOURS:
        run = cell * (DIAGONAL if dr and dk else 1.0)
        cost_b = _shift(cost, dr, dk, np.nan)
        surf = np.full(cost.shape, run)
        if surface is not None:
            surf = np.hypot(run, _shift(surface, dr, dk, np.nan) - surface)
        vf_ab = np.ones(cost.shape)
        if vertical is not None:
            rise = _shift(vertical, dr, dk, np.nan) - vertical
            vrma = np.degrees(np.arctan(np.where(np.isnan(rise), 0.0, rise) / run))
            if to_source:
                vrma = -vrma
            # Not rounded to the page's "0.25 degrees": Pro 3.7 answers a 5.7106° step unrounded (measured
            # 2026-10-31, ADR-241 §5.6 i).
            vf_ab = vf.value(vrma, meters)
        hf_a = hf_b = 1.0
        if horizontal is not None:
            azimuth = _azimuth(-dr, -dk) if to_source else _azimuth(dr, dk)
            hf_a = _horizontal(hf, horizontal, azimuth)
            hf_b = _horizontal(hf, np.nan_to_num(_shift(horizontal, dr, dk, np.nan), nan=-1.0), azimuth)
        with np.errstate(invalid="ignore"):
            step = (cost * hf_a + cost_b * hf_b) / 2.0 * surf * vf_ab
        steps.append(np.where(np.isnan(step), np.inf, step))
    return steps


def _dijkstra(sources, barrier, steps, start=None, traits=None):
    """The least accumulated cost from the sources, the source each cell is reached from and the back-link code of
    the neighbour it is reached through. Of two equally cheap routes the one settled first wins (INFERRED, as
    CostDistance's).

    ``traits`` (``source_traits``): the legacy source characteristics, each read at the source a route starts from, as
    *How the source characteristics affect cost distance analysis* gives them — a source's accumulated cost starts at
    its start cost; each step's cost is multiplied by its cost multiplier; past the first cell the cost accumulated so
    far is multiplied by one plus its resistance rate before the step is added (``accum_cost = a1 * (1 + resistance_rate)
    + step * cost_multiplier``, ``a1 = starting_cost + step * cost_multiplier``); and a route stops where it would
    exceed its source's capacity. Every update is increasing in the cost it starts from, so the cheapest cell on the
    list is still settled for good."""
    rows, columns = sources.shape
    total = np.full(sources.shape, np.inf)
    origin = np.full(sources.shape, -1, np.int64)
    back = np.zeros(sources.shape, np.int32)
    heap = []
    for r, k in zip(*np.nonzero(sources & ~barrier)):
        total[r, k] = (0.0 if start is None else start) if traits is None else traits["start"][r, k]
        origin[r, k] = r * columns + k
        heapq.heappush(heap, (total[r, k], r * columns + k, r, k))
    done = np.zeros(sources.shape, bool)
    while heap:
        value, _, r, k = heapq.heappop(heap)
        if done[r, k]:
            continue
        done[r, k] = True
        if traits is not None:
            sr, sk = divmod(int(origin[r, k]), columns)
            multiplier, capacity = traits["multiplier"][sr, sk], traits["capacity"][sr, sk]
            base = value if (sr, sk) == (r, k) else value * (1.0 + traits["rate"][sr, sk])
        else:
            multiplier, capacity, base = 1.0, math.inf, value
        for index, (dr, dk) in enumerate(NEIGHBOURS):
            nr, nk = r + dr, k + dk
            if not (0 <= nr < rows and 0 <= nk < columns) or barrier[nr, nk] or done[nr, nk]:
                continue
            step = steps[index][r, k]
            if not math.isfinite(step):
                continue
            reached = base + step * multiplier if traits is not None else value + step
            if reached > capacity:
                continue
            if reached < total[nr, nk]:
                total[nr, nk] = reached
                origin[nr, nk] = origin[r, k]
                back[nr, nk] = NEIGHBOURS.index((-dr, -dk)) + 1
                heapq.heappush(heap, (reached, nr * columns + nk, nr, nk))
    return total, origin, back


def source_traits(tool, in_source_data, grid, direction, multiplier=None, start=None, rate=None, capacity=None):
    """The legacy source characteristics as arrays over the grid — one value for every source, or a field of the
    source features read source by source — or None when none is given. INFERRED (D-332, Q-223 (d)): a multiplier and a capacity
    must be greater than 0, a start cost and a resistance rate at least 0, as the page's examples are; a route equal to
    its capacity is within it ("continues to grow until the capacity ... is reached").

    Refused: a resistance rate travelling FROM_SOURCE. The page gives that form for one path of five cells,
    ``a5 = c1 + c2(1+r) + c3(1+r)^2 + c4(1+r)^3 + c5(1+r)^4``: each cell's cost compounded by its count of cells from
    the source. A cell's least cost is then not a function of its neighbour's least cost — a longer, cheaper route can
    lose to a shorter, dearer one further on — so the expansion every cost distance tool documents cannot find it, and
    the page does not say how the tool takes the least over routes of different lengths."""
    sa = _sa()
    given = {name: value for name, value in (("source_cost_multiplier", multiplier), ("source_start_cost", start),
                                             ("source_resistance_rate", rate), ("source_capacity", capacity))
             if _given(value)}
    if not given:
        return None
    traits = {"multiplier": _per_source(multiplier, in_source_data, grid, tool, "source_cost_multiplier", 1.0),
              "start": _per_source(start, in_source_data, grid, tool, "source_start_cost", 0.0),
              "rate": _per_source(rate, in_source_data, grid, tool, "source_resistance_rate", 0.0),
              "capacity": _per_source(capacity, in_source_data, grid, tool, "source_capacity", math.inf)}
    for name, key, low, strict in (("source_cost_multiplier", "multiplier", 0.0, True),
                                   ("source_capacity", "capacity", 0.0, True),
                                   ("source_start_cost", "start", 0.0, False),
                                   ("source_resistance_rate", "rate", 0.0, False)):
        values = traits[key][np.isfinite(traits[key])]
        if name in given and len(values) and ((values <= low).any() if strict else (values < low).any()):
            raise ValueError(f"{tool}: {name} must be {'greater than' if strict else 'at least'} {low:g}.")
    if direction != "TO_SOURCE" and (traits["rate"] != 0).any():
        sa._refuse(tool, "source_resistance_rate", rate, "travelling FROM_SOURCE compounds each cell's cost by its count "
                   "of cells from the source, so a cell's least cost does not follow from its neighbour's and the page "
                   "does not say how the least over routes of different lengths is taken; with TO_SOURCE it is built "
                   "(D-332)")
    return traits


_PATH_SOURCE = ("source_cost_multiplier", "source_start_cost", "source_resistance_rate", "source_capacity")


def _path(tool, in_source_data, in_cost_raster, in_surface_raster, in_horizontal_raster, horizontal_factor,
          in_vertical_raster, vertical_factor, source_direction, extra, field=None):
    from ._raster import _refuse_environment
    sa = _sa()
    _refuse_environment(f"arcpy.sa.{tool}")
    direction = str(source_direction or "FROM_SOURCE").upper()
    if direction not in ("FROM_SOURCE", "TO_SOURCE"):
        sa._refuse(tool, "source_direction", source_direction, "a field of travel directions is not built")
    cost, surface, horizontal, vertical = (_optional_raster(v, tool) for v in (
        in_cost_raster, in_surface_raster, in_horizontal_raster, in_vertical_raster))
    grid = _place([cost, surface, vertical, horizontal], in_source_data, tool, 250, False)
    sources, values = _source_cells(in_source_data, grid, tool, field)
    barrier = np.zeros(sources.shape, bool)
    for raster in (cost, surface, horizontal, vertical):
        if raster is not None:
            barrier |= raster._mask
    hf = _factor(horizontal_factor, "horizontal", tool) if horizontal is not None else None
    vf = _factor(vertical_factor, "vertical", tool) if vertical is not None else None
    meters = _meters_per_unit(grid) if isinstance(vf, VfHikingTime) else 1.0

    def array(raster, fill=0.0):
        return None if raster is None else np.where(raster._mask, fill, raster._array.astype(np.float64))

    steps = _steps(array(cost, np.nan) if cost is not None else np.ones(sources.shape), array(surface),
                   array(vertical), vf, array(horizontal, -1.0), hf, direction == "TO_SOURCE", grid.width, meters)
    traits = source_traits(tool, in_source_data, grid, direction, *(extra.get(name) for name in _PATH_SOURCE))
    total, origin, back = _dijkstra(sources, barrier, steps, traits=traits)
    unreached = ~np.isfinite(total) | barrier
    return total, origin, back, unreached, values, grid


def _cut(total, unreached, maximum_distance):
    sa = _sa()
    return unreached | (total > float(maximum_distance)) if _given(maximum_distance) else unreached


def _save(array, mask, grid, out, dtype):
    from ._raster import Raster
    if _given(out):
        Raster._of(np.where(mask, 0, array).astype(dtype), mask.copy(), grid).save(out)


def PathDistance(in_source_data, in_cost_raster=None, in_surface_raster=None, in_horizontal_raster=None,
                 horizontal_factor=None, in_vertical_raster=None, vertical_factor=None, maximum_distance=None,
                 out_backlink_raster=None, source_cost_multiplier=None, source_start_cost=None,
                 source_resistance_rate=None, source_capacity=None, source_direction=None):
    """The least accumulated cost from each cell to the cheapest source, over the cost raster (1 per unit distance
    without one), the surface distance and the horizontal and vertical factors, as *How the path distance tools
    work* gives the step cost. NoData on any input is a barrier and NoData in the output; so are cells beyond
    ``maximum_distance``."""
    from ._raster import Raster
    total, _, back, unreached, _, grid = _path(
        "PathDistance", in_source_data, in_cost_raster, in_surface_raster, in_horizontal_raster, horizontal_factor,
        in_vertical_raster, vertical_factor, source_direction, dict(zip(_PATH_SOURCE, (
            source_cost_multiplier, source_start_cost, source_resistance_rate, source_capacity))))
    mask = _cut(total, unreached, maximum_distance)
    _save(back, mask, grid, out_backlink_raster, np.int32)
    return Raster._of(np.where(mask, 0, total).astype(np.float32), mask, grid)


def PathBackLink(in_source_data, in_cost_raster=None, in_surface_raster=None, in_horizontal_raster=None,
                 horizontal_factor=None, in_vertical_raster=None, vertical_factor=None, maximum_distance=None,
                 out_distance_raster=None, source_cost_multiplier=None, source_start_cost=None,
                 source_resistance_rate=None, source_capacity=None, source_direction=None):
    """The back-link code of the neighbour each cell's least-cost path to its source runs through first: 1 to the
    right, 2 lower right, 3 below, clockwise to 8 upper right; 0 on the sources. Fitted as PathDistance is."""
    from ._raster import Raster
    total, _, back, unreached, _, grid = _path(
        "PathBackLink", in_source_data, in_cost_raster, in_surface_raster, in_horizontal_raster, horizontal_factor,
        in_vertical_raster, vertical_factor, source_direction, dict(zip(_PATH_SOURCE, (
            source_cost_multiplier, source_start_cost, source_resistance_rate, source_capacity))))
    mask = _cut(total, unreached, maximum_distance)
    _save(total, mask, grid, out_distance_raster, np.float32)
    return Raster._of(np.where(mask, 0, back).astype(np.int32), mask, grid)


def PathAllocation(in_source_data, in_cost_raster=None, in_surface_raster=None, in_horizontal_raster=None,
                   horizontal_factor=None, in_vertical_raster=None, vertical_factor=None, maximum_distance=None,
                   in_value_raster=None, source_field=None, out_distance_raster=None, out_backlink_raster=None,
                   source_cost_multiplier=None, source_start_cost=None, source_resistance_rate=None,
                   source_capacity=None, source_direction=None):
    """Each cell given the value of the source it is reached from most cheaply, as PathDistance finds it: the
    source raster's integer value, the features' ``source_field``, or the value raster's value at the source."""
    from ._raster import Raster
    sa = _sa()
    if not sa._is_raster(in_source_data) and not _given(source_field):
        sa._refuse("PathAllocation", "source_field", "(empty)", "the default field of features is not built; name an "
                                                                "integer field")
    total, origin, back, unreached, values, grid = _path(
        "PathAllocation", in_source_data, in_cost_raster, in_surface_raster, in_horizontal_raster, horizontal_factor,
        in_vertical_raster, vertical_factor, source_direction, dict(zip(_PATH_SOURCE, (
            source_cost_multiplier, source_start_cost, source_resistance_rate, source_capacity))),
        None if sa._is_raster(in_source_data) else source_field)
    values = _value_raster(in_value_raster, values, grid, "PathAllocation")
    mask = _cut(total, unreached, maximum_distance)
    _save(total, mask, grid, out_distance_raster, np.float32)
    _save(back, mask, grid, out_backlink_raster, np.int32)
    flat = np.asarray(values).ravel()
    allocated = np.where(origin >= 0, flat[np.clip(origin, 0, None)], 0).astype(np.int32)
    return Raster._of(np.where(mask, 0, allocated), mask, grid)


def _value_raster(in_value_raster, values, grid, tool):
    sa = _sa()
    if not _given(in_value_raster):
        return values
    raster = _optional_raster(in_value_raster, tool)
    if not raster._grid.same(grid):
        sa._refuse(tool, "in_value_raster", in_value_raster, "a value raster on another grid needs resampling")
    return raster._array


# ---- distance accumulation: the surface reconstruction


def _thicken(barrier):
    """*Barriers are edge connected*: where two barrier cells meet only at a corner, one of the two open cells
    between them becomes a barrier too — the one above (INFERRED, ADR-241 §5.2)."""
    barrier = barrier.copy()
    rows, columns = barrier.shape
    for r in range(rows - 1):
        for k in range(columns - 1):
            a, b, c, d = barrier[r, k], barrier[r, k + 1], barrier[r + 1, k], barrier[r + 1, k + 1]
            if a and d and not b and not c:
                barrier[r, k + 1] = True
            elif b and c and not a and not d:
                barrier[r, k] = True
    return barrier


class _Field:
    """The rasters the effective slope ``C · S · M · HF · VF`` is read from, and how to read it along a direction."""

    def __init__(self, cost, surface, vertical, vf, horizontal, hf, multiplier, to_source, cell, meters):
        self.cost, self.surface, self.vertical, self.vf = cost, surface, vertical, vf
        self.horizontal, self.hf, self.multiplier = horizontal, hf, multiplier
        self.to_source, self.cell, self.meters = to_source, cell, meters

    def rate(self, r, k, azimuth, surface_slope, vertical_slope, owner):
        """The effective slope at cell (r, k) for motion on ``azimuth`` (away from the source), given the surface's
        and the vertical raster's rise per unit run along it."""
        value = self.cost[r, k] * self.multiplier[owner]
        if self.surface is not None:
            value *= math.sqrt(1.0 + surface_slope * surface_slope)
        if self.vf is not None:
            vrma = math.degrees(math.atan(-vertical_slope if self.to_source else vertical_slope))
            value *= float(self.vf.value(np.array(vrma), self.meters))
        if self.hf is not None:
            heading = (azimuth + 180.0) % 360.0 if self.to_source else azimuth
            value *= float(_horizontal(self.hf, np.array(self.horizontal[r, k]), heading))
        return value


def _reconstruct(field, sources, barrier, start, maximum):
    """The accumulated cost surface, cheapest-first (Sethian 1999, fast marching): each cell's height is the least of
    its eight network estimates — a neighbour's height plus the step times the cell's own effective slope — and its
    four Eikonal steps, the plane through two known edge neighbours whose gradient has the effective slope's
    magnitude. Where the slope depends on the direction of motion (the factors, the surface) the Eikonal step is
    solved again along its own gradient, three times. Answers the heights, the source each cell belongs to and the
    back direction (the azimuth back down the gradient; 0 on the sources)."""
    rows, columns = sources.shape
    h = field.cell
    total = np.full(sources.shape, np.inf)
    owner = np.full(sources.shape, -1, np.int64)
    back = np.zeros(sources.shape)
    done = np.zeros(sources.shape, bool)
    heap = []
    for r, k in zip(*np.nonzero(sources & ~barrier)):
        total[r, k] = start[r, k]
        owner[r, k] = r * columns + k
        heapq.heappush(heap, (total[r, k], r * columns + k, r, k))

    def rise(raster, r, k, dr, dk):
        return 0.0 if raster is None else float(raster[r, k] - raster[r + dr, k + dk])

    def estimate(r, k):
        best, best_owner, best_back = np.inf, -1, 0.0
        for dr, dk in NEIGHBOURS:
            nr, nk = r + dr, k + dk
            if not (0 <= nr < rows and 0 <= nk < columns) or not done[nr, nk]:
                continue
            run = h * (DIAGONAL if dr and dk else 1.0)
            o = owner[nr, nk]
            rate = field.rate(r, k, _azimuth(-dr, -dk), rise(field.surface, r, k, dr, dk) / run,
                              rise(field.vertical, r, k, dr, dk) / run, o)
            value = total[nr, nk] + run * rate
            if value < best and value <= maximum[o]:
                best, best_owner, best_back = value, o, _azimuth(dr, dk) or 360.0
        for dk in (-1, 1):
            for dr in (-1, 1):
                ar, ak, br, bk = r, k + dk, r + dr, k
                if not (0 <= ak < columns and 0 <= br < rows) or not (done[ar, ak] and done[br, bk]):
                    continue
                ta, tb = total[ar, ak], total[br, bk]
                o = owner[ar, ak] if ta <= tb else owner[br, bk]
                rate = field.cost[r, k] * field.multiplier[o]
                value = None
                for _ in range(3):
                    disc = 2.0 * (rate * h) ** 2 - (ta - tb) ** 2
                    if not math.isfinite(rate) or disc < 0:
                        value = None
                        break
                    value = (ta + tb + math.sqrt(disc)) / 2.0
                    east, north = -dk * (value - ta) / h, dr * (value - tb) / h
                    norm = math.hypot(east, north)
                    if norm == 0:
                        break
                    ue, un = east / norm, north / norm
                    azimuth = math.degrees(math.atan2(ue, un)) % 360.0

                    def along(raster):
                        if raster is None:
                            return 0.0
                        return (-dk * (raster[r, k] - raster[ar, ak]) * ue + dr * (raster[r, k] - raster[br, bk]) * un) / h

                    new = field.rate(r, k, azimuth, along(field.surface), along(field.vertical), o)
                    if field.surface is None and field.vf is None and field.hf is None:
                        break
                    rate = new
                if value is None or not math.isfinite(value) or value < max(ta, tb) or value > maximum[o]:
                    continue
                if value < best:
                    east, north = -dk * (value - ta) / h, dr * (value - tb) / h
                    best, best_owner = value, o
                    best_back = (math.degrees(math.atan2(-east, -north)) % 360.0) or 360.0
        return best, best_owner, best_back

    while heap:
        value, _, r, k = heapq.heappop(heap)
        if done[r, k] or value > total[r, k]:
            continue
        done[r, k] = True
        for dr, dk in NEIGHBOURS:
            nr, nk = r + dr, k + dk
            if not (0 <= nr < rows and 0 <= nk < columns) or barrier[nr, nk] or done[nr, nk]:
                continue
            best, best_owner, best_back = estimate(nr, nk)
            if best < total[nr, nk]:
                total[nr, nk], owner[nr, nk], back[nr, nk] = best, best_owner, best_back
                heapq.heappush(heap, (best, nr * columns + nk, nr, nk))
    return total, owner, back


def _straight_line(sources, maximum, cell):
    """The plain case — sources alone, or with only a maximum accumulation — answered as the straight-line distance to
    the nearest source centre, with the back direction pointing straight at it. ArcGIS Pro 3.7 does this (measured
    2026-10-31, ADR-241 §5.3: exact Euclidean on 41 × 41, where the reconstruction is up to 5.1 % long); any cost,
    surface, factor, barrier or other source characteristic, even a multiplier of 1, puts it back on the
    reconstruction. Of two equally near sources the first in scan order wins."""
    rows, columns = sources.shape
    r, k = np.indices(sources.shape)
    total = np.full(sources.shape, np.inf)
    owner = np.full(sources.shape, -1, np.int64)
    for sr, sk in zip(*np.nonzero(sources)):
        d = cell * np.hypot(r - sr, k - sk)
        o = sr * columns + sk
        nearer = (d < total) & (d <= maximum[o])
        total[nearer], owner[nearer] = d[nearer], o
    sr, sk = np.divmod(np.clip(owner, 0, None), columns)
    back = np.degrees(np.arctan2(sk - k, r - sr)) % 360.0
    back = np.where(back == 0, 360.0, back)
    back = np.where(((sr == r) & (sk == k)) | (owner < 0), 0.0, back)
    return total, owner, back


def _accumulate(tool, in_source_data, in_barrier_data, in_surface_raster, in_cost_raster, in_vertical_raster,
                vertical_factor, in_horizontal_raster, horizontal_factor, source_initial_accumulation,
                source_maximum_accumulation, source_cost_multiplier, source_direction, distance_method, field=None):
    from ._raster import _refuse_environment
    sa = _sa()
    _refuse_environment(f"arcpy.sa.{tool}")
    if str(distance_method or "PLANAR").upper() != "PLANAR":
        sa._refuse(tool, "distance_method", distance_method, "the geodesic reconstruction is not built")
    direction = str(source_direction or "FROM_SOURCE").upper()
    if direction not in ("FROM_SOURCE", "TO_SOURCE"):
        sa._refuse(tool, "source_direction", source_direction, "a field of travel directions is not built")
    cost, surface, vertical, horizontal = (_optional_raster(v, tool) for v in (
        in_cost_raster, in_surface_raster, in_vertical_raster, in_horizontal_raster))
    grid = _place([cost, surface, vertical, horizontal], in_source_data, tool, 1000, True, in_barrier_data)
    sources, values = _source_cells(in_source_data, grid, tool, field)
    barrier = np.zeros(sources.shape, bool)
    for raster in (cost, surface, vertical, horizontal):
        if raster is not None:
            barrier |= raster._mask
    if _given(in_barrier_data):
        if sa._is_raster(in_barrier_data):
            wall = _optional_raster(in_barrier_data, tool)
            if not wall._grid.same(grid):
                sa._refuse(tool, "in_barrier_data", in_barrier_data, "a barrier raster on another grid needs resampling")
            barrier |= ~wall._mask
        else:
            barrier |= ~sa._rasterize_any(in_barrier_data, grid)[1]
    barrier = _thicken(barrier)
    flat = (grid.rows * grid.columns,)
    start = _per_source(source_initial_accumulation, in_source_data, grid, tool, "source_initial_accumulation", 0)
    if (start[sources] < 0).any():
        raise ValueError(f"{tool}: source_initial_accumulation must be zero or greater.")
    maximum = _per_source(source_maximum_accumulation, in_source_data, grid, tool, "source_maximum_accumulation",
                          np.inf).reshape(flat)
    multiplier = _per_source(source_cost_multiplier, in_source_data, grid, tool, "source_cost_multiplier",
                             1).reshape(flat)
    if (maximum[sources.ravel()] <= 0).any() or (multiplier[sources.ravel()] <= 0).any():
        raise ValueError(f"{tool}: source_maximum_accumulation and source_cost_multiplier must be greater than zero.")
    c = np.ones(sources.shape) if cost is None else np.where(cost._mask, 1.0, cost._array.astype(np.float64))
    c = np.where(c > 0, c, 1e-6)  # "negative or zero … treated as small positive cost values" (INFERRED: 1e-6)
    hf = _factor(horizontal_factor, "horizontal", tool) if horizontal is not None else None
    vf = _factor(vertical_factor, "vertical", tool) if vertical is not None else None
    meters = _meters_per_unit(grid) if isinstance(vf, VfHikingTime) else 1.0

    def array(raster, fill=0.0):
        return None if raster is None else np.where(raster._mask, fill, raster._array.astype(np.float64))

    plain = not any(_given(v) for v in (in_barrier_data, in_surface_raster, in_cost_raster, in_vertical_raster,
                                        in_horizontal_raster, source_initial_accumulation, source_cost_multiplier,
                                        source_direction))
    if plain:
        total, owner, back = _straight_line(sources, maximum, grid.width)
    else:
        field_ = _Field(c, array(surface), array(vertical), vf, array(horizontal, -1.0), hf, multiplier,
                        direction == "TO_SOURCE", grid.width, meters)
        total, owner, back = _reconstruct(field_, sources, barrier, start, maximum)
    mask = ~np.isfinite(total) | barrier
    return total, owner, back, mask, values, grid


def _source_outputs(owner, mask, grid, out_source_direction_raster, out_source_location_raster):
    from ._raster import Raster
    rows, columns = owner.shape
    sr, sk = np.divmod(np.clip(owner, 0, None), columns)
    if _given(out_source_direction_raster):
        r, k = np.indices(owner.shape)
        # Whole degrees, as Pro 3.7 writes it (16-bit integers, rounded; measured 2026-10-31); north is 360, not 0.
        azimuth = np.rint(np.degrees(np.arctan2(sk - k, r - sr)) % 360.0)
        azimuth = np.where((sr == r) & (sk == k), 0.0, np.where(azimuth == 0, 360.0, azimuth))
        _save(azimuth, mask, grid, out_source_direction_raster, np.int16)
    if _given(out_source_location_raster):
        Raster._of_bands([np.where(mask, 0, sr).astype(np.int32), np.where(mask, 0, sk).astype(np.int32)],
                         [mask.copy(), mask.copy()], grid).save(out_source_location_raster)


def DistanceAccumulation(in_source_data, in_barrier_data=None, in_surface_raster=None, in_cost_raster=None,
                         in_vertical_raster=None, vertical_factor=None, in_horizontal_raster=None,
                         horizontal_factor=None, out_back_direction_raster=None, out_source_direction_raster=None,
                         out_source_location_raster=None, source_initial_accumulation=None,
                         source_maximum_accumulation=None, source_cost_multiplier=None, source_direction=None,
                         distance_method="PLANAR"):
    """The accumulated distance or cost from the cheapest source, reconstructed as a surface rather than found over a
    network of cell centres (*Distance accumulation algorithm*): straight-line distance without a cost raster, cost
    distance with one, stretched by the surface and weighted by the source's multiplier and the horizontal and
    vertical factors. Barriers and NoData on any input stay NoData. With sources alone (or with only a maximum
    accumulation) it is the exact straight-line distance, as Pro 3.7 answers it."""
    from ._raster import Raster
    total, owner, back, mask, _, grid = _accumulate(
        "DistanceAccumulation", in_source_data, in_barrier_data, in_surface_raster, in_cost_raster, in_vertical_raster,
        vertical_factor, in_horizontal_raster, horizontal_factor, source_initial_accumulation,
        source_maximum_accumulation, source_cost_multiplier, source_direction, distance_method)
    _save(back, mask, grid, out_back_direction_raster, np.float32)
    _source_outputs(owner, mask, grid, out_source_direction_raster, out_source_location_raster)
    return Raster._of(np.where(mask, 0, total).astype(np.float32), mask, grid)


def DistanceAllocation(in_source_data, in_barrier_data=None, in_surface_raster=None, in_cost_raster=None,
                       in_vertical_raster=None, vertical_factor=None, in_horizontal_raster=None, horizontal_factor=None,
                       out_distance_accumulation_raster=None, out_back_direction_raster=None,
                       out_source_direction_raster=None, out_source_location_raster=None, source_field=None,
                       source_initial_accumulation=None, source_maximum_accumulation=None,
                       source_cost_multiplier=None, source_direction=None, distance_method="PLANAR"):
    """Each cell given the value of its cheapest source as DistanceAccumulation reconstructs it: the source raster's
    value or the features' ``source_field`` (their ObjectID without one, INFERRED)."""
    from ._raster import Raster
    sa = _sa()
    total, owner, back, mask, values, grid = _accumulate(
        "DistanceAllocation", in_source_data, in_barrier_data, in_surface_raster, in_cost_raster, in_vertical_raster,
        vertical_factor, in_horizontal_raster, horizontal_factor, source_initial_accumulation,
        source_maximum_accumulation, source_cost_multiplier, source_direction, distance_method,
        None if sa._is_raster(in_source_data) else source_field)
    _save(total, mask, grid, out_distance_accumulation_raster, np.float32)
    _save(back, mask, grid, out_back_direction_raster, np.float32)
    _source_outputs(owner, mask, grid, out_source_direction_raster, out_source_location_raster)
    flat = np.asarray(values).ravel()
    allocated = np.where(owner >= 0, flat[np.clip(owner, 0, None)], 0).astype(np.int32)
    return Raster._of(np.where(mask, 0, allocated), mask, grid)


# ---- least-cost paths


def _destinations(in_destination_data, grid, tool, field):
    """The destination cells (any valid value, zero included) and their values on the distance raster's grid."""
    sa = _sa()
    if sa._is_raster(in_destination_data):
        raster = _optional_raster(in_destination_data, tool)
        if not raster._grid.same(grid):
            sa._refuse(tool, "in_destination_data", in_destination_data, "a destination raster on another grid needs "
                                                                         "resampling")
        return ~raster._mask, raster._array
    burnt, empty, _ = sa._rasterize_any(in_destination_data, grid, field if _given(field) else None)
    return ~empty, burnt


def _starts(present, values, distance, distance_mask, path_type, tool):
    """The cells paths start from, in the order the scan meets them (row by row from the upper left, INFERRED):
    every cell, each zone's cheapest cell, or the one cheapest cell."""
    kind = str(path_type).upper()
    usable = present & ~distance_mask
    cells = [(r, k) for r, k in zip(*np.nonzero(usable))]
    if kind == "EACH_CELL":
        return cells
    if kind == "BEST_SINGLE":
        return [min(cells, key=lambda c: distance[c])] if cells else []
    if kind == "EACH_ZONE":
        zones = {}
        for c in cells:
            zones.setdefault(values[c], []).append(c)
        return [min(group, key=lambda c: distance[c]) for group in zones.values()]
    _sa()._refuse(tool, "path_type", path_type)


def _direction_kind(raster, force_flow):
    if force_flow:
        return "flow"
    valid = raster._array[~raster._mask]
    top = float(valid.max()) if valid.size else 0.0
    if top <= 8:
        return "backlink"
    return "flow" if np.issubdtype(raster._array.dtype, np.integer) else "direction"


def _cell_trace(direction, mask, kind, start):
    """The cells from ``start`` to a source, a step at a time: a back-link code, a D8 flow direction, or a back
    direction taken to its nearest of the eight neighbours (no longer CostPath's: it traces a back direction over the
    lattice since the Pro 3.7 run of 2026-10-31). Ends on a source (0), off the grid, on NoData
    or where the path would revisit a cell."""
    rows, columns = direction.shape
    path, seen = [start], {start}
    r, k = start
    while True:
        code = direction[r, k]
        if code == 0:
            return path, True
        if kind == "backlink":
            step = NEIGHBOURS[int(code) - 1] if 1 <= code <= 8 else None
        elif kind == "flow":
            step = _D8.get(int(code))
        else:
            step = NEIGHBOURS[int(round(((code - 90.0) % 360.0) / 45.0)) % 8]  # east first, then clockwise
        if step is None:
            return path, False
        r, k = r + step[0], k + step[1]
        if not (0 <= r < rows and 0 <= k < columns) or mask[r, k] or (r, k) in seen:
            return path, False
        path.append((r, k))
        seen.add((r, k))


def _parse_flow_force(value):
    return value is True or str(value).upper() in ("FLOW_DIRECTION", "TRUE")


def _cost_paths(tool, in_destination_data, in_cost_distance_raster, in_cost_backlink_raster, path_type,
                destination_field, force_flow_direction_convention):
    from ._raster import _refuse_environment
    _refuse_environment(f"arcpy.sa.{tool}")
    distance = _optional_raster(in_cost_distance_raster, tool)
    link = _optional_raster(in_cost_backlink_raster, tool)
    if not distance._grid.same(link._grid):
        _sa()._refuse(tool, "in_cost_distance_raster", in_cost_distance_raster, "it must share the back-link raster's "
                                                                                "grid")
    present, values = _destinations(in_destination_data, link._grid, tool, destination_field)
    kind = _direction_kind(link, _parse_flow_force(force_flow_direction_convention))
    starts = _starts(present, values, distance._array, distance._mask | link._mask, path_type, tool)
    if kind == "direction":
        # A back direction is traced across the lattice as Optimal Path traces it, and the path is the cells that
        # hold its vertices: Pro 3.7's Cost Path over a back-direction raster marks the same cells as its Optimal
        # Path As Raster (measured 2026-10-31, ADR-241 §5.4).
        limit = 4 * (link._grid.rows + link._grid.columns) * max(link._grid.rows, link._grid.columns)
        traces = []
        for start in starts:
            points, reached = _lattice_trace(link._array, link._mask, start, limit)
            cells = list(dict.fromkeys((int(math.floor(r + 0.5)), int(math.floor(k + 0.5))) for r, k in points))
            traces.append((start, cells, reached))
    else:
        traces = [(start, *_cell_trace(link._array, link._mask, kind, start)) for start in starts]
    return traces, values, distance, link._grid


def CostPath(in_destination_data, in_cost_distance_raster, in_cost_backlink_raster, path_type="EACH_CELL",
             destination_field=None, force_flow_direction_convention=None):
    """The least-cost paths traced back over a back-link (or flow direction, or back direction) raster, as *Cost
    Path* numbers them: the source cell a path ends on 1, the first path 3, the next 4 and on in the order the scan
    meets their starts, and 2 where paths have merged."""
    from ._raster import Raster
    traces, _, _, grid = _cost_paths("CostPath", in_destination_data, in_cost_distance_raster,
                                     in_cost_backlink_raster, path_type, destination_field,
                                     force_flow_direction_convention)
    out = np.zeros((grid.rows, grid.columns), np.int32)
    for number, (_, path, _) in enumerate(traces, start=3):
        for cell in path:
            out[cell] = number if out[cell] in (0, number) else 2
    for _, path, reached in traces:
        if reached:
            out[path[-1]] = 1
    mask = out == 0
    return Raster._of(out, mask, grid)


def _centre(grid, r, k):
    return grid.left + (k + 0.5) * grid.width, grid.top - (r + 0.5) * grid.height


def _write_paths(out_polyline_features, grid, rows, fields):
    from . import _gpkg, da
    from .analysis import _out
    from ._result import Result
    from shapely.geometry import LineString
    out = _out(out_polyline_features)
    _gpkg.create(out, "Polyline", grid.srid, [_gpkg.Column(name, kind) for name, kind in fields])
    with da.InsertCursor(out, [name for name, _ in fields] + ["SHAPE@"]) as written:
        for *attributes, points in rows:
            if len(points) < 2:
                continue
            written.insertRow((*attributes, LineString(points)))
    return Result(out)


def CostPathAsPolyline(in_destination_data, in_cost_distance_raster, in_cost_backlink_raster, out_polyline_features,
                       path_type="BEST_SINGLE", destination_field=None, force_flow_direction_convention=None):
    """The least-cost paths as lines through the cell centres from each destination to its source, with ``DestID``
    the destination's value. A path is written whole even where it runs with another (INFERRED)."""
    traces, values, _, grid = _cost_paths("CostPathAsPolyline", in_destination_data, in_cost_distance_raster,
                                          in_cost_backlink_raster, path_type, destination_field,
                                          force_flow_direction_convention)
    rows = [(int(values[start]), [_centre(grid, r, k) for r, k in path]) for start, path, _ in traces]
    return _write_paths(out_polyline_features, grid, rows, [("DestID", "Integer")])


def _edge_azimuth(direction, mask, r, k, cell):
    """The azimuth at a point on the lattice: a cell centre's own, or on the line between two centres the two
    centres' azimuths interpolated linearly by distance, the short way round the circle. ArcGIS Pro 3.7 turns this way
    (measured 2026-10-31, ADR-241 §5.4: every vertex of a 17-vertex path within 0.01° of the interpolation). A source
    (0) or NoData at either end leaves the nearer cell's own."""
    own = float(direction[cell])
    fr, fk = r - math.floor(r), k - math.floor(k)
    if fr > 1e-9 and fk < 1e-9:
        a, b, t = (int(math.floor(r)), int(round(k))), (int(math.floor(r)) + 1, int(round(k))), fr
    elif fk > 1e-9 and fr < 1e-9:
        a, b, t = (int(round(r)), int(math.floor(k))), (int(round(r)), int(math.floor(k)) + 1), fk
    else:
        return own
    rows, columns = direction.shape
    if not (0 <= b[0] < rows and 0 <= b[1] < columns) or mask[a] or mask[b]:
        return own
    za, zb = float(direction[a]), float(direction[b])
    if za == 0 or zb == 0:
        return own
    turn = (zb - za + 180.0) % 360.0 - 180.0
    return ((za + t * turn) % 360.0) or 360.0


def _lattice_trace(direction, mask, start, limit):
    """A path down a back-direction raster as *Distance accumulation algorithm* draws it: from the start cell's
    centre it runs on the azimuth of the nearest cell to the lattice line it crosses next (the lines through the cell
    centres), turns there to that cell's azimuth, and ends at the centre of a source cell. Points are (row, column)
    with cell centres on whole numbers."""
    rows, columns = direction.shape
    r, k = float(start[0]), float(start[1])
    points = [(r, k)]
    cell = start
    for _ in range(limit):
        if mask[cell]:
            return points, False
        azimuth = _edge_azimuth(direction, mask, r, k, cell)
        if azimuth == 0:
            if points[-1] != (float(cell[0]), float(cell[1])):
                points.append((float(cell[0]), float(cell[1])))
            return points, True
        dr, dk = -math.cos(math.radians(azimuth)), math.sin(math.radians(azimuth))
        steps = []
        # The next line is the one beyond the nearest, not the nearest ahead: Pro 3.7 steps from k = 7.076 west to
        # k = 6, past the line at 7 (measured 2026-10-31, ADR-241 §5.4).
        if abs(dk) > 1e-12:
            target = round(k) + (1 if dk > 0 else -1)
            steps.append((target - k) / dk)
        if abs(dr) > 1e-12:
            target = round(r) + (1 if dr > 0 else -1)
            steps.append((target - r) / dr)
        t = min(steps)
        r, k = r + t * dr, k + t * dk
        if abs(r - round(r)) < 1e-9:
            r = float(round(r))
        if abs(k - round(k)) < 1e-9:
            k = float(round(k))
        cell = (int(round(r)), int(round(k)))
        if not (0 <= cell[0] < rows and 0 <= cell[1] < columns):
            return points, False
        if not mask[cell] and float(direction[cell]) == 0:
            # A step that ends nearest a source ends at its centre, not at the crossing (Pro 3.7, measured 2026-10-31).
            points.append((float(cell[0]), float(cell[1])))
            return points, True
        points.append((r, k))
    return points, False


def _optimal(tool, in_destination_data, in_distance_accumulation_raster, in_back_direction_raster,
             destination_field, path_type):
    from ._raster import _refuse_environment
    _refuse_environment(f"arcpy.sa.{tool}")
    distance = _optional_raster(in_distance_accumulation_raster, tool)
    back = _optional_raster(in_back_direction_raster, tool)
    if not distance._grid.same(back._grid):
        _sa()._refuse(tool, "in_distance_accumulation_raster", in_distance_accumulation_raster,
                      "it must share the back direction raster's grid")
    grid = back._grid
    present, values = _destinations(in_destination_data, grid, tool, destination_field)
    starts = _starts(present, values, distance._array, distance._mask | back._mask, path_type, tool)
    flow = np.issubdtype(back._array.dtype, np.integer)
    limit = 4 * (grid.rows + grid.columns) * max(grid.rows, grid.columns)
    traces = []
    for start in starts:
        if flow:
            cells, reached = _cell_trace(back._array, back._mask, "flow", start)
            points = [(float(r), float(k)) for r, k in cells]
        else:
            points, reached = _lattice_trace(back._array, back._mask, start, limit)
        traces.append((start, points, reached))
    return traces, values, distance, grid


def _cells_on(points):
    """The cells that hold a vertex of the path. ArcGIS Pro 3.7 marks these and no others (measured 2026-10-31,
    ADR-241 §5.4); a cell the line only crosses between two vertices is not on it. A vertex on a cell edge, half
    way between two centres, is in the cell below or to the right (floor of the half-shifted index)."""
    return {(int(math.floor(r + 0.5)), int(math.floor(k + 0.5))) for r, k in points}


def OptimalPathAsRaster(in_destination_data, in_distance_accumulation_raster, in_back_direction_raster,
                        destination_field=None, path_type="EACH_ZONE"):
    """The optimal paths from the destinations to their sources traced down the back direction raster (or a D8 flow
    direction raster cell by cell), each cell that holds a vertex of a path given the number of such paths, NoData
    off them."""
    from ._raster import Raster
    traces, _, _, grid = _optimal("OptimalPathAsRaster", in_destination_data, in_distance_accumulation_raster,
                                  in_back_direction_raster, destination_field, path_type)
    count = np.zeros((grid.rows, grid.columns), np.int32)
    for _, points, _ in traces:
        for r, k in _cells_on(points):
            if 0 <= r < grid.rows and 0 <= k < grid.columns:
                count[r, k] += 1
    return Raster._of(count, count == 0, grid)


def OptimalPathAsLine(in_destination_data, in_distance_accumulation_raster, in_back_direction_raster,
                      out_polyline_features, destination_field=None, path_type="EACH_ZONE",
                      create_network_paths="DESTINATIONS_TO_SOURCES"):
    """The optimal paths as lines from each destination to its source, traced as OptimalPathAsRaster traces them, with
    ``DestID`` the destination's value and ``PathCost`` the accumulated cost at the destination."""
    if create_network_paths is True or str(create_network_paths).upper() in ("NETWORK_PATHS", "TRUE"):
        _sa()._refuse("OptimalPathAsLine", "create_network_paths", create_network_paths,
                      "the page does not say where overlapping paths are cut into a network")
    traces, values, distance, grid = _optimal("OptimalPathAsLine", in_destination_data,
                                              in_distance_accumulation_raster, in_back_direction_raster,
                                              destination_field, path_type)
    rows = []
    for start, points, _ in traces:
        xy = [(grid.left + (k + 0.5) * grid.width, grid.top - (r + 0.5) * grid.height) for r, k in points]
        rows.append((int(values[start]), float(distance._array[start]), xy))
    return _write_paths(out_polyline_features, grid, rows, [("DestID", "Integer"), ("PathCost", "Double")])
