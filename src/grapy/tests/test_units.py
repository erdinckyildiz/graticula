"""D-323: grapy's units are the sizes ArcGIS Pro 3.7 measured on 2026-10-30, tool by tool.

Every expected number below was read from Pro itself — a tool run on a geometry of known size in UTM 35N — not worked
out from a definition, so a test here fails when grapy stops answering what Pro answers. The shapes are a thousand
kilometres across so that a US survey foot and an international one (two parts in a million apart) cannot be confused.
"""
import os
import re
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _network, _units, analysis, sa  # noqa: E402

SR = 32635
X0, Y0, D = 300000.0, 3000000.0, 1_000_000.0

# As Pro printed them.
FOOT_US = 0.30480060960121924
MILE_US = 1609.3472186944375
YARD_US = 0.9144018288036577
INCH_US = 0.025400050800101603
NAUTICAL_MILE_US = 1853.248
POINT = 0.0003527784833347445
ACRE_INT = 4046.8564224
ACRE_US = 4046.872609874252
SQUARE_FOOT_INT = 0.09290304
SQUARE_FOOT_US = 0.09290341161327487
SQUARE_MILE_INT = 2589988.11
SQUARE_MILE_US = 2589998.4703195216


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), SR)


def square(x, y, side):
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x, y), arcpy.Point(x, y + side), arcpy.Point(x + side, y + side),
                                      arcpy.Point(x + side, y)]), SR)


def line(x0, y0, x1, y1):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y1)]), SR)


class Units(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def make(self, name, shape, rows, fields=(), z=False):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=SR,
                                            has_z="ENABLED" if z else "DISABLED")
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def out(self, name):
        return os.path.join(self.directory, f"{name}.gpkg")

    def first(self, path, field):
        with arcpy.da.SearchCursor(path, [field]) as read:
            return max(r[0] for r in read)

    # ---- the linear unit, the factor functions and the geometry methods

    def test_buffer_reads_a_bare_feet_as_the_us_survey_foot(self):
        pts = self.make("p", "POINT", [(point(X0, Y0),)])
        for i, (word, radius) in enumerate([("Feet", 304800.6096), ("feet", 304800.6096), ("Foot", 304800.6096),
                                            ("FeetInt", 304800.0), ("FeetUS", 304800.6096)]):
            out = arcpy.analysis.Buffer(pts, self.out(f"b{i}"), f"1000000 {word}").getOutput(0)
            with arcpy.da.SearchCursor(out, ["SHAPE@"]) as read:
                shape = next(read)[0]
            far = max(((x - X0) ** 2 + (y - Y0) ** 2) ** 0.5 for x, y in shape._shape.exterior.coords)
            self.assertAlmostEqual(far, radius, delta=1e-3, msg=word)
        with self.assertRaises(ValueError):
            arcpy.analysis.Buffer(pts, self.out("bad"), "10 Furlongs")

    def test_linear_unit_words(self):
        for word, metres in [("Miles", MILE_US), ("MilesInt", 1609.344), ("Yards", YARD_US), ("Inches", INCH_US),
                             ("NauticalMiles", NAUTICAL_MILE_US), ("NauticalMilesInt", 1852.0), ("Points", POINT)]:
            self.assertAlmostEqual(analysis._distance(f"1 {word}", SR), metres, places=12, msg=word)

    def test_conversion_factors(self):
        self.assertAlmostEqual(arcpy.LinearUnitConversionFactor("Feet", "Meters"), FOOT_US, places=15)
        self.assertAlmostEqual(arcpy.LinearUnitConversionFactor("Points", "Meters"), POINT, places=17)
        self.assertAlmostEqual(arcpy.LinearUnitConversionFactor("NauticalMiles", "Meters"), NAUTICAL_MILE_US)
        self.assertAlmostEqual(arcpy.LinearUnitConversionFactor("NauticalMilesUS", "Meters"), NAUTICAL_MILE_US)
        self.assertAlmostEqual(arcpy.LinearUnitConversionFactor("NauticalMilesInt", "Meters"), 1852.0)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("SquareFeet", "SquareMeters"), SQUARE_FOOT_INT)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("SquareFeetUS", "SquareMeters"), SQUARE_FOOT_US)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("Acres", "SquareMeters"), ACRE_INT)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("AcresUS", "SquareMeters"), ACRE_US, places=9)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("SquareMiles", "SquareMeters"), SQUARE_MILE_INT)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("SquareNauticalMiles", "SquareMeters"), 3429904.0)
        self.assertAlmostEqual(arcpy.ArealUnitConversionFactor("SquareNauticalMilesUS", "SquareMeters"), 3434528.149504)
        with self.assertRaises(ValueError):
            arcpy.ArealUnitConversionFactor("SquareNauticalMilesInt", "SquareMeters")

    def test_geometry_methods(self):
        length = line(X0, Y0, X0 + D, Y0)
        self.assertAlmostEqual(D / length.getLength("PLANAR", "FEET"), FOOT_US, places=15)
        self.assertAlmostEqual(D / length.getLength("PLANAR", "NAUTICALMILES"), NAUTICAL_MILE_US, places=9)
        for refused in ("POINTS", "NAUTICALMILESUS"):
            with self.assertRaises(ValueError):
                length.getLength("PLANAR", refused)
        area = square(X0, Y0, D)
        self.assertAlmostEqual(D * D / area.getArea("PLANAR", "ACRES"), ACRE_INT, places=7)
        self.assertAlmostEqual(D * D / area.getArea("PLANAR", "SQUAREFEET"), SQUARE_FOOT_INT, places=12)
        self.assertAlmostEqual(D * D / area.getArea("PLANAR", "SQUAREMILES"), SQUARE_MILE_INT, places=3)
        self.assertAlmostEqual(D * D / area.getArea("PLANAR", "ACRESUS"), ACRE_US, places=7)
        with self.assertRaises(ValueError):
            area.getArea("PLANAR", "SQUARENAUTICALMILES")

    # ---- each tool's own keywords

    def test_near_and_generate_near_table(self):
        a = self.make("a", "POINT", [(point(X0, Y0),)])
        b = self.make("b", "POINT", [(point(X0 + D, Y0),)])
        for word, metres in [("Feet", FOOT_US), ("FeetInt", 0.3048), ("Miles", MILE_US), ("MilesInt", 1609.344),
                             ("NauticalMiles", NAUTICAL_MILE_US), ("NauticalMilesInt", 1852.0)]:
            arcpy.analysis.Near(a, b, distance_unit=word)
            self.assertAlmostEqual(D / self.first(a, "NEAR_DIST"), metres, places=9, msg=word)
            table = arcpy.analysis.GenerateNearTable(a, b, self.out("nt" + word), distance_unit=word).getOutput(0)
            self.assertAlmostEqual(D / self.first(table, "NEAR_DIST"), metres, places=9, msg=word)

    def test_origin_destination_links(self):
        a = self.make("a", "POINT", [(point(X0, Y0),)])
        b = self.make("b", "POINT", [(point(X0 + D, Y0),)])
        for word, metres in [("FEET", FOOT_US), ("MILES", MILE_US), ("NAUTICALMILES", NAUTICAL_MILE_US)]:
            out = arcpy.analysis.GenerateOriginDestinationLinks(a, b, self.out("od" + word), distance_unit=word)
            self.assertAlmostEqual(D / self.first(out.getOutput(0), "LINK_DIST"), metres, places=9, msg=word)

    def test_polygon_neighbors_and_tabulate_intersection(self):
        touching = self.make("t", "POLYGON", [(square(X0, Y0, D),), (square(X0 + D, Y0, D),)])
        overlapping = self.make("o", "POLYGON", [(square(X0, Y0, D),), (square(X0 + D / 2, Y0, D),)])
        for word, metres in [("FEET", FOOT_US), ("FEET_INTERNATIONAL", 0.3048), ("POINTS", POINT)]:
            out = arcpy.analysis.PolygonNeighbors(touching, self.out("pn" + word), out_linear_units=word).getOutput(0)
            self.assertAlmostEqual(D / self.first(out, "LENGTH"), metres, places=12, msg=word)
        for word, square_metres in [("ACRES", ACRE_INT), ("ACRES_US", ACRE_US), ("SQUARE_MILES", SQUARE_MILE_INT),
                                    ("SQUARE_FEET_US", SQUARE_FOOT_US)]:
            out = arcpy.analysis.PolygonNeighbors(overlapping, self.out("pa" + word), area_overlap="AREA_OVERLAP",
                                                  out_area_units=word).getOutput(0)
            self.assertAlmostEqual(D * D / 2 / self.first(out, "AREA") / square_metres, 1.0, places=12, msg=word)
        zone = self.make("z", "POLYGON", [(1, square(X0, Y0, D))], [("ZID", "LONG")])
        cls = self.make("c", "POLYGON", [(square(X0, Y0, D),)])
        for word, square_metres in [("SQUARE_FEET", SQUARE_FOOT_INT), ("SQUARE_FEET_US", SQUARE_FOOT_US)]:
            out = arcpy.analysis.TabulateIntersection(zone, "ZID", cls, self.out("ti" + word), out_units=word)
            self.assertAlmostEqual(D * D / self.first(out.getOutput(0), "AREA") / square_metres, 1.0, places=12)

    def test_calculate_and_add_geometry_attributes(self):
        poly = self.make("g", "POLYGON", [(square(X0, Y0, D),)])
        for word, metres in [("YARDS", YARD_US), ("YARDS_INT", 0.9144), ("NAUTICAL_MILES", NAUTICAL_MILE_US)]:
            arcpy.management.CalculateGeometryAttributes(poly, [["P", "PERIMETER_LENGTH"]], length_unit=word)
            self.assertAlmostEqual(4 * D / self.first(poly, "P"), metres, places=9, msg=word)
        for word, square_metres in [("ACRES", ACRE_INT), ("SQUARE_YARDS", 0.83612736), ("ACRES_US", ACRE_US)]:
            arcpy.management.CalculateGeometryAttributes(poly, [["AR", "AREA"]], area_unit=word)
            self.assertAlmostEqual(D * D / self.first(poly, "AR") / square_metres, 1.0, places=12, msg=word)
        for word, metres in [("YARDS", YARD_US), ("NAUTICAL_MILES", NAUTICAL_MILE_US)]:
            poly = self.make("l" + word, "POLYGON", [(square(X0, Y0, D),)])
            arcpy.management.AddGeometryAttributes(poly, "PERIMETER_LENGTH", Length_Unit=word)
            self.assertAlmostEqual(4 * D / self.first(poly, "PERIMETER"), metres, places=9, msg=word)
        for word, square_metres in [("ACRES", ACRE_INT), ("SQUARE_YARDS", 0.83612736), ("SQUARE_NAUTICAL_MILES", 1852.0 ** 2)]:
            poly = self.make("a" + word, "POLYGON", [(square(X0, Y0, D),)])
            arcpy.management.AddGeometryAttributes(poly, "AREA", Area_Unit=word)
            self.assertAlmostEqual(D * D / self.first(poly, "POLY_AREA") / square_metres, 1.0, places=12, msg=word)

    def test_adjust_3d_z(self):
        for word, metres in [("FEET", FOOT_US), ("INCHES", INCH_US), ("FATHOMS", 1.8288036576)]:
            z = self.make("z" + word, "POINT", [(arcpy.PointGeometry(arcpy.Point(X0, Y0, 1e6), SR, True),)], z=True)
            arcpy.management.Adjust3DZ(z, "NO_REVERSE", 0, word, "METERS")
            with arcpy.da.SearchCursor(z, ["SHAPE@Z"]) as read:
                # Pro's own run stores z to 0.1 mm, so its fathom printed as 1.8288036576; nine places are its digits.
                self.assertAlmostEqual(next(read)[0] / 1e6, metres, places=9, msg=word)

    def test_bearing_distance_units(self):
        # BearingDistanceToLine and TableToEllipse: FEET is international beside US_SURVEY_FEET, MILES a US survey mile.
        from grapy import management
        for word, metres in [("FEET", 0.3048), ("US_SURVEY_FEET", FOOT_US), ("MILES", MILE_US),
                             ("NAUTICAL_MILES", 1852.0)]:
            self.assertAlmostEqual(management._metres(word, "BearingDistanceToLine", _units.BEARING_DISTANCE), metres,
                                   places=9, msg=word)

    def test_areal_unit_sizes_a_tessellation(self):
        extent = arcpy.Extent(X0, Y0, X0 + 3 * D, Y0 + 3 * D)
        for word, n, square_metres in [("Acres", 2.5e8, ACRE_INT), ("AcresUS", 2.5e8, ACRE_US),
                                       ("SquareMiles", 4e5, SQUARE_MILE_INT)]:
            out = arcpy.management.GenerateTessellation(self.out("tess" + word), extent, "SQUARE", f"{n:.0f} {word}",
                                                        SR).getOutput(0)
            self.assertAlmostEqual(self.first(out, "SHAPE@AREA") / n / square_metres, 1.0, places=9, msg=word)

    def test_density_area_units_are_us_survey(self):
        for word, square_metres in [("SQUARE_FEET", SQUARE_FOOT_US), ("ACRES", ACRE_US), ("SQUARE_MILES", SQUARE_MILE_US)]:
            self.assertAlmostEqual(sa._area_unit(word, SR, "PointDensity") / square_metres, 1.0, places=12, msg=word)

    def test_network_distance_units_are_us_survey(self):
        # A route of a known 1,000 km in Pro: Total_Miles, Total_Feet … all in US survey units, and a network attribute
        # in Miles read the same way.
        for word, metres in [("MILES", MILE_US), ("FEET", FOOT_US), ("YARDS", YARD_US), ("INCHES", INCH_US),
                             ("NAUTICALMILES", NAUTICAL_MILE_US)]:
            self.assertAlmostEqual(_network._LINEAR[word], metres, places=12, msg=word)

    def test_summarize_within_keeps_the_servers_units(self):
        # Q-193: grapy's SummarizeWithin is the server's, so its sizes are the server's own table, read from its source.
        source = os.path.join(os.path.dirname(__file__), "..", "..", "Graticula.Host", "AnalysisEndpoints.cs")
        if not os.path.exists(source):
            self.skipTest("the server's source is not beside grapy")
        text = open(source, encoding="utf-8").read()

        def table(name):
            block = text[text.index(name + " = new"):]
            block = block[:block.index("};")]
            out = {}
            for key, value in re.findall(r'\["(\w+)"\]\s*=\s*([\d_.]+(?:\s*/\s*[\d_.]+)?),', block):
                parts = [float(p.replace("_", "")) for p in value.split("/")]
                out[key.upper()] = parts[0] / parts[1] if len(parts) == 2 else parts[0]
            return out

        lengths, areas = table("SummaryLengthUnits"), table("AreaUnits")
        for key, (_, size) in analysis._SUM_LENGTH_UNITS.items():
            self.assertAlmostEqual(size, lengths[key], places=12, msg=key)
        for key, (_, size) in analysis._SUM_AREA_UNITS.items():
            self.assertAlmostEqual(size, areas[key], places=12, msg=key)
        # Pro's own, measured 2026-10-30: a summary's feet is the US survey foot.
        self.assertAlmostEqual(lengths["FEET"], 1200 / 3937, places=15)


if __name__ == "__main__":
    unittest.main()
