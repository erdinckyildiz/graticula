"""arcpy.analysis.SummarizeWithin against hand-worked answers, with the field names the owner decided in Q-193 for the
server's own Summarize Within (ADR-222 §5 item 9).

Everything sits on the equator in Web Mercator, where x is a·λ with a WGS 84's equatorial radius: a line along y = 0 is
exactly as long on the ellipsoid as on the plane, so its lengths are worked by hand exactly. North–south, a metre of
Web Mercator at the equator is a(1 − e²)/a = 1 − e² metres of meridian (the meridian's radius of curvature there), so a
polygon there measures (1 − e²) times its planar area, to well within the places asserted.
"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class SummarizeWithin(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.scratchWorkspace = self.directory

    def path(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def layer(self, name, kind, fields, rows):
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", kind, spatial_reference=3857)
        for field, field_type in fields:
            arcpy.management.AddField(self.path(name), field, field_type)
        with arcpy.da.InsertCursor(self.path(name), [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return self.path(name)

    @staticmethod
    def box(x0, y0, x1, y1):
        return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1),
                                          arcpy.Point(x0, y1)]), arcpy.SpatialReference(3857))

    def areas(self, extra=()):
        fields = [("label", "TEXT"), *[(f, "LONG") for f in extra]]
        rows = [("P1", *[0] * len(extra), self.box(0, -10, 10, 10)), ("P2", *[0] * len(extra), self.box(10, -10, 20, 10)),
                ("P3", *[0] * len(extra), self.box(100, -10, 110, 10))]
        return self.layer("areas", "POLYGON", fields, rows)

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def test_points_counted_and_summarised_with_the_decided_names(self):
        # P1 = [0,10]×[−10,10] holds (1,1) Pop 2 a, (2,2) Pop 4 b, (3,3) Pop null a, (4,4) Pop null b and, on its
        # border with P2, (10,5) Pop 1 b; P2 holds (15,5) Pop 10 a and the border point; P3 nothing.
        # P1: point_count 5; the non-null Pop are 2, 4, 1: sum 7, mean 7/3, min 1, max 4, sample sd √((1/9 + 25/9 +
        # 16/9)/2) = √(7/3). Groups: a 2 points, b 3 — majority b, minority a; percent_points 40 and 60.
        # P2: point_count 2; Pop 10 and 1: sum 11, sd √((4.5² + 4.5²)/1) = √40.5; a and b one each — the tie goes to a.
        areas = self.areas(extra=["point_count"])
        points = self.layer("pts", "POINT", [("Pop", "DOUBLE"), ("Kind", "TEXT")],
                            [(2.0, "a", arcpy.PointGeometry(arcpy.Point(1, 1))), (4.0, "b", arcpy.PointGeometry(arcpy.Point(2, 2))),
                             (None, "a", arcpy.PointGeometry(arcpy.Point(3, 3))), (None, "b", arcpy.PointGeometry(arcpy.Point(4, 4))),
                             (1.0, "b", arcpy.PointGeometry(arcpy.Point(10, 5))),
                             (10.0, "a", arcpy.PointGeometry(arcpy.Point(15, 5)))])
        result = arcpy.analysis.SummarizeWithin(
            areas, points, self.path("out"), "KEEP_ALL", [["Pop", "Sum"], ["Pop", "Mean"], ["Pop", "Min"], ["Pop", "Max"],
                                                         ["Pop", "Stddev"]],
            group_field="Kind", add_min_maj="ADD_MIN_MAJ", add_group_percent="ADD_PERCENT",
            out_group_table=self.path("groups"))
        names = [f.name for f in arcpy.ListFields(result.getOutput(0))]
        # The polygons already hold point_count, so the count takes point_count_1, as the server's Unclaimed does.
        for name in ("point_count_1", "sum_pop", "mean_pop", "min_pop", "max_pop", "stddev_pop", "join_id",
                     "majority_kind", "minority_kind"):
            self.assertIn(name, names)
        got = self.rows(result.getOutput(0), ["label", "point_count_1", "sum_pop", "mean_pop", "min_pop", "max_pop",
                                              "stddev_pop", "majority_kind", "minority_kind"])
        p1, p2, p3 = got
        self.assertEqual(p1[:3], ("P1", 5, 7.0))
        self.assertAlmostEqual(p1[3], 7 / 3)
        self.assertEqual(p1[4:6], (1.0, 4.0))
        self.assertAlmostEqual(p1[6], math.sqrt(7 / 3))
        self.assertEqual(p1[7:], ("b", "a"))
        self.assertEqual(p2[:3], ("P2", 2, 11.0))
        self.assertAlmostEqual(p2[6], math.sqrt(40.5))
        self.assertEqual(p2[7:], ("a", "a"))
        self.assertEqual(p3, ("P3", 0, None, None, None, None, None, None, None))
        groups = self.rows(result.getOutput(1), ["join_id", "Kind", "point_count_1", "sum_pop", "percent_points"])
        self.assertEqual(groups, [(1, "a", 2, 2.0, 40.0), (1, "b", 3, 5.0, 60.0), (2, "a", 1, 10.0, 50.0),
                                  (2, "b", 1, 1.0, 50.0)])
        only = arcpy.analysis.SummarizeWithin(areas, points, self.path("only"), "ONLY_INTERSECTING")
        self.assertEqual([r[0] for r in self.rows(only.getOutput(0), ["label"])], ["P1", "P2"])

    def test_lines_in_proportion_to_their_inside_length(self):
        # L1 from (5,0) to (15,0), Len 10, half in P1 and half in P2; L2 from (2,0) to (4,0), Len 6, all in P1.
        # P1: count 2; inside length 5 + 2 = 7 m; sum 10·½ + 6·1 = 11; mean weighted by inside length
        # (10·5 + 6·2)/7 = 62/7; sd √(Σw·v²/Σw − mean²) = √(572/7 − 3844/49) = √160/7; min 6, max 10.
        # P2: count 1; 5 m; sum 5; mean 10; sd 0.
        areas = self.areas()
        line = lambda x0, x1: arcpy.Polyline(arcpy.Array([arcpy.Point(x0, 0), arcpy.Point(x1, 0)]),  # noqa: E731
                                             arcpy.SpatialReference(3857))
        lines = self.layer("lines", "POLYLINE", [("Len", "DOUBLE")], [(10.0, line(5, 15)), (6.0, line(2, 4))])
        out = arcpy.analysis.SummarizeWithin(areas, lines, self.path("lout"), sum_fields="Len Sum;Len Mean;Len Min;"
                                             "Len Max;Len Stddev").getOutput(0)
        got = self.rows(out, ["count", "sum_length_meters", "sum_len", "mean_len", "min_len", "max_len", "stddev_len"])
        for value, want in zip(got[0], [2, 7, 11, 62 / 7, 6, 10, math.sqrt(160) / 7]):
            self.assertAlmostEqual(value, want, places=6)
        for value, want in zip(got[1], [1, 5, 5, 10, 10, 10, 0]):
            self.assertAlmostEqual(value, want, places=6)
        self.assertEqual(got[2][:2], (0, 0.0))
        out = arcpy.analysis.SummarizeWithin(areas, lines, self.path("km"), shape_unit="KILOMETERS").getOutput(0)
        self.assertAlmostEqual(self.rows(out, ["sum_length_kilometers"])[0][0], 0.007, places=9)

    def test_polygons_in_proportion_to_their_inside_area(self):
        # S = [5,15]×[0,10], Pop 100: half in P1 and half in P2, so each takes half its Pop, and 50 planar m², which is
        # 50·(1 − e²) m² on the ellipsoid — e² = 0.00669437999014 for WGS 84 — or 0.005·(1 − e²) hectares.
        areas = self.areas()
        parts = self.layer("polys", "POLYGON", [("Pop", "DOUBLE")], [(100.0, self.box(5, 0, 15, 10))])
        out = arcpy.analysis.SummarizeWithin(areas, parts, self.path("pout"), sum_fields=[["Pop", "SUM"]],
                                             shape_unit="HECTARES").getOutput(0)
        got = self.rows(out, ["count", "sum_area_hectares", "sum_pop"])
        for row in got[:2]:
            self.assertEqual(row[0], 1)
            self.assertAlmostEqual(row[1], 0.005 * (1 - 0.00669437999014), places=9)
            self.assertAlmostEqual(row[2], 50.0, places=6)
        with self.assertRaisesRegex(ValueError, "shape_unit"):
            arcpy.analysis.SummarizeWithin(areas, parts, self.path("bad"), shape_unit="METERS")
        with self.assertRaisesRegex(ValueError, "out_group_table"):
            arcpy.analysis.SummarizeWithin(areas, parts, self.path("bad"), group_field="Pop")


if __name__ == "__main__":
    unittest.main()
