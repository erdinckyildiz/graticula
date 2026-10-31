"""arcpy.stats.EvaluateBinSizes, built as an equivalent of the method its pages describe (ADR-224 §5.6).

The tool's page (tool-reference/spatial-statistics/evaluate-bin-sizes-for-point-aggregation.html) and its "How
Evaluate Bin Sizes for Point Aggregation works" page (how-evaluate-bins-works.html), both read 2026-10-31, give the
method and cite Ramos 2025 for it: 100 candidate bin sizes evenly between the size that gives 20 bins for every input
point and a quarter of the boundary's larger extent; each size scored by internal uniformity (the share of non-empty
bins whose point counts in 25 sub-squares or 24 sub-triangles pass a chi-square goodness-of-fit test at p > 0.05) times
point count variety (the Shannon entropy of the bin counts in five equal intervals over that of a uniform distribution)
or, with an analysis field, times the variance loss 1 − Var_w(bin means) / Var(X); the scores smoothed with a spline,
whose largest value is the recommended size, and a bootstrapped confidence region around it.
"""
from __future__ import annotations

import math

from . import _gpkg, da
from ._result import Result
from ._runtime import AddWarning, equivalent

_TOOL = "EvaluateBinSizes"
_RESAMPLES = 100  # bootstrap resamples of the evaluation scores (INFERRED: the page gives no number)


def _s():
    from . import stats
    return stats


def _boundary(kind, points, custom_polygons):
    import shapely
    from shapely.geometry import MultiPoint, box
    cloud = MultiPoint(points)
    if kind == "ENVELOPE":
        return box(*cloud.bounds)
    if kind == "CONVEX_HULL":
        return cloud.convex_hull
    if kind == "CONCAVE_HULL":
        return shapely.concave_hull(cloud, ratio=0.3)
    if kind in ("CUSTOM", "CUSTOM_POLYGONS"):
        if not _s()._given(custom_polygons):
            raise ValueError(f"arcpy.stats.{_TOOL}: aggregation_boundary CUSTOM needs custom_polygons.")
        from .management import _source
        path, where = _source(custom_polygons)
        with da.SearchCursor(path, ["SHAPE@"], where) as rows:
            shapes = [r[0]._shape for r in rows if r[0] is not None and not r[0]._shape.is_empty]
        if not shapes:
            raise ValueError(f"arcpy.stats.{_TOOL}: custom_polygons holds no polygon.")
        return shapely.union_all(shapes)
    raise ValueError(f"arcpy.stats.{_TOOL}: aggregation_boundary is CONCAVE_HULL, CONVEX_HULL, ENVELOPE or CUSTOM.")


class _Grid:
    """A square or flat-topped hexagonal tessellation of bin height ``h`` anchored on (x0, y0): each point's bin, each
    bin's polygon and each point's sub-cell inside its bin (25 squares, or 24 triangles)."""

    def __init__(self, hexagon, h, x0, y0):
        self.hexagon, self.h, self.x0, self.y0 = hexagon, h, x0, y0
        self.a = h / math.sqrt(3.0)  # a hexagon's side and circumradius

    def centre(self, col, row):
        import numpy as np
        col, row = np.asarray(col), np.asarray(row)
        if not self.hexagon:
            return self.x0 + (col + 0.5) * self.h, self.y0 + (row + 0.5) * self.h
        return self.x0 + col * 1.5 * self.a, self.y0 + row * self.h + np.where(col % 2 == 1, self.h / 2, 0.0)

    def locate(self, xy):
        """Each point's (col, row) and sub-cell index."""
        import numpy as np
        x, y = xy[:, 0], xy[:, 1]
        if not self.hexagon:
            u, v = (x - self.x0) / self.h, (y - self.y0) / self.h
            col, row = np.floor(u).astype(np.int64), np.floor(v).astype(np.int64)
            su = np.clip(np.floor((u - col) * 5), 0, 4).astype(np.int64)
            sv = np.clip(np.floor((v - row) * 5), 0, 4).astype(np.int64)
            return col, row, sv * 5 + su
        # The nearest centre is the hexagon holding the point; the two nearest columns carry the candidates.
        base = np.floor((x - self.x0) / (1.5 * self.a)).astype(np.int64)
        best_d = np.full(len(x), np.inf)
        col = np.zeros(len(x), np.int64)
        row = np.zeros(len(x), np.int64)
        for c in (base, base + 1):
            offset = np.where(c % 2 == 1, self.h / 2, 0.0)
            r = np.round((y - self.y0 - offset) / self.h).astype(np.int64)
            cx, cy = self.centre(c, r)
            d = (x - cx) ** 2 + (y - cy) ** 2
            better = d < best_d
            best_d, col, row = np.where(better, d, best_d), np.where(better, c, col), np.where(better, r, row)
        cx, cy = self.centre(col, row)
        dx, dy = x - cx, y - cy
        # Six equilateral triangles from the centre, each cut into four by its edges' midpoints.
        angle = np.mod(np.arctan2(dy, dx), 2 * math.pi)
        sector = np.clip(np.floor(angle / (math.pi / 3)), 0, 5).astype(np.int64)
        t0, t1 = sector * math.pi / 3, (sector + 1) * math.pi / 3
        v1x, v1y, v2x, v2y = self.a * np.cos(t0), self.a * np.sin(t0), self.a * np.cos(t1), self.a * np.sin(t1)
        det = v1x * v2y - v2x * v1y
        l1 = (dx * v2y - v2x * dy) / det
        l2 = (v1x * dy - dx * v1y) / det
        l0 = 1.0 - l1 - l2
        part = np.where(l0 > 0.5, 0, np.where(l1 > 0.5, 1, np.where(l2 > 0.5, 2, 3)))
        return col, row, sector * 4 + part

    def polygon(self, col, row):
        from shapely.geometry import Polygon, box
        cx, cy = (float(v) for v in self.centre(col, row))
        if not self.hexagon:
            return box(cx - self.h / 2, cy - self.h / 2, cx + self.h / 2, cy + self.h / 2)
        return Polygon([(cx + self.a * math.cos(k * math.pi / 3), cy + self.a * math.sin(k * math.pi / 3))
                        for k in range(6)])

    def cells(self, boundary):
        """Every bin intersecting the boundary, as (cols, rows, polygons)."""
        import numpy as np
        import shapely
        xmin, ymin, xmax, ymax = boundary.bounds
        if self.hexagon:
            cols = np.arange(int(math.floor((xmin - self.x0) / (1.5 * self.a))) - 1,
                             int(math.ceil((xmax - self.x0) / (1.5 * self.a))) + 2)
            rows = np.arange(int(math.floor((ymin - self.y0) / self.h)) - 1,
                             int(math.ceil((ymax - self.y0) / self.h)) + 2)
        else:
            cols = np.arange(int(math.floor((xmin - self.x0) / self.h)), int(math.ceil((xmax - self.x0) / self.h)) + 1)
            rows = np.arange(int(math.floor((ymin - self.y0) / self.h)), int(math.ceil((ymax - self.y0) / self.h)) + 1)
        cc, rr = np.meshgrid(cols, rows, indexing="ij")
        cc, rr = cc.ravel(), rr.ravel()
        cx, cy = self.centre(cc, rr)
        if self.hexagon:
            k = np.arange(6) * math.pi / 3
            ring = np.stack([cx[:, None] + self.a * np.cos(k)[None, :], cy[:, None] + self.a * np.sin(k)[None, :]], -1)
        else:
            half = self.h / 2
            ring = np.stack([np.stack([cx - half, cx + half, cx + half, cx - half], 1),
                             np.stack([cy - half, cy - half, cy + half, cy + half], 1)], -1)
        polygons = shapely.polygons(ring)
        keep = shapely.intersects(polygons, boundary)
        return cc[keep], rr[keep], polygons[keep]


def _lookup(cols, rows, pc, pr):
    """Each point's index among the bins (cols, rows), or -1 when its bin is not one of them."""
    import numpy as np
    c0, r0 = int(min(cols.min(), pc.min())), int(min(rows.min(), pr.min()))
    width = int(max(rows.max(), pr.max())) - r0 + 1
    dense = np.full((int(max(cols.max(), pc.max())) - c0 + 1) * width, -1, np.int64)
    dense[(cols - c0) * width + (rows - r0)] = np.arange(len(cols))
    return dense[(pc - c0) * width + (pr - r0)]


def _evaluate(grid, boundary, xy, values):
    """(internal uniformity, variety or variance loss, valid bins) for one bin size, or None when skipped."""
    import numpy as np
    from scipy import stats as sps
    cols, rows, _ = grid.cells(boundary)
    if not len(cols):
        return None
    pc, pr, sub = grid.locate(xy)
    where = _lookup(cols, rows, pc, pr)
    inside = where >= 0
    where, sub = where[inside], sub[inside]
    parts = 24 if grid.hexagon else 25
    counts = np.bincount(where, minlength=len(cols))
    occupied = np.nonzero(counts)[0]
    if values is not None and len(occupied) < 0.1 * len(cols):
        return None
    table = np.zeros((len(cols), parts))
    np.add.at(table, (where, sub), 1)
    observed = table[occupied]
    expected = observed.sum(axis=1, keepdims=True) / parts
    statistic = ((observed - expected) ** 2 / expected).sum(axis=1)   # Pearson's chi-square, as scipy's chisquare
    passed = int((sps.chi2.sf(statistic, parts - 1) > 0.05).sum())
    uniformity = passed / len(occupied) if len(occupied) else 0.0
    if values is None:
        lo, hi = counts.min(), counts.max()
        if hi == lo:
            variety = 0.0
        else:
            frequency = np.histogram(counts, bins=5, range=(lo, hi))[0] / len(counts)
            frequency = frequency[frequency > 0]
            variety = float(-(frequency * np.log(frequency)).sum() / math.log(5))
        return uniformity, variety, len(occupied)
    v = values[inside]
    sums = np.bincount(where, weights=v, minlength=len(cols))
    means = sums[occupied] / counts[occupied]
    weights = counts[occupied]
    mean_w = float((weights * means).sum() / weights.sum())
    var_w = float((weights * (means - mean_w) ** 2).sum() / weights.sum())
    total = float(np.var(values))
    loss = 1.0 - var_w / total if total > 0 else 0.0
    return uniformity, loss, len(occupied)


def _smooth(x, y, w=None):
    """A cubic smoothing spline with its penalty chosen by generalized cross-validation (Craven and Wahba 1979;
    scipy.interpolate.make_smoothing_spline)."""
    from scipy.interpolate import make_smoothing_spline
    return make_smoothing_spline(x, y, w=w)


@equivalent("bin sizes scored over a full tessellation by the documented criteria, the scores smoothed by a cubic "
            "smoothing spline chosen by generalized cross-validation and bootstrapped with a seeded generator",
            "Ramos 2025, Finding an Adequate Areal Unit to Map Crime, doi:10.1007/978-3-031-81580-5_2; Craven and "
            "Wahba 1979; Efron and Tibshirani 1993")
def EvaluateBinSizes(in_point_features, out_features, out_charts_table, out_agg_bdry, bin_type="HEXAGON",
                     aggregation_boundary="CONCAVE_HULL", custom_polygons=None, analysis_field=None,
                     combine_coincident_pts="COMBINE", time_field=None, time_interval=None):
    """EQUIVALENT: the method of the tool's two pages (read 2026-10-31), which cite Ramos 2025 for it. 100 bin sizes
    evenly from the size that gives 20 bins for every input point (in the boundary's area) to 25 percent of the
    boundary's larger extent; for each, internal uniformity (the share of non-empty bins whose counts in 25 sub-squares
    or 24 sub-triangles pass scipy's chi-square goodness-of-fit test at p > 0.05) times point count variety (the
    Shannon entropy of all bins' counts, empty ones included, in five equal intervals, over ln 5) or, with an analysis
    field, times the variance loss 1 − Var_w(bin means) / Var(X), the page's formula. The recommended size is the
    largest value of the smoothed scores. Bin size is a square's side or a hexagon's flat-to-flat height, as documented.

    INFERRED (Q-215), the pages leaving them open or describing Pro's own shortcut:
    - every bin of a full tessellation intersecting the boundary is scored, not randomly placed simulated bins (the
      page calls simulation a speed-up whose results vary between runs); with an analysis field a size is skipped when
      fewer than one bin in ten holds a point, the page's 100 of 1000 simulated bins;
    - the tessellation is anchored on the boundary's lower-left corner; hexagons are flat-topped (two edges horizontal,
      so their height is vertical), and a point belongs to the bin whose centre is nearest; each hexagon's 24 triangles
      are its six centre triangles each cut in four by its edges' midpoints;
    - the smoothing is a cubic smoothing spline whose penalty is chosen by generalized cross-validation (Craven and
      Wahba 1979), the page naming only "a spline"; the recommended size is its maximum over 1000 even steps;
    - the confidence region: 100 bootstrap resamples of the (size, score) pairs, NumPy's PCG64 from
      ``env.randomGenerator``'s seed (0 when unset), each smoothed again and read at the recommended size; a size is in
      the region when its smoothed score exceeds the fifth percentile of those readings, as the page describes;
    - CONCAVE_HULL is shapely's concave hull with ratio 0.3; Var(X) and the weighted variance are population
      variances; COMBINE averages coincident points' analysis values into one point and applies only with an analysis
      field, as documented, and coincident points warn either way;
    - the table's fields are BIN_SIZE, INT_UNIF, CNT_VARIETY (VAR_LOSS with an analysis field), EVAL_SCORE (null for a
      skipped size), SMOOTH_SCORE, IN_CI (1 inside the confidence region) and N_VALID (non-empty bins); the bins carry
      COUNT, and MEAN with an analysis field; the boundary carries BIN_SIZE;
    - the derived outputs are returned in the page's order: out_features, out_charts_table, out_agg_bdry, the bin size
      as text (a Double, as the module's other scalars), and the output group layer as out_features' path, grapy
      having no group layer to make; no charts are attached to the table.

    Refused: time_field and time_interval — the page divides the points into intervals counted back from the latest
    time and drops a short earliest interval, but neither page says how the bins of different intervals share one
    tessellation's empty-bin counts when an interval is partly outside the data's time span."""
    import numpy as np
    s = _s()
    for name, value in (("time_field", time_field), ("time_interval", time_interval)):
        if s._given(value):
            s._refuse(_TOOL, name, value, "neither page says how the bins of intervals partly outside the data's time "
                      "span are counted among the empty bins")
    hexagon = s._keyword(bin_type or "HEXAGON")
    if hexagon not in ("HEXAGON", "SQUARE"):
        raise ValueError(f"arcpy.stats.{_TOOL}: bin_type is HEXAGON or SQUARE.")
    hexagon = hexagon == "HEXAGON"
    kind = s._keyword(aggregation_boundary or "CONCAVE_HULL")
    from .management import _source
    path, where = _source(in_point_features)
    fields = [analysis_field] if s._given(analysis_field) else []
    features, _, shape_kind, srid = s._features(_TOOL, path, fields, where)
    if shape_kind not in ("Point", "Multipoint") and features and features[0][4].geom_type != "Point":
        raise ValueError(f"arcpy.stats.{_TOOL}: in_point_features must be points.")
    if s._given(analysis_field):
        features = [f for f in features if f[1][analysis_field.lower()] is not None]
    if len(features) < 3:
        raise ValueError(f"arcpy.stats.{_TOOL}: at least three points are needed.")
    xy = np.array([(f[2], f[3]) for f in features], float)
    values = np.array([float(f[1][analysis_field.lower()]) for f in features]) if fields else None
    keys = [(round(x, 9), round(y, 9)) for x, y in xy]
    if len(set(keys)) < len(keys):
        AddWarning(f"{_TOOL}: some input points are coincident; large numbers of them can give unrealistic bin sizes.")
        if values is not None and s._keyword(combine_coincident_pts or "COMBINE") in ("COMBINE", "TRUE"):
            groups: dict = {}
            for key, point, value in zip(keys, xy, values):
                groups.setdefault(key, [point, []])[1].append(value)
            xy = np.array([g[0] for g in groups.values()])
            values = np.array([float(np.mean(g[1])) for g in groups.values()])
    boundary = _boundary(kind, [tuple(p) for p in xy], custom_polygons)
    if boundary.is_empty or boundary.area <= 0:
        raise ValueError(f"arcpy.stats.{_TOOL}: the aggregation boundary has no area.")
    xmin, ymin, xmax, ymax = boundary.bounds
    per_bin = boundary.area / (20 * len(xy))
    smallest = math.sqrt(2 * per_bin / math.sqrt(3)) if hexagon else math.sqrt(per_bin)
    largest = 0.25 * max(xmax - xmin, ymax - ymin)
    if not smallest < largest:
        raise ValueError(f"arcpy.stats.{_TOOL}: the boundary is too small for the number of points to test bin sizes.")
    sizes = np.linspace(smallest, largest, 100)
    scored = []
    for h in sizes:
        scored.append(_evaluate(_Grid(hexagon, float(h), xmin, ymin), boundary, xy, values))
    valid = np.array([r is not None for r in scored])
    if valid.sum() < 5:
        raise ValueError(f"arcpy.stats.{_TOOL}: too few bin sizes could be scored.")
    score = np.array([r[0] * r[1] if r is not None else np.nan for r in scored])
    x, y = sizes[valid], score[valid]
    spline = _smooth(x, y)
    fine = np.linspace(x[0], x[-1], 1000)
    best = float(fine[int(np.argmax(spline(fine)))])
    smoothed = spline(sizes)
    from .management import _seed
    generator = np.random.default_rng(_seed())  # PCG64 from the seed, or MT19937 (ADR-247 §5.2)
    readings = []
    for _ in range(_RESAMPLES):
        pick = generator.integers(0, len(x), len(x))
        unique, inverse = np.unique(pick, return_inverse=True)
        if len(unique) < 5:
            continue
        w = np.bincount(inverse).astype(float)
        try:
            readings.append(float(_smooth(x[unique], y[unique], w)(best)))
        except (ValueError, np.linalg.LinAlgError):
            continue
    floor = float(np.percentile(readings, 5)) if readings else float(spline(best))
    in_ci = [bool(valid[i] and smoothed[i] > floor) for i in range(len(sizes))]

    second = "VAR_LOSS" if values is not None else "CNT_VARIETY"
    columns = [_gpkg.Column("BIN_SIZE", "Double"), _gpkg.Column("INT_UNIF", "Double"), _gpkg.Column(second, "Double"),
               _gpkg.Column("EVAL_SCORE", "Double"), _gpkg.Column("SMOOTH_SCORE", "Double"),
               _gpkg.Column("IN_CI", "SmallInteger"), _gpkg.Column("N_VALID", "Integer")]
    rows = [(float(h), *(r[:2] if r is not None else (None, None)), None if r is None else float(r[0] * r[1]),
             float(smoothed[i]), int(in_ci[i]), r[2] if r is not None else 0)
            for i, (h, r) in enumerate(zip(sizes, scored))]
    table = s._write_table(out_charts_table, columns, rows)

    grid = _Grid(hexagon, best, xmin, ymin)
    cols, rws, polygons = grid.cells(boundary)
    pc, pr, _ = grid.locate(xy)
    at = _lookup(cols, rws, pc, pr)
    kept = at >= 0
    counts = np.bincount(at[kept], minlength=len(cols))
    sums = np.bincount(at[kept], weights=values[kept], minlength=len(cols)) if values is not None else None
    out = s._out(out_features)
    written = [_gpkg.Column("COUNT", "Integer")] + ([_gpkg.Column("MEAN", "Double")] if values is not None else [])
    _gpkg.create(out, "Polygon", srid, written)
    with da.InsertCursor(out, [*[c.name for c in written], "SHAPE@"]) as cursor:
        for i, polygon in enumerate(polygons):
            mean = [float(sums[i] / counts[i]) if counts[i] else None] if values is not None else []
            cursor.insertRow((int(counts[i]), *mean, polygon))
    boundary_out = s._out(out_agg_bdry)
    _gpkg.create(boundary_out, "Polygon", srid, [_gpkg.Column("BIN_SIZE", "Double")])
    with da.InsertCursor(boundary_out, ["BIN_SIZE", "SHAPE@"]) as cursor:
        cursor.insertRow((best, boundary))
    from ._runtime import AddMessage
    AddMessage(f"Recommended bin size: {best}")
    return Result(out, table, boundary_out, s._text(best), out)

