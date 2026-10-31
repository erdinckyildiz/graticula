"""Fleet routing on small networks with known optima: vehicle routing, last mile delivery, waste collection and the
travelling salesman order of a route (ADR-239).

Two networks. ``line``: one road from (0,0) to (100,0), ten minutes end to end, a depot at its middle. ``grid``: the
streets of a 3 × 3 grid of 10 m blocks, (0,0) to (20,20), a minute a block. Where an answer is not worked by hand, every
assignment and order is enumerated and the heuristic must find the cheapest — Solomon's insertion and ruin and recreate
against brute force.
"""
import datetime
import itertools
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.na as na  # noqa: E402
import grapy.nax as nax  # noqa: E402
from grapy import _fleet  # noqa: E402
from grapy._runtime import _SAID_EQUIVALENT  # noqa: E402

DAY = datetime.date(2026, 1, 5)
I2 = nax.VehicleRoutingProblemInputDataType2
O2 = nax.VehicleRoutingProblemOutputDataType2


def at(hour, minute=0):
    return datetime.datetime.combine(DAY, datetime.time(hour, minute))


class Fleet(unittest.TestCase):
    def network(self, lines):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]
        self.fds = arcpy.management.CreateFeatureDataset(gpkg, "t", 3857)[0]
        roads = arcpy.management.CreateFeatureclass(self.fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(roads, "minutes", "DOUBLE")
        import shapely
        with arcpy.da.InsertCursor(roads, ["SHAPE@", "minutes"]) as written:
            for wkt in lines:
                written.insertRow((wkt, shapely.from_wkt(wkt).length / 10))
        network = na.CreateNetworkDataset(self.fds, "net", ["roads"], "NO_ELEVATION")[0]
        arcpy.graticula.AddNetworkAttribute(network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes")])
        arcpy.graticula.AddNetworkTravelMode(network, "Driving", "Minutes", [], "Minutes", "Length")
        na.BuildNetwork(network)
        return network

    def line(self):
        return self.network(["LINESTRING(0 0,100 0)"])

    def grid(self):
        lines = [f"LINESTRING({x} {y},{x + 10} {y})" for y in (0, 10, 20) for x in (0, 10)]
        lines += [f"LINESTRING({x} {y},{x} {y + 10})" for x in (0, 10, 20) for y in (0, 10)]
        return self.network(lines)

    def vrp(self, network, orders, routes, depot=(50, 0), extra=None):
        vrp = nax.VehicleRoutingProblem(network, nax.VehicleRoutingProblemSchemaVersion.Two)
        vrp.travelMode = "Driving"
        vrp.defaultDate = DAY
        with vrp.insertCursor(I2.Depots, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["D", depot])
        fields = sorted({k for o in orders for k in o})
        with vrp.insertCursor(I2.Orders, fields) as cursor:
            for order in orders:
                cursor.insertRow([order.get(f) for f in fields])
        fields = sorted({k for r in routes for k in r})
        with vrp.insertCursor(I2.Routes, fields) as cursor:
            for route in routes:
                cursor.insertRow([route.get(f) for f in fields])
        for input_type, rows in (extra or {}).items():
            fields = sorted({k for r in rows for k in r})
            with vrp.insertCursor(input_type, fields) as cursor:
                for row in rows:
                    cursor.insertRow([row.get(f) for f in fields])
        return vrp

    def rows(self, result, output, fields):
        return list(result.searchCursor(output, fields))

    # ---- worked by hand

    def test_two_routes_on_a_line(self):
        network = self.line()
        orders = [{"Name": n, "ServiceTime": 1.0, "DeliveryQuantity_1": 1.0, "SHAPE@XY": (x, 0)}
                  for n, x in (("a", 10), ("b", 20), ("c", 80), ("e", 90))]
        routes = [{"Name": n, "StartDepotName": "D", "EndDepotName": "D", "Capacity_1": 2} for n in ("R1", "R2")]
        _SAID_EQUIVALENT.discard("nax.VehicleRoutingProblem")
        result = self.vrp(network, orders, routes).solve()
        self.assertTrue(result.solveSucceeded)
        # Each route takes the two orders on its side: out 3 minutes, on 1, back 4, two minutes of service — 10.
        got = self.rows(result, O2.Orders, ["Name", "RouteName", "Sequence", "ArriveTime", "Status"])
        self.assertEqual(got, [("a", "R1", 3, at(8, 5), 0), ("b", "R1", 2, at(8, 3), 0), ("c", "R2", 2, at(8, 3), 0),
                               ("e", "R2", 3, at(8, 5), 0)])
        routes = self.rows(result, O2.Routes, ["Name", "OrderCount", "TotalCost", "TotalTime", "TotalTravelTime",
                                               "TotalOrderServiceTime", "TotalDistance", "EndTime", "SHAPE@WKT"])
        self.assertEqual([r[:2] + tuple(round(v, 9) for v in r[2:7]) + r[7:] for r in routes],
                         [("R1", 2, 10.0, 10.0, 8.0, 2.0, 0.08, at(8, 10), "LINESTRING (50 0, 20 0, 10 0, 50 0)"),
                          ("R2", 2, 10.0, 10.0, 8.0, 2.0, 0.08, at(8, 10), "LINESTRING (50 0, 80 0, 90 0, 50 0)")])
        self.assertEqual(self.rows(result, O2.DepotVisits, ["DepotName", "RouteName", "Sequence", "VisitType"]),
                         [("D", "R1", 1, 1), ("D", "R1", 4, 2), ("D", "R2", 1, 1), ("D", "R2", 4, 2)])
        self.assertTrue(nax.VehicleRoutingProblem.__grapy_equivalent__.startswith("Solomon's insertion"))
        self.assertIn("nax.VehicleRoutingProblem.solve is an equivalent", arcpy.GetMessages())
        # One route of capacity 4 takes all four, and the costlier second route is left unused.
        routes = [{"Name": "Big", "StartDepotName": "D", "EndDepotName": "D", "Capacity_1": 4},
                  {"Name": "Dear", "StartDepotName": "D", "EndDepotName": "D", "Capacity_1": 4, "FixedCost": 100}]
        result = self.vrp(network, orders, routes).solve()
        counts = self.rows(result, O2.Routes, ["Name", "OrderCount", "TotalTravelTime"])
        self.assertEqual([c[:2] for c in counts], [("Big", 4), ("Dear", 0)])
        self.assertAlmostEqual(counts[0][2], 16.0)                    # 50 → 10 → 90 → 50, either way round

    def test_schema_one_and_its_unassigned_stops(self):
        network = self.line()
        vrp = nax.VehicleRoutingProblem(network)                      # version One, as documented
        self.assertIs(vrp.version, nax.VehicleRoutingProblemSchemaVersion.One)
        vrp.travelMode = "Driving"
        vrp.defaultDate = DAY
        one = nax.VehicleRoutingProblemInputDataType
        with vrp.insertCursor(one.Depots, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["D", (50, 0)])
        with vrp.insertCursor(one.Orders, ["Name", "DeliveryQuantities", "SpecialtyNames", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["near", "1 0", None, (40, 0)])
            cursor.insertRow(["heavy", "5 0", None, (60, 0)])          # more than the route carries
            cursor.insertRow(["skilled", "1 0", "Crane", (70, 0)])     # no route has a crane
        with vrp.insertCursor(one.Routes, ["Name", "StartDepotName", "EndDepotName", "Capacities"]) as cursor:
            cursor.insertRow(["R", "D", "D", "2 2"])
        result = vrp.solve()
        one_out = nax.VehicleRoutingProblemOutputDataType
        stops = self.rows(result, one_out.Stops, ["Name", "StopType", "RouteName", "Sequence", "DeliveryQuantities"])
        self.assertEqual(stops, [("D", 1, "R", 1, None), ("near", 0, "R", 2, "1 0"), ("D", 1, "R", 3, None)])
        unassigned = self.rows(result, one_out.UnassignedStops, ["Name", "ViolatedConstraint_1", "ViolatedConstraints"])
        self.assertEqual(unassigned, [("heavy", _fleet.CAPACITY, 2), ("skilled", _fleet.SPECIALTY, 64)])
        self.assertEqual(self.rows(result, one_out.UnassignedStops, ["SHAPE@"]), [(None,), (None,)])  # no stop shapes

    def test_time_windows_breaks_pairs_zones_and_rules(self):
        network = self.line()
        orders = [{"Name": "late", "TimeWindowStart": at(9), "TimeWindowEnd": at(9, 30), "SHAPE@XY": (60, 0)},
                  {"Name": "never", "TimeWindowStart": at(7), "TimeWindowEnd": at(7, 30), "MaxViolationTime": 0,
                   "SHAPE@XY": (40, 0)},
                  {"Name": "pick", "PickupQuantity_1": 1, "SHAPE@XY": (90, 0)},
                  {"Name": "drop", "DeliveryQuantity_1": 1, "SHAPE@XY": (10, 0)},
                  {"Name": "kept", "RouteName": "R1", "AssignmentRule": 2, "SHAPE@XY": (95, 0)},
                  {"Name": "out", "AssignmentRule": 0, "SHAPE@XY": (5, 0)}]
        routes = [{"Name": "R1", "StartDepotName": "D", "EndDepotName": "D", "Capacity_1": 1,
                   "EarliestStartTime": at(8), "LatestStartTime": at(8, 30)}]
        breaks = [{"RouteName": "R1", "Precedence": 1, "ServiceTime": 30, "TimeWindowStart": at(10),
                   "TimeWindowEnd": at(11), "IsPaid": 0}]
        pairs = [{"FirstOrderName": "pick", "SecondOrderName": "drop", "MaxTransitTime": 30}]
        result = self.vrp(network, orders, routes, extra={I2.Breaks: breaks, I2.OrderPairs: pairs}).solve()
        got = {r[0]: r[1:] for r in self.rows(result, O2.Orders, ["Name", "RouteName", "ArriveTime", "DepartTime",
                                                                   "WaitTime", "ViolatedConstraint_1"])}
        self.assertEqual(got["never"][0], None)                           # a hard window it cannot meet
        self.assertEqual(got["never"][-1], _fleet.HARD_WINDOW)
        self.assertEqual(got["out"][0], None)                              # excluded: left as it is
        self.assertEqual(got["kept"][0], "R1")
        self.assertEqual(got["late"][0], "R1")
        self.assertEqual(got["late"][2], at(9))                            # served when its window opens
        sequence = {r[0]: r[1] for r in self.rows(result, O2.Orders, ["Name", "Sequence"])}
        self.assertLess(sequence["pick"], sequence["drop"])                # the pickup first, on the same route
        (route,) = self.rows(result, O2.Routes, ["StartTime", "EndTime", "TotalTime", "TotalBreakServiceTime",
                                                 "RegularTimeCost", "TotalWaitTime"])
        # The break, unpaid, waits for its window after the last order and is left out of the cost.
        (brk,) = self.rows(result, O2.Breaks, ["RouteName", "ArriveTime", "DepartTime", "Sequence"])
        self.assertEqual(brk[0], "R1")
        self.assertEqual(brk[2] - brk[1] >= datetime.timedelta(minutes=30), True)
        self.assertAlmostEqual(route[4], route[2] - 30)
        self.assertEqual(route[3], 30.0)
        # A route kept to orders inside its hard zone leaves the others.
        zone = {"RouteName": "R1", "IsHardZone": 1, "SHAPE@WKT": "POLYGON((30 -5,70 -5,70 5,30 5,30 -5))"}
        orders = [{"Name": "in", "SHAPE@XY": (40, 0)}, {"Name": "outside", "SHAPE@XY": (90, 0)}]
        result = self.vrp(network, orders, routes[:1], extra={I2.RouteZones: [zone]}).solve()
        self.assertEqual(self.rows(result, O2.Orders, ["Name", "RouteName"]), [("in", "R1"), ("outside", None)])

    def test_what_pro_answers(self):
        """ArcGIS Pro 3.7's answers on the same road, measured 2026-10-31 (tools/pro-compare/adr239-fleet.py)."""
        network = self.line()
        late = lambda importance, x, minutes: self.vrp(                                       # noqa: E731
            network, [{"Name": "a", "SHAPE@XY": (x, 0)},
                      {"Name": "b", "SHAPE@XY": (100, 0), "TimeWindowStart": at(8),
                       "TimeWindowEnd": at(8) + datetime.timedelta(minutes=(50 - x) / 5 + 5 - minutes)}],
            [{"Name": "R", "StartDepotName": "D", "EarliestStartTime": at(8), "LatestStartTime": at(8),
              "CostPerUnitTime": 1.0, "CostPerUnitDistance": 0.0}], extra=None)
        for importance, x, minutes, first in (("High", 5, 0.04, "a"), ("High", 5, 0.06, "b"), ("Medium", 5, 0.45, "a"),
                                              ("Medium", 5, 0.55, "b"), ("Low", 5, 8, "a"), ("Low", 0.5, 9, "a"),
                                              ("Low", 0, 5, "b")):
            vrp = late(importance, x, minutes)
            vrp.timeWindowFactor = getattr(nax.Importance, importance)
            got = min(self.rows(vrp.solve(), O2.Orders, ["Sequence", "Name"]))[1]
            self.assertEqual(got, first, (importance, x, minutes))
        orders = [{"Name": f"o{x}", "ServiceTime": 1.0, "SHAPE@XY": (x, 0)} for x in (10, 20, 80, 90)]
        routes = [{"Name": "R", "StartDepotName": "D", "EndDepotName": "D", "EarliestStartTime": at(8),
                   "LatestStartTime": at(8), "CostPerUnitTime": 1.0, "CostPerUnitDistance": 0.0}]
        fields = ["OrderCount", "TotalTravelTime", "TotalWaitTime", "EndTime"]
        # A window break that opens after the last order is waited for at the end depot, after the drive home.
        brk = {"RouteName": "R", "Precedence": 1, "ServiceTime": 3.0, "TimeWindowStart": at(9),
               "TimeWindowEnd": at(9, 30), "IsPaid": 1}
        result = self.vrp(network, orders, routes, extra={I2.Breaks: [brk]}).solve()
        self.assertEqual(self.rows(result, O2.Routes, fields), [(4, 16.0, 40.0, at(9, 3))])
        self.assertEqual(self.rows(result, O2.Breaks, ["Sequence", "ArriveTime"]), [(6, at(8, 20))])
        # The only travel time break also bounds the drive after it: two orders are left, not served late.
        brk = {"RouteName": "R", "Precedence": 1, "ServiceTime": 2.0, "MaxTravelTimeBetweenBreaks": 6.0, "IsPaid": 1}
        result = self.vrp(network, orders, routes, extra={I2.Breaks: [brk]}).solve()
        self.assertEqual(self.rows(result, O2.Routes, fields), [(2, 8.0, 0.0, at(8, 12))])
        self.assertEqual(self.rows(result, O2.Breaks, ["Sequence", "ArriveTime"]), [(4, at(8, 8))])

    def test_heuristic_finds_the_brute_force_optimum(self):
        network = self.grid()
        points = [(0, 5), (5, 20), (20, 15), (15, 0), (10, 15), (0, 15)]
        orders = [{"Name": f"o{k}", "ServiceTime": 2.0, "DeliveryQuantity_1": 1.0 + (k % 2), "SHAPE@XY": p}
                  for k, p in enumerate(points)]
        orders[2].update(TimeWindowStart=at(8, 0), TimeWindowEnd=at(8, 6))
        orders[4].update(TimeWindowStart=at(8, 20), TimeWindowEnd=at(8, 40))
        routes = [{"Name": "R1", "StartDepotName": "D", "EndDepotName": "D", "Capacity_1": 5, "CostPerUnitTime": 1.0},
                  {"Name": "R2", "StartDepotName": "D", "EndDepotName": "D", "Capacity_1": 5, "CostPerUnitTime": 1.0,
                   "FixedCost": 3.0}]
        vrp = self.vrp(network, orders, routes, depot=(10, 10))
        graph = vrp._check()
        problem = _fleet.Fleet(graph, vrp._fleet_inputs(graph), "VRP", "Two", "Minutes", "Kilometers", DAY)
        solution = problem.solve()
        found = sum(problem.plan(r, s)["objective"] for r, s in solution.items())
        self.assertEqual(problem.unassigned, [])
        best = math.inf
        items = list(range(len(orders)))
        for mask in range(1 << len(items)):
            sides = [[i for i in items if not mask >> i & 1], [i for i in items if mask >> i & 1]]
            total = 0.0
            for r, side in enumerate(sides):
                options = [problem.plan(r, list(p)) for p in itertools.permutations(side)]
                options = [o["objective"] for o in options if not o["codes"]]
                if not options:
                    total = math.inf
                    break
                total += min(options)
            best = min(best, total)
        self.assertAlmostEqual(found, best)

    def test_route_finds_the_best_order(self):
        network = self.grid()
        points = [(0, 0), (20, 20), (0, 20), (20, 0), (10, 5)]
        route = nax.Route(network)
        route.travelMode = "Driving"
        with route.insertCursor(nax.RouteInputDataType.Stops, ["Name", "SHAPE@XY"]) as cursor:
            for k, p in enumerate(points):
                cursor.insertRow([f"s{k}", p])
        route.findBestSequence = True
        route.preserveFirstStop = True
        result = route.solve()
        ((total,),) = list(result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes"]))

        def minutes(a, b):
            return (abs(a[0] - b[0]) + abs(a[1] - b[1])) / 10

        # Brute force over the grid's Manhattan distances (every stop here is on a street, so they are the network's).
        best = min(sum(minutes(p, q) for p, q in zip((points[0],) + order, order))
                   for order in itertools.permutations(points[1:]))
        self.assertAlmostEqual(total, best)
        sequence = [s[0] for s in sorted(result.searchCursor(nax.RouteOutputDataType.Stops, ["Name", "Sequence"]),
                                         key=lambda s: s[1])]
        self.assertEqual(sequence[0], "s0")
        route.returnToStart = True
        ((closed, last),) = list(route.solve().searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes",
                                                                                              "LastStopOID"]))
        self.assertEqual(last, 1)
        tour = min(sum(minutes(p, q) for p, q in zip((points[0],) + order, order + (points[0],)))
                   for order in itertools.permutations(points[1:]))
        self.assertAlmostEqual(closed, tour)

    # ---- last mile delivery

    def test_last_mile_delivery(self):
        network = self.line()
        lmd = nax.LastMileDelivery(network)
        lmd.travelMode = "Driving"
        lmd.earliestRouteStartDate = DAY
        lmd.earliestRouteStartTime = datetime.time(9)
        lmd.maxRouteTotalTime = 60
        lmd.sequenceGap = 10
        I = nax.LastMileDeliveryInputDataType
        with lmd.insertCursor(I.Depots, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["D", (50, 0)])
        with lmd.insertCursor(I.Orders, ["Name", "AnchorRule", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["first", 1, (90, 0)])
            cursor.insertRow(["mid", None, (60, 0)])
        with lmd.insertCursor(I.Routes, ["Name", "StartDepotName", "EndDepotName"]) as cursor:
            cursor.insertRow(["R", "D", "D"])
        _SAID_EQUIVALENT.discard("nax.LastMileDelivery")
        result = lmd.solve()
        out = nax.LastMileDeliveryOutputDataType
        # Anchored first, 90 comes before 60 though 60 is nearer: 4 + 3 + 1 = 8 minutes.
        self.assertEqual(self.rows(result, out.Orders, ["Name", "Sequence", "ArriveTime"]),
                         [("first", 10, at(9, 4)), ("mid", 20, at(9, 7))])
        self.assertEqual(self.rows(result, out.DepotVisits, ["VisitType", "Sequence"]), [(1, 0), (2, 30)])
        ((total,),) = self.rows(result, out.Routes, ["TotalTravelTime"])
        self.assertAlmostEqual(total, 8.0)
        self.assertIn("nax.LastMileDelivery.solve is an equivalent", arcpy.GetMessages())

    # ---- waste collection

    def test_waste_collection_and_its_update_tools(self):
        network = self.grid()
        wc = nax.WasteCollection(network)
        wc.travelMode = "Driving"
        wc.routeStartTime = datetime.time(6)
        wc.maxRouteTotalTime = 120
        I = nax.WasteCollectionInputDataType
        with wc.insertCursor(I.Depots, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["Yard", (0, 0)])
        with wc.insertCursor(I.Renewals, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["Tip", (20, 20)])
        with wc.insertCursor(I.Stops, ["Name", "Bins_1", "ServiceTime", "SHAPE@XY"]) as cursor:
            for k, p in enumerate([(3, 0), (6, 0), (10, 4), (20, 6), (14, 20), (0, 13)]):
                cursor.insertRow([f"s{k}", 1, 0.5, p])
        with wc.insertCursor(I.Routes, ["Name", "StartDepotName", "EndDepotName", "MaxBins_1"]) as cursor:
            cursor.insertRow(["T1", "Yard", "Yard", 3])
            cursor.insertRow(["T2", "Yard", "Yard", 3])
        with wc.insertCursor(I.RouteRenewals, ["RouteName", "RenewalName", "ServiceTime"]) as cursor:
            cursor.insertRow(["T1", "Tip", 5])
        result = wc.solve()
        out = nax.WasteCollectionOutputDataType
        stops = self.rows(result, out.Stops, ["Name", "RouteName", "Sequence", "RouteLineID"])
        self.assertTrue(all(s[1] == "T1" for s in stops))                # one route with a renewal beats two
        self.assertEqual(sorted(s[2] for s in stops), [1, 2, 3, 4, 5, 6])
        # s0 and s1 share the edge (0,0)–(10,0): one unit, one collecting line.
        self.assertEqual(stops[0][3], stops[1][3])
        routes = self.rows(result, out.Routes, ["Name", "StopCount", "RenewalCount", "TotalBins_1"])
        self.assertEqual(routes, [("T1", 6, 2, 6.0), ("T2", 0, 0, None)])
        visits = self.rows(result, out.RenewalVisits, ["RouteName", "RenewalName", "Sequence", "UnloadedBins_1"])
        self.assertEqual([v[:3] for v in visits], [("T1", "Tip", 1), ("T1", "Tip", 2)])
        self.assertEqual(sum(v[3] for v in visits), 6.0)
        activities = {r[0] for r in self.rows(result, out.RouteLines, ["Activity"])}
        self.assertTrue({1, 4} <= activities)
        self.assertTrue(nax.WasteCollection.__grapy_equivalent__.startswith("collection units"))

    def test_waste_collection_layer_and_fleet_routing_updates(self):
        network = self.grid()
        layer = na.MakeWasteCollectionAnalysisLayer(network, "bins", "Driving", route_start_time=datetime.time(6),
                                                    max_route_total_time=120, stop_collection_mode="BOTH_SIDES")[0]
        self.assertEqual(na.GetNAClassNames(layer, "INPUT"), {
            "Stops": "Stops", "Depots": "Depots", "Routes": "Routes", "Renewals": "Renewals",
            "RouteRenewals": "Route Renewals", "RenewalVisits": "Renewal Visits", "PointBarriers": "Point Barriers",
            "PolylineBarriers": "Line Barriers", "PolygonBarriers": "Polygon Barriers"})
        depot = self.points("yard", [("Yard", "POINT(0 0)")])
        stops = self.points("stops", [("s0", "POINT(5 0)"), ("s1", "POINT(20 15)"), ("s2", "POINT(5 20)")])
        na.AddLocations(layer, "Depots", depot)
        na.AddLocations(layer, "Stops", stops)
        na.AddVehicleRoutingProblemRoutes(layer, 2, "Truck", "Yard", "Yard", waste_capacities=[[10]])
        na.Solve(layer)
        path = na.GetNASublayer(layer, "Stops")
        assigned = {r[0]: r[1] for r in arcpy.da.SearchCursor(path, ["Name", "RouteName"])}
        self.assertEqual(set(assigned.values()), {"Truck1"})               # fewer routes first
        # Move s1 to the second truck by hand, and let the results follow.
        with arcpy.da.UpdateCursor(path, ["Name", "RouteName"]) as rows:
            for row in rows:
                if row[0] == "s1":
                    rows.updateRow([row[0], "Truck2"])
        na.UpdateFleetRoutingResults(layer)
        counts = {r[0]: r[1] for r in arcpy.da.SearchCursor(na.GetNASublayer(layer, "Routes"), ["Name", "StopCount"])}
        self.assertEqual(counts, {"Truck1": 2, "Truck2": 1})
        # Select s0 and take it off its route.
        view = arcpy.management.MakeFeatureLayer(path, "stops_view")[0]
        arcpy.management.SelectLayerByAttribute(view, "NEW_SELECTION", "Name = 's0'")
        na.UpdateFleetRoutingAssignments(layer, "NOT_ASSIGNED")
        assigned = {r[0]: r[1] for r in arcpy.da.SearchCursor(path, ["Name", "RouteName"])}
        self.assertEqual(assigned, {"s0": None, "s1": "Truck2", "s2": "Truck1"})
        with self.assertRaises(arcpy.ExecuteError):
            vrp = na.MakeVehicleRoutingProblemAnalysisLayer(network, "v", "Driving",
                                                            generate_directions_on_solve="NO_DIRECTIONS")[0]
            na.UpdateFleetRoutingResults(vrp)                              # waste collection layers only

    def points(self, name, rows):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(path, "Name", "TEXT")
        with arcpy.da.InsertCursor(path, ["Name", "SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    # ---- the analysis layer tools

    def test_vehicle_routing_problem_layer(self):
        network = self.line()
        layer = na.MakeVehicleRoutingProblemAnalysisLayer(network, "vrp", "Driving", default_date=DAY,
                                                          generate_directions_on_solve="NO_DIRECTIONS")[0]
        depots = self.points("depots", [("D", "POINT(50 0)")])
        orders = self.points("orders", [("a", "POINT(10 0)"), ("b", "POINT(90 0)")])
        na.AddLocations(layer, "Depots", depots)
        na.AddLocations(layer, "Orders", orders)
        na.AddVehicleRoutingProblemRoutes(layer, 1, "Van", "D", "D", max_order_count=5)
        na.AddVehicleRoutingProblemBreaks(layer, "Van1", "MAXIMUM_TRAVEL_TIME_BREAK",
                                          travel_time_properties=[[True, 15, 10]])
        _SAID_EQUIVALENT.discard("na.Solve Vehicle Routing Problem")
        result = na.Solve(layer)
        self.assertEqual(result[1], "true")
        routes = list(arcpy.da.SearchCursor(na.GetNASublayer(layer, "Routes"),
                                            ["Name", "OrderCount", "TotalTravelTime", "TotalBreakServiceTime",
                                             "EarliestStartTime"]))
        self.assertEqual(routes[0][:4], ("Van1", 2, 16.0, 15.0))
        self.assertTrue(str(routes[0][4]).startswith("2026-01-05 08:00"))
        # The break is taken ten minutes into the drive, part-way along the second leg (4 minutes then 8); the four
        # minutes home after it are within its limit, which bounds them too.
        (brk,) = list(arcpy.da.SearchCursor(na.GetNASublayer(layer, "Breaks"),
                                            ["RouteName", "Sequence", "RelativePosition", "CumulTravelTime"]))
        self.assertEqual(brk[:2], ("Van1", 3))
        self.assertAlmostEqual(brk[3], 10.0)
        visits = list(arcpy.da.SearchCursor(na.GetNASublayer(layer, "Depot Visits"), ["VisitType", "Sequence"]))
        self.assertEqual(visits, [(1, 1), (2, 5)])
        self.assertIn("arcpy.na.Solve solves a vehicle routing problem by an equivalent", arcpy.GetMessages())
        lmd = na.MakeLastMileDeliveryAnalysisLayer(network, "lmd", "Driving", earliest_route_start_date=DAY,
                                                   earliest_route_start_time=datetime.time(7), max_route_total_time=60)[0]
        self.assertIn("Zones", na.GetNAClassNames(lmd, "INPUT"))


if __name__ == "__main__":
    unittest.main()
