"""Attribute rules, field groups and contingent values, and database sequences held in the GeoPackage (ADR-235),
against hand-worked answers: each rule's effect on an edit made through grapy's cursors is worked out by hand from the
rule's script and the rows written."""
import csv
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _runtime  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402


def square(x0, y0, size):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x0, y0 + size), arcpy.Point(x0 + size, y0 + size),
                                      arcpy.Point(x0 + size, y0)]), arcpy.SpatialReference(3857))


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        _runtime.MESSAGES.clear()
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "city")[0]
        self.parcels = arcpy.management.CreateFeatureclass(self.gpkg, "parcels", "POLYGON", spatial_reference=3857)[0]
        for name, kind in (("kind", "SHORT"), ("area", "DOUBLE"), ("label", "TEXT"), ("value", "DOUBLE"),
                           ("parcel_id", "TEXT"), ("zone", "TEXT"), ("edits", "LONG")):
            arcpy.management.AddField(self.parcels, name, kind)

    def tearDown(self):
        for name in list(arcpy.management._LAYERS):
            arcpy.management.Delete(name)

    def rule(self, name, kind, script, **options):
        arcpy.management.AddAttributeRule(self.parcels, name, kind, script, **options)

    def insert(self, path, fields, rows):
        with arcpy.da.InsertCursor(path, fields) as written:
            return [written.insertRow(r) for r in rows]

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    def update(self, path, fields, change, where=None):
        with arcpy.da.UpdateCursor(path, fields, where) as rows:
            for row in rows:
                rows.updateRow(change(list(row)))


class CalculationRules(_Workspace):
    def test_a_calculation_rule_sets_its_field_on_insert_and_on_update(self):
        self.rule("area", "CALCULATION", "Area($feature)", field="area", triggering_events="INSERT;UPDATE")
        self.insert(self.parcels, ["kind", "SHAPE@"], [(1, square(0, 0, 10)), (2, square(0, 0, 3))])
        self.assertEqual(self.rows(self.parcels, ["OID@", "area"]), [(1, 100.0), (2, 9.0)])
        self.update(self.parcels, ["SHAPE@"], lambda row: [square(0, 0, 4)], "fid = 2")
        self.assertEqual(self.rows(self.parcels, ["OID@", "area"]), [(1, 100.0), (2, 16.0)])

    def test_rules_run_in_evaluation_order_and_each_sees_what_the_one_before_set(self):
        self.rule("area", "CALCULATION", "Area($feature)", field="area", triggering_events="INSERT")
        self.rule("label", "CALCULATION", "`${$feature.kind}: ${$feature.area} m²`", field="label",
                  triggering_events="INSERT")
        self.insert(self.parcels, ["kind", "SHAPE@"], [(3, square(0, 0, 10))])
        self.assertEqual(self.rows(self.parcels, ["label"]), [("3: 100 m²",)])
        # Moved first, the label rule sees the area before it is calculated: null, written as empty text.
        arcpy.management.ReorderAttributeRule(self.parcels, "label", 1)
        self.assertEqual([(r.name, r.evaluationOrder) for r in arcpy.Describe(self.parcels).attributeRules],
                         [("label", 1), ("area", 2)])
        self.insert(self.parcels, ["kind", "SHAPE@"], [(4, square(0, 0, 10))])
        self.assertEqual(self.rows(self.parcels, ["label"], "fid = 2"), [("4:  m²",)])

    def test_an_update_rule_with_triggering_fields_runs_only_when_one_of_them_changes(self):
        self.rule("count", "CALCULATION", "DefaultValue($feature.edits, 0) + 1", field="edits",
                  triggering_events="UPDATE", triggering_fields="value")
        self.insert(self.parcels, ["value", "edits"], [(1, 0)])
        self.update(self.parcels, ["zone"], lambda row: ["A"])   # not a triggering field
        self.update(self.parcels, ["value"], lambda row: [2])    # a triggering field
        self.update(self.parcels, ["value"], lambda row: [2])    # the same value: nothing changed
        self.assertEqual(self.rows(self.parcels, ["edits", "zone"]), [(1, "A")])

    def test_original_feature_and_edit_context(self):
        # IIf would evaluate both of its values, and $originalFeature is null on an insert: an if decides first.
        self.rule("history", "CALCULATION",
                  "if ($editcontext.editType == 'UPDATE') { return `${$originalFeature.value}->${$feature.value}` } "
                  "$editcontext.editType", field="label", triggering_events="INSERT;UPDATE")
        self.insert(self.parcels, ["value"], [(5,)])
        self.assertEqual(self.rows(self.parcels, ["label"]), [("INSERT",)])
        self.update(self.parcels, ["value"], lambda row: [7])
        self.assertEqual(self.rows(self.parcels, ["label"]), [("5->7",)])

    def test_a_non_editable_field_cannot_be_edited_directly(self):
        self.rule("id", "CALCULATION", "'P-' + $feature.kind", field="parcel_id", triggering_events="INSERT",
                  is_editable="NONEDITABLE")
        self.insert(self.parcels, ["kind"], [(9,)])
        self.assertEqual(self.rows(self.parcels, ["parcel_id"]), [("P-9",)])
        with self.assertRaisesRegex(RuntimeError, "is calculated by the attribute rule 'id'"):
            self.update(self.parcels, ["parcel_id"], lambda row: ["X"])
        self.update(self.parcels, ["kind"], lambda row: [8])  # other fields still edit
        self.assertEqual(self.rows(self.parcels, ["kind", "parcel_id"]), [(8, "P-9")])

    def test_a_rule_returning_a_dictionary_sets_attributes_and_the_geometry(self):
        self.rule("snap", "CALCULATION",
                  "return {result: {attributes: {zone: 'Z' + $feature.kind, value: 10}, "
                  "geometry: Buffer(Centroid($feature), 1)}}", triggering_events="INSERT")
        self.insert(self.parcels, ["kind", "SHAPE@"], [(2, square(0, 0, 10))])
        zone, value, shape = self.rows(self.parcels, ["zone", "value", "SHAPE@"])[0]
        self.assertEqual((zone, value), ("Z2", 10.0))
        self.assertAlmostEqual(shape.centroid.X, 5.0)
        self.assertAlmostEqual(shape.area, 3.1365, places=3)  # 64 sides of radius 1

    def test_a_rule_without_a_field_must_return_a_dictionary(self):
        self.rule("bare", "CALCULATION", "5", triggering_events="INSERT")
        with self.assertRaisesRegex(RuntimeError, "has no field"):
            self.insert(self.parcels, ["kind"], [(1,)])
        self.assertEqual(self.rows(self.parcels, ["OID@"]), [])

    def test_an_error_message_refuses_the_edit(self):
        self.rule("guard", "CALCULATION", "if ($feature.value < 0) { return {errorMessage: 'negative'} } "
                  "$feature.value", field="value", triggering_events="INSERT")
        self.insert(self.parcels, ["value"], [(1,)])
        with self.assertRaisesRegex(RuntimeError, "refused the insert: negative"):
            self.insert(self.parcels, ["value"], [(-1,)])
        self.assertEqual(self.rows(self.parcels, ["value"]), [(1.0,)])

    def test_a_rule_on_one_subtype_runs_on_that_subtype_only(self):
        arcpy.management.SetSubtypeField(self.parcels, "kind")
        arcpy.management.AddSubtype(self.parcels, 1, "Residential")
        arcpy.management.AddSubtype(self.parcels, 2, "Commercial")
        self.rule("zone", "CALCULATION", "'C'", field="zone", triggering_events="INSERT", subtype="Commercial")
        self.insert(self.parcels, ["kind"], [(1,), (2,)])
        self.assertEqual(self.rows(self.parcels, ["kind", "zone"]), [(1, None), (2, "C")])
        self.assertEqual(arcpy.Describe(self.parcels).attributeRules[0].subtypeCode, 2)

    def test_a_rule_reads_other_tables_through_datastore(self):
        zones = arcpy.management.CreateFeatureclass(self.gpkg, "zones", "POLYGON", spatial_reference=3857)[0]
        arcpy.management.AddField(zones, "code", "TEXT")
        arcpy.management.AddField(zones, "rate", "DOUBLE")
        self.insert(zones, ["code", "rate", "SHAPE@"], [("A", 2.0, square(0, 0, 50)), ("B", 3.0, square(50, 0, 50)),
                                                        ("C", 5.0, square(500, 0, 50))])
        self.rule("zone", "CALCULATION",
                  "var z = First(Intersects(FeatureSetByName($datastore, 'ZONES', ['code', 'rate']), "
                  "Centroid($feature))); if (z == null) { return null } z.code", field="zone", triggering_events="INSERT")
        self.rule("value", "CALCULATION",
                  "var all = FeatureSetByName($datastore, 'zones', ['*'], false); var low = 2.5; "
                  "Count(all) * 1000 + Sum(Filter(all, 'rate > @low'), 'rate') * 10 + Max(all, 'rate')",
                  field="value", triggering_events="INSERT")
        self.insert(self.parcels, ["SHAPE@"], [(square(60, 10, 10),), (square(10, 10, 10),), (square(300, 0, 1),)])
        # Three zones; rates above 2.5 are 3 and 5, so 8 × 10; the largest is 5: 3000 + 80 + 5.
        self.assertEqual(self.rows(self.parcels, ["zone", "value"]), [("B", 3085.0), ("A", 3085.0), (None, 3085.0)])

    def test_domain_schema_and_subtype_functions_read_the_table(self):
        arcpy.management.CreateDomain(self.gpkg, "kinds", "kinds", "SHORT", "CODED")
        for code, name in ((1, "House"), (2, "Shop")):
            arcpy.management.AddCodedValueToDomain(self.gpkg, "kinds", code, name)
        arcpy.management.AssignDomainToField(self.parcels, "kind", "kinds")
        arcpy.management.AddField(self.parcels, "class", "SHORT")
        arcpy.management.SetSubtypeField(self.parcels, "class")
        arcpy.management.AddSubtype(self.parcels, 1, "Urban")
        self.rule("label", "CALCULATION",
                  "var s = Schema($feature); Concatenate([DomainName($feature, 'kind'), DomainCode($feature, 'kind', "
                  "'Shop'), Domain($feature, 'kind').codedValues[0].name, s.geometryType, s.objectIdField, "
                  "SubtypeName($feature), SubtypeCode($feature), Subtypes($feature).subtypeField, "
                  "GdbVersion($feature)], '|')", field="label", triggering_events="INSERT")
        self.insert(self.parcels, ["kind", "class"], [(2, 1)])
        self.assertEqual(self.rows(self.parcels, ["label"]),
                         [("Shop|2|House|esriGeometryPolygon|fid|Urban|1|class|",)])

    def test_a_rule_edits_another_class_and_that_class_runs_its_own_rules(self):
        log = arcpy.management.CreateTable(self.gpkg, "log")[0]
        arcpy.management.AddField(log, "what", "TEXT")
        arcpy.management.AddField(log, "stamp", "TEXT")
        arcpy.management.AddAttributeRule(log, "stamp", "CALCULATION", "Upper($feature.what)", field="stamp",
                                          triggering_events="INSERT;UPDATE")
        self.rule("log", "CALCULATION",
                  "return {edit: [{className: 'log', adds: [{attributes: {what: 'added ' + $feature.kind}}]}]}",
                  triggering_events="INSERT", exclude_from_client_evaluation="EXCLUDE")
        self.rule("unlog", "CALCULATION",
                  "return {edit: [{className: 'log', updates: [{objectID: 1, attributes: {what: 'gone'}}], "
                  "deletes: [{objectID: 2}]}]}", triggering_events="DELETE")
        self.insert(self.parcels, ["kind"], [(1,), (2,)])
        self.assertEqual(self.rows(log, ["OID@", "what", "stamp"]), [(1, "added 1", "ADDED 1"), (2, "added 2", "ADDED 2")])
        with arcpy.da.UpdateCursor(self.parcels, ["kind"], "kind = 1") as rows:
            for _ in rows:
                rows.deleteRow()
        self.assertEqual(self.rows(log, ["OID@", "what", "stamp"]), [(1, "gone", "GONE")])
        self.assertEqual(self.rows(self.parcels, ["kind"]), [(2,)])

    def test_a_rule_that_edits_itself_forever_is_stopped(self):
        self.rule("loop", "CALCULATION",
                  "return {edit: [{className: 'parcels', adds: [{attributes: {kind: 1}}]}]}", triggering_events="INSERT")
        with self.assertRaisesRegex(RuntimeError, "eight levels deep"):
            self.insert(self.parcels, ["kind"], [(1,)])

    def test_disabled_and_deleted_rules_do_not_run(self):
        self.rule("a", "CALCULATION", "'a'", field="zone", triggering_events="INSERT")
        arcpy.management.DisableAttributeRules(self.parcels, "a", "CONSTRAINT")  # another type: left as it is
        self.insert(self.parcels, ["kind"], [(1,)])
        arcpy.management.DisableAttributeRules(self.parcels, ["a"])
        self.insert(self.parcels, ["kind"], [(2,)])
        arcpy.management.EnableAttributeRules(self.parcels, "a")
        self.insert(self.parcels, ["kind"], [(3,)])
        arcpy.management.DeleteAttributeRule(self.parcels, "a")
        self.insert(self.parcels, ["kind"], [(4,)])
        self.assertEqual(self.rows(self.parcels, ["zone"]), [("a",), (None,), ("a",), (None,)])
        self.assertEqual(arcpy.Describe(self.parcels).attributeRules, [])


class ConstraintRules(_Workspace):
    def test_a_constraint_refuses_an_insert_update_or_delete_with_its_error(self):
        self.rule("cap", "CONSTRAINT", "$feature.value <= 300", triggering_events="INSERT;UPDATE", error_number=999,
                  error_message="Invalid value")
        self.rule("keep", "CONSTRAINT", "$feature.zone != 'locked'", triggering_events="DELETE", error_number=5,
                  error_message="locked")
        self.insert(self.parcels, ["value", "zone"], [(100, "locked"), (200, None)])
        with self.assertRaisesRegex(RuntimeError, "'cap' of 'parcels' refused the insert: 999: Invalid value"):
            self.insert(self.parcels, ["value"], [(301,)])
        with self.assertRaisesRegex(RuntimeError, "refused the update: 999: Invalid value"):
            self.update(self.parcels, ["value"], lambda row: [400])
        with self.assertRaisesRegex(RuntimeError, "refused the delete: 5: locked"):
            with arcpy.da.UpdateCursor(self.parcels, ["zone"]) as rows:
                for _ in rows:
                    rows.deleteRow()
        self.assertEqual(self.rows(self.parcels, ["value", "zone"]), [(100.0, "locked"), (200.0, None)])

    def test_a_constraint_may_answer_its_own_message_and_must_answer_a_boolean(self):
        self.rule("msg", "CONSTRAINT", "IIf($feature.value > 0, true, {errorMessage: 'not positive: ' + $feature.value})",
                  triggering_events="INSERT", error_number=1, error_message="unused")
        with self.assertRaisesRegex(RuntimeError, "refused the insert: 1: not positive: -2"):
            self.insert(self.parcels, ["value"], [(-2,)])
        arcpy.management.AlterAttributeRule(self.parcels, "msg", script_expression="1")
        with self.assertRaisesRegex(RuntimeError, "must return true, false"):
            self.insert(self.parcels, ["value"], [(2,)])

    def test_calculation_rules_run_before_constraint_rules(self):
        self.rule("check", "CONSTRAINT", "$feature.area < 50", triggering_events="INSERT", error_number=1,
                  error_message="too big")
        self.rule("area", "CALCULATION", "Area($feature)", field="area", triggering_events="INSERT")
        self.insert(self.parcels, ["SHAPE@"], [(square(0, 0, 5),)])
        with self.assertRaisesRegex(RuntimeError, "too big"):
            self.insert(self.parcels, ["SHAPE@"], [(square(0, 0, 10),)])

    def test_a_script_error_names_the_rule(self):
        self.rule("bad", "CONSTRAINT", "$feature.nothing > 1", triggering_events="INSERT", error_number=1,
                  error_message="x")
        with self.assertRaisesRegex(RuntimeError, "'bad' of 'parcels' failed on insert: The feature has no field "
                                                  "'nothing'"):
            self.insert(self.parcels, ["value"], [(1,)])


class RuleTools(_Workspace):
    def test_the_documented_errors(self):
        with self.assertRaisesRegex(ExecuteError, "needs an error number and an error message"):
            self.rule("c", "CONSTRAINT", "true", triggering_events="INSERT")
        with self.assertRaisesRegex(ExecuteError, "constraint rule is NOT_BATCH"):
            self.rule("c", "CONSTRAINT", "true", error_number=1, error_message="m", batch="BATCH")
        with self.assertRaisesRegex(ExecuteError, "needs a triggering event"):
            self.rule("c", "CALCULATION", "1", field="value")
        with self.assertRaisesRegex(ExecuteError, "do not apply to a batch rule"):
            self.rule("c", "CALCULATION", "1", field="value", batch="BATCH", triggering_events="INSERT")
        with self.assertRaisesRegex(ExecuteError, "severity applies to validation rules only"):
            self.rule("c", "CALCULATION", "1", field="value", triggering_events="INSERT", severity=2)
        with self.assertRaisesRegex(ExecuteError, "1 to 64 characters"):
            self.rule("n" * 65, "CALCULATION", "1", field="value", triggering_events="INSERT")
        with self.assertRaisesRegex(ExecuteError, "not valid"):
            self.rule("c", "CALCULATION", "var = ;", field="value", triggering_events="INSERT")
        with self.assertRaisesRegex(ExecuteError, "field 'nope' does not exist"):
            self.rule("c", "CALCULATION", "1", field="nope", triggering_events="INSERT")
        self.rule("c", "CALCULATION", "1", field="value", triggering_events="INSERT")
        with self.assertRaisesRegex(ExecuteError, "already has a rule named 'C'"):
            self.rule("C", "CALCULATION", "1", field="value", triggering_events="INSERT")
        # A validation rule is batch whatever ``batch`` says.
        self.rule("v", "VALIDATION", "true", error_number=1, error_message="m", severity=3)
        found = {r.name: r for r in arcpy.Describe(self.parcels).attributeRules}
        self.assertTrue(found["v"].batch)
        self.assertEqual((found["v"].type, found["v"].severity, found["v"].triggeringEvents), ("esriARTValidation", 3, []))
        self.assertEqual((found["c"].type, found["c"].fieldName, found["c"].triggeringEvents, found["c"].isEnabled,
                          found["c"].id), ("esriARTCalculation", "value", ["esriARTEInsert"], True, 1))

    def test_alter_keeps_resets_and_replaces(self):
        self.rule("c", "CONSTRAINT", "true", triggering_events="INSERT", error_number=1, error_message="m",
                  description="first", tags="a;b")
        arcpy.management.AlterAttributeRule(self.parcels, "c", "second", "999", "Invalid", "Pipeline;OP",
                                            "INSERT;UPDATE")
        rule = arcpy.Describe(self.parcels).attributeRules[0]
        self.assertEqual((rule.description, rule.errorNumber, rule.errorMessage, rule.tags, rule.triggeringEvents),
                         ("second", 999, "Invalid", "Pipeline;OP", ["esriARTEInsert", "esriARTEUpdate"]))
        arcpy.management.AlterAttributeRule(self.parcels, "c", description="RESET", tags="RESET")
        rule = arcpy.Describe(self.parcels).attributeRules[0]
        self.assertEqual((rule.description, rule.tags, rule.errorMessage), ("", "", "Invalid"))
        with self.assertRaisesRegex(ExecuteError, "cannot be cleared"):
            arcpy.management.AlterAttributeRule(self.parcels, "c", error_message="RESET")
        with self.assertRaisesRegex(ExecuteError, "no rule named 'x'"):
            arcpy.management.AlterAttributeRule(self.parcels, "x", description="y")

    def test_export_writes_the_twenty_columns_and_import_reads_them_back(self):
        arcpy.management.SetSubtypeField(self.parcels, "kind")
        arcpy.management.AddSubtype(self.parcels, 1, "Residential")
        self.rule("area", "CALCULATION", "Area($feature)", field="area", triggering_events="INSERT;UPDATE",
                  subtype="Residential", triggering_fields="Shape", is_editable="NONEDITABLE", tags="geo")
        self.rule("cap", "CONSTRAINT", "$feature.value <= 300", triggering_events="INSERT", error_number=999,
                  error_message="too high")
        arcpy.management.DisableAttributeRules(self.parcels, "cap")
        out = os.path.join(self.directory, "rules.csv")
        arcpy.management.ExportAttributeRules(self.parcels, out)
        with open(out, encoding="utf-8", newline="") as read:
            rows = list(csv.reader(read))
        self.assertEqual(rows[0], ["NAME", "DESCRIPTION", "TYPE", "SUBTYPE", "FIELD", "ISEDITABLE", "TRIGGERINSERT",
                                   "TRIGGERDELETE", "TRIGGERUPDATE", "SCRIPTEXPRESSION", "ERRORNUMBER", "ERRORMESSAGE",
                                   "EXCLUDECLIENTEVALUATION", "ISENABLED", "BATCH", "SEVERITY", "TAGS", "CATEGORY",
                                   "CHECKPARAMETERS", "TRIGGERINGFIELDS"])
        self.assertEqual(rows[1], ["area", "", "CALCULATION", "Residential", "area", "False", "True", "False", "True",
                                   "Area($feature)", "", "", "False", "True", "False", "", "geo", "", "", '["geom"]'])
        self.assertEqual(rows[2][:3] + rows[2][10:15], ["cap", "", "CONSTRAINT", "999", "too high", "False", "False",
                                                        "False"])
        other = arcpy.management.CreateFeatureclass(self.gpkg, "other", "POLYGON", spatial_reference=3857)[0]
        for name, kind in (("kind", "SHORT"), ("area", "DOUBLE"), ("value", "DOUBLE")):
            arcpy.management.AddField(other, name, kind)
        arcpy.management.SetSubtypeField(other, "kind")
        arcpy.management.AddSubtype(other, 1, "Residential")
        arcpy.management.ImportAttributeRules(other, out)
        imported = {r.name: r for r in arcpy.Describe(other).attributeRules}
        self.assertEqual((imported["area"].subtypeCodes, imported["area"].userEditable, imported["area"].triggeringFields,
                          imported["cap"].isEnabled, imported["cap"].errorNumber), ([1], False, ["geom"], False, 999))
        self.insert(other, ["kind", "SHAPE@"], [(1, square(0, 0, 2))])
        self.assertEqual(self.rows(other, ["area"]), [(4.0,)])

    def test_rules_go_with_their_table_when_it_is_renamed_or_deleted(self):
        self.rule("z", "CALCULATION", "'z'", field="zone", triggering_events="INSERT")
        renamed = arcpy.management.Rename(self.parcels, f"{self.gpkg}/lots")[0]
        self.insert(renamed, ["kind"], [(1,)])
        self.assertEqual(self.rows(renamed, ["zone"]), [("z",)])
        arcpy.management.Delete(renamed)
        again = arcpy.management.CreateFeatureclass(self.gpkg, "lots", "POLYGON", spatial_reference=3857)[0]
        self.assertEqual(arcpy.Describe(again).attributeRules, [])


class EvaluatingRules(_Workspace):
    def test_validation_rules_write_the_features_they_fail_to_the_error_tables(self):
        self.insert(self.parcels, ["value", "SHAPE@"], [(10, square(0, 0, 10)), (500, square(20, 0, 10)), (900, None)])
        notes = arcpy.management.CreateTable(self.gpkg, "notes")[0]
        arcpy.management.AddField(notes, "text", "TEXT")
        self.insert(notes, ["text"], [("ok",), ("",)])
        self.rule("cap", "VALIDATION", "$feature.value < 100", error_number=7, error_message="value too high",
                  severity=2, description="caps")
        arcpy.management.AddAttributeRule(notes, "filled", "VALIDATION",
                                          "IIf(IsEmpty($feature.text), {errorMessage: 'empty note'}, true)",
                                          error_number=8, error_message="unused")
        arcpy.management.EvaluateRules(self.gpkg, "VALIDATION_RULES")
        polygons = self.rows(f"{self.gpkg}/GDB_ValidationPolygonErrors",
                             ["RULENAME", "ORIGINOBJECTCLASSNAME", "ORIGINOBJECTID", "ERRORNUMBER", "ERRORMESSAGE",
                              "SEVERITY", "RULETYPE", "DESCRIPTION"])
        self.assertEqual(polygons, [("cap", "parcels", 2, "7", "value too high", 2, "esriARTValidation", "caps")])
        shape = self.rows(f"{self.gpkg}/GDB_ValidationPolygonErrors", ["SHAPE@"])[0][0]
        self.assertAlmostEqual(shape.area, 100.0)
        objects = self.rows(f"{self.gpkg}/GDB_ValidationObjectErrors",
                            ["RULENAME", "ORIGINOBJECTCLASSNAME", "ORIGINOBJECTID", "ERRORMESSAGE"])
        self.assertEqual(sorted(objects), [("cap", "parcels", 3, "value too high"), ("filled", "notes", 2, "empty note")])
        # Evaluated again, a rule's errors are replaced, not added to.
        self.update(self.parcels, ["value"], lambda row: [50], "fid = 2")
        arcpy.management.EvaluateRules(self.gpkg, ["VALIDATION_RULES"])
        self.assertEqual(self.rows(f"{self.gpkg}/GDB_ValidationPolygonErrors", ["OID@"]), [])

    def test_batch_calculation_rules_write_their_results_and_an_extent_limits_them(self):
        self.insert(self.parcels, ["SHAPE@"], [(square(0, 0, 10),), (square(100, 0, 2),)])
        self.rule("area", "CALCULATION", "Area($feature)", field="area", batch="BATCH")
        self.insert(self.parcels, ["SHAPE@"], [(square(0, 0, 1),)])
        self.assertEqual(self.rows(self.parcels, ["area"]), [(None,), (None,), (None,)])  # a batch rule waits
        arcpy.management.EvaluateRules(self.gpkg, "CALCULATION_RULES", "-1 -1 50 50")
        self.assertEqual(self.rows(self.parcels, ["area"]), [(100.0,), (None,), (1.0,)])
        arcpy.management.EvaluateRules(self.gpkg, "CALCULATION_RULES")
        self.assertEqual(self.rows(self.parcels, ["area"]), [(100.0,), (4.0,), (1.0,)])
        with self.assertRaisesRegex(ExecuteError, "CALCULATION_RULES and VALIDATION_RULES"):
            arcpy.management.EvaluateRules(self.gpkg, "TOPOLOGY")


class Sequences(_Workspace):
    def test_sequences_are_created_drawn_listed_and_deleted(self):
        arcpy.management.CreateDatabaseSequence(self.gpkg, "parcel_ids", 100, 5)
        arcpy.management.CreateDatabaseSequence(self.gpkg, "other")
        self.assertEqual([(s.name, s.startValue, s.incrementValue, s.currentValue)
                          for s in arcpy.da.ListDatabaseSequences(self.gpkg)],
                         [("other", 1, 1, 1), ("parcel_ids", 100, 5, 100)])
        self.rule("id", "CALCULATION", "'P' + Text(NextSequenceValue('parcel_ids'), '00000')", field="parcel_id",
                  triggering_events="INSERT")
        self.insert(self.parcels, ["kind"], [(1,), (2,), (3,)])
        self.assertEqual(self.rows(self.parcels, ["parcel_id"]), [("P00100",), ("P00105",), ("P00110",)])
        self.assertEqual({s.name: s.currentValue for s in arcpy.da.ListDatabaseSequences(self.gpkg)}["parcel_ids"], 110)
        with self.assertRaisesRegex(ExecuteError, "already exists"):
            arcpy.management.CreateDatabaseSequence(self.gpkg, "PARCEL_IDS")
        with self.assertRaisesRegex(ExecuteError, "greater than 0"):
            arcpy.management.CreateDatabaseSequence(self.gpkg, "zero", 0)
        arcpy.management.DeleteDatabaseSequence(self.gpkg, "parcel_ids")
        with self.assertRaisesRegex(RuntimeError, "'parcel_ids' does not exist"):
            self.insert(self.parcels, ["kind"], [(4,)])
        with self.assertRaisesRegex(ExecuteError, "does not exist"):
            arcpy.management.DeleteDatabaseSequence(self.gpkg, "parcel_ids")
        self.assertEqual([s.name for s in arcpy.da.ListDatabaseSequences(self.gpkg)], ["other"])


class ContingentValues(_Workspace):
    def setUp(self):
        super().setUp()
        arcpy.management.CreateDomain(self.gpkg, "kinds", "kinds", "SHORT", "CODED")
        for code, name in ((1, "House"), (2, "Shop"), (3, "Farm")):
            arcpy.management.AddCodedValueToDomain(self.gpkg, "kinds", code, name)
        arcpy.management.AssignDomainToField(self.parcels, "kind", "kinds")
        arcpy.management.CreateFieldGroup(self.parcels, "use", ["kind", "value"])

    def test_a_restrictive_group_allows_only_its_contingent_values(self):
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 1], ["value", "RANGE", "0;150"]])
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 2]])  # value: ANY
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 3], ["value", "NULL", ""]])
        self.insert(self.parcels, ["kind", "value"], [(1, 150), (2, 9999), (3, None), (None, None)])
        for refused in ((1, 151), (3, 5), (None, 4)):
            with self.assertRaisesRegex(RuntimeError, "field group 'use' of 'parcels' is restrictive"):
                self.insert(self.parcels, ["kind", "value"], [refused])
        with self.assertRaisesRegex(RuntimeError, "no contingent value allows kind = 1, value = 200"):
            self.update(self.parcels, ["value"], lambda row: [200], "kind = 1")
        arcpy.management.AlterFieldGroup(self.parcels, "use", is_restrictive="DO_NOT_RESTRICT")
        self.insert(self.parcels, ["kind", "value"], [(1, 151)])
        self.assertEqual(len(self.rows(self.parcels, ["OID@"])), 5)

    def test_a_retired_value_is_kept_by_rows_that_hold_it_and_refused_to_new_ones(self):
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 1]])
        self.insert(self.parcels, ["kind", "value"], [(1, 5)])
        arcpy.management.RemoveContingentValue(self.parcels, 1)
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 1]], retire_value="RETIRE")
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 2]])
        self.update(self.parcels, ["zone"], lambda row: ["kept"])  # the group's fields unchanged: allowed
        with self.assertRaisesRegex(RuntimeError, "restrictive"):
            self.insert(self.parcels, ["kind"], [(1,)])
        self.assertEqual([(c.id, c.isRetired) for c in arcpy.da.ListContingentValues(self.parcels)], [(1, True), (2, False)])

    def test_values_are_checked_against_the_domain_and_the_group(self):
        with self.assertRaisesRegex(ExecuteError, "'7' is not a code of the domain of 'kind'"):
            arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 7]])
        with self.assertRaisesRegex(ExecuteError, "'value' has no coded value domain"):
            arcpy.management.AddContingentValue(self.parcels, "use", [["value", "CODED_VALUE", 7]])
        with self.assertRaisesRegex(ExecuteError, "'zone' is not a field of the group"):
            arcpy.management.AddContingentValue(self.parcels, "use", [["zone", "ANY", ""]])
        with self.assertRaisesRegex(ExecuteError, "'min;max'"):
            arcpy.management.AddContingentValue(self.parcels, "use", [["value", "RANGE", "5"]])
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", "2"]])
        with self.assertRaisesRegex(ExecuteError, "already holds this contingent value"):
            arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 2], ["value", "ANY", ""]])
        with self.assertRaisesRegex(ExecuteError, "system-maintained field or the subtype field"):
            arcpy.management.CreateFieldGroup(self.parcels, "bad", ["fid"])
        with self.assertRaisesRegex(ExecuteError, "already has a field group 'USE'"):
            arcpy.management.CreateFieldGroup(self.parcels, "USE", ["zone"])

    def test_altering_a_group_adds_any_and_drops_removed_fields_and_deleting_it_drops_its_values(self):
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 1], ["value", "RANGE", "1;2"]])
        arcpy.management.AlterFieldGroup(self.parcels, "use", "land use", ["kind", "zone"])
        found = arcpy.da.ListContingentValues(self.parcels)
        self.assertEqual([(c.fieldGroupName, [(v.name, v.type, v.code) for v in c.values]) for c in found],
                         [("land use", [("kind", "CODED_VALUE", 1), ("zone", "ANY", None)])])
        arcpy.management.DeleteFieldGroup(self.parcels, "land use")
        self.assertEqual(arcpy.da.ListContingentValues(self.parcels), [])

    def test_contingent_values_by_subtype_and_listed_by_group_and_subtype(self):
        arcpy.management.AddField(self.parcels, "class", "SHORT")
        arcpy.management.SetSubtypeField(self.parcels, "class")
        arcpy.management.AddSubtype(self.parcels, 1, "Urban")
        arcpy.management.AddSubtype(self.parcels, 2, "Rural")
        with self.assertRaisesRegex(ExecuteError, "has subtypes; name the one"):
            arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 1]])
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 2]], "Urban")
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 3]], "Rural")
        self.insert(self.parcels, ["class", "kind"], [(1, 2), (2, 3)])
        with self.assertRaisesRegex(RuntimeError, "restrictive"):
            self.insert(self.parcels, ["class", "kind"], [(1, 3)])
        self.assertEqual([c.id for c in arcpy.da.ListContingentValues(self.parcels, "use", 2)], [2])
        self.assertEqual([c.id for c in arcpy.da.ListContingentValues(self.parcels, "other")], [])
        self.assertEqual([c.subtype for c in arcpy.da.ListContingentValues(self.parcels)], [1, 2])

    def test_export_and_import_round_trip_through_the_documented_files(self):
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 1], ["value", "RANGE", "0;150"]])
        arcpy.management.AddContingentValue(self.parcels, "use", [["kind", "CODED_VALUE", 2], ["value", "NULL", ""]],
                                            retire_value="RETIRE")
        groups, values = os.path.join(self.directory, "fg.csv"), os.path.join(self.directory, "cv.csv")
        arcpy.management.ExportContingentValues(self.parcels, groups, values)
        with open(groups, encoding="utf-8", newline="") as read:
            self.assertEqual(list(csv.reader(read)), [["NAME", "IS_RESTRICTIVE", "FIELD1", "FIELD2"],
                                                      ["use", "TRUE", "kind", "value"]])
        with open(values, encoding="utf-8", newline="") as read:
            self.assertEqual(list(csv.reader(read)), [
                ["CAV_ID", "IS_RETIRED", "FIELD_GROUP", "SUBTYPE", "SUBTYPE_NAME", "CV_TYPE1", "CV_VALUE1",
                 "DESCRIPTION1", "CV_TYPE2", "CV_VALUE2", "DESCRIPTION2"],
                ["1", "False", "use", "", "", "3", "1", "House", "4", "0;150", ""],
                ["2", "True", "use", "", "", "3", "2", "Shop", "2", "", ""]])
        other = arcpy.management.CreateFeatureclass(self.gpkg, "other", "POLYGON", spatial_reference=3857)[0]
        arcpy.management.AddField(other, "kind", "SHORT")
        arcpy.management.AddField(other, "value", "DOUBLE")
        arcpy.management.AssignDomainToField(other, "kind", "kinds")
        arcpy.management.ImportContingentValues(other, groups, values)
        arcpy.management.ImportContingentValues(other, groups, values)  # UNION: duplicates left out
        found = arcpy.da.ListContingentValues(other)
        self.assertEqual([(c.isRetired, [(v.type, v.code, v.range) for v in c.values]) for c in found],
                         [(False, [("CODED_VALUE", 1, None), ("RANGE", None, [0.0, 150.0])]),
                          (True, [("CODED_VALUE", 2, None), ("NULL", None, None)])])
        arcpy.management.ImportContingentValues(other, groups, values, "REPLACE")
        self.assertEqual(len(arcpy.da.ListContingentValues(other)), 2)

    def test_generate_writes_the_combinations_of_the_data_or_of_the_schema(self):
        self.insert(self.parcels, ["kind", "value"], [(1, 5), (1, 7), (2, None)])
        groups, values = os.path.join(self.directory, "fg.csv"), os.path.join(self.directory, "cv.csv")
        arcpy.management.GenerateContingentValues(self.parcels, groups, values)
        with open(values, encoding="utf-8", newline="") as read:
            rows = list(csv.reader(read))[1:]
        # DATA: kind's codes as found, value (no domain) ANY when it holds a value and NULL when it is null.
        self.assertEqual([r[5:] for r in rows], [["3", "1", "House", "1", "", ""], ["3", "2", "Shop", "2", "", ""]])
        arcpy.management.GenerateContingentValues(self.parcels, groups, values, "SCHEMA", ["use"])
        with open(values, encoding="utf-8", newline="") as read:
            rows = list(csv.reader(read))[1:]
        # SCHEMA: every code of kind and its NULL (the field allows nulls), each with value's ANY and NULL: 4 × 2.
        self.assertEqual(len(rows), 8)
        self.assertEqual([r[0] for r in rows], [str(i) for i in range(1, 9)])


class Refusals(unittest.TestCase):
    def test_replicas_and_extraction_are_refused_with_their_reasons(self):
        for name in ("CreateReplica", "SynchronizeChanges", "ExportDataChangeMessage", "ImportMessage",
                     "CompareReplicaSchema"):
            with self.assertRaisesRegex(NotBuiltError, "traditionally versioned"):
                getattr(arcpy.management, name)()
        with self.assertRaisesRegex(NotBuiltError, "replica XML, whose schema is not published"):
            arcpy.management.ExportAcknowledgementMessage()
        with self.assertRaisesRegex(NotBuiltError, "fenced tool cannot reach"):
            arcpy.management.CreateReplicaFromServer()
        with self.assertRaisesRegex(NotBuiltError, "expand_feature_classes_and_tables without defining them"):
            arcpy.management.ExtractDataFromGeodatabase()
        with self.assertRaisesRegex(NotBuiltError, "no geodatabase replicas"):
            arcpy.da.ListReplicas()


if __name__ == "__main__":
    unittest.main()
