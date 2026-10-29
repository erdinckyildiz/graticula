"""Z and M on grapy's feature classes, to the OGC GeoPackage specification (ADR-224): the geometry column's z and m
flags (§2.1.5), and ISO WKB type codes in the geometry blob (§2.1.3) — 1000 more for Z, 2000 for M, 3000 for ZM.
Every value below is written by hand; 2D classes are checked to be stored exactly as before.
"""
import os
import sqlite3
import struct
import sys
import tempfile
import unittest

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


if __name__ == "__main__":
    unittest.main()
