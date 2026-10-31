"""Hot Spot Analysis Comparison against "How Hot Spot Analysis Comparison works" (read 2026-10-31)."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import numpy as np  # noqa: E402

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _hscompare as hsc  # noqa: E402


class Weights(unittest.TestCase):
    def test_fuzzy_weights_are_the_pages(self):
        # "the weight between 95% hot and 99% hot is 1.645/2.33 = 0.71"; 99/90 is 0.55 and 95/90 0.78.
        w = hsc.weights("FUZZY")
        self.assertAlmostEqual(w[3, 2], 0.71, delta=0.005)
        self.assertAlmostEqual(w[3, 1], 0.55, delta=0.005)
        self.assertAlmostEqual(w[2, 1], 0.78, delta=0.005)
        self.assertAlmostEqual(w[-3, -2], 0.71, delta=0.005)
        self.assertEqual(w[3, -3], 0.0)
        self.assertEqual(w[1, 0], 0.0)
        self.assertEqual(w[0, 0], 1.0)

    def test_grouped_and_reversed_weights(self):
        above_95 = hsc.weights("ABOVE_95")
        self.assertEqual((above_95[2, 3], above_95[-1, 1], above_95[1, 2]), (1.0, 1.0, 0.0))
        reverse = hsc.weights("REVERSE")
        self.assertEqual(reverse[3, -3], 1.0)
        self.assertAlmostEqual(reverse[3, -2], 0.71, delta=0.005)
        self.assertEqual(reverse[3, 3], 0.0)
        # The page's table example: [-3, -2, 0.6], a combination not given being 0.
        custom = hsc.weights("CUSTOM", [[-3, -2, 0.6]])
        self.assertEqual((custom[-3, -2], custom[-2, -3], custom[0, 0]), (0.6, 0.6, 0.0))


class WorkedExample(unittest.TestCase):
    def test_the_three_pairs_of_the_page(self):
        # A: cold, hot, not significant; B: hot, not significant, cold. Matching categories weigh 1, hot and cold 0,
        # not significant 1/2 with either. Feature 1 is nearer 2 than 3; two neighbours: distance weights 1, 2/3, 1/3.
        custom = [[c, c, 1] for c in (-3, 0, 3)] + [[3, -3, 0], [0, 3, 0.5], [0, -3, 0.5]]
        m = hsc._matrix(hsc.weights("CUSTOM", custom))
        a, b = np.array([-3, 3, 0]), np.array([3, 0, -3])
        neighbours, dw = hsc.neighbourhoods([(0, 0), (1, 0), (2.5, 0)], 2)
        self.assertEqual(neighbours.tolist(), [[0, 1, 2], [1, 0, 2], [2, 1, 0]])
        np.testing.assert_allclose(dw, [1.0, 2 / 3, 1 / 3], atol=1e-12)
        self.assertAlmostEqual(hsc.directional(a, b, neighbours, dw, m)[0], 1 / 3, delta=1e-12)   # A to B: 0.33
        self.assertAlmostEqual(hsc.directional(b, a, neighbours, dw, m)[0], 2 / 3, delta=1e-12)   # B to A: 0.67
        s = hsc.local_similarity(a, b, neighbours, dw, m)
        np.testing.assert_allclose(s, [1 / 3, 0.5, 0.5], atol=1e-12)
        self.assertAlmostEqual(float(s.mean()), 4 / 9, delta=1e-12)                              # 4/9 = 0.44


class Tool(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.randomGenerator = None

    def result(self, name, bins):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(path, "Gi_Bin", "LONG")
        with arcpy.da.InsertCursor(path, ["Gi_Bin", "SHAPE@"]) as rows:
            for i, value in enumerate(bins):
                rows.insertRow((value, f"POINT({i % 5 * 10} {i // 5 * 10})"))
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as cursor:
            return [tuple(r) for r in cursor]

    def bins(self):
        # A block of hot spots in the south-west, cold in the north-east, the rest not significant.
        return [3, 3, 2, 0, 0, 3, 2, 1, 0, 0, 1, 0, 0, 0, -1, 0, 0, -1, -2, -3, 0, 0, -2, -3, -3]

    def test_identical_results_are_perfectly_similar_and_associated(self):
        one = self.result("one", self.bins())
        two = self.result("two", self.bins())
        out = arcpy.stats.HotSpotAnalysisComparison(one, two, os.path.join(self.directory, "cmp.gpkg"), 4, 99)
        self.assertAlmostEqual(out[1], 1.0, delta=1e-12)
        self.assertLess(out[2], 1.0)
        self.assertAlmostEqual(out[3], 1.0, delta=1e-12)
        written = self.rows(out[0], ["GI_BIN_1", "GI_BIN_2", "GI_SIG_1", "SIM_VALUE", "KAPPA", "CAT_PAIR"])
        self.assertEqual(len(written), 25)
        self.assertEqual(written[0][:3], (3, 3, "Hot 99%"))
        self.assertEqual(written[0][5], "Hot 99% - Hot 99%")
        self.assertTrue(all(abs(r[3] - 1.0) < 1e-12 for r in written))

    def test_opposite_results_have_the_documented_minimum(self):
        # "The similarity value will be equal to 0 when all neighbors of the first result have completely
        # dissimilar significance level categories as all neighbors of the second result (all hot spots in the
        # first result and all cold spots in the second result)."
        one = self.result("hot", [3] * 25)
        two = self.result("cold", [-3] * 25)
        out = arcpy.stats.HotSpotAnalysisComparison(one, two, os.path.join(self.directory, "opp.gpkg"))
        self.assertEqual(out[1], 0.0)
        self.assertEqual(out[2], 0.0)
        self.assertEqual(out[3], 0.0)
        reversed_ = arcpy.stats.HotSpotAnalysisComparison(one, two, os.path.join(self.directory, "rev.gpkg"),
                                                          weighting_method="REVERSE")
        self.assertAlmostEqual(reversed_[1], 1.0, delta=1e-12)

    def test_excluding_nonsignificant_pairs_conditions_on_hot_and_cold_spots(self):
        one = self.result("one", self.bins())
        two = self.result("two", self.bins())
        out = arcpy.stats.HotSpotAnalysisComparison(one, two, os.path.join(self.directory, "ex.gpkg"), 4, 99,
                                                    exclude_nonsig_features="EXCLUDE")
        written = self.rows(out[0], ["GI_BIN_1", "SIM_VALUE", "EXP_SIM"])
        self.assertTrue(all(r[1] is None and r[2] is None for r in written if r[0] == 0))
        self.assertTrue(all(r[1] is not None for r in written if r[0] != 0))
        self.assertAlmostEqual(out[1], 1.0, delta=1e-12)

    def test_a_seeded_run_repeats_itself(self):
        one = self.result("one", self.bins())
        two = self.result("two", list(reversed(self.bins())))
        arcpy.env.randomGenerator = "7 ACM599"
        first = arcpy.stats.HotSpotAnalysisComparison(one, two, os.path.join(self.directory, "a.gpkg"), 4, 99)
        second = arcpy.stats.HotSpotAnalysisComparison(one, two, os.path.join(self.directory, "b.gpkg"), 4, 99)
        self.assertEqual((first[1], first[2], first[3]), (second[1], second[2], second[3]))
        self.assertAlmostEqual(first[3], (first[1] - first[2]) / (1 - first[2]), delta=1e-12)

    def test_twenty_intersections_are_required(self):
        one = self.result("few1", [0] * 19)
        two = self.result("few2", [0] * 19)
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.stats.HotSpotAnalysisComparison(one, two, os.path.join(self.directory, "few.gpkg"))


if __name__ == "__main__":
    unittest.main()
