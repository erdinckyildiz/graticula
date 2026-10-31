"""arcpy.stats.EvaluateBinSizes as grapy builds it: an equivalent of the method its two pages describe (Ramos 2025)."""
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _binsizes  # noqa: E402


def points(directory, name, xy, values=None):
    path = arcpy.management.CreateFeatureclass(directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
    arcpy.management.AddField(path, "v", "DOUBLE")
    with arcpy.da.InsertCursor(path, ["v", "SHAPE@XY"]) as rows:
        for i, (x, y) in enumerate(xy):
            rows.insertRow((None if values is None else float(values[i]), (float(x), float(y))))
    return path


def table(path, fields):
    with arcpy.da.SearchCursor(path, fields) as rows:
        return [tuple(r) for r in rows]


class EvaluateBinSizes(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        arcpy.env.randomGenerator = None
        self._resamples = _binsizes._RESAMPLES
        _binsizes._RESAMPLES = 20      # the confidence region's mechanism, faster; the tool's own count is 100
        self.addCleanup(setattr, _binsizes, "_RESAMPLES", self._resamples)

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def run_tool(self, xy, values=None, **kwargs):
        source = points(self.directory, "pts", xy, values)
        return arcpy.stats.EvaluateBinSizes(source, self.out("bins"), self.out("scores"), self.out("bdry"), **kwargs)

    def test_a_uniform_grid_scores_within_bounds_and_recommends_a_tested_size(self):
        g = np.arange(0.5, 40, 1.0)
        xy = [(x, y) for x in g for y in g]                        # 1600 points, one per unit square
        result = self.run_tool(xy, bin_type="SQUARE", aggregation_boundary="ENVELOPE")
        rows = table(self.out("scores"), ["BIN_SIZE", "INT_UNIF", "CNT_VARIETY", "EVAL_SCORE", "SMOOTH_SCORE",
                                          "IN_CI", "N_VALID"])
        self.assertEqual(len(rows), 100)                            # "The tool tests 100 bin sizes"
        sizes = [r[0] for r in rows]
        # Smallest: 20 bins for every point in the envelope's area; largest: a quarter of the larger extent.
        area = 39.0 * 39.0
        self.assertAlmostEqual(sizes[0], (area / (20 * 1600)) ** 0.5, delta=1e-9)
        self.assertAlmostEqual(sizes[-1], 0.25 * 39.0, delta=1e-9)
        for _, uniformity, variety, score, *_ in rows:
            for v in (uniformity, variety, score):
                self.assertGreaterEqual(v, -1e-12)
                self.assertLessEqual(v, 1 + 1e-12)
            self.assertAlmostEqual(score, uniformity * variety, delta=1e-12)
        best = float(result[3])
        self.assertTrue(sizes[0] <= best <= sizes[-1])
        # A regular grid of points is spread evenly inside bins of any size: high internal uniformity throughout.
        self.assertGreater(np.mean([r[1] for r in rows]), 0.9)
        self.assertTrue(any(r[5] for r in rows))                   # a confidence region exists
        self.assertEqual(table(self.out("bdry"), ["BIN_SIZE"])[0][0], best)
        counts = [r[0] for r in table(self.out("bins"), ["COUNT"])]
        self.assertEqual(sum(counts), 1600)                         # every point is in one bin of the output

    def test_clustered_points_are_less_internally_uniform_at_large_sizes_than_a_uniform_grid(self):
        rng = np.random.default_rng(7)
        g = np.arange(0.5, 40, 1.0)
        uniform = [(x, y) for x in g for y in g]
        self.run_tool(uniform, bin_type="HEXAGON", aggregation_boundary="ENVELOPE")
        flat = [r[0] for r in table(self.out("scores"), ["INT_UNIF"])][-20:]
        centres = rng.uniform(2, 38, (12, 2))
        clustered = np.vstack([c + rng.normal(0, 0.4, (130, 2)) for c in centres])
        clustered = np.vstack([clustered, [[0.5, 0.5], [39.5, 39.5]]]).clip(0.01, 39.99)
        self.setUp()
        self.run_tool(clustered, bin_type="HEXAGON", aggregation_boundary="ENVELOPE")
        lumpy = [r[0] for r in table(self.out("scores"), ["INT_UNIF"])][-20:]
        self.assertLess(np.mean(lumpy), np.mean(flat))

    def test_hexagon_sub_triangles_cover_the_bin(self):
        grid = _binsizes._Grid(True, 2.0, 0.0, 0.0)
        rng = np.random.default_rng(1)
        xy = rng.uniform(-1.2, 1.2, (20000, 2))
        col, row, sub = grid.locate(xy)
        mine = (col == 0) & (row == 0)
        inside = np.hypot(*xy.T) < 1.0                              # the inscribed circle, radius h / 2
        self.assertTrue(mine[inside].all())
        self.assertEqual(set(sub[mine].tolist()), set(range(24)))
        # Equal areas: every triangle holds about 1/24 of a uniform sample in the hexagon.
        share = np.bincount(sub[mine], minlength=24) / mine.sum()
        self.assertTrue(np.allclose(share, 1 / 24, atol=0.012))

    def test_an_analysis_field_scores_variance_loss(self):
        g = np.arange(0.5, 30, 1.0)
        xy = [(x, y) for x in g for y in g]
        values = [x + 0.5 * y for x, y in xy]
        result = self.run_tool(xy, values, bin_type="SQUARE", aggregation_boundary="CONVEX_HULL", analysis_field="v")
        rows = table(self.out("scores"), ["BIN_SIZE", "INT_UNIF", "VAR_LOSS", "EVAL_SCORE"])
        self.assertEqual(len(rows), 100)
        scored = [r for r in rows if r[3] is not None]
        self.assertTrue(scored)
        for _, uniformity, loss, score in scored:
            self.assertAlmostEqual(score, uniformity * loss, delta=1e-12)
        means = table(self.out("bins"), ["COUNT", "MEAN"])
        self.assertTrue(all(m is None for c, m in means if c == 0))
        self.assertTrue(any(m is not None for c, m in means if c))
        self.assertGreater(float(result[3]), 0)

    def test_a_time_field_is_refused_by_name(self):
        with self.assertRaises(arcpy.NotBuiltError) as caught:
            self.run_tool([(0, 0), (1, 1), (2, 0), (3, 3)], time_field="t", time_interval="1 Days")
        self.assertIn("time_field", str(caught.exception))
        for word in ("yet", "later", "not held"):
            self.assertNotIn(word, str(caught.exception))


if __name__ == "__main__":
    unittest.main()
