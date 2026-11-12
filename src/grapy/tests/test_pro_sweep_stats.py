"""arcpy.stats answers the Pro sweep measured in ArcGIS Pro 3.7, 2026-11-08 (tools/pro-compare/sweep), each pinned by
an example worked out by hand (or, for GWR's standard errors, by the hat matrix computed in the test itself).

The fixes pinned in test_stats.py beside the tests they changed: Local Moran's I's variance, Neighborhood Summary
Statistics, Export XYv's digits, Dimension Reduction, Generalized Linear Regression, Spatial Outlier Detection,
Similarity Search, OLS's coefficient table, Calculate Composite Index and Calculate Rates.
"""
import math
import os
import sys
import shutil
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import stats  # noqa: E402


class ProSweepStats(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        arcpy.env.workspace = self.directory
        arcpy.env.overwriteOutput = True

    def points(self, name, xys, columns):
        """Points with DOUBLE fields: ``columns`` maps a field name to its values."""
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)
        for field in columns:
            arcpy.management.AddField(path, field, "DOUBLE")
        with arcpy.da.InsertCursor(path, [*columns, "SHAPE@"]) as written:
            for i, xy in enumerate(xys):
                written.insertRow((*[values[i] for values in columns.values()],
                                   arcpy.PointGeometry(arcpy.Point(*xy), 3857)))
        return path

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def names(self, path):
        return [f.name for f in arcpy.ListFields(path) if f.type not in ("OID", "Geometry")]

    def test_hot_spots_count_the_neighbours_with_the_feature_itself(self):
        # Four points 10 apart, band 10: the ends have one neighbour and themselves (2), the middle two have three.
        path = self.points("hs", [(0, 0), (10, 0), (20, 0), (30, 0)], {"v": [1.0, 2.0, 3.0, 4.0]})
        out = stats.HotSpots(path, "v", self.out("hs_out"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "NONE", 10)[0]
        self.assertEqual(self.names(out), ["SOURCE_ID", "v", "GiZScore", "GiPValue", "NNeighbors", "Gi_Bin"])
        self.assertEqual([r[0] for r in self.rows(out, ["NNeighbors"])], [2, 3, 3, 2])

    def test_local_morans_i_writes_neighbours_z_transform_and_spatial_lag(self):
        # Values 1..4, mean 2.5, population σ = √1.25: z = (−1.5, −.5, .5, 1.5)/√1.25. Band 10, row-standardized: the
        # first point's lag is its one neighbour's z, the second's the mean of z₁ and z₃ = (−1.5 + .5)/2/√1.25.
        path = self.points("lm", [(0, 0), (10, 0), (20, 0), (30, 0)], {"v": [1.0, 2.0, 3.0, 4.0]})
        out = stats.ClustersOutliers(path, "v", self.out("lm_out"), "FIXED_DISTANCE_BAND", "EUCLIDEAN_DISTANCE", "ROW",
                                     10, None, "NO_FDR", 0)[0]
        got = self.rows(out, ["NNeighbors", "ZTransform", "SpatialLag"])
        sd = math.sqrt(1.25)
        self.assertEqual([g[0] for g in got], [1, 2, 2, 1])
        for (_, z, lag), want_z, want_lag in zip(got, [-1.5, -0.5, 0.5, 1.5], [-0.5, -0.5, 0.5, 0.5]):
            self.assertAlmostEqual(z, want_z / sd)
            self.assertAlmostEqual(lag, want_lag / sd)

    def test_incremental_spatial_autocorrelation_leaves_out_features_with_no_neighbour(self):
        # Four points 10 apart and a fifth 20 beyond: at 10 the fifth has no neighbour and is left out, so the band's
        # E[I] is −1/(4 − 1); at 40 all five count, −1/4. The table's last fields are z_score and p_value.
        path = self.points("isa", [(0, 0), (10, 0), (20, 0), (30, 0), (50, 0)], {"v": [1.0, 3, 2, 5, 4]})
        table = stats.IncrementalSpatialAutocorrelation(path, "v", 2, 10, 30, Output_Table=self.out("isa_t"))[0]
        self.assertEqual(self.names(table), ["Distance", "MoransI", "ExpectedI", "Variance", "z_score", "p_value"])
        expected = [r[0] for r in self.rows(table, ["ExpectedI"])]
        self.assertAlmostEqual(expected[0], -1 / 3)
        self.assertAlmostEqual(expected[1], -1 / 4)

    def test_density_based_clustering_writes_source_cluster_and_color(self):
        # Three points within 5 of each other and one alone: with 3 points a cluster, one cluster and noise.
        path = self.points("db", [(0, 0), (3, 0), (0, 3), (100, 100)], {"v": [0.0] * 4})
        out = stats.DensityBasedClustering(path, self.out("db_out"), "DBSCAN", 3, "5 Meters")[0]
        self.assertEqual(self.names(out), ["SOURCE_ID", "CLUSTER_ID", "COLOR_ID"])
        self.assertEqual(self.rows(out, ["SOURCE_ID", "CLUSTER_ID", "COLOR_ID"]),
                         [(1, 1, 1), (2, 1, 1), (3, 1, 1), (4, -1, -1)])

    def test_gwr_standard_errors_take_sigma_from_n_minus_2_tr_s_plus_tr_sts(self):
        # σ² = RSS/(n − 2 tr S + tr S'S), S the hat matrix, computed here from the bisquare weights of a band of 25;
        # SE_ = σ √diag(C C'), T_ = C/SE, and SG_ = 1 where |T| exceeds t(1 − α/2, n − k) at α = .05/(tr S/k).
        import numpy as np
        from scipy.stats import t as student
        xs = [(10 * i, 5 * (i % 3)) for i in range(12)]
        x1 = [0.0, 1, 3, 2, 5, 4, 6, 8, 7, 9, 11, 10]
        y = [1.0, 2.5, 6.0, 4.0, 11.0, 8.5, 13.0, 17.5, 14.0, 19.0, 23.5, 21.0]
        path = self.points("gw", xs, {"v": y, "x": x1})
        out = stats.GWR(path, "v", "CONTINUOUS", "x", self.out("gw_out"), "DISTANCE_BAND", "USER_DEFINED",
                        distance_band=60)[0]
        coords = np.array(xs, dtype=float)
        d = np.hypot(*(coords[:, None, :] - coords[None, :, :]).transpose(2, 0, 1))
        w = np.where(d < 60, (1 - (d / 60) ** 2) ** 2, 0.0)
        x = np.c_[np.ones(12), x1]
        hat, spread, betas = np.zeros((12, 12)), [], []
        for i in range(12):
            c = np.linalg.inv((x.T * w[i]) @ x) @ (x.T * w[i])
            hat[i] = x[i] @ c
            betas.append(c @ np.array(y))
            spread.append(np.sqrt(np.diag(c @ c.T)))
        residual = np.array(y) - (x * np.array(betas)).sum(axis=1)
        trace = np.trace(hat)
        sigma = math.sqrt(float(residual @ residual) / (12 - 2 * trace + float((hat * hat).sum())))
        critical = student.ppf(1 - 0.05 / (trace / 2) / 2, 12 - 2)
        got = self.rows(out, ["INTRCPT", "SE_INTRCPT", "T_INTRCPT", "SG_INTRCPT", "C_X", "SE_X", "T_X", "SG_X"])
        for i, row in enumerate(got):
            for j in range(2):
                b, se, t, sg = row[4 * j:4 * j + 4]
                self.assertAlmostEqual(b, betas[i][j])
                self.assertAlmostEqual(se, sigma * spread[i][j])
                self.assertAlmostEqual(t, b / se)
                self.assertEqual(sg, int(abs(t) > critical))
        self.assertEqual(self.names(out)[:3], ["SOURCE_ID", "v", "x"])

    def test_legacy_gwr_adaptive_is_the_bisquare_kernel(self):
        xs = [(10 * i, 5 * (i % 3)) for i in range(12)]
        values = {"v": [1.0, 2.5, 6.0, 4.0, 11.0, 8.5, 13.0, 17.5, 14.0, 19.0, 23.5, 21.0],
                  "x": [0.0, 1, 3, 2, 5, 4, 6, 8, 7, 9, 11, 10]}
        path = self.points("lg", xs, values)
        legacy = stats.GeographicallyWeightedRegression(path, "v", "x", self.out("lg_out"), "ADAPTIVE",
                                                        "BANDWIDTH_PARAMETER", None, 8)[0]
        modern = stats.GWR(path, "v", "CONTINUOUS", "x", self.out("mod_out"), "NUMBER_OF_NEIGHBORS", "USER_DEFINED",
                           number_of_neighbors=8, local_weighting_scheme="BISQUARE")[0]
        for a, b in zip(self.rows(legacy, ["INTRCPT", "C_X"]), self.rows(modern, ["INTRCPT", "C_X"])):
            self.assertAlmostEqual(a[0], b[0])
            self.assertAlmostEqual(a[1], b[1])

    def test_spatially_constrained_clustering_runs_without_an_output_table(self):
        path = self.points("sc", [(0, 0), (10, 0), (20, 0), (30, 0)], {"v": [1.0, 1.1, 9.0, 9.2]})
        result = stats.SpatiallyConstrainedMultivariateClustering(path, self.out("sc_out"), ["v"], "NONE", None, None,
                                                                  None, 2, "TRIMMED_DELAUNAY_TRIANGULATION")
        self.assertEqual(result[1], "")
        clusters = [r[0] for r in self.rows(result[0], ["CLUSTER_ID"])]
        self.assertEqual((clusters[0] == clusters[1], clusters[2] == clusters[3], clusters[0] != clusters[2]),
                         (True, True, True))

    def test_decompose_spatial_structure_takes_pros_keywords(self):
        path = self.points("ds", [(0, 0), (1, 0), (100, 0), (101, 0), (50, 80)], {"v": [1.0, 2, 3, 4, 5]})
        out = stats.DecomposeSpatialStructure(path, self.out("ds_out"), "ALL", 0.25, 1, "NUMBER_OF_NEIGHBORS",
                                              number_of_neighbors=2)[0]
        self.assertIn("v", self.names(out))
        bare = stats.DecomposeSpatialStructure(path, self.out("ds_bare"), "NO_FIELDS", 0.25, 1, "NUMBER_OF_NEIGHBORS",
                                               number_of_neighbors=2)[0]
        self.assertNotIn("v", self.names(bare))


if __name__ == "__main__":
    unittest.main()
