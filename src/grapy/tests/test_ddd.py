"""grapy.ddd against answers worked out by hand — ADR-224.

Rasters are written north to south with 10-unit cells on EPSG:3857, so every cell centre, distance and value below is
arithmetic on the numbers in the test.
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
import grapy.ddd as ddd  # noqa: E402


def grid(values, cell=10.0, left=0.0, nodata=None):
    array = np.array(values)
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(array, arcpy.Point(left, 0.0), cell, cell, nodata)


def cells(path, nodata=-1):
    return arcpy.RasterToNumPyArray(arcpy.Raster(path), nodata_to_value=nodata).tolist()


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


class Ddd(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None
        # z = x: a plane rising 10 per 10-unit cell to the east — 45° of slope facing west.
        self.plane = grid([[0, 10, 20], [0, 10, 20], [0, 10, 20]])

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def out(self, name):
        return os.path.join(self.directory, name)

    def points(self, name, rows):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(path, "z", "DOUBLE")
        with arcpy.da.InsertCursor(path, ["z", "SHAPE@"]) as written:
            for x, y, z in rows:
                written.insertRow((z, point(x, y)))
        return path

    # ---- the sa computations, saved

    def test_surface_tools_save_what_arcpy_sa_answers(self):
        slope = ddd.Slope(self.plane, self.out("slope.tif")).getOutput(0)
        self.assertTrue(os.path.exists(slope))
        self.assertAlmostEqual(cells(slope)[1][1], 45.0, places=4)
        self.assertAlmostEqual(cells(ddd.Slope(self.plane, self.out("pr.tif"), "PERCENT_RISE").getOutput(0))[1][1], 100.0, places=3)
        self.assertAlmostEqual(cells(ddd.Aspect(self.plane, self.out("aspect.tif")).getOutput(0))[1][1], 270.0, places=4)
        self.assertEqual(cells(ddd.HillShade(self.plane, self.out("hs.tif")).getOutput(0))[1][1], 218)

    def test_mathematics_and_integer_division(self):
        a = grid([[3, -3], [4, 1]])
        b = grid([[2, 2], [0, 1]])
        self.assertEqual(cells(ddd.Plus(a, b, self.out("p.tif")).getOutput(0)), [[5, -1], [4, 2]])
        self.assertEqual(cells(ddd.Minus(a, 1, self.out("m.tif")).getOutput(0)), [[2, -4], [3, 0]])
        self.assertEqual(cells(ddd.Times(a, b, self.out("t.tif")).getOutput(0)), [[6, -6], [0, 1]])
        divided = arcpy.Raster(ddd.Divide(a, b, self.out("d.tif")).getOutput(0))
        # Two integers: 3/2 is 1, −3/2 is −1 (toward zero), 4/0 is NoData.
        self.assertTrue(divided.isInteger)
        self.assertEqual(cells(divided, nodata=99), [[1, -1], [99, 1]])
        self.assertEqual(cells(ddd.Divide(ddd.Float(a, self.out("f.tif")).getOutput(0), b, self.out("df.tif")).getOutput(0),
                               nodata=99), [[1.5, -1.5], [99, 1.0]])
        self.assertEqual(cells(ddd.Int(grid([[1.7, -1.7]]), self.out("i.tif")).getOutput(0)), [[1, -1]])

    def test_reclassify_slice_and_reclass_by_table(self):
        values = grid([[1, 2], [3, 4]])
        self.assertEqual(cells(ddd.Reclassify(values, "VALUE", "1 2 10;3 4 20", self.out("r.tif")).getOutput(0)),
                         [[10, 10], [20, 20]])
        with self.assertRaises(arcpy.NotBuiltError):
            ddd.Reclassify(grid([[1.5, 2.5]]), "VALUE", "1 2 10", self.out("rf.tif"))
        # Range 1–4 in two equal intervals of 1.5: 1 and 2 in the first, 3 and 4 in the second.
        self.assertEqual(cells(ddd.Slice(values, self.out("s.tif"), 2).getOutput(0)), [[1, 1], [2, 2]])
        # With 4 gone the range is 1–3 in intervals of 1: 1 in the first zone, 2 and 3 in the second.
        holed = grid([[1, 2], [3, -9]], nodata=-9)
        self.assertEqual(cells(ddd.Slice(holed, self.out("s0.tif"), 2, nodata_to_value=0).getOutput(0), nodata=99),
                         [[1, 2], [2, 0]])
        table = os.path.join(self.directory, "remap.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "remap.gpkg", "POINT", spatial_reference=3857)
        for field in ("low", "high", "new"):
            arcpy.management.AddField(table, field, "DOUBLE")
        with arcpy.da.InsertCursor(table, ["low", "high", "new", "SHAPE@"]) as written:
            written.insertRow((1, 1, 5, None))
            written.insertRow((2, 3, 6, None))
        reclassed = ddd.ReclassByTable(values, table, "low", "high", "new", self.out("rt.tif"), "NODATA").getOutput(0)
        self.assertEqual(cells(reclassed, nodata=99), [[5, 6], [6, 99]])

    # ---- surfaces from points

    def test_idw_weights_by_inverse_square_distance(self):
        points = self.points("p", [(0, 0, 0.0), (100, 100, 100.0)])
        out = ddd.Idw(points, "z", self.out("idw.tif"), 50).getOutput(0)
        # Centres (25,75) (75,75) / (25,25) (75,25). At (25,25): d² is 1250 to the first point and 11250 to the second,
        # so 100·(1/11250)/(1/1250 + 1/11250) = 10; (75,75) mirrors it at 90; the other two are equidistant: 50.
        got = cells(out)
        self.assertAlmostEqual(got[0][0], 50.0, places=4)
        self.assertAlmostEqual(got[0][1], 90.0, places=4)
        self.assertAlmostEqual(got[1][0], 10.0, places=4)
        self.assertAlmostEqual(got[1][1], 50.0, places=4)
        # A fixed radius of 40 reaches only the point 35.36 away; the cells 79.06 from both points find none.
        fixed = cells(ddd.Idw(points, "z", self.out("fixed.tif"), 50, 2, "FIXED 40 0").getOutput(0), nodata=-1)
        self.assertEqual([[round(v, 4) for v in r] for r in fixed], [[-1, 100.0], [0.0, -1]])
        exact = self.points("e", [(0, 0, 0.0), (100, 100, 100.0), (25, 75, 7.0)])
        self.assertAlmostEqual(cells(ddd.Idw(exact, "z", self.out("exact.tif"), 50).getOutput(0))[0][0], 7.0, places=4)
        with self.assertRaises(arcpy.NotBuiltError):
            ddd.Idw(points, "z", self.out("barrier.tif"), 50, in_barrier_polyline_features=points)

    def test_trend_fits_a_least_squares_polynomial(self):
        plane = self.points("pl", [(x, y, 2 * x + 3 * y + 1) for x in (0, 20) for y in (0, 20)])
        got = cells(ddd.Trend(plane, "z", self.out("trend.tif"), 10).getOutput(0))
        # Centres (5,15) (15,15) / (5,5) (15,5) on z = 2x + 3y + 1.
        for row, expected in zip(got, [[56, 76], [26, 46]]):
            for value, wanted in zip(row, expected):
                self.assertAlmostEqual(value, wanted, places=3)
        square = self.points("sq", [(x, y, x * x) for x in (0, 10, 20) for y in (0, 10, 20)])
        self.assertAlmostEqual(cells(ddd.Trend(square, "z", self.out("t2.tif"), 10, 2).getOutput(0))[1][0], 25.0, places=3)
        with self.assertRaises(arcpy.NotBuiltError):
            ddd.Trend(plane, "z", self.out("lg.tif"), 10, 1, "LOGISTIC")

    # ---- surface analysis

    def test_curvature_is_minus_two_hundred_times_the_second_differences(self):
        xs = [5 + 10 * c for c in range(5)]
        bowl = grid([[x * x for x in xs] for _ in range(5)])
        # Along a row (x−10)² + (x+10)² over 2, less x², is 100; over L² = 100 that is D = 1; E = 0; −2·(1 + 0)·100.
        got = cells(ddd.Curvature(bowl, self.out("c.tif")).getOutput(0))
        self.assertAlmostEqual(got[2][2], -200.0, places=3)
        # The bowl's profile output, an equivalent, is written beside it.
        ddd.Curvature(bowl, self.out("c2.tif"), 1, self.out("profile.tif"))
        self.assertTrue(os.path.exists(self.out("profile.tif")))

    def test_contours_run_through_the_interpolated_triangles(self):
        # Centres x = 5 and 15, y = 15 and 5; values 0 west, 10 east. The block's centre (10,10) is 5.
        ramp = grid([[0.0, 10.0], [0.0, 10.0]])
        out = ddd.Contour(ramp, self.out("contours.gpkg"), 5).getOutput(0)
        with arcpy.da.SearchCursor(out, ["Contour", "SHAPE@"]) as rows:
            got = sorted((r[0], tuple(sorted(r[1]._shape.coords))) for r in rows)
        # 5 runs north–south through the middle; 10 lies on the eastern centres (a value equal to a contour counts as
        # above it, so 0 on the western centres draws nothing).
        self.assertEqual(got, [(5.0, ((10.0, 5.0), (10.0, 10.0), (10.0, 15.0))), (10.0, ((15.0, 5.0), (15.0, 15.0)))])
        listed = ddd.ContourList(ramp, self.out("list.gpkg"), "2.5;7.5").getOutput(0)
        with arcpy.da.SearchCursor(listed, ["Contour", "SHAPE@LENGTH"]) as rows:
            self.assertEqual(sorted(tuple(r) for r in rows), [(2.5, 10.0), (7.5, 10.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            ddd.Contour(ramp, self.out("poly.gpkg"), 5, contour_type="CONTOUR_POLYGON")

    def test_add_surface_information_samples_bilinearly(self):
        ramp = grid([[0.0, 10.0], [0.0, 10.0]])
        surface = ramp.save(self.out("ramp.tif"))
        points = os.path.join(self.directory, "wells.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "wells.gpkg", "POINT", spatial_reference=3857)
        with arcpy.da.InsertCursor(points, ["SHAPE@"]) as written:
            written.insertRow((point(10, 10),))
            written.insertRow((point(12.5, 10),))
        ddd.AddSurfaceInformation(points, surface, "Z", z_factor=2)
        with arcpy.da.SearchCursor(points, ["Z"]) as rows:
            got = [r[0] for r in rows]
        # Halfway between the 0 and 10 centres is 5, doubled; three quarters of the way is 7.5, doubled.
        self.assertEqual(got, [10.0, 15.0])
        with self.assertRaises(arcpy.NotBuiltError):
            ddd.AddSurfaceInformation(points, surface, "Z_MEAN")

    def test_tools_not_built_say_why(self):
        for name, words in (("CreateTerrain", "terrain dataset"), ("RasterDomain", "corner"),
                            ("TopoToRaster", "not specified")):
            with self.assertRaises(arcpy.NotBuiltError) as raised:
                getattr(ddd, name)("a", "b", "c")
            self.assertIn(words, str(raised.exception))


if __name__ == "__main__":
    unittest.main()
