"""grapy.management's second batch — field mappings, domains, random points and raster management — ADR-224.

Every answer is worked out by hand from the coordinates and values written here, never read back from the code under
test.
"""
import os
import sqlite3
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy.management import FieldMap, FieldMappings  # noqa: E402 — exported at the top level by the coordinator


def box(x0, y0, x1, y1):
    return f"POLYGON(({x0} {y0},{x1} {y0},{x1} {y1},{x0} {y1},{x0} {y0}))"


class Round2(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        self.parcels = self.make("parcels", "POLYGON", [("name", "TEXT"), ("kind", "TEXT"), ("value", "DOUBLE"),
                                                        ("extra", "DOUBLE")],
                                 [("p0", "a", 1.0, 10.0, box(0, 0, 50, 50)), ("p1", "a", 2.0, None, box(100, 0, 150, 50)),
                                  ("p2", "b", 4.0, 30.0, box(200, 0, 250, 50))])

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None
        if hasattr(arcpy.env, "randomGenerator"):
            del arcpy.env.randomGenerator

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

    def raster(self, values, left=0.0, bottom=0.0, cell=10.0, nodata=None, name=None):
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
        made = arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(left, bottom), cell, cell, nodata)
        arcpy.env.outputCoordinateSystem = None
        return made.save(self.out(name or f"r{len(os.listdir(self.directory))}.tif"))

    def cells(self, path):
        raster = arcpy.Raster(path)
        return [[None if m else v for v, m in zip(r, k)] for r, k in zip(raster._array.tolist(), raster._mask.tolist())]

    # ---- field mappings

    def test_merge_and_append_follow_field_mappings(self):
        others = self.make("others", "POLYGON", [("title", "TEXT"), ("value", "DOUBLE")], [("x", 9.0, box(300, 0, 350, 50))])
        mappings = FieldMappings()
        mappings.addTable(self.parcels)
        mappings.addTable(others)
        self.assertEqual([f.name for f in mappings.fields], ["name", "kind", "value", "extra", "title"])
        name_map = mappings.getFieldMap(mappings.findFieldMapIndex("name"))
        name_map.addInputField(others, "title")
        output = name_map.outputField
        output.name = "label"
        name_map.outputField = output
        mappings.replaceFieldMap(mappings.findFieldMapIndex("name"), name_map)
        mappings.removeFieldMap(mappings.findFieldMapIndex("title"))
        mappings.removeFieldMap(mappings.findFieldMapIndex("kind"))
        total = mappings.getFieldMap(mappings.findFieldMapIndex("extra"))
        total.addInputField(self.parcels, "value")
        total.mergeRule = "Sum"
        mappings.replaceFieldMap(mappings.findFieldMapIndex("extra"), total)
        merged = arcpy.management.Merge([self.parcels, others], self.out("m.gpkg"), mappings)[0]
        self.assertEqual([f.name for f in arcpy.ListFields(merged)][1:-1], ["label", "value", "extra"])
        # extra is extra + value of each parcel, nulls left out; the other input has no extra and no value input there.
        self.assertEqual(self.rows(merged, ["label", "value", "extra"]),
                         [("p0", 1.0, 11.0), ("p1", 2.0, 2.0), ("p2", 4.0, 34.0), ("x", 9.0, None)])
        joined = FieldMap()
        joined.addInputField(self.parcels, "name")
        joined.addInputField(self.parcels, "kind")
        joined.mergeRule = "Join"
        joined.joinDelimiter = "/"
        both = FieldMappings()
        both.addFieldMap(joined)
        target = self.make("target", "POLYGON", [("name", "TEXT"), ("value", "DOUBLE")], [])
        arcpy.management.Append(self.parcels, target, "NO_TEST", both)
        self.assertEqual(self.rows(target, ["name", "value"]), [("p0/a", None), ("p1/a", None), ("p2/b", None)])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.Merge([self.parcels, others], self.out("s.gpkg"), 'name "name" true true false 50 Text 0 0,First,#')

    # ---- domains

    def test_coded_and_range_domains_live_in_the_geopackage_schema_extension(self):
        mg = arcpy.management
        mg.CreateDomain(self.parcels, "kinds", "Parcel kinds", "TEXT", "CODED")
        mg.AddCodedValueToDomain(self.parcels, "kinds", "a", "Agricultural")
        mg.AddCodedValueToDomain(self.parcels, "kinds", "b", "Built")
        with self.assertRaises(arcpy.ExecuteError):
            mg.AddCodedValueToDomain(self.parcels, "kinds", "a", "again")
        mg.AssignDomainToField(self.parcels, "kind", "kinds")
        connection = sqlite3.connect(self.parcels)
        self.assertEqual(connection.execute("select table_name, column_name, constraint_name from gpkg_data_columns").fetchall(),
                         [("parcels", "kind", "kinds")])
        self.assertEqual(connection.execute("select value, description from gpkg_data_column_constraints where "
                                            "constraint_type = 'enum' order by rowid").fetchall(),
                         [("a", "Agricultural"), ("b", "Built")])
        self.assertEqual(connection.execute("select table_name from gpkg_extensions where extension_name = 'gpkg_schema' "
                                            "order by table_name").fetchall(),
                         [("gpkg_data_column_constraints",), ("gpkg_data_columns",)])
        connection.close()
        mg.SortCodedValueDomain(self.parcels, "kinds", "DESCRIPTION", "DESCENDING")
        table = mg.DomainToTable(self.parcels, "kinds", self.out("kinds.gpkg"), "code", "label")[0]
        self.assertEqual(self.rows(table, ["code", "label"]), [("b", "Built"), ("a", "Agricultural")])
        with self.assertRaises(arcpy.ExecuteError):
            mg.DeleteDomain(self.parcels, "kinds")                     # still on parcels.kind
        mg.DeleteCodedValueFromDomain(self.parcels, "kinds", "b")
        self.assertEqual(self.rows(mg.DomainToTable(self.parcels, "kinds", self.out("k2.gpkg"), "c", "d")[0], ["c"]), [("a",)])
        mg.RemoveDomainFromField(self.parcels, "kind")
        mg.DeleteDomain(self.parcels, "kinds")
        with self.assertRaises(arcpy.ExecuteError):
            mg.AddCodedValueToDomain(self.parcels, "kinds", "c", "gone")

        mg.CreateDomain(self.parcels, "values", "From 0 to 10", "DOUBLE", "RANGE")
        mg.SetValueForRangeDomain(self.parcels, "values", "0", "10")
        mg.AssignDomainToField(self.parcels, "value", "values")
        with self.assertRaises(arcpy.ExecuteError):
            mg.AssignDomainToField(self.parcels, "name", "values")     # a text field and a double domain
        connection = sqlite3.connect(self.parcels)
        self.assertEqual(connection.execute("select min, min_is_inclusive, max, max_is_inclusive, description from "
                                            "gpkg_data_column_constraints where constraint_type = 'range'").fetchall(),
                         [(0, 1, 10, 1, "From 0 to 10")])
        connection.close()

        lookup = self.table("lookup", [("code", "LONG"), ("label", "TEXT")], [(3, "three"), (1, "one")])
        mg.TableToDomain(lookup, "code", "label", self.parcels, "numbers")
        mg.TableToDomain(lookup, "code", "label", self.parcels, "numbers")       # appending the same codes changes nothing
        mg.SortCodedValueDomain(self.parcels, "numbers", "CODE", "ASCENDING")
        self.assertEqual(self.rows(mg.DomainToTable(self.parcels, "numbers", self.out("n.gpkg"), "c", "d")[0], ["c", "d"]),
                         [(1, "one"), (3, "three")])

    def test_alter_alias_name_sets_the_geopackage_identifier(self):
        arcpy.management.AlterAliasName(self.parcels, "Land parcels")
        connection = sqlite3.connect(self.parcels)
        self.assertEqual(connection.execute("select identifier from gpkg_contents where table_name = 'parcels'").fetchone(),
                         ("Land parcels",))
        connection.close()

    # ---- random points

    def test_random_points_fall_where_asked_and_repeat_with_the_seed(self):
        made = arcpy.management.CreateRandomPoints(self.directory, "inbox.gpkg", None, "0 0 10 10", 50)[0]
        got = self.rows(made, ["CID", "SHAPE@XY"])
        self.assertEqual(len(got), 50)
        self.assertTrue(all(c == 0 and 0 <= x <= 10 and 0 <= y <= 10 for c, (x, y) in got))
        again = self.rows(arcpy.management.CreateRandomPoints(self.directory, "again.gpkg", None, "0 0 10 10", 50)[0], ["SHAPE@XY"])
        self.assertEqual([g[1] for g in got], [a[0] for a in again])
        arcpy.env.randomGenerator = "7 ACM599"
        other = self.rows(arcpy.management.CreateRandomPoints(self.directory, "other.gpkg", None, "0 0 10 10", 50)[0], ["SHAPE@XY"])
        self.assertNotEqual([g[1] for g in got], [o[0] for o in other])
        inside = arcpy.management.CreateRandomPoints(self.directory, "in.gpkg", self.parcels, None, 5)[0]
        by_cid = {}
        for cid, (x, y) in self.rows(inside, ["CID", "SHAPE@XY"]):
            by_cid.setdefault(cid, []).append((x, y))
        self.assertEqual({k: len(v) for k, v in by_cid.items()}, {1: 5, 2: 5, 3: 5})
        # parcel n spans x from 100·(n−1) to 100·(n−1)+50.
        self.assertTrue(all(100 * (cid - 1) <= x <= 100 * (cid - 1) + 50 and 0 <= y <= 50
                            for cid, points in by_cid.items() for x, y in points))
        multi = arcpy.management.CreateRandomPoints(self.directory, "multi.gpkg", self.parcels, None, 5, None, "MULTIPOINT", 2)[0]
        self.assertEqual(sorted(r[0] for r in self.rows(multi, ["CID"])), [1, 1, 1, 2, 2, 2, 3, 3, 3])
        line = self.make("line", "POLYLINE", [], [("LINESTRING(0 0,100 0)",)])
        on_line = arcpy.management.CreateRandomPoints(self.directory, "online.gpkg", line, None, 4)[0]
        self.assertTrue(all(y == 0 and 0 <= x <= 100 for (x, y), in self.rows(on_line, ["SHAPE@XY"])))
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CreateRandomPoints(self.directory, "far.gpkg", None, "0 0 10 10", 5, "1 Meters")

    # ---- rasters

    def grid(self):
        # 1  2  3  4      cells of 10 on EPSG:3857, the top-left corner at (0, 30); 12 is NoData.
        # 5  6  7  8
        # 9 10 11 12
        return self.raster([[1, 2, 3, 4], [5, 6, 7, 8], [9, 10, 11, 12]], nodata=12, name="g.tif")

    def test_get_raster_properties(self):
        path = self.grid()
        ask = lambda p: arcpy.management.GetRasterProperties(path, p).getOutput(0)  # noqa: E731
        self.assertEqual([ask(p) for p in ("MINIMUM", "MAXIMUM", "MEAN", "UNIQUEVALUECOUNT", "TOP", "LEFT", "RIGHT",
                                           "BOTTOM", "CELLSIZEX", "COLUMNCOUNT", "ROWCOUNT", "BANDCOUNT", "ANYNODATA",
                                           "ALLNODATA")],
                         ["1", "11", "6", "11", "30", "0", "40", "0", "10", "4", "3", "1", "1", "0"])
        # 1..11: mean 6, squares summing to 110, so the population variance is 10.
        self.assertAlmostEqual(float(ask("STD")), 10 ** 0.5)
        with self.assertRaises(arcpy.NotBuiltError):
            ask("CLOUDCOVER")

    def test_clip_keeps_the_cells_where_they_lie(self):
        path = self.grid()
        clipped = arcpy.management.Clip(path, "12 5 28 22", self.out("c.tif"))[0]
        self.assertEqual(self.cells(clipped), [[2, 3], [6, 7], [10, 11]])
        self.assertEqual((arcpy.Raster(clipped).extent.XMin, arcpy.Raster(clipped).extent.YMax), (10.0, 30.0))
        shape = self.make("cut", "POLYGON", [], [(box(0, 0, 22, 30),)])
        # The template reaches into the third column, whose centres (x = 25) lie outside it.
        cut = arcpy.management.Clip(path, "#", self.out("g2.tif"), shape, "#", "ClippingGeometry")[0]
        self.assertEqual(self.cells(cut), [[1, 2, None], [5, 6, None], [9, 10, None]])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.Clip(path, "12 5 28 22", self.out("c3.tif"), maintain_clipping_extent="MAINTAIN_EXTENT")

    def test_copy_raster_flip_mirror_and_shift(self):
        import rasterio
        path = self.grid()
        copied = arcpy.management.CopyRaster(path, self.out("copy.tif"), nodata_value="5", pixel_type="16_BIT_SIGNED")[0]
        with rasterio.open(copied) as read:
            self.assertEqual((read.dtypes[0], read.nodata), ("int16", 5.0))
        self.assertEqual(self.cells(copied)[1], [None, 6, 7, 8])
        big = self.raster([[1, 300]], name="big.tif")
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.CopyRaster(big, self.out("small.tif"), pixel_type="8_BIT_UNSIGNED")
        self.assertEqual(self.cells(arcpy.management.Flip(path, self.out("f.tif"))[0])[0], [9, 10, 11, None])
        self.assertEqual(self.cells(arcpy.management.Mirror(path, self.out("mi.tif"))[0])[0], [4, 3, 2, 1])
        moved = arcpy.Raster(arcpy.management.Shift(path, self.out("s.tif"), 5, -10)[0])
        self.assertEqual((moved.extent.XMin, moved.extent.YMin, moved.extent.XMax, moved.extent.YMax), (5.0, -10.0, 45.0, 20.0))

    def test_mosaic_to_new_raster_by_method(self):
        left = self.raster([[1, 2], [3, 4]], 0, 0, name="left.tif")             # x 0..20
        right = self.raster([[10, 20], [30, 40]], 10, 0, name="right.tif")      # x 10..30: the middle column overlaps
        mosaic = lambda method, name: self.cells(arcpy.management.MosaicToNewRaster(  # noqa: E731
            [left, right], self.directory, name, None, "8_BIT_UNSIGNED", None, 1, method)[0])
        self.assertEqual(mosaic("LAST", "last.tif"), [[1, 10, 20], [3, 30, 40]])
        self.assertEqual(mosaic("FIRST", "first.tif"), [[1, 2, 20], [3, 4, 40]])
        self.assertEqual(mosaic("SUM", "sum.tif"), [[1, 12, 20], [3, 34, 40]])
        self.assertEqual(mosaic("MEAN", "mean.tif"), [[1, 6, 20], [3, 17, 40]])
        self.assertEqual(mosaic("MAXIMUM", "max.tif"), [[1, 10, 20], [3, 30, 40]])
        holed = self.raster([[-1, 20], [30, 40]], 10, 0, nodata=-1, name="holed.tif")
        self.assertEqual(self.cells(arcpy.management.MosaicToNewRaster([left, holed], self.directory, "h.tif", None,
                                                                       "8_BIT_UNSIGNED", None, 1, "LAST")[0]),
                         [[1, 2, 20], [3, 30, 40]])
        with self.assertRaises(arcpy.NotBuiltError):
            mosaic("BLEND", "blend.tif")

    def test_resample_nearest(self):
        path = self.grid()
        fine = arcpy.Raster(arcpy.management.Resample(path, self.out("fine.tif"), 5)[0])
        self.assertEqual((fine.height, fine.width), (6, 8))
        self.assertEqual(self.cells(fine.catalogPath)[0], [1, 1, 2, 2, 3, 3, 4, 4])
        self.assertEqual(self.cells(fine.catalogPath)[5][6:], [None, None])
        square = self.raster(np.arange(36).reshape(6, 6).tolist(), cell=1.0, name="sq.tif")
        # New 3 × 3 cells centred at 1.5 and 4.5 from each edge take input cells 1 and 4.
        self.assertEqual(self.cells(arcpy.management.Resample(square, self.out("coarse.tif"), 3)[0]), [[7, 10], [25, 28]])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.Resample(square, self.out("even.tif"), 2)      # centres on the edges between cells
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.Resample(path, self.out("odd.tif"), 20)        # 30 is not a whole number of 20s
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.Resample(path, self.out("bil.tif"), 5, "BILINEAR")


if __name__ == "__main__":
    unittest.main()
