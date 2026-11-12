"""The diagram rules by category and attribute, the layouts a template presets, and the layouts that reshape a diagram,
on the hand-drawn water network of test_network_diagrams (ADR-232); and Delete Network Category (ADR-231).

    S ─L1─ V1 ──────L2────── T ─L3─ V2        x: S 0, V1 6, T 20, V2 30; E at (20, 10).
                             │      [VA]      V1, V2 valves of category Isolating; VA a vault round V2
                             L4
                             │
                             E

Some tests add to it, each saying what and validating the topology after: a tee F at (26, 4) on a line L5 from T (an
edge at atan(4/6) = 33.69 degrees), a second line L6 between V1 and T (two edges on one line once drawn straight), or
a line L7 from E back to V1 (the loop V1, T, E).

Every expected position is worked by hand from the tool's page (doc.esri.com/en/arcgis-pro/latest/tool-reference/
network-diagrams/<tool>.html) and, where the page leaves the arrangement open, from the method grapy documents for
it in _ndlayout, marked INFERRED there and here.
"""
import math
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _diagram  # noqa: E402
from grapy._runtime import ExecuteError  # noqa: E402
import test_network_diagrams as base  # noqa: E402

# arcpy.un holds these diagram tools under arcpy.nd's names (the Utility Network toolbox lists them too).
UN_NAMES = ("AddAngleDirectedLayout", "AddCollapseContainerByAttributeRule", "AddCompressionLayout",
            "AddForceDirectedLayout", "AddLinearDispatchLayout", "AddMainRingLayout", "AddMainlineTreeLayout",
            "AddPartialOverlappingEdgesLayout", "AddRadialTreeLayout", "AddReduceJunctionByAttributeRule",
            "AddReshapeDiagramEdgesLayout", "AddSpatialDispatchLayout", "AddStructuralAttachmentsRule",
            "ApplyMainRingLayout", "ApplyPartialOverlappingEdgesLayout", "ReshapeDiagramEdgesLayout")


class DiagramLayouts(unittest.TestCase):
    # The fixture and helpers of test_network_diagrams, borrowed rather than inherited so that its tests run once.
    setUp = base.Diagrams.setUp
    insert, name_of, content = base.Diagrams.insert, base.Diagrams.name_of, base.Diagrams.content
    aggregated, template, create = base.Diagrams.aggregated, base.Diagrams.template, base.Diagrams.create

    # -- helpers --

    def steps(self, template):
        with base._utility_network(self.un) as network:
            return _diagram.template(network, template)["steps"]

    def last_step(self, template):
        return self.steps(template)[-1]

    def lines(self, rows, junctions=()):
        if junctions:
            self.insert(self.junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], list(junctions))
        self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], list(rows))
        arcpy.un.ValidateNetworkTopology(self.un)

    def branch(self):
        """The tee F at (26, 4) on L5 from T."""
        self.lines([("L5", 1, 1, "LINESTRING(20 0, 26 4)")], [("F", 1, 1, "POINT(26 4)")])

    def chamber(self):
        """The vault's asset type in a network category of its own, Chamber."""
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.AddNetworkCategory(self.un, "Chamber")
        arcpy.un.SetNetworkCategory(self.un, "Structure", "StructureJunction", "Vault", "Concrete", ["Chamber"])
        arcpy.un.EnableNetworkTopology(self.un)

    def categories(self):
        with base._utility_network(self.un) as network:
            return {r[0] for r in network.q("select name from grapy_un_category where network = ?", network.name)}

    def near(self, a, b, places=6):
        self.assertEqual(len(a), len(b))
        for x, y in zip(a, b):
            self.assertAlmostEqual(x, y, places=places)

    # -- arcpy.un's names --

    def test_arcpy_un_holds_arcpy_nd_s_diagram_tools(self):
        for name in UN_NAMES:
            self.assertIs(getattr(arcpy.un, name), getattr(arcpy.nd, name), name)

    # -- rules by attribute and by category --

    def test_reduce_junction_by_attribute_reduces_the_junctions_the_clause_selects(self):
        # The page: the junctions of the source the where clause selects are reduced as Reduce Junction says; V1 has
        # two connected junctions, S and T, and REDUCE_JCT_TO_2JCTS is the default: it and L1, L2 become one edge S-T.
        self.template()
        arcpy.un.AddReduceJunctionByAttributeRule(self.un, "Blank", "ACTIVE", self.device, "NAME = 'V1'")
        step = self.last_step("Blank")
        self.assertEqual((step["kind"], step["rule"], step["active"]), ("rule", "ReduceJunction", True))
        self.assertEqual(step["params"]["selector"], {"by": "attribute", "source": "WaterDevice", "where": "NAME = 'V1'"})
        self.assertEqual((step["params"]["connectivity"], step["params"]["two"], step["params"]["one"]),
                         ("MAX_2_CONNECTED_JUNCTIONS", True, False))
        name = self.create()
        found = self.content(name)
        self.assertEqual(set(found["J"]), {"S", "T", "V2", "E"})
        self.assertEqual([(e[1], e[2], e[3]) for e in found["E"] if e[0] == "R"], [("S", "T", [(0, 0), (20, 0)])])
        self.assertIn(["L1", "L2", "V1"], self.aggregated(name).values())
        with self.assertRaises(ExecuteError):  # the junction source is required
            arcpy.nd.AddReduceJunctionByAttributeRule(self.un, "Blank", "ACTIVE", None, "NAME = 'V1'")

    def test_reduce_junction_by_category_reduces_the_category_s_junctions(self):
        # Isolating is V1's and V2's category. V1 is reduced as above; V2 is the vault's content and stays in it, as
        # the Reduce Junction tests of test_network_diagrams found Pro 3.7 doing. S is a Source, not Isolating.
        self.template()
        arcpy.nd.AddReduceJunctionByCategoryRule(self.un, "Blank", "ACTIVE", "INCLUDE_CATEGORIES", "Isolating")
        self.assertEqual(self.last_step("Blank")["params"]["selector"],
                         {"by": "category", "include": True, "categories": ["Isolating"]})
        found = self.content(self.create())
        self.assertEqual(set(found["J"]), {"S", "T", "V2", "E"})
        self.assertEqual([(e[1], e[2]) for e in found["E"] if e[0] == "R"], [("S", "T")])
        # EXCLUDE_CATEGORIES Isolating: every other junction is a candidate; S, E (one connection, kept by default)
        # and T (three) are not reducible, so nothing changes.
        self.template("Others")
        arcpy.nd.AddReduceJunctionByCategoryRule(self.un, "Others", "ACTIVE", "EXCLUDE_CATEGORIES", "Isolating")
        self.assertEqual(set(self.content(self.create("Others"))["J"]), {"S", "V1", "T", "V2", "E"})
        with self.assertRaises(ExecuteError):  # including categories needs one
            arcpy.nd.AddReduceJunctionByCategoryRule(self.un, "Blank", "ACTIVE", "INCLUDE_CATEGORIES", None)

    def test_remove_feature_by_category_removes_the_category_s_junctions(self):
        # V1 and V2 (Isolating) removed; a removed junction takes its edges (INFERRED, ADR-232 §5.9 (c)): L1, L2, L3.
        # The vault, left empty, is drawn as a junction: a polygon only with SAVE_EMPTY_CONTAINER_AS_POLYGON_IF_
        # UNCONNECTED, as the Add Diagram Feature Capability pages offer it.
        self.template()
        arcpy.nd.AddRemoveFeatureByCategoryRule(self.un, "Blank", "ACTIVE", "JUNCTIONS", "INCLUDE_CATEGORIES",
                                                ["Isolating"])
        step = self.last_step("Blank")
        self.assertEqual((step["rule"], step["params"]["source_type"], step["params"]["selector"]["categories"]),
                         ("RemoveFeature", "JUNCTIONS", ["Isolating"]))
        found = self.content(self.create())
        self.assertEqual((set(found["J"]), found["C"]), ({"S", "T", "E", "VA"}, {}))
        self.assertEqual([e[0] for e in found["E"]], ["L4"])
        # EDGES of an edge-less category removes nothing: no line is Isolating.
        self.template("Edges")
        arcpy.nd.AddRemoveFeatureByCategoryRule(self.un, "Edges", "ACTIVE", "EDGES", "INCLUDE_CATEGORIES",
                                                ["Isolating"])
        self.assertEqual(len(self.content(self.create("Edges"))["E"]), 4)

    def test_collapse_container_by_attribute_collapses_the_selected_vault(self):
        # The page: the container's content is aggregated into it and the edges from the content reconnected to it,
        # by default as one reduction edge: VA becomes a junction at its point (30, 0), V2 goes, L3 becomes T-VA.
        self.template()
        arcpy.un.AddCollapseContainerByAttributeRule(self.un, "Blank", "ACTIVE", self.vault, "NAME = 'VA'")
        step = self.last_step("Blank")
        self.assertEqual((step["rule"], step["params"]["selector"], step["params"]["aggregate"]),
                         ("CollapseContainer", {"by": "attribute", "source": "StructureJunction",
                                                "where": "NAME = 'VA'"}, True))
        name = self.create()
        found = self.content(name)
        self.assertEqual(found["C"], {})
        self.assertEqual(found["J"]["VA"], (30, 0))
        self.assertNotIn("V2", found["J"])
        self.assertEqual([(e[1], e[2]) for e in found["E"] if e[0] == "R"], [("T", "VA")])
        self.template("None")
        arcpy.nd.AddCollapseContainerByAttributeRule(self.un, "None", "ACTIVE", self.vault, "NAME = 'nothing'")
        self.assertIn("VA", self.content(self.create("None"))["C"])

    def test_collapse_and_expand_container_by_category(self):
        self.chamber()
        self.template()
        arcpy.nd.AddCollapseContainerByCategoryRule(self.un, "Blank", "ACTIVE", "BOTH", "INCLUDE_CATEGORIES",
                                                    ["Chamber"])
        self.assertEqual(self.last_step("Blank")["params"]["selector"],
                         {"by": "category", "include": True, "categories": ["Chamber"]})
        found = self.content(self.create())
        self.assertEqual((found["C"], found["J"]["VA"]), ({}, (30, 0)))
        self.template("Other")
        arcpy.nd.AddCollapseContainerByCategoryRule(self.un, "Other", "ACTIVE", "BOTH", "EXCLUDE_CATEGORIES",
                                                    ["Chamber"])
        self.assertEqual(self.content(self.create("Other"))["C"], {"VA": (29.5, -0.5, 30.5, 0.5)})
        # Expand: a diagram of the vault alone gains its content, V2; HIDE drops the container itself.
        arcpy.management.MakeFeatureLayer(self.vault, "vaults")
        self.template("Expand")
        arcpy.nd.AddExpandContainerByCategoryRule(self.un, "Expand", "ACTIVE", "KEEP_VISIBLE", "BOTH",
                                                  "INCLUDE_CATEGORIES", ["Chamber"])
        step = self.last_step("Expand")
        self.assertEqual((step["rule"], step["params"]["hide"]), ("ExpandContainer", False))
        found = self.content(self.create("Expand", ["vaults"]))
        self.assertEqual((set(found["J"]), found["C"]), ({"V2"}, {"VA": (29.5, -0.5, 30.5, 0.5)}))
        self.template("Hidden")
        arcpy.nd.AddExpandContainerByCategoryRule(self.un, "Hidden", "ACTIVE", "HIDE", "BOTH", "INCLUDE_CATEGORIES",
                                                  ["Chamber"])
        found = self.content(self.create("Hidden", ["vaults"]))
        self.assertEqual((set(found["J"]), found["C"]), ({"V2"}, {}))
        self.template("NotIsolating")  # a category no container has expands nothing: the vault alone, a point
        arcpy.nd.AddExpandContainerByCategoryRule(self.un, "NotIsolating", "ACTIVE", "KEEP_VISIBLE", "BOTH",
                                                  "INCLUDE_CATEGORIES", ["Isolating"])
        found = self.content(self.create("NotIsolating", ["vaults"]))
        self.assertEqual((found["J"], found["C"]), ({"VA": (30, 0)}, {}))

    def test_a_capability_by_category_holds_back_a_later_rule(self):
        # PREVENT_TO_REDUCE_JUNCTION on Isolating: the Reduce Junction rule after it leaves V1 alone. The valences
        # say which junctions it is set on by their count of connected junctions; V1 has two, so DO_NOT_APPLY at
        # valence 2 leaves V1 reducible.
        self.template()
        arcpy.nd.AddDiagramFeatureCapabilityByCategoryRule(self.un, "Blank", "ACTIVE", "INCLUDE_CATEGORIES",
                                                           ["Isolating"], "PREVENT_TO_REDUCE_JUNCTION")
        step = self.last_step("Blank")
        self.assertEqual((step["rule"], step["params"]["capability"], step["params"]["valences"]),
                         ("Capability", "PREVENT_TO_REDUCE_JUNCTION", [True, True, True, True]))
        arcpy.nd.AddReduceJunctionRule(self.un, "Blank", "ACTIVE", "INCLUDE_SOURCE_CLASSES", [self.device])
        found = self.content(self.create())
        self.assertIn("V1", found["J"])
        self.assertEqual([e for e in found["E"] if e[0] == "R"], [])
        self.template("Two")
        arcpy.nd.AddDiagramFeatureCapabilityByCategoryRule(self.un, "Two", "ACTIVE", "INCLUDE_CATEGORIES",
                                                           ["Isolating"], "PREVENT_TO_REDUCE_JUNCTION",
                                                           valence_2="DO_NOT_APPLY")
        self.assertEqual(self.last_step("Two")["params"]["valences"], [True, True, False, True])
        arcpy.nd.AddReduceJunctionRule(self.un, "Two", "ACTIVE", "INCLUDE_SOURCE_CLASSES", [self.device])
        self.assertNotIn("V1", self.content(self.create("Two"))["J"])
        # PREVENT_TO_COLLAPSE_CONTAINER on the vault's category keeps it from a Collapse Container rule.
        self.chamber()
        self.template("Kept")
        arcpy.nd.AddDiagramFeatureCapabilityByCategoryRule(self.un, "Kept", "ACTIVE", "INCLUDE_CATEGORIES",
                                                           ["Chamber"], "PREVENT_TO_COLLAPSE_CONTAINER")
        arcpy.nd.AddCollapseContainerRule(self.un, "Kept")
        self.assertIn("VA", self.content(self.create("Kept"))["C"])

    def test_structural_attachments_are_drawn_from_the_structure(self):
        # The page: a straight edge from the structure to the attached feature, both being in the diagram.
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.AddRule(self.un, "STRUCTURAL_ATTACHMENT", "StructureJunction", "Vault", "Concrete", "WaterJunction",
                         "Tee", "Standard")
        arcpy.un.EnableNetworkTopology(self.un)
        arcpy.un.UtilityNetwork(self.un).AddStructuralAttachmentAssociation("StructureJunction", self.ids["VA"],
                                                                            "WaterJunction", self.ids["E"])
        arcpy.un.ValidateNetworkTopology(self.un)
        self.template()
        self.assertEqual(len(self.content(self.create())["E"]), 4)  # without the rule, no attachment edge
        arcpy.un.AddStructuralAttachmentsRule(self.un, "Blank")
        step = self.last_step("Blank")
        self.assertEqual((step["kind"], step["rule"], step["active"], step["params"]),
                         ("rule", "StructuralAttachments", True, {}))
        edges = self.content(self.create())["E"]
        self.assertEqual(len(edges), 5)
        self.assertIn(("VA", "E"), [(e[1], e[2]) for e in edges])
        # Only when both ends are in the diagram: a diagram of L4 alone (T and E) has no vault to draw it from.
        arcpy.management.MakeFeatureLayer(self.line, "lines")
        arcpy.management.SelectLayerByAttribute("lines", "NEW_SELECTION", "NAME = 'L4'")
        self.assertEqual([e[0] for e in self.content(self.create(features=["lines"]))["E"]], ["L4"])
        self.template("Off")
        arcpy.nd.AddStructuralAttachmentsRule(self.un, "Off", "INACTIVE")
        self.assertEqual(len(self.content(self.create("Off"))["E"]), 4)

    # -- layouts a template presets: stored with their parameters, run when a diagram is made --

    def test_linear_dispatch_preset_runs_when_the_diagram_is_made(self):
        # ITERATIVE_DISTANCE moves V1 toward its even place, midway on S-T at x 10, by an equal share of the maximum
        # shift per iteration (INFERRED, _ndlayout.linear_dispatch): 2 junction sizes over 2 iterations, 1 each: x 8.
        self.template()
        arcpy.un.AddLinearDispatchLayout(self.un, "Blank", "ACTIVE", "ITERATIVE_DISTANCE", "PROPORTIONAL_UNIT",
                                         maximum_shift_proportional=2, iterations_number=2,
                                         is_path_preserved="IGNORE_PATH")
        self.assertEqual(self.last_step("Blank"), {"kind": "layout", "layout": "LinearDispatch", "active": True,
                                                   "params": {"placement": "ITERATIVE_DISTANCE", "maximum_shift": 2.0,
                                                              "iterations": 2, "preserve_path": False}})
        found = self.content(self.create())
        self.assertEqual(found["J"]["V1"], (8, 0))
        self.assertEqual({e[0]: e[3] for e in found["E"]}["L1"], [(0, 0), (8, 0)])
        self.template("Even")  # EQUAL_DISTANCE: V1 midway, (10, 0)
        arcpy.nd.AddLinearDispatchLayout(self.un, "Even", "ACTIVE", "EQUAL_DISTANCE")
        self.assertEqual(self.content(self.create("Even"))["J"]["V1"], (10, 0))

    def test_spatial_dispatch_preset_runs_when_the_diagram_is_made(self):
        # A separation of 7 junction sizes (INFERRED, _ndlayout.spatial_dispatch): only S and V1 are nearer, 6 apart;
        # each is pushed half the shortfall, 0.5, away from the other.
        self.template()
        arcpy.un.AddSpatialDispatchLayout(self.un, "Blank", "ACTIVE", None, 1, 7)
        self.assertEqual(self.last_step("Blank")["params"], {"iterations": 1, "maximum_shift_factor": 7.0})
        found = self.content(self.create())["J"]
        self.assertEqual((found["S"], found["V1"], found["T"], found["E"]), ((-0.5, 0), (6.5, 0), (20, 0), (20, 10)))

    def test_compression_preset_runs_when_the_diagram_is_made(self):
        # Groups by edges of 6 m or less: {S, V1} (centre 3, 0), {T}, {V2}, {E}. The x gaps 3→20→30 shrink to 6 and
        # the whole keeps its middle: 3, 20, 30 → 10.5, 16.5, 22.5; y 0→10 → 2, 8. ALL removes every edge vertex.
        self.template()
        arcpy.nd.AlterDiagramTemplate(self.un, "Blank", are_vertices_kept="KEEP_VERTICES")
        arcpy.un.AddCompressionLayout(self.un, "Blank", "ACTIVE", None, "6 Meters", "ALL")
        self.assertEqual(self.last_step("Blank")["params"], {"grouping_distance": 6.0, "vertices_removal": "ALL"})
        found = self.content(self.create())
        self.assertEqual(found["J"], {"S": (7.5, 2), "V1": (13.5, 2), "T": (16.5, 2), "V2": (22.5, 2), "E": (16.5, 8)})
        self.assertEqual({e[0]: e[3] for e in found["E"]}["L2"], [(13.5, 2), (16.5, 2)])

    def test_angle_directed_preset_turns_an_edge_to_the_nearest_direction(self):
        # L5 runs from T at 33.69 degrees, 7.2111 long. Its to junction is moved, the length kept (INFERRED,
        # _ndlayout.angle_directed): four directions, nearest 0 → F at (27.2111, 0); eight, nearest 45 → (25.099, 5.099).
        self.branch()
        self.template()
        arcpy.un.AddAngleDirectedLayout(self.un, "Blank", "ACTIVE", None, 1, "FOUR_DIRECTIONS")
        self.assertEqual(self.last_step("Blank")["params"], {"iterations": 1, "directions": "FOUR_DIRECTIONS"})
        found = self.content(self.create())["J"]
        length = math.hypot(6, 4)
        self.near(found["F"], (20 + length, 0), 5)
        self.assertEqual((found["T"], found["E"], found["V1"]), ((20, 0), (20, 10), (6, 0)))  # already on 0 or 90
        self.template("Eight")
        arcpy.nd.AddAngleDirectedLayout(self.un, "Eight", "ACTIVE", None, 1, "EIGHT_DIRECTIONS")
        self.near(self.content(self.create("Eight"))["J"]["F"], (20 + length / math.sqrt(2), length / math.sqrt(2)), 5)

    def test_force_directed_preset_balances_repulsion_and_attraction(self):
        # A diagram of L4 alone: T (20, 0), E (20, 10), ideal length k = 10. Repulsion repel·k²/d against attraction
        # d²/k (Fruchterman and Reingold, _ndlayout.force_directed): with repel 1 they cancel and nothing moves; with
        # repel 2 the net 10 pushes each apart by the temperature, LOW 0.1·k = 1, in the one iteration.
        arcpy.management.MakeFeatureLayer(self.line, "lines")
        arcpy.management.SelectLayerByAttribute("lines", "NEW_SELECTION", "NAME = 'L4'")
        self.template()
        arcpy.un.AddForceDirectedLayout(self.un, "Blank", "ACTIVE", None, 1, 2, "LOW")
        self.assertEqual(self.last_step("Blank")["params"], {"iterations": 1, "repel_factor": 2.0,
                                                             "degree_freedom": "LOW"})
        found = self.content(self.create(features=["lines"]))["J"]
        self.assertEqual(found, {"T": (20, -1), "E": (20, 11)})
        self.template("Still")
        arcpy.nd.AddForceDirectedLayout(self.un, "Still", "ACTIVE", None, 1, 1, "LOW")
        self.assertEqual(self.content(self.create("Still", ["lines"]))["J"], {"T": (20, 0), "E": (20, 10)})
        with self.assertRaises(ExecuteError):  # a curved edge breaks from 15 to 40 per cent along it
            arcpy.nd.AddForceDirectedLayout(self.un, "Still", "ACTIVE", breakpoint_position=50,
                                            edge_display_type="CURVED_EDGES")

    def test_mainline_tree_preset_from_the_root_junction(self):
        # From S, left to right, 3 m along and 4 m across, branches on the left only. The main line is the longest
        # branch from the root: S, V1, T and one of V2 or E (equally deep, the lower feature id, INFERRED) at x 0, 3, 6,
        # 9; the other leaves T as a branch on the left, 4 across: (6, 4).
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Blank", "ACTIVE", self.device, "NAME = 'S'")
        arcpy.un.AddMainlineTreeLayout(self.un, "Blank", "ACTIVE", None, "FROM_LEFT_TO_RIGHT", "LEFT_SIDE",
                                       "ABSOLUTE_UNIT", "4 Meters", None, "3 Meters")
        self.assertEqual(self.last_step("Blank")["params"], {"direction": "FROM_LEFT_TO_RIGHT", "placement": "LEFT_SIDE",
                                                             "perpendicular": 4.0, "along": 3.0})
        found = self.content(self.create())["J"]
        self.assertEqual([found[n] for n in ("S", "V1", "T")], [(0, 0), (3, 0), (6, 0)])
        self.assertEqual(sorted([found["E"], found["V2"]]), [(6, 4), (9, 0)])

    def test_radial_tree_preset_from_the_root_junction(self):
        # The root S at its own place; level radii 4, 4 + 8 = 12, 12 + 16 = 28 (radius factor 2, INFERRED). V1 and T
        # each the one child of their parent: the middle of the whole circle, angle π; E and V2 split T's wedge in
        # halves, at π/2 and 3π/2 on the third circle.
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Blank", "ACTIVE", self.device, "NAME = 'S'")
        arcpy.un.AddRadialTreeLayout(self.un, "Blank", "ACTIVE", None, "ABSOLUTE_UNIT", "4 Meters", radius_factor=2)
        self.assertEqual(self.last_step("Blank")["params"], {"initial_radius": 4.0, "radius_factor": 2.0})
        found = self.content(self.create())["J"]
        self.assertEqual((found["S"], found["V1"], found["T"]), ((0, 0), (-4, 0), (-12, 0)))
        self.assertEqual(sorted([found["E"], found["V2"]]), [(0, -28), (0, 28)])

    def test_main_ring_preset_and_apply(self):
        # L7 from E to V1 makes the loop V1, T, E, centred at their mean (46/3, 10/3). On an ellipse 12 by 12 — a
        # circle of radius 6 — the three are 120 degrees apart, the lowest feature id due east (INFERRED); S and V2
        # hang outward from V1 and T, one level of 2 further from the centre.
        self.template()
        arcpy.un.AddMainRingLayout(self.un, "Blank", "ACTIVE", None, "ELLIPSE", "PROPORTIONAL_UNIT",
                                   ring_width_proportional=12, ring_height_proportional=12)
        self.assertEqual(self.last_step("Blank")["params"], {"ring_type": "ELLIPSE", "ring_width": 12.0,
                                                             "ring_height": 12.0})
        no_loop = self.create()
        self.assertEqual(self.content(no_loop)["J"]["V1"], (6, 0))  # no loop: the page says no effect
        self.lines([("L7", 1, 1, "LINESTRING(20 10, 6 0)")])
        found = self.content(self.create())["J"]
        c = (46 / 3, 10 / 3)
        ring = [found[n] for n in ("V1", "T", "E")]
        for x, y in ring:
            self.assertAlmostEqual(math.hypot(x - c[0], y - c[1]), 6, places=5)
        for i in range(3):
            self.assertAlmostEqual(math.dist(ring[i], ring[(i + 1) % 3]), 6 * math.sqrt(3), places=5)
        self.assertTrue(any(abs(x - (c[0] + 6)) < 1e-5 and abs(y - c[1]) < 1e-5 for x, y in ring))
        for leaf, hub in (("S", "V1"), ("V2", "T")):
            hx, hy = found[hub]
            self.near(found[leaf], (c[0] + (hx - c[0]) * 8 / 6, c[1] + (hy - c[1]) * 8 / 6), 5)
        # Apply: a rectangle 12 by 6 round the same centre, perimeter 36, the three 12 apart along it counterclockwise
        # from the middle of its east side (6, 0): 3 north and 9 west to (-3, 3), then 3 west, 6 south, 3 east to
        # (-3, -3).
        name = self.create()
        arcpy.un.ApplyMainRingLayout(name, None, "RECTANGLE", "ABSOLUTE_UNIT", "12 Meters", None, "6 Meters")
        found = self.content(name)["J"]
        got = sorted((round(x - c[0], 5), round(y - c[1], 5)) for x, y in (found[n] for n in ("V1", "T", "E")))
        self.assertEqual(got, [(-3, -3), (-3, 3), (6, 0)])
        arcpy.nd.ApplyMainRingLayout(no_loop)  # made before L7: its content has no loop, so nothing moves
        self.assertEqual(self.content(no_loop)["J"]["V1"], (6, 0))

    def test_partial_overlapping_edges_preset_inactive_then_applied(self):
        # L6 joins V1 and T beside L2; drawn straight both run (6, 0)-(20, 0). Within the 0.5 m buffer they form one
        # set of two, spread offset 2 apart about their middle: one to y -1, the other to y +1, each keeping its
        # junctions and gaining a vertex at each end.
        self.lines([("L6", 1, 1, "LINESTRING(6 0, 13 1, 20 0)")])
        self.template()
        arcpy.un.AddPartialOverlappingEdgesLayout(self.un, "Blank", "INACTIVE", "0.5 Meters", "2 Meters")
        self.assertEqual(self.last_step("Blank"), {"kind": "layout", "layout": "PartialOverlappingEdges",
                                                   "active": False, "params": {"buffer_width": 0.5, "offset": 2.0}})
        name = self.create()
        edges = {e[0]: e[3] for e in self.content(name)["E"]}
        self.assertEqual((edges["L2"], edges["L6"]), ([(6, 0), (20, 0)], [(6, 0), (20, 0)]))  # inactive: not run
        arcpy.un.ApplyPartialOverlappingEdgesLayout(name)  # the template's values, as the page says
        edges = {e[0]: e[3] for e in self.content(name)["E"]}
        spread = sorted([edges["L2"], edges["L6"]])
        self.assertEqual(spread, [[(6, 0), (6, -1), (20, -1), (20, 0)], [(6, 0), (6, 1), (20, 1), (20, 0)]])
        self.assertEqual(edges["L1"], [(0, 0), (6, 0)])  # meets L2 end to end only: not overlapping
        self.template("On")
        arcpy.nd.AddPartialOverlappingEdgesLayout(self.un, "On", "ACTIVE", "0.5 Meters", "4 Meters")
        edges = {e[0]: e[3] for e in self.content(self.create("On"))["E"]}
        self.assertEqual(sorted([edges["L2"], edges["L6"]]),
                         [[(6, 0), (6, -2), (20, -2), (20, 0)], [(6, 0), (6, 2), (20, 2), (20, 0)]])
        with self.assertRaises(ExecuteError):  # buffer width and offset are required
            arcpy.nd.AddPartialOverlappingEdgesLayout(self.un, "On", "ACTIVE", None, "4 Meters")
        self.template("Bare")
        bare = self.create("Bare")
        with self.assertRaises(ExecuteError):
            arcpy.nd.ApplyPartialOverlappingEdgesLayout(bare)
        arcpy.nd.ApplyPartialOverlappingEdgesLayout(bare, "0.5 Meters", "2 Meters", "OPTIMIZE_EDGES")
        edges = {e[0]: e[3] for e in self.content(bare)["E"]}
        self.assertEqual(sorted([edges["L2"], edges["L6"]]),
                         [[(6, 0), (6, -1), (20, -1), (20, 0)], [(6, 0), (6, 1), (20, 1), (20, 0)]])

    def test_reshape_edges_preset_and_apply(self):
        # REMOVE_VERTICES: L2, drawn with its vertex (13, 0) when vertices are kept, loses it.
        self.template()
        arcpy.nd.AlterDiagramTemplate(self.un, "Blank", are_vertices_kept="KEEP_VERTICES")
        self.assertEqual({e[0]: e[3] for e in self.content(self.create())["E"]}["L2"], [(6, 0), (13, 0), (20, 0)])
        arcpy.un.AddReshapeDiagramEdgesLayout(self.un, "Blank", "ACTIVE", None, "REMOVE_VERTICES")
        self.assertEqual(self.last_step("Blank")["params"], {"reshape_type": "REMOVE_VERTICES"})
        self.assertEqual({e[0]: e[3] for e in self.content(self.create())["E"]}["L2"], [(6, 0), (20, 0)])
        # REDUCE_VERTICES_BY_ANGLE: the vertex (13, 0) is a straight angle, 180 >= 160 → removed.
        self.template("Kept")
        arcpy.nd.AlterDiagramTemplate(self.un, "Kept", are_vertices_kept="KEEP_VERTICES")
        name = self.create("Kept")
        arcpy.un.ReshapeDiagramEdgesLayout(name, None, "REDUCE_VERTICES_BY_ANGLE", angle_threshold=160)
        self.assertEqual({e[0]: e[3] for e in self.content(name)["E"]}["L2"], [(6, 0), (20, 0)])
        # SQUARE_EDGES: L5 from T (20, 0) to F (26, 4) runs more east than north, so it breaks 2 m east of T, goes
        # north and then east to F; edges already horizontal or vertical are left alone.
        self.branch()
        name = self.create("Kept")
        arcpy.nd.ReshapeDiagramEdgesLayout(name, None, "SQUARE_EDGES", None, None, "2 Meters")
        edges = {e[0]: e[3] for e in self.content(name)["E"]}
        self.assertEqual(edges["L5"], [(20, 0), (22, 0), (22, 4), (26, 4)])
        self.assertEqual(edges["L4"], [(20, 0), (20, 10)])
        with self.assertRaises(ExecuteError):
            arcpy.nd.ReshapeDiagramEdgesLayout(name, None, "NO_SUCH_TYPE")

    # -- network categories --

    def test_delete_network_category(self):
        # doc.esri.com …/utility-networks/delete-network-category.html: the network topology must be disabled; a
        # category assigned to an asset type cannot be deleted, and Set Network Category unassigns it.
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.DeleteNetworkCategory(self.un, "Isolating")
        self.assertIn("disabled", str(said.exception))
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.AddNetworkCategory(self.un, "Spare")
        self.assertIn("Spare", self.categories())
        result = arcpy.un.DeleteNetworkCategory(self.un, "Spare")
        self.assertEqual(result[0], self.un)
        self.assertNotIn("Spare", self.categories())
        with self.assertRaises(ExecuteError):  # gone: it can no longer be assigned
            arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Valve", "Standard", ["Spare"])
        arcpy.un.AddNetworkCategory(self.un, "Spare")  # and its name is free again
        with self.assertRaises(ExecuteError):  # a name that is no category (INFERRED: the page names none)
            arcpy.un.DeleteNetworkCategory(self.un, "Nope")
        with self.assertRaises(ExecuteError) as said:  # in use by the valves
            arcpy.un.DeleteNetworkCategory(self.un, "Isolating")
        self.assertIn("assigned", str(said.exception))
        self.assertIn("Isolating", self.categories())
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Valve", "Standard")  # none: unassigned
        arcpy.un.DeleteNetworkCategory(self.un, "Isolating")
        self.assertNotIn("Isolating", self.categories())
        # A system-provided category is refused (INFERRED: the page does not say; Pro's are the network's own).
        with self.assertRaises(ExecuteError):
            arcpy.un.DeleteNetworkCategory(self.un, "Subnetwork Tap")
        self.assertIn("Subnetwork Tap", self.categories())


if __name__ == "__main__":
    unittest.main()
