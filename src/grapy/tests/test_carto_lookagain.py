"""arcpy.cartography's names looked at again (ADR-224 round 2): the cartogram, partitions and strip-map pages that a
published method makes buildable, each checked against an answer worked by hand. The names kept refused are checked
for their own reasons."""
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.cartography  # noqa: E402

cartography = grapy.cartography


def square(x, y, size=1.0):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x, y + size), arcpy.Point(x + size, y + size),
                                      arcpy.Point(x + size, y), arcpy.Point(x, y)]), 3857)


class Base(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def path(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def make(self, name, kind, fields, rows):
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", kind, spatial_reference=3857)
        for field, ftype in fields:
            arcpy.management.AddField(self.path(name), field, ftype)
        with arcpy.da.InsertCursor(self.path(name), [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return self.path(name)


class Cartogram(Base):
    def run_method(self, method):
        # Two unit squares sharing the edge x = 1, valued 1 and 3: their shares of the 2 units of area are 0.5 and 1.5.
        # A third, valued 0, is omitted, as the page says.
        source = self.make("states", "POLYGON", [("name", "TEXT"), ("pop", "DOUBLE")],
                           [("a", 1.0, square(0, 0)), ("b", 3.0, square(1, 0)), ("c", 0.0, square(0, 1))])
        out = cartography.GenerateContiguousCartogram(source, "pop", self.path("carto_" + method.lower()), method)[0]
        with arcpy.da.SearchCursor(out, ["name", "pop", "SHAPE@"]) as rows:
            got = {r[0]: r[2] for r in rows}
        self.assertEqual(sorted(got), ["a", "b"])
        self.assertAlmostEqual(got["a"].area, 0.5, delta=0.5 * 0.03)
        self.assertAlmostEqual(got["b"].area, 1.5, delta=1.5 * 0.03)
        # The shared edge stays shared: the two outlines still meet along a line, and do not overlap.
        a, b = got["a"]._shape, got["b"]._shape
        self.assertGreater(a.intersection(b).length, 0.5)
        self.assertAlmostEqual(a.intersection(b).area, 0.0, delta=1e-6)

    def test_flow_based_makes_areas_proportional_and_keeps_the_shared_edge(self):
        self.run_method("FLOW-BASED")

    def test_diffusion_makes_areas_proportional_and_keeps_the_shared_edge(self):
        self.run_method("DIFFUSION")

    def test_it_is_marked_an_equivalent(self):
        self.assertIn("Gastner", cartography.GenerateContiguousCartogram.__grapy_equivalent__)

    def test_an_unknown_method_is_an_error(self):
        source = self.make("one", "POLYGON", [("pop", "DOUBLE")], [(1.0, square(0, 0))])
        with self.assertRaises(ValueError):
            cartography.GenerateContiguousCartogram(source, "pop", self.path("x"), "RUBBER_SHEET")


class Partitions(Base):
    def points(self, name, xys):
        return self.make(name, "POINT", [], [(arcpy.PointGeometry(arcpy.Point(x, y), 3857),) for x, y in xys])

    @staticmethod
    def lattice(x0, y0, nx, ny, dx, dy):
        return [(x0 + dx * (i + 0.5), y0 + dy * (j + 0.5)) for i in range(nx) for j in range(ny)]

    def cells(self, path):
        with arcpy.da.SearchCursor(path, ["SHAPE@"]) as rows:
            return sorted(tuple(round(v, 6) for v in r[0]._shape.bounds) for r in rows)

    def test_a_cell_with_more_than_the_count_is_split_in_four(self):
        # Two anchors fix the extent to [0, 100]². Each quadrant holds a 20×20 lattice (400 points), except the
        # south-west, which holds a 20×40 lattice (800): root 1602 > 500 → four quadrants; the south-west has 801
        # (with the anchor at 0,0) → four sub-quadrants of 200 (201 with the anchor). 3 + 4 = 7 partitions.
        xys = [(0, 0), (100, 100)]
        xys += self.lattice(0, 0, 20, 40, 2.5, 1.25)
        for x0, y0 in ((50, 0), (0, 50), (50, 50)):
            xys += self.lattice(x0, y0, 20, 20, 2.5, 2.5)
        source = self.points("pts", xys)
        out = cartography.CreateCartographicPartitions(source, self.path("parts"), 500)[0]
        self.assertEqual(self.cells(out), sorted([
            (0, 0, 25, 25), (25, 0, 50, 25), (0, 25, 25, 50), (25, 25, 50, 50),
            (50, 0, 100, 50), (0, 50, 50, 100), (50, 50, 100, 100)]))

    def test_vertices_counts_vertices_not_features(self):
        # One line of 1,200 vertices along y = 0..1199 at x alternating 0 and 1: one feature, 1,200 vertices.
        line = arcpy.Polyline(arcpy.Array([arcpy.Point(i % 2, i) for i in range(1200)]), 3857)
        source = self.make("ln", "POLYLINE", [], [(line,)])
        one = cartography.CreateCartographicPartitions(source, self.path("by_features"), 500, "FEATURES")[0]
        self.assertEqual(len(self.cells(one)), 1)
        many = cartography.CreateCartographicPartitions(source, self.path("by_vertices"), 500, "VERTICES")[0]
        self.assertGreater(len(self.cells(many)), 1)

    def test_the_minimum_count_is_500(self):
        source = self.points("few", [(0, 0), (1, 1)])
        with self.assertRaises(ValueError):
            cartography.CreateCartographicPartitions(source, self.path("bad"), 499)


class StripMap(Base):
    FIELDS = ["PageNumber", "GroupId", "SeqId", "PrevPage", "NextPage", "LeftPage", "RightPage", "TopPage",
              "BottomPage", "Angle"]

    def line(self, name, *xys):
        shape = arcpy.Polyline(arcpy.Array([arcpy.Point(*xy) for xy in xys]), 3857)
        return self.make(name, "POLYLINE", [], [(shape,)])

    def pages(self, path):
        with arcpy.da.SearchCursor(path, [*self.FIELDS, "SHAPE@"]) as rows:
            return [dict(zip([*self.FIELDS, "shape"], r)) for r in rows]

    def test_an_east_running_line_is_paged_west_to_east_with_overlap(self):
        # 1,000 long, pages 300 × 100, 10% overlap: starts at 0, 270, 540, 810; the page from 810 reaches the end.
        source = self.line("eastward", (0, 0), (1000, 0))
        out = cartography.StripMapIndexFeatures(source, self.path("strip"), "NO_USEPAGEUNIT", None, 300, 100,
                                                "HORIZONTAL", 10, 5, "WE_NS")[0]
        pages = self.pages(out)
        self.assertEqual([p["PageNumber"] for p in pages], [5, 6, 7, 8])
        self.assertEqual([p["SeqId"] for p in pages], [1, 2, 3, 4])
        for page, x in zip(pages, (0, 270, 540, 810)):
            x0, y0, x1, y1 = page["shape"]._shape.bounds
            self.assertAlmostEqual(x0, x, delta=1e-6)
            self.assertAlmostEqual(x1, x + 300, delta=1e-6)
            self.assertAlmostEqual(y0, -50, delta=1e-6)
            self.assertAlmostEqual(y1, 50, delta=1e-6)
            self.assertAlmostEqual(page["Angle"], 0.0, delta=1e-9)
        self.assertEqual((pages[0]["PrevPage"], pages[0]["NextPage"]), (None, 6))
        self.assertEqual((pages[0]["LeftPage"], pages[0]["RightPage"]), (None, 6))
        self.assertEqual((pages[1]["LeftPage"], pages[1]["RightPage"]), (5, 7))
        self.assertEqual((pages[3]["NextPage"], pages[3]["RightPage"]), (None, None))

    def test_east_to_west_starts_at_the_east_end(self):
        source = self.line("eastward2", (0, 0), (1000, 0))
        out = cartography.StripMapIndexFeatures(source, self.path("strip_ew"), "NO_USEPAGEUNIT", None, 300, 100,
                                                "HORIZONTAL", 10, 1, "EW_SN")[0]
        first = self.pages(out)[0]["shape"]._shape.bounds
        self.assertAlmostEqual(first[2], 1000, delta=1e-6)
        self.assertAlmostEqual(first[0], 700, delta=1e-6)
        # Running west, the page's top would face south (azimuth 270 − 90 = 180): folded, north stays up — Angle 0,
        # and the next page, to the west, is on the left.
        page = self.pages(out)[0]
        self.assertAlmostEqual(page["Angle"], 0.0, delta=1e-9)
        self.assertEqual((page["LeftPage"], page["RightPage"]), (2, None))

    def test_a_north_south_line_starts_north_and_turns_the_page(self):
        # WE_NS on a line trending north–south starts at its north end and runs south (azimuth 180). A horizontal
        # page's top then faces east: Angle 90. Its right faces south, where the next page is.
        source = self.line("southward", (0, 0), (0, 1000))
        out = cartography.StripMapIndexFeatures(source, self.path("strip_ns"), "NO_USEPAGEUNIT", None, 300, 100,
                                                "HORIZONTAL", 10, 1, "WE_NS")[0]
        pages = self.pages(out)
        self.assertAlmostEqual(pages[0]["shape"]._shape.bounds[3], 1000, delta=1e-6)
        self.assertAlmostEqual(pages[0]["Angle"], 90.0, delta=1e-9)
        self.assertEqual(pages[0]["RightPage"], 2)
        # VERTICAL: the line runs down the page, so its top faces north: Angle 0, the next page at the bottom.
        out = cartography.StripMapIndexFeatures(source, self.path("strip_nsv"), "NO_USEPAGEUNIT", None, 300, 100,
                                                "VERTICAL", 10, 1, "WE_NS")[0]
        page = self.pages(out)[0]
        self.assertAlmostEqual(page["Angle"], 0.0, delta=1e-9)
        self.assertEqual(page["BottomPage"], 2)

    def test_defaults_follow_the_page(self):
        # Along: 1/100 of the extent's width = 10; across: half of that = 5.
        source = self.line("short", (0, 0), (1000, 0))
        out = cartography.StripMapIndexFeatures(source, self.path("strip_default"))[0]
        x0, y0, x1, y1 = self.pages(out)[0]["shape"]._shape.bounds
        self.assertAlmostEqual(x1 - x0, 10, delta=1e-6)
        self.assertAlmostEqual(y1 - y0, 5, delta=1e-6)

    def test_page_units_are_inches_at_the_scale(self):
        # 2 inches at 1:1000 is 50.8 m.
        source = self.line("pu", (0, 0), (1000, 0))
        out = cartography.StripMapIndexFeatures(source, self.path("strip_pu"), "USEPAGEUNIT", 1000, 2, 1)[0]
        x0, _, x1, _ = self.pages(out)[0]["shape"]._shape.bounds
        self.assertAlmostEqual(x1 - x0, 50.8, delta=1e-6)

    def test_unconnected_lines_are_separate_groups(self):
        a = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(100, 0)]), 3857)
        b = arcpy.Polyline(arcpy.Array([arcpy.Point(0, 500), arcpy.Point(100, 500)]), 3857)
        source = self.make("two", "POLYLINE", [], [(a,), (b,)])
        out = cartography.StripMapIndexFeatures(source, self.path("strip_two"), "NO_USEPAGEUNIT", None, 60, 20)[0]
        pages = self.pages(out)
        self.assertEqual(sorted({p["GroupId"] for p in pages}), [1, 2])
        self.assertEqual([p["PageNumber"] for p in pages], list(range(1, len(pages) + 1)))


class KeptRefused(unittest.TestCase):
    def test_each_keeps_its_own_reason(self):
        expected = {
            "CollapseHydroPolygon": "centreline method",
            "CollapseRoadDetail": "road detail",
            "MergeDividedRoads": "generally parallel",
            "ThinRoadNetwork": "long journeys",
            "GenerateHachuresForDefinedSlopes": "even spacing",
        }
        for name, words in expected.items():
            with self.assertRaises(arcpy.NotBuiltError if hasattr(arcpy, "NotBuiltError")
                                   else NotImplementedError) as said:
                getattr(cartography, name)()
            self.assertIn(words, str(said.exception), name)
            self.assertNotIn(" yet", str(said.exception), name)


if __name__ == "__main__":
    unittest.main()
