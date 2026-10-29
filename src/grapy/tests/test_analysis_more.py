"""The arcpy.analysis tools ADR-224 adds to grapy, each against shapes whose answer is worked out by hand.

Everything is on EPSG:3857, so a distance of "N Meters" is N data units and every area below is plain arithmetic.
"""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402


def rect(x0, y0, x1, y1):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1),
                                      arcpy.Point(x0, y1)]), 3857)


def square(x, y, side=50):
    return rect(x, y, x + side, y + side)


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


def line(*points):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in points]), 3857)


class Analysis(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
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

    # ---- proximity

    def test_point_distance_pairs_every_point_within_the_radius_but_not_itself(self):
        points = self.make("pts", "POINT", [], [(point(0, 0),), (point(3, 4),), (point(100, 0),)])
        out = arcpy.analysis.PointDistance(points, points, self.out("pd"), "10 Meters").getOutput(0)
        self.assertEqual(self.rows(out, ["INPUT_FID", "NEAR_FID", "DISTANCE"]), [(1, 2, 5.0), (2, 1, 5.0)])
        every = arcpy.analysis.PointDistance(points, points, self.out("all")).getOutput(0)
        self.assertEqual(len(self.rows(every, ["DISTANCE"])), 6)

    def test_generate_near_table_ranks_writes_locations_angles_and_units(self):
        parcels = self.make("parcels", "POLYGON", [], [(square(0, 0),), (square(100, 0),), (square(200, 0),)])
        wells = self.make("wells", "POINT", [], [(point(10, 10),), (point(20, 20),), (point(400, 10),)])
        closest = arcpy.analysis.GenerateNearTable(wells, parcels, self.out("c"), "50 Meters").getOutput(0)
        # The third well is 150 from the nearest parcel, beyond the radius: no row.
        self.assertEqual(self.rows(closest, ["IN_FID", "NEAR_FID", "NEAR_DIST", "NEAR_RANK"]),
                         [(1, 1, 0.0, 1), (2, 1, 0.0, 1)])
        two = arcpy.analysis.GenerateNearTable(wells, parcels, self.out("two"), None, "LOCATION", "ANGLE", "ALL", 2,
                                               distance_unit="Kilometers").getOutput(0)
        got = self.rows(two, ["IN_FID", "NEAR_FID", "NEAR_DIST", "NEAR_RANK", "FROM_X", "FROM_Y", "NEAR_X", "NEAR_Y",
                              "NEAR_ANGLE"])
        self.assertEqual(len(got), 6)
        self.assertEqual(got[1], (1, 2, 0.09, 2, 10.0, 10.0, 100.0, 10.0, 0.0))
        self.assertEqual(got[3][:4], (2, 2, 0.08, 2))
        self.assertEqual(got[4][1:4] + got[4][-1:], (3, 0.15, 1, 180.0))
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.GenerateNearTable(wells, parcels, self.out("g"), method="GEODESIC")
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.GenerateNearTable(wells, parcels, self.out("f"), distance_unit="Feet")

    def test_origin_destination_links_nearest_grouped_and_within_a_distance(self):
        origins = self.make("o", "POINT", [("g", "TEXT")], [("a", point(0, 0)), ("b", point(100, 0))])
        destinations = self.make("d", "POINT", [("g", "TEXT")], [("a", point(0, 10)), ("a", point(0, 30)),
                                                                  ("b", point(100, 20))])
        fields = ["ORIG_FID", "DEST_FID", "LINK_DIST", "ORIG_X", "DEST_Y"]
        nearest = arcpy.analysis.GenerateOriginDestinationLinks(origins, destinations, self.out("n"), num_nearest=1,
                                                                distance_unit="KILOMETERS").getOutput(0)
        self.assertEqual(self.rows(nearest, fields), [(1, 1, 0.01, 0.0, 10.0), (2, 3, 0.02, 100.0, 20.0)])
        grouped = arcpy.analysis.GenerateOriginDestinationLinks(origins, destinations, self.out("gr"), "g", "g").getOutput(0)
        self.assertEqual(self.rows(grouped, ["ORIG_FID", "DEST_FID", "LINK_DIST", "GROUP_ID"]),
                         [(1, 1, 10.0, "a"), (1, 2, 30.0, "a"), (2, 3, 20.0, "b")])
        near = arcpy.analysis.GenerateOriginDestinationLinks(origins, destinations, self.out("s"), search_distance=25).getOutput(0)
        self.assertEqual(self.rows(near, ["ORIG_FID", "DEST_FID"]), [(1, 1), (2, 3)])
        self.assertTrue(all(1 <= r[0] <= 8 for r in self.rows(near, ["COLOR_ID"])))
        self.assertEqual(self.rows(near, ["SHAPE@LENGTH"]), [(10.0,), (20.0,)])

    def test_polygon_neighbors_report_edges_nodes_and_overlaps(self):
        # p1 and p2 share the edge x = 50 (50 long); p3 touches p2 at the corner (100, 50) only.
        polygons = self.make("p", "POLYGON", [("name", "TEXT")],
                             [("p1", square(0, 0)), ("p2", square(50, 0)), ("p3", square(100, 50))])
        fields = ["src_name", "nbr_name", "LENGTH", "NODE_COUNT"]
        both = arcpy.analysis.PolygonNeighbors(polygons, self.out("nb"), "name").getOutput(0)
        self.assertEqual(self.rows(both, fields), [("p1", "p2", 50.0, 0), ("p2", "p1", 50.0, 0), ("p2", "p3", 0.0, 1),
                                                   ("p3", "p2", 0.0, 1)])
        one = arcpy.analysis.PolygonNeighbors(polygons, self.out("one"), None, both_sides="NO_BOTH_SIDES",
                                              out_linear_units="KILOMETERS").getOutput(0)
        self.assertEqual(self.rows(one, ["src_OBJECTID", "nbr_OBJECTID", "LENGTH", "NODE_COUNT"]),
                         [(1, 2, 0.05, 0), (2, 3, 0.0, 1)])
        # q2 overlaps q1 over [40,50]×[10,30], 200 square units; their boundaries cross twice, at (50,10) and (50,30).
        crossing = self.make("x", "POLYGON", [], [(square(0, 0),), (rect(40, 10, 60, 30),)])
        nodes = arcpy.analysis.PolygonNeighbors(crossing, self.out("xn")).getOutput(0)
        self.assertEqual(self.rows(nodes, ["src_OBJECTID", "LENGTH", "NODE_COUNT"]), [(1, 0.0, 2), (2, 0.0, 2)])
        area = arcpy.analysis.PolygonNeighbors(crossing, self.out("xa"), area_overlap="AREA_OVERLAP").getOutput(0)
        self.assertEqual(self.rows(area, ["src_OBJECTID", "AREA", "LENGTH", "NODE_COUNT"]), [(1, 200.0, 0.0, 0), (2, 200.0, 0.0, 0)])
        pairwise = arcpy.analysis.PairwisePolygonNeighbors(crossing, self.out("xp")).getOutput(0)
        self.assertEqual(self.rows(pairwise, ["LENGTH"]), [])
        same = self.make("same", "POLYGON", [("k", "TEXT")], [("a", square(0, 0)), ("a", square(50, 0))])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.PolygonNeighbors(same, self.out("grouped"), "k")

    def test_thiessen_polygons_divide_the_extent_plus_a_tenth(self):
        points = self.make("pts", "POINT", [("name", "TEXT")],
                           [("a", point(0, 0)), ("b", point(100, 0)), ("c", point(0, 100)), ("d", point(100, 100))])
        out = arcpy.analysis.CreateThiessenPolygons(points, self.out("t"), "ALL").getOutput(0)
        # The frame is (−10, −10)–(110, 110); the bisectors x = 50 and y = 50 leave each point a 60 × 60 cell.
        self.assertEqual(sorted(self.rows(out, ["Input_FID", "name", "SHAPE@AREA"])),
                         [(1, "a", 3600.0), (2, "b", 3600.0), (3, "c", 3600.0), (4, "d", 3600.0)])
        only = arcpy.analysis.CreateThiessenPolygons(points, self.out("o")).getOutput(0)
        self.assertEqual([f.name for f in arcpy.ListFields(only)][1:-1], ["Input_FID"])

    # ---- overlay

    def test_count_overlapping_features_counts_each_piece_of_the_union(self):
        left = self.make("left", "POLYGON", [], [(square(0, 0),)])
        right = self.make("right", "POLYGON", [], [(square(25, 0),)])
        both = self.make("both", "POLYGON", [], [(square(0, 0),), (square(25, 0),)])
        out = arcpy.analysis.CountOverlappingFeatures(both, self.out("c")).getOutput(0)
        # [0,25], [25,50] and [50,75] across, each 25 × 50.
        self.assertEqual(sorted(self.rows(out, ["COUNT_", "COUNT_FC", "SHAPE@AREA"])),
                         [(1, 1, 1250.0), (1, 1, 1250.0), (2, 1, 1250.0)])
        result = arcpy.analysis.CountOverlappingFeatures([left, right], self.out("c2"), 2, self.out("t"))
        self.assertEqual(self.rows(result.getOutput(0), ["COUNT_", "COUNT_FC", "SHAPE@AREA"]), [(2, 2, 1250.0)])
        self.assertEqual(self.rows(result.getOutput(1), ["OVERLAP_OID", "ORIG_OID", "ORIG_NAME"]),
                         [(1, 1, "left"), (1, 1, "right")])
        points = self.make("pts", "POINT", [], [(point(0, 0),), (point(0, 0),), (point(5, 5),)])
        counted = arcpy.analysis.CountOverlappingFeatures(points, self.out("cp"), 2).getOutput(0)
        self.assertEqual(self.rows(counted, ["COUNT_", "SHAPE@XY"]), [(2, (0.0, 0.0))])
        lines = self.make("lines", "POLYLINE", [], [(line((0, 0), (1, 0)),)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.CountOverlappingFeatures(lines, self.out("cl"))

    def test_update_lays_the_update_polygons_over_the_input(self):
        base = self.make("base", "POLYGON", [("name", "TEXT"), ("kind", "TEXT")], [("in", "k", square(0, 0, 100))])
        update = self.make("upd", "POLYGON", [("name", "TEXT")], [("up", square(50, 0, 100))])
        out = arcpy.analysis.Update(base, update, self.out("u")).getOutput(0)
        self.assertEqual(self.rows(out, ["name", "kind", "SHAPE@AREA"]), [("in", "k", 5000.0), ("up", None, 10000.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.Update(base, update, self.out("nb"), "NO_BORDERS")

    def test_split_and_split_by_attributes_write_one_output_per_value(self):
        land = self.make("land", "POLYGON", [("v", "DOUBLE")], [(1.0, square(0, 0, 100))])
        halves = self.make("halves", "POLYGON", [("zone", "TEXT")],
                           [("south", rect(0, 0, 100, 50)), ("north", rect(0, 50, 100, 120)), ("east", square(500, 0))])
        folder = os.path.join(self.directory, "split")
        os.makedirs(folder)
        arcpy.analysis.Split(land, halves, "zone", folder)
        self.assertEqual(sorted(os.listdir(folder)), ["north.gpkg", "south.gpkg"])
        self.assertEqual(self.rows(os.path.join(folder, "north.gpkg"), ["v", "SHAPE@AREA"]), [(1.0, 5000.0)])
        parcels = self.make("parcels", "POLYGON", [("name", "TEXT"), ("kind", "TEXT")],
                            [("p0", "a", square(0, 0)), ("p1", "a", square(100, 0)), ("p2", "b c", square(200, 0))])
        target = os.path.join(self.directory, "by")
        os.makedirs(target)
        arcpy.analysis.SplitByAttributes(parcels, target, "kind")
        self.assertEqual(sorted(os.listdir(target)), ["a.gpkg", "b_c.gpkg"])
        self.assertEqual(self.rows(os.path.join(target, "a.gpkg"), ["name"]), [("p0",), ("p1",)])
        arcpy.analysis.SplitByAttributes(parcels, self.out("byboth"), ["kind", "name"])
        self.assertEqual(self.rows(self.out("byboth") + "/b_c_p2", ["name"]), [("p2",)])

    def test_graphic_buffer_draws_caps_and_joins(self):
        wells = self.make("w", "POINT", [("name", "TEXT")], [("w", point(0, 0))])
        out = arcpy.analysis.GraphicBuffer(wells, self.out("bw"), "10 Meters").getOutput(0)
        # A square cap on a point: a square 20 on a side.
        self.assertEqual(self.rows(out, ["name", "BUFF_DIST", "ORIG_FID", "SHAPE@AREA"]), [("w", 10.0, 1, 400.0)])
        roads = self.make("r", "POLYLINE", [], [(line((0, 0), (100, 0)),)])
        square_cap = arcpy.analysis.GraphicBuffer(roads, self.out("rs"), 10).getOutput(0)
        butt = arcpy.analysis.GraphicBuffer(roads, self.out("rb"), 10, "BUTT").getOutput(0)
        self.assertAlmostEqual(self.rows(square_cap, ["SHAPE@AREA"])[0][0], 120 * 20)
        self.assertAlmostEqual(self.rows(butt, ["SHAPE@AREA"])[0][0], 100 * 20)
        lots = self.make("l", "POLYGON", [], [(square(0, 0),)])
        mitre = arcpy.analysis.GraphicBuffer(lots, self.out("lm"), 10).getOutput(0)
        bevel = arcpy.analysis.GraphicBuffer(lots, self.out("lb"), 10, line_joins="BEVEL").getOutput(0)
        self.assertAlmostEqual(self.rows(mitre, ["SHAPE@AREA"])[0][0], 70 * 70)
        # Each corner keeps half of its 10 × 10 square: 2500 + 4·500 + 4·50.
        self.assertAlmostEqual(self.rows(bevel, ["SHAPE@AREA"])[0][0], 4700)
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.GraphicBuffer(wells, self.out("bb"), 10, "BUTT")

    def test_tabulate_intersection_shares_and_sums_by_zone_and_class(self):
        zones = self.make("z", "POLYGON", [("zone", "TEXT")], [("z1", square(0, 0)), ("z2", square(100, 0))])
        classes = self.make("c", "POLYGON", [("cls", "TEXT"), ("pop", "DOUBLE")],
                            [("c", 100.0, rect(25, 0, 125, 50)), ("d", 8.0, square(0, 0, 10))])
        out = arcpy.analysis.TabulateIntersection(zones, "zone", classes, self.out("t"), "cls", "pop").getOutput(0)
        # c (5000 in all) lies 1250 in each zone, half of each and a quarter of itself; d (100) lies wholly in z1.
        self.assertEqual(sorted(self.rows(out, ["zone", "cls", "pop", "AREA", "PERCENTAGE"])),
                         [("z1", "c", 25.0, 1250.0, 50.0), ("z1", "d", 8.0, 100.0, 4.0), ("z2", "c", 25.0, 1250.0, 50.0)])
        hectares = arcpy.analysis.TabulateIntersection(zones, "zone", classes, self.out("h"), out_units="HECTARES").getOutput(0)
        self.assertEqual(sorted(self.rows(hectares, ["zone", "AREA", "PERCENTAGE"])), [("z1", 0.135, 54.0), ("z2", 0.125, 50.0)])

    def test_apportion_polygon_shares_by_area(self):
        tracts = self.make("tracts", "POLYGON", [("pop", "DOUBLE")], [(100.0, rect(0, 0, 100, 50))])
        blocks = self.make("blocks", "POLYGON", [("name", "TEXT")],
                           [("a", square(0, 0)), ("b", square(50, 0)), ("far", square(500, 0))])
        out = arcpy.analysis.ApportionPolygon(tracts, "pop SUM", blocks, self.out("ap")).getOutput(0)
        self.assertEqual(self.rows(out, ["name", "pop"]), [("a", 50.0), ("b", 50.0), ("far", 0.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.analysis.ApportionPolygon(tracts, "pop MEAN", blocks, self.out("am"))

    def test_assign_weights_normalises_columns_and_averages_rows(self):
        # a is twice b and four times c, b twice c: every column normalises to 4/7, 2/7, 1/7.
        out = arcpy.analysis.AssignWeightsByPairwiseComparison(["a", "b", "c"], self.out("w"), "ADD_MATRIX",
                                                               "a b 2;a c 4;b c 2").getOutput(0)
        got = self.rows(out, ["Variables", "a", "b", "c", "Weights"])
        self.assertEqual([r[:4] for r in got], [("a", 1.0, 2.0, 4.0), ("b", 0.5, 1.0, 2.0), ("c", 0.25, 0.5, 1.0)])
        for r, expected in zip(got, (4 / 7, 2 / 7, 1 / 7)):
            self.assertAlmostEqual(r[4], expected)
        equal = arcpy.analysis.AssignWeightsByPairwiseComparison(["a", "b"], self.out("e"), "WEIGHTS_ONLY").getOutput(0)
        self.assertEqual(self.rows(equal, ["Variables", "Weights"]), [("a", 0.5), ("b", 0.5)])

    def test_tools_refused_by_name_say_why(self):
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            arcpy.analysis.Enrich("a", "b")
        self.assertIn("group 3", str(raised.exception))
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            arcpy.analysis.SummarizeNearby("a", "b", "c", "DRIVING_TIME", [5])
        self.assertIn("network", str(raised.exception))


if __name__ == "__main__":
    unittest.main()
