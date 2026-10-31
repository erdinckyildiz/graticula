"""Turns in grapy's network datasets: turn feature classes, their tools, and solvers that honour them (ADR-239).

                 N(10,20) ──────── (20,20)          EN: E(20,10)–(20,20)–N(10,20), 20 long
                    │                  │
    W(0,10) ───── C(10,10) ───────── E(20,10)       WC 10, CE 10, CN 10
                    │
                  S(10,5)                           CS: C–S, 5 long, a dead end

Every road takes one minute per 10 m. From W to N the straight road turns left at C (20 m); with that turn prohibited
the cheapest way is out to the dead end at S, a U-turn there and back up (30 m) — or, U-turns forbidden, round by E
(40 m).
"""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.na as na  # noqa: E402
import grapy.nax as nax  # noqa: E402
from grapy import _turns  # noqa: E402

ROADS = [("WC", "LINESTRING(0 10,10 10)"), ("CE", "LINESTRING(10 10,20 10)"), ("CN", "LINESTRING(10 10,10 20)"),
         ("CS", "LINESTRING(10 10,10 5)"), ("EN", "LINESTRING(20 10,20 20,10 20)")]
OID = {name: n for n, (name, _) in enumerate(ROADS, start=1)}


class Turns(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "transport", 3857)[0]
        self.roads = arcpy.management.CreateFeatureclass(self.fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(self.roads, "name", "TEXT")
        arcpy.management.AddField(self.roads, "road_id", "LONG")
        arcpy.management.AddField(self.roads, "minutes", "DOUBLE")
        import shapely
        with arcpy.da.InsertCursor(self.roads, ["name", "road_id", "SHAPE@", "minutes"]) as written:
            for name, wkt in ROADS:
                written.insertRow((name, 100 + OID[name], wkt, shapely.from_wkt(wkt).length / 10))
        self.turns = na.CreateTurnFeatureClass(self.fds, "turns", 3)[0]
        arcpy.management.AddField(self.turns, "closed", "SHORT")
        arcpy.management.AddField(self.turns, "delay", "DOUBLE")
        self.network = na.CreateNetworkDataset(self.fds, "streets", ["roads", "turns"], "NO_ELEVATION")[0]
        self.minutes()

    def minutes(self, *extra):
        """A minute for every 10 m of road, and the turn evaluators given."""
        arcpy.graticula.AddNetworkAttribute(self.network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes"), *extra])

    def turn(self, edges, end, **values):
        fields = ["Edge1End"] + [f"Edge{k}{p}" for k in range(1, len(edges) + 1) for p in ("FCID", "FID", "Pos")]
        row = [end] + [v for name in edges for v in (1, OID[name], 0.5)]
        with arcpy.da.InsertCursor(self.turns, [*fields, *values]) as written:
            written.insertRow([*row, *values.values()])

    def points(self, name, rows):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(path, "Name", "TEXT")
        with arcpy.da.InsertCursor(path, ["Name", "SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def route(self, start, end, mode="Driving"):
        route = nax.Route(self.network)
        route.travelMode = mode
        with route.insertCursor(nax.RouteInputDataType.Stops, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["a", start])
            cursor.insertRow(["b", end])
        result = route.solve()
        if not result.solveSucceeded:
            return None
        (total,), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes"])
        return round(total, 9)

    # ---- the turn feature class and its tools

    def test_turn_feature_class_schema_and_maximum_edges(self):
        names = [f.name for f in arcpy.ListFields(self.turns)]
        self.assertEqual(names[1:11], ["Edge1End", "Edge1FCID", "Edge1FID", "Edge1Pos", "Edge2FCID", "Edge2FID",
                                       "Edge2Pos", "Edge3FCID", "Edge3FID", "Edge3Pos"])
        na.IncreaseMaximumEdges(self.turns, 5)
        names = [f.name for f in arcpy.ListFields(self.turns)]
        self.assertIn("Edge5Pos", names)
        with self.assertRaises(arcpy.ExecuteError):
            na.IncreaseMaximumEdges(self.turns, 4)                     # the maximum only grows
        with self.assertRaises(arcpy.ExecuteError):
            na.CreateTurnFeatureClass(self.fds, "too_many", 51)
        described = arcpy.Describe(self.network)
        self.assertTrue(described.supportsTurns)
        self.assertEqual([(s.name, s.sourceType) for s in described.turnSources], [("turns", "TurnFeature")])
        self.assertIs(nax.CreateTurnFeatureClass, na.CreateTurnFeatureClass)

    def test_build_reads_turns_and_the_query_api_lists_them(self):
        self.turn(["WC", "CN"], "Y", closed=1)
        self.turn(["WC", "CE", "EN"], "Y", delay=2.0)
        self.turn(["WC", "EN"], "Y")                                    # WC and EN do not meet: not a turn
        na.BuildNetwork(self.network)
        self.assertIn("1 turn feature(s) do not name a chain", arcpy.GetMessages())
        network = nax.NetworkDataset(self.network)
        self.assertEqual(network.turnCount, 2)
        arcpy.graticula.AddNetworkAttribute(self.network, "Closed", "Restriction", None, [("turns", "BOTH", "FIELD",
                                                                                           "closed")])
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Length", ["Closed"])
        na.BuildNetwork(self.network)
        rows = list(network.turns(["EID", "SOURCEOID", "EDGEEIDS", "ISRESTRICTED"], ["Closed"], travel_mode="Driving"))
        # Edges are numbered as built: WC 1, CE 2, CN 3, CS 4, EN 5.
        self.assertEqual(rows, [(1, 1, [1, 3], True, True), (2, 2, [1, 2, 5], False, False)])

    def test_update_by_geometry_and_alternate_ids(self):
        # A turn drawn from the middle of WC through C and up CN, its edge fields empty.
        with arcpy.da.InsertCursor(self.turns, ["SHAPE@"]) as written:
            written.insertRow(("LINESTRING(5 10,10 10,10 15)",))
            written.insertRow(("LINESTRING(15 10,10 10,10 7.5)",))     # from CE back through C and down CS
        na.BuildNetwork(self.network)
        na.UpdateByGeometry(self.turns)
        read = lambda: [r for r in arcpy.da.SearchCursor(  # noqa: E731
            self.turns, ["Edge1End", "Edge1FCID", "Edge1FID", "Edge2FID", "Edge3FID"])]
        self.assertEqual(read(), [("Y", 1, OID["WC"], OID["CN"], None), ("N", 1, OID["CE"], OID["CS"], None)])
        na.PopulateAlternateIDFields(self.network, "road_id")
        self.assertEqual([r for r in arcpy.da.SearchCursor(self.turns, ["AltID1", "AltID2", "AltID3"])],
                         [(101, 103, None), (102, 104, None)])
        # Edge object ids change (here, by hand); the alternate ids find them again.
        with arcpy.da.UpdateCursor(self.turns, ["Edge1FID", "Edge2FID"]) as rows:
            for row in rows:
                rows.updateRow([99, 98])
        na.UpdateByAlternateIDFields(self.network, "road_id")
        self.assertEqual([r[2:4] for r in read()], [(OID["WC"], OID["CN"]), (OID["CE"], OID["CS"])])
        na.BuildNetwork(self.network)
        self.assertEqual(nax.NetworkDataset(self.network).turnCount, 2)

    def test_turn_table_to_turn_feature_class(self):
        table = arcpy.management.CreateTable(self.directory, "turntable.gpkg")[0]
        for field, kind in (("NODE_", "LONG"), ("ARC1_", "LONG"), ("ARC2_", "LONG"), ("ANGLE", "DOUBLE"),
                            ("MINUTES", "DOUBLE")):
            arcpy.management.AddField(table, field, kind)
        with arcpy.da.InsertCursor(table, ["NODE_", "ARC1_", "ARC2_", "ANGLE", "MINUTES"]) as written:
            written.insertRow((1, OID["WC"], OID["CN"], 90, 0.5))
            written.insertRow((2, OID["CE"], OID["CE"], 180, 1))       # a U-turn: not made without a nodes table
        out = na.TurnTableToTurnFeatureClass(table, self.roads, "from_table", maximum_edges=2)[0]
        self.assertTrue(out.replace("\\", "/").endswith("transport/from_table"))
        rows = list(arcpy.da.SearchCursor(out, ["Edge1End", "Edge1FCID", "Edge1FID", "Edge2FID", "MINUTES", "SHAPE@WKT"]))
        self.assertEqual(rows, [("Y", 1, OID["WC"], OID["CN"], 0.5, "LINESTRING (5 10, 10 10, 10 15)")])
        self.assertNotIn("ANGLE", [f.name for f in arcpy.ListFields(out)])
        with self.assertRaises(arcpy.NotBuiltError):
            na.TurnTableToTurnFeatureClass(table, self.roads, "with_nodes", "nodes.dbf")

    # ---- solving through turns

    def test_restricted_turns_and_u_turn_policies(self):
        self.minutes()
        self.turn(["WC", "CN"], "Y", closed=1)
        arcpy.graticula.AddNetworkAttribute(self.network, "Closed", "Restriction", None,
                                            [("turns", "BOTH", "FIELD", "closed")])
        arcpy.graticula.AddNetworkTravelMode(self.network, "Free", "Minutes", [], "Minutes")
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Minutes", ["Closed"], "Minutes")
        arcpy.graticula.AddNetworkTravelMode(self.network, "NoUTurns", "Minutes", ["Closed"], "Minutes",
                                             u_turns="NO_UTURNS")
        arcpy.graticula.AddNetworkTravelMode(self.network, "DeadEnds", "Minutes", ["Closed"], "Minutes",
                                             u_turns="ALLOW_DEAD_ENDS_ONLY")
        na.BuildNetwork(self.network)
        self.assertEqual(self.route((1, 10), (10, 19), "Free"), 1.8)          # 9 + 9 m, left at C
        self.assertEqual(self.route((1, 10), (10, 19), "Driving"), 2.8)       # 9 + 5 + 5 + 9: U-turn at S
        self.assertEqual(self.route((1, 10), (10, 19), "DeadEnds"), 2.8)      # S is a dead end
        self.assertEqual(self.route((1, 10), (10, 19), "NoUTurns"), 4.0)      # 9 + 10 + 20 + 1 round by E

    def test_multi_edge_turns_and_global_turn_delays(self):
        self.minutes()
        self.turn(["WC", "CE", "EN"], "Y", closed=1)
        arcpy.graticula.AddNetworkAttribute(self.network, "Closed", "Restriction", None,
                                            [("turns", "BOTH", "FIELD", "closed")])
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Minutes", ["Closed"], "Minutes")
        na.BuildNetwork(self.network)
        # W to (20,15) on EN: by E is 10 + 10 + 5, but that completes the three-edge turn; by N 10 + 10 + 15.
        self.assertEqual(self.route((0, 10), (20, 15)), 3.5)
        # From the middle of CE the same two last edges are no turn: its first edge was never travelled.
        self.assertEqual(self.route((15, 10), (20, 15)), 1.0)
        # Global turns: a left costs a minute, a right 30 seconds; a U-turn ten minutes.
        self.minutes(("GLOBAL_TURNS", "BOTH", "TURN_CATEGORY", {"left": 60, "right": 30, "reverse": 600}))
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Minutes", [], "Minutes")
        na.BuildNetwork(self.network)
        self.assertEqual(self.route((5, 10), (10, 15)), 2.0)                  # 0.5 + 0.5 and a left
        self.assertEqual(self.route((15, 10), (10, 15)), 1.5)                 # a right from CE into CN
        self.assertEqual(self.route((5, 10), (15, 10)), 1.0)                  # straight on: no delay
        from grapy import _network
        graph = _network.Graph(self.network)
        self.assertEqual(_turns.category(_turns.rotation(graph, (0, True), (2, True))), "left")       # WC into CN
        self.assertEqual(_turns.category(_turns.rotation(graph, (1, False), (2, True))), "right")     # CE back into CN
        self.assertEqual(_turns.category(_turns.rotation(graph, (0, True), (1, True))), "straight")
        with self.assertRaises(arcpy.ExecuteError):                           # a turn category gives time only
            arcpy.graticula.AddNetworkAttribute(self.network, "Metres", "Cost", "Meters",
                                                [("GLOBAL_TURNS", "BOTH", "TURN_CATEGORY", {"left": 1})])

    def test_two_edge_turn_delay_replaces_the_global_turn(self):
        self.minutes(("turns", "BOTH", "FIELD", "delay"),
                     ("GLOBAL_TURNS", "BOTH", "TURN_CATEGORY", {"left": 60}))
        self.turn(["WC", "CN"], "Y", delay=0.25)
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Minutes", [], "Minutes")
        na.BuildNetwork(self.network)
        self.assertEqual(self.route((5, 10), (10, 15)), 1.25)
        # The na tools see the same: a route layer through the turn, and a service area that reaches less.
        stops = self.points("stops", [("a", "POINT(5 10)"), ("b", "POINT(10 15)")])
        layer = na.MakeRouteAnalysisLayer(self.network, "trip", "Driving", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", stops)
        na.Solve(layer)
        (total,), = arcpy.da.SearchCursor(na.GetNASublayer(layer, "Routes"), ["Total_Minutes"])
        self.assertAlmostEqual(total, 1.25)
        facility = self.points("facility", [("f", "POINT(5 10)")])
        area = na.MakeServiceAreaAnalysisLayer(self.network, "sa", "Driving", cutoffs=[1.0], output_type="LINES")[0]
        na.AddLocations(area, "Facilities", facility)
        na.Solve(area)
        lines = list(arcpy.da.SearchCursor(na.GetNASublayer(area, "Lines"), ["ToCumul_Minutes", "SHAPE@LENGTH"]))
        # Within a minute of (5,10): 5 m back to W, 5 to C, then 2.5 m up CN (after the quarter-minute turn), 5 on CE
        # (straight, no delay) and all 5 m of CS (a right, which costs nothing here).
        self.assertAlmostEqual(sum(length for _, length in lines), 5 + 5 + 2.5 + 5 + 5)
        self.assertTrue(all(cost <= 1.0 + 1e-9 for cost, _ in lines))


if __name__ == "__main__":
    unittest.main()
