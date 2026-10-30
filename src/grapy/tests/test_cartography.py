"""arcpy.cartography in grapy, each tool against an answer worked by hand from the algorithm Esri documents."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.cartography  # noqa: E402

cartography = grapy.cartography


def line(*xys):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(*xy) for xy in xys]), 3857)


# A zig-zag whose answers are worked out below: (0,0), (10,1), (20,0), (30,5), (40,0).
ZIGZAG = ((0, 0), (10, 1), (20, 0), (30, 5), (40, 0))


class Cartography(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def make(self, name, rows):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POLYLINE", spatial_reference=3857)
        arcpy.management.AddField(path, "name", "TEXT")
        with arcpy.da.InsertCursor(path, ["name", "SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def coords(self, path):
        with arcpy.da.SearchCursor(path, ["SHAPE@WKT"]) as rows:
            import shapely
            return [[tuple(c) for c in shapely.from_wkt(r[0]).geoms[0].coords] if "MULTI" in r[0]
                    else [tuple(c) for c in shapely.from_wkt(r[0]).coords] for r in rows]

    def test_douglas_peucker_keeps_what_is_farther_than_the_tolerance(self):
        # Endpoints (0,0)–(40,0): (10,1) is 1 away, (20,0) 0, (30,5) 5 — keep (30,5).
        # (0,0)–(30,5): (10,1) is |30·1 − 5·10| / sqrt(925) = 20/30.41 ≈ 0.66, (20,0) is |−100| / 30.41 ≈ 3.29.
        # Tolerance 2 keeps (20,0); then (0,0)–(20,0) leaves (10,1) at 1 < 2. Tolerance 4 drops (20,0) as well.
        path = self.make("z", [("z", line(*ZIGZAG))])
        two = cartography.SimplifyLine(path, self.out("dp2"), "POINT_REMOVE", "2 Meters")
        self.assertEqual(self.coords(two[0]), [[(0, 0), (20, 0), (30, 5), (40, 0)]])
        four = cartography.SimplifyLine(path, self.out("dp4"), "POINT_REMOVE", 4)
        self.assertEqual(self.coords(four[0]), [[(0, 0), (30, 5), (40, 0)]])
        with arcpy.da.SearchCursor(four[0], ["name", "InLine_FID", "SimLnFlag", "MinSimpTol", "MaxSimpTol"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("z", 1, 0, 4.0, 4.0)])

    def test_visvalingam_whyatt_removes_triangles_smaller_than_the_tolerance_squared(self):
        # Triangles: (10,1) with its neighbours: |10·0 − 20·1|/2 = 10; (20,0): |10·4 − 20·(−1)|/2 = 30;
        # (30,5): |10·0 − 20·5|/2 = 50. Tolerance 3 (area 9): nothing is smaller — the line is unchanged.
        # Tolerance 4 (16): (10,1) goes; (20,0) is recomputed with (0,0) and (30,5): |20·5 − 30·0|/2 = 50. Stop.
        # Tolerance 8 (64): then (20,0) and (30,5) both have 50 — the earlier goes; (30,5) becomes
        # |30·0 − 40·5|/2 = 100 ≥ 64. Stop.
        path = self.make("z", [("z", line(*ZIGZAG))])
        three = cartography.SimplifyLine(path, self.out("vw3"), "EFFECTIVE_AREA", 3)
        self.assertEqual(self.coords(three[0]), [list(map(tuple, ZIGZAG))])
        four = cartography.SimplifyLine(path, self.out("vw4"), "EFFECTIVE_AREA", 4)
        self.assertEqual(self.coords(four[0]), [[(0, 0), (20, 0), (30, 5), (40, 0)]])
        eight = cartography.SimplifyLine(path, self.out("vw8"), "EFFECTIVE_AREA", 8)
        self.assertEqual(self.coords(eight[0]), [[(0, 0), (30, 5), (40, 0)]])

    def test_a_line_collapsed_to_zero_length_becomes_a_point(self):
        # A small closed loop from (0,100): every vertex is within 5 of its start, which is also its end.
        path = self.make("loop", [("loop", line((0, 100), (1, 101), (2, 100), (0, 100))), ("z", line(*ZIGZAG))])
        result = cartography.SimplifyLine(path, self.out("c"), "POINT_REMOVE", 5)
        self.assertEqual(arcpy.management.GetCount(result[0])[0], "1")
        self.assertTrue(result[1].endswith("c_Pnt.gpkg"))
        with arcpy.da.SearchCursor(result[1], ["InLine_FID", "SHAPE@XY"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [(1, (0.0, 100.0))])
        none = cartography.SimplifyLine(path, self.out("n"), "POINT_REMOVE", 5, None, "NO_KEEP")
        self.assertEqual(none[1], "")

    def test_a_simplification_that_would_change_topology_is_refused(self):
        # A hook around A's end at (10,0): its two corners are 6 from its own chord (5,−1)–(5,1). At tolerance 7
        # both go and the chord crosses A at (5,0), where the inputs never met.
        path = self.make("hook", [("a", line((0, 0), (10, 0))), ("b", line((5, -1), (11, -1), (11, 1), (5, 1)))])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "meet differently"):
            cartography.SimplifyLine(path, self.out("bad"), "POINT_REMOVE", 7)
        # At tolerance 5 the hook stays, and lines that met at an endpoint still meet there.
        joined = self.make("joined", [("a", line((0, 0), (20, 0))), ("b", line((20, 0), (30, 1), (40, 0)))])
        kept = cartography.SimplifyLine(joined, self.out("ok"), "POINT_REMOVE", 5)
        self.assertEqual(self.coords(kept[0]), [[(0, 0), (20, 0)], [(20, 0), (40, 0)]])

    def test_input_topological_errors_are_flagged(self):
        # Two lines crossing at (0,0), which is a vertex of neither: both are flagged. A cross through shared vertices
        # is not an error.
        path = self.make("x", [("h", line((-5, 0), (5, 0))), ("v", line((0, -5), (0, 5))),
                               ("h2", line((-5, 20), (0, 20), (5, 20))), ("v2", line((0, 15), (0, 20), (0, 25)))])
        result = cartography.SimplifyLine(path, self.out("f"), "POINT_REMOVE", 0.5)
        with arcpy.da.SearchCursor(result[0], ["name", "SimLnFlag"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("h", 1), ("v", 1), ("h2", 0), ("v2", 0)])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "RESOLVE_ERRORS"):
            cartography.SimplifyLine(path, self.out("r"), "POINT_REMOVE", 0.5, error_option="RESOLVE_ERRORS")

    def test_undocumented_algorithms_and_tools_are_refused_by_name(self):
        path = self.make("z", [("z", line(*ZIGZAG))])
        for algorithm in ("BEND_SIMPLIFY", "WEIGHTED_AREA"):
            with self.assertRaisesRegex(arcpy.NotBuiltError, algorithm):
                cartography.SimplifyLine(path, self.out("b"), algorithm, 2)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "in_barriers"):
            cartography.SimplifyLine(path, self.out("b"), "POINT_REMOVE", 2, in_barriers=[path])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "SmoothLine.*PAEK"):
            cartography.SmoothLine(path, self.out("s"), "PAEK", 10)

    def test_simplify_polygon_opens_each_ring_at_its_first_vertex(self):
        # Ring from (0,0): (10,0), (20,1), (30,0), (30,30), (0,30). Closed, its trend "line" is the point (0,0): (30,30)
        # is farthest (42.4) and kept. (0,0)–(30,30): (30,0) is 21.2 away, kept; (0,0)–(30,0): (10,0) is 0 and (20,1)
        # is 1 away, under 2, both dropped. (30,30)–(0,0): (0,30) is 21.2 away, kept. The square 0..30 remains.
        # A 1×1 square far away, under minimum_area 2, is removed and leaves its centroid in _Pnt.
        path = os.path.join(self.directory, "pg.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "pg.gpkg", "POLYGON", spatial_reference=3857)
        ring = [(0, 0), (10, 0), (20, 1), (30, 0), (30, 30), (0, 30)]
        tiny = [(100, 100), (101, 100), (101, 101), (100, 101)]
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            for shape in (ring, tiny):
                rows.insertRow((arcpy.Polygon(arcpy.Array([arcpy.Point(*xy) for xy in shape]), 3857),))
        result = cartography.SimplifyPolygon(path, self.out("sp"), "POINT_REMOVE", 2, 2)
        with arcpy.da.SearchCursor(result[0], ["InPoly_FID", "SHAPE@"]) as rows:
            (oid, shape), = [tuple(r) for r in rows]
        self.assertEqual(oid, 1)
        self.assertEqual(shape.area, 900.0)
        self.assertEqual(shape.pointCount, 5)
        with arcpy.da.SearchCursor(result[1], ["InPoly_FID", "SHAPE@XY"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [(2, (100.5, 100.5))])
        self.assertIn("Douglas", cartography.SimplifyPolygon.__grapy_equivalent__)
        # Two squares sharing an edge: simplified apart, their outlines would no longer coincide — refused.
        shared = os.path.join(self.directory, "sh.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "sh.gpkg", "POLYGON", spatial_reference=3857)
        with arcpy.da.InsertCursor(shared, ["SHAPE@"]) as rows:
            rows.insertRow((arcpy.Polygon(arcpy.Array([arcpy.Point(*xy) for xy in
                                                       [(0, 0), (10, 0), (10, 5), (10.5, 6), (10, 7), (10, 10), (0, 10)]]), 3857),))
            rows.insertRow((arcpy.Polygon(arcpy.Array([arcpy.Point(*xy) for xy in
                                                       [(10, 0), (20, 0), (20, 10), (10, 10), (10, 7), (10.5, 6), (10, 5)]]), 3857),))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "meet differently"):
            cartography.SimplifyPolygon(shared, self.out("shx"), "POINT_REMOVE", 2)

    def polygons(self, name, rings):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POLYGON", spatial_reference=3857)
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            for ring in rings:
                rows.insertRow((arcpy.Polygon(arcpy.Array([arcpy.Point(*xy) for xy in ring]), 3857),))
        return path

    def test_color_theorem_field_by_greedy_colouring(self):
        # Four unit squares in a row, A B C D: degrees 1, 2, 2, 1. Welsh–Powell order, ties by object id: B, C, A, D.
        # B takes 1; C (next to B) has none free, takes 2; A (next to B) takes the free 2; D (next to C) takes 1.
        row = [[(x, 0), (x + 10, 0), (x + 10, 10), (x, 10)] for x in (0, 10, 20, 30)]
        path = self.polygons("row", row)
        cartography.CalculateColorTheoremField(path, "color")
        with arcpy.da.SearchCursor(path, ["color"]) as rows:
            self.assertEqual([r[0] for r in rows], [2, 1, 2, 1])
        # A 2 × 2 grid: diagonal squares touch at one vertex only and are not adjacent, so two values suffice, the
        # diagonals sharing one. All have degree 2; order by id: 1 → 1, 2 (next to 1) → 2, 3 (next to 1) → 2,
        # 4 (next to 2 and 3) → 1.
        grid = [[(x, y), (x + 10, y), (x + 10, y + 10), (x, y + 10)] for y in (0, 10) for x in (0, 10)]
        path = self.polygons("grid", grid)
        cartography.CalculateColorTheoremField(path, "color")
        with arcpy.da.SearchCursor(path, ["color"]) as rows:
            self.assertEqual([r[0] for r in rows], [1, 2, 2, 1])
        self.assertIn("Welsh", cartography.CalculateColorTheoremField.__grapy_equivalent__)

    def test_aggregate_polygons_bridges_gaps_narrower_than_the_distance(self):
        # Squares [0,10]² and [14,24]×[0,10] are 4 apart: at 5 they join (the bridge holds (12, 5)), at 3 they do not.
        path = self.polygons("sq", [[(0, 0), (10, 0), (10, 10), (0, 10)], [(14, 0), (24, 0), (24, 10), (14, 10)]])
        joined = cartography.AggregatePolygons(path, self.out("agg5"), 5, out_table=self.out("agg5_t"))
        with arcpy.da.SearchCursor(joined[0], ["SHAPE@"]) as rows:
            (shape,), = [tuple(r) for r in rows]
        self.assertTrue(shape.contains(arcpy.PointGeometry(arcpy.Point(12, 5), 3857)))
        self.assertGreater(shape.area, 200.0)
        with arcpy.da.SearchCursor(joined[1], ["OUTPUT_FID", "INPUT_FID"]) as rows:
            self.assertEqual(sorted(tuple(r) for r in rows), [(1, 1), (1, 2)])
        apart = cartography.AggregatePolygons(path, self.out("agg3"), 3)
        self.assertEqual(arcpy.management.GetCount(apart[0])[0], "2")
        # No self-aggregation: a notch 2 wide in one polygon stays open at 5 — 100 − 2·6 = 88.
        notch = self.polygons("notch", [[(0, 0), (10, 0), (10, 10), (6, 10), (6, 4), (4, 4), (4, 10), (0, 10)]])
        kept = cartography.AggregatePolygons(notch, self.out("agg_n"), 5)
        with arcpy.da.SearchCursor(kept[0], ["SHAPE@AREA"]) as rows:
            self.assertAlmostEqual(next(rows)[0], 88.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "ORTHOGONAL"):
            cartography.AggregatePolygons(path, self.out("o"), 5, 0, 0, "ORTHOGONAL")

    def test_simplify_building_by_rectangle_or_douglas_peucker(self):
        # A 20 × 10 rectangle with a 0.5-deep notch is within 1 of its minimum rectangle: BLD_STATUS 2, area 200.
        # An L of three 10 × 10 squares lies 10 from its 20 × 20 rectangle (at (120, 20)), so Douglas–Peucker at 1 keeps its
        # six corners — nothing to remove: status 5. A 0.5 × 0.5 square is under 1² and becomes its rectangle: 3.
        path = self.polygons("bld", [
            [(0, 0), (20, 0), (20, 10), (12, 10), (12, 9.5), (8, 9.5), (8, 10), (0, 10)],
            [(100, 0), (120, 0), (120, 10), (110, 10), (110, 20), (100, 20)],
            [(200, 0), (200.5, 0), (200.5, 0.5), (200, 0.5)]])
        out = cartography.SimplifyBuilding(path, self.out("b"), 1).getOutput(0)
        with arcpy.da.SearchCursor(out, ["BLD_STATUS", "SHAPE@AREA"]) as rows:
            got = [tuple(r) for r in rows]
        self.assertEqual([g[0] for g in got], [2, 5, 3])
        self.assertAlmostEqual(got[0][1], 200.0)
        self.assertAlmostEqual(got[1][1], 300.0)

    def test_collapse_dual_lines_to_centerline_is_the_medial_axis(self):
        # Casings y = 0 and y = 10 from x = 0 to 100: their medial axis is y = 5. The first casing runs east, so the one
        # at y = 10 is on the left. A line far away stays unpaired: LnType 2.
        path = self.make("casings", [("a", line((0, 0), (100, 0))), ("b", line((0, 10), (100, 10))),
                                     ("c", line((0, 500), (100, 500)))])
        out = cartography.CollapseDualLinesToCenterline(path, self.out("cl"), 20).getOutput(0)
        with arcpy.da.SearchCursor(out, ["LnType", "LeftLn_FID", "RightLn_FID", "SHAPE@"]) as rows:
            got = [tuple(r) for r in rows]
        centre = [g for g in got if g[0] == 1]
        self.assertEqual([(g[1], g[2]) for g in centre], [(2, 1)])
        ys = {round(c[1], 9) for c in centre[0][3]._shape.coords}
        self.assertEqual(ys, {5.0})
        self.assertGreater(centre[0][3].length, 90)
        self.assertEqual([g[:3] for g in got if g[0] == 2], [(2, 0, 0)])


if __name__ == "__main__":
    unittest.main()
