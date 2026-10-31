"""Spline with Barriers (ADR-243): the minimum curvature surface through the points, broken along the barriers.

How Spline with Barriers works names the method — Briggs's (1974) minimum curvature surface, with discontinuities
along barriers in the manner of Terzopoulos (1988) — and an iterative multigrid way of approximating it. This
equivalent computes the surface the iteration approaches directly, as the least-squares solution of the discretised
thin-plate energy.
"""
from __future__ import annotations

import math

import numpy as np

from ._raster import Raster, _Grid, _refuse_environment
from ._runtime import NotBuiltError, equivalent


def _given(value):
    return value is not None and not (isinstance(value, str) and value.strip() in ("", "#"))


METHOD = ("the minimum curvature (thin-plate) surface on the grid, its second-difference terms switched off where they "
          "cross a barrier, solved by sparse least squares",
          "Briggs 1974, Geophysics 39(1); Terzopoulos 1988, IEEE Trans. Pattern Analysis and Machine Intelligence "
          "10(4) (discontinuities); Smith and Wessel 1990, Geophysics 55(3)")

_EXACT = 1.0e4  # the weight of the data when no smoothing is asked: they are honoured to within numerical noise
_ANCHOR = 1.0e-6  # a faint pull towards the data's mean, so that a compartment without data stays defined


def _all_points(Input_point_features, Z_value_field, tool):
    from . import sa
    sources = Input_point_features if isinstance(Input_point_features, (list, tuple)) else [Input_point_features]
    xs, ys, zs, srid = [], [], [], 0
    from .analysis import _schema
    for source in sources:
        x, y, z = sa._points(source, Z_value_field, tool)
        xs.append(x)
        ys.append(y)
        zs.append(z)
        srid = srid or _schema(source)[2]
    xs, ys, zs = np.concatenate(xs), np.concatenate(ys), np.concatenate(zs)
    # Coincident points are averaged, as the tool's page says.
    found = {}
    for x, y, z in zip(xs, ys, zs):
        found.setdefault((float(x), float(y)), []).append(float(z))
    keys = list(found)
    return (np.array([k[0] for k in keys]), np.array([k[1] for k in keys]),
            np.array([sum(v) / len(v) for v in found.values()]), srid)


def _barriers(Input_barrier_features):
    """The barriers as one line geometry: lines as they are, polygons by their boundaries."""
    import shapely
    from . import da
    if not _given(Input_barrier_features):
        return None
    sources = Input_barrier_features if isinstance(Input_barrier_features, (list, tuple)) else [Input_barrier_features]
    lines = []
    for source in sources:
        with da.SearchCursor(source, ["SHAPE@"]) as rows:
            for (shape,) in rows:
                if shape is None:
                    continue
                geometry = shape._shape
                lines.append(geometry.boundary if geometry.geom_type.endswith("Polygon") else geometry)
    return shapely.union_all(lines) if lines else None


def SplineWithBarriers(Input_point_features, Z_value_field, Input_barrier_features=None, Output_cell_size=None,
                       Smoothing_Factor=0.0):
    """EQUIVALENT: Briggs's (1974) minimum curvature surface with barriers as discontinuities (Terzopoulos 1988): the
    grid of cell-centre values minimising the sum over the grid of the squared second differences z_xx², 2·z_xy² and
    z_yy² — the discrete thin-plate energy whose 13-point molecule How Spline with Barriers works describes — where a
    difference whose cells are joined across a barrier line or polygon boundary is left out, so the surface bends
    freely there; and honouring the points, each tied to the grid by bilinear interpolation of the four cell centres
    around it. With ``Smoothing_Factor`` 0 the points are honoured (to numerical precision); towards 1 they weigh
    less against the curvature, as documented. Coincident points are averaged, as documented. The output is floating
    point; its grid spans the points' extent, the cell size given or the shorter side divided by 250, as documented.

    INFERRED: a smoothing factor s > 0 weighs the data (1 − s)/s against the curvature energy measured in cell units;
    the grid's top-left corner is the points' extent's; a part of the grid cut off by barriers from every point keeps
    the points' mean."""
    import shapely
    from scipy.sparse import coo_matrix, identity
    from scipy.sparse.linalg import spsolve
    tool = "SplineWithBarriers"
    _refuse_environment(f"arcpy.sa.{tool}")
    xs, ys, zs, srid = _all_points(Input_point_features, Z_value_field, tool)
    if not _given(Output_cell_size):
        shorter = min(xs.max() - xs.min(), ys.max() - ys.min())
        if shorter <= 0:
            raise NotBuiltError(f"{tool}: the points' extent has no width or height to divide by 250; give "
                                "Output_cell_size.")
        cell = shorter / 250.0
    else:
        from . import sa
        cell = sa._cell_size(Output_cell_size, tool)
    smoothing = float(Smoothing_Factor if _given(Smoothing_Factor) else 0.0)
    if not 0 <= smoothing <= 1:
        raise ValueError(f"{tool}: Smoothing_Factor runs from 0 to 1.")
    columns = max(1, int(math.ceil((xs.max() - xs.min()) / cell - 1e-9)))
    rows = max(1, int(math.ceil((ys.max() - ys.min()) / cell - 1e-9)))
    grid = _Grid(xs.min(), ys.max(), cell, cell, rows, columns, srid)
    cx, cy = grid.centres()
    n = rows * columns
    index = np.arange(n).reshape(rows, columns)
    barrier = _barriers(Input_barrier_features)

    def broken(r0, c0, r1, c1):
        """Whether the links between the cell centres (r0, c0) and (r1, c1), arrays of them, cross a barrier."""
        if barrier is None or r0.size == 0:
            return np.zeros(r0.shape, bool)
        links = shapely.linestrings(np.stack([np.stack([cx[c0], cy[r0]], -1), np.stack([cx[c1], cy[r1]], -1)], 1))
        return shapely.intersects(links, barrier)

    r, c = np.indices((rows, columns))
    east = np.zeros((rows, columns), bool)  # the link from (r, c) to (r, c + 1) is cut
    south = np.zeros((rows, columns), bool)  # the link from (r, c) to (r + 1, c) is cut
    if columns > 1:
        east[:, :-1] = broken(r[:, :-1].ravel(), c[:, :-1].ravel(), r[:, 1:].ravel(),
                              c[:, 1:].ravel()).reshape(rows, columns - 1)
    if rows > 1:
        south[:-1] = broken(r[:-1].ravel(), c[:-1].ravel(), r[1:].ravel(), c[1:].ravel()).reshape(rows - 1, columns)
    values, row_of = [], 0
    rows_i, cols_i = [], []

    def term(cells, weights):
        nonlocal row_of
        for k, w in zip(cells, weights):
            rows_i.append(row_of)
            cols_i.append(k)
            values.append(w)
        row_of += 1

    for i in range(rows):
        for j in range(columns):
            if 0 < j < columns - 1 and not east[i, j - 1] and not east[i, j]:
                term((index[i, j - 1], index[i, j], index[i, j + 1]), (1.0, -2.0, 1.0))
            if 0 < i < rows - 1 and not south[i - 1, j] and not south[i, j]:
                term((index[i - 1, j], index[i, j], index[i + 1, j]), (1.0, -2.0, 1.0))
            if i < rows - 1 and j < columns - 1 and not (east[i, j] or east[i + 1, j] or south[i, j] or south[i, j + 1]):
                root = math.sqrt(2.0)
                term((index[i, j], index[i, j + 1], index[i + 1, j], index[i + 1, j + 1]), (root, -root, -root, root))
    energy = coo_matrix((values, (rows_i, cols_i)), shape=(max(row_of, 1), n)).tocsr()
    # Each point as the bilinear interpolation of the four cell centres around it.
    # A point beyond the outermost cell centres is tied to the nearest four by linear extrapolation (INFERRED).
    fx = (xs - grid.left) / cell - 0.5
    fy = (grid.top - ys) / cell - 0.5
    c0 = np.clip(np.floor(fx).astype(int), 0, max(columns - 2, 0))
    r0 = np.clip(np.floor(fy).astype(int), 0, max(rows - 2, 0))
    c1, r1 = np.minimum(c0 + 1, columns - 1), np.minimum(r0 + 1, rows - 1)
    tx, ty = fx - c0, fy - r0
    k = np.arange(xs.size)
    data = coo_matrix((np.concatenate([(1 - tx) * (1 - ty), tx * (1 - ty), (1 - tx) * ty, tx * ty]),
                       (np.concatenate([k, k, k, k]),
                        np.concatenate([index[r0, c0], index[r0, c1], index[r1, c0], index[r1, c1]]))),
                      shape=(xs.size, n)).tocsr()
    weight = _EXACT if smoothing == 0 else (1 - smoothing) / smoothing
    mean = float(zs.mean())
    system = (energy.T @ energy + weight * (data.T @ data) + _ANCHOR * identity(n)).tocsc()
    right = weight * (data.T @ zs) + _ANCHOR * mean
    surface = spsolve(system, right).reshape(rows, columns)
    return Raster._of(surface.astype(np.float32), np.zeros((rows, columns), bool), grid)


SplineWithBarriers = equivalent(*METHOD)(SplineWithBarriers)
