"""grapy.sa against answers worked out by hand — ADR-224.

The surface tests use a plane rising 10 m per 10 m cell to the east (z = x), so by Esri's published Horn formulas:
dz/dx = ((c + 2f + i) - (a + 2d + g)) / (8 · 10) = (40 + 40 − 0 … ) worked out below, slope 45°, aspect 270° (downhill
to the west), and a hillshade from azimuth 315°, altitude 45° of 255 · (cos 45° · cos 45° + sin 45° · sin 45° ·
cos(135° − 180°)) = 255 · (0.5 + 0.5 · 0.70711) = 217.66, rounded to 218.
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import sa  # noqa: E402


def grid(values, cell=10.0, left=0.0, top=None, nodata=None, srid=3857):
    """A raster from rows written north to south, its top-left corner at (left, top)."""
    array = np.array(values)
    rows = array.shape[0]
    top = rows * cell if top is None else top
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(srid)
    return arcpy.NumPyArrayToRaster(array, arcpy.Point(left, top - rows * cell), cell, cell, nodata)


def cells(raster, nodata=-1):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


class MapAlgebra(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def tearDown(self):
        arcpy.env.mask = None
        arcpy.env.outputCoordinateSystem = None

    def test_operators_keep_nodata_and_integer_type(self):
        a = grid([[1, 2], [3, -9]], nodata=-9)
        b = grid([[10, 20], [30, 40]])
        added = a + b
        self.assertTrue(added.isInteger)
        self.assertEqual(cells(added), [[11, 22], [33, -1]])
        self.assertEqual(cells(a * 2 - 1), [[1, 3], [5, -1]])
        self.assertEqual(cells(a > 1), [[0, 1], [1, -1]])
        self.assertEqual(cells((a > 1) & (b < 30)), [[0, 1], [0, -1]])
        self.assertEqual(cells(~(a > 1)), [[1, 0], [0, -1]])

    def test_division_of_integers_is_integer_and_a_zero_divisor_is_nodata(self):
        # Divide's page: "If both inputs are integers, an integer division is performed … 3 divided by 2 is 1."
        a = grid([[1, 3], [4, 8]])
        b = grid([[2, 0], [4, 3]])
        divided = a / b
        self.assertTrue(divided.isInteger)
        self.assertEqual(cells(divided), [[0, -1], [1, 2]])
        self.assertEqual(cells(sa.Divide(grid([[-3, 3]]), 2)), [[-1, 1]])
        floating = sa.Float(a) / b
        self.assertFalse(floating.isInteger)
        self.assertEqual(cells(floating), [[0.5, -1], [1.0, 8 / 3]])
        # Int truncates toward zero, and a value past the 32-bit range is NoData.
        self.assertEqual(cells(sa.Int(grid([[2.7, -2.7, 3e10]]))), [[2, -2, -1]])
        self.assertEqual(cells(a // b), [[0, -1], [1, 2]])
        self.assertEqual(cells(sa.Mod(a, b)), [[1, -1], [0, 2]])

    def test_functions_and_conditions(self):
        a = grid([[1.0, 4.0], [-1.0, 0.0]])
        self.assertEqual(cells(sa.Sqrt(a)), [[1.0, 2.0], [-1, 0.0]])
        self.assertEqual(cells(sa.Ln(sa.Exp(a))), [[1.0, 4.0], [-1.0, 0.0]])
        self.assertEqual(cells(sa.Con(a > 0, 100, 200)), [[100, 100], [200, 200]])
        self.assertEqual(cells(sa.Con(a, 7, where_clause="VALUE >= 1")), [[7, 7], [-1, -1]])
        self.assertEqual(cells(sa.SetNull(a, a, "VALUE < 0")), [[1.0, 4.0], [-1, 0.0]])
        self.assertEqual(cells(sa.IsNull(sa.SetNull(a < 0, a))), [[0, 0], [1, 0]])
        self.assertEqual(cells(sa.Abs(a)), [[1.0, 4.0], [1.0, 0.0]])

    def test_rasters_combine_over_the_intersection_of_their_extents(self):
        # b sits one cell east and one south of a: they share a's middle-right 2×2 block.
        a = grid([[1, 2, 3], [4, 5, 6], [7, 8, 9]], left=0, top=30)
        b = grid([[10, 20, 30], [40, 50, 60], [70, 80, 90]], left=10, top=20)
        summed = a + b
        self.assertEqual((summed.width, summed.height), (2, 2))
        self.assertEqual((summed.extent.XMin, summed.extent.YMax), (10.0, 20.0))
        self.assertEqual(cells(summed), [[15, 26], [48, 59]])
        coarse = grid([[1]], cell=20)
        with self.assertRaises(NotImplementedError):
            a + coarse

    def test_an_environment_it_does_not_honour_is_refused(self):
        arcpy.env.mask = "somewhere.gpkg"
        with self.assertRaises(NotImplementedError):
            sa.Slope(grid([[1, 2], [3, 4]]))

    def test_save_and_reopen_keep_values_nodata_and_reference(self):
        a = grid([[1.5, -9.0], [2.5, 3.5]], nodata=-9.0, srid=32635)
        path = a.save("kept")
        self.assertTrue(path.endswith("kept.tif"))
        back = arcpy.Raster(path)
        self.assertEqual(cells(back), [[1.5, -1], [2.5, 3.5]])
        self.assertEqual(back.spatialReference.factoryCode, 32635)
        self.assertEqual((back.meanCellWidth, back.extent.XMin, back.extent.YMax), (10.0, 0.0, 20.0))
        self.assertEqual((back.minimum, back.maximum), (1.5, 3.5))
        self.assertAlmostEqual(back.mean, 2.5)


class Surface(unittest.TestCase):
    def setUp(self):
        # z = x: 0, 10, 20, 30 across, the same in every row.
        self.plane = grid([[0, 10, 20, 30]] * 4)

    def test_slope_of_a_45_degree_plane_and_at_its_edge(self):
        slope = cells(sa.Slope(self.plane))
        self.assertAlmostEqual(slope[1][1], 45.0, places=4)
        self.assertAlmostEqual(slope[2][2], 45.0, places=4)
        # The western column: a, d, g are off the grid and take the centre's z, so
        # dz/dx = (4 · (z+10) − 4 · z) / 80 = 0.5 and the slope is atan(0.5) = 26.565°.
        self.assertAlmostEqual(slope[1][0], math.degrees(math.atan(0.5)), places=4)
        self.assertAlmostEqual(cells(sa.Slope(self.plane, "PERCENT_RISE"))[1][1], 100.0, places=3)
        self.assertAlmostEqual(cells(sa.Slope(self.plane, "DEGREE", 0.5))[1][1], math.degrees(math.atan(0.5)), places=4)

    def test_aspect_faces_downhill_and_flat_is_minus_one(self):
        self.assertAlmostEqual(cells(sa.Aspect(self.plane))[1][1], 270.0, places=4)
        # z = −y·…: rising to the north (rows written north first), so downhill faces south, 180°.
        north = grid([[30] * 3, [20] * 3, [10] * 3, [0] * 3])
        self.assertAlmostEqual(cells(sa.Aspect(north))[1][1], 180.0, places=4)
        self.assertEqual(cells(sa.Aspect(grid([[5] * 3] * 3)))[1][1], -1.0)

    def test_hillshade_by_the_published_formula(self):
        self.assertEqual(cells(sa.Hillshade(self.plane))[1][1], 218)
        # Flat ground: 255 · cos(45°) = 180.31.
        self.assertEqual(cells(sa.Hillshade(grid([[5] * 3] * 3)))[1][1], 180)
        with self.assertRaises(NotImplementedError):
            sa.Hillshade(self.plane, model_shadows="SHADOWS")


class Neighbourhoods(unittest.TestCase):
    def test_focal_statistics_over_a_rectangle_and_a_circle(self):
        a = grid([[1, 2, 3], [4, 5, 6], [7, 8, 9]])
        mean = cells(sa.FocalStatistics(a, sa.NbrRectangle(3, 3, "CELL"), "MEAN"))
        self.assertEqual(mean[1][1], 5.0)
        self.assertEqual(mean[0][0], (1 + 2 + 4 + 5) / 4)
        self.assertEqual(cells(sa.FocalStatistics(a, sa.NbrRectangle(3, 3, "CELL"), "SUM"))[0][0], 12)
        self.assertEqual(cells(sa.FocalStatistics(a, "Rectangle 3 3 CELL", "MAXIMUM"))[0][0], 5)
        # A circle of radius 1 cell: the centre and its four edge neighbours.
        self.assertEqual(cells(sa.FocalStatistics(a, sa.NbrCircle(1, "CELL"), "SUM"))[1][1], 5 + 2 + 4 + 6 + 8)
        with_gap = grid([[1, 2], [3, -9]], nodata=-9)
        self.assertEqual(cells(sa.FocalStatistics(with_gap, statistics_type="SUM"))[0][0], 6)
        self.assertEqual(cells(sa.FocalStatistics(with_gap, statistics_type="SUM", ignore_nodata="NODATA"))[0][0], -1)
        with self.assertRaises(NotImplementedError):
            sa.FocalStatistics(a, sa.NbrRectangle(2, 2, "CELL"))

    def test_block_and_cell_statistics(self):
        a = grid([[1, 2, 3, 4], [5, 6, 7, 8]])
        self.assertEqual(cells(sa.BlockStatistics(a, sa.NbrRectangle(2, 2, "CELL"), "SUM")), [[14, 14, 22, 22], [14, 14, 22, 22]])
        b = grid([[10, -9, 30, 40], [50, 60, 70, 80]], nodata=-9)
        self.assertEqual(cells(sa.CellStatistics([a, b, 100], "MAXIMUM"))[0][:2], [100, 100])
        self.assertEqual(cells(sa.CellStatistics([a, b], "SUM"))[0][:2], [11, 2])
        self.assertEqual(cells(sa.CellStatistics([a, b], "SUM", "NODATA"))[0][:2], [11, -1])
        self.assertEqual(cells(sa.HighestPosition([a, b]))[0], [2, -1, 2, 2])


class ReclassAndZones(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def test_reclassify_by_value_and_by_range(self):
        a = grid([[1, 2, 3], [4, 5, -9]], nodata=-9)
        self.assertEqual(cells(sa.Reclassify(a, "Value", sa.RemapValue([[1, 10], [2, 20]]))), [[10, 20, 3], [4, 5, -1]])
        self.assertEqual(cells(sa.Reclassify(a, "Value", sa.RemapValue([[1, 10]]), "NODATA")), [[10, -1, -1], [-1, -1, -1]])
        # Ranges include both ends; 3 lies on the boundary of the first two rows and takes the first.
        ranged = sa.Reclassify(a, "Value", sa.RemapRange([[1, 3, 1], [3, 5, 2], ["NODATA", "NODATA", 0]]))
        self.assertEqual(cells(ranged), [[1, 1, 1], [2, 2, 0]])
        self.assertEqual(cells(sa.Reclassify(a, "Value", "1 2 100;3 5 200")), [[100, 100, 200], [200, 200, -1]])
        self.assertEqual(cells(sa.Reclassify(a, "Value", sa.RemapValue([["NODATA", 0]]))), [[1, 2, 3], [4, 5, 0]])

    def test_slice_into_equal_intervals(self):
        a = grid([[0, 1, 2, 3], [4, 5, 6, 7]])
        self.assertEqual(cells(sa.Slice(a, 4)), [[1, 1, 2, 2], [3, 3, 4, 4]])

    def test_zonal_statistics_from_a_zone_raster_and_from_polygons(self):
        zones = grid([[1, 1, 2], [1, 2, 2]])
        values = grid([[10, 20, 30], [30, 50, 70]])
        self.assertEqual(cells(sa.ZonalStatistics(zones, "Value", values, "MEAN")), [[20.0, 20.0, 50.0], [20.0, 50.0, 50.0]])
        self.assertEqual(cells(sa.ZonalStatistics(zones, "Value", values, "SUM")), [[60, 60, 150], [60, 150, 150]])
        # Two polygons on the 3×2 grid of 10 m cells: the western column (x 0–10) and the rest.
        path = os.path.join(self.directory, "z.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "z.gpkg", "POLYGON", spatial_reference=3857)
        arcpy.management.AddField(path, "name", "TEXT")
        with arcpy.da.InsertCursor(path, ["name", "SHAPE@"]) as rows:
            for name, (x0, x1) in (("west", (0, 10)), ("east", (10, 30))):
                rows.insertRow((name, arcpy.Polygon(arcpy.Array([arcpy.Point(x0, 0), arcpy.Point(x1, 0),
                                                                 arcpy.Point(x1, 20), arcpy.Point(x0, 20)]), 3857)))
        table = sa.ZonalStatisticsAsTable(path, "name", values, os.path.join(self.directory, "t.gpkg")).getOutput(0)
        with arcpy.da.SearchCursor(table, ["name", "COUNT", "AREA", "MIN", "MAX", "MEAN", "SUM"]) as rows:
            got = sorted(tuple(r) for r in rows)
        self.assertEqual(got, [("east", 4, 400.0, 20.0, 70.0, 42.5, 170.0), ("west", 2, 200.0, 10.0, 30.0, 20.0, 40.0)])


class DistanceAndExtraction(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def test_euclidean_distance_from_source_cells(self):
        source = grid([[-9, -9, -9], [-9, 1, -9], [-9, -9, -9]], nodata=-9)
        distance = cells(sa.EucDistance(source))
        self.assertAlmostEqual(distance[1][1], 0.0)
        self.assertAlmostEqual(distance[0][1], 10.0, places=4)
        self.assertAlmostEqual(distance[0][0], math.hypot(10, 10), places=4)
        self.assertEqual(cells(sa.EucDistance(source, 10))[0][0], -1)

    def test_extract_by_mask_attributes_and_to_points(self):
        a = grid([[1, 2], [3, 4]])
        mask = grid([[1, -9], [-9, 1]], nodata=-9)
        self.assertEqual(cells(sa.ExtractByMask(a, mask)), [[1, -1], [-1, 4]])
        self.assertEqual(cells(sa.ExtractByMask(a, mask, "OUTSIDE")), [[-1, 2], [3, -1]])
        self.assertEqual(cells(sa.ExtractByAttributes(a, "VALUE > 1 AND VALUE < 4")), [[-1, 2], [3, -1]])
        path = os.path.join(self.directory, "p.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "p.gpkg", "POINT", spatial_reference=3857)
        with arcpy.da.InsertCursor(path, ["SHAPE@XY"]) as rows:
            for xy in ((5, 15), (15, 5), (50, 50)):
                rows.insertRow((xy,))
        out = sa.ExtractValuesToPoints(path, a, os.path.join(self.directory, "v.gpkg")).getOutput(0)
        with arcpy.da.SearchCursor(out, ["RASTERVALU"]) as rows:
            self.assertEqual([r[0] for r in rows], [1.0, 4.0, -9999.0])
        # Bilinear at the very centre of the grid: the mean of the four cell centres around it.
        sa.ExtractMultiValuesToPoints(path, [[a, "elev"]], "BILINEAR")
        with arcpy.da.SearchCursor(path, ["elev", "SHAPE@XY"]) as rows:
            got = list(rows)
        self.assertEqual(got[2][0], None)
        arcpy.management.Delete(path)
        arcpy.management.CreateFeatureclass(self.directory, "p.gpkg", "POINT", spatial_reference=3857)
        with arcpy.da.InsertCursor(path, ["SHAPE@XY"]) as rows:
            rows.insertRow(((10, 10),))
        sa.ExtractMultiValuesToPoints(path, [[a, "elev"]], "BILINEAR")
        with arcpy.da.SearchCursor(path, ["elev"]) as rows:
            self.assertEqual([r[0] for r in rows], [2.5])

    def test_constant_raster_and_raster_calculator(self):
        constant = sa.CreateConstantRaster(7, "INTEGER", 10, arcpy.Extent(0, 0, 30, 20))
        self.assertEqual((constant.width, constant.height, cells(constant)[0][0]), (3, 2, 7))
        a = grid([[1, 2], [3, 4]])
        self.assertEqual(cells(sa.RasterCalculator([a], ["dem"], "Con(dem > 2, dem * 10, 0)")), [[0, 0], [30, 40]])


if __name__ == "__main__":
    unittest.main()
