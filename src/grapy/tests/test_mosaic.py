"""Mosaic datasets held in a GeoPackage (ADR-230), against hand-worked answers.

Most tests mosaic two 4 × 4 rasters of 1 m cells in EPSG:3857 that overlap in two columns:

    A: x 0–4, y 0–4, every cell 10        B: x 2–6, y 0–4, every cell 20

so the mosaic is 6 columns by 4 rows and only columns 2 and 3 (x 2–4) are decided by the mosaic method and operator.
A's centre is (2, 2) and B's (4, 2); the north-west corner of the mosaic is (0, 4), 2.83 from A and 4.47 from B; the
halfway line between the centres is x = 3.
"""
import os
import shutil
import sys
import tempfile
import unittest

import numpy as np
import rasterio
import shapely
from rasterio.transform import from_origin

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _mosaic, da  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

GAP = 99   # what a NoData cell is read as in these tests


def write_tif(path, left, top, values, size=1.0, nodata=0, dtype="uint8", epsg=3857):
    array = np.asarray(values, dtype=dtype)
    if array.ndim == 2:
        array = array[np.newaxis]
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with rasterio.open(path, "w", driver="GTiff", height=array.shape[1], width=array.shape[2], count=array.shape[0],
                       dtype=dtype, crs=f"EPSG:{epsg}", transform=from_origin(left, top, size, size),
                       nodata=nodata) as out:
        out.write(array)
    return path


def cells(value):
    return arcpy.RasterToNumPyArray(value, nodata_to_value=GAP).tolist()


def rows_of(path, fields, where=None):
    with da.SearchCursor(path, fields, where) as cursor:
        return [tuple(r) for r in cursor]


class Mosaic(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.images = os.path.join(self.directory, "images")
        self.a = write_tif(os.path.join(self.images, "a.tif"), 0, 4, np.full((4, 4), 10))
        self.b = write_tif(os.path.join(self.images, "b.tif"), 2, 4, np.full((4, 4), 20))
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]
        self.md = arcpy.management.CreateMosaicDataset(self.gpkg, "md", 3857)[0]
        arcpy.management.AddRastersToMosaicDataset(self.md, "Raster Dataset", [self.a, self.b])

    def tearDown(self):
        _mosaic.LAYERS.clear()
        shutil.rmtree(self.directory, ignore_errors=True)

    def set_field(self, field, values, kind="LONG"):
        if field not in [f.name for f in arcpy.ListFields(self.md)]:
            arcpy.management.AddField(self.md, field, kind)
        with da.UpdateCursor(self.md, ["Name", field]) as cursor:
            for row in cursor:
                cursor.updateRow([row[0], values.get(row[0])])

    def overlap(self, value):
        """The two overlap columns of the first row."""
        return cells(value)[0][2:4]

    # ---- the model

    def test_a_mosaic_dataset_is_a_catalog_table_and_grapys_rows_registered_as_an_extension(self):
        import sqlite3
        connection = sqlite3.connect(self.gpkg)
        try:
            registered = {r[0] for r in connection.execute(
                "select table_name from gpkg_extensions where extension_name = 'grapy_mosaic'")}
            columns = [r[1] for r in connection.execute('pragma table_info("md")')]
        finally:
            connection.close()
        self.assertEqual(registered, {"grapy_mosaic", "grapy_mosaic_item", "md"})
        self.assertEqual(columns[0], "OBJECTID")
        for field in ("Raster", "Name", "MinPS", "MaxPS", "LowPS", "HighPS", "Category", "Tag", "GroupName",
                      "ProductName", "CenterX", "CenterY", "ZOrder"):
            self.assertIn(field, columns)

    def test_adding_rasters_fills_the_documented_fields(self):
        found = rows_of(self.md, ["OBJECTID", "Name", "LowPS", "HighPS", "MinPS", "MaxPS", "Category", "Tag", "CenterX",
                                  "CenterY", "ZOrder", "SHAPE@AREA"])
        self.assertEqual(found, [(1, "a", 1.0, 1.0, 0.0, 10.0, 1, "Dataset", 2.0, 2.0, None, 16.0),
                                 (2, "b", 1.0, 1.0, 0.0, 10.0, 1, "Dataset", 4.0, 2.0, None, 16.0)])
        self.assertEqual(rows_of(self.md, ["Raster"])[0][0], os.path.abspath(self.a))

    def test_describe_answers_the_mosaic_dataset_properties(self):
        described = arcpy.Describe(self.md)
        self.assertEqual(described.dataType, "MosaicDataset")
        self.assertEqual((described.bandCount, described.width, described.height), (1, 6, 4))
        self.assertEqual((described.extent.XMin, described.extent.XMax), (0.0, 6.0))
        self.assertEqual(described.spatialReference.factoryCode, 3857)
        self.assertEqual((described.defaultMosaicMethod, described.mosaicOperator, described.sortAscending),
                         ("NorthWest", "FIRST", "Ascending"))
        self.assertFalse(described.referenced)
        self.assertEqual(described.OIDFieldName, "OBJECTID")
        self.assertEqual(arcpy.da.Describe(self.md)["dataType"], "MosaicDataset")

    def test_a_mosaic_dataset_is_listed_as_a_dataset_and_not_as_a_feature_class(self):
        arcpy.env.workspace = self.gpkg
        self.assertEqual(arcpy.ListDatasets(feature_type="Mosaic"), ["md"])
        self.assertIn("md", arcpy.ListDatasets())
        self.assertEqual(arcpy.ListFeatureClasses(), [])
        arcpy.env.workspace = self.directory
        self.assertEqual(arcpy.ListDatasets("*", "Mosaic"), ["data.gpkg/md"])

    def test_a_mosaic_dataset_is_named_by_its_name_inside_a_geopackage_workspace(self):
        arcpy.env.workspace = self.gpkg
        self.assertEqual(cells("md")[0], [10, 10, 10, 10, 20, 20])

    # ---- mosaic methods

    def test_north_west_puts_the_raster_nearest_the_north_west_corner_on_top(self):
        self.assertEqual(cells(self.md), [[10, 10, 10, 10, 20, 20]] * 4)
        raster = arcpy.Raster(self.md)
        self.assertEqual((raster.extent.XMin, raster.extent.YMax, raster.meanCellWidth), (0.0, 4.0, 1.0))
        self.assertEqual(raster.catalogPath, self.md)

    def test_zorder_comes_before_the_method_and_lower_is_on_top(self):
        with da.UpdateCursor(self.md, ["Name", "ZOrder"]) as cursor:
            for row in cursor:
                cursor.updateRow([row[0], -1 if row[0] == "b" else None])
        self.assertEqual(self.overlap(self.md), [20, 20])

    def test_by_attribute_orders_by_the_difference_from_the_base(self):
        self.set_field("Year", {"a": 2010, "b": 2020})
        arcpy.management.SetMosaicDatasetProperties(self.md, default_mosaic_method="ByAttribute", order_field="Year",
                                                    order_base=2019)
        self.assertEqual(self.overlap(self.md), [20, 20])   # |2020 − 2019| = 1 before |2010 − 2019| = 9
        arcpy.management.SetMosaicDatasetProperties(self.md, sorting_order="DESCENDING")
        self.assertEqual(self.overlap(self.md), [10, 10])
        self.assertEqual(arcpy.Describe(self.md).orderField, "Year")

    def test_by_attribute_reads_dates_against_a_base_in_a_documented_format(self):
        self.set_field("Acquired", {"a": "2001-06-01", "b": "2015-06-01"}, "DATE")
        layer = arcpy.management.MakeMosaicLayer(self.md, "by_date", mosaic_method="BY_ATTRIBUTE",
                                                 order_field="Acquired", order_base_value="2002/01/01")[0]
        self.assertEqual(self.overlap(layer), [10, 10])

    def test_closest_to_center_and_north_west_measure_from_the_extent_read(self):
        layer = arcpy.management.MakeMosaicLayer(self.md, "east", template="2 0 6 4",
                                                 mosaic_method="CLOSEST_TO_CENTER")[0]
        self.assertEqual(cells(layer)[0], [20, 20, 20, 20])    # the centre (4, 2) is B's own
        layer = arcpy.management.MakeMosaicLayer(self.md, "east_nw", template="2 0 6 4", mosaic_method="NORTH_WEST")[0]
        self.assertEqual(cells(layer)[0], [10, 10, 20, 20])    # (2, 4) is 2 from A and 2.83 from B

    def test_closest_to_nadir_reads_centerx_and_centery(self):
        with da.UpdateCursor(self.md, ["Name", "CenterX"]) as cursor:
            for row in cursor:
                cursor.updateRow([row[0], 100.0 if row[0] == "a" else 3.0])
        layer = arcpy.management.MakeMosaicLayer(self.md, "nadir", mosaic_method="CLOSEST_TO_NADIR")[0]
        self.assertEqual(self.overlap(layer), [20, 20])

    def test_lock_raster_shows_only_the_rasters_named(self):
        layer = arcpy.management.MakeMosaicLayer(self.md, "locked", mosaic_method="LOCK_RASTER", lock_rasterid="2")[0]
        self.assertEqual(cells(layer)[0], [GAP, GAP, 20, 20, 20, 20])
        layer = arcpy.management.MakeMosaicLayer(self.md, "by_name", mosaic_method="LOCK_RASTER",
                                                 lock_rasterid="b;a")[0]
        self.assertEqual(self.overlap(layer), [20, 20])    # in the order given

    def test_seamline_draws_each_item_on_its_side_of_the_halfway_line(self):
        arcpy.management.SetMosaicDatasetProperties(self.md, default_mosaic_method="Seamline")
        self.assertEqual(cells(self.md), [[10, 10, 10, 20, 20, 20]] * 4)
        values = np.full((4, 4), 10)
        values[0, 2] = 0
        write_tif(self.a, 0, 4, values)
        self.assertEqual(cells(self.md)[0], [10, 10, 20, 20, 20, 20])    # the next nearest shows through NoData
        layer = arcpy.management.MakeMosaicLayer(self.md, "seam_max", mosaic_method="SEAMLINE", mosaic_operator="MAX")[0]
        self.assertEqual(self.overlap(layer), [20, 20])
        with self.assertRaises(ExecuteError):
            arcpy.management.MakeMosaicLayer(self.md, "seam_last", mosaic_method="SEAMLINE", mosaic_operator="LAST")

    def test_none_keeps_objectid_order(self):
        arcpy.management.SetMosaicDatasetProperties(self.md, default_mosaic_method="None", mosaic_operator="LAST")
        self.assertEqual(self.overlap(self.md), [20, 20])

    # ---- mosaic operators

    def test_each_operator_combines_the_overlapping_cells(self):
        expected = {"FIRST": 10, "LAST": 20, "MIN": 10, "MAX": 20, "MEAN": 15, "SUM": 30}
        for operator, value in expected.items():
            arcpy.management.SetMosaicDatasetProperties(self.md, mosaic_operator=operator)
            self.assertEqual(cells(self.md)[0], [10, 10, value, value, 20, 20], operator)

    def test_blend_weighs_cells_by_their_distance_inside_their_item(self):
        # Row 1 at x 2–3: A's cell is 1 in from its edge (weight 2) and B's on its edge (weight 1): (2·10 + 20) / 3.
        arcpy.management.SetMosaicDatasetProperties(self.md, mosaic_operator="BLEND")
        self.assertEqual(cells(self.md), [[10, 10, 15, 15, 20, 20], [10, 10, 13, 17, 20, 20],
                                          [10, 10, 13, 17, 20, 20], [10, 10, 15, 15, 20, 20]])

    def test_mean_of_integer_cells_rounds_half_to_even(self):
        write_tif(self.b, 2, 4, np.full((4, 4), 21))
        arcpy.management.SetMosaicDatasetProperties(self.md, mosaic_operator="MEAN")
        self.assertEqual(self.overlap(self.md), [16, 16])   # 15.5

    def test_a_layer_overrides_the_operator_and_selects_items(self):
        layer = arcpy.management.MakeMosaicLayer(self.md, "max_layer", mosaic_operator="MAX")[0]
        self.assertEqual(self.overlap(layer), [20, 20])
        layer = arcpy.management.MakeMosaicLayer(self.md, "only_a", where_clause="Name = 'a'")[0]
        self.assertEqual(cells(layer)[0], [10, 10, 10, 10, GAP, GAP])
        self.assertEqual(rows_of(layer, ["Name"]), [("a",)])

    # ---- NoData, footprints, boundary

    def test_nodata_in_the_top_raster_shows_the_one_beneath(self):
        values = np.full((4, 4), 10)
        values[0, 2] = 0
        write_tif(self.a, 0, 4, values)
        self.assertEqual(self.overlap(self.md), [20, 10])
        arcpy.management.SetMosaicDatasetProperties(self.md, footprints_may_contain_nodata="FOOTPRINTS_DO_NOT_CONTAIN_NODATA")
        self.assertEqual(self.overlap(self.md), [GAP, 10])

    def test_define_mosaic_dataset_nodata_makes_values_nodata(self):
        arcpy.management.DefineMosaicDatasetNoData(self.md, 1, "BAND_1 10", where_clause="Name = 'a'")
        self.assertEqual(cells(self.md)[0], [GAP, GAP, 20, 20, 20, 20])
        arcpy.management.DefineMosaicDatasetNoData(self.md, 1, bands_for_valid_data_range=[["ALL_BANDS", 0, 15]])
        self.assertEqual(cells(self.md)[0], [10, 10, 10, 10, GAP, GAP])
        with self.assertRaises(ExecuteError):
            arcpy.management.DefineMosaicDatasetNoData(self.md, 3, "ALL_BANDS 0")

    def test_build_footprints_by_radiometry_excludes_cells_out_of_range(self):
        values = np.full((4, 4), 10)
        values[:, 0] = 0
        values[:, 1] = 5
        write_tif(self.a, 0, 4, values)
        arcpy.management.BuildFootprints(self.md, "Name = 'a'")
        self.assertEqual(rows_of(self.md, ["SHAPE@AREA"], "Name = 'a'"), [(12.0,)])
        arcpy.management.BuildFootprints(self.md, "Name = 'a'", min_data_value=6)
        self.assertEqual(rows_of(self.md, ["SHAPE@AREA"], "Name = 'a'"), [(8.0,)])
        arcpy.management.BuildFootprints(self.md, "Name = 'a'", reset_footprint="GEOMETRY", shrink_distance=0.5)
        self.assertEqual(rows_of(self.md, ["SHAPE@AREA"], "Name = 'a'"), [(9.0,)])
        arcpy.management.BuildFootprints(self.md, "Name = 'a'", reset_footprint="GEOMETRY")
        self.assertEqual(rows_of(self.md, ["SHAPE@AREA"], "Name = 'a'"), [(16.0,)])

    def test_build_footprints_keeps_masked_regions_smaller_than_the_minimum(self):
        values = np.full((5, 5), 10)
        values[2, 2] = 0
        write_tif(self.a, 0, 4, values)
        arcpy.management.BuildFootprints(self.md, "Name = 'a'")
        self.assertEqual(rows_of(self.md, ["SHAPE@AREA"], "Name = 'a'"), [(24.0,)])
        arcpy.management.BuildFootprints(self.md, "Name = 'a'", min_region_size=2)
        self.assertEqual(rows_of(self.md, ["SHAPE@AREA"], "Name = 'a'"), [(25.0,)])

    def test_clipping_to_footprints_uses_only_the_cells_inside_them(self):
        fc = arcpy.management.CreateFeatureclass(self.gpkg, "cut", "POLYGON", spatial_reference=3857)[0]
        arcpy.management.AddField(fc, "Name", "TEXT")
        with da.InsertCursor(fc, ["Name", "SHAPE@"]) as cursor:
            cursor.insertRow(["a", shapely.box(0, 0, 2, 4)])
        arcpy.management.ImportMosaicDatasetGeometry(self.md, "FOOTPRINT", "Name", fc, "Name")
        self.assertEqual(rows_of(self.md, ["SHAPE@AREA"], "Name = 'a'"), [(8.0,)])
        self.assertEqual(self.overlap(self.md), [10, 10])      # NOT_CLIP, the default
        arcpy.management.SetMosaicDatasetProperties(self.md, clip_to_footprints="CLIP")
        self.assertEqual(self.overlap(self.md), [20, 20])

    def test_the_boundary_is_the_union_of_the_footprints_and_clips_the_mosaic(self):
        far = write_tif(os.path.join(self.images, "far.tif"), 4, 8, np.full((2, 2), 30))
        arcpy.management.AddRastersToMosaicDataset(self.md, "Raster Dataset", far)
        geometry = os.path.join(self.gpkg, "boundary")
        arcpy.management.ExportMosaicDatasetGeometry(self.md, geometry, geometry_type="BOUNDARY")
        self.assertEqual(rows_of(geometry, ["SHAPE@AREA"]), [(28.0,)])   # 24 + 4
        self.assertEqual(cells(self.md)[0], [GAP, GAP, GAP, GAP, 30, 30])  # the grid reaches y 8; x 0–4 is outside
        arcpy.management.SetMosaicDatasetProperties(self.md, clip_to_boundary="NOT_CLIP")
        self.assertEqual(cells(self.md)[4], [10, 10, 10, 10, 20, 20])
        arcpy.management.BuildBoundary(self.md, append_to_existing="OVERWRITE", simplification_method="ENVELOPE")
        arcpy.management.ExportMosaicDatasetGeometry(self.md, geometry, geometry_type="BOUNDARY")
        self.assertEqual(rows_of(geometry, ["SHAPE@AREA"]), [(28.0,)])   # two separate groups, each its envelope

    def test_build_boundary_appends_or_overwrites(self):
        arcpy.management.RemoveRastersFromMosaicDataset(self.md, "Name = 'b'", update_boundary="NO_BOUNDARY")
        geometry = os.path.join(self.gpkg, "boundary")
        arcpy.management.BuildBoundary(self.md)
        arcpy.management.ExportMosaicDatasetGeometry(self.md, geometry, geometry_type="BOUNDARY")
        self.assertEqual(rows_of(geometry, ["SHAPE@AREA"]), [(24.0,)])   # appended to the old one
        arcpy.management.BuildBoundary(self.md, append_to_existing="OVERWRITE")
        arcpy.management.ExportMosaicDatasetGeometry(self.md, geometry, geometry_type="BOUNDARY")
        self.assertEqual(rows_of(geometry, ["SHAPE@AREA"]), [(16.0,)])

    # ---- cell sizes

    def test_cell_size_ranges_multiply_the_native_cell_size_and_group_by_tolerance(self):
        coarse = write_tif(os.path.join(self.images, "coarse.tif"), 0, 8, [[1, 2, 3], [4, 5, 6]], size=2)
        arcpy.management.AddRastersToMosaicDataset(self.md, "Raster Dataset", coarse)
        self.assertEqual(rows_of(self.md, ["MaxPS"], "Name = 'coarse'"), [(20.0,)])
        arcpy.management.CalculateCellSizeRanges(self.md, max_range_factor=5)
        self.assertEqual([r[0] for r in rows_of(self.md, ["MaxPS"])], [5.0, 5.0, 10.0])
        arcpy.management.CalculateCellSizeRanges(self.md, cell_size_tolerance_factor=1.0)
        self.assertEqual([r[0] for r in rows_of(self.md, ["MaxPS"])], [10.0, 10.0, 10.0])

    def test_a_coarser_item_is_read_by_nearest_beneath_finer_ones(self):
        coarse = write_tif(os.path.join(self.images, "coarse.tif"), 0, 8, [[1, 2, 3], [4, 5, 6]], size=2)
        arcpy.management.AddRastersToMosaicDataset(self.md, "Raster Dataset", coarse)
        found = cells(self.md)
        self.assertEqual(found[0], [1, 1, 2, 2, 3, 3])
        self.assertEqual(found[3], [4, 4, 5, 5, 6, 6])
        self.assertEqual(found[4], [10, 10, 10, 10, 20, 20])

    def test_a_coarser_item_is_read_by_bilinear_and_cubic_beneath_finer_ones(self):
        # ADR-230 condition 4. A 2 m item over x 0–12, y 4–16 holding the plane f = 1 + u + 3v of its own column u and
        # row v; read at A's 1 m, a cell centred at (x, y) lies at u = (x − 1) / 2, v = (15 − y) / 2. Bilinear and Keys's
        # cubic convolution both reproduce a plane exactly, so every cell whose neighbourhood lies inside the item is
        # f there, by hand.
        u, v = np.meshgrid(np.arange(6), np.arange(6))
        coarse = write_tif(os.path.join(self.images, "plane.tif"), 0, 16, 1 + u + 3 * v, size=2, nodata=-1,
                           dtype="float32")
        md = arcpy.management.CreateMosaicDataset(self.gpkg, "floats", 3857, 1, "32_BIT_FLOAT")[0]
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", [self.a, coarse])
        for kind, margin in (("BILINEAR", 1), ("CUBIC", 3)):
            arcpy.management.SetMosaicDatasetProperties(md, resampling_type=kind)
            found = np.array(cells(md))
            for row in range(2 * margin, 12 - 2 * margin):
                for column in range(2 * margin, 12 - 2 * margin):
                    x, y = column + 0.5, 16 - row - 0.5
                    expected = 1 + (x - 1) / 2 + 3 * (15 - y) / 2
                    self.assertAlmostEqual(float(found[row, column]), expected, 4, (kind, row, column))

    def test_majority_reads_coarser_than_an_item_by_the_mode_of_the_cells_it_covers(self):
        md = arcpy.management.CreateMosaicDataset(self.gpkg, "classes", 3857)[0]
        item = write_tif(os.path.join(self.directory, "classes", "c.tif"), 0, 4,
                         [[5, 5, 1, 2], [5, 7, 3, 3], [9, 9, 4, 4], [9, 1, 4, 8]])
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", item)
        arcpy.management.SetMosaicDatasetProperties(md, resampling_type="MAJORITY")
        layer = arcpy.management.MakeMosaicLayer(md, "coarse_read", cell_size=2)[0]
        self.assertEqual(cells(layer), [[5, 3], [9, 4]])

    def test_an_item_out_of_its_cell_size_range_is_not_in_view(self):
        with da.UpdateCursor(self.md, ["Name", "MaxPS"]) as cursor:
            for row in cursor:
                cursor.updateRow([row[0], 0.5 if row[0] == "a" else row[1]])
        self.assertEqual(cells(self.md)[0], [GAP, GAP, 20, 20, 20, 20])

    def test_an_item_whose_cells_do_not_line_up_is_resampled_onto_the_grid(self):
        # ADR-230 condition 4. A 1 m item a quarter cell east of A's grid, x 6.25–10.25, its columns 0, 10, 20 and 30
        # centred at 6.75 … 9.75. A's grid centres 7.5, 8.5 and 9.5 lie a quarter of the way to the next column.
        odd = write_tif(os.path.join(self.images, "odd.tif"), 6.25, 4, np.tile([0, 10, 20, 30], (4, 1)), nodata=-1,
                        dtype="float32")
        md = arcpy.management.CreateMosaicDataset(self.gpkg, "floats", 3857, 1, "32_BIT_FLOAT")[0]
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", [self.a, self.b, odd])
        found = cells(md)
        self.assertEqual(found[1][7:10], [10, 20, 30])   # NEAREST: the column whose centre is nearest
        arcpy.management.SetMosaicDatasetProperties(md, resampling_type="BILINEAR")
        self.assertEqual([round(v, 6) for v in cells(md)[1][7:10]], [7.5, 17.5, 27.5])
        self.assertEqual(cells(md)[1][0:6], [10, 10, 10, 10, 20, 20])   # A and B, on the grid, unchanged

    def test_an_item_in_another_reference_is_projected_into_the_mosaics(self):
        # 0.001° square at the origin in EPSG:4326 is 111.32 m east and north in EPSG:3857.
        md = arcpy.management.CreateMosaicDataset(self.gpkg, "projected", 3857)[0]
        item = write_tif(os.path.join(self.directory, "geo", "g.tif"), 0, 0.001, np.full((10, 10), 7), size=0.0001,
                         epsg=4326)
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", item)
        (area,), = rows_of(md, ["SHAPE@AREA"])   # the footprint is held in the mosaic's reference
        self.assertAlmostEqual(area, 111.3195 * 111.3195, delta=1.0)
        raster = arcpy.Raster(md)
        self.assertEqual(raster.spatialReference.factoryCode, 3857)
        self.assertAlmostEqual(raster.extent.XMax, 111.32, delta=raster.meanCellWidth + 0.01)
        self.assertAlmostEqual(raster.extent.YMax, 111.32, delta=raster.meanCellHeight + 0.01)
        values = np.array(cells(md))
        self.assertEqual(set(values[values != GAP].tolist()), {7})
        self.assertGreater(int((values == 7).sum()), 0.9 * values.size)

    # ---- bands

    def test_band_index_reads_the_bands_named_in_that_order(self):
        md = arcpy.management.CreateMosaicDataset(self.gpkg, "two", 3857, 2, "8_BIT_UNSIGNED")[0]
        pair = write_tif(os.path.join(self.directory, "multi", "p.tif"), 0, 2, [[[1, 1], [1, 1]], [[2, 2], [2, 2]]])
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", pair)
        self.assertEqual(arcpy.Raster(md).bandCount, 2)
        layer = arcpy.management.MakeMosaicLayer(md, "swapped", band_index="2;1")[0]
        raster = arcpy.Raster(layer)
        self.assertEqual(raster.read()[0, 0].tolist(), [2, 1])
        with self.assertRaises(NotBuiltError):
            arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", self.a)

    # ---- the catalog

    def test_folders_are_searched_by_filter_and_duplicates_follow_the_action(self):
        md = arcpy.management.CreateMosaicDataset(self.gpkg, "folder", 3857)[0]
        write_tif(os.path.join(self.images, "deep", "c.tif"), 6, 4, np.full((2, 2), 30))
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", self.images, filter="*b.tif")
        self.assertEqual(rows_of(md, ["Name"]), [("b",)])
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", self.images, sub_folder="NO_SUBFOLDERS",
                                                   duplicate_items_action="EXCLUDE_DUPLICATES")
        self.assertEqual(rows_of(md, ["Name"]), [("b",), ("a",)])
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", self.images, filter="REGEX:.*/deep/.*",
                                                   duplicate_items_action="EXCLUDE_DUPLICATES")
        self.assertEqual(rows_of(md, ["Name"]), [("b",), ("a",), ("c",)])
        arcpy.management.AddRastersToMosaicDataset(md, "Raster Dataset", self.b)
        self.assertEqual(len(rows_of(md, ["Name"])), 4)    # ALLOW_DUPLICATES, the default

    def test_remove_rasters_needs_a_query(self):
        with self.assertRaises(ExecuteError):
            arcpy.management.RemoveRastersFromMosaicDataset(self.md)
        arcpy.management.RemoveRastersFromMosaicDataset(self.md, "OBJECTID = 1")
        self.assertEqual(rows_of(self.md, ["Name"]), [("b",)])
        self.assertTrue(os.path.exists(self.a))
        self.assertEqual(cells(self.md)[0], [20, 20, 20, 20])

    def test_paths_are_exported_and_repaired(self):
        table = os.path.join(self.gpkg, "paths")
        arcpy.management.ExportMosaicDatasetPaths(self.md, table)
        self.assertEqual(rows_of(table, ["SourceOID", "Type"]), [(1, "RASTER"), (2, "RASTER")])
        moved = os.path.join(self.directory, "moved")
        shutil.move(self.images, moved)
        arcpy.management.ExportMosaicDatasetPaths(self.md, table, export_mode="BROKEN")
        self.assertEqual(len(rows_of(table, ["Path"])), 2)
        arcpy.management.RepairMosaicDatasetPaths(self.md, [[self.images, moved]], "Name = 'a'")
        arcpy.management.ExportMosaicDatasetPaths(self.md, table, export_mode="BROKEN")
        self.assertEqual(rows_of(table, ["SourceOID"]), [(2,)])
        arcpy.management.RepairMosaicDatasetPaths(self.md, f"* {moved}")
        arcpy.management.ExportMosaicDatasetPaths(self.md, table, export_mode="BROKEN")
        self.assertEqual(rows_of(table, ["SourceOID"]), [])
        self.assertEqual(cells(self.md)[0], [10, 10, 10, 10, 20, 20])

    def test_a_broken_item_is_left_out_of_the_read(self):
        os.remove(self.b)
        self.assertEqual(cells(self.md)[0], [10, 10, 10, 10, GAP, GAP])

    def test_synchronize_reads_stale_items_adds_new_ones_and_removes_broken(self):
        write_tif(self.a, 0, 4, np.full((5, 5), 11))
        os.utime(self.a, (1_000_000_000, 1_000_000_000))
        write_tif(os.path.join(self.images, "new.tif"), 6, 4, np.full((2, 2), 40))
        arcpy.management.SynchronizeMosaicDataset(self.md, new_items="UPDATE_WITH_NEW_ITEMS")
        self.assertEqual(rows_of(self.md, ["Name", "SHAPE@AREA"]), [("a", 25.0), ("b", 16.0), ("new", 4.0)])
        os.remove(self.b)
        arcpy.management.SynchronizeMosaicDataset(self.md, broken_items="REMOVE_BROKEN_ITEMS")
        self.assertEqual([r[0] for r in rows_of(self.md, ["Name"])], ["a", "new"])

    def test_items_are_exported_as_rasters_whole_or_clipped(self):
        out = os.path.join(self.directory, "out")
        os.makedirs(out)
        arcpy.management.ExportMosaicDatasetItems(self.md, out, "img")
        self.assertEqual(sorted(os.listdir(out)), ["img1.tif", "img2.tif"])
        self.assertEqual(arcpy.RasterToNumPyArray(os.path.join(out, "img2.tif")).tolist(), [[20] * 4] * 4)
        clipped = os.path.join(self.directory, "clipped")
        os.makedirs(clipped)
        arcpy.management.ExportMosaicDatasetItems(self.md, clipped, where_clause="Name = 'a'", clip_type="EXTENT",
                                                  template_dataset="0 0 2 2")
        raster = arcpy.Raster(os.path.join(clipped, "a.tif"))
        self.assertEqual((raster.width, raster.height, raster.extent.XMin, raster.extent.YMax), (2, 2, 0.0, 2.0))

    def test_geometry_is_exported_as_footprints_and_levels(self):
        out = os.path.join(self.gpkg, "footprints")
        arcpy.management.ExportMosaicDatasetGeometry(self.md, out, "Name = 'b'", "FOOTPRINT")
        self.assertEqual(rows_of(out, ["SourceOID", "Name", "SHAPE@AREA"]), [(2, "b", 16.0)])
        arcpy.management.ExportMosaicDatasetGeometry(self.md, out, geometry_type="LEVEL")
        self.assertEqual(rows_of(out, ["LowPS", "MaxPS", "SHAPE@AREA"]), [(1.0, 10.0, 24.0)])

    def test_the_boundary_is_imported_from_features(self):
        fc = arcpy.management.CreateFeatureclass(self.gpkg, "edge", "POLYGON", spatial_reference=3857)[0]
        with da.InsertCursor(fc, ["SHAPE@"]) as cursor:
            cursor.insertRow([shapely.box(1, 0, 5, 4)])
        arcpy.management.ImportMosaicDatasetGeometry(self.md, "BOUNDARY", "OBJECTID", fc, "OBJECTID")
        self.assertEqual(cells(self.md)[0], [10, 10, 10, 20])    # x 1–5: A to x 4, A on top over x 2–4, then B

    def test_a_referenced_mosaic_dataset_takes_the_items_selected(self):
        ref = arcpy.management.CreateReferencedMosaicDataset(self.md, os.path.join(self.gpkg, "ref"),
                                                             where_clause="Name = 'b'")[0]
        self.assertTrue(arcpy.Describe(ref).referenced)
        self.assertEqual(rows_of(ref, ["OBJECTID", "Name"]), [(2, "b")])
        self.assertEqual(cells(ref)[0], [20, 20, 20, 20])
        ref2 = arcpy.management.CreateReferencedMosaicDataset(self.md, os.path.join(self.gpkg, "ref2"),
                                                              extent="0 0 1 1")[0]
        self.assertEqual(rows_of(ref2, ["Name"]), [("a",)])
        with self.assertRaises(NotBuiltError):
            arcpy.management.CreateReferencedMosaicDataset(self.md, os.path.join(self.directory, "ref.amd"))

    def test_a_mosaic_dataset_is_deleted_without_its_rasters(self):
        arcpy.management.DeleteMosaicDataset(self.md)
        self.assertFalse(_mosaic.exists(self.md))
        self.assertFalse(arcpy.Exists(self.md))
        self.assertTrue(os.path.exists(self.a))
        other = arcpy.management.CreateMosaicDataset(self.gpkg, "other", 3857)[0]
        arcpy.management.Delete(other)
        self.assertFalse(_mosaic.exists(other))

    def test_map_algebra_reads_a_mosaic_dataset(self):
        result = arcpy.Raster(self.md) + 1
        self.assertEqual(arcpy.RasterToNumPyArray(result)[0].tolist(), [11, 11, 11, 11, 21, 21])
        self.assertEqual(arcpy.sa.Times(self.md, 2).maximum, 40.0)

    # ---- what is refused, and why

    def test_undocumented_methods_operators_and_formats_are_refused_by_name(self):
        for call in (lambda: arcpy.management.SetMosaicDatasetProperties(self.md, default_mosaic_method="Viewpoint"),
                     lambda: arcpy.management.MakeMosaicLayer(self.md, "v", mosaic_method="CLOSEST_TO_VIEWPOINT"),
                     lambda: arcpy.management.AddRastersToMosaicDataset(self.md, "Landsat 8", self.a),
                     lambda: arcpy.management.AddRastersToMosaicDataset(self.md, "Raster Dataset", self.a,
                                                                        update_overviews="UPDATE_OVERVIEWS"),
                     lambda: arcpy.management.BuildFootprints(self.md, approx_num_vertices=80),
                     lambda: arcpy.management.ExportMosaicDatasetItems(self.md, self.directory, format="CRF"),
                     lambda: arcpy.management.ExportMosaicDatasetGeometry(self.md, os.path.join(self.gpkg, "s"),
                                                                          geometry_type="SEAMLINE"),
                     lambda: arcpy.management.CreateMosaicDataset(self.directory, "folder_md", 3857)):
            with self.assertRaises((NotBuiltError, ExecuteError)):
                call()
        for name in ("BuildOverviews", "BuildSeamlines", "AnalyzeMosaicDataset", "MergeMosaicDatasetItems",
                     "BuildMosaicDatasetItemCache", "CreateCatalogDataset", "ColorBalanceMosaicDataset"):
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.management, name)()
            self.assertNotIn("yet", str(said.exception), name)
            self.assertIn("ADR-2", str(said.exception), name)


if __name__ == "__main__":
    unittest.main()
