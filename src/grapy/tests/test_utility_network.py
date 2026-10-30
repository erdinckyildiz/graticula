"""Utility networks on a hand-drawn water network with known trace results (ADR-231).

    S ─L1─ V1 ─L2─ T ─L3─ V2 ─L4─ T2          S   source device, its Load terminal the subnetwork controller of Zone1
                   │      │       │           V1, V2  valves (category Isolating)
                   L5     L9      L7          T, T2   tees (WaterJunction)
                   │      │       │           CV  check valve: L5 meets its Source (upstream) terminal, L6 its Load
                   CV     └──L8───┘           L7, L8, L9 meet without a point, through system junctions
                   │
                   L6                         V2 ─ L4 ─ T2 ─ L7 ─ L8 ─ L9 ─ V2 is a loop: flow there has no direction
    x: S 0, V1 10, T 20, V2 30, T2 40; y: the main at 0, CV at 10, L6 ends at 20, the loop's top at 10.

Worked by hand: flow leaves S's Load terminal, so everything downstream of L2 is T, L3, L5, CV, L6, V2 and the loop;
upstream of L5 is T, L2, V1, L1 and S, never L3 (T dominates L3); the leak on L3 is isolated by closing V1 alone,
because V2 is fed only through the leak's own area.
"""
import csv
import json
import os
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _layers  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

ALL = {"S", "L1", "V1", "L2", "T", "L3", "V2", "L4", "T2", "L7", "L8", "L9", "L5", "CV", "L6"}


class WaterNetwork(unittest.TestCase):
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
        arcpy.un.AddDomainNetwork(self.un, "Water", "PARTITIONED", "SOURCE", "Water Distribution")
        arcpy.un.AddTier(self.un, "Water", "Pressure", 1, "RADIAL")
        self.device, self.line = f"{self.fds}/WaterDevice", f"{self.fds}/WaterLine"
        self.junction, self.vault = f"{self.fds}/WaterJunction", f"{self.fds}/StructureJunction"
        for name, codes in (("DeviceTypes", [(1, "Standard")]), ("LineTypes", [(1, "Ductile")]),
                            ("JunctionTypes", [(1, "Standard")]), ("VaultTypes", [(1, "Concrete")])):
            arcpy.management.CreateDomain(self.gpkg, name, name, "SHORT", "CODED")
            for code, text in codes:
                arcpy.management.AddCodedValueToDomain(self.gpkg, name, code, text)
        for table, groups, domain in ((self.device, [(1, "Source"), (2, "Valve"), (3, "CheckValve")], "DeviceTypes"),
                                      (self.line, [(1, "Main")], "LineTypes"),
                                      (self.junction, [(1, "Tee"), (2, "Plug")], "JunctionTypes"),
                                      (self.vault, [(1, "Vault")], "VaultTypes")):
            for code, text in groups:
                arcpy.management.AddSubtype(table, code, text)
                arcpy.management.AssignDomainToField(table, "ASSETTYPE", domain, code)
        arcpy.management.AddField(self.device, "NAME", "TEXT")
        arcpy.management.AddField(self.device, "STATUS", "SHORT")
        arcpy.management.AddField(self.line, "NAME", "TEXT")
        arcpy.management.AddField(self.junction, "NAME", "TEXT")
        arcpy.management.AddField(self.vault, "NAME", "TEXT")
        arcpy.un.AddTerminalConfiguration(self.un, "Source-Load", "DIRECTIONAL", "Source true;Load false")
        for group in ("Source", "CheckValve"):
            arcpy.un.SetTerminalConfiguration(self.un, "Water", "WaterDevice", group, "Standard", "Source-Load")
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Source", "Standard", "Subnetwork Controller")
        arcpy.un.AddNetworkCategory(self.un, "Isolating")
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Valve", "Standard", ["Isolating"])
        arcpy.un.AddNetworkAttribute(self.un, "Device Status", "SHORT")
        arcpy.un.SetNetworkAttribute(self.un, "Device Status", "Water", "WaterDevice", "STATUS")
        je = "JUNCTION_EDGE_CONNECTIVITY"
        arcpy.un.AddRule(self.un, je, "WaterDevice", "Source", "Standard", "WaterLine", "Main", "Ductile", "Load")
        arcpy.un.AddRule(self.un, je, "WaterDevice", "Valve", "Standard", "WaterLine", "Main", "Ductile")
        arcpy.un.AddRule(self.un, je, "WaterDevice", "CheckValve", "Standard", "WaterLine", "Main", "Ductile", "Source")
        arcpy.un.AddRule(self.un, je, "WaterDevice", "CheckValve", "Standard", "WaterLine", "Main", "Ductile", "Load")
        arcpy.un.AddRule(self.un, je, "WaterJunction", "Tee", "Standard", "WaterLine", "Main", "Ductile")
        arcpy.un.AddRule(self.un, "CONTAINMENT", "StructureJunction", "Vault", "Concrete", "WaterDevice", "Valve",
                         "Standard")
        self.devices = self.insert(self.device, ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "SHAPE@"], [
            ("S", 1, 1, 0, "POINT(0 0)"), ("V1", 2, 1, 0, "POINT(10 0)"), ("V2", 2, 1, 0, "POINT(30 0)"),
            ("CV", 3, 1, 0, "POINT(20 10)")])
        self.insert(self.junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [
            ("T", 1, 1, "POINT(20 0)"), ("T2", 1, 1, "POINT(40 0)")])
        self.lines = self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "FROMDEVICETERMINAL", "TODEVICETERMINAL",
                                             "SHAPE@"], [
            ("L1", 1, 1, None, None, "LINESTRING(0 0, 10 0)"), ("L2", 1, 1, None, None, "LINESTRING(10 0, 20 0)"),
            ("L3", 1, 1, None, None, "LINESTRING(20 0, 30 0)"), ("L4", 1, 1, None, None, "LINESTRING(30 0, 40 0)"),
            ("L5", 1, 1, None, 2, "LINESTRING(20 0, 20 10)"), ("L6", 1, 1, 3, None, "LINESTRING(20 10, 20 20)"),
            ("L7", 1, 1, None, None, "LINESTRING(40 0, 40 10)"), ("L8", 1, 1, None, None, "LINESTRING(40 10, 30 10)"),
            ("L9", 1, 1, None, None, "LINESTRING(30 10, 30 0)")])
        self.vaults = self.insert(self.vault, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("VA", 1, 1, "POINT(30 0)")])
        self.controllers = os.path.join(self.directory, "controllers.csv")
        with open(self.controllers, "w", newline="", encoding="utf-8") as written:
            out = csv.writer(written)
            out.writerow(["SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                          "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES"])
            out.writerow(["Plant", self.devices["S"], "WaterDevice", "Source", "Standard", "Load", "Pressure", "Zone1",
                          "the plant", ""])
        arcpy.un.ImportSubnetworkControllers(self.un, self.controllers)
        arcpy.un.UtilityNetwork(self.un).AddContainmentAssociation("StructureJunction", self.vaults["VA"],
                                                                   "WaterDevice", self.devices["V2"], False)
        arcpy.un.EnableNetworkTopology(self.un)

    # -- helpers --

    def insert(self, table, fields, rows):
        with arcpy.da.InsertCursor(table, fields) as written:
            for row in rows:
                written.insertRow(row)
        with arcpy.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
            return {n: g for n, g in read}

    def locations(self, name, rows):
        """Starting points or barriers: (feature name, terminal id, percent along)."""
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        for field, kind in (("FEATUREGLOBALID", "GUID"), ("TERMINALID", "LONG"), ("PERCENTALONG", "DOUBLE")):
            arcpy.management.AddField(path, field, kind)
        ids = {**self.devices, **self.ids(self.line), **self.ids(self.junction)}
        with arcpy.da.InsertCursor(path, ["FEATUREGLOBALID", "TERMINALID", "PERCENTALONG", "SHAPE@XY"]) as written:
            for feature, terminal, percent in rows:
                written.insertRow((ids[feature], terminal, percent, (0, 0)))
        return path

    def ids(self, table):
        with arcpy.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
            return {n: g for n, g in read}

    def selected(self, layers=("WaterDevice", "WaterLine", "WaterJunction")):
        found = set()
        for layer in layers:
            if layer in _layers.LAYERS and _layers.LAYERS[layer].selection:
                with arcpy.da.SearchCursor(layer, ["NAME"]) as read:
                    found |= {r[0] for r in read}
        return found

    def trace(self, kind, starts, **more):
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, kind, self.locations("start", starts), domain_network="Water", tier="Pressure", **more)
        return self.selected()

    def set_status(self, name, value):
        with arcpy.da.UpdateCursor(self.device, ["STATUS"], f"NAME = '{name}'") as rows:
            for row in rows:
                rows.updateRow([value])

    def dirty(self):
        with arcpy.da.SearchCursor(f"{self.fds}/Water_DirtyAreas", ["STATUS", "ERRORCODE", "FEATUREGLOBALID"]) as read:
            return sorted(tuple(r) for r in read)

    # -- the definition --

    def test_the_classes_are_made_with_their_documented_names_and_system_fields(self):
        names = {r[0] for r in sqlite3.connect(self.gpkg).execute("select table_name from gpkg_contents")}
        for suffix in ("Device", "Line", "Junction", "Assembly", "SubnetLine", "JunctionObject", "EdgeObject"):
            self.assertIn(f"utility/Water{suffix}", names)
        for name in ("StructureJunction", "StructureLine", "StructureBoundary", "StructureJunctionObject",
                     "StructureEdgeObject", "Water_Associations", "Water_DirtyAreas"):
            self.assertIn(f"utility/{name}", names)
        fields = {f.name.upper() for f in arcpy.ListFields(self.device)}
        self.assertTrue({"ASSETGROUP", "ASSETTYPE", "ASSOCIATIONSTATUS", "ISSUBNETWORKCONTROLLER",
                         "SUBNETWORKCONTROLLERNAME", "TIERNAME", "TERMINALCONFIGURATION", "GLOBALID"} <= fields)
        self.assertTrue({"FROMDEVICETERMINAL", "TODEVICETERMINAL"} <= {f.name.upper() for f in arcpy.ListFields(self.line)})
        self.assertEqual(arcpy.da.ListSubtypes(self.device)[2]["Name"], "Valve")

    def test_the_service_territory_must_be_z_and_m_enabled(self):
        flat = arcpy.management.CreateFeatureclass(self.fds, "Flat", "POLYGON")[0]
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.CreateUtilityNetwork(self.fds, "Other", flat)
        self.assertIn("z- and m-enabled", str(said.exception))

    def test_a_tier_name_is_unique_in_the_network_and_a_hierarchical_tier_needs_its_group(self):
        arcpy.un.DisableNetworkTopology(self.un)
        with self.assertRaises(ExecuteError):
            arcpy.un.AddTier(self.un, "Water", "Pressure", 2, "RADIAL")
        arcpy.un.AddDomainNetwork(self.un, "Gas", "HIERARCHICAL", "SOURCE")
        with self.assertRaises(ExecuteError):
            arcpy.un.AddTier(self.un, "Gas", "System", 1)
        arcpy.un.AddTierGroup(self.un, "Gas", "Distribution")
        arcpy.un.AddTier(self.un, "Gas", "System", 1, None, "Distribution", "systemsubnet")
        self.assertIn("SYSTEMSUBNET", {f.name.upper() for f in arcpy.ListFields(f"{self.fds}/GasLine")})
        with self.assertRaises(ExecuteError):
            arcpy.un.AddTierGroup(self.un, "Water", "Nope")

    def test_configuration_tools_need_the_topology_disabled(self):
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.AddNetworkCategory(self.un, "Protective")
        self.assertIn("disabled", str(said.exception))

    def test_network_attribute_constraints_are_the_pages(self):
        arcpy.un.DisableNetworkTopology(self.un)
        with self.assertRaises(ExecuteError):
            arcpy.un.AddNetworkAttribute(self.un, "Phase", "SHORT", "INLINE")
        with self.assertRaises(ExecuteError):
            arcpy.un.AddNetworkAttribute(self.un, "Length", "LONG", "NOT_INLINE", "APPORTIONABLE")
        with self.assertRaises(ExecuteError):
            arcpy.un.AddNetworkAttribute(self.un, "Subst", "SHORT", is_substitution="SUBSTITUTION")
        with self.assertRaises(ExecuteError):  # codes from 1, not 0
            arcpy.un.AddNetworkAttribute(self.un, "Lifecycle", "SHORT", "INLINE", domain="DeviceTypes")
        arcpy.management.CreateDomain(self.gpkg, "Lifecycle", "", "SHORT", "CODED")
        for code, text in ((0, "Unknown"), (1, "In service"), (2, "Proposed")):
            arcpy.management.AddCodedValueToDomain(self.gpkg, "Lifecycle", code, text)
        arcpy.un.AddNetworkAttribute(self.un, "Lifecycle", "SHORT", "INLINE", domain="Lifecycle")
        with self.assertRaises(ExecuteError):
            arcpy.un.SetNetworkAttribute(self.un, "Lifecycle", "Water", "WaterDevice", "NAME")
        with self.assertRaises(ExecuteError):
            arcpy.un.SetNetworkAttribute(self.un, "Device Status", "Water", "WaterDevice", "STATUS")

    def test_terminal_configurations_follow_the_page(self):
        arcpy.un.DisableNetworkTopology(self.un)
        with self.assertRaises(ExecuteError):
            arcpy.un.AddTerminalConfiguration(self.un, "One", "BIDIRECTIONAL", None, "A")
        with self.assertRaises(ExecuteError):
            arcpy.un.AddTerminalConfiguration(self.un, "Three", "BIDIRECTIONAL", None, "A;B;C")
        arcpy.un.AddTerminalConfiguration(self.un, "Three", "BIDIRECTIONAL", None, "A;B;C", "Top A-B;Bottom A-C", "Top")
        arcpy.un.AddTerminalConfiguration(self.un, "Pairs", "DIRECTIONAL", "A true;B false;C false", None,
                                          "AB A-B;ABAC 'A-B;A-C'", "AB")
        with self.assertRaises(ExecuteError):
            arcpy.un.AddTerminalConfiguration(self.un, "Down", "DIRECTIONAL", "A false;B false")
        # Assigned once the topology has been enabled: permanent.
        with self.assertRaises(ExecuteError):
            arcpy.un.SetTerminalConfiguration(self.un, "Water", "WaterDevice", "Valve", "Standard", "Three")
        with self.assertRaises(ExecuteError):
            arcpy.un.DeleteTerminalConfiguration(self.un, "Three")

    def test_rules_round_trip_through_csv_and_are_deleted_by_id(self):
        out = os.path.join(self.directory, "rules.csv")
        arcpy.un.ExportRules(self.un, "ALL", out)
        with open(out, newline="", encoding="utf-8") as read:
            rows = list(csv.DictReader(read))
        self.assertEqual(len(rows), 6)
        self.assertEqual(rows[0]["RULETYPE"], "Junction Edge Connectivity")
        self.assertEqual((rows[0]["FROMFEATURECLASS"], rows[0]["FROMASSETGROUP"], rows[0]["FROMTERMINAL"]),
                         ("WaterDevice", "Source", "Load"))
        self.assertEqual(rows[1]["FROMTERMINAL"], "Single Terminal")
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.DeleteRule(self.un, "JUNCTION_EDGE_CONNECTIVITY",
                            ["2: From[WaterDevice.Valve.Standard] To[WaterLine.Main.Ductile]"])
        with self.assertRaises(ExecuteError):
            arcpy.un.DeleteRule(self.un, "CONTAINMENT", ["1: From[x] To[y]"])
        arcpy.un.ImportRules(self.un, "ALL", out)
        exported = os.path.join(self.directory, "again.csv")
        arcpy.un.ExportRules(self.un, "CONTAINMENT", exported)
        with open(exported, newline="", encoding="utf-8") as read:
            self.assertEqual([r["TOASSETGROUP"] for r in csv.DictReader(read)], ["Valve"])
        with self.assertRaises(ExecuteError):  # a terminal is required on an asset type with terminals
            arcpy.un.AddRule(self.un, "JUNCTION_EDGE_CONNECTIVITY", "WaterDevice", "Source", "Standard", "WaterLine",
                             "Main", "Ductile")

    def test_associations_need_a_rule_and_round_trip_through_csv(self):
        network = arcpy.un.UtilityNetwork(self.un)
        with self.assertRaises(ExecuteError):  # no rule lets a vault contain a source
            network.AddContainmentAssociation("StructureJunction", self.vaults["VA"], "WaterDevice", self.devices["S"])
        out = os.path.join(self.directory, "associations.csv")
        arcpy.un.ExportAssociations(self.un, "ALL", out)
        with open(out, newline="", encoding="utf-8") as read:
            rows = list(csv.DictReader(read))
        self.assertEqual([(r["ASSOCIATIONTYPE"], r["FROMASSETGROUP"], r["TOGLOBALID"], r["ISCONTENTVISIBLE"])
                          for r in rows], [("Containment", "Vault", self.devices["V2"], "False")])
        with arcpy.da.SearchCursor(self.device, ["ASSOCIATIONSTATUS"], "NAME = 'V2'") as read:
            self.assertEqual(next(read)[0], 4)  # Content
        with arcpy.da.SearchCursor(f"{self.fds}/Water_Associations", ["GLOBALID@"]) as read:
            association = next(read)[0]
        network.DeleteContainmentAssociation(association)
        arcpy.un.ImportAssociations(self.un, "CONTAINMENT", out)
        with arcpy.da.SearchCursor(self.vault, ["ASSOCIATIONSTATUS"]) as read:
            self.assertEqual(next(read)[0], 1)  # Container

    def test_subnetwork_controllers_round_trip_and_follow_the_prerequisites(self):
        out = os.path.join(self.directory, "out.csv")
        arcpy.un.ExportSubnetworkControllers(self.un, out)
        with open(out, newline="", encoding="utf-8") as read:
            row = next(csv.DictReader(read))
        self.assertEqual((row["SUBNETWORKCONTROLLERNAME"], row["FEATURETERMINAL"], row["SUBNETWORKNAME"]),
                         ("Plant", "Load", "Zone1"))
        with arcpy.da.SearchCursor(self.device, ["ISSUBNETWORKCONTROLLER", "TIERNAME"], "NAME = 'S'") as read:
            self.assertEqual(next(read), (1, "Pressure"))
        network = arcpy.un.UtilityNetwork(self.un)
        with self.assertRaises(ExecuteError):  # a source in a partitioned network is a downstream terminal
            network.EnableSubnetworkController("WaterDevice", self.devices["S"], "Source", "Other", "Zone9", "Pressure")
        with self.assertRaises(ExecuteError):  # a valve's asset type has no Subnetwork Controller category
            network.EnableSubnetworkController("WaterDevice", self.devices["V1"], None, "V", "Zone9", "Pressure")
        network.DisableSubnetworkController("WaterDevice", self.devices["S"], "Load")
        with self.assertRaises(ExecuteError):
            arcpy.un.ExportSubnetworkControllers(self.un, out)
        self.assertTrue(network.HasValidNetworkTopology())

    # -- the topology --

    def test_enable_finds_the_documented_errors(self):
        arcpy.un.DisableNetworkTopology(self.un)
        self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("L10", 1, 1, "LINESTRING(60 60, 70 60)"),
                                                                                ("L11", 0, 0, "LINESTRING(80 80, 90 80)")])
        plug = self.insert(self.junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("P", 2, 1, "POINT(70 60)")])
        arcpy.un.EnableNetworkTopology(self.un)
        lines = self.ids(self.line)
        self.assertEqual(self.dirty(), sorted([(8, 2, lines["L11"]), (8, 8, lines["L10"]), (8, 8, plug["P"])]))
        # Errors past the maximum: the topology is not enabled.
        arcpy.un.DisableNetworkTopology(self.un)
        with self.assertRaises(ExecuteError):
            arcpy.un.EnableNetworkTopology(self.un, 2)
        self.assertFalse(arcpy.un.UtilityNetwork(self.un).HasValidNetworkTopology())

    def test_a_line_meeting_a_two_terminal_device_without_its_terminal_named_is_ambiguous(self):
        arcpy.un.DisableNetworkTopology(self.un)
        with arcpy.da.UpdateCursor(self.line, ["TODEVICETERMINAL"], "NAME = 'L5'") as rows:
            for row in rows:
                rows.updateRow([None])
        arcpy.un.EnableNetworkTopology(self.un)
        self.assertEqual({(s, c) for s, c, _ in self.dirty()}, {(8, 9)})

    def test_disabling_leaves_one_dirty_area_over_the_service_territory(self):
        arcpy.un.DisableNetworkTopology(self.un)
        with arcpy.da.SearchCursor(f"{self.fds}/Water_DirtyAreas", ["STATUS", "SHAPE@"]) as read:
            rows = list(read)
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0][0], 0)
        self.assertAlmostEqual(rows[0][1].area, 110 * 110)
        with self.assertRaises(ExecuteError):
            self.trace("CONNECTED", [("L4", None, 0.5)])

    def test_a_trace_reads_the_topology_as_last_validated(self):
        self.set_status("V1", 1)
        barrier = "'Device Status' IS_EQUAL_TO SPECIFIC_VALUE 1 #"
        self.assertEqual(self.trace("CONNECTED", [("L4", None, 0.5)], condition_barriers=barrier), ALL)
        self.assertEqual([(s, c, g) for s, c, g in self.dirty()], [(1, 0, self.devices["V1"])])
        with self.assertRaises(ExecuteError):
            self.trace("CONNECTED", [("L4", None, 0.5)], validate_consistency="VALIDATE_CONSISTENCY")
        arcpy.un.ValidateNetworkTopology(self.un, "50 50 60 60")  # the edit lies outside: still dirty
        self.assertEqual(len(self.dirty()), 1)
        out = arcpy.un.ValidateNetworkTopology(self.un)
        self.assertEqual(self.dirty(), [])
        self.assertIn("discoveredSubnetworks", out.getOutput(1))
        self.assertEqual(self.trace("CONNECTED", [("L4", None, 0.5)], condition_barriers=barrier), ALL - {"S", "L1"})
        self.assertEqual(self.trace("CONNECTED", [("L4", None, 0.5)], condition_barriers=barrier,
                                    include_barriers="EXCLUDE_BARRIERS"), ALL - {"S", "L1", "V1"})

    # -- traces --

    def test_connected_spans_everything_until_a_barrier(self):
        self.assertEqual(self.trace("CONNECTED", [("L4", None, 0.5)]), ALL)
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "CONNECTED", self.locations("s", [("L4", None, 0.5)]),
                       self.locations("b", [("V1", None, None)]))
        self.assertEqual(self.selected(), ALL - {"S", "L1"})
        # A line row without PERCENTALONG places no starting point, as the page says.
        with self.assertRaises(ExecuteError):
            self.trace("CONNECTED", [("L4", None, None)])

    def test_a_category_condition_barrier_and_the_traversability_scope(self):
        isolating = "Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"
        self.assertEqual(self.trace("CONNECTED", [("L3", None, 0.5)], condition_barriers=isolating),
                         {"V1", "L2", "T", "L3", "V2", "L5", "CV", "L6"})
        self.assertEqual(self.trace("CONNECTED", [("L3", None, 0.5)], condition_barriers=isolating,
                                    traversability_scope="EDGES_ONLY"), ALL)

    def test_subnetwork_and_its_controllers(self):
        self.assertEqual(self.trace("SUBNETWORK", [("L6", None, 0.5)]), ALL)
        self.assertEqual(self.trace("SUBNETWORK_CONTROLLERS", [("L6", None, 0.5)]), {"S"})
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "SUBNETWORK", domain_network="Water", tier="Pressure", subnetwork_name="Zone1")
        self.assertEqual(self.selected(), ALL)
        with self.assertRaises(ExecuteError):  # domain network and tier are required
            arcpy.un.Trace(self.un, "SUBNETWORK", self.locations("s", [("L6", None, 0.5)]))

    def test_downstream_and_upstream_follow_the_flow_from_the_controller(self):
        loop = {"V2", "L4", "T2", "L7", "L8", "L9"}
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)]), {"L2", "T", "L3", "L5", "CV", "L6"} | loop)
        self.assertEqual(self.trace("DOWNSTREAM", [("L5", None, 0.5)]), {"L5", "CV", "L6"})
        self.assertEqual(self.trace("UPSTREAM", [("L5", None, 0.5)]), {"L5", "T", "L2", "V1", "L1", "S"})
        self.assertEqual(self.trace("UPSTREAM", [("L6", None, 0.5)]), {"L6", "CV", "L5", "T", "L2", "V1", "L1", "S"})
        # In the loop flow has no direction: traced whole, or not at all when indeterminate flow is ignored — and a
        # trace starting on an indeterminate edge then returns nothing, as Pro 3.7 was measured doing.
        self.assertEqual(self.trace("UPSTREAM", [("L8", None, 0.5)]), loop | {"L3", "T", "L2", "V1", "L1", "S"})
        self.assertEqual(self.trace("UPSTREAM", [("L8", None, 0.5)], allow_indeterminate_flow="IGNORE_INDETERMINATE_FLOW"),
                         set())
        self.assertEqual(self.trace("UPSTREAM", [("L3", None, 0.5)], allow_indeterminate_flow="IGNORE_INDETERMINATE_FLOW"),
                         {"L3", "T", "L2", "V1", "L1", "S"})
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)], filter_barriers="Category IS_EQUAL_TO "
                                                                                     "SPECIFIC_VALUE Isolating"),
                         {"L2", "T", "L3", "L5", "CV", "L6", "V2"})
        # Closed as condition barriers, the valves take part in the flow (measured in Pro 3.7): V1 cuts L2 off from S,
        # no resource reaches it, and it has no downstream (INFERRED for an unfed start, ADR-231 §5.9 (f)).
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)], barriers=None,
                                    condition_barriers="Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"), set())

    def test_isolation_closes_only_the_valves_that_feed_the_leak(self):
        isolating = "Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"
        self.assertEqual(self.trace("ISOLATION", [("L3", None, 0.5)], filter_barriers=isolating), {"V1"})
        self.assertEqual(self.trace("ISOLATION", [("L3", None, 0.5)], filter_barriers=isolating,
                                    include_isolated_features="INCLUDE_ISOLATED_FEATURES"), ALL - {"S", "L1"})
        self.assertEqual(self.trace("ISOLATION", [("L4", None, 0.5)], filter_barriers=isolating), {"V2"})
        with self.assertRaises(ExecuteError):
            self.trace("ISOLATION", [("L3", None, 0.5)])

    def test_isolation_returns_its_valves_without_barriers_and_nothing_around_a_controller(self):
        # Measured in Pro 3.7 (ADR-231 condition 5): the isolating valves are the answer, not barriers the trace met,
        # so EXCLUDE_BARRIERS keeps them; and a leak on L1 has S in its own area — no valve isolates a controller, and
        # the trace returns nothing, with or without the isolated features.
        isolating = "Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"
        self.assertEqual(self.trace("ISOLATION", [("L3", None, 0.5)], filter_barriers=isolating,
                                    include_barriers="EXCLUDE_BARRIERS"), {"V1"})
        self.assertEqual(self.trace("ISOLATION", [("L1", None, 0.5)], filter_barriers=isolating), set())
        self.assertEqual(self.trace("ISOLATION", [("L1", None, 0.5)], filter_barriers=isolating,
                                    include_isolated_features="INCLUDE_ISOLATED_FEATURES"), set())

    def test_a_check_valve_hides_a_second_source_and_two_subnetworks_are_an_error(self):
        arcpy.un.DisableNetworkTopology(self.un)
        second = self.insert(self.device, ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "SHAPE@"],
                             [("S2", 1, 1, 0, "POINT(20 20)")])
        with arcpy.da.UpdateCursor(self.line, ["TODEVICETERMINAL"], "NAME = 'L6'") as rows:
            for row in rows:
                rows.updateRow([3])  # S2's Load
        arcpy.un.UtilityNetwork(self.un).EnableSubnetworkController("WaterDevice", second["S2"], "Load", "Well", "Zone2",
                                                                    "Pressure")
        arcpy.un.EnableNetworkTopology(self.un)
        self.assertEqual(self.trace("SUBNETWORK_CONTROLLERS", [("L5", None, 0.5)]), {"S"})
        with self.assertRaises(ExecuteError) as said:
            self.trace("SUBNETWORK", [("L6", None, 0.5)])
        self.assertIn("two or more subnetworks", str(said.exception))

    def test_output_filters_and_containers(self):
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)], output_assettypes=["WaterDevice/Valve/Standard"]),
                         {"V2"})
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)],
                                    output_conditions="Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"), {"V2"})
        self.trace("UPSTREAM", [("L4", None, 0.5)], include_containers="INCLUDE_CONTAINERS")
        self.assertEqual(self.selected(("StructureJunction",)), {"VA"})

    def test_terminal_ids_and_the_elements_an_output_condition_keeps(self):
        # Pro 3.7 numbers the Single Terminal 1 and a configuration's terminals from 2: downstream of L2, Terminal ID 1
        # is the tees and the valve; Terminal ID 2 the check valve's Source — and the ELEMENTS result lists that
        # terminal alone, not the Load the feature also has in the trace.
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)],
                                    output_conditions="'Terminal ID' IS_EQUAL_TO SPECIFIC_VALUE 1"), {"T", "V2", "T2"})
        found = self.json_trace("DOWNSTREAM", [("L2", None, 0.5)], ["ELEMENTS"],
                                output_conditions="'Terminal ID' IS_EQUAL_TO SPECIFIC_VALUE 2")
        elements = found["traceResults"]["elements"]
        self.assertEqual([(self.names_of([e["globalId"]]), e["terminalId"]) for e in elements], [({"CV"}, 2)])

    def test_aggregated_geometry(self):
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "DOWNSTREAM", self.locations("s", [("L5", None, 0.5)]), domain_network="Water",
                       tier="Pressure", result_types=["AGGREGATED_GEOMETRY"], trace_name="leak")
        with arcpy.da.SearchCursor(f"{self.gpkg}/Trace_Results_Aggregated_Lines", ["TRACENAME", "SHAPE@"]) as read:
            (name, shape), = list(read)
        self.assertEqual(name, "leak")
        self.assertAlmostEqual(shape.length, 20)
        with arcpy.da.SearchCursor(f"{self.gpkg}/Trace_Results_Aggregated_Points", ["SHAPE@"]) as read:
            self.assertEqual(next(read)[0].pointCount, 1)
        self.assertEqual(self.selected(), set())

    def test_what_is_not_built_is_refused_by_name(self):
        start = self.locations("s", [("L5", None, 0.5)])
        with self.assertRaises(NotBuiltError) as said:
            arcpy.un.Trace(self.un, "CIRCUIT", start)
        self.assertIn("telecom domain network", str(said.exception))
        with self.assertRaises(NotBuiltError):
            arcpy.un.Trace(self.un, "CONNECTED", start, result_types="CIRCUITS",
                           out_json_file=os.path.join(self.directory, "c.json"))
        with self.assertRaises(NotBuiltError) as said:
            arcpy.un.Trace(self.un, "CONNECTED", start, result_types="AGGREGATED_GEOMETRY",
                           synthesize_geometries="SYNTHESIZE_GEOMETRIES")
        self.assertIn("synthesize_geometries", str(said.exception))
        with self.assertRaises(NotBuiltError) as said:
            arcpy.un.CreateDiagram(self.un, "d")
        self.assertIn("network diagram", str(said.exception))
        for name in ("AddTelecomDomainNetwork", "VerifyCircuits", "AnalyzeNetworkData", "ApplyErrorResolutions"):
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.un, name)(self.un)
            self.assertNotIn("later batch", str(said.exception))
        with self.assertRaises(NotBuiltError) as said:
            arcpy.un.AnalyzeNetworkData(self.un, self.directory)
        self.assertIn("error resolution database", str(said.exception))

    # -- ADR-231's second batch: the remaining trace types --------------------------------------------------------

    def json_trace(self, kind, starts, types, **more):
        out = os.path.join(self.directory, "trace.json")
        arcpy.un.Trace(self.un, kind, self.locations("start", starts), domain_network="Water", tier="Pressure",
                       result_types=types, out_json_file=out, **more)
        with open(out, encoding="utf-8") as read:
            return json.load(read)

    def names_of(self, globalids):
        everything = {**self.ids(self.device), **self.ids(self.line), **self.ids(self.junction)}
        back = {g.upper(): n for n, g in everything.items()}
        return {back.get(str(g).upper()) for g in globalids if g}

    def test_loops_return_the_cycle_and_nothing_hanging_off_it(self):
        # The only cycle is V2 ─ L4 ─ T2 ─ L7 ─ L8 ─ L9 ─ V2; L3 reaches it as a bridge.
        self.assertEqual(self.trace("LOOPS", [("L2", None, 0.5)]), {"V2", "L4", "T2", "L7", "L8", "L9"})
        # A barrier on L8 breaks the cycle: no loop is left.
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "LOOPS", self.locations("s", [("L2", None, 0.5)]),
                       self.locations("b", [("L8", None, 0.5)]))
        self.assertEqual(self.selected(), set())

    def test_shortest_path_takes_the_main_not_the_loop(self):
        # S to T2: along the main through L4 costs 40; round the loop through L9, L8, L7 costs 60.
        self.assertEqual(self.trace("SHORTEST_PATH", [("S", None, None), ("T2", None, None)],
                                    shortest_path_network_attribute_name="Shape length"),
                         {"S", "L1", "V1", "L2", "T", "L3", "V2", "L4", "T2"})
        with self.assertRaises(ExecuteError):  # two starting points, and a cost attribute
            self.trace("SHORTEST_PATH", [("S", None, None)], shortest_path_network_attribute_name="Shape length")

    def test_path_counts_hops_and_returns_the_paths_asked_for(self):
        # With no stopping point the path ends at the unterminated end of L6: through the check valve's terminals.
        self.assertEqual(self.trace("PATH", [("S", None, None)]), {"S", "L1", "V1", "L2", "T", "L5", "CV", "L6"})
        stop = self.locations("stop", [("T2", None, None)])
        # L3 to T2: through L4 (one hop) or round the loop (three hops).
        self.assertEqual(self.trace("PATH", [("L3", None, 0.5)], stopping_points=stop), {"L3", "V2", "L4", "T2"})
        self.assertEqual(self.trace("PATH", [("L3", None, 0.5)], stopping_points=stop, num_paths=2),
                         {"L3", "V2", "L4", "T2", "L9", "L8", "L7"})
        self.assertEqual(self.trace("PATH", [("L3", None, 0.5)], stopping_points=stop, num_paths=2, max_hops=2),
                         {"L3", "V2", "L4", "T2"})

    def test_a_function_barrier_with_local_values_stops_each_branch(self):
        barrier = [["ADD", "Shape length", "IS_GREATER_THAN", 15, True]]
        # From L2 (10 m): L3 and L5 each bring the total to 20 and stop the trace.
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)], function_barriers=barrier,
                                    include_barriers="EXCLUDE_BARRIERS"), {"L2", "T"})
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)], function_barriers=barrier), {"L2", "T", "L3", "L5"})
        with self.assertRaises(ExecuteError):  # local values allow one starting point
            self.trace("DOWNSTREAM", [("L2", None, 0.5), ("L4", None, 0.5)], function_barriers=barrier)

    def test_functions_are_reported(self):
        from grapy import _runtime
        del _runtime.MESSAGES[:]
        found = self.json_trace("DOWNSTREAM", [("L5", None, 0.5)], ["ELEMENTS"],
                                functions=[["ADD", "Shape length", None, None, None, None, "Length"],
                                           ["COUNT", "Shape length", None, None, None, None, None]])
        results = found["traceResults"]["globalFunctionResults"]
        self.assertEqual([(r["functionType"], r["result"]) for r in results], [("add", 20.0), ("count", 2)])
        self.assertIn("Length: 20.0", arcpy.GetMessages())
        with self.assertRaises(ExecuteError):  # subtract is not supported in a loops trace
            self.trace("LOOPS", [("L2", None, 0.5)], functions=[["SUBTRACT", "Shape length", None, None, None, None,
                                                                   None]])

    def test_a_propagator_makes_a_barrier_of_what_fails_it(self):
        self.set_status("V2", 1)
        arcpy.un.ValidateNetworkTopology(self.un)
        propagator = [["Device Status", None, "PROPAGATED_MAX", "IS_LESS_THAN", 1]]
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "SUBNETWORK", domain_network="Water", tier="Pressure", subnetwork_name="Zone1",
                       propagators=propagator)
        self.assertEqual(self.selected(), ALL - {"V2", "L4", "T2", "L7", "L8", "L9"})

    def test_attribute_substitution_maps_bits_as_the_page_encodes_them(self):
        from grapy._untrace import Accumulator, substitute
        self.assertEqual(substitute(4, 128), 2)    # A to B
        self.assertEqual(substitute(7, 140), 7)    # A→B, B→C, C→A
        self.assertEqual(substitute(1, 2), 2)      # C to B
        self.assertEqual(substitute(6, 2), 0)      # A and B de-energised
        self.assertEqual(Accumulator("SUBTRACT").add(10).add(3).add(None).add(2).value, 5)

    def test_a_bitset_filter_stops_where_no_bit_is_left(self):
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.management.AddField(self.device, "PHASES", "SHORT")
        arcpy.un.AddNetworkAttribute(self.un, "Phases", "SHORT")
        arcpy.un.SetNetworkAttribute(self.un, "Phases", "Water", "WaterDevice", "PHASES")
        for name, value in (("S", 7), ("V1", 3), ("CV", 4), ("V2", 1)):
            with arcpy.da.UpdateCursor(self.device, ["PHASES"], f"NAME = '{name}'") as rows:
                for row in rows:
                    rows.updateRow([value])
        arcpy.un.EnableNetworkTopology(self.un)
        # From L1 the bits are 7, then V1's 3; the check valve's 4 leaves none, so it and L6 are not traced.
        self.assertEqual(self.trace("DOWNSTREAM", [("L1", None, 0.5)], filter_bitset_network_attribute_name="Phases"),
                         {"L1", "V1", "L2", "T", "L3", "L5", "V2", "L4", "T2", "L7", "L8", "L9"})

    def test_nearest_returns_the_cheapest_features_of_a_category(self):
        nearest = dict(filter_nearest="FILTER_BY_NEAREST", nearest_cost_network_attribute="Shape length",
                       nearest_categories=["Isolating"])
        self.assertEqual(self.trace("CONNECTED", [("L3", None, 0.5)], nearest_count=1, **nearest), {"V2"})
        self.assertEqual(self.trace("CONNECTED", [("L3", None, 0.5)], nearest_count=2, **nearest), {"V1", "V2"})
        with self.assertRaises(ExecuteError):
            self.trace("CONNECTED", [("L3", None, 0.5)], filter_nearest="FILTER_BY_NEAREST")

    def test_a_target_tier_passes_the_controllers_between_the_tiers(self):
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.AddTier(self.un, "Water", "District", 2, "RADIAL")
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "CheckValve", "Standard", "Subnetwork Controller")
        arcpy.un.UtilityNetwork(self.un).EnableSubnetworkController("WaterDevice", self.devices["CV"], "Load", "Gate",
                                                                    "D1", "District")
        arcpy.un.EnableNetworkTopology(self.un)
        loop = {"V2", "L4", "T2", "L7", "L8", "L9"}
        # The check valve now bounds the Pressure tier; downstream stops at it unless District is the target.
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)]), {"L2", "T", "L3", "L5", "CV"} | loop)
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)], target_tier="District"),
                         {"L2", "T", "L3", "L5", "CV", "L6"} | loop)
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "UPSTREAM", self.locations("s", [("L6", None, 0.5)]), domain_network="Water",
                       tier="District")
        self.assertEqual(self.selected(), {"L6", "CV"})
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "UPSTREAM", self.locations("s", [("L6", None, 0.5)]), domain_network="Water",
                       tier="District", target_tier="Pressure")
        self.assertEqual(self.selected(), {"L6", "CV", "L5", "T", "L2", "V1", "L1", "S"})

    def test_digitized_direction_follows_the_lines_as_drawn(self):
        self.assertEqual(self.trace("DOWNSTREAM", [("L4", None, 0.5)], use_digitized_direction="USE_DIGITIZED_DIRECTION"),
                         {"L4", "T2", "L7", "L8", "L9", "V2"})
        self.assertEqual(self.trace("UPSTREAM", [("L3", None, 0.5)], use_digitized_direction="USE_DIGITIZED_DIRECTION"),
                         {"L3", "T", "L2", "V1", "L1", "S"})

    def test_json_results_carry_the_rest_layout(self):
        found = self.json_trace("DOWNSTREAM", [("L5", None, 0.5)],
                                ["ELEMENTS", "CONNECTIVITY", "FEATURES"], include_flow_directions="INCLUDE_FLOW_DIRECTIONS",
                                include_domain_descriptions="INCLUDE_DOMAIN_DESCRIPTIONS",
                                result_fields=[["WaterDevice", "NAME"]], include_geometry="INCLUDE_GEOMETRY")
        results = found["traceResults"]
        self.assertTrue(found["success"])
        self.assertEqual(found["resultTypes"], ["elements", "connectivity", "features"])
        self.assertEqual(self.names_of(e["globalId"] for e in results["elements"]), {"L5", "CV", "L6"})
        # A configuration's terminals are numbered from 2 (Source 2, Load 3), as Pro 3.7 numbers them.
        self.assertEqual(sorted(e["terminalId"] for e in results["elements"] if e["terminalId"]), [2, 3])
        # L6 runs from the check valve's Load terminal to its open end; L5's tee was not traced, so it has no entry.
        (link,) = results["connectivity"]
        self.assertEqual((self.names_of([link["viaGlobalId"]]), link["fromTerminalId"], link["toNetworkSourceId"]),
                         ({"L6"}, 3, 0))
        self.assertEqual(link["flowDirection"], "withDigitized")
        self.assertEqual(link["fromGeometry"], {"x": 20.0, "y": 10.0})
        named = [f for f in results["featureElements"] if f.get("fieldValues")]
        self.assertEqual({f["fieldValues"][0]["value"] for f in named}, {"CV"})
        self.assertEqual({f["assetGroupName"] for f in named}, {"CheckValve"})
        found = self.json_trace("UPSTREAM", [("L4", None, 0.5)], ["CONTAINMENT_AND_ATTACHMENT_ASSOCIATIONS",
                                                                   "RELATED_RECORDS"],
                                include_containers="INCLUDE_CONTAINERS")
        self.assertEqual(found["traceResults"]["relatedRecords"], [])  # no relationship class
        (association,) = found["traceResults"]["associations"]
        self.assertEqual((association["associationType"], association["toGlobalId"]), ("containment", self.devices["V2"]))
        with self.assertRaises(ExecuteError):  # a JSON result type needs its file
            arcpy.un.Trace(self.un, "CONNECTED", self.locations("s", [("L5", None, 0.5)]), result_types="FEATURES")

    def test_locatability_and_the_first_spatial_container(self):
        objects = f"{self.fds}/WaterJunctionObject"
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.management.CreateDomain(self.gpkg, "ObjectTypes", "", "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(self.gpkg, "ObjectTypes", 1, "Card")
        arcpy.management.AddSubtype(objects, 1, "Sensor")
        arcpy.management.AssignDomainToField(objects, "ASSETTYPE", "ObjectTypes", 1)
        arcpy.management.AddField(objects, "NAME", "TEXT")
        arcpy.un.AddRule(self.un, "JUNCTION_JUNCTION_CONNECTIVITY", "WaterJunctionObject", "Sensor", "Card",
                         "WaterJunction", "Tee", "Standard")
        with arcpy.da.InsertCursor(objects, ["NAME", "ASSETGROUP", "ASSETTYPE"]) as rows:
            rows.insertRow(("O1", 1, 1))
        with arcpy.da.SearchCursor(objects, ["GLOBALID@"]) as read:
            sensor = next(read)[0]
        arcpy.un.UtilityNetwork(self.un).AddConnectivityAssociation(1, "WaterJunctionObject", sensor, None,
                                                                     "WaterJunction", self.ids(self.junction)["T"], None)
        arcpy.un.EnableNetworkTopology(self.un)
        start = self.locations("s", [("L3", None, 0.5)])
        arcpy.un.Trace(self.un, "CONNECTED", start)
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.Trace(self.un, "CONNECTED", start, validate_locatability="VALIDATE_LOCATABILITY")
        self.assertIn("cannot be", str(said.exception))

    # -- trace locations and configurations -------------------------------------------------------------------------

    def test_trace_locations_are_written_in_the_schema_trace_reads(self):
        out = f"{self.gpkg}/TraceLocations"
        arcpy.un.AddTraceLocations(self.un, out, trace_locations=[["WaterLine", self.lines["L3"], None, None]])
        with arcpy.da.SearchCursor(out, ["FEATUREGLOBALID", "PERCENTALONG", "ISFILTERBARRIER", "SHAPE@XY"]) as read:
            self.assertEqual(list(read), [(self.lines["L3"].upper(), 0.5, 0, (25.0, 0.0))])
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "SUBNETWORK_CONTROLLERS", out, domain_network="Water", tier="Pressure")
        self.assertEqual(self.selected(), {"S"})
        # Valves added as filter barriers isolate the leak without a filter barrier condition.
        barriers = f"{self.gpkg}/Barriers"
        arcpy.un.AddTraceLocations(self.un, barriers, trace_locations=[["WaterDevice", self.devices["V1"], None, None],
                                                                        ["WaterDevice", self.devices["V2"], None, None]],
                                   filter_barrier="FILTER_BARRIER")
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, "ISOLATION", out, barriers, domain_network="Water", tier="Pressure")
        self.assertEqual(self.selected(), {"V1"})
        arcpy.un.AddTraceLocations(self.un, barriers, clear_trace_locations="CLEAR_LOCATIONS",
                                   trace_locations=[["WaterDevice", self.devices["S"], "Load", None]])
        with arcpy.da.SearchCursor(barriers, ["TERMINALID", "PERCENTALONG"]) as read:
            self.assertEqual(list(read), [(3, None)])  # Load: Source is 2, the Single Terminal 1

    def test_trace_configurations_are_kept_used_exported_and_imported(self):
        arcpy.un.AddTraceConfiguration(self.un, "Leak", "ISOLATION", "Valves to close", ["water", "leak"], "Water",
                                       "Pressure",
                                       filter_barriers="Category IS_EQUAL_TO SPECIFIC_VALUE Isolating")
        start = self.locations("s", [("L3", None, 0.5)])
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, starting_points=start, use_trace_config="USE_TRACE_CONFIGURATION",
                       trace_config_name="Leak")
        self.assertEqual(self.selected(), {"V1"})
        out = os.path.join(self.directory, "configs.json")
        arcpy.un.ExportTraceConfigurations(self.un, ["Leak"], out)
        with open(out, encoding="utf-8") as read:
            (item,) = json.load(read)
        self.assertEqual(set(item), {"creator", "name", "description", "traceType", "configuration", "resultTypes",
                                     "tags"})
        configuration = json.loads(item["configuration"])
        self.assertEqual((item["traceType"], configuration["domainNetworkName"], configuration["traversabilityScope"]),
                         (8, "Water", "junctionsAndEdges"))
        self.assertEqual(configuration["filterBarriers"][0]["type"], "category")
        self.assertEqual(json.loads(item["tags"]), ["water", "leak"])
        with self.assertRaises(ExecuteError):  # the name is taken
            arcpy.un.ImportTraceConfigurations(self.un, out)
        arcpy.un.DeleteTraceConfiguration(self.un, "Leak")
        arcpy.un.ImportTraceConfigurations(self.un, out)
        _layers.LAYERS.clear()
        arcpy.un.Trace(self.un, starting_points=start, use_trace_config="USE_TRACE_CONFIGURATION",
                       trace_config_name="Leak")
        self.assertEqual(self.selected(), {"V1"})
        with self.assertRaises(ExecuteError):  # a subnetwork-based configuration needs its tier
            arcpy.un.AddTraceConfiguration(self.un, "Down", "DOWNSTREAM")

    # -- subnetworks ---------------------------------------------------------------------------------------------------

    def subnet_rows(self):
        with arcpy.da.SearchCursor(f"{self.fds}/WaterSubnetLine", ["SUBNETWORKNAME", "ISDIRTY",
                                                                   "SUBNETWORKCONTROLLERNAMES", "SHAPE@"]) as read:
            return [(n, d, c, round(s.length, 6) if s else None) for n, d, c, s in read]

    def names_written(self, table=None):
        with arcpy.da.SearchCursor(table or self.line, ["NAME", "SUBNETWORKNAME", "ISCONNECTED"]) as read:
            return {n: (s, c) for n, s, c in read}

    def test_update_subnetwork_writes_names_and_its_subnet_line(self):
        out = arcpy.un.ValidateNetworkTopology(self.un)
        self.assertEqual(json.loads(out.getOutput(1))["discoveredSubnetworks"],
                         [{"domainNetworkName": "Water", "tierName": "Pressure", "subnetworkName": "Zone1"}])
        with self.assertRaises(ExecuteError):  # still dirty: export refuses
            arcpy.un.ExportSubnetwork(self.un, "Water", "Pressure", "Zone1", "NO_ACKNOWLEDGE",
                                      os.path.join(self.directory, "z.json"))
        arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
        self.assertEqual(set(self.names_written().values()), {("Zone1", 1)})
        self.assertEqual(self.subnet_rows(), [("Zone1", 0, "Plant", 90.0)])
        self.assertEqual(json.loads(arcpy.un.ValidateNetworkTopology(self.un).getOutput(1))["discoveredSubnetworks"], [])
        # A new line at the open end of L6: an unvalidated edit stops the update, a validated one is discovered.
        self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("L10", 1, 1, "LINESTRING(20 20, 20 30)")])
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
        self.assertIn("dirty areas", str(said.exception))
        discovered = json.loads(arcpy.un.ValidateNetworkTopology(self.un).getOutput(1))["discoveredSubnetworks"]
        self.assertEqual([d["subnetworkName"] for d in discovered], ["Zone1"])
        self.assertEqual(self.subnet_rows()[0][1], 1)
        arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "ALL_SUBNETWORKS_IN_TIER")
        self.assertEqual(self.subnet_rows(), [("Zone1", 0, "Plant", 100.0)])
        self.assertEqual(self.names_written()["L10"], ("Zone1", 1))
        # Exported and acknowledged.
        out = os.path.join(self.directory, "zone1.json")
        arcpy.un.ExportSubnetwork(self.un, "Water", "Pressure", "Zone1", "ACKNOWLEDGE", out, result_types=["FEATURES"])
        with open(out, encoding="utf-8") as read:
            document = json.load(read)
        self.assertEqual([c["subnetworkControllerName"] for c in document["controllers"]], ["Plant"])
        self.assertEqual(self.names_of(f["globalId"] for f in document["traceResults"]["featureElements"]),
                         ALL | {"L10"})
        with arcpy.da.SearchCursor(f"{self.fds}/WaterSubnetLine", ["LASTACKEXPORTSUBNETWORK"]) as read:
            self.assertIsNotNone(next(read)[0])
        # Its controller gone, the subnetwork is deleted: names cleared, its SubnetLine removed.
        arcpy.un.UtilityNetwork(self.un).DisableSubnetworkController("WaterDevice", self.devices["S"], "Load")
        arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
        self.assertEqual(set(self.names_written().values()), {("Unknown", 0)})
        self.assertEqual(self.subnet_rows(), [])

    def test_a_subnetwork_definition_shapes_the_update(self):
        with self.assertRaises(ExecuteError):  # the topology must be disabled
            arcpy.un.SetSubnetworkDefinition(self.un, "Water", "Pressure")
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.management.AddField(f"{self.fds}/WaterSubnetLine", "TOTALLENGTH", "DOUBLE")
        arcpy.management.AddField(self.line, "MAXSTATUS", "SHORT")
        with self.assertRaises(ExecuteError):  # aggregated lines are a subset of the valid lines
            arcpy.un.SetSubnetworkDefinition(self.un, "Water", "Pressure", valid_lines=None,
                                             aggregated_line=["Main/Ductile"])
        arcpy.un.SetSubnetworkDefinition(
            self.un, "Water", "Pressure", valid_lines=["Main/Ductile"], aggregated_line=["Main/Ductile"],
            summaries=[["ADD", "Shape length", None, None, None, None, "TOTALLENGTH"]],
            propagators=[["Device Status", None, "PROPAGATED_MAX", "IS_LESS_THAN", 5, "MAXSTATUS"]])
        self.set_status("V1", 2)
        arcpy.un.EnableNetworkTopology(self.un)
        arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
        with arcpy.da.SearchCursor(f"{self.fds}/WaterSubnetLine", ["TOTALLENGTH"]) as read:
            self.assertEqual(next(read)[0], 90.0)
        with arcpy.da.SearchCursor(self.line, ["NAME", "MAXSTATUS"]) as read:
            status = dict(read)
        self.assertEqual((status["L1"], status["L3"], status["L6"]), (0, 2, 2))
        # Only sources are valid devices: the valves make the subnetwork invalid.
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.SetSubnetworkDefinition(self.un, "Water", "Pressure", valid_devices=["Source/Standard"])
        arcpy.un.EnableNetworkTopology(self.un)
        with self.assertRaises(ExecuteError) as said:
            arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure", "SPECIFIC_SUBNETWORK", "Zone1")
        self.assertIn("valid", str(said.exception))
        self.assertEqual(self.subnet_rows()[0][1], 2)
        with self.assertRaises(NotBuiltError):
            arcpy.un.DisableNetworkTopology(self.un)
            arcpy.un.SetSubnetworkDefinition(self.un, "Water", "Pressure", diagram_template=["Basic"])

    def test_update_is_connected(self):
        arcpy.un.DisableNetworkTopology(self.un)
        self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("L10", 1, 1, "LINESTRING(60 60, 70 60)")])
        arcpy.un.EnableNetworkTopology(self.un)
        arcpy.un.UpdateIsConnected(self.un)
        written = self.names_written()
        self.assertEqual((written["L1"][1], written["L8"][1], written["L10"][1]), (1, 1, 2))

    # -- asset type settings and maintenance --------------------------------------------------------------------------

    def test_end_vertex_connectivity(self):
        arcpy.un.DisableNetworkTopology(self.un)
        # L10 passes V1 at a middle vertex: under ANY_VERTEX it connects there, under END_VERTEX it does not.
        self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("L10", 1, 1, "LINESTRING(10 10, 10 0, 10 -5)")])
        arcpy.un.EnableNetworkTopology(self.un)
        self.assertIn("V1", self.trace("CONNECTED", [("L10", None, 0.25)]))
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.SetEdgeConnectivity(self.un, "Water", "WaterLine", "Main", "Ductile", "END_VERTEX")
        arcpy.un.EnableNetworkTopology(self.un)
        self.assertEqual(self.trace("CONNECTED", [("L10", None, 0.25)]), {"L10"})

    def test_association_roles_and_cascading_deletes(self):
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.SetAssociationRole(self.un, "Structure", "StructureJunction", "Vault", "Concrete", "CONTAINER", "CASCADE")
        with self.assertRaises(ExecuteError):  # removing the role after the topology was enabled
            arcpy.un.SetAssociationRole(self.un, "Structure", "StructureJunction", "Vault", "Concrete", "NONE")
        with self.assertRaises(ExecuteError):  # only the structure network has structures
            arcpy.un.SetAssociationRole(self.un, "Water", "WaterDevice", "Valve", "Standard", "STRUCTURE", "CASCADE")
        arcpy.un.EnableNetworkTopology(self.un)
        with arcpy.da.UpdateCursor(self.vault, ["NAME"]) as rows:
            for _ in rows:
                rows.deleteRow()
        arcpy.un.ValidateNetworkTopology(self.un)
        self.assertNotIn("V2", self.ids(self.device))
        with arcpy.da.SearchCursor(f"{self.fds}/Water_Associations", ["OID@"]) as read:
            self.assertEqual(list(read), [])

    def test_rebuild_takes_every_feature_in_the_extent(self):
        self.set_status("V1", 1)
        self.trace("CONNECTED", [("L4", None, 0.5)])
        self.assertEqual(len(self.dirty()), 1)
        arcpy.un.RebuildNetworkTopology(self.un, "5 -5 15 5")
        self.assertEqual(self.dirty(), [])

    def test_cluster_keys_group_what_is_connected_and_contained(self):
        with self.assertRaises(ExecuteError):  # the field first
            arcpy.un.DisableNetworkTopology(self.un)
            arcpy.un.CalculateClusterKeys(self.un)
        arcpy.un.EnableClusterKeys(self.un)
        self.insert(self.line, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"], [("L10", 1, 1, "LINESTRING(60 60, 70 60)")])
        arcpy.un.EnableNetworkTopology(self.un)
        arcpy.un.DisableNetworkTopology(self.un)
        arcpy.un.CalculateClusterKeys(self.un)
        with arcpy.da.SearchCursor(self.line, ["NAME", "CLUSTERKEY"]) as read:
            keys = dict(read)
        with arcpy.da.SearchCursor(self.vault, ["CLUSTERKEY"]) as read:
            vault = next(read)[0]
        self.assertEqual({keys[n] for n in ("L1", "L6", "L8")}, {vault})
        self.assertNotEqual(keys["L10"], vault)

class MeshNetwork(unittest.TestCase):
    """The mesh of two controllers and a closed valve that ArcGIS Pro 3.7 was measured on (ADR-231 condition 5):

                   E1 (10,20)
        (0,10)  J4 ─B2─ CV2 ─B3─ J5           CV2: Source on J4's side, Load on J5's
                │                │            CV : Source on D1, Load on D2
                B1               B4           VB : a valve with Device Status 1
                │                │
        S1 ─A1─ J1 ─────A2────── J2 ─A3a─ VB ─A3b─ J3 ─A4─ S2      (x 0, 10, 20, 25, 30, 40)
                                 │                 │
                                 C1               D1 ─ CV ─ D2 (up to y 20)

    Worked by hand, as the measurement found it: with VB open, S2 reaches J2 through A3a and A3b, so the A-line and
    the loop J1─A2─J2─B4─J5─B3─CV2─B2─J4─B1 are indeterminate and upstream of C1 is everything but the far ends.
    With VB closed as a condition or a feature barrier, S1 alone feeds the left half: A1 flows into J1, upstream of C1
    is the left half without A3a and VB, and downstream of B1 runs round the loop and back through A2 to J1, never
    to A1 or S1. As a filter barrier VB changes no flow: the trace stops at it and keeps it.
    """

    def setUp(self):
        _layers.LAYERS.clear()
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        gpkg = arcpy.management.CreateFileGDB(self.directory, "mesh")[0]
        fds = arcpy.management.CreateFeatureDataset(gpkg, "utility", 3857)[0]
        territory = arcpy.management.CreateFeatureclass(fds, "Territory", "POLYGON", has_z="ENABLED", has_m="ENABLED")[0]
        with arcpy.da.InsertCursor(territory, ["SHAPE@"]) as rows:
            rows.insertRow(["POLYGON((-100 -100, 200 -100, 200 200, -100 200, -100 -100))"])
        self.un = arcpy.un.CreateUtilityNetwork(fds, "Water", "Territory")[0]
        arcpy.un.AddDomainNetwork(self.un, "Water", "PARTITIONED", "SOURCE", "Water Distribution")
        arcpy.un.AddTier(self.un, "Water", "Pressure", 1, "MESH")
        device, line, junction = f"{fds}/WaterDevice", f"{fds}/WaterLine", f"{fds}/WaterJunction"
        arcpy.management.CreateDomain(gpkg, "Types", "Types", "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(gpkg, "Types", 1, "Standard")
        for table, groups in ((device, [(1, "Source"), (2, "Valve"), (3, "CheckValve")]), (line, [(1, "Main")]),
                              (junction, [(1, "Tee")])):
            for code, text in groups:
                arcpy.management.AddSubtype(table, code, text)
                arcpy.management.AssignDomainToField(table, "ASSETTYPE", "Types", code)
            arcpy.management.AddField(table, "NAME", "TEXT")
        arcpy.management.AddField(device, "STATUS", "SHORT")
        arcpy.un.AddTerminalConfiguration(self.un, "Source-Load", "DIRECTIONAL", "Source true;Load false")
        for group in ("Source", "CheckValve"):
            arcpy.un.SetTerminalConfiguration(self.un, "Water", "WaterDevice", group, "Standard", "Source-Load")
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Source", "Standard", "Subnetwork Controller")
        arcpy.un.AddNetworkCategory(self.un, "Isolating")
        arcpy.un.SetNetworkCategory(self.un, "Water", "WaterDevice", "Valve", "Standard", ["Isolating"])
        arcpy.un.AddNetworkAttribute(self.un, "Device Status", "SHORT")
        arcpy.un.SetNetworkAttribute(self.un, "Device Status", "Water", "WaterDevice", "STATUS")
        je = "JUNCTION_EDGE_CONNECTIVITY"
        arcpy.un.AddRule(self.un, je, "WaterDevice", "Source", "Standard", "WaterLine", "Main", "Standard", "Load")
        arcpy.un.AddRule(self.un, je, "WaterDevice", "Valve", "Standard", "WaterLine", "Main", "Standard")
        for terminal in ("Source", "Load"):
            arcpy.un.AddRule(self.un, je, "WaterDevice", "CheckValve", "Standard", "WaterLine", "Main", "Standard",
                             terminal)
        arcpy.un.AddRule(self.un, je, "WaterJunction", "Tee", "Standard", "WaterLine", "Main", "Standard")
        source, load = 2, 3  # as Pro 3.7 numbers a configuration's terminals
        self.ids = {}
        for table, fields, rows in (
                (device, ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "SHAPE@"],
                 [("S1", 1, 1, 0, "POINT(0 0)"), ("S2", 1, 1, 0, "POINT(40 0)"), ("CV2", 3, 1, 0, "POINT(15 10)"),
                  ("CV", 3, 1, 0, "POINT(30 10)"), ("VB", 2, 1, 1, "POINT(25 0)")]),
                (junction, ["NAME", "ASSETGROUP", "ASSETTYPE", "SHAPE@"],
                 [("J1", 1, 1, "POINT(10 0)"), ("J2", 1, 1, "POINT(20 0)"), ("J3", 1, 1, "POINT(30 0)"),
                  ("J4", 1, 1, "POINT(10 10)"), ("J5", 1, 1, "POINT(20 10)")]),
                (line, ["NAME", "ASSETGROUP", "ASSETTYPE", "FROMDEVICETERMINAL", "TODEVICETERMINAL", "SHAPE@"],
                 [("A1", 1, 1, load, None, "LINESTRING(0 0, 10 0)"), ("A2", 1, 1, None, None, "LINESTRING(10 0, 20 0)"),
                  ("A3a", 1, 1, None, None, "LINESTRING(20 0, 25 0)"),
                  ("A3b", 1, 1, None, None, "LINESTRING(25 0, 30 0)"),
                  ("A4", 1, 1, None, load, "LINESTRING(30 0, 40 0)"), ("B1", 1, 1, None, None, "LINESTRING(10 0, 10 10)"),
                  ("B2", 1, 1, None, source, "LINESTRING(10 10, 15 10)"),
                  ("B3", 1, 1, load, None, "LINESTRING(15 10, 20 10)"),
                  ("B4", 1, 1, None, None, "LINESTRING(20 10, 20 0)"), ("C1", 1, 1, None, None, "LINESTRING(20 0, 20 -10)"),
                  ("D1", 1, 1, None, source, "LINESTRING(30 0, 30 10)"),
                  ("D2", 1, 1, load, None, "LINESTRING(30 10, 30 20)"),
                  ("E1", 1, 1, None, None, "LINESTRING(10 10, 10 20)")])):
            with arcpy.da.InsertCursor(table, fields) as written:
                for row in rows:
                    written.insertRow(row)
            with arcpy.da.SearchCursor(table, ["NAME", "GLOBALID@"]) as read:
                self.ids.update(dict(read))
        self.lines = {"A1", "A2", "A3a", "A3b", "A4", "B1", "B2", "B3", "B4", "C1", "D1", "D2", "E1"}
        controllers = os.path.join(self.directory, "controllers.csv")
        with open(controllers, "w", newline="", encoding="utf-8") as written:
            out = csv.writer(written)
            out.writerow(["SUBNETWORKCONTROLLERNAME", "FEATUREGLOBALID", "FEATURECLASSNAME", "FEATUREASSETGROUP",
                          "FEATUREASSETTYPE", "FEATURETERMINAL", "TIERNAME", "SUBNETWORKNAME", "DESCRIPTION", "NOTES"])
            for name, controller in (("S1", "Plant1"), ("S2", "Plant2")):
                out.writerow([controller, self.ids[name], "WaterDevice", "Source", "Standard", "Load", "Pressure",
                              "Zone1", "", ""])
        arcpy.un.EnableNetworkTopology(self.un)
        arcpy.un.ImportSubnetworkControllers(self.un, controllers)
        arcpy.un.ValidateNetworkTopology(self.un)

    def locations(self, name, names):
        path = arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)[0]
        for field, kind in (("FEATUREGLOBALID", "GUID"), ("TERMINALID", "LONG"), ("PERCENTALONG", "DOUBLE")):
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, ["FEATUREGLOBALID", "TERMINALID", "PERCENTALONG", "SHAPE@XY"]) as written:
            for n in names:
                written.insertRow((self.ids[n], None, 0.5 if n in self.lines else None, (0, 0)))
        return path

    def trace(self, kind, start, **more):
        _layers.LAYERS.clear()
        if "barriers" in more:
            more["barriers"] = self.locations("barriers", more["barriers"])
        arcpy.un.Trace(self.un, kind, self.locations("start", [start]), domain_network="Water", tier="Pressure", **more)
        found = set()
        for layer in ("WaterDevice", "WaterLine", "WaterJunction"):
            if layer in _layers.LAYERS and _layers.LAYERS[layer].selection:
                with arcpy.da.SearchCursor(layer, ["NAME"]) as read:
                    found |= {r[0] for r in read}
        return found

    def test_feature_and_condition_barriers_take_part_in_the_flow_and_filter_barriers_do_not(self):
        closed = "'Device Status' IS_EQUAL_TO SPECIFIC_VALUE 1 #"
        left = {"A1", "A2", "B1", "B2", "B3", "B4", "C1", "CV2", "J1", "J2", "J4", "J5", "S1"}
        self.assertEqual(self.trace("UPSTREAM", "C1"), left | {"A3a", "VB", "A3b", "J3", "A4", "S2"})
        self.assertEqual(self.trace("UPSTREAM", "C1", condition_barriers=closed), left)
        self.assertEqual(self.trace("UPSTREAM", "C1", barriers=["VB"]), left)
        self.assertEqual(self.trace("UPSTREAM", "C1", filter_barriers=closed), left | {"A3a", "VB"})
        self.assertEqual(self.trace("DOWNSTREAM", "B1", condition_barriers=closed),
                         left - {"A1", "S1"} | {"E1", "A3a", "VB"})
        self.assertEqual(self.trace("DOWNSTREAM", "A4", barriers=["VB"]), {"A4", "J3", "A3b", "VB", "D1", "CV", "D2"})

    def test_ignoring_indeterminate_flow_from_an_indeterminate_start_returns_nothing(self):
        ignore = dict(allow_indeterminate_flow="IGNORE_INDETERMINATE_FLOW")
        self.assertEqual(self.trace("UPSTREAM", "A3a", **ignore), set())
        self.assertEqual(self.trace("DOWNSTREAM", "A1", **ignore), set())
        self.assertEqual(self.trace("UPSTREAM", "D2", **ignore), {"D2", "CV", "D1", "J3"})


if __name__ == "__main__":
    unittest.main()
