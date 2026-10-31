"""Topo to Raster (ADR-242): an equivalent of ANUDEM after Hutchinson's published descriptions.

*How Topo to Raster works* says the tool is based on ANUDEM (Hutchinson 1988, 1989, 1996, 2000, 2011) and describes it:
an iterative finite difference interpolation, *"essentially a discretized thin plate spline technique (Wahba, 1990)"*,
at successively finer resolutions, with drainage enforcement that clears spurious sinks *"via the lowest saddle point"*
subject to two tolerances, stream lines that impose descent, cliffs that break continuity, lakes, coasts, boundaries and
exclusion areas. It does not give ANUDEM's equations or code. grapy builds the published method as an equivalent
(ADR-224 §5.6) from Hutchinson, Stein, Stein and Xu (2009, MODSIM, §2.1) and Hutchinson, Xu and Stein (2011,
Geomorphometry):

- **The fit.** The grid ``f`` minimises Σ ((zᵢ − fᵢ)/wᵢ)² + λ·J(f) (2009, eq. 3), where wᵢ² is the vertical standard
  error squared plus the discretisation error (k·h·sᵢ)²/12 — h the cell size, sᵢ the cell's slope, k the
  *discretisation error factor* (2009, eq. 2) — and λ is chosen so the weighted residual sum of squares equals the
  number of data cells (2009, after eq. 3; Hutchinson 2000). J is the thin plate roughness — the squared second
  differences f_xx² + 2f_xy² + f_yy², summed — of which *profile_penalty* p is replaced by the squared profile
  curvature (the 2009 paper: *"a minimum curvature roughness penalty augmented by a locally adaptive minimum profile
  curvature penalty"*), plus *roughness_penalty* r times the squared first differences, the *"potential"* term the
  2009 paper adds for point data. Differences are taken in grid units, so J is in the data's own units.
- **Resolutions.** A coarse grid first, then grids each half the cell size of the last until the output's (2011:
  *"a simple multi-grid method … successively finer resolutions"*); the slopes sᵢ come from the previous grid.
- **Data.** Points are averaged per cell, the first 100 in each (the tool page); contours are sampled every quarter
  cell and the first 50 samples in a cell averaged (*How Topo to Raster works*); data inside exclusion and lake
  polygons, or outside the z limits, are not used.
- **Drainage enforcement** (2011: *"find for each sink point the lowest adjacent saddle point that leads to a lower
  data point, sink or edge … enforces a descending chain condition from the sink via the intervening saddle"*): every
  cell lower than its eight neighbours, other than a known sink, is a sink; a priority flood from it (the minimax path
  of Barnes, Lehman and Mulla 2014, Computers & Geosciences 62) finds the lowest saddle leading to a lower cell, an edge,
  a known sink or a lake. The chain is pinned: up to the saddle at the saddle's height, falling by a negligible step,
  and beyond it never rising. A sink is left when the saddle is more than **tolerance_2** above it (*"unrealistically
  high barriers"*) or when the chain would move an elevation data cell by more than **tolerance_1** (*"it does not
  clear spurious sinks that would contradict input elevation data by more than the value of Tolerance 1"*). Each
  iteration solves again and looks for sinks again, up to *maximum_iterations* times per resolution.
- **Streams** are chains of cells pinned to descend downstream, overriding elevation data (*"Stream data always takes
  priority"*). **Cliffs** cut every difference that crosses them, so the surface is free to break there, and a data
  point on the far side of a cliff from its cell's centre is dropped. **Lakes** take the lowest value of the cells
  round their shore (the tool page). **Coasts** set the cells outside them below the minimum z limit; a **boundary**
  makes the cells outside it NoData. The **margin** interpolates that many cells beyond the extent and boundary.

The equivalent does not infer stream and ridge lines from contour corners (Hutchinson 1988), so the stream output never
carries codes 4 or 5; and it never meets the conditions of the error codes it does not detect (see ``TopoToRaster``).
"""
from __future__ import annotations

import heapq
import math
import os
import shlex

import numpy as np

from ._raster import Raster, _Grid
from ._runtime import AddWarning, ExecuteError

METHOD = ("ANUDEM's locally adaptive thin plate fit at successively finer resolutions with drainage enforcement by "
          "descending chains through the lowest saddle",
          "Hutchinson 1989, Journal of Hydrology 106; Hutchinson, Stein, Stein and Xu 2009, MODSIM §2.1; Hutchinson, "
          "Xu and Stein 2011, Geomorphometry; Barnes, Lehman and Mulla 2014, Computers & Geosciences 62")

# ---- the input classes


class _TopoInput:
    """One of the nine Topo classes: ``inFeatures``, as its page names the property."""

    kind = ""
    has_field = False

    def __init__(self, inFeatures):
        self.inFeatures = inFeatures

    def __repr__(self):
        return f"{type(self).__name__}({self.inFeatures!r})"

    def _entries(self):
        """(path, field) pairs: a field class takes ``[[path, field], …]`` or ``[path, …]``, the others paths."""
        value = self.inFeatures
        if isinstance(value, (str, os.PathLike)):
            value = [value]
        out = []
        for item in value:
            if isinstance(item, (list, tuple)):
                path = item[0]
                field = item[1] if len(item) > 1 and self.has_field else None
            else:
                path, field = item, None
            if field in ("", "#") or (isinstance(field, str) and field.upper() == "NONE"):
                field = None
            out.append((os.fspath(path), field))
        return out


def _topo_class(name, kind, has_field, doc):
    cls = type(name, (_TopoInput,), {"kind": kind, "has_field": has_field, "__doc__": doc})
    return cls


TopoPointElevation = _topo_class("TopoPointElevation", "POINTELEVATION", True,
                                 "arcpy.sa.TopoPointElevation: point feature classes of surface elevations and the "
                                 "fields holding them, ``[[inFeatures, {field}], …]``.")
TopoContour = _topo_class("TopoContour", "CONTOUR", True,
                          "arcpy.sa.TopoContour: line feature classes of contours and the fields holding their "
                          "elevations, ``[[inFeatures, {field}], …]``.")
TopoStream = _topo_class("TopoStream", "STREAM", False,
                         "arcpy.sa.TopoStream: line feature classes of streams, every line pointing downstream.")
TopoSink = _topo_class("TopoSink", "SINK", True,
                       "arcpy.sa.TopoSink: point feature classes of known depressions and, optionally, the field of "
                       "their elevations; NONE uses only their locations.")
TopoBoundary = _topo_class("TopoBoundary", "BOUNDARY", False,
                           "arcpy.sa.TopoBoundary: polygon feature classes of the output's outer boundary.")
TopoLake = _topo_class("TopoLake", "LAKE", False, "arcpy.sa.TopoLake: polygon feature classes of lakes.")
TopoCliff = _topo_class("TopoCliff", "CLIFF", False,
                        "arcpy.sa.TopoCliff: line feature classes of cliffs, low side on the left.")
TopoExclusion = _topo_class("TopoExclusion", "EXCLUSION", False,
                            "arcpy.sa.TopoExclusion: polygon feature classes of areas whose data is ignored.")
TopoCoast = _topo_class("TopoCoast", "COAST", False,
                        "arcpy.sa.TopoCoast: polygon feature classes outlining the coastal area.")

_CLASSES = {c.kind: c for c in (TopoPointElevation, TopoContour, TopoStream, TopoSink, TopoBoundary, TopoLake,
                                TopoCliff, TopoExclusion, TopoCoast)}
_KEYWORDS = {"POINTELEVATION": "POINTELEVATION", "POINT": "POINTELEVATION", "CONTOUR": "CONTOUR", "STREAM": "STREAM",
             "SINK": "SINK", "BOUNDARY": "BOUNDARY", "LAKE": "LAKE", "CLIFF": "CLIFF", "EXCLUSION": "EXCLUSION",
             "COAST": "COAST"}


def _inputs(in_topo_features):
    """The inputs as (kind, path, field): Topo objects (arcpy.sa), or the value table ``"path field Type; …"`` and its
    list form (arcpy.ddd)."""
    from .management import _list
    items = in_topo_features if isinstance(in_topo_features, (list, tuple)) else _list(in_topo_features)
    if isinstance(in_topo_features, _TopoInput):
        items = [in_topo_features]
    out = []
    for item in items:
        if isinstance(item, _TopoInput):
            out += [(item.kind, p, f) for p, f in item._entries()]
            continue
        parts = list(item) if isinstance(item, (list, tuple)) else shlex.split(str(item), posix=False)
        parts = [str(p).strip('"\'') for p in parts]
        if len(parts) < 2:
            raise ValueError(f"TopoToRaster: '{item}' names no input type; write 'path field Type'.")
        kind = _KEYWORDS.get(parts[-1].upper())
        if kind is None:
            raise ValueError(f"TopoToRaster: '{parts[-1]}' is not one of the nine input types.")
        field = parts[1] if len(parts) > 2 else None
        if field in ("#", "") or (field or "").upper() == "NONE" or not _CLASSES[kind].has_field:
            field = None
        out.append((kind, parts[0], field))
    if not out:
        raise ValueError("TopoToRaster needs input feature data.")
    for kind, _, field in out:
        if kind in ("POINTELEVATION", "CONTOUR") and not field:
            raise ValueError(f"TopoToRaster: a {kind.title()} input needs the field that holds its elevations.")
    return out


# ---- reading


def _features(path, field):
    from . import da
    names = ([field] if field else []) + ["SHAPE@"]
    out = []
    with da.SearchCursor(path, names) as rows:
        for row in rows:
            shape = row[-1]
            if shape is None:
                continue
            value = row[0] if field else None
            if field and value is None:
                continue  # a Null value is ignored, as documented
            out.append((None if value is None else float(value), shape._shape))
    return out


def _srid(path):
    from .analysis import _schema
    return int(_schema(path)[2] or 0)


def _extent_of(paths):
    import shapely
    boxes = []
    for path in paths:
        shapes = [s for _, s in _features(path, None)]
        if shapes:
            boxes.append(shapely.GeometryCollection(shapes).bounds)
    return boxes


# ---- the grid


def _parse_extent(extent, boxes, tool):
    from ._reference import Extent
    if isinstance(extent, Extent):
        return float(extent.XMin), float(extent.YMin), float(extent.XMax), float(extent.YMax)
    text = str(extent).strip().upper() if extent not in (None, "", "#") else "MAXOF"
    if text in ("MAXOF", "DEFAULT"):
        return (min(b[0] for b in boxes), min(b[1] for b in boxes), max(b[2] for b in boxes), max(b[3] for b in boxes))
    if text == "MINOF":
        box = (max(b[0] for b in boxes), max(b[1] for b in boxes), min(b[2] for b in boxes), min(b[3] for b in boxes))
        if box[0] >= box[2] or box[1] >= box[3]:
            raise ValueError(f"{tool}: the inputs have no extent in common (MINOF).")
        return box
    if text == "DISPLAY":
        raise ValueError(f"{tool}: DISPLAY needs a map, which a user tool on the server does not have.")
    parts = str(extent).split()
    try:
        return tuple(float(p) for p in parts[:4])
    except ValueError:
        raise ValueError(f"{tool}: '{extent}' is not an extent grapy reads (x-min y-min x-max y-max, MAXOF, MINOF or "
                         "an Extent).") from None


class _Plan:
    """Everything one run needs, from either tool."""

    def __init__(self):
        self.inputs = []
        self.cell = None
        self.extent = None
        self.margin = 20
        self.zmin = self.zmax = None
        self.enforce = "ENFORCE"
        self.data_type = "CONTOUR"
        self.iterations = 20
        self.roughness = None
        self.discrete = 1.0
        self.vse = 0.0
        self.tol1 = self.tol2 = None
        self.profile = 0.0
        self.diagnostic = None
        self.parameter_file = None


def _num(value, default, cast=float):
    return default if value in (None, "", "#") else cast(value)


def _validate(plan, tool):
    if plan.margin < 0:
        raise ValueError(f"{tool}: the margin must be 0 or greater.")
    if plan.iterations <= 0:
        raise ValueError(f"{tool}: the number of iterations must be greater than zero.")
    if plan.enforce not in ("ENFORCE", "NO_ENFORCE", "ENFORCE_WITH_SINK"):
        raise ValueError(f"{tool}: enforce is ENFORCE, NO_ENFORCE or ENFORCE_WITH_SINK.")
    if plan.data_type not in ("CONTOUR", "SPOT"):
        raise ValueError(f"{tool}: data_type is CONTOUR or SPOT.")
    contour = plan.data_type == "CONTOUR"
    plan.roughness = (0.0 if contour else 0.5) if plan.roughness is None else plan.roughness
    plan.tol1 = (2.5 if contour else 0.0) if plan.tol1 is None else plan.tol1
    plan.tol2 = (100.0 if contour else 200.0) if plan.tol2 is None else plan.tol2
    for name, value, low, strict in (("roughness_penalty", plan.roughness, 0, False),
                                     ("discrete_error_factor", plan.discrete, 0, True),
                                     ("vertical_standard_error", plan.vse, 0, False),
                                     ("tolerance_1", plan.tol1, 0, False), ("tolerance_2", plan.tol2, 0, True),
                                     ("profile_penalty", plan.profile, 0, False)):
        if value < low or (strict and value == low):
            raise ValueError(f"{tool}: {name} must be {'greater than' if strict else 'zero or greater than'} {low}.")
    if plan.profile >= 1:
        raise ValueError(f"{tool}: profile_penalty must be below 1; the page recommends no more than 0.8.")
    if plan.profile > 0.8:
        AddWarning(f"{tool}: a profile_penalty above 0.8 is not recommended and should not be used, as the page says.")


# ---- the finite difference operators


def _operators(rows, columns, cut_h, cut_v):
    """Sparse difference operators over the grid, each row dropped where it would cross a cliff: first differences
    across and down, second differences across and down, and the mixed difference of each 2 × 2 block."""
    from scipy.sparse import coo_matrix
    index = np.arange(rows * columns).reshape(rows, columns)
    n = rows * columns

    def build(stencil, where):
        r, c = np.nonzero(where)
        data, ri, ci = [], [], []
        for k, (dr, dc, w) in enumerate(stencil):
            ri.append(np.arange(r.size))
            ci.append(index[r + dr, c + dc])
            data.append(np.full(r.size, float(w)))
        return coo_matrix((np.concatenate(data), (np.concatenate(ri), np.concatenate(ci))), shape=(r.size, n)).tocsr()

    full = np.ones((rows, columns), bool)
    dx_where = full.copy()
    dx_where[:, -1] = False
    dx_where[:, :-1] &= ~cut_h
    dy_where = full.copy()
    dy_where[-1, :] = False
    dy_where[:-1, :] &= ~cut_v
    dxx_where = np.zeros((rows, columns), bool)
    if columns >= 3:
        dxx_where[:, 1:-1] = ~cut_h[:, :-1] & ~cut_h[:, 1:]
    dyy_where = np.zeros((rows, columns), bool)
    if rows >= 3:
        dyy_where[1:-1, :] = ~cut_v[:-1, :] & ~cut_v[1:, :]
    dxy_where = np.zeros((rows, columns), bool)
    if rows >= 2 and columns >= 2:
        dxy_where[:-1, :-1] = ~cut_h[:-1, :] & ~cut_h[1:, :] & ~cut_v[:, :-1] & ~cut_v[:, 1:]
    return {
        "dx": build([(0, 0, -1), (0, 1, 1)], dx_where),
        "dy": build([(0, 0, -1), (1, 0, 1)], dy_where),
        "dxx": build([(0, -1, 1), (0, 0, -2), (0, 1, 1)], dxx_where),
        "dyy": build([(-1, 0, 1), (0, 0, -2), (1, 0, 1)], dyy_where),
        "dxy": build([(0, 0, 1), (0, 1, -1), (1, 0, -1), (1, 1, 1)], dxy_where),
        "index": index,
    }


def _profile_operator(f, rows, columns, cut_h, cut_v):
    """The profile curvature — the second derivative along the gradient — at each interior cell whose 3 × 3 block no
    cliff crosses, its direction taken from the current surface ``f`` (INFERRED: where the surface is flat, the
    diagonal)."""
    from scipy.sparse import coo_matrix
    index = np.arange(rows * columns).reshape(rows, columns)
    if rows < 3 or columns < 3:
        return None
    gr, gc = np.gradient(f)
    norm = np.hypot(gr, gc)
    u = np.where(norm > 0, gc / np.where(norm > 0, norm, 1), math.sqrt(0.5))
    v = np.where(norm > 0, gr / np.where(norm > 0, norm, 1), math.sqrt(0.5))
    ok = np.zeros((rows, columns), bool)
    inner = np.ones((rows - 2, columns - 2), bool)
    for dr in (-1, 0, 1):
        for dc in (-1, 0):
            inner &= ~cut_h[1 + dr:rows - 1 + dr, 1 + dc:columns - 1 + dc]
    for dr in (-1, 0):
        for dc in (-1, 0, 1):
            inner &= ~cut_v[1 + dr:rows - 1 + dr, 1 + dc:columns - 1 + dc]
    ok[1:-1, 1:-1] = inner
    r, c = np.nonzero(ok)
    uu, vv, uv = u[r, c] ** 2, v[r, c] ** 2, u[r, c] * v[r, c]
    stencil = [(0, -1, uu), (0, 0, -2 * uu), (0, 1, uu), (-1, 0, vv), (0, 0, -2 * vv), (1, 0, vv),
               (1, 1, uv / 2), (1, -1, -uv / 2), (-1, 1, -uv / 2), (-1, -1, uv / 2)]
    data = np.concatenate([np.broadcast_to(w, r.shape) for _, _, w in stencil])
    ri = np.concatenate([np.arange(r.size)] * len(stencil))
    ci = np.concatenate([index[r + dr, c + dc] for dr, dc, _ in stencil])
    return coo_matrix((data, (ri, ci)), shape=(r.size, rows * columns)).tocsr()


# ---- one resolution


class _Level:
    """One grid of the multi-resolution fit."""

    def __init__(self, left, top, cell, rows, columns):
        self.left, self.top, self.cell, self.rows, self.columns = left, top, cell, rows, columns

    def centres(self):
        xs = self.left + (np.arange(self.columns) + 0.5) * self.cell
        ys = self.top - (np.arange(self.rows) + 0.5) * self.cell
        return xs, ys

    def cell_of(self, x, y):
        c = np.floor((np.asarray(x) - self.left) / self.cell).astype(int)
        r = np.floor((self.top - np.asarray(y)) / self.cell).astype(int)
        inside = (r >= 0) & (r < self.rows) & (c >= 0) & (c < self.columns)
        return r, c, inside


def _cuts(level, cliffs):
    """The cell-centre links each cliff line crosses: across (rows × columns−1) and down (rows−1 × columns)."""
    import shapely
    cut_h = np.zeros((level.rows, max(level.columns - 1, 0)), bool)
    cut_v = np.zeros((max(level.rows - 1, 0), level.columns), bool)
    if not cliffs:
        return cut_h, cut_v
    xs, ys = level.centres()
    for line in cliffs:
        x0, y0, x1, y1 = line.bounds
        c_lo, c_hi = max(int((x0 - level.left) / level.cell) - 1, 0), min(int((x1 - level.left) / level.cell) + 2,
                                                                          level.columns)
        r_lo, r_hi = max(int((level.top - y1) / level.cell) - 1, 0), min(int((level.top - y0) / level.cell) + 2,
                                                                         level.rows)
        for r in range(r_lo, r_hi):
            for c in range(c_lo, c_hi):
                if c + 1 < level.columns and not cut_h[r, c]:
                    cut_h[r, c] = shapely.intersects(line, shapely.LineString([(xs[c], ys[r]), (xs[c + 1], ys[r])]))
                if r + 1 < level.rows and not cut_v[r, c]:
                    cut_v[r, c] = shapely.intersects(line, shapely.LineString([(xs[c], ys[r]), (xs[c], ys[r + 1])]))
    return cut_h, cut_v


def _allocate(level, samples, cliffs_union):
    """The data cells: each cell's average of the first 100 point samples and the first 50 contour samples in it;
    a sample on the far side of a cliff from its cell's centre is dropped."""
    import shapely
    sums, counts = {}, {}
    taken = {"point": {}, "contour": {}}
    limit = {"point": 100, "contour": 50}
    xs_c, ys_c = level.centres()
    for kind, x, y, z, feature in samples:
        r, c, inside = level.cell_of(x, y)
        if not inside:
            continue
        key = (int(r), int(c))
        if taken[kind].get(key, 0) >= limit[kind]:
            continue
        if cliffs_union is not None and shapely.intersects(cliffs_union, shapely.LineString([(x, y), (xs_c[key[1]],
                                                                                                  ys_c[key[0]])])):
            if (x, y) != (xs_c[key[1]], ys_c[key[0]]):
                continue
        taken[kind][key] = taken[kind].get(key, 0) + 1
        sums[key] = sums.get(key, 0.0) + z
        counts[key] = counts.get(key, 0) + 1
    cells = {k: sums[k] / counts[k] for k in sums}
    return cells


def _chain_cells(level, line):
    """A line's cells in its own direction, each 8-adjacent to the next."""
    length = line.length
    steps = max(int(math.ceil(length / (level.cell / 4))), 1)
    out = []
    for k in range(steps + 1):
        p = line.interpolate(length * k / steps)
        r, c, inside = level.cell_of(p.x, p.y)
        if not inside:
            continue
        cell = (int(r), int(c))
        if out and cell == out[-1]:
            continue
        if out:
            pr, pc = out[-1]
            while max(abs(cell[0] - pr), abs(cell[1] - pc)) > 1:
                pr += int(np.sign(cell[0] - pr))
                pc += int(np.sign(cell[1] - pc))
                out.append((pr, pc))
        out.append(cell)
    return out


def _solve(ops, profile_op, plan, n, data_index, data_value, data_weight, pins, lam):
    from scipy.sparse import diags
    from scipy.sparse.linalg import spsolve
    thin = ops["dxx"].T @ ops["dxx"] + 2 * (ops["dxy"].T @ ops["dxy"]) + ops["dyy"].T @ ops["dyy"]
    penalty = (1 - plan.profile) * thin if profile_op is not None else thin
    if profile_op is not None and plan.profile > 0:
        penalty = penalty + plan.profile * (profile_op.T @ profile_op)
    if plan.roughness > 0:
        penalty = penalty + plan.roughness * (ops["dx"].T @ ops["dx"] + ops["dy"].T @ ops["dy"])
    weight = np.zeros(n)
    right = np.zeros(n)
    weight[data_index] += data_weight
    right[data_index] += data_weight * data_value
    if pins:
        big = 1e6 * max(float(data_weight.max()) if data_weight.size else 1.0, 1.0)
        keys = np.fromiter(pins.keys(), int, len(pins))
        values = np.fromiter(pins.values(), float, len(pins))
        weight[keys] = big
        right[keys] = big * values
    system = (diags(weight) + lam * penalty + diags(np.full(n, 1e-10))).tocsc()
    return spsolve(system, right)


def _choose_lambda(ops, profile_op, plan, n, di, dv, dw, pins):
    """λ such that the weighted residual sum of squares equals the number of data cells (Hutchinson 2000), by
    bisection on its logarithm."""
    target = float(len(di))
    low, high = -8.0, 8.0
    f = None
    for _ in range(24):
        middle = (low + high) / 2
        f = _solve(ops, profile_op, plan, n, di, dv, dw, pins, 10.0 ** middle)
        rss = float((dw * (dv - f[di]) ** 2).sum())
        if rss > target:
            high = middle
        else:
            low = middle
    return 10.0 ** ((low + high) / 2)


def _pits(f, exclude):
    """Cells lower than all eight neighbours, the grid's edge and ``exclude`` left out."""
    rows, columns = f.shape
    pit = np.zeros(f.shape, bool)
    if rows < 3 or columns < 3:
        return pit
    centre = f[1:-1, 1:-1]
    lower = np.ones(centre.shape, bool)
    for dr in (-1, 0, 1):
        for dc in (-1, 0, 1):
            if dr or dc:
                lower &= centre < f[1 + dr:rows - 1 + dr, 1 + dc:columns - 1 + dc]
    pit[1:-1, 1:-1] = lower
    return pit & ~exclude


_EIGHT = [(-1, -1), (-1, 0), (-1, 1), (0, -1), (0, 1), (1, -1), (1, 0), (1, 1)]


def _outlet_path(f, start, outlets):
    """The minimax path from a sink to the first cell lower than it, an edge cell or an outlet cell — a priority flood
    (Barnes et al. 2014) whose priority is the highest cell on the way — and the saddle's height."""
    rows, columns = f.shape
    sr, sc = start
    base = f[sr, sc]
    seen = {start: None}
    heap = [(base, sr, sc)]
    while heap:
        level, r, c = heapq.heappop(heap)
        if (r, c) != start and (f[r, c] < base or r in (0, rows - 1) or c in (0, columns - 1) or outlets[r, c]):
            path, at = [], (r, c)
            while at is not None:
                path.append(at)
                at = seen[at]
            return path[::-1], level
        for dr, dc in _EIGHT:
            nr, nc = r + dr, c + dc
            if 0 <= nr < rows and 0 <= nc < columns and (nr, nc) not in seen:
                seen[(nr, nc)] = (r, c)
                heapq.heappush(heap, (max(level, f[nr, nc]), nr, nc))
    return None, None


def _descend(values, step):
    """A chain made to descend: each value no higher than the last, less ``step``."""
    out = [values[0]]
    for v in values[1:]:
        out.append(min(v, out[-1] - step))
    return out


def _fit_level(level, plan, samples, streams, cliffs, cliffs_union, lakes_mask_of, sinks_known, previous, zscale,
               zmid, diag, lam_previous):
    """One resolution: the data allocated, the fit, the enforcement iterations. Answers the grid, the data, the
    pinned chains, the remaining sinks and the iteration log."""
    rows, columns = level.rows, level.columns
    n = rows * columns
    cut_h, cut_v = _cuts(level, cliffs)
    ops = _operators(rows, columns, cut_h, cut_v)
    data = _allocate(level, samples, cliffs_union)
    lake = lakes_mask_of(level)
    known = np.zeros((rows, columns), bool)
    if plan.enforce == "ENFORCE_WITH_SINK":
        for x, y in sinks_known:
            r, c, inside = level.cell_of(x, y)
            if inside:
                known[int(r), int(c)] = True
    keys = sorted(data)
    di = np.array([ops["index"][r, c] for r, c in keys], int)
    dv = (np.array([data[k] for k in keys]) - zmid) / zscale
    if previous is None:
        slope = np.full((rows, columns), 1.0 / max(diag, 1e-12) * zscale)
    else:
        slope = previous
    s = np.array([slope[r, c] for r, c in keys]) if keys else np.zeros(0)
    w = np.sqrt(plan.vse ** 2 + (plan.discrete * level.cell * s) ** 2 / 12.0) / zscale
    w = np.maximum(w, 1e-4)
    dw = 1.0 / w ** 2
    data_cells = np.zeros((rows, columns), bool)
    for r, c in keys:
        data_cells[r, c] = True
    zvalue = np.full((rows, columns), np.nan)
    for (r, c), v in data.items():
        zvalue[r, c] = v
    pins = {}
    stream_lines = []
    step = 1e-6
    # Streams descend downstream, overriding data; their chains are set again after every solve.
    chains = [(_chain_cells(level, line), line) for line in streams]
    profile_op = None
    if n > 40000 and lam_previous is not None:
        lam = lam_previous * 4.0
    else:
        lam = _choose_lambda(ops, None, plan, n, di, dv, dw, pins) if len(di) else 1.0
    f = _solve(ops, None, plan, n, di, dv, dw, pins, lam).reshape(rows, columns)
    log, cleared_paths, remaining = [], {}, []
    def impose_streams(surface, into):
        """Every stream chain made to descend on the surface, and pinned."""
        for cells, _ in chains:
            if len(cells) < 2:
                continue
            values = _descend([surface[r, c] for r, c in cells], step)
            for (r, c), t in zip(cells, values):
                surface[r, c] = t
                into[int(ops["index"][r, c])] = t

    def clear(surface, into):
        """One pass over the surface's sinks, lowest first: each cleared through its saddle in place, and its chain
        pinned. Answers (sinks found, sinks cleared)."""
        outlets = known | lake
        order = sorted(zip(*np.nonzero(_pits(surface, outlets))), key=lambda rc: surface[rc])
        done = 0
        for r, c in order:
            if not all(surface[r, c] < surface[r + dr, c + dc] for dr, dc in _EIGHT):
                continue  # an earlier chain has already drained it
            path, saddle = _outlet_path(surface, (int(r), int(c)), outlets)
            if path is None or (saddle - surface[r, c]) * zscale > plan.tol2:
                continue
            top = int(np.argmax([surface[a, b] for a, b in path]))
            targets = [saddle + (top - i) * step for i in range(top + 1)]
            targets += _descend([targets[-1]] + [surface[a, b] for a, b in path[top + 1:]], step)[1:]
            if any(data_cells[a, b] and abs(t - (zvalue[a, b] - zmid) / zscale) * zscale > plan.tol1
                   for (a, b), t in zip(path, targets)):
                continue
            # The chain up to the saddle, and beyond it only the cells it has to lower.
            for i, ((a, b), t) in enumerate(zip(path[:-1], targets[:-1])):
                if i <= top or t < surface[a, b]:
                    surface[a, b] = t
                    into[int(ops["index"][a, b])] = t
            done += 1
            cleared_paths[(int(r), int(c))] = path
        return len(order), done

    for iteration in range(1, plan.iterations + 1):
        if plan.profile > 0:
            profile_op = _profile_operator(f, rows, columns, cut_h, cut_v)
        new_pins = {}
        impose_streams(f, new_pins)
        found = cleared = 0
        if plan.enforce != "NO_ENFORCE":
            # Clearing in place until no sink yields, so the surface this iteration leaves has only the sinks the
            # tolerances keep; the next solve then fits the data round the pinned chains.
            for _ in range(rows * columns):
                seen, done = clear(f, new_pins)
                found, cleared = (found or seen), cleared + done
                if not done:
                    break
        log.append((iteration, found, cleared, int(_pits(f, known | lake).sum())))
        changed = any(abs(pins.get(k, np.inf) - v) > 1e-9 for k, v in new_pins.items())
        pins.update(new_pins)
        if not changed or iteration == plan.iterations:
            break
        f = _solve(ops, profile_op, plan, n, di, dv, dw, pins, lam).reshape(rows, columns)
    # The sinks left are those of the surface as finally made: the ones the tolerances keep, or every one when
    # drainage is not enforced.
    remaining = [(int(r), int(c)) for r, c in zip(*np.nonzero(_pits(f, known | lake)))]
    for cells, line in chains:
        stream_lines.append((cells, line))
    return {"f": f, "cut_h": cut_h, "cut_v": cut_v, "data": data, "w": w, "keys": keys, "lam": lam, "log": log,
            "cleared": list(cleared_paths.values()), "remaining": remaining, "streams": stream_lines, "lake": lake}


def _slopes(f, cell, zscale):
    """Each cell's slope, rise over run in the data's units."""
    gr, gc = np.gradient(f * zscale, cell)
    return np.hypot(gr, gc)


# ---- the run


def run(plan, out_stream_features=None, out_sink_features=None, out_residual_feature=None,
        out_stream_cliff_error_feature=None, out_contour_error_feature=None, tool="TopoToRaster"):
    import shapely
    from scipy.ndimage import zoom
    from ._raster import _refuse_environment
    _refuse_environment(f"arcpy.sa.{tool}")
    _validate(plan, tool)
    inputs = plan.inputs
    srids = {_srid(p) for _, p, _ in inputs}
    srid = sorted(srids)[-1] if srids else 0
    if len(srids - {0}) > 1:
        raise ValueError(f"{tool}: the inputs are in different coordinate systems; project them to one first.")
    by_kind = {}
    for kind, path, field in inputs:
        by_kind.setdefault(kind, []).append((path, field, _features(path, field)))
    boxes = []
    for kind, entries in by_kind.items():
        for path, field, feats in entries:
            if feats:
                boxes.append(shapely.GeometryCollection([s for _, s in feats]).bounds)
    if not boxes:
        raise ValueError(f"{tool}: the inputs hold no features.")
    xmin, ymin, xmax, ymax = _parse_extent(plan.extent, boxes, tool)
    plan.box = (xmin, ymin, xmax, ymax)
    if plan.cell is None:
        shorter = min(xmax - xmin, ymax - ymin)
        if shorter <= 0:
            raise ValueError(f"{tool}: the extent has no width or height to divide by 250.")
        plan.cell = shorter / 250.0
    cell = float(plan.cell)
    out_columns = max(1, int(math.ceil((xmax - xmin) / cell - 1e-9)))
    out_rows = max(1, int(math.ceil((ymax - ymin) / cell - 1e-9)))
    margin = int(plan.margin)
    left, top = xmin - margin * cell, ymin + out_rows * cell + margin * cell
    rows, columns = out_rows + 2 * margin, out_columns + 2 * margin

    def polygons(kind):
        shapes = [s for _, _, feats in by_kind.get(kind, []) for _, s in feats]
        return shapely.union_all(shapes) if shapes else None

    exclusion, lakes, coast, boundary = polygons("EXCLUSION"), polygons("LAKE"), polygons("COAST"), polygons("BOUNDARY")
    cliffs = [s for _, _, feats in by_kind.get("CLIFF", []) for _, s in feats]
    cliff_parts = []
    for s in cliffs:
        cliff_parts += list(getattr(s, "geoms", [s]))
    cliffs_union = shapely.union_all(cliff_parts) if cliff_parts else None
    streams = []
    for _, _, feats in by_kind.get("STREAM", []):
        for _, s in feats:
            streams += list(getattr(s, "geoms", [s]))
    # The data: points and contours (and sinks with an elevation), minus exclusion areas, lakes and the z limits.
    raw = []
    contour_features = []
    point_count = 0
    for kind in ("POINTELEVATION", "SINK", "CONTOUR"):
        for path, field, feats in by_kind.get(kind, []):
            for k, (z, shape) in enumerate(feats):
                if z is None:
                    continue
                if kind == "CONTOUR":
                    contour_features.append((z, shape))
                    for part in getattr(shape, "geoms", [shape]):
                        length = part.length
                        steps = max(int(math.ceil(length / (cell / 4))), 1)
                        for i in range(steps + 1):
                            p = part.interpolate(length * i / steps)
                            raw.append(("contour", p.x, p.y, z, len(contour_features) - 1))
                else:
                    for p in getattr(shape, "geoms", [shape]):
                        raw.append(("point", p.x, p.y, z, None))
                        point_count += kind == "POINTELEVATION"
    values = [z for _, _, _, z, _ in raw]
    if not values:
        raise ValueError(f"{tool}: the inputs hold no elevation data (point elevations, contours or sinks with a "
                         "field).")
    low, high = min(values), max(values)
    zmin = plan.zmin if plan.zmin is not None else low - 0.2 * abs(low)
    zmax = plan.zmax if plan.zmax is not None else high + 0.2 * abs(high)
    plan.zmin, plan.zmax = zmin, zmax

    def kept(x, y, z):
        if not zmin <= z <= zmax:
            return False
        if exclusion is not None and shapely.contains_xy(exclusion, x, y):
            return False
        if lakes is not None and shapely.contains_xy(lakes, x, y):
            return False
        return True

    samples = [s for s in raw if kept(s[1], s[2], s[3])]
    sinks_known = [(p.x, p.y) for _, _, feats in by_kind.get("SINK", []) for _, s in feats
                   for p in getattr(s, "geoms", [s])]
    zmid = (low + high) / 2.0
    zscale = max(high - low, 1e-9)
    diag = math.hypot(columns * cell, rows * cell)

    def lakes_mask_of(level):
        if lakes is None:
            return np.zeros((level.rows, level.columns), bool)
        xs, ys = level.centres()
        gx, gy = np.meshgrid(xs, ys)
        return shapely.contains_xy(lakes, gx, gy)

    # Resolutions: the cell doubled until the grid's shorter side is under 8 cells, at most five times.
    levels = 0
    while levels < 5 and min(rows, columns) / (2 ** (levels + 1)) >= 8:
        levels += 1
    diagnostics = []
    previous, lam, result = None, None, None
    for k in range(levels, -1, -1):
        factor = 2 ** k
        level = _Level(left, top, cell * factor, int(math.ceil(rows / factor)), int(math.ceil(columns / factor)))
        result = _fit_level(level, plan, samples, streams, cliff_parts, cliffs_union, lakes_mask_of, sinks_known,
                            previous, zscale, zmid, diag, lam)
        lam = result["lam"]
        diagnostics.append((level, result["log"], len(result["data"])))
        if k:
            slope = _slopes(result["f"], level.cell, zscale)
            nr, nc = int(math.ceil(rows / (factor // 2))), int(math.ceil(columns / (factor // 2)))
            previous = zoom(slope, (nr / slope.shape[0], nc / slope.shape[1]), order=1)[:nr, :nc]
    f = result["f"] * zscale + zmid
    # Lakes take the lowest value of the cells round their shore (the tool page).
    lake = result["lake"]
    if lake.any():
        from scipy.ndimage import binary_dilation, label
        pieces, count = label(lake)
        for p in range(1, count + 1):
            inside = pieces == p
            shore = binary_dilation(inside, structure=np.ones((3, 3), bool)) & ~lake
            if shore.any():
                f[inside] = f[shore].min()
    f = np.clip(f, zmin, zmax)
    grid_level = _Level(left, top, cell, rows, columns)
    xs, ys = grid_level.centres()
    gx, gy = np.meshgrid(xs, ys)
    nodata = np.zeros((rows, columns), bool)
    if coast is not None:
        f = np.where(shapely.contains_xy(coast, gx, gy), f, zmin - 1.0)
    if boundary is not None:
        nodata |= ~shapely.contains_xy(boundary, gx, gy)
    out = f[margin:margin + out_rows, margin:margin + out_columns]
    mask = nodata[margin:margin + out_rows, margin:margin + out_columns]
    grid = _Grid(xmin, ymin + out_rows * cell, cell, cell, out_rows, out_columns, srid)
    raster = Raster._of(np.where(mask, 0, out).astype(np.float32), mask, grid)
    _outputs(plan, result, grid_level, f, zscale, zmid, srid, contour_features, streams, cliff_parts,
             out_stream_features, out_sink_features, out_residual_feature, out_stream_cliff_error_feature,
             out_contour_error_feature)
    if plan.diagnostic:
        _write_diagnostics(plan, diagnostics, result, grid_level, f)
    if plan.parameter_file:
        write_parameter_file(plan, plan.parameter_file)
    return raster


# ---- the optional outputs


def _create(path, shape, srid, fields):
    from . import _gpkg
    from .analysis import _out
    out = _out(path)
    _gpkg.create(out, shape, srid, [_gpkg.Column(n, t) for n, t in fields])
    return out


def _insert(path, names, rows):
    from . import da
    with da.InsertCursor(path, names) as written:
        for row in rows:
            written.insertRow(row)


def _outputs(plan, result, level, f, zscale, zmid, srid, contour_features, streams, cliffs, out_stream, out_sink,
             out_residual, out_errors, out_contour_errors):
    import shapely
    from shapely.geometry import LineString, Point
    xs, ys = level.centres()

    def at(rc):
        return float(xs[rc[1]]), float(ys[rc[0]])

    def crosses(a, b):
        if not cliffs:
            return False
        return any(shapely.intersects(c, LineString([at(a), at(b)])) for c in cliffs)

    if out_stream not in (None, "", "#"):
        rows = []
        for cells, _ in result["streams"]:
            run = [cells[0]] if cells else []
            for a, b in zip(cells, cells[1:]):
                if crosses(a, b):
                    if len(run) > 1:
                        rows.append((1, LineString([at(c) for c in run])))
                    rows.append((2, LineString([at(a), at(b)])))
                    run = [b]
                else:
                    run.append(b)
            if len(run) > 1:
                rows.append((1, LineString([at(c) for c in run])))
        for path in result["cleared"]:
            if len(path) > 1:
                rows.append((3, LineString([at(c) for c in path])))
        target = _create(out_stream, "Polyline", srid, [("Code", "Integer")])
        _insert(target, ["Code", "SHAPE@"], rows)
    if out_sink not in (None, "", "#"):
        target = _create(out_sink, "Point", srid, [("Elevation", "Double")])
        _insert(target, ["Elevation", "SHAPE@"], [(float(f[r, c]), Point(at((r, c)))) for r, c in result["remaining"]])
    if out_residual not in (None, "", "#"):
        rows = []
        for (r, c), w in zip(result["keys"], result["w"]):
            z = result["data"][(r, c)]
            residual = z - f[r, c]
            scaled = residual / (w * zscale)
            if abs(scaled) > 1:
                rows.append((z, float(residual), float(scaled), Point(at((r, c)))))
        target = _create(out_residual, "Point", srid, [("Elevation", "Double"), ("Residual", "Double"),
                                                         ("Scaled", "Double")])
        _insert(target, ["Elevation", "Residual", "Scaled", "SHAPE@"], rows)
    if out_errors not in (None, "", "#"):
        rows = _stream_cliff_errors(result, level, f, streams, cliffs, at, crosses)
        target = _create(out_errors, "Point", srid, [("Code", "Integer")])
        _insert(target, ["Code", "SHAPE@"], rows)
    if out_contour_errors not in (None, "", "#"):
        rows = _contour_errors(contour_features, level, f)
        target = _create(out_contour_errors, "Point", srid, [("ErrorValue", "Integer"), ("Bias", "Double")])
        _insert(target, ["ErrorValue", "Bias", "SHAPE@"], rows)


def _stream_cliff_errors(result, level, f, streams, cliffs, at, crosses):
    """Codes 1 (a closed loop in the stream lines), 4 (a distributary: a node with two lines leaving it), 5 (a stream
    over a cliff) and 8 (a cliff whose low side is higher than its high side on the output)."""
    from shapely.geometry import Point
    rows = []
    out_edges = {}

    def node(p):
        return (round(p[0], 6), round(p[1], 6))

    for line in streams:
        coords = list(line.coords)
        a, b = node(coords[0]), node(coords[-1])
        out_edges.setdefault(a, []).append(b)
    # Code 1: a node reachable from itself.
    reported = set()
    for start in out_edges:
        stack, seen = list(out_edges.get(start, [])), set()
        while stack:
            at_ = stack.pop()
            if at_ == start:
                if start not in reported:
                    rows.append((1, Point(start)))
                    reported.add(start)
                break
            if at_ in seen:
                continue
            seen.add(at_)
            stack += out_edges.get(at_, [])
    for start, ends in out_edges.items():
        if len(ends) >= 2:
            rows.append((4, Point(start)))
    for cells, _ in result["streams"]:
        for a, b in zip(cells, cells[1:]):
            if crosses(a, b):
                (xa, ya), (xb, yb) = at(a), at(b)
                rows.append((5, Point((xa + xb) / 2, (ya + yb) / 2)))
    if cliffs:
        cut_h, cut_v = result["cut_h"], result["cut_v"]
        pairs = [((r, c), (r, c + 1)) for r, c in zip(*np.nonzero(cut_h))]
        pairs += [((r, c), (r + 1, c)) for r, c in zip(*np.nonzero(cut_v))]
        for a, b in pairs:
            pa, pb = np.array(at(a)), np.array(at(b))
            middle = (pa + pb) / 2
            line = min(cliffs, key=lambda c: c.distance(Point(middle)))
            d = line.project(Point(middle))
            p0 = line.interpolate(max(d - 1e-6, 0))
            p1 = line.interpolate(min(d + 1e-6, line.length))
            direction = np.array([p1.x - p0.x, p1.y - p0.y])
            side_a = direction[0] * (pa[1] - p0.y) - direction[1] * (pa[0] - p0.x)
            low, high = (a, b) if side_a > 0 else (b, a)  # positive: a is on the line's left, the low side
            if f[low] > f[high]:
                rows.append((8, Point(middle)))
    return rows


def _contour_errors(contour_features, level, f):
    """ErrorValue 1 where a contour meets a contour of another elevation; 2, with its bias, at a contour whose mean
    departure from the output exceeds five times the standard deviation of all contours' departures (the page)."""
    from shapely.geometry import Point
    rows = []
    for i, (zi, si) in enumerate(contour_features):
        for zj, sj in contour_features[i + 1:]:
            if zi != zj and si.intersects(sj):
                meet = si.intersection(sj)
                p = meet.representative_point() if not meet.is_empty else si.interpolate(0)
                rows.append((1, None, Point(p.x, p.y)))
    departures, per = [], []
    for z, shape in contour_features:
        own = []
        for part in getattr(shape, "geoms", [shape]):
            steps = max(int(math.ceil(part.length / (level.cell / 2))), 1)
            for k in range(steps + 1):
                p = part.interpolate(part.length * k / steps)
                r, c, inside = level.cell_of(p.x, p.y)
                if inside:
                    own.append((float(f[int(r), int(c)] - z), p))
        departures += [d for d, _ in own]
        per.append(own)
    if len(departures) > 1:
        sd = float(np.std(departures))
        for own in per:
            if own and sd > 0:
                bias = float(np.mean([d for d, _ in own]))
                if abs(bias) > 5 * sd:
                    worst = max(own, key=lambda dp: abs(dp[0]))[1]
                    rows.append((2, bias, Point(worst.x, worst.y)))
    return rows


def _write_diagnostics(plan, diagnostics, result, level, f):
    lines = ["grapy TopoToRaster diagnostics (an equivalent of ANUDEM; see arcpy.sa.TopoToRaster)", ""]
    lines += ["Inputs:"] + [f"  {kind} {path} {field or '#'}" for kind, path, field in plan.inputs]
    lines += ["", "Parameters:"] + [f"  {line}" for line in _parameter_lines(plan, inputs=False)]
    lines += ["", "Resolution  rows  columns  data cells  iteration  sinks found  cleared  remaining"]
    for lv, log, count in diagnostics:
        for iteration, found, cleared, remaining in log:
            lines.append(f"{lv.cell:.6g}  {lv.rows}  {lv.columns}  {count}  {iteration}  {found}  {cleared}  "
                         f"{remaining}")
    lines += ["", f"Remaining sinks: {len(result['remaining'])}", f"Smoothing parameter (lambda): {result['lam']:.6g}"]
    with open(plan.diagnostic, "w", encoding="utf-8") as out:
        out.write("\n".join(lines) + "\n")


# ---- the parameter file (Topo to Raster by File's documented syntax)

_FILE_KEYWORDS = {"POINTELEVATION": "Point", "CONTOUR": "Contour", "SINK": "Sink", "STREAM": "Stream",
                  "LAKE": "Lake", "BOUNDARY": "Boundary", "CLIFF": "Cliff", "EXCLUSION": "Exclusion",
                  "COAST": "Coast"}
_ENFORCE_WORDS = {"ENFORCE": "ON", "NO_ENFORCE": "OFF", "ENFORCE_WITH_SINK": "ON_WITH_SINK"}


def _quoted(path):
    return f'"{path}"' if any(ch.isspace() for ch in path) else path


def _parameter_lines(plan, inputs=True):
    lines = []
    if inputs:
        for kind, path, field in plan.inputs:
            word = _FILE_KEYWORDS[kind]
            if kind in ("POINTELEVATION", "CONTOUR"):
                lines.append(f"{word} {_quoted(path)} {field}")
            elif kind == "SINK":
                lines.append(f"{word} {_quoted(path)} {field or 'NONE'}")
            else:
                lines.append(f"{word} {_quoted(path)}")
    lines += [f"ENFORCE {_ENFORCE_WORDS[plan.enforce]}", f"DATATYPE {plan.data_type}",
              f"ITERATIONS {plan.iterations}", f"ROUGHNESS_PENALTY {plan.roughness!r}",
              f"PROFILE_PENALTY {plan.profile!r}", f"DISCRETE_ERROR_FACTOR {plan.discrete!r}",
              f"VERTICAL_STANDARD_ERROR {plan.vse!r}", f"TOLERANCES {plan.tol1!r} {plan.tol2!r}",
              f"ZLIMITS {plan.zmin!r} {plan.zmax!r}"]
    if getattr(plan, "box", None) is not None:
        lines.append("EXTENT " + " ".join(repr(float(v)) for v in plan.box))
    if plan.cell is not None:
        lines.append(f"CELL_SIZE {float(plan.cell)!r}")
    lines.append(f"MARGIN {int(plan.margin)}")
    if plan.diagnostic:
        lines.append(f"OUT_DIAGNOSTICS {_quoted(os.fspath(plan.diagnostic))}")
    return lines


def write_parameter_file(plan, path):
    with open(path, "w", encoding="utf-8") as out:
        out.write("\n".join(_parameter_lines(plan)) + "\n")


def read_parameter_file(path):
    """A plan from a parameter file in the syntax Topo to Raster by File's page documents. Output keywords without a
    path (OUT_STREAM …) are accepted and ignored, as the page says their paths are given to the tool."""
    plan = _Plan()
    reverse_enforce = {v: k for k, v in _ENFORCE_WORDS.items()}
    inputs = {w.upper(): k for k, w in _FILE_KEYWORDS.items()}
    with open(path, encoding="utf-8") as source:
        for number, line in enumerate(source, 1):
            parts = [p.strip('"\'') for p in shlex.split(line, posix=False)]
            if not parts:
                continue
            word = parts[0].upper()
            try:
                if word in inputs:
                    kind = inputs[word]
                    field = parts[2] if len(parts) > 2 else None
                    if field and field.upper() in ("NONE", "#"):
                        field = None
                    plan.inputs.append((kind, parts[1], field if _CLASSES[kind].has_field else None))
                elif word == "ENFORCE":
                    plan.enforce = reverse_enforce[parts[1].upper()]
                elif word == "DATATYPE":
                    plan.data_type = parts[1].upper()
                elif word == "ITERATIONS":
                    plan.iterations = int(parts[1])
                elif word == "ROUGHNESS_PENALTY":
                    plan.roughness = float(parts[1])
                elif word == "PROFILE_PENALTY":
                    plan.profile = float(parts[1])
                elif word == "DISCRETE_ERROR_FACTOR":
                    plan.discrete = float(parts[1])
                elif word == "VERTICAL_STANDARD_ERROR":
                    plan.vse = float(parts[1])
                elif word == "TOLERANCES":
                    plan.tol1, plan.tol2 = float(parts[1]), float(parts[2])
                elif word == "ZLIMITS":
                    plan.zmin, plan.zmax = float(parts[1]), float(parts[2])
                elif word == "EXTENT":
                    plan.extent = " ".join(parts[1:5])
                elif word == "CELL_SIZE":
                    plan.cell = float(parts[1])
                elif word == "MARGIN":
                    plan.margin = int(parts[1])
                elif word == "OUT_DIAGNOSTICS":
                    plan.diagnostic = parts[1]
                elif word in ("OUT_STREAM", "OUT_SINK", "OUT_RESIDUALS", "OUT_STREAM_CLIFF_ERRORS",
                              "OUT_CONTOUR_ERRORS"):
                    continue
                else:
                    raise ExecuteError(f"TopoToRasterByFile: line {number} of '{path}' begins with '{parts[0]}', "
                                       "which is not a keyword of the parameter file.")
            except (IndexError, KeyError, ValueError):
                raise ExecuteError(f"TopoToRasterByFile: line {number} of '{path}' is not in the documented syntax: "
                                   f"{line.strip()}") from None
    if not plan.inputs:
        raise ExecuteError(f"TopoToRasterByFile: '{path}' names no input datasets.")
    for kind, _, field in plan.inputs:
        if kind in ("POINTELEVATION", "CONTOUR") and not field:
            raise ExecuteError(f"TopoToRasterByFile: a {_FILE_KEYWORDS[kind]} line needs the item holding heights.")
    return plan


def plan_of(in_topo_features, cell_size, extent, margin, minimum_z_value, maximum_z_value, enforce, data_type,
            maximum_iterations, roughness_penalty, discrete_error_factor, vertical_standard_error, tolerance_1,
            tolerance_2, out_diagnostic_file, out_parameter_file, profile_penalty):
    from .sa import _cell_size
    plan = _Plan()
    plan.inputs = _inputs(in_topo_features)
    plan.cell = _cell_size(cell_size, "TopoToRaster")
    plan.extent = None if extent in (None, "", "#") else extent
    plan.margin = _num(margin, 20, int)
    plan.zmin = _num(minimum_z_value, None)
    plan.zmax = _num(maximum_z_value, None)
    plan.enforce = str(enforce or "ENFORCE").upper() if enforce not in ("#",) else "ENFORCE"
    plan.data_type = str(data_type or "CONTOUR").upper() if data_type not in ("#",) else "CONTOUR"
    plan.iterations = _num(maximum_iterations, 20, int)
    plan.roughness = _num(roughness_penalty, None)
    plan.discrete = _num(discrete_error_factor, 1.0)
    plan.vse = _num(vertical_standard_error, 0.0)
    plan.tol1 = _num(tolerance_1, None)
    plan.tol2 = _num(tolerance_2, None)
    plan.profile = _num(profile_penalty, 0.0)
    plan.diagnostic = None if out_diagnostic_file in (None, "", "#") else os.fspath(out_diagnostic_file)
    plan.parameter_file = None if out_parameter_file in (None, "", "#") else os.fspath(out_parameter_file)
    return plan
