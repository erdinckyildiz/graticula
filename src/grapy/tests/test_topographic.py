"""arcpy.topographic's editing tools and CalculateMetrics, against hand-worked answers (ADR-251).

Every class is in EPSG:32633 (metres) unless a test says otherwise; the numbers in the comments are worked by hand
from the coordinates written.
"""
import getpass
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import shapely  # noqa: E402
from shapely.geometry import LineString, Polygon  # noqa: E402

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _layers  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

UTM = 32633


def square(x0, y0, x1, y1):
    return Polygon([(x0, y0), (x1, y0), (x1, y1), (x0, y1)])


class Fixture(unittest.TestCase):
    def setUp(self):
        _layers.LAYERS.clear()
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "topo")[0]

    def fc(self, name, kind, fields=(), rows=(), srid=UTM):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, kind, spatial_reference=srid)[0]
        for field, field_type in fields:
            arcpy.management.AddField(path, field, field_type)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as cursor:
            for row in rows:
                cursor.insertRow(list(row))
        return path

    def table(self, name, fields, rows):
        path = arcpy.management.CreateTable(self.gpkg, name)[0]
        for field, field_type in fields:
            arcpy.management.AddField(path, field, field_type)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields]) as cursor:
            for row in rows:
                cursor.insertRow(list(row))
        return path

    def rows(self, path, fields=()):
        with arcpy.da.SearchCursor(path, ["OID@", *fields, "SHAPE@WKB"]) as cursor:
            return [(r[0], *r[1:-1], shapely.from_wkb(bytes(r[-1])) if r[-1] else None) for r in cursor]


class FeatureLevelMetadata(Fixture):
    def setUp(self):
        super().setUp()
        self.metadata = self.table("FeatureLevelMetadata", [("MetadataFavoriteName", "TEXT"), ("MetadataTokens", "TEXT"),
                                                            ("SOURCE", "TEXT"), ("ACC", "LONG"),
                                                            ("created_by", "TEXT")],
                                   [("Survey", json.dumps([{"created_by": "USER"}]), "GPS", 5, None),
                                    ("Office", None, "Scan", 50, "nobody")])

    def test_the_favourite_and_its_token_are_written(self):
        roads = self.fc("Roads", "POLYLINE", [("SOURCE", "TEXT"), ("ACC", "LONG"), ("created_by", "TEXT")],
                        [("old", 1, "x", LineString([(0, 0), (1, 0)])), ("old", 1, "x", LineString([(0, 1), (1, 1)]))])
        arcpy.topographic.ApplyFeatureLevelMetadata(roads, self.metadata, "Survey")
        # Every feature: SOURCE and ACC from the Survey row; created_by from the USER token, the login in capitals as
        # editor tracking records it (ADR-240 §5.6 (b)).
        self.assertEqual([r[1:4] for r in self.rows(roads, ["SOURCE", "ACC", "created_by"])],
                         [("GPS", 5, getpass.getuser().upper())] * 2)

    def test_only_the_selection_of_a_layer_changes(self):
        roads = self.fc("Roads", "POLYLINE", [("SOURCE", "TEXT")],
                        [("a", LineString([(0, 0), (1, 0)])), ("b", LineString([(0, 1), (1, 1)]))])
        arcpy.management.MakeFeatureLayer(roads, "roads_lyr")
        arcpy.management.SelectLayerByAttribute("roads_lyr", "NEW_SELECTION", "SOURCE = 'b'")
        arcpy.topographic.ApplyFeatureLevelMetadata("roads_lyr", self.metadata, "Office")
        self.assertEqual([r[1] for r in self.rows(roads, ["SOURCE"])], ["a", "Scan"])

    def test_a_field_of_another_type_stops_before_any_change(self):
        roads = self.fc("Roads", "POLYLINE", [("SOURCE", "TEXT"), ("ACC", "TEXT")],
                        [("old", "1", LineString([(0, 0), (1, 0)]))])
        with self.assertRaises(ExecuteError):
            arcpy.topographic.ApplyFeatureLevelMetadata(roads, self.metadata, "Survey")
        self.assertEqual(self.rows(roads, ["SOURCE"])[0][1], "old")

    def test_an_unknown_favourite_is_an_error(self):
        roads = self.fc("Roads", "POLYLINE", [("SOURCE", "TEXT")], [("a", LineString([(0, 0), (1, 0)]))])
        with self.assertRaises(ExecuteError):
            arcpy.topographic.ApplyFeatureLevelMetadata(roads, self.metadata, "Nothing")


class DefaultValues(Fixture):
    def test_nulls_take_the_default_and_values_stay(self):
        path = self.fc("Poles", "POINT", [("CAT", "LONG")], [(None, (0, 0)), (3, (1, 1))])
        arcpy.management.AssignDefaultToField(path, "CAT", 7)
        arcpy.topographic.CalculateDefaultValues(path)
        self.assertEqual([r[1] for r in self.rows(path, ["CAT"])], [7, 3])


class Eliminate(Fixture):
    def test_the_sliver_goes_to_the_longest_shared_boundary(self):
        # A (0,0)-(10,10) and B (10,0)-(20,10), 100 m² each; the sliver C (0,10)-(15,11), 15 m², shares 10 m with A
        # and 5 m with B: under 20 m², it joins A, which grows to 115 m², and is deleted.
        path = self.fc("Land", "POLYGON", [], [(square(0, 0, 10, 10),), (square(10, 0, 20, 10),),
                                               (square(0, 10, 15, 11),)])
        arcpy.topographic.EliminatePolygon(path, path, 20)
        found = {oid: shape.area for oid, shape in self.rows(path)}
        self.assertEqual(sorted(found), [1, 2])
        self.assertAlmostEqual(found[1], 115.0, places=6)
        self.assertAlmostEqual(found[2], 100.0, places=6)


class Gaps(Fixture):
    def test_a_hole_of_one_feature_is_filled(self):
        # The hole (4,4)-(6,6) is 4 m², within 5: the feature becomes the whole 100 m² square.
        holed = Polygon(square(0, 0, 10, 10).exterior.coords, [square(4, 4, 6, 6).exterior.coords])
        path = self.fc("Land", "POLYGON", [], [(holed,)])
        arcpy.topographic.FillGaps(path, 5)
        self.assertAlmostEqual(self.rows(path)[0][1].area, 100.0, places=6)

    def test_by_length_and_by_order(self):
        # The gap (10,10)-(12,20) is 20 m²: A and B border it along 10 m each, C and D along 2 m each. By length A and B
        # tie, and A has the lower object ID: A grows from 300 to 320 m². By order, the earlier input (C, D) takes it:
        # C and D tie at 2 m, and C has the lower object ID.
        def build(suffix):
            ab = self.fc("AB" + suffix, "POLYGON", [], [(square(0, 0, 10, 30),), (square(12, 0, 22, 30),)])
            cd = self.fc("CD" + suffix, "POLYGON", [], [(square(10, 20, 12, 30),), (square(10, 0, 12, 10),)])
            return ab, cd
        ab, cd = build("1")
        arcpy.topographic.FillGaps([ab, cd], 25)
        self.assertAlmostEqual(self.rows(ab)[0][1].area, 320.0, places=6)
        ab, cd = build("2")
        arcpy.topographic.FillGaps([cd, ab], 25, "FILL_BY_ORDER")
        self.assertAlmostEqual(self.rows(cd)[0][1].area, 40.0, places=6)   # 20 + 20
        self.assertAlmostEqual(self.rows(ab)[0][1].area, 300.0, places=6)

    def test_an_unenclosed_gap_with_fill_all(self):
        # A (0,0)-(10,10) and B (11,0)-(21,10): closing by 2 m fills the strip (10,0)-(11,10), 10 m², which both
        # border along 10 m; A, the lower object ID, takes it.
        path = self.fc("Land", "POLYGON", [], [(square(0, 0, 10, 10),), (square(11, 0, 21, 10),)])
        arcpy.topographic.FillGaps(path, 15, "FILL_BY_LENGTH", "SKIP_UNENCLOSED_GAPS")
        self.assertAlmostEqual(self.rows(path)[0][1].area, 100.0, places=6)
        arcpy.topographic.FillGaps(path, 15, "FILL_BY_LENGTH", "FILL_ALL", 2)
        self.assertAlmostEqual(self.rows(path)[0][1].area, 110.0, places=6)


class PseudoNodes(Fixture):
    def test_two_lines_at_a_pseudo_node_merge_and_sum(self):
        # L1 and L2 meet at (10,0) and nowhere else: one line (0,0)-(20,0), W = 2 + 3. L3 has another TYPE, and at
        # (20,0) three lines meet, so nothing else merges.
        path = self.fc("Rail", "POLYLINE", [("TYPE", "LONG"), ("W", "LONG")],
                       [(1, 2, LineString([(0, 0), (10, 0)])), (1, 3, LineString([(10, 0), (20, 0)])),
                        (2, 4, LineString([(20, 0), (30, 0)])), (1, 5, LineString([(20, 0), (20, 10)]))])
        arcpy.topographic.MergeLinesByPseudoNode(path, "TYPE", "W SUM", "FIRST")
        found = {r[0]: (r[1], r[2].length) for r in self.rows(path, ["W"])}
        self.assertEqual(sorted(found), [1, 3, 4])
        self.assertEqual(found[1][0], 5)
        self.assertAlmostEqual(found[1][1], 20.0)


class Cutbacks(Fixture):
    def test_a_cutback_vertex_is_removed(self):
        # At (10,0) the line turns back: its neighbours (0,0) and (5,0.5) make atan(0.5/5) = 5.71°, under 10°.
        # At (5,0.5) afterwards: 180 - 5.71 - 2.86 = 171.4°, kept.
        path = self.fc("Roads", "POLYLINE", [], [(LineString([(0, 0), (10, 0), (5, 0.5), (15, 1)]),)])
        arcpy.topographic.RemoveCutbackVertices(path, 10)
        line = self.rows(path)[0][1].geoms[0]
        self.assertEqual([tuple(c) for c in line.coords], [(0, 0), (5, 0.5), (15, 1)])


class SelfIntersections(Fixture):
    LOOP = LineString([(0, 0), (10, 0), (10, 5), (5, 5), (5, -5)])

    def test_delete_and_max_length(self):
        # The line crosses its first segment at (5,0); the loop (5,0)-(10,0)-(10,5)-(5,5)-(5,0) is 20 m long.
        path = self.fc("Roads", "POLYLINE", [], [(self.LOOP,)])
        arcpy.topographic.RepairSelfIntersection(path, "DELETE", 15)
        self.assertAlmostEqual(self.rows(path)[0][1].length, self.LOOP.length)
        arcpy.topographic.RepairSelfIntersection(path, "DELETE")
        line = self.rows(path)[0][1].geoms[0]
        self.assertEqual([tuple(c) for c in line.coords], [(0, 0), (5, 0), (5, -5)])

    def test_split_keeps_the_loop_as_a_feature(self):
        path = self.fc("Roads", "POLYLINE", [("NAME", "TEXT")], [("a", self.LOOP)])
        arcpy.topographic.RepairSelfIntersection(path, "SPLIT")
        found = sorted((round(r[2].length, 6), r[1]) for r in self.rows(path, ["NAME"]))
        self.assertEqual(found, [(10.0, "a"), (20.0, "a")])

    def test_an_end_snapped_back_is_repaired_only_with_repair_ends(self):
        # A cul-de-sac, 25 m long: its end (5,0) lies on its own first segment; the loop from there is 5+5+5+5 = 20 m,
        # and (0,0)-(5,0) is left.
        line = LineString([(0, 0), (10, 0), (10, 5), (5, 5), (5, 0)])
        path = self.fc("Roads", "POLYLINE", [], [(line,)])
        arcpy.topographic.RepairSelfIntersection(path)
        self.assertAlmostEqual(self.rows(path)[0][1].length, 25.0)
        arcpy.topographic.RepairSelfIntersection(path, "DELETE", None, "REPAIR_ENDS")
        self.assertAlmostEqual(self.rows(path)[0][1].length, 5.0)


class Splitting(Fixture):
    def test_targets_split_and_cutters_gain_a_vertex(self):
        target = self.fc("Roads", "POLYLINE", [("NAME", "TEXT")], [("a", LineString([(0, 0), (20, 0)]))])
        cutter = self.fc("Cut", "POLYLINE", [], [(LineString([(10, -5), (10, 5)]),)])
        arcpy.topographic.SplitFeatures(cutter, target)   # nothing selected: a warning, nothing split
        self.assertEqual(len(self.rows(target)), 1)
        arcpy.management.MakeFeatureLayer(cutter, "cut_lyr")
        arcpy.management.SelectLayerByAttribute("cut_lyr", "NEW_SELECTION", "fid = 1")
        arcpy.topographic.SplitFeatures("cut_lyr", target)
        self.assertEqual(sorted((round(r[2].length, 6), r[1]) for r in self.rows(target, ["NAME"])),
                         [(10.0, "a"), (10.0, "a")])
        self.assertEqual([tuple(c) for c in self.rows(cutter)[0][1].geoms[0].coords], [(10, -5), (10, 0), (10, 5)])


class SmallLines(Fixture):
    def test_dangles_and_overshoots(self):
        # The main road (0,0)-(100,0); a dangle (50,0)-(50,8), 8 m; a crossing road (40,-3)-(40,30) whose 3 m below
        # the main road is an overshoot. Minimum 10 m: both go, the main road is split at 40 and 50 into three pieces.
        path = self.fc("Roads", "POLYLINE", [], [(LineString([(0, 0), (100, 0)]),), (LineString([(50, 0), (50, 8)]),),
                                                 (LineString([(40, -3), (40, 30)]),)])
        arcpy.topographic.RemoveSmallLines(path, "10 Meters")
        lengths = sorted(round(r[1].length, 6) for r in self.rows(path))
        self.assertEqual(lengths, [10.0, 30.0, 40.0, 50.0])

    def test_the_line_continuing_the_main_path_is_kept(self):
        # At (50,0): B continues A straight on (declination 0°), C turns off at 90°. Maximum angle 30°: C goes; B is
        # kept, since with A up to the first intersection it is 8 + 50 = 58 m, not under 10.
        path = self.fc("Roads", "POLYLINE", [("N", "TEXT")],
                       [("A", LineString([(0, 0), (50, 0)])), ("B", LineString([(50, 0), (58, 0)])),
                        ("C", LineString([(50, 0), (50, 5)]))])
        arcpy.topographic.RemoveSmallLines(path, 10, 30)
        self.assertEqual(sorted(r[1] for r in self.rows(path, ["N"])), ["A", "B"])


class Metrics(Fixture):
    def test_a_rectangle_is_measured(self):
        # 40 m east by 10 m north: length 40, width 10, area 400, the long side's azimuth 90°.
        path = self.fc("Blocks", "POLYGON", [("LEN", "DOUBLE"), ("WID", "DOUBLE"), ("ARA", "DOUBLE"), ("AOO", "DOUBLE")],
                       [(None, None, None, None, square(0, 0, 40, 10))])
        arcpy.topographic.CalculateMetrics(path, "LENGTH;WIDTH;AREA;ANGLE_OF_ORIENTATION")
        _, length, width, area, angle, _ = self.rows(path, ["LEN", "WID", "ARA", "AOO"])[0]
        self.assertAlmostEqual(length, 40.0, places=6)
        self.assertAlmostEqual(width, 10.0, places=6)
        self.assertAlmostEqual(area, 400.0, places=6)
        self.assertAlmostEqual(angle % 180, 90.0, places=6)

    def test_only_null_values_are_populated(self):
        path = self.fc("Blocks", "POLYGON", [("ARA", "DOUBLE")], [(5.0, square(0, 0, 40, 10)), (None, square(0, 0, 1, 1))])
        arcpy.topographic.CalculateMetrics(path, "AREA", only_populate_null_values="ONLYPOPNULLVALS")
        self.assertEqual([r[1] for r in self.rows(path, ["ARA"])], [5.0, 1.0])

    def test_mgrs_on_the_equator_at_a_central_meridian(self):
        # 15°E, 0°: UTM zone 33, band N, easting 500000 and northing 0. Zone 33 is in the third column set (S–Z):
        # column 500000 // 100000 - 1 = 4, W; odd zone, row 0, A. At 1 m: 33NWA 00000 00000.
        path = self.fc("Spots", "POINT", [("MGRS", "TEXT")], [(None, (15, 0))], srid=4326)
        arcpy.topographic.CalculateMetrics(path, "MGRS", mgrs_precision="1m (4QFJ1234567890)")
        self.assertEqual(self.rows(path, ["MGRS"])[0][1], "33NWA0000000000")
        arcpy.topographic.CalculateMetrics(path, "MGRS", mgrs_precision="100km (4QFJ)")
        self.assertEqual(self.rows(path, ["MGRS"])[0][1], "33NWA")
        with self.assertRaises(NotBuiltError) as said:
            arcpy.topographic.CalculateMetrics(path, "MGRS")
        self.assertIn("Image City Map", str(said.exception))


class Refused(unittest.TestCase):
    NAMES = ("ApplyBuildingOffsets", "ApplyElementValues", "ApplyMasksFromRules", "ApplyVisualSpecificationToMap",
             "CalculateBridgeOffsets", "MakeMasksFromRules", "PopulateMapSheetInfo", "GenerateProductLayout",
             "DownloadJobFiles", "UploadJobFiles", "UpdateTopographicProductionServiceDatabase",
             "MakeGridsAndGraticulesLayer", "ExportGeneralizedData", "ImportGeneralizationData", "ExportMetadata",
             "ImportMetadata", "UnzipCellAndImport", "GAIT")

    def test_each_refused_name_says_its_own_name(self):
        for name in self.NAMES:
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.topographic, name)("x")
            self.assertIn(f"arcpy.topographic.{name} ", str(said.exception))
        self.assertEqual(sorted(self.NAMES), sorted(arcpy.topographic._REFUSED))

    def test_every_documented_name_is_built_or_refused(self):
        import grapy._runtime as runtime
        sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools"))
        try:
            import grapy_refusals
        finally:
            sys.path.pop(0)
        row = grapy_refusals.survey()["arcpy.topographic"]
        self.assertEqual(row["unexamined"], [])
        self.assertEqual(len(row["refused"]), 18)
        self.assertEqual(row["built"], 37)
        del runtime


if __name__ == "__main__":
    unittest.main()
