"""arcpy.edit's tools in grapy, each against shapes whose answer is worked out by hand — ADR-224.

Everything is on EPSG:3857, so a distance of "N Meters" is N data units and every length below is plain arithmetic.
"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
import grapy.edit  # noqa: E402


def line(*points):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in points]), 3857)


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


def square(x, y, side=50):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x + side, y), arcpy.Point(x + side, y + side),
                                      arcpy.Point(x, y + side)]), 3857)


class Edit(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def make(self, name, shape, shapes, fields=()):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in shapes:
                written.insertRow(row if isinstance(row, tuple) else (row,))
        return path

    def shapes(self, path):
        with arcpy.da.SearchCursor(path, ["SHAPE@"]) as rows:
            return [r[0]._shape for r in rows]

    def coordinates(self, path):
        return [[tuple(round(v, 9) for v in c) for c in arcpy.management._coordinates(s)] for s in self.shapes(path)]

    def test_densify_divides_each_segment_into_equal_pieces_no_longer_than_the_distance(self):
        lines = self.make("l", "POLYLINE", [line((0, 0), (100, 0))])
        polygons = self.make("p", "POLYGON", [square(0, 0)])
        grapy.edit.Densify(lines, "DISTANCE", "30 Meters")
        # 100 / 30 needs four pieces; four pieces of 25.
        self.assertEqual(self.coordinates(lines), [[(0, 0), (25, 0), (50, 0), (75, 0), (100, 0)]])
        grapy.edit.Densify(polygons, "DISTANCE", 20)
        # Each 50-long side needs three pieces: 4 sides × 3 + the closing vertex.
        self.assertEqual(len(self.coordinates(polygons)[0]), 13)
        self.assertAlmostEqual(self.shapes(polygons)[0].area, 2500.0)
        with self.assertRaises(arcpy.NotBuiltError):
            grapy.edit.Densify(lines)
        with self.assertRaises(arcpy.NotBuiltError):
            grapy.edit.Densify(lines, "DISTANCE", 10, max_vertex_per_segment=5)

    def test_generalize_keeps_what_douglas_peucker_keeps_and_a_polygon_keeps_three_vertices(self):
        lines = self.make("l", "POLYLINE", [line((0, 0), (10, 1), (20, 0), (30, 5), (40, 0))])
        grapy.edit.Generalize(lines, "2 Meters")
        # (30,5) is 5 off the chord; then (20,0) is 100/√925 ≈ 3.29 off (0,0)–(30,5); (10,1) is 1 off (0,0)–(20,0).
        self.assertEqual(self.coordinates(lines), [[(0, 0), (20, 0), (30, 5), (40, 0)]])
        polygons = self.make("p", "POLYGON", [square(0, 0)])
        grapy.edit.Generalize(polygons, 1000)
        # Everything is within 1000: the first vertex, the one farthest from it (50,50), and the first of the two
        # equally far from that diagonal (50,0) — a triangle of half the square.
        self.assertEqual(self.shapes(polygons)[0].area, 1250.0)
        self.assertEqual(len(self.coordinates(polygons)[0]), 4)

    def test_flip_line_reverses_the_vertices(self):
        lines = self.make("l", "POLYLINE", [line((0, 0), (10, 0), (10, 10))])
        grapy.edit.FlipLine(lines)
        self.assertEqual(self.coordinates(lines), [[(10, 10), (10, 0), (0, 0)]])

    def test_erase_point_deletes_inside_with_the_boundary_or_outside(self):
        mask = self.make("mask", "POLYGON", [square(0, 0)])
        points = self.make("pts", "POINT", [point(10, 10), point(50, 10), point(200, 0)])
        grapy.edit.ErasePoint(points, mask)
        self.assertEqual(self.coordinates(points), [[(200, 0)]])
        points = self.make("pts2", "POINT", [point(10, 10), point(50, 10), point(200, 0)])
        grapy.edit.ErasePoint(points, mask, "OUTSIDE")
        self.assertEqual(self.coordinates(points), [[(10, 10)], [(50, 10)]])

    def test_trim_line_cuts_dangles_within_the_length_and_deletes_short_free_lines(self):
        # A crosses B at (10,0): A's dangles are 10 and 90 long, B's are 5 (below A) and 50 (above). C touches nothing.
        lines = self.make("l", "POLYLINE", [line((0, 0), (100, 0)), line((10, -5), (10, 50)), line((200, 0), (203, 0))])
        grapy.edit.TrimLine(lines, "12 Meters", "DELETE_SHORT")
        self.assertEqual(self.coordinates(lines), [[(10, 0), (100, 0)], [(10, 0), (10, 50)]])
        lines = self.make("k", "POLYLINE", [line((0, 0), (100, 0)), line((10, -5), (10, 50)), line((200, 0), (203, 0))])
        grapy.edit.TrimLine(lines, "12 Meters", "KEEP_SHORT")
        self.assertEqual(len(self.shapes(lines)), 3)
        # E along the bottom, D standing on it at 50, F crossing it at 80.
        tee = self.make("t", "POLYLINE", [line((0, 0), (100, 0)), line((50, 0), (50, 30)), line((80, -10), (80, 10))])
        grapy.edit.TrimLine(tee)
        # E meets D at 50 and F at 80: its dangles 0–50 and 80–100 go. F meets E at its middle: both of its dangles
        # would meet, so F stays. D meets E at its start: its other end has nothing to be trimmed back to.
        self.assertEqual(self.coordinates(tee), [[(50, 0), (80, 0)], [(50, 0), (50, 30)], [(80, -10), (80, 10)]])

    def test_extend_line_reaches_the_first_line_within_the_length(self):
        lines = self.make("l", "POLYLINE", [line((0, 0), (100, 0)), line((50, 10), (50, 5))])
        grapy.edit.ExtendLine(lines, "3 Meters", "FEATURE")
        self.assertEqual(self.coordinates(lines)[1], [(50, 10), (50, 5)])
        grapy.edit.ExtendLine(lines, "10 Meters", "FEATURE")
        self.assertEqual(self.coordinates(lines), [[(0, 0), (100, 0)], [(50, 10), (50, 5), (50, 0)]])
        with self.assertRaises(arcpy.NotBuiltError):
            grapy.edit.ExtendLine(lines, "10 Meters")

    def test_snap_moves_vertices_by_the_first_rule_that_finds_a_target(self):
        target = self.make("t", "POLYLINE", [line((0, 0), (10, 0), (10, 20))])
        points = self.make("p", "POINT", [point(12, 0), point(0, 30), point(5, 3)])
        grapy.edit.Snap(points, [[target, "END", "1 Meters"], [target, "VERTEX", "5 Meters"], [target, "EDGE", "4 Meters"]])
        # (12,0) is 2 from the vertex (10,0); (0,30) is 10 from (10,20), beyond every rule; (5,3) is 5.83 from the
        # nearest vertex and 3 from the edge, so the edge rule takes it to (5,0).
        self.assertEqual(self.coordinates(points), [[(10, 0)], [(0, 30)], [(5, 0)]])
        twins = self.make("twins", "POINT", [point(0, 0), point(1, 0)])
        grapy.edit.Snap(twins, [[twins, "VERTEX", 2]])
        # The lower ObjectID snaps to the higher; the higher has none higher to snap to.
        self.assertEqual(self.coordinates(twins), [[(1, 0)], [(1, 0)]])

    def test_transform_features_and_its_errors(self):
        moved = self.make("moved", "POLYLINE", [line((0, 0), (10, 20)), line((1, 0), (11, 20)), line((0, 1), (10, 21))])
        points = self.make("p", "POINT", [point(5, 5)])
        result = grapy.edit.TransformFeatures(points, moved, "AFFINE")
        self.assertEqual(self.coordinates(points), [[(15, 25)]])
        self.assertAlmostEqual(result.getOutput(2), 0.0)
        # Two links: (0,0) stays and (1,0) goes to (0,2) — a quarter turn and a scale of 2, x' = −2y, y' = 2x.
        turned = self.make("turned", "POLYLINE", [line((0, 0), (0, 0)), line((1, 0), (0, 2))])
        points = self.make("q", "POINT", [point(3, 0)])
        grapy.edit.TransformFeatures(points, turned, "SIMILARITY")
        x, y = self.coordinates(points)[0][0]
        self.assertAlmostEqual(x, 0.0, places=5)
        self.assertAlmostEqual(y, 6.0, places=5)
        # A unit square whose (1,1) corner is sent 4 too high: the best affine fit of y' leaves every corner ±1 off
        # (t/4 with t = 4), so every residual is 1 and the RMSE is 1.
        skewed = self.make("skewed", "POLYLINE", [line((0, 0), (0, 0)), line((1, 0), (1, 0)), line((0, 1), (0, 1)),
                                                  line((1, 1), (1, 5))])
        table = os.path.join(self.directory, "errors.gpkg")
        result = grapy.edit.CalculateTransformationErrors(skewed, table)
        self.assertAlmostEqual(result.getOutput(1), 1.0)
        with arcpy.da.SearchCursor(table, ["Orig_FID", "X_Destination", "Y_Destination", "Residual_Error"]) as rows:
            got = [tuple(r) for r in rows]
        self.assertEqual([r[:3] for r in got], [(1, 0.0, 0.0), (2, 1.0, 0.0), (3, 0.0, 1.0), (4, 1.0, 5.0)])
        for r in got:
            self.assertAlmostEqual(r[3], 1.0)
        doubled = self.make("doubled", "POLYLINE", [line((0, 0), (0, 0)), line((1, 0), (2, 0)), line((1, 1), (2, 2)),
                                                    line((0, 1), (0, 2))])
        points = self.make("r", "POINT", [point(0.5, 0.5)])
        grapy.edit.TransformFeatures(points, doubled, "PROJECTIVE")
        x, y = self.coordinates(points)[0][0]
        self.assertAlmostEqual(x, 1.0)
        self.assertAlmostEqual(y, 1.0)
        with self.assertRaises(arcpy.NotBuiltError):
            grapy.edit.CalculateTransformationErrors(skewed + "", None, "PROJECTIVE") if False else \
                grapy.edit.CalculateTransformationErrors(
                    self.make("five", "POLYLINE", [line((0, 0), (0, 0)), line((1, 0), (2, 0)), line((1, 1), (2, 2)),
                                               line((0, 1), (0, 2)), line((2, 2), (4, 4))]), None, "PROJECTIVE")
        with self.assertRaises(arcpy.ExecuteError):
            grapy.edit.CalculateTransformationErrors(turned, None, "AFFINE")

    def test_the_tools_not_built_say_why(self):
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            grapy.edit.SplitLineByMatch("a", "b", "t", "o", "1 Meters")
        self.assertIn("match table", str(raised.exception))
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            grapy.edit.UpdateCOGO("lines")
        self.assertIn("ADR-224 group 2", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
