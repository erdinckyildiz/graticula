"""Relationship classes held in the GeoPackage (ADR-225): cardinality, composite deletes, SearchRelatedRecords and the
OGC Related Tables Extension, against hand-worked answers."""
import os
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _gpkg, _relationships  # noqa: E402


class Relationships(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "grid")[0]
        self.poles = arcpy.management.CreateFeatureclass(self.gpkg, "poles", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(self.poles, "pole_id", "TEXT")
        with arcpy.da.InsertCursor(self.poles, ["pole_id", "SHAPE@"]) as written:
            for row in (("P1", "POINT(0 0)"), ("P2", "POINT(10 0)"), ("P3", "POINT(20 0)")):
                written.insertRow(row)
        self.transformers = arcpy.management.CreateTable(self.gpkg, "transformers")[0]
        arcpy.management.AddField(self.transformers, "pole", "TEXT")
        arcpy.management.AddField(self.transformers, "kva", "LONG")
        with arcpy.da.InsertCursor(self.transformers, ["pole", "kva"]) as written:
            for row in (("P1", 50), ("P1", 75), ("P2", 100), ("P9", 25)):
                written.insertRow(row)

    def tearDown(self):
        for name in list(arcpy.management._LAYERS):
            arcpy.management.Delete(name)

    def composite(self):
        return arcpy.management.CreateRelationshipClass(
            self.poles, self.transformers, self.gpkg + "/PoleTransformer", "COMPOSITE", "Holds", "Is mounted on",
            "NONE", "ONE_TO_MANY", "NONE", "pole_id", "pole")[0]

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def test_describe_answers_the_relationship_class_properties(self):
        described = arcpy.Describe(self.composite())
        self.assertEqual((described.dataType, described.cardinality, described.isComposite, described.isAttributed,
                          described.originClassNames, described.destinationClassNames, described.forwardPathLabel,
                          described.backwardPathLabel, described.notification),
                         ("RelationshipClass", "OneToMany", True, False, ["poles"], ["transformers"], "Holds",
                          "Is mounted on", "None"))
        self.assertEqual(described.originClassKeys, [("pole_id", "OriginPrimary"), ("pole", "OriginForeign")])

    def test_search_related_records_both_ways(self):
        rc = self.composite()
        with arcpy.da.SearchRelatedRecords(rc, [1], "pole_id", ["OID@", "kva"]) as related:
            self.assertEqual(list(related), [(("P1",), (1, 50)), (("P1",), (2, 75))])
        self.assertEqual(list(arcpy.da.SearchRelatedRecords(rc, [3], "pole_id", "kva", backward=True)), [(("P2",), (100,))])
        self.assertEqual(list(arcpy.da.SearchRelatedRecords(rc, [3], "pole_id", "kva")), [])     # P3 holds nothing
        self.assertEqual(list(arcpy.da.SearchRelatedRecords(rc, destination_fields="kva")), [((), (50,)), ((), (75,)), ((), (100,))])

    def test_the_related_tables_extension_holds_the_same_pairs(self):
        self.composite()
        connection = sqlite3.connect(self.gpkg)
        try:
            self.assertEqual(connection.execute("select base_table_name, related_table_name, relation_name, mapping_table_name "
                                                "from gpkgext_relations").fetchall(),
                             [("poles", "transformers", "attributes", "PoleTransformer_rte")])
            self.assertEqual(_relationships.read_related_tables(connection, "PoleTransformer"), [(1, 1), (1, 2), (2, 3)])
            self.assertEqual(sorted(r[0] for r in connection.execute(
                "select table_name from gpkg_extensions where extension_name = 'related_tables'")),
                ["PoleTransformer_rte", "gpkgext_relations"])
        finally:
            connection.close()

    def test_a_composite_class_deletes_destination_rows_through_grapys_delete_paths(self):
        self.composite()
        arcpy.management.MakeFeatureLayer(self.poles, "p", "pole_id = 'P1'")
        arcpy.management.DeleteFeatures("p")
        # P1's two transformers go with it; P2's and the orphan P9's stay.
        self.assertEqual(self.rows(self.transformers, ["pole", "kva"]), [("P2", 100), ("P9", 25)])
        with arcpy.da.UpdateCursor(self.poles, ["pole_id"]) as rows:
            for row in rows:
                if row[0] == "P2":
                    rows.deleteRow()
        self.assertEqual(self.rows(self.transformers, ["pole"]), [("P9",)])
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(_relationships.read_related_tables(connection, "PoleTransformer"), [])
        connection.close()

    def test_a_simple_class_leaves_destination_rows(self):
        arcpy.management.CreateRelationshipClass(self.poles, self.transformers, self.gpkg + "/Simple", "SIMPLE", "a", "b",
                                                 "NONE", "ONE_TO_MANY", "NONE", "pole_id", "pole")
        arcpy.management.TruncateTable(self.poles)
        self.assertEqual(arcpy.management.GetCount(self.transformers)[0], "4")
        arcpy.management.Delete(self.poles)
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(connection.execute("select count(*) from grapy_relationship_class").fetchone(), (0,))
        connection.close()

    def test_many_to_many_from_a_table_with_attributes(self):
        owners = arcpy.management.CreateTable(self.gpkg, "owners")[0]
        arcpy.management.AddField(owners, "name", "TEXT")
        with arcpy.da.InsertCursor(owners, ["name"]) as written:
            written.insertRow(("Ada",))
            written.insertRow(("Bo",))
        deeds = arcpy.management.CreateTable(self.gpkg, "deeds")[0]
        for field, kind in (("owner_id", "LONG"), ("pole_ref", "TEXT"), ("share", "DOUBLE")):
            arcpy.management.AddField(deeds, field, kind)
        with arcpy.da.InsertCursor(deeds, ["owner_id", "pole_ref", "share"]) as written:
            for row in ((1, "P1", 0.5), (2, "P1", 0.5), (2, "P3", 1.0)):
                written.insertRow(row)
        rc = arcpy.management.TableToRelationshipClass(owners, self.poles, self.gpkg + "/Owns", "SIMPLE", "owns",
                                                       "owned by", "NONE", "MANY_TO_MANY", deeds, ["share"], "fid",
                                                       "owner_id", "pole_id", "pole_ref")[0]
        self.assertEqual(sorted(arcpy.da.SearchRelatedRecords(rc, "*", "name", "pole_id")),
                         [(("Ada",), ("P1",)), (("Bo",), ("P1",)), (("Bo",), ("P3",))])
        self.assertEqual(self.rows(self.gpkg + "/Owns", ["owner_id", "pole_ref", "share"]),
                         [(1, "P1", 0.5), (2, "P1", 0.5), (2, "P3", 1.0)])
        # Deleting Bo removes Bo's pairs from the relationship table and from the Related Tables mapping.
        with arcpy.da.UpdateCursor(owners, ["name"]) as rows:
            for row in rows:
                if row[0] == "Bo":
                    rows.deleteRow()
        self.assertEqual(self.rows(self.gpkg + "/Owns", ["owner_id"]), [(1,)])
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(_relationships.read_related_tables(connection, "Owns"), [(1, 1)])
        connection.close()
        with arcpy.da.InsertCursor(deeds, ["owner_id", "pole_ref"]) as written:
            written.insertRow((None, "P2"))
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.TableToRelationshipClass(owners, self.poles, self.gpkg + "/Owns2", "SIMPLE", "a", "b", "NONE",
                                                      "MANY_TO_MANY", deeds, None, "fid", "owner_id", "pole_id", "pole_ref")

    def test_rules_are_kept_and_removed(self):
        rc = self.composite()
        arcpy.management.AddRuleToRelationshipClass(rc, None, None, None, None, 1, 3)
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(connection.execute("select destination_minimum, destination_maximum from grapy_relationship_rule")
                         .fetchall(), [(1, 3)])
        connection.close()
        arcpy.management.RemoveRuleFromRelationshipClass(rc, remove_all="REMOVE")
        connection = sqlite3.connect(self.gpkg)
        self.assertEqual(connection.execute("select count(*) from grapy_relationship_rule").fetchone(), (0,))
        connection.close()
        with self.assertRaises(arcpy.ExecuteError):  # poles has no subtypes (ADR-228 builds them)
            arcpy.management.AddRuleToRelationshipClass(rc, "Residential", 0, 1)
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CreateRelationshipClass(self.poles, self.transformers, self.gpkg + "/Bad", "COMPOSITE", "a",
                                                     "b", "NONE", "MANY_TO_MANY", "NONE", "pole_id", "pole_ref", "fid", "x")
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            arcpy.management.MigrateRelationshipClass(rc)
        self.assertIn("GlobalID", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
