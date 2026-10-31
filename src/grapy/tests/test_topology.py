"""Topology held in the GeoPackage (ADR-225): rules against hand-worked violations, clustering as an equivalent."""
import os
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


def box(x0, y0, x1, y1):
    return f"POLYGON(({x0} {y0},{x1} {y0},{x1} {y1},{x0} {y1},{x0} {y0}))"


class Topology(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "land")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "fds", 3857)[0]
        # A (0..10) and B (8..20) overlap by 2 × 10; C stands apart; E1–E4 frame a 10 × 10 hole at 110..120, 10..20.
        self.parcels = self.make("parcels", "POLYGON", [box(0, 0, 10, 10), box(8, 0, 20, 10), box(30, 0, 40, 10),
                                                        box(100, 0, 130, 10), box(100, 20, 130, 30), box(100, 10, 110, 20),
                                                        box(120, 10, 130, 20)])
        # r1 and r2 cross at (5, 0); r1 meets r3 end to end at (10, 0), r3 meets r4 at (20, 0); r4 ends in the air.
        self.roads = self.make("roads", "POLYLINE", ["LINESTRING(0 0,10 0)", "LINESTRING(5 -5,5 5)",
                                                     "LINESTRING(10 0,20 0)", "LINESTRING(20 0,20 10)"])
        self.wells = self.make("wells", "POINT", ["POINT(5 5)", "POINT(25 5)"])

    def make(self, name, shape, rows):
        path = arcpy.management.CreateFeatureclass(self.fds, name, shape)[0]
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow((row,))
        return path

    def topology(self, name="topo", tolerance=None):
        topo = arcpy.management.CreateTopology(self.fds, name, tolerance)[0]
        for i, fc in enumerate((self.parcels, self.roads, self.wells), start=1):
            arcpy.management.AddFeatureClassToTopology(topo, fc, i, 1)
        return topo

    def errors(self, topo):
        connection = sqlite3.connect(self.gpkg)
        try:
            return connection.execute("select description, kind, origin_oid, destination_oid, shape from grapy_topology_error "
                                      "order by error_id").fetchall()
        finally:
            connection.close()

    def shapes(self, topo, description):
        from grapy import _gpkg
        return [_gpkg.decode(r[4]) for r in self.errors(topo) if r[0] == description]

    def test_area_rules(self):
        topo = self.topology()
        arcpy.management.AddRuleToTopology(topo, "Must Not Overlap (Area)", self.parcels)
        arcpy.management.AddRuleToTopology(topo, "Must Not Have Gaps (Area)", self.parcels)
        arcpy.management.ValidateTopology(topo)
        overlap = [e for e in self.errors(topo) if e[0] == "Must Not Overlap"]
        self.assertEqual([(e[1], e[2], e[3]) for e in overlap], [("area", 1, 2)])
        self.assertEqual(self.shapes(topo, "Must Not Overlap")[0].area, 20.0)
        # Outlines: A∪B 20 × 10 (60), C (40), the frame's outside 30 × 30 (120) and its hole (40).
        self.assertEqual(sorted(s.length for s in self.shapes(topo, "Must Not Have Gaps")), [40.0, 40.0, 60.0, 120.0])

    def test_line_rules(self):
        topo = self.topology()
        for rule in ("Must Not Intersect (Line)", "Must Not Have Dangles (Line)", "Must Not Have Pseudo-Nodes (Line)"):
            arcpy.management.AddRuleToTopology(topo, rule, self.roads)
        arcpy.management.ValidateTopology(topo)
        self.assertEqual([(p.x, p.y) for p in self.shapes(topo, "Must Not Intersect")], [(5.0, 0.0)])
        self.assertEqual(sorted((p.x, p.y) for p in self.shapes(topo, "Must Not Have Dangles")),
                         [(0.0, 0.0), (5.0, -5.0), (5.0, 5.0), (20.0, 10.0)])
        self.assertEqual(sorted((p.x, p.y) for p in self.shapes(topo, "Must Not Have Pseudo-Nodes")), [(10.0, 0.0), (20.0, 0.0)])

    def test_rules_between_classes_and_export(self):
        topo = self.topology()
        arcpy.management.AddRuleToTopology(topo, "Must Be Properly Inside (Point-Area)", self.wells, None, self.parcels)
        arcpy.management.AddRuleToTopology(topo, "Must Not Overlap (Area)", self.parcels)
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.AddRuleToTopology(topo, "Must Not Overlap (Area)", self.roads)       # roads are lines
        with self.assertRaises(arcpy.ExecuteError):   # parcels have no subtypes (ADR-228 condition 6)
            arcpy.management.AddRuleToTopology(topo, "Must Not Overlap (Area)", self.parcels, "Residential")
        arcpy.management.ValidateTopology(topo)
        points, lines, polygons = arcpy.management.ExportTopologyErrors(topo, self.gpkg, "topo")
        with arcpy.da.SearchCursor(points, ["OriginObjectClassName", "OriginObjectID", "DestinationObjectID", "RuleType",
                                            "RuleDescription", "IsException", "SHAPE@XY"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("wells", 2, 0, "esriTRTPointProperlyInsideArea",
                                                         "Must Be Properly Inside", 0, (25.0, 5.0))])
        self.assertEqual(arcpy.management.GetCount(lines)[0], "0")                 # made even when empty
        with arcpy.da.SearchCursor(polygons, ["OriginObjectID", "DestinationObjectClassName", "DestinationObjectID",
                                              "SHAPE@AREA"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [(1, "parcels", 2, 20.0)])
        self.assertTrue(points.endswith("topoPointsErrors") and polygons.endswith("topoPolygonErrors"))

    def test_describe_answers_the_topology_properties(self):
        described = arcpy.Describe(self.topology())
        self.assertEqual((described.dataType, described.clusterTolerance, described.featureClassNames),
                         ("Topology", 0.001, ["parcels", "roads", "wells"]))
        self.assertEqual(described.spatialReference.factoryCode, 3857)

    def test_rules_are_removed_by_name_and_with_their_class(self):
        topo = self.topology()
        arcpy.management.AddRuleToTopology(topo, "Must Not Overlap (Area)", self.parcels)
        arcpy.management.AddRuleToTopology(topo, "Must Be Properly Inside (Point-Area)", self.wells, None, self.parcels)
        arcpy.management.AddRuleToTopology(topo, "Must Not Have Dangles (Line)", self.roads)
        # Class ids follow the order the classes were added: parcels 1, roads 2, wells 3.
        arcpy.management.RemoveRuleFromTopology(topo, "Must Not Overlap (1)")
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.RemoveRuleFromTopology(topo, "Must Not Overlap (1)")
        arcpy.management.RemoveFeatureClassFromTopology(topo, self.wells)
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(connection.execute("select rule_type from grapy_topology_rule").fetchall(),
                         [("Must Not Have Dangles (Line)",)])
        self.assertEqual(connection.execute("select count(*) from gpkg_extensions where extension_name = 'grapy_topology'")
                         .fetchone(), (4,))
        connection.close()
        arcpy.management.RemoveRuleFromTopology(topo, "Must Not Have Dangles (2)")

    def test_clustering_is_an_equivalent_that_snaps_to_the_better_rank(self):
        near = self.make("near", "POLYLINE", ["LINESTRING(10.3 0.2,10.3 5)"])
        topo = arcpy.management.CreateTopology(self.fds, "snap", 0.5)[0]
        arcpy.management.AddFeatureClassToTopology(topo, self.parcels, 1, 1)
        arcpy.management.AddFeatureClassToTopology(topo, near, 2, 1)
        arcpy.management.ValidateTopology(topo)
        # (10.3, 0.2) lies 0.36 from parcels' corner (10, 0) and moves onto it; (10.3, 5) has nothing within 0.5.
        with arcpy.da.SearchCursor(near, ["SHAPE@WKT"]) as rows:
            self.assertEqual(next(rows)[0], "LINESTRING (10 0, 10.3 5)")
        with arcpy.da.SearchCursor(self.parcels, ["SHAPE@AREA"]) as rows:
            self.assertEqual(next(rows)[0], 100.0)                      # the better-ranked class does not move
        self.assertTrue(hasattr(arcpy.management.ValidateTopology, "__grapy_equivalent__"))
        self.assertIn("ValidateTopology is an equivalent", arcpy.GetMessages())
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.CreateTopology(self.gpkg, "outside")       # not in a feature dataset


class SubtypeRules(unittest.TestCase):
    """ADR-228 condition 6: AddRuleToTopology's subtype and subtype2 restrict a rule, and ValidateTopology evaluates it on
    those features only. Blocks R1 (0..2) and R2 (1..3) are Residential and overlap by 1 x 2; park P (1.5..2.5) is a Park
    and overlaps both. Well W1 stands in R1 only, W2 in the park."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "land")[0]
        self.fds = arcpy.management.CreateFeatureDataset(self.gpkg, "fds", 3857)[0]
        self.blocks = arcpy.management.CreateFeatureclass(self.fds, "blocks", "POLYGON")[0]
        arcpy.management.AddField(self.blocks, "kind", "SHORT")
        arcpy.management.SetSubtypeField(self.blocks, "kind")
        arcpy.management.AddSubtype(self.blocks, 1, "Residential")
        arcpy.management.AddSubtype(self.blocks, 2, "Park")
        with arcpy.da.InsertCursor(self.blocks, ["kind", "SHAPE@"]) as written:
            for kind, shape in ((1, box(0, 0, 2, 2)), (1, box(1, 0, 3, 2)), (2, box(1.5, 0, 2.5, 2))):
                written.insertRow((kind, shape))
        self.wells = arcpy.management.CreateFeatureclass(self.fds, "wells", "POINT")[0]
        with arcpy.da.InsertCursor(self.wells, ["SHAPE@"]) as written:
            for shape in ("POINT(0.5 1)", "POINT(2 1)"):
                written.insertRow((shape,))
        self.topo = arcpy.management.CreateTopology(self.fds, "topo")[0]
        arcpy.management.AddFeatureClassToTopology(self.topo, self.blocks, 1, 1)
        arcpy.management.AddFeatureClassToTopology(self.topo, self.wells, 2, 1)

    def errors(self):
        connection = sqlite3.connect(self.gpkg)
        try:
            return connection.execute("select description, origin_oid, destination_oid, shape from grapy_topology_error "
                                      "order by error_id").fetchall()
        finally:
            connection.close()

    def test_a_rule_on_one_subtype_reads_that_subtypes_features_only(self):
        from grapy import _gpkg
        arcpy.management.AddRuleToTopology(self.topo, "Must Not Overlap (Area)", self.blocks, "Residential")
        arcpy.management.ValidateTopology(self.topo)
        (description, a, b, shape), = self.errors()
        self.assertEqual((description, a, b), ("Must Not Overlap", 1, 2))
        self.assertAlmostEqual(_gpkg.decode(shape).area, 2.0, 9)
        # The same rule on every subtype also finds the park's two overlaps.
        arcpy.management.AddRuleToTopology(self.topo, "Must Not Overlap (Area)", self.blocks)
        arcpy.management.ValidateTopology(self.topo)
        self.assertEqual(len(self.errors()), 1 + 3)

    def test_a_destination_subtype_restricts_the_destination(self):
        arcpy.management.AddRuleToTopology(self.topo, "Must Be Properly Inside (Point-Area)", self.wells, None,
                                           self.blocks, "Park")
        arcpy.management.ValidateTopology(self.topo)
        self.assertEqual([(d, a) for d, a, _, _ in self.errors()], [("Must Be Properly Inside", 1)])

    def test_a_subtype_is_named_not_coded_and_must_exist(self):
        from grapy._runtime import ExecuteError
        with self.assertRaises(ExecuteError):
            arcpy.management.AddRuleToTopology(self.topo, "Must Not Overlap (Area)", self.blocks, "1")
        with self.assertRaises(ExecuteError):
            arcpy.management.AddRuleToTopology(self.topo, "Must Be Disjoint (Point)", self.wells, "Hydrant")
        with self.assertRaises(ExecuteError):
            arcpy.management.AddRuleToTopology(self.topo, "Must Not Overlap (Area)", self.blocks, "Park", None, "Park")
        arcpy.management.AddRuleToTopology(self.topo, "Must Not Overlap (Area)", self.blocks, "park")
        with self.assertRaises(ExecuteError):   # the same rule on the same subtype twice
            arcpy.management.AddRuleToTopology(self.topo, "Must Not Overlap (Area)", self.blocks, "Park")
        arcpy.management.AddRuleToTopology(self.topo, "Must Not Overlap (Area)", self.blocks, "Residential")


if __name__ == "__main__":
    unittest.main()
