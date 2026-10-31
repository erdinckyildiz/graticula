"""Network diagrams of a hand-drawn water network, with known contents and layouts (ADR-232).

    S ─L1─ V1 ──────L2────── T ─L3─ V2        S   source device, its Load terminal the controller of Zone1
                             │      [VA]      V1, V2  valves; VA a vault (StructureJunction) containing V2
                             L4               T, E  tees (WaterJunction)
                             │
                             E
    x: S 0, V1 6, T 20, V2 30; E at (20, 10).

Worked by hand: a diagram of every feature has five junctions — S, V1, T, V2, E — four edges and the vault as a
container round V2 (a margin of 0.5: 29.5 … 30.5); reducing the valves turns V1 and its two lines into one reduction
edge from S to T and, reducing junctions with one connection too, folds V2 and L3 into T; the smart tree from S runs
S, V1, T two apart, then E above and V2 below, since E is higher on the map — the whole centred at (15, 5), where the
junctions were, as Pro 3.7 places it; linear
dispatch puts V1 midway between S and T, at (10, 0).
"""
import csv
import json
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _diagram, _layers, _ndlayout  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402
from shapely.geometry import LineString, Point  # noqa: E402


class Diagrams(unittest.TestCase):
    def setUp(self):
        _layers.LAYERS.clear()
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "water")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "utility", 3857)[0]
        territory = arcpy.management.CreateFeatureclass(self.fds, "Territory", "POLYGON", has_z="ENABLED",
                                                        has_m="ENABLED")[0]
        with arcpy.da.InsertCursor(territory, ["SHAPE@"]) as rows:
            rows.insertRow(["POLYGON((-10 -10, 100 -10, 100 100, -10 100, -10 -10))"])
        self.un = arcpy.un.CreateUtilityNetwork(self.fds, "Water", "Territory")[0]
        arcpy.un.AddDomainNetwork(self.un, "Water", "PARTITIONED", "SOURCE")
        arcpy.un.AddTier(self.un, "Water", "Pressure", 1, "RADIAL")
        self.device, self.line = f"{self.fds}/WaterDevice", f"{self.fds}/WaterLine"
        self.junction, self.vault = f"{self.fds}/WaterJunction", f"{self.fds}/StructureJunction"
        for name, codes in (("DeviceTypes", [(1, "Standard")]), ("LineTypes", [(1, "Ductile")]),
                            ("JunctionTypes", [(1, "Standard")]), ("VaultTypes", [(1, "Concrete")])):
            arcpy.management.CreateDomain(self.gpkg, name, name, "SHORT", "CODED")
            for code, text in codes:
                arcpy.management.AddCodedValueToDomain(self.gpkg, name, code, text)
        for table, groups, domain in ((self.device, [(1, "Source"), (2, "Valve")], "DeviceTypes"),
                                      (self.line, [(1, "Main")], "LineTypes"), (self.junction, [(1, "Tee")],
                                                                                "JunctionTypes"),
                                      (self.vault, [(1, "Vault")], "VaultTypes")):
            for code, text in groups:
                arcpy.management.AddSubtype(table, code, text)
                arcpy.management.AssignDomainToField(table, "ASSETTYPE", domain, code)
        for table in (self.device, self.line, self.junction, self.vault):
            arcpy.management.AddField(table, "NAME", "TEXT")
        arcpy.un.AddTerminalConfiguration(self.un, "Source-Load", "DIRECTIONAL", "Source true;Load false")
        arcpy.un.SetTerminalConfiguration(self.un, "Water", "WaterDevice", "Source", "Standard", "Source-Load")
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Source", "Standard", "Subnetwork Controller")
        arcpy.un.AddNetworkCategory(self.un, "Isolating")
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Valve", "Standard", ["Isolating"])
        je = "JUNCTION_EDGE_CONNECTIVITY"
        arcpy.un.AddRule(self.un, je, "WaterDevice", "Source", "Standard", "WaterLine", "Main", "Ductile", "Load")
        arcpy.un.AddRule(self.un, je, "WaterDevice", "Valve", "Standard", "WaterLine", "Main", "Ductile")
        arcpy.un.AddRule(self.un, je, "WaterJunction", "Tee", "Standard", "WaterLine", "Main", "Ductile")
        arcpy.un.AddRule(self.un, "JUNCTION_JUNCTION_CONNECTIVITY", "WaterJunction", "Tee", "Standard", "WaterDevice",
                         "Valve", "Standard")
        arcpy.un.AddRule(self.un, "CONTAINMENT", "StructureJunction", "Vault", "Concrete", "WaterDevice", "Valve",
                         "Standard")
        self.ids = {}
        self.insert(self.device, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"],
                    [("S", 1, 1, "POINT(0 0)"), ("V1", 2, 1, "POINT(6 0)"), ("V2", 2, 1, "POINT(30 0)")])
        self.insert(self.junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"],
                    [("T", 1, 1, "POINT(20 0)"), ("E", 1, 1, "POINT(20 10)")])
        self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [
            ("L1", 1, 1, "LINESTRING(0 0, 6 0)"), ("L2", 1, 1, "LINESTRING(6 0, 13 0, 20 0)"),
            ("L3", 1, 1, "LINESTRING(20 0, 30 0)"), ("L4", 1, 1, "LINESTRING(20 0, 20 10)")])
        self.insert(self.vault, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("VA", 1, 1, "POINT(30 0)")])
        controllers = os.path.join(self.directory, "controllers.csv")
        with open(controllers, "w", newline="", encoding="utf-8") as written:
            out = csv.writer(written)
            out.writerow(["SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                          "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES"])
            out.writerow(["Plant", self.ids["S"], "WaterDevice", "Source", "Standard", "Load", "Pressure", "Zone1", "",
                          ""])
        arcpy.un.ImportSubnetworkControllers(self.un, controllers)
        arcpy.un.UtilityNetwork(self.un).AddContainmentAssociation("StructureJunction", self.ids["VA"], "WaterDevice",
                                                                   self.ids["V2"], False)
        arcpy.un.EnableNetworkTopology(self.un)
        self.everything = [self.device, self.line, self.junction, self.vault]

    # -- helpers --

    def insert(self, table, fields, rows):
        with arcpy.da.InsertCursor(table, fields) as written:
            for row in rows:
                written.insertRow(row)
        with arcpy.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
            self.ids.update({n: g.upper() for n, g in read})

    def name_of(self, globalid):
        found = {g: n for n, g in self.ids.items()}
        return found.get(str(globalid).upper()) if globalid else None

    def content(self, diagram):
        """{"J": {name: (x, y)}, "C": {name: bounds}, "E": [(name or None, from name, to name, coords)]} and the
        reduction features' aggregated names."""
        out = {"J": {}, "C": {}, "E": []}
        ids = {}
        where = f"DIAGRAMNAME = '{diagram}'"
        with arcpy.da.SearchCursor(f"{self.fds}/Water_DiagramJunctions",
                                   ["DIAGRAMFEATUREID", "ASSOCGLOBALID", "SHAPE@XY", "ISREDUCTION"], where) as read:
            for fid, globalid, xy, reduction in read:
                label = ("R:" if reduction else "") + (self.name_of(globalid) or f"#{fid}")
                ids[fid] = label
                out["J"][label] = (round(xy[0], 6), round(xy[1], 6))
        with arcpy.da.SearchCursor(f"{self.fds}/Water_DiagramContainers",
                                   ["DIAGRAMFEATUREID", "ASSOCGLOBALID", "SHAPE@WKT"], where) as read:
            for fid, globalid, wkt in read:
                ids[fid] = self.name_of(globalid)
                import shapely
                out["C"][ids[fid]] = tuple(round(b, 6) for b in shapely.from_wkt(wkt).bounds)
        with arcpy.da.SearchCursor(f"{self.fds}/Water_DiagramEdges",
                                   ["ASSOCGLOBALID", "FROMID", "TOID", "SHAPE@WKT", "ISREDUCTION"], where) as read:
            for globalid, a, b, wkt, reduction in read:
                import shapely
                coords = [tuple(round(c, 6) for c in xy) for xy in shapely.from_wkt(wkt).coords]
                out["E"].append(("R" if reduction else self.name_of(globalid), ids.get(a), ids.get(b), coords))
        return out

    def aggregated(self, diagram):
        with _utility_network(self.un) as network:
            d = _diagram.load(network, diagram)
            return {f.id: sorted(self.name_of(g) for _, g in f.aggregated) for f in d.features.values() if f.aggregated}

    def template(self, name="Basic"):
        arcpy.nd.AddDiagramTemplate(self.un, name)
        return name

    def create(self, template="Basic", features=None):
        return arcpy.nd.CreateDiagram(self.un, template, features or self.everything)[1]

    # -- templates --

    def test_templates_are_added_named_altered_and_deleted_with_their_diagrams(self):
        self.template("Basic")
        self.template("Reduced")
        self.assertEqual(arcpy.nd.GetDiagramTemplateNames(self.un)[0], "Basic;Reduced")
        with self.assertRaises(ExecuteError):
            arcpy.nd.AddDiagramTemplate(self.un, "Basic")
        name = self.create("Reduced")
        self.assertEqual(name, "Temporary Diagram")
        self.assertEqual(self.create("Reduced"), "Temporary Diagram 2")
        arcpy.nd.AlterDiagramTemplate(self.un, "Reduced", "Simple", "DEFAULT_TEMPLATE")
        self.assertEqual(arcpy.nd.GetDiagramTemplateNames(self.un)[0], "Basic;Simple")
        arcpy.nd.DeleteDiagramTemplate(self.un, "Simple")
        self.assertEqual(arcpy.nd.GetDiagramTemplateNames(self.un)[0], "Basic")
        self.assertEqual(self.content(name)["J"], {})

    def test_a_diagram_of_every_feature_holds_its_junctions_edges_and_container(self):
        self.template()
        name = self.create()
        found = self.content(name)
        self.assertEqual(found["J"], {"S": (0, 0), "V1": (6, 0), "T": (20, 0), "V2": (30, 0), "E": (20, 10)})
        self.assertEqual(found["C"], {"VA": (29.5, -0.5, 30.5, 0.5)})
        edges = {e[0]: (e[1], e[2], e[3]) for e in found["E"]}
        self.assertEqual(edges["L4"], ("T", "E", [(20, 0), (20, 10)]))
        self.assertEqual(edges["L2"], ("V1", "T", [(6, 0), (20, 0)]))  # vertices not kept by default
        self.assertEqual(len(edges), 4)
        self.assertIn(name, _layers.LAYERS)

    def test_kept_vertices_follow_the_line(self):
        self.template()
        arcpy.nd.AlterDiagramTemplate(self.un, "Basic", are_vertices_kept="KEEP_VERTICES")
        edges = {e[0]: e[3] for e in self.content(self.create())["E"]}
        self.assertEqual(edges["L2"], [(6, 0), (13, 0), (20, 0)])

    def test_a_layer_selection_makes_a_diagram_of_what_it_selects_and_the_junctions_its_edges_need(self):
        self.template()
        arcpy.management.MakeFeatureLayer(self.line, "lines")
        arcpy.management.SelectLayerByAttribute("lines", "NEW_SELECTION", "NAME = 'L4'")
        found = self.content(self.create(features=["lines"]))
        self.assertEqual(set(found["J"]), {"T", "E"})
        self.assertEqual([e[0] for e in found["E"]], ["L4"])
        _layers.LAYERS.clear()
        arcpy.management.MakeFeatureLayer(self.device, "devices")
        arcpy.management.SelectLayerByAttribute("devices", "NEW_SELECTION", "NAME = 'V1'")
        name = arcpy.nd.CreateDiagram(self.un, "Basic")[1]  # the map: grapy's layers' selections
        self.assertEqual(set(self.content(name)["J"]), {"V1"})

    def test_nonnetwork_input_and_dirty_areas_make_no_diagram(self):
        self.template()
        other = arcpy.management.CreateFeatureclass(self.directory, "other.gpkg", "POINT", spatial_reference=3857)[0]
        with self.assertRaises(ExecuteError):
            arcpy.nd.CreateDiagram(self.un, "Basic", [other])
        with arcpy.da.UpdateCursor(self.line, ["SHAPE@"], "NAME = 'L4'") as rows:
            for _ in rows:
                rows.updateRow(["LINESTRING(20 0, 21 10)"])
        with self.assertRaises(ExecuteError) as said:
            self.create()
        self.assertIn("dirty", str(said.exception))
        arcpy.un.ValidateNetworkTopology(self.un)
        self.assertIn("E", self.content(self.create())["J"])

    # -- rules --

    def test_reducing_junctions_by_their_connections(self):
        self.template()
        arcpy.nd.AddReduceJunctionRule(self.un, "Basic", "ACTIVE", "INCLUDE_SOURCE_CLASSES", [self.device])
        name = self.create()
        found = self.content(name)
        self.assertEqual(set(found["J"]), {"S", "T", "V2", "E"})
        reduction = [e for e in found["E"] if e[0] == "R"]
        self.assertEqual([(e[1], e[2], e[3]) for e in reduction], [("S", "T", [(0, 0), (20, 0)])])
        self.assertIn(["L1", "L2", "V1"], self.aggregated(name).values())
        self.template("One")
        arcpy.nd.AddReduceJunctionRule(self.un, "One", "ACTIVE", "INCLUDE_SOURCE_CLASSES", [self.device],
                                       one_connected_junction="REDUCE_JCT_TO_1JCT")
        found = self.content(self.create("One"))
        # In feature id order: S (one connection) is reduced onto V1, which then has one connection and is reduced
        # onto T; V2, the vault's content, stays in it — as ArcGIS Pro 3.7 answered on this network (2026-10-31).
        self.assertEqual(set(found["J"]), {"T", "V2", "E"})
        self.assertEqual(found["C"], {"VA": (29.5, -0.5, 30.5, 0.5)})

    def test_the_valence_count_and_edge_attributes_hold_a_reduction_back(self):
        self.template()
        arcpy.nd.AddReduceJunctionRule(self.un, "Basic", "ACTIVE", "INCLUDE_SOURCE_CLASSES", ["WaterJunction"])
        found = self.content(self.create())
        self.assertIn("T", found["J"])  # T meets three junctions: MAX_2 leaves it
        self.template("Three")
        arcpy.nd.AddReduceJunctionRule(self.un, "Three", "ACTIVE", "INCLUDE_SOURCE_CLASSES", ["WaterJunction"],
                                       "MIN_3_CONNECTED_JUNCTIONS")
        found = self.content(self.create("Three"))
        # T is reduced onto its one neighbour nearer the controller, V1; L2 folds into V1 and L3, L4 now start there.
        self.assertNotIn("T", found["J"])
        edges = {e[0]: (e[1], e[2]) for e in found["E"]}
        self.assertEqual(edges["L3"], ("V1", "V2"))
        self.assertEqual(edges["L4"], ("V1", "E"))

    def test_a_reduce_edge_rule_merges_a_line_and_its_ends_into_one_junction(self):
        self.template()
        arcpy.nd.AddReduceEdgeByAttributeRule(self.un, "Basic", "ACTIVE", self.line, "NAME = 'L4'")
        found = self.content(self.create())
        self.assertIn("R:L4", found["J"])
        self.assertNotIn("T", found["J"])
        self.assertNotIn("E", found["J"])
        self.assertEqual(found["J"]["R:L4"], (20, 5))  # the centre of what it aggregates: L4, T and E
        reconnected = sorted((e[1], e[2]) for e in found["E"] if e[0] == "R")
        self.assertEqual(reconnected, [("R:L4", "V2"), ("V1", "R:L4")])

    def test_removing_features_with_and_without_a_connectivity_constraint(self):
        self.template()
        arcpy.nd.AddRemoveFeatureByAttributeRule(self.un, "Basic", "ACTIVE", self.junction, "NAME = 'E'",
                                                 unconnected_junctions="MUST_BE_UNCONNECTED")
        self.assertIn("E", self.content(self.create())["J"])
        self.template("Plain")
        arcpy.nd.AddRemoveFeatureByAttributeRule(self.un, "Plain", "ACTIVE", self.junction, "NAME = 'E'")
        found = self.content(self.create("Plain"))
        self.assertNotIn("E", found["J"])
        self.assertNotIn("L4", [e[0] for e in found["E"]])
        self.template("Lines")
        arcpy.nd.AddRemoveFeatureRule(self.un, "Lines", "ACTIVE", "EDGES", "INCLUDE_SOURCE_CLASSES", [self.line])
        self.assertEqual(self.content(self.create("Lines"))["E"], [])

    def test_collapsing_a_container_and_preventing_it(self):
        self.template()
        arcpy.nd.AddCollapseContainerRule(self.un, "Basic")
        name = self.create()
        found = self.content(name)
        self.assertEqual(found["C"], {})
        self.assertEqual(found["J"]["VA"], (30, 0))
        self.assertNotIn("V2", found["J"])
        self.assertEqual([(e[1], e[2]) for e in found["E"] if e[0] == "R"], [("T", "VA")])
        self.assertIn(["L3"], self.aggregated(name).values())
        self.template("Kept")
        arcpy.nd.AddDiagramFeatureCapabilityByAttributeRule(self.un, "Kept", "ACTIVE", self.vault, "NAME = 'VA'",
                                                            "PREVENT_TO_COLLAPSE_CONTAINER")
        arcpy.nd.AddCollapseContainerRule(self.un, "Kept")
        self.assertIn("VA", self.content(self.create("Kept"))["C"])

    def test_expanding_a_container_adds_its_content(self):
        self.template()
        arcpy.nd.AddExpandContainerRule(self.un, "Basic")
        arcpy.management.MakeFeatureLayer(self.vault, "vaults")
        found = self.content(self.create(features=["vaults"]))
        self.assertEqual(found["C"], {"VA": (29.5, -0.5, 30.5, 0.5)})
        self.assertEqual(set(found["J"]), {"V2"})
        self.template("Hidden")
        arcpy.nd.AddExpandContainerByAttributeRule(self.un, "Hidden", "ACTIVE", "HIDE", self.vault, "NAME = 'VA'")
        found = self.content(self.create("Hidden", ["vaults"]))
        self.assertEqual((set(found["J"]), found["C"]), ({"V2"}, {}))

    def test_a_trace_rule_makes_the_diagram_what_the_trace_returns(self):
        self.template()
        arcpy.nd.AddTraceRule(self.un, "Basic", "ACTIVE", "CONNECTED")
        arcpy.management.MakeFeatureLayer(self.junction, "tees")
        arcpy.management.SelectLayerByAttribute("tees", "NEW_SELECTION", "NAME = 'E'")
        found = self.content(self.create(features=["tees"]))
        self.assertEqual(set(found["J"]), {"S", "V1", "T", "V2", "E"})
        self.assertEqual(len(found["E"]), 4)
        self.template("Down")
        arcpy.nd.AddSetStartingPointByAttributeRule(self.un, "Down", "ACTIVE", self.junction, "NAME = 'T'")
        arcpy.nd.AddTraceRule(self.un, "Down", "ACTIVE", "DOWNSTREAM", "Water", "Pressure")
        found = self.content(self.create("Down"))
        self.assertEqual(set(found["J"]), {"T", "V2", "E"})

    def test_a_spatial_query_rule_adds_what_meets_the_diagram(self):
        self.template()
        arcpy.nd.AddSpatialQueryRule(self.un, "Basic", "ACTIVE", self.device, "WITHIN_A_DISTANCE", self.junction,
                                     "11 Meters", "NAME = 'V2'")
        arcpy.management.MakeFeatureLayer(self.junction, "tees")
        found = self.content(self.create(features=["tees"]))
        self.assertEqual(set(found["J"]), {"T", "E", "V2"})  # V2 is 10 m from T

    def test_connectivity_associations_are_drawn_between_junctions_in_the_diagram(self):
        self.insert(self.device, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("V3", 2, 1, "POINT(24 10)")])
        arcpy.un.UtilityNetwork(self.un).AddConnectivityAssociation(1, "WaterJunction", self.ids["E"], None,
                                                                    "WaterDevice", self.ids["V3"], None)
        arcpy.un.ValidateNetworkTopology(self.un)
        self.template()
        self.assertEqual(len(self.content(self.create())["E"]), 4)
        arcpy.nd.AddConnectivityAssociationsRule(self.un, "Basic")
        edges = self.content(self.create())["E"]
        self.assertIn(("E", "V3"), [(e[1], e[2]) for e in edges])

    def test_iteration_reduces_again_what_a_reduction_made_reducible(self):
        # Tees first, then devices: T (three connections) is passed over, E is folded into T, S into V1, V1 — now with
        # one connection — into T; V2 stays in its vault. Only a second pass finds T with one connection and folds it
        # into V2.
        for name, loop in (("Once", False), ("Loop", True)):
            self.template(name)
            if loop:
                arcpy.nd.AddStartIterationRule(self.un, name)
            arcpy.nd.AddReduceJunctionRule(self.un, name, "ACTIVE", "INCLUDE_SOURCE_CLASSES",
                                           [self.junction, self.device], "MAX_2_CONNECTED_JUNCTIONS",
                                           "REDUCE_UNCONNECTED_JCT", "REDUCE_JCT_TO_1JCT", "KEEP_JCT_TO_2JCTS")
            if loop:
                arcpy.nd.AddStopIterationRule(self.un, name)
        self.assertEqual(set(self.content(self.create("Once"))["J"]), {"T", "V2"})
        self.assertEqual(set(self.content(self.create("Loop"))["J"]), {"V2"})

    # -- layouts --

    def test_the_smart_tree_from_the_root_junction(self):
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Basic", "ACTIVE", self.device, "NAME = 'S'")
        name = self.create()
        arcpy.nd.ApplySmartTreeLayout(name)
        found = self.content(name)
        # The tree is centred where the junctions were — (15, 5) — as ArcGIS Pro 3.7 places it (2026-10-31).
        self.assertEqual(found["J"], {"S": (12, 5), "V1": (14, 5), "T": (16, 5), "E": (18, 6), "V2": (18, 4)})
        self.assertEqual(found["C"]["VA"], (17.5, 3.5, 18.5, 4.5))
        arcpy.nd.ApplySmartTreeLayout(name, tree_direction="FROM_TOP_TO_BOTTOM", is_unit_absolute="ABSOLUTE_UNIT",
                                      along_absolute="5 Meters", perpendicular_absolute="4 Meters")
        found = self.content(name)
        # Top to bottom from S: levels 5 apart downward, E and V2 4 apart across, centred on T, the whole round (15, 5)
        # — Pro 3.7's answer to the coordinate: S (15, 12.5), V1 (15, 7.5), T (15, 2.5), E (13, -2.5), V2 (17, -2.5).
        self.assertEqual([found["J"][n] for n in ("S", "V1", "T")], [(15, 12.5), (15, 7.5), (15, 2.5)])
        self.assertEqual(sorted([found["J"]["E"], found["J"]["V2"]]), [(13, -2.5), (17, -2.5)])  # tied across here

    def test_orthogonal_edges_break_where_the_breakpoint_says(self):
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Basic", "ACTIVE", self.device, "NAME = 'S'")
        name = self.create()
        arcpy.nd.ApplySmartTreeLayout(name, edge_display_type="ORTHOGONAL_EDGES", breakpoint_position=0)
        edges = {e[0]: e[3] for e in self.content(name)["E"]}
        self.assertEqual(edges["L4"], [(16, 5), (16, 6), (18, 6)])

    def test_the_radial_tree_puts_each_level_on_its_circle(self):
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Basic", "ACTIVE", self.device, "NAME = 'S'")
        name = self.create()
        arcpy.nd.ApplyRadialTreeLayout(name)
        found = self.content(name)["J"]
        self.assertEqual(found["S"], (0, 0))
        self.assertEqual(found["V1"], (-5, 0))   # one child: its wedge is the whole circle, its place the middle
        self.assertEqual(found["T"], (-10, 0))
        self.assertEqual(sorted([found["E"], found["V2"]]), [(0, -15), (0, 15)])
        arcpy.nd.ApplyRadialTreeLayout(name, radius_factor=2)
        self.assertEqual(self.content(name)["J"]["T"], (-15, 0))  # 5, then 5 + 10

    def test_the_mainline_tree_runs_the_longest_branch_straight(self):
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Basic", "ACTIVE", self.device, "NAME = 'S'")
        name = self.create()
        arcpy.nd.ApplyMainlineTreeLayout(name)
        found = self.content(name)["J"]
        self.assertEqual([found[n] for n in ("S", "V1", "T")], [(0, 0), (2, 0), (4, 0)])
        self.assertEqual(sorted([found["E"], found["V2"]]), [(4, 2), (6, 0)])

    def test_geo_positions_bring_a_laid_out_diagram_back(self):
        self.template()
        name = self.create()
        arcpy.nd.ApplySmartTreeLayout(name)
        arcpy.nd.ApplyGeoPositionsLayout(name)
        found = self.content(name)
        self.assertEqual(found["J"]["V1"], (6, 0))
        self.assertEqual({e[0]: e[3] for e in found["E"]}["L2"], [(6, 0), (13, 0), (20, 0)])

    def test_grid_snaps_junctions_and_a_template_presets_its_cell(self):
        self.template()
        arcpy.nd.AddGridLayout(self.un, "Basic", "INACTIVE", None, "3 Meters", "3 Meters")
        name = self.create()
        self.assertEqual(self.content(name)["J"]["V1"], (6, 0))  # an inactive layout does not run
        arcpy.nd.ApplyGridLayout(name)
        found = self.content(name)["J"]
        self.assertEqual(found, {"S": (0, 0), "V1": (6, 0), "T": (21, 0), "V2": (30, 0), "E": (21, 9)})

    def test_linear_dispatch_spaces_two_edge_junctions_evenly(self):
        self.template()
        name = self.create()
        arcpy.nd.ApplyLinearDispatchLayout(name)
        found = self.content(name)
        self.assertEqual(found["J"]["V1"], (10, 0))
        edges = {e[0]: e[3] for e in found["E"]}
        # PRESERVE_PATH keeps the path's vertices, V1's old place among them, as the page says.
        self.assertEqual(edges["L1"], [(0, 0), (6, 0), (10, 0)])
        self.assertEqual(edges["L2"], [(10, 0), (20, 0)])
        arcpy.nd.ApplyGeoPositionsLayout(name)
        arcpy.nd.ApplyLinearDispatchLayout(name, is_path_preserved="IGNORE_PATH")
        self.assertEqual({e[0]: e[3] for e in self.content(name)["E"]}["L1"], [(0, 0), (10, 0)])

    def test_spatial_dispatch_separates_near_junctions(self):
        self.template()
        name = self.create()
        arcpy.nd.ApplySpatialDispatchLayout(name, iterations_number=1, maximum_shift_factor=7)
        found = self.content(name)["J"]
        self.assertEqual((found["S"], found["V1"]), ((-0.5, 0), (6.5, 0)))
        self.assertEqual(found["T"], (20, 0))

    def test_compression_closes_gaps_to_the_grouping_distance(self):
        self.template()
        name = self.create()
        arcpy.nd.ApplyCompressionLayout(name, grouping_distance_absolute="6 Meters")
        found = self.content(name)["J"]
        self.assertEqual(found, {"S": (7.5, 2), "V1": (13.5, 2), "T": (16.5, 2), "V2": (22.5, 2), "E": (16.5, 8)})

    def test_angle_directed_and_force_directed(self):
        self.template()
        name = self.create()
        before = self.content(name)["J"]
        arcpy.nd.ApplyAngleDirectedLayout(name)
        self.assertEqual(self.content(name)["J"], before)  # every edge already runs at 0 or 90 degrees
        arcpy.nd.ApplyForceDirectedLayout(name, degree_freedom="HIGH")
        moved = self.content(name)
        self.assertNotEqual(moved["J"], before)
        arcpy.nd.ApplyAngleDirectedLayout(name, iterations_number=1, number_of_directions="FOUR_DIRECTIONS")
        for _, _, _, coords in self.content(name)["E"]:
            (x0, y0), (x1, y1) = coords[0], coords[-1]
            self.assertTrue(abs(x1 - x0) < 1e-6 or abs(y1 - y0) < 1e-6)

    def test_a_template_lays_its_diagrams_out_when_they_are_made(self):
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Basic", "ACTIVE", self.device, "NAME = 'S'")
        arcpy.nd.AddSmartTreeLayout(self.un, "Basic")
        name = self.create()
        self.assertEqual(self.content(name)["J"]["T"], (16, 5))
        arcpy.nd.ApplyGeoPositionsLayout(name)
        arcpy.nd.ApplyTemplateLayouts(name)
        self.assertEqual(self.content(name)["J"]["T"], (16, 5))

    def test_a_feature_selection_limits_the_next_layout(self):
        self.template()
        arcpy.nd.AddDiagramFeatureSelectionToLayout(self.un, "Basic", "ACTIVE", "SELECT_BY_ATTRIBUTES",
                                                    source_by_attribute=self.device, where_clause="NAME = 'V1'")
        arcpy.nd.AddGridLayout(self.un, "Basic", "ACTIVE", None, "4 Meters", "4 Meters")
        found = self.content(self.create())["J"]
        self.assertEqual(found["V1"], (8, 0))
        self.assertEqual(found["T"], (20, 0))  # not selected: not moved

    # -- the layouts on small diagrams of their own --

    def diagram(self, points, edges):
        d = _diagram.Diagram("synthetic", "t")
        for name, xy in points.items():
            f = d.add("J")
            f.shape = Point(xy)
            points[name] = f.id
        for a, b, coords in edges:
            e = d.add("E")
            e.from_id, e.to_id = points[a], points[b]
            e.shape = LineString(coords)
        return d

    def test_main_ring_places_the_longest_loop_on_an_ellipse(self):
        d = self.diagram({"A": (0, 0), "B": (10, 0), "C": (10, 10), "D": (0, 10), "X": (20, 10)},
                         [("A", "B", [(0, 0), (10, 0)]), ("B", "C", [(10, 0), (10, 10)]),
                          ("C", "D", [(10, 10), (0, 10)]), ("D", "A", [(0, 10), (0, 0)]),
                          ("C", "X", [(10, 10), (20, 10)])])
        view = _ndlayout.View(d)
        _ndlayout.main_ring(view, {"ring_width": 20.0, "ring_height": 10.0}, 0.5)
        at = {i: tuple(round(c, 6) for c in d.features[i].shape.coords[0]) for i in (1, 2, 3, 4, 5)}
        # Ring A B C D round its centre (5, 5): from A east, counterclockwise toward its lower-id neighbour B.
        self.assertEqual([at[i] for i in (1, 2, 3, 4)], [(15, 5), (5, 10), (-5, 5), (5, 0)])
        self.assertEqual(at[5], (-7, 5))  # X hangs from C (to the west) outward, one level of 2 away

    def test_partial_overlapping_edges_spread_apart(self):
        d = self.diagram({"A": (0, 0), "B": (20, 0), "C": (0, 1), "D": (20, 1)},
                         [("A", "B", [(0, 0), (5, 0), (15, 0), (20, 0)]),
                          ("C", "D", [(0, 1), (5, 1), (15, 1), (20, 1)])])
        _ndlayout.partial_overlapping_edges(_ndlayout.View(d), {"buffer_width": 2.0, "offset": 4.0}, 0.5)
        first = [tuple(round(c, 6) for c in xy) for xy in d.features[5].shape.coords]
        second = [tuple(round(c, 6) for c in xy) for xy in d.features[6].shape.coords]
        self.assertEqual(first, [(0, 0), (0, -1.5), (5, -1.5), (15, -1.5), (20, -1.5), (20, 0)])
        self.assertEqual(second, [(0, 1), (0, 2.5), (5, 2.5), (15, 2.5), (20, 2.5), (20, 1)])

    def test_reshape_edges(self):
        d = self.diagram({"A": (0, 0), "B": (20, 10)}, [("A", "B", [(0, 0), (10, 9.9), (20, 10)])])
        _ndlayout.reshape_edges(_ndlayout.View(d), {"reshape_type": "REDUCE_VERTICES_BY_ANGLE",
                                                    "angle_threshold": 130}, 0.5)
        self.assertEqual(list(d.features[3].shape.coords), [(0, 0), (20, 10)])
        _ndlayout.reshape_edges(_ndlayout.View(d), {"reshape_type": "SQUARE_EDGES", "breakpoint": 8.66}, 0.5)
        self.assertEqual(list(d.features[3].shape.coords), [(0, 0), (8.66, 0), (8.66, 10), (20, 10)])
        two = self.diagram({"A": (0, 0), "B": (10, 0)}, [("A", "B", [(0, 0), (10, 0)]), ("A", "B", [(0, 0), (10, 0)])])
        _ndlayout.reshape_edges(_ndlayout.View(two), {"reshape_type": "SEPARATE_OVERLAPPING_EDGES",
                                                      "shift_between_edge": 1.0}, 0.5)
        self.assertEqual([list(two.features[i].shape.coords)[1] for i in (3, 4)], [(5, -0.5), (5, 0.5)])
        cross = self.diagram({"A": (0, 0), "B": (10, 0), "C": (5, -5), "D": (5, 5)},
                             [("A", "B", [(0, 0), (10, 0)]), ("C", "D", [(5, -5), (5, 5)])])
        _ndlayout.reshape_edges(_ndlayout.View(cross), {"reshape_type": "MARK_CROSSING_EDGES", "arc_radius": 1.0},
                                0.5)
        coords = [tuple(round(c, 6) for c in xy) for xy in cross.features[5].shape.coords]
        self.assertEqual((coords[1], coords[-2]), ((4, 0), (6, 0)))
        self.assertAlmostEqual(max(y for _, y in coords), 1.0)
        self.assertEqual(list(cross.features[6].shape.coords), [(5, -5), (5, 5)])

    def test_force_directed_leaves_an_edge_of_ideal_length_alone(self):
        d = self.diagram({"A": (0, 0), "B": (4, 0)}, [("A", "B", [(0, 0), (4, 0)])])
        _ndlayout.force_directed(_ndlayout.View(d), {"iterations": 5}, 0.5)
        self.assertEqual([d.features[i].shape.coords[0] for i in (1, 2)], [(0, 0), (4, 0)])

    # -- diagrams --

    def test_store_alter_owner_delete_and_purge(self):
        self.template()
        name = self.create()
        with self.assertRaises(NotBuiltError) as said:
            arcpy.nd.StoreDiagram(name, "Mine", "PRIVATE")
        self.assertIn("portal", str(said.exception))
        arcpy.nd.StoreDiagram(name, "Mine", "PUBLIC", "main#zone1")
        self.assertEqual(_layers.LAYERS[name].diagram, "Mine")
        arcpy.nd.AlterDiagramProperties(name, "Ours", tags="zone1")
        self.create()
        with _utility_network(self.un) as network:
            rows = dict(network.q("select name, stored from grapy_nd_diagram").fetchall())
        self.assertEqual(rows, {"Ours": 1, "Temporary Diagram": 0})
        with self.assertRaises(ExecuteError):
            arcpy.nd.ChangeDiagramsOwner(self.un, "nobody-owns-one", diagram_names=["Ours"])
        arcpy.nd.PurgeTemporaryDiagrams(self.un)
        with _utility_network(self.un) as network:
            self.assertEqual([r[0] for r in network.q("select name from grapy_nd_diagram")], ["Ours"])
        arcpy.nd.DeleteDiagram(self.un, "Basic")
        with _utility_network(self.un) as network:
            self.assertEqual(network.q("select count(*) from grapy_nd_diagram").fetchone()[0], 0)

    def test_storing_needs_a_consistent_diagram_and_updating_makes_it_so(self):
        self.template()
        name = self.create()
        arcpy.nd.AddReduceJunctionRule(self.un, "Basic", "ACTIVE", "INCLUDE_SOURCE_CLASSES", [self.device])
        with self.assertRaises(ExecuteError) as said:
            arcpy.nd.StoreDiagram(name, "Kept")
        self.assertIn("inconsistent", str(said.exception))
        arcpy.nd.UpdateDiagram(self.un, diagram_names=[name])
        self.assertNotIn("V1", self.content(name)["J"])
        arcpy.nd.StoreDiagram(name, "Kept")
        arcpy.nd.AlterDiagramTemplate(self.un, "Basic", is_diagram_storage_enabled="DISABLE_DIAGRAM_STORAGE")
        with self.assertRaises(ExecuteError):
            arcpy.nd.StoreDiagram(self.create(), "Other")

    def test_append_overwrite_and_extend(self):
        self.template()
        arcpy.management.MakeFeatureLayer(self.line, "lines")
        arcpy.management.SelectLayerByAttribute("lines", "NEW_SELECTION", "NAME = 'L4'")
        name = arcpy.nd.CreateDiagram(self.un, "Basic", ["lines"])[1]
        arcpy.management.SelectLayerByAttribute("lines", "NEW_SELECTION", "NAME = 'L1'")
        arcpy.nd.AppendToDiagram(name, "Map")
        self.assertEqual(set(self.content(name)["J"]), {"T", "E", "S", "V1"})
        arcpy.nd.OverwriteDiagram(name, "Map")
        self.assertEqual(set(self.content(name)["J"]), {"S", "V1"})
        with self.assertRaises(ExecuteError) as said:
            arcpy.nd.ExtendDiagram(name)
        self.assertIn("extended", str(said.exception))
        arcpy.nd.AlterDiagramTemplate(self.un, "Basic", is_diagram_extension_enabled="ENABLE_DIAGRAM_EXTENSION")
        arcpy.nd.ExtendDiagram(name, None, "BY_CONNECTIVITY")
        found = self.content(name)
        self.assertEqual(set(found["J"]), {"S", "V1", "T"})
        self.assertEqual(found["J"]["T"], (20, 0))
        arcpy.management.MakeFeatureLayer(self.vault, "vaults")
        vault = arcpy.nd.CreateDiagram(self.un, "Basic", ["vaults"])[1]
        self.assertEqual(set(self.content(vault)["J"]), {"VA"})
        arcpy.nd.ExtendDiagram(vault, None, "BY_CONTAINMENT")
        found = self.content(vault)
        self.assertEqual((set(found["J"]), set(found["C"])), ({"V2"}, {"VA"}))

    def test_diagram_layers_sublayers_and_exported_content(self):
        self.template()
        arcpy.nd.AddReduceJunctionRule(self.un, "Basic", "ACTIVE", "INCLUDE_SOURCE_CLASSES", [self.device])
        name = self.create()
        arcpy.nd.MakeDiagramLayer(self.un, name, "view", "ADD_SUBLAYERS")
        with arcpy.da.SearchCursor("view/Edges", ["OID@"]) as read:
            self.assertEqual(len(list(read)), 3)
        copied = arcpy.conversion.ExportFeatures("view/Junctions", os.path.join(self.directory, "j.gpkg"))[0]
        self.assertEqual(int(arcpy.management.GetCount(copied)[0]), 4)
        out = os.path.join(self.directory, "content.json")
        arcpy.nd.ExportDiagramContent(self.un, name, out, "INCLUDE_DIAGRAM_PROPERTIES", "INCLUDE_GEOMETRIES",
                                      "INCLUDE_ATTRIBUTES", "INCLUDE_AGGREGATIONS", "USE_CODED_VALUE_NAMES")
        doc = json.load(open(out, encoding="utf-8"))
        self.assertEqual(doc["diagram"], name)
        self.assertEqual(doc["diagramInfo"]["template"], "Basic")
        self.assertEqual((doc["diagramInfo"]["junctionCount"], doc["diagramInfo"]["edgeCount"],
                          doc["diagramInfo"]["containerCount"]), (4, 3, 1))
        reduction = [e for e in doc["edges"] if e["userType"] == "Reduction"][0]
        valve = [a for a in reduction["aggregations"] if a["globalID"] == self.ids["V1"]][0]
        self.assertEqual((valve["assetGroup"], valve["assetType"]), ("Valve", "Standard"))
        tee = [j for j in doc["junctions"] if j["assocGlobalID"] == self.ids["T"]][0]
        self.assertEqual((tee["geometry"], tee["attributes"]["NAME"]), ({"x": 20.0, "y": 0.0}, "T"))
        self.assertEqual(doc["containers"][0]["id"], [j for j in doc["junctions"] if j["assocGlobalID"] ==
                                                       self.ids["V2"]][0]["containerID"])

    def test_template_definitions_round_trip_between_networks(self):
        self.template()
        arcpy.nd.AddReduceJunctionRule(self.un, "Basic", "ACTIVE", "INCLUDE_SOURCE_CLASSES", [self.device])
        arcpy.nd.AddSmartTreeLayout(self.un, "Basic", "INACTIVE", tree_direction="FROM_TOP_TO_BOTTOM")
        out = os.path.join(self.directory, "basic.ndbd")
        arcpy.nd.ExportDiagramTemplateDefinitions(self.un, "Basic", out)
        self.template("Copy")
        arcpy.nd.ImportDiagramTemplateDefinitions(self.un, "Copy", out)
        self.assertNotIn("V1", self.content(self.create("Copy"))["J"])
        with self.assertRaises(NotBuiltError):
            arcpy.nd.ExportDiagramTemplateDefinitions(self.un, "Basic", out, os.path.join(self.directory, "x.ndld"))

    def test_subnetwork_system_diagrams_follow_update_subnetwork(self):
        self.template()
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.SetSubnetworkDefinition(self.un, "Water", "Pressure", diagram_template=["Basic"])
        arcpy.un.EnableNetworkTopology(self.un)
        arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
        found = self.content("Zone1")
        self.assertEqual(set(found["J"]), {"S", "V1", "T", "V2", "E"})
        arcpy.nd.MakeDiagramLayer(self.un, "Zone1", "zone")
        for tool in (arcpy.nd.AppendToDiagram, arcpy.nd.OverwriteDiagram, arcpy.nd.ExtendDiagram):
            with self.assertRaises(ExecuteError) as said:
                tool("zone")
            self.assertIn("system diagram", str(said.exception))
        with self.assertRaises(ExecuteError):
            arcpy.nd.DeleteDiagram("zone")

    def test_what_is_not_built_is_refused_by_name(self):
        self.template()
        for module in (arcpy.nd, arcpy.un):
            for name, words in (("ApplyRotateTreeLayout", "pivot junctions"),
                                ("ApplyRelativeMainlineLayout", "published method"),
                                ("CreateDiagramLayerDefinition", "layer definition"),
                                ("ExportDiagramLayerDefinition", "layer definition")):
                with self.assertRaises(NotBuiltError) as said:
                    getattr(module, name)(self.un)
                self.assertIn(words, str(said.exception))
        with self.assertRaises(NotBuiltError) as said:
            arcpy.nd.CreateGeoprocessingModelFromDiagramTemplate(self.un, "Basic", "x.atbx")
        self.assertIn("ModelBuilder", str(said.exception))
        with self.assertRaises(NotBuiltError) as said:
            arcpy.nd.CreateDiagram(self.un, "Basic", None, None, "CREATE_DIAGRAM_FROM_CIRCUIT_NAME", "C1")
        self.assertIn("telecom", str(said.exception))
        with self.assertRaises(NotBuiltError):
            arcpy.nd.AddTraceRule(self.un, "Basic", "ACTIVE", "CIRCUIT")
        with self.assertRaises(NotBuiltError) as said:
            arcpy.nd.AddTraceRule(self.un, "Basic", "ACTIVE", "PATH", path_direction="PATH_UPSTREAM")
        self.assertIn("trace network", str(said.exception))
        with self.assertRaises(NotBuiltError):
            arcpy.nd.AddDiagramTemplate(self.un, "Other", None, os.path.join(self.directory, "x.ndld"))


def _utility_network(path):
    from grapy import _utility
    return _utility.Network(path)


if __name__ == "__main__":
    unittest.main()
