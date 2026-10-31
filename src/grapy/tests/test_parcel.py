"""Parcel fabrics (ADR-250) on hand-drawn parcels with answers worked out below.

The fabric ``Fab`` lives in the feature dataset ``Parcels`` (EPSG:3857, metres; xy tolerance 0.001 m). Its type ``Lot``
holds three parcels:

    (0,200) ┌───────────── C ─────────────┐ (200,200)      A: record R1, recorded 2020-01-01
            │                             │                B: record R2, recorded 2021-01-01
    (0,100) ├────────────┬────────────────┤ (200,100)      C: record R3, no date
            │     A      │       B        │                A has an extra vertex at (50,0), collinear
    (0,0)   └─────●──────┴────────────────┘ (200,0)        C's bottom edge has no vertex at (100,100)
               (50,0)

Built by hand: A gives its 5 edges ((50,0) is collinear, and ends two lines all the same, as ArcGIS Pro 3.7 builds
it); B gives 3 more (the shared edge x = 100 is one line); C's bottom is split at (100,100), a corner of A and B, so
its two halves are A's and B's tops, and C adds its left, top and right: 5 + 3 + 3 = 11 lines, at 9 vertices: 9
points. The shared edge x = 100 goes to R2, recorded later than R1; C's bottom halves go to A's R1 and B's R2 (both
dated, C's R3 not). Each unshared line runs clockwise round its parcel. All of this is what Pro 3.7 answered on the
same three parcels on 2026-10-31 (ADR-250 condition 2, tools/pro-compare/adr250-parcel.py).
"""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _layers  # noqa: E402
from grapy import _parcel as pf  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

A = "POLYGON((0 0, 50 0, 100 0, 100 100, 0 100, 0 0))"
B = "POLYGON((100 0, 200 0, 200 100, 100 100, 100 0))"
C = "POLYGON((0 100, 200 100, 200 200, 0 200, 0 100))"


def rows(path, fields, where=None):
    with arcpy.da.SearchCursor(path, fields, where) as cursor:
        return [list(r) for r in cursor]


def ends(geometry):
    shape = geometry._shape
    import shapely
    c = shapely.get_coordinates(shape)
    return (round(c[0][0], 6), round(c[0][1], 6)), (round(c[-1][0], 6), round(c[-1][1], 6))


class Fabric(unittest.TestCase):
    def setUp(self):
        _layers.LAYERS.clear()
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "parcels")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "Parcels", 3857)[0]
        self.fabric = arcpy.parcel.CreateParcelFabric(self.fds, "Fab")[0]
        arcpy.parcel.AddParcelType(self.fabric, "Lot")
        self.lots, self.lot_lines = f"{self.fds}/Lot", f"{self.fds}/Lot_Lines"
        self.points, self.records = f"{self.fds}/Fab_Points", f"{self.fds}/Fab_Records"
        self.connections = f"{self.fds}/Fab_Connections"

    def record(self, name, date=None):
        with arcpy.da.InsertCursor(self.records, ["Name", "RecordedDate", "SHAPE@"]) as cursor:
            oid = cursor.insertRow([name, date, None])
        return rows(self.records, ["GlobalID"], f"fid = {oid}")[0][0]

    def three_parcels(self):
        r1, r2, r3 = self.record("R1", "2020-01-01"), self.record("R2", "2021-01-01"), self.record("R3")
        with arcpy.da.InsertCursor(self.lots, ["Name", "CreatedByRecord", "SHAPE@"]) as cursor:
            cursor.insertRow(["A", r1, A])
            cursor.insertRow(["B", r2, B])
            cursor.insertRow(["C", r3, C])
        return r1, r2, r3

    def built(self):
        guids = self.three_parcels()
        arcpy.parcel.EnableParcelTopology(self.fabric)
        arcpy.parcel.BuildParcelFabric(self.fabric)
        return guids

    def line_at(self, a, b):
        for oid, record, shape in rows(self.lot_lines, ["OID@", "CreatedByRecord", "SHAPE@"]):
            if set(ends(shape)) == {a, b}:
                return oid, record, shape
        return None


class Model(Fabric):
    def test_the_documented_classes_are_made(self):
        for name in ("Fab_Records", "Fab_Points", "Fab_Connections", "Fab_AdjustmentLines", "Fab_AdjustmentPoints",
                     "Fab_AdjustmentVectors", "Lot", "Lot_Lines"):
            self.assertTrue(arcpy.Exists(f"{self.fds}/{name}"), name)
        fields = {f.name for f in arcpy.ListFields(self.lot_lines)}
        for name in ("CreatedByRecord", "RetiredByRecord", "Direction", "Distance", "Radius", "ArcLength", "Radius2",
                     "COGOType", "IsCOGOGround", "Rotation", "Scale", "ParentLineID", "DirectionAccuracy",
                     "DistanceAccuracy", "LabelPosition", "GlobalID", "created_user"):
            self.assertIn(name, fields)
        self.assertNotIn("FloorOrder", fields)
        self.assertIn("IsSeed", {f.name for f in arcpy.ListFields(self.lots)})
        self.assertIn("ParcelCount", {f.name for f in arcpy.ListFields(self.records)})

    def test_the_names_codes_and_defaults_are_pro_s(self):
        # Measured in Pro 3.7 on 2026-10-31: the physical names where an alias's words are not the name, the coded
        # values of PF_LabelPosition, PF_AreaUnits and the MeasurementType subtypes, the defaults, and no
        # VALIDATIONSTATUS on the points (they carry no validation rule).
        fields = {f.name for f in arcpy.ListFields(f"{self.fds}/Fab_AdjustmentLines")}
        for name in ("MeasurementStdDev", "AdjustedStdDev", "Residual", "NormalStatistic", "StudentStatistic",
                     "PelzerReliablity"):
            self.assertIn(name, fields)
        for table in ("Fab_Points", "Fab_AdjustmentPoints"):
            self.assertTrue({"EllipseMajor", "EllipseMinor", "EllipseDirection"} <=
                            {f.name for f in arcpy.ListFields(f"{self.fds}/{table}")})
        self.assertNotIn("VALIDATIONSTATUS", {f.name.upper() for f in arcpy.ListFields(self.points)})
        self.assertIn("VALIDATIONSTATUS", {f.name for f in arcpy.ListFields(self.lot_lines)})
        domains = {d.name: d.codedValues for d in arcpy.da.ListDomains(self.gpkg)}
        self.assertEqual(domains["PF_LabelPosition"], {1: "Right", 2: "Left", 3: "Centered"})
        self.assertEqual(domains["PF_AreaUnits"][109404], "Square Meter")
        self.assertEqual(len(domains["PF_AreaUnits"]), 66)
        self.assertNotIn("PF_COGOAccuracy", domains)
        self.assertNotIn("ExportStatus", domains)
        self.assertEqual({k: v["Name"] for k, v in arcpy.da.ListSubtypes(f"{self.fds}/Fab_AdjustmentLines").items()},
                         {1: "Direction Set", 2: "Distance", 3: "Geodetic Latitude", 4: "Geodetic Longitude"})
        with arcpy.da.InsertCursor(self.lots, ["Name", "SHAPE@"]) as cursor:
            cursor.insertRow(["A", A])
        with arcpy.da.InsertCursor(self.lot_lines, ["SHAPE@"]) as cursor:
            cursor.insertRow(["LINESTRING(0 0, 1 0)"])
        self.assertEqual(rows(self.lots, ["IsSeed"]), [[0]])
        self.assertEqual(rows(self.lot_lines, ["DirectionAccuracy", "DistanceAccuracy", "LabelPosition"]),
                         [[30.0, 0.15, pf.RIGHT]])

    def test_a_new_fabric_s_topology_is_enabled(self):
        # Pro 3.7: EnableParcelTopology on a new fabric warns 002007; disabling twice warns 002006.
        self.assertTrue(pf.fabric(self.fabric, "t").enabled)
        arcpy.parcel.EnableParcelTopology(self.fabric)
        self.assertIn("002007", arcpy.GetMessages(1))
        arcpy.parcel.DisableParcelTopology(self.fabric)
        arcpy.parcel.DisableParcelTopology(self.fabric)
        self.assertIn("002006", arcpy.GetMessages(1))
        self.assertFalse(pf.fabric(self.fabric, "t").enabled)

    def test_one_fabric_per_feature_dataset(self):
        with self.assertRaises(ExecuteError):
            arcpy.parcel.CreateParcelFabric(self.fds, "Other")

    def test_config_keyword_is_refused_by_name(self):
        fds = arcpy.management.CreateFeatureDataset(self.gpkg, "More", 3857)[0]
        with self.assertRaises(NotBuiltError) as said:
            arcpy.parcel.CreateParcelFabric(fds, "F2", "DEFAULTS")
        self.assertIn("config_keyword", str(said.exception))

    def test_a_strata_type_has_floor_order_and_a_type_joins_an_enabled_topology(self):
        # Pro 3.7 adds a parcel type to an enabled topology (measured); its classes and rules join it.
        arcpy.parcel.AddParcelType(self.fabric, "Strata", "TOPOLOGY_POLYGON", "STRATA_PARCELS")
        self.assertIn("FloorOrder", {f.name for f in arcpy.ListFields(f"{self.fds}/Strata")})
        self.assertIn("FloorOrder", {f.name for f in arcpy.ListFields(f"{self.fds}/Strata_Lines")})
        arcpy.parcel.AddParcelType(self.fabric, "Tax")
        import sqlite3
        connection = sqlite3.connect(self.gpkg)
        classes = {r[0] for r in connection.execute("select class_name from grapy_topology_class")}
        connection.close()
        self.assertTrue({"Parcels/Tax", "Parcels/Tax_Lines", "Parcels/Strata"} <= classes)

    def test_enable_adds_the_six_rules_per_type_and_one_for_connections(self):
        # Lot: 6 rules; connection lines: endpoint covered by points = 7; the administrative type adds 1 (its lines'
        # endpoints) + 3 more line rules, but no rule with its polygons: 4. 11 in all.
        arcpy.parcel.AddParcelType(self.fabric, "County", "ADMINISTRATIVE_POLYGON")
        arcpy.parcel.EnableParcelTopology(self.fabric)
        import sqlite3
        connection = sqlite3.connect(self.gpkg)
        rules = connection.execute("select rule_type, origin, destination from grapy_topology_rule").fetchall()
        classes = {r[0] for r in connection.execute("select class_name from grapy_topology_class")}
        connection.close()
        self.assertEqual(len(rules), 11)
        self.assertNotIn("Parcels/County", classes)
        self.assertIn("Parcels/County_Lines", classes)
        self.assertIn(("Boundary Must Be Covered By (Area-Line)", "Parcels/Lot", "Parcels/Lot_Lines"), rules)
        arcpy.management.AddRuleToTopology(f"{self.fds}/Fab_Topology", "Must Not Overlap (Area)", self.lots)
        arcpy.parcel.DisableParcelTopology(self.fabric)
        connection = sqlite3.connect(self.gpkg)
        rules = connection.execute("select rule_type, origin from grapy_topology_rule").fetchall()
        classes = {r[0] for r in connection.execute("select class_name from grapy_topology_class")}
        connection.close()
        self.assertEqual(rules, [("Must Not Overlap (Area)", "Parcels/Lot")])
        self.assertEqual(classes, {"Parcels/Lot"})

    def test_remove_parcel_type_keeps_the_classes(self):
        arcpy.parcel.DisableParcelTopology(self.fabric)
        arcpy.parcel.RemoveParcelType(self.fabric, "Lot")
        self.assertTrue(arcpy.Exists(self.lots))
        self.assertEqual(pf.fabric(self.fabric, "t").types, [])

    def test_a_class_is_named_under_its_fabric_as_the_samples_write_it(self):
        # ".../Parcels/Fab/Lot" (the pages' samples) is the class ".../Parcels/Lot": A's five lines once built.
        self.built()
        arcpy.management.MakeFeatureLayer(f"{self.fabric}/Lot", "lots", "Name = 'A'")
        out = arcpy.parcel.SelectParcelFeatures("lots")
        self.assertEqual(len(_layers.LAYERS[out.getOutput(0)].selection), 5)
        arcpy.parcel.SetParcelLineLabelPosition(f"{self.fabric}/Lot_Lines")

    def test_the_default_attribute_rules(self):
        # PRESERVE: a fixed point, or one with a recorded X, is preserved; SHAPE IS FIXED: a fixed point cannot move.
        with arcpy.da.InsertCursor(self.points, ["IsFixed", "X", "SHAPE@"]) as cursor:
            cursor.insertRow([1, None, "POINT(0 0)"])
            cursor.insertRow([0, 5.0, "POINT(5 0)"])
            cursor.insertRow([0, None, "POINT(9 0)"])
        self.assertEqual([r[0] for r in rows(self.points, ["Preserve"])], [1, 1, None])
        with self.assertRaises(Exception):
            with arcpy.da.UpdateCursor(self.points, ["SHAPE@"], "IsFixed = 1") as cursor:
                for _ in cursor:
                    cursor.updateRow(["POINT(1 1)"])
        self.assertEqual(rows(self.points, ["SHAPE@"], "IsFixed = 1")[0][0].firstPoint.X, 0.0)

    def test_the_refused_names_say_why(self):
        for name in ("UpgradeArcMapParcelFabric",):
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.parcel, name)("x")
            self.assertIn(f"arcpy.parcel.{name}", str(said.exception))
            self.assertNotIn("yet", str(said.exception))


class Links(Fabric):
    def test_links_from_the_points_history(self):
        # P1 (0,0) and P2 (10,0) are made and archived; then, after the From date, P1 moves to (1,0). A link
        # (0,0)→(1,0), 1 m long, and an anchor at (10,0). With a 2 m minimum, two anchors and no link.
        import datetime
        import time
        arcpy.management.EnableArchiving(self.points)
        with arcpy.da.InsertCursor(self.points, ["Name", "SHAPE@"]) as cursor:
            cursor.insertRow(["P1", "POINT(0 0)"])
            cursor.insertRow(["P2", "POINT(10 0)"])
        time.sleep(0.05)
        start = datetime.datetime.now(datetime.timezone.utc)
        time.sleep(0.05)
        with arcpy.da.UpdateCursor(self.points, ["SHAPE@"], "Name = 'P1'") as cursor:
            for _ in cursor:
                cursor.updateRow(["POINT(1 0)"])
        links, anchors = arcpy.parcel.GenerateParcelFabricLinks(self.fabric, f"{self.gpkg}/links", f"{self.gpkg}/anchors",
                                                                start)
        found = rows(links, ["SHAPE@"])
        self.assertEqual([ends(r[0]) for r in found], [((0.0, 0.0), (1.0, 0.0))])
        self.assertEqual([r[0].firstPoint.X for r in rows(anchors, ["SHAPE@"])], [10.0])
        links, anchors = arcpy.parcel.GenerateParcelFabricLinks(self.fabric, f"{self.gpkg}/l2", f"{self.gpkg}/a2", start,
                                                                None, "2 Meters")
        self.assertEqual(rows(links, ["OID@"]), [])
        self.assertEqual(sorted(r[0].firstPoint.X for r in rows(anchors, ["SHAPE@"])), [0.0, 10.0])

    def test_links_need_the_points_history(self):
        with self.assertRaises(ExecuteError):
            arcpy.parcel.GenerateParcelFabricLinks(self.fabric, f"{self.gpkg}/l", f"{self.gpkg}/a", "2026-01-01")


class Build(Fabric):
    def test_building_from_polygons_makes_eleven_lines_and_nine_points(self):
        r1, r2, r3 = self.built()
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 11)
        self.assertEqual(len(rows(self.points, ["OID@"])), 9)
        self.assertIsNone(self.line_at((0.0, 0.0), (100.0, 0.0)))               # the collinear vertex ends two lines
        self.assertIsNotNone(self.line_at((0.0, 0.0), (50.0, 0.0)))
        self.assertEqual(ends(self.line_at((0.0, 0.0), (0.0, 100.0))[2]), ((0.0, 0.0), (0.0, 100.0)))   # clockwise
        self.assertEqual(ends(self.line_at((100.0, 0.0), (50.0, 0.0))[2]), ((100.0, 0.0), (50.0, 0.0)))
        self.assertTrue(pf.same_guid(self.line_at((100.0, 0.0), (100.0, 100.0))[1], r2))   # R2 recorded later
        self.assertTrue(pf.same_guid(self.line_at((0.0, 100.0), (100.0, 100.0))[1], r1))   # A's R1 has a date, C's not
        self.assertTrue(pf.same_guid(self.line_at((100.0, 100.0), (200.0, 100.0))[1], r2))
        self.assertTrue(pf.same_guid(self.line_at((0.0, 200.0), (200.0, 200.0))[1], r3))
        counts = {r[0]: r[1] for r in rows(self.records, ["Name", "ParcelCount"])}
        self.assertEqual(counts, {"R1": 1, "R2": 1, "R3": 1})
        area = {r[0]: r[1].area for r in rows(self.records, ["Name", "SHAPE@"])}
        self.assertAlmostEqual(area["R1"], 10000.0)
        self.assertAlmostEqual(area["R3"], 20000.0)

    def test_building_twice_makes_nothing_more(self):
        self.built()
        arcpy.parcel.BuildParcelFabric(self.fabric)
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 11)
        self.assertEqual(len(rows(self.points, ["OID@"])), 9)

    def test_build_runs_with_the_topology_disabled(self):
        # Pro 3.7 builds with the topology disabled (measured).
        self.three_parcels()
        arcpy.parcel.DisableParcelTopology(self.fabric)
        arcpy.parcel.BuildParcelFabric(self.fabric)
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 11)

    def test_a_record_name_and_an_extent_limit_what_is_built(self):
        # Only A (R1) and, with the extent 150 50 250 150, nothing of R1: A lies at x <= 100.
        self.three_parcels()
        arcpy.parcel.EnableParcelTopology(self.fabric)
        arcpy.parcel.BuildParcelFabric(self.fabric, "150 50 250 150", "R1")
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 0)
        arcpy.parcel.BuildParcelFabric(self.fabric, None, "R1")
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 5)      # A's five edges
        self.assertEqual(len(rows(self.points, ["OID@"])), 5)

    def test_display_extent_is_refused_by_name(self):
        self.three_parcels()
        arcpy.parcel.EnableParcelTopology(self.fabric)
        with self.assertRaises(NotBuiltError):
            arcpy.parcel.BuildParcelFabric(self.fabric, "DISPLAY")

    def test_validation_before_and_after_building(self):
        # Before: each of the three polygons' boundaries is uncovered by lines — 3 errors. After: none.
        self.three_parcels()
        arcpy.parcel.EnableParcelTopology(self.fabric)
        arcpy.parcel.ValidateParcelFabric(self.fabric)
        import sqlite3
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(connection.execute("select count(*) from grapy_topology_error").fetchone()[0], 3)
        connection.close()
        arcpy.parcel.BuildParcelFabric(self.fabric)
        arcpy.parcel.ValidateParcelFabric(self.fabric)
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(connection.execute("select count(*) from grapy_topology_error").fetchone()[0], 0)
        connection.close()

    def test_validation_within_an_extent_keeps_the_errors_outside(self):
        # The three boundary errors; an extent around C only (y >= 150) replaces C's alone after building.
        self.three_parcels()
        arcpy.parcel.EnableParcelTopology(self.fabric)
        arcpy.parcel.ValidateParcelFabric(self.fabric)
        arcpy.parcel.BuildParcelFabric(self.fabric)
        arcpy.parcel.ValidateParcelFabric(self.fabric, "-10 150 210 210")
        import sqlite3
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(connection.execute("select count(*) from grapy_topology_error").fetchone()[0], 2)
        connection.close()

    def test_seeds_from_lines_and_building_them(self):
        # Four lines of record R4 close the square (300,0)–(400,100): one seed, a circle of radius 1 m at (350,50) as
        # Pro 3.7 makes it; again, none (the seed is there); a whole-fabric build leaves it a seed (counted in R4's
        # parcels), a build named by R4 makes it the 10,000 m² parcel — both as Pro 3.7 answered.
        r4 = self.record("R4")
        corners = [(300, 0), (400, 0), (400, 100), (300, 100)]
        with arcpy.da.InsertCursor(self.lot_lines, ["CreatedByRecord", "SHAPE@"]) as cursor:
            for a, b in zip(corners, corners[1:] + corners[:1]):
                cursor.insertRow([r4, f"LINESTRING({a[0]} {a[1]}, {b[0]} {b[1]})"])
        arcpy.parcel.CreateParcelSeeds(self.fabric, "r4")          # not case sensitive
        seeds = rows(self.lots, ["IsSeed", "CreatedByRecord", "SHAPE@"])
        self.assertEqual(len(seeds), 1)
        self.assertEqual(seeds[0][0], 1)
        self.assertTrue(pf.same_guid(seeds[0][1], r4))
        self.assertTrue(seeds[0][2]._shape.within(__import__("shapely").box(300, 0, 400, 100)))
        # a 64-sided circle of radius 1: area 32 sin(2 pi / 64), within 0.3 % of pi
        self.assertAlmostEqual(seeds[0][2].area, 32 * math.sin(2 * math.pi / 64), delta=1e-6)
        self.assertEqual((round(seeds[0][2].centroid.X, 6), round(seeds[0][2].centroid.Y, 6)), (350.0, 50.0))
        arcpy.parcel.CreateParcelSeeds(self.fabric, "R4")
        self.assertEqual(len(rows(self.lots, ["OID@"])), 1)
        arcpy.parcel.BuildParcelFabric(self.fabric)
        self.assertEqual(rows(self.lots, ["IsSeed"]), [[1]])
        self.assertEqual(rows(self.records, ["ParcelCount"], "Name = 'R4'")[0][0], 1)
        arcpy.parcel.BuildParcelFabric(self.fabric, None, "R4")
        built = rows(self.lots, ["IsSeed", "SHAPE@"])
        self.assertEqual(built[0][0], 0)
        self.assertAlmostEqual(built[0][1].area, 10000.0)
        self.assertEqual(len(rows(self.points, ["OID@"])), 4)
        self.assertEqual(rows(self.records, ["ParcelCount"], "Name = 'R4'")[0][0], 1)

    def test_seeds_need_an_existing_record(self):
        with self.assertRaises(ExecuteError):
            arcpy.parcel.CreateParcelSeeds(self.fabric, "Nobody")


class Records(Fabric):
    def test_records_from_a_field_and_from_an_expression(self):
        # RecName: "Deed 9", "deed 9" (same record, names compared without case), "" (no record), "Plan X": 2 records.
        arcpy.management.AddField(self.lots, "RecName", "TEXT")
        with arcpy.da.InsertCursor(self.lots, ["Name", "RecName", "SHAPE@"]) as cursor:
            cursor.insertRow(["A1", "Deed 9", A])
            cursor.insertRow(["B1", "deed 9", B])
            cursor.insertRow(["C1", "", C])
            cursor.insertRow(["D1", "Plan X", "POLYGON((300 0, 400 0, 400 100, 300 100, 300 0))"])
        arcpy.parcel.CreateParcelRecords(self.lots, "RecName")
        made = {r[0]: r[1] for r in rows(self.records, ["Name", "ParcelCount"])}
        self.assertEqual(made, {"Deed 9": 2, "Plan X": 1})
        self.assertIsNone(rows(self.lots, ["CreatedByRecord"], "Name = 'C1'")[0][0])
        # Left($feature.Name, 1): "A", "B", "C", "D" — four more records, one parcel each.
        arcpy.parcel.CreateParcelRecords(self.lots, None, "Left($feature.Name, 1)", "EXPRESSION")
        made = {r[0]: r[1] for r in rows(self.records, ["Name", "ParcelCount"])}
        self.assertEqual(made, {"Deed 9": 0, "Plan X": 0, "A": 1, "B": 1, "C": 1, "D": 1})
        area = {r[0]: r[1] for r in rows(self.records, ["Name", "SHAPE@"])}
        self.assertAlmostEqual(area["C"].area, 20000.0)

    def test_the_field_must_be_text(self):
        with self.assertRaises(ExecuteError):
            arcpy.parcel.CreateParcelRecords(self.lots, "IsSeed")


class Editing(Fabric):
    def test_delete_parcels_takes_lines_and_points_no_one_else_uses(self):
        # Deleting B: its bottom (100,0)–(200,0) and right (200,0)–(200,100) go; x = 100 stays for A and the top
        # (100,100)–(200,100) for C. Of B's corners only (200,0) is used by nothing left: one point goes.
        self.built()
        arcpy.management.MakeFeatureLayer(self.lots, "lots", "Name = 'B'")
        arcpy.parcel.DeleteParcels("lots")
        self.assertEqual(sorted(r[0] for r in rows(self.lots, ["Name"])), ["A", "C"])
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 9)
        self.assertIsNone(self.line_at((200.0, 0.0), (200.0, 100.0)))
        self.assertIsNotNone(self.line_at((100.0, 0.0), (100.0, 100.0)))
        self.assertEqual(len(rows(self.points, ["OID@"])), 8)
        self.assertEqual(rows(self.records, ["ParcelCount"], "Name = 'R2'")[0][0], 0)

    def test_select_parcel_features_answers_a_parcels_lines_and_points(self):
        self.built()
        arcpy.management.MakeFeatureLayer(self.lots, "lots")
        arcpy.management.SelectLayerByAttribute("lots", "NEW_SELECTION", "Name = 'A'")
        out = arcpy.parcel.SelectParcelFeatures("lots")
        lines, points = _layers.LAYERS[out.getOutput(0)], _layers.LAYERS[out.getOutput(1)]
        self.assertEqual(len(lines.selection), 5)
        self.assertEqual(len(points.selection), 5)

    def test_label_positions(self):
        # The shared lines (x = 100, and the tops of A and B under C) are Centered; every other line has one parcel,
        # Right when the parcel is on the right of its direction (start → end), Left otherwise.
        self.built()
        arcpy.parcel.SetParcelLineLabelPosition([self.lot_lines])
        shared = {frozenset({(100.0, 0.0), (100.0, 100.0)}), frozenset({(0.0, 100.0), (100.0, 100.0)}),
                  frozenset({(100.0, 100.0), (200.0, 100.0)})}
        import shapely
        parcels = [shapely.from_wkt(w) for w in (A, B, C)]
        for position, shape in rows(self.lot_lines, ["LabelPosition", "SHAPE@"]):
            a, b = ends(shape)
            if frozenset({a, b}) in shared:
                self.assertEqual(position, pf.CENTERED)
                continue
            middle = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
            length = math.hypot(b[0] - a[0], b[1] - a[1])
            right = shapely.Point(middle[0] + (b[1] - a[1]) / length, middle[1] - (b[0] - a[0]) / length)
            expected = pf.RIGHT if any(p.covers(right) for p in parcels) else pf.LEFT
            self.assertEqual(position, expected)

    def test_merge_collinear_boundaries(self):
        # (0,0)–(50,0.02) and (50,0.02)–(100,0): the shared point is 0.02 m off the chord (0,0)–(100,0). With a 0.01 m
        # tolerance nothing merges; with 0.05 m one line: Distance 50 + 50.01 = 100.01, Direction 90 (both 90),
        # COGO Type Computed, the vertex kept, the point at (50,0.02) deleted.
        r1 = self.record("R1")
        with arcpy.da.InsertCursor(self.lot_lines, ["CreatedByRecord", "Distance", "Direction", "SHAPE@"]) as cursor:
            cursor.insertRow([r1, 50.0, 90.0, "LINESTRING(0 0, 50 0.02)"])
            cursor.insertRow([r1, 50.01, 90.0, "LINESTRING(50 0.02, 100 0)"])
        with arcpy.da.InsertCursor(self.points, ["CreatedByRecord", "SHAPE@"]) as cursor:
            for xy in ("0 0", "50 0.02", "100 0"):
                cursor.insertRow([r1, f"POINT({xy})"])
        arcpy.parcel.MergeCollinearParcelBoundaries(self.lot_lines, "0.01 Meters")
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 2)
        arcpy.parcel.MergeCollinearParcelBoundaries(self.lot_lines, "0.05 Meters")
        merged = rows(self.lot_lines, ["Distance", "Direction", "COGOType", "SHAPE@"])
        self.assertEqual(len(merged), 1)
        self.assertAlmostEqual(merged[0][0], 100.01)
        self.assertAlmostEqual(merged[0][1], 90.0)
        self.assertEqual(merged[0][2], pf.COMPUTED)
        self.assertEqual(merged[0][3].pointCount, 3)
        self.assertEqual(len(rows(self.points, ["OID@"])), 2)

    def test_no_merge_where_a_third_line_meets(self):
        with arcpy.da.InsertCursor(self.lot_lines, ["SHAPE@"]) as cursor:
            cursor.insertRow(["LINESTRING(0 0, 50 0)"])
            cursor.insertRow(["LINESTRING(50 0, 100 0)"])
            cursor.insertRow(["LINESTRING(50 0, 50 50)"])
        arcpy.parcel.MergeCollinearParcelBoundaries(self.lot_lines, "1 Meters")
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 3)

    def test_different_directions_give_a_null_direction(self):
        # 90 and 90.01 degrees differ by 36 seconds, beyond rounding: the merged Direction is null.
        with arcpy.da.InsertCursor(self.lot_lines, ["Direction", "SHAPE@"]) as cursor:
            cursor.insertRow([90.0, "LINESTRING(0 0, 50 0)"])
            cursor.insertRow([90.01, "LINESTRING(50 0, 100 0)"])
        arcpy.parcel.MergeCollinearParcelBoundaries(self.lot_lines, "0.1 Meters")
        merged = rows(self.lot_lines, ["Direction"])
        self.assertEqual(merged, [[None]])

    def test_duplicate_to_strata_floors(self):
        # A duplicated three times into the strata type Strata, floors 1, 2, 3: three polygons and A's five lines per
        # floor, fifteen; the same type gets polygons only.
        self.built()
        arcpy.parcel.DisableParcelTopology(self.fabric)
        arcpy.parcel.AddParcelType(self.fabric, "Strata", "TOPOLOGY_POLYGON", "STRATA_PARCELS")
        arcpy.management.MakeFeatureLayer(self.lots, "lots", "Name = 'A'")
        arcpy.parcel.DuplicateParcels("lots", "Condo", "Strata", 3, "FloorOrder", 1, 1)
        floors = sorted(r[0] for r in rows(f"{self.fds}/Strata", ["FloorOrder"]))
        self.assertEqual(floors, [1, 2, 3])
        lines = rows(f"{self.fds}/Strata_Lines", ["FloorOrder"])
        self.assertEqual(len(lines), 15)
        self.assertEqual(sorted({r[0] for r in lines}), [1, 2, 3])
        self.assertEqual(rows(self.records, ["ParcelCount"], "Name = 'Condo'")[0][0], 3)
        arcpy.parcel.DuplicateParcels("lots", "Again", "Lot")
        self.assertEqual(len(rows(self.lots, ["OID@"])), 4)
        self.assertEqual(len(rows(self.lot_lines, ["OID@"])), 11)


class Exports(Fabric):
    def test_export_record_features(self):
        # R1 created A and four lines: A's two bottom halves, its left and its top under C (x = 100 went to R2).
        self.built()
        out_fds = arcpy.management.CreateFeatureDataset(self.gpkg, "Export", 3857)[0]
        arcpy.management.MakeFeatureLayer(self.records, "recs", "Name = 'R1'")
        arcpy.parcel.ExportParcelRecordFeatures("recs", out_fds)
        self.assertEqual(rows(f"{out_fds}/Lot", ["Name", "ExportedStatus"]), [["A", pf.CREATED]])
        self.assertEqual(len(rows(f"{out_fds}/Lot_Lines", ["OID@"])), 4)
        self.assertEqual(rows(f"{out_fds}/Fab_Records", ["Name"]), [["R1"]])
        self.assertTrue(arcpy.Exists(f"{out_fds}/Fab_Connections"))
        # again, the classes are emptied first
        arcpy.parcel.ExportParcelRecordFeatures("recs", out_fds)
        self.assertEqual(len(rows(f"{out_fds}/Lot_Lines", ["OID@"])), 4)

    def test_export_record_needs_one_record(self):
        self.built()
        out_fds = arcpy.management.CreateFeatureDataset(self.gpkg, "Export", 3857)[0]
        with self.assertRaises(ExecuteError):
            arcpy.parcel.ExportParcelRecordFeatures(self.records, out_fds)

    def test_export_sequenced_parcel_features(self):
        # A from its north-east corner (100,100), clockwise: (100,100) → (100,0) → (50,0) → (0,0) → (0,100). Lines:
        # the right edge 1, the bottom's halves 2 and 3, the left 4, the top 5; points P1 … P5 in that order; in
        # "ABC-##1" the ## is the number and the 1 is kept, ABC-011 … ABC-051. Every value is Pro 3.7's (measured).
        self.built()
        out_fds = arcpy.management.CreateFeatureDataset(self.gpkg, "Seq", 3857)[0]
        arcpy.management.MakeFeatureLayer(self.lots, "a", "Name = 'A'")
        arcpy.parcel.ExportSequencedParcelFeatures("a", out_fds, "NE", "P#", "ABC-##1")
        lines = {frozenset(ends(r[1])): r[0] for r in rows(f"{out_fds}/Lot_Lines", ["Sequence", "SHAPE@"])}
        self.assertEqual(lines[frozenset({(100.0, 100.0), (100.0, 0.0)})], "ABC-011")
        self.assertEqual(lines[frozenset({(100.0, 0.0), (50.0, 0.0)})], "ABC-021")
        self.assertEqual(lines[frozenset({(50.0, 0.0), (0.0, 0.0)})], "ABC-031")
        self.assertEqual(lines[frozenset({(0.0, 0.0), (0.0, 100.0)})], "ABC-041")
        self.assertEqual(lines[frozenset({(0.0, 100.0), (100.0, 100.0)})], "ABC-051")
        points = {(r[1].firstPoint.X, r[1].firstPoint.Y): r[0] for r in rows(f"{out_fds}/Fab_Points", ["Sequence", "SHAPE@"])}
        self.assertEqual(points, {(100.0, 100.0): "P1", (100.0, 0.0): "P2", (50.0, 0.0): "P3", (0.0, 0.0): "P4",
                                  (0.0, 100.0): "P5"})
        arcpy.parcel.ExportSequencedParcelFeatures("a", out_fds, "SW")
        points = {(r[1].firstPoint.X, r[1].firstPoint.Y): r[0] for r in rows(f"{out_fds}/Fab_Points", ["Sequence", "SHAPE@"])}
        self.assertEqual(points, {(0.0, 0.0): "1", (0.0, 100.0): "2", (100.0, 100.0): "3", (100.0, 0.0): "4",
                                  (50.0, 0.0): "5"})

    def test_copy_parcels_to_another_geopackage(self):
        self.built()
        arcpy.management.AddField(self.lots, "Zoning", "TEXT")
        other = arcpy.management.CreateFileGDB(self.directory, "copy")[0]
        out = arcpy.parcel.CopyParcels(self.fabric, other)
        self.assertEqual(out.getOutput(1), f"{other}/Parcels/Fab")
        self.assertEqual(len(rows(f"{other}/Parcels/Lot_Lines", ["OID@"])), 11)
        self.assertIn("Zoning", {f.name for f in arcpy.ListFields(f"{other}/Parcels/Lot")})
        self.assertEqual(sorted(r[0] for r in rows(f"{other}/Parcels/Lot", ["GlobalID"])),
                         sorted(r[0] for r in rows(self.lots, ["GlobalID"])))

    def test_copy_a_selection(self):
        # A selected: A, its five lines, its five points and its records (R1, and R2 for the shared line).
        self.built()
        arcpy.management.MakeFeatureLayer(self.lots, "a", "Name = 'A'")
        arcpy.management.SelectLayerByAttribute("a", "NEW_SELECTION", "Name = 'A'")
        out = arcpy.parcel.CopyParcels("a", self.gpkg, "Copied", "Fab2")
        self.assertEqual(len(rows(f"{self.gpkg}/Copied/Lot", ["OID@"])), 1)
        self.assertEqual(len(rows(f"{self.gpkg}/Copied/Lot_Lines", ["OID@"])), 5)
        self.assertEqual(len(rows(f"{self.gpkg}/Copied/Fab2_Points", ["OID@"])), 5)
        self.assertEqual(sorted(r[0] for r in rows(f"{self.gpkg}/Copied/Fab2_Records", ["Name"])), ["R1", "R2"])
        self.assertEqual(out.getOutput(0), f"{self.gpkg}/Copied")

    def test_append_parcels_matches_records_by_name(self):
        self.built()
        other = arcpy.management.CreateFileGDB(self.directory, "target")[0]
        fds = arcpy.management.CreateFeatureDataset(other, "P", 3857)[0]
        target = arcpy.parcel.CreateParcelFabric(fds, "T")[0]
        arcpy.parcel.AddParcelType(target, "Lot")
        with arcpy.da.InsertCursor(f"{fds}/T_Records", ["Name", "SHAPE@"]) as cursor:
            cursor.insertRow(["R1", None])
        arcpy.parcel.AppendParcels(self.fabric, target, "KEEP_TARGET")
        self.assertEqual(sorted(r[0] for r in rows(f"{fds}/T_Records", ["Name"])), ["R1", "R2", "R3"])
        self.assertEqual(len(rows(f"{fds}/Lot", ["OID@"])), 3)
        self.assertEqual(len(rows(f"{fds}/Lot_Lines", ["OID@"])), 11)
        guid = rows(f"{fds}/T_Records", ["GlobalID"], "Name = 'R1'")[0][0]
        self.assertTrue(pf.same_guid(rows(f"{fds}/Lot", ["CreatedByRecord"], "Name = 'A'")[0][0], guid))
        # KEEP_TARGET: appending again adds parcels, but no point on top of a point already there
        arcpy.parcel.AppendParcels(self.fabric, target, "KEEP_TARGET")
        self.assertEqual(len(rows(f"{fds}/T_Points", ["OID@"])), 9)
        self.assertEqual(len(rows(f"{fds}/Lot", ["OID@"])), 6)

    def test_append_needs_matching_types(self):
        self.built()
        fds = arcpy.management.CreateFeatureDataset(self.gpkg, "P2", 3857)[0]
        target = arcpy.parcel.CreateParcelFabric(fds, "T")[0]
        with self.assertRaises(ExecuteError):
            arcpy.parcel.AppendParcels(self.fabric, target)


class Points(Fabric):
    def test_import_updates_creates_and_lines_follow(self):
        # Fabric points at (0,0) and (100,0) with the line between. Source: (0.0004,0) named P1 is within 0.001 m of
        # (0,0) — updated, the line's start follows; (500,500) matches nothing — created under record Imp.
        with arcpy.da.InsertCursor(self.points, ["SHAPE@"]) as cursor:
            cursor.insertRow(["POINT(0 0)"])
            cursor.insertRow(["POINT(100 0)"])
        with arcpy.da.InsertCursor(self.lot_lines, ["SHAPE@"]) as cursor:
            cursor.insertRow(["LINESTRING(0 0, 100 0)"])
        source = arcpy.management.CreateFeatureclass(self.gpkg, "source", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(source, "Name", "TEXT")
        with arcpy.da.InsertCursor(source, ["Name", "SHAPE@"]) as cursor:
            cursor.insertRow(["P1", "POINT(0.0004 0)"])
            cursor.insertRow(["P2", "POINT(500 500)"])
        arcpy.parcel.ImportParcelFabricPoints(source, self.fabric, "PROXIMITY", "0.001 Meters", "ALL", "Imp")
        found = sorted((r[0] or "", round(r[1].firstPoint.X, 4)) for r in rows(self.points, ["Name", "SHAPE@"]))
        self.assertEqual(found, [("", 100.0), ("P1", 0.0004), ("P2", 500.0)])
        self.assertEqual(ends(rows(self.lot_lines, ["SHAPE@"])[0][0])[0], (0.0004, 0.0))
        self.assertEqual(len(rows(self.records, ["OID@"], "Name = 'Imp'")), 1)

    def test_retire_and_replace_and_conflicts(self):
        # Two fabric points within 1 m of the source point (0,0): a conflict, nothing imported, two table rows.
        # Then RETIRE_AND_REPLACE at (100,0): the old point retired by R, a new one created.
        with arcpy.da.InsertCursor(self.points, ["SHAPE@"]) as cursor:
            cursor.insertRow(["POINT(0 0)"])
            cursor.insertRow(["POINT(0.5 0)"])
            cursor.insertRow(["POINT(100 0)"])
        source = arcpy.management.CreateFeatureclass(self.gpkg, "src", "POINT", spatial_reference=3857)[0]
        with arcpy.da.InsertCursor(source, ["SHAPE@"]) as cursor:
            cursor.insertRow(["POINT(0 0)"])
            cursor.insertRow(["POINT(100.1 0)"])
        conflicts = os.path.join(self.directory, "conflicts.gpkg") + "/conflicts"
        arcpy.parcel.ImportParcelFabricPoints(source, self.fabric, "PROXIMITY", "1 Meters", "RETIRE_AND_REPLACE", "R",
                                              None, conflicts)
        self.assertEqual(len(rows(conflicts, ["SourceOID"])), 2)
        guid = rows(self.records, ["GlobalID"], "Name = 'R'")[0][0]
        retired = rows(self.points, ["SHAPE@"], "RetiredByRecord is not null")
        self.assertEqual([r[0].firstPoint.X for r in retired], [100.0])
        self.assertEqual(len(rows(self.points, ["OID@"], f"CreatedByRecord = '{guid}'")), 1)

    def test_a_fixed_point_is_not_updated(self):
        with arcpy.da.InsertCursor(self.points, ["IsFixed", "SHAPE@"]) as cursor:
            cursor.insertRow([1, "POINT(0 0)"])
        source = arcpy.management.CreateFeatureclass(self.gpkg, "src", "POINT", spatial_reference=3857)[0]
        with arcpy.da.InsertCursor(source, ["SHAPE@"]) as cursor:
            cursor.insertRow(["POINT(0.5 0)"])
        arcpy.parcel.ImportParcelFabricPoints(source, self.fabric, "PROXIMITY", "1 Meters", "GEOMETRY_XYZ")
        self.assertEqual([r[0].firstPoint.X for r in rows(self.points, ["SHAPE@"])], [0.0])

    def test_find_adjacent_points(self):
        # (0,0) and (0.5,0) are within 1 m of each other; (10,0) is not. At 0, the stacked pair at (20,0) only.
        with arcpy.da.InsertCursor(self.points, ["SHAPE@"]) as cursor:
            for xy in ("0 0", "0.5 0", "10 0", "20 0", "20 0"):
                cursor.insertRow([f"POINT({xy})"])
        out = arcpy.parcel.FindAdjacentParcelPoints(self.points, f"{self.gpkg}/adjacent", "1 Meters")[0]
        self.assertEqual(sorted(r[0].firstPoint.X for r in rows(out, ["SHAPE@"])), [0.0, 0.5, 20.0, 20.0])
        out = arcpy.parcel.FindAdjacentParcelPoints(self.points, f"{self.gpkg}/stacked", "0 Meters")[0]
        self.assertEqual(sorted(r[0].firstPoint.X for r in rows(out, ["SHAPE@"])), [20.0, 20.0])

    def test_find_disconnected_points(self):
        # The line (0,0)–(100,0). (0,0) is at its end; (50,0) on its interior; (500,500) alone; (600,600) alone and
        # fixed. Defaults: (500,500). NO_IGNORE_FIXED: and (600,600). CONSIDER_EDGE: and (50,0).
        with arcpy.da.InsertCursor(self.lot_lines, ["SHAPE@"]) as cursor:
            cursor.insertRow(["LINESTRING(0 0, 100 0)"])
        with arcpy.da.InsertCursor(self.points, ["IsFixed", "SHAPE@"]) as cursor:
            for fixed, xy in ((0, "0 0"), (0, "50 0"), (0, "500 500"), (1, "600 600")):
                cursor.insertRow([fixed, f"POINT({xy})"])

        def found(*args):
            out = arcpy.parcel.FindDisconnectedParcelPoints(self.points, f"{self.gpkg}/d{len(args)}", *args)[0]
            return sorted(r[0].firstPoint.X for r in rows(out, ["SHAPE@"]))
        self.assertEqual(found(), [500.0])
        self.assertEqual(found("NO_IGNORE_FIXED"), [500.0, 600.0])
        self.assertEqual(found("IGNORE_FIXED", "IGNORE_CONSTRAINED", "CONSIDER_EDGE"), [50.0, 500.0])

    def test_gaps_and_overlaps(self):
        # P1 (0,0)-(10,10), P2 (11,0)-(20,10), P3 (0,10)-(20,20), P4 (0,-10)-(20,0) enclose the strip x 10..11,
        # y 0..10: a gap 1 m wide, 10 m². P5 (19.5,0)-(25,10) overlaps P2 by a strip 0.5 m wide: 5 m². P6
        # (0,0)-(5,5) overlaps P1 by 25 m², 5 m wide: wider than 2 m, left out.
        boxes = {"P1": (0, 0, 10, 10), "P2": (11, 0, 20, 10), "P3": (0, 10, 20, 20), "P4": (0, -10, 20, 0),
                 "P5": (19.5, 0, 25, 10), "P6": (0, 0, 5, 5)}
        with arcpy.da.InsertCursor(self.lots, ["Name", "SHAPE@"]) as cursor:
            for name, (x0, y0, x1, y1) in boxes.items():
                cursor.insertRow([name, f"POLYGON(({x0} {y0}, {x1} {y0}, {x1} {y1}, {x0} {y1}, {x0} {y0}))"])
        # Pro 3.7 found the same two, in a field GapOverlap holding GAP and OVERLAP, naming the input in ParcelInput1.
        out = arcpy.parcel.FindGapsAndOverlaps([self.lots], f"{self.gpkg}/go")[0]
        found = sorted((r[0], r[1], round(r[2].area, 6)) for r in rows(out, ["GapOverlap", "ParcelInput1", "SHAPE@"]))
        self.assertEqual(found, [("GAP", "Lot", 10.0), ("OVERLAP", "Lot", 5.0)])
        arcpy.management.MakeFeatureLayer(self.lots, "first", "Name <> 'P5'")
        arcpy.management.MakeFeatureLayer(self.lots, "second", "Name = 'P5'")
        out = arcpy.parcel.FindGapsAndOverlaps(["first", "second"], f"{self.gpkg}/go2", "2 Meters", "BETWEEN_LAYERS")[0]
        found = sorted((r[0], r[1], r[2], round(r[3].area, 6))
                       for r in rows(out, ["GapOverlap", "ParcelInput1", "ParcelInput2", "SHAPE@"]))
        self.assertEqual(found, [("GAP", "first", None, 10.0), ("OVERLAP", "first", "second", 5.0)])


if __name__ == "__main__":
    unittest.main()
