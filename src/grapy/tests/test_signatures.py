"""Signature files, class probabilities, training sample inspection and segment attributes — ADR-243.

Rasters are written north to south with 10-unit cells on EPSG:3857, the top-left corner at (0, rows·10), so the cell
in row r and column k has its centre at (5 + 10k, rows·10 − 5 − 10r).
"""
import math
import os
import sys
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _classify as classify, _signatures  # noqa: E402
import test_classify as base  # noqa: E402
from test_classify import BAND1, BAND2, COLUMNS, ROWS, TRUTH, bands, point, values  # noqa: E402


class _Fixture(base._Fixture):
    pass


class Signatures(_Fixture):
    def test_create_signatures_feeds_maximum_likelihood(self):
        image = [bands(BAND1), bands(BAND2)]
        gsg = os.path.join(self.directory, "made.gsg")
        _signatures.CreateSignatures(image, self.training(), gsg, sample_field="classvalue")
        classes = classify._read_gsg(gsg, "test")
        west = np.array([[BAND1[r][c], BAND2[r][c]] for r in range(ROWS) for c in (0, 1)], dtype=float)
        self.assertEqual([(c["value"], c["count"]) for c in classes], [(1, 12), (2, 12)])
        np.testing.assert_allclose(classes[0]["means"], west.mean(axis=0), atol=1e-5)
        np.testing.assert_allclose(classes[0]["covariance"], np.cov(west, rowvar=False), atol=1e-5)
        self.assertEqual(values(classify.MLClassify(image, gsg)), [TRUTH])

    def test_a_text_sample_field_names_the_classes_and_mean_only_drops_covariance(self):
        gsg = os.path.join(self.directory, "named.gsg")
        _signatures.CreateSignatures([bands(BAND1), bands(BAND2)], self.training(), gsg, "MEAN_ONLY", "classname")
        classes = classify._read_gsg(gsg, "test", covariance=False)
        # "east" sorts before "west": 1 and 2.
        self.assertEqual([(c["value"], c["name"], c["covariance"]) for c in classes], [(1, "east", []), (2, "west", [])])

    def test_edit_signatures_pools_merged_classes(self):
        image = [bands(BAND1), bands(BAND2)]
        labels = np.array([[1 if c < 2 else 2 if c < 4 else 3 for c in range(COLUMNS)] for _ in range(ROWS)])
        three = os.path.join(self.directory, "three.gsg")
        sample = bands(labels)
        sample._arrays = [labels.astype(np.int32)]
        _signatures.CreateSignatures(image, sample, three)
        remap = os.path.join(self.directory, "edit.rmp")
        with open(remap, "w", encoding="utf-8") as handle:
            handle.write("2 : 1\n3 : -9999\n")
        edited = os.path.join(self.directory, "edited.gsg")
        _signatures.EditSignatures(image, three, remap, edited)
        got = classify._read_gsg(edited, "test")
        # The merged class is the statistics of the union of the cells of classes 1 and 2: columns 0 to 3.
        union = np.array([[BAND1[r][c], BAND2[r][c]] for r in range(ROWS) for c in range(4)], dtype=float)
        self.assertEqual([(c["value"], c["count"]) for c in got], [(1, 24)])
        np.testing.assert_allclose(got[0]["means"], union.mean(axis=0), atol=1e-5)
        np.testing.assert_allclose(got[0]["covariance"], np.cov(union, rowvar=False), atol=1e-4)


class Probability(_Fixture):
    def write(self):
        path = os.path.join(self.directory, "sig.gsg")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write(base.SignatureFile.GSG)
        return path

    def test_posteriors_scale_to_the_maximum(self):
        image = [bands([[0.0, 5.0]]), bands([[0.0, 5.0]])]
        out = _signatures.ClassProbability(image, self.write())
        # At (0, 0) class 1 is e^(−0) against e^(−100): all of it; halfway, the two are equal.
        self.assertEqual(values(out), [[[100, 50]], [[0, 50]]])
        floating = _signatures.ClassProbability(image, self.write(), maximum_output_value=1)
        self.assertAlmostEqual(values(floating)[0][0][1], 0.5, places=6)

    def test_a_class_of_prior_zero_has_no_band(self):
        priors = os.path.join(self.directory, "p.txt")
        with open(priors, "w", encoding="utf-8") as handle:
            handle.write("1  0\n")
        out = _signatures.ClassProbability([bands([[0.0]]), bands([[0.0]])], self.write(),
                                           a_priori_probabilities="FILE", in_a_priori_file=priors)
        # Class 7 takes the whole remainder of 1, so it is certain wherever it is the only class left.
        self.assertEqual(values(out), [[[100]]])

    def test_read_priors_shares_the_remainder(self):
        path = os.path.join(self.directory, "share.txt")
        with open(path, "w", encoding="utf-8") as handle:
            handle.write("    1  .3\n    2  .1\n    4  .0\n    5  .15\n    7  .05\n    8  .2\n")
        # The page's own example: 0.8 given, so classes 3 and 6 get 0.1 each.
        got = classify.read_priors(path, [1, 2, 3, 4, 5, 6, 7, 8], "test")
        np.testing.assert_allclose(got, [0.3, 0.1, 0.1, 0.0, 0.15, 0.1, 0.05, 0.2])


class Inspect(_Fixture):
    def test_each_sample_is_scored_and_the_misclassified_cells_kept(self):
        image = bands(BAND1, BAND2)
        ecd = os.path.join(self.directory, "ml.ecd")
        classify.TrainMaximumLikelihoodClassifier(image, self.training(), ecd)
        # A point sample of class 2 on a western cell, which the classifier calls class 1.
        samples = self.features("check", "POINT", [(2, "east", point(5, 55)), (1, "west", point(15, 55))],
                                [("classvalue", "LONG"), ("classname", "TEXT")])
        out_fc = os.path.join(self.directory, "scored.gpkg")
        wrong = os.path.join(self.directory, "wrong.tif")
        classify.InspectTrainingSamples(image, samples, ecd, out_fc, wrong)
        with arcpy.da.SearchCursor(out_fc, ["classvalue", "accuracy"]) as rows:
            self.assertEqual(sorted(tuple(r) for r in rows), [(1, 1.0), (2, 0.0)])
        got = arcpy.RasterToNumPyArray(arcpy.Raster(wrong), nodata_to_value=-1)
        self.assertEqual(got[0][0], 1)
        self.assertEqual(int((got != -1).sum()), 1)


if __name__ == "__main__":
    unittest.main()
