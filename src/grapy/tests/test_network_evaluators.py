"""Custom evaluators, traversed elements and solver properties on grapy's network datasets (ADR-246).

The network is test_turns' (ADR-239):

                 N(10,20) ──────── (20,20)          EN: E(20,10)–(20,20)–N(10,20), 20 long
                    │                  │
    W(0,10) ───── C(10,10) ───────── E(20,10)       WC 10, CE 10, CN 10
                    │
                  S(10,5)                           CS: C–S, 5 long, a dead end

Every road takes one minute per 10 m, so W to N is two minutes through C, and four round by E. The answers below are
worked by hand from those numbers.
"""
import datetime
import os
import sys
import tempfile
import textwrap
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.na as na  # noqa: E402
import grapy.nax as nax  # noqa: E402

ROADS = [("WC", "LINESTRING(0 10,10 10)"), ("CE", "LINESTRING(10 10,20 10)"), ("CN", "LINESTRING(10 10,10 20)"),
         ("CS", "LINESTRING(10 10,10 5)"), ("EN", "LINESTRING(20 10,20 20,10 20)")]
OID = {name: n for n, (name, _) in enumerate(ROADS, start=1)}
W, N, E = (0, 10), (10, 20), (20, 10)


class Doubler(nax.AttributeEvaluator):
    """The custom evaluators topic's sample: every edge costs twice its base value."""

    def __init__(self, attribute_name, source_names=None):
        super().__init__(attribute_name, source_names)
        self.refreshed = 0

    def refresh(self):
        self.refreshed += 1

    def edgeValue(self, edge):
        return self.networkQuery.attributeValue(edge, self.attribute) * 2


class Network(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "transport", 3857)[0]
        self.roads = arcpy.management.CreateFeatureclass(self.fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(self.roads, "minutes", "DOUBLE")
        import shapely
        with arcpy.da.InsertCursor(self.roads, ["SHAPE@", "minutes"]) as written:
            for _, wkt in ROADS:
                written.insertRow((wkt, shapely.from_wkt(wkt).length / 10))
        self.turns = na.CreateTurnFeatureClass(self.fds, "turns", 3)[0]
        arcpy.management.AddField(self.turns, "delay", "DOUBLE")
        self.network = na.CreateNetworkDataset(self.fds, "streets", ["roads", "turns"], "NO_ELEVATION")[0]
        arcpy.graticula.AddNetworkAttribute(self.network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes"), ("turns", "BOTH", "FIELD", "delay")])
        arcpy.graticula.AddNetworkAttribute(self.network, "Closed", "Restriction", None,
                                            [("roads", "BOTH", "CONSTANT", "false")])
        arcpy.graticula.AddNetworkTravelMode(self.network, "Driving", "Minutes", ["Closed"], "Minutes", "Length")
        na.BuildNetwork(self.network)

    def turn(self, edges, end, delay=None):
        fields = ["Edge1End"] + [f"Edge{k}{p}" for k in range(1, len(edges) + 1) for p in ("FCID", "FID", "Pos")]
        row = [end] + [v for name in edges for v in (1, OID[name], 0.5)]
        with arcpy.da.InsertCursor(self.turns, [*fields, "delay"]) as written:
            written.insertRow([*row, delay])
        na.BuildNetwork(self.network)

    def route(self, network=None, start=W, end=N, mode="Driving", **settings):
        route = nax.Route(network or self.network)
        route.travelMode = mode
        for name, value in settings.items():
            setattr(route, name, value)
        with route.insertCursor(nax.RouteInputDataType.Stops, ["Name", "SHAPE@XY"]) as cursor:
            cursor.insertRow(["a", start])
            cursor.insertRow(["b", end])
        return route.solve()

    def minutes(self, result):
        if not result.solveSucceeded:
            return None
        (total,), = result.searchCursor(nax.RouteOutputDataType.Routes, ["Total_Minutes"])
        return round(total, 9)

    def points(self, name, rows):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(path, "Name", "TEXT")
        with arcpy.da.InsertCursor(path, ["Name", "SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    # ---- custom evaluators

    def test_a_temporary_evaluator_doubles_the_edges_as_the_topic_samples_it(self):
        self.assertEqual(self.minutes(self.route()), 2.0)
        network = nax.NetworkDataset(self.network)
        doubler = Doubler("Minutes")
        network.customEvaluators = [doubler]
        self.assertEqual(network.customEvaluators, [doubler])
        self.assertEqual(self.minutes(self.route(network)), 4.0)
        self.assertTrue(doubler.attached)
        self.assertEqual(doubler.attribute.name, "Minutes")
        self.assertEqual(doubler.refreshed, 1)                       # refresh runs once, before the solve
        self.assertEqual(self.minutes(self.route(network)), 4.0)
        self.assertEqual(doubler.refreshed, 2)
        # The evaluator is the script's: the network itself does not change.
        self.assertEqual(self.minutes(self.route()), 2.0)
        # An analysis layer made from the NetworkDataset object takes its evaluators too.
        layer = na.MakeRouteAnalysisLayer(network, "doubled", "Driving", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", self.points("stops", [("a", "POINT(0 10)"), ("b", "POINT(10 20)")]))
        na.Solve(layer)
        (total,), = arcpy.da.SearchCursor(na.GetNASublayer(layer, "Routes"), ["Total_Minutes"])
        self.assertAlmostEqual(total, 4.0)
        # Limited to a source the network does not route on, it changes nothing.
        network.customEvaluators = [Doubler("Minutes", ["no_such_source"])]
        self.assertEqual(self.minutes(self.route(network)), 2.0)
        # An attribute the network does not have: not attached, and no other method runs.
        missing = Doubler("Nothing")
        network.customEvaluators = [missing]
        self.assertEqual(self.minutes(self.route(network)), 2.0)
        self.assertFalse(missing.attached)
        self.assertEqual(missing.refreshed, 0)

        class Declines(Doubler):
            def attach(self, network_query):
                return network_query.attribute("Seconds") is not None       # the network has none

        declines = Declines("Minutes")
        network.customEvaluators = [declines]
        self.assertEqual(self.minutes(self.route(network)), 2.0)
        self.assertFalse(declines.attached)
        with self.assertRaises(TypeError):
            network.customEvaluators = ["Minutes"]
        network.customEvaluators = [Doubler("Minutes"), Doubler("Minutes")]
        with self.assertRaises(ValueError):                           # one evaluator per attribute
            self.route(network)

    def test_restriction_junction_and_turn_values(self):
        network = nax.NetworkDataset(self.network)

        class CloseCN(nax.AttributeEvaluator):
            def edgeValue(self, edge):
                return self.networkQuery.sourceInfo(edge)[1] == OID["CN"]

        network.customEvaluators = [CloseCN("Closed")]
        self.assertEqual(self.minutes(self.route(network)), 4.0)        # round by E

        class SlowC(nax.AttributeEvaluator):
            def junctionValue(self, junction):
                x, y = self.where[junction.eid]
                return 0.5 if (x, y) == (10, 10) else None

        slow = SlowC("Minutes")
        graph = __import__("grapy._network", fromlist=["Graph"]).Graph(self.network)
        slow.where = graph.junctions
        network.customEvaluators = [slow]
        self.assertEqual(self.minutes(self.route(network)), 2.5)        # through C, half a minute there

        class CloseC(nax.AttributeEvaluator):
            def junctionValue(self, junction):
                return slow.where[junction.eid] == (10, 10)

        network.customEvaluators = [CloseC("Closed")]
        self.assertIsNone(self.minutes(self.route(network)))            # every way from W passes C

        # A turn feature from WC into CN, its delay set by the evaluator: three minutes makes E's way cheaper.
        self.turn(["WC", "CN"], "Y", 0.0)
        self.assertEqual(self.minutes(self.route()), 2.0)

        class SlowTurns(nax.AttributeEvaluator):
            def turnValue(self, turn):
                return self.networkQuery.attributeValue(turn, self.attribute) + 3.0

        network.customEvaluators = [SlowTurns("Minutes")]
        # Five minutes through the turn; three out to the dead end at S, a U-turn there and back up CN.
        self.assertEqual(self.minutes(self.route(network)), 3.0)

    def test_the_network_query_answers_the_documented_methods(self):
        self.turn(["WC", "CN"], "Y", 0.5)
        seen = {}

        class Probe(nax.AttributeEvaluator):
            def attach(self, query):
                seen["query"] = query
                return True

            def edgeValue(self, edge):
                return None                                           # keeps the base value

        network = nax.NetworkDataset(self.network)
        network.customEvaluators = [Probe("Minutes")]
        self.assertEqual(self.minutes(self.route(network)), 2.5)        # the turn's delay, the base values kept
        q = seen["query"]
        self.assertEqual(q.networkName, "streets")
        self.assertEqual(list(q.sourceNames), ["roads", "streets_Junctions", "turns"])
        self.assertIsNone(q.attribute("Seconds"))
        minutes = q.attribute("Minutes")
        self.assertIsInstance(minutes, nax.Attribute)
        self.assertEqual(q.attributeUsage(minutes), nax.AttributeUsage.Cost)
        self.assertEqual(q.attributeUsage(q.attribute("Closed")), nax.AttributeUsage.Restriction)
        # Edges are numbered as built: WC 1, CE 2, CN 3, CS 4, EN 5; junctions W 1, C 2 ….
        wc = nax.Edge(1, True)
        self.assertTrue(wc.isEdge and not wc.isJunction and not wc.isTurn)
        c = q.toJunction(wc)
        self.assertTrue(c.isJunction)
        self.assertEqual(q.fromJunction(nax.Edge(1, False)), c)
        self.assertEqual(q.edgePositions(wc), (0.0, 1.0))
        self.assertEqual(q.edgeAzimuths(wc), (90.0, 90.0))               # due east
        self.assertEqual(q.edgeAzimuths(nax.Edge(5, False)), (90.0, 180.0))   # from N east, then south to E
        self.assertEqual(q.sourceInfo(wc), (1, OID["WC"]))
        self.assertEqual(q.sourceInfo(c), (2, c.eid))
        self.assertEqual(len(q.edges(c)), 4)                            # WC, CE, CN and CS meet at C
        self.assertTrue(all(isinstance(e, nax.AdjacentEdge) for e in q.edges(c)))
        (turn,) = q.turns(c)
        self.assertTrue(turn.isTurn)
        self.assertEqual(q.atJunction(turn), c)
        self.assertEqual(q.edges(turn), [nax.Edge(1, True), nax.Edge(3, True)])
        self.assertEqual(q.sourceInfo(turn), (3, 1))
        self.assertEqual(q.attributeValue(wc, minutes), 1.0)
        self.assertEqual(q.attributeValue(turn, minutes), 0.5)
        self.assertEqual(q.attributeValue(c, minutes, nax.NetworkTimeUsage.Unused), 0.0)
        self.assertFalse(q.attributeValue(wc, q.attribute("Closed")))

    def test_a_persisted_evaluator_and_attribute_parameters(self):
        folder = tempfile.mkdtemp()
        with open(os.path.join(folder, "grapy_test_customization.py"), "w", encoding="utf-8") as written:
            written.write(textwrap.dedent('''
                import grapy as arcpy

                class Scaled(arcpy.nax.AttributeEvaluator):
                    def attach(self, network_query):
                        self.factor = 1.0
                        return True

                    def refresh(self):
                        found = [p for p in self.networkQuery.attributeParameters(self.attribute) if p.name == "Factor"]
                        self.factor = found[0].value if found else 1.0

                    def edgeValue(self, edge):
                        return self.networkQuery.attributeValue(edge, self.attribute) * self.factor
            '''))
        sys.path.insert(0, folder)
        try:
            arcpy.graticula.AddNetworkAttributeParameter(self.network, "Minutes", "Factor", 3.0)
            nax.NetworkDataset(self.network).updateNetworkDatasetSchema(
                custom_evaluators={"Minutes": {"class": "grapy_test_customization.Scaled"}})
            self.assertEqual(self.minutes(self.route()), 6.0)            # every solve on the network uses it
            # A travel mode's parameter value.
            mode = arcpy.na.GetTravelModes(self.network)["Driving"]
            mode.attributeParameters = {("Minutes", "Factor"): 0.5}
            self.assertEqual(self.minutes(self.route(mode=mode)), 1.0)
            # An analysis layer's: UpdateAnalysisLayerAttributeParameter and the solver properties.
            layer = na.MakeRouteAnalysisLayer(self.network, "r", "Driving",
                                              generate_directions_on_solve="NO_DIRECTIONS")[0]
            na.AddLocations(layer, "Stops", self.points("stops", [("a", "POINT(0 10)"), ("b", "POINT(10 20)")]))
            na.UpdateAnalysisLayerAttributeParameter(layer, "Minutes", "Factor", 2.0)
            self.assertEqual(na.GetSolverProperties(layer).attributeParameters, {("Minutes", "Factor"): 2.0})
            na.Solve(layer)
            (total,), = arcpy.da.SearchCursor(na.GetNASublayer(layer, "Routes"), ["Total_Minutes"])
            self.assertAlmostEqual(total, 4.0)
            with self.assertRaises(arcpy.ExecuteError):
                na.UpdateAnalysisLayerAttributeParameter(layer, "Minutes", "Nothing", 1)
            # A class that does not load: the solve goes on without it, with a warning.
            nax.NetworkDataset(self.network).updateNetworkDatasetSchema(
                custom_evaluators={"Minutes": {"class": "grapy_test_customization.Missing"}})
            self.assertEqual(self.minutes(self.route()), 2.0)
            self.assertIn("could not be loaded", arcpy.GetMessages())
        finally:
            sys.path.remove(folder)
            sys.modules.pop("grapy_test_customization", None)

    # ---- traversed elements

    def test_a_route_keeps_its_edges_junctions_and_turns(self):
        self.turn(["WC", "CN"], "Y", 0.5)
        result = self.route()
        self.assertEqual(self.minutes(result), 2.5)
        edges = list(result.searchCursor(nax.RouteOutputDataType.RouteEdges,
                                         ["SourceName", "SourceOID", "RouteEdgeID", "FromPosition", "ToPosition",
                                          "FromJunctionID", "ToJunctionID", "RouteID", "Attr_Minutes",
                                          "Cumul_Minutes", "Attr_Kilometers"]))
        self.assertEqual(edges, [("roads", OID["WC"], 1, 0.0, 1.0, 1, 2, 1, 1.0, 1.0, 0.01),
                                 ("roads", OID["CN"], 3, 0.0, 1.0, 2, 3, 1, 1.0, 2.5, 0.01)])
        junctions = list(result.searchCursor(nax.RouteOutputDataType.RouteJunctions,
                                             ["ObjectID", "SourceName", "SourceOID", "RouteJunctionID", "Cumul_Minutes"]))
        # The first stop, C, N and the last stop; the stops by their ObjectIDs.
        self.assertEqual([j[1] for j in junctions], ["Stops", "streets_Junctions", "streets_Junctions", "Stops"])
        self.assertEqual([j[3] for j in junctions][::3], [1, 2])
        self.assertEqual([j[4] for j in junctions], [0.0, 1.0, 2.5, 2.5])
        turns = list(result.searchCursor(nax.RouteOutputDataType.RouteTurns,
                                         ["SourceName", "SourceOID", "RouteTurnID", "FromEdgeID", "ToEdgeID",
                                          "Attr_Minutes", "Cumul_Minutes"]))
        self.assertEqual(turns, [("turns", 1, 1, 1, 2, 0.5, 1.5)])
        self.assertIn("Attr_Closed", result.fieldNames(nax.RouteOutputDataType.RouteEdges))
        # Not asked for: not kept.
        quiet = self.route(returnRouteEdges=False, returnRouteJunctions=False, returnRouteTurns=False)
        self.assertEqual(quiet.count(nax.RouteOutputDataType.RouteEdges), 0)
        # The Traversed* objects, in travel order.
        found = arcpy.graticula.TraversedElements(result)
        self.assertEqual([type(e).__name__ for e in found],
                         ["TraversedJunction", "TraversedEdge", "TraversedJunction", "TraversedTurn", "TraversedEdge",
                          "TraversedJunction", "TraversedJunction"])
        self.assertEqual([e.type for e in found][:2], [nax.TraversedElementType.Stop, nax.TraversedElementType.Edge])
        self.assertEqual([e.accumulatedImpedance for e in found], [0.0, 1.0, 1.0, 1.5, 2.5, 2.5, 2.5])
        self.assertAlmostEqual(found[4].accumulatedLength, 20.0)
        self.assertEqual(found[1].element, nax.Edge(1, True))
        self.assertEqual((found[1].sourceID, found[1].sourceObjectID, found[1].fromPosition, found[1].toPostition),
                         (1, OID["WC"], 0.0, 1.0))
        self.assertEqual(found[1].immediateAzimuths, (90.0, 90.0))
        self.assertEqual(found[3].element, nax.Turn(1))
        self.assertEqual(found[1].getName(fallback="unnamed"), "unnamed")
        self.assertTrue(isinstance(found[2], nax.TraversedElement))

    def test_a_global_turn_is_an_implicit_turn(self):
        arcpy.graticula.AddNetworkAttribute(self.network, "Minutes", "Cost", "Minutes",
                                            [("roads", "BOTH", "FIELD", "minutes"),
                                             ("GLOBAL_TURNS", "BOTH", "TURN_CATEGORY", {"left": 30})])
        na.BuildNetwork(self.network)
        result = self.route()
        self.assertEqual(self.minutes(result), 2.5)                     # WC to CN is a left turn: 30 seconds
        turns = list(result.searchCursor(nax.RouteOutputDataType.RouteTurns,
                                         ["SourceName", "SourceOID", "RouteTurnID", "Attr_Minutes"]))
        self.assertEqual(turns, [("ImplicitTurns", -1, -1, 0.5)])

    def test_copy_traversed_source_features(self):
        layer = na.MakeRouteAnalysisLayer(self.network, "r", "Driving", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", self.points("stops", [("a", "POINT(0 10)"), ("b", "POINT(20 20)")]))
        out = arcpy.management.CreateFileGDB(self.directory, "out")[0]
        result = na.CopyTraversedSourceFeatures(layer, out, "Edges", "Junctions", "Turns")  # solved first
        edges, junctions, turns = result[0], result[1], result[2]
        names = [f.name for f in arcpy.ListFields(edges)]
        for field in ("SourceName", "SourceOID", "SourceType", "EID", "FromPosition", "ToPosition", "FromJunctionID",
                      "ToJunctionID", "RouteID", "Attr_Minutes", "Cumul_Minutes", "Attr_Length", "Attr_Closed"):
            self.assertIn(field, names)
        # W to (20,20) the shortest way: WC, CN, then N along EN against its digitised direction (3 minutes).
        rows = list(arcpy.da.SearchCursor(edges, ["SourceOID", "EID", "FromPosition", "ToPosition", "Cumul_Minutes",
                                                  "SourceType", "RouteID"]))
        self.assertEqual([r[:2] for r in rows], [(OID["WC"], 1), (OID["CN"], 3), (OID["EN"], 5)])
        self.assertEqual((rows[2][2], rows[2][3]), (1.0, 0.5))
        self.assertEqual([r[4] for r in rows], [1.0, 2.0, 3.0])
        self.assertEqual({r[5] for r in rows} | {r[6] for r in rows}, {"NETWORK", 1})
        kinds = [r for r in arcpy.da.SearchCursor(junctions, ["SourceName", "SourceType", "EID"])]
        self.assertEqual([k[1] for k in kinds], ["NA_CLASS", "NETWORK", "NETWORK", "NA_CLASS"])
        self.assertEqual([k[2] for k in kinds][::3], [-1, -1])
        self.assertEqual(len(list(arcpy.da.SearchCursor(turns, ["OID@"]))), 0)
        # A service area keeps its lines and the junctions they end at.
        area = na.MakeServiceAreaAnalysisLayer(self.network, "sa", "Driving", cutoffs=[1.5])[0]
        na.AddLocations(area, "Facilities", self.points("f", [("C", "POINT(10 10)")]))
        result = na.CopyTraversedSourceFeatures(area, out, "SAEdges", "SAJunctions", "SATurns")
        rows = list(arcpy.da.SearchCursor(result[0], ["FacilityID", "Cumul_Minutes"]))
        self.assertTrue(rows and all(r[0] == 1 for r in rows))
        self.assertAlmostEqual(max(r[1] for r in rows), 1.5)
        od = na.MakeODCostMatrixAnalysisLayer(self.network, "od", "Driving")[0]
        with self.assertRaises(arcpy.ExecuteError):
            na.CopyTraversedSourceFeatures(od, out, "a", "b", "c")      # not supported, as documented

    # ---- solver properties

    def test_solver_properties_read_and_write_the_layer(self):
        layer = na.MakeRouteAnalysisLayer(self.network, "r", "Driving", generate_directions_on_solve="NO_DIRECTIONS")[0]
        na.AddLocations(layer, "Stops", self.points("stops", [("a", "POINT(0 10)"), ("b", "POINT(10 20)")]))
        props = na.GetSolverProperties(layer)
        self.assertIsInstance(props, na.RouteSolverProperties)
        self.assertEqual(props.solverName, "Route Solver")
        self.assertEqual((props.impedance, props.restrictions, props.uTurns), ("Minutes", ["Closed"], "ALLOW_UTURNS"))
        self.assertEqual((props.findBestSequence, props.outputPathShape, props.useHierarchy),
                         ("USE_INPUT_ORDER", "TRUE_LINES_WITH_MEASURES", "NO_HIERARCHY"))
        self.assertIsNone(props.attributeParameters)                   # the network has no parameters
        props.impedance = "Length"
        props.accumulators = ["Minutes"]
        props.outputPathShape = "STRAIGHT_LINES"
        na.Solve(layer)
        (total, minutes, shape), = arcpy.da.SearchCursor(na.GetNASublayer(layer, "Routes"),
                                                         ["Total_Length", "Total_Minutes", "SHAPE@"])
        self.assertAlmostEqual(total, 20.0)
        self.assertAlmostEqual(minutes, 2.0)
        self.assertEqual(shape.pointCount, 2)                         # a straight line
        props.findBestSequence = True
        self.assertEqual((props.findBestSequence, props.orderingType), ("FIND_BEST_ORDER", "PRESERVE_BOTH"))
        props.orderingType = "PRESERVE_NONE"
        self.assertEqual(layer.settings["sequence"], "FIND_BEST_ORDER")
        props.applyTravelMode(na.GetTravelModes(self.network)["Driving"])
        self.assertEqual(props.impedance, "Minutes")
        with self.assertRaises(arcpy.NotBuiltError):
            props.timeOfDay = datetime.datetime(1900, 1, 1, 8)
        with self.assertRaises(arcpy.NotBuiltError):
            props.useHierarchy = True
        with self.assertRaises(ValueError):
            props.impedance = "Closed"
        with self.assertRaises(TypeError):
            props.applyTravelMode(3)
        # The other kinds.
        cf = na.MakeClosestFacilityAnalysisLayer(self.network, "cf", "Driving")[0]
        p = na.GetSolverProperties(cf)
        self.assertEqual((p.travelDirection, p.defaultTargetFacilityCount, p.defaultCutoff), ("TRAVEL_TO", 1, None))
        p.travelDirection = "TRAVEL_FROM"
        p.defaultCutoff = 5
        self.assertEqual((cf.settings["direction"], cf.settings["cutoff"]), ("FROM_FACILITIES", 5.0))
        sa = na.MakeServiceAreaAnalysisLayer(self.network, "sa", "Driving", cutoffs=[1, 2])[0]
        p = na.GetSolverProperties(sa)
        self.assertEqual((p.defaultBreaks, p.polygonNesting, p.lineType, p.polygonType),
                         ([1.0, 2.0], "RINGS", "NO_LINES", "DETAILED_POLYS"))
        p.lineType = "TRUE_LINES"
        p.polygonNesting = "DISKS"
        self.assertEqual((sa.settings["output"], sa.settings["rings"]), ("POLYGONS_AND_LINES", False))
        with self.assertRaises(arcpy.NotBuiltError):
            p.polygonType = "SIMPLE_POLYS"
        od = na.MakeODCostMatrixAnalysisLayer(self.network, "od", "Driving")[0]
        p = na.GetSolverProperties(od)
        p.defaultTargetDestinationCount = 2
        self.assertEqual((p.outputPathShape, od.settings["count"]), ("STRAIGHT_LINES", 2))
        la = na.MakeLocationAllocationAnalysisLayer(self.network, "la", "Driving")[0]
        p = na.GetSolverProperties(la)
        self.assertEqual((p.problemType, p.travelDirection, p.facilitiesToFind), ("MINIMIZE_IMPEDANCE",
                                                                                 "FACILITY_TO_DEMAND", 1))
        p.travelDirection = "DEMAND_TO_FACILITY"
        p.facilitiesToFind = 2
        self.assertEqual((la.settings["direction"], la.settings["count"]), ("TO_FACILITIES", 2))
        with self.assertRaises(arcpy.NotBuiltError):
            p.problemType = "MAXIMIZE_COVERAGE"
        vrp = na.MakeVehicleRoutingProblemAnalysisLayer(self.network, "v", "Driving",
                                                        generate_directions_on_solve="NO_DIRECTIONS")[0]
        p = na.GetSolverProperties(vrp)
        self.assertEqual((p.solverName, p.timeAttribute, p.timeWindowViolationImportance), (
            "Vehicle Routing Problem Solver", "Minutes", "Medium"))
        p.timeWindowViolationImportance = "High"
        p.distanceFieldUnits = "kilometers"
        self.assertEqual((vrp.settings["time_window_factor"], vrp.settings["distance_units"]), ("High", "Kilometers"))
        with self.assertRaises(ValueError):
            p.timeAttribute = "Length"                                  # not a time attribute
        lmd = na.MakeLastMileDeliveryAnalysisLayer(self.network, "lmd", "Driving")[0]
        with self.assertRaises(arcpy.ExecuteError):
            na.GetSolverProperties(lmd)                                 # not supported, as documented


class FleetTraversal(unittest.TestCase):
    """One road from (0,0) to (100,0), ten minutes end to end; a depot at its middle, orders at 10 and 90."""

    def test_a_vehicle_routing_layer_keeps_its_legs(self):
        directory = tempfile.mkdtemp()
        arcpy.env.workspace = directory
        gpkg = arcpy.management.CreateFileGDB(directory, "city")[0]
        fds = arcpy.management.CreateFeatureDataset(gpkg, "t", 3857)[0]
        roads = arcpy.management.CreateFeatureclass(fds, "roads", "POLYLINE")[0]
        arcpy.management.AddField(roads, "minutes", "DOUBLE")
        with arcpy.da.InsertCursor(roads, ["SHAPE@", "minutes"]) as written:
            written.insertRow(("LINESTRING(0 0,100 0)", 10.0))
        network = na.CreateNetworkDataset(fds, "net", ["roads"], "NO_ELEVATION")[0]
        arcpy.graticula.AddNetworkAttribute(network, "Minutes", "Cost", "Minutes", [("roads", "BOTH", "FIELD", "minutes")])
        arcpy.graticula.AddNetworkTravelMode(network, "Driving", "Minutes", [], "Minutes", "Length")
        na.BuildNetwork(network)
        layer = na.MakeVehicleRoutingProblemAnalysisLayer(network, "vrp", "Driving", default_date=datetime.date(2026, 1, 5),
                                                          generate_directions_on_solve="NO_DIRECTIONS")[0]

        def points(name, rows):
            path = arcpy.management.CreateFeatureclass(directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
            arcpy.management.AddField(path, "Name", "TEXT")
            with arcpy.da.InsertCursor(path, ["Name", "SHAPE@"]) as written:
                for row in rows:
                    written.insertRow(row)
            return path

        na.AddLocations(layer, "Depots", points("depots", [("D", "POINT(50 0)")]))
        na.AddLocations(layer, "Orders", points("orders", [("a", "POINT(10 0)"), ("b", "POINT(90 0)")]))
        na.AddVehicleRoutingProblemRoutes(layer, 1, "Van", "D", "D")
        result = na.CopyTraversedSourceFeatures(layer, gpkg, "Edges", "Junctions", "Turns")
        # Out to one order (4 minutes), across to the other (8), back to the depot (4); no network junction is passed.
        rows = list(arcpy.da.SearchCursor(result[0], ["Attr_Minutes", "Cumul_Minutes", "RouteID"]))
        self.assertEqual([round(r[0], 9) for r in rows], [4.0, 8.0, 4.0])
        self.assertAlmostEqual(rows[-1][1], 16.0)
        kinds = [r[0] for r in arcpy.da.SearchCursor(result[1], ["SourceName"])]
        self.assertEqual(kinds, ["Depots", "Orders", "Orders", "Depots"])


if __name__ == "__main__":
    unittest.main()
