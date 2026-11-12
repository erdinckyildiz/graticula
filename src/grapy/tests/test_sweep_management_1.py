"""arcpy.management, AddAttachments through EnableAttributeRules, aligned to what ArcGIS Pro 3.7 answers: every answer
here was measured in ArcGIS Pro 3.7 on 2026-11-08 (tools/pro-compare/sweep, cases/management_1.py) and is worked out
by hand below."""
import math
import os
import sqlite3
import sys
import shutil
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class SweepManagement1(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, arcpy.env, "workspace", arcpy.env.workspace)
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "w")[0]

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def out(self, name):
        return os.path.join(self.directory, name)

    def make(self, name, shape, fields, rows, srid=3857):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, shape, spatial_reference=srid)[0]
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def table(self, name, fields, rows=()):
        path = arcpy.management.CreateTable(self.gpkg, name)[0]
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def raster(self, values, name="r.tif"):
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        made = arcpy.NumPyArrayToRaster(np.array(values, dtype="float32"), arcpy.Point(0, 0), 10, 10)
        arcpy.env.outputCoordinateSystem = None
        return made.save(self.out(name))

    # ---- geometry attributes --------------------------------------------------------------------------------------

    def test_line_bearing_is_the_planar_start_to_end_bearing(self):
        # (0, 0) → (10, 10) is 45°, (0, 0) → (10, 0) 90°, (0, 0) → (0, -10) 180°, (0, 0) → (-10, 0) 270°.
        lines = self.make("l", "POLYLINE", [("k", "LONG")],
                          [(1, "LINESTRING(0 0,5 1,10 10)"), (2, "LINESTRING(0 0,10 0)"), (3, "LINESTRING(0 0,0 -10)"),
                           (4, "LINESTRING(0 0,-10 0)")])
        arcpy.management.AddGeometryAttributes(lines, "LINE_BEARING")
        self.assertEqual([round(r[0], 9) for r in self.rows(lines, ["BEARING"])], [45.0, 90.0, 180.0, 270.0])
        arcpy.management.CalculateGeometryAttributes(lines, [["b2", "LINE_BEARING"]])
        self.assertEqual([round(r[0], 9) for r in self.rows(lines, ["b2"])], [45.0, 90.0, 180.0, 270.0])
        with self.assertRaises(arcpy.NotBuiltError):   # an unknown property is refused by name, not a KeyError
            arcpy.management.CalculateGeometryAttributes(lines, [["x", "NO_SUCH_PROPERTY"]])

    # ---- rasters ---------------------------------------------------------------------------------------------------

    def test_raster_standard_deviation_is_the_sample_form(self):
        # 1, 2, 3, 4: mean 2.5, squares of the differences 5, so 5 / 3 under n - 1: √(5/3) = 1.2909944…
        path = self.raster([[1, 2], [3, 4]])
        self.assertAlmostEqual(arcpy.Raster(path).standardDeviation, math.sqrt(5 / 3))
        self.assertAlmostEqual(float(arcpy.management.GetRasterProperties(path, "STD")[0]), math.sqrt(5 / 3))
        arcpy.management.CalculateStatistics(path)
        import rasterio
        with rasterio.open(path) as read:
            self.assertAlmostEqual(float(read.tags(1)["STATISTICS_STDDEV"]), math.sqrt(5 / 3))

    def test_copy_raster_rounds_half_up_and_clamps(self):
        path = self.raster([[1.5, 2.5, -1.5, 2.7], [-2.7, 0.5, -0.5, 3.49]], "f.tif")
        s16 = arcpy.management.CopyRaster(path, self.out("s16.tif"), pixel_type="16_BIT_SIGNED")[0]
        self.assertEqual(arcpy.RasterToNumPyArray(s16).tolist(), [[2, 3, -1, 3], [-3, 1, 0, 3]])
        wide = self.raster([[300, -5, 255.5, 254.5], [0.4, 70000, -0.6, 12.5]], "w.tif")
        u8 = arcpy.management.CopyRaster(wide, self.out("u8.tif"), pixel_type="8_BIT_UNSIGNED")[0]
        # 300, 255.5, 254.5 and 70000 are 255, a valid value: the raster is written without a NoData value.
        self.assertEqual(arcpy.RasterToNumPyArray(u8).tolist(), [[255, 0, 255, 255], [0, 255, 0, 13]])
        self.assertIsNone(arcpy.Raster(u8).noDataValue)

    def test_create_random_raster_writes_32_bit_floats(self):
        made = arcpy.management.CreateRandomRaster(self.directory, "rr.tif", "UNIFORM 0.0 1.0", "0 0 100 50", 10)[0]
        self.assertEqual(arcpy.Raster(made).pixelType, "F32")

    # ---- domains, tables, fields ---------------------------------------------------------------------------------

    def test_a_range_domain_given_no_range_answers_zero_to_zero(self):
        arcpy.management.CreateDomain(self.gpkg, "r", "a range", "LONG", "RANGE")
        self.assertEqual(tuple(arcpy.da.ListDomains(self.gpkg)[0].range), (0, 0))

    def test_create_table_numbers_a_repeated_template_field(self):
        a = self.table("a", [("rid", "LONG"), ("val", "DOUBLE"), ("cat", "TEXT")])
        b = self.table("b", [("gid", "LONG"), ("cat", "TEXT"), ("val", "DOUBLE")])
        made = arcpy.management.CreateTable(self.gpkg, "two", [a, b])[0]
        self.assertEqual([f.name for f in arcpy.ListFields(made)][1:], ["rid", "val", "cat", "gid", "cat_1", "val_1"])

    def test_add_field_keeps_a_text_length_and_an_alias(self):
        t = self.table("t", [("a", "LONG")])
        arcpy.management.AddField(t, "f", "TEXT", field_length=20, field_alias="Text alias")
        arcpy.management.AddFields(t, [["g", "TEXT", "Alias G", 30, "x", ""], ["h", "LONG", "", None, 5, ""]])
        fields = {f.name: f for f in arcpy.ListFields(t)}
        self.assertEqual((fields["f"].length, fields["f"].aliasName), (20, "Text alias"))
        self.assertEqual((fields["g"].length, fields["g"].aliasName, fields["g"].defaultValue), (30, "Alias G", "x"))
        self.assertEqual(fields["h"].defaultValue, 5)
        # A text field the calculation adds is 512 long.
        arcpy.management.CalculateField(t, "lab", "'x'", "PYTHON3", None, "TEXT")
        self.assertEqual({f.name: f.length for f in arcpy.ListFields(t)}["lab"], 512)

    def test_alter_field_changes_an_empty_tables_type_and_keeps_an_old_name_as_alias(self):
        empty = self.table("e", [("n", "SHORT"), ("s", "TEXT"), ("z", "DOUBLE")])
        arcpy.management.AlterField(empty, "n", field_type="LONG")
        arcpy.management.AlterField(empty, "s", field_length=40)
        fields = [(f.name, f.type, f.length) for f in arcpy.ListFields(empty)][1:]
        self.assertEqual(fields, [("n", "Integer", 4), ("s", "String", 40), ("z", "Double", 8)])
        full = self.table("f", [("n", "SHORT"), ("cat", "TEXT")], [(1, "a")])
        with self.assertRaisesRegex(arcpy.ExecuteError, "001658"):
            arcpy.management.AlterField(full, "n", field_type="LONG")
        arcpy.management.AlterField(full, "n", field_type="SHORT")   # its own type: nothing to change
        arcpy.management.AlterField(full, "cat", "cat2")
        self.assertEqual({f.name: f.aliasName for f in arcpy.ListFields(full)}["cat2"], "cat")

    # ---- describe --------------------------------------------------------------------------------------------------

    def test_describe_answers_a_feature_dataset_and_the_class_properties(self):
        dataset = arcpy.management.CreateFeatureDataset(self.gpkg, "fd", arcpy.SpatialReference(32635))[0]
        described = arcpy.Describe(dataset)
        self.assertEqual((described.dataType, described.spatialReference.factoryCode), ("FeatureDataset", 32635))
        lines = self.make("cogo", "POLYLINE", [], [("LINESTRING(0 0,1 1)",)])
        self.assertTrue(arcpy.Describe(lines).hasSpatialIndex)
        arcpy.management.EnableCOGO(lines)
        self.assertTrue(arcpy.Describe(lines).isCOGOEnabled)
        fields = [(f.name, f.aliasName) for f in arcpy.ListFields(lines) if f.type not in ("OID", "Geometry")]
        self.assertEqual(fields, [("Direction", "Direction"), ("Distance", "Distance"), ("Radius", "Radius"),
                                  ("ArcLength", "Arc Length"), ("Radius2", "Radius2")])
        arcpy.management.DisableCOGO(lines)
        self.assertFalse(arcpy.Describe(lines).isCOGOEnabled)
        arcpy.management.EnableArchiving(lines)
        self.assertTrue(arcpy.da.Describe(lines)["isArchived"])

    def test_describe_finds_the_extent_of_an_invalid_shape(self):
        bow = self.make("bow", "POLYGON", [("k", "LONG")], [(1, "POLYGON((0 0,10 10,10 0,0 10,0 0))"), (2, None)])
        e = arcpy.Describe(bow).extent
        self.assertEqual((e.XMin, e.YMin, e.XMax, e.YMax), (0.0, 0.0, 10.0, 10.0))
        checked = arcpy.management.CheckGeometry(bow, self.out("c.gpkg"))[0]
        self.assertEqual(self.rows(checked, ["FEATURE_ID", "PROBLEM"]), [(1, "self intersections"), (2, "null geometry")])

    def test_relationship_rules_answer_minus_one_for_bounds_not_given(self):
        origin = self.make("o", "POINT", [("gid", "LONG")], [(1, "POINT(0 0)")])
        dest = self.table("d", [("gid", "LONG"), ("st", "LONG")])
        arcpy.management.SetSubtypeField(origin, "gid")
        arcpy.management.AddSubtype(origin, 1, "One")
        arcpy.management.SetSubtypeField(dest, "st")
        arcpy.management.AddSubtype(dest, 2, "Two")
        rc = self.gpkg + "/rc"
        arcpy.management.CreateRelationshipClass(origin, dest, rc, "SIMPLE", "f", "b", "NONE", "ONE_TO_MANY", "NONE",
                                                 "gid", "gid")
        arcpy.management.AddRuleToRelationshipClass(rc, "One", None, None, "Two", 0, 3)
        rule = arcpy.Describe(rc).relationshipRules[0]
        self.assertEqual((rule.originSubtypeCode, rule.originMinimumCardinality, rule.originMaximumCardinality,
                          rule.destinationSubtypeCode, rule.destinationMinimumCardinality,
                          rule.destinationMaximumCardinality), (1, -1, -1, 2, 0, 3))

    def test_an_attribute_rule_without_error_number_or_severity_answers_minus_one(self):
        parcels = self.make("p", "POINT", [("gid", "LONG"), ("val", "DOUBLE")], [(1, None, "POINT(0 0)")])
        arcpy.management.AddGlobalIDs(parcels)
        arcpy.management.AddAttributeRule(parcels, "dbl", "CALCULATION", "return $feature.gid * 2", "EDITABLE",
                                          "INSERT;UPDATE", field="val")
        rule = arcpy.Describe(parcels).attributeRules[0]
        self.assertEqual((rule.errorNumber, rule.severity), (-1, -1))

    def test_contingent_values_of_a_table_without_subtypes_answer_subtype_zero(self):
        parcels = self.make("p", "POINT", [("cat", "TEXT"), ("val", "DOUBLE")], [])
        arcpy.management.CreateFieldGroup(parcels, "fg", "cat;val")
        arcpy.management.AddContingentValue(parcels, "fg", [["cat", "ANY", ""], ["val", "NULL", ""]])
        self.assertEqual([c.subtype for c in arcpy.da.ListContingentValues(parcels)], [0])

    # ---- other tools -----------------------------------------------------------------------------------------------

    def test_dissolve_unsplit_lines_joins_only_at_shared_ends(self):
        # Two roads cross at (5, 5) and a third starts there; a fourth continues the first from its end (10, 5).
        lines = self.make("roads", "POLYLINE", [("kind", "TEXT")],
                          [("road", "LINESTRING(0 5,10 5)"), ("road", "LINESTRING(5 0,5 10)"),
                           ("road", "LINESTRING(5 5,8 8)"), ("road", "LINESTRING(10 5,15 5)")])
        out = arcpy.management.Dissolve(lines, self.gpkg + "/d", "kind", None, "MULTI_PART", "UNSPLIT_LINES")[0]
        self.assertEqual(sorted(round(r[0], 9) for r in self.rows(out, ["SHAPE@LENGTH"])),
                         sorted([10.0, 15.0, round(math.hypot(3, 3), 9)]))

    def test_eliminate_polygon_part_writes_orig_fid(self):
        parts = self.make("parts", "POLYGON", [("k", "LONG")],
                          [(7, "MULTIPOLYGON(((0 0,0 100,100 100,100 0,0 0)),((200 0,200 5,205 5,205 0,200 0)))")])
        out = arcpy.management.EliminatePolygonPart(parts, self.gpkg + "/e", "AREA", 100, part_option="ANY")[0]
        self.assertEqual(self.rows(out, ["k", "ORIG_FID", "SHAPE@AREA"]), [(7, 1, 10000.0)])

    def test_adjust_3d_z_adds_the_shift_before_reversing(self):
        points = arcpy.management.CreateFeatureclass(self.gpkg, "z", "POINT", has_z="ENABLED", spatial_reference=3857)[0]
        with arcpy.da.InsertCursor(points, ["SHAPE@"]) as written:
            written.insertRow((arcpy.PointGeometry(arcpy.Point(0, 0, 10), None, True),))
        arcpy.management.Adjust3DZ(points, "REVERSE", 10)
        self.assertEqual(self.rows(points, ["SHAPE@Z"]), [(-20.0,)])   # -(10 + 10)

    def test_an_attachment_relationship_class_exists(self):
        points = self.make("pts", "POINT", [], [("POINT(0 0)",)])
        arcpy.management.EnableAttachments(points)
        self.assertTrue(arcpy.Exists(points + "__ATTACHREL"))
        self.assertFalse(arcpy.Exists(points + "__NOTHING"))

    def test_clear_workspace_cache_answers_true(self):
        self.assertEqual(arcpy.management.ClearWorkspaceCache()[0], "true")

    def test_coordinate_notations_as_pro_writes_them(self):
        t = self.table("pts", [("lon", "DOUBLE"), ("lat", "DOUBLE")], [(27.0012345, 40.654321), (5.5, -3.25),
                                                                       (-0.0000123, 0.9999999999)])
        def convert(kind, *fields):
            out = arcpy.management.ConvertCoordinateNotation(t, self.gpkg + "/" + kind.lower(), "lon", "lat", "DD_2",
                                                             kind, spatial_reference=4326)[0]
            return self.rows(out, [*fields, "ORIG_OID"])
        # 0.654321° = 39.25926' = 39' 15.5556"; 0.0012345° = 0.07407' = 4.4442".
        self.assertEqual(convert("DMS_2", "DMSLat", "DMSLon"), [
            ("40 39 15.55560000N", "027 00 04.44420000E", 1), ("03 15 00.00000000S", "005 30 00.00000000E", 2),
            ("01 00 00.00000000N", "000 00 00.04428000W", 3)])
        self.assertEqual(convert("DD_1", "DDLatLon")[1], ("03.25000000S 005.50000000E", 2))
        self.assertEqual(convert("DDM_2", "DDMLat")[2], ("01 00.00000000N", 3))
        # Zone 35 easting 500104.36, northing 4500384.57: UTM truncated, MGRS rounded.
        self.assertEqual(convert("UTM_ZONES", "UTM_ZONES")[0], ("35N5001044500384", 1))
        self.assertEqual(convert("UTM_BANDS", "UTM_BANDS")[0], ("35T 500104 4500384", 1))
        self.assertEqual(convert("MGRS", "MGRS")[0], ("35TNF0010400385", 1))
        self.assertEqual(convert("USNG", "USNG")[0], ("35T NF 00104 00385", 1))
        # 5.5° E is 185.5° from 180° W: letters N, F; 3.25° S is 86.75° from the pole: F, M; 30.000' and 45.000'.
        self.assertEqual(convert("GEOREF", "GEOREF")[1], ("NFFM3000045000", 2))

    def test_create_fishnet_polyline_rotated(self):
        # The y-axis toward (10, 10): x-axis lines run south-east; 2 × 2 cells of 50 × 80 give 3 lines 100 long and
        # 3 lines 160 long.
        lines = arcpy.management.CreateFishnet(self.gpkg + "/net", "0 0", "10 10", 50, 80, 2, 2, None, "NO_LABELS",
                                               None, "POLYLINE")[0]
        self.assertEqual(sorted(round(r[0], 9) for r in self.rows(lines, ["SHAPE@LENGTH"])), [100.0] * 3 + [160.0] * 3)


if __name__ == "__main__":
    unittest.main()
