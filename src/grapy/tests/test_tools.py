"""grapy's geoprocessing tools, each against a shape whose answer is known — ADR-223 §5.2.

Squares 50 wide, 100 apart, on a projected reference, so every area, distance and count below is worked out by hand
and none is read back from the code under test.
"""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


def square(x, y, side=50):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x + side, y), arcpy.Point(x + side, y + side),
                                      arcpy.Point(x, y + side)]), 3857)


class Tools(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.parcels = self.make("parcels", "POLYGON", [("name", "TEXT"), ("kind", "TEXT"), ("value", "DOUBLE")],
                                 [("p0", "a", 1.0, square(0, 0)), ("p1", "a", 2.0, square(100, 0)),
                                  ("p2", "b", 4.0, square(200, 0))])
        # One zone over the right half of p0 and all of p1: 25×50 of p0 and 50×50 of p1 lie inside it.
        self.zones = self.make("zones", "POLYGON", [("zone", "TEXT")], [("z", arcpy.Polygon(arcpy.Array(
            [arcpy.Point(25, 0), arcpy.Point(150, 0), arcpy.Point(150, 50), arcpy.Point(25, 50)]), 3857))])
        self.wells = self.make("wells", "POINT", [("well", "TEXT")],
                               [("w0", arcpy.PointGeometry(arcpy.Point(10, 10), 3857)),
                                ("w1", arcpy.PointGeometry(arcpy.Point(20, 20), 3857)),
                                ("w2", arcpy.PointGeometry(arcpy.Point(400, 10), 3857))])

    def make(self, name, shape, fields, rows):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    # ---- management

    def test_merge_and_append_carry_the_fields_each_input_has(self):
        merged = arcpy.management.Merge([self.parcels, self.zones], self.out("m"), add_source="ADD_SOURCE_INFO").getOutput(0)
        got = self.rows(merged, ["name", "zone", "MERGE_SRC"])
        self.assertEqual(len(got), 4)
        self.assertEqual(got[3][:2], (None, "z"))
        self.assertTrue(got[0][2].endswith("parcels.gpkg"))
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.Merge([self.parcels, self.wells], self.out("bad"))
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.Append(self.zones, self.parcels, "TEST")
        arcpy.management.Append(self.zones, self.parcels, "NO_TEST")
        self.assertEqual(arcpy.management.GetCount(self.parcels)[0], "4")

    def test_dissolve_unions_by_field_and_names_its_statistics_as_arcpy_does(self):
        out = arcpy.management.Dissolve(self.parcels, self.out("d"), "kind", [["value", "SUM"], ["name", "COUNT"]]).getOutput(0)
        got = sorted(self.rows(out, ["kind", "SUM_value", "COUNT_name", "SHAPE@AREA"]))
        self.assertEqual(got, [("a", 3.0, 2, 5000.0), ("b", 4.0, 1, 2500.0)])
        single = arcpy.management.Dissolve(self.parcels, self.out("s"), "kind", multi_part="SINGLE_PART").getOutput(0)
        self.assertEqual(arcpy.management.GetCount(single)[0], "3")

    def test_multipart_to_singlepart_and_feature_to_point_keep_orig_fid(self):
        whole = arcpy.management.Dissolve(self.parcels, self.out("whole")).getOutput(0)
        parts = arcpy.management.MultipartToSinglepart(whole, self.out("parts")).getOutput(0)
        self.assertEqual(sorted(r[0] for r in self.rows(parts, ["ORIG_FID"])), [1, 1, 1])
        points = arcpy.management.FeatureToPoint(self.parcels, self.out("pts")).getOutput(0)
        self.assertEqual(self.rows(points, ["name", "SHAPE@XY", "ORIG_FID"])[1], ("p1", (125.0, 25.0), 2))
        corners = arcpy.management.FeatureVerticesToPoints(self.parcels, self.out("v")).getOutput(0)
        self.assertEqual(arcpy.management.GetCount(corners)[0], "12")
        ends = arcpy.management.FeatureVerticesToPoints(self.parcels, self.out("e"), "START").getOutput(0)
        self.assertEqual(self.rows(ends, ["SHAPE@XY"])[0], ((0.0, 0.0),))

    def test_fields_are_added_altered_and_deleted(self):
        arcpy.management.AddFields(self.parcels, [["a", "LONG"], ["b", "TEXT"]])
        arcpy.management.AlterField(self.parcels, "a", "count")
        arcpy.management.DeleteField(self.parcels, ["b", "kind"])
        self.assertEqual([f.name for f in arcpy.ListFields(self.parcels)][1:-1], ["name", "value", "count"])
        arcpy.management.DeleteField(self.parcels, "name", "KEEP_FIELDS")
        self.assertEqual([f.name for f in arcpy.ListFields(self.parcels)][1:-1], ["name"])

    def test_geometry_attributes_and_xy_are_written_in_the_unit_asked(self):
        arcpy.management.CalculateGeometryAttributes(self.parcels, [["area", "AREA"], ["len", "PERIMETER_LENGTH"],
                                                                    ["cx", "CENTROID_X"], ["n", "POINT_COUNT"]],
                                                     "KILOMETERS", "SQUARE_KILOMETERS")
        area, length, cx, n = self.rows(self.parcels, ["area", "len", "cx", "n"])[0]
        self.assertAlmostEqual(area, 0.0025)
        self.assertAlmostEqual(length, 0.2)
        self.assertEqual((cx, n), (25.0, 5))
        arcpy.management.AddXY(self.wells)
        self.assertEqual(self.rows(self.wells, ["POINT_X", "POINT_Y"])[2], (400.0, 10.0))

    def test_minimum_bounding_geometry_around_each_and_around_groups(self):
        hull = arcpy.management.MinimumBoundingGeometry(self.wells, self.out("h"), "CONVEX_HULL", "ALL").getOutput(0)
        self.assertEqual(arcpy.management.GetCount(hull)[0], "1")
        boxes = arcpy.management.MinimumBoundingGeometry(self.parcels, self.out("b"), "ENVELOPE", "LIST", "kind").getOutput(0)
        self.assertEqual(sorted(self.rows(boxes, ["kind", "SHAPE@AREA"])), [("a", 7500.0), ("b", 2500.0)])

    def test_repair_delete_and_rename(self):
        bow = arcpy.Polygon(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(10, 10), arcpy.Point(10, 0), arcpy.Point(0, 10)]), 3857)
        with arcpy.da.InsertCursor(self.parcels, ["name", "SHAPE@"]) as written:
            written.insertRow(("bow", bow))
        arcpy.management.RepairGeometry(self.parcels)
        self.assertTrue(all(r[0]._shape.is_valid for r in self.rows(self.parcels, ["SHAPE@"])))
        renamed = arcpy.management.Rename(self.parcels, self.out("lots")).getOutput(0)
        self.assertTrue(arcpy.Exists(renamed))
        self.assertFalse(arcpy.Exists(self.parcels))
        arcpy.management.DeleteFeatures(renamed)
        self.assertEqual(arcpy.management.GetCount(renamed)[0], "0")

    # ---- analysis

    def test_select_chooses_by_where_clause(self):
        out = arcpy.analysis.Select(self.parcels, self.out("sel"), "value > 1").getOutput(0)
        self.assertEqual([r[0] for r in self.rows(out, ["name"])], ["p1", "p2"])

    def test_intersect_union_identity_and_symdiff_cut_where_arcpy_cuts(self):
        cut = arcpy.analysis.Intersect([self.parcels, self.zones], self.out("i")).getOutput(0)
        self.assertEqual(sorted(self.rows(cut, ["name", "zone", "SHAPE@AREA"])), [("p0", "z", 1250.0), ("p1", "z", 2500.0)])
        self.assertIn("FID_parcels", [f.name for f in arcpy.ListFields(cut)])
        union = arcpy.analysis.Union([self.parcels, self.zones], self.out("u")).getOutput(0)
        # p0's left half, p0∩z, p1∩z, p2, and the zone's gap between p0 and p1 (50×50).
        self.assertEqual(sorted(round(r[0]) for r in self.rows(union, ["SHAPE@AREA"])), [1250, 1250, 2500, 2500, 2500])
        self.assertEqual(sum(1 for r in self.rows(union, ["FID_parcels"]) if r[0] == -1), 1)
        identity = arcpy.analysis.Identity(self.parcels, self.zones, self.out("id")).getOutput(0)
        self.assertEqual(sum(r[0] for r in self.rows(identity, ["SHAPE@AREA"])), 7500.0)
        sym = arcpy.analysis.SymDiff(self.parcels, self.zones, self.out("sd")).getOutput(0)
        self.assertEqual(sorted(round(r[0]) for r in self.rows(sym, ["SHAPE@AREA"])), [1250, 2500, 2500])

    def test_spatial_join_counts_matches_and_writes_one_row_per_match_when_asked(self):
        one = arcpy.analysis.SpatialJoin(self.parcels, self.wells, self.out("sj")).getOutput(0)
        self.assertEqual(self.rows(one, ["name", "Join_Count", "well"]), [("p0", 2, "w0"), ("p1", 0, None), ("p2", 0, None)])
        many = arcpy.analysis.SpatialJoin(self.parcels, self.wells, self.out("sjm"), "JOIN_ONE_TO_MANY", "KEEP_COMMON").getOutput(0)
        self.assertEqual(self.rows(many, ["name", "well", "JOIN_FID"]), [("p0", "w0", 1), ("p0", "w1", 2)])
        near = arcpy.analysis.SpatialJoin(self.wells, self.parcels, self.out("sjc"), match_option="CLOSEST",
                                          distance_field_name="dist").getOutput(0)
        self.assertEqual(self.rows(near, ["well", "name", "dist"])[2], ("w2", "p2", 150.0))

    def test_near_writes_the_nearest_feature_and_its_distance(self):
        arcpy.analysis.Near(self.wells, self.parcels, "100 Meters", "LOCATION", "ANGLE")
        got = self.rows(self.wells, ["NEAR_FID", "NEAR_DIST", "NEAR_X", "NEAR_Y", "NEAR_ANGLE"])
        self.assertEqual(got[0][:2], (1, 0.0))
        self.assertEqual(got[2][:2], (-1, -1.0))
        arcpy.analysis.Near(self.wells, self.parcels, location="LOCATION", angle="ANGLE")
        self.assertEqual(self.rows(self.wells, ["NEAR_FID", "NEAR_DIST", "NEAR_X", "NEAR_ANGLE"])[2], (3, 150.0, 250.0, 180.0))

    def test_statistics_and_frequency_summarise_by_case(self):
        out = arcpy.analysis.Statistics(self.parcels, self.out("st"), [["value", "SUM"], ["value", "MEAN"], ["value", "MAX"]], "kind").getOutput(0)
        self.assertEqual(sorted(self.rows(out, ["kind", "FREQUENCY", "SUM_value", "MEAN_value", "MAX_value"])),
                         [("a", 2, 3.0, 1.5, 2.0), ("b", 1, 4.0, 4.0, 4.0)])
        frequency = arcpy.analysis.Frequency(self.parcels, self.out("fq"), "kind", "value").getOutput(0)
        self.assertEqual(sorted(self.rows(frequency, ["kind", "FREQUENCY", "value"])), [("a", 2, 3.0), ("b", 1, 4.0)])

    def test_multiple_ring_buffer_writes_bands(self):
        out = arcpy.analysis.MultipleRingBuffer(self.wells, self.out("rings"), [10, 20], "Meters",
                                                dissolve_option="NONE").getOutput(0)
        got = self.rows(out, ["well", "distance", "SHAPE@AREA"])
        self.assertEqual([r[:2] for r in got[:2]], [("w0", 10.0), ("w0", 20.0)])
        self.assertAlmostEqual(got[1][2] / got[0][2], 3.0, places=2)  # π(20²−10²) over π·10²

    def test_pairwise_tools_answer_as_their_classic_twins(self):
        out = arcpy.analysis.PairwiseIntersect([self.parcels, self.zones], self.out("pi")).getOutput(0)
        self.assertEqual(arcpy.management.GetCount(out)[0], "2")
        out = arcpy.analysis.PairwiseDissolve(self.parcels, self.out("pd"), "kind").getOutput(0)
        self.assertEqual(arcpy.management.GetCount(out)[0], "2")

    # ---- conversion

    def test_export_features_and_the_json_round_trips(self):
        out = arcpy.conversion.ExportFeatures(self.parcels, self.out("ex"), "kind = 'a'", sort_field=[["value", "DESCENDING"]]).getOutput(0)
        self.assertEqual([r[0] for r in self.rows(out, ["name"])], ["p1", "p0"])
        esri = os.path.join(self.directory, "p.json")
        arcpy.conversion.FeaturesToJSON(self.parcels, esri)
        document = json.loads(open(esri, encoding="utf-8").read())
        self.assertEqual(document["geometryType"], "esriGeometryPolygon")
        self.assertEqual(document["spatialReference"]["wkid"], 3857)
        ring = document["features"][0]["geometry"]["rings"][0]
        # Esri's outer ring runs clockwise: the shoelace sum Σ(x₂−x₁)(y₂+y₁) is positive for one that does.
        self.assertGreater(sum((b[0] - a[0]) * (b[1] + a[1]) for a, b in zip(ring, ring[1:])), 0)
        back = arcpy.conversion.JSONToFeatures(esri, self.out("back")).getOutput(0)
        self.assertEqual(self.rows(back, ["name", "SHAPE@AREA"]), self.rows(self.parcels, ["name", "SHAPE@AREA"]))
        geojson = os.path.join(self.directory, "p.geojson")
        arcpy.conversion.FeaturesToJSON(self.parcels, geojson, geoJSON="GEOJSON")
        self.assertEqual(json.loads(open(geojson, encoding="utf-8").read())["type"], "FeatureCollection")
        self.assertEqual(arcpy.management.GetCount(arcpy.conversion.JSONToFeatures(geojson, self.out("gj")).getOutput(0))[0], "3")


if __name__ == "__main__":
    unittest.main()
