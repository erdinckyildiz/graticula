"""Branch versions and archives in the GeoPackage (ADR-227), against hand-worked conflicts and moments.

Two parcels, A (object 1: name "a", value 1) and B (object 2: name "b", value 2). A version "edit" is made off DEFAULT;
then the version and DEFAULT each change both parcels:

    version "edit": A.value = 10        B.name = "bb"
    DEFAULT:        A.value = 20        B.value = 30

BY_OBJECT, both parcels conflict (each was changed on both sides). BY_ATTRIBUTE, only A.value conflicts; B's two
changes touch different columns and merge to ("bb", 30).
"""
import datetime
import os
import sys
import tempfile
import time
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


class Versions(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        _runtime.env._reset()
        arcpy.env.workspace = self.directory
        self.gpkg = os.path.join(self.directory, "parcels.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "parcels.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(self.gpkg, "name", "TEXT")
        arcpy.management.AddField(self.gpkg, "value", "LONG")
        with arcpy.da.InsertCursor(self.gpkg, ["name", "value", "SHAPE@"]) as rows:
            rows.insertRow(("a", 1, point(0, 0)))
            rows.insertRow(("b", 2, point(1, 1)))

    def tearDown(self):
        _runtime.env._reset()

    def rows(self, source):
        with arcpy.da.SearchCursor(source, ["OID@", "name", "value"]) as read:
            return sorted(tuple(r) for r in read)

    def set_value(self, source, where, field, value):
        with arcpy.da.UpdateCursor(source, [field], where) as rows:
            for row in rows:
                rows.updateRow([value])

    def diverge(self):
        arcpy.management.RegisterAsVersioned(self.gpkg)
        arcpy.management.CreateVersion(self.gpkg, "DEFAULT", "edit", "PUBLIC", "parcel fixes")
        with arcpy.da.Editor(self.gpkg, version="edit"):
            self.set_value(self.gpkg, '"name" = \'a\'', "value", 10)
            self.set_value(self.gpkg, '"name" = \'b\'', "name", "bb")
        self.set_value(self.gpkg, '"name" = \'a\'', "value", 20)
        self.set_value(self.gpkg, '"name" = \'b\'', "value", 30)

    def version_layer(self, name):
        layer = arcpy.management.MakeFeatureLayer(self.gpkg, f"lyr_{name}").getOutput(0)
        arcpy.management.ChangeVersion(layer, "TRANSACTIONAL", name)
        return layer

    def test_a_version_sees_its_own_edits_and_default_as_it_was(self):
        self.diverge()
        # DEFAULT, read as any reader reads the table: its own edits only.
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 20), (2, "b", 30)])
        # The version: DEFAULT as it was when the version was made (A 1, B 2), with the version's edits on top.
        self.assertEqual(self.rows(self.version_layer("edit")), [(1, "a", 10), (2, "bb", 2)])
        versions = {v.name: v for v in arcpy.da.ListVersions(self.gpkg)}
        self.assertEqual((versions["edit"].parentVersionName, versions["edit"].access, versions["edit"].description),
                         ("DEFAULT", "Public", "parcel fixes"))
        self.assertEqual([v.name for v in versions["DEFAULT"].children], ["edit"])

    def test_by_object_favouring_the_edit_version_then_post(self):
        self.diverge()
        arcpy.management.ReconcileVersions(self.gpkg, "ALL_VERSIONS", "DEFAULT", ["edit"], None, "NO_ABORT",
                                           "BY_OBJECT", "FAVOR_EDIT_VERSION", "POST")
        # Both rows conflict; the version's whole row wins each: A (a, 10), B (bb, 2) — DEFAULT's 30 is overwritten.
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 10), (2, "bb", 2)])

    def test_by_attribute_favouring_the_target_merges_what_does_not_conflict(self):
        self.diverge()
        arcpy.management.ReconcileVersions(self.gpkg, "ALL_VERSIONS", "DEFAULT", ["edit"], None, "NO_ABORT",
                                           "BY_ATTRIBUTE", "FAVOR_TARGET_VERSION", "POST", "DELETE_VERSION")
        # A.value conflicts and DEFAULT's 20 wins; B's name (version) and value (DEFAULT) merge.
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 20), (2, "bb", 30)])
        self.assertEqual([v.name for v in arcpy.da.ListVersions(self.gpkg)], ["DEFAULT"])
        # No version refers to a moment any more: every superseded DEFAULT row can go. A and B each have a
        # registration row superseded by DEFAULT's own edit, and the post's updates supersede those edits: 4 rows.
        log = os.path.join(self.directory, "prune.log")
        arcpy.management.PruneBranchHistory(self.gpkg, log, "PRUNE")
        with open(log, encoding="utf-8") as read:
            self.assertIn("4 archive rows pruned", read.read())
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 20), (2, "bb", 30)])

    def test_abort_on_conflicts_changes_nothing(self):
        self.diverge()
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.ReconcileVersions(self.gpkg, "ALL_VERSIONS", "DEFAULT", ["edit"], None, "ABORT_CONFLICTS",
                                               "BY_OBJECT", "FAVOR_EDIT_VERSION", "POST")
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 20), (2, "b", 30)])
        self.assertEqual(self.rows(self.version_layer("edit")), [(1, "a", 10), (2, "bb", 2)])

    def test_inserts_and_deletes_on_a_version_post_to_default(self):
        arcpy.management.RegisterAsVersioned(self.gpkg)
        arcpy.management.CreateVersion(self.gpkg, "DEFAULT", "more")
        with arcpy.da.Editor(self.gpkg, version="more"):
            with arcpy.da.InsertCursor(self.gpkg, ["name", "value", "SHAPE@"]) as rows:
                new = rows.insertRow(("c", 3, point(2, 2)))
            with arcpy.da.UpdateCursor(self.gpkg, ["name"], '"name" = \'a\'') as rows:
                for _ in rows:
                    rows.deleteRow()
        self.assertEqual(new, 3)   # the table's next id, reserved so DEFAULT cannot give it to another row
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 1), (2, "b", 2)])
        arcpy.management.ReconcileVersions(self.gpkg, with_post="POST")
        self.assertEqual(self.rows(self.gpkg), [(2, "b", 2), (3, "c", 3)])
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.UnregisterAsVersioned(self.gpkg + "/nowhere") if False else \
                arcpy.management.DeleteVersion(self.gpkg, "DEFAULT")

    def test_unregister_keeps_or_discards_pending_edits(self):
        self.diverge()
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.UnregisterAsVersioned(self.gpkg)
        arcpy.management.UnregisterAsVersioned(self.gpkg, "NO_KEEP_EDIT")
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 20), (2, "b", 30)])

    def test_archiving_reads_a_past_moment(self):
        arcpy.management.EnableArchiving(self.gpkg)
        time.sleep(0.01)
        before = datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)
        time.sleep(0.01)
        self.set_value(self.gpkg, '"name" = \'a\'', "value", 100)
        with arcpy.da.UpdateCursor(self.gpkg, ["name"], '"name" = \'b\'') as rows:
            for _ in rows:
                rows.deleteRow()
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 100)])
        layer = arcpy.management.MakeFeatureLayer(self.gpkg, "then").getOutput(0)
        arcpy.management.ChangeVersion(layer, "HISTORICAL", None, before)
        # As it was at `before`: both parcels, A still 1.
        self.assertEqual(self.rows(layer), [(1, "a", 1), (2, "b", 2)])
        with self.assertRaises(RuntimeError):
            with arcpy.da.UpdateCursor(layer, ["value"]) as rows:
                for row in rows:
                    rows.updateRow([5])
        arcpy.management.TrimArchiveHistory(self.gpkg, "DELETE")
        self.assertEqual(self.rows(self.gpkg), [(1, "a", 100)])
        arcpy.management.DisableArchiving(self.gpkg, "PRESERVE")
        import sqlite3
        connection = sqlite3.connect(self.gpkg)
        kept = connection.execute("select count(*) from parcels_h").fetchone()[0]
        connection.close()
        # After the trim only the current rows' history is left: A's latest row and B's delete.
        self.assertEqual(kept, 2)

    def test_what_stays_refused(self):
        with self.assertRaisesRegex(arcpy.NotBuiltError, "EDITS_TO_BASE"):
            arcpy.management.RegisterAsVersioned(self.gpkg, "EDITS_TO_BASE")
        arcpy.management.RegisterAsVersioned(self.gpkg)
        arcpy.management.CreateVersion(self.gpkg, "DEFAULT", "child")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "parent is DEFAULT"):
            arcpy.management.CreateVersion(self.gpkg, "child", "grandchild")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "BLOCKING_VERSIONS"):
            arcpy.management.ReconcileVersions(self.gpkg, "BLOCKING_VERSIONS")
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.DisableArchiving(self.gpkg)


if __name__ == "__main__":
    unittest.main()
