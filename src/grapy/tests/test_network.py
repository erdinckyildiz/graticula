"""Network datasets and their solvers on a hand-drawn network with unique shortest paths (ADR-226).

    D(10,10) ── (15,15) ── E(20,10)        DE runs up to (15,15): 2·√50 ≈ 14.142 long
       │                      │
    (10,5) ──────────────── (20,5)         F: (0,5)–(10,5)–(20,5), a vertex where it crosses BD and CE
       │                      │
    A(0,0) ── B(10,0) ──── C(20,0)         every other road is 10 long

BD and CE carry a vertex at y = 5, so with any-vertex connectivity F joins them there; with end-point connectivity only
F's own ends are junctions and it joins nothing.
"""
import math
import os
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.na as na  # noqa: E402
import grapy.nax as nax  # noqa: E402
from grapy import _network  # noqa: E402

DE = 2 * math.sqrt(50)


class Network(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "transport", 3857)[0]
        self.roads = arcpy.management.CreateFeatureclass(self.fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(self.roads, "minutes", "DOUBLE")
        arcpy.management.AddField(self.roads, "closed", "SHORT")
        rows = [("LINESTRING(0 0,10 0)", 1, 0), ("LINESTRING(10 0,20 0)", 1, 0), ("LINESTRING(10 0,10 5,10 10)", 1, 0),
                ("LINESTRING(10 10,15 15,20 10)", 1, 0), ("LINESTRING(20 0,20 5,20 10)", 1, 0),
                ("LINESTRING(0 5,10 5,20 5)", 4, 0)]
        with arcpy.da.InsertCursor(self.roads, ["SHAPE@", "minutes", "closed"]) as written:
            for row in rows:
                written.insertRow(row)
        self.points = self.make_points("stops", [("start", "POINT(2 0)"), ("end", "POINT(20 4)")])
        self.network = na.CreateNetworkDataset(self.fds, "streets", ["roads"], "NO_ELEVATION")[0]

    def make_points(self, name, rows):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(path, "Name", "TEXT")
        with arcpy.da.InsertCursor(path, ["Name", "SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def counts(self):
        connection = sqlite3.connect(self.gpkg)
        try:
            return (connection.execute("select count(*) from grapy_network_edge").fetchone()[0],
                    connection.execute("select count(*) from grapy_network_junction").fetchone()[0])
        finally:
            connection.close()

    # ---- building

    def test_end_point_and_any_vertex_connectivity(self):
        na.BuildNetwork(self.network)
        # End points: A B C D E and F's two ends; six roads, six edges.
        self.assertEqual(self.counts(), (6, 7))
        _network.set_connectivity(self.network, "roads", "ANY_VERTEX")
        with self.assertRaises(arcpy.ExecuteError):
            _network.Graph(self.network)                       # changed since its last build
        na.BuildNetwork(self.network)
        # (10,5) and (20,5) join F to BD and CE: BD, CE and F each split in two.
        self.assertEqual(self.counts(), (9, 8))

    # ---- solving with the na tools

    def test_route_follows_the_unique_shortest_path(self):
        na.BuildNetwork(self.network)
        layer = na.MakeRouteAnalysisLayer(self.network, "trip", None, "USE_CURRENT_ORDER", None, "LOCAL_TIME_AT_LOCATIONS",
                                          "ALONG_NETWORK", None, "NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", self.points, search_tolerance="5 Meters")
        result = na.Solve(layer)
        self.assertEqual(result[1], "true")
        # (2,0) → B 8 → C 10 → (20,4) 4: 22. Through D it would be 8 + 10 + 14.14 + 6.
        routes = na.GetNASublayer(layer, "Routes")
        self.assertEqual(self.rows(routes, ["Name", "StopCount", "FirstStopID", "LastStopID", "Total_Length", "SHAPE@WKT"]),
                         [("start - end", 2, 1, 2, 22.0, "LINESTRING (2 0, 10 0, 20 0, 20 4)")])
        stops = na.GetNASublayer(layer, "Stops")
        self.assertEqual(self.rows(stops, ["Name", "Sequence", "Cumul_Length", "Status", "SourceOID", "SnapX", "SnapY"]),
                         [("start", 1, 0.0, 0, 1, 2.0, 0.0), ("end", 2, 22.0, 0, 5, 20.0, 4.0)])
        self.assertIn("Solve is an equivalent", arcpy.GetMessages())
        self.assertTrue(hasattr(na.Solve, "__grapy_equivalent__"))

    def test_field_costs_restrictions_and_travel_modes(self):
        # Minutes from a field; F (4 minutes over 20 m) prorated; a restriction closing BC.
        arcpy.graticula.AddNetworkAttribute(self.network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes")])
        arcpy.graticula.AddNetworkAttribute(self.network, "Closed", "Restriction", None,
                                            [("roads", "BOTH", "FIELD", "closed")])
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Minutes", ["Closed"])
        arcpy.graticula.SetNetworkConnectivity(self.network, "roads", "ANY_VERTEX")
        with arcpy.da.UpdateCursor(self.roads, ["OID@", "closed"]) as rows:
            for row in rows:
                if row[0] == 2:
                    rows.updateRow([row[0], 1])
        na.BuildNetwork(self.network)
        self.assertEqual(list(na.GetTravelModes(self.network)), ["Length", "Driving"])
        layer = na.MakeRouteAnalysisLayer(self.network, "drive", "Driving", generate_directions_on_solve="NO_DIRECTIONS",
                                          accumulate_attributes=["Length"])[0]
        na.AddLocations(layer, "Stops", self.points)
        na.Solve(layer)
        # BC closed: (2,0) → B (0.8 min) → (10,5) (half of BD: 0.5) → F to (20,5) (half of F: 2) → (20,4) (0.1 of CE).
        got = self.rows(na.GetNASublayer(layer, "Routes"), ["Total_Minutes", "Total_Length"])[0]
        self.assertAlmostEqual(got[0], 0.8 + 0.5 + 2.0 + 0.1)
        self.assertAlmostEqual(got[1], 8 + 5 + 10 + 1)

    def test_closest_facility_od_matrix_and_service_area(self):
        na.BuildNetwork(self.network)
        facilities = self.make_points("facilities", [("near", "POINT(10 9)"), ("far", "POINT(0 0.5)")])
        incidents = self.make_points("incidents", [("call", "POINT(14 0)")])
        cf = na.MakeClosestFacilityAnalysisLayer(self.network, "cf")[0]
        na.AddLocations(cf, "Facilities", facilities)
        na.AddLocations(cf, "Incidents", incidents)
        na.Solve(cf)
        # From (14,0): to (10,9) via B 4 + 9 = 13; to (0,0) via B 4 + 10 = 14 — the first is closest.
        self.assertEqual(self.rows(na.GetNASublayer(cf, "Routes"), ["Name", "FacilityID", "FacilityRank", "IncidentID",
                                                                    "Total_Length"]),
                         [("call - near", 1, 1, 1, 13.0)])
        od = na.MakeODCostMatrixAnalysisLayer(self.network, "od")[0]
        na.AddLocations(od, "Origins", incidents)
        na.AddLocations(od, "Destinations", facilities)
        na.Solve(od)
        self.assertEqual(self.rows(na.GetNASublayer(od, "Lines"), ["Name", "OriginID", "DestinationID", "DestinationRank",
                                                                   "Total_Length"]),
                         [("call - near", 1, 1, 1, 13.0), ("call - far", 1, 2, 2, 14.0)])
        sa = na.MakeServiceAreaAnalysisLayer(self.network, "sa", None, "FROM_FACILITIES", [5, 12], output_type="POLYGONS_AND_LINES",
                                             polygon_trim_distance="1 Meters")[0]
        na.AddLocations(sa, "Facilities", incidents)
        na.Solve(sa)
        lines = self.rows(na.GetNASublayer(sa, "Lines"), ["FromCumul_Length", "ToCumul_Length", "SHAPE@LENGTH"])
        # Within 12 of (14,0): 4 west to B and 8 on to (2,0); 6 east to C and 6 up CE; 8 up BD from B.
        reached = sum(l[2] for l in lines)
        self.assertAlmostEqual(reached, 4 + 8 + 6 + 6 + 8)
        self.assertTrue(all(l[1] <= 12 + 1e-9 for l in lines))
        polygons = self.rows(na.GetNASublayer(sa, "Polygons"), ["Name", "FromBreak", "ToBreak"])
        self.assertEqual(polygons, [("call : 0.0 - 5.0", 0.0, 5.0), ("call : 5.0 - 12.0", 5.0, 12.0)])

    # ---- solving with the nax objects

    def test_nax_route_and_its_documented_totals(self):
        na.BuildNetwork(self.network)
        route = nax.Route(self.network)
        route.load(nax.RouteInputDataType.Stops, self.points)
        result = route.solve()
        self.assertTrue(result.solveSucceeded)
        # 22 metres of Length, a distance attribute: Total_Kilometers and Total_Miles, as the Route output page names them.
        (km, miles, name), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Kilometers", "Total_Miles", "Name"])
        self.assertAlmostEqual(km, 0.022)
        self.assertAlmostEqual(miles, 22 / 1609.344)
        self.assertEqual(name, "start - end")
        stops = list(result.searchCursor(nax.RouteOutputDataType.Stops, ["Name", "Sequence", "Cumul_Kilometers"]))
        self.assertEqual([s[:2] for s in stops], [("start", 1), ("end", 2)])
        self.assertAlmostEqual(stops[1][2], 0.022)
        out = os.path.join(self.directory, "route_out.gpkg")
        result.export(nax.RouteOutputDataType.Routes, out)
        self.assertEqual(self.rows(out, ["SHAPE@LENGTH"]), [(22.0,)])
        self.assertTrue(hasattr(nax.Route, "__grapy_equivalent__"))
        self.assertIn("nax.Route.solve is an equivalent", arcpy.GetMessages())

    def test_nax_closest_facility_od_and_service_area(self):
        na.BuildNetwork(self.network)
        closest = nax.ClosestFacility(self.network)
        with closest.insertCursor(nax.ClosestFacilityInputDataType.Facilities, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["near", (10, 9)])
            cursor.insertRow(["far", (0, 0.5)])
        with closest.insertCursor(nax.ClosestFacilityInputDataType.Incidents, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["call", (14, 0)])
        closest.defaultTargetFacilityCount = 2
        rows = list(closest.solve().searchCursor(nax.ClosestFacilityOutputDataType.Routes,
                                                 ["Name", "FacilityRank", "Total_Kilometers"]))
        self.assertEqual([(r[0], r[1]) for r in rows], [("call - near", 1), ("call - far", 2)])
        self.assertAlmostEqual(rows[1][2], 0.014)
        od = nax.OriginDestinationCostMatrix(self.network)
        with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Origins, ["SHAPE@XY"]) as cursor:
            cursor.insertRow([(14, 0)])
        with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Destinations, ["SHAPE@XY"]) as cursor:
            cursor.insertRow([(10, 9)])
        lines = list(od.solve().searchCursor(nax.OriginDestinationCostMatrixOutputDataType.Lines,
                                             ["OriginOID", "DestinationOID", "DestinationRank", "Total_Kilometers"]))
        self.assertEqual(lines[0][:3], (1, 1, 1))
        self.assertAlmostEqual(lines[0][3], 0.013)
        area = nax.ServiceArea(self.network)
        area.defaultImpedanceCutoffs = [12]
        area.polygonBufferDistance = 1
        with area.insertCursor(nax.ServiceAreaInputDataType.Facilities, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["call", (14, 0)])
        result = area.solve()
        self.assertEqual(list(result.searchCursor(nax.ServiceAreaOutputDataType.Polygons, ["Name", "ToBreak"])),
                         [("call : 0.0 - 12.0", 12.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            result.searchCursor(nax.ServiceAreaOutputDataType.Lines, ["FacilityID"])     # polygons were asked for

    def test_nax_location_allocation_minimize_impedance(self):
        na.BuildNetwork(self.network)

        def solve(count, required=None):
            la = nax.LocationAllocation(self.network)
            la.facilityCount = count
            with la.insertCursor(nax.LocationAllocationInputDataType.Facilities,
                                 ["Name", "FacilityType", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["A", 0, (2, 0)])
                cursor.insertRow(["B", 0, (18, 0)])
                cursor.insertRow(["C", 1 if required else 0, (10, 9)])
            with la.insertCursor(nax.LocationAllocationInputDataType.DemandPoints, ["Name", "Weight", "SHAPE@XY"]) as cursor:
                for name, weight, xy in (("d1", 1, (0, 0)), ("d2", 1, (4, 0)), ("d3", 1, (16, 0)), ("d4", 3, (20, 0))):
                    cursor.insertRow([name, weight, xy])
            result = la.solve()
            return (list(result.searchCursor(nax.LocationAllocationOutputDataType.Facilities,
                                             ["Name", "FacilityType", "DemandCount", "DemandWeight", "Total_Kilometers",
                                              "TotalWeighted_Kilometers"])),
                    list(result.searchCursor(nax.LocationAllocationOutputDataType.Lines, ["Name", "Weight"])))

        # One facility: A costs 2+2+14+3·18 = 72 m, B 18+14+2+3·2 = 40 m, C at least 9 m more than A or B to each.
        facilities, _ = solve(1)
        self.assertEqual([(f[0], f[1], f[2]) for f in facilities], [("A", 0, 0), ("B", 3, 4), ("C", 0, 0)])
        self.assertAlmostEqual(facilities[1][4], 0.036)            # 18 + 14 + 2 + 2 m
        self.assertAlmostEqual(facilities[1][5], 0.040)
        # Two: A takes d1 and d2 at 2 m each, B d3 and d4 at 2 m each — 12 m weighted, every other pair is worse.
        facilities, lines = solve(2)
        self.assertEqual([(f[0], f[1], f[2], f[3]) for f in facilities], [("A", 3, 2, 2), ("B", 3, 2, 4), ("C", 0, 0, 0)])
        self.assertAlmostEqual(facilities[1][5], 0.008)
        self.assertEqual(lines, [("A - d1", 1), ("A - d2", 1), ("B - d3", 1), ("B - d4", 3)])
        # C required: with A the total is 72 m, with B 40 m (d1 and d2 still nearer B than C's 19 and 15 m).
        facilities, _ = solve(2, required=True)
        self.assertEqual([(f[0], f[1], f[2]) for f in facilities], [("A", 0, 0), ("B", 3, 4), ("C", 1, 0)])
        # The same allocation through an analysis layer and Solve.
        facilities_fc = self.make_points("cands", [("A", "POINT(2 0)"), ("B", "POINT(18 0)"), ("C", "POINT(10 9)")])
        demand_fc = self.make_points("demand", [("d1", "POINT(0 0)"), ("d2", "POINT(4 0)"), ("d3", "POINT(16 0)"),
                                                ("d4", "POINT(20 0)")])
        arcpy.management.AddField(demand_fc, "Weight", "DOUBLE")
        with arcpy.da.UpdateCursor(demand_fc, ["Name", "Weight"]) as rows:
            for row in rows:
                rows.updateRow([row[0], 3.0 if row[0] == "d4" else 1.0])
        layer = na.MakeLocationAllocationAnalysisLayer(self.network, "la", number_of_facilities_to_find=1)[0]
        na.AddLocations(layer, "Facilities", facilities_fc)
        na.AddLocations(layer, "Demand Points", demand_fc)
        na.Solve(layer)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Facilities"), ["Name", "FacilityType", "DemandCount"]),
                         [("A", 0, 0), ("B", 3, 4), ("C", 0, 0)])
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Demand Points"), ["Name", "FacilityID"]),
                         [("d1", 2), ("d2", 2), ("d3", 2), ("d4", 2)])
        weighted = self.rows(na.GetNASublayer(layer, "Lines"), ["Name", "TotalWeighted_Length"])
        # B serves all four, 18, 14, 2 and 2 m away, d4 weighing 3.
        self.assertEqual([(n, round(v, 9)) for n, v in weighted], [("B - d1", 18.0), ("B - d2", 14.0), ("B - d3", 2.0),
                                                                   ("B - d4", 6.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            na.MakeLocationAllocationAnalysisLayer(self.network, "cover", problem_type="MAXIMIZE_COVERAGE")
        la = nax.LocationAllocation(self.network)
        la.problemType = nax.LocationAllocationProblemType.MaximizeCoverage
        with self.assertRaises(arcpy.NotBuiltError):
            la.solve()
        self.assertTrue(hasattr(nax.LocationAllocation, "__grapy_equivalent__"))

    def test_locations_layers_and_travel_mode_files(self):
        na.BuildNetwork(self.network)
        arcpy.management.AddField(self.points, "Status", "LONG")
        na.CalculateLocations(self.points, self.network, "5 Meters")
        self.assertEqual(self.rows(self.points, ["SourceID", "SourceOID", "SnapX", "SnapY", "DistanceToNetworkInMeters",
                                                 "Status"]),
                         [(1, 1, 2.0, 0.0, 0.0, 0), (1, 5, 20.0, 4.0, 0.0, 0)])
        layer = na.MakeODCostMatrixAnalysisLayer(self.network, "od")[0]
        na.AddLocations(layer, "Origins", self.points)
        copy = na.CopyNetworkAnalysisLayer(layer, "od copy")[0]
        self.assertEqual(self.rows(na.GetNASublayer(copy, "Origins"), ["Name"]), [("start",), ("end",)])
        na.DeleteNetworkAnalysisLayer(layer)
        self.assertFalse(arcpy.Exists(na.GetNASublayer(copy, "Origins").replace("od copy", "od")))
        with self.assertRaises(arcpy.ExecuteError):
            na.GetNASublayer("od", "Origins")
        modes = os.path.join(self.directory, "modes.json")
        na.ExportNetworkTravelModes(self.network, modes)
        with open(modes, encoding="utf-8") as read:
            import json
            self.assertEqual(json.load(read)["Length"]["impedanceAttributeName"], "Length")
        _network.add_attribute(self.network, "Minutes", "Cost", "Minutes", [("roads", "BOTH", "FIELD", "minutes")])
        with open(modes, "w", encoding="utf-8") as written:
            written.write('{"Driving": {"name": "Driving", "impedanceAttributeName": "Minutes"}}')
        na.ReplaceNetworkTravelModes(modes, self.network)
        self.assertEqual(list(na.GetTravelModes(self.network)), ["Driving"])
        self.assertEqual(na.UpgradeNetwork(self.network)[0], self.network)
        self.assertIs(nax.BuildNetwork, na.BuildNetwork)
        route = nax.Route(self.network)
        mappings = route.fieldMappings(nax.RouteInputDataType.Stops, False, arcpy.ListFields(self.points))
        self.assertEqual(mappings["Name"].mappedFieldName, "Name")
        self.assertIsNone(mappings["RouteName"].mappedFieldName)

    def test_describe_and_the_network_dataset_query_api(self):
        na.BuildNetwork(self.network)
        described = arcpy.Describe(self.network)
        self.assertEqual((described.dataType, described.defaultTravelModeName, described.supportsTurns,
                          described.trafficSupportType), ("NetworkDataset", "Length", False, "NONE"))
        self.assertEqual([(s.name, s.sourceID, s.sourceType) for s in described.sources],
                         [("roads", 1, "EdgeFeature"), ("streets_Junctions", 2, "SystemJunction")])
        self.assertEqual([(a.name, a.usageType, a.units) for a in described.attributes], [("Length", "Cost", "Meters")])
        network = nax.NetworkDataset(self.network)
        self.assertEqual((network.edgeCount, network.junctionCount, network.turnCount, network.isBuilt), (6, 7, 0, True))
        self.assertIsNotNone(network.buildTimestamp)
        self.assertTrue(network.getDataSourceFromSourceID(1).endswith("transport/roads"))
        # Junctions are numbered as the ends are met: AB gives 1 (0,0) and 2 (10,0); BC 3 (20,0); BD 4 (10,10) …
        with network.edges(["EID", "SOURCEOID", "DIRECTION", "FROMJUNCTION", "TOJUNCTION", "COST"], eids=[1],
                           travel_mode="Length") as edges:
            self.assertEqual(list(edges), [(1, 1, True, 1, 2, 10.0), (1, 1, False, 2, 1, 10.0)])
        self.assertEqual(list(network.edges("EID", "Length", eids=[3])), [(3, 10.0), (3, 10.0)])
        self.assertEqual(list(network.junctions(["EID", "SOURCEID"], eids=[1, 4])), [(1, 2), (4, 2)])
        with self.assertRaises(ValueError):
            network.edges("COST")                                    # a cost needs a travel mode
        with self.assertRaises(arcpy.NotBuiltError):
            network.turns()
        area = arcpy.management.CreateFeatureclass(self.directory, "area.gpkg", "POLYGON", spatial_reference=3857)[0]
        with arcpy.da.InsertCursor(area, ["SHAPE@"]) as written:
            written.insertRow(("POLYGON((0 -1,11 -1,11 1,0 1,0 -1))",))
        # The strip meets AB, BC and BD (which starts at (10,0)): three source features.
        self.assertTrue(network.checkIntersectingFeatures(area, 3))
        self.assertFalse(network.checkIntersectingFeatures(area, 2))

    def test_refused_by_name(self):
        na.BuildNetwork(self.network)
        with self.assertRaises(arcpy.NotBuiltError):
            na.MakeRouteAnalysisLayer(self.network, "best", sequence="FIND_BEST_ORDER")
        with self.assertRaises(arcpy.NotBuiltError):
            na.MakeRouteAnalysisLayer(self.network, "timed", time_of_day="1/1/1900 8:00 AM",
                                      generate_directions_on_solve="NO_DIRECTIONS")
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            na.MakeVehicleRoutingProblemAnalysisLayer(self.network)
        self.assertIn("NP-hard", str(raised.exception))
        with self.assertRaises(arcpy.NotBuiltError):
            _network.add_attribute(self.network, "Script", "Cost", "Meters", [("roads", "BOTH", "SCRIPT", "x")])
        route = nax.Route(self.network)
        route.findBestSequence = True
        with self.assertRaises(arcpy.NotBuiltError):
            route.solve()
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            nax.VehicleRoutingProblem(self.network)
        self.assertIn("NP-hard", str(raised.exception))
        for module, name, words in ((na, "GenerateDirectionsFeatures", "direction"), (na, "RouteSolverProperties", "solver"),
                                    (nax, "Edge", "custom evaluators"), (nax, "TraversedEdge", "traversed"),
                                    (nax, "DissolveNetwork", "dissolves")):
            with self.assertRaises(arcpy.NotBuiltError) as raised:
                getattr(module, name)()
            self.assertIn(words, str(raised.exception))
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            arcpy.nd.ApplyRotateTreeLayout("diagram")
        self.assertIn("pivot junctions", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
