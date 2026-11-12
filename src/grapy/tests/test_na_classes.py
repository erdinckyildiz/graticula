"""arcpy.na's and arcpy.nax's classes and enumerations, each checked by what it changes in a solve (ADR-226, ADR-239,
ADR-246).

Expected values are worked by hand from Esri's public arcpy pages (doc.esri.com/en/arcgis-pro/latest/arcpy/
network-analyst/…: NAClassFieldMappings, NAClassFieldMap, TravelMode, the *SolverProperties classes; …/arcpy/
network-analyst/<solver>.htm for the nax solvers and their enumerations; the Add Field To Analysis Layer tool page).
Where a page does not say, the comment says INFERRED.

Three networks:

``grid`` is test_network's (ADR-226), each road one minute except F (four):

    D(10,10) ── (15,15) ── E(20,10)
       │                      │
    (10,5) ──────────────── (20,5)         F joins nothing: its ends are its only junctions
       │                      │
    A(0,0) ── B(10,0) ──── C(20,0)

``oneway``: one road (0,0)–(100,0) costing 10 minutes along its digitised direction and 30 against it.

``line``: one road (0,0)–(100,0), ten minutes either way (test_fleet's, ADR-239).
"""
import datetime
import json
import os
import sys
import shutil
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.na as na  # noqa: E402
import grapy.nax as nax  # noqa: E402
# Length is an edge's length on the ellipsoid, as ArcGIS Pro's Length attribute measures it (measured in ArcGIS Pro 3.7,
# 2026-11-12, Pro sweep): on Web Mercator at the equator a north–south metre is 1 − e² of a metre on the ellipsoid, an
# east–west one a metre. Until then Length was the planar length.
NS = 1 - 0.00669437999014

DAY = datetime.date(2026, 1, 5)


class _Networks(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, arcpy.env, "workspace", arcpy.env.workspace)
        arcpy.env.workspace = self.directory

    def feature_dataset(self, name):
        gpkg = arcpy.management.CreateFileGDB(self.directory, name)[0]
        return arcpy.management.CreateFeatureDataset(gpkg, "transport", 3857)[0]

    def grid(self):
        fds = self.feature_dataset("grid")
        roads = arcpy.management.CreateFeatureclass(fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(roads, "minutes", "DOUBLE")
        rows = [("LINESTRING(0 0,10 0)", 1), ("LINESTRING(10 0,20 0)", 1), ("LINESTRING(10 0,10 5,10 10)", 1),
                ("LINESTRING(10 10,15 15,20 10)", 1), ("LINESTRING(20 0,20 5,20 10)", 1),
                ("LINESTRING(0 5,10 5,20 5)", 4)]
        with arcpy.da.InsertCursor(roads, ["SHAPE@", "minutes"]) as written:
            for row in rows:
                written.insertRow(row)
        network = na.CreateNetworkDataset(fds, "streets", ["roads"], "NO_ELEVATION")[0]
        arcpy.graticula.AddNetworkAttribute(network, "Minutes", "Cost", "Minutes", [("roads", "BOTH", "FIELD", "minutes")])
        arcpy.graticula.AddNetworkTravelMode(network, "Driving", "Minutes", [], "Minutes", "Length")
        na.BuildNetwork(network)
        return network

    def oneway(self):
        fds = self.feature_dataset("oneway")
        roads = arcpy.management.CreateFeatureclass(fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(roads, "along", "DOUBLE")
        arcpy.management.AddField(roads, "against", "DOUBLE")
        with arcpy.da.InsertCursor(roads, ["SHAPE@", "along", "against"]) as written:
            written.insertRow(("LINESTRING(0 0,100 0)", 10.0, 30.0))
        network = na.CreateNetworkDataset(fds, "streets", ["roads"], "NO_ELEVATION")[0]
        arcpy.graticula.AddNetworkAttribute(network, "Minutes", "Cost", "Minutes",
                                            [("roads", "FROM_TO", "FIELD", "along"),
                                             ("roads", "TO_FROM", "FIELD", "against")])
        arcpy.graticula.AddNetworkTravelMode(network, "Driving", "Minutes", [], "Minutes", "Length")
        na.BuildNetwork(network)
        return network

    def line(self):
        fds = self.feature_dataset("line")
        roads = arcpy.management.CreateFeatureclass(fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(roads, "minutes", "DOUBLE")
        with arcpy.da.InsertCursor(roads, ["SHAPE@", "minutes"]) as written:
            written.insertRow(("LINESTRING(0 0,100 0)", 10.0))
        network = na.CreateNetworkDataset(fds, "net", ["roads"], "NO_ELEVATION")[0]
        arcpy.graticula.AddNetworkAttribute(network, "Minutes", "Cost", "Minutes", [("roads", "BOTH", "FIELD", "minutes")])
        arcpy.graticula.AddNetworkTravelMode(network, "Driving", "Minutes", [], "Minutes", "Length")
        na.BuildNetwork(network)
        return network

    def points(self, name, rows, extra=()):
        """A point class with Name and the ``extra`` (field, type) columns; each row (Name, WKT, *extra values)."""
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(path, "Name", "TEXT")
        for field, kind in extra:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, ["Name", *[f for f, _ in extra], "SHAPE@"]) as written:
            for row in rows:
                written.insertRow((row[0], *row[2:], row[1]))
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]


# ---- arcpy.na ---------------------------------------------------------------------------------------------------------

class NaClasses(_Networks):

    def test_make_network_dataset_layer_is_usable_by_a_solver(self):
        network = self.grid()
        layer = na.MakeNetworkDatasetLayer(network, "streets_layer")[0]
        self.assertEqual(layer, "streets_layer")
        route = na.MakeRouteAnalysisLayer(layer, "trip", "Driving", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(route, "Stops", self.points("stops", [("start", "POINT(2 0)"), ("end", "POINT(20 4)")]))
        self.assertEqual(na.Solve(route)[1], "true")
        # (2,0) → B 0.8 min → C 1 → (20,4) 0.4: 2.2 minutes, 22 m.
        (minutes,), = self.rows(na.GetNASublayer(route, "Routes"), ["Total_Minutes"])
        self.assertAlmostEqual(minutes, 2.2)
        # The layer name reaches the nax solvers and NetworkDataset too. edgeCount counts each edge once in each
        # direction, as Pro does (measured in ArcGIS Pro 3.7, 2026-11-12 (Pro sweep)); this said 6.
        self.assertEqual(nax.NetworkDataset(layer).edgeCount, 12)

    def test_travel_mode_object_and_its_impedance(self):
        network = self.grid()
        modes = na.GetTravelModes(network)
        driving = modes["Driving"]
        self.assertIsInstance(driving, na.TravelMode)
        self.assertEqual((driving.name, driving.impedance, driving.timeAttributeName, driving.distanceAttributeName),
                         ("Driving", "Minutes", "Minutes", "Length"))
        # The page: a TravelMode is made from another one or from its JSON string, and str() gives that JSON.
        copy = na.TravelMode(driving)
        self.assertEqual((copy.name, copy.impedance, copy.restrictions), ("Driving", "Minutes", []))
        again = na.TravelMode(str(copy))
        self.assertEqual(json.loads(str(again))["impedanceAttributeName"], "Minutes")
        # The impedance decides the solve. A mode minimising Length on the same network routes by metres.
        by_length = na.TravelMode(str(driving))
        by_length.name, by_length.impedance = "Shortest", "Length"
        # A solve totals the mode's time attribute too (Pro 3.7, measured 2026-11-12, Pro sweep): none here.
        by_length.timeAttributeName = None
        fixtures = self.points("stops", [("start", "POINT(2 0)"), ("end", "POINT(20 4)")])
        totals = {}
        for mode in (driving, by_length):
            layer = na.MakeRouteAnalysisLayer(network, mode.name, mode, generate_directions_on_solve="NO_DIRECTIONS")[0]
            na.AddLocations(layer, "Stops", fixtures)
            na.Solve(layer)
            fields = [f.name for f in arcpy.ListFields(na.GetNASublayer(layer, "Routes"))]
            totals[mode.name] = fields
        self.assertIn("Total_Minutes", totals["Driving"])
        self.assertIn("Total_Length", totals["Shortest"])
        self.assertNotIn("Total_Minutes", totals["Shortest"])
        self.assertIs(nax.TravelMode, na.TravelMode)                     # nax documents the same class

    def test_add_field_to_analysis_layer_and_field_mappings(self):
        # The Add Field To Analysis Layer page's workflow: add Address to Facilities, map it with NAClassFieldMappings,
        # AddLocations transfers the values.
        network = self.grid()
        layer = na.MakeServiceAreaAnalysisLayer(network, "coverage", "Driving", cutoffs=[2])[0]
        names = na.GetNAClassNames(layer)
        self.assertEqual(names["Facilities"], "Facilities")
        na.AddFieldToAnalysisLayer(layer, names["Facilities"], "Address", "TEXT")
        fields = {f.name: f.type for f in arcpy.ListFields(na.GetNASublayer(layer, "Facilities"))}
        self.assertEqual(fields.get("Address"), "String")
        stations = self.points("stations", [("s1", "POINT(10 9)", "1 Main St", "North"),
                                            ("s2", "POINT(0 0.5)", "2 Side Rd", "West")],
                               [("Address", "TEXT"), ("Label", "TEXT")])
        mappings = na.NAClassFieldMappings(layer, names["Facilities"], False, arcpy.ListFields(stations))
        self.assertIsInstance(mappings["Name"], na.NAClassFieldMap)
        self.assertEqual(mappings["Name"].mappedFieldName, "Name")       # a candidate of the same name
        self.assertEqual(mappings["Address"].mappedFieldName, "Address")
        self.assertNotIn("SourceOID", mappings)                         # location fields are not properties here
        mappings["Name"].mappedFieldName = "Label"                       # names from another field
        na.AddLocations(layer, names["Facilities"], stations, mappings)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Facilities"), ["Name", "Address"]),
                         [("North", "1 Main St"), ("West", "2 Side Rd")])
        na.Solve(layer)
        polygons = self.rows(na.GetNASublayer(layer, "Polygons"), ["Name"])
        # Whole cutoffs without decimals (measured in ArcGIS Pro 3.7, 2026-11-12, Pro sweep); this said 0.0 - 2.0.
        self.assertEqual(sorted(p[0] for p in polygons), ["North : 0 - 2", "West : 0 - 2"])

    def test_na_class_field_map_default_value(self):
        # NAClassFieldMap: a property takes a constant (defaultValue) when no field is mapped. Two facilities per
        # incident, as TargetFacilityCount 2, though the layer finds one.
        network = self.grid()
        layer = na.MakeClosestFacilityAnalysisLayer(network, "cf", "Driving")[0]
        na.AddLocations(layer, "Facilities", self.points("f", [("near", "POINT(10 9)"), ("far", "POINT(0 0.5)")]))
        incidents = self.points("i", [("x", "POINT(14 0)", "call")], [("Caller", "TEXT")])
        mappings = na.NAClassFieldMappings(layer, "Incidents", False, arcpy.ListFields(incidents))
        self.assertIsNone(mappings["TargetFacilityCount"].mappedFieldName)
        mappings["TargetFacilityCount"] = na.NAClassFieldMap(defaultValue=2)
        mappings["Name"] = na.NAClassFieldMap("Caller")
        na.AddLocations(layer, "Incidents", incidents, mappings)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Incidents"), ["Name", "TargetFacilityCount"]), [("call", 2)])
        na.Solve(layer)
        # From (14,0): near 4 + 9 = 1.3 minutes, far 4 + 10 = 1.4.
        routes = self.rows(na.GetNASublayer(layer, "Routes"), ["Name", "FacilityRank", "Total_Minutes"])
        self.assertEqual([r[:2] for r in routes], [("call - near", 1), ("call - far", 2)])
        self.assertAlmostEqual(routes[1][2], 1.4)

    def test_closest_facility_solver_properties(self):
        network = self.oneway()
        layer = na.MakeClosestFacilityAnalysisLayer(network, "cf", "Driving")[0]
        na.AddLocations(layer, "Facilities", self.points("f", [("depot", "POINT(0 0)"), ("yard", "POINT(50 0)")]))
        na.AddLocations(layer, "Incidents", self.points("i", [("call", "POINT(100 0)")]))
        props = na.GetSolverProperties(layer)
        self.assertIsInstance(props, na.ClosestFacilitySolverProperties)
        self.assertEqual((props.solverName, props.travelDirection, props.defaultTargetFacilityCount),
                         ("Closest Facility Solver", "TRAVEL_TO", 1))

        def solve():
            na.Solve(layer)
            return [(n, round(m, 9)) for n, m in self.rows(na.GetNASublayer(layer, "Routes"), ["Name", "Total_Minutes"])]

        # Toward the facilities the incident travels against the digitising: 30 minutes the whole road.
        self.assertEqual(solve(), [("call - yard", 15.0)])
        props.travelDirection = "TRAVEL_FROM"
        self.assertEqual(solve(), [("yard - call", 5.0)])
        props.defaultTargetFacilityCount = 2
        self.assertEqual(solve(), [("yard - call", 5.0), ("depot - call", 10.0)])
        props.defaultCutoff = 6
        self.assertEqual(solve(), [("yard - call", 5.0)])
        props.outputPathShape = "NO_LINES"
        na.Solve(layer)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Routes"), ["SHAPE@"]), [(None,)])

    def test_service_area_solver_properties(self):
        network = self.grid()
        layer = na.MakeServiceAreaAnalysisLayer(network, "sa", "Driving", cutoffs=[1])[0]
        na.AddLocations(layer, "Facilities", self.points("f", [("C", "POINT(20 0)")]))
        props = na.GetSolverProperties(layer)
        self.assertIsInstance(props, na.ServiceAreaSolverProperties)
        self.assertEqual((props.defaultBreaks, props.polygonNesting, props.lineType), ([1.0], "RINGS", "NO_LINES"))
        props.defaultBreaks = [1, 0.5]
        props.lineType = "TRUE_LINES"
        na.Solve(layer)
        polygons = self.rows(na.GetNASublayer(layer, "Polygons"), ["FromBreak", "ToBreak"])
        self.assertEqual(sorted(polygons), [(0.0, 0.5), (0.5, 1.0)])           # rings, the breaks sorted
        lines = self.rows(na.GetNASublayer(layer, "Lines"), ["SHAPE@LENGTH"])
        # One minute from C: 10 m west, 10 m up CE — 20 m of road.
        self.assertAlmostEqual(sum(l[0] for l in lines), 20.0)
        props.polygonNesting = "DISKS"
        na.Solve(layer)
        self.assertEqual(sorted(self.rows(na.GetNASublayer(layer, "Polygons"), ["FromBreak", "ToBreak"])),
                         [(0.0, 0.5), (0.0, 1.0)])
        # The STANDARD polygons the Make tool builds are SIMPLE_POLYS, as Pro answers (measured in ArcGIS Pro 3.7,
        # 2026-11-12, Pro sweep); DETAILED_POLYS, HIGH detail, is the one not built. This refused SIMPLE_POLYS.
        with self.assertRaises(arcpy.NotBuiltError):
            props.polygonType = "DETAILED_POLYS"

    def test_od_cost_matrix_solver_properties(self):
        network = self.grid()
        layer = na.MakeODCostMatrixAnalysisLayer(network, "od", "Driving")[0]
        na.AddLocations(layer, "Origins", self.points("o", [("call", "POINT(14 0)")]))
        na.AddLocations(layer, "Destinations", self.points("d", [("near", "POINT(10 9)"), ("far", "POINT(0 0.5)")]))
        props = na.GetSolverProperties(layer)
        self.assertIsInstance(props, na.ODCostMatrixSolverProperties)
        self.assertEqual((props.outputPathShape, props.defaultCutoff, props.defaultTargetDestinationCount),
                         ("STRAIGHT_LINES", None, None))
        na.Solve(layer)
        self.assertEqual(len(self.rows(na.GetNASublayer(layer, "Lines"), ["OID@"])), 2)
        props.defaultTargetDestinationCount = 1
        na.Solve(layer)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Lines"), ["Name"]), [("call - near",)])
        props.defaultTargetDestinationCount = None
        props.defaultCutoff = 1.35                                    # near 1.3 minutes, far 1.4
        props.outputPathShape = "NO_LINES"
        na.Solve(layer)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Lines"), ["Name", "SHAPE@"]), [("call - near", None)])

    def test_location_allocation_solver_properties(self):
        network = self.grid()
        layer = na.MakeLocationAllocationAnalysisLayer(network, "la", "Driving")[0]
        na.AddLocations(layer, "Facilities", self.points("f", [("A", "POINT(2 0)"), ("B", "POINT(18 0)")]))
        na.AddLocations(layer, "Demand Points", self.points("d", [("d1", "POINT(0 0)"), ("d2", "POINT(20 0)"),
                                                                 ("d3", "POINT(4 0)")]))
        props = na.GetSolverProperties(layer)
        self.assertIsInstance(props, na.LocationAllocationSolverProperties)
        self.assertEqual((props.facilitiesToFind, props.problemType, props.impedanceTransformation),
                         (1, "MINIMIZE_IMPEDANCE", "LINEAR"))
        na.Solve(layer)
        # One facility: A costs 0.2 + 1.8 + 0.2 = 2.2 minutes, B 1.8 + 0.2 + 1.4 = 3.4.
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Facilities"), ["Name", "FacilityType"]), [("A", 3), ("B", 0)])
        props.facilitiesToFind = 2
        na.Solve(layer)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Demand Points"), ["Name", "FacilityID"]),
                         [("d1", 1), ("d2", 2), ("d3", 1)])
        props.facilitiesToFind = 1
        props.impedanceCutoff = 1          # A reaches d1 and d3, B only d2: A, and d2 is left unserved
        na.Solve(layer)
        self.assertEqual(self.rows(na.GetNASublayer(layer, "Demand Points"), ["Name", "FacilityID"]),
                         [("d1", 1), ("d2", None), ("d3", 1)])
        with self.assertRaises(arcpy.NotBuiltError):
            props.impedanceTransformation = "POWER"

    def test_vehicle_routing_problem_solver_properties(self):
        network = self.line()
        layer = na.MakeVehicleRoutingProblemAnalysisLayer(network, "vrp", "Driving", default_date=DAY,
                                                          generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Depots", self.points("depots", [("D", "POINT(50 0)")]))
        na.AddLocations(layer, "Orders", self.points("orders", [("a", "POINT(10 0)"), ("b", "POINT(90 0)")]))
        na.AddVehicleRoutingProblemRoutes(layer, 1, "Van", "D", "D")
        props = na.GetSolverProperties(layer)
        self.assertIsInstance(props, na.VehicleRoutingProblemSolverProperties)
        self.assertEqual((props.timeFieldUnits, props.distanceFieldUnits, props.timeAttribute, props.distanceAttribute),
                         ("Minutes", "Miles", "Minutes", "Length"))
        props.timeFieldUnits = "Seconds"
        props.distanceFieldUnits = "Kilometers"
        na.Solve(layer)
        # D → a 40 m (4 min) → b 80 m (8) → D 40 m (4): 16 minutes, 160 m.
        (travel, distance), = self.rows(na.GetNASublayer(layer, "Routes"), ["TotalTravelTime", "TotalDistance"])
        self.assertAlmostEqual(travel, 16 * 60)
        self.assertAlmostEqual(distance, 0.16)


# ---- arcpy.nax --------------------------------------------------------------------------------------------------------

class NaxClasses(_Networks):

    def route(self, network, **settings):
        route = nax.Route(network)
        route.travelMode = "Driving"
        for name, value in settings.items():
            setattr(route, name, value)
        with route.insertCursor(nax.RouteInputDataType.Stops, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["start", (2, 0)])
            cursor.insertRow(["end", (20, 4)])
        return route.solve()

    def test_make_network_dataset_layer_is_usable_by_a_solver(self):
        network = self.grid()
        # With no name, the layer takes the dataset's own (INFERRED: the page gives the parameter as optional).
        self.assertEqual(nax.MakeNetworkDatasetLayer(network)[0], "streets")
        name = nax.MakeNetworkDatasetLayer(network, "nd")[0]
        result = self.route(name)
        self.assertTrue(result.solveSucceeded)
        (minutes,), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes"])
        self.assertAlmostEqual(minutes, 2.2)
        self.assertTrue(self.route("streets").solveSucceeded)

    def test_travel_mode_impedance_is_the_solve_cost(self):
        network = self.grid()
        mode = nax.TravelMode(na.GetTravelModes(network)["Driving"])
        self.assertEqual(mode.impedance, "Minutes")
        mode.impedance = "Length"
        mode.timeAttributeName = None
        result = self.route(network, travelMode=mode)
        names = result.fieldNames(nax.RouteOutputDataType.Routes)
        self.assertNotIn("Total_Minutes", names)                 # no time attribute in the mode
        (km,), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Kilometers"])
        self.assertAlmostEqual(km, (18 + 4 * NS) / 1000)

    def test_time_and_distance_units_scale_the_totals(self):
        network = self.grid()
        result = self.route(network, timeUnits=nax.TimeUnits.Seconds, distanceUnits=nax.DistanceUnits.Meters)
        (minutes, seconds, meters, km), = result.searchCursor(
            nax.RouteOutputDataType.Routes, ["Total_Minutes", "Total_Seconds", "Total_Meters", "Total_Kilometers"])
        self.assertAlmostEqual(minutes, 2.2)
        self.assertAlmostEqual(seconds, 132.0)
        self.assertAlmostEqual(meters, 18 + 4 * NS)
        self.assertAlmostEqual(km, (18 + 4 * NS) / 1000)
        result = self.route(network, timeUnits=nax.TimeUnits.Hours, distanceUnits=nax.DistanceUnits.Feet)
        (hours, feet), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Hours", "Total_Feet"])
        self.assertAlmostEqual(hours, 2.2 / 60)
        # D-323, measured in Pro: arcpy.nax's Feet and Yards are US survey units (1200/3937 m to the foot).
        self.assertAlmostEqual(feet, (18 + 4 * NS) / (1200 / 3937))
        result = self.route(network, timeUnits=nax.TimeUnits.Days, distanceUnits=nax.DistanceUnits.Yards)
        (days, yards), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Days", "Total_Yards"])
        self.assertAlmostEqual(days, 2.2 / 1440)
        self.assertAlmostEqual(yards, (18 + 4 * NS) / (3600 / 3937))
        # searchToleranceUnits: a stop 3 m from the road is lost at 2 m and found at 10 feet (3.048 m).
        for units, tolerance, located in ((nax.DistanceUnits.Meters, 2, False), (nax.DistanceUnits.Feet, 10, True)):
            route = nax.Route(network)
            route.travelMode = "Driving"
            route.searchTolerance, route.searchToleranceUnits = tolerance, units
            with route.insertCursor(nax.RouteInputDataType.Stops, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["a", (2, -3)])
                cursor.insertRow(["b", (20, 4)])
            status = [s for s, in route.solve().searchCursor(nax.RouteOutputDataType.Stops, ["Status"])]
            self.assertEqual(status[0] == 0, located)
        self.assertEqual({u.name for u in nax.DistanceUnits},
                         {"Meters", "Kilometers", "Feet", "Yards", "Miles", "NauticalMiles", "Inches", "Centimeters",
                          "Millimeters", "Decimeters"})
        self.assertEqual([u.name for u in nax.TimeUnits], ["Seconds", "Minutes", "Hours", "Days"])

    def test_route_shape_type_changes_the_geometry(self):
        network = self.grid()
        shapes = {}
        for kind in nax.RouteShapeType:
            result = self.route(network, routeShapeType=kind)
            (shapes[kind],), = result.searchCursor(nax.RouteOutputDataType.Routes, ["SHAPE@WKT"])
        # Pro 3.7 writes a polyline as MULTILINESTRING (measured 2026-11-08, Pro sweep); this said LINESTRING.
        self.assertEqual(shapes[nax.RouteShapeType.TrueShape], "MULTILINESTRING ((2 0, 10 0, 20 0, 20 4))")
        self.assertIn("2 0", shapes[nax.RouteShapeType.TrueShapeWithMeasures])
        # Pro 3.7 writes a polyline as MULTILINESTRING (measured 2026-11-08, Pro sweep); this said LINESTRING.
        self.assertEqual(shapes[nax.RouteShapeType.StraightLine], "MULTILINESTRING ((2 0, 20 4))")
        self.assertIsNone(shapes[nax.RouteShapeType.NoLine])

    def test_line_shape_type_changes_the_geometry(self):
        network = self.grid()
        # Pro 3.7 writes a polyline as MULTILINESTRING (measured 2026-11-08, Pro sweep); this said LINESTRING.
        self.assertEqual([m.name for m in nax.LineShapeType], ["NoLine", "StraightLine"])   # Pro's order (measured in ArcGIS Pro 3.7, 2026-11-12 (Pro sweep))
        for kind, wkt in ((nax.LineShapeType.StraightLine, "MULTILINESTRING ((14 0, 10 9))"), (nax.LineShapeType.NoLine, None)):
            od = nax.OriginDestinationCostMatrix(network)
            od.travelMode = "Driving"
            od.lineShapeType = kind
            with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Origins, ["SHAPE@XY"]) as cursor:
                cursor.insertRow([(14, 0)])
            with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Destinations, ["SHAPE@XY"]) as cursor:
                cursor.insertRow([(10, 9)])
            # Pro's OD lines hold Total_Time, in timeUnits (measured in ArcGIS Pro 3.7, 2026-11-12 (Pro sweep)); this read Total_Minutes.
            (shape, minutes), = od.solve().searchCursor(nax.OriginDestinationCostMatrixOutputDataType.Lines,
                                                        ["SHAPE@WKT", "Total_Time"])
            self.assertEqual(shape, wkt)
            self.assertAlmostEqual(minutes, 1.3)                   # the cost is the network's either way

    def test_travel_direction_changes_closest_facility_and_service_area(self):
        network = self.oneway()
        self.assertEqual({d.name for d in nax.TravelDirection}, {"FromFacility", "ToFacility"})
        totals = {}
        for direction in nax.TravelDirection:
            closest = nax.ClosestFacility(network)
            closest.travelMode = "Driving"
            closest.travelDirection = direction
            with closest.insertCursor(nax.ClosestFacilityInputDataType.Facilities, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["f", (0, 0)])
            with closest.insertCursor(nax.ClosestFacilityInputDataType.Incidents, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["i", (100, 0)])
            (totals[direction],), = closest.solve().searchCursor(nax.ClosestFacilityOutputDataType.Routes,
                                                                 ["Total_Minutes"])
        # From the facility the road is driven along its digitising (10 minutes), toward it against (30).
        self.assertAlmostEqual(totals[nax.TravelDirection.FromFacility], 10.0)
        self.assertAlmostEqual(totals[nax.TravelDirection.ToFacility], 30.0)
        reach = {}
        for direction in nax.TravelDirection:
            area = nax.ServiceArea(network)
            area.travelMode = "Driving"
            area.travelDirection = direction
            area.defaultImpedanceCutoffs = [15]
            area.polygonBufferDistance = 1
            with area.insertCursor(nax.ServiceAreaInputDataType.Facilities, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["f", (0, 0)])
            reach[direction] = area.solve().extent(nax.ServiceAreaOutputDataType.Polygons).XMax
        # 15 minutes: the whole 100 m away from the facility; 50 m of it toward (15 of 30 minutes).
        self.assertAlmostEqual(reach[nax.TravelDirection.FromFacility], 101.0, places=6)
        self.assertAlmostEqual(reach[nax.TravelDirection.ToFacility], 51.0, places=6)

    def test_service_area_output_and_polygon_enumerations(self):
        network = self.grid()

        def solve(**settings):
            area = nax.ServiceArea(network)
            area.travelMode = "Driving"
            area.defaultImpedanceCutoffs = [0.5, 1]
            for name, value in settings.items():
                setattr(area, name, value)
            with area.insertCursor(nax.ServiceAreaInputDataType.Facilities, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["C", (20, 0)])
            return area.solve()

        out = nax.ServiceAreaOutputDataType
        result = solve(outputType=nax.ServiceAreaOutputType.Lines)
        self.assertEqual(result.count(out.Polygons), 0)
        self.assertGreater(result.count(out.Lines), 0)
        self.assertAlmostEqual(sum(l for l, in result.searchCursor(out.Lines, ["SHAPE@LENGTH"])), 20.0)
        result = solve(outputType=nax.ServiceAreaOutputType.PolygonsAndLines)
        self.assertGreater(result.count(out.Polygons), 0)
        self.assertGreater(result.count(out.Lines), 0)
        rings = solve(geometryAtCutoff=nax.ServiceAreaPolygonCutoffGeometry.Rings)
        self.assertEqual(sorted(rings.searchCursor(out.Polygons, ["FromBreak", "ToBreak"])), [(0.0, 0.5), (0.5, 1.0)])
        disks = solve(geometryAtCutoff=nax.ServiceAreaPolygonCutoffGeometry.Disks)
        self.assertEqual(sorted(disks.searchCursor(out.Polygons, ["FromBreak", "ToBreak"])), [(0.0, 0.5), (0.0, 1.0)])
        # Overlap and Standard are what grapy builds; the other documented members are refused, not ignored.
        self.assertEqual({m.name for m in nax.ServiceAreaOverlapGeometry}, {"Overlap", "Dissolve", "Split"})
        self.assertEqual({m.name for m in nax.ServiceAreaPolygonDetail}, {"Standard", "Generalized", "High"})
        self.assertTrue(solve(geometryAtOverlap=nax.ServiceAreaOverlapGeometry.Overlap,
                              polygonDetail=nax.ServiceAreaPolygonDetail.Standard).solveSucceeded)
        for settings in ({"geometryAtOverlap": nax.ServiceAreaOverlapGeometry.Dissolve},
                         {"geometryAtOverlap": nax.ServiceAreaOverlapGeometry.Split},
                         {"polygonDetail": nax.ServiceAreaPolygonDetail.Generalized},
                         {"polygonDetail": nax.ServiceAreaPolygonDetail.High}):
            with self.assertRaises(arcpy.NotBuiltError):
                solve(**settings)

    def test_curb_approach_is_refused_where_it_is_not_built(self):
        # CurbApproach's members as Pro names and numbers them (measured in ArcGIS Pro 3.7, 2026-11-12 (Pro sweep)); they were EitherSideOfVehicle,
        # RightSideOfVehicle and LeftSideOfVehicle, with no Unknown.
        self.assertEqual([(m.name, m.value) for m in nax.CurbApproach],
                         [("Unknown", -1), ("EitherSide", 0), ("RightSide", 1), ("LeftSide", 2), ("NoUTurn", 3)])
        network = self.grid()
        for curb in (nax.CurbApproach.EitherSide, 0, None):
            route = nax.Route(network)
            with route.insertCursor(nax.RouteInputDataType.Stops, ["Name", "CurbApproach", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["a", curb, (2, 0)])
                cursor.insertRow(["b", curb, (20, 4)])
            self.assertTrue(route.solve().solveSucceeded)
        # grapy approaches every location from either side, so a side of the vehicle would answer the wrong route:
        # refused, as arcpy.na.Solve and the fleet solvers refuse it.
        for curb in (nax.CurbApproach.RightSide, 2, nax.CurbApproach.NoUTurn):
            route = nax.Route(network)
            with route.insertCursor(nax.RouteInputDataType.Stops, ["Name", "CurbApproach", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["a", curb, (2, 0)])
                cursor.insertRow(["b", 0, (20, 4)])
            with self.assertRaises(arcpy.NotBuiltError):
                route.solve()
        # The OD and closest facility pages: NoUTurn there is the same as either side, as arcpy.na.Solve takes it.
        od = nax.OriginDestinationCostMatrix(network)
        with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Origins, ["CurbApproach", "SHAPE@XY"]) as c:
            c.insertRow([nax.CurbApproach.NoUTurn, (14, 0)])
        with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Destinations, ["SHAPE@XY"]) as c:
            c.insertRow([(10, 9)])
        self.assertTrue(od.solve().solveSucceeded)
        od = nax.OriginDestinationCostMatrix(network)
        with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Origins, ["CurbApproach", "SHAPE@XY"]) as c:
            c.insertRow([nax.CurbApproach.LeftSide, (14, 0)])
        with od.insertCursor(nax.OriginDestinationCostMatrixInputDataType.Destinations, ["SHAPE@XY"]) as c:
            c.insertRow([(10, 9)])
        with self.assertRaises(arcpy.NotBuiltError):
            od.solve()

    def test_na_class_field_mappings_load_a_solver_input(self):
        network = self.grid()
        closest = nax.ClosestFacility(network)
        closest.travelMode = "Driving"
        with closest.insertCursor(nax.ClosestFacilityInputDataType.Facilities, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["near", (10, 9)])
            cursor.insertRow(["far", (0, 0.5)])
        incidents = self.points("i", [("x", "POINT(14 0)", "call")], [("Caller", "TEXT")])
        mappings = closest.fieldMappings(nax.ClosestFacilityInputDataType.Incidents, False, arcpy.ListFields(incidents))
        self.assertIsInstance(mappings, nax.NAClassFieldMappings)
        self.assertIsInstance(mappings["Name"], nax.NAClassFieldMap)
        self.assertIs(nax.NAClassFieldMap, na.NAClassFieldMap)
        self.assertEqual(mappings["Name"].mappedFieldName, "Name")
        mappings["Name"].mappedFieldName = "Caller"
        mappings["TargetFacilityCount"].defaultValue = 2
        closest.load(nax.ClosestFacilityInputDataType.Incidents, incidents, mappings)
        rows = list(closest.solve().searchCursor(nax.ClosestFacilityOutputDataType.Routes, ["Name", "FacilityRank"]))
        self.assertEqual(rows, [("call - near", 1), ("call - far", 2)])

    def test_waste_collection_stop_collection_mode_and_driving_side(self):
        self.assertEqual([(m.name, m.value) for m in nax.StopCollectionMode], [("OneSide", 1), ("BothSides", 2)])
        self.assertEqual({m.name for m in nax.DrivingSide}, {"Unknown", "LeftSide", "RightSide"})
        network = self.line()

        def activity(mode, side=None):
            if side is not None:
                arcpy.graticula.SetNetworkDrivingSide(network, side)
            wc = nax.WasteCollection(network)
            wc.travelMode = "Driving"
            wc.routeStartTime = datetime.time(6)
            wc.maxRouteTotalTime = 120
            wc.stopCollectionMode = mode
            I = nax.WasteCollectionInputDataType
            with wc.insertCursor(I.Depots, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["Yard", (0, 0)])
            with wc.insertCursor(I.Stops, ["Name", "Bins_1", "ServiceTime", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["bin", 1, 0.5, (50, 2)])     # left of the road's digitised direction
            with wc.insertCursor(I.Routes, ["Name", "StartDepotName", "EndDepotName", "MaxBins_1"]) as cursor:
                cursor.insertRow(["T1", "Yard", "Yard", 3])
            result = wc.solve()
            return {a for a, in result.searchCursor(nax.WasteCollectionOutputDataType.RouteLines, ["Activity"])} - {0}

        # The RouteLines Activity codes: 1 both sides, 2 the left side, 3 the right side of travel. Both sides at once
        # need no particular direction.
        self.assertIn(1, activity(nax.StopCollectionMode.BothSides))
        # One side: the bin is collected from the curb — the right of travel where traffic keeps right (driving
        # west, against the digitising), the left where it keeps left (driving east).
        self.assertIn(3, activity(nax.StopCollectionMode.OneSide, nax.DrivingSide.RightSide))
        self.assertNotIn(2, activity(nax.StopCollectionMode.OneSide, nax.DrivingSide.RightSide))
        self.assertIn(2, activity(nax.StopCollectionMode.OneSide, nax.DrivingSide.LeftSide))
        self.assertNotIn(3, activity(nax.StopCollectionMode.OneSide, nax.DrivingSide.LeftSide))

    def test_location_allocation_decay_function_type(self):
        self.assertEqual([m.name for m in nax.DecayFunctionType], ["Linear", "Power", "Exponential"])
        network = self.grid()

        def solve(decay):
            la = nax.LocationAllocation(network)
            la.travelMode = "Driving"
            la.decayFunctionType = decay
            with la.insertCursor(nax.LocationAllocationInputDataType.Facilities, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["A", (2, 0)])
            with la.insertCursor(nax.LocationAllocationInputDataType.DemandPoints, ["Name", "SHAPE@XY"]) as cursor:
                cursor.insertRow(["d", (0, 0)])
            return la.solve()

        self.assertEqual(nax.LocationAllocation(network).decayFunctionType, nax.DecayFunctionType.Linear)
        (minutes,), = solve(nax.DecayFunctionType.Linear).searchCursor(nax.LocationAllocationOutputDataType.Lines,
                                                                      ["Total_Minutes"])
        self.assertAlmostEqual(minutes, 0.2)
        for decay in (nax.DecayFunctionType.Power, nax.DecayFunctionType.Exponential):
            with self.assertRaises(arcpy.NotBuiltError):
                solve(decay)

    def test_time_zone_time_of_day_and_message_severity_enumerations(self):
        # The members the pages list. INFERRED: grapy refuses a time of day (traffic, ADR-226 §5.3), so neither
        # TimeZoneUsage nor TimeOfDayUsage can change a grapy solve; the defaults are the documented ones.
        self.assertEqual({m.name for m in nax.TimeZoneUsage}, {"LocalTimeAtLocations", "UTC"})
        # Pro's members (measured in ArcGIS Pro 3.7, 2026-11-12 (Pro sweep)); these said StartTime and EndTime, and no Info.
        self.assertEqual({m.name for m in nax.TimeOfDayUsage}, {"DepartureTime", "ArrivalTime"})
        self.assertEqual({m.name for m in nax.MessageSeverity}, {"All", "Warning", "Error", "Info"})
        network = self.grid()
        route = nax.Route(network)
        self.assertIs(route.timeZone, nax.TimeZoneUsage.LocalTimeAtLocations)
        route.timeOfDay = datetime.datetime(2026, 1, 5, 8)
        with self.assertRaises(arcpy.NotBuiltError):
            route.solve()
        result = self.route(network)
        for severity in nax.MessageSeverity:
            self.assertIsInstance(result.solverMessages(severity), list)

    def test_attribute_parameters_and_network_elements_in_a_custom_evaluator(self):
        network = self.grid()
        arcpy.graticula.AddNetworkAttributeParameter(network, "Minutes", "Factor", 3.0, "General")
        arcpy.graticula.AddNetworkAttributeParameter(network, "Minutes", "Closed", False,
                                                     nax.AttributeParameterUsage.Restriction.name)
        seen = {}

        class Scaled(nax.AttributeEvaluator):
            def refresh(self):
                found = self.networkQuery.attributeParameters(self.attribute)
                seen["parameters"] = found
                self.factor = next(p.value for p in found if p.name == "Factor")

            def edgeValue(self, edge):
                seen.setdefault("elements", []).append(edge)
                return self.networkQuery.attributeValue(edge, self.attribute) * self.factor

        dataset = nax.NetworkDataset(network)
        dataset.customEvaluators = [Scaled("Minutes")]
        result = self.route(dataset)
        (minutes,), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes"])
        self.assertAlmostEqual(minutes, 2.2 * 3)
        parameters = {p.name: p for p in seen["parameters"]}
        self.assertTrue(all(isinstance(p, nax.AttributeParameter) for p in parameters.values()))
        self.assertEqual((parameters["Factor"].usage, parameters["Factor"].value),
                         (nax.AttributeParameterUsage.General, 3.0))
        self.assertIs(parameters["Closed"].usage, nax.AttributeParameterUsage.Restriction)
        self.assertEqual([(m.name, m.value) for m in nax.AttributeParameterUsage], [("General", 0), ("Restriction", 1)])
        # A travel mode's parameter value replaces the default for its solves.
        mode = na.GetTravelModes(network)["Driving"]
        mode.attributeParameters = {("Minutes", "Factor"): 0.5}
        result = self.route(dataset, travelMode=mode)
        (minutes,), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes"])
        self.assertAlmostEqual(minutes, 1.1)
        # The elements an edge evaluator is given: NetworkElement's edges, one per direction of travel.
        elements = seen["elements"]
        self.assertTrue(elements and all(isinstance(e, nax.NetworkElement) for e in elements))
        self.assertTrue(all(e.isEdge and not e.isJunction and not e.isTurn for e in elements))
        self.assertEqual({e.direction for e in elements}, {True, False})
        self.assertEqual(nax.Edge(1, True), nax.Edge(1, True))
        self.assertNotEqual(nax.Edge(1, True), nax.Junction(1))
        self.assertTrue(nax.Junction(4).isJunction and nax.Turn(1).isTurn)


if __name__ == "__main__":
    unittest.main()
