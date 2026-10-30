"""Each topology rule not covered by test_topology.py against one hand-worked violation (ADR-225 condition 1)."""
import os
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _gpkg  # noqa: E402


def box(x0, y0, x1, y1):
    return f"POLYGON(({x0} {y0},{x1} {y0},{x1} {y1},{x0} {y1},{x0} {y0}))"


_SHAPES = {"area": "POLYGON", "line": "POLYLINE", "point": "POINT"}


class Rules(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "rules")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "fds", 3857)[0]
        self.count = 0

    def errors(self, rule, first, second=None):
        """Errors of one rule over classes made from (kind, [wkt …]) — as (kind, origin id, destination id, shape)."""
        self.count += 1
        made = []
        for i, (kind, rows) in enumerate([c for c in (first, second) if c]):
            path = arcpy.management.CreateFeatureclass(self.fds, f"c{self.count}_{i}", _SHAPES[kind])[0]
            with arcpy.da.InsertCursor(path, ["SHAPE@"]) as written:
                for row in rows:
                    written.insertRow((row,))
            made.append(path)
        topo = arcpy.management.CreateTopology(self.fds, f"t{self.count}")[0]
        for path in made:
            arcpy.management.AddFeatureClassToTopology(topo, path, 1, 1)
        arcpy.management.AddRuleToTopology(topo, rule, made[0], None, made[1] if len(made) > 1 else None)
        arcpy.management.ValidateTopology(topo)
        connection = sqlite3.connect(self.gpkg)
        try:
            rows = connection.execute("select kind, origin_oid, destination_oid, shape from grapy_topology_error where "
                                      "topology = ? order by error_id", (f"t{self.count}",)).fetchall()
        finally:
            connection.close()
        return [(k, o, d, _gpkg.decode(s)) for k, o, d, s in rows]

    def summary(self, found):
        return sorted((k, o, d, round(s.area if k == "area" else s.length if k == "line" else 0, 9),
                       (round(s.x, 9), round(s.y, 9)) if k == "point" else None) for k, o, d, s in found)

    # ---- one class

    def test_must_not_overlap_line(self):
        found = self.errors("Must Not Overlap (Line)", ("line", ["LINESTRING(0 0,10 0)", "LINESTRING(5 0,15 0)"]))
        self.assertEqual(self.summary(found), [("line", 1, 2, 5.0, None)])        # they share 5..10

    def test_must_not_intersect_or_touch_interior_line(self):
        # l2 ends on l1's interior at (5, 0): touching an interior is a violation here.
        found = self.errors("Must Not Intersect Or Touch Interior (Line)",
                            ("line", ["LINESTRING(0 0,10 0)", "LINESTRING(5 0,5 5)"]))
        self.assertEqual(self.summary(found), [("point", 1, 2, 0, (5.0, 0.0))])

    def test_must_not_self_overlap_line(self):
        found = self.errors("Must Not Self-Overlap (Line)", ("line", ["LINESTRING(0 0,10 0,5 0)"]))
        self.assertEqual(self.summary(found), [("line", 1, 0, 5.0, None)])        # back over 5..10

    def test_must_not_self_intersect_line(self):
        found = self.errors("Must Not Self-Intersect (Line)", ("line", ["LINESTRING(0 0,10 10,10 0,0 10)"]))
        self.assertEqual(self.summary(found), [("point", 1, 0, 0, (5.0, 5.0))])

    def test_must_be_single_part_line(self):
        found = self.errors("Must Be Single Part (Line)",
                            ("line", ["MULTILINESTRING((0 0,1 0),(2 0,3 0))", "LINESTRING(0 5,1 5)"]))
        self.assertEqual(self.summary(found), [("line", 1, 0, 2.0, None)])

    def test_must_be_disjoint_point(self):
        found = self.errors("Must Be Disjoint (Point)", ("point", ["POINT(1 1)", "POINT(1 1)", "POINT(2 2)"]))
        self.assertEqual(self.summary(found), [("point", 1, 2, 0, (1.0, 1.0))])

    # ---- two classes of areas

    def test_must_not_overlap_with_area_area(self):
        found = self.errors("Must Not Overlap With (Area-Area)", ("area", [box(0, 0, 10, 10)]), ("area", [box(5, 0, 15, 10)]))
        self.assertEqual(self.summary(found), [("area", 1, 1, 50.0, None)])

    def test_must_be_covered_by_feature_class_of_area_area(self):
        found = self.errors("Must Be Covered By Feature Class Of (Area-Area)", ("area", [box(0, 0, 10, 10)]),
                            ("area", [box(0, 0, 6, 10), box(6, 0, 8, 10)]))
        self.assertEqual(self.summary(found), [("area", 1, 0, 20.0, None)])       # 8..10 by 10 uncovered

    def test_must_cover_each_other_area_area(self):
        found = self.errors("Must Cover Each Other (Area-Area)", ("area", [box(0, 0, 10, 10)]),
                            ("area", [box(0, 0, 10, 5), box(20, 0, 21, 1)]))
        # The first's upper half has no cover; the second's small square has none either.
        self.assertEqual(self.summary(found), [("area", 0, 2, 1.0, None), ("area", 1, 0, 50.0, None)])

    def test_must_be_covered_by_area_area(self):
        # a2 lies across b1 and b2: covered by the class, but by no single polygon.
        found = self.errors("Must Be Covered By (Area-Area)", ("area", [box(0, 0, 4, 4), box(4, 0, 6, 4)]),
                            ("area", [box(0, 0, 5, 5), box(5, 0, 10, 5)]))
        self.assertEqual(self.summary(found), [("area", 2, 0, 8.0, None)])

    def test_boundary_must_be_covered_by_boundary_of_area_area(self):
        found = self.errors("Boundary Must Be Covered By Boundary Of (Area-Area)", ("area", [box(0, 0, 10, 10)]),
                            ("area", [box(0, 0, 10, 5)]))
        # Uncovered: the left and right edges from 5 to 10 (5 each) and the top (10).
        self.assertEqual(sum(s.length for _, _, _, s in found), 20.0)
        self.assertEqual({(k, o) for k, o, _, _ in found}, {("line", 1)})

    def test_boundary_must_be_covered_by_area_line(self):
        found = self.errors("Boundary Must Be Covered By (Area-Line)", ("area", [box(0, 0, 10, 10)]),
                            ("line", ["LINESTRING(0 0,10 0,10 10)"]))
        self.assertEqual(sum(s.length for _, _, _, s in found), 20.0)               # the top and the left edge

    def test_contains_point_area_point(self):
        found = self.errors("Contains Point (Area-Point)", ("area", [box(0, 0, 10, 10), box(20, 0, 30, 10)]),
                            ("point", ["POINT(5 5)"]))
        self.assertEqual(self.summary(found), [("area", 2, 0, 100.0, None)])

    def test_contains_one_point_area_point(self):
        found = self.errors("Contains One Point (Area-Point)", ("area", [box(0, 0, 10, 10), box(20, 0, 30, 10)]),
                            ("point", ["POINT(1 1)", "POINT(2 2)", "POINT(25 5)", "POINT(50 50)"]))
        # The first square holds two points; the fourth point lies in no square.
        self.assertEqual(self.summary(found), [("area", 1, 0, 100.0, None), ("point", 0, 4, 0, (50.0, 50.0))])

    # ---- lines against areas, lines and points

    def test_must_be_covered_by_boundary_of_line_area(self):
        found = self.errors("Must Be Covered By Boundary Of (Line-Area)", ("line", ["LINESTRING(0 0,20 0)"]),
                            ("area", [box(0, 0, 10, 10)]))
        self.assertEqual(self.summary(found), [("line", 1, 0, 10.0, None)])

    def test_must_be_inside_line_area(self):
        found = self.errors("Must Be Inside (Line-Area)", ("line", ["LINESTRING(5 5,15 5)"]), ("area", [box(0, 0, 10, 10)]))
        self.assertEqual(self.summary(found), [("line", 1, 0, 5.0, None)])

    def test_must_be_covered_by_feature_class_of_line_line(self):
        found = self.errors("Must Be Covered By Feature Class Of (Line-Line)", ("line", ["LINESTRING(0 0,10 0)"]),
                            ("line", ["LINESTRING(0 0,4 0)"]))
        self.assertEqual(self.summary(found), [("line", 1, 0, 6.0, None)])

    def test_must_not_overlap_with_line_line(self):
        found = self.errors("Must Not Overlap With (Line-Line)", ("line", ["LINESTRING(0 0,10 0)"]),
                            ("line", ["LINESTRING(6 0,12 0)"]))
        self.assertEqual(self.summary(found), [("line", 1, 1, 4.0, None)])

    _CROSSING = ("line", ["LINESTRING(5 -5,5 5)", "LINESTRING(10 0,10 5)", "LINESTRING(2 0,2 5)"])

    def test_must_not_intersect_with_line_line(self):
        # A crossing at (5, 0) is an error; ending on the other line, end to end or at an interior, is allowed.
        found = self.errors("Must Not Intersect With (Line-Line)", ("line", ["LINESTRING(0 0,10 0)"]), self._CROSSING)
        self.assertEqual(self.summary(found), [("point", 1, 1, 0, (5.0, 0.0))])

    def test_must_not_intersect_or_touch_interior_with_line_line(self):
        # Here an end on the other's interior is an error too: (2, 0). End to end at (10, 0) is not.
        found = self.errors("Must Not Intersect or Touch Interior With (Line-Line)", ("line", ["LINESTRING(0 0,10 0)"]),
                            self._CROSSING)
        self.assertEqual(self.summary(found), [("point", 1, 1, 0, (5.0, 0.0)), ("point", 1, 3, 0, (2.0, 0.0))])

    def test_endpoint_must_be_covered_by_line_point(self):
        found = self.errors("Endpoint Must Be Covered By (Line-Point)", ("line", ["LINESTRING(0 0,10 0)"]),
                            ("point", ["POINT(0 0)"]))
        self.assertEqual(self.summary(found), [("point", 1, 0, 0, (10.0, 0.0))])

    # ---- points against areas, lines and points

    def test_must_be_covered_by_boundary_of_point_area(self):
        found = self.errors("Must Be Covered By Boundary Of (Point-Area)", ("point", ["POINT(0 5)", "POINT(5 5)"]),
                            ("area", [box(0, 0, 10, 10)]))
        self.assertEqual(self.summary(found), [("point", 2, 0, 0, (5.0, 5.0))])

    def test_must_be_covered_by_point_line(self):
        found = self.errors("Must Be Covered By (Point-Line)", ("point", ["POINT(5 0)", "POINT(5 1)"]),
                            ("line", ["LINESTRING(0 0,10 0)"]))
        self.assertEqual(self.summary(found), [("point", 2, 0, 0, (5.0, 1.0))])

    def test_must_be_covered_by_endpoint_of_point_line(self):
        found = self.errors("Must Be Covered By Endpoint Of (Point-Line)", ("point", ["POINT(0 0)", "POINT(5 0)"]),
                            ("line", ["LINESTRING(0 0,10 0)"]))
        self.assertEqual(self.summary(found), [("point", 2, 0, 0, (5.0, 0.0))])

    def test_must_coincide_with_point_point(self):
        found = self.errors("Must Coincide With (Point-Point)", ("point", ["POINT(1 1)", "POINT(2 2)"]),
                            ("point", ["POINT(1 1)"]))
        self.assertEqual(self.summary(found), [("point", 2, 0, 0, (2.0, 2.0))])


if __name__ == "__main__":
    unittest.main()
