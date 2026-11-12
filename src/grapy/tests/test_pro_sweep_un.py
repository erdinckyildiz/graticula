"""arcpy.un aligned to ArcGIS Pro 3.7 — the Pro sweep of 2026-11-12 (tools/pro-compare/sweep/cases/un.py,
results/un.md). Each test pins what Pro was measured doing, on the water network of test_utility_network (WaterNetwork:
a source S with a Source-Load configuration, valves V1 and V2, tees T and T2, a check valve CV, lines L1…L9, a vault VA
containing V2). Measured in ArcGIS Pro 3.7, 2026-11-12, in a file geodatabase.
"""
import csv
import json
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy._runtime import ExecuteError  # noqa: E402
import test_utility_network as water  # noqa: E402 — the module, so that its tests are not collected here again


class ProSweepUn(unittest.TestCase):
    def setUp(self):
        self.w = water.WaterNetwork("test_update_is_connected")
        self.w.setUp()
        self.un = self.w.un

    def describe(self):
        return arcpy.Describe(self.un)

    # -- the definition --

    def test_describe_answers_the_network_definition(self):
        d = self.describe()
        self.assertEqual((d.dataType, d.name, d.serviceTerritoryFeatureClassName), ("UtilityNetwork", "Water", "Territory"))
        self.assertEqual([n.domainNetworkName for n in d.domainNetworks], ["Structure", "Water"])
        water = d.domainNetworks[1]
        self.assertEqual((water.tierDefinition, water.subnetworkControllerType, water.subnetworkLabelFieldName,
                          water.subnetworkTableName), ("Partitioned", "Source", "SUBNETWORKNAME", "WaterSubnetLine"))
        self.assertEqual([(t.name, t.rank, t.tierTopology) for t in water.tiers], [("Pressure", 1, "Radial")])
        # The SubnetLine is an edge source of its own; the default asset group and type are listed.
        self.assertIn(("WaterSubnetLine", "SubnetLine"), [(s.sourceName, s.utilityNetworkFeatureClassUsageType)
                                                          for s in water.edgeSources])
        device = next(s for s in water.junctionSources if s.sourceName == "WaterDevice")
        self.assertEqual([g.assetGroupName for g in device.assetGroups], ["Unknown", "Source", "Valve", "CheckValve"])
        valve = next(g for g in device.assetGroups if g.assetGroupName == "Valve").assetTypes
        self.assertEqual([(t.assetTypeCode, t.categories) for t in valve], [(0, []), (1, ["Isolating"])])
        names = [a.name for a in d.networkAttributes]
        self.assertEqual(names[:10], ["Source ID", "Terminal ID", "Asset group", "Asset type", "Is subnetwork controller",
                                      "Tier name", "Shape length", "Position from", "Position to", "Flow direction"])
        status = d.networkAttributes[names.index("Device Status")]
        self.assertEqual((status.dataType, status.fieldType, status.isNullable,
                          [(s.networkSourceName, s.evaluator.fieldName) for s in status.assignments]),
                         ("Integer", "SmallInteger", True, [("WaterDevice", "STATUS")]))
        # Pro 3.7 makes a version 7 network for COMPATIBLE: no Propagator Resetter.
        self.assertEqual(d.schemaGeneration, 7)
        self.assertEqual(sorted(c.name for c in d.categories),
                         ["Attribute Substitution", "Isolating", "Subnetwork Controller", "Subnetwork Tap"])

    def test_terminal_ids_run_on_through_the_network_and_paths_name_them(self):
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.AddTerminalConfiguration(self.un, "Three", "BIDIRECTIONAL", None, "A;B;C", "Top A-B;Bottom A-C", "Top")
        configs = self.describe().terminalConfigurations
        self.assertEqual([(c.terminalConfigurationID, c.terminalConfigurationName) for c in configs],
                         [(0, "Single terminal"), (2, "Source-Load"), (3, "Three")])
        # Source-Load holds 2 and 3, so Three's terminals are 4, 5 and 6 — and Top is A-B, not the ids of another.
        self.assertEqual([(t.terminalID, t.terminalName) for t in configs[2].terminals], [(4, "A"), (5, "B"), (6, "C")])
        self.assertEqual([(p.name, p.description) for p in configs[2].validConfigurationPaths],
                         [("Top", "A-B"), ("Bottom", "A-C")])

    def test_a_terminal_configuration_is_refused_on_an_asset_type_a_rule_names(self):
        # A network never enabled (the water network was, which refuses for that reason first).
        gpkg = arcpy.management.CreateFileGDB(self.w.directory, "fresh")[0]
        fds = arcpy.management.CreateFeatureDataset(gpkg, "utility", 3857)[0]
        territory = arcpy.management.CreateFeatureclass(fds, "Territory", "POLYGON", has_z="ENABLED", has_m="ENABLED")[0]
        with arcpy.da.InsertCursor(territory, ["SHAPE@"]) as rows:
            rows.insertRow(["POLYGON((0 0, 10 0, 10 10, 0 10, 0 0))"])
        un = arcpy.un.CreateUtilityNetwork(fds, "Water", "Territory")[0]
        arcpy.un.AddDomainNetwork(un, "Water", "PARTITIONED", "SOURCE")
        arcpy.management.CreateDomain(gpkg, "Types", "", "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(gpkg, "Types", 1, "Standard")
        for table, group in (("WaterDevice", "Valve"), ("WaterDevice", "Meter"), ("WaterLine", "Main")):
            code = 2 if group == "Meter" else 1
            arcpy.management.AddSubtype(f"{fds}/{table}", code, group)
            arcpy.management.AssignDomainToField(f"{fds}/{table}", "ASSETTYPE", "Types", code)
        arcpy.un.AddRule(un, "JUNCTION_EDGE_CONNECTIVITY", "WaterDevice", "Valve", "Standard", "WaterLine", "Main",
                         "Standard")
        arcpy.un.AddTerminalConfiguration(un, "Pair", "BIDIRECTIONAL", None, "A;B")
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.SetTerminalConfiguration(un, "Water", "WaterDevice", "Valve", "Standard", "Pair")
        self.assertIn("ERROR 004114: The asset type has one or more connectivity rules: [1].", str(said.exception))
        arcpy.un.SetTerminalConfiguration(un, "Water", "WaterDevice", "Meter", "Standard", "Pair")

    def test_no_category_named_leaves_the_categories(self):
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Valve", "Standard")
        device = next(s for s in self.describe().domainNetworks[1].junctionSources if s.sourceName == "WaterDevice")
        valve = next(g for g in device.assetGroups if g.assetGroupName == "Valve").assetTypes[1]
        self.assertEqual(valve.categories, ["Isolating"])

    def test_a_container_role_needs_its_deletion_semantics(self):
        arcpy.un.DisableNetworkTopology(self.un)
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.SetAssociationRole(self.un, "Water", "WaterJunction", "Plug", "Standard", "CONTAINER")
        self.assertIn("Deletion Semantics", str(said.exception))
        arcpy.un.SetAssociationRole(self.un, "Water", "WaterJunction", "Plug", "Standard", "CONTAINER", "CASCADE")

    def test_rules_export_an_asset_type_without_terminals_with_no_terminal(self):
        out = os.path.join(self.w.directory, "rules.csv")
        arcpy.un.ExportRules(self.un, "ALL", out)
        with open(out, newline="", encoding="utf-8") as read:
            rows = list(csv.DictReader(read))
        self.assertEqual([(r["FROMASSETGROUP"], r["FROMTERMINAL"], r["TOTERMINAL"]) for r in rows[:2]],
                         [("Source", "Load", ""), ("Valve", "", "")])

    def test_a_trace_configuration_keeps_selection_or_aggregated_geometry(self):
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.AddTraceConfiguration(self.un, "Down", "DOWNSTREAM", domain_network="Water", tier="Pressure",
                                           result_types=["ELEMENTS"])
        self.assertIn("SELECTION | AGGREGATED_GEOMETRY", str(said.exception))

    def test_a_subnetwork_definition_includes_containers_and_manages_is_dirty_by_default(self):
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.SetSubnetworkDefinition(self.un, "Water", "Pressure", None, ["Source/Standard", "Valve/Standard"],
                                         ["Source/Standard"], ["Main/Ductile"])
        tier = self.describe().domainNetworks[1].tiers[0]
        trace = tier.updateSubnetworkTraceConfiguration
        self.assertEqual((trace.includeContainers, trace.includeContent, trace.includeStructures,
                          tier.manageSubnetwork.isDirty), (True, True, True, True))

    def test_the_condition_barriers_of_a_definition_are_described_as_pro_does(self):
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.SetSubnetworkDefinition(self.un, "Water", "Pressure",
                                         condition_barriers="Category IS_EQUAL_TO SPECIFIC_VALUE Isolating")
        (barrier,) = self.describe().domainNetworks[1].tiers[0].updateSubnetworkTraceConfiguration.conditionBarriers
        self.assertEqual((barrier.name, barrier.type, barrier.operator, barrier.value, barrier.isSpecificValue,
                          barrier.combineUsingOr), ("Category", "Category", "Equal", "Isolating", True, False))

    # -- the classes --

    def test_the_classes_have_pro_s_fields(self):
        def fields(table):
            return {f.name.upper(): f.type for f in arcpy.ListFields(f"{self.w.fds}/{table}")}
        line, structure = fields("WaterLine"), fields("StructureJunction")
        self.assertEqual((line["FROMDEVICETERMINAL"], line["TODEVICETERMINAL"], line["FLOWDIRECTION"]),
                         ("SmallInteger", "SmallInteger", "SmallInteger"))
        self.assertIn("FLOWDIRECTION", fields("StructureLine"))
        self.assertNotIn("SUPPORTEDSUBNETWORKNAME", structure)
        # SUPPORTED and SUPPORTING come with the first tier: a new domain network has neither.
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.AddDomainNetwork(self.un, "Gas", "HIERARCHICAL", "SOURCE")
        self.assertNotIn("SUPPORTEDSUBNETWORKNAME", fields("GasLine"))
        arcpy.un.AddTierGroup(self.un, "Gas", "Distribution")
        arcpy.un.AddTier(self.un, "Gas", "System", 1, None, "Distribution", "systemsubnet")
        self.assertTrue({"SYSTEMSUBNET", "SUPPORTEDSUBNETWORKNAME", "SUPPORTINGSUBNETWORKNAME"} <= set(fields("GasLine")))
        self.assertEqual(set(fields("GasAssembly")) & {"SUPPORTEDSUBNETWORKNAME", "SUPPORTINGSUBNETWORKNAME"},
                         {"SUPPORTEDSUBNETWORKNAME"})

    def test_sources_are_numbered_as_pro_numbers_them(self):
        sources = {s.sourceName: s.sourceID for n in self.describe().domainNetworks
                   for s in list(n.junctionSources) + list(n.edgeSources)}
        self.assertEqual([sources[n] for n in ("StructureJunction", "StructureLine", "StructureBoundary",
                                               "StructureJunctionObject", "StructureEdgeObject", "WaterDevice",
                                               "WaterLine", "WaterAssembly", "WaterJunction", "WaterSubnetLine",
                                               "WaterJunctionObject", "WaterEdgeObject")], list(range(4, 16)))

    # -- the topology and the subnetworks --

    def test_a_controller_is_named_with_its_terminal(self):
        with arcpy.da.SearchCursor(self.w.device, ["SUBNETWORKCONTROLLERNAME"], "NAME = 'S'") as read:
            self.assertEqual(next(read)[0], "Load/Plant")

    def test_validate_and_rebuild_need_an_extent_with_the_network_in_it(self):
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.ValidateNetworkTopology(self.un, "500 500 600 600")
        self.assertIn("160521", str(said.exception))
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.RebuildNetworkTopology(self.un)
        self.assertIn("Extent: Value is required", str(said.exception))

    def test_is_connected_codes_are_pro_s(self):
        arcpy.un.DisableNetworkTopology(self.un)
        self.w.insert(self.w.junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("LONE", 1, 1, "POINT(60 60)")])
        arcpy.un.EnableNetworkTopology(self.un)
        arcpy.un.UpdateIsConnected(self.un)
        with arcpy.da.SearchCursor(self.w.junction, ["NAME", "ISCONNECTED"]) as read:
            self.assertEqual(dict(read), {"T": 1, "T2": 1, "LONE": 0})  # 0 False, 1 True, 2 Unknown

    def test_a_trace_including_containers_lists_the_container(self):
        out = os.path.join(self.w.directory, "t.json")
        arcpy.un.Trace(self.un, "CONNECTED", self.w.locations("s", [("L4", None, 0.5)]), domain_network="Water",
                       tier="Pressure", include_containers="INCLUDE_CONTAINERS", result_types=["ELEMENTS"],
                       out_json_file=out)
        with open(out, encoding="utf-8") as read:
            elements = json.load(read)["traceResults"]["elements"]
        self.assertEqual(self.w.names_of(e["globalId"] for e in elements), water.ALL | {"VA"})

    def test_an_exported_subnetwork_is_written_in_pro_s_layout(self):
        arcpy.un.ValidateNetworkTopology(self.un)
        arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
        out = os.path.join(self.w.directory, "zone1.json")
        arcpy.un.ExportSubnetwork(self.un, "Water", "Pressure", "Zone1", "NO_ACKNOWLEDGE", out,
                                  result_types=["FEATURES", "CONNECTIVITY"])
        with open(out, encoding="utf-8") as read:
            document = json.load(read)
        self.assertEqual(sorted(document), ["connectivity", "controllers", "featureElements", "resultTypes",
                                            "sourceMapping", "spatialReference"])
        self.assertEqual([t["type"] for t in document["resultTypes"]], ["features", "connectivity"])
        self.assertNotIn("subnetworkControllerName", document["controllers"][0])

    def test_trace_locations_carry_pro_s_fields(self):
        out = arcpy.management.CreateFeatureclass(self.w.directory, "locations.gpkg", "POINT")[0]
        arcpy.un.AddTraceLocations(self.un, out, trace_locations=[["WaterLine", self.w.lines["L3"], "", 0.25],
                                                                  ["WaterDevice", self.w.devices["V1"],
                                                                   "(1/Single Terminal)", ""]])
        fields = [f.name for f in arcpy.ListFields(out)]
        self.assertTrue({"LAYERNAME", "SOURCEID", "ASSETGROUPCODE", "ASSETTYPE", "FEATUREGLOBALID", "FEATUREOID",
                         "TERMINALID", "PERCENTALONG", "DISPLAYNAME", "ISFILTERBARRIER"} <= set(fields))
        with arcpy.da.SearchCursor(out, ["LAYERNAME", "SOURCEID", "TERMINALID", "PERCENTALONG", "DISPLAYNAME"]) as read:
            self.assertEqual(list(read), [("WaterLine", 10, -1, 0.25, "Main"), ("WaterDevice", 9, 1, -1.0, "Valve")])

    def test_cluster_keys_are_calculated_on_an_enabled_topology(self):
        arcpy.un.EnableClusterKeys(self.un)
        arcpy.un.CalculateClusterKeys(self.un)
        with arcpy.da.SearchCursor(self.w.line, ["CLUSTERKEY"]) as read:
            self.assertTrue(all(k is not None for (k,) in read))

    def test_the_association_method_is_spelled_as_pro_spells_it(self):
        self.assertTrue(callable(arcpy.un.UtilityNetwork(self.un).DeleteStructuralAttachmentAssociation))

    # -- network diagrams --

    def diagram(self, name, rules=(), where=None):
        """A diagram of the lines ``where`` selects (all with none), its content by name: junctions, containers, edges."""
        arcpy.nd.AddDiagramTemplate(self.un, name)
        for rule, args in rules:
            getattr(arcpy.nd, rule)(self.un, name, *args)
        layer = arcpy.management.MakeFeatureLayer(self.w.line, f"lines_{name}", where)[0]
        diagram = arcpy.nd.CreateDiagram(self.un, name, [layer])[1]
        out = os.path.join(self.w.directory, f"{name}.json")
        arcpy.nd.ExportDiagramContent(self.un, diagram, out)
        with open(out, encoding="utf-8") as read:
            doc = json.load(read)
        names = {**{g.strip("{}").upper(): n for n, g in self.w.ids(self.w.device).items()},
                 **{g.strip("{}").upper(): n for n, g in self.w.ids(self.w.junction).items()},
                 **{g.strip("{}").upper(): n for n, g in self.w.ids(self.w.line).items()},
                 **{g.strip("{}").upper(): n for n, g in self.w.ids(self.w.vault).items()}}
        def name_of(item):
            return names.get(str(item.get("assocGlobalID", "")).strip("{}").upper(), "system")
        return (sorted(name_of(j) for j in doc.get("junctions", [])), sorted(name_of(c) for c in doc.get("containers", [])),
                sorted(name_of(e) for e in doc.get("edges", [])))

    def test_a_diagram_holds_the_containers_of_the_junctions_its_lines_bring(self):
        junctions, containers, _ = self.diagram("Lines")
        self.assertEqual(containers, ["VA"])  # V2, at the end of L3, L4 and L9, is in the vault

    def test_removing_a_line_takes_the_system_junction_it_alone_reached(self):
        junctions, _, edges = self.diagram("Removed", [("AddRemoveFeatureByAttributeRule",
                                                        ["ACTIVE", self.w.line, "NAME = 'L6'"])])
        self.assertNotIn("L6", edges)
        self.assertEqual(junctions.count("system"), 2)  # L8's two ends; L6's open end went with it

    def test_a_trace_rule_adds_to_the_diagram_and_traces_indeterminate_flow(self):
        _, _, edges = self.diagram("Down", [("AddSetStartingPointByAttributeRule", ["ACTIVE", self.w.line, "NAME = 'L5'"]),
                                            ("AddTraceRule", ["ACTIVE", "DOWNSTREAM", "Water", "Pressure"])],
                                   "NAME IN ('L1', 'L5')")
        self.assertEqual(edges, ["L1", "L5", "L6"])  # L1 kept, L5's downstream added
        _, _, edges = self.diagram("FromL5", [("AddTraceRule", ["ACTIVE", "DOWNSTREAM", "Water", "Pressure"])],
                                   "NAME = 'L5'")
        self.assertEqual(edges, ["L3", "L4", "L5", "L6", "L7", "L8", "L9"])  # on round the loop beyond V2


    def test_a_partial_overlapping_edges_layout_is_added_with_its_buffer_width_alone(self):
        arcpy.nd.AddDiagramTemplate(self.un, "Partial")
        arcpy.nd.AddPartialOverlappingEdgesLayout(self.un, "Partial", "ACTIVE", "1 Meters")
        diagram = arcpy.nd.CreateDiagram(self.un, "Partial", [self.w.line])[1]
        layer = arcpy.nd.MakeDiagramLayer(self.un, diagram, "partial_view")[0]
        with self.assertRaises(ExecuteError):  # the Apply tool needs the offset too
            arcpy.nd.ApplyPartialOverlappingEdgesLayout(layer, "1 Meters")


if __name__ == "__main__":
    unittest.main()
