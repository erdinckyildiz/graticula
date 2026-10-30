"""arcpy.analysis's equivalents (ADR-224 §5.6), against hand-worked answers of the method each names."""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class AnalysisEquivalents(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def polygons(self, name, boxes):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POLYGON", spatial_reference=3857)
        arcpy.management.AddField(path, "label", "TEXT")
        with arcpy.da.InsertCursor(path, ["label", "SHAPE@"]) as rows:
            for label, (x0, y0, x1, y1) in boxes:
                rows.insertRow((label, arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0),
                                                                  arcpy.Point(x1, y1), arcpy.Point(x0, y1)]), 3857)))
        return path

    def areas(self, path):
        with arcpy.da.SearchCursor(path, ["label", "SHAPE@AREA"]) as rows:
            return {label: area for label, area in rows}

    def test_remove_overlap_by_centre_line_and_by_thiessen(self):
        # A = [0,10]², B = [6,16]×[0,10]: the overlap [6,10] lies between A's own part (x ≤ 6) and B's (x ≥ 10), so its
        # centre line is x = 8: 80 each, to within the boundary spacing.
        path = self.polygons("ab", [("A", (0, 0, 10, 10)), ("B", (6, 0, 16, 10))])
        areas = self.areas(arcpy.analysis.RemoveOverlapMultiple(path, os.path.join(self.directory, "c.gpkg"))[0])
        self.assertAlmostEqual(areas["A"], 80.0, delta=0.5)
        self.assertAlmostEqual(areas["B"], 80.0, delta=0.5)
        # THIESSEN: B = [6,26]×[0,10] has its centroid at (16, 5), A at (5, 5); the bisector x = 10.5 leaves the whole
        # overlap [6,10] to A: A 100, B 200 − 40 = 160 — exactly.
        long_b = self.polygons("al", [("A", (0, 0, 10, 10)), ("B", (6, 0, 26, 10))])
        areas = self.areas(arcpy.analysis.RemoveOverlapMultiple(long_b, os.path.join(self.directory, "t.gpkg"),
                                                                "THIESSEN")[0])
        self.assertAlmostEqual(areas["A"], 100.0)
        self.assertAlmostEqual(areas["B"], 160.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "GRID"):
            arcpy.analysis.RemoveOverlapMultiple(path, os.path.join(self.directory, "g.gpkg"), "GRID")


if __name__ == "__main__":
    unittest.main()
