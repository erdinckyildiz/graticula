"""arcpy.ddd aligned to ArcGIS Pro 3.7 by the Pro sweep (tools/pro-compare/sweep, case module ddd). Each test pins an
answer measured in ArcGIS Pro 3.7 on 2026-11-08 with an example small enough to work by hand."""
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy._parameter import _tokens  # noqa: E402


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self._old = arcpy.env.workspace
        arcpy.env.workspace = self.directory
        arcpy.env.overwriteOutput = True
        self.gpkg = arcpy.management.CreateSQLiteDatabase(os.path.join(self.directory, "store.gpkg"), "GEOPACKAGE")[0]
        self.sr = arcpy.SpatialReference(32635)

    def tearDown(self):
        arcpy.env.workspace = self._old
        shutil.rmtree(self.directory, ignore_errors=True)

    def make(self, name, shape, fields, rows, has_z="ENABLED"):
        fc = arcpy.management.CreateFeatureclass(self.gpkg, name, shape, has_z=has_z, spatial_reference=self.sr)[0]
        for field, kind in fields:
            arcpy.management.AddField(fc, field, kind)
        with arcpy.da.InsertCursor(fc, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for *values, wkt in rows:
                cursor.insertRow((*values, arcpy.FromWKT(wkt, self.sr)))
        return fc

    def out(self, name):
        return os.path.join(self.gpkg, name)

    def fields(self, path):
        return [f.name for f in arcpy.ListFields(path) if f.type not in ("OID", "Geometry")]

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as cursor:
            return [tuple(r) for r in cursor]


class ValueTables(unittest.TestCase):
    def test_a_windows_path_in_a_value_table_row_keeps_its_backslashes(self):
        # Pro reads "C:\data\a.gdb\fc Shape.Z Mass_Points" (CreateTin's in_features) as the path C:\data\a.gdb\fc;
        # shlex's POSIX mode had made it C:dataa.gdbfc.
        self.assertEqual(_tokens("C:\\data\\a.gdb\\fc Shape.Z Mass_Points"),
                         ["C:\\data\\a.gdb\\fc", "Shape.Z", "Mass_Points"])
        self.assertEqual(_tokens("'C:\\my data\\a.gdb\\fc' <None>"), ["C:\\my data\\a.gdb\\fc", "<None>"])


class ThreeDFeatures(_Workspace):
    LINE = "LINESTRING Z (0 0 0, 3 4 12, 6 8 12)"

    def test_copy_features_keeps_z(self):
        # Pro's copy of a 3D line class is 3D, its z kept.
        line = self.make("l", "POLYLINE", [("k", "LONG")], [(1, self.LINE)])
        copy = arcpy.management.CopyFeatures(line, self.out("copy"))[0]
        self.assertTrue(arcpy.Describe(copy).hasZ)
        self.assertEqual(self.rows(copy, ["SHAPE@WKT"]), [("MULTILINESTRING Z ((0 0 0, 3 4 12, 6 8 12))",)])

    def test_simplify_3d_line_writes_orig_oid(self):
        # Pro writes ORIG_OID, the input's ObjectID. The middle vertex is 13 units off the end-to-end segment in 3D
        # here, so a tolerance of 1 keeps it.
        line = self.make("l", "POLYLINE", [("k", "LONG")], [(7, self.LINE)])
        out = arcpy.ddd.Simplify3DLine(line, self.out("s"), "1 Meters")[0]
        self.assertEqual(self.fields(out), ["k", "ORIG_OID"])
        self.assertEqual(self.rows(out, ["k", "ORIG_OID"]), [(7, 1)])

    def test_sight_lines_to_points_have_no_dist_along(self):
        # Pro: an observer and two point targets give two lines with OID_OBSERV and OID_TARGET only.
        obs = self.make("o", "POINT", [], [("POINT Z (0 0 10)",)])
        targets = self.make("t", "POINT", [], [("POINT Z (10 0 0)",), ("POINT Z (0 10 0)",)])
        out = arcpy.ddd.ConstructSightLines(obs, targets, self.out("sight"))[0]
        self.assertEqual(self.fields(out), ["OID_OBSERV", "OID_TARGET"])
        self.assertEqual(sorted(self.rows(out, ["OID_OBSERV", "OID_TARGET"])), [(1, 1), (1, 2)])

    def test_skyline_barrier_writes_minus_one_and_the_skyline_id(self):
        # Pro: a skyline line without an ORIGFTR_ID field gives −1 there, and SKYLINE_ID is the line's ObjectID.
        obs = self.make("o", "POINT", [], [("POINT Z (0 0 0)",)])
        sky = self.make("s", "POLYLINE", [], [("LINESTRING Z (10 -5 5, 10 5 6)",), ("LINESTRING Z (-10 -5 5, -10 5 6)",)])
        out = arcpy.ddd.SkylineBarrier(obs, sky, self.out("b"))[0]
        self.assertEqual(self.fields(out), ["OBSV_PT_ID", "ORIGFTR_ID", "SKYLINE_ID"])
        self.assertEqual(sorted(self.rows(out, ["OBSV_PT_ID", "ORIGFTR_ID", "SKYLINE_ID"])), [(1, -1, 1), (1, -1, 2)])


class RasterSurfaces(_Workspace):
    def test_interpolate_shape_reaches_the_rasters_edge_and_cuts_shorter_pieces(self):
        # z = x on three 10 m cells from x = 0 to 30 (centres 5, 15, 25). Pro keeps a line from the left edge to the
        # right edge: its ends take the edge cells' values (5 and 25, the centres' heights carried out to the edge),
        # and the 30 m line sampled every 10 m is cut into 4 pieces of 7.5 (shorter than 10), not 3.
        import numpy as np
        arcpy.env.outputCoordinateSystem = self.sr
        try:
            dem = os.path.join(self.directory, "ramp.tif")
            arcpy.NumPyArrayToRaster(np.array([[5.0, 15.0, 25.0]] * 3), arcpy.Point(0, 0), 10, 10).save(dem)
        finally:
            arcpy.env.outputCoordinateSystem = None
        line = self.make("l", "POLYLINE", [], [("LINESTRING (0 15, 30 15)",)], has_z="DISABLED")
        out = arcpy.ddd.InterpolateShape(dem, line, self.out("z"))[0]
        import shapely
        (shape,), = self.rows(out, ["SHAPE@"])
        got = np.round(shapely.get_coordinates(shape._shape, include_z=True), 6).tolist()
        self.assertEqual(got, [[0, 15, 5], [7.5, 15, 7.5], [15, 15, 15], [22.5, 15, 22.5], [30, 15, 25]])


class Tins(_Workspace):
    def test_edit_tin_reads_shape_as_the_height_of_the_shapes(self):
        # Pro's EditTin lists the height field as <None> | Shape | <fields>: "Shape" is the shapes' own z. A hard line
        # across a square of four mass points adds its two end nodes.
        pts = self.make("p", "POINT", [], [(f"POINT Z ({x} {y} 1)",) for x, y in ((0, 0), (10, 0), (10, 10), (0, 10))])
        tin = os.path.join(self.directory, "t")
        arcpy.ddd.CreateTin(tin, self.sr, f"{pts} Shape.Z Mass_Points <None>")
        line = self.make("l", "POLYLINE", [], [("LINESTRING Z (2 5 3, 8 5 3)",)])
        arcpy.ddd.EditTin(tin, f"{line} Shape <None> Hard_Line", "CONSTRAINED_DELAUNAY")
        nodes = arcpy.ddd.TinNode(tin, self.out("n"))[0]
        self.assertEqual(sorted(z for z, in self.rows(nodes, ["SHAPE@Z"])), [1, 1, 1, 1, 3, 3])


if __name__ == "__main__":
    unittest.main()
