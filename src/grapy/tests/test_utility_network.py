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
            ("L5", 1, 1, None, 1, "LINESTRING(20 0, 20 10)"), ("L6", 1, 1, 2, None, "LINESTRING(20 10, 20 20)"),
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
        ids = {**self.devices, **self.lines, **self.ids(self.junction)}
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
        # In the loop flow has no direction: traced whole, or not at all when indeterminate flow is ignored.
        self.assertEqual(self.trace("UPSTREAM", [("L8", None, 0.5)]), loop | {"L3", "T", "L2", "V1", "L1", "S"})
        self.assertEqual(self.trace("UPSTREAM", [("L8", None, 0.5)], allow_indeterminate_flow="IGNORE_INDETERMINATE_FLOW"),
                         {"L8"})
        self.assertEqual(self.trace("DOWNSTREAM", [("L2", None, 0.5)], barriers=None,
                                    condition_barriers="Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"),
                         {"L2", "T", "L3", "L5", "CV", "L6", "V2"})

    def test_isolation_closes_only_the_valves_that_feed_the_leak(self):
        isolating = "Category IS_EQUAL_TO SPECIFIC_VALUE Isolating"
        self.assertEqual(self.trace("ISOLATION", [("L3", None, 0.5)], filter_barriers=isolating), {"V1"})
        self.assertEqual(self.trace("ISOLATION", [("L3", None, 0.5)], filter_barriers=isolating,
                                    include_isolated_features="INCLUDE_ISOLATED_FEATURES"), ALL - {"S", "L1"})
        self.assertEqual(self.trace("ISOLATION", [("L4", None, 0.5)], filter_barriers=isolating), {"V2"})
        with self.assertRaises(ExecuteError):
            self.trace("ISOLATION", [("L3", None, 0.5)])

    def test_a_check_valve_hides_a_second_source_and_two_subnetworks_are_an_error(self):
        arcpy.un.DisableNetworkTopology(self.un)
        second = self.insert(self.device, ["NAME", "ASSETGROUP", "ASSETTYPE", "STATUS", "SHAPE@"],
                             [("S2", 1, 1, 0, "POINT(20 20)")])
        with arcpy.da.UpdateCursor(self.line, ["TODEVICETERMINAL"], "NAME = 'L6'") as rows:
            for row in rows:
                rows.updateRow([2])
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
        for kind in ("LOOPS", "PATH", "CIRCUIT"):
            with self.assertRaises(NotBuiltError):
                arcpy.un.Trace(self.un, kind, start)
        with self.assertRaises(NotBuiltError) as said:
            arcpy.un.Trace(self.un, "UPSTREAM", start, domain_network="Water", tier="Pressure", target_tier="Pressure")
        self.assertIn("target_tier", str(said.exception))
        with self.assertRaises(NotBuiltError):
            arcpy.un.Trace(self.un, "CONNECTED", start, result_types="FEATURES")
        with self.assertRaises(NotBuiltError) as said:
            arcpy.un.CreateDiagram(self.un, "d")
        self.assertIn("network diagram", str(said.exception))
        with self.assertRaises(NotBuiltError) as said:
            arcpy.un.UpdateSubnetwork(self.un, "Water", "Pressure")
        self.assertIn("later batch", str(said.exception))


if __name__ == "__main__":
    unittest.main()
