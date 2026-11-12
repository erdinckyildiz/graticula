"""arcpy.na as ArcGIS Pro 3.7 answers it: the differences the Pro sweep found, each pinned by a hand-workable example
(measured in ArcGIS Pro 3.7, 2026-11-12, tools/pro-compare/sweep/cases/na.py).

The network: two roads on Web Mercator at the equator, A(0,0) → B(100,0) east and B → C(100,100) north. There a metre
east–west is a metre on the ellipsoid and a metre north–south is 1 − e² of one, so Length, which Pro measures on the
ellipsoid, is 100 for AB and 100·(1 − e²) for BC.
"""
import datetime
import json
import os
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.na as na  # noqa: E402

NS = 1 - 0.00669437999014         # 1 − e² of WGS 1984


class ProSweepNa(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "t", 3857)[0]
        self.roads = arcpy.management.CreateFeatureclass(self.fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(self.roads, "minutes", "DOUBLE")
        arcpy.management.AddField(self.roads, "alt", "LONG")
        with arcpy.da.InsertCursor(self.roads, ["SHAPE@", "minutes", "alt"]) as written:
            written.insertRow(("LINESTRING(0 0,100 0)", 1.0, 11))
            written.insertRow(("LINESTRING(100 0,100 100)", 2.0, 12))
        self.network = na.CreateNetworkDataset(self.fds, "net", ["roads"], "NO_ELEVATION")[0]
        na.BuildNetwork(self.network)

    def points(self, name, rows):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(path, "Name", "TEXT")
        with arcpy.da.InsertCursor(path, ["Name", "SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def timed(self):
        arcpy.graticula.AddNetworkAttribute(self.network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes")])
        modes = os.path.join(self.directory, "modes.json")
        with open(modes, "w", encoding="utf-8") as written:
            json.dump({"Driving": {"name": "Driving", "impedanceAttributeName": "Minutes",
                                   "timeAttributeName": "Minutes", "distanceAttributeName": "Length"}}, written)
        na.ReplaceNetworkTravelModes(modes, self.network)
        na.BuildNetwork(self.network)

    # ---- the network dataset

    def test_length_is_measured_on_the_ellipsoid_and_no_travel_mode_is_made(self):
        # Pro: a Length cost in metres, no travel mode; a solve with none minimises Length.
        described = arcpy.Describe(self.network)
        self.assertEqual([(a.name, a.usageType, a.units) for a in described.attributes], [("Length", "Cost", "Meters")])
        self.assertEqual(na.GetTravelModes(self.network), {})
        self.assertEqual(described.defaultTravelModeName, "")
        layer = na.MakeRouteAnalysisLayer(self.network, "r", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", self.points("s", [("a", "POINT(0 0)"), ("c", "POINT(100 100)")]))
        na.Solve(layer)
        (total,), = self.rows(na.GetNASublayer(layer, "Routes"), ["Total_Length"])
        self.assertAlmostEqual(total, 100 + 100 * NS, places=6)       # 199.33, not the planar 200

    def test_a_utm_grid_length_is_undone(self):
        # Pro's route of 410 m on the UTM 35N grid near its central meridian cost 410.164 m: 410 / 0.9996.
        fds = arcpy.management.CreateFeatureDataset(self.gpkg, "utm", 32635)[0]
        roads = arcpy.management.CreateFeatureclass(fds, "utm_roads", "POLYLINE")[0]
        with arcpy.da.InsertCursor(roads, ["SHAPE@"]) as written:
            written.insertRow(("LINESTRING(500000 4500000,500000 4500410)",))
        network = na.CreateNetworkDataset(fds, "utm_net", ["utm_roads"], "NO_ELEVATION")[0]
        na.BuildNetwork(network)
        stops = arcpy.management.CreateFeatureclass(self.directory, "utm_stops.gpkg", "POINT", spatial_reference=32635)[0]
        with arcpy.da.InsertCursor(stops, ["SHAPE@"]) as written:
            written.insertRow(("POINT(500000 4500000)",))
            written.insertRow(("POINT(500000 4500410)",))
        layer = na.MakeRouteAnalysisLayer(network, "u", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", stops)
        na.Solve(layer)
        (total,), = self.rows(na.GetNASublayer(layer, "Routes"), ["Total_Length"])
        self.assertAlmostEqual(total, 410 / 0.9996, delta=0.01)

    def test_replace_travel_modes_wants_a_time_attribute_on_a_timed_network(self):
        arcpy.graticula.AddNetworkAttribute(self.network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes")])
        modes = os.path.join(self.directory, "modes.json")
        with open(modes, "w", encoding="utf-8") as written:
            json.dump({"Walk": {"name": "Walk", "impedanceAttributeName": "Length"}}, written)
        with self.assertRaisesRegex(arcpy.ExecuteError, "030403.*Walk"):
            na.ReplaceNetworkTravelModes(modes, self.network)

    def test_upgrade_refuses_a_current_network(self):
        with self.assertRaisesRegex(arcpy.ExecuteError, "030087"):
            na.UpgradeNetwork(self.network)

    def test_a_travel_mode_writes_the_rest_words(self):
        mode = na.TravelMode({"name": "Walk", "impedanceAttributeName": "Length", "uturnAtJunctions": "esriNFSBNoBacktrack"})
        self.assertEqual(mode.uTurns, "NO_UTURNS")
        written = json.loads(str(mode))
        self.assertEqual((written["uturnAtJunctions"], written["simplificationToleranceUnits"]),
                         ("esriNFSBNoBacktrack", "esriMeters"))
        self.assertNotIn("uTurns", written)

    # ---- analysis layers

    def test_sublayer_classes_and_fields(self):
        layer = na.MakeRouteAnalysisLayer(self.network, "r", generate_directions_on_solve="NO_DIRECTIONS")[0]
        self.assertEqual(na.GetNAClassNames(layer), {"Stops": "Stops", "Routes": "Routes", "Barriers": "Point Barriers",
                                                     "PolylineBarriers": "Line Barriers",
                                                     "PolygonBarriers": "Polygon Barriers"})
        stops = [f.name for f in arcpy.ListFields(na.GetNASublayer(layer, "Stops"))][1:-1]
        self.assertEqual(stops, ["Name", "RouteName", "Sequence", "TimeWindowStart", "TimeWindowEnd",
                                 "ArriveCurbApproach", "DepartCurbApproach", "ArriveTime", "DepartTime", "ArriveTimeUTC",
                                 "DepartTimeUTC", "LocationType", "SourceID", "SourceOID", "PosAlong", "SideOfEdge",
                                 "CurbApproach", "Status", "SnapX", "SnapY", "SnapZ", "DistanceToNetworkInMeters",
                                 "Attr_Length", "Cumul_Length"])
        routes = [f.name for f in arcpy.ListFields(na.GetNASublayer(layer, "Routes"))][1:-1]
        self.assertEqual(routes, ["Name", "FirstStopID", "LastStopID", "StopCount", "StartTime", "EndTime",
                                  "StartTimeUTC", "EndTimeUTC", "Total_Length"])
        mappings = na.NAClassFieldMappings(layer, "Stops")
        self.assertEqual(sorted(mappings), ["Attr_Length", "CurbApproach", "LocationType", "Name", "RouteName",
                                            "Sequence", "TimeWindowEnd", "TimeWindowStart"])

    def test_a_timed_mode_totals_its_time_and_distance(self):
        self.timed()
        layer = na.MakeRouteAnalysisLayer(self.network, "r", "Driving", generate_directions_on_solve="NO_DIRECTIONS")[0]
        fields = [f.name for f in arcpy.ListFields(na.GetNASublayer(layer, "Routes"))]
        self.assertEqual(fields[-5:-1], ["TotalWait_Minutes", "TotalViolation_Minutes", "Total_Length", "Total_Minutes"])
        na.AddLocations(layer, "Stops", self.points("s", [("a", "POINT(0 0)"), ("c", "POINT(100 100)")]))
        na.Solve(layer)
        (length, minutes), = self.rows(na.GetNASublayer(layer, "Routes"), ["Total_Length", "Total_Minutes"])
        self.assertAlmostEqual(minutes, 3.0)
        self.assertAlmostEqual(length, 100 + 100 * NS, places=6)
        cumul = self.rows(na.GetNASublayer(layer, "Stops"), ["Cumul_Minutes"])
        self.assertEqual([round(c[0], 9) for c in cumul], [0.0, 3.0])

    def test_add_locations_writes_pro_s_defaults(self):
        layer = na.MakeRouteAnalysisLayer(self.network, "r", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", self.points("s", [("a", "POINT(10 1)"), ("b", "POINT(90 -1)")]))
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Stops"),
                                   ["Sequence", "CurbApproach", "LocationType", "SnapZ", "Attr_Length", "Status"]),
                         [(1, 0, 0, 0.0, 0.0, 0), (2, 0, 0, 0.0, 0.0, 0)])
        la = na.MakeLocationAllocationAnalysisLayer(self.network, "la")[0]
        na.AddLocations(la, "Facilities", self.points("f", [("f", "POINT(0 0)")]))
        na.AddLocations(la, "Demand Points", self.points("d", [("d", "POINT(100 0)")]))
        self.assertEqual(self.rows(na.GetNASublayer(la, "Facilities"),
                                   ["Weight", "FacilityType", "DemandCount", "DemandWeight"]), [(1.0, 0, 0, 0.0)])
        self.assertEqual(self.rows(na.GetNASublayer(la, "DemandPoints"), ["Weight"]), [(1.0,)])

    def test_service_area_names_whole_cutoffs_without_decimals(self):
        layer = na.MakeServiceAreaAnalysisLayer(self.network, "sa", None, "FROM_FACILITIES", [50, 120])[0]
        na.AddLocations(layer, "Facilities", self.points("f", [("f1", "POINT(0 0)")]))
        na.Solve(layer)
        names = sorted(r[0] for r in self.rows(na.GetNASublayer(layer, "SAPolygons"), ["Name"]))
        self.assertEqual(names, ["f1 : 0 - 50", "f1 : 50 - 120"])

    def test_solver_properties_defaults(self):
        route = na.GetSolverProperties(na.MakeRouteAnalysisLayer(self.network, "r",
                                                                 generate_directions_on_solve="NO_DIRECTIONS")[0])
        route.findBestSequence = True
        self.assertEqual((route.orderingType, route.useTimeWindows), ("PRESERVE_NONE", "NO_TIMEWINDOWS"))
        cf = na.GetSolverProperties(na.MakeClosestFacilityAnalysisLayer(self.network, "cf")[0])
        self.assertEqual(cf.timeOfDayUsage, "NOT_USED")
        sa = na.GetSolverProperties(na.MakeServiceAreaAnalysisLayer(self.network, "sa")[0])
        self.assertEqual(sa.polygonType, "SIMPLE_POLYS")

    # ---- fleet routing

    def test_vehicle_routing_routes_default_to_times_alone(self):
        self.timed()
        layer = na.MakeVehicleRoutingProblemAnalysisLayer(self.network, "v", "Driving", default_date="2026-01-05",
                                                          generate_directions_on_solve="NO_DIRECTIONS")[0]
        with self.assertRaisesRegex(arcpy.ExecuteError, "030241"):
            na.AddVehicleRoutingProblemRoutes(layer, 1, "Truck")
        na.AddVehicleRoutingProblemRoutes(layer, 1, "Truck", "d", "d")
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Routes"),
                                   ["EarliestStartTime", "LatestStartTime", "AssignmentRule"]),
                         [(datetime.datetime(1899, 12, 30, 8), datetime.datetime(1899, 12, 30, 10), 1)])

    def test_waste_collection_wants_a_collection_mode(self):
        self.timed()
        layer = na.MakeWasteCollectionAnalysisLayer(self.network, "w", "Driving", route_start_time="08:00",
                                                    max_route_total_time=480)[0]
        na.AddLocations(layer, "Stops", self.points("s", [("s1", "POINT(50 1)")]))
        na.AddLocations(layer, "Depots", self.points("d", [("d1", "POINT(0 0)")]))
        na.AddVehicleRoutingProblemRoutes(layer, 1, "Bin", "d1", "d1")
        with self.assertRaisesRegex(arcpy.ExecuteError, "CollectionMode field value(.|\n)*030092"):
            na.Solve(layer)

    def test_a_stop_taken_off_its_route_reports_no_violation(self):
        self.timed()
        layer = na.MakeWasteCollectionAnalysisLayer(self.network, "w", "Driving", route_start_time="08:00",
                                                    max_route_total_time=480, stop_collection_mode="BOTH_SIDES")[0]
        na.AddLocations(layer, "Stops", self.points("s", [("s1", "POINT(50 1)"), ("s2", "POINT(100 50)")]))
        na.AddLocations(layer, "Depots", self.points("d", [("d1", "POINT(0 0)")]))
        na.AddVehicleRoutingProblemRoutes(layer, 1, "Bin", "d1", "d1")
        na.Solve(layer)
        stops = na.GetNASublayer(layer, "Stops")
        arcpy.management.SelectLayerByAttribute(stops, "NEW_SELECTION", "Name = 's1'")
        na.UpdateFleetRoutingAssignments(layer, "NOT_ASSIGNED")
        self.assertEqual(sorted(self.rows(stops, ["Name", "RouteName", "ViolatedConstraint_1"])),
                         [("s1", None, None), ("s2", "Bin1", None)])

    # ---- turns

    def test_turn_table_from_an_arcview_dbf(self):
        # Pro takes a .dbf turn table with NODE_ (grapy read only GeoPackage tables), refuses one without NODE_, and
        # draws each turn from three quarters along its first edge to a quarter along its second. AB ends at B, where
        # BC starts.
        def dbf(path, fields, values):
            record = 1 + sum(f[2] for f in fields)
            data = bytearray(struct.pack("<BBBBIHH20x", 3, 126, 11, 12, 1, 32 + 32 * len(fields) + 1, record))
            for name, kind, length, decimals in fields:
                data += struct.pack("<11sc4xBB14x", name, kind, length, decimals)
            data += b"\r" + b" " + b"".join(v for v in values) + b"\x1a"
            with open(path, "wb") as written:
                written.write(bytes(data))
            return path

        table = dbf(os.path.join(self.directory, "turns.dbf"),
                    [(b"NODE_", b"N", 10, 0), (b"ARC1_", b"N", 10, 0), (b"ARC2_", b"N", 10, 0), (b"MINUTES", b"N", 8, 2)],
                    [b"7".rjust(10), b"1".rjust(10), b"2".rjust(10), b"0.50".rjust(8)])
        out = na.TurnTableToTurnFeatureClass(table, self.roads, "turns")[0]
        self.assertEqual(self.rows(out, ["Edge1FID", "Edge1End", "Edge1Pos", "Edge2FID", "Edge2Pos", "MINUTES",
                                         "SHAPE@WKT"]),
                         [(1, "Y", 0.75, 2, 0.25, 0.5, "MULTILINESTRING ((75 0, 100 0, 100 25))")])
        bare = dbf(os.path.join(self.directory, "bare.dbf"), [(b"F_EDGE", b"N", 10, 0), (b"T_EDGE", b"N", 10, 0)],
                   [b"1".rjust(10), b"2".rjust(10)])
        with self.assertRaisesRegex(arcpy.ExecuteError, "000591"):
            na.TurnTableToTurnFeatureClass(bare, self.roads, "bare_turns")

if __name__ == "__main__":
    unittest.main()
