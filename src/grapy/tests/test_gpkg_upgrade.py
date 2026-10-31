"""D-340: a GeoPackage an earlier grapy wrote is found, offered an upgrade, and upgraded by UpgradeGDB.

The old file is written here as grapy wrote one before 2026-10-31 — the SQL of `_gpkg.create` and `_ensure_metadata`
as they stood then (git history of src/grapy/grapy/_gpkg.py): core tables without Annex C's constraints, a feature
class whose geometry column is declared BLOB, a table written as a GEOMETRY feature table with an empty geom column, WKT
2 in `definition` and `user_version` 0. ArcGIS Pro 3.7 opened that class as a table with a `geom:Blob` field; the
declared type alone repaired it, bisected against Pro on 2026-10-31 (D-340). The tests check what Pro needed.
"""
import os
import sqlite3
import sys
import tempfile
import unittest

import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _gpkg, _gpkgupgrade, _runtime  # noqa: E402


def old_file(path):
    from pyproj import CRS
    connection = sqlite3.connect(path)
    connection.execute("pragma application_id = 1196444487")
    connection.execute("""create table if not exists gpkg_spatial_ref_sys (srs_name text not null, srs_id integer primary key,
        organization text not null, organization_coordsys_id integer not null, definition text not null, description text)""")
    connection.execute("""create table if not exists gpkg_contents (table_name text primary key, data_type text not null,
        identifier text unique, description text default '', last_change datetime default (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
        min_x double, min_y double, max_x double, max_y double, srs_id integer)""")
    connection.execute("""create table if not exists gpkg_geometry_columns (table_name text not null, column_name text not null,
        geometry_type_name text not null, srs_id integer not null, z tinyint not null, m tinyint not null,
        primary key (table_name, column_name))""")
    for srs in (-1, 0, 3857):
        connection.execute("insert or ignore into gpkg_spatial_ref_sys values (?, ?, ?, ?, ?, '')",
                           (f"EPSG:{srs}" if srs > 0 else "undefined", srs, "EPSG" if srs > 0 else "NONE", srs,
                            CRS.from_epsg(srs).to_wkt() if srs > 0 else "undefined"))
    connection.execute('create table "wells" (fid integer primary key autoincrement, geom BLOB, "name" TEXT, '
                       '"depth" REAL)')
    connection.execute("insert into gpkg_contents (table_name, data_type, identifier, srs_id) values "
                       "('wells', 'features', 'wells', 3857)")
    connection.execute("insert into gpkg_geometry_columns values ('wells', 'geom', 'POINT', 3857, 0, 0)")
    for i, (x, y) in enumerate(((1.0, 2.0), (3.0, 4.0)), start=1):
        connection.execute('insert into "wells" (geom, "name", "depth") values (?, ?, ?)',
                           (_gpkg.encode(shapely.Point(x, y), 3857), f"w{i}", 10.0 * i))
    connection.execute('create table "visits" (fid integer primary key autoincrement, geom BLOB, "well" INTEGER)')
    connection.execute("insert into gpkg_contents (table_name, data_type, identifier, srs_id) values "
                       "('visits', 'features', 'visits', 0)")
    connection.execute("insert into gpkg_geometry_columns values ('visits', 'geom', 'GEOMETRY', 0, 0, 0)")
    connection.executemany('insert into "visits" ("well") values (?)', [(1,), (1,), (2,)])
    connection.commit()
    connection.close()
    return path


class Upgrade(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.mark = len(_runtime.MESSAGES)   # other tests' messages are left alone: some read them later
        _gpkgupgrade._SAID.clear()
        self.file = old_file(os.path.join(self.directory, "old.gpkg"))

    def said(self, warnings=False):
        kinds = ("esriJobMessageTypeWarning", "esriJobMessageTypeError")
        return "\n".join(m["description"] for m in _runtime.MESSAGES[self.mark:] if not warnings or m["type"] in kinds)

    def declared(self, table):
        connection = sqlite3.connect(self.file)
        try:
            return {r[1]: r[2] for r in connection.execute(f'pragma table_info("{table}")')}
        finally:
            connection.close()

    def test_the_old_form_is_found(self):
        connection = sqlite3.connect(self.file)
        try:
            kinds = sorted({(k, t) for k, t, _ in _gpkgupgrade.findings(connection)}, key=str)
        finally:
            connection.close()
        self.assertEqual(kinds, sorted({("attributes", "visits"), ("core table", "gpkg_contents"),
                                        ("core table", "gpkg_geometry_columns"), ("definition", "EPSG:3857"),
                                        ("geometry type", "wells"), ("version", None)}, key=str))

    def test_writing_into_an_old_file_offers_the_upgrade_once(self):
        arcpy.management.CreateFeatureclass(self.file, "new_one", "POINT", spatial_reference=3857)
        arcpy.management.CreateFeatureclass(self.file, "new_two", "POINT", spatial_reference=3857)
        said = [m for m in self.said(True).splitlines() if "UpgradeGDB" in m]
        self.assertEqual(len(said), 1)
        self.assertIn("before 2026-10-31", said[0])

    def test_upgrade_gdb_rebuilds_the_old_tables_and_keeps_every_row(self):
        arcpy.management.UpgradeGDB(self.file)
        self.assertTrue(os.path.exists(self.file + ".before-upgrade"))
        # The class's geometry column is declared POINT, which is what ArcGIS Pro needed (D-340's bisection).
        self.assertEqual(self.declared("wells")["geom"].upper(), "POINT")
        with arcpy.da.SearchCursor(self.file + "/wells", ["name", "depth", "SHAPE@XY"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("w1", 10.0, (1.0, 2.0)), ("w2", 20.0, (3.0, 4.0))])
        # The table is an attributes table, with no geometry column (GeoPackage 1.4 §2.4).
        self.assertNotIn("geom", self.declared("visits"))
        connection = sqlite3.connect(self.file)
        try:
            self.assertEqual(connection.execute("select data_type, srs_id from gpkg_contents where table_name = 'visits'"
                                                ).fetchone(), ("attributes", None))
            self.assertIsNone(connection.execute("select 1 from gpkg_geometry_columns where table_name = 'visits'"
                                                 ).fetchone())
            self.assertEqual(connection.execute('select "well" from "visits" order by fid').fetchall(), [(1,), (1,), (2,)])
            contents = connection.execute("select sql from sqlite_master where name = 'gpkg_contents'").fetchone()[0]
            self.assertIn("fk_gc_r_srs_id", contents)
            self.assertEqual(connection.execute("pragma user_version").fetchone()[0], _gpkg.USER_VERSION)
            definition = connection.execute("select definition from gpkg_spatial_ref_sys where srs_id = 3857").fetchone()[0]
            self.assertTrue(definition.startswith("PROJCS"))
            self.assertEqual(connection.execute("pragma integrity_check").fetchone()[0], "ok")
            self.assertEqual(connection.execute("pragma foreign_key_check").fetchall(), [])
            found = _gpkgupgrade.findings(connection)
        finally:
            connection.close()
        self.assertEqual(found, [])
        self.assertEqual(arcpy.Describe(self.file + "/wells").shapeType, "Point")
        # A second run finds nothing to do and keeps no second copy.
        arcpy.management.UpgradeGDB(self.file)
        self.assertIn("nothing to upgrade", self.said())
        self.assertFalse(os.path.exists(self.file + ".before-upgrade-2"))

    def test_the_prerequisite_check_alone_changes_nothing(self):
        arcpy.management.UpgradeGDB(self.file, "PREREQUISITE_CHECK", "NO_UPGRADE")
        self.assertEqual(self.declared("wells")["geom"].upper(), "BLOB")
        self.assertIn("Old form (geometry type) in wells", self.said())
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.UpgradeGDB(self.file, "NO_PREREQUISITE_CHECK", "NO_UPGRADE")

    def test_a_file_grapy_writes_now_is_already_current(self):
        current = arcpy.management.CreateFeatureclass(self.directory, "fresh.gpkg", "POINT", spatial_reference=3857)[0]
        file = _gpkg.split(current)[0]
        arcpy.management.UpgradeGDB(file)
        self.assertIn("nothing to upgrade", self.said())
        self.assertFalse(os.path.exists(file + ".before-upgrade"))


if __name__ == "__main__":
    unittest.main()
