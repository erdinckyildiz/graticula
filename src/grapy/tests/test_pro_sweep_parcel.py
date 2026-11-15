"""arcpy.parcel aligned to ArcGIS Pro 3.7 by the Pro sweep (tools/pro-compare/sweep, case module parcel). Each test pins
an answer measured in ArcGIS Pro 3.7 on 2026-11-08 with an example small enough to work by hand. The fabric and its
three parcels are test_parcel.py's."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from test_parcel import Fabric, ends, rows  # noqa: E402


class Merge(Fabric):
    def test_two_lines_without_cogo_merge_with_cogo_type_left_null(self):
        # A's bottom edge is built as (100,0)→(50,0) and (50,0)→(0,0), neither with a Distance or Direction: merged
        # into one line through (50,0), whose COGO Type Pro leaves null (grapy wrote Computed).
        self.built()
        arcpy.parcel.MergeCollinearParcelBoundaries(self.lot_lines, "0.1 Meters")
        merged = [r for r in rows(self.lot_lines, ["COGOType", "Distance", "SHAPE@"])
                  if set(ends(r[2])) == {(100.0, 0.0), (0.0, 0.0)}]
        self.assertEqual(len(merged), 1)
        self.assertEqual(merged[0][:2], [None, None])

    def test_lines_with_a_distance_merge_as_computed(self):
        self.built()
        with arcpy.da.UpdateCursor(self.lot_lines, ["Distance", "SHAPE@"]) as cursor:
            for distance, shape in cursor:
                if set(ends(shape)) in ({(100.0, 0.0), (50.0, 0.0)}, {(50.0, 0.0), (0.0, 0.0)}):
                    cursor.updateRow([50.0, shape])
        arcpy.parcel.MergeCollinearParcelBoundaries(self.lot_lines, "0.1 Meters")
        merged = [r for r in rows(self.lot_lines, ["COGOType", "Distance", "SHAPE@"])
                  if set(ends(r[2])) == {(100.0, 0.0), (0.0, 0.0)}]
        self.assertEqual(merged[0][:2], [3, 100.0])


class Export(Fabric):
    def test_the_exported_record_carries_a_null_exported_status(self):
        self.built()
        target = arcpy.management.CreateFeatureDataset(arcpy.management.CreateFileGDB(self.directory, "out")[0],
                                                       "Out", 3857)[0]
        layer = arcpy.management.MakeFeatureLayer(self.records, "one_record")[0]
        arcpy.management.SelectLayerByAttribute(layer, "NEW_SELECTION", "Name = 'R2'")
        arcpy.parcel.ExportParcelRecordFeatures(layer, target)
        self.assertEqual(rows(f"{target}/Fab_Records", ["Name", "ExportedStatus"]), [["R2", None]])


class Types(Fabric):
    def test_a_type_added_again_after_removal_takes_suffixed_classes(self):
        # Remove Tax (its classes stay, as documented), add Tax again: Pro makes Tax_1 and Tax_Lines_1.
        arcpy.parcel.AddParcelType(self.fabric, "Tax")
        arcpy.parcel.DisableParcelTopology(self.fabric)
        arcpy.parcel.RemoveParcelType(self.fabric, "Tax")
        arcpy.parcel.AddParcelType(self.fabric, "Tax")
        for name in ("Tax", "Tax_Lines", "Tax_1", "Tax_Lines_1"):
            self.assertTrue(arcpy.Exists(f"{self.fds}/{name}"), name)


class Points(Fabric):
    def test_a_new_point_is_not_preserved_and_a_built_one_not_fixed(self):
        # Pro writes Preserve 0 on every new point — the field has no default — and Is Fixed 0 on the points Build
        # makes, where a point a cursor inserts keeps Is Fixed null.
        record = self.record("R1")
        with arcpy.da.InsertCursor(self.points, ["Name", "CreatedByRecord", "SHAPE@"]) as cursor:
            cursor.insertRow(["typed", record, "POINT(500 500)"])
        self.built()
        got = {(r[2].firstPoint.X, r[2].firstPoint.Y): r[:2] for r in rows(self.points, ["IsFixed", "Preserve", "SHAPE@"])}
        self.assertEqual(got[(500.0, 500.0)], [None, 0])
        self.assertEqual(got[(50.0, 0.0)], [0, 0])
        self.assertEqual(len(got), 10)


class Links(Fabric):
    def test_without_history_both_outputs_are_empty_with_source_guid(self):
        self.built()
        links, anchors = arcpy.parcel.GenerateParcelFabricLinks(self.fabric, f"{self.gpkg}/l", f"{self.gpkg}/a",
                                                                "2020-01-01")
        for path in (links, anchors):
            self.assertEqual(rows(path, ["OID@"]), [])
            self.assertEqual([f.name for f in arcpy.ListFields(path) if f.type not in ("OID", "Geometry")],
                             ["SOURCE_GUID"])


if __name__ == "__main__":
    unittest.main()
