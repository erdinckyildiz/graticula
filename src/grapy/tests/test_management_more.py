"""grapy's management tools beyond the first batch, each against a shape whose answer is worked out by hand — ADR-224.

Squares 50 wide, 100 apart, on EPSG:3857 as in test_tools; every count, length and area below is worked out from the
coordinates written here, never read back from the code under test.
"""
import math
import os
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


def square(x, y, side=50):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x + side, y), arcpy.Point(x + side, y + side),
                                      arcpy.Point(x, y + side)]), 3857)


def box(x0, y0, x1, y1):
    return f"POLYGON(({x0} {y0},{x1} {y0},{x1} {y1},{x0} {y1},{x0} {y0}))"


class ManagementMore(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        self.parcels = self.make("parcels", "POLYGON", [("name", "TEXT"), ("kind", "TEXT"), ("value", "DOUBLE")],
                                 [("p0", "a", 1.0, square(0, 0)), ("p1", "a", 2.0, square(100, 0)),
                                  ("p2", "b", 4.0, square(200, 0))])
        self.wells = self.make("wells", "POINT", [("well", "TEXT")],
                               [("w0", "POINT(10 10)"), ("w1", "POINT(20 20)"), ("w2", "POINT(400 10)")])

    def tearDown(self):
        for name in list(arcpy.management._LAYERS):
            arcpy.management.Delete(name)

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
        path = arcpy.management.CreateTable(self.directory, f"{name}.gpkg").getOutput(0)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    def count(self, value):
        return int(arcpy.management.GetCount(value)[0])

    # ---- layers and selections

    def test_a_layer_shows_its_query_and_tools_take_its_selection(self):
        layer = arcpy.management.MakeFeatureLayer(self.parcels, "lyr", "value > 1").getOutput(0)
        self.assertEqual(layer, "lyr")
        self.assertEqual(self.count("lyr"), 2)                        # p1, p2
        result = arcpy.management.SelectLayerByAttribute("lyr", "NEW_SELECTION", "name = 'p2'")
        self.assertEqual((result[0], result[1]), ("lyr", "1"))
        self.assertEqual(self.count("lyr"), 1)
        # p0 is outside the definition query, so adding it selects nothing more.
        self.assertEqual(arcpy.management.SelectLayerByAttribute("lyr", "ADD_TO_SELECTION", "name = 'p0'")[1], "1")
        self.assertEqual(arcpy.management.SelectLayerByAttribute("lyr", "SWITCH_SELECTION")[1], "1")
        copied = arcpy.management.CopyFeatures("lyr", self.out("copied")).getOutput(0)
        self.assertEqual(self.rows(copied, ["name"]), [("p1",)])
        self.assertEqual(arcpy.management.SelectLayerByAttribute("lyr", "CLEAR_SELECTION")[1], "0")
        self.assertEqual(self.count("lyr"), 2)                        # nothing selected: everything shown
        self.assertEqual(self.count(self.parcels), 3)                 # the dataset itself is untouched

    def test_selection_types_combine_and_a_path_gets_a_layer(self):
        made = arcpy.management.SelectLayerByAttribute(self.parcels, "NEW_SELECTION", "value < 3")
        self.assertEqual((made[0], made[1]), ("parcels", "2"))       # p0, p1
        self.assertEqual(arcpy.management.SelectLayerByAttribute("parcels", "SUBSET_SELECTION", "kind = 'a' and value > 1")[1], "1")
        self.assertEqual(arcpy.management.SelectLayerByAttribute("parcels", "ADD_TO_SELECTION", "name = 'p2'")[1], "2")
        self.assertEqual(arcpy.management.SelectLayerByAttribute("parcels", "REMOVE_FROM_SELECTION", "name = 'p1'")[1], "1")
        inverted = arcpy.management.SelectLayerByAttribute("parcels", "NEW_SELECTION", "value < 3", "INVERT")
        self.assertEqual(inverted[1], "1")
        self.assertEqual(self.rows(arcpy.management.CopyFeatures("parcels", self.out("inv"))[0], ["name"]), [("p2",)])

    def test_every_module_reads_a_layers_query_and_selection_and_delete_removes_only_the_layer(self):
        # Query value > 1 shows p1 and p2; selecting kind = 'b' within it leaves p2 alone.
        arcpy.management.MakeFeatureLayer(self.parcels, "lyr", "value > 1")
        selected = arcpy.analysis.Select("lyr", self.out("sel")).getOutput(0)
        with arcpy.da.SearchCursor(selected, ["name"]) as rows:
            self.assertEqual([r[0] for r in rows], ["p1", "p2"])
        arcpy.management.SelectLayerByAttribute("lyr", "NEW_SELECTION", "kind = 'b'")
        buffered = arcpy.analysis.Buffer("lyr", self.out("buf"), "5 Meters").getOutput(0)
        with arcpy.da.SearchCursor(buffered, ["name"]) as rows:
            self.assertEqual([r[0] for r in rows], ["p2"])
        with arcpy.da.SearchCursor("lyr", ["name"], "value < 10") as rows:
            self.assertEqual([r[0] for r in rows], ["p2"])
        self.assertEqual(arcpy.Describe("lyr").shapeType, "Polygon")
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.MakeFeatureLayer(self.parcels, "bad.gpkg")
        arcpy.management.Delete("lyr")
        self.assertTrue(arcpy.Exists(self.parcels))
        with self.assertRaises(ValueError):
            arcpy.management.GetCount("lyr")

    def test_select_by_location_relations(self):
        select = arcpy.management.SelectLayerByLocation
        arcpy.management.MakeFeatureLayer(self.parcels, "p")
        self.assertEqual(select("p", "INTERSECT", self.wells)[1], "1")                         # p0 holds w0, w1
        # w1 (20, 20) is 80 from p1's left edge (100); w2 (400, 10) is 150 from p2's right edge (250).
        self.assertEqual(select("p", "WITHIN_A_DISTANCE", self.wells, "100 Meters")[1], "2")
        self.assertEqual(select("p", "WITHIN_A_DISTANCE", self.wells, "0.15 Kilometers")[1], "3")
        self.assertEqual(select("p", "INTERSECT", self.wells, "79")[1], "1")
        edge = self.make("edge", "POINT", [], [("POINT(50 25)",)])                            # on p0's right edge
        self.assertEqual(select("p", "CONTAINS", edge)[1], "1")
        self.assertEqual(select("p", "CONTAINS_CLEMENTINI", edge)[1], "0")
        self.assertEqual(select("p", "COMPLETELY_CONTAINS", self.wells)[1], "1")
        zone = self.make("zone", "POLYGON", [], [(box(30, 0, 150, 50),)])
        self.assertEqual(select("p", "INTERSECT", zone)[1], "2")                               # p0, p1
        self.assertEqual(select("p", "HAVE_THEIR_CENTER_IN", zone)[1], "1")                    # p1's (125, 25); p0's (25, 25) is out
        self.assertEqual(select("p", "WITHIN", zone)[1], "1")                                  # p1 lies inside, edges shared
        self.assertEqual(select("p", "COMPLETELY_WITHIN", zone)[1], "0")
        self.assertEqual(select("p", "SHARE_A_LINE_SEGMENT_WITH", zone)[1], "2")               # both bottoms run on y = 0
        self.assertEqual(select("p", "INTERSECT", zone, None, "NEW_SELECTION", "INVERT")[1], "1")  # p2
        self.assertEqual(select("p", "INTERSECT", self.wells, None, "ADD_TO_SELECTION")[1], "2")    # p2 + p0
        self.assertEqual(select("p", "INTERSECT", zone, None, "REMOVE_FROM_SELECTION")[1], "1")     # p2
        self.assertEqual(select("p", selection_type="SWITCH_SELECTION")[1], "2")
        twin = arcpy.management.CopyFeatures(self.parcels, self.out("twin")).getOutput(0)
        self.assertEqual(select("p", "ARE_IDENTICAL_TO", twin)[1], "3")
        arcpy.management.MakeFeatureLayer(self.wells, "w")
        self.assertEqual(select("w", "WITHIN", self.parcels)[1], "2")
        self.assertEqual(select("w", "WITHIN", self.parcels, None, "NEW_SELECTION", "INVERT")[1], "1")
        # A selecting layer's selection is what selects.
        arcpy.management.SelectLayerByAttribute("p", "NEW_SELECTION", "name = 'p2'")
        self.assertEqual(select("w", "INTERSECT", "p")[1], "0")
        with self.assertRaises(arcpy.NotBuiltError):
            select("w", "BOUNDARY_TOUCHES", self.parcels)

    # ---- tables

    def test_copy_rows_create_table_and_truncate(self):
        arcpy.management.MakeTableView(self.parcels, "view", "kind = 'a'")
        rows = arcpy.management.CopyRows("view", self.out("rows")).getOutput(0)
        self.assertEqual(self.rows(rows, ["name", "value", "SHAPE@"]), [("p0", 1.0, None), ("p1", 2.0, None)])
        made = arcpy.management.CreateTable(self.directory, "empty.gpkg", self.parcels).getOutput(0)
        self.assertEqual([f.name for f in arcpy.ListFields(made)][1:-1], ["name", "kind", "value"])
        self.assertEqual(self.count(made), 0)
        arcpy.management.TruncateTable(rows)
        self.assertEqual(self.count(rows), 0)
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CopyRows(self.parcels, os.path.join(self.directory, "rows.csv"))

    def test_join_field_takes_the_first_match_and_renames_a_taken_name(self):
        lookup = self.table("lookup", [("kind", "TEXT"), ("label", "TEXT")],
                            [("a", "alpha"), ("a", "second"), ("b", "beta")])
        arcpy.management.JoinField(self.parcels, "kind", lookup, "kind", ["label"])
        self.assertEqual(self.rows(self.parcels, ["name", "label"]), [("p0", "alpha"), ("p1", "alpha"), ("p2", "beta")])
        arcpy.management.JoinField(self.parcels, "kind", lookup, "kind")
        self.assertEqual([f.name for f in arcpy.ListFields(self.parcels)][1:-1],
                         ["name", "kind", "value", "label", "kind_1", "label_1"])
        partial = self.table("partial", [("code", "TEXT"), ("note", "TEXT")], [("b", "only b")])
        arcpy.management.JoinField(self.parcels, "kind", partial, "code", "note")
        self.assertEqual([r[0] for r in self.rows(self.parcels, ["note"])], [None, None, "only b"])

    def test_calculate_field_and_fields_on_a_selection(self):
        arcpy.management.MakeFeatureLayer(self.parcels, "lyr")
        arcpy.management.SelectLayerByAttribute("lyr", "NEW_SELECTION", "kind = 'b'")
        arcpy.management.CalculateField("lyr", "flag", "'yes'")                    # a new TEXT field, selected row only
        self.assertEqual([r[0] for r in self.rows(self.parcels, ["flag"])], [None, None, "yes"])
        arcpy.management.CalculateFields(self.parcels, "PYTHON3", [["v2", "!value! * 10", None, "DOUBLE"],
                                                                   ["tag", "size(!v2!)", "value > 1"]],
                                         "def size(v):\n    return 'big' if v > 15 else 'small'")
        self.assertEqual(self.rows(self.parcels, ["v2", "tag"]), [(10.0, None), (20.0, "big"), (40.0, "big")])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CalculateField(self.parcels, "v2", "$feature.value", "ARCADE")

    def test_sort_orders_by_each_field_in_turn(self):
        out = arcpy.management.Sort(self.parcels, self.out("sorted"), [["kind", "DESCENDING"], ["value", "ASCENDING"]])[0]
        self.assertEqual([r[0] for r in self.rows(out, ["name"])], ["p2", "p0", "p1"])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.Sort(self.parcels, self.out("s2"), [["Shape", "ASCENDING"]])

    # ---- shapes to shapes

    def test_feature_to_line_splits_where_lines_cross(self):
        lines = self.make("lines", "POLYLINE", [("lid", "LONG")],
                          [(1, "LINESTRING(0 0,100 0)"), (2, "LINESTRING(50 -50,50 50)")])
        out = arcpy.management.FeatureToLine(lines, self.out("ftl"))[0]
        got = sorted((r[0], r[1], r[2]) for r in self.rows(out, ["FID_lines", "lid", "SHAPE@LENGTH"]))
        self.assertEqual(got, [(1, 1, 50.0), (1, 1, 50.0), (2, 2, 50.0), (2, 2, 50.0)])
        bare = arcpy.management.FeatureToLine(lines, self.out("ftl2"), attributes="NO_ATTRIBUTES")[0]
        self.assertEqual([f.name for f in arcpy.ListFields(bare)][1:-1], [])
        # p0's bottom edge (0..50 on y = 0) lies on line 1: one line carries both inputs.
        both = arcpy.management.FeatureToLine([self.parcels, lines], self.out("ftl3"))[0]
        shared = self.rows(both, ["FID_parcels", "FID_lines", "SHAPE@LENGTH"], '"FID_parcels" = 1 and "FID_lines" = 1')
        self.assertEqual(shared, [(1, 1, 50.0)])

    def test_feature_to_polygon_builds_each_enclosed_area(self):
        lines = self.make("frame", "POLYLINE", [],
                          [("LINESTRING(0 0,100 0,100 100,0 100,0 0)",), ("LINESTRING(0 50,100 50)",)])
        labels = self.make("labels", "POINT", [("lab", "TEXT")], [("low", "POINT(10 10)")])
        out = arcpy.management.FeatureToPolygon(lines, self.out("ftp"), label_features=labels)[0]
        self.assertEqual(sorted(self.rows(out, ["SHAPE@AREA", "lab"]), key=lambda r: r[1] is None), [(5000.0, "low"), (5000.0, None)])
        zone = self.make("zone", "POLYGON", [("zone", "TEXT")], [("z", box(30, 0, 150, 50))])
        p0 = arcpy.analysis.Select(self.parcels, self.out("p0"), "name = 'p0'")[0]
        faces = arcpy.management.FeatureToPolygon([p0, zone], self.out("faces"))[0]
        self.assertEqual(sorted(r[0] for r in self.rows(faces, ["SHAPE@AREA"])), [1000.0, 1500.0, 5000.0])
        self.assertEqual({r for r in self.rows(faces, ["FID_p0", "FID_zone", "zone"])}, {(-1, -1, None)})

    def test_polygon_to_line_finds_neighbours(self):
        pair = self.make("pair", "POLYGON", [("n", "TEXT")], [("a", box(0, 0, 50, 50)), ("b", box(50, 0, 100, 50))])
        out = arcpy.management.PolygonToLine(pair, self.out("ptl"))[0]
        got = sorted((r[0], r[1], r[2]) for r in self.rows(out, ["LEFT_FID", "RIGHT_FID", "SHAPE@LENGTH"]))
        self.assertEqual(got[:2], [(-1, 1, 150.0), (-1, 2, 150.0)])
        self.assertEqual((sorted(got[2][:2]), got[2][2]), ([1, 2], 50.0))
        # a's outer line runs clockwise, a on its right: from (50, 0) west, up, and east to (50, 50).
        outer = self.rows(out, ["SHAPE@WKT"], '"RIGHT_FID" = 1 and "LEFT_FID" = -1')[0][0]
        self.assertEqual(outer, "LINESTRING (50 0, 0 0, 0 50, 50 50)")
        rings = arcpy.management.PolygonToLine(pair, self.out("rings"), "IGNORE_NEIGHBORS")[0]
        self.assertEqual(self.rows(rings, ["n", "ORIG_FID", "SHAPE@LENGTH"]), [("a", 1, 200.0), ("b", 2, 200.0)])

    def test_split_line_at_vertices_and_at_points(self):
        lines = self.make("route", "POLYLINE", [("r", "TEXT")], [("x", "LINESTRING(0 0,10 0,10 10,20 10)"),
                                                                ("y", "LINESTRING(0 0,100 0)")])
        out = arcpy.management.SplitLine(lines, self.out("split"))[0]
        self.assertEqual(self.rows(out, ["r", "ORIG_FID", "ORIG_SEQ", "SHAPE@LENGTH"]),
                         [("x", 1, 1, 10.0), ("x", 1, 2, 10.0), ("x", 1, 3, 10.0), ("y", 2, 1, 100.0)])
        self.assertEqual(self.count(arcpy.management.SplitLine(self.parcels, self.out("sides"))[0]), 12)
        points = self.make("cuts", "POINT", [], [("POINT(30 5)",), ("POINT(70 -2)",), ("POINT(50 50)",)])
        only_y = arcpy.analysis.Select(lines, self.out("y"), "r = 'y'")[0]
        cut = arcpy.management.SplitLineAtPoint(only_y, points, self.out("cut"), "10 Meters")[0]
        self.assertEqual(self.rows(cut, ["ORIG_SEQ", "SHAPE@LENGTH"]), [(1, 30.0), (2, 40.0), (3, 30.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.SplitLineAtPoint(only_y, points, self.out("cut2"))

    def test_points_to_line(self):
        stops = self.make("stops", "POINT", [("route", "TEXT"), ("seq", "LONG")],
                          [("r1", 2, "POINT(0 0)"), ("r1", 1, "POINT(10 0)"), ("r1", 3, "POINT(10 10)"),
                           ("r2", 1, "POINT(0 0)")])
        diagonal = math.sqrt(200)
        line = arcpy.management.PointsToLine(stops, self.out("ptl"), "route", "seq")[0]
        got = self.rows(line, ["route", "SHAPE@LENGTH"])
        self.assertEqual(len(got), 1)                                     # r2 has one point and makes no line
        self.assertAlmostEqual(got[0][1], 10 + diagonal)
        closed = arcpy.management.PointsToLine(stops, self.out("closed"), "route", "seq", "CLOSE")[0]
        self.assertAlmostEqual(self.rows(closed, ["SHAPE@LENGTH"])[0][0], 10 + diagonal + 10)
        pairs = arcpy.management.PointsToLine(stops, self.out("pairs"), "route", "seq", "NO_CLOSE", "TWO_POINT",
                                              "BOTH_ENDS", ["seq"])[0]
        self.assertEqual([r[:2] for r in self.rows(pairs, ["START_seq", "END_seq", "SHAPE@LENGTH"])], [(1, 2), (2, 3)])

    def test_feature_envelopes_and_points_along_lines(self):
        lines = self.make("ln", "POLYLINE", [("k", "TEXT")],
                          [("bent", "LINESTRING(0 0,10 0,10 10)"), ("flat", "LINESTRING(0 0,10 0)"),
                           ("two", "MULTILINESTRING((0 0,10 10),(20 20,25 30))")])
        boxes = arcpy.management.FeatureEnvelopeToPolygon(lines, self.out("env"))[0]
        self.assertEqual(self.rows(boxes, ["k", "ORIG_FID", "SHAPE@AREA"]), [("bent", 1, 100.0), ("two", 3, 750.0)])
        parts = arcpy.management.FeatureEnvelopeToPolygon(lines, self.out("env2"), "MULTIPART")[0]
        self.assertEqual(self.rows(parts, ["SHAPE@AREA"], "k = 'two'"), [(150.0,)])
        line = self.make("long", "POLYLINE", [], [("LINESTRING(0 0,100 0)",)])
        along = arcpy.management.GeneratePointsAlongLines
        self.assertEqual([r[0] for r in self.rows(along(line, self.out("d"), "DISTANCE", "30 Meters")[0], ["SHAPE@X"])],
                         [30.0, 60.0, 90.0])
        ends = along(line, self.out("e"), "DISTANCE", "30 Meters", Include_End_Points="END_POINTS",
                     Add_Chainage_Fields="ADD_CHAINAGE")[0]
        self.assertEqual(self.rows(ends, ["ORIG_LEN", "ORIG_SEQ", "ORIG_FID"]),
                         [(0.0, 1, 1), (30.0, 2, 1), (60.0, 3, 1), (90.0, 4, 1), (100.0, 5, 1)])
        self.assertEqual([r[0] for r in self.rows(along(line, self.out("p"), "PERCENTAGE", Percentage=40)[0], ["SHAPE@X"])],
                         [40.0, 80.0])

    def test_unsplit_line_joins_through_ends_where_two_meet(self):
        lines = self.make("parts", "POLYLINE", [("k", "TEXT"), ("v", "LONG")],
                          [("a", 1, "LINESTRING(0 0,10 0)"), ("a", 2, "LINESTRING(10 0,20 0)"), ("b", 4, "LINESTRING(20 0,30 0)")])
        out = arcpy.management.UnsplitLine(lines, self.out("un"), "k", [["v", "SUM"]])[0]
        self.assertEqual(sorted(self.rows(out, ["k", "SUM_v", "SHAPE@LENGTH"])), [("a", 3, 20.0), ("b", 4, 10.0)])
        self.assertEqual(self.rows(arcpy.management.UnsplitLine(lines, self.out("all"))[0], ["SHAPE@LENGTH"]), [(30.0,)])
        with arcpy.da.InsertCursor(lines, ["k", "v", "SHAPE@"]) as written:
            written.insertRow(("a", 8, "LINESTRING(10 0,10 10)"))          # three lines now meet at (10, 0)
        self.assertEqual(sorted(r[0] for r in self.rows(arcpy.management.UnsplitLine(lines, self.out("t"))[0], ["SHAPE@LENGTH"])),
                         [10.0, 10.0, 20.0])

    # ---- tables to shapes

    def test_xy_table_to_point(self):
        xy = self.table("xy", [("x", "DOUBLE"), ("y", "DOUBLE"), ("name", "TEXT")],
                        [(100.0, 20.0, "a"), (None, 5.0, "b"), (500.0, 10.0, "c")])
        out = arcpy.management.XYTableToPoint(xy, self.out("pts"), "x", "y")[0]
        self.assertEqual(arcpy.Describe(out).spatialReference.factoryCode, 4326)
        got = self.rows(out, ["name", "SHAPE@WKT"])
        self.assertEqual(got, [("a", "POINT (100 20)"), ("b", "POINT EMPTY")])     # c's x of 500 is out of range
        projected = arcpy.management.XYTableToPoint(xy, self.out("pts2"), "x", "y", coordinate_system=3857)[0]
        self.assertEqual(self.count(projected), 3)

    def test_xy_and_bearing_distance_to_line(self):
        ends = self.table("ends", [("sx", "DOUBLE"), ("sy", "DOUBLE"), ("ex", "DOUBLE"), ("ey", "DOUBLE"), ("id", "TEXT"),
                                   ("note", "TEXT")], [(0.0, 0.0, 30.0, 40.0, "a", "n")])
        out = arcpy.management.XYToLine(ends, self.out("xyl"), "sx", "sy", "ex", "ey", "PLANAR", "id", 3857, "ATTRIBUTES")[0]
        self.assertEqual(self.rows(out, ["id", "note", "ORIG_FID", "SHAPE@LENGTH"]), [("a", "n", 1, 50.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.XYToLine(ends, self.out("geo"), "sx", "sy", "ex", "ey")
        legs = self.table("legs", [("x", "DOUBLE"), ("y", "DOUBLE"), ("d", "DOUBLE"), ("b", "DOUBLE")],
                          [(0.0, 0.0, 100.0, 90.0), (10.0, 10.0, 0.1, 0.0)])
        lines = arcpy.management.BearingDistanceToLine(legs, self.out("bdl"), "x", "y", "d", None, "b", "DEGREES", "PLANAR",
                                                       spatial_reference=3857)[0]
        ends_at = [r[0]._shape.coords[-1] for r in self.rows(lines, ["SHAPE@"])]
        self.assertAlmostEqual(ends_at[0][0], 100.0)
        self.assertAlmostEqual(ends_at[0][1], 0.0)
        self.assertAlmostEqual(ends_at[1][0], 10.0)
        self.assertAlmostEqual(ends_at[1][1], 10.1)
        km = arcpy.management.BearingDistanceToLine(legs, self.out("km"), "x", "y", "d", "KILOMETERS", "b", "DEGREES",
                                                    "PLANAR", spatial_reference=3857)[0]
        self.assertAlmostEqual(self.rows(km, ["SHAPE@LENGTH"])[1][0], 100.0)      # 0.1 km
        grads = self.table("grads", [("x", "DOUBLE"), ("y", "DOUBLE"), ("d", "DOUBLE"), ("b", "DOUBLE")], [(0.0, 0.0, 10.0, 100.0)])
        east = arcpy.management.BearingDistanceToLine(grads, self.out("gr"), "x", "y", "d", None, "b", "GRADS", "PLANAR",
                                                      spatial_reference=3857)[0]
        end = self.rows(east, ["SHAPE@"])[0][0]._shape.coords[-1]
        self.assertAlmostEqual(end[0], 10.0)
        self.assertAlmostEqual(end[1], 0.0)
        self.assertEqual([f.name for f in arcpy.ListFields(east)][1:-1], ["x", "y", "d", "b"])

    # ---- grids

    def test_create_fishnet(self):
        net = arcpy.management.CreateFishnet(self.out("net"), "0 0", "0 10", 10, 20, 2, 3, geometry_type="POLYGON")
        self.assertEqual([r[0] for r in self.rows(net[0], ["SHAPE@AREA"])], [200.0] * 6)
        self.assertEqual(self.rows(net[1], ["SHAPE@XY"])[:2], [((5.0, 10.0),), ((15.0, 10.0),)])
        from_corner = arcpy.management.CreateFishnet(self.out("net2"), "0 0", "0 10", 0, 0, 2, 3, "30 40",
                                                     "NO_LABELS", geometry_type="POLYGON")
        self.assertEqual(from_corner.outputCount, 1)
        self.assertEqual(sorted({round(r[0], 9) for r in self.rows(from_corner[0], ["SHAPE@AREA"])}), [200.0])
        # The y-axis points east: the x-axis then points south, so the first centre is (10, -5).
        turned = arcpy.management.CreateFishnet(self.out("net3"), "0 0", "10 0", 10, 20, 1, 1, geometry_type="POLYGON")
        x, y = self.rows(turned[1], ["SHAPE@XY"])[0][0]
        self.assertAlmostEqual(x, 10.0)
        self.assertAlmostEqual(y, -5.0)
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CreateFishnet(self.out("net4"), "0 0", "0 10", 10, 20, 2, 3)

    def test_generate_tessellation(self):
        grid = arcpy.management.GenerateTessellation(self.out("sq"), "0 0 25 15", "SQUARE", "100 SquareMeters", 3857)[0]
        got = self.rows(grid, ["GRID_ID", "SHAPE@AREA"])
        self.assertEqual([g for g, _ in got], ["A-1", "A-2", "A-3", "B-1", "B-2", "B-3"])
        self.assertEqual({round(a, 9) for _, a in got}, {100.0})
        hexes = arcpy.management.GenerateTessellation(self.out("hx"), "0 0 25 15", "HEXAGON", "100 SquareMeters", 3857)[0]
        cells = [r[0]._shape for r in self.rows(hexes, ["SHAPE@"])]
        self.assertTrue(all(abs(c.area - 100.0) < 1e-9 for c in cells))
        import shapely
        from shapely.geometry import box as shapely_box
        self.assertAlmostEqual(shapely.union_all(cells).intersection(shapely_box(0, 0, 25, 15)).area, 375.0)
        flat = cells[0]
        ys = sorted({round(y, 9) for _, y in flat.exterior.coords})
        self.assertEqual(len(ys), 3)                       # a flat-topped hexagon has a top, a middle and a bottom
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.GenerateTessellation(self.out("h3"), "0 0 25 15", "H3_HEXAGON", "100 SquareMeters", 3857)

    # ---- cleaning

    def test_eliminate_merges_a_sliver_into_its_neighbour(self):
        tiles = self.make("tiles", "POLYGON", [("t", "TEXT")],
                          [("A", box(0, 0, 100, 100)), ("B", box(100, 0, 250, 100)), ("S", box(0, 100, 150, 110))])
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.Eliminate(tiles, self.out("none"))
        arcpy.management.MakeFeatureLayer(tiles, "tiles_lyr")
        arcpy.management.SelectLayerByAttribute("tiles_lyr", "NEW_SELECTION", "t = 'S'")
        # S borders A for 100 and B for 50; B (15000) is larger than A (10000). S is 150 × 10.
        by_length = arcpy.management.Eliminate("tiles_lyr", self.out("el"))[0]
        self.assertEqual(self.rows(by_length, ["t", "SHAPE@AREA"]), [("A", 11500.0), ("B", 15000.0)])
        by_area = arcpy.management.Eliminate("tiles_lyr", self.out("ea"), "AREA")[0]
        self.assertEqual(self.rows(by_area, ["t", "SHAPE@AREA"]), [("A", 10000.0), ("B", 16500.0)])
        kept = arcpy.management.Eliminate("tiles_lyr", self.out("ek"), "LENGTH", "t = 'A'")[0]
        self.assertEqual(self.rows(kept, ["t", "SHAPE@AREA"]), [("A", 10000.0), ("B", 16500.0)])

    def test_find_and_delete_identical(self):
        found = arcpy.management.FindIdentical(self.parcels, self.out("fi"), "kind")[0]
        self.assertEqual(self.rows(found, ["IN_FID", "FEAT_SEQ"]), [(1, 1), (2, 1), (3, 2)])
        dupes = arcpy.management.FindIdentical(self.parcels, self.out("fd"), ["kind"], output_record_option="ONLY_DUPLICATES")[0]
        self.assertEqual(self.rows(dupes, ["IN_FID", "FEAT_SEQ"]), [(1, 1), (2, 1)])
        with arcpy.da.InsertCursor(self.parcels, ["name", "kind", "SHAPE@"]) as written:
            # p0's square again, its ring starting at another corner.
            written.insertRow(("copy", "c", "POLYGON((50 50,0 50,0 0,50 0,50 50))"))
        shapes = arcpy.management.FindIdentical(self.parcels, self.out("fs"), "Shape")[0]
        self.assertEqual(self.rows(shapes, ["IN_FID", "FEAT_SEQ"]), [(1, 1), (2, 2), (3, 3), (4, 1)])
        arcpy.management.DeleteIdentical(self.parcels, ["kind"])
        self.assertEqual(self.rows(self.parcels, ["name"]), [("p0",), ("p2",), ("copy",)])

    def test_check_geometry_names_each_problem(self):
        with arcpy.da.InsertCursor(self.parcels, ["name", "SHAPE@"]) as written:
            written.insertRow(("bow", "POLYGON((0 0,10 10,10 0,0 10,0 0))"))
            written.insertRow(("null", None))
            written.insertRow(("dup", "POLYGON((0 0,10 0,10 0,10 10,0 10,0 0))"))
            written.insertRow(("short", "POLYGON((0 0,10 0,10.00001 0,10 10,0 10,0 0))"))
        out = arcpy.management.CheckGeometry(self.parcels, self.out("check"))[0]
        self.assertEqual(self.rows(out, ["FEATURE_ID", "TEXT_ID", "PROBLEM"]),
                         [(4, "4", "Self intersections"), (5, "5", "Null geometry"), (6, "6", "Duplicate vertex"),
                          (7, "7", "Short segment")])
        self.assertEqual(self.rows(out, ["CLASS"])[0][0], self.parcels)

    # ---- geometry attributes and references

    def test_add_geometry_attributes_names_its_fields_as_documented(self):
        arcpy.management.AddGeometryAttributes(self.parcels, "AREA;PERIMETER_LENGTH;CENTROID;EXTENT;POINT_COUNT;PART_COUNT",
                                               "KILOMETERS", "HECTARES")
        got = self.rows(self.parcels, ["POLY_AREA", "PERIMETER", "CENTROID_X", "CENTROID_Y", "EXT_MIN_X", "EXT_MAX_Y",
                                       "PNT_COUNT", "PART_COUNT"])[1]
        self.assertAlmostEqual(got[0], 0.25)                 # 2500 m² is a quarter hectare
        self.assertAlmostEqual(got[1], 0.2)
        self.assertEqual(got[2:], (125.0, 25.0, 100.0, 50.0, 5, 1))
        bent = self.make("bent", "POLYLINE", [], [("LINESTRING(0 0,10 0,10 10)",)])
        arcpy.management.AddGeometryAttributes(bent, ["LINE_START_MID_END", "LENGTH"])
        self.assertEqual(self.rows(bent, ["START_X", "START_Y", "MID_X", "MID_Y", "END_X", "END_Y", "LENGTH"]),
                         [(0.0, 0.0, 10.0, 0.0, 10.0, 10.0, 20.0)])
        equator = self.make("equator", "POLYLINE", [], [("LINESTRING(0 0,1 0)",)], srid=4326)
        arcpy.management.AddGeometryAttributes(equator, "LENGTH_GEODESIC", "METERS")
        # One degree of the WGS 1984 equator: 6378137 m × π / 180.
        self.assertAlmostEqual(self.rows(equator, ["LENGTH_GEO"])[0][0], 6378137 * math.pi / 180, places=3)
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.AddGeometryAttributes(self.parcels, "CENTROID_INSIDE")

    def test_define_projection_changes_the_reference_not_the_coordinates(self):
        arcpy.management.DefineProjection(self.wells, 4326)
        self.assertEqual(arcpy.Describe(self.wells).spatialReference.factoryCode, 4326)
        self.assertEqual(self.rows(self.wells, ["SHAPE@XY"])[2], ((400.0, 10.0),))

    # ---- workspaces

    def test_workspaces_copy_and_indexes(self):
        gdb = arcpy.management.CreateFileGDB(self.directory, "work.gdb").getOutput(0)
        self.assertEqual(gdb, os.path.join(self.directory, "work.gpkg"))
        arcpy.management.CreateFeatureclass(gdb, "roads", "POLYLINE", spatial_reference=3857)
        self.assertTrue(arcpy.Exists(gdb + "/roads"))
        lite = arcpy.management.CreateSQLiteDatabase(os.path.join(self.directory, "lite"), "GEOPACKAGE")[0]
        self.assertTrue(lite.endswith("lite.gpkg") and os.path.exists(lite))
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CreateSQLiteDatabase(os.path.join(self.directory, "st.sqlite"))
        folder = arcpy.management.CreateFolder(self.directory, "sub")[0]
        self.assertTrue(os.path.isdir(folder))
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.CreateFolder(self.directory, "sub")
        copied = arcpy.management.Copy(self.parcels, self.out("copy"))[0]
        self.assertEqual(self.rows(copied, ["OID@", "name"]), [(1, "p0"), (2, "p1"), (3, "p2")])
        arcpy.management.DeleteFeatures(self.parcels + "")  # leave OIDs with a gap in a table copy below
        with arcpy.da.InsertCursor(self.parcels, ["name", "SHAPE@"]) as written:
            written.insertRow(("late", square(0, 0)))
        table_copy = arcpy.management.Copy(self.parcels, gdb + "/parcels")[0]
        self.assertEqual(self.rows(table_copy, ["OID@", "name"]), [(4, "late")])
        arcpy.management.AddIndex(copied, ["name", "value"], "by_name")
        connection = sqlite3.connect(copied)
        self.assertEqual(connection.execute("select count(*) from sqlite_master where type = 'index' and name = 'by_name'").fetchone()[0], 1)
        connection.close()
        arcpy.management.RemoveIndex(copied, ["by_name"])
        connection = sqlite3.connect(copied)
        self.assertEqual(connection.execute("select count(*) from sqlite_master where name = 'by_name'").fetchone()[0], 0)
        connection.close()
        arcpy.management.RecalculateFeatureClassExtent(copied)
        connection = sqlite3.connect(copied)
        self.assertEqual(connection.execute("select min_x, min_y, max_x, max_y from gpkg_contents where table_name = 'copy'").fetchone(),
                         (0.0, 0.0, 250.0, 50.0))
        connection.close()
        arcpy.management.Compact(copied)
        self.assertEqual(self.count(copied), 3)

    def test_group_two_is_refused_with_its_reason(self):
        for name in ("CreateReplica", "CreateMosaicDataset", "AddAttributeRule", "CreateEnterpriseGeodatabase"):
            with self.assertRaises(arcpy.NotBuiltError) as raised:
                getattr(arcpy.management, name)()
            self.assertIn("needs a data model this server does not hold yet (ADR-224 group 2)", str(raised.exception))
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            arcpy.management.DetectFeatureChanges()
        self.assertIn("Esri does not document", str(raised.exception))
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            arcpy.management.StandardizeField()
        self.assertIn("standardize-field.html answered 404", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
