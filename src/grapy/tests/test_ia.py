"""grapy.ia's own functions against answers worked out by hand — ADR-224.

A four-band raster of two cells. Cell 1 holds band 1 = 0.1, band 2 = 0.2, band 3 = 0.3, band 4 = 0.5, so with NIR as
band 4 and red as band 3, NDVI is (0.5 − 0.3)/(0.5 + 0.3) = 0.25. Cell 2's bands 3 and 4 are both 0, so any index that
divides by their sum is NoData there.
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ia  # noqa: E402

ND = -1


def grid(values, cell=10.0, nodata=None, left=0.0):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(left, 0.0), cell, cell, nodata)


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


class ImageAnalyst(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None
        self.bands = ia.CompositeBand([grid([[0.1, 0.2]]), grid([[0.2, 0.2]]), grid([[0.3, 0.0]]), grid([[0.5, 0.0]])])

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def first(self, raster):
        return cells(raster)[0][0]

    def test_composite_band_saves_and_reopens_as_bands(self):
        self.assertEqual(self.bands.bandCount, 4)
        path = self.bands.save(os.path.join(self.directory, "four.tif"))
        self.assertEqual(cells(ia.NDVI(path)), [[0.25, ND]])
        # A Raster opens all four bands; a single-band tool refuses it by name.
        self.assertEqual(arcpy.Raster(path).bandCount, 4)
        with self.assertRaises(arcpy.NotBuiltError):
            ia.Slope(path)
        self.assertAlmostEqual(self.first(ia.ExtractBand(self.bands, [3])), 0.3, places=6)
        self.assertEqual(ia.ExtractBand(path, [4, 1]).bandCount, 2)
        with self.assertRaises(arcpy.NotBuiltError):
            ia.ExtractBand(self.bands, [9])

    def test_vegetation_and_water_indexes_follow_their_formulas(self):
        r = self.bands
        checks = [
            (ia.NDVI(r), 0.25),
            (ia.SAVI(r, 4, 3, 0.5), 0.2 / 1.3 * 1.5),
            (ia.EVI(r, 4, 3, 1), 2.5 * 0.2 / (0.5 + 1.8 - 0.75 + 1)),
            (ia.MSAVI(r, 4, 3), 0.5 * (3 - math.sqrt(4 - 1.6))),
            (ia.NDWI(r, 4, 2), (0.2 - 0.5) / 0.7),
            (ia.NBR(r, 3, 4), 0.25),
            (ia.BAI(r, 3, 4), 1 / (0.2 ** 2 + 0.44 ** 2)),
            (ia.CIg(r, 4, 2), 1.5),
            (ia.CIre(r, 4, 3), 0.5 / 0.3 - 1),
            (ia.ClayMinerals(r, 4, 3), 0.5 / 0.3),
            (ia.FerrousMinerals(r, 4, 3), 0.5 / 0.3),
            (ia.GEMI(r, 4, 3), (lambda eta: eta * (1 - 0.25 * eta) - 0.175 / 0.7)((0.32 + 0.75 + 0.15) / 1.3)),
            (ia.IronOxide(r, 3, 1), 3.0),
            (ia.NDBI(r, 4, 3), 0.25),
            (ia.NDMI(r, 4, 3), 0.25),
            (ia.NDSI(r, 2, 4), (0.2 - 0.5) / 0.7),
            (ia.NDVIre(r, 4, 3), 0.25),
            (ia.PVI(r, 4, 3), (0.5 - 0.09 - 0.5) / math.sqrt(1.09)),
            (ia.RTVICore(r, 4, 3, 2), 17.0),
            (ia.SR(r, 4, 3), 0.5 / 0.3),
            (ia.SRre(r, 4, 3), 0.5 / 0.3),
            (ia.TSAVI(r, 4, 3), 0.33 * (0.5 - 0.099 - 0.5) / (0.25 + 0.3 - 0.165 + 1.5 * 1.1089)),
            (ia.VARI(r, 3, 2, 1), -0.1 / 0.4),
        ]
        for raster, expected in checks:
            self.assertAlmostEqual(self.first(raster), expected, places=5)
        self.assertEqual(cells(ia.NDVI(r))[0][1], ND)
        with self.assertRaises(arcpy.NotBuiltError):
            ia.FerrousMinerals(r, 4)  # the page gives no default for the NIR band
        with self.assertRaises(ValueError):
            ia.SAVI(r)  # the page's defaults are bands 7 and 6, which a four-band raster does not have

    def test_band_arithmetic_by_method_code(self):
        self.assertAlmostEqual(self.first(ia.BandArithmetic(self.bands, "4 3", 1)), 0.25, places=6)
        self.assertAlmostEqual(self.first(ia.BandArithmetic(self.bands, "4 3 0.5", 2)), 0.2 / 1.3 * 1.5, places=6)
        # WNDWI with green 0.2, NIR 0.5, SWIR 0.3 and α 0.5: (0.2 − 0.25 − 0.15) / (0.2 + 0.25 + 0.15).
        self.assertAlmostEqual(self.first(ia.BandArithmetic(self.bands, "2 4 3", 23)), -0.2 / 0.6, places=6)
        for method in (0, 8, 14, 18):
            with self.assertRaises(arcpy.NotBuiltError):
                ia.BandArithmetic(self.bands, "4 3", method)

    def test_local_statistics_across_rasters(self):
        a, b, c = grid([[1, 5]]), grid([[3, -9]], nodata=-9), grid([[2, 2]])
        self.assertEqual(cells(ia.Max([a, b, c])), [[3, ND]])
        self.assertEqual(cells(ia.Max([a, b, c], ignore_nodata=True)), [[3, 5]])
        self.assertEqual(cells(ia.Min([a, b, c]))[0][0], 1)
        self.assertEqual(cells(ia.Mean([a, b, c]))[0][0], 2.0)
        self.assertEqual(cells(ia.Median([a, b, c]))[0][0], 2.0)
        self.assertEqual(cells(ia.Sum([a, b, c]))[0][0], 6)
        self.assertEqual(cells(ia.Range([a, b, c]))[0][0], 2)
        self.assertEqual(cells(ia.Variety([a, b, c]))[0][0], 3)
        self.assertAlmostEqual(cells(ia.StdDev([a, b, c]))[0][0], math.sqrt(2 / 3))
        self.assertEqual(cells(ia.Majority([a, a, c]))[0][0], 1)
        self.assertEqual(cells(ia.Minority([a, a, c]))[0][0], 2)
        shifted = grid([[1, 1]], left=10)
        with self.assertRaises(arcpy.NotBuiltError):
            ia.Max([a, shifted])
        self.assertEqual(cells(ia.Max([a, shifted], "IntersectionOf")), [[5]])

    def test_arithmetic_remap_and_square_root(self):
        a, c = grid([[1, 5]]), grid([[2, 2]])
        self.assertEqual(cells(ia.Arithmetic(a, c, "Minus")), [[-1, 3]])
        self.assertEqual(cells(ia.Arithmetic(a, c, "Multiply")), [[2, 10]])
        with self.assertRaises(arcpy.NotBuiltError):
            ia.Arithmetic(a, c, "Mode")
        values = grid([[1, 2], [5, -1]])
        self.assertEqual(cells(ia.Remap(values, [0, 2, 2, 5], [10, 20]), nodata=99), [[10, 20], [5, -1]])
        self.assertEqual(cells(ia.Remap(values, [0, 2, 2, 5], [10, 20], allow_unmatched=False), nodata=99), [[10, 20], [99, 99]])
        self.assertEqual(cells(ia.Remap(values, [0, 2, 2, 5], [10, 20], [0, 2], False, 0), nodata=99), [[99, 20], [0, 0]])
        self.assertEqual(cells(ia.SquareRoot(grid([[4.0, -1.0]]))), [[2.0, ND]])

    def test_gvitm_and_subset_bands(self):
        seven = ia.CompositeBand([grid([[float(k)]]) for k in range(1, 8)])
        expected = -0.2848 * 1 - 0.2435 * 2 - 0.5436 * 3 + 0.7243 * 4 + 0.084 * 5 - 1.18 * 7
        self.assertAlmostEqual(self.first(ia.GVITM(seven)), expected, places=5)
        subset = ia.SubsetBands(self.bands, bands="4 1-2")
        # Kept in the input's order: bands 1, 2 and 4.
        self.assertEqual(subset.bandCount, 3)
        self.assertAlmostEqual(cells(subset._band(3))[0][0], 0.5, places=6)
        with self.assertRaises(arcpy.NotBuiltError):
            ia.SubsetBands(self.bands, "BY_WAVELENGTHS", "500")

    def test_refused_names_say_why(self):
        for name, words in (("DetectObjectsUsingText", "404"), ("InterpolateRasterByDimension", "no Python signature"),
                            ("TrainRandomTreesClassifier", "decision-tree learner"),
                            ("FloatDivide", "404")):
            with self.assertRaises(arcpy.NotBuiltError) as raised:
                getattr(ia, name)("a")
            self.assertIn(words, str(raised.exception))


if __name__ == "__main__":
    unittest.main()
