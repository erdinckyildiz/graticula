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


if __name__ == "__main__":
    unittest.main()
