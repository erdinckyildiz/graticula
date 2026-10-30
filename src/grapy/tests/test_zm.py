"""Z and M on grapy's feature classes, to the OGC GeoPackage specification (ADR-224): the geometry column's z and m
flags (§2.1.5), and ISO WKB type codes in the geometry blob (§2.1.3) — 1000 more for Z, 2000 for M, 3000 for ZM.
Every value below is written by hand; 2D classes are checked to be stored exactly as before.
"""
import math
import os
import sqlite3
import struct
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _gpkg  # noqa: E402


def wkb_type(blob):
    """The WKB geometry type code after the GeoPackage header (8 bytes, no envelope) and the byte-order byte."""
    return struct.unpack("<I", blob[9:13])[0]


class ZM(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def make(self, name, kind, has_z="DISABLED", has_m="DISABLED"):
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", kind, has_m=has_m, has_z=has_z,
                                            spatial_reference=3857)
        return os.path.join(self.directory, f"{name}.gpkg")

    def blob(self, path):
        connection = sqlite3.connect(path)
        try:
            row = connection.execute(f'select geom from "{os.path.splitext(os.path.basename(path))[0]}"').fetchone()
            flags = connection.execute("select z, m from gpkg_geometry_columns").fetchone()
        finally:
            connection.close()
        return row[0], flags

    def test_a_2d_class_is_stored_as_before(self):
        path = self.make("flat", "POINT")
        with arcpy.da.InsertCursor(path, ["SHAPE@XY"]) as rows:
            rows.insertRow(((1.0, 2.0),))
        blob, flags = self.blob(path)
        # Header GP, version 0, flags 1 (little-endian, no envelope), srs 3857, then WKB Point (type 1) x 1 y 2.
        self.assertEqual(blob, b"GP\x00\x01" + struct.pack("<i", 3857) + b"\x01" + struct.pack("<Idd", 1, 1.0, 2.0))
        self.assertEqual(flags, (0, 0))

    def test_a_z_class_carries_z_through_the_cursors(self):
        path = self.make("lines", "POLYLINE", has_z="ENABLED")
        line = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0, 10), arcpy.Point(30, 40, 20)]), 3857, True)
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            rows.insertRow((line,))
            # A 2D line written to a Z class gets z 0.
            rows.insertRow((arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(1, 0)]), 3857),))
        blob, flags = self.blob(path)
        self.assertEqual(flags, (1, 0))
        self.assertEqual(wkb_type(blob), 1005)  # MultiLineString Z
        with arcpy.da.SearchCursor(path, ["SHAPE@"]) as rows:
            first, second = [r[0] for r in rows]
        self.assertTrue(first.hasZ)
        self.assertFalse(first.hasM)
        self.assertEqual((first.firstPoint.Z, first.lastPoint.Z), (10.0, 20.0))
        self.assertEqual(second.lastPoint.Z, 0.0)
        # The 2D length is unchanged by z: 50.
        self.assertEqual(first.length, 50.0)
        described = arcpy.Describe(path)
        self.assertEqual((described.hasZ, described.hasM), (True, False))

    def test_m_and_zm_points_and_the_tokens(self):
        path = self.make("pts", "POINT", has_z="ENABLED", has_m="ENABLED")
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            rows.insertRow((arcpy.PointGeometry(arcpy.Point(1, 2, 3, 4), 3857, True, True),))
        with arcpy.da.InsertCursor(path, ["SHAPE@XYZ"]) as rows:
            rows.insertRow(((5.0, 6.0, 7.0),))
        blob, flags = self.blob(path)
        self.assertEqual((flags, wkb_type(blob)), ((1, 1), 3001))  # Point ZM
        with arcpy.da.SearchCursor(path, ["SHAPE@Z", "SHAPE@M", "SHAPE@XYZ"]) as rows:
            got = [tuple(r) for r in rows]
        self.assertEqual(got[0], (3.0, 4.0, (1.0, 2.0, 3.0)))
        # Written without an m into a ZM class: its m is NaN.
        self.assertEqual(got[1][0], 7.0)
        self.assertNotEqual(got[1][1], got[1][1])
        measured = self.make("measured", "POINT", has_m="ENABLED")
        with arcpy.da.InsertCursor(measured, ["SHAPE@"]) as rows:
            rows.insertRow((arcpy.PointGeometry(arcpy.Point(1, 2, None, 9), 3857, False, True),))
        blob, flags = self.blob(measured)
        self.assertEqual((flags, wkb_type(blob)), ((0, 1), 2001))  # Point M
        with arcpy.da.SearchCursor(measured, ["SHAPE@M", "SHAPE@Z"]) as rows:
            self.assertEqual(tuple(next(rows)), (9.0, None))

    def test_a_z_shape_written_to_a_2d_class_loses_its_z(self):
        path = self.make("flat", "POINT")
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            rows.insertRow((arcpy.PointGeometry(arcpy.Point(1, 2, 3), 3857, True),))
        blob, _ = self.blob(path)
        self.assertEqual(wkb_type(blob), 1)

    def test_projecting_and_moving_keep_z_and_m(self):
        line = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0, 10, 1), arcpy.Point(1000, 0, 20, 2)]), 3857, True, True)
        projected = line.projectAs(4326)
        self.assertEqual((projected.firstPoint.Z, projected.lastPoint.M), (10.0, 2.0))
        moved = line.move(1, 2, 5)
        self.assertEqual((moved.firstPoint.X, moved.firstPoint.Y, moved.firstPoint.Z, moved.firstPoint.M), (1, 2, 15, 1))
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(1, 0)]), 3857).move(0, 0, 1)

    def test_same_as_template(self):
        template = self.make("t", "POINT", has_z="ENABLED")
        arcpy.management.CreateFeatureclass(self.directory, "copy.gpkg", "POINT", template, "SAME_AS_TEMPLATE",
                                            "SAME_AS_TEMPLATE", 3857)
        connection = sqlite3.connect(os.path.join(self.directory, "copy.gpkg"))
        try:
            self.assertEqual(_gpkg.dimensions(connection, "copy"), (True, False))
        finally:
            connection.close()


    def test_geojson_elevations_cross_as_z(self):
        import json
        from grapy import _io
        source = os.path.join(self.directory, "in.geojson")
        with open(source, "w", encoding="utf-8") as out:
            json.dump({"type": "FeatureCollection", "features": [
                {"type": "Feature", "properties": {"n": 1}, "geometry": {"type": "Point", "coordinates": [10, 20, 30]}}]},
                out)
        target = os.path.join(self.directory, "in.gpkg")
        _io.geojson_to_gpkg(source, target)
        with arcpy.da.SearchCursor(target, ["SHAPE@Z"]) as rows:
            self.assertEqual(next(rows)[0], 30.0)
        back = os.path.join(self.directory, "out.geojson")
        _io.gpkg_to_geojson(target, back)
        with open(back, encoding="utf-8") as read:
            self.assertEqual(json.load(read)["features"][0]["geometry"]["coordinates"], [10.0, 20.0, 30.0])



class ZTools(unittest.TestCase):
    """The tools Z makes buildable, against hand-worked answers."""

    setUp = ZM.setUp
    make = ZM.make

    def lines(self, name, rows, has_z="ENABLED", fields=()):
        path = self.make(name, "POLYLINE", has_z=has_z)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def zline(self, *points):
        return arcpy.Polyline(arcpy.Array([arcpy.Point(*p) for p in points]), 3857, True)

    def zcoords(self, path):
        with arcpy.da.SearchCursor(path, ["SHAPE@"]) as rows:
            return [[(p.X, p.Y, p.Z) for p in r[0].getPart(0)] for r in rows]

    def test_feature_to_3d_by_attribute(self):
        flat = self.lines("flat", [(0.0, 30.0, arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(10, 0),
                                                                             arcpy.Point(30, 0)]), 3857))],
                          "DISABLED", [("h1", "DOUBLE"), ("h2", "DOUBLE")])
        out = arcpy.ddd.FeatureTo3DByAttribute(flat, os.path.join(self.directory, "sloped.gpkg"), "h1", "h2").getOutput(0)
        # From 0 to 30 over 30 units of length: the vertex 10 along is at 10.
        self.assertEqual(self.zcoords(out), [[(0.0, 0.0, 0.0), (10.0, 0.0, 10.0), (30.0, 0.0, 30.0)]])
        level = arcpy.ddd.FeatureTo3DByAttribute(flat, os.path.join(self.directory, "level.gpkg"), "h2").getOutput(0)
        self.assertEqual([p[2] for p in self.zcoords(level)[0]], [30.0, 30.0, 30.0])

    def test_add_z_information(self):
        path = self.lines("z", [(self.zline((0, 0, 0), (3, 4, 12), (6, 8, 12)),)])
        arcpy.ddd.AddZInformation(path, ["Z_MIN", "Z_MAX", "Z_MEAN", "LENGTH_3D", "VERTEX_COUNT", "MIN_SLOPE", "MAX_SLOPE",
                                         "AVG_SLOPE"])
        with arcpy.da.SearchCursor(path, ["Z_MIN", "Z_MAX", "Z_MEAN", "LENGTH_3D", "VERTEX_COUNT", "MIN_SLOPE",
                                          "MAX_SLOPE", "AVG_SLOPE"]) as rows:
            got = next(rows)
        # Segments: run 5 rise 12 (3D 13, grade 240 %, mid z 6) and run 5 rise 0 (3D 5, grade 0, mid z 12).
        self.assertEqual(got[:2], (0.0, 12.0))
        self.assertAlmostEqual(got[2], (13 * 6 + 5 * 12) / 18)
        self.assertEqual(got[3:7], (18.0, 3, 0.0, 240.0))
        self.assertAlmostEqual(got[7], 13 * 240 / 18)
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.ddd.AddZInformation(path, "VOLUME")

    def test_near_3d(self):
        observers = self.make("obs", "POINT", has_z="ENABLED")
        with arcpy.da.InsertCursor(observers, ["SHAPE@XYZ"]) as rows:
            rows.insertRow(((0.0, 0.0, 0.0),))
        targets = self.make("tgt", "POINT", has_z="ENABLED")
        with arcpy.da.InsertCursor(targets, ["SHAPE@XYZ"]) as rows:
            rows.insertRow(((3.0, 4.0, 12.0),))
        arcpy.ddd.Near3D(observers, targets, angle="ANGLE")
        with arcpy.da.SearchCursor(observers, ["NEAR_FID", "NEAR_DIST", "NEAR_DIST3", "NEAR_ANG_H", "NEAR_ANG_V"]) as rows:
            got = next(rows)
        # 5 across and 12 up: 13 in 3D, at atan2(4, 3) = 53.13° and atan2(12, 5) = 67.38°.
        self.assertEqual(got[:3], (1, 5.0, 13.0))
        self.assertAlmostEqual(got[3], math.degrees(math.atan2(4, 3)))
        self.assertAlmostEqual(got[4], math.degrees(math.atan2(12, 5)))
        line = self.lines("near_line", [(self.zline((0, 10, 0), (10, 10, 0)),)])
        probe = self.make("probe", "POINT", has_z="ENABLED")
        with arcpy.da.InsertCursor(probe, ["SHAPE@XYZ"]) as rows:
            rows.insertRow(((5.0, 0.0, 0.0),))
        arcpy.ddd.Near3D(probe, line, location="LOCATION")
        with arcpy.da.SearchCursor(probe, ["NEAR_DIST3", "NEAR_X", "NEAR_Y"]) as rows:
            self.assertEqual(tuple(next(rows)), (10.0, 5.0, 10.0))

    def test_interpolate_shape_and_update_feature_z(self):
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        try:
            ramp = arcpy.NumPyArrayToRaster(np.array([[0.0, 10.0], [0.0, 10.0]]), arcpy.Point(0, 0), 10, 10)
        finally:
            arcpy.env.outputCoordinateSystem = None
        surface = ramp.save(os.path.join(self.directory, "ramp.tif"))
        flat = self.lines("route", [(arcpy.Polyline(arcpy.Array([arcpy.Point(5, 10), arcpy.Point(15, 10)]), 3857),),
                                    (arcpy.Polyline(arcpy.Array([arcpy.Point(5, 10), arcpy.Point(100, 10)]), 3857),)],
                          "DISABLED")
        out = arcpy.ddd.InterpolateShape(surface, flat, os.path.join(self.directory, "draped.gpkg"), 5).getOutput(0)
        # Densified every 5: x = 5, 10, 15 between the centres valued 0 and 10. The second line leaves the raster and
        # is left out.
        self.assertEqual(self.zcoords(out), [[(5.0, 10.0, 0.0), (10.0, 10.0, 5.0), (15.0, 10.0, 10.0)]])
        lifted = self.lines("lifted", [(0, self.zline((5, 10, 99), (10, 10, 99))), (0, self.zline((5, 10, 99), (100, 10, 99)))],
                            fields=[("ok", "LONG")])
        arcpy.ddd.UpdateFeatureZ(lifted, surface, status_field="ok")
        with arcpy.da.SearchCursor(lifted, ["ok", "SHAPE@"]) as rows:
            got = [(r[0], [p.Z for p in r[1].getPart(0)]) for r in rows]
        self.assertEqual(got, [(1, [0.0, 5.0]), (0, [99.0, 99.0])])

    def test_xy_table_to_point_with_z_and_adjust_3d_z(self):
        table = self.make("table", "POINT")
        for field in ("x", "y", "z"):
            arcpy.management.AddField(table, field, "DOUBLE")
        with arcpy.da.InsertCursor(table, ["x", "y", "z", "SHAPE@XY"]) as rows:
            rows.insertRow((1.0, 2.0, 2.0, (0.0, 0.0)))
        out = arcpy.management.XYTableToPoint(table, os.path.join(self.directory, "xyz.gpkg"), "x", "y", "z", 3857).getOutput(0)
        with arcpy.da.SearchCursor(out, ["SHAPE@XYZ"]) as rows:
            self.assertEqual(next(rows)[0], (1.0, 2.0, 2.0))
        arcpy.management.Adjust3DZ(out, "REVERSE", 1, "METERS", "FEET")
        with arcpy.da.SearchCursor(out, ["SHAPE@Z"]) as rows:
            # −2 + 1 = −1 m, which is −1/0.3048 ft.
            self.assertAlmostEqual(next(rows)[0], -1 / 0.3048)


    def test_simplify_and_points_along_3d_lines(self):
        lines = self.lines("s", [(self.zline((0, 0, 0), (10, 0, 1), (20, 0, 0)),), (self.zline((0, 0, 0), (10, 0, 5), (20, 0, 0)),)])
        out = arcpy.ddd.Simplify3DLine(lines, os.path.join(self.directory, "simple.gpkg"), "2 Meters").getOutput(0)
        # The middle vertex is 1 above the chord in the first line (dropped) and 5 in the second (kept): 3D distance.
        self.assertEqual([len(c) for c in self.zcoords(out)], [2, 3])
        slope = self.lines("slope", [(self.zline((0, 0, 0), (3, 4, 12)),)])
        points = arcpy.ddd.GeneratePointsAlong3DLines(slope, os.path.join(self.directory, "pts.gpkg"), "DISTANCE", 5,
                                                      add_chainage_fields="ADD_CHAINAGE").getOutput(0)
        with arcpy.da.SearchCursor(points, ["ORIG_FID", "ORIG_LEN", "ORIG_SEQ", "SHAPE@XYZ"]) as rows:
            got = [tuple(r) for r in rows]
        # 13 long in 3D: points at 5 and 10, i.e. 5/13 and 10/13 of (3, 4, 12).
        self.assertEqual([g[:3] for g in got], [(1, 5.0, 1), (1, 10.0, 2)])
        for value, want in zip(got[0][3], (15 / 13, 20 / 13, 60 / 13)):
            self.assertAlmostEqual(value, want)
        ends = arcpy.ddd.GeneratePointsAlong3DLines(slope, os.path.join(self.directory, "ends.gpkg"), "PERCENTAGE", None,
                                                    40, "END_POINTS").getOutput(0)
        self.assertEqual(arcpy.management.GetCount(ends)[0], "4")  # start, 40 %, 80 %, end

    def test_ascii_out_and_in(self):
        lines = self.lines("a", [(self.zline((0, 0, 0), (3, 4, 12)),)])
        text = arcpy.ddd.FeatureClassZToASCII(lines, self.directory, "lines.txt").getOutput(0)
        with open(text, encoding="utf-8") as read:
            self.assertEqual(read.read(), "1\n0 0 0\n3 4 12\nEND\nEND\n")
        xyz = os.path.join(self.directory, "pts.xyz")
        with open(xyz, "w", encoding="utf-8") as out:
            out.write("x y z\n1 2 3\n4 5 6 extra\n")
        points = arcpy.ddd.ASCII3DToFeatureClass(xyz, "XYZ", os.path.join(self.directory, "xyz.gpkg"), "POINT", 2).getOutput(0)
        with arcpy.da.SearchCursor(points, ["SHAPE@XYZ"]) as rows:
            self.assertEqual([r[0] for r in rows], [(1.0, 2.0, 6.0), (4.0, 5.0, 12.0)])
        back = arcpy.ddd.ASCII3DToFeatureClass(text, "GENERATE", os.path.join(self.directory, "gen.gpkg"), "POLYLINE").getOutput(0)
        self.assertEqual(self.zcoords(back), [[(0.0, 0.0, 0.0), (3.0, 4.0, 12.0)]])

    def test_construct_sight_lines(self):
        observers = self.make("o", "POINT", has_z="ENABLED")
        with arcpy.da.InsertCursor(observers, ["SHAPE@XYZ"]) as rows:
            rows.insertRow(((0.0, 0.0, 10.0),))
        targets = self.make("t", "POINT", has_z="ENABLED")
        with arcpy.da.InsertCursor(targets, ["SHAPE@XYZ"]) as rows:
            rows.insertRow(((30.0, 40.0, 60.0),))
        out = arcpy.ddd.ConstructSightLines(observers, targets, os.path.join(self.directory, "sight.gpkg"),
                                            output_the_direction="OUTPUT_THE_DIRECTION").getOutput(0)
        with arcpy.da.SearchCursor(out, ["OID_OBSERV", "OID_TARGET", "AZIMUTH", "VERT_ANGLE", "SHAPE@"]) as rows:
            got = next(rows)
        # 30 east and 40 north: azimuth atan2(30, 40) = 36.87°; 50 away and 50 up: 45°.
        self.assertEqual(got[:2], (1, 1))
        self.assertAlmostEqual(got[2], math.degrees(math.atan2(30, 40)))
        self.assertAlmostEqual(got[3], 45.0)
        self.assertEqual((got[4].firstPoint.Z, got[4].lastPoint.Z), (10.0, 60.0))


    def test_interpolators_read_the_shapes_own_z(self):
        points = self.make("zp", "POINT", has_z="ENABLED")
        with arcpy.da.InsertCursor(points, ["SHAPE@XYZ"]) as rows:
            for xyz in ((0.0, 0.0, 0.0), (20.0, 0.0, 20.0), (0.0, 20.0, 40.0)):
                rows.insertRow((xyz,))
        # z = x + 2y on three points: a first-order trend reproduces it, read from "Shape" as documented.
        got = arcpy.RasterToNumPyArray(arcpy.sa.Trend(points, "Shape", 10)).tolist()
        self.assertAlmostEqual(got[1][0], 5 + 2 * 5, places=3)


if __name__ == "__main__":
    unittest.main()
