"""ADR-289 §5.1: grapy writes a GeoPackage's object ID as ``OBJECTID`` and its geometry as ``Shape``, as ArcGIS Pro 3.7
does (measured 2026-11-08 and 2026-11-12: tools/pro-compare/sweep, arcpy_top, da and management_1/2, and a probe of
Describe, ListIndexes and da.SearchCursor("*") on a GeoPackage Pro wrote and on one written by hand with fid/geom).

It still reads, edits and copies a GeoPackage whose columns are ``fid`` and ``geom`` — what GDAL writes, and what
grapy wrote before 2026-11-12 — because every reader finds a table's object ID (its integer primary key) and its
geometry column (its gpkg_geometry_columns row) from the table, never from a constant.
"""
import os
import shutil
import sqlite3
import sys
import tempfile
import unittest

import shapely

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _gpkg, _runtime  # noqa: E402

SRID = 32635


def gdal_style(path, rtree=True):
    """A GeoPackage written by hand as GDAL writes one (and grapy did before 2026-11-12): Annex C's core tables, a
    point class ``wells`` keyed ``fid`` with its shape in ``geom`` and, with ``rtree``, its RTree index; an attributes
    table ``visits`` keyed ``fid``."""
    from pyproj import CRS
    connection = sqlite3.connect(path)
    connection.execute("pragma application_id = 1196444487")
    connection.execute("pragma user_version = 10400")
    connection.execute("""create table gpkg_spatial_ref_sys (srs_name TEXT NOT NULL, srs_id INTEGER PRIMARY KEY,
        organization TEXT NOT NULL, organization_coordsys_id INTEGER NOT NULL, definition TEXT NOT NULL,
        description TEXT)""")
    connection.execute("""create table gpkg_contents (table_name TEXT NOT NULL PRIMARY KEY, data_type TEXT NOT NULL,
        identifier TEXT UNIQUE, description TEXT DEFAULT '',
        last_change DATETIME NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
        min_x DOUBLE, min_y DOUBLE, max_x DOUBLE, max_y DOUBLE, srs_id INTEGER)""")
    connection.execute("""create table gpkg_geometry_columns (table_name TEXT NOT NULL, column_name TEXT NOT NULL,
        geometry_type_name TEXT NOT NULL, srs_id INTEGER NOT NULL, z TINYINT NOT NULL, m TINYINT NOT NULL,
        CONSTRAINT pk_geom_cols PRIMARY KEY (table_name, column_name))""")
    connection.executemany("insert into gpkg_spatial_ref_sys values (?, ?, ?, ?, ?, ?)", [
        ("Undefined Cartesian SRS", -1, "NONE", -1, "undefined", None),
        ("Undefined geographic SRS", 0, "NONE", 0, "undefined", None),
        ("WGS 84 / UTM zone 35N", SRID, "EPSG", SRID, CRS.from_epsg(SRID).to_wkt("WKT1_GDAL"), None)])
    connection.execute('create table "wells" (fid INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, geom POINT, '
                       '"name" TEXT(20), "depth" DOUBLE)')
    connection.execute("insert into gpkg_contents (table_name, data_type, identifier, srs_id) values "
                       "('wells', 'features', 'wells', ?)", (SRID,))
    connection.execute("insert into gpkg_geometry_columns values ('wells', 'geom', 'POINT', ?, 0, 0)", (SRID,))
    for i, (x, y) in enumerate(((500000.0, 4500000.0), (500100.0, 4500050.0), (500200.0, 4500300.0)), start=1):
        connection.execute('insert into "wells" (geom, "name", "depth") values (?, ?, ?)',
                           (_gpkg.encode(shapely.Point(x, y), SRID), f"w{i}", 10.0 * i))
    connection.execute('create table "visits" (fid INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL, "well" INTEGER, '
                       '"note" TEXT)')
    connection.execute("insert into gpkg_contents (table_name, data_type, identifier) values "
                       "('visits', 'attributes', 'visits')")
    connection.executemany('insert into "visits" ("well", "note") values (?, ?)', [(1, "a"), (1, "b"), (2, "c")])
    if rtree:
        _gpkg.functions(connection)   # the triggers' functions, as GDAL registers its own
        _gpkg.add_index(connection, "wells")
    connection.commit()
    connection.close()
    return path


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, _runtime.env, "workspace", _runtime.env.workspace)
        self.addCleanup(setattr, _runtime.env, "overwriteOutput", _runtime.env.overwriteOutput)
        _runtime.env.overwriteOutput = True

    def columns(self, path):
        file, table = _gpkg.split(path)
        with sqlite3.connect(file) as connection:
            return [(r[1], r[2].upper(), r[5]) for r in connection.execute(f'pragma table_info("{table}")')]


class WrittenAsProWritesThem(_Workspace):
    """What Pro 3.7 answers on a GeoPackage it writes, and grapy now writes the same."""

    def setUp(self):
        super().setUp()
        self.gpkg = arcpy.management.CreateSQLiteDatabase(os.path.join(self.directory, "gp.gpkg"), "GEOPACKAGE")[0]
        self.points = arcpy.management.CreateFeatureclass(self.gpkg, "points", "POINT",
                                                          spatial_reference=arcpy.SpatialReference(SRID))[0]
        for name, kind in (("pid", "LONG"), ("val", "DOUBLE"), ("cat", "TEXT")):
            arcpy.management.AddField(self.points, name, kind)
        with arcpy.da.InsertCursor(self.points, ["pid", "val", "cat", "SHAPE@XY"]) as rows:
            for i in range(3):
                rows.insertRow((i, i * 1.5, "abc"[i], (500000.0 + i, 4500000.0 + i)))

    def test_the_columns_are_objectid_and_shape(self):
        self.assertEqual(self.columns(self.points)[:2], [("OBJECTID", "INTEGER", 1), ("Shape", "POINT", 0)])
        with sqlite3.connect(os.fspath(self.gpkg)) as connection:
            self.assertEqual(connection.execute("select column_name from gpkg_geometry_columns where table_name = "
                                                "'points'").fetchone(), ("Shape",))
            # the RTree index and its triggers are named after the column, as Pro lists rtree_points_Shape
            self.assertTrue(connection.execute("select 1 from sqlite_master where name = 'rtree_points_Shape'").fetchone())

    def test_describe_answers_pros_names_in_the_tables_order(self):
        d = arcpy.Describe(self.points)
        self.assertEqual((d.OIDFieldName, d.shapeFieldName), ("OBJECTID", "Shape"))
        self.assertEqual([f.name for f in d.fields], ["OBJECTID", "Shape", "pid", "val", "cat"])
        self.assertEqual([f.name for f in arcpy.ListFields(self.points)], ["OBJECTID", "Shape", "pid", "val", "cat"])

    def test_a_cursor_on_star_names_objectid_and_shape(self):
        with arcpy.da.SearchCursor(self.points, "*") as rows:
            self.assertEqual(rows.fields, ("OBJECTID", "Shape", "pid", "val", "cat"))
            first = next(rows)
        self.assertEqual(first[0], 1)
        self.assertEqual(first[1], (500000.0, 4500000.0))   # "*" reads the shape as SHAPE@XY, as documented

    def test_list_indexes_lists_the_object_id_and_the_spatial_index(self):
        arcpy.management.AddIndex(self.points, ["val"], "val_idx")
        self.assertEqual(sorted(i.name for i in arcpy.ListIndexes(self.points)),
                         ["FDO_OBJECTID", "rtree_points_Shape", "val_idx"])
        self.assertEqual([i.name for i in arcpy.ListIndexes(self.points, "val*")], ["val_idx"])
        oid = next(i for i in arcpy.ListIndexes(self.points) if i.name == "FDO_OBJECTID")
        self.assertEqual(([f.name for f in oid.fields], oid.isUnique), (["OBJECTID"], True))

    def test_a_search_left_before_its_last_row_releases_the_file(self):
        # Found while writing these tests: a cursor left after one row of three kept the file locked, so the next
        # tool writing to it failed with "database is locked".
        with arcpy.da.SearchCursor(self.points, ["OID@"]) as rows:
            next(rows)
        arcpy.management.CreateFeatureclass(self.gpkg, "more", "POINT", spatial_reference=arcpy.SpatialReference(SRID))
        self.assertTrue(arcpy.Exists(os.path.join(os.fspath(self.gpkg), "more")))

    def test_a_table_is_keyed_objectid(self):
        table = arcpy.management.CreateTable(self.gpkg, "rows")[0]
        self.assertEqual(self.columns(table), [("OBJECTID", "INTEGER", 1)])
        self.assertEqual(arcpy.Describe(table).OIDFieldName, "OBJECTID")

    def test_names_derived_from_the_object_id_follow_it(self):
        lookup = arcpy.management.CreateTable(self.gpkg, "lookup")[0]
        arcpy.management.AddField(lookup, "code", "TEXT")
        with arcpy.da.InsertCursor(lookup, ["code"]) as rows:
            rows.insertRow(("a",))
        layer = arcpy.management.MakeFeatureLayer(self.points, "points_layer")[0]
        arcpy.management.AddJoin(layer, "cat", lookup, "code")
        names = [f.name for f in arcpy.ListFields(layer)]
        self.assertIn("points.OBJECTID", names)
        self.assertIn("lookup.OBJECTID", names)
        joined = os.path.join(os.fspath(self.gpkg), "joined")
        arcpy.management.CopyFeatures(layer, joined)
        self.assertIn("lookup_OBJECTID", [f.name for f in arcpy.ListFields(joined)])


class ReadsWhatGdalWrites(_Workspace):
    """A GeoPackage keyed ``fid`` with its shape in ``geom`` is read, edited and copied as it was before ADR-289."""

    def setUp(self):
        super().setUp()
        self.file = gdal_style(os.path.join(self.directory, "old.gpkg"))
        self.wells = self.file + "/wells"
        self.visits = self.file + "/visits"

    def test_describe_answers_the_tables_own_names(self):
        d = arcpy.Describe(self.wells)
        self.assertEqual((d.OIDFieldName, d.shapeFieldName, d.shapeType), ("fid", "geom", "Point"))
        self.assertEqual([f.name for f in d.fields], ["fid", "geom", "name", "depth"])
        self.assertEqual(arcpy.Describe(self.visits).OIDFieldName, "fid")
        self.assertTrue(d.hasSpatialIndex)

    def test_list_indexes_names_follow_the_old_columns(self):
        self.assertEqual(sorted(i.name for i in arcpy.ListIndexes(self.wells)), ["FDO_fid", "rtree_wells_geom"])

    def test_cursors_read_and_edit_it(self):
        with arcpy.da.SearchCursor(self.wells, "*") as rows:
            self.assertEqual(rows.fields, ("fid", "geom", "name", "depth"))
            self.assertEqual([r[0] for r in rows], [1, 2, 3])
        with arcpy.da.UpdateCursor(self.wells, ["OID@", "depth", "SHAPE@XY"], "name = 'w2'") as rows:
            for oid, depth, xy in rows:
                rows.updateRow((oid, depth + 1, (xy[0] + 5, xy[1])))
        with arcpy.da.InsertCursor(self.wells, ["name", "SHAPE@XY"]) as rows:
            new = rows.insertRow(("w4", (500300.0, 4500400.0)))
        self.assertEqual(new, 4)
        with arcpy.da.SearchCursor(self.wells, ["OID@", "name", "depth", "SHAPE@XY"]) as rows:
            self.assertEqual(list(rows), [(1, "w1", 10.0, (500000.0, 4500000.0)), (2, "w2", 21.0, (500105.0, 4500050.0)),
                                          (3, "w3", 30.0, (500200.0, 4500300.0)), (4, "w4", None, (500300.0, 4500400.0))])
        with arcpy.da.UpdateCursor(self.wells, ["OID@"], "name = 'w1'") as rows:
            for _ in rows:
                rows.deleteRow()
        self.assertEqual(int(arcpy.management.GetCount(self.wells)[0]), 3)
        # the old RTree index was kept by its own triggers, keyed by fid: the moved, the new and the deleted well
        with sqlite3.connect(self.file) as connection:
            self.assertEqual(connection.execute('select id, minx from "rtree_wells_geom" order by id').fetchall(),
                             [(2, 500105.0), (3, 500200.0), (4, 500300.0)])

    def test_fields_are_added_and_calculated(self):
        arcpy.management.AddField(self.wells, "deep", "SHORT")
        arcpy.management.CalculateField(self.wells, "deep", "1 if !depth! > 15 else 0", "PYTHON3")
        with arcpy.da.SearchCursor(self.wells, ["name", "deep"]) as rows:
            self.assertEqual(list(rows), [("w1", 0), ("w2", 1), ("w3", 1)])

    def test_copies_are_written_with_pros_names_and_keep_the_rows(self):
        copied = os.path.join(self.directory, "new.gpkg", "wells")
        arcpy.management.CopyFeatures(self.wells, copied)
        self.assertEqual(self.columns(copied)[:2], [("OBJECTID", "INTEGER", 1), ("Shape", "POINT", 0)])
        with arcpy.da.SearchCursor(copied, ["OID@", "name", "SHAPE@XY"]) as rows:
            self.assertEqual(list(rows), [(1, "w1", (500000.0, 4500000.0)), (2, "w2", (500100.0, 4500050.0)),
                                          (3, "w3", (500200.0, 4500300.0))])
        rows_copy = os.path.join(self.directory, "new.gpkg", "visits")
        arcpy.management.CopyRows(self.visits, rows_copy)
        with arcpy.da.SearchCursor(rows_copy, ["OID@", "well", "note"]) as rows:
            self.assertEqual(list(rows), [(1, 1, "a"), (2, 1, "b"), (3, 2, "c")])
        # management.Copy of one class keeps the source's object IDs in the new names
        whole = os.path.join(self.directory, "copy.gpkg", "wells")
        arcpy.management.Copy(self.wells, whole)
        self.assertEqual(self.columns(whole)[:2], [("OBJECTID", "INTEGER", 1), ("Shape", "POINT", 0)])
        self.assertEqual(int(arcpy.management.GetCount(whole)[0]), 3)

    def test_a_tool_reads_it_and_carries_its_object_ids(self):
        out = os.path.join(self.directory, "out.gpkg", "rings")
        arcpy.analysis.Buffer(self.wells, out, "10 Meters")
        with arcpy.da.SearchCursor(out, ["ORIG_FID", "SHAPE@AREA"]) as rows:
            found = list(rows)
        self.assertEqual([r[0] for r in found], [1, 2, 3])
        self.assertTrue(all(abs(area - 314.0) < 2.0 for _, area in found))

    def test_fields_named_objectid_and_shape_are_kept_beside_the_new_key_and_shape(self):
        # A fid/geom table may hold plain fields called OBJECTID and Shape (data that left ArcGIS as a shapefile, then
        # passed through GDAL). A copy keeps them as fields and names its own key OBJECTID_1, as ArcGIS does when the
        # data copied hold an OBJECTID field, and its shape Shape_1 by the same rule (INFERRED).
        with sqlite3.connect(self.file) as connection:
            _gpkg.functions(connection)
            connection.execute('alter table wells add column "OBJECTID" INTEGER')
            connection.execute('alter table wells add column "Shape" TEXT')
            connection.execute("update wells set OBJECTID = fid * 100, Shape = 'round'")
        copied = os.path.join(self.directory, "new.gpkg", "wells")
        arcpy.management.CopyFeatures(self.wells, copied)
        d = arcpy.Describe(copied)
        self.assertEqual((d.OIDFieldName, d.shapeFieldName), ("OBJECTID_1", "Shape_1"))
        with arcpy.da.SearchCursor(copied, ["OID@", "OBJECTID", "Shape", "SHAPE@XY"]) as rows:
            self.assertEqual(next(rows), (1, 100, "round", (500000.0, 4500000.0)))
        table = os.path.join(self.directory, "new.gpkg", "rows")
        arcpy.management.CopyRows(self.wells, table)
        self.assertEqual([f.name for f in arcpy.ListFields(table)], ["OBJECTID_1", "name", "depth", "OBJECTID", "Shape"])

    def test_a_join_reads_the_old_key(self):
        layer = arcpy.management.MakeFeatureLayer(self.wells, "wells_join")[0]
        arcpy.management.AddJoin(layer, "fid", self.visits, "well")
        names = [f.name for f in arcpy.ListFields(layer)]
        self.assertIn("wells.fid", names)
        self.assertIn("visits.fid", names)


if __name__ == "__main__":
    unittest.main()
