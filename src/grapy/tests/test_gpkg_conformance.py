"""grapy's GeoPackages against the OGC GeoPackage 1.4 standard (OGC 12-128r19), the points ArcGIS Pro 3.7 depends on
or the standard requires of every file. Found 2026-10-31: Pro read every feature class grapy wrote as a table with a
blob field — "table has no geometry", ERROR 000732 — because the geometry column was declared BLOB, which
Requirement 31 forbids. Bisected against Pro: that one declaration was the whole cause; the other points below are the
standard's and were wrong too, but Pro read files with them wrong. tools/pro-compare/gpkg-pro-reads.py is the check
against Pro itself, which CI cannot run.
"""
import os
import shutil
import sqlite3
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _gpkg  # noqa: E402


def declared_type(connection, table, column):
    return next(r[2] for r in connection.execute(f'pragma table_info("{table}")') if r[1] == column)


class Conformance(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.file = os.path.join(self.directory, "made.gpkg")
        arcpy.env.overwriteOutput = True
        sr = arcpy.SpatialReference(3857)
        for name, kind in (("pts", "POINT"), ("lines", "POLYLINE"), ("polys", "POLYGON")):
            arcpy.management.CreateFeatureclass(self.file, name, kind, spatial_reference=sr)
        arcpy.management.CreateTable(self.file, "attrs")
        with arcpy.da.InsertCursor(f"{self.file}/pts", ["SHAPE@XY"]) as rows:
            rows.insertRow(((10.0, 20.0),))
            rows.insertRow(((30.0, -5.0),))
        with arcpy.da.InsertCursor(f"{self.file}/polys", ["SHAPE@WKT"]) as rows:
            rows.insertRow(("POLYGON ((0 0, 4 0, 4 3, 0 3, 0 0))",))
        self.connection = sqlite3.connect(self.file)

    def tearDown(self):
        self.connection.close()
        shutil.rmtree(self.directory, ignore_errors=True)

    def test_the_header_says_geopackage_1_4(self):
        # §1.1.1.1.1, Requirement 2: application_id 0x47504B47 ("GPKG") and user_version 10400 for 1.4.0.
        self.assertEqual(self.connection.execute("pragma application_id").fetchone()[0], 0x47504B47)
        self.assertEqual(self.connection.execute("pragma user_version").fetchone()[0], 10400)

    def test_a_geometry_column_is_declared_with_its_geometry_type_name(self):
        # §2.1.6.1.1, Requirement 31 — the cause of Pro's "table has no geometry".
        rows = self.connection.execute("select table_name, column_name, geometry_type_name from gpkg_geometry_columns")
        found = {t: (declared_type(self.connection, t, c), g) for t, c, g in rows}
        self.assertEqual(found, {"pts": ("POINT", "POINT"), "lines": ("MULTILINESTRING", "MULTILINESTRING"),
                                 "polys": ("MULTIPOLYGON", "MULTIPOLYGON")})

    def test_a_table_is_an_attributes_table_without_a_geometry_column(self):
        # §2.4.2: an attributes table has no geometry column and no gpkg_geometry_columns row. Written as a GEOMETRY
        # feature table, Pro opens it as a feature class of unknown type and cannot read it.
        self.assertEqual(self.connection.execute(
            "select data_type, srs_id from gpkg_contents where table_name = 'attrs'").fetchone(), ("attributes", None))
        self.assertIsNone(self.connection.execute(
            "select 1 from gpkg_geometry_columns where table_name = 'attrs'").fetchone())
        self.assertEqual([r[1] for r in self.connection.execute('pragma table_info("attrs")')], ["fid"])
        arcpy.management.AddField(f"{self.file}/attrs", "NAME", "TEXT")
        with arcpy.da.InsertCursor(f"{self.file}/attrs", ["NAME"]) as rows:
            rows.insertRow(("a",))
        self.assertEqual(list(arcpy.da.SearchCursor(f"{self.file}/attrs", ["OID@", "NAME"])), [(1, "a")])

    def test_a_table_is_listed_as_a_table(self):
        arcpy.env.workspace = self.file
        self.assertIn("attrs", arcpy.ListTables())
        self.assertNotIn("attrs", arcpy.ListFeatureClasses())

    def test_the_three_required_references_are_there(self):
        # §1.1.2.1.2, Requirement 11 and Table 3: -1 and 0 "undefined", and 4326 — even in a file that uses neither.
        rows = {r[0]: r[1:] for r in self.connection.execute(
            "select srs_id, organization, organization_coordsys_id, definition from gpkg_spatial_ref_sys")}
        self.assertEqual(rows[-1], ("NONE", -1, "undefined"))
        self.assertEqual(rows[0], ("NONE", 0, "undefined"))
        self.assertEqual(rows[4326][:2], ("EPSG", 4326))
        self.assertIn(3857, rows)

    def test_a_definition_is_wkt1(self):
        # §1.1.2.1.1: definition is OGC 01-009 WKT ([I32]); WKT 2 belongs only in the optional gpkg_crs_wkt
        # extension's definition_12_063 (Annex F.10).
        rows = dict(self.connection.execute("select srs_id, definition from gpkg_spatial_ref_sys where srs_id > 0"))
        self.assertTrue(rows[4326].startswith('GEOGCS["WGS 84"'), rows[4326][:40])
        self.assertTrue(rows[3857].startswith("PROJCS["), rows[3857][:40])
        self.assertIn('AUTHORITY["EPSG","3857"]', rows[3857])

    def test_the_core_tables_are_the_annex_c_definitions(self):
        # Requirements 10, 13 and 21 with Annex C.1–C.3: the foreign keys among them, and nothing violates them.
        keys = {(r[2], r[3]) for r in self.connection.execute("pragma foreign_key_list(gpkg_geometry_columns)")}
        self.assertEqual(keys, {("gpkg_contents", "table_name"), ("gpkg_spatial_ref_sys", "srs_id")})
        self.assertEqual([(r[2], r[3]) for r in self.connection.execute("pragma foreign_key_list(gpkg_contents)")],
                         [("gpkg_spatial_ref_sys", "srs_id")])
        last_change = next(r for r in self.connection.execute("pragma table_info(gpkg_contents)") if r[1] == "last_change")
        self.assertEqual((last_change[2], last_change[3]), ("DATETIME", 1))
        self.assertEqual(self.connection.execute("pragma foreign_key_check").fetchall(), [])
        self.assertEqual(self.connection.execute("pragma integrity_check").fetchone()[0], "ok")

    def test_the_contents_extent_covers_the_shapes_written(self):
        # §1.1.3.1.1: an informative bounding box; grapy's cursors widen it as they write, an empty class keeps none.
        rows = {r[0]: r[1:] for r in self.connection.execute(
            "select table_name, min_x, min_y, max_x, max_y, srs_id from gpkg_contents")}
        self.assertEqual(rows["pts"], (10.0, -5.0, 30.0, 20.0, 3857))
        self.assertEqual(rows["polys"], (0.0, 0.0, 4.0, 3.0, 3857))
        self.assertEqual(rows["lines"], (None, None, None, None, 3857))
        self.connection.close()
        with arcpy.da.UpdateCursor(f"{self.file}/pts", ["SHAPE@XY"]) as rows_:
            for _ in rows_:
                rows_.updateRow(((50.0, 1.0),))
        self.connection = sqlite3.connect(self.file)
        self.assertEqual(self.connection.execute(
            "select min_x, min_y, max_x, max_y from gpkg_contents where table_name = 'pts'").fetchone(),
            (10.0, -5.0, 50.0, 20.0))

    def test_the_primary_key_is_an_integer_rowid_alias(self):
        # §2.1.6.1.1, Requirement 29.
        key = [r for r in self.connection.execute('pragma table_info("pts")') if r[5]]
        self.assertEqual([(r[1], r[2]) for r in key], [("fid", "INTEGER")])

    def test_a_copy_keeps_a_table_a_table_and_a_class_its_extent(self):
        arcpy.management.AddField(f"{self.file}/attrs", "NAME", "TEXT")
        with arcpy.da.InsertCursor(f"{self.file}/attrs", ["NAME"]) as rows:
            rows.insertRow(("a",))
        other = os.path.join(self.directory, "other.gpkg")
        arcpy.management.Copy(f"{self.file}/attrs", f"{other}/attrs")
        arcpy.management.Copy(f"{self.file}/pts", f"{other}/pts")
        self.assertEqual(list(arcpy.da.SearchCursor(f"{other}/attrs", ["OID@", "NAME"])), [(1, "a")])
        c = sqlite3.connect(other)
        try:
            self.assertEqual(c.execute("select data_type from gpkg_contents where table_name = 'attrs'").fetchone(),
                             ("attributes",))
            self.assertEqual(c.execute("select min_x, min_y, max_x, max_y from gpkg_contents where table_name = 'pts'")
                             .fetchone(), (10.0, -5.0, 30.0, 20.0))
            self.assertEqual(c.execute("select count(*) from pts").fetchone()[0], 2)
        finally:
            c.close()

    def test_a_rename_keeps_the_foreign_keys_whole(self):
        arcpy.management.Rename(f"{self.file}/polys", f"{self.file}/areas")
        self.connection.close()
        self.connection = sqlite3.connect(self.file)
        self.assertEqual(self.connection.execute(
            "select table_name from gpkg_geometry_columns where table_name in ('polys', 'areas')").fetchall(), [("areas",)])
        self.assertEqual(self.connection.execute("pragma foreign_key_check").fetchall(), [])


class OtherWriters(unittest.TestCase):
    """Files grapy did not write the same way: its own before 2026-10-31, and one with the RTree triggers GDAL and Pro
    add (Annex F.3), which call ST_* functions an SQLite connection does not have unless its writer registers them."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()

    def tearDown(self):
        shutil.rmtree(self.directory, ignore_errors=True)

    def old_grapy_file(self):
        """A file as grapy wrote it before 2026-10-31: geometry declared BLOB, WKT 2, no version, no 4326 row."""
        from pyproj import CRS
        file = os.path.join(self.directory, "old.gpkg")
        c = sqlite3.connect(file)
        c.executescript(f"""
            pragma application_id = 1196444487;
            create table gpkg_spatial_ref_sys (srs_name text not null, srs_id integer primary key, organization text not null,
                organization_coordsys_id integer not null, definition text not null, description text);
            create table gpkg_contents (table_name text primary key, data_type text not null, identifier text unique,
                description text default '', last_change datetime default (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                min_x double, min_y double, max_x double, max_y double, srs_id integer);
            create table gpkg_geometry_columns (table_name text not null, column_name text not null,
                geometry_type_name text not null, srs_id integer not null, z tinyint not null, m tinyint not null,
                primary key (table_name, column_name));
            create table "pts" (fid integer primary key autoincrement, geom BLOB, "NAME" TEXT);
            insert into gpkg_contents (table_name, data_type, identifier, srs_id) values ('pts', 'features', 'pts', 3857);
            insert into gpkg_geometry_columns values ('pts', 'geom', 'POINT', 3857, 0, 0);
            insert into gpkg_spatial_ref_sys values ('undefined', -1, 'NONE', -1, 'undefined', '');
            insert into gpkg_spatial_ref_sys values ('undefined', 0, 'NONE', 0, 'undefined', '');
        """)
        c.execute("insert into gpkg_spatial_ref_sys values ('EPSG:3857', 3857, 'EPSG', 3857, ?, '')",
                  (CRS.from_epsg(3857).to_wkt(),))
        c.execute("insert into pts (geom, NAME) values (?, 'a')", (_gpkg.encode(_gpkg.shapely.Point(1, 2), 3857),))
        c.commit()
        c.close()
        return file

    def test_an_older_grapy_file_is_read_and_written(self):
        file = self.old_grapy_file()
        self.assertEqual(list(arcpy.da.SearchCursor(f"{file}/pts", ["SHAPE@XY", "NAME"])), [((1.0, 2.0), "a")])
        self.assertEqual(arcpy.Describe(f"{file}/pts").spatialReference.factoryCode, 3857)
        with arcpy.da.InsertCursor(f"{file}/pts", ["SHAPE@XY", "NAME"]) as rows:
            rows.insertRow(((5.0, 6.0), "b"))
        arcpy.management.CreateFeatureclass(file, "more", "POLYGON", spatial_reference=arcpy.SpatialReference(3857))
        c = sqlite3.connect(file)
        try:
            self.assertEqual(c.execute("select count(*) from pts").fetchone()[0], 2)
            # Its WKT 2 definition is rewritten as WKT 1 when grapy next writes to it, and it gains 4326 and a version.
            self.assertTrue(c.execute("select definition from gpkg_spatial_ref_sys where srs_id = 3857").fetchone()[0]
                            .startswith("PROJCS["))
            self.assertIsNotNone(c.execute("select 1 from gpkg_spatial_ref_sys where srs_id = 4326").fetchone())
            self.assertEqual(c.execute("pragma user_version").fetchone()[0], 10400)
            self.assertEqual(c.execute("select max_x from gpkg_contents where table_name = 'pts'").fetchone()[0], 5.0)
        finally:
            c.close()

    def test_a_file_with_rtree_triggers_is_written(self):
        # Annex F.3: the index's triggers call ST_IsEmpty and ST_MinX … ST_MaxY; grapy registers them, so an insert and
        # an update keep the index as the file's writer would.
        file = os.path.join(self.directory, "indexed.gpkg")
        arcpy.management.CreateFeatureclass(file, "pts", "POINT", spatial_reference=arcpy.SpatialReference(3857))
        c = sqlite3.connect(file)
        c.executescript("""
            create virtual table rtree_pts_geom using rtree(id, minx, maxx, miny, maxy);
            create trigger rtree_pts_geom_insert after insert on pts
              when (new.geom not null and not ST_IsEmpty(NEW.geom))
            begin
              insert or replace into rtree_pts_geom values
                (NEW.fid, ST_MinX(NEW.geom), ST_MaxX(NEW.geom), ST_MinY(NEW.geom), ST_MaxY(NEW.geom));
            end;
        """)
        c.commit()
        c.close()
        with arcpy.da.InsertCursor(f"{file}/pts", ["SHAPE@XY"]) as rows:
            rows.insertRow(((7.0, 8.0),))
        c = sqlite3.connect(file)
        try:
            self.assertEqual(c.execute("select * from rtree_pts_geom").fetchall(), [(1, 7.0, 7.0, 8.0, 8.0)])
        finally:
            c.close()

    def test_a_file_with_the_crs_wkt_extension_takes_a_new_reference(self):
        # Annex F.10: definition_12_063 is NOT NULL; a reference grapy adds there carries WKT 2 in it.
        file = os.path.join(self.directory, "extended.gpkg")
        arcpy.management.CreateFeatureclass(file, "pts", "POINT", spatial_reference=arcpy.SpatialReference(4326))
        c = sqlite3.connect(file)
        c.execute("alter table gpkg_spatial_ref_sys add column definition_12_063 TEXT NOT NULL DEFAULT 'undefined'")
        c.commit()
        c.close()
        arcpy.management.CreateFeatureclass(file, "merc", "POINT", spatial_reference=arcpy.SpatialReference(3857))
        c = sqlite3.connect(file)
        try:
            wkt1, wkt2 = c.execute("select definition, definition_12_063 from gpkg_spatial_ref_sys "
                                   "where srs_id = 3857").fetchone()
        finally:
            c.close()
        self.assertTrue(wkt1.startswith("PROJCS["))
        self.assertTrue(wkt2.startswith("PROJCRS["))


if __name__ == "__main__":
    unittest.main()
