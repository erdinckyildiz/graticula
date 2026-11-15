"""arcpy.cartography and arcpy.edit answer the Pro sweep measured in ArcGIS Pro 3.7, 2026-11-08
(tools/pro-compare/sweep), each pinned by an example worked out by hand.

Everything is on EPSG:3857, so a distance of "N Meters" is N data units.
"""
import math
import os
import sys
import shutil
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402


def line(*xys):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(*xy) for xy in xys]), 3857)


def polygon(*xys):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(*xy) for xy in xys]), 3857)


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


class ProSweepCartographyEdit(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, arcpy.env, "workspace", arcpy.env.workspace)
        arcpy.env.workspace = self.directory
        self.addCleanup(setattr, arcpy.env, "overwriteOutput", arcpy.env.overwriteOutput)
        arcpy.env.overwriteOutput = True

    def make(self, name, shape, rows, fields=(("name", "TEXT"),)):
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

    def coords(self, path):
        out = []
        for (shape,) in self.rows(path, ["SHAPE@"]):
            g = shape._shape
            g = g.geoms[0] if hasattr(g, "geoms") else g
            out.append([tuple(round(v, 6) for v in c[:2]) for c in (g.exterior.coords if hasattr(g, "exterior")
                                                                   else g.coords)])
        return out

    def names(self, path):
        return [f.name for f in arcpy.ListFields(path) if f.type not in ("OID", "Geometry")]

    # ---- arcpy.edit

    def test_update_cogo_stores_the_span_between_the_ends(self):
        # A line (0,0)–(30,40)–(60,0) is 100 long, but its ends are 60 apart, due east: Distance 60, Direction 90.
        lines = self.make("cogo", "POLYLINE", [("a", line((0, 0), (30, 40), (60, 0)))])
        arcpy.management.EnableCOGO(lines)
        arcpy.edit.UpdateCOGO(lines)
        (direction, distance), = self.rows(lines, ["Direction", "Distance"])
        self.assertAlmostEqual(distance, 60.0)
        self.assertAlmostEqual(direction, 90.0)

    def test_trim_line_deletes_a_whole_dangle_under_either_option(self):
        # E runs along y = 0; D stands on it at x = 50 and is 20 long; F crosses it at x = 80 with 10 either side.
        # Dangle length 25: D is all dangle and goes under KEEP_SHORT too; F's two dangles meet at its middle, so
        # KEEP_SHORT keeps it whole and DELETE_SHORT deletes it. E loses its 20 m east end.
        def make(name):
            return self.make(name, "POLYLINE", [("e", line((0, 0), (100, 0))), ("d", line((50, 0), (50, 20))),
                                                ("f", line((80, -10), (80, 10)))])
        keep = make("keep")
        arcpy.edit.TrimLine(keep, "25 Meters", "KEEP_SHORT")
        self.assertEqual([r[0] for r in self.rows(keep, ["name"])], ["e", "f"])
        self.assertEqual(self.coords(keep)[0], [(0, 0), (80, 0)])
        delete = make("delete")
        arcpy.edit.TrimLine(delete, "25 Meters", "DELETE_SHORT")
        self.assertEqual([r[0] for r in self.rows(delete, ["name"])], ["e"])

    def test_align_features_takes_a_target_end_within_the_search_distance(self):
        # Input (0,0)–(100,0); target (2,4)–(102,4). Both target ends are within 10 of the input's: the input becomes
        # the whole target, not the stretch between the projections (0,4)–(100,4).
        inputs = self.make("in", "POLYLINE", [("a", line((0, 0), (100, 0)))])
        target = self.make("tg", "POLYLINE", [("t", line((2, 4), (102, 4)))])
        arcpy.edit.AlignFeatures(inputs, target, "10 Meters")
        self.assertEqual(self.coords(inputs), [[(2, 4), (102, 4)]])

    def test_rubbersheet_links_join_interior_vertices_by_their_share_of_the_length(self):
        # Source (0,0)–(100,0)–(200,0)–(300,0), 300 long; target (2,3)–(302,3), 300 long. The ends link to the ends;
        # the vertex a third along goes to the point a third along the target, (102,3); two thirds to (202,3).
        source = self.make("src", "POLYLINE", [("a", line((0, 0), (100, 0), (200, 0), (300, 0)))])
        target = self.make("tgt", "POLYLINE", [("a", line((2, 3), (302, 3)))])
        result = arcpy.edit.GenerateRubbersheetLinks(source, target, self.out("links"), "10 Meters")
        self.assertEqual(self.coords(result[0]), [[(0, 0), (2, 3)], [(300, 0), (302, 3)], [(100, 0), (102, 3)],
                                                  [(200, 0), (202, 3)]])
        self.assertEqual(self.rows(result[0], ["SRC_FID", "TGT_FID"]), [(1, 1)] * 4)
        self.assertEqual(result[1], "")

    def test_transfer_attributes_answers_the_match_table_first(self):
        sources = self.make("s", "POLYLINE", [("Main", line((0, 0), (10, 0)))])
        targets = self.make("t", "POLYLINE", [("old", line((0, 0.5), (10, 0.5)))])
        result = arcpy.edit.TransferAttributes(sources, targets, ["name"], "1 Meters")
        self.assertEqual((result.outputCount, result[0], result[1]), (2, "", targets))

    def test_edgematch_features_reads_pros_tgt_fid_links(self):
        # A link class as Pro writes it — SRC_FID, TGT_FID, EM_CONF — moves the source end (10,0) to (10.5,0.2).
        source = self.make("src", "POLYLINE", [("x", line((0, 0), (10, 0)))])
        links = self.make("links", "POLYLINE", [(1, 1, 100.0, line((10, 0), (10.5, 0.2)))],
                          fields=(("SRC_FID", "LONG"), ("TGT_FID", "LONG"), ("EM_CONF", "DOUBLE")))
        arcpy.edit.EdgematchFeatures(source, links, "MOVE_ENDPOINT")
        self.assertEqual(self.coords(source), [[(0, 0), (10.5, 0.2)]])

    # ---- arcpy.cartography

    def test_simplify_polygon_keeps_a_collapsing_outline_as_a_triangle(self):
        # A 4 × 4 square at a 3 m tolerance: Douglas–Peucker keeps only (0,0) and (4,4). Pro keeps three corners —
        # the first, the farthest from it, and the first of the two tied farthest from that diagonal.
        path = self.make("sq", "POLYGON", [("a", polygon((0, 0), (0, 4), (4, 4), (4, 0)))])
        out = arcpy.cartography.SimplifyPolygon(path, self.out("sp"), "POINT_REMOVE", "3 Meters")[0]
        (area,), = self.rows(out, ["SHAPE@AREA"])
        self.assertAlmostEqual(area, 8.0)
        self.assertEqual(self.names(out), ["name", "InPoly_FID", "MaxSimpTol", "MinSimpTol"])

    def test_simplify_building_keeps_a_rectangle_and_names_its_source(self):
        # A 40 × 30 rectangle is already its own minimum rectangle: BLD_STATUS 5 (unchanged), with InBld_FID 1. The
        # second output is the _Pnt path though nothing is kept, and the feature class is not created.
        path = self.make("b", "POLYGON", [("a", polygon((0, 0), (0, 30), (40, 30), (40, 0)))])
        result = arcpy.cartography.SimplifyBuilding(path, self.out("sb"), "5 Meters", "20 SquareMeters")
        self.assertEqual(self.rows(result[0], ["name", "InBld_FID", "BLD_STATUS"]), [("a", 1, 5)])
        self.assertTrue(result[1].endswith("sb_Pnt.gpkg"))
        self.assertFalse(arcpy.Exists(result[1]))

    def test_cartographic_partitions_start_from_a_square_and_carry_their_rank(self):
        # Points over x 10..190, y 7..133 (180 × 126): one partition, the 180 m square on the extent's centre (100, 70),
        # RANK 0 and STATUS null.
        points = self.make("p", "POINT", [(str(i), point(10 * i, 7 * i)) for i in range(1, 20)])
        out = arcpy.cartography.CreateCartographicPartitions(points, self.out("cp"), 500)[0]
        (rank, status, shape), = self.rows(out, ["RANK", "STATUS", "SHAPE@"])
        self.assertEqual((rank, status), (0, None))
        self.assertEqual(tuple(round(v, 6) for v in shape._shape.bounds), (10.0, -20.0, 190.0, 160.0))

    def test_collapse_dual_lines_names_the_left_casing_on_a_bent_road(self):
        # Casings 20 apart bending north-east: (0,0)–(200,0)–(400,60) and (0,20)–(200,20)–(400,80). Running east, the
        # upper casing (2) is on the left — though its middle lies below the centreline's chord, which made grapy say 1.
        path = self.make("cas", "POLYLINE", [("a", line((0, 0), (200, 0), (400, 60))),
                                             ("b", line((0, 20), (200, 20), (400, 80)))])
        out = arcpy.cartography.CollapseDualLinesToCenterline(path, self.out("cl"), "30 Meters")[0]
        (kind, left, right), = self.rows(out, ["LnType", "LeftLn_FID", "RightLn_FID"])
        self.assertEqual((kind, left, right), (1, 2, 1))
        ends = self.coords(out)[0]
        self.assertEqual((ends[0], ends[-1]), ((0, 10), (400, 70)))

    def test_strip_map_pages_name_previous_and_next(self):
        source = self.make("s", "POLYLINE", [("a", line((0, 0), (1000, 0)))])
        out = arcpy.cartography.StripMapIndexFeatures(source, self.out("strip"), "NO_USEPAGEUNIT", None, 300, 100)[0]
        self.assertEqual(self.rows(out, ["Previous", "Next"])[:2], [(None, 2), (1, 3)])


if __name__ == "__main__":
    unittest.main()
