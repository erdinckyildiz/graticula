"""arcpy.nd answers pinned to what ArcGIS Pro 3.7 answered in the Pro sweep (measured 2026-11-12,
tools/pro-compare/sweep/cases/nd.py, results/nd.md), on ADR-232 §4's water network (test_network_diagrams.py):

    S ─L1─ V1 ──────L2────── T ─L3─ V2        V2 in the vault VA; a tee–valve association T → V1 added here
                             │
                             L4
                             │
                             E
"""
import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.dirname(__file__))

import grapy as arcpy  # noqa: E402
from grapy._runtime import ExecuteError  # noqa: E402
import test_network_diagrams as _diagrams  # noqa: E402 — its helpers, not its tests


class ProSweepNd(unittest.TestCase):
    # the water network and its helpers, without running test_network_diagrams' own tests again
    setUp = _diagrams.Diagrams.setUp
    D = _diagrams.Diagrams
    insert, name_of, content, template, create = (D.insert, D.name_of, D.content, D.template, D.create)

    def associate(self):
        """The junction-junction connectivity association T → V1 (the tee is its from feature)."""
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.UtilityNetwork(self.un).AddConnectivityAssociation(1, "WaterJunction", self.ids["T"], None,
                                                                    "WaterDevice", self.ids["V1"], None)
        arcpy.un.EnableNetworkTopology(self.un)

    def test_a_new_network_has_pro_s_three_templates(self):
        # Pro: GetDiagramTemplateNames on a new network answers Basic, CollapseContainers and ExpandContainers.
        self.assertEqual(sorted(arcpy.nd.GetDiagramTemplateNames(self.un)[0].split(";")),
                         ["Basic", "CollapseContainers", "ExpandContainers"])

    def test_basic_keeps_vertices_and_draws_associations(self):
        # Pro's Basic diagram of every feature: L2 keeps its vertex at (13, 0), the association is an edge V1 → T.
        self.associate()
        found = self.content(self.create("Basic"))
        edges = {(e[1], e[2]): e for e in found["E"]}
        self.assertEqual(edges[("V1", "T")][0], None)       # an association has no network feature name
        l2 = next(e for e in found["E"] if e[0] == "L2")
        self.assertEqual(l2[3], [(6, 0), (13, 0), (20, 0)])
        self.assertEqual(found["C"], {"VA": (29.5, -0.5, 30.5, 0.5)})

    def test_collapse_containers_folds_the_vault(self):
        # Pro's CollapseContainers: VA becomes a junction at (30, 0) and V2 goes into it; L3 runs T → VA.
        found = self.content(self.create("CollapseContainers"))
        self.assertEqual(found["C"], {})
        self.assertIn("VA", found["J"])
        self.assertNotIn("V2", found["J"])

    def test_a_category_rule_needs_a_category(self):
        # Pro: ERROR 000735 "Network Categories: Value is required" for every by-category rule, EXCLUDE included.
        self.template()
        for tool, args in (("AddReduceJunctionByCategoryRule", ["ACTIVE", "EXCLUDE_CATEGORIES"]),
                           ("AddRemoveFeatureByCategoryRule", ["ACTIVE", "BOTH", "EXCLUDE_CATEGORIES"]),
                           ("AddDiagramFeatureCapabilityByCategoryRule", ["ACTIVE"]),
                           ("AddCollapseContainerByCategoryRule", ["ACTIVE"]),
                           ("AddExpandContainerByCategoryRule", ["ACTIVE"])):
            with self.assertRaises(ExecuteError, msg=tool) as said:
                getattr(arcpy.nd, tool)(self.un, "Blank", *args)
            self.assertIn("000735", str(said.exception))
        with self.assertRaises(ExecuteError) as said:
            arcpy.nd.AddDiagramFeatureSelectionToLayout(self.un, "Blank", "ACTIVE", "SELECT_BY_CATEGORY")
        self.assertIn("000663", str(said.exception))

    def test_a_trace_adds_to_the_diagram_with_the_associations_it_passes(self):
        # Pro: a CONNECTED trace from L4 alone gives every line and junction, and the association drawn T → V1 (its
        # from feature first) though the template has no Connectivity Associations rule.
        self.associate()
        self.template()
        arcpy.nd.AddTraceRule(self.un, "Blank", "ACTIVE", "CONNECTED")
        arcpy.management.MakeFeatureLayer(self.line, "l4", "NAME = 'L4'")
        found = self.content(self.create(features=["l4"]))
        self.assertEqual(set(found["J"]), {"S", "V1", "T", "V2", "E"})
        self.assertIn((None, "T", "V1"), [tuple(e[:3]) for e in found["E"]])
        self.assertEqual(sorted(e[0] for e in found["E"] if e[0]), ["L1", "L2", "L3", "L4"])

    def test_a_spatial_query_brings_the_container_of_what_it_adds(self):
        # Pro: valves within 15 m of the tees come in, V2 with its vault VA.
        self.template()
        arcpy.nd.AddSpatialQueryRule(self.un, "Blank", "ACTIVE", self.device, "WITHIN_A_DISTANCE", self.junction,
                                     "15 Meters")
        found = self.content(self.create(features=[self.junction]))
        self.assertEqual(set(found["J"]), {"T", "E", "V1", "V2"})
        self.assertEqual(list(found["C"]), ["VA"])

    def test_the_association_rule_draws_from_the_first_junction(self):
        # Pro: the association imported T → V1 is drawn V1 → T by the Connectivity Associations rule.
        self.associate()
        self.template()
        arcpy.nd.AddConnectivityAssociationsRule(self.un, "Blank")
        found = self.content(self.create())
        self.assertIn((None, "V1", "T"), [tuple(e[:3]) for e in found["E"]])

    def test_a_diagram_layer_is_described_as_one(self):
        # Pro: Describe of MakeDiagramLayer's layer answers dataType DiagramLayer.
        self.template()
        diagram = self.create()
        layer = arcpy.nd.MakeDiagramLayer(self.un, diagram, "view")[0]
        self.assertEqual(arcpy.Describe(layer).dataType, "DiagramLayer")

    def test_a_smart_tree_slants_its_edges_to_a_breakpoint_of_30_per_cent(self):
        # Pro, ApplySmartTreeLayout with its defaults on a tree whose levels are 10 apart: T (20, -5) → V2 (30, -10)
        # through (23, -10), T → E through (23, 0); S → V1, straight along, has no bend.
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Blank", "ACTIVE", self.device, "NAME = 'S'")
        diagram = self.create()
        arcpy.nd.ApplySmartTreeLayout(arcpy.nd.MakeDiagramLayer(self.un, diagram, "tree")[0])
        found = self.content(diagram)
        edges = {e[0]: e[3] for e in found["E"]}
        for name in ("L3", "L4"):
            (ax, ay), (bx, by), (cx, cy) = edges[name]
            self.assertAlmostEqual(bx, ax + 0.3 * (cx - ax))
            self.assertAlmostEqual(by, cy)
        self.assertEqual(len(edges["L1"]), 2)

    def test_export_keys_attributes_by_pro_s_aliases(self):
        # Pro's ExportDiagramContent: S's attributes by alias ("Asset group", "Object ID", "Tier name" …), sourceMapping
        # only the classes in the diagram under their aliases, Web Mercator as wkid 102100.
        import json
        import tempfile
        self.template()
        diagram = self.create()
        out = os.path.join(tempfile.mkdtemp(), "content.json")
        arcpy.nd.ExportDiagramContent(self.un, diagram, out, "INCLUDE_DIAGRAM_PROPERTIES", "INCLUDE_GEOMETRIES",
                                      "INCLUDE_ATTRIBUTES")
        with open(out, encoding="utf-8") as read:
            doc = json.load(read)
        s = next(j for j in doc["junctions"] if j["assocGlobalID"].upper() == self.ids["S"])
        self.assertEqual(sorted(s["attributes"]), [
            "Asset group", "Asset type", "Association status", "Global ID", "Is connected", "Is subnetwork controller",
            "NAME", "Object ID", "Subnetwork controller name", "Subnetwork name", "Supported subnetwork name",
            "Supporting subnetwork name", "Terminal path", "Tier name", "Tier rank"])
        self.assertEqual(sorted(doc["sourceMapping"]), ["Structure Junction", "Water Device", "Water Junction",
                                                        "Water Line"])
        self.assertEqual(doc["spatialRef"], {"wkid": 102100, "latestWkid": 3857})

    def test_geo_positions_draw_edges_straight_by_default(self):
        # Pro: ApplyGeoPositionsLayout with no values drew L2 without its vertex at (13, 0).
        self.template()
        diagram = self.create()
        arcpy.nd.ApplyGeoPositionsLayout(diagram)
        self.assertEqual({e[0]: e[3] for e in self.content(diagram)["E"]}["L2"], [(6, 0), (20, 0)])

    def test_a_mainline_tree_is_fitted_and_its_branch_starts_a_step_along(self):
        # Pro: 10 apart on a diagram 30 wide, E at T + (10, 10) (the branch one step along as well as across).
        self.template()
        arcpy.nd.AddSetRootJunctionByAttributeRule(self.un, "Blank", "ACTIVE", self.device, "NAME = 'S'")
        diagram = self.create()
        arcpy.nd.ApplyMainlineTreeLayout(diagram)
        found = self.content(diagram)["J"]
        self.assertEqual((found["T"][0] - found["V1"][0], found["V1"][0] - found["S"][0]), (10, 10))
        self.assertEqual({found["E"], found["V2"]}, {(found["T"][0] + 10, found["T"][1]),
                                                     (found["T"][0] + 10, found["T"][1] + 10)})

    def test_partial_overlapping_edges_need_no_values(self):
        # Pro: the association V1 → T on L2, spread with no values: L2 through (6.5, 0.25) and (19.5, 0.25), the
        # association through (6.5, -0.25) and (19.5, -0.25); with 2 and 1: (8, ±0.5) and (18, ±0.5).
        self.associate()
        self.template()
        arcpy.nd.AddConnectivityAssociationsRule(self.un, "Blank")
        for args, along, half in (((), 0.5, 0.25), (("2 Meters", "1 Meters"), 2, 0.5), (("1 Meters", "2 Meters"), 2, 1)):
            diagram = self.create()
            arcpy.nd.ApplyPartialOverlappingEdgesLayout(diagram, *args)
            edges = {e[0]: e[3] for e in self.content(diagram)["E"]}
            self.assertEqual(edges["L2"], [(6, 0), (6 + along, half), (20 - along, half), (20, 0)])
            self.assertEqual(edges[None], [(6, 0), (6 + along, -half), (20 - along, -half), (20, 0)])

    def test_apply_partial_overlapping_edges_wants_both_values_or_neither(self):
        # Pro (the un sweep, measured in ArcGIS Pro 3.7, 2026-11-12): Apply with the buffer width alone is ERROR 000530;
        # with neither it runs (above). INFERRED from those two that the offset alone is refused the same way.
        self.associate()
        self.template()
        diagram = self.create()
        for args in (("1 Meters",), (None, "1 Meters")):
            with self.assertRaisesRegex(arcpy.ExecuteError, "000530"):
                arcpy.nd.ApplyPartialOverlappingEdgesLayout(diagram, *args)


if __name__ == "__main__":
    unittest.main()
