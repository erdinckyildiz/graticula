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
        _network.add_attribute(self.network, "Minutes", "Cost", "Minutes",
                               [("roads", "BOTH", "FIELD", "minutes")])
        _network.add_attribute(self.network, "Closed", "Restriction", None, [("roads", "BOTH", "FIELD", "closed")])
        _network.add_travel_mode(self.network, "Driving", "Minutes", ["Closed"])
        _network.set_connectivity(self.network, "roads", "ANY_VERTEX")
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


if __name__ == "__main__":
    unittest.main()
