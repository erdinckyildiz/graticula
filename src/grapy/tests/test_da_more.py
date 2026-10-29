"""arcpy.da's further names in grapy — Walk, the NumPy functions, ExtendTable, ListDomains and Editor — against answers
worked out by hand."""
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


class DaMore(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        _runtime.env._reset()
        arcpy.env.workspace = self.directory
        self.fc = os.path.join(self.directory, "pts.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "pts.gpkg", "POINT", spatial_reference=3857)
        for name, kind in (("name", "TEXT"), ("value", "DOUBLE"), ("count", "LONG")):
            arcpy.management.AddField(self.fc, name, kind)
        with arcpy.da.InsertCursor(self.fc, ["name", "value", "count", "SHAPE@"]) as rows:
            rows.insertRow(("a", 1.5, 1, point(1, 2)))
            rows.insertRow(("bb", None, None, point(3, 4)))

    def tearDown(self):
        _runtime.env._reset()

    def count(self, path):
        return int(arcpy.management.GetCount(path)[0])

    def test_walk_treats_a_geopackage_as_a_workspace(self):
        arcpy.management.CreateFeatureclass(self.directory, "multi.gpkg/parcels", "POLYGON", spatial_reference=3857)
        arcpy.management.CreateFeatureclass(self.directory, "multi.gpkg/roads", "POLYLINE", spatial_reference=3857)
        os.makedirs(os.path.join(self.directory, "sub"))
        open(os.path.join(self.directory, "sub", "t.csv"), "w").close()
        open(os.path.join(self.directory, "notes.txt"), "w").close()
        walked = list(arcpy.da.Walk(self.directory))
        self.assertEqual(walked[0], (self.directory, ["multi.gpkg", "pts.gpkg", "sub"], []))
        self.assertEqual(walked[1], (os.path.join(self.directory, "multi.gpkg"), [], ["parcels", "roads"]))
        self.assertEqual(walked[3], (os.path.join(self.directory, "sub"), [], ["t.csv"]))
        polygons = [f for _, _, files in arcpy.da.Walk(self.directory, datatype="FeatureClass", type="Polygon") for f in files]
        self.assertEqual(polygons, ["parcels"])
        bottom_up = [path for path, _, _ in arcpy.da.Walk(self.directory, topdown=False)]
        self.assertEqual(bottom_up[-1], self.directory)

    def test_table_to_numpy_array_and_its_nulls(self):
        array = arcpy.da.TableToNumPyArray(self.fc, ["OID@", "name", "value"])
        self.assertEqual(array.dtype.names, ("OID@", "name", "value"))
        self.assertEqual((array["OID@"].tolist(), array["name"].tolist()), ([1, 2], ["a", "bb"]))
        self.assertEqual(array.dtype["name"], np.dtype("<U2"))
        self.assertTrue(np.isnan(array["value"][1]))
        with self.assertRaisesRegex(ValueError, "count"):
            arcpy.da.TableToNumPyArray(self.fc, ["count"])
        filled = arcpy.da.TableToNumPyArray(self.fc, ["count"], null_value=-9999)
        self.assertEqual(filled["count"].tolist(), [1, -9999])
        skipped = []
        kept = arcpy.da.TableToNumPyArray(self.fc, ["name", "count"], skip_nulls=lambda oid: skipped.append(oid) or True)
        self.assertEqual((kept["name"].tolist(), skipped), (["a"], [2]))
        self.assertEqual(arcpy.da.TableToNumPyArray(self.fc, ["name"], "value > 1")["name"].tolist(), ["a"])

    def test_feature_class_to_numpy_array_and_exploding(self):
        array = arcpy.da.FeatureClassToNumPyArray(self.fc, ["OID@", "SHAPE@XY"])
        self.assertEqual(array["SHAPE@XY"].tolist(), [[1.0, 2.0], [3.0, 4.0]])
        square = os.path.join(self.directory, "sq.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "sq.gpkg", "POLYGON", spatial_reference=3857)
        with arcpy.da.InsertCursor(square, ["SHAPE@"]) as rows:
            rows.insertRow((arcpy.Polygon(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(2, 0), arcpy.Point(2, 2),
                                                        arcpy.Point(0, 2)]), 3857),))
        # A square's ring has five vertices, the first repeated at the end: five rows, each with the area 4.
        exploded = arcpy.da.FeatureClassToNumPyArray(square, ["SHAPE@X", "SHAPE@AREA"], explode_to_points=True)
        self.assertEqual(len(exploded), 5)
        self.assertEqual(sorted(set(exploded["SHAPE@X"].tolist())), [0.0, 2.0])
        self.assertEqual(set(exploded["SHAPE@AREA"].tolist()), {4.0})
        with self.assertRaisesRegex(arcpy.NotBuiltError, "SHAPE@"):
            arcpy.da.FeatureClassToNumPyArray(self.fc, ["SHAPE@"])

    def test_numpy_array_to_table_and_feature_class(self):
        records = np.array([("a", 1, 1.5), ("b", 2, 2.5)],
                           dtype=[("text field", "<U8"), ("whole", np.int32), ("real", "<f8")])
        table = arcpy.da.NumPyArrayToTable(records, os.path.join(self.directory, "t.gpkg"))
        back = arcpy.da.TableToNumPyArray(table, ["text_field", "whole", "real"])
        self.assertEqual(back.tolist(), [("a", 1, 1.5), ("b", 2, 2.5)])
        self.assertEqual([f.type for f in arcpy.ListFields(table)][1:-1], ["String", "Integer", "Double"])
        with self.assertRaisesRegex(RuntimeError, "already exists"):
            arcpy.da.NumPyArrayToTable(records, table)
        located = np.array([(1, (10.0, 20.0)), (2, (30.0, 40.0))], dtype=[("id", np.int32), ("XY", "<f8", 2)])
        out = arcpy.da.NumPyArrayToFeatureClass(located, os.path.join(self.directory, "xy.gpkg"), ["XY"], 3857)
        with arcpy.da.SearchCursor(out, ["id", "SHAPE@XY"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [(1, (10.0, 20.0)), (2, (30.0, 40.0))])
        self.assertEqual(arcpy.Describe(out).spatialReference.factoryCode, 3857)
        columns = np.array([(5.0, 6.0)], dtype=[("x", "<f8"), ("y", "<f8")])
        two = arcpy.da.NumPyArrayToFeatureClass(columns, os.path.join(self.directory, "two.gpkg"), ("x", "y"))
        with arcpy.da.SearchCursor(two, ["SHAPE@XY"]) as rows:
            self.assertEqual(next(rows)[0], (5.0, 6.0))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "z and m"):
            arcpy.da.NumPyArrayToFeatureClass(columns, os.path.join(self.directory, "z.gpkg"), ("x", "y", "x"))

    def test_extend_table(self):
        extra = np.array([("a", 10), ("zz", 99)], dtype=[("key", "<U4"), ("score", np.int32)])
        arcpy.da.ExtendTable(self.fc, "name", extra, "key")
        with arcpy.da.SearchCursor(self.fc, ["name", "score"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("a", 10), ("bb", None)])
        with self.assertRaisesRegex(RuntimeError, "append_only"):
            arcpy.da.ExtendTable(self.fc, "name", extra, "key")
        arcpy.da.ExtendTable(self.fc, "name", np.array([("a", 11)], dtype=extra.dtype), "key", False)
        with arcpy.da.SearchCursor(self.fc, ["score"], "name = 'a'") as rows:
            self.assertEqual(next(rows)[0], 11)

    def test_list_domains(self):
        arcpy.management.CreateDomain(self.fc, "kinds", "Kinds of thing", "SHORT", "CODED")
        arcpy.management.AddCodedValueToDomain(self.fc, "kinds", 1, "One")
        arcpy.management.AddCodedValueToDomain(self.fc, "kinds", 2, "Two")
        arcpy.management.CreateDomain(self.fc, "depth", "Depth", "DOUBLE", "RANGE")
        arcpy.management.SetValueForRangeDomain(self.fc, "depth", 0, 50)
        depth, kinds = arcpy.da.ListDomains(self.fc)
        self.assertEqual((kinds.name, kinds.domainType, kinds.type, kinds.codedValues, kinds.description),
                         ("kinds", "CodedValue", "Short", {1: "One", 2: "Two"}, "Kinds of thing"))
        self.assertEqual((depth.domainType, depth.range, depth.splitPolicy), ("Range", (0, 50), "DefaultValue"))
        arcpy.management.CreateFeatureclass(self.directory, "bare.gpkg", "POINT", spatial_reference=3857)
        self.assertEqual(arcpy.da.ListDomains(os.path.join(self.directory, "bare.gpkg")), [])

    def test_editor_commits_aborts_and_discards(self):
        with arcpy.da.Editor(self.fc) as editor:
            self.assertTrue(editor.isEditing)
            with arcpy.da.InsertCursor(self.fc, ["name", "SHAPE@"]) as rows:
                rows.insertRow(("kept", point(5, 5)))
        self.assertEqual(self.count(self.fc), 3)
        editor = arcpy.da.Editor(self.fc)
        editor.startEditing(False, True)
        editor.startOperation()
        with arcpy.da.InsertCursor(self.fc, ["name", "SHAPE@"]) as rows:
            rows.insertRow(("aborted", point(6, 6)))
        editor.abortOperation()
        editor.startOperation()
        with arcpy.da.UpdateCursor(self.fc, ["name"], "name = 'a'") as rows:
            for row in rows:
                rows.updateRow(["A"])
        editor.stopOperation()
        editor.stopEditing(True)
        with arcpy.da.SearchCursor(self.fc, ["name"]) as rows:
            self.assertEqual([r[0] for r in rows], ["A", "bb", "kept"])
        editor.startEditing()
        with arcpy.da.UpdateCursor(self.fc, ["name"]) as rows:
            for row in rows:
                rows.deleteRow()
        editor.stopEditing(False)
        self.assertEqual(self.count(self.fc), 3)
        try:
            with arcpy.da.Editor(self.fc):
                with arcpy.da.InsertCursor(self.fc, ["name", "SHAPE@"]) as rows:
                    rows.insertRow(("lost", point(7, 7)))
                raise KeyError("stop")
        except KeyError:
            pass
        self.assertEqual(self.count(self.fc), 3)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "undo"):
            arcpy.da.Editor(self.fc).undoOperation()
        with self.assertRaisesRegex(arcpy.NotBuiltError, "GeoPackage"):
            arcpy.da.Editor(self.directory)

    def test_describe_dictionary_and_refusals(self):
        described = arcpy.da.Describe(self.fc)
        self.assertEqual((described["shapeType"], described["dataType"]), ("Point", "FeatureClass"))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "replicas"):
            arcpy.da.ListReplicas(self.directory)



class CreateTableAsDocumented(unittest.TestCase):
    def test_a_table_and_a_feature_class_with_validated_names(self):
        import tempfile
        directory = tempfile.mkdtemp()
        path, names = arcpy.da.CreateTable(os.path.join(directory, "t.gpkg"),
                                           [("street", "Text", "Street Address"), ("state", "Text", "", 2), ("bad name", "LONG")])
        self.assertEqual(names, ["street", "state", "bad_name"])
        self.assertEqual(arcpy.Describe(path).dataType, "Table")
        fc, _ = arcpy.da.CreateTable(os.path.join(directory, "p.gpkg"), [("v", "DOUBLE")], ("POLYGON", 4326))
        described = arcpy.Describe(fc)
        self.assertEqual((described.shapeType, described.spatialReference.factoryCode), ("Polygon", 4326))
        with self.assertRaises(NotImplementedError):
            arcpy.da.CreateTable(os.path.join(directory, "z.gpkg"), None, ("POINT", 4326, True))
        with self.assertRaises(RuntimeError):
            arcpy.da.CreateTable(path)

if __name__ == "__main__":
    unittest.main()
