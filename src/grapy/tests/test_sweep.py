"""The final sweep's equivalents, each against a hand-worked answer of the method it names (ADR-224 §5.6)."""
import math
import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class Sweep(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        if hasattr(arcpy.env, "randomGenerator"):
            del arcpy.env.randomGenerator

    def make(self, name, shape, fields, rows, srid=3857):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=srid)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def table(self, name, fields, rows):
        path = arcpy.management.CreateTable(self.directory, f"{name}.gpkg")[0]
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def out(self, name):
        return os.path.join(self.directory, name)

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def test_geodetic_densify(self):
        line = self.make("eq", "POLYLINE", [], [("LINESTRING(0 0,1 0)",), ("LINESTRING(5 5,5 5)",)], srid=4326)
        out = arcpy.management.GeodeticDensify(line, self.out("d.gpkg"), "GEODESIC", "50 Kilometers")[0]
        # One degree of the equator is 111.3 km: two points between, at a third and two thirds of a degree.
        (shape,), = self.rows(out, ["SHAPE@"])
        xs = [round(c[0], 9) for c in shape._shape.coords]
        self.assertEqual(xs, [0.0, round(1 / 3, 9), round(2 / 3, 9), 1.0])
        self.assertEqual(arcpy.management.GetCount(out)[0], "1")           # the zero-length segment is not written

    def test_dice_and_integrate(self):
        square = self.make("sq", "POLYGON", [("n", "TEXT")],
                           [("a", "POLYGON((0 0,40 0,40 40,0 40,0 0))")])
        with arcpy.da.UpdateCursor(square, ["SHAPE@"]) as rows:
            for row in rows:
                rows.updateRow([row[0].densify("DISTANCE", 4)])
        diced = arcpy.management.Dice(square, self.out("diced.gpkg"), 25)[0]
        # 41 vertices; cut across x = 20, each half holds 24 and is within the limit.
        self.assertEqual(sorted(r[1] for r in self.rows(diced, ["n", "SHAPE@AREA"])), [800.0, 800.0])
        a = self.make("a", "POLYLINE", [], [("LINESTRING(0 0,10 0)",)])
        b = self.make("b", "POLYLINE", [], [("LINESTRING(5 -5,5 5)",), ("LINESTRING(10.0004 0,10.0004 5)",)])
        arcpy.management.Integrate([[b, 1], [a, 2]], "0.001 Meters")
        # A's end at (10,0) moves onto B's better-ranked vertex 0.0004 away, and both lines get the crossing (5,0).
        self.assertEqual(self.rows(a, ["SHAPE@WKT"]), [("LINESTRING (0 0, 5 0, 10.0004 0)",)])
        self.assertEqual(self.rows(b, ["SHAPE@WKT"])[0], ("LINESTRING (5 -5, 5 0, 5 5)",))

    def test_table_to_ellipse(self):
        centres = self.table("c", [("x", "DOUBLE"), ("y", "DOUBLE"), ("major", "DOUBLE"), ("minor", "DOUBLE"),
                                   ("az", "DOUBLE")], [(0.0, 0.0, 20.0, 10.0, 90.0)])
        out = arcpy.management.TableToEllipse(centres, self.out("e.gpkg"), "x", "y", "major", "minor", "METERS", "az",
                                              spatial_reference=3857, geometry_type="POLYGON", method="PLANAR")[0]
        (shape,), = self.rows(out, ["SHAPE@"])
        # Full axes 20 and 10, the major one east–west: half-widths 10 and 5, area close to π·10·5.
        x0, y0, x1, y1 = shape._shape.bounds
        self.assertAlmostEqual(x1, 10.0)
        self.assertAlmostEqual(y1, 5.0)
        # The 360-sided polygon lies inside the ellipse, short of π·10·5 by under 0.02.
        self.assertTrue(math.pi * 50 - 0.02 < shape._shape.area < math.pi * 50)

    def test_random_rasters_and_subsets_repeat_with_their_seed(self):
        first = arcpy.management.CreateRandomRaster(self.directory, "r1.tif", "INTEGER 1 3", "0 0 40 20", 10)[0]
        again = arcpy.management.CreateRandomRaster(self.directory, "r2.tif", "INTEGER 1 3", "0 0 40 20", 10)[0]
        values = arcpy.Raster(first)._array
        self.assertEqual(values.shape, (2, 4))
        self.assertTrue(((values >= 1) & (values <= 3)).all())
        self.assertEqual(values.tolist(), arcpy.Raster(again)._array.tolist())
        self.assertEqual(sum(r[1] for r in self.rows(first, ["Value", "Count"])), 8)
        points = self.make("pts", "POINT", [("i", "LONG")], [(i, f"POINT({i} 0)") for i in range(10)])
        arcpy.management.SubsetFeatures(points, self.out("train.gpkg"), self.out("test.gpkg"), 30)
        train = [r[0] for r in self.rows(self.out("train.gpkg"), ["i"])]
        test = [r[0] for r in self.rows(self.out("test.gpkg"), ["i"])]
        self.assertEqual((len(train), len(test), sorted(train + test)), (3, 7, list(range(10))))

    def test_encode_transform_and_reclassify_fields(self):
        data = self.table("d", [("kind", "TEXT"), ("v", "DOUBLE")],
                          [("a", 1.0), ("b", math.e), ("a", math.e ** 2)])
        arcpy.management.EncodeField(data, "kind")
        self.assertEqual(self.rows(data, ["kind_a", "kind_b"]), [(1, 0), (0, 1), (1, 0)])
        arcpy.management.TransformField(data, "v", "LOG")
        self.assertEqual([round(r[0], 12) for r in self.rows(data, ["v_LOG"])], [0.0, 1.0, 2.0])
        arcpy.management.TransformField(data, "v", "BOX-COX", 1)
        self.assertEqual([round(r[0], 12) for r in self.rows(data, ["v_BOXCOX"])],
                         [0.0, round(math.e - 1, 12), round(math.e ** 2 - 1, 12)])
        values = self.table("n", [("x", "DOUBLE")], [(v,) for v in (0.0, 5.0, 10.0)])
        arcpy.management.ReclassifyField(values, "x", "EQUAL_INTERVAL", 2, output_field_name="eq")
        self.assertEqual(self.rows(values, ["eq_CLASS", "eq_RANGE"]), [(1, "0 - 5"), (1, "0 - 5"), (2, "5 - 10")])
        groups = self.table("g", [("x", "DOUBLE")], [(v,) for v in (1.0, 2.0, 3.0, 10.0, 11.0, 12.0)])
        arcpy.management.ReclassifyField(groups, "x", "NATURAL_BREAKS", 2, output_field_name="nb")
        # The two natural groups 1–3 and 10–12.
        self.assertEqual([r[0] for r in self.rows(groups, ["nb_CLASS"])], [1, 1, 1, 2, 2, 2])

    def test_convert_coordinate_notation(self):
        origin = self.table("o", [("lon", "DOUBLE"), ("lat", "DOUBLE")], [(0.0, 0.0), (500.0, 0.0)])
        mgrs = arcpy.management.ConvertCoordinateNotation(origin, self.out("m.gpkg"), "lon", "lat", "DD_2", "MGRS")[0]
        # 0°, 0° is 166021 m east in UTM zone 31N: square AA, 66021 → 6602 at 10 m.
        self.assertEqual(self.rows(mgrs, ["MGRS", "SHAPE@XY"]), [("31NAA66020000", (0.0, 0.0))])  # 500° excluded
        gars = arcpy.management.ConvertCoordinateNotation(origin, self.out("g.gpkg"), "lon", "lat", "DD_2", "GARS",
                                                          exclude_invalid_records="INCLUDE_INVALID")[0]
        self.assertEqual(self.rows(gars, ["GARS"]), [("361HN37",), (None,)])
        dd = arcpy.management.ConvertCoordinateNotation(origin, self.out("dd.gpkg"), "lon", "lat", "DD_2", "DD_1")[0]
        self.assertEqual(self.rows(dd, ["DD_1"]), [("0.00000N 0.00000E",)])

    def test_shapefile_and_dbase_writers(self):
        lines = self.make("routes", "POLYLINE", [("averylongname1", "TEXT"), ("averylongname2", "LONG")],
                          [("x", 7, "LINESTRING(0 0,3 4)")])
        arcpy.conversion.FeatureClassToShapefile(lines, self.directory)
        with open(self.out("routes.shp"), "rb") as shp:
            head = shp.read(100)
        self.assertEqual(struct.unpack(">i", head[:4])[0], 9994)
        self.assertEqual(struct.unpack("<ii", head[28:36]), (1000, 3))          # version 1000, PolyLine
        with open(self.out("routes.dbf"), "rb") as dbf:
            data = dbf.read()
        count, header = struct.unpack("<IH", data[4:10])
        names = [data[32 + 32 * i:32 + 32 * i + 11].split(b"\0")[0].decode() for i in range((header - 33) // 32)]
        self.assertEqual((count, names), (1, ["averylongn", "averylon_1"]))
        self.assertTrue(os.path.exists(self.out("routes.shx")) and os.path.exists(self.out("routes.prj")))
        table = self.table("people", [("name", "TEXT")], [("Ada",)])
        arcpy.conversion.TableToDBASE(table, self.directory)
        with open(self.out("people.dbf"), "rb") as dbf:
            data = dbf.read()
        self.assertIn(b"Ada", data)

    def test_polyline_to_raster(self):
        lines = self.make("ln", "POLYLINE", [("v", "LONG"), ("p", "LONG")],
                          [(1, 0, "LINESTRING(0 6,20 6)"), (2, 0, "LINESTRING(0 8,5 8)"), (2, 0, "LINESTRING(0 7,6 7)")])
        # Cell 0 (x 0..10): value 1 runs 10, value 2 runs 5 and 6 — the longest line is 1's, the longest total is 2's.
        longest = arcpy.Raster(arcpy.conversion.PolylineToRaster(lines, "v", self.out("a.tif"), cellsize=10)[0])
        self.assertEqual(longest._array.tolist(), [[1, 1]])
        combined = arcpy.Raster(arcpy.conversion.PolylineToRaster(lines, "v", self.out("b.tif"), "MAXIMUM_COMBINED_LENGTH",
                                                                  cellsize=10)[0])
        self.assertEqual(combined._array.tolist(), [[2, 1]])


if __name__ == "__main__":
    unittest.main()
