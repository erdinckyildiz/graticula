"""The arcpy.stats tools ADR-244 built with a seeded generator, each against an answer worked by hand.

Spatially Constrained Multivariate Clustering, Local Bivariate Relationships and Prepare Data for Prediction were
refused because their answers depend on random draws. They are built as equivalents over the methods their pages name
(SKATER, the power-weighted spanning tree entropy, the published balancing methods), drawn from env.randomGenerator's
seed. The worked numbers are derived in the comment above each assertion; the draws are tested by what the page says
they must do, and by repeating exactly under one seed.
"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.stats  # noqa: E402

stats = grapy.stats


class Base(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.scratchWorkspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        arcpy.env.randomGenerator = None

    def tearDown(self):
        arcpy.env.randomGenerator = None

    def make(self, name, shape, fields, rows, srid=3857):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=srid)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def points(self, name, xys, fields, columns):
        rows = [(*[c[i] for c in columns], arcpy.PointGeometry(arcpy.Point(*xy), 3857)) for i, xy in enumerate(xys)]
        return self.make(name, "POINT", fields, rows)

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def fields(self, path):
        return [f.name for f in arcpy.ListFields(path)]


class SpatiallyConstrained(Base):
    def line(self, name, values):
        return self.points(name, [(10 * i, 0) for i in range(len(values))], [("v", "DOUBLE")], [values])

    def test_two_clusters_cut_where_the_sum_of_squares_falls_most(self):
        # Six collinear points: their trimmed Delaunay neighbours are the ones beside them, so the spanning tree is the
        # path. Values 1 2 3 | 10 11 12: the cut between 3 and 10 leaves 2 + 2 of a 125.5 total sum of squares.
        # Pseudo F with k = 2: R² = 1 − 4/125.5, F = (R²/1)/((1 − R²)/4) = (121.5/125.5)·4/(4/125.5) = 121.5.
        path = self.line("scmc", [1.0, 2.0, 3.0, 10.0, 11.0, 12.0])
        result = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("scmc_out"), ["v"],
                                                                  number_of_clusters=2, output_table=self.out("scmc_t"))
        self.assertEqual([r[0] for r in self.rows(result[0], ["CLUSTER_ID"])], [1, 1, 1, 2, 2, 2])
        self.assertAlmostEqual(self.rows(result[1], ["NUM_CLUSTERS", "PSEUDO_F"])[0][1], 121.5, places=9)
        self.assertNotIn("PROB", self.fields(result[0]))

    def test_with_no_number_two_to_n_minus_one_are_evaluated(self):
        # k = 3 splits a three-point cluster, every cut a singleton (allowed when no other is): SSE 2 + 0.5,
        # R² = 123/125.5, F = (R²/2)/((2.5/125.5)/3) = 61.5·3/2.5 = 73.8. The peak is k = 2.
        path = self.line("scmc2", [1.0, 2.0, 3.0, 10.0, 11.0, 12.0])
        result = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("scmc2_out"), ["v"],
                                                                  output_table=self.out("scmc2_t"))
        table = self.rows(result[1], ["NUM_CLUSTERS", "PSEUDO_F"])
        self.assertEqual([r[0] for r in table], [2, 3, 4, 5])
        self.assertAlmostEqual(table[0][1], 121.5, places=9)
        self.assertAlmostEqual(table[1][1], 73.8, places=9)
        self.assertEqual([r[0] for r in self.rows(result[0], ["CLUSTER_ID"])], [1, 1, 1, 2, 2, 2])

    def test_singletons_are_avoided_and_a_minimum_size_is_kept(self):
        # 1 1 1 1 1 50: cutting off the 50 alone reduces the sum of squares most, but leaves a singleton; the best cut
        # that leaves none is 1 1 1 1 | 1 50. With a minimum of three features each side, only 1 1 1 | 1 1 50 is left.
        path = self.line("single", [1.0, 1.0, 1.0, 1.0, 1.0, 50.0])
        free = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("single_out"), ["v"],
                                                                number_of_clusters=2, output_table=self.out("s_t"))
        self.assertEqual([r[0] for r in self.rows(free[0], ["CLUSTER_ID"])], [1, 1, 1, 1, 2, 2])
        held = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("min_out"), ["v"], "NUM_FEATURES",
                                                                min_constraint=3, number_of_clusters=2,
                                                                output_table=self.out("m_t"))
        self.assertEqual([r[0] for r in self.rows(held[0], ["CLUSTER_ID"])], [1, 1, 1, 2, 2, 2])

    def test_a_maximum_splits_until_no_cluster_is_above_it(self):
        # At most two features a cluster: 1 2 3 | 10 11 12, then each three-point part split once.
        path = self.line("max", [1.0, 2.0, 3.0, 10.0, 11.0, 12.0])
        result = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("max_out"), ["v"], "NUM_FEATURES",
                                                                  max_constraint=2, output_table=self.out("max_t"))
        ids = [r[0] for r in self.rows(result[0], ["CLUSTER_ID"])]
        self.assertEqual(len(set(ids)), 4)
        self.assertTrue(all(ids.count(c) <= 2 for c in set(ids)))
        self.assertEqual(len(set(ids[:3]) & set(ids[3:])), 0)

    def test_a_path_has_one_spanning_tree_so_every_feature_stays(self):
        # The neighbour graph is a path: every random spanning tree is that path, every permuted solution the original,
        # and PROB is 1 everywhere.
        arcpy.env.randomGenerator = "7 ACM599"
        path = self.line("prob", [1.0, 2.0, 3.0, 10.0, 11.0, 12.0])
        result = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("prob_out"), ["v"],
                                                                  number_of_clusters=2, number_of_permutations=9,
                                                                  output_table=self.out("prob_t"))
        for (p,) in self.rows(result[0], ["PROB"]):
            self.assertAlmostEqual(p, 1.0, delta=1e-12)

    def test_polygons_by_shared_edges(self):
        # A 2 × 2 grid: the bottom row holds 1, the top 10. Edge contiguity links each square to two; the cut that
        # separates the rows is the one that removes all the variation.
        def square(x, y):
            return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x + 1, y), arcpy.Point(x + 1, y + 1),
                                              arcpy.Point(x, y + 1), arcpy.Point(x, y)]), 3857)
        rows = [(1.0, square(0, 0)), (1.0, square(1, 0)), (10.0, square(0, 1)), (10.0, square(1, 1))]
        path = self.make("grid", "POLYGON", [("v", "DOUBLE")], rows)
        result = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("grid_out"), ["v"],
                                                                  number_of_clusters=2,
                                                                  spatial_constraints="CONTIGUITY_EDGES_ONLY",
                                                                  output_table=self.out("grid_t"))
        self.assertEqual([r[0] for r in self.rows(result[0], ["CLUSTER_ID"])], [1, 1, 2, 2])

    def test_trimmed_delaunay_drops_edges_outside_the_hull(self):
        # Four corners and a centre: the corners' Thiessen polygons meet one another only outside the square (or at
        # a single point on its edge), so trimmed to the hull each corner's one neighbour is the centre.
        from grapy import _statsmore
        from shapely.geometry import Point
        xys = [(0, 0), (10, 0), (0, 10), (10, 10), (5, 5)]
        features = [(i, {}, x, y, Point(x, y)) for i, (x, y) in enumerate(xys)]
        self.assertEqual(_statsmore._trimmed_delaunay("t", features), [[4], [4], [4], [4], [0, 1, 2, 3]])

    def test_more_unconnected_groups_than_clusters_fails_and_a_file_is_refused(self):
        def square(x):
            return arcpy.Polygon(arcpy.Array([arcpy.Point(x, 0), arcpy.Point(x + 1, 0), arcpy.Point(x + 1, 1),
                                              arcpy.Point(x, 1), arcpy.Point(x, 0)]), 3857)
        path = self.make("apart", "POLYGON", [("v", "DOUBLE")], [(float(i), square(3 * i)) for i in range(3)])
        with self.assertRaisesRegex(ValueError, "unconnected"):
            stats.SpatiallyConstrainedMultivariateClustering(path, self.out("apart_out"), ["v"], number_of_clusters=2,
                                                             output_table=self.out("apart_t"))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "swm"):
            stats.SpatiallyConstrainedMultivariateClustering(path, self.out("f_out"), ["v"], number_of_clusters=2,
                                                             spatial_constraints="GET_SPATIAL_WEIGHTS_FROM_FILE",
                                                             output_table=self.out("f_t"))

    def test_it_is_marked_an_equivalent(self):
        self.assertIn("SKATER", stats.SpatiallyConstrainedMultivariateClustering.__grapy_equivalent__)


class LocalBivariate(Base):
    def line(self, name, ys):
        n = len(ys)
        return self.points(name, [(10 * i, 0) for i in range(n)], [("x", "DOUBLE"), ("y", "DOUBLE")],
                           [[float(i) for i in range(n)], ys])

    def test_a_near_line_is_positive_linear_and_significant(self):
        # y = x ± 0.05 alternating: the neighbourhood's points lie almost on a line, whose spanning tree is far shorter
        # than any permutation's, so no permutation has a smaller joint entropy: p = (0 + 1)/(99 + 1) = 0.01 for every
        # feature, which Benjamini–Hochberg keeps at 99% (p_(m) = 0.01 ≤ m·0.01/m). The alternation is no curve, so the
        # quadratic is not 3 AICc better, and the slope is positive.
        arcpy.env.randomGenerator = "1 ACM599"
        path = self.line("lbr", [i + 0.05 * (-1) ** i for i in range(40)])
        out = stats.LocalBivariateRelationships(path, "y", "x", self.out("lbr_out"), 30, 99).getOutput(0)
        rows = self.rows(out, ["PVALUES", "SIG_LEVEL", "LBR_TYPE", "COEF_2", "R2", "ENTROPY"])
        self.assertEqual(len(rows), 40)
        for p, level, kind, curve, r2, entropy in rows:
            self.assertAlmostEqual(p, 0.01, delta=1e-12)
            self.assertEqual(level, "99%")
            self.assertEqual(kind, "Positive Linear")
            self.assertIsNone(curve)
            self.assertGreater(r2, 0.99)
            self.assertGreater(entropy, 0)

    def test_a_parabola_is_convex(self):
        # y = (i − 19.5)²: every neighbourhood is fitted exactly by the quadratic, whose x² coefficient is positive.
        arcpy.env.randomGenerator = "2 ACM599"
        path = self.line("parabola", [(i - 19.5) ** 2 for i in range(40)])
        out = stats.LocalBivariateRelationships(path, "y", "x", self.out("par_out"), 30, 99,
                                                apply_false_discovery_rate_fdr_correction="NO_FDR").getOutput(0)
        kinds = {r[0] for r in self.rows(out, ["LBR_TYPE"])}
        self.assertEqual(kinds, {"Convex"})

    def test_one_seed_repeats_itself(self):
        values = [math.sin(3.7 * i) for i in range(35)]
        path = self.line("noise", values)
        arcpy.env.randomGenerator = "5 ACM599"
        a = self.rows(stats.LocalBivariateRelationships(path, "y", "x", self.out("n1"), 30, 99).getOutput(0),
                      ["PVALUES"])
        arcpy.env.randomGenerator = "5 ACM599"
        b = self.rows(stats.LocalBivariateRelationships(path, "y", "x", self.out("n2"), 30, 99).getOutput(0),
                      ["PVALUES"])
        self.assertEqual(a, b)

    def test_the_documented_ranges_are_held(self):
        path = self.line("short", [float(i) for i in range(35)])
        with self.assertRaisesRegex(ValueError, "30 and 1,000"):
            stats.LocalBivariateRelationships(path, "y", "x", self.out("s1"), 29)
        with self.assertRaisesRegex(ValueError, "99, 199, 499 or 999"):
            stats.LocalBivariateRelationships(path, "y", "x", self.out("s2"), 30, 100)
        with self.assertRaisesRegex(ValueError, "scaling_factor"):
            stats.LocalBivariateRelationships(path, "y", "x", self.out("s3"), 30, 99, scaling_factor=0)


class Prepare(Base):
    def data(self, name, classes, values, more=None):
        n = len(classes)
        fields = [("cls", "TEXT"), ("v", "DOUBLE")] + ([("cat", "TEXT")] if more else [])
        columns = [classes, values] + ([more] if more else [])
        return self.points(name, [(10 * i, 0) for i in range(n)], fields, columns)

    def test_a_random_split_reserves_the_percent_and_repeats_under_one_seed(self):
        path = self.data("split", ["a"] * 20, [float(i) for i in range(20)])
        arcpy.env.randomGenerator = "3 ACM599"
        first = stats.PrepareData(path, self.out("tr"), "RANDOM_SPLIT", self.out("te"), "v",
                                  explanatory_variables=[["v", None]])
        train = sorted(r[0] for r in self.rows(first[0], ["v"]))
        test = sorted(r[0] for r in self.rows(first[1], ["v"]))
        self.assertEqual(len(test), 2)
        self.assertEqual(sorted(train + test), [float(i) for i in range(20)])
        arcpy.env.randomGenerator = "3 ACM599"
        again = stats.PrepareData(path, self.out("tr2"), "RANDOM_SPLIT", self.out("te2"), "v")
        self.assertEqual(sorted(r[0] for r in self.rows(again[1], ["v"])), test)

    def test_a_spatial_split_takes_a_feature_and_its_nearest_neighbours(self):
        # 25 percent of 20 points on a line is 5: one point and its four nearest — five consecutive points.
        path = self.data("spatial", ["a"] * 20, [float(i) for i in range(20)])
        result = stats.PrepareData(path, self.out("str"), "SPATIAL_SPLIT", self.out("ste"), "v", percent=25)
        test = sorted(r[0] for r in self.rows(result[1], ["v"]))
        self.assertEqual(len(test), 5)
        self.assertEqual(test, [test[0] + k for k in range(5)])

    def test_every_category_stays_in_training(self):
        # One 'b' among 20 at half held out: a split that tests the 'b' is drawn again.
        path = self.data("cats", ["a"] * 19 + ["b"], [float(i) for i in range(20)])
        for seed in range(5):
            arcpy.env.randomGenerator = f"{seed} ACM599"
            result = stats.PrepareData(path, self.out(f"c{seed}"), "RANDOM_SPLIT", self.out(f"ct{seed}"), "cls",
                                       "CATEGORICAL", percent=50)
            self.assertIn("b", {r[0] for r in self.rows(result[0], ["cls"])})

    def test_random_under_and_oversampling_match_the_counts(self):
        path = self.data("bal", ["a"] * 15 + ["b"] * 5, [float(i) for i in range(20)])
        under = stats.PrepareData(path, self.out("under"), "NONE", None, "cls", "CATEGORICAL", [["v", None]],
                                  balancing_type="RANDOM_UNDER").getOutput(0)
        classes = [r[0] for r in self.rows(under, ["cls"])]
        self.assertEqual((classes.count("a"), classes.count("b")), (5, 5))
        over = stats.PrepareData(path, self.out("over"), "NONE", None, "cls", "CATEGORICAL", [["v", None]],
                                 balancing_type="RANDOM_OVER").getOutput(0)
        rows = self.rows(over, ["cls", "v"])
        self.assertEqual(([r[0] for r in rows].count("a"), [r[0] for r in rows].count("b")), (15, 15))
        self.assertTrue(all(v in (15.0, 16.0, 17.0, 18.0, 19.0) for c, v in rows if c == "b"))

    def test_smote_interpolates_between_minority_features(self):
        path = self.data("smote", ["a"] * 15 + ["b"] * 5, [float(i) for i in range(20)])
        out = stats.PrepareData(path, self.out("smote_out"), "NONE", None, "cls", "CATEGORICAL", [["v", None]],
                                balancing_type="SMOTE_OVER").getOutput(0)
        b = [v for c, v in self.rows(out, ["cls", "v"]) if c == "b"]
        self.assertEqual(len(b), 15)
        self.assertTrue(all(15.0 <= v <= 19.0 for v in b))

    def test_tomek_links_drop_the_majority_member(self):
        # v: a 0 1 2 9, b 10 20. b10's nearest is a9 and a9's is b10 — a Tomek link, so a9 goes; b20's nearest is b10.
        path = self.data("tomek", ["a", "a", "a", "a", "b", "b"], [0.0, 1.0, 2.0, 9.0, 10.0, 20.0])
        out = stats.PrepareData(path, self.out("tomek_out"), "NONE", None, "cls", "CATEGORICAL", [["v", None]],
                                balancing_type="TOMEK_UNDER").getOutput(0)
        self.assertEqual(sorted(r[0] for r in self.rows(out, ["v"])), [0.0, 1.0, 2.0, 10.0, 20.0])

    def test_k_medoids_keeps_the_medoids(self):
        # a: 0 1 2 and 10 11 12, b: 5 6. Two medoids of a: the middles of its two groups, 1 and 11.
        path = self.data("kmed", ["a"] * 6 + ["b"] * 2, [0.0, 1.0, 2.0, 10.0, 11.0, 12.0, 5.0, 6.0])
        out = stats.PrepareData(path, self.out("kmed_out"), "NONE", None, "cls", "CATEGORICAL", [["v", None]],
                                balancing_type="KMED_UNDER").getOutput(0)
        self.assertEqual(sorted(v for c, v in self.rows(out, ["cls", "v"]) if c == "a"), [1.0, 11.0])

    def test_spatial_thinning_keeps_features_apart(self):
        # Ten points 10 apart, a band of 15: no two kept neighbours; any greedy pass keeps at least 4 of 10.
        path = self.data("thin", ["a"] * 10, [float(i) for i in range(10)])
        out = stats.PrepareData(path, self.out("thin_out"), "NONE", None, "v", balancing_type="SPATIAL_THINNING",
                                thinning_distance_band=15).getOutput(0)
        kept = sorted(r[0] for r in self.rows(out, ["v"]))
        self.assertGreaterEqual(len(kept), 4)
        self.assertTrue(all(b - a >= 1.5 for a, b in zip(kept, kept[1:])))

    def test_encoding_and_no_append(self):
        path = self.data("enc", ["a"] * 4, [1.0, 2.0, 3.0, 4.0], ["x", "y", "x", "y"])
        out = stats.PrepareData(path, self.out("enc_out"), "NONE", None, "v", "NUMERIC", [["cat", "CATEGORICAL"]],
                                encode_variables="ENCODE", append_all_fields="NO_APPEND").getOutput(0)
        names = self.fields(out)
        self.assertNotIn("cls", names)
        self.assertEqual(self.rows(out, ["v", "cat", "cat_x", "cat_y"]),
                         [(1.0, "x", 1, 0), (2.0, "y", 0, 1), (3.0, "x", 1, 0), (4.0, "y", 0, 1)])

    def test_distance_features_and_rasters_are_refused(self):
        path = self.data("ref", ["a"] * 4, [1.0, 2.0, 3.0, 4.0])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "distance_features"):
            stats.PrepareData(path, self.out("r1"), "NONE", None, "v", distance_features=path)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "explanatory_rasters"):
            stats.PrepareData(path, self.out("r2"), "NONE", None, "v", explanatory_rasters="x.tif")


class StillRefused(unittest.TestCase):
    def test_each_keeps_a_reason_of_its_own(self):
        reasons = {
            "AttributeUncertainty": "geoprocessing history",
            "CrossValidate": "that run's own parameters",
            "PresenceOnlyPrediction": "regularization",
            "BuildBalancedZones": "crossover",
        }
        for name, said in reasons.items():
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                getattr(stats, name)()
            with self.assertRaises(arcpy.NotBuiltError) as caught:
                getattr(stats, name)()
            self.assertNotIn("random draws", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
