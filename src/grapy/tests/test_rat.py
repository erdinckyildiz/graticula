"""Raster attribute tables (ADR-224): kept as GDAL keeps them, a GDALRasterAttributeTable in the GeoTIFF's .aux.xml
PAM file, and the tools that read or write them. Every expected value below is worked out by hand.

The rasters are written north to south with 10-unit cells on EPSG:3857, the lower-left corner at (0, 0), so in a
three-row raster the cell in row r and column k has its centre at (5 + 10k, 25 - 10r) and an area of 100.
"""
import os
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ddd, sa  # noqa: E402

ND = -1
LAND = [[1, 1, 2], [3, 1, 2], [3, 3, 1]]  # 1 four times, 2 twice, 3 three times


def grid(values, nodata=None):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0.0, 0.0), 10.0, 10.0, nodata)


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


def table(path, fields="*"):
    with arcpy.da.SearchCursor(path, fields) as rows:
        return [list(row) for row in rows]


class Rat(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def saved(self, values, name):
        path = os.path.join(self.directory, name)
        grid(values).save(path)
        return path

    def land(self, name="land.tif"):
        """LAND with a table: Code 10/20/10, Name forest/water/urban and Weight 0.5/1.5/2.0 for values 1/2/3."""
        path = self.saved(LAND, name)
        arcpy.management.BuildRasterAttributeTable(path)
        arcpy.management.AddField(path, "Code", "LONG")
        arcpy.management.AddField(path, "Name", "TEXT")
        arcpy.management.AddField(path, "Weight", "DOUBLE")
        extra = {1: (10, "forest", 0.5), 2: (20, "water", 1.5), 3: (10, "urban", 2.0)}
        with arcpy.da.UpdateCursor(path, ["Value", "Code", "Name", "Weight"]) as rows:
            for row in rows:
                rows.updateRow([row[0], *extra[row[0]]])
        return path

    # ---- the table itself

    def test_build_writes_value_and_count_into_the_aux_xml(self):
        path = self.saved(LAND, "land.tif")
        self.assertFalse(arcpy.Raster(path).hasRAT)
        arcpy.management.BuildRasterAttributeTable(path)
        self.assertTrue(arcpy.Raster(path).hasRAT)
        root = ET.parse(path + ".aux.xml").getroot()
        rat = root.find("PAMRasterBand").find("GDALRasterAttributeTable")
        # GDAL's codes: type 0 integer; usage 5 the value (min-max) column, 1 the pixel count.
        self.assertEqual([(d.findtext("Name"), d.findtext("Type"), d.findtext("Usage")) for d in rat.findall("FieldDefn")],
                         [("Value", "0", "5"), ("Count", "0", "1")])
        self.assertEqual([[f.text for f in r.findall("F")] for r in rat.findall("Row")], [["1", "4"], ["2", "2"], ["3", "3"]])
        self.assertEqual(table(path), [[1, 1, 4], [2, 2, 2], [3, 3, 3]])

    def test_a_floating_point_raster_has_no_table(self):
        path = self.saved([[0.5, 1.5]], "float.tif")
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.BuildRasterAttributeTable(path)

    def test_added_fields_cursors_and_where_clauses(self):
        path = self.land()
        self.assertEqual(table(path, ["Value", "Name"], ), [[1, "forest"], [2, "water"], [3, "urban"]])
        with arcpy.da.SearchCursor(path, ["value", "WEIGHT"], "Code = 10") as rows:
            self.assertEqual([tuple(r) for r in rows], [(1, 0.5), (3, 2.0)])
        with arcpy.da.SearchCursor(path, ["OID@", "Value"], sql_clause=(None, "ORDER BY Count DESC")) as rows:
            self.assertEqual(list(rows), [(1, 1), (3, 3), (2, 2)])
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.management.AddField(path, "code", "LONG")
        with arcpy.da.UpdateCursor(path, ["Value"], "Value = 2") as rows:
            for _ in rows:
                rows.deleteRow()
        self.assertEqual(table(path, ["Value"]), [[1], [3]])

    def test_rebuilding_keeps_fields_and_overwrite_drops_them(self):
        path = self.land()
        arcpy.management.BuildRasterAttributeTable(path, "NONE")
        self.assertEqual(table(path, ["Value", "Count", "Code"]), [[1, 4, 10], [2, 2, 20], [3, 3, 10]])
        arcpy.management.BuildRasterAttributeTable(path, "Overwrite")
        self.assertEqual(table(path), [[1, 1, 4], [2, 2, 2], [3, 3, 3]])

    def test_delete_removes_the_table_and_its_empty_file(self):
        path = self.land()
        arcpy.management.DeleteRasterAttributeTable(path)
        self.assertFalse(arcpy.Raster(path).hasRAT)
        self.assertFalse(os.path.exists(path + ".aux.xml"))

    # ---- the tools

    def test_lookup_by_field_type(self):
        path = self.land()
        code = sa.Lookup(path, "Code")
        self.assertEqual(cells(code), [[10, 10, 20], [10, 10, 20], [10, 10, 10]])
        code.save(os.path.join(self.directory, "code.tif"))
        self.assertEqual(table(os.path.join(self.directory, "code.tif")), [[1, 10, 7], [2, 20, 2]])
        self.assertEqual(cells(sa.Lookup(path, "Weight")), [[0.5, 0.5, 1.5], [2.0, 0.5, 1.5], [2.0, 2.0, 0.5]])
        named = sa.Lookup(path, "Name")
        self.assertEqual(cells(named), LAND)
        ddd.Lookup(path, "Name", os.path.join(self.directory, "named.tif"))
        self.assertEqual(table(os.path.join(self.directory, "named.tif"), ["Value", "Count", "Name"]),
                         [[1, 4, "forest"], [2, 2, "water"], [3, 3, "urban"]])
        with self.assertRaises(ValueError):
            sa.Lookup(self.saved(LAND, "bare.tif"), "Code")

    def test_reclassify_on_a_field(self):
        path = self.land()
        names = sa.RemapValue([["forest", 100], ["water", 200]])
        self.assertEqual(cells(sa.Reclassify(path, "Name", names)), [[100, 100, 200], [3, 100, 200], [3, 3, 100]])
        self.assertEqual(cells(sa.Reclassify(path, "Name", names, "NODATA")),
                         [[100, 100, 200], [ND, 100, 200], [ND, ND, 100]])
        # Weights 0.5, 1.5 and 2.0 against the ranges 0–1 and 1–3.
        self.assertEqual(cells(sa.Reclassify(path, "Weight", sa.RemapRange([[0, 1, 5], [1, 3, 9]]))),
                         [[5, 5, 9], [9, 5, 9], [9, 9, 5]])

    def test_zonal_statistics_on_a_field(self):
        path = self.land()
        values = grid([[1, 2, 3], [4, 5, 6], [7, 8, 9]])
        # Code 10 covers every cell but (0,2) and (1,2): 1+2+4+5+7+8+9 = 36 over 7; code 20 is 3 and 6.
        mean = arcpy.RasterToNumPyArray(sa.ZonalStatistics(path, "Code", values, "MEAN"))
        self.assertTrue(np.allclose(mean, [[36 / 7, 36 / 7, 4.5], [36 / 7, 36 / 7, 4.5], [36 / 7, 36 / 7, 36 / 7]]))
        # By text: forest 1+2+5+9 = 17, water 3+6 = 9, urban 4+7+8 = 19.
        self.assertEqual(cells(sa.ZonalStatistics(path, "Name", values, "SUM")), [[17, 17, 9], [19, 17, 9], [19, 19, 17]])

    def test_where_clauses_on_fields(self):
        path = self.land()
        # Name = 'water' is value 2; Code = 10 AND Weight > 1 is value 3 (urban).
        self.assertEqual(cells(sa.ExtractByAttributes(path, "Name = 'water'")), [[ND, ND, 2], [ND, ND, 2], [ND, ND, ND]])
        self.assertEqual(cells(sa.Con(path, 1, 0, "Code = 10 AND Weight > 1")), [[0, 0, 0], [1, 0, 0], [1, 1, 0]])
        self.assertEqual(cells(sa.SetNull(path, path, "Name <> 'forest'")), [[1, 1, ND], [ND, 1, ND], [ND, ND, 1]])
        with self.assertRaises(ValueError):
            sa.Con(path, 1, 0, "Colour = 'red'")
        with self.assertRaises(arcpy.NotBuiltError):
            sa.Con(grid(LAND), 1, 0, "Name = 'water'")

    def test_weighted_sum_on_a_field(self):
        path = self.land()
        self.assertEqual(cells(sa.WeightedSum(sa.WSTable([[path, "Weight", 2]]))),
                         [[1.0, 1.0, 3.0], [4.0, 1.0, 3.0], [4.0, 4.0, 1.0]])

    def test_extract_values_to_points_all(self):
        path = self.land()
        points = os.path.join(self.directory, "p.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "p.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(points, "Code", "LONG")
        with arcpy.da.InsertCursor(points, ["Code", "SHAPE@XY"]) as rows:
            rows.insertRow((7, (5.0, 25.0)))   # row 0, column 0: value 1
            rows.insertRow((8, (5.0, 15.0)))   # row 1, column 0: value 3
        out = os.path.join(self.directory, "out.gpkg")
        sa.ExtractValuesToPoints(points, path, out, "NONE", "ALL")
        # Count is left out; the raster's Code clashes with the point's and becomes Code_1.
        self.assertEqual(table(out, ["Code", "RASTERVALU", "Value", "Code_1", "Name", "Weight"]),
                         [[7, 1.0, 1, 10, "forest", 0.5], [8, 3.0, 3, 10, "urban", 2.0]])
        self.assertNotIn("count", [f.name.lower() for f in arcpy.ListFields(out)])

    def test_region_group_links_each_region_to_its_zone(self):
        regions = sa.RegionGroup(grid(LAND), "FOUR", "WITHIN", "ADD_LINK")
        self.assertEqual(cells(regions), [[1, 1, 2], [3, 1, 2], [3, 3, 4]])
        path = os.path.join(self.directory, "regions.tif")
        regions.save(path)
        self.assertEqual(table(path, ["Value", "Count", "LINK"]), [[1, 3, 1], [2, 2, 2], [3, 3, 3], [4, 1, 1]])
        cross = os.path.join(self.directory, "cross.tif")
        sa.RegionGroup(grid(LAND), "FOUR", "CROSS", "ADD_LINK").save(cross)
        self.assertEqual(table(cross), [[1, 1, 9]])

    def test_combine_numbers_combinations_in_scan_order(self):
        self.assertTrue(hasattr(sa.Combine, "__grapy_equivalent__"))
        land = self.saved(LAND, "land-use.tif")
        other = self.saved([[5, 5, 5], [5, 6, 6], [6, 6, 6]], "b.tif")
        combined = sa.Combine([land, other])
        # (1,5) (1,5) (2,5) / (3,5) (1,6) (2,6) / (3,6) (3,6) (1,6)
        self.assertEqual(cells(combined), [[1, 1, 2], [3, 4, 5], [6, 6, 4]])
        path = os.path.join(self.directory, "combined.tif")
        combined.save(path)
        self.assertEqual(table(path, ["Value", "Count", "land_use", "b"]),
                         [[1, 2, 1, 5], [2, 1, 2, 5], [3, 1, 3, 5], [4, 2, 1, 6], [5, 1, 2, 6], [6, 2, 3, 6]])
        twice = os.path.join(self.directory, "twice.tif")
        sa.Combine([other, other]).save(twice)
        self.assertEqual(table(twice, ["Value", "Count", "b", "b_1"]), [[1, 4, 5, 5], [2, 5, 6, 6]])

    def test_cut_fill_regions_volumes_and_areas(self):
        before = grid([[5, 5, 5], [5, 5, 5]])
        after = grid([[3, 5, 7], [3, 5, 8]])
        # Change before - after: 2 0 -2 / 2 0 -3: cut in column 0, none in 1, fill in 2; cells of 100.
        out = os.path.join(self.directory, "cutfill.tif")
        ddd.CutFill(before, after, out)
        self.assertEqual(cells(out), [[1, 2, 3], [1, 2, 3]])
        self.assertEqual(table(out, ["Value", "Count", "Volume", "Area"]),
                         [[1, 2, 400.0, 200.0], [2, 2, 0.0, 200.0], [3, 2, -500.0, 200.0]])
        halved = os.path.join(self.directory, "halved.tif")
        sa.CutFill(before, after, 0.5).save(halved)
        self.assertEqual(table(halved, ["Volume"]), [[200.0], [0.0], [-250.0]])
        # Diagonal neighbours are not edge-connected: two cut regions and two unchanged ones.
        self.assertEqual(cells(sa.CutFill(grid([[1, 0], [0, 1]]), grid([[0, 0], [0, 0]]))), [[1, 2], [3, 4]])


class ArrayZM(unittest.TestCase):
    def test_feature_class_to_numpy_array_reads_z_and_m(self):
        directory = tempfile.mkdtemp()
        arcpy.management.CreateFeatureclass(directory, "zm.gpkg", "POINT", has_m="ENABLED", has_z="ENABLED",
                                            spatial_reference=3857)
        path = os.path.join(directory, "zm.gpkg")
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            rows.insertRow((arcpy.PointGeometry(arcpy.Point(1, 2, 3, 4), 3857, True, True),))
        array = arcpy.da.FeatureClassToNumPyArray(path, ["SHAPE@Z", "SHAPE@M", "SHAPE@XYZ"])
        self.assertEqual(array["SHAPE@Z"].tolist(), [3.0])
        self.assertEqual(array["SHAPE@M"].tolist(), [4.0])
        self.assertEqual(array["SHAPE@XYZ"].tolist(), [[1.0, 2.0, 3.0]])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.da.FeatureClassToNumPyArray(path, ["SHAPE@"])


if __name__ == "__main__":
    unittest.main()
