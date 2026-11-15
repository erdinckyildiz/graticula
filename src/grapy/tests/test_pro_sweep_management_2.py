"""arcpy.management (the names from EnableCOGO on), aligned to ArcGIS Pro 3.7 by the Pro sweep
(tools/pro-compare/sweep, case module management_2). Each test pins an answer measured in ArcGIS Pro 3.7 on 2026-11-08
with an example small enough to work by hand."""
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class _Workspace(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self._old = arcpy.env.workspace
        arcpy.env.workspace = self.directory
        arcpy.env.overwriteOutput = True
        self.gpkg = arcpy.management.CreateSQLiteDatabase(os.path.join(self.directory, "store.gpkg"), "GEOPACKAGE")[0]

    def tearDown(self):
        arcpy.env.workspace = self._old
        shutil.rmtree(self.directory, ignore_errors=True)

    def make(self, name, shape, fields, rows, srid=32635):
        fc = arcpy.management.CreateFeatureclass(self.gpkg, name, shape, spatial_reference=srid)[0]
        for field, kind in fields:
            arcpy.management.AddField(fc, field, kind)
        with arcpy.da.InsertCursor(fc, [f for f, _ in fields] + ["SHAPE@WKT"]) as rows_in:
            for row in rows:
                rows_in.insertRow(row)
        return fc

    def out(self, name):
        return os.path.join(self.gpkg, name)

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as cursor:
            return [tuple(r) for r in cursor]


class FeaturesAsPro(_Workspace):
    # A counter-clockwise triangle with its centroid (10, 10) inside it.
    TRIANGLE = "POLYGON ((0 0, 30 0, 0 30, 0 0))"

    def test_inside_point_is_the_centroid_when_it_lies_inside(self):
        # Pro: a triangle's INSIDE point is its centroid, (0 + 30 + 0) / 3 = 10 each way.
        tri = self.make("tri", "POLYGON", [("k", "LONG")], [(1, self.TRIANGLE)])
        out = arcpy.management.FeatureToPoint(tri, self.out("inside"), "INSIDE")[0]
        self.assertEqual(self.rows(out, ["SHAPE@XY"]), [((10.0, 10.0),)])

    def test_polygon_vertices_include_the_closing_one_and_run_clockwise(self):
        # Pro walks the exterior clockwise from its first vertex and writes the closing vertex too: 4 points.
        tri = self.make("tri", "POLYGON", [("k", "LONG")], [(1, self.TRIANGLE)])
        out = arcpy.management.FeatureVerticesToPoints(tri, self.out("v"), "ALL")[0]
        self.assertEqual([r[0] for r in self.rows(out, ["SHAPE@XY"])],
                         [(0.0, 0.0), (0.0, 30.0), (30.0, 0.0), (0.0, 0.0)])
        split = arcpy.management.SplitLine(tri, self.out("segments"))[0]
        self.assertEqual(self.rows(split, ["ORIG_SEQ", "SHAPE@WKT"])[0], (1, "MULTILINESTRING ((0 0, 0 30))"))

    def test_dangles_and_their_length(self):
        # A (0 0 → 10 0) crosses B (5 -5 → 5 5) at (5, 0): all four ends dangle, each 5 from the crossing; C, alone,
        # dangles at both ends by its whole length 7.
        lines = self.make("lines", "POLYLINE", [("k", "LONG")],
                          [(1, "LINESTRING (0 0, 10 0)"), (2, "LINESTRING (5 -5, 5 5)"), (3, "LINESTRING (20 0, 27 0)")])
        out = arcpy.management.FeatureVerticesToPoints(lines, self.out("d"), "DANGLE")[0]
        self.assertIn("DANGLE_LEN", [f.name for f in arcpy.ListFields(out)])
        self.assertEqual(sorted(self.rows(out, ["k", "DANGLE_LEN"])),
                         [(1, 5.0), (1, 5.0), (2, 5.0), (2, 5.0), (3, 7.0), (3, 7.0)])

    def test_an_end_on_another_line_is_no_dangle(self):
        # B starts on A's middle: B's start is connected, its end (5, 8) dangles 8 from A.
        lines = self.make("lines", "POLYLINE", [("k", "LONG")],
                          [(1, "LINESTRING (0 0, 10 0)"), (2, "LINESTRING (5 0, 5 8)")])
        out = arcpy.management.FeatureVerticesToPoints(lines, self.out("d"), "DANGLE")[0]
        self.assertEqual(sorted(self.rows(out, ["k", "DANGLE_LEN", "SHAPE@XY"])),
                         [(1, 5.0, (0.0, 0.0)), (1, 5.0, (10.0, 0.0)), (2, 8.0, (5.0, 8.0))])

    def test_feature_to_polygon_empty_values_and_line_inputs(self):
        # Pro: a line input brings no fields; a polygon input's are 0 and "" (FID_ -1), not null.
        lines = self.make("frame", "POLYLINE", [("kind", "TEXT")], [("x", "LINESTRING (0 0, 10 0, 10 10, 0 10, 0 0)")])
        out = arcpy.management.FeatureToPolygon(lines, self.out("from_lines"))[0]
        self.assertEqual([f.name for f in arcpy.ListFields(out) if f.type not in ("OID", "Geometry")], [])
        squares = self.make("sq", "POLYGON", [("n", "LONG"), ("t", "TEXT")],
                            [(5, "x", "POLYGON ((0 0, 0 10, 10 10, 10 0, 0 0))")])
        out = arcpy.management.FeatureToPolygon(squares, self.out("from_polys"))[0]
        self.assertEqual(self.rows(out, ["FID_sq", "n", "t"]), [(-1, 0, "")])

    def test_transects_run_from_the_right_to_the_left(self):
        # Along an eastward line the right side is south: the transect at 50 runs from (50, -10) to (50, 10).
        line = self.make("l", "POLYLINE", [("k", "LONG")], [(1, "LINESTRING (0 0, 100 0)")])
        out = arcpy.management.GenerateTransectsAlongLines(line, self.out("t"), "50 Meters", "20 Meters")[0]
        self.assertEqual(self.rows(out, ["SHAPE@WKT"]), [("MULTILINESTRING ((50 -10, 50 10))",)])

    def test_sort_writes_orig_fid(self):
        pts = self.make("p", "POINT", [("v", "DOUBLE")], [(3.0, "POINT (0 0)"), (1.0, "POINT (1 1)"), (2.0, "POINT (2 2)")])
        out = arcpy.management.Sort(pts, self.out("s"), [["v", "ASCENDING"]])[0]
        self.assertEqual(self.rows(out, ["v", "ORIG_FID"]), [(1.0, 2), (2.0, 3), (3.0, 1)])

    def test_unsplit_counts_only_the_lines_merged(self):
        # Two roads that cross (not end to end) stay apart, each counting itself once.
        lines = self.make("r", "POLYLINE", [("kind", "TEXT"), ("n", "LONG")],
                          [("road", 1, "LINESTRING (0 5, 10 5)"), ("road", 2, "LINESTRING (5 0, 5 10)")])
        out = arcpy.management.UnsplitLine(lines, self.out("u"), "kind", [["n", "COUNT"], ["n", "MAX"]])[0]
        self.assertEqual(sorted(self.rows(out, ["COUNT_n", "MAX_n"])), [(1, 1), (1, 2)])

    def test_encode_field_one_cold(self):
        table = arcpy.management.CreateTable(self.gpkg, "t")[0]
        arcpy.management.AddField(table, "c", "TEXT")
        with arcpy.da.InsertCursor(table, ["c"]) as rows:
            for v in ("a", "b"):
                rows.insertRow((v,))
        arcpy.management.EncodeField(table, "c", "ONECOLD")
        self.assertEqual([(f.name, f.type) for f in arcpy.ListFields(table)][-2:],
                         [("ONECOLD_c_a", "Integer"), ("ONECOLD_c_b", "Integer")])
        self.assertEqual(self.rows(table, ["ONECOLD_c_a", "ONECOLD_c_b"]), [(0, 1), (1, 0)])

    def test_tessellation_lattice(self):
        # Pro: a 100 m² square grid over 0..25 × 0..15 is 4 × 3 cells centred on (0 0), (10 0) …
        out = arcpy.management.GenerateTessellation(self.out("g"), "0 0 25 15", "SQUARE", "100 SquareMeters", 32635)[0]
        got = self.rows(out, ["GRID_ID", "SHAPE@XY"])
        self.assertEqual(len(got), 12)
        self.assertEqual(got[0], ("A-3", (0.0, 0.0)))
        self.assertEqual(got[-1], ("D-1", (30.0, 20.0)))


class SchemaAsPro(_Workspace):
    def test_cogo_fields_in_pros_order_and_describe(self):
        lines = self.make("l", "POLYLINE", [("k", "LONG")], [(1, "LINESTRING (0 0, 1 1)")])
        self.assertFalse(arcpy.Describe(lines).isCOGOEnabled)
        arcpy.management.EnableCOGO(lines)
        self.assertEqual([f.name for f in arcpy.ListFields(lines) if f.type != "Geometry"][-5:],
                         ["Direction", "Distance", "Radius", "ArcLength", "Radius2"])
        self.assertTrue(arcpy.Describe(lines).isCOGOEnabled)

    def test_match_table_fields_and_any_case_in_a_cursor(self):
        pts = self.make("p", "POINT", [("key", "LONG")], [(1, "POINT (0 0)")])
        folder = os.path.join(self.directory, "files")
        os.makedirs(folder)
        open(os.path.join(folder, "1.txt"), "w").close()
        out = arcpy.management.GenerateAttachmentMatchTable(pts, folder, self.out("m"), "key", "*.txt")[0]
        self.assertEqual([f.name for f in arcpy.ListFields(out)][1:], ["MatchID", "Filename"])
        # A cursor takes a field in any case, as arcpy's do.
        self.assertEqual(self.rows(out, ["MATCHID", "filename"]), [(1, "1.txt")])

    def test_relationship_rules_are_objects(self):
        origin = self.make("o", "POINT", [("k", "LONG")], [(1, "POINT (0 0)")])
        dest = arcpy.management.CreateTable(self.gpkg, "d")[0]
        arcpy.management.AddField(dest, "k", "LONG")
        arcpy.management.AddField(dest, "st", "SHORT")
        arcpy.management.SetSubtypeField(dest, "st")
        arcpy.management.AddSubtype(dest, 1, "One")
        rc = os.path.join(self.gpkg, "o_d")
        arcpy.management.CreateRelationshipClass(origin, dest, rc, "SIMPLE", "d", "o", "NONE", "ONE_TO_MANY", "NONE",
                                                 "k", "k")
        arcpy.management.AddRuleToRelationshipClass(rc, None, 0, 5, "One", 0, 3)
        rule = arcpy.Describe(rc).relationshipRules[0]
        self.assertEqual((rule.destinationSubtypeCode, rule.destinationMinimumCardinality,
                          rule.destinationMaximumCardinality), (1, 0, 3))

    def test_mosaic_allowed_compressions_list_the_default_first(self):
        import numpy as np
        folder = os.path.join(self.directory, "img")
        os.makedirs(folder)
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(32635)
        arcpy.NumPyArrayToRaster(np.ones((2, 2), "float32"), arcpy.Point(0, 0), 1, 1).save(os.path.join(folder, "a.tif"))
        arcpy.env.outputCoordinateSystem = None
        md = arcpy.management.CreateMosaicDataset(self.gpkg, "md", arcpy.SpatialReference(32635))[0]
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", os.path.join(folder, "a.tif"))
        arcpy.management.SetMosaicDatasetProperties(md, allowed_compressions="None;JPEG;LZ77",
                                                    default_compression_type="LZ77")
        self.assertEqual(arcpy.Describe(md).allowedCompressionMethods, "LZ77;None;JPEG")


class RuleGeneratorsAsPro(_Workspace):
    def test_generate_id_without_a_configuration(self):
        # Pro writes <out>/<table>/GenerateID.csv, ID_Table.csv and create_database_sequences.py; with no
        # configuration given, one for every row: no filter, start 1, increment 1, no padding.
        pts = self.make("assets", "POINT", [("asset_id", "TEXT")], [])
        folder = os.path.join(self.directory, "rules")
        result = arcpy.management.GenerateIDAttributeRule(pts, folder, "asset_id")
        self.assertEqual(sorted(os.listdir(os.path.join(folder, "assets"))),
                         ["GenerateID.csv", "ID_Table.csv", "create_database_sequences.py"])
        import csv
        with open(result[0], newline="", encoding="utf-8") as f:
            rule = dict(zip(*list(csv.reader(f))[:2]))
        self.assertEqual((rule["NAME"], rule["DESCRIPTION"], rule["ISEDITABLE"], rule["TRIGGERINSERT"],
                          rule["TRIGGERUPDATE"]), ("GenerateID", "Generate ID using Database Sequences", "False", "True",
                                                   "False"))
        with open(result[1], newline="", encoding="utf-8") as f:
            ids = list(csv.reader(f))
        self.assertEqual(ids[0][:5], ["whereclause", "description", "sequencename", "startingvalue", "incrementvalue"])
        self.assertEqual((ids[1][1], ids[1][3], ids[1][4], ids[1][7]),
                         ("Generate an ID for any row in the class", "1", "1", "0"))


class LayersAsPro(_Workspace):
    def test_a_tin_layer_is_described_as_one(self):
        pts = self.make("p", "POINT", [("z", "DOUBLE")],
                        [(1.0, "POINT (0 0)"), (2.0, "POINT (10 0)"), (3.0, "POINT (0 10)"), (4.0, "POINT (10 10)")])
        tin = os.path.join(self.directory, "t")
        arcpy.ddd.CreateTin(tin, arcpy.SpatialReference(32635), [[pts, "z", "Mass_Points", "<None>"]])
        layer = arcpy.management.MakeTinLayer(tin, "t_lyr")[0]
        self.assertEqual((arcpy.Describe(layer).dataType, arcpy.Describe(tin).dataType), ("TinLayer", "Tin"))


if __name__ == "__main__":
    unittest.main()
