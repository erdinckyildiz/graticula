"""D-412: grapy's general tools keep z and m as ArcGIS Pro 3.7 does.

Measured 2026-11-20 with tools/pro-compare/sweep/cases/zm.py (results/zm.md): 77 cases of 50 tools on PolylineZM,
PolygonZM and PointZM inputs. Every number below is Pro's answer in that run:

- an output has z and m when an input has them (the output Z and M environments' *Same As Input*), and
  ``outputZFlag = "Disabled"`` drops the z only; Buffer, PairwiseBuffer, GraphicBuffer and MultipleRingBuffer write
  2D polygons whatever their input;
- a cut keeps each vertex's own z and m and interpolates them at a new vertex on the input's segment (Clip of the
  polygon at x = 150 writes z 115, m 2.25); a vertex on no 3D input gets z 0 and m NaN; where two lines cross, each
  piece keeps its own line's z;
- a tool that makes a shape rather than cutting one (Feature To Point, Minimum Bounding Geometry, Create Thiessen
  Polygons) writes z 0 and m NaN.
"""
import math
import os
import shutil
import sys
import tempfile
import unittest

import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402

SRID = 32635
X0, Y0 = 500000.0, 4500000.0


def coordinates(path, field="tag"):
    """{tag: [(x, y, z, m) of every vertex]} of a feature class, offsets from (X0, Y0)."""
    out = {}
    with arcpy.da.SearchCursor(path, [field, "SHAPE@"]) as rows:
        for tag, shape in rows:
            raw = shapely.get_coordinates(shape._shape, include_z=True, include_m=True)
            out.setdefault(tag, []).extend((round(x - X0, 6), round(y - Y0, 6), z, m) for x, y, z, m in raw)
    return out


class ZM(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        for name in ("workspace", "overwriteOutput", "outputZFlag", "outputMFlag"):
            self.addCleanup(setattr, _runtime.env, name, getattr(_runtime.env, name))
        arcpy.env.overwriteOutput = True
        self.gpkg = os.path.join(self.directory, "zm.gpkg")
        sr = arcpy.SpatialReference(SRID)
        self.lines = self.make("zlines", "POLYLINE", sr, [
            ("a", [(0, 100, 10.0, 0.0), (200, 100, 30.0, 200.0), (400, 100, 50.0, 400.0)]),
            ("b", [(100, 0, 5.0, 0.0), (100, 300, 35.0, 300.0)])])
        ring = [(x, y, 100.0 + 0.1 * x + 0.05 * y, float(k % 4))
                for k, (x, y) in enumerate([(0, 0), (0, 200), (200, 200), (200, 0), (0, 0)])]
        self.polys = self.make("zpolys", "POLYGON", sr, [("p", ring)])
        self.points = self.make("zpoints", "POINT", sr, [("a", (50, 50, 11.0, 1.0)), ("d", (450, 50, 44.0, 4.0))])
        self.flat = arcpy.management.CreateFeatureclass(self.gpkg, "zflat", "POLYGON", spatial_reference=sr)[0]
        arcpy.management.AddField(self.flat, "side", "TEXT")
        with arcpy.da.InsertCursor(self.flat, ["side", "SHAPE@"]) as cursor:
            cursor.insertRow(("L", arcpy.FromWKT(
                f"POLYGON (({X0 - 50} {Y0 - 50}, {X0 - 50} {Y0 + 250}, {X0 + 150} {Y0 + 250}, {X0 + 150} {Y0 - 50}, "
                f"{X0 - 50} {Y0 - 50}))", sr)))

    def make(self, name, shape, sr, rows):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, shape, has_m="ENABLED", has_z="ENABLED",
                                                   spatial_reference=sr)[0]
        arcpy.management.AddField(path, "tag", "TEXT")
        with arcpy.da.InsertCursor(path, ["tag", "SHAPE@"]) as cursor:
            for tag, parts in rows:
                if shape == "POINT":
                    x, y, z, m = parts
                    geometry = arcpy.PointGeometry(arcpy.Point(X0 + x, Y0 + y, z, m), sr, True, True)
                else:
                    kind = arcpy.Polyline if shape == "POLYLINE" else arcpy.Polygon
                    geometry = kind(arcpy.Array([arcpy.Point(X0 + x, Y0 + y, z, m) for x, y, z, m in parts]), sr,
                                    True, True)
                cursor.insertRow((tag, geometry))
        return path

    def out(self, name):
        return f"{self.gpkg}/{name}"

    def flags(self, path):
        d = arcpy.Describe(path)
        return bool(d.hasZ), bool(d.hasM)

    def assertVertex(self, vertices, x, y, z, m):
        hits = [v for v in vertices if math.isclose(v[0], x, abs_tol=1e-6) and math.isclose(v[1], y, abs_tol=1e-6)]
        self.assertTrue(hits, f"no vertex at {x}, {y} in {vertices}")
        for _, _, vz, vm in hits:
            self.assertAlmostEqual(vz, z, places=6)
            if m != m:
                self.assertTrue(vm != vm, f"m at {x}, {y} is {vm}, not NaN")
            else:
                self.assertAlmostEqual(vm, m, places=6)

    def test_a_cut_interpolates_z_and_m_on_the_input_segment(self):
        out = arcpy.analysis.Clip(self.polys, self.flat, self.out("clip"))[0]
        self.assertEqual(self.flags(out), (True, True))
        vertices = coordinates(out)["p"]
        self.assertVertex(vertices, 150, 0, 115.0, 2.25)
        self.assertVertex(vertices, 150, 200, 125.0, 1.75)
        self.assertVertex(vertices, 0, 200, 110.0, 1.0)

    def test_a_vertex_on_no_3d_input_has_z_0_and_m_nan(self):
        out = arcpy.analysis.Union([self.polys, self.flat], self.out("union"))[0]
        rows = {}
        with arcpy.da.SearchCursor(out, ["FID_zpolys", "FID_zflat", "SHAPE@"]) as cursor:
            for a, b, shape in cursor:
                rows[(a, b)] = [(round(x - X0, 6), round(y - Y0, 6), z, m) for x, y, z, m in
                                shapely.get_coordinates(shape._shape, include_z=True, include_m=True)]
        alone = rows[(-1, 1)]
        self.assertVertex(alone, -50, 250, 0.0, math.nan)
        self.assertVertex(alone, 150, 200, 125.0, 1.75)   # on the 3D polygon's edge, though not its feature

    def test_crossing_lines_keep_each_its_own_z(self):
        out = arcpy.management.FeatureToLine(self.lines, self.out("ftl"))[0]
        vertices = coordinates(out)
        self.assertVertex(vertices["a"], 100, 100, 20.0, 100.0)
        self.assertVertex(vertices["b"], 100, 100, 15.0, 100.0)
        dissolved = arcpy.analysis.PairwiseDissolve(self.lines, self.out("pd"))[0]
        with arcpy.da.SearchCursor(dissolved, ["SHAPE@"]) as cursor:
            shape = next(cursor)[0]._shape
        at = sorted(z for part in shape.geoms for x, y, z, m in shapely.get_coordinates(part, include_z=True,
                                                                                        include_m=True)
                    if (round(x - X0, 6), round(y - Y0, 6)) == (100, 100))
        self.assertEqual(at, [15.0, 15.0, 20.0, 20.0])

    def test_lines_split_and_densified_keep_z_and_m(self):
        split = arcpy.management.SplitLine(self.lines, self.out("split"))[0]
        self.assertEqual(self.flags(split), (True, True))
        self.assertVertex(coordinates(split)["a"], 200, 100, 30.0, 200.0)
        at_point = arcpy.management.SplitLineAtPoint(self.lines, self.points, self.out("sap"), "60 Meters")[0]
        self.assertVertex(coordinates(at_point)["a"], 50, 100, 15.0, 50.0)
        copy = arcpy.management.CopyFeatures(self.lines, self.out("dense"))[0]
        arcpy.edit.Densify(copy, "DISTANCE", "50 Meters")
        self.assertVertex(coordinates(copy)["a"], 50, 100, 15.0, 50.0)
        vertices = arcpy.management.FeatureVerticesToPoints(self.polys, self.out("fvtp"), "ALL")[0]
        self.assertVertex(coordinates(vertices)["p"], 200, 200, 130.0, 2.0)

    def test_a_shape_made_not_cut_has_z_0_and_m_nan(self):
        centre = arcpy.management.FeatureToPoint(self.polys, self.out("ftp"))[0]
        self.assertEqual(self.flags(centre), (True, True))
        self.assertVertex(coordinates(centre)["p"], 100, 100, 0.0, math.nan)

    def test_buffers_are_2d(self):
        for tool, args in ((arcpy.analysis.Buffer, ("10 Meters",)), (arcpy.analysis.PairwiseBuffer, ("10 Meters",)),
                           (arcpy.analysis.GraphicBuffer, ("10 Meters",))):
            out = tool(self.lines, self.out(f"buf_{tool.__name__}"), *args)[0]
            self.assertEqual(self.flags(out), (False, False), tool.__name__)
        rings = arcpy.analysis.MultipleRingBuffer(self.points, self.out("rings"), [10, 20], "Meters")[0]
        self.assertEqual(self.flags(rings), (False, False))

    def test_pass_through_tools_keep_both(self):
        for name, run in (("select", lambda o: arcpy.analysis.Select(self.points, o, "tag = 'a'")),
                          ("merge", lambda o: arcpy.management.Merge([self.points, self.points + "_copy"], o)),
                          ("sort", lambda o: arcpy.management.Sort(self.points, o, [["tag", "DESCENDING"]])),
                          ("export", lambda o: arcpy.conversion.ExportFeatures(self.points, o))):
            if name == "merge":
                arcpy.management.CopyFeatures(self.points, self.points + "_copy")
            out = run(self.out(name))[0]
            self.assertEqual(self.flags(out), (True, True), name)
            self.assertVertex(coordinates(out)["a"], 50, 50, 11.0, 1.0)

    def test_the_output_z_and_m_environments(self):
        self.assertEqual(arcpy.env.outputZFlag, "Same As Input")
        arcpy.env.outputZFlag = "DISABLED"
        self.assertEqual(arcpy.env.outputZFlag, "Disabled")       # as Pro reads it back
        out = arcpy.analysis.Clip(self.lines, self.flat, self.out("clip2d"))[0]
        self.assertEqual(self.flags(out), (False, True))
        arcpy.env.outputZFlag = "Same As Input"
        arcpy.env.outputMFlag = "Enabled"
        flat_copy = arcpy.management.CopyFeatures(self.flat, self.out("flatm"))[0]
        self.assertEqual(self.flags(flat_copy), (False, True))   # Pro 3.7: a PolygonM, its m NaN
        clipped = arcpy.analysis.Clip(self.flat, self.polys, self.out("flatclip"))[0]
        self.assertEqual(self.flags(clipped), (True, True))       # z from the 3D clip input, m from the environment
        arcpy.ResetEnvironments()
        self.assertEqual(arcpy.env.outputMFlag, "Same As Input")

    def test_a_table_never_takes_z(self):
        table = arcpy.analysis.Statistics(self.lines, self.out("stats"), [["tag", "COUNT"]])[0]
        self.assertEqual(arcpy.Describe(table).dataType, "Table")


if __name__ == "__main__":
    unittest.main()
