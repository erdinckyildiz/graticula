"""arcpy.analysis answers the Pro sweep measured in ArcGIS Pro 3.7, 2026-11-08 (tools/pro-compare/sweep), each pinned
by an example worked out by hand.

Everything is on EPSG:3857, so a distance of "N Meters" is N data units and every area below is plain arithmetic.
"""
import os
import sys
import shutil
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402


def rect(x0, y0, x1, y1):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1),
                                      arcpy.Point(x0, y1)]), 3857)


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


class ProSweepAnalysis(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, arcpy.env, "workspace", arcpy.env.workspace)
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

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

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as read:
            return [tuple(r) for r in read]

    def names(self, path):
        return [f.name for f in arcpy.ListFields(path) if f.type not in ("OID", "Geometry")]

    # ---- buffers: BUFF_DIST and ORIG_FID

    def test_buffer_and_pairwise_buffer_write_the_distance_and_the_feature_they_came_from(self):
        points = self.make("pts", "POINT", [("k", "TEXT")], [("a", point(0, 0)), ("b", point(100, 0))])
        for tool in (arcpy.analysis.Buffer, arcpy.analysis.PairwiseBuffer):
            out = tool(points, self.out(tool.__name__), "10 Meters").getOutput(0)
            self.assertEqual(self.names(out), ["k", "BUFF_DIST", "ORIG_FID"])
            self.assertEqual(self.rows(out, ["k", "BUFF_DIST", "ORIG_FID"]), [("a", 10.0, 1), ("b", 10.0, 2)])
            # Pro writes the distance in the data's units: 30 US survey feet is 30 × 1200/3937 m.
            feet = tool(points, self.out(tool.__name__ + "ft"), "30 Feet").getOutput(0)
            self.assertAlmostEqual(self.rows(feet, ["BUFF_DIST"])[0][0], 30 * 1200 / 3937, places=9)
            dissolved = tool(points, self.out(tool.__name__ + "all"), "10 Meters", dissolve_option="ALL").getOutput(0)
            self.assertEqual(self.names(dissolved), [])

    def test_multiple_ring_buffer_without_dissolving_keeps_the_fields_and_orig_fid(self):
        points = self.make("pts", "POINT", [("k", "TEXT")], [("a", point(0, 0))])
        out = arcpy.analysis.MultipleRingBuffer(points, self.out("r"), [10, 20], "Meters", "distance", "NONE")
        self.assertEqual(self.names(out.getOutput(0)), ["k", "distance", "ORIG_FID"])
        self.assertEqual(self.rows(out.getOutput(0), ["k", "distance", "ORIG_FID"]), [("a", 10.0, 1), ("a", 20.0, 1)])
        dissolved = arcpy.analysis.MultipleRingBuffer(points, self.out("d"), [10, 20], "Meters")
        self.assertEqual(self.names(dissolved.getOutput(0)), ["distance"])

    # ---- overlays cut one input's own overlaps

    def overlapping(self):
        # Two 100 × 100 squares of one input sharing a 50 × 100 strip, and a cover over both.
        a = self.make("a", "POLYGON", [("n", "LONG"), ("t", "TEXT")],
                      [(1, "x", rect(0, 0, 100, 100)), (2, "y", rect(50, 0, 150, 100))])
        b = self.make("b", "POLYGON", [("c", "LONG")], [(9, rect(0, 0, 150, 100))])
        return a, b

    def pieces(self, path, fields):
        return sorted((*r[:-1], round(r[-1], 6)) for r in self.rows(path, [*fields, "SHAPE@AREA"]))

    def test_intersect_writes_an_overlap_within_one_input_once_for_each_polygon(self):
        a, b = self.overlapping()
        out = arcpy.analysis.Intersect([a, b], self.out("i")).getOutput(0)
        self.assertEqual(self.pieces(out, ["FID_a", "FID_b"]), [(1, 1, 5000.0), (1, 1, 5000.0), (2, 1, 5000.0),
                                                                 (2, 1, 5000.0)])
        alone = arcpy.analysis.Intersect([a], self.out("i1")).getOutput(0)
        self.assertEqual(self.pieces(alone, ["FID_a"]), [(1, 5000.0), (2, 5000.0)])
        # PairwiseIntersect does not: each pair once, whole.
        pairwise = arcpy.analysis.PairwiseIntersect([a, b], self.out("p")).getOutput(0)
        self.assertEqual(self.pieces(pairwise, ["FID_a", "FID_b"]), [(1, 1, 10000.0), (2, 1, 10000.0)])

    def test_union_and_identity_cut_one_inputs_overlaps_and_blank_the_side_that_is_missing(self):
        a, b = self.overlapping()
        c = self.make("c", "POLYGON", [("c", "LONG"), ("w", "TEXT")], [(9, "z", rect(0, 0, 50, 100))])
        out = arcpy.analysis.Union([a, c], self.out("u")).getOutput(0)
        self.assertEqual(self.pieces(out, ["FID_a", "n", "t", "FID_c", "c", "w"]),
                         [(1, 1, "x", -1, 0, "", 5000.0), (1, 1, "x", 1, 9, "z", 5000.0),
                          (2, 2, "y", -1, 0, "", 5000.0), (2, 2, "y", -1, 0, "", 5000.0)])
        only = arcpy.analysis.Union([a, c], self.out("uo"), "ONLY_FID").getOutput(0)
        self.assertEqual(self.names(only), ["FID_a", "FID_c"])
        identity = arcpy.analysis.Identity(a, c, self.out("id")).getOutput(0)
        self.assertEqual(len(self.rows(identity, ["FID_a"])), 4)
        # Identity's ONLY_FID keeps the input's fields and only the identity features' FID.
        identity_only = arcpy.analysis.Identity(a, c, self.out("io"), "ONLY_FID").getOutput(0)
        self.assertEqual(self.names(identity_only), ["FID_a", "n", "t", "FID_c"])

    def test_split_cuts_one_inputs_overlaps(self):
        a, _ = self.overlapping()
        zones = self.make("z", "POLYGON", [("zone", "TEXT")], [("left", rect(0, 0, 150, 100))])
        workspace = arcpy.management.CreateFileGDB(self.directory, "split")[0]
        arcpy.analysis.Split(a, zones, "zone", workspace)
        self.assertEqual(self.pieces(f"{workspace}/left", ["n"]), [(1, 5000.0), (1, 5000.0), (2, 5000.0), (2, 5000.0)])

    def test_remove_overlap_multiple_writes_orig_fid(self):
        a, b = self.overlapping()
        out = arcpy.analysis.RemoveOverlapMultiple([a, b], self.out("r"), "THIESSEN").getOutput(0)
        self.assertEqual(self.names(out), ["ORIG_FID", "n", "t", "c"])
        only = arcpy.analysis.RemoveOverlapMultiple([a], self.out("o"), "THIESSEN", "ONLY_FID").getOutput(0)
        self.assertEqual(self.names(only), ["ORIG_FID"])

    def test_count_overlapping_features_writes_single_pieces_first(self):
        a, _ = self.overlapping()
        out = arcpy.analysis.CountOverlappingFeatures(a, self.out("c"), 1, self.out("t"))
        self.assertEqual(self.rows(out.getOutput(0), ["COUNT_"]), [(1,), (1,), (2,)])
        self.assertEqual(self.rows(out.getOutput(1), ["OVERLAP_OID", "ORIG_OID", "ORIG_NAME"]),
                         [(1, 1, a), (2, 2, a), (3, 1, a), (3, 2, a)])

    # ---- the rest

    def test_an_unset_optional_output_is_an_empty_string_in_the_result(self):
        a, b = self.overlapping()
        dissolved = arcpy.analysis.PairwiseDissolve(a, self.out("d"), "t")
        self.assertEqual((dissolved.outputCount, dissolved.getOutput(1)), (2, ""))
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.PairwiseDissolve(a, self.out("d2"), out_lineage_table=self.out("lineage"))
        counted = arcpy.analysis.CountOverlappingFeatures(a, self.out("c"))
        self.assertEqual((counted.outputCount, counted.getOutput(1)), (2, ""))
        summary = arcpy.analysis.SummarizeWithin(b, a, self.out("s"))
        self.assertEqual((summary.outputCount, summary.getOutput(1)), (2, ""))

    def test_origin_destination_links_colour_each_origin_in_turn(self):
        origins = self.make("o", "POINT", [], [(point(10 * i, 0),) for i in range(1, 11)])
        destinations = self.make("d", "POINT", [], [(point(0, 100),), (point(0, 200),)])
        out = arcpy.analysis.GenerateOriginDestinationLinks(origins, destinations, self.out("l")).getOutput(0)
        rows = self.rows(out, ["ORIG_FID", "DEST_FID", "COLOR_ID"])
        # Every link of the n-th origin is (n - 1) % 8 + 1, and an origin's links are in destination order.
        self.assertEqual(rows, [(o, d, (o - 1) % 8 + 1) for o in range(1, 11) for d in (1, 2)])

    def test_thiessen_frame_is_the_extent_a_tenth_larger(self):
        points = self.make("p", "POINT", [], [(point(0, 0),), (point(200, 0),), (point(0, 100),), (point(200, 100),)])
        out = arcpy.analysis.CreateThiessenPolygons(points, self.out("t")).getOutput(0)
        extent = arcpy.Describe(out).extent
        self.assertEqual([round(v, 6) for v in (extent.XMin, extent.YMin, extent.XMax, extent.YMax)],
                         [-10.0, -5.0, 210.0, 105.0])

    def test_pairwise_weights_read_the_documented_variables_form(self):
        out = arcpy.analysis.AssignWeightsByPairwiseComparison(
            ["slope", "landuse", "distance"], os.path.join(self.directory, "w.gpkg"), "ADD_MATRIX",
            "Variables slope landuse 3; slope distance 5; landuse distance 2").getOutput(0)
        rows = self.rows(out, ["Variables", "slope", "landuse", "distance", "Weights"])
        # Pro 3.7's answer: the matrix [[1, 3, 5], [1/3, 1, 2], [1/5, 1/2, 1]], each column divided by its total, the
        # rows averaged.
        expected = [("slope", 1.0, 3.0, 5.0, 0.6479468599033816), ("landuse", 1 / 3, 1.0, 2.0, 0.22987117552334943),
                    ("distance", 0.2, 0.5, 1.0, 0.12218196457326892)]
        for got, want in zip(rows, expected):
            self.assertEqual(got[0], want[0])
            for g, w in zip(got[1:], want[1:]):
                self.assertAlmostEqual(g, w, places=12)


if __name__ == "__main__":
    unittest.main()
