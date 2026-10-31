"""Editor tracking, last edit time, COGO, incrementing IDs, unregistered tables, registration, views and statistics on a
GeoPackage (ADR-240), and the enterprise-geodatabase, binning, split-model, trajectory and annotation names it refuses
one by one — against hand-worked answers."""
import datetime
import os
import sqlite3
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _attachments, _behaviours  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

T1 = datetime.datetime(2026, 10, 31, 9, 0, 0)
T2 = datetime.datetime(2026, 10, 31, 10, 30, 0)


def _line(*xy):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in xy]))


class _Base(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.mkdtemp()
        self.gpkg = os.path.join(self.folder, "work.gpkg")
        arcpy.management.CreateFeatureclass(self.gpkg, "roads", "POLYLINE", spatial_reference=3857)
        self.roads = self.gpkg + "/roads"
        arcpy.management.AddField(self.roads, "name", "TEXT")

    def sql(self, statement, *args):
        connection = sqlite3.connect(self.gpkg)
        try:
            rows = connection.execute(statement, args).fetchall()
            connection.commit()
            return rows
        finally:
            connection.close()

    def insert(self, path, fields, rows):
        with arcpy.da.InsertCursor(path, fields) as cursor:
            for row in rows:
                cursor.insertRow(row)

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as cursor:
            return [tuple(r) for r in cursor]


class EditorTracking(_Base):
    def edit_as(self, who, when):
        return mock.patch.multiple(_behaviours, user=mock.Mock(return_value=who), moment=mock.Mock(return_value=when))

    def test_an_insert_stamps_all_four_fields_and_an_update_the_last_two(self):
        self.insert(self.roads, ["name"], [("before",)])
        arcpy.management.EnableEditorTracking(self.roads, "made_by", "made_on", "changed_by", "changed_on", "ADD_FIELDS")
        with self.edit_as("alice", T1):
            self.insert(self.roads, ["name", "SHAPE@"], [("main", _line((0, 0), (1, 0)))])
        with self.edit_as("bob", T2):
            with arcpy.da.UpdateCursor(self.roads, ["name"], "name = 'main'") as rows:
                for _ in rows:
                    rows.updateRow(["high"])
        fields = ["name", "made_by", "made_on", "changed_by", "changed_on"]
        self.assertEqual(self.rows(self.roads, fields), [
            ("before", None, None, None, None),  # a row there before stays null, as the help topic says
            ("high", "alice", "2026-10-31 09:00:00", "bob", "2026-10-31 10:30:00")])

    def test_a_value_the_edit_gives_a_tracked_field_is_replaced(self):
        arcpy.management.EnableEditorTracking(self.roads, "made_by", add_fields="ADD_FIELDS")
        with self.edit_as("alice", T1):
            self.insert(self.roads, ["name", "made_by"], [("x", "mallory")])
        self.assertEqual(self.rows(self.roads, ["made_by"]), [("alice",)])

    def test_no_field_named_with_add_fields_takes_the_default_names(self):
        arcpy.management.EnableEditorTracking(self.roads, add_fields="ADD_FIELDS")
        described = arcpy.Describe(self.roads)
        self.assertTrue(described.editorTrackingEnabled)
        self.assertEqual((described.creatorFieldName, described.createdAtFieldName, described.editorFieldName,
                          described.editedAtFieldName), _behaviours.DEFAULT_TRACKING)
        self.assertTrue(described.isTimeInUTC)
        self.assertEqual({f.name: f.type for f in arcpy.ListFields(self.roads)}["created_date"], "Date")

    def test_the_documented_errors(self):
        with self.assertRaisesRegex(ExecuteError, "does not exist"):
            arcpy.management.EnableEditorTracking(self.roads, "made_by")  # NO_ADD_FIELDS and no such field
        with self.assertRaisesRegex(ExecuteError, "must be a text field"):
            arcpy.management.AddField(self.roads, "count", "LONG")
            arcpy.management.EnableEditorTracking(self.roads, "count")
        with self.assertRaisesRegex(ExecuteError, "must be a date field"):
            arcpy.management.EnableEditorTracking(self.roads, creation_date_field="name")
        arcpy.management.EnableEditorTracking(self.roads, "made_by", add_fields="ADD_FIELDS")
        with self.assertRaisesRegex(ExecuteError, "disable editor tracking to use another field"):
            arcpy.management.EnableEditorTracking(self.roads, "someone", add_fields="ADD_FIELDS")
        with self.assertRaisesRegex(ExecuteError, "disable editor tracking to change it"):
            arcpy.management.EnableEditorTracking(self.roads, "made_by", record_dates_in="DATABASE_TIME")
        arcpy.management.EnableEditorTracking(self.roads, last_editor_field="changed_by", add_fields="ADD_FIELDS")
        self.assertEqual(arcpy.Describe(self.roads).editorFieldName, "changed_by")  # run again: one more field
        self.assertEqual(arcpy.Describe(self.roads).creatorFieldName, "made_by")

    def test_disabling_stops_the_fields_asked_and_keeps_them(self):
        arcpy.management.EnableEditorTracking(self.roads, "made_by", last_editor_field="changed_by",
                                              add_fields="ADD_FIELDS")
        arcpy.management.DisableEditorTracking(self.roads, "NO_DISABLE_CREATOR")
        with self.edit_as("alice", T1):
            self.insert(self.roads, ["name"], [("x",)])
        self.assertEqual(self.rows(self.roads, ["made_by", "changed_by"]), [("alice", None)])
        arcpy.management.DisableEditorTracking(self.roads)
        self.assertFalse(arcpy.Describe(self.roads).editorTrackingEnabled)
        self.assertIn("made_by", [f.name for f in arcpy.ListFields(self.roads)])

    def test_database_time_is_local_and_utc_is_not(self):
        utc = _behaviours.moment(True)
        local = _behaviours.moment(False)
        offset = datetime.datetime.now().astimezone().utcoffset()
        self.assertLess(abs((local - utc) - offset), datetime.timedelta(seconds=2))
        self.assertEqual(utc.microsecond, 0)  # a low precision date: to the second

    def test_a_versioned_class_must_record_in_utc(self):
        arcpy.management.RegisterAsVersioned(self.roads)
        with self.assertRaisesRegex(ExecuteError, "must be UTC"):
            arcpy.management.EnableEditorTracking(self.roads, "made_by", add_fields="ADD_FIELDS",
                                                  record_dates_in="DATABASE_TIME")
        arcpy.management.EnableEditorTracking(self.roads, "made_by", add_fields="ADD_FIELDS")
        with self.edit_as("carol", T1):
            self.insert(self.roads, ["name"], [("v",)])
        self.assertEqual(self.rows(self.roads, ["made_by"]), [("carol",)])

    def test_a_feature_dataset_tracks_every_class_in_it(self):
        arcpy.management.CreateFeatureDataset(self.gpkg, "net", 3857)
        arcpy.management.CreateFeatureclass(self.gpkg + "/net", "pipes", "POLYLINE")
        arcpy.management.CreateFeatureclass(self.gpkg + "/net", "valves", "POINT")
        arcpy.management.EnableEditorTracking(self.gpkg + "/net", add_fields="ADD_FIELDS")
        for name in ("pipes", "valves"):
            self.assertTrue(arcpy.Describe(self.gpkg + "/net/" + name).editorTrackingEnabled)

    def test_attachments_are_tracked_with_their_class(self):
        arcpy.management.EnableAttachments(self.roads)
        self.insert(self.roads, ["name"], [("x",)])
        arcpy.management.EnableEditorTracking(self.roads, "made_by", add_fields="ADD_FIELDS")
        connection = sqlite3.connect(self.gpkg)
        try:
            key = _attachments.global_id_field(connection, "roads")
            guid = connection.execute(f'select "{key}" from roads').fetchone()[0]
            with self.edit_as("dave", T1):
                _attachments.add(connection, "roads", guid, "a.txt", b"hello")
            connection.commit()
        finally:
            connection.close()
        self.assertEqual(self.sql('select made_by from "roads__ATTACH"'), [("dave",)])

    def test_tracking_runs_after_attribute_rules_and_on_their_edits(self):
        arcpy.management.AddField(self.roads, "label", "TEXT")
        arcpy.management.AddAttributeRule(self.roads, "label", "CALCULATION", "Upper($feature.name)", field="label",
                                          triggering_events="INSERT;UPDATE")
        log = arcpy.management.CreateTable(self.gpkg, "log")[0]
        arcpy.management.AddField(log, "what", "TEXT")
        arcpy.management.EnableEditorTracking(log, "made_by", add_fields="ADD_FIELDS")
        arcpy.management.AddAttributeRule(self.roads, "log", "CALCULATION",
                                          "return {edit: [{className: 'log', adds: [{attributes: {what: $feature.name}}]}]}",
                                          triggering_events="INSERT", exclude_from_client_evaluation="EXCLUDE")
        arcpy.management.EnableEditorTracking(self.roads, "made_by", add_fields="ADD_FIELDS")
        with self.edit_as("erin", T1):
            self.insert(self.roads, ["name"], [("main",)])
        self.assertEqual(self.rows(self.roads, ["label", "made_by"]), [("MAIN", "erin")])
        self.assertEqual(self.rows(log, ["what", "made_by"]), [("main", "erin")])

    def test_rename_and_delete_carry_it(self):
        arcpy.management.EnableEditorTracking(self.roads, "made_by", add_fields="ADD_FIELDS")
        arcpy.management.Rename(self.roads, self.gpkg + "/streets")
        self.assertEqual(arcpy.Describe(self.gpkg + "/streets").creatorFieldName, "made_by")
        arcpy.management.Delete(self.gpkg + "/streets")
        self.assertEqual(self.sql(f"select count(*) from {_behaviours.TRACKING}"), [(0,)])


class LastEditTime(_Base):
    OLD = "2000-01-01T00:00:00.000Z"

    def changed(self, table="roads"):
        return self.sql("select last_change from gpkg_contents where table_name = ?", table)[0][0]

    def test_every_writer_moves_last_change(self):
        arcpy.management.EnableLastEditTime(self.roads)
        for statement in ("insert into roads (name) values ('a')", "update roads set name = 'b'", "delete from roads"):
            self.sql("update gpkg_contents set last_change = ? where table_name = 'roads'", self.OLD)
            self.sql(statement)  # not grapy: any writer of the file
            self.assertGreater(self.changed(), "2026-")
        arcpy.management.DisableLastEditTime(self.roads)
        self.sql("update gpkg_contents set last_change = ? where table_name = 'roads'", self.OLD)
        self.insert(self.roads, ["name"], [("c",)])
        self.assertEqual(self.changed(), self.OLD)

    def test_a_versioned_class_is_refused_as_documented(self):
        arcpy.management.EnableArchiving(self.roads)
        with self.assertRaisesRegex(ExecuteError, "nonversioned"):
            arcpy.management.EnableLastEditTime(self.roads)

    def test_a_feature_dataset_passes_it_to_classes_created_later(self):
        arcpy.management.CreateFeatureDataset(self.gpkg, "net", 3857)
        arcpy.management.CreateFeatureclass(self.gpkg + "/net", "pipes", "POLYLINE")
        arcpy.management.EnableLastEditTime(self.gpkg + "/net")
        arcpy.management.CreateFeatureclass(self.gpkg + "/net", "valves", "POINT")
        for table in ("net/pipes", "net/valves"):
            self.sql("update gpkg_contents set last_change = ? where table_name = ?", self.OLD, table)
            self.sql(f'insert into "{table}" (geom) values (null)')
            self.assertGreater(self.changed(table), "2026-")

    def test_a_rename_keeps_it(self):
        arcpy.management.EnableLastEditTime(self.roads)
        arcpy.management.Rename(self.roads, self.gpkg + "/streets")
        self.sql("update gpkg_contents set last_change = ? where table_name = 'streets'", self.OLD)
        self.sql("insert into streets (name) values ('a')")
        self.assertGreater(self.changed("streets"), "2026-")


class Cogo(_Base):
    def test_the_five_double_fields_are_added_and_kept(self):
        arcpy.management.AddField(self.roads, "Distance", "DOUBLE")
        arcpy.management.EnableCOGO(self.roads)
        types = {f.name: f.type for f in arcpy.ListFields(self.roads)}
        for name in ("Direction", "Distance", "ArcLength", "Radius", "Radius2"):
            self.assertEqual(types[name], "Double")
        arcpy.management.DisableCOGO(self.roads)
        self.assertIn("ArcLength", [f.name for f in arcpy.ListFields(self.roads)])
        self.assertEqual(self.sql(f"select count(*) from {_behaviours.COGO}"), [(0,)])

    def test_only_a_line_class_and_only_double_fields(self):
        arcpy.management.CreateFeatureclass(self.gpkg, "wells", "POINT")
        with self.assertRaisesRegex(ExecuteError, "line classes"):
            arcpy.management.EnableCOGO(self.gpkg + "/wells")
        arcpy.management.AddField(self.roads, "Radius", "TEXT")
        with self.assertRaisesRegex(ExecuteError, "must be a double field"):
            arcpy.management.EnableCOGO(self.roads)


class UnregisteredAndRegistration(_Base):
    def test_an_unregistered_table_is_a_plain_table_with_an_objectid(self):
        out = arcpy.management.CreateUnRegisteredTable(self.gpkg, "plain")[0]
        self.assertEqual(out, self.gpkg.replace("\\", "/") + "/plain")
        self.assertEqual(self.sql("select count(*) from gpkg_contents where table_name = 'plain'"), [(0,)])
        self.assertEqual([(r[1], r[2]) for r in self.sql("pragma table_info(plain)")], [("OBJECTID", "INTEGER")])
        arcpy.env.workspace = self.gpkg
        self.addCleanup(setattr, arcpy.env, "workspace", None)
        self.assertNotIn("plain", arcpy.ListTables())

    def test_a_template_gives_its_fields(self):
        arcpy.management.CreateUnRegisteredTable(self.gpkg, "copy", self.roads)
        self.assertEqual([r[1] for r in self.sql("pragma table_info(copy)")], ["fid", "name"])

    def test_an_incrementing_id_is_kept_by_the_database(self):
        arcpy.management.CreateUnRegisteredTable(self.gpkg, "plain")
        self.sql("insert into plain (OBJECTID) values (7)")
        self.sql("insert into plain (OBJECTID) values (8)")
        arcpy.management.AddIncrementingIDField(self.gpkg + "/plain", "rid")
        self.sql("insert into plain (OBJECTID) values (9)")  # not grapy
        self.sql("insert into plain (OBJECTID, rid) values (10, 20)")  # an insert that gives one keeps it
        self.sql("insert into plain (OBJECTID) values (11)")
        self.assertEqual(self.sql("select OBJECTID, rid from plain order by rowid"),
                         [(7, 1), (8, 2), (9, 3), (10, 20), (11, 21)])
        with self.assertRaises(sqlite3.IntegrityError):
            self.sql("insert into plain (OBJECTID, rid) values (12, 1)")

    def test_incrementing_id_errors_as_documented(self):
        with self.assertRaisesRegex(ExecuteError, "registered with the geodatabase"):
            arcpy.management.AddIncrementingIDField(self.roads)
        arcpy.management.CreateUnRegisteredTable(self.gpkg, "plain")
        with self.assertRaisesRegex(ExecuteError, "already exists"):
            arcpy.management.AddIncrementingIDField(self.gpkg + "/plain")  # ObjectID: OBJECTID is there
        arcpy.management.AddIncrementingIDField(self.gpkg + "/plain", "rid")
        arcpy.management.AddIncrementingIDField(self.gpkg + "/plain", "rid2")
        self.assertIn("already has an incrementing ID field", arcpy.GetMessages(1))
        self.assertNotIn("rid2", [r[1] for r in self.sql("pragma table_info(plain)")])

    def test_an_unregistered_class_is_written_then_registered(self):
        arcpy.management.CreateUnRegisteredFeatureclass(self.gpkg, "parcels", "POLYGON", spatial_reference=3857)
        self.assertEqual(self.sql("select count(*) from gpkg_geometry_columns where table_name = 'parcels'"), [(0,)])
        square = arcpy.Polygon(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(2, 0), arcpy.Point(2, 2), arcpy.Point(0, 2)]))
        self.insert(self.gpkg + "/parcels", ["SHAPE@"], [(square,), (square,)])
        arcpy.management.RegisterWithGeodatabase(self.gpkg + "/parcels")
        self.assertEqual(self.sql("select data_type, srs_id, min_x, min_y, max_x, max_y from gpkg_contents where "
                                  "table_name = 'parcels'"), [("features", 3857, 0.0, 0.0, 2.0, 2.0)])
        self.assertEqual(self.sql("select column_name, geometry_type_name, srs_id from gpkg_geometry_columns where "
                                  "table_name = 'parcels'"), [("geom", "MULTIPOLYGON", 3857)])
        self.assertEqual(arcpy.Describe(self.gpkg + "/parcels").spatialReference.factoryCode, 3857)
        self.assertEqual(self.sql("select distinct substr(geom, 5, 4) from parcels"), [((3857).to_bytes(4, "little"),)])
        described = arcpy.Describe(self.gpkg + "/parcels")
        self.assertEqual((described.OIDFieldName, described.shapeType), ("OBJECTID", "Polygon"))
        self.assertEqual(self.rows(self.gpkg + "/parcels", ["OID@"]), [(1,), (2,)])
        self.insert(self.gpkg + "/parcels", ["SHAPE@"], [(square,)])
        self.assertEqual(self.rows(self.gpkg + "/parcels", ["OID@"]), [(1,), (2,), (3,)])

    def test_an_empty_class_registers_with_the_reference_it_was_made_with(self):
        arcpy.management.CreateUnRegisteredFeatureclass(self.gpkg, "wells", "POINT", has_z="ENABLED",
                                                        spatial_reference=3857)
        arcpy.management.RegisterWithGeodatabase(self.gpkg + "/wells")
        self.assertEqual(self.sql("select geometry_type_name, srs_id, z, m from gpkg_geometry_columns where "
                                  "table_name = 'wells'"), [("POINT", 3857, 1, 0)])
        self.assertTrue(arcpy.Describe(self.gpkg + "/wells").hasZ)

    def test_registration_errors_as_documented(self):
        arcpy.management.CreateUnRegisteredTable(self.gpkg, "plain")
        self.sql("alter table plain add column shape BLOB")
        with self.assertRaisesRegex(ExecuteError, "in_geometry_type is required"):
            arcpy.management.RegisterWithGeodatabase(self.gpkg + "/plain", in_shape_field="shape")
        with self.assertRaisesRegex(ExecuteError, "in_spatial_reference is required"):
            arcpy.management.RegisterWithGeodatabase(self.gpkg + "/plain", in_shape_field="shape",
                                                     in_geometry_type="POINT")
        self.sql("insert into plain (OBJECTID) values (1)")
        self.sql("insert into plain (OBJECTID) values (1)")
        with self.assertRaisesRegex(ExecuteError, "must be unique"):
            arcpy.management.RegisterWithGeodatabase(self.gpkg + "/plain", "OBJECTID")
        with self.assertRaisesRegex(ExecuteError, "already registered"):
            arcpy.management.RegisterWithGeodatabase(self.roads)
        arcpy.management.CreateDatabaseView(self.gpkg, "v", "select name from roads")
        with self.assertRaises(NotBuiltError):
            arcpy.management.RegisterWithGeodatabase(self.gpkg + "/v")

    def test_a_shape_of_another_type_is_refused(self):
        arcpy.management.CreateUnRegisteredFeatureclass(self.gpkg, "mixed", "POLYLINE", spatial_reference=3857)
        self.insert(self.gpkg + "/mixed", ["SHAPE@"], [(arcpy.PointGeometry(arcpy.Point(1, 1)),)])
        with self.assertRaisesRegex(ExecuteError, "is not a MULTILINESTRING"):
            arcpy.management.RegisterWithGeodatabase(self.gpkg + "/mixed")

    def test_a_plain_table_registers_as_a_table_with_a_new_objectid(self):
        connection = sqlite3.connect(self.gpkg)
        connection.execute("create table people (who TEXT)")
        connection.executemany("insert into people values (?)", [("a",), ("b",)])
        connection.commit()
        connection.close()
        arcpy.management.RegisterWithGeodatabase(self.gpkg + "/people")
        self.assertEqual(self.sql("select data_type from gpkg_contents where table_name = 'people'"), [("attributes",)])
        self.assertEqual(self.rows(self.gpkg + "/people", ["OID@", "who"]), [(1, "a"), (2, "b")])
        self.assertEqual(self.rows(self.gpkg + "/people", "*"), [(1, "a"), (2, "b")])  # no shape to read

    def test_config_keyword_is_refused(self):
        with self.assertRaises(NotBuiltError):
            arcpy.management.CreateUnRegisteredTable(self.gpkg, "plain", config_keyword="DEFAULTS")


class ViewsAndStatistics(_Base):
    def test_a_view_is_made_and_read(self):
        self.insert(self.roads, ["name"], [("a",), ("b",)])
        out = arcpy.management.CreateDatabaseView(self.gpkg, "named", "select name from roads where name <> 'a'")[0]
        self.assertEqual(self.rows(out, ["name"]), [("b",)])
        self.assertEqual(self.sql("select count(*) from gpkg_contents where table_name = 'named'"), [(0,)])
        keyed = arcpy.management.CreateDatabaseView(self.gpkg, "keyed", "select fid, name from roads")[0]
        self.assertEqual(self.rows(keyed, "*"), [(1, "a"), (2, "b")])  # OID@ is its first integer field
        self.assertEqual(arcpy.Describe(keyed).OIDFieldName, "fid")
        self.assertIn("no integer field", arcpy.GetMessages(1))  # 'named' had none

    def test_a_bad_or_second_statement_is_an_error(self):
        with self.assertRaisesRegex(ExecuteError, "not valid SQL"):
            arcpy.management.CreateDatabaseView(self.gpkg, "bad", "select nothing from nowhere")
        with self.assertRaisesRegex(ExecuteError, "not valid SQL"):
            arcpy.management.CreateDatabaseView(self.gpkg, "two", "select 1; drop table roads")
        self.assertEqual(self.sql("select count(*) from sqlite_master where name in ('bad', 'two')"), [(0,)])
        self.assertEqual(self.sql("select count(*) from sqlite_master where name = 'roads'"), [(1,)])

    def test_analyze_writes_sqlite_statistics(self):
        self.insert(self.roads, ["name"], [("a",), ("b",), ("c",)])
        arcpy.management.Analyze(self.roads, ["BUSINESS"])
        self.assertEqual(self.sql("select stat from sqlite_stat1 where tbl = 'roads'"), [("3",)])
        with self.assertRaisesRegex(ExecuteError, "components is required"):
            arcpy.management.Analyze(self.roads, "")

    def test_adds_and_deletes_are_the_history_table(self):
        arcpy.management.EnableArchiving(self.roads)
        self.insert(self.roads, ["name"], [("a",), ("b",)])
        arcpy.management.Analyze(self.roads, "ADDS;DELETES")
        self.assertEqual(self.sql("select tbl from sqlite_stat1 where tbl like 'roads%' order by tbl"),
                         [("roads__history",)])

    def test_analyze_datasets_and_rebuild_indexes(self):
        self.insert(self.roads, ["name"], [("a",)])
        arcpy.management.AnalyzeDatasets(self.gpkg, "NO_SYSTEM", "roads")
        self.assertEqual(self.sql("select stat from sqlite_stat1 where tbl = 'roads'"), [("1",)])
        arcpy.management.AnalyzeDatasets(self.gpkg, "SYSTEM")
        self.assertTrue(self.sql("select 1 from sqlite_stat1 where tbl = 'gpkg_contents'"))
        with self.assertRaisesRegex(ExecuteError, "at least one dataset"):
            arcpy.management.RebuildIndexes(self.gpkg, "NO_SYSTEM")
        arcpy.management.RebuildIndexes(self.gpkg, "NO_SYSTEM", "roads", "ALL")
        self.assertIn("indexes rebuilt for roads", arcpy.GetMessages())
        with self.assertRaisesRegex(ExecuteError, "does not exist"):
            arcpy.management.AnalyzeDatasets(self.gpkg, "NO_SYSTEM", "nowhere")


class Refused(unittest.TestCase):
    NAMES = ("AppendAnnotation", "CreateEnterpriseGeodatabase", "EnableEnterpriseGeodatabase",
             "UpdateEnterpriseGeodatabaseLicense", "UpgradeGDB", "CreateDatabaseUser", "CreateRole", "ChangePrivileges",
             "CreateSpatialType", "ConfigureGeodatabaseLogFileTables", "ExportGeodatabaseConfigurationKeywords",
             "ImportGeodatabaseConfigurationKeywords", "DeleteSchemaGeodatabase", "MigrateStorage",
             "UpdatePortalDatasetOwner", "EnableFeatureBinning", "DisableFeatureBinning", "ManageFeatureBinCache",
             "SetFeatureClassSplitModel", "CreateTrajectoryDataset", "AddDataToTrajectoryDataset",
             "MakeTrajectoryLayer", "RepairTrajectoryDatasetPaths")
    BUILT = ("EnableEditorTracking", "DisableEditorTracking", "EnableLastEditTime", "DisableLastEditTime", "EnableCOGO",
             "DisableCOGO", "AddIncrementingIDField", "CreateUnRegisteredTable", "CreateUnRegisteredFeatureclass",
             "RegisterWithGeodatabase", "CreateDatabaseView", "Analyze", "AnalyzeDatasets", "RebuildIndexes")

    def test_each_name_is_refused_with_a_reason_of_its_own_and_no_schedule(self):
        reasons = []
        for name in self.NAMES:
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.management, name)()
            text = str(said.exception)
            self.assertTrue(text.startswith(f"arcpy.management.{name} "), text)
            for schedule in ("yet", "until", "later", "not chosen", "group 2"):
                self.assertNotIn(schedule, text, name)
            reasons.append(text.split(" ", 1)[1])
        self.assertEqual(len(set(reasons)), len(reasons))

    def test_the_built_names_are_built(self):
        for name in self.BUILT:
            self.assertNotIn(name, arcpy.management._REFUSED)
            self.assertTrue(callable(getattr(arcpy.management, name)))

    def test_annotation_is_refused_for_the_same_reason_in_both_places(self):
        with self.assertRaisesRegex(NotBuiltError, "Element BLOB field"):
            arcpy.Annotation()
        with self.assertRaisesRegex(NotBuiltError, "Element BLOB field"):
            arcpy.management.AppendAnnotation()


if __name__ == "__main__":
    unittest.main()
