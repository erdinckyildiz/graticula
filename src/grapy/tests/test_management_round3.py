"""grapy's third batch: feature datasets, values, pivots, time fields, comparisons, polygon parts, transects, mosaics,
raster statistics and pyramids, and KML — ADR-224. Every answer is worked out by hand from what is written here."""
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


def box(x0, y0, x1, y1):
    return f"(({x0} {y0},{x1} {y0},{x1} {y1},{x0} {y1},{x0} {y0}))"


class Round3(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def make(self, name, shape, fields, rows, srid=3857):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=srid)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def table(self, name, fields, rows):
        path = arcpy.management.CreateTable(self.directory, f"{name}.gpkg").getOutput(0)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields]) as written:
            for row in rows:
                written.insertRow(row)
        return path

    def out(self, name):
        return os.path.join(self.directory, name)

    def rows(self, path, fields, where=None):
        with arcpy.da.SearchCursor(path, fields, where) as read:
            return [tuple(r) for r in read]

    def raster(self, values, left=0.0, cell=10.0, nodata=None, name="r.tif"):
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        made = arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(left, 0), cell, cell, nodata)
        arcpy.env.outputCoordinateSystem = None
        return made.save(self.out(name))

    # ---- workspaces and values

    def test_a_feature_dataset_gives_its_classes_its_reference(self):
        gpkg = arcpy.management.CreateFileGDB(self.directory, "work")[0]
        dataset = arcpy.management.CreateFeatureDataset(gpkg, "transport", arcpy.SpatialReference(3857))[0]
        self.assertEqual(dataset, gpkg + "/transport")
        roads = arcpy.management.CreateFeatureclass(dataset, "roads", "POLYLINE")[0]
        self.assertEqual(arcpy.Describe(roads).spatialReference.factoryCode, 3857)
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.CreateFeatureclass(dataset, "rails", "POLYLINE", spatial_reference=4326)
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.CreateFeatureDataset(gpkg, "transport")

    def test_calculate_value_and_clear_workspace_cache(self):
        self.assertEqual(arcpy.management.CalculateValue("double(21)", "def double(x):\n    return x * 2", "Long")[0], "42")
        self.assertEqual(arcpy.management.CalculateValue("7 / 2", None, "Double")[0], "3.5")
        self.assertEqual(arcpy.management.ClearWorkspaceCache()[0], "")

    # ---- tables

    def test_pivot_table(self):
        source = self.table("src", [("owner", "TEXT"), ("year", "LONG"), ("val", "DOUBLE")],
                            [("a", 2020, 1.0), ("a", 2021, 2.0), ("b", 2020, 3.0)])
        out = arcpy.management.PivotTable(source, "owner", "year", "val", self.out("pivot.gpkg"))[0]
        self.assertEqual([f.name for f in arcpy.ListFields(out)][1:], ["owner", "year2020", "year2021"])
        self.assertEqual(self.rows(out, ["owner", "year2020", "year2021"]), [("a", 1.0, 2.0), ("b", 3.0, None)])
        tags = self.table("tags", [("id", "LONG"), ("tag", "TEXT"), ("v", "TEXT")], [(1, "colour", "red"), (1, "size", "big")])
        text = arcpy.management.PivotTable(tags, "id", "tag", "v", self.out("tags_p.gpkg"))[0]
        self.assertEqual(self.rows(text, ["id", "colour", "size"]), [(1, "red", "big")])
        twice = self.table("twice", [("owner", "TEXT"), ("year", "LONG"), ("val", "DOUBLE")], [("a", 2020, 1.0), ("a", 2020, 5.0)])
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.PivotTable(twice, "owner", "year", "val", self.out("bad.gpkg"))

    def test_convert_time_field(self):
        times = self.table("times", [("t", "TEXT"), ("n", "LONG")],
                           [("20261029", 0), ("29/10/2026 03:15 PM", 1700000000), ("not a date", None)])
        convert = arcpy.management.ConvertTimeField
        convert(times, "t", "yyyyMMdd", "d1")
        self.assertEqual([r[0] for r in self.rows(times, ["d1"])], ["2026-10-29T00:00:00", None, None])
        convert(times, "t", "dd/MM/yyyy hh:mm tt", "d2", "DATE")
        self.assertEqual(self.rows(times, ["d2"])[1], ("2026-10-29T15:15:00",))
        convert(times, "d2", None, "label", "TEXT", "MMM dd, yyyy")
        self.assertEqual(self.rows(times, ["label"])[1], ("Oct 29, 2026",))
        convert(times, "d1", None, "number", "LONG", "yyyyMMdd")
        self.assertEqual(self.rows(times, ["number"])[0], (20261029,))
        # 1 700 000 000 seconds after 1970-01-01 is 2023-11-14 22:13:20 UTC.
        convert(times, "n", "unix_s", "u")
        self.assertEqual([r[0] for r in self.rows(times, ["u"])], ["1970-01-01T00:00:00", "2023-11-14T22:13:20", None])
        with self.assertRaises(arcpy.NotBuiltError):
            convert(times, "t", "yyyyMMdd", "z", "DATE", None, "UTC")

    def test_table_and_feature_compare(self):
        base = self.make("base", "POLYGON", [("k", "LONG"), ("v", "DOUBLE")],
                         [(2, 1.0, "POLYGON" + box(0, 0, 10, 10)), (1, 2.0, "POLYGON" + box(20, 0, 30, 10))])
        same = self.make("same", "POLYGON", [("k", "LONG"), ("v", "DOUBLE")],
                         [(1, 2.0005, "POLYGON" + box(20, 0, 30, 10)), (2, 1.0, "POLYGON((10 10,0 10,0 0,10 0,10 10))")])
        moved = self.make("moved", "POLYGON", [("k", "LONG"), ("v", "DOUBLE")],
                          [(1, 2.0, "POLYGON" + box(20, 0, 30, 10)), (2, 1.0, "POLYGON" + box(0, 0, 10, 10.5))])
        compare = arcpy.management.FeatureCompare
        self.assertEqual(compare(base, same, "k", "ALL", attribute_tolerances="v 0.001")[1], "true")
        self.assertEqual(compare(base, same, "k", "ALL")[1], "false")                    # v differs by 0.0005
        self.assertEqual(compare(base, moved, "k", "ATTRIBUTES_ONLY")[1], "true")
        self.assertEqual(compare(base, moved, "k", "GEOMETRY_ONLY")[1], "false")         # a corner moved by 0.5
        self.assertEqual(compare(base, moved, "k", "GEOMETRY_ONLY", xy_tolerance="1 Meters")[1], "true")
        other = self.make("other", "POLYGON", [("k", "LONG"), ("v", "DOUBLE")], [], srid=4326)
        self.assertEqual(compare(base, other, "k", "SPATIAL_REFERENCE_ONLY")[1], "false")
        t1 = self.table("t1", [("k", "LONG"), ("name", "TEXT")], [(1, "a"), (2, "b")])
        t2 = self.table("t2", [("k", "LONG"), ("name", "TEXT")], [(2, "b"), (1, "a")])
        t3 = self.table("t3", [("k", "LONG"), ("label", "TEXT")], [(1, "a"), (2, "b")])
        self.assertEqual(arcpy.management.TableCompare(t1, t2, "k")[1], "true")
        self.assertEqual(arcpy.management.TableCompare(t1, t3, "k", "SCHEMA_ONLY")[1], "false")
        self.assertEqual(arcpy.management.TableCompare(t1, t3, "k", "ALL", omit_field="name;label")[1], "true")
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.TableCompare(t1, t2, "k", out_compare_file=self.out("c.txt"))

    # ---- polygons and lines

    def test_eliminate_polygon_part(self):
        # A 100 × 100 square with a 2 × 2 hole and a 20 × 20 hole holding a 1 × 1 island, and a 1 × 1 part apart.
        shape = (f"MULTIPOLYGON(((0 0,100 0,100 100,0 100,0 0),(10 10,12 10,12 12,10 12,10 10),(40 40,60 40,60 60,40 60,40 40)),"
                 f"{box(45, 45, 46, 46)},{box(200, 0, 201, 1)})")
        parcel = self.make("parts", "POLYGON", [("n", "TEXT")], [("x", shape)])
        contained = arcpy.management.EliminatePolygonPart(parcel, self.out("e1.gpkg"), "AREA", "10 SquareMeters")[0]
        # The small hole is filled (+4), the island inside the large hole goes (−1), the separate part stays.
        self.assertEqual(self.rows(contained, ["n", "SHAPE@AREA"]), [("x", 10000 - 400 + 1)])
        anywhere = arcpy.management.EliminatePolygonPart(parcel, self.out("e2.gpkg"), "AREA", 10, part_option="ANY")[0]
        self.assertEqual(self.rows(anywhere, ["SHAPE@AREA"]), [(10000 - 400,)])
        # 5 % of the total outer area (10 002) is 500.1: the 400 hole goes too.
        by_share = arcpy.management.EliminatePolygonPart(parcel, self.out("e3.gpkg"), "PERCENT", part_area_percent=5)[0]
        self.assertEqual(self.rows(by_share, ["SHAPE@AREA"]), [(10000 + 1,)])
        tiny = self.make("tiny", "POLYGON", [], [("MULTIPOLYGON(" + box(0, 0, 1, 1) + "," + box(5, 5, 7, 7) + ")",)])
        kept = arcpy.management.EliminatePolygonPart(tiny, self.out("e4.gpkg"), "AREA", 100, part_option="ANY")[0]
        self.assertEqual(self.rows(kept, ["SHAPE@AREA"]), [(4.0,)])             # every part small: the largest stays

    def test_transects_along_lines(self):
        line = self.make("line", "POLYLINE", [], [("LINESTRING(0 0,100 0)",)])
        out = arcpy.management.GenerateTransectsAlongLines(line, self.out("tr.gpkg"), "30 Meters", "20 Meters")[0]
        self.assertEqual(self.rows(out, ["ORIG_FID", "SHAPE@WKT"]),
                         [(1, "LINESTRING (30 10, 30 -10)"), (1, "LINESTRING (60 10, 60 -10)"), (1, "LINESTRING (90 10, 90 -10)")])
        ends = arcpy.management.GenerateTransectsAlongLines(line, self.out("tr2.gpkg"), 30, 20, "END_POINTS")[0]
        self.assertEqual([r[0] for r in self.rows(ends, ["SHAPE@WKT"])][::4], ["LINESTRING (0 10, 0 -10)", "LINESTRING (100 10, 100 -10)"])

    # ---- rasters

    def test_mosaic_into_an_existing_raster(self):
        target = self.raster([[1, 2], [3, 4]], name="target.tif")
        extra = self.raster([[10, 20], [30, 40]], left=10, name="extra.tif")    # its first column lies on the target's second
        arcpy.management.Mosaic(extra, target, "LAST")
        result = arcpy.Raster(target)
        self.assertEqual(result._array.tolist(), [[1, 10], [3, 30]])
        self.assertEqual((result.extent.XMin, result.extent.XMax), (0.0, 20.0))
        arcpy.management.Mosaic(extra, target, "SUM")
        self.assertEqual(arcpy.Raster(target)._array.tolist(), [[1, 20], [3, 60]])

    def test_statistics_and_pyramids_leave_the_cells(self):
        import rasterio
        path = self.raster([[1, 2, 3, 4], [5, 6, 7, 8]], name="s.tif")
        arcpy.management.CalculateStatistics(path, 2, 1)                       # columns 1 and 3: 1, 3, 5, 7
        with rasterio.open(path) as read:
            tags = read.tags(1)
        self.assertEqual((float(tags["STATISTICS_MINIMUM"]), float(tags["STATISTICS_MAXIMUM"]), float(tags["STATISTICS_MEAN"])),
                         (1.0, 7.0, 4.0))
        big = self.raster(np.arange(600 * 600).reshape(600, 600) % 7, cell=1.0, name="big.tif")
        arcpy.management.BuildPyramids(big)
        with rasterio.open(big) as read:
            self.assertEqual(read.overviews(1), [2, 4])                        # 300 is over 256, 150 is not
        self.assertEqual(int(arcpy.Raster(big)._array[599, 599]), (600 * 600 - 1) % 7)

    # ---- KML

    def test_kml_to_layer(self):
        kml = self.out("places.kml")
        with open(kml, "w", encoding="utf-8") as written:
            written.write('<?xml version="1.0"?><kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>Doc</name>'
                          '<Folder><name>Wells</name><Placemark><name>w1</name><description>deep</description>'
                          '<Point><coordinates>30,40,5</coordinates></Point></Placemark></Folder>'
                          '<Placemark><name>road</name><LineString><coordinates>0,0 3,4</coordinates></LineString></Placemark>'
                          '<Placemark><name>lot</name><Polygon><outerBoundaryIs><LinearRing><coordinates>0,0 10,0 10,10 0,10 0,0'
                          '</coordinates></LinearRing></outerBoundaryIs><innerBoundaryIs><LinearRing><coordinates>2,2 4,2 4,4 2,4 2,2'
                          '</coordinates></LinearRing></innerBoundaryIs></Polygon></Placemark>'
                          '<Placemark><name>pair</name><MultiGeometry><Point><coordinates>1,1</coordinates></Point>'
                          '<Point><coordinates>2,2</coordinates></Point></MultiGeometry></Placemark></Document></kml>')
        gpkg = arcpy.conversion.KMLToLayer(kml, self.directory)[0]
        self.assertEqual(gpkg, os.path.join(self.directory, "places.gpkg"))
        points = gpkg + "/Placemarks/Points"
        self.assertEqual(self.rows(points, ["Name", "FolderPath", "PopupInfo", "SHAPE@WKT"]),
                         [("w1", "Doc/Wells", "deep", "MULTIPOINT ((30 40))"), ("pair", "Doc", None, "MULTIPOINT ((1 1), (2 2))")])
        self.assertEqual(self.rows(gpkg + "/Placemarks/Lines", ["Name", "SHAPE@LENGTH"]), [("road", 5.0)])
        self.assertEqual(self.rows(gpkg + "/Placemarks/Polygons", ["Name", "SHAPE@AREA"]), [("lot", 96.0)])
        self.assertEqual(arcpy.Describe(points).spatialReference.factoryCode, 4326)


if __name__ == "__main__":
    unittest.main()
