"""arcpy.conversion answers the Pro sweep measured in ArcGIS Pro 3.7, 2026-11-08 (tools/pro-compare/sweep), each pinned
by an example worked out by hand.

Everything is on EPSG:3857, so a distance of "N Meters" is N data units.
"""
import os
import struct
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402


def rect(x0, y0, x1, y1):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1),
                                      arcpy.Point(x0, y1)]), 3857)


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


def line(*points):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in points]), 3857)


class ProSweepConversion(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def make(self, name, shape, fields, rows):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def raster(self, path):
        r = arcpy.Raster(path)
        e = r.extent
        return [e.XMin, e.YMin, e.XMax, e.YMax], arcpy.RasterToNumPyArray(r, nodata_to_value=0).tolist()

    # ---- the grid a features-to-raster tool lays

    def test_the_grid_of_polygons_lines_and_points(self):
        polygons = self.make("pa", "POLYGON", [("v", "LONG")], [(1, rect(0, 0, 95, 47))])
        arcpy.conversion.PolygonToRaster(polygons, "v", os.path.join(self.directory, "pa.tif"), cellsize=10)
        # From the lower-left corner, as many cells as cover 95 × 47: 10 × 5, so the top is at 50.
        self.assertEqual(self.raster(os.path.join(self.directory, "pa.tif"))[0], [0, 0, 100, 50])
        lines = self.make("la", "POLYLINE", [("v", "LONG")], [(1, line((0, 0), (100, 100)))])
        arcpy.conversion.PolylineToRaster(lines, "v", os.path.join(self.directory, "la.tif"), cellsize=10)
        # One cell more than fit whole: 100 m is 11 cells.
        self.assertEqual(self.raster(os.path.join(self.directory, "la.tif"))[0], [0, 0, 110, 110])
        points = self.make("qa", "POINT", [("v", "LONG")], [(1, point(3, 2)), (2, point(98, 49))])
        arcpy.conversion.PointToRaster(points, "v", os.path.join(self.directory, "qa.tif"), cellsize=10)
        # Half a cell around the points, then as many cells as cover it: 105 × 57 is 11 × 6.
        self.assertEqual(self.raster(os.path.join(self.directory, "qa.tif"))[0], [-2, -3, 108, 57])

    def test_overlapping_polygons_keep_the_first_ones_value(self):
        polygons = self.make("ov", "POLYGON", [("v", "LONG")], [(9, rect(0, 0, 100, 100)), (3, rect(50, 0, 150, 100))])
        out = os.path.join(self.directory, "ov.tif")
        arcpy.conversion.PolygonToRaster(polygons, "v", out, "CELL_CENTER", None, 10)
        self.assertEqual(self.raster(out)[1][0], [9] * 10 + [3] * 5)

    def test_a_point_or_line_on_a_cell_edge_goes_left_and_up(self):
        points = self.make("e", "POINT", [("v", "LONG")], [(1, point(0, 0)), (2, point(15, 15)), (3, point(40, 40))])
        out = os.path.join(self.directory, "e.tif")
        arcpy.conversion.PointToRaster(points, "v", out, "MOST_FREQUENT", None, 10)
        # The grid runs from (−5, −5) to (45, 45); (15, 15) is on the corner of four cells and takes the upper left.
        self.assertEqual(self.raster(out)[1], [[0, 0, 0, 0, 3], [0, 0, 0, 0, 0], [0, 2, 0, 0, 0], [0, 0, 0, 0, 0],
                                               [1, 0, 0, 0, 0]])
        lines = self.make("el", "POLYLINE", [("v", "LONG")],
                          [(1, line((0, 5), (0, 95))), (2, line((100, 5), (100, 95))), (3, line((5, 0), (95, 0))),
                           (4, line((5, 100), (95, 100))), (5, line((50, 15), (50, 85))), (6, line((15, 50), (85, 50)))])
        out = os.path.join(self.directory, "el.tif")
        arcpy.conversion.PolylineToRaster(lines, "v", out, "MAXIMUM_LENGTH", None, 10)
        # Pro 3.7's raster of these lines, cell for cell: the grid is 0–110 both ways; x = 0 and y = 0 are the grid's own
        # edges; x = 100 and x = 50 go to the column on their left, y = 100 and y = 50 to the row above; a tie of
        # lengths goes to the lower value.
        self.assertEqual(self.raster(out)[1], [
            [4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 0],
            [1, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0],
            *[[1, 0, 0, 0, 5, 0, 0, 0, 0, 2, 0]] * 3,
            [1, 6, 6, 6, 5, 6, 6, 6, 6, 2, 0],
            *[[1, 0, 0, 0, 5, 0, 0, 0, 0, 2, 0]] * 4,
            [1, 3, 3, 3, 3, 3, 3, 3, 3, 2, 0]])

    def test_las_binning_puts_a_point_between_two_rows_in_the_upper_one(self):
        from grapy._las import _cells
        from grapy._raster import _Grid
        grid = _Grid(0, 20, 10, 10, 2, 2, 3857)
        found = {"x": np.array([0.0, 10.0, 5.0, 5.0, 20.0]), "y": np.array([10.0, 10.0, 0.0, 20.0, 15.0])}
        rows, columns = _cells(found, grid)
        # y = 10 is the line between the rows: the upper one, row 0. x = 10 is the line between the columns: the right
        # one. The grid's own bottom and top and right edges keep their points in the edge cells.
        self.assertEqual(rows.tolist(), [0, 0, 1, 0, 0])
        self.assertEqual(columns.tolist(), [0, 1, 0, 0, 1])

    # ---- dBASE

    def test_dbase_writes_text_254_wide_and_reals_as_f(self):
        table = os.path.join(self.directory, "t.gpkg")
        arcpy.management.CreateTable(self.directory, "t.gpkg")
        for field, kind in (("n", "LONG"), ("v", "DOUBLE"), ("s", "TEXT")):
            arcpy.management.AddField(table, field, kind)
        with arcpy.da.InsertCursor(table, ["n", "v", "s"]) as written:
            written.insertRow((1, 1.5, "ab"))
        folder = os.path.join(self.directory, "dbf")
        os.makedirs(folder)
        arcpy.conversion.TableToDBASE(table, folder)
        data = open(os.path.join(folder, "t.dbf"), "rb").read()
        fields, at = [], 32
        while data[at] != 0x0D:
            fields.append((data[at:at + 11].split(b"\0")[0].decode(), chr(data[at + 11]), data[at + 16], data[at + 17]))
            at += 32
        self.assertEqual(fields, [("n", "N", 10, 0), ("v", "F", 19, 11), ("s", "C", 254, 0)])
        self.assertEqual(struct.unpack("<H", data[10:12])[0], 1 + 10 + 19 + 254)


if __name__ == "__main__":
    unittest.main()
