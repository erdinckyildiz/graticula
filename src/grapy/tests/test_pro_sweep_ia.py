"""arcpy.ia's own names (those grapy does not also build in arcpy.sa) as ArcGIS Pro 3.7 answers them — measured in
ArcGIS Pro 3.7, 2026-11-08, by the Pro sweep (tools/pro-compare/sweep/cases/ia.py, results/ia.md). Each test pins
Pro's answer by an example worked by hand.
"""
import math
import os
import sys
import shutil
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ia, sa  # noqa: E402
from grapy import _mdim  # noqa: E402
from grapy._raster import _Grid  # noqa: E402

ND = -1


def grid(values, cell=10.0, nodata=None):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    try:
        return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0.0, 0.0), cell, cell, nodata)
    finally:
        arcpy.env.outputCoordinateSystem = None


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


def cube_raster(series, mask=None, columns=1):
    data = np.asarray(series, float).reshape(len(series), 1, columns)   # (slices, 1 row, columns)
    times = [_mdim.to_time(f"2020-01-{d + 1:02d}") for d in range(data.shape[0])]
    m = np.zeros(data.shape, bool) if mask is None else np.asarray(mask, bool).reshape(data.shape)
    g = _Grid(0, 10, 10, 10, 1, columns, 3857)
    return _mdim.raster_of(_mdim.Cube(g, [_mdim.Variable("v", ["StdTime"], [{"StdTime": t} for t in times], data, m)]))


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        arcpy.env.workspace = self.directory


class Correlation(_Fixture):
    def test_a_nodata_slice_makes_the_pixel_nodata(self):
        # Pro 3.7 wrote NoData where one slice of six was NoData; grapy left the pair out and answered r of five.
        a = cube_raster([[1, 1], [2, 2], [3, 3], [4, 4], [5, 5]], columns=2,
                        mask=[[0, 0], [0, 0], [0, 0], [0, 1], [0, 0]])
        b = cube_raster([[2, 2], [4, 4], [6, 6], [8, 8], [11, 11]], columns=2)
        got = cells(ia.MultidimensionalRasterCorrelation(a, b))[0]
        self.assertAlmostEqual(got[0], float(np.corrcoef([1, 2, 3, 4, 5], [2, 4, 6, 8, 11])[0, 1]))
        self.assertEqual(got[1], ND)


class Signatures(_Fixture):
    PRO = """# Signatures Produced by ClassSig from
#    Class-Grid __1000001
#    and Stack __1000000

#    Number of selected grids
/*           2
#    Layer-Number    Band-name
/*           1       a.tif\\Band_1
/*           2       b.tif\\Band_1

#  Type   Number of Classes   Number of Layers    Number of Parametric Layers
    1             2                  2                         2
# ===================================================================

#  Class ID     Number of Cells    Class Name
        1               4         1
# Layers             1             2
# Means
             1.000000e+00  2.000000e+00
# Covariance
    1        1.000000e+00  0.000000e+00
    2        0.000000e+00  1.000000e+00
# -------------------------------------------------------------------

#  Class ID     Number of Cells    Class Name
        2               4         2
# Layers             1             2
# Means
             5.000000e+00  6.000000e+00
# Covariance
    1        1.000000e+00  0.000000e+00
    2        0.000000e+00  1.000000e+00
"""

    def test_reads_the_file_pro_writes(self):
        # Pro's layout (spaces as Pro 3.7 writes them, CRLF aside): "#  Class ID" has two spaces, which grapy did not
        # recognise, so it read no class at all.
        path = os.path.join(self.directory, "pro.gsg")
        with open(path, "w", encoding="utf-8") as f:
            f.write(self.PRO)
        a = grid([[1.0, 5.0, 2.0]])
        b = grid([[2.0, 6.0, 5.5]])
        # Equal identity covariances: the nearer mean wins — (1, 2), (5, 6), and (2, 5.5) is nearer (5, 6).
        self.assertEqual(cells(ia.MLClassify(sa.CompositeBand([a, b]), path)), [[1, 2, 2]])

    def test_writes_seven_significant_digits(self):
        # Pro writes 4.061075e-05 where grapy wrote 0.000041: a reflectance covariance kept two digits.
        a = grid([[0.0001, 0.0002, 0.0004, 0.0003, 0.5, 0.5001, 0.5003, 0.5002]])
        b = grid([[0.0002, 0.0001, 0.0003, 0.0005, 0.7, 0.7003, 0.7001, 0.7002]])
        zones = grid([[1, 1, 1, 1, 2, 2, 2, 2]])
        out = os.path.join(self.directory, "s.gsg")
        sa.CreateSignatures([a, b], zones, out)
        with open(out, encoding="utf-8") as f:
            text = f.read()
        # Class 1's mean in band 1 is 0.00025 and its variance (n − 1) 1.6666667e-08.
        self.assertIn("2.500000e-04", text)
        self.assertIn("1.666667e-08", text)


class Spatiotemporal(_Fixture):
    def test_takes_pro_s_six_arguments_and_answers_a_raster(self):
        # Pro 3.7's arcpy takes (in_dataset, variable_field, time_field, temporal_aggregation, cell_size,
        # interpolation_method), as the page's syntax line, and answers a Raster; a seventh argument is a TypeError.
        path = os.path.join(self.directory, "p.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "p.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(path, "temp", "DOUBLE")
        arcpy.management.AddField(path, "when", "TEXT")
        with arcpy.da.InsertCursor(path, ["temp", "when", "SHAPE@XY"]) as rows:
            for value, xy in ((10, (0, 0)), (20, (30, 0))):
                rows.insertRow((value, "2020-01-01", xy))
        r = ia.InterpolateFromSpatiotemporalPoints(path, "temp", "when", "DAILY", 10, "NEAREST_NEIGHBOR")
        self.assertTrue(r.isMultidimensional)
        # Pro's grid: the top-left cell centred on the points' corner, round(30 / 10) + 1 = 4 cells, centres at
        # x = 0, 10, 20, 30.
        self.assertEqual(r.read(nodata_to_value=ND)[0, 0, :, 0].tolist(), [10.0, 10.0, 20.0, 20.0])
        e = r.extent
        self.assertEqual((e.XMin, e.XMax), (-5.0, 35.0))
        with self.assertRaises(TypeError):
            ia.InterpolateFromSpatiotemporalPoints(path, "out.nc", "temp", "when", "DAILY", 10, "MEAN")


class Local(_Fixture):
    def test_percentile_nearest_rounds_the_position_half_down(self):
        # Pro 3.7: NEAREST takes 0-based position (n - 1)·p/100 rounded, a half down. Of 1, 4, 10 at p = 75 the
        # position is 1.5, so 4 (grapy took rank ceil(p·n/100) = 3, 10); at p = 80 it is 1.6, so 10. Of 1, 2, 3, 4 at
        # p = 50 the position is 1.5, so 2.
        three = [grid([[v]]) for v in (1, 4, 10)]
        self.assertEqual(cells(ia.Percentile(three, 75, "NEAREST")), [[4]])
        self.assertEqual(cells(ia.Percentile(three, 80, "NEAREST")), [[10]])
        self.assertEqual(cells(ia.Percentile(three, 40, "NEAREST")), [[4]])
        self.assertEqual(cells(ia.Percentile([grid([[v]]) for v in (4, 1, 3, 2)], 50, "NEAREST")), [[2]])
        self.assertEqual(cells(ia.Percentile([grid([[v]]) for v in (2, 9, 4, 6, 8, 1)], 90, "NEAREST")), [[8]])

    def test_median_of_integers_is_the_lower_middle_value(self):
        # Pro 3.7, arcpy.ia.Median and arcpy.sa.CellStatistics MEDIAN alike: 1, 2, 5, 7 answer 2 as an integer (U8),
        # where grapy answered 3.5; floating point keeps the mean of the two (1.25, 2.25, 5.25, 7.25 answer 3.75).
        ints = [grid([[v]]) for v in (5, 1, 7, 2)]
        out = ia.Median(ints)
        self.assertTrue(out.isInteger)
        self.assertEqual(cells(out), [[2]])
        self.assertEqual(cells(sa.CellStatistics(ints, "MEDIAN")), [[2]])
        self.assertEqual(cells(ia.Median([grid([[v + 0.25]]) for v in (5, 1, 7, 2)])), [[3.75]])

    def test_pansharpen_keeps_the_image_s_integer_type(self):
        # Pro 3.7 on a 16-bit image: Esri takes the multispectral cell under each pan cell and cuts to the integer
        # below. One band of 100 at 10 m, the pan 103 and 104 at 5 m, weight 1 on red: ADJ = pan − 100, so 103 and 104.
        # Simple Mean: (100 + 103)/2 = 101.5 → 101.
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        try:
            ms = arcpy.NumPyArrayToRaster(np.full((3, 1, 1), 100, np.uint16), arcpy.Point(0, 0), 10, 10)
            pan = arcpy.NumPyArrayToRaster(np.array([[103, 104], [103, 104]], np.uint16), arcpy.Point(0, 0), 5, 5)
        finally:
            arcpy.env.outputCoordinateSystem = None
        out = ia.Pansharpen(pan, ms, None, False, [1, 0, 0, 0], "ESRI")
        self.assertEqual(np.asarray(out.getRasterBands(1).read()).dtype, np.uint16)
        self.assertEqual(cells(out.getRasterBands(1)), [[103, 104], [103, 104]])
        self.assertEqual(cells(ia.Pansharpen(pan, ms, None, False, [1, 0, 0, 0], "SimpleMean").getRasterBands(1)),
                         [[101, 102], [101, 102]])

    def test_variety_is_an_integer_of_floating_point_inputs(self):
        # Pro 3.7 answers Variety of three floating-point rasters as U8; grapy answered floating point.
        out = ia.Variety([grid([[1.5, 2.5]]), grid([[1.5, 3.5]]), grid([[4.5, 2.5]])])
        self.assertTrue(out.isInteger)
        self.assertEqual(cells(out), [[2, 2]])


class Change(_Fixture):
    def test_categorical_change_is_encoded_as_pro_encodes_it(self):
        # From classes 2, 3, 4 to classes 1, 2, 3, 4, as Pro 3.7 wrote them. ALL: the pair's place in the 3 × 4 grid
        # (2->1 is 0, 3->3 is 6, 4->4 is 11). CHANGED_PIXELS_ONLY: the place among the nine differing pairs, No Change
        # after them (9). Classvalue: the class for an unchanged pair, else 5 (the largest class, 4, plus one) plus the
        # place among the differing pairs.
        a = grid([[2, 2, 3, 3, 4, 4, 2, 3]])
        b = grid([[2, 1, 3, 1, 1, 4, 3, 2]])
        out = ia.ComputeChangeRaster(a, b, "CATEGORICAL_DIFFERENCE", filter_method="ALL")
        self.assertEqual(cells(out), [[1, 0, 6, 4, 8, 11, 2, 5]])
        self.assertEqual([r[:3] for r in out._rat.rows][:3], [[0, 5, "2->1"], [1, 2, "2"], [2, 6, "2->3"]])
        out = ia.ComputeChangeRaster(a, b, "CATEGORICAL_DIFFERENCE")      # the tool's default, CHANGED_PIXELS_ONLY
        self.assertEqual(cells(out), [[9, 0, 9, 3, 6, 9, 1, 4]])
        self.assertEqual(out._rat.rows[-1][:5], [9, 14, "No Change", "Same", "Same"])
        out = ia.ComputeChangeRaster(a, b, "CATEGORICAL_DIFFERENCE", filter_method="UNCHANGED_PIXELS_ONLY")
        self.assertEqual(cells(out), [[0, 3, 1, 3, 3, 2, 3, 3]])
        self.assertEqual(out._rat.rows[-1][:5], [3, 5, "Changed", "Any", "Any"])
        out = ia.ComputeChangeRaster(a, b, "CATEGORICAL_DIFFERENCE", "2;3", "1;2", "ALL")
        self.assertEqual(cells(out), [[1, 0, 4, 2, 4, 4, 4, 3]])
        self.assertEqual(out._rat.rows[-1][:3], [4, 8, "Other"])
        # The function lists every class it encodes, present or not.
        self.assertEqual(len(ia.ComputeChange(a, b, "CATEGORICAL_DIFFERENCE")._rat.rows), 10)


class Regions(_Fixture):
    def seeds(self, x, y, radius, similarity, fill):
        path = os.path.join(self.directory, "seeds.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "seeds.gpkg", "POINT", spatial_reference=3857)
        for name, kind in (("fill", "LONG"), ("radius", "DOUBLE"), ("sim", "DOUBLE")):
            arcpy.management.AddField(path, name, kind)
        with arcpy.da.InsertCursor(path, ["fill", "radius", "sim", "SHAPE@XY"]) as rows:
            rows.insertRow((fill, radius, similarity, (x, y)))
        return path

    def test_region_grow_bounds_are_strict_and_measured_from_the_seed(self):
        # Pro 3.7 on a ramp of 0 … 8 by column, seed in the middle cell (value 4), radius 40, threshold 2: columns
        # 3 … 5 (2 and 6 are 2 away, not closer), rows 1 … 7 (rows 0 and 8 are 40 away). grapy took both bounds
        # inclusively.
        ramp = grid(np.tile(np.arange(9, dtype=float), (9, 1)))
        out = cells(ia.RegionGrow(ramp, self.seeds(45, 45, 40.0, 2.0, 50), "radius", "sim", "fill"))
        expected = [[float(c) for c in range(9)] for _ in range(9)]
        for r in range(1, 8):
            expected[r][3:6] = [50.0] * 3
        self.assertEqual(out, expected)

    def test_region_grow_compares_with_the_seed_not_the_region_mean(self):
        # 10, 10.95, 10.95, 11.4 with threshold 1: from the seed 10, 11.4 is 1.4 away and stays; against the region's
        # running mean (10.63 once both 10.95 have joined) it is 0.77 away and would join.
        line = grid([[10.0, 10.95, 10.95, 11.4]])
        out = cells(ia.RegionGrow(line, self.seeds(5, 5, 100.0, 1.0, 0), "radius", "sim", "fill"))
        self.assertEqual(out, [[0.0, 0.0, 0.0, 11.4]])


class ObjectAccuracy(_Fixture):
    def test_the_table_is_laid_out_as_pro_writes_it(self):
        # Pro 3.7: the first field is named by min_iou (Min_IoU_is_30_percent) and All_Classes is the first row.
        def boxes(name, rows, fields):
            path = os.path.join(self.directory, f"{name}.gpkg")
            arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POLYGON", spatial_reference=3857)
            for field, kind in fields:
                arcpy.management.AddField(path, field, kind)
            with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
                for *values, (a, b, c, d) in rows:
                    ring = arcpy.Array([arcpy.Point(a, b), arcpy.Point(a, d), arcpy.Point(c, d), arcpy.Point(c, b)])
                    written.insertRow((*values, arcpy.Polygon(ring, 3857)))
            return path
        found = boxes("found", [("car", 0.9, (0, 0, 10, 10))], (("Class", "TEXT"), ("Confidence", "DOUBLE")))
        truth = boxes("truth", [("car", (0, 0, 10, 10))], (("Class", "TEXT"),))
        out = os.path.join(self.directory, "acc.gpkg")
        ia.ComputeAccuracyForObjectDetection(found, truth, out, None, "Class", "Class", 0.3)
        names = [f.name for f in arcpy.ListFields(out)]
        self.assertIn("Min_IoU_is_30_percent", names)
        with arcpy.da.SearchCursor(out, ["Min_IoU_is_30_percent", "True_Positive"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("All_Classes", 1), ("car", 1)])


if __name__ == "__main__":
    unittest.main()
