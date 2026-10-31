"""Classification, pan sharpening, region growing and mean shift segmentation, built as equivalents — ADR-243.

Rasters are written north to south with 10-unit cells on EPSG:3857, the top-left corner at (0, rows·10), so the cell
in row r and column k has its centre at (5 + 10k, rows·10 − 5 − 10r).
"""
import json
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _classify as classify  # noqa: E402
from grapy._raster import Raster, _Grid  # noqa: E402


def bands(*arrays, cell=10.0):
    arrays = [np.asarray(a, dtype=np.float64) for a in arrays]
    rows, columns = arrays[0].shape
    grid = _Grid(0.0, rows * cell, cell, cell, rows, columns, 3857)
    if len(arrays) == 1:
        return Raster._of(arrays[0], np.zeros(arrays[0].shape, bool), grid)
    return Raster._of_bands(arrays, [np.zeros(a.shape, bool) for a in arrays], grid)


def values(raster):
    return [np.where(m, -1, np.asarray(a, dtype=np.float64)).tolist() for a, m in zip(raster._arrays, raster._masks)]


def box(x0, y0, x1, y1):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1),
                                      arcpy.Point(x0, y1), arcpy.Point(x0, y0)]), arcpy.SpatialReference(3857))


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), arcpy.SpatialReference(3857))


# Two land covers on a 6 × 6 raster of two bands: the west half near (10, 50), the east near (60, 20), each with a
# small, uncorrelated wobble so that the classes' covariance matrices are not singular.
ROWS = COLUMNS = 6
WOBBLE1 = [[(r * 7 + c * 3) % 5 for c in range(COLUMNS)] for r in range(ROWS)]
WOBBLE2 = [[(r * 3 + c * 5) % 4 for c in range(COLUMNS)] for r in range(ROWS)]
BAND1 = [[(10 if c < 3 else 60) + WOBBLE1[r][c] for c in range(COLUMNS)] for r in range(ROWS)]
BAND2 = [[(50 if c < 3 else 20) + WOBBLE2[r][c] for c in range(COLUMNS)] for r in range(ROWS)]
TRUTH = [[1 if c < 3 else 2 for c in range(COLUMNS)] for r in range(ROWS)]
# Classify Raster's cells hold each class's 0-based index in the definition, as ArcGIS Pro 3.7 writes them (measured
# 2026-10-31, ADR-243 condition 2: class values 1-5 written as 0-4, Classvalue and Class_name in the table).
INDEX = [[v - 1 for v in row] for row in TRUTH]


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def features(self, name, shape, rows, fields=()):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def training(self):
        # Two polygons covering the westmost two columns and the eastmost two, all six rows.
        return self.features("train", "POLYGON", [(1, "west", box(0, 0, 20, 60)), (2, "east", box(40, 0, 60, 60))],
                             [("classvalue", "LONG"), ("classname", "TEXT")])


class Supervised(_Fixture):
    def test_maximum_likelihood_definition_holds_the_class_statistics(self):
        image = bands(BAND1, BAND2)
        ecd = os.path.join(self.directory, "ml.ecd")
        classify.TrainMaximumLikelihoodClassifier(image, self.training(), ecd)
        with open(ecd, encoding="utf-8") as handle:
            document = json.load(handle)
        west = np.array([[BAND1[r][c], BAND2[r][c]] for r in range(ROWS) for c in (0, 1)], dtype=float)
        self.assertEqual(document["classifier"], "MaximumLikelihood")
        self.assertEqual([c["name"] for c in document["classes"]], ["west", "east"])
        np.testing.assert_allclose(document["means"][0], west.mean(axis=0))
        np.testing.assert_allclose(document["covariances"][0], np.cov(west, rowvar=False))
        self.assertEqual(values(classify.ClassifyRaster(image, ecd)), [INDEX])
        self.assertEqual(values(classify.Classify(image, None, ecd)), [INDEX])
        table = classify.ClassifyRaster(image, ecd)._rat
        self.assertEqual(table.names(), ["Value", "Classvalue", "Class_name", "Red", "Green", "Blue"])
        self.assertEqual([r[:3] for r in table.rows], [[0, 1, "west"], [1, 2, "east"]])

    def test_k_nearest_neighbour_and_support_vector_machine_separate_the_covers(self):
        image = bands(BAND1, BAND2)
        for train, name in ((classify.TrainKNearestNeighborClassifier, "knn.ecd"),
                            (classify.TrainSupportVectorMachineClassifier, "svm.ecd")):
            ecd = os.path.join(self.directory, name)
            train(image, self.training(), ecd)
            self.assertEqual(values(classify.ClassifyRaster(image, ecd)), [INDEX], name)

    def test_knn_votes_by_plurality(self):
        samples = np.array([[0.0], [1.0], [2.0], [10.0]])
        labels = np.array([1, 2, 2, 3])
        # The three nearest to 0.4 are 0, 1 and 2: two votes for class 2 against one for class 1.
        self.assertEqual(classify._knn(np.array([[0.4]]), samples, labels, 3).tolist(), [2])
        # k = 1 takes the nearest.
        self.assertEqual(classify._knn(np.array([[0.4], [9.0]]), samples, labels, 1).tolist(), [1, 3])

    def test_smo_finds_the_maximum_margin_hyperplane(self):
        # Two points per side of x = 0: the maximum margin hyperplane is x = 0 with |w| = 1 (margin 1 to each side).
        x = np.array([[-1.0, 0.0], [-1.0, 1.0], [1.0, 0.0], [1.0, 1.0]])
        w, b = classify._smo(x, np.array([-1.0, -1.0, 1.0, 1.0]), c=10.0)
        self.assertAlmostEqual(w[0], 1.0, places=3)
        self.assertAlmostEqual(w[1], 0.0, delta=1e-3)
        self.assertAlmostEqual(b, 0.0, delta=1e-3)

    def test_smo_reaches_the_optimum_when_the_classes_overlap(self):
        # ADR-243 condition 2, measured beside ArcGIS Pro 3.7 on 2026-10-31: on two overlapping classes, fifty plain
        # passes over every example stopped at twice the optimal objective. The soft-margin primal reached must be the
        # one an independent quadratic programme reaches.
        from scipy.optimize import minimize
        rng = np.random.default_rng(11)
        x = np.vstack([rng.normal(0.0, 1.0, (150, 4)), rng.normal(0.6, 1.0, (150, 4))])
        y = np.repeat([1.0, -1.0], 150)
        w, b = classify._smo(x, y)
        q = (y[:, None] * y[None, :]) * (x @ x.T)
        a = minimize(lambda v: 0.5 * v @ q @ v - v.sum(), np.zeros(y.size), jac=lambda v: q @ v - 1, method="SLSQP",
                     bounds=[(0, 1.0)] * y.size, constraints=[{"type": "eq", "fun": lambda v: v @ y, "jac": lambda v: y}],
                     options={"maxiter": 500, "ftol": 1e-12}).x
        best = -(0.5 * a @ q @ a - a.sum())  # the dual optimum equals the primal optimum
        primal = 0.5 * w @ w + np.maximum(0.0, 1 - y * (x @ w + b)).sum()
        self.assertLess(primal, best * 1.01)

    def test_a_definition_grapy_did_not_write_is_refused(self):
        foreign = os.path.join(self.directory, "pro.ecd")
        with open(foreign, "w", encoding="utf-8") as handle:
            handle.write('{"Type": "ClassifierDefinition"}')
        with self.assertRaises(arcpy.NotBuiltError):
            classify.ClassifyRaster(bands(BAND1, BAND2), foreign)

    def test_segment_attributes_are_refused(self):
        with self.assertRaises(arcpy.NotBuiltError):
            classify.TrainMaximumLikelihoodClassifier(bands(BAND1, BAND2), self.training(), "x.ecd",
                                                      used_attributes="COLOR;COUNT")


class Unsupervised(_Fixture):
    def test_isodata_finds_the_two_covers(self):
        image = bands(BAND1, BAND2)
        ecd = os.path.join(self.directory, "iso.ecd")
        # Four seeds, merged where they are closer than one standard deviation, cleaned of clusters under 5 cells.
        classify.TrainIsoClusterClassifier(image, 4, ecd, max_iterations=20, min_samples_per_cluster=5, skip_factor=1,
                                           max_merge_distance=1.0)
        with open(ecd, encoding="utf-8") as handle:
            document = json.load(handle)
        self.assertEqual(len(document["means"]), 2)
        west = np.array([[BAND1[r][c], BAND2[r][c]] for r in range(ROWS) for c in range(3)], dtype=float)
        np.testing.assert_allclose(document["means"][0], west.mean(axis=0))
        self.assertEqual(values(classify.ClassifyRaster(image, ecd)), [INDEX])


class SignatureFile(_Fixture):
    GSG = """# Signatures Produced by ClassSig from Zone-Grid sample and Stack image
# Number of selected grids
/*        2
# Layer-Number        Grid-name
/*        1            b1
/*        2            b2
# Type  Number of Classes  Number of Layers  Number of Parametric Layers
  1     2                  2                 2
#  ----------------------------------------------------------------------
# Class ID    Number of Cells    Class Name
  1           300                low
# Layers   1           2
# Means
          0.0000      0.0000
# Covariance
1        1.0000      0.0000
2        0.0000      1.0000
# ---------------------------------------------------------------
# Class ID    Number of Cells    Class Name
  7           100                high
# Layers   1           2
# Means
         10.0000     10.0000
# Covariance
1        1.0000      0.0000
2        0.0000      1.0000
"""

    def write(self):
        path = os.path.join(self.directory, "sig.gsg")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(self.GSG)
        return path

    def test_cells_go_to_the_nearest_class_and_far_ones_are_rejected(self):
        b1, b2 = bands([[0.0, 4.0, 9.0, 3.0]], [[0.0, 0.0, 10.0, 0.0]])._arrays
        image = [bands(b1), bands(b2)]
        signature = self.write()
        confidence = os.path.join(self.directory, "conf.tif")
        out = classify.MLClassify(image, signature, out_confidence_raster=confidence)
        self.assertEqual(values(out), [[[1, 1, 7, 1]]])
        # The squared Mahalanobis distances to their class are 0, 16, 1 and 9, with two degrees of freedom: the
        # chi-squared tail exp(−d²/2) is 1, 3.4e-4, 0.607 and 0.0111 — levels 1, 14, 7 (≥ 0.5) and 12 (≥ 0.01).
        self.assertEqual(arcpy.RasterToNumPyArray(arcpy.Raster(confidence)).tolist(), [[1, 14, 7, 12]])
        rejected = classify.MLClassify(image, signature, reject_fraction="0.05")
        self.assertEqual(values(rejected), [[[1, -1, 7, -1]]])

    def test_sample_priors_move_the_boundary_towards_the_rarer_class(self):
        # Halfway between the means, at (5, 5), the priors 0.75 and 0.25 favour the commoner class.
        image = [bands([[5.0]]), bands([[5.0]])]
        self.assertEqual(values(classify.MLClassify(image, self.write(), a_priori_probabilities="SAMPLE")), [[[1]]])

    def test_a_priori_file_tips_a_cell_towards_the_likelier_class(self):
        # At (5.1, 5.1) the squared distances are 52.02 and 48.02, a likelihood ratio of e² ≈ 7.39 for class 7; the
        # priors 0.9 and 0.1 (a ratio of 9) tip it back to class 1.
        image = [bands([[5.1]]), bands([[5.1]])]
        self.assertEqual(values(classify.MLClassify(image, self.write())), [[[7]]])
        priors = os.path.join(self.directory, "priors.txt")
        with open(priors, "w", encoding="utf-8") as handle:
            handle.write("1  .9\n")
        self.assertEqual(values(classify.MLClassify(image, self.write(), a_priori_probabilities="FILE",
                                                    in_a_priori_file=priors)), [[[1]]])


class Pansharpening(unittest.TestCase):
    # A 1 × 1 multispectral raster of 20-unit cells over a 2 × 2 panchromatic one of 10-unit cells: every pan cell
    # samples the one multispectral cell, so the bands are R = 40, G = 30, B = 20, I = 60.
    MS = [[[40.0]], [[30.0]], [[20.0]], [[60.0]]]
    PAN = [[50.0, 60.0], [70.0, 80.0]]

    def inputs(self):
        return bands(self.PAN), bands(*self.MS, cell=20.0)

    def test_esri_adds_the_pan_minus_the_weighted_average(self):
        pan, ms = self.inputs()
        out = classify.Pansharpen(pan, ms, weights=[0.25, 0.25, 0.25, 0.25], type="ESRI")
        # WA = (40 + 30 + 20 + 60)/4 = 37.5; ADJ = P − 37.5.
        self.assertEqual(values(out)[0], [[52.5, 62.5], [72.5, 82.5]])
        self.assertEqual(values(out)[3], [[72.5, 82.5], [92.5, 102.5]])

    def test_brovey_scales_by_the_pan_less_infrared_over_the_weighted_colours(self):
        pan, ms = self.inputs()
        out = classify.Pansharpen(pan, ms, weights="0.25 0.25 0.25 0.25", type="Brovey")
        # DNF = (P − 0.25·60) / (0.25·(40 + 30 + 20)) = (P − 15) / 22.5; at P = 60, DNF = 2.
        self.assertAlmostEqual(values(out)[0][0][1], 80.0, places=4)
        self.assertAlmostEqual(values(out)[3][0][1], 120.0, places=4)

    def test_simple_mean_and_ihs(self):
        pan, ms = self.inputs()
        mean = classify.Pansharpen(pan, ms, type="SimpleMean")
        self.assertEqual(values(mean)[1], [[40.0, 45.0], [50.0, 55.0]])
        ihs = classify.Pansharpen(pan, ms, weights=[0.2, 0.2, 0.2, 0.4], type="IHS")
        # Intensity (40 + 30 + 20)/3 = 30 replaced by P − 0.4·60 = P − 24: each band + P − 54.
        self.assertAlmostEqual(values(ihs)[0][1][1], 40 + 80 - 54, places=4)

    def test_gram_schmidt_returns_the_bands_when_the_pan_is_their_weighted_mean(self):
        # Varying bands at the pan's resolution: a pan equal to the simulated one (the weighted mean) changes nothing.
        r = np.array([[10.0, 20.0], [30.0, 50.0]])
        g = np.array([[5.0, 25.0], [15.0, 40.0]])
        b = np.array([[8.0, 9.0], [30.0, 10.0]])
        w = np.array([1.0, 2.0, 1.0]) / 4
        pan = w[0] * r + w[1] * g + w[2] * b
        out = classify.Pansharpen(bands(pan), bands(r, g, b), fourth_band_of_ms_is_ir=False, weights=[1, 2, 1],
                                  type="Gram-Schmidt")
        for got, want in zip(values(out), (r, g, b)):
            np.testing.assert_allclose(got, want, atol=1e-4)

    def test_an_unlisted_sensor_is_refused(self):
        pan, ms = self.inputs()
        with self.assertRaises(arcpy.NotBuiltError):
            classify.Pansharpen(pan, ms, type="Gram-Schmidt", sensor="Landsat 8")


class Regions(_Fixture):
    def test_region_grows_within_threshold_and_radius(self):
        image = bands([[10, 11, 30, 12],
                       [11, 12, 31, 10],
                       [10, 10, 10, 10]])
        seeds = self.features("seeds", "POINT", [(100.0, 3.0, 99, point(5, 25))],
                              [("radius", "DOUBLE"), ("threshold", "DOUBLE"), ("fill", "LONG")])
        out = classify.RegionGrow(image, seeds, "radius", "threshold", "fill")
        # Everything within 3 of the region's mean (about 10.5) joins; the 30s stay, and so does nothing else.
        self.assertEqual(values(out), [[[99, 99, 30, 99], [99, 99, 31, 99], [99, 99, 99, 99]]])
        near = self.features("near", "POINT", [(15.0, 3.0, 99, point(5, 25))],
                             [("radius", "DOUBLE"), ("threshold", "DOUBLE"), ("fill", "LONG")])
        # A 15-unit radius from (5, 25) reaches the centres (15, 25), (5, 15) and (15, 15), but not (25, 25).
        self.assertEqual(values(classify.RegionGrow(image, near, "radius", "threshold", "fill")),
                         [[[99, 99, 30, 12], [99, 99, 31, 10], [10, 10, 10, 10]]])


class MeanShift(unittest.TestCase):
    def test_two_flat_covers_become_two_segments_of_their_mean_colour(self):
        left = [[100 + (r + c) % 3 for c in range(4)] for r in range(6)]
        right = [[200 + (r * c) % 3 for c in range(4)] for r in range(6)]
        grey = np.hstack([np.array(left, float), np.array(right, float)])
        out = classify.SegmentMeanShift(bands(grey), spectral_detail=15.5, spatial_detail=15, min_segment_size=5)
        got = np.array(values(out)[0])
        self.assertEqual(sorted(np.unique(got).tolist()), [int(round(np.mean(left))), int(round(np.mean(right)))])
        self.assertTrue((got[:, :4] == round(np.mean(left))).all())

    def test_small_segments_merge_into_the_nearest_neighbour(self):
        grey = np.full((5, 5), 50.0)
        grey[2, 2] = 120.0
        out = classify.SegMeanShift(bands(grey), spectral_radius=5, spatial_radius=2, min_num_pixels_per_segment=2)
        got = np.array(values(out)[0])
        # The single bright cell is a segment of one, merged into the only neighbour: the mean is (24·50 + 120)/25.
        self.assertTrue((got == round((24 * 50 + 120) / 25)).all())

    def test_max_segment_size_is_refused(self):
        with self.assertRaises(arcpy.NotBuiltError):
            classify.SegmentMeanShift(bands(np.zeros((3, 3))), max_segment_size=10)


if __name__ == "__main__":
    unittest.main()
