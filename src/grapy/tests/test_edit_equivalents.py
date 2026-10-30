"""arcpy.edit's equivalents (ADR-224 §5.6), against hand-worked answers of the method each names."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.edit as edit  # noqa: E402


class EditEquivalents(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def make(self, name, shape, rows):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow((row,))
        return path

    def links(self, name, pairs):
        return self.make(name, "POLYLINE", [arcpy.Polyline(arcpy.Array([arcpy.Point(*a), arcpy.Point(*b)]), 3857)
                                            for a, b in pairs])

    def points(self, name, xys):
        return self.make(name, "POINT", [arcpy.PointGeometry(arcpy.Point(*xy), 3857) for xy in xys])

    def xy(self, path):
        with arcpy.da.SearchCursor(path, ["SHAPE@XY"]) as rows:
            return [tuple(round(v, 9) for v in r[0]) for r in rows]

    def test_rubbersheet_is_piecewise_linear_on_the_link_triangles(self):
        # Corners (0,0), (10,0), (0,10) carried to (0,0), (20,0), (0,10). (2,2) has barycentric weights .6, .2, .2, so
        # it goes to .2·(20,0) + .2·(0,10) = (4, 2). (50,50) is outside the triangle and stays.
        points = self.points("p", [(2, 2), (50, 50)])
        links = self.links("l", [((0, 0), (0, 0)), ((10, 0), (20, 0)), ((0, 10), (0, 10))])
        edit.RubbersheetFeatures(points, links)
        self.assertEqual(self.xy(points), [(4.0, 2.0), (50.0, 50.0)])
        # Two links and an identity point at (0,10): a shift of +1 in x at the two links' corners only. (2,2): weights
        # .6 on (0,0)→(1,0), .2 on (10,0)→(11,0), .2 on the identity (0,10): (.6 + 2.2, 2) = (2.8, 2).
        more = self.points("q", [(2, 2)])
        two = self.links("m", [((0, 0), (1, 0)), ((10, 0), (11, 0))])
        identity = self.points("i", [(0, 10)])
        edit.RubbersheetFeatures(more, two, identity)
        self.assertEqual(self.xy(more), [(2.8, 2.0)])
        with self.assertRaisesRegex(arcpy.NotBuiltError, "NATURAL_NEIGHBOR"):
            edit.RubbersheetFeatures(points, links, None, "NATURAL_NEIGHBOR")

    def named(self, name, rows):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POLYLINE", spatial_reference=3857)
        arcpy.management.AddField(path, "name", "TEXT")
        with arcpy.da.InsertCursor(path, ["name", "SHAPE@"]) as written:
            for label, xys in rows:
                written.insertRow((label, arcpy.Polyline(arcpy.Array([arcpy.Point(*xy) for xy in xys]), 3857)))
        return path

    def coords(self, path, fields=()):
        with arcpy.da.SearchCursor(path, [*fields, "SHAPE@"]) as rows:
            return [(*r[:-1], [tuple(round(v, 9) for v in c[:2]) for c in
                               (r[-1]._shape.geoms[0] if hasattr(r[-1]._shape, "geoms") else r[-1]._shape).coords])
                    for r in rows]

    def test_rubbersheet_links_from_matched_lines(self):
        # Source (0,0)–(10,0) matches target (0,1)–(10,1) (Hausdorff 1, within 2): links (0,0)→(0,1), (10,0)→(10,1).
        # Source (20,0)–(30,0) matches (20,0)–(30,0.5): (20,0) is an identity link, (30,0)→(30,0.5) a link.
        source = self.named("s", [("a", [(0, 0), (10, 0)]), ("b", [(20, 0), (30, 0)])])
        target = self.named("t", [("a", [(0, 1), (10, 1)]), ("b", [(20, 0), (30, 0.5)])])
        result = edit.GenerateRubbersheetLinks(source, target, os.path.join(self.directory, "links.gpkg"), 2)
        self.assertEqual(sorted(self.coords(result[0], ["SRC_FID", "TGT_FID"])),
                         [(1, 1, [(0, 0), (0, 1)]), (1, 1, [(10, 0), (10, 1)]), (2, 2, [(30, 0), (30, 0.5)])])
        self.assertEqual(self.xy(result[1]), [(20.0, 0.0)])

    def test_edgematch_links_and_features(self):
        # Source (0,0)–(5,0)–(10,0) and adjacent (10.5,0.2)–(20,0): the dangles (10,0) and (10.5,0.2) are 0.54 apart.
        source = self.named("src", [("x", [(0, 0), (5, 0), (10, 0)])])
        adjacent = self.named("adj", [("x", [(10.5, 0.2), (20, 0)])])
        links = edit.GenerateEdgematchLinks(source, adjacent, os.path.join(self.directory, "em.gpkg"), 1).getOutput(0)
        self.assertEqual(self.coords(links, ["SRC_FID", "ADJ_FID", "EM_CONF"]), [(1, 1, 100.0, [(10, 0), (10.5, 0.2)])])
        # With the adjacent features, both meet at the link's midpoint (10.25, 0.1).
        edit.EdgematchFeatures(source, links, "MOVE_ENDPOINT", adjacent)
        self.assertEqual(self.coords(source)[0][-1][-1], (10.25, 0.1))
        self.assertEqual(self.coords(adjacent)[0][-1][0], (10.25, 0.1))
        # ADJUST_VERTICES alone: the end moves to the link's end, shift (0.5, 0.2); (5,0), halfway along from the far
        # end, moves half as far: (5.25, 0.1); (0,0) stays.
        again = self.named("src2", [("x", [(0, 0), (5, 0), (10, 0)])])
        edit.EdgematchFeatures(again, links, "ADJUST_VERTICES")
        self.assertEqual(self.coords(again)[0][-1], [(0, 0), (5.25, 0.1), (10.5, 0.2)])

    def test_align_features_and_transfer_attributes(self):
        # (0,0.5)–(10,0.5) lies within 1 of the target (−5,0)–(15,0): it becomes the target's stretch (0,0)–(10,0), and
        # the line that ended at (10,0.5) follows to (10,0).
        inputs = self.named("in", [("a", [(0, 0.5), (10, 0.5)]), ("b", [(10, 0.5), (20, 5)])])
        target = self.named("tg", [("t", [(-5, 0), (15, 0)])])
        edit.AlignFeatures(inputs, target, 1)
        self.assertEqual(self.coords(inputs, ["AF_CONF"]), [(100.0, [(0, 0), (10, 0)]), (-1.0, [(10, 0), (20, 5)])])
        # Two sources within 1 of the target (0,0)–(10,0); the longer (11) gives its name, written as name_1.
        sources = self.named("src", [("Main", [(0, 0.5), (10, 0.5)]), ("Long", [(-0.5, -0.5), (10.5, -0.5)])])
        targets = self.named("dst", [("old", [(0, 0), (10, 0)])])
        edit.TransferAttributes(sources, targets, ["name"], 1)
        with arcpy.da.SearchCursor(targets, ["name", "name_1"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [("old", "Long")])


if __name__ == "__main__":
    unittest.main()
