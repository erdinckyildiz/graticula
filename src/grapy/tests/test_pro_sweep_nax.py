"""arcpy.nax as ArcGIS Pro 3.7 answers it: what the Pro sweep measured (2026-11-12) and grapy now does.

The network is three roads drawn by hand, every one a minute long, so each answer can be worked out on paper:

    D(10,10)
       │                 AB (0,0)→(10,0) and BC (10,0)→(20,0) run east,
    A ─ B ─ C            BD (10,0)→(10,10) runs north.

The case module is tools/pro-compare/sweep/cases/nax.py; the results are tools/pro-compare/sweep/results/nax.md.
"""
import datetime
import enum
import os
import sys
import shutil
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
import grapy.na as na  # noqa: E402
import grapy.nax as nax  # noqa: E402

MEASURED = "measured in ArcGIS Pro 3.7, 2026-11-12"


class ProSweepNax(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        arcpy.env.workspace = self.directory
        gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]
        fds = arcpy.management.CreateFeatureDataset(gpkg, "transport", 3857)[0]
        roads = arcpy.management.CreateFeatureclass(fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(roads, "minutes", "DOUBLE")
        with arcpy.da.InsertCursor(roads, ["SHAPE@", "minutes"]) as written:
            for wkt in ("LINESTRING(0 0,10 0)", "LINESTRING(10 0,20 0)", "LINESTRING(10 0,10 10)"):
                written.insertRow((wkt, 1.0))
        self.network = na.CreateNetworkDataset(fds, "streets", ["roads"], "NO_ELEVATION")[0]
        arcpy.graticula.AddNetworkAttribute(self.network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes")])
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Minutes", [], "Minutes", "Length")
        na.BuildNetwork(self.network)

    def solver(self, kind):
        solver = getattr(nax, kind)(self.network)
        solver.travelMode = "Driving"
        return solver

    @staticmethod
    def insert(solver, input_type, rows, fields=("Name",)):
        with solver.insertCursor(input_type, [*fields, "SHAPE@XY"]) as cursor:
            for row in rows:
                cursor.insertRow(row)

    # ---- enumerations

    def test_enumerations_are_pro_s_int_enums(self):
        # Every nax enumeration is an IntEnum with Pro's members and values (MEASURED); they held their names as text.
        self.assertTrue(issubclass(nax.DistanceUnits, enum.IntEnum))
        self.assertEqual((nax.DistanceUnits.Meters, nax.DistanceUnits.Kilometers, nax.TimeUnits.Minutes), (9, 10, 21))
        self.assertEqual([(m.name, m.value) for m in nax.CurbApproach],
                         [("Unknown", -1), ("EitherSide", 0), ("RightSide", 1), ("LeftSide", 2), ("NoUTurn", 3)])
        self.assertEqual([m.name for m in nax.ClosestFacilityOutputDataType],
                         ["Incidents", "Facilities", "Routes", "ClosestFacilities", "DirectionPoints",
                          "DirectionLines", "Directions"])
        self.assertEqual([m.name for m in nax.VehicleRoutingProblemOutputDataType],
                         ["UnassignedStops", "Stops", "Routes", "Directions"])
        self.assertEqual((nax.WasteCollectionInputDataType.Stops, nax.LastMileDeliveryOutputDataType.Orders), (400, 250))
        self.assertEqual((nax.AttributeUsage.Restriction, nax.AttributeUsage.Hierarchy), (2, 3))
        self.assertEqual([m.name for m in nax.NetworkTimeUsage], ["BeforeTraversal", "AfterTraversal"])
        self.assertEqual((nax.TraversedElementType.Junction, nax.TraversedElementType.Edge,
                          nax.TraversedElementType.Turn, nax.TraversedElementType.Stop), (65536, 131072, 262144, 65636))
        self.assertEqual([m.name for m in nax.MessageSeverity], ["All", "Warning", "Error", "Info"])
        self.assertEqual([(m.name, m.value) for m in nax.DrivingSide], [("Unknown", 0), ("RightSide", 1), ("LeftSide", 2)])

    # ---- the network dataset

    def test_edge_count_counts_both_directions(self):
        # Three roads, three edges in grapy's tables: Pro's edgeCount is 6, one for each direction (MEASURED).
        network = nax.NetworkDataset(self.network)
        self.assertEqual((network.edgeCount, network.junctionCount), (6, 4))
        self.assertEqual(len(list(network.edges(["EID"]))), network.edgeCount)

    def test_a_point_on_the_line_is_on_its_left(self):
        # SideOfEdge 1 is right of the digitised direction, 2 left; a point on the line is 2 (MEASURED).
        points = arcpy.management.CreateFeatureclass(self.directory, "p.gpkg", "POINT", spatial_reference=3857)[0]
        with arcpy.da.InsertCursor(points, ["SHAPE@XY"]) as cursor:
            for xy in ((5, -1), (5, 1), (5, 0)):
                cursor.insertRow((xy,))
        na.CalculateLocations(points, self.network, "5 Meters")
        with arcpy.da.SearchCursor(points, ["SideOfEdge"]) as rows:
            self.assertEqual([r[0] for r in rows], [1, 2, 2])

    # ---- inputs

    def test_field_names_and_field_mappings(self):
        route = self.solver("Route")
        stops = ["Name", "RouteName", "Sequence", "TimeWindowStart", "TimeWindowEnd", "LocationType", "CurbApproach",
                 "AdditionalTime", "AdditionalDistance", "AdditionalCost", "Bearing", "BearingTol", "NavLatency"]
        self.assertEqual(route.fieldNames(nax.RouteInputDataType.Stops), stops)       # no ObjectID or Shape
        with_locations = route.fieldNames(nax.RouteInputDataType.Stops, True)
        self.assertEqual(with_locations[5:11], ["LocationType", "SourceID", "SourceOID", "PosAlong", "SideOfEdge",
                                                "CurbApproach"])
        # Every property maps to the field of its own name, with Pro's defaults.
        mappings = route.fieldMappings(nax.RouteInputDataType.Stops)
        self.assertEqual([(k, m.mappedFieldName, m.defaultValue) for k, m in mappings.items()
                          if k in ("Name", "CurbApproach", "BearingTol", "AdditionalCost")],
                         [("Name", "Name", None), ("CurbApproach", "CurbApproach", 0), ("AdditionalCost", "AdditionalCost", 0.0),
                          ("BearingTol", "BearingTol", 30.0)])
        self.assertEqual(self.solver("ServiceArea").defaultImpedanceCutoffs, [5.0, 10.0, 15.0])

    def test_load_takes_the_default_where_the_field_is_missing(self):
        # A property mapped to a field the input does not hold takes the map's defaultValue.
        incidents = arcpy.management.CreateFeatureclass(self.directory, "i.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(incidents, "Name", "TEXT")
        with arcpy.da.InsertCursor(incidents, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(("call", (0, 0)))
        closest = self.solver("ClosestFacility")
        self.insert(closest, nax.ClosestFacilityInputDataType.Facilities, [("near", (10, 5)), ("far", (20, 0))])
        mappings = closest.fieldMappings(nax.ClosestFacilityInputDataType.Incidents)
        mappings["TargetFacilityCount"].defaultValue = 2
        closest.load(nax.ClosestFacilityInputDataType.Incidents, incidents, mappings)
        result = closest.solve()
        # (0,0) → B 1 min → (10,5) 0.5 or → C 1: both facilities, ranked. The incident was loaded: IncidentOID is its
        # feature's ObjectID; FacilityID is the facility's own ID property (None), OutputFacilityOID its row's.
        rows = list(result.searchCursor(nax.ClosestFacilityOutputDataType.Routes,
                                        ["Name", "FacilityRank", "Total_Minutes", "IncidentOID", "FacilityID",
                                         "OutputFacilityOID"]))
        self.assertEqual(rows, [("call - near", 1, 1.5, 1, None, 1), ("call - far", 2, 2.0, 1, None, 2)])

    # ---- outputs

    def test_route_outputs_carry_pro_s_fields(self):
        route = self.solver("Route")
        # a: right of AB as it is drawn; b: left of BC. The route runs east along both.
        self.insert(route, nax.RouteInputDataType.Stops, [("a", (5, -1)), ("b", (15, 1))])
        result = route.solve()
        self.assertEqual(result.fieldNames(nax.RouteOutputDataType.Routes),
                         ["ObjectID", "Name", "FirstStopID", "LastStopID", "StopCount", "StartTime", "EndTime",
                          "StartTimeUTC", "EndTimeUTC", "Total_Miles", "Total_Kilometers", "Total_Minutes",
                          "TotalWait_Minutes", "TotalViolation_Minutes", "FirstStopOID", "LastStopOID"])
        with result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes", "FirstStopOID", "StartTime"]) as rows:
            self.assertEqual(list(rows), [(1.0, None, None)])          # 0.5 + 0.5; inserted, not loaded; no time
        fields = ["Name", "SourceOID", "PosAlong", "SideOfEdge", "SnapX", "SnapY", "SnapZ",
                  "DistanceToNetworkInMeters", "CurbApproach", "LocationType", "AdditionalTime", "ArriveCurbApproach",
                  "DepartCurbApproach", "ORIG_FID", "Cumul_Minutes"]
        stops = list(result.searchCursor(nax.RouteOutputDataType.Stops, fields))
        # Departing a along AB, a (right of the line) is on the vehicle's right: 1. Arriving at b along BC, b is on the
        # left: 2.
        self.assertEqual(stops, [("a", 1, 0.5, 1, 5.0, 0.0, 0.0, 1.0, 0, 0, 0.0, None, 1, None, 0.0),
                                 ("b", 2, 0.5, 2, 15.0, 0.0, 0.0, 1.0, 0, 0, 0.0, 2, None, None, 1.0)])
        self.assertNotIn("AdditionalCost", result.fieldNames(nax.RouteOutputDataType.Stops))

    def test_od_lines_and_their_counts(self):
        od = self.solver("OriginDestinationCostMatrix")
        self.insert(od, nax.OriginDestinationCostMatrixInputDataType.Origins, [("o", (5, -1))])
        self.insert(od, nax.OriginDestinationCostMatrixInputDataType.Destinations, [("d", (10, 5)), ("e", (20, 0))])
        result = od.solve()
        T = nax.OriginDestinationCostMatrixOutputDataType
        self.assertEqual(result.fieldNames(T.Lines),
                         ["ObjectID", "DestinationRank", "Total_Time", "Total_Distance", "OriginID", "OriginOID",
                          "OriginName", "DestinationID", "DestinationOID", "DestinationName"])
        lines = list(result.searchCursor(T.Lines, ["OriginName", "DestinationName", "DestinationRank", "Total_Time"]))
        self.assertEqual(lines, [("o", "d", 1, 1.0), ("o", "e", 2, 1.5)])
        self.assertEqual(list(result.searchCursor(T.Origins, ["DestinationCount", "OriginOID"])), [(2, None)])
        self.assertEqual(list(result.searchCursor(T.Destinations, ["OriginCount"])), [(1,), (1,)])

    def test_service_area_cutoffs_are_in_the_solver_s_units(self):
        area = self.solver("ServiceArea")
        area.timeUnits = nax.TimeUnits.Seconds
        area.defaultImpedanceCutoffs = [90]                 # 1.5 minutes: A to B, then half of BC and of BD
        area.outputType = nax.ServiceAreaOutputType.PolygonsAndLines
        area.polygonBufferDistance = 1
        self.insert(area, nax.ServiceAreaInputDataType.Facilities, [("F", (0, 0))])
        result = area.solve()
        self.assertEqual(list(result.searchCursor(nax.ServiceAreaOutputDataType.Polygons,
                                                  ["Name", "FromBreak", "ToBreak", "Name_1"])),
                         [("F : 0 - 90", 0.0, 90.0, "F")])
        lines = list(result.searchCursor(nax.ServiceAreaOutputDataType.Lines,
                                         ["SourceName", "ToCumul_Minutes", "ToCumul_Seconds", "SHAPE@LENGTH"]))
        self.assertAlmostEqual(sum(r[3] for r in lines), 20.0)
        self.assertAlmostEqual(max(r[1] for r in lines), 1.5)
        self.assertAlmostEqual(max(r[2] for r in lines), 90.0)
        self.assertEqual({r[0] for r in lines}, {"roads"})

    def test_location_allocation_facility_totals_are_the_impedance_s(self):
        la = self.solver("LocationAllocation")
        self.insert(la, nax.LocationAllocationInputDataType.Facilities, [("A", (0, 0)), ("B", (10, 0))])
        self.insert(la, nax.LocationAllocationInputDataType.DemandPoints, [("d", 2.0, (20, 0))], ("Name", "Weight"))
        result = la.solve()
        T = nax.LocationAllocationOutputDataType
        names = result.fieldNames(T.Facilities)
        self.assertIn("Total_Minutes", names)
        self.assertNotIn("Total_Kilometers", names)                 # Pro writes the impedance's only
        self.assertEqual(list(result.searchCursor(T.Facilities, ["Name", "FacilityType", "Total_Minutes",
                                                                 "TotalWeighted_Minutes"])),
                         [("A", 0, 0.0, 0.0), ("B", 3, 1.0, 2.0)])
        self.assertNotIn("TotalWeighted_Kilometers", result.fieldNames(T.Lines))
        self.assertIn("Total_Kilometers", result.fieldNames(T.Lines))

    def test_a_time_of_day_is_exported(self):
        # A waste collection route's EndTime is a time of day; export wrote nothing a GeoPackage could hold.
        result = nax._Result({nax.WasteCollectionOutputDataType.Routes: [
            {"ObjectID": 1, "Name": "r", "EndTime": datetime.time(8, 3, 36), "SHAPE": None}]}, 3857, True)
        out = os.path.join(self.directory, "out.gpkg", "routes")
        result.export(nax.WasteCollectionOutputDataType.Routes, out)
        with arcpy.da.SearchCursor(out, ["Name", "EndTime"]) as rows:
            self.assertEqual(list(rows), [("r", "08:03:36")])

    def test_vehicle_routing_schema_one_routes(self):
        # Schema version One's Routes hold the solve's fields, not the route's inputs; no violation is 0; a network
        # with no time zone writes its UTC times as its local ones; the start depot's FromPrevDistance is 0.
        vrp = self.solver("VehicleRoutingProblem")
        T = nax.VehicleRoutingProblemInputDataType
        self.insert(vrp, T.Depots, [("depot", (10, 0))])
        self.insert(vrp, T.Orders, [("o", 1.0, (20, 0))], ("Name", "ServiceTime"))
        with vrp.insertCursor(T.Routes, ["Name", "StartDepotName", "EndDepotName"]) as cursor:
            cursor.insertRow(("truck", "depot", "depot"))
        result = vrp.solve()
        U = nax.VehicleRoutingProblemOutputDataType
        names = result.fieldNames(U.Routes)
        self.assertNotIn("StartDepotName", names)
        (row,) = result.searchCursor(U.Routes, ["ViolatedConstraints", "StartTime", "StartTimeUTC", "EndTime",
                                                "EndTimeUTC", "TotalTime"])
        self.assertEqual(row[0], 0)
        self.assertEqual((row[2], row[4]), (row[1], row[3]))
        self.assertAlmostEqual(row[5], 3.0)                              # 1 there, 1 service, 1 back
        depot = [r for r in result.searchCursor(U.Stops, ["StopType", "Sequence", "FromPrevDistance"]) if r[1] == 1]
        self.assertEqual(depot, [(1, 1, 0.0)])
        self.assertEqual(vrp.fieldNames(T.Breaks)[0], "RouteName")       # Pro's order, no Name

    def test_waste_collection_times_to_the_second(self):
        self.assertEqual(nax._second(datetime.time(8, 3, 35, 999999)), datetime.time(8, 3, 36))
        self.assertEqual(nax._second(datetime.time(8, 3, 35, 400000)), datetime.time(8, 3, 35))

    # ---- custom evaluators

    def test_custom_evaluator_objects(self):
        seen = {}

        class Doubled(nax.AttributeEvaluator):
            def attach(self, network_query):
                seen["during_attach"] = self.attribute                 # None: Pro sets it after attach
                attribute = network_query.attribute("Minutes")
                seen["attribute"] = (str(attribute), int(attribute), str(network_query.attribute("Length")))
                seen["query"] = network_query
                return True

            def edgeValue(self, edge):
                if "edge" not in seen:
                    q = seen["query"]
                    junction = q.fromJunction(edge)
                    seen["edge"] = (repr(edge), edge.isAlong, edge.isAgainst, edge.opposite.isAgainst,
                                    {type(e).__name__ for e in q.edges(junction)})
                return 2.0

        network = nax.NetworkDataset(self.network)
        network.customEvaluators = [Doubled("Minutes")]
        route = nax.Route(network)
        route.travelMode = "Driving"
        self.insert(route, nax.RouteInputDataType.Stops, [("a", (0, 0)), ("b", (20, 0))])
        (minutes,), = route.solve().searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes"])
        self.assertEqual(minutes, 4.0)
        self.assertIsNone(seen["during_attach"])
        self.assertEqual(seen["attribute"], ("2", 2, "1"))              # Length is 1, Minutes 2: from 1, as printed
        self.assertEqual(seen["edge"], ("1", True, False, True, {"Edge"}))
        self.assertFalse(issubclass(nax.AdjacentEdge, nax.NetworkElement))
        self.assertFalse(issubclass(nax.TraversedTurn, nax.TraversedElement))
        self.assertTrue(issubclass(nax.TraversedEdge, nax.TraversedElement))


if __name__ == "__main__":
    unittest.main()
