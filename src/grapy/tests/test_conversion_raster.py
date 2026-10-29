"""grapy.conversion between rasters and features, against answers worked out by hand — ADR-224."""
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


def grid(values, cell=10.0, nodata=None):
    array = np.array(values)
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(array, arcpy.Point(0, 0), cell, cell, nodata)


class RasterAndFeatures(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def out(self, name):
        return os.path.join(self.directory, name)

    def test_raster_to_polygon_follows_cell_edges(self):
        # 1 1 2      Value 1 is an L of three 10 m cells (300 m²), value 2 four cells joined edge to edge
        # 1 2 2      (400 m²), value 3 one cell (100 m²); the NoData cell yields nothing.
        # 3 -9 2
        raster = grid([[1, 1, 2], [1, 2, 2], [3, -9, 2]], nodata=-9)
        out = arcpy.conversion.RasterToPolygon(raster, self.out("p.gpkg"), "NO_SIMPLIFY").getOutput(0)
        with arcpy.da.SearchCursor(out, ["gridcode", "SHAPE@AREA"]) as rows:
            got = sorted((r[0], round(r[1])) for r in rows)
        self.assertEqual(got, [(1, 300), (2, 400), (3, 100)])
        with self.assertRaises(NotImplementedError):
            arcpy.conversion.RasterToPolygon(raster, self.out("q.gpkg"))

    def test_raster_to_point_puts_a_point_at_each_cell_centre(self):
        raster = grid([[5, -9], [7, 8]], nodata=-9)
        out = arcpy.conversion.RasterToPoint(raster, self.out("pts.gpkg")).getOutput(0)
        with arcpy.da.SearchCursor(out, ["pointid", "grid_code", "SHAPE@XY"]) as rows:
            self.assertEqual(list(map(tuple, rows)), [(1, 5, (5.0, 15.0)), (2, 7, (5.0, 5.0)), (3, 8, (15.0, 5.0))])

    def test_polygon_and_point_to_raster(self):
        polygons = self.out("zones.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "zones.gpkg", "POLYGON", spatial_reference=3857)
        arcpy.management.AddField(polygons, "code", "LONG")
        with arcpy.da.InsertCursor(polygons, ["code", "SHAPE@"]) as rows:
            # A 20×10 rectangle split at x = 12: the cell centred at x = 15 falls in the second.
            rows.insertRow((4, arcpy.Polygon(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(12, 0), arcpy.Point(12, 10), arcpy.Point(0, 10)]), 3857)))
            rows.insertRow((9, arcpy.Polygon(arcpy.Array([arcpy.Point(12, 0), arcpy.Point(20, 0), arcpy.Point(20, 10), arcpy.Point(12, 10)]), 3857)))
        path = arcpy.conversion.PolygonToRaster(polygons, "code", self.out("z.tif"), cellsize=10).getOutput(0)
        self.assertEqual(arcpy.RasterToNumPyArray(arcpy.Raster(path)).tolist(), [[4, 9]])

        points = self.out("pts.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "pts.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(points, "v", "LONG")
        with arcpy.da.InsertCursor(points, ["v", "SHAPE@XY"]) as rows:
            # Extent x 0–20, y 0–10 at 10 m: two cells. Left cell holds 1, 1, 3; the right one holds 5 (on the edge).
            for v, xy in ((1, (0, 0)), (1, (2, 2)), (3, (8, 8)), (5, (20, 10))):
                rows.insertRow((v, xy))
        frequent = arcpy.conversion.PointToRaster(points, "v", self.out("f.tif"), cellsize=10).getOutput(0)
        self.assertEqual(arcpy.RasterToNumPyArray(arcpy.Raster(frequent)).tolist(), [[1, 5]])
        summed = arcpy.conversion.PointToRaster(points, "v", self.out("s.tif"), "SUM", cellsize=10).getOutput(0)
        self.assertEqual(arcpy.RasterToNumPyArray(arcpy.Raster(summed)).tolist(), [[5, 5]])


if __name__ == "__main__":
    unittest.main()
