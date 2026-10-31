"""arcpy.locref on hand-drawn LRS networks with hand-worked measures (ADR-252).

The main network, in EPSG:3857 (metres), measured in METERS:

    R1  (0,0) ─────────────────────────────── (100,0)       one centerline, calibrated 0 at x=0 and 100 at x=100
    R2  (0,0) │ up to (0,50)                                 one centerline, calibrated 0 and 50

so on R1 a measure is x. Every expected number below is worked out in the comment beside it, never read back from
the tool.
"""
import datetime
import os
import sys
import tempfile
import unittest

import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _layers  # noqa: E402
from grapy import _lrs_measure as M  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

D2024 = datetime.datetime(2024, 1, 1)


def rows(path, fields, where=None):
    with arcpy.da.SearchCursor(path, fields, where) as cursor:
        return [list(r) for r in cursor]


def insert(path, fields, values):
    with arcpy.da.InsertCursor(path, fields) as cursor:
        for v in values:
            cursor.insertRow(v)


class Base(unittest.TestCase):
    def setUp(self):
        _layers.LAYERS.clear()
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.scratchWorkspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "lrs")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "LRS", 3857)[0]
        out = arcpy.locref.CreateLRS(self.fds, "Roads", "Centerline", "CalibrationPoint", "Redline",
                                     "CenterlineSequence", 3857)
        self.centerline, self.cp, self.redline, self.sequence = out[1], out[2], out[3], out[4]
        self.net = arcpy.locref.CreateLRSNetwork(self.fds, "Roads", "Highways", "RouteId", "RouteName", "FromDate",
                                                 "ToDate", measure_unit="METERS")[0]

    def source(self, name, routes, extra=()):
        """A polyline z class of routes: (id, name, wkt, *extra values)."""
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, "POLYLINE", has_z="ENABLED",
                                                   spatial_reference=3857)[0]
        arcpy.management.AddField(path, "RouteId", "TEXT")
        arcpy.management.AddField(path, "RouteName", "TEXT")
        for field, kind in extra:
            arcpy.management.AddField(path, field, kind)
        insert(path, ["RouteId", "RouteName", *[f for f, _ in extra], "SHAPE@"],
               [[r[0], r[1], *r[3:], r[2]] for r in routes])
        return path

    def load_main(self):
        src = self.source("src", [("R1", "Main", "LINESTRING Z (0 0 0, 100 0 0)"),
                                  ("R2", "North", "LINESTRING Z (0 0 0, 0 50 0)")])
        arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName")
        arcpy.locref.GenerateCalibrationPoints(self.net, "RouteId", "FromDate", "ToDate", self.cp, self.net)
        arcpy.locref.GenerateRoutes(self.net)

    def line_event(self, name="Speed", value_field=None):
        path = arcpy.locref.CreateLRSEvent(self.net, name, "LINE", "EventId", "RouteId", "FromDate", "ToDate",
                                           "LocError", "FromM", "ToM")[0]
        if value_field:
            arcpy.management.AddField(path, value_field, "LONG")
        return path

    def table(self, name, fields, values):
        path = arcpy.management.CreateTable(self.gpkg, name)[0]
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        insert(path, [f for f, _ in fields], values)
        return path

    def route(self, route_id, network=None):
        found = [r for r in rows(network or self.net, ["RouteId", "SHAPE@WKT", "ToDate"]) if r[0] == route_id
                 and r[2] is None]
        return M.parts(shapely.from_wkt(found[0][1]))


class Measures(unittest.TestCase):
    def test_calibration_interpolates_and_extrapolates(self):
        # a line of 20 along x; points at d=0 (m 0) and d=10 (m 20): d=5 is 10; past the last, the same ratio 2 holds,
        # so d=20 is 20 + 10 * 2 = 40
        pieces = [M.parts(shapely.from_wkt("LINESTRING (0 0, 5 0, 10 0, 20 0)"))[0]]
        placed, unplaced = M.place(pieces, [(0, 0, 0.0), (10, 0, 20.0)], 0.001)
        out = M.calibrate(pieces, placed)[0]
        self.assertEqual(unplaced, [])
        self.assertEqual([round(v, 9) for v in out[:, 3]], [0.0, 10.0, 20.0, 40.0])

    def test_one_point_extrapolates_one_to_one(self):
        # a single point m 7 at d=2: d=0 is 5, d=10 is 15
        pieces = [M.parts(shapely.from_wkt("LINESTRING (0 0, 2 0, 10 0)"))[0]]
        placed, _ = M.place(pieces, [(2, 0, 7.0)], 0.001)
        self.assertEqual([round(v, 9) for v in M.calibrate(pieces, placed)[0][:, 3]], [5.0, 7.0, 15.0])

    def test_loop_start_and_end_are_told_apart(self):
        # a closed square of side 10 (length 40): 0 and 40 share (0,0); 13.333.. at a third
        loop = [M.parts(shapely.from_wkt("LINESTRING (0 0, 10 0, 10 10, 0 10, 0 0)"))[0]]
        placed, _ = M.place(loop, [(0, 0, 0.0), (0, 0, 40.0), (10, 3.3333333333, 13.3333333333)], 0.001)
        out = M.calibrate(loop, placed)[0]
        self.assertAlmostEqual(out[0, 3], 0.0, delta=1e-6)
        self.assertAlmostEqual(out[-1, 3], 40.0, delta=1e-6)
        self.assertAlmostEqual(out[3, 3], 20.0, delta=1e-6)  # (10,10), after the vertex added at the third, is d=20

    def test_segment_between_measures(self):
        # a route 0..100 along x: 20..60 is the 40 long piece from x=20 to x=60
        route = M.parts(shapely.from_wkt("LINESTRING M (0 0 0, 100 0 100)"))
        piece = M.between(route, 20, 60)
        self.assertEqual(len(piece), 1)
        self.assertAlmostEqual(piece[0][0, 0], 20.0)
        self.assertAlmostEqual(piece[0][-1, 0], 60.0)
        self.assertAlmostEqual(M.locate(route, 37.5, 0.0004, 0.001)[0][0], 37.5)
        self.assertIsNone(M.at(route, 120))


class Lrs(Base):
    def test_create_lrs_makes_the_minimum_schema(self):
        fields = {f.name for f in arcpy.ListFields(self.cp)}
        self.assertTrue({"Measure", "FromDate", "ToDate", "RouteID", "NetworkID"} <= fields)
        self.assertTrue({"CenterlineID", "FromDate", "ToDate", "RouteID", "NetworkID"} <=
                        {f.name for f in arcpy.ListFields(self.sequence)})
        self.assertTrue(arcpy.Describe(self.centerline).hasZ)
        self.assertFalse(arcpy.Describe(self.cp).hasM)  # "cannot be m-enabled"
        net = arcpy.Describe(self.net)
        self.assertTrue(net.hasZ and net.hasM)

    def test_tolerance_below_twice_the_resolution_is_refused(self):
        with self.assertRaises(ExecuteError):
            arcpy.locref.CreateLRS(self.gpkg, "Other", "C2", "P2", "R2", "S2", 3857, "0.001", None, "0.001")

    def test_conflict_prevention_enable_is_refused_by_name(self):
        with self.assertRaises(NotBuiltError) as said:
            arcpy.locref.ModifyLRS(self.gpkg, "Roads", conflict_prevention="ENABLE")
        self.assertIn("conflict_prevention", str(said.exception))

    def test_modify_lrs_renames(self):
        arcpy.locref.ModifyLRS(self.gpkg, "Roads", "Streets")
        arcpy.locref.CreateLRSNetwork(self.gpkg, "Streets", "Second", "RouteId", None, "FromDate", "ToDate",
                                      measure_unit="METERS")
        with self.assertRaises(ExecuteError):
            arcpy.locref.CreateLRSNetwork(self.gpkg, "Roads", "Third", "RouteId", None, "FromDate", "ToDate")

    def test_create_lrs_from_existing_dataset_checks_z(self):
        fds = arcpy.management.CreateFeatureDataset(self.gpkg, "Old", 3857)[0]
        cl = arcpy.management.CreateFeatureclass(fds, "CL", "POLYLINE", has_z="ENABLED")[0]
        arcpy.management.AddField(cl, "CID", "GUID")
        seq = arcpy.management.CreateTable(self.gpkg, "SEQ")[0]
        for f, k in (("CID", "GUID"), ("RID", "TEXT"), ("FD", "DATE"), ("TD", "DATE"), ("NID", "SHORT")):
            arcpy.management.AddField(seq, f, k)
        cp = arcpy.management.CreateFeatureclass(fds, "CP", "POINT", has_z="DISABLED")[0]
        for f, k in (("M", "DOUBLE"), ("FD", "DATE"), ("TD", "DATE"), ("RID", "TEXT"), ("NID", "SHORT")):
            arcpy.management.AddField(cp, f, k)
        rl = arcpy.management.CreateFeatureclass(fds, "RL", "POLYLINE", has_z="ENABLED")[0]
        for f, k in (("FM", "DOUBLE"), ("TM", "DOUBLE"), ("RID", "TEXT"), ("RN", "TEXT"), ("ED", "DATE"),
                     ("AT", "SHORT"), ("NID", "SHORT")):
            arcpy.management.AddField(rl, f, k)
        args = ["Old", cl, "CID", seq, "CID", "RID", "FD", "TD", "NID", cp, "M", "FD", "TD", "RID", "NID", rl, "FM",
                "TM", "RID", "RN", "ED", "AT", "NID"]
        with self.assertRaises(ExecuteError) as said:  # the calibration points have no z
            arcpy.locref.CreateLRSFromExistingDataset(*args)
        self.assertIn("z-enabled", str(said.exception))
        arcpy.management.Delete(cp)
        cp = arcpy.management.CreateFeatureclass(fds, "CP", "POINT", has_z="ENABLED")[0]
        for f, k in (("M", "DOUBLE"), ("FD", "DATE"), ("TD", "DATE"), ("RID", "TEXT"), ("NID", "SHORT")):
            arcpy.management.AddField(cp, f, k)
        arcpy.locref.CreateLRSFromExistingDataset(*args)
        net = arcpy.management.CreateFeatureclass(fds, "Net", "POLYLINE", has_z="ENABLED", has_m="ENABLED")[0]
        for f, k in (("RID", "TEXT"), ("FD", "DATE"), ("TD", "DATE")):
            arcpy.management.AddField(net, f, k)
        arcpy.locref.CreateLRSNetworkFromExistingDataset(net, "Old", "RID", None, "FD", "TD")
        with self.assertRaises(ExecuteError):  # already registered
            arcpy.locref.CreateLRSNetworkFromExistingDataset(net, "Old", "RID", None, "FD", "TD")

    def test_remove_lrs_entity_cascades(self):
        ev = self.line_event()
        arcpy.locref.RemoveLRSEntity(self.gpkg, "NETWORK", "Highways")
        with self.assertRaises(ExecuteError):
            arcpy.locref.GenerateEvents(ev)
        arcpy.locref.RemoveLRSEntity(self.gpkg, "LRS", "Roads")
        with self.assertRaises(ExecuteError):
            arcpy.locref.ModifyLRS(self.gpkg, "Roads", "X")


class Routes(Base):
    def test_append_calibrate_and_generate(self):
        self.load_main()
        # one centerline and one sequence row per route, two calibration points each
        self.assertEqual(len(rows(self.centerline, ["CenterlineID"])), 2)
        self.assertEqual(sorted(r[0] for r in rows(self.sequence, ["RouteID"])), ["R1", "R2"])
        self.assertEqual(sorted(round(r[0], 9) for r in rows(self.cp, ["Measure"], "RouteID = 'R1'")), [0.0, 100.0])
        r1 = self.route("R1")
        self.assertAlmostEqual(M.locate(r1, 25, 0, 0.001)[0][0], 25.0)
        self.assertEqual(M.extent(self.route("R2")), (0.0, 50.0))

    def test_a_third_point_interpolates(self):
        self.load_main()
        # a point at x=50 with measure 30: x=25 is 15 (0..30 over 0..50), x=75 is 65 (30..100 over 50..100)
        insert(self.cp, ["Measure", "RouteID", "NetworkID", "SHAPE@"], [[30.0, "R1", 1, "POINT Z (50 0 0)"]])
        arcpy.locref.GenerateRoutes(self.net)
        r1 = self.route("R1")
        self.assertAlmostEqual(M.locate(r1, 25, 0, 0.001)[0][0], 15.0)
        self.assertAlmostEqual(M.locate(r1, 75, 0, 0.001)[0][0], 65.0)

    def test_length_counts_z(self):
        src = self.source("src", [("Z1", "Slope", "LINESTRING Z (0 0 0, 3 0 4)")])
        arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName")
        arcpy.locref.GenerateCalibrationPoints(self.net, "RouteId", "FromDate", "ToDate", self.cp, self.net)
        # the 3-4-5 triangle: the route is 5 long
        self.assertEqual(sorted(round(r[0], 9) for r in rows(self.cp, ["Measure"])), [0.0, 5.0])

    def _gapped(self, rule, offset):
        arcpy.locref.ModifyNetworkCalibrationRules(self.net, rule, offset)
        src = self.source("gap", [("G", "Gap", "MULTILINESTRING Z ((0 0 0, 10 0 0), (13 4 0, 23 4 0))")])
        arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName")
        arcpy.locref.GenerateCalibrationPoints(self.net, "RouteId", "FromDate", "ToDate", self.cp, self.net)
        return sorted(round(r[0], 9) for r in rows(self.cp, ["Measure"]))

    def test_gap_by_euclidean_distance(self):
        # the gap from (10,0) to (13,4) is 5 long: 0, 10, then 15, 25
        self.assertEqual(self._gapped("ADDING_EUCLIDEAN_DISTANCE", None), [0.0, 10.0, 15.0, 25.0])

    def test_gap_by_stepping_increment(self):
        # 10 steps up to the next multiple of 4 above it, 12; the part after runs 12..22
        self.assertEqual(self._gapped("STEPPING_INCREMENT", 4), [0.0, 10.0, 12.0, 22.0])

    def test_gap_by_adding_increment(self):
        # 10 + 4 = 14; the part after runs 14..24
        self.assertEqual(self._gapped("ADDING_INCREMENT", 4), [0.0, 10.0, 14.0, 24.0])

    def test_offset_within_the_m_tolerance_is_refused(self):
        with self.assertRaises(ExecuteError):  # 0.0005 is above 0 but not above the 0.001 m tolerance
            arcpy.locref.ModifyNetworkCalibrationRules(self.net, "ADDING_INCREMENT", 0.0005)

    def test_attribute_fields_scale_the_length(self):
        src = self.source("att", [("A", "Att", "LINESTRING Z (0 0 0, 50 0 0)", 100.0, 200.0)],
                          [("FM", "DOUBLE"), ("TM", "DOUBLE")])
        arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName")
        arcpy.locref.GenerateCalibrationPoints(src, "RouteId", None, None, self.cp, self.net,
                                               calibration_method="ATTRIBUTE_FIELDS", from_measure_field="FM",
                                               to_measure_field="TM")
        self.assertEqual(sorted(round(r[0], 9) for r in rows(self.cp, ["Measure"])), [100.0, 200.0])

    def test_add_skips_a_duplicate_and_partial_loading(self):
        self.load_main()
        src = self.source("dup", [("R1", "Main", "LINESTRING Z (0 0 0, 100 0 0)"),
                                  ("R9", "New", "LINESTRING Z (0 0 0, 0 -40 0)")])
        with self.assertRaises(ExecuteError):  # R1 is already there for all time
            arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName")
        result = arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName", allow_partial_loading="ALLOW")
        self.assertEqual(sorted(r[0] for r in rows(self.net, ["RouteId"])), ["R1", "R2", "R9"])
        self.assertEqual(rows(result[2], ["RouteId"]), [["R1"]])

    def test_retire_by_route_id_closes_the_old_record(self):
        self.load_main()
        newer = self.source("newer", [("R1", "Main", "LINESTRING Z (0 0 0, 120 0 0)", D2024)], [("FD", "DATE")])
        arcpy.locref.AppendRoutes(newer, self.net, "RouteId", "RouteName", "FD", load_type="RETIRE_BY_ROUTE_ID")
        got = sorted(((r[1], r[2]) for r in rows(self.net, ["RouteId", "FromDate", "ToDate"]) if r[0] == "R1"), key=str)
        # the record of all time ends on 2024-01-01 and the new one begins then
        self.assertEqual(got, [(None, D2024), (D2024, None)])

    def test_delete_routes_needs_a_selection(self):
        self.load_main()
        with self.assertRaises(ExecuteError):
            arcpy.locref.DeleteRoutes(self.net)
        layer = arcpy.management.MakeFeatureLayer(self.net, "hw")[0]
        arcpy.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "RouteId = 'R2'")
        arcpy.locref.DeleteRoutes(layer, "DELETE_CALIBRATION_POINTS", "NO_DELETE_EVENTS", "DELETE_CENTERLINES")
        self.assertEqual([r[0] for r in rows(self.net, ["RouteId"])], ["R1"])
        self.assertEqual(len(rows(self.cp, ["RouteID"], "RouteID = 'R2'")), 0)
        self.assertEqual(len(rows(self.centerline, ["CenterlineID"])), 1)
        self.assertEqual([r[0] for r in rows(self.sequence, ["RouteID"])], ["R1"])

    def test_remove_overlapping_centerlines(self):
        src = self.source("ov", [("A", "A", "LINESTRING Z (0 0 0, 60 0 0)"), ("B", "B", "LINESTRING Z (40 0 0, 100 0 0)")])
        arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName")
        arcpy.locref.RemoveOverlappingCenterlines(self.centerline)
        # 40..60 becomes a third centerline; A keeps 0..40 and B 60..100
        lengths = sorted(round(r[0], 6) for r in rows(self.centerline, ["SHAPE@LENGTH"]))
        self.assertEqual(lengths, [20.0, 40.0, 40.0])
        seq = rows(self.sequence, ["RouteID", "CenterlineID"])
        self.assertEqual(sorted(r[0] for r in seq), ["A", "A", "B", "B"])
        shared = {r[1] for r in seq if r[0] == "A"} & {r[1] for r in seq if r[0] == "B"}
        self.assertEqual(len(shared), 1)
        arcpy.locref.GenerateCalibrationPoints(self.net, "RouteId", "FromDate", "ToDate", self.cp, self.net)
        arcpy.locref.GenerateRoutes(self.net)
        self.assertEqual(M.extent(self.route("A")), (0.0, 60.0))

    def test_multi_field_route_id_with_padding_and_lookup(self):
        net = arcpy.management.CreateFeatureclass(self.fds, "Multi", "POLYLINE", has_z="ENABLED", has_m="ENABLED")[0]
        for f, k in (("RID", "TEXT"), ("FD", "DATE"), ("TD", "DATE"), ("RouteSystem", "TEXT"), ("RouteNumber", "LONG")):
            arcpy.management.AddField(net, f, k)
        arcpy.locref.CreateLRSNetworkFromExistingDataset(net, "Roads", "RID", None, "FD", "TD",
                                                         route_id_configuration="MULTI_FIELD_ROUTE_ID",
                                                         individual_route_id_fields="RouteSystem;RouteNumber")
        arcpy.locref.ModifyRouteIdPadding(net, "RouteSystem 3 false true @ LEFT false false;"
                                               "RouteNumber 5 false true 0 LEFT false false")
        src = self.source("ms", [(None, "x", "LINESTRING Z (0 0 0, 10 0 0)", "SR", 7)],
                          [("RouteSystem", "TEXT"), ("RouteNumber", "LONG")])
        arcpy.locref.AppendRoutes(src, net, "RouteId", None)
        # "SR" padded left to 3 with "@", 7 to 5 with "0"
        self.assertEqual(rows(net, ["RID"]), [["@SR00007"]])
        keys = self.table("Systems", [("Code", "TEXT")], [["IS"]])
        arcpy.locref.ConfigureLookupTable(net, keys, "RouteSystem", "Code")
        with self.assertRaises(ExecuteError):  # "SR" is not in the lookup
            arcpy.locref.AppendRoutes(src, net, "RouteId", None)

    def test_padding_rules_are_checked(self):
        net = arcpy.management.CreateFeatureclass(self.fds, "Multi", "POLYLINE", has_z="ENABLED", has_m="ENABLED")[0]
        for f, k in (("RID", "TEXT"), ("FD", "DATE"), ("TD", "DATE"), ("A", "TEXT"), ("B", "TEXT")):
            arcpy.management.AddField(net, f, k)
        arcpy.locref.CreateLRSNetworkFromExistingDataset(net, "Roads", "RID", None, "FD", "TD",
                                                         route_id_configuration="MULTI_FIELD_ROUTE_ID",
                                                         individual_route_id_fields="A;B")
        with self.assertRaises(ExecuteError):  # pad if null without allow null
            arcpy.locref.ModifyRouteIdPadding(net, "A 3 false true # LEFT true false")
        with self.assertRaises(ExecuteError):  # A allows null, B after it does not
            arcpy.locref.ModifyRouteIdPadding(net, "A 3 false true # LEFT false true;B 3 false true # LEFT false false")


class Lines(Base):
    """A line network Engineering with its derived network Derived: line L1 is Ra (0,0)-(30,0) order 100, then
    Rb (30,0)-(70,0) order 200; each route is measured 0 to its length, the derived route 0 to 70."""

    def setUp(self):
        super().setUp()
        self.eng = arcpy.locref.CreateLRSNetwork(self.fds, "Roads", "Engineering", "RouteId", "RouteName", "FromDate",
                                                 "ToDate", include_fields_to_support_lines="INCLUDE",
                                                 line_id_field="LineId", line_name_field="LineName",
                                                 line_order_field="LineOrder", measure_unit="METERS")[0]
        self.derived = arcpy.locref.CreateLRSNetwork(self.fds, "Roads", "Derived", "RouteId", "RouteName", "FromDate",
                                                     "ToDate", "DERIVE", "Engineering", measure_unit="METERS")[0]
        src = self.source("lines", [("Ra", "A", "LINESTRING Z (0 0 0, 30 0 0)", "L1", "Line one", 100),
                                    ("Rb", "B", "LINESTRING Z (30 0 0, 70 0 0)", "L1", "Line one", 200)],
                          [("LID", "TEXT"), ("LNAME", "TEXT"), ("LORD", "LONG")])
        arcpy.locref.AppendRoutes(src, self.eng, "RouteId", "RouteName", None, None, "LID", "LNAME", "LORD")
        arcpy.locref.GenerateCalibrationPoints(self.eng, "RouteId", "FromDate", "ToDate", self.cp, self.eng)
        arcpy.locref.GenerateRoutes(self.eng)

    def test_derived_route_is_the_line_from_zero(self):
        derived = self.route("L1", self.derived)
        self.assertEqual(M.extent(derived), (0.0, 70.0))
        self.assertAlmostEqual(M.locate(derived, 50, 0, 0.001)[0][0], 50.0)
        self.assertEqual(rows(self.derived, ["RouteName"]), [["Line one"]])

    def test_line_order_must_step_by_hundreds(self):
        src = self.source("bad", [("Rc", "C", "LINESTRING Z (70 0 0, 80 0 0)", "L1", "Line one", 250)],
                          [("LID", "TEXT"), ("LNAME", "TEXT"), ("LORD", "LONG")])
        with self.assertRaises(ExecuteError):
            arcpy.locref.AppendRoutes(src, self.eng, "RouteId", "RouteName", None, None, "LID", "LNAME", "LORD")

    def test_reverse_line_orders(self):
        layer = arcpy.management.MakeFeatureLayer(self.eng, "eng")[0]
        arcpy.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "RouteId = 'Ra'")
        arcpy.locref.ReverseLineOrders(layer)
        self.assertEqual(sorted(rows(self.eng, ["RouteId", "LineOrder"])), [["Ra", 200], ["Rb", 100]])

    def test_spanning_event_runs_along_the_line(self):
        ev = arcpy.locref.CreateLRSEvent(self.eng, "Work", "LINE", "EventId", "RouteId", "FromDate", "ToDate",
                                         "LocError", "FromM", "ToM", "SPANS_ROUTES", "ToRouteId")[0]
        insert(ev, ["EventId", "RouteId", "FromM", "ToRouteId", "ToM"], [["E1", "Ra", 10.0, "Rb", 20.0]])
        arcpy.locref.GenerateEvents(ev)
        # Ra from 10 (x=10) to its end (x=30), then Rb from 0 (x=30) to 20 (x=50): 40 long, no error
        length, error = rows(ev, ["SHAPE@LENGTH", "LocError"])[0]
        self.assertAlmostEqual(length, 40.0, delta=1e-6)
        self.assertEqual(error, "No Error")

    def test_derive_event_measures(self):
        ev = arcpy.locref.CreateLRSEvent(self.eng, "Signs", "LINE", "EventId", "RouteId", "FromDate", "ToDate",
                                         "LocError", "FromM", "ToM")[0]
        for f, k in (("DerivedRouteId", "TEXT"), ("DerivedFromMeasure", "DOUBLE"), ("DerivedToMeasure", "DOUBLE")):
            arcpy.management.AddField(ev, f, k)
        arcpy.locref.EnableDerivedMeasureFields(ev)
        insert(ev, ["EventId", "RouteId", "FromM", "ToM"], [["S1", "Rb", 10.0, 20.0]])
        arcpy.locref.DeriveEventMeasures(self.eng)
        # Rb 10..20 is x 40..50, which on the derived route is 40..50
        rid, a, b = rows(ev, ["DerivedRouteId", "DerivedFromMeasure", "DerivedToMeasure"])[0]
        self.assertEqual(rid, "L1")
        self.assertAlmostEqual(a, 40.0, delta=1e-6)
        self.assertAlmostEqual(b, 50.0, delta=1e-6)
        arcpy.locref.DisableDerivedMeasureFields(ev)
        with self.assertRaises(ExecuteError):
            arcpy.locref.DisableDerivedMeasureFields(ev)

    def test_events_cannot_be_registered_to_a_derived_network(self):
        with self.assertRaises(ExecuteError):
            arcpy.locref.CreateLRSEvent(self.derived, "Nope", "POINT", "EventId", "RouteId", "FromDate", "ToDate",
                                        "LocError", "M")


class Concurrency(Base):
    """Route A (0,0)-(100,0) and route B (0,0)-(50,0)-(50,50) share the centerline C0 (0,0)-(50,0)."""

    def setUp(self):
        super().setUp()
        arcpy.management.AddField(self.net, "Num", "LONG")
        insert(self.centerline, ["CenterlineID", "SHAPE@"],
               [["{C0}", "LINESTRING Z (0 0 0, 50 0 0)"], ["{CA}", "LINESTRING Z (50 0 0, 100 0 0)"],
                ["{CB}", "LINESTRING Z (50 0 0, 50 50 0)"]])
        src = self.source("conc", [("A", "SR10", "LINESTRING Z (0 0 0, 100 0 0)", 10),
                                   ("B", "SR2", "LINESTRING Z (0 0 0, 50 0 0, 50 50 0)", 2)], [("Num", "LONG")])
        arcpy.locref.AppendRoutes(src, self.net, "RouteId", "RouteName", consider_existing_centerlines="CONSIDER")
        arcpy.locref.GenerateCalibrationPoints(self.net, "RouteId", "FromDate", "ToDate", self.cp, self.net)
        arcpy.locref.GenerateRoutes(self.net)

    def test_consider_reuses_the_centerlines(self):
        self.assertEqual(len(rows(self.centerline, ["CenterlineID"])), 3)
        seq = sorted((r[0], r[1]) for r in rows(self.sequence, ["RouteID", "CenterlineID"]))
        self.assertEqual(seq, [("A", "{C0}"), ("A", "{CA}"), ("B", "{C0}"), ("B", "{CB}")])

    def concurrencies(self):
        out = os.path.join(self.gpkg, "conc_out")
        arcpy.locref.CalculateRouteConcurrencies(self.net, out)
        return {r[0]: r[1:] for r in rows(out, ["RouteId", "FromMeasure", "ToMeasure", "DominantFlag",
                                                "DominantError", "SectionId"])}

    def test_no_rule_is_a_tie(self):
        got = self.concurrencies()
        # both routes run 0..50 on C0; with no rule they tie (error 1), the smaller ID A taken
        self.assertEqual([round(v, 9) for v in got["A"][:2]], [0.0, 50.0])
        self.assertEqual([round(v, 9) for v in got["B"][:2]], [0.0, 50.0])
        self.assertEqual((got["A"][2], got["A"][3], got["B"][2], got["B"][3]), (1, 1, 0, 1))
        self.assertEqual(got["A"][4], got["B"][4])

    def test_alphanumeric_and_numeric_rules(self):
        # alphanumerically "SR10" < "SR2", so LESSER makes A dominant
        arcpy.locref.ConfigureRouteDominanceRules(self.net, "ADD", "ByName", fields="RouteName")
        got = self.concurrencies()
        self.assertEqual((got["A"][2], got["B"][2], got["A"][3]), (1, 0, 0))
        # numerically 2 < 10, so LESSER on Num makes B dominant
        arcpy.locref.ConfigureRouteDominanceRules(self.net, "UPDATE", "ByName", "ByNum", fields="Num",
                                                  order_type="NUMERIC")
        got = self.concurrencies()
        self.assertEqual((got["A"][2], got["B"][2]), (0, 1))
        # an exception checked first: Num 10 wins whatever the order
        arcpy.locref.ConfigureRouteDominanceRules(self.net, "UPDATE", "ByNum", fields="Num", order_type="NUMERIC",
                                                  prioritized_exceptions="10")
        got = self.concurrencies()
        self.assertEqual((got["A"][2], got["B"][2]), (1, 0))
        arcpy.locref.ConfigureRouteDominanceRules(self.net, "DELETE", "ByNum")
        with self.assertRaises(ExecuteError):
            arcpy.locref.ConfigureRouteDominanceRules(self.net, "DELETE", "ByNum")

    def test_append_to_dominant_route(self):
        arcpy.locref.ConfigureRouteDominanceRules(self.net, "ADD", "ByName", fields="RouteName")
        ev = arcpy.locref.CreateLRSEvent(self.net, "Poles", "POINT", "EventId", "RouteId", "FromDate", "ToDate",
                                         "LocError", "M")[0]
        src = self.table("poles", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("M", "DOUBLE")],
                         [["P1", "B", 20.0], ["P2", "B", 80.0]])
        arcpy.locref.AppendEvents(src, ev, append_to_dominant_route="APPEND_TO_DOMINANT_ROUTE")
        got = {r[0]: (r[1], round(r[2], 6)) for r in rows(ev, ["EventId", "RouteId", "M"])}
        # P1 at B 20 is (20,0) on the shared section, where A dominates: A 20. P2 at B 80 is (50,30), B's own
        self.assertEqual(got, {"P1": ("A", 20.0), "P2": ("B", 80.0)})


class Events(Base):
    def setUp(self):
        super().setUp()
        self.load_main()
        self.speed = self.line_event("Speed", "Limit")
        src = self.table("speeds", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("FromM", "DOUBLE"), ("ToM", "DOUBLE"),
                                    ("Limit", "LONG")], [["S1", "R1", 20.0, 60.0, 50]])
        arcpy.locref.AppendEvents(src, self.speed)

    def test_append_events_makes_the_shape(self):
        length, error, start = rows(self.speed, ["SHAPE@LENGTH", "LocError", "FromDate"])[0]
        self.assertAlmostEqual(length, 40.0, delta=1e-6)  # x 20..60
        self.assertEqual(error, "No Error")
        self.assertIsNone(start)  # no date mapped: the route's current record, which began at the beginning of time

    def test_to_date_without_from_date_is_an_error(self):
        src = self.table("bad", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("FromM", "DOUBLE"), ("ToM", "DOUBLE"),
                                 ("ToDate", "DATE")], [["S9", "R1", 1.0, 2.0, D2024]])
        with self.assertRaises(ExecuteError):
            arcpy.locref.AppendEvents(src, self.speed)

    def test_retire_overlaps(self):
        src = self.table("later", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("FromM", "DOUBLE"), ("ToM", "DOUBLE"),
                                   ("FromDate", "DATE")], [["S2", "R1", 40.0, 80.0, D2024]])
        arcpy.locref.AppendEvents(src, self.speed, load_type="RETIRE_OVERLAPS")
        got = sorted((r[0], r[1]) for r in rows(self.speed, ["EventId", "ToDate"]))
        # S1 (20..60, all time) overlaps S2 (40..80 from 2024) and began earlier: it is retired on 2024-01-01
        self.assertEqual(got, [("S1", D2024), ("S2", None)])

    def test_replace_by_event_id_and_generated_ids(self):
        src = self.table("rep", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("FromM", "DOUBLE"), ("ToM", "DOUBLE")],
                         [["S1", "R1", 0.0, 10.0], [None, "R1", 90.0, 100.0]])
        arcpy.locref.AppendEvents(src, self.speed, load_type="REPLACE_BY_EVENT_ID",
                                  generate_event_ids="GENERATE_EVENT_IDS")
        got = rows(self.speed, ["EventId", "FromM"])
        self.assertEqual(sorted(r[1] for r in got), [0.0, 90.0])
        self.assertTrue(all(r[0] for r in got))

    def test_generate_events_after_a_measure_change(self):
        with arcpy.da.UpdateCursor(self.speed, ["FromM", "ToM"]) as cursor:
            for _ in cursor:
                cursor.updateRow([0.0, 10.0])
        arcpy.locref.GenerateEvents(self.speed)
        self.assertAlmostEqual(rows(self.speed, ["SHAPE@LENGTH"])[0][0], 10.0, delta=1e-6)
        with arcpy.da.UpdateCursor(self.speed, ["FromM", "ToM"]) as cursor:
            for _ in cursor:
                cursor.updateRow([90.0, 150.0])
        arcpy.locref.GenerateEvents(self.speed)
        # 90..150 runs past R1's end at 100: a partial match, 10 long
        length, error = rows(self.speed, ["SHAPE@LENGTH", "LocError"])[0]
        self.assertAlmostEqual(length, 10.0, delta=1e-6)
        self.assertEqual(error, "Partial Match")

    def test_bypass_needs_a_utility_network(self):
        with self.assertRaises(ExecuteError):
            arcpy.locref.GenerateEvents(self.speed, "BYPASS_EVENTS_WITH_NULL_LRS_FIELDS")

    def _recalibrate(self):
        # a point at x=50 with measure 70 from 2024: x=20 becomes 1.4 * 20 = 28 and x=60 becomes 70 + 10 * 30/50 = 76
        insert(self.cp, ["Measure", "RouteID", "NetworkID", "FromDate", "SHAPE@"],
               [[70.0, "R1", 1, D2024, "POINT Z (50 0 0)"]])
        arcpy.locref.GenerateRoutes(self.net)
        arcpy.locref.ApplyEventBehaviors(self.net)

    def test_stay_put_after_calibration(self):
        self._recalibrate()
        got = sorted(((r[1], r[2], round(r[3], 6), round(r[4], 6)) for r in
                      rows(self.speed, ["EventId", "FromDate", "ToDate", "FromM", "ToM"])), key=str)
        # the old record ends on the edit's date and a new one, at the same place, begins then
        self.assertEqual(got, [(None, D2024, 20.0, 60.0), (D2024, None, 28.0, 76.0)])

    def test_move_after_calibration(self):
        arcpy.locref.ModifyEventBehaviorRules(self.speed, calibrate_rule="MOVE")
        self._recalibrate()
        got = rows(self.speed, ["FromM", "ToM", "SHAPE@WKT", "ToDate"])
        self.assertEqual(len(got), 1)
        # the measures stay and the shape moves: m 20 is x 20/1.4 = 14.2857.., m 60 is 60/1.4 = 42.857..
        coords = shapely.from_wkt(got[0][2])
        xs = shapely.get_coordinates(coords)[:, 0]
        self.assertAlmostEqual(xs.min(), 20 / 1.4, delta=1e-6)
        self.assertAlmostEqual(xs.max(), 60 / 1.4, delta=1e-6)

    def test_retire_after_calibration(self):
        arcpy.locref.ModifyEventBehaviorRules(self.speed, calibrate_rule="RETIRE")
        self._recalibrate()
        self.assertEqual(rows(self.speed, ["ToDate"]), [[D2024]])

    def _extend(self):
        """R1 extended by 20 at its start from 2024: a centerline (-20,0)-(0,0) joins it, the old end points retire
        and new ones put 0 at x=-20 and 120 at x=100, so from 2024 a measure is x + 20."""
        insert(self.centerline, ["CenterlineID", "SHAPE@"], [["{EXT}", "LINESTRING Z (-20 0 0, 0 0 0)"]])
        insert(self.sequence, ["CenterlineID", "RouteID", "NetworkID", "FromDate"], [["{EXT}", "R1", 1, D2024]])
        with arcpy.da.UpdateCursor(self.cp, ["ToDate"], "RouteID = 'R1'") as cursor:
            for _ in cursor:
                cursor.updateRow([D2024])
        insert(self.cp, ["Measure", "RouteID", "NetworkID", "FromDate", "SHAPE@"],
               [[0.0, "R1", 1, D2024, "POINT Z (-20 0 0)"], [120.0, "R1", 1, D2024, "POINT Z (100 0 0)"]])
        arcpy.locref.GenerateRoutes(self.net)
        arcpy.locref.ApplyEventBehaviors(self.net)

    def test_extend_stay_put_and_cover(self):
        src = self.table("first", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("FromM", "DOUBLE"), ("ToM", "DOUBLE")],
                         [["S0", "R1", 0.0, 10.0]])
        arcpy.locref.AppendEvents(src, self.speed)
        arcpy.locref.ModifyEventBehaviorRules(self.speed, extend_rule="COVER")
        self._extend()
        self.assertEqual(M.extent(self.route("R1")), (0.0, 120.0))
        got = sorted((r[0], round(r[1], 6), round(r[2], 6)) for r in
                     rows(self.speed, ["EventId", "FromM", "ToM"], "ToDate is null"))
        # every event shifts by 20 (Stay Put): S1 20..60 is 40..80; S0, touching the new section 0..20, covers it: 0..30
        self.assertEqual(got, [("S0", 0.0, 30.0), ("S1", 40.0, 80.0)])

    def test_extend_stay_put(self):
        self._extend()
        got = [(round(r[0], 6), round(r[1], 6)) for r in rows(self.speed, ["FromM", "ToM"], "ToDate is null")]
        self.assertEqual(got, [(40.0, 80.0)])

    def test_reverse_stay_put(self):
        # the end points swapped: 100 at x=0 and 0 at x=100, so m' = 100 - m and S1 20..60 is 40..80
        with arcpy.da.UpdateCursor(self.cp, ["Measure", "FromDate"], "RouteID = 'R1'") as cursor:
            for measure, _ in cursor:
                cursor.updateRow([100.0 - measure, D2024])
        arcpy.locref.GenerateRoutes(self.net)
        arcpy.locref.ApplyEventBehaviors(self.net)
        got = [(round(r[0], 6), round(r[1], 6)) for r in rows(self.speed, ["FromM", "ToM"], "ToDate is null")]
        self.assertEqual(got, [(40.0, 80.0)])

    def test_a_rule_outside_the_activity_list_is_refused(self):
        with self.assertRaises(ExecuteError):  # COVER is not a calibrate rule
            arcpy.locref.ModifyEventBehaviorRules(self.speed, calibrate_rule="COVER")

    def test_overlay_events(self):
        lanes = self.line_event("Lanes", "Count")
        src = self.table("lanes", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("FromM", "DOUBLE"), ("ToM", "DOUBLE"),
                                   ("Count", "LONG")], [["L1", "R1", 40.0, 80.0, 2]])
        arcpy.locref.AppendEvents(src, lanes)
        out = os.path.join(self.gpkg, "overlay")
        arcpy.locref.OverlayEvents(self.net, f"{self.speed};{lanes}", out)
        got = [(r[0], r[1], r[2], r[3], r[4]) for r in
               rows(out, ["Route_ID", "From_Measure", "To_Measure", "Speed_Limit", "Lanes_Count"])]
        # speed 20..60 and lanes 40..80 cut each other at 40 and 60
        self.assertEqual(sorted(got), [("R1", 20.0, 40.0, 50, None), ("R1", 40.0, 60.0, 50, 2),
                                       ("R1", 60.0, 80.0, None, 2)])

    def test_overlay_splits_address_ranges(self):
        addresses = self.line_event("Addresses")
        for f in ("LF", "LT", "RF", "RT"):
            arcpy.management.AddField(addresses, f, "LONG")
        arcpy.management.AddField(addresses, "Road", "TEXT")
        sites = arcpy.management.CreateFeatureclass(self.fds, "Sites", "POINT")[0]
        arcpy.management.AddField(sites, "Num", "LONG")
        arcpy.management.AddField(sites, "Road", "TEXT")
        arcpy.locref.ConfigureAddressFeatureClasses(addresses, "LF", "LT", "RF", "RT", sites, "Num", "Road", "Road")
        src = self.table("addr", [("EventId", "TEXT"), ("RouteId", "TEXT"), ("FromM", "DOUBLE"), ("ToM", "DOUBLE"),
                                  ("LF", "LONG"), ("LT", "LONG"), ("RF", "LONG"), ("RT", "LONG")],
                         [["A1", "R1", 0.0, 100.0, 100, 200, 101, 201]])
        arcpy.locref.AppendEvents(src, addresses)
        out = os.path.join(self.gpkg, "overlay2")
        arcpy.locref.OverlayEvents(self.net, f"{self.speed};{addresses}", out)
        got = sorted((r[0], r[1], r[2]) for r in rows(out, ["From_Measure", "Addresses_LF", "Addresses_LT"]))
        # 100..200 over 0..100 split at 20 and 60: 100-120, 120-160, 160-200
        self.assertEqual(got, [(0.0, 100, 120), (20.0, 120, 160), (60.0, 160, 200)])

    def test_translate_event_measures(self):
        county = arcpy.locref.CreateLRSNetwork(self.fds, "Roads", "County", "RouteId", None, "FromDate", "ToDate",
                                               measure_unit="METERS")[0]
        src = self.source("cty", [("C1", "x", "LINESTRING Z (100 0 0, 0 0 0)")])
        arcpy.locref.AppendRoutes(src, county, "RouteId", None)
        arcpy.locref.GenerateCalibrationPoints(county, "RouteId", "FromDate", "ToDate", self.cp, county)
        arcpy.locref.GenerateRoutes(county)
        out = os.path.join(self.gpkg, "translated")
        arcpy.locref.TranslateEventMeasures(self.speed, county, out)
        # C1 runs from x=100 (m 0) to x=0 (m 100): x 20..60 is 40..80 there
        rid, a, b, error = rows(out, ["RouteId", "FromM", "ToM", "Loc_Error"])[0]
        self.assertEqual((rid, round(a, 6), round(b, 6), error), ("C1", 40.0, 80.0, "No Error"))

    def test_update_measures_from_lrs(self):
        pts = arcpy.management.CreateFeatureclass(self.gpkg, "pts", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(pts, "Rid", "TEXT")
        arcpy.management.AddField(pts, "Meas", "DOUBLE")
        insert(pts, ["SHAPE@"], [["POINT (25 0.5)"], ["POINT (500 500)"]])
        arcpy.locref.UpdateMeasuresFromLRS(self.net, None, pts, "Rid", "Meas", search_tolerance="1")
        # (25, 0.5) is 0.5 from R1 at x=25: R1, 25; the far point finds nothing and stays null
        self.assertEqual(sorted(rows(pts, ["Rid", "Meas"]), key=str), [["R1", 25.0], [None, None]])

    def test_modify_lrs_network_remaps_a_field(self):
        # the route name moves to a new field holding "Avenue" for R1: AppendEvents by route name then finds R1
        arcpy.management.AddField(self.net, "Alias", "TEXT")
        with arcpy.da.UpdateCursor(self.net, ["Alias"], "RouteId = 'R1'") as cursor:
            for _ in cursor:
                cursor.updateRow(["Avenue"])
        arcpy.locref.ModifyLRSNetwork(self.net, route_name_field="Alias")
        ev = arcpy.locref.CreateLRSEvent(self.net, "Named", "POINT", "EventId", "RouteId", "FromDate", "ToDate",
                                         "LocError", "M", None, "NO_SPANS_ROUTES", None, "STORE_ROUTE_NAME",
                                         "RouteName")[0]
        src = self.table("named", [("EventId", "TEXT"), ("RouteName", "TEXT"), ("M", "DOUBLE")], [["N1", "Avenue", 5.0]])
        arcpy.locref.AppendEvents(src, ev)
        self.assertEqual(rows(ev, ["RouteId", "SHAPE@XY"]), [["R1", (5.0, 0.0)]])
        with self.assertRaises(ExecuteError):  # a field that does not exist
            arcpy.locref.ModifyLRSNetwork(self.net, route_name_field="Nope")

    def test_intersection_from_existing_dataset(self):
        fc = arcpy.management.CreateFeatureclass(self.fds, "Xing", "POINT", has_z="ENABLED", has_m="ENABLED")[0]
        for f, k in (("IId", "GUID"), ("IName", "TEXT"), ("RId", "TEXT"), ("FeatId", "TEXT"), ("FCN", "TEXT"),
                     ("FD", "DATE"), ("TD", "DATE"), ("Mv", "DOUBLE")):
            arcpy.management.AddField(fc, f, k)
        arcpy.locref.CreateLRSIntersectionFromExistingDataset(self.net, "RouteName", fc, "IId", "IName", "RId", "FeatId",
                                                              "FCN", "FD", "TD", "Highways RouteId RouteName ,",
                                                              measure_field="Mv")
        arcpy.locref.GenerateIntersections(fc, edited_by_current_user="ALL_USERS")
        # R1 and R2 meet at (0,0): each is crossed by the other there, at measure 0, named "<own>,<other>"
        got = sorted((r[0], r[1], r[2]) for r in rows(fc, ["RId", "IName", "Mv"]))
        self.assertEqual(got, [("R1", "Main,North", 0.0), ("R2", "North,Main", 0.0)])

    def test_referent_and_stationing_registration(self):
        poles = arcpy.locref.CreateLRSEvent(self.net, "Poles", "POINT", "EventId", "RouteId", "FromDate", "ToDate",
                                            "LocError", "M")[0]
        for f, k in (("RM", "SHORT"), ("RL", "TEXT"), ("RO", "TEXT"), ("St", "TEXT"), ("Bk", "TEXT"), ("Dir", "TEXT")):
            arcpy.management.AddField(poles, f, k)
        arcpy.locref.EnableReferentFields(poles, "RM", "RL", "RO", offset_units="FEET")
        arcpy.locref.DisableReferentFields(poles)
        with self.assertRaises(ExecuteError):
            arcpy.locref.DisableReferentFields(poles)
        arcpy.locref.EnableStationingFields(poles, "St", "Bk", "Dir")
        other = arcpy.locref.CreateLRSEvent(self.net, "Other", "POINT", "EventId", "RouteId", "FromDate", "ToDate",
                                            "LocError", "M")[0]
        for f in ("St", "Bk", "Dir"):
            arcpy.management.AddField(other, f, "TEXT")
        with self.assertRaises(ExecuteError):  # one stationing event per network
            arcpy.locref.EnableStationingFields(other, "St", "Bk", "Dir")
        arcpy.locref.DisableStationingFields(poles)
        arcpy.locref.EnableStationingFields(other, "St", "Bk", "Dir")
        with self.assertRaises(ExecuteError):  # stationing is for point events
            arcpy.locref.EnableStationingFields(self.speed, "EventId", "EventId", "EventId")

    def test_modify_lrs_event(self):
        arcpy.management.AddField(self.speed, "Other", "TEXT")
        arcpy.locref.ModifyLRSEvent(self.speed, "Other", "RouteId", "FromDate", "ToDate", "LocError", "FromM", "ToM")
        with self.assertRaises(ExecuteError):  # spanning on a network that is not a line network
            arcpy.locref.ModifyLRSEvent(self.speed, "Other", "RouteId", "FromDate", "ToDate", "LocError", "FromM",
                                        "ToM", "SPANS_ROUTES", "RouteId")

    def test_create_event_from_existing_dataset(self):
        fc = arcpy.management.CreateFeatureclass(self.fds, "Signs", "POINT", has_z="ENABLED", has_m="ENABLED")[0]
        for f, k in (("EID", "TEXT"), ("RID", "TEXT"), ("FD", "DATE"), ("TD", "DATE"), ("LE", "TEXT"), ("Mv", "DOUBLE")):
            arcpy.management.AddField(fc, f, k)
        arcpy.locref.CreateLRSEventFromExistingDataset(self.net, fc, "EID", "RID", "FD", "TD", "LE", "Mv")
        insert(fc, ["EID", "RID", "Mv"], [["X", "R2", 30.0]])
        arcpy.locref.GenerateEvents(fc)
        # R2 runs up x=0 from 0 to 50: measure 30 is (0, 30)
        self.assertEqual([round(v, 6) for v in rows(fc, ["SHAPE@XY"])[0][0]], [0.0, 30.0])


class Intersections(Base):
    def setUp(self):
        super().setUp()
        self.load_main()
        self.streets = arcpy.management.CreateFeatureclass(self.fds, "Streets", "POLYLINE", has_z="ENABLED")[0]
        arcpy.management.AddField(self.streets, "StreetId", "TEXT")
        arcpy.management.AddField(self.streets, "Name", "TEXT")
        insert(self.streets, ["StreetId", "Name", "SHAPE@"], [["S1", "Oak", "LINESTRING Z (30 -10 0, 30 10 0)"]])
        self.inter = arcpy.locref.CreateLRSIntersection(self.net, "RouteName", "Crossings",
                                                        "Streets StreetId Name -")[0]

    def test_generate_and_measure(self):
        arcpy.locref.GenerateIntersections(self.inter, edited_by_current_user="ALL_USERS")
        got = rows(self.inter, ["IntersectionName", "RouteId", "FeatureId", "FeatureClassName", "Measure"])
        # Oak crosses R1 at x=30, measure 30; R2 (x=0) is not crossed
        self.assertEqual(got, [["Main-Oak", "R1", "S1", "Streets", 30.0]])
        out = os.path.join(self.gpkg, "irm")
        arcpy.locref.CalculateIntersectingRouteMeasures(self.inter, out)
        self.assertEqual(rows(out, ["RouteId", "Measure"]), [["R1", 30.0]])
        arcpy.locref.GenerateIntersections(self.inter, edited_by_current_user="ALL_USERS")
        self.assertEqual(len(rows(self.inter, ["RouteId"])), 1)  # made again, not added twice

    def test_start_date_needs_editor_tracking(self):
        with self.assertRaises(ExecuteError):
            arcpy.locref.GenerateIntersections(self.inter, None, "1/1/2024", "ALL_USERS")

    def test_current_user_reads_editor_tracking(self):
        arcpy.management.EnableEditorTracking(self.net, last_editor_field="Editor", add_fields="ADD_FIELDS")
        with arcpy.da.UpdateCursor(self.net, ["RouteName"], "RouteId = 'R2'") as cursor:
            for row in cursor:
                cursor.updateRow(row)
        import sqlite3
        connection = sqlite3.connect(self.gpkg)  # another user's edit, written past grapy's stamping
        connection.execute('update "LRS/Highways" set Editor = ? where RouteId = ?', ("SOMEONE ELSE", "R1"))
        connection.commit()
        connection.close()
        arcpy.locref.GenerateIntersections(self.inter)
        # only R2 was last edited by this user, and Oak does not cross it
        self.assertEqual(rows(self.inter, ["RouteId"]), [])

    def test_modify_intersection(self):
        arcpy.management.AddField(self.streets, "Code", "TEXT")
        arcpy.locref.ModifyLRSIntersection(self.inter, intersecting_layers="Streets StreetId Code ,")
        with self.assertRaises(ExecuteError):  # the network on itself takes a comma only
            arcpy.locref.ModifyLRSIntersection(self.inter, intersecting_layers="Highways RouteId RouteName -")


class UtilityNetwork(unittest.TestCase):
    """An LRS whose centerline is a utility network's Pipeline Line class (ADR-231)."""

    def test_configure_utility_network_classes(self):
        directory = tempfile.mkdtemp()
        arcpy.env.workspace = arcpy.env.scratchWorkspace = directory
        gpkg = arcpy.management.CreateFileGDB(directory, "un")[0]
        fds = arcpy.management.CreateFeatureDataset(gpkg, "net", 3857)[0]
        territory = arcpy.management.CreateFeatureclass(fds, "Territory", "POLYGON", has_z="ENABLED",
                                                        has_m="ENABLED")[0]
        insert(territory, ["SHAPE@"], [["POLYGON((-10 -10, 100 -10, 100 100, -10 100, -10 -10))"]])
        un = arcpy.un.CreateUtilityNetwork(fds, "Gas", "Territory")[0]
        arcpy.un.AddDomainNetwork(un, "Pipeline", "PARTITIONED", "SOURCE")
        line, device, junction = f"{fds}/PipelineLine", f"{fds}/PipelineDevice", f"{fds}/PipelineJunction"
        for table, fields in ((line, ("CID", "RID", "FM", "TM")), (device, ("RID", "M")), (junction, ("RID", "M"))):
            for f in fields:
                arcpy.management.AddField(table, f, "DOUBLE" if f in ("FM", "TM", "M") else "TEXT")
        seq = arcpy.management.CreateTable(gpkg, "SEQ")[0]
        for f, k in (("CID", "GUID"), ("RID", "TEXT"), ("FD", "DATE"), ("TD", "DATE"), ("NID", "SHORT")):
            arcpy.management.AddField(seq, f, k)
        cp = arcpy.management.CreateFeatureclass(fds, "CP", "POINT", has_z="ENABLED")[0]
        for f, k in (("M", "DOUBLE"), ("FD", "DATE"), ("TD", "DATE"), ("RID", "TEXT"), ("NID", "SHORT")):
            arcpy.management.AddField(cp, f, k)
        rl = arcpy.management.CreateFeatureclass(fds, "RL", "POLYLINE", has_z="ENABLED")[0]
        for f, k in (("FM", "DOUBLE"), ("TM", "DOUBLE"), ("RID", "TEXT"), ("RN", "TEXT"), ("ED", "DATE"),
                     ("AT", "SHORT"), ("NID", "SHORT")):
            arcpy.management.AddField(rl, f, k)
        arcpy.locref.CreateLRSFromExistingDataset("Pipes", line, "CID", seq, "CID", "RID", "FD", "TD", "NID", cp, "M",
                                                  "FD", "TD", "RID", "NID", rl, "FM", "TM", "RID", "RN", "ED", "AT",
                                                  "NID")
        with self.assertRaises(ExecuteError):  # the device class is not a Junction class
            arcpy.locref.ConfigureUtilityNetworkFeatureClasses(line, "RID", "FM", "TM", device, "RID", "M", device,
                                                               "RID", "M")
        arcpy.locref.ConfigureUtilityNetworkFeatureClasses(line, "RID", "FM", "TM", device, "RID", "M", junction,
                                                           "RID", "M")
        net = arcpy.locref.CreateLRSNetwork(fds, "Pipes", "Engineering", "RouteId", None, "FromDate", "ToDate",
                                            measure_unit="METERS")[0]
        ev = arcpy.locref.CreateLRSEvent(net, "Valves", "POINT", "EventId", "RouteId", "FromDate", "ToDate",
                                         "LocError", "M")[0]
        insert(ev, ["EventId", "SHAPE@"], [["V1", "POINT ZM (1 1 0 0)"]])
        # with the utility network configured, BYPASS leaves the event with no route alone
        arcpy.locref.GenerateEvents(ev, "BYPASS_EVENTS_WITH_NULL_LRS_FIELDS")
        self.assertEqual(rows(ev, ["LocError", "SHAPE@XY"]), [[None, (1.0, 1.0)]])
        arcpy.locref.GenerateEvents(ev)
        self.assertEqual(rows(ev, ["LocError"]), [["Route Not Found"]])
        arcpy.locref.RemoveLRSEntity(gpkg, "UN_FEATURE_CLASS", "PipelineLine")
        with self.assertRaises(ExecuteError):
            arcpy.locref.GenerateEvents(ev, "BYPASS_EVENTS_WITH_NULL_LRS_FIELDS")


class Refused(unittest.TestCase):
    def test_each_refused_name_raises_naming_itself(self):
        for name in ("ConfigureExternalEventBehaviorsWithLRS", "ConfigureExternalEventWithLRS",
                     "ConfigureUtilityNetworkFeatureClass", "GenerateLRFeatureCount", "GenerateLRLengthSummary",
                     "GenerateLRRouteLog", "GenerateLrsDataProduct"):
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.locref, name)()
            self.assertIn(f"arcpy.locref.{name} ", str(said.exception))
            self.assertNotIn("not built in grapy yet", str(said.exception))

    def test_equivalents_are_marked(self):
        for name in ("ApplyEventBehaviors", "CalculateIntersectingRouteMeasures", "DeriveEventMeasures",
                     "GenerateCalibrationPoints", "GenerateRoutes", "OverlayEvents", "RemoveOverlappingCenterlines",
                     "ReverseLineOrders", "TranslateEventMeasures"):
            self.assertTrue(getattr(arcpy.locref, name).__grapy_equivalent__, name)
            self.assertTrue(getattr(arcpy.locref, name).__doc__.startswith("EQUIVALENT"), name)


if __name__ == "__main__":
    unittest.main()
