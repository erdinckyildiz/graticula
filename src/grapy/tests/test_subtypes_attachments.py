"""Subtypes, field defaults, global IDs and attachments held in the GeoPackage (ADR-228), against hand-worked answers."""
import datetime
import os
import re
import sqlite3
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _exif, _runtime  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

GUID = re.compile(r"^\{[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}\}$")


def _entry(tag, kind, count, value):
    return tag, kind, count, value


def _ifd(entries, start):
    """An IFD at ``start`` with its out-of-line values after it, little-endian."""
    head = struct.pack("<H", len(entries))
    data_at = start + 2 + 12 * len(entries) + 4
    body, extra = b"", b""
    for tag, kind, count, value in entries:
        if len(value) <= 4:
            body += struct.pack("<HHI", tag, kind, count) + value.ljust(4, b"\0")
        else:
            body += struct.pack("<HHII", tag, kind, count, data_at + len(extra))
            extra += value
    return head + body + struct.pack("<I", 0) + extra


def _rational(*pairs):
    return b"".join(struct.pack("<II", n, d) for n, d in pairs)


def exif_jpeg(path, taken=None, latitude=None, longitude=None, altitude=None, direction=None):
    """A JPEG holding only an Exif APP1 segment (CIPA DC-008), written from parts: DMS rationals and references."""
    exif_at = 8 + 2 + 2 * 12 + 4
    exif = _ifd([_entry(0x9003, 2, 20, taken.encode("ascii") + b"\0")] if taken else [], exif_at)
    gps_at = exif_at + len(exif)
    gps_entries = []
    if latitude:
        (ref, dms) = latitude
        gps_entries += [_entry(1, 2, 2, ref.encode() + b"\0"), _entry(2, 5, 3, _rational(*dms))]
    if longitude:
        (ref, dms) = longitude
        gps_entries += [_entry(3, 2, 2, ref.encode() + b"\0"), _entry(4, 5, 3, _rational(*dms))]
    if altitude:
        (below, pair) = altitude
        gps_entries += [_entry(5, 1, 1, bytes([below])), _entry(6, 5, 1, _rational(pair))]
    if direction:
        gps_entries += [_entry(16, 2, 2, b"T\0"), _entry(17, 5, 1, _rational(direction))]
    gps = _ifd(gps_entries, gps_at)
    first = _ifd([_entry(0x8769, 4, 1, struct.pack("<I", exif_at)), _entry(0x8825, 4, 1, struct.pack("<I", gps_at))], 8)
    tiff = b"II" + struct.pack("<HI", 42, 8) + first + exif + gps
    payload = b"Exif\0\0" + tiff
    with open(path, "wb") as written:
        written.write(b"\xff\xd8\xff\xe1" + struct.pack(">H", len(payload) + 2) + payload + b"\xff\xd9")
    return path


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        _runtime.MESSAGES.clear()
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "water")[0]
        self.pipes = arcpy.management.CreateFeatureclass(self.gpkg, "pipes", "POINT", spatial_reference=3857)[0]
        arcpy.management.AddField(self.pipes, "kind", "SHORT")
        arcpy.management.AddField(self.pipes, "diameter", "DOUBLE")
        arcpy.management.AddField(self.pipes, "material", "TEXT")
        arcpy.management.AddField(self.pipes, "code", "TEXT")

    def tearDown(self):
        for name in list(arcpy.management._LAYERS):
            arcpy.management.Delete(name)

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    def insert(self, path, fields, rows):
        with arcpy.da.InsertCursor(path, fields) as written:
            return [written.insertRow(r) for r in rows]

    def subtyped(self):
        arcpy.management.SetSubtypeField(self.pipes, "kind")
        arcpy.management.AddSubtype(self.pipes, 1, "Main")
        arcpy.management.AddSubtype(self.pipes, "2", "Lateral")
        arcpy.management.AddSubtype(self.pipes, 3, "Service")


class Subtypes(_Workspace):
    def test_subtypes_are_added_altered_removed_and_listed(self):
        self.subtyped()
        arcpy.management.AlterSubtype(self.pipes, "3", "Service line")
        arcpy.management.SetDefaultSubtype(self.pipes, 2)
        listed = arcpy.da.ListSubtypes(self.pipes)
        self.assertEqual({c: (v["Name"], v["Default"], v["SubtypeField"], v["Code"]) for c, v in listed.items()},
                         {1: ("Main", False, "kind", 1), 2: ("Lateral", True, "kind", 2), 3: ("Service line", False, "kind", 3)})
        self.assertEqual(sorted(listed[1]["FieldValues"]), ["code", "diameter", "kind", "material"])
        arcpy.management.RemoveSubtype(self.pipes, ["1", "3"])
        self.assertEqual(list(arcpy.da.ListSubtypes(self.pipes)), [2])

    def test_a_table_without_a_subtype_field_lists_one_subtype_named_after_it(self):
        listed = arcpy.da.ListSubtypes(self.pipes)
        self.assertEqual(list(listed), [0])
        self.assertEqual((listed[0]["Name"], listed[0]["Default"], listed[0]["SubtypeField"]), ("pipes", True, ""))

    def test_the_documented_errors(self):
        with self.assertRaises(ExecuteError):  # no subtype field yet
            arcpy.management.AddSubtype(self.pipes, 1, "Main")
        with self.assertRaises(ExecuteError):  # the subtype field must be an integer field
            arcpy.management.SetSubtypeField(self.pipes, "material")
        with self.assertRaises(ExecuteError):  # no such field
            arcpy.management.SetSubtypeField(self.pipes, "nothing")
        self.subtyped()
        with self.assertRaises(ExecuteError):  # a code already there
            arcpy.management.AddSubtype(self.pipes, 2, "Again")
        with self.assertRaises(ExecuteError):  # a code is an integer
            arcpy.management.AddSubtype(self.pipes, "2.5", "Half")
        with self.assertRaises(ExecuteError):
            arcpy.management.SetDefaultSubtype(self.pipes, 9)
        with self.assertRaises(ExecuteError):
            arcpy.management.RemoveSubtype(self.pipes, 9)
        with self.assertRaises(ExecuteError):  # the default must be the field's type
            arcpy.management.AssignDefaultToField(self.pipes, "diameter", "wide")
        with self.assertRaises(ExecuteError):
            arcpy.management.AssignDefaultToField(self.pipes, "kind", "1.5")
        with self.assertRaises(ExecuteError):  # a subtype the table does not have
            arcpy.management.AssignDefaultToField(self.pipes, "diameter", 5, 9)
        with self.assertRaises(ExecuteError):  # the subtype field cannot be deleted
            arcpy.management.DeleteField(self.pipes, "kind")

    def test_a_new_row_takes_its_subtypes_defaults_or_the_fields_own(self):
        self.subtyped()
        arcpy.management.SetDefaultSubtype(self.pipes, 2)
        arcpy.management.AssignDefaultToField(self.pipes, "diameter", 100)
        arcpy.management.AssignDefaultToField(self.pipes, "diameter", "300", ["1: Main"])
        arcpy.management.AssignDefaultToField(self.pipes, "material", "PVC")
        arcpy.management.AssignDefaultToField(self.pipes, "material", "Steel", "1;3")
        oids = self.insert(self.pipes, ["SHAPE@XY"], [((0, 0),)])                       # names no subtype: default 2
        oids += self.insert(self.pipes, ["kind", "SHAPE@XY"], [(1, (1, 1)), (3, (2, 2))])
        oids += self.insert(self.pipes, ["SUBTYPE@", "diameter", "SHAPE@XY"], [(1, None, (3, 3))])  # a named None stays
        self.assertEqual(self.rows(self.pipes, ["OID@", "SUBTYPE@", "diameter", "material"]),
                         [(oids[0], 2, 100.0, "PVC"), (oids[1], 1, 300.0, "Steel"), (oids[2], 3, 100.0, "Steel"),
                          (oids[3], 1, None, "Steel")])
        listed = arcpy.da.ListSubtypes(self.pipes)
        self.assertEqual((listed[1]["FieldValues"]["diameter"][0], listed[2]["FieldValues"]["diameter"][0],
                          listed[3]["FieldValues"]["material"][0]), (300.0, 100.0, "Steel"))
        arcpy.management.AssignDefaultToField(self.pipes, "diameter", "", 1)          # cleared: the field's own again
        arcpy.management.AssignDefaultToField(self.pipes, "material", None, clear_value="CLEAR_VALUE")
        oid = self.insert(self.pipes, ["kind", "SHAPE@XY"], [(1, (4, 4))])[0]
        self.assertEqual(self.rows(self.pipes, ["diameter", "material"], f"fid = {oid}"), [(100.0, "Steel")])

    def test_an_update_cursor_writes_the_subtype_through_its_token(self):
        self.subtyped()
        self.insert(self.pipes, ["kind", "SHAPE@XY"], [(1, (0, 0))])
        with arcpy.da.UpdateCursor(self.pipes, ["SUBTYPE@"]) as rows:
            for row in rows:
                rows.updateRow([3])
        self.assertEqual(self.rows(self.pipes, ["kind"]), [(3,)])

    def test_tokens_on_a_class_without_the_model_are_errors(self):
        with self.assertRaises(RuntimeError):
            arcpy.da.SearchCursor(self.pipes, ["SUBTYPE@"])
        with self.assertRaises(RuntimeError):
            arcpy.da.SearchCursor(self.pipes, ["GLOBALID@"])

    def test_domains_by_subtype(self):
        self.subtyped()
        arcpy.management.CreateDomain(self.gpkg, "Sizes", "", "DOUBLE", "RANGE")
        arcpy.management.CreateDomain(self.gpkg, "MainSizes", "", "DOUBLE", "RANGE")
        arcpy.management.AssignDomainToField(self.pipes, "diameter", "Sizes")
        arcpy.management.AssignDomainToField(self.pipes, "diameter", "MainSizes", ["1"])
        listed = arcpy.da.ListSubtypes(self.pipes)
        self.assertEqual((listed[1]["FieldValues"]["diameter"][1].name, listed[2]["FieldValues"]["diameter"][1].name,
                          listed[1]["FieldValues"]["material"][1]), ("MainSizes", "Sizes", None))
        arcpy.management.RemoveDomainFromField(self.pipes, "diameter", 1)
        self.assertEqual(arcpy.da.ListSubtypes(self.pipes)[1]["FieldValues"]["diameter"][1].name, "Sizes")
        # The field's own domain is where it always was: Describe and the schema extension.
        self.assertEqual(next(f.domain for f in arcpy.ListFields(self.pipes) if f.name == "diameter"), "Sizes")
        with self.assertRaises(ExecuteError):
            arcpy.management.AssignDomainToField(self.pipes, "diameter", "Sizes", 9)

    def test_describe_answers_the_subtype_properties(self):
        described = arcpy.Describe(self.pipes)
        self.assertEqual((described.subtypeFieldName, described.defaultSubtypeCode), ("", -1))
        self.subtyped()
        arcpy.management.SetDefaultSubtype(self.pipes, 3)
        arcpy.management.AssignDefaultToField(self.pipes, "material", "PVC")
        described = arcpy.Describe(self.pipes)
        self.assertEqual((described.subtypeFieldName, described.defaultSubtypeCode), ("kind", 3))
        self.assertEqual({f.name: f.defaultValue for f in described.fields if f.name in ("material", "code")},
                         {"material": "PVC", "code": None})
        self.assertEqual(arcpy.da.Describe(self.pipes)["subtypeFieldName"], "kind")

    def test_clearing_the_subtype_field_removes_the_subtypes_and_keeps_the_fields_defaults(self):
        self.subtyped()
        arcpy.management.AssignDefaultToField(self.pipes, "material", "PVC")
        arcpy.management.AssignDefaultToField(self.pipes, "material", "Steel", 1)
        arcpy.management.SetSubtypeField(self.pipes, "", "CLEAR_SUBTYPE_FIELD")
        self.assertEqual(list(arcpy.da.ListSubtypes(self.pipes)), [0])
        self.insert(self.pipes, ["kind", "SHAPE@XY"], [(1, (0, 0))])
        self.assertEqual(self.rows(self.pipes, ["kind", "material"]), [(1, "PVC")])
        arcpy.management.DeleteField(self.pipes, "kind")   # no longer the subtype field

    def test_rename_keeps_subtypes_and_delete_takes_them(self):
        self.subtyped()
        renamed = arcpy.management.Rename(self.pipes, self.gpkg + "/mains")[0]
        self.assertEqual(sorted(arcpy.da.ListSubtypes(renamed)), [1, 2, 3])
        arcpy.management.Delete(renamed)
        connection = sqlite3.connect(self.gpkg)
        try:
            self.assertEqual(connection.execute("select count(*) from grapy_subtype").fetchone()[0], 0)
            self.assertEqual(sorted(r[0] for r in connection.execute(
                "select table_name from gpkg_extensions where extension_name = 'grapy_subtypes'")),
                ["grapy_field_default", "grapy_subtype", "grapy_subtype_domain", "grapy_subtype_field"])
        finally:
            connection.close()

    def test_relationship_rules_take_subtypes(self):
        self.subtyped()
        valves = arcpy.management.CreateTable(self.gpkg, "valves")[0]
        arcpy.management.AddField(valves, "pipe", "TEXT")
        rc = arcpy.management.CreateRelationshipClass(self.pipes, valves, self.gpkg + "/PipeValve", "SIMPLE", "has",
                                                      "on", "NONE", "ONE_TO_MANY", "NONE", "code", "pipe")[0]
        arcpy.management.AddRuleToRelationshipClass(rc, "1", 0, 4, None, 1, 1)
        arcpy.management.AddRuleToRelationshipClass(rc, "Lateral", 0, 1)   # by name
        with self.assertRaises(ExecuteError):
            arcpy.management.AddRuleToRelationshipClass(rc, 9, 0, 1)
        connection = sqlite3.connect(self.gpkg)
        rules = lambda: connection.execute("select origin_subtype, origin_maximum from grapy_relationship_rule "  # noqa: E731
                                           "order by rule_id").fetchall()
        try:
            self.assertEqual(rules(), [("1", 4), ("2", 1)])
            arcpy.management.RemoveRuleFromRelationshipClass(rc, 1)
            self.assertEqual(rules(), [("2", 1)])
            arcpy.management.RemoveRuleFromRelationshipClass(rc, remove_all="REMOVE")
            self.assertEqual(rules(), [])
        finally:
            connection.close()

    def test_a_topology_rule_on_one_subtype_is_refused_by_name(self):
        with self.assertRaises(NotBuiltError) as said:
            arcpy.management.AddRuleToTopology(self.gpkg + "/ds/t", "Must Not Overlap (Area)", self.pipes, "1")
        self.assertIn("subtype", str(said.exception))


class GlobalIDs(_Workspace):
    def test_global_ids_fill_every_row_old_and_new(self):
        old = self.insert(self.pipes, ["SHAPE@XY"], [((0, 0),), ((1, 1),)])
        arcpy.management.AddGlobalIDs(self.pipes)
        arcpy.management.AddGlobalIDs([self.pipes])   # a second time changes nothing
        new = self.insert(self.pipes, ["SHAPE@XY"], [((2, 2),)])
        ids = self.rows(self.pipes, ["OID@", "GLOBALID@"])
        self.assertEqual([r[0] for r in ids], old + new)
        self.assertTrue(all(GUID.match(r[1]) for r in ids))
        self.assertEqual(len({r[1] for r in ids}), 3)
        described = arcpy.Describe(self.pipes)
        self.assertEqual((described.hasGlobalID, described.globalIDFieldName), (True, "GlobalID"))
        self.assertEqual([f.type for f in arcpy.ListFields(self.pipes, field_type="GlobalID")], ["GlobalID"])

    def test_global_id_is_read_only_through_its_token(self):
        arcpy.management.AddGlobalIDs(self.pipes)
        self.insert(self.pipes, ["SHAPE@XY"], [((0, 0),)])
        before = self.rows(self.pipes, ["GLOBALID@"])
        with arcpy.da.UpdateCursor(self.pipes, ["GLOBALID@", "code"]) as rows:
            for row in rows:
                rows.updateRow(["{00000000-0000-0000-0000-000000000000}", "x"])
        self.assertEqual(self.rows(self.pipes, ["GLOBALID@", "code"]), [(before[0][0], "x")])

    def test_a_feature_dataset_gets_global_ids_in_every_class(self):
        dataset = arcpy.management.CreateFeatureDataset(self.gpkg, "net", 3857)[0]
        a = arcpy.management.CreateFeatureclass(dataset, "a", "POINT")[0]
        b = arcpy.management.CreateFeatureclass(dataset, "b", "POINT")[0]
        arcpy.management.AddGlobalIDs(dataset)
        self.assertEqual((arcpy.Describe(a).hasGlobalID, arcpy.Describe(b).hasGlobalID, arcpy.Describe(self.pipes).hasGlobalID),
                         (True, True, False))


class Attachments(_Workspace):
    def setUp(self):
        super().setUp()
        self.oids = self.insert(self.pipes, ["code", "SHAPE@XY"], [("A1", (0, 0)), ("B2", (1, 1)), ("C3", (2, 2))])
        self.files = os.path.join(self.directory, "files")
        os.makedirs(self.files)
        for name, body in (("A1.jpg", b"jpeg-a"), ("A1_side.png", b"png-a"), ("B2.pdf", b"%PDF-b"), ("notes.txt", b"n")):
            with open(os.path.join(self.files, name), "wb") as written:
                written.write(body)
        self.attach = self.gpkg + "/pipes__ATTACH"

    def match(self, rows, name="match"):
        table = arcpy.management.CreateTable(self.gpkg, name)[0]
        arcpy.management.AddField(table, "key", "TEXT")
        arcpy.management.AddField(table, "path", "TEXT")
        self.insert(table, ["key", "path"], rows)
        return table

    def attached(self):
        return self.rows(self.attach, ["OID@", "ATT_NAME", "CONTENT_TYPE", "DATA_SIZE", "DATA"])

    def test_enabling_makes_the_table_the_relationship_class_and_the_media_relation(self):
        arcpy.management.EnableAttachments(self.pipes)
        self.assertEqual([f.name for f in arcpy.ListFields(self.attach)],
                         ["ATTACHMENTID", "REL_GLOBALID", "CONTENT_TYPE", "ATT_NAME", "DATA_SIZE", "DATA"])
        described = arcpy.Describe(self.gpkg + "/pipes__ATTACHREL")
        self.assertEqual((described.isAttachmentRelationship, described.isComposite, described.cardinality,
                          described.originClassKeys),
                         (True, True, "OneToMany", [("GlobalID", "OriginPrimary"), ("REL_GLOBALID", "OriginForeign")]))
        self.assertEqual(arcpy.Describe(self.pipes).relationshipClassNames, ["pipes__ATTACHREL"])
        connection = sqlite3.connect(self.gpkg)
        try:
            self.assertEqual(connection.execute("select base_table_name, related_table_name, related_primary_column, "
                                                "relation_name from gpkgext_relations").fetchall(),
                             [("pipes", "pipes__ATTACH", "ATTACHMENTID", "media")])
        finally:
            connection.close()
        arcpy.management.EnableAttachments(self.pipes)
        self.assertIn("already enabled", arcpy.GetMessages(1))

    def test_a_file_of_one_class_and_its_attachments_is_still_that_class(self):
        single = arcpy.management.CreateFeatureclass(self.directory, "wells", "POINT", spatial_reference=4326)[0]
        arcpy.management.EnableAttachments(single)
        self.assertEqual(arcpy.Describe(single).dataType, "FeatureClass")
        self.assertIn("wells.gpkg", arcpy.ListFeatureClasses())

    def test_attachments_are_added_from_a_match_table(self):
        arcpy.management.EnableAttachments(self.pipes)
        table = self.match([("A1", "A1.jpg"), ("A1", os.path.join(self.files, "A1_side.png")), ("B2", "B2.pdf"),
                            ("B2", "gone.pdf"), ("Z9", "notes.txt"), ("C3", None)])
        arcpy.management.AddAttachments(self.pipes, "code", table, "key", "path", self.files)
        self.assertEqual([r[1:] for r in self.attached()],
                         [("A1.jpg", "image/jpeg", 6, b"jpeg-a"), ("A1_side.png", "image/png", 5, b"png-a"),
                          ("B2.pdf", "application/pdf", 6, b"%PDF-b")])
        self.assertIn("gone.pdf", arcpy.GetMessages(1))
        with arcpy.da.SearchRelatedRecords(self.gpkg + "/pipes__ATTACHREL", destination_fields="ATT_NAME",
                                           origin_fields="code") as related:
            self.assertEqual(list(related), [(("A1",), ("A1.jpg",)), (("A1",), ("A1_side.png",)), (("B2",), ("B2.pdf",))])
        connection = sqlite3.connect(self.gpkg)
        try:
            self.assertEqual(sorted(connection.execute("select base_id, related_id from pipes__ATTACHREL_rte")),
                             [(self.oids[0], 1), (self.oids[0], 2), (self.oids[1], 3)])
        finally:
            connection.close()

    def test_without_attachments_enabled_the_tools_are_errors(self):
        table = self.match([("A1", "A1.jpg")])
        for call in (lambda: arcpy.management.AddAttachments(self.pipes, "code", table, "key", "path", self.files),
                     lambda: arcpy.management.RemoveAttachments(self.pipes, "code", table, "key"),
                     lambda: arcpy.management.ExportAttachments(self.pipes, self.directory),
                     lambda: arcpy.management.UpgradeAttachments(self.pipes)):
            with self.assertRaises(ExecuteError):
                call()
        arcpy.management.DisableAttachments(self.pipes)
        self.assertIn("not enabled", arcpy.GetMessages(1))

    def test_attachments_are_removed_by_name_or_all_of_a_row(self):
        arcpy.management.EnableAttachments(self.pipes)
        arcpy.management.AddAttachments(self.pipes, "code", self.match([("A1", "A1.jpg"), ("A1", "A1_side.png"),
                                                                        ("B2", "B2.pdf")]), "key", "path", self.files)
        arcpy.management.RemoveAttachments(self.pipes, "code", self.match([("A1", r"C:\x\a1_SIDE.png")], "byname"),
                                           "key", "path")
        self.assertEqual([r[1] for r in self.attached()], ["A1.jpg", "B2.pdf"])
        arcpy.management.RemoveAttachments(self.pipes, "code", self.match([("B2", "")], "all"), "key")
        self.assertEqual([r[1] for r in self.attached()], ["A1.jpg"])

    def test_deleting_a_row_deletes_its_attachments_and_disabling_deletes_them_all(self):
        arcpy.management.EnableAttachments(self.pipes)
        arcpy.management.AddAttachments(self.pipes, "code", self.match([("A1", "A1.jpg"), ("B2", "B2.pdf")]), "key",
                                        "path", self.files)
        with arcpy.da.UpdateCursor(self.pipes, ["code"], "code = 'A1'") as rows:
            for _ in rows:
                rows.deleteRow()
        self.assertEqual([r[1] for r in self.attached()], ["B2.pdf"])
        arcpy.management.DisableAttachments(self.pipes)
        self.assertFalse(arcpy.Exists(self.attach))
        self.assertEqual(arcpy.Describe(self.pipes).relationshipClassNames, [])
        arcpy.management.EnableAttachments(self.pipes)       # enabled again: none of the old attachments are there
        self.assertEqual(self.attached(), [])

    def test_rename_carries_the_attachments_and_delete_drops_them(self):
        arcpy.management.EnableAttachments(self.pipes)
        arcpy.management.AddAttachments(self.pipes, "code", self.match([("B2", "B2.pdf")]), "key", "path", self.files)
        mains = arcpy.management.Rename(self.pipes, self.gpkg + "/mains")[0]
        self.assertEqual(self.rows(self.gpkg + "/mains__ATTACH", ["ATT_NAME"]), [("B2.pdf",)])
        self.assertTrue(arcpy.Describe(self.gpkg + "/mains__ATTACHREL").isAttachmentRelationship)
        arcpy.management.Delete(mains)
        self.assertFalse(arcpy.Exists(self.gpkg + "/mains__ATTACH"))
        connection = sqlite3.connect(self.gpkg)
        try:
            self.assertEqual(connection.execute("select count(*) from grapy_relationship_class").fetchone()[0], 0)
        finally:
            connection.close()

    def test_a_match_table_is_generated_by_each_pattern(self):
        out = self.gpkg + "/m"
        arcpy.management.GenerateAttachmentMatchTable(self.pipes, self.files, out, "code")
        self.assertEqual(self.rows(out, ["MATCHID", "FILENAME"]), [(self.oids[0], "A1.jpg"), (self.oids[1], "B2.pdf")])
        arcpy.management.GenerateAttachmentMatchTable(self.pipes, self.files, out, "code", match_pattern="PREFIX")
        self.assertEqual(self.rows(out, ["MATCHID", "FILENAME"]),
                         [(self.oids[0], "A1.jpg"), (self.oids[0], "A1_side.png"), (self.oids[1], "B2.pdf")])
        arcpy.management.GenerateAttachmentMatchTable(self.pipes, self.files, out, "code", ".png; *.pdf", "ABSOLUTE", "ANY")
        self.assertEqual(self.rows(out, ["MATCHID", "FILENAME"]),
                         [(self.oids[0], os.path.abspath(os.path.join(self.files, "A1_side.png"))),
                          (self.oids[1], os.path.abspath(os.path.join(self.files, "B2.pdf")))])
        arcpy.management.EnableAttachments(self.pipes)
        arcpy.management.GenerateAttachmentMatchTable(self.pipes, self.files, out, "code", match_pattern="SUFFIX")
        arcpy.management.AddAttachments(self.pipes, "OID@" if False else "fid", out, "MATCHID", "FILENAME", self.files)
        self.assertEqual([r[1] for r in self.attached()], ["A1.jpg", "B2.pdf"])
        with self.assertRaises(NotBuiltError):
            arcpy.management.GenerateAttachmentMatchTable(self.pipes, self.files, os.path.join(self.directory, "m.dbf"),
                                                          "code")

    def test_attachments_are_exported_by_each_name_format(self):
        arcpy.management.AddField(self.pipes, "street", "TEXT")
        with arcpy.da.UpdateCursor(self.pipes, ["code", "street"]) as rows:
            for row in rows:
                rows.updateRow([row[0], {"A1": "Main/High", "B2": None}.get(row[0])])
        arcpy.management.EnableAttachments(self.pipes)
        arcpy.management.AddAttachments(self.pipes, "code", self.match([("A1", "A1.jpg"), ("A1", "A1_side.png"),
                                                                        ("B2", "B2.pdf"), ("B2", "B2.pdf")]),
                                        "key", "path", self.files)
        listing = lambda folder: sorted(os.path.relpath(os.path.join(r, n), folder).replace("\\", "/")  # noqa: E731
                                        for r, _, names in os.walk(folder) for n in names)
        original = os.path.join(self.directory, "original")
        arcpy.management.ExportAttachments(self.pipes, original)
        self.assertEqual(listing(original), ["A1.jpg", "A1_side.png", "B2.pdf", "B2_1.pdf"])
        with open(os.path.join(original, "A1_side.png"), "rb") as read:
            self.assertEqual(read.read(), b"png-a")
        replaced = os.path.join(self.directory, "replaced")
        arcpy.management.ExportAttachments(self.pipes, replaced, "code", "REPLACE", ["street", "code"])
        self.assertEqual(listing(replaced), ["A1/Main-High_A1.jpg", "A1/Main-High_A1.png", "B2/None_B2.pdf",
                                             "B2/None_B2_1.pdf"])
        prefixed = os.path.join(self.directory, "prefixed")
        arcpy.management.ExportAttachments(self.pipes, prefixed, None, "PREFIX", "code")
        self.assertEqual(listing(prefixed), ["A1_A1.jpg", "A1_A1_side.png", "B2_B2.pdf", "B2_B2_1.pdf"])
        suffixed = os.path.join(self.directory, "suffixed")
        arcpy.management.MakeFeatureLayer(self.pipes, "only_b", "code = 'B2'")
        arcpy.management.ExportAttachments("only_b", suffixed, None, "SUFFIX", "code")   # a layer's query is honoured
        self.assertEqual(listing(suffixed), ["B2_B2.pdf", "B2_B2_1.pdf"])
        with self.assertRaises(ExecuteError):
            arcpy.management.ExportAttachments(self.pipes, suffixed, None, "REPLACE")

    def test_upgrading_adds_the_documented_fields_and_downgrading_removes_them(self):
        arcpy.management.EnableAttachments(self.pipes)
        names = lambda: [f.name for f in arcpy.ListFields(self.attach)][6:]  # noqa: E731
        arcpy.management.UpgradeAttachments(self.pipes)
        self.assertEqual(names(), ["KEYWORDS", "EXIFINFO"])
        arcpy.management.UpgradeAttachments(self.pipes, "EXIF_AND_ALT")
        self.assertEqual(names(), ["KEYWORDS", "EXIFINFO", "ATT_TITLE", "ATT_ALT_TEXT", "ATT_CAPTION", "ATT_CREDIT"])
        arcpy.management.DowngradeAttachments(self.pipes)
        self.assertEqual(names(), [])
        with self.assertRaises(ExecuteError):
            arcpy.management.UpgradeAttachments(self.pipes, "ALL")

    def test_the_global_id_field_of_a_class_with_attachments_cannot_be_deleted(self):
        arcpy.management.EnableAttachments(self.pipes)
        with self.assertRaises(ExecuteError):
            arcpy.management.DeleteField(self.pipes, "GlobalID")


class Photos(_Workspace):
    def setUp(self):
        super().setUp()
        self.folder = os.path.join(self.directory, "photos")
        os.makedirs(os.path.join(self.folder, "day2"))
        # 41° 0' 36" N, 28° 58' 48" E: 41.01, 28.98; 50.5 m; facing east.
        exif_jpeg(os.path.join(self.folder, "a.jpg"), "2026:05:01 11:35:17", ("N", ((41, 1), (0, 1), (36, 1))),
                  ("E", ((28, 1), (58, 1), (48, 1))), (0, (505, 10)), (90, 1))
        # 33° 51' 54" S, 151° 12' 36" W, 10 m below sea level.
        exif_jpeg(os.path.join(self.folder, "day2", "b.JPG"), "2026:05:01 12:00:00", ("S", ((33, 1), (51, 1), (54, 1))),
                  ("W", ((151, 1), (12, 1), (36, 1))), (1, (10, 1)))
        exif_jpeg(os.path.join(self.folder, "c.jpg"), "2026:05:01 11:40:00")                      # no position
        exif_jpeg(os.path.join(self.folder, "d.jpg"), None, ("N", ((0, 1), (0, 1), (0, 1))),
                  ("E", ((0, 1), (0, 1), (0, 1))))                                                # at 0,0
        with open(os.path.join(self.folder, "e.jpg"), "wb") as written:
            written.write(b"not a photo")
        with open(os.path.join(self.folder, "f.heic"), "wb") as written:
            written.write(b"\0\0\0\x18ftypheic")

    def test_exif_is_read_from_its_parts(self):
        taken, x, y, z, direction = _exif.read(os.path.join(self.folder, "a.jpg"))
        self.assertEqual(str(taken), "2026-05-01 11:35:17")
        self.assertAlmostEqual(x, 28.98, places=12)
        self.assertAlmostEqual(y, 41.01, places=12)
        self.assertEqual((z, direction), (50.5, 90.0))
        _, x, y, z, direction = _exif.read(os.path.join(self.folder, "day2", "b.JPG"))
        self.assertAlmostEqual(x, -(151 + 12 / 60 + 36 / 3600), places=12)
        self.assertAlmostEqual(y, -(33 + 51 / 60 + 54 / 3600), places=12)
        self.assertEqual((z, direction), (-10.0, None))
        with self.assertRaises(_exif.Unreadable):
            _exif.read(os.path.join(self.folder, "e.jpg"))

    def test_geotagged_photos_become_points_with_their_photos_attached(self):
        out = self.gpkg + "/photos"
        invalid = self.gpkg + "/invalid"
        arcpy.management.GeoTaggedPhotosToPoints(self.folder, out, invalid)
        rows = self.rows(out, ["Name", "DateTime", "Direction", "X", "Y", "Z", "SHAPE@XY"])
        self.assertEqual([r[0] for r in rows], ["a.jpg", "c.jpg", "d.jpg", "b.JPG"])    # by path: day2/ after d.jpg
        self.assertEqual(rows[1][1:], (datetime.datetime(2026, 5, 1, 11, 40), None, None, None, None, None))
        self.assertEqual(rows[2][3:], (None, None, None, None))                     # 0,0 is no position
        a = rows[0]
        self.assertEqual((a[1], a[2], a[5]), (datetime.datetime(2026, 5, 1, 11, 35, 17), 90.0, 50.5))
        self.assertAlmostEqual(a[6][0], 28.98, places=9)
        self.assertAlmostEqual(a[6][1], 41.01, places=9)
        self.assertEqual(arcpy.Describe(out).spatialReference.factoryCode, 4326)
        self.assertEqual(sorted(r[0] for r in self.rows(invalid, ["Name"])), ["c.jpg", "d.jpg", "e.jpg", "f.heic"])
        self.assertIn("f.heic", arcpy.GetMessages(1))
        with arcpy.da.SearchRelatedRecords(self.gpkg + "/photos__ATTACHREL", origin_fields="Name",
                                           destination_fields=["ATT_NAME", "DATA_SIZE"]) as related:
            pairs = list(related)
        self.assertEqual([p[0][0] for p in pairs], ["a.jpg", "c.jpg", "d.jpg", "b.JPG"])
        self.assertEqual(pairs[0][1], ("a.jpg", os.path.getsize(os.path.join(self.folder, "a.jpg"))))

    def test_only_geotagged_photos_and_no_attachments(self):
        out = self.gpkg + "/located"
        arcpy.management.GeoTaggedPhotosToPoints(self.folder, out, None, "ONLY_GEOTAGGED", "NO_ATTACHMENTS")
        self.assertEqual([r[0] for r in self.rows(out, ["Name"])], ["a.jpg", "b.JPG"])
        self.assertFalse(arcpy.Exists(self.gpkg + "/located__ATTACH"))
        arcpy.management.GeoTaggedPhotosToPoints(self.folder, self.gpkg + "/kw",
                                                 **{"Include_Non-GeoTagged_Photos": "ONLY_GEOTAGGED",
                                                    "Add_Photos_As_Attachments": "NO_ATTACHMENTS"})
        self.assertEqual(len(self.rows(self.gpkg + "/kw", ["Name"])), 2)

    def test_photos_are_matched_to_the_closest_row_in_time(self):
        track = arcpy.management.CreateFeatureclass(self.gpkg, "track", "POINT", spatial_reference=4326)[0]
        arcpy.management.AddField(track, "at", "DATE")
        arcpy.management.AddField(track, "label", "TEXT")
        import datetime
        oids = self.insert(track, ["at", "label", "SHAPE@XY"], [
            (datetime.datetime(2026, 5, 1, 11, 35, 32), "p1", (0, 0)),
            (datetime.datetime(2026, 5, 1, 11, 40, 20), "p2", (1, 1)),
            (datetime.datetime(2026, 5, 1, 13, 0, 0), "p3", (2, 2))])
        out, unmatched = self.gpkg + "/matched", self.gpkg + "/unmatched"
        # Camera 15 s behind: a.jpg 11:35:17 + 15 = 11:35:32, p1 exactly; c.jpg 11:40:15 is 5 s from p2;
        # b.JPG 12:00:15 is 19.75 min from p2 and 59.75 from p3, beyond 600 s; d.jpg has no time; e.jpg no Exif.
        arcpy.management.MatchPhotosToRowsByTime(self.folder, track, "at", out, unmatched, "ADD_ATTACHMENTS", 600, 15)
        self.assertEqual(self.rows(out, ["IN_FID", "Photo_Name", "Match_Diff"]),
                         [(oids[0], "a.jpg", 0.0), (oids[1], "c.jpg", 5.0)])
        self.assertEqual(sorted(r[0] for r in self.rows(unmatched, ["Photo_Name"])), ["b.JPG", "d.jpg", "e.jpg"])
        with arcpy.da.SearchRelatedRecords(track + "__ATTACHREL", origin_fields="label",
                                           destination_fields="ATT_NAME") as related:
            self.assertEqual(list(related), [(("p1",), ("a.jpg",)), (("p2",), ("c.jpg",))])
        # No tolerance: any distance matches.
        arcpy.management.MatchPhotosToRowsByTime(self.folder, track, "at", out, None, "NO_ATTACHMENTS", 0, 15)
        self.assertEqual([r[:2] for r in self.rows(out, ["IN_FID", "Photo_Name"])],
                         [(oids[0], "a.jpg"), (oids[1], "c.jpg"), (oids[1], "b.JPG")])
        with self.assertRaises(ExecuteError):
            arcpy.management.MatchPhotosToRowsByTime(self.folder, track, "label", out)


class Refusals(unittest.TestCase):
    def test_the_built_names_answer_and_the_remaining_one_says_why(self):
        for name in ("AddSubtype", "AlterSubtype", "RemoveSubtype", "SetDefaultSubtype", "SetSubtypeField",
                     "AssignDefaultToField", "AddGlobalIDs", "AddAttachments", "RemoveAttachments", "EnableAttachments",
                     "DisableAttachments", "ExportAttachments", "GenerateAttachmentMatchTable", "DowngradeAttachments",
                     "UpgradeAttachments", "GeoTaggedPhotosToPoints", "MatchPhotosToRowsByTime"):
            self.assertNotIn(name, arcpy.management._REFUSED)
            self.assertTrue(callable(getattr(arcpy.management, name)))
        self.assertTrue(callable(arcpy.da.ListSubtypes))
        with self.assertRaises(NotBuiltError) as said:
            arcpy.management.MigrateRelationshipClass("x.gpkg/rc")
        self.assertIn("foreign key", str(said.exception))


if __name__ == "__main__":
    unittest.main()
