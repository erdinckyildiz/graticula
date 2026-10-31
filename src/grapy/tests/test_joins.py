"""AddJoin and RemoveJoin, and the cursors reading a joined layer — ADR-224. Answers are worked out by hand."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


def box(x0):
    return f"POLYGON(({x0} 0,{x0 + 50} 0,{x0 + 50} 50,{x0} 50,{x0} 0))"


class Joins(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        if hasattr(arcpy.env, "qualifiedFieldNames"):
            arcpy.env.qualifiedFieldNames = True
        self.parcels = os.path.join(self.directory, "parcels.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "parcels.gpkg", "POLYGON", spatial_reference=3857)
        arcpy.management.AddField(self.parcels, "name", "TEXT")
        arcpy.management.AddField(self.parcels, "kind", "TEXT")
        with arcpy.da.InsertCursor(self.parcels, ["name", "kind", "SHAPE@"]) as written:
            # p1's kind is upper case: a one-to-first join matches text without regard to case.
            for row in (("p0", "a", box(0)), ("p1", "A", box(100)), ("p2", "c", box(200))):
                written.insertRow(row)
        self.lookup = arcpy.management.CreateTable(self.directory, "lookup.gpkg").getOutput(0)
        arcpy.management.AddField(self.lookup, "kind", "TEXT")
        arcpy.management.AddField(self.lookup, "label", "TEXT")
        with arcpy.da.InsertCursor(self.lookup, ["kind", "label"]) as written:
            for row in (("a", "alpha"), ("a", "second"), ("b", "beta")):
                written.insertRow(row)

    def tearDown(self):
        for name in list(arcpy.management._LAYERS):
            arcpy.management.Delete(name)
        if hasattr(arcpy.env, "qualifiedFieldNames"):
            arcpy.env.qualifiedFieldNames = True

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    def test_a_joined_layer_reads_qualified_fields_one_to_first(self):
        layer = arcpy.management.AddJoin(self.parcels, "kind", self.lookup, "kind")[0]
        self.assertEqual(layer, "parcels")
        self.assertEqual(self.rows(layer, ["parcels.name", "lookup.label", "lookup.fid"]),
                         [("p0", "alpha", 1), ("p1", "alpha", 1), ("p2", None, None)])
        self.assertEqual(int(arcpy.management.GetCount(layer)[0]), 3)
        with self.assertRaises(RuntimeError):
            self.rows(layer, ["name"])
        # The dataset itself, and a layer without joins, read as before.
        self.assertEqual(self.rows(self.parcels, ["name"]), [("p0",), ("p1",), ("p2",)])
        arcpy.management.MakeFeatureLayer(self.parcels, "plain")
        self.assertEqual(self.rows("plain", ["name", "kind"]), [("p0", "a"), ("p1", "A"), ("p2", "c")])
        common = arcpy.management.MakeFeatureLayer(self.parcels, "common")[0]
        arcpy.management.AddJoin(common, "kind", self.lookup, "kind", "KEEP_COMMON")
        self.assertEqual(int(arcpy.management.GetCount(common)[0]), 2)
        self.assertEqual(arcpy.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "lookup.label = 'alpha'")[1], "2")
        self.assertEqual(self.rows(layer, ["parcels.name"]), [("p0",), ("p1",)])
        carried = arcpy.management.MakeFeatureLayer(layer, "carried")[0]
        self.assertEqual(self.rows(carried, ["lookup.label"]), [("alpha",), ("alpha",)])

    def test_writing_through_a_join_writes_only_the_layers_own_table(self):
        layer = arcpy.management.AddJoin(self.parcels, "kind", self.lookup, "kind")[0]
        arcpy.management.CalculateField(layer, "parcels.note", "!lookup.label! or 'none'")
        self.assertEqual(self.rows(self.parcels, ["note"]), [("alpha",), ("alpha",), ("none",)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CalculateField(layer, "lookup.label", "'x'")
        with arcpy.da.UpdateCursor(layer, ["parcels.name", "lookup.label"]) as rows:
            for row in rows:
                rows.updateRow([row[0].upper(), row[1]])          # the join field read and written back unchanged
        self.assertEqual(self.rows(self.parcels, ["name"]), [("P0",), ("P1",), ("P2",)])
        with self.assertRaises(arcpy.NotBuiltError):
            with arcpy.da.UpdateCursor(layer, ["lookup.label"]) as rows:
                for row in rows:
                    rows.updateRow(["changed"])
        self.assertEqual(self.rows(self.lookup, ["label"]), [("alpha",), ("second",), ("beta",)])
        with self.assertRaises(arcpy.NotBuiltError):
            with arcpy.da.InsertCursor(layer, ["parcels.name"]) as rows:
                rows.insertRow(["new"])

    def test_copying_a_joined_layer_names_fields_as_the_environment_says(self):
        layer = arcpy.management.AddJoin(self.parcels, "kind", self.lookup, "kind", "KEEP_COMMON")[0]
        out = arcpy.management.CopyFeatures(layer, os.path.join(self.directory, "copy.gpkg"))[0]
        self.assertEqual([f.name for f in arcpy.ListFields(out)][1:-1],
                         ["parcels_name", "parcels_kind", "lookup_fid", "lookup_kind", "lookup_label"])
        self.assertEqual(self.rows(out, ["parcels_name", "lookup_label", "SHAPE@AREA"]), [("p0", "alpha", 2500.0),
                                                                                        ("p1", "alpha", 2500.0)])
        arcpy.env.qualifiedFieldNames = False
        table = arcpy.management.CopyRows(layer, os.path.join(self.directory, "rows.gpkg"))[0]
        self.assertEqual([f.name for f in arcpy.ListFields(table)][1:], ["name", "kind", "fid_1", "kind_1", "label"])
        with self.assertRaises((arcpy.NotBuiltError, RuntimeError)):
            arcpy.analysis.Buffer(layer, os.path.join(self.directory, "b.gpkg"), 5)

    def test_remove_join(self):
        layer = arcpy.management.AddJoin(self.parcels, "kind", self.lookup, "kind")[0]
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.RemoveJoin(layer, "nothing")
        arcpy.management.RemoveJoin(layer, "lookup")
        self.assertEqual(self.rows(layer, ["name"]), [("p0",), ("p1",), ("p2",)])
        arcpy.management.AddJoin(layer, "kind", self.lookup, "kind")
        arcpy.management.RemoveJoin(layer)
        self.assertEqual(self.rows(layer, ["kind"]), [("a",), ("A",), ("c",)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.AddJoin(layer, "kind", self.lookup, "kind", join_operation="JOIN_ONE_TO_MANY")


if __name__ == "__main__":
    unittest.main()
