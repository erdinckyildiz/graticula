"""D-333 and D-334: Topo to Raster, Segment Mean Shift and the solar radiation tools were made faster on real data, and
every change keeps the answer. Each test runs the faster code beside the implementation it replaced — kept here as the
reference, or switched back on through a flag — and asserts the two equal, exactly.

Measured on real public data (2026-10-31): AreaSolarRadiation of a 250 x 250 cell 3DEP DEM (Mt Tamalpais, 10 m) went
from 413 s to 62 s of CPU, and 500 x 500 runs in 377 s, inside ADR-223's 10 minutes; Topo to Raster of 2,000 spot
heights from the same DEM on a 100 x 100 grid from 42 s to 27 s; Segment Mean Shift of a 1,024 x 1,024 Sentinel-2
crop from 218 s to 182 s.
"""
import math
import os
import sys
import shutil
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _insolation as _ins, _segment, _topo, sa  # noqa: E402


# ---- the implementations replaced, kept as references


def horizons_before(z, cell_w, cell_h, rows, cols, base, directions):
    """_insolation.horizons' rule traced the slow way: every location in every direction to the raster's edge, each
    step reading the nearest cell and measuring the angle to that cell's centre, a ray stopping at the outermost
    cells' centres — the reading ArcGIS Pro 3.7's durations follow (D-337), which replaced the bilinear sampling this
    reference first held."""
    rows, cols = np.asarray(rows, float), np.asarray(cols, float)
    base = np.broadcast_to(np.asarray(base, float), rows.shape)
    step = min(cell_w, cell_h)
    reach = int(math.ceil(math.hypot(z.shape[0] * cell_h, z.shape[1] * cell_w) / step)) + 1
    out = np.zeros((rows.size, int(directions)))
    for k in range(int(directions)):
        a = math.radians(k * 360.0 / directions)
        dr, dc = -math.cos(a) * step / cell_h, math.sin(a) * step / cell_w
        best = np.zeros(rows.size)
        gone = np.zeros(rows.size, bool)
        for s in range(1, reach + 1):
            r, c = rows + dr * s, cols + dc * s
            gone |= ~((r >= 0) & (r <= z.shape[0] - 1) & (c >= 0) & (c <= z.shape[1] - 1))
            if gone.all():
                break
            ri = np.clip(np.rint(r), 0, z.shape[0] - 1).astype(int)
            ci = np.clip(np.rint(c), 0, z.shape[1] - 1).astype(int)
            distance = np.hypot((ri - rows) * cell_h, (ci - cols) * cell_w)
            distance = np.where(distance > 0, distance, s * step)
            with np.errstate(invalid="ignore"):
                angle = np.degrees(np.arctan2(z[ri, ci] - base, distance))
            angle = np.where(gone | np.isnan(z[ri, ci]), 0.0, angle)
            best = np.maximum(best, angle)
        out[:, k] = best
    return out


def gaps_before(sky, sectors, horizon, zenith_divisions=8, azimuth_divisions=8):
    """The sun and sky gaps as _insolation.insolation formed them: sparse averaging matrices, one share per cell."""
    from scipy import sparse
    cells = sky.zenith.size
    rows_, cols_, vals = [], [], []
    for k, s in enumerate(sectors):
        hit = sky.cells_of(s.samples)
        rows_.extend(hit.tolist())
        cols_.extend([k] * hit.size)
        vals.extend([1.0 / hit.size] * hit.size)
    sun_matrix = sparse.csr_matrix((vals, (rows_, cols_)), shape=(cells, len(sectors)))
    ids = sky.sky_sectors(zenith_divisions, azimuth_divisions)
    total = zenith_divisions * azimuth_divisions
    counts = np.bincount(ids, minlength=total).astype(float)
    sky_matrix = sparse.csr_matrix((1.0 / np.maximum(counts[ids], 1), (np.arange(cells), ids)), shape=(cells, total))
    seen = sky.visible(horizon).astype(float)
    return (sparse.csr_matrix(seen) @ sun_matrix).toarray(), (sparse.csr_matrix(seen) @ sky_matrix).toarray()


def adjacent_before(labels):
    pairs = set()
    for x, y in ((labels[:, :-1], labels[:, 1:]), (labels[:-1, :], labels[1:, :])):
        keep = (x != y) & (x > 0) & (y > 0)
        for p, q in zip(x[keep].tolist(), y[keep].tolist()):
            pairs.add((p, q))
            pairs.add((q, p))
    near = {}
    for p, q in pairs:
        near.setdefault(p, set()).add(q)
    return near


def merge_small_before(labels, colours, minimum):
    """_segment._merge_small before D-333's second cut: a Python set of neighbours per region, candidates sorted."""
    for _ in range(int(labels.max()) + 1):
        means, sizes = _segment._region_means(labels, colours)
        small = [r for r in range(1, len(sizes)) if 0 < sizes[r] < minimum]
        if not small:
            break
        near = adjacent_before(labels)
        small.sort(key=lambda r: (sizes[r], r))
        lookup = np.arange(len(sizes))
        done = set()
        for r in small:
            if r in done or not near.get(r):
                continue
            candidates = sorted(near[r] - done, key=lambda q: (float(((means[q] - means[r]) ** 2).sum()), q))
            if not candidates:
                continue
            lookup[r] = candidates[0]
            done.update({r, candidates[0]})
        if (lookup == np.arange(len(sizes))).all():
            break
        while True:
            again = lookup[lookup]
            if (again == lookup).all():
                break
            lookup = again
        labels = lookup[labels]
    return _segment._renumber(labels)


def mean_shift_before(colours, mask, hs, hr, iterations=20):
    rows, columns, k = colours.shape
    yy, xx = np.mgrid[0:rows, 0:columns]
    valid = ~mask
    py, px = yy[valid].astype(np.float64), xx[valid].astype(np.float64)
    pc = colours[valid].astype(np.float64)
    radius = int(np.floor(hs))
    offsets = [(dy, dx) for dy in range(-radius, radius + 1) for dx in range(-radius, radius + 1)
               if dy * dy + dx * dx <= hs * hs]
    active = np.ones(py.size, bool)
    for _ in range(iterations):
        if not active.any():
            break
        index = np.flatnonzero(active)
        cy, cx, cc = py[index], px[index], pc[index]
        ry, rx = np.rint(cy).astype(int), np.rint(cx).astype(int)
        sy, sx = np.zeros(index.size), np.zeros(index.size)
        sc, n = np.zeros((index.size, k)), np.zeros(index.size)
        for dy, dx in offsets:
            ny, nx = ry + dy, rx + dx
            inside = (ny >= 0) & (ny < rows) & (nx >= 0) & (nx < columns)
            ny, nx = np.where(inside, ny, 0), np.where(inside, nx, 0)
            take = inside & valid[ny, nx]
            take &= (ny - cy) ** 2 + (nx - cx) ** 2 <= hs * hs
            neighbour = colours[ny, nx].astype(np.float64)
            take &= ((neighbour - cc) ** 2).sum(axis=1) <= hr * hr
            sy += np.where(take, ny, 0)
            sx += np.where(take, nx, 0)
            sc += np.where(take[:, None], neighbour, 0)
            n += take
        n = np.maximum(n, 1)
        ny_, nx_, nc_ = sy / n, sx / n, sc / n[:, None]
        moved = (np.hypot(ny_ - cy, nx_ - cx) >= 0.1) | (np.abs(nc_ - cc).max(axis=1) >= 0.1)
        py[index], px[index], pc[index] = ny_, nx_, nc_
        active[index[~moved]] = False
    modes = np.zeros((rows, columns, k + 2))
    modes[valid] = np.column_stack([py, px, pc])
    return modes


def rough(n, m, seed):
    """A rough surface: hills, a valley and noise, with a hole of NoData."""
    rng = np.random.default_rng(seed)
    y, x = np.mgrid[0:n, 0:m]
    z = 300 * np.exp(-((x - 0.7 * m) ** 2 + (y - 0.3 * n) ** 2) / (0.05 * n * m)) + 2.0 * x + rng.normal(0, 4, (n, m))
    return z


class Insolation(unittest.TestCase):
    def test_horizons_are_the_same_with_rays_stopped_when_they_can_gain_nothing(self):
        z = rough(40, 50, 1)
        z[10:13, 30:33] = np.nan
        rows, cols = np.nonzero(np.isfinite(z))
        for base, directions in ((z[rows, cols], 32), (z[rows, cols] + 15.0, 16)):
            got = _ins.horizons(z, 10.0, 7.5, rows, cols, base, directions)
            self.assertTrue(np.array_equal(got, horizons_before(z, 10.0, 7.5, rows, cols, base, directions)))
        # fractional locations, as Points Solar Radiation gives them, and a surface with no NoData
        z = rough(30, 30, 2)
        r, c = np.array([0.0, 3.4, 17.25, 29.0]), np.array([5.5, 0.0, 12.75, 29.0])
        base = np.array([10.0, 400.0, 150.0, 0.0])
        self.assertTrue(np.array_equal(_ins.horizons(z, 5.0, 5.0, r, c, base, 64),
                                       horizons_before(z, 5.0, 5.0, r, c, base, 64)))

    def test_sector_gaps_are_the_same_doubles_the_sparse_products_gave(self):
        sky = _ins.Sky(200)
        sectors, _ = _ins.sun_sectors(_ins.TimeMultipleDays(2024, 100, 160), 37.9, 14, 0.5)
        rng = np.random.default_rng(3)
        horizon = rng.uniform(0, 40, (64, 32))
        horizon[:5] = 0.0
        sun, sky_gap = gaps_before(sky, sectors, horizon)
        rows_, cols_, shares = [], [], []
        for k, s in enumerate(sectors):
            hit = sky.cells_of(s.samples)
            rows_ += hit.tolist()
            cols_ += [k] * hit.size
            shares.append((1.0 / hit.size, hit.size))
        from scipy import sparse
        members = sparse.csr_matrix((np.ones(len(rows_)), (rows_, cols_)), shape=(sky.zenith.size, len(sectors)))
        seen = sky.visible(horizon).astype(float)
        self.assertTrue(np.array_equal(_ins._shares(_ins._running_sums(shares), seen @ members), sun))
        ids = sky.sky_sectors(8, 8)
        counts = np.bincount(ids, minlength=64)
        members = sparse.csr_matrix((np.ones(ids.size), (np.arange(ids.size), ids)), shape=(ids.size, 64))
        table = _ins._running_sums([(1.0 / max(c, 1), int(c)) for c in counts])
        self.assertTrue(np.array_equal(_ins._shares(table, seen @ members), sky_gap))
        # and as insolation reads them: the visibility by cell, in the sky's wedge order
        order, _ = sky.wedge_order(32)
        by_cell = sky.visible_by_cell(horizon, np.float64)
        self.assertTrue(np.array_equal(_ins._shares(table, np.asarray(members[order].T.tocsr() @ by_cell).T), sky_gap))


class Segmentation(unittest.TestCase):
    def test_adjacency_and_mean_shift_are_the_same(self):
        rng = np.random.default_rng(4)
        labels = rng.integers(0, 30, (40, 50))
        self.assertEqual(_segment._adjacent(labels), adjacent_before(labels))
        self.assertEqual(_segment._adjacent(np.zeros((4, 4), np.int64)), adjacent_before(np.zeros((4, 4), np.int64)))
        colours = np.clip(rng.normal(120, 40, (35, 45, 3)), 0, 255).astype(np.uint8)
        colours[:, 20:] //= 3
        mask = np.zeros((35, 45), bool)
        mask[3:6, 7:9] = True
        for hs, hr in ((6.0, 6.0), (2.5, 19.0)):
            self.assertTrue(np.array_equal(_segment._mean_shift(colours, mask, hs, hr),
                                           mean_shift_before(colours, mask, hs, hr)))

    def test_mean_shift_in_chunks_and_merging_by_compressed_rows_are_the_same(self):
        # D-333: points moved a chunk at a time, and neighbours held as two arrays rather than a set per region.
        rng = np.random.default_rng(5)
        r, k = np.indices((70, 90), dtype=float)
        palette = rng.integers(20, 235, size=(16, 3))
        colours = palette[((r // 12) * 4 + (k // 12)).astype(int) % 16] + rng.normal(0, 6, (70, 90, 3))
        colours = np.clip(colours, 0, 255).astype(np.uint8)
        mask = np.zeros((70, 90), bool)
        mask[5:9, 10:30] = True
        self.addCleanup(setattr, _segment, "MEAN_SHIFT_CHUNK", _segment.MEAN_SHIFT_CHUNK)
        _segment.MEAN_SHIFT_CHUNK = 97
        self.assertTrue(np.array_equal(_segment._mean_shift(colours, mask, 6.0, 5.5),
                                       mean_shift_before(colours, mask, 6.0, 5.5)))
        labels = rng.integers(1, 400, (70, 90))
        labels[mask] = 0
        image = colours.astype(np.float64)
        for minimum in (3, 20, 60):
            self.assertTrue(np.array_equal(_segment._merge_small(labels, image, minimum),
                                           merge_small_before(labels, image, minimum)), minimum)


class Topo(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        self.addCleanup(setattr, _topo, "REMEMBER_OUTLETS", True)

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def test_remembered_outlet_paths_give_the_surface_searching_every_pass_gives(self):
        z = rough(60, 60, 5)
        rng = np.random.default_rng(6)
        picks = rng.choice(z.size, 300, replace=False)
        path = os.path.join(self.directory, "spots.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "spots.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(path, "z", "DOUBLE")
        with arcpy.da.InsertCursor(path, ["SHAPE@XY", "z"]) as cursor:
            for i in picks:
                r, c = divmod(int(i), 60)
                cursor.insertRow([(c * 10.0 + 5, 600 - r * 10.0 - 5), float(z.flat[i])])
        got = {}
        for remember in (True, False):
            _topo.REMEMBER_OUTLETS = remember
            out = sa.TopoToRaster([sa.TopoPointElevation([[path, "z"]])], 10, "0 0 600 600", data_type="SPOT",
                                  tolerance_1=5)
            got[remember] = arcpy.RasterToNumPyArray(out)
        self.assertTrue(np.array_equal(got[True], got[False], equal_nan=True))

    def test_the_symmetric_factor_gives_the_general_solves_surface_to_rounding(self):
        # D-333: SuperLU on a symmetric ordering without pivoting, against spsolve's default; a different order of
        # elimination, so equal to rounding rather than to the bit: within 1e-9 of the surface's range.
        self.addCleanup(setattr, _topo, "SYMMETRIC_FACTOR", True)
        z = rough(60, 60, 7)
        rng = np.random.default_rng(8)
        picks = rng.choice(z.size, 300, replace=False)
        path = os.path.join(self.directory, "spots2.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "spots2.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(path, "z", "DOUBLE")
        with arcpy.da.InsertCursor(path, ["SHAPE@XY", "z"]) as cursor:
            for i in picks:
                r, c = divmod(int(i), 60)
                cursor.insertRow([(c * 10.0 + 5, 600 - r * 10.0 - 5), float(z.flat[i])])
        got = {}
        for symmetric in (True, False):
            _topo.SYMMETRIC_FACTOR = symmetric
            out = sa.TopoToRaster([sa.TopoPointElevation([[path, "z"]])], 10, "0 0 600 600", data_type="SPOT",
                                  tolerance_1=5)
            got[symmetric] = arcpy.RasterToNumPyArray(out).astype(float)
        spread = float(np.nanmax(got[False]) - np.nanmin(got[False]))
        self.assertLess(float(np.nanmax(np.abs(got[True] - got[False]))), 1e-9 * spread)

    def test_the_grid_factor_gives_superlus_surface_to_rounding(self):
        # D-333: the nested-dissection Cholesky (_gridchol) in place of SuperLU, whose reservation for the factor put a
        # 400 x 400 grid past ADR-223's memory: another order of elimination, so equal to rounding.
        self.addCleanup(setattr, _topo, "GRID_FACTOR", True)
        z = rough(50, 70, 9)
        rng = np.random.default_rng(10)
        picks = rng.choice(z.size, 400, replace=False)
        path = os.path.join(self.directory, "spots3.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "spots3.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(path, "z", "DOUBLE")
        with arcpy.da.InsertCursor(path, ["SHAPE@XY", "z"]) as cursor:
            for i in picks:
                r, c = divmod(int(i), 70)
                cursor.insertRow([(c * 10.0 + 5, 500 - r * 10.0 - 5), float(z.flat[i])])
        got = {}
        for grid in (True, False):
            _topo.GRID_FACTOR = grid
            out = sa.TopoToRaster([sa.TopoPointElevation([[path, "z"]])], 10, "0 0 700 500", data_type="SPOT",
                                  tolerance_1=5, profile_penalty=0.3, roughness_penalty=0.01)
            got[grid] = arcpy.RasterToNumPyArray(out).astype(float)
        spread = float(np.nanmax(got[False]) - np.nanmin(got[False]))
        self.assertLess(float(np.nanmax(np.abs(got[True] - got[False]))), 1e-9 * spread)

    def test_the_grid_factor_solves_a_cliff_cut_system_as_spsolve_does(self):
        from scipy.sparse import diags
        from scipy.sparse.linalg import spsolve
        from grapy import _gridchol
        rng = np.random.default_rng(11)
        for rows, columns in ((1, 9), (5, 3), (37, 61), (64, 40)):
            cut_h = np.zeros((rows, max(columns - 1, 0)), bool)
            cut_v = np.zeros((max(rows - 1, 0), columns), bool)
            if rows > 6 and columns > 6:
                cut_h[rows // 3, :] = True
                cut_v[:, columns // 2] = True
            ops = _topo._operators(rows, columns, cut_h, cut_v)
            f = rng.normal(size=(rows, columns)).cumsum(0).cumsum(1)
            profile = _topo._profile_operator(f, rows, columns, cut_h, cut_v)
            penalty = ops["dxx"].T @ ops["dxx"] + 2 * (ops["dxy"].T @ ops["dxy"]) + ops["dyy"].T @ ops["dyy"]
            if profile is not None:
                penalty = penalty + profile.T @ profile
            n = rows * columns
            weight = np.where(rng.random(n) < 0.2, rng.uniform(1, 100, n), 0.0)
            weight[0] = 1e8                                         # a pinned cell, as a stream chain makes
            for lam in (1e-6, 1.0, 1e6):
                system = (diags(weight) + lam * penalty + diags(np.full(n, 1e-10))).tocsc()
                right = weight * rng.normal(size=n)
                want = spsolve(system, right)
                got = _gridchol.GridCholesky(system, rows, columns).solve(right)
                # both direct, so judged as a direct solve is: by the residual, which a backward-stable factor keeps
                # at rounding whatever the conditioning (λ = 1e6 on 15 cells is near singular, and there the two
                # answers themselves may part by more than rounding)
                scale = float(np.abs(right).max()) + float(abs(system).max()) * float(np.abs(want).max())
                residual = float(np.abs(system @ got - right).max())
                self.assertLess(residual, max(10 * float(np.abs(system @ want - right).max()), 1e-13 * scale),
                                (rows, columns, lam))
                if lam == 1.0:
                    self.assertLess(float(np.abs(got - want).max()), 1e-9 * float(np.abs(want).max()))


if __name__ == "__main__":
    unittest.main()
