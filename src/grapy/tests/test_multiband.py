"""arcpy.Raster on rasters of several bands, to the Raster class page (ADR-224): bandCount, bandNames, getRasterBands,
a band opened as "<path>/Band_n" (Describe's raster band page), read and write, RasterInfo, and map algebra band by band
where the tools' pages document it. Every expected value below is worked out by hand.

The rasters have 10-unit cells on EPSG:3857 with the lower-left corner at (0, 0).
"""
import json
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ia, sa  # noqa: E402
from grapy._raster import RasterInfo  # noqa: E402

ND = -1
B1, B2, B3 = [[1, 2], [3, 4]], [[10, 20], [30, 40]], [[5, 5], [5, ND]]


def grid(values, nodata=ND, x=0.0):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(x, 0.0), 10.0, 10.0, nodata)


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


def bands(raster):
    return [cells(b) for b in raster.getRasterBands()]


class Multiband(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None
        self.path = sa.CompositeBand([grid(B1), grid(B2), grid(B3)]).save(os.path.join(self.directory, "three.tif"))

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def named(self):
        import rasterio
        path = os.path.join(self.directory, "named.tif")
        with rasterio.open(path, "w", driver="GTiff", height=2, width=2, count=3, dtype="int16", nodata=-1,
                           transform=rasterio.transform.from_origin(0, 20, 10, 10), crs="EPSG:3857") as out:
            for i, (values, name) in enumerate(((B1, "Red"), (B2, "Green"), (B3, "Blue")), 1):
                out.write(np.array(values, dtype="int16"), i)
                out.set_band_description(i, name)
        return path

    # ---- the Raster object

    def test_opening_a_multiband_raster_and_its_bands(self):
        raster = arcpy.Raster(self.path)
        self.assertEqual(raster.bandCount, 3)
        self.assertEqual(raster.bandNames, ["Band_1", "Band_2", "Band_3"])
        self.assertEqual(raster.noDataValues, (-2147483648,) * 3)  # three int32 bands, saved with int32's own NoData
        self.assertEqual(bands(raster), [B1, B2, B3])
        second = arcpy.Raster(os.path.join(self.path, "Band_2"))
        self.assertEqual((second.bandCount, second.bandNames, cells(second)), (1, ["Band_2"], B2))
        self.assertEqual(cells(raster.getRasterBands(3)), B3)
        self.assertEqual([cells(b) for b in raster.getRasterBands([3, 1])], [B3, B1])
        self.assertEqual(raster.getRasterBands("band_2").catalogPath, os.path.join(self.path, "Band_2"))
        with self.assertRaises(ValueError):
            raster.getRasterBands(4)

    def test_band_names_from_the_geotiff(self):
        path = self.named()
        raster = arcpy.Raster(path)
        self.assertEqual(raster.bandNames, ["Red", "Green", "Blue"])
        self.assertEqual(cells(arcpy.Raster(os.path.join(path, "green"))), B2)
        self.assertEqual(cells(ia.ExtractBand(path, band_names="Blue")), B3)
        both = ia.ExtractBand(path, band_names=["Blue", "Red"])
        self.assertEqual((both.bandNames, bands(both)), (["Blue", "Red"], [B3, B1]))
        with self.assertRaises(arcpy.NotBuiltError):
            ia.ExtractBand(path, band_names="NIR")

    def test_statistics_and_single_band_tools_refuse_several_bands(self):
        raster = arcpy.Raster(self.path)
        with self.assertRaises(arcpy.NotBuiltError):
            raster.minimum
        self.assertEqual(raster.getRasterBands(2).maximum, 40.0)
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            sa.Slope(self.path)
        self.assertIn("3 bands", str(raised.exception))

    def test_a_single_band_raster_is_unchanged(self):
        single = grid(B1)
        self.assertEqual((single.bandCount, single.bandNames, single.noDataValues), (1, ["Band_1"], (single.noDataValue,)))
        self.assertEqual(cells(single.getRasterBands()), B1)
        self.assertEqual(single.minimum, 1.0)

    # ---- map algebra, band by band as the local tools' pages document

    def test_operators_band_by_band(self):
        raster = arcpy.Raster(self.path)
        self.assertEqual(bands(raster + 1), [[[2, 3], [4, 5]], [[11, 21], [31, 41]], [[6, 6], [6, ND]]])
        self.assertEqual(bands(raster * raster)[1], [[100, 400], [900, 1600]])
        self.assertEqual(bands(-raster)[0], [[-1, -2], [-3, -4]])
        self.assertEqual(bands(raster > 2), [[[0, 0], [1, 1]], [[1, 1], [1, 1]], [[1, 1], [1, ND]]])
        with self.assertRaises(arcpy.NotBuiltError):
            raster + grid(B1)  # a multiband raster with a single-band one: the pages are silent
        with self.assertRaises(arcpy.ExecuteError):
            raster + sa.CompositeBand([grid(B1), grid(B2)])  # band counts differ

    def test_tools_band_by_band(self):
        self.assertEqual(bands(sa.Plus(self.path, 1))[2], [[6, 6], [6, ND]])
        self.assertEqual(bands(sa.Con(arcpy.Raster(self.path) > 4, 100, 0)), [[[0, 0], [0, 0]], [[100, 100], [100, 100]],
                                                                              [[100, 100], [100, ND]]])
        self.assertEqual(bands(sa.IsNull(self.path))[2], [[0, 0], [0, 1]])
        self.assertEqual(bands(sa.SetNull(self.path, self.path, "VALUE < 3"))[0], [[ND, ND], [3, 4]])

    def test_cell_statistics_single_and_multi_band(self):
        # SINGLE_BAND: each band is an input; the NoData of band 3 is left out under DATA: 4 + 40 = 44.
        self.assertEqual(cells(sa.CellStatistics([self.path], "SUM")), [[16, 27], [38, 44]])
        # MULTI_BAND with a constant: the constant is added to each band.
        added = sa.CellStatistics([self.path, 1], "SUM", process_as_multiband="MULTI_BAND")
        self.assertEqual(bands(added), [[[2, 3], [4, 5]], [[11, 21], [31, 41]], [[6, 6], [6, 1]]])

    # ---- read and write

    def test_read_is_rows_columns_and_bands(self):
        single = grid([[1, 2], [3, ND]])
        self.assertEqual(single.read().shape, (2, 2, 1))
        self.assertEqual(single.read(nodata_to_value=0)[:, :, 0].tolist(), [[1, 2], [3, 0]])
        raster = arcpy.Raster(self.path)
        self.assertEqual(raster.read(nodata_to_value=0).tolist(), [[[1, 10, 5], [2, 20, 5]], [[3, 30, 5], [4, 40, 0]]])
        # The second column only: one column from (column 1, row 0).
        self.assertEqual(raster.read((1, 0), ncols=1, nodata_to_value=0)[:, 0, :].tolist(), [[2, 20, 5], [4, 40, 0]])
        self.assertEqual(raster.read(origin_coordinate=arcpy.Point(10, 10), nodata_to_value=0).tolist(), [[[4, 40, 0]]])
        with self.assertRaises(arcpy.NotBuiltError):
            raster.read(ncols=3)
        with self.assertRaises(arcpy.NotBuiltError):
            raster.write(np.zeros((2, 2, 3)))

    def test_raster_info_makes_an_empty_raster_to_write(self):
        info = RasterInfo()
        info.setBandCount(2)
        info.setExtent(arcpy.Extent(0, 0, 20, 10))
        info.setCellSize((10, 10))
        info.setPixelType("S16")
        info.setSpatialReference(arcpy.SpatialReference(3857))
        raster = arcpy.Raster(info)
        self.assertEqual((raster.width, raster.height, raster.bandCount, raster.readOnly), (2, 1, 2, False))
        self.assertEqual(raster.read(nodata_to_value=99).tolist(), [[[99, 99], [99, 99]]])
        raster.write(np.array([[[1, 5], [2, -32768]]]))
        self.assertEqual(bands(raster), [[[1, 2]], [[5, ND]]])
        path = raster.save(os.path.join(self.directory, "made.tif"))
        self.assertEqual(bands(arcpy.Raster(path)), [[[1, 2]], [[5, ND]]])
        info.setBandCount(1)
        one = arcpy.Raster(info)
        one[0, 1] = 7
        self.assertEqual(cells(one), [[ND, 7]])
        self.assertTrue(np.isnan(one[0, 0]))
        with self.assertRaises(RuntimeError):
            arcpy.Raster(path)[0, 0] = 1  # opened from a path: read-only

    def test_raster_info_json_both_ways(self):
        info = RasterInfo()
        info.fromJSONString(json.dumps({
            "extent": {"xmin": 0, "ymin": 0, "xmax": 30, "ymax": 20, "spatialReference": {"wkid": 3857, "latestWkid": 3857}},
            "geodataXform": {"spatialReference": {"wkid": 3857, "latestWkid": 3857}, "type": "IdentityXform"},
            "blockWidth": 128, "blockHeight": 128, "bandCount": 1, "pixelType": "F32", "noData": 3.402823e+38,
            "pixelSizeX": 10, "pixelSizeY": 10}))
        self.assertEqual((info.getBandCount(), info.getPixelType(), info.getCellSize(), info.getNoDataValues()),
                         (1, "F32", (10.0, 10.0), 3.402823e+38))
        self.assertEqual(info.getSpatialReference().factoryCode, 3857)
        self.assertEqual((info.getExtent().XMax, info.getBlockWidth()), (30, 128))
        back = json.loads(info.toJSONString())
        self.assertEqual(back["geodataXform"], {"spatialReference": {"wkid": 3857, "latestWkid": 3857}, "type": "IdentityXform"})
        self.assertEqual((back["pixelSizeX"], back["bandCount"], back["noData"]), (10.0, 1, 3.402823e+38))
        info.setBandCount(2)
        info.setNoDataValues(0)
        self.assertEqual(info.getNoDataValues(), (0, 0))
        with self.assertRaises(ValueError):
            info.setNoDataValues((1, 2, 3))
        made = arcpy.Raster(self.path).getRasterInfo()
        self.assertEqual((made.getBandCount(), made.getCellSize(), made.getPixelType()), (3, (10.0, 10.0), "S32"))

    # ---- management tools on several bands

    def test_management_tools_keep_every_band(self):
        out = os.path.join(self.directory, "clip.tif")
        arcpy.management.Clip(self.path, "0 0 10 10", out)  # the lower-left cell
        self.assertEqual(bands(arcpy.Raster(out)), [[[3]], [[30]], [[5]]])
        copied = os.path.join(self.directory, "copy.tif")
        arcpy.management.CopyRaster(self.path, copied)
        self.assertEqual(bands(arcpy.Raster(copied)), [B1, B2, B3])
        flipped = os.path.join(self.directory, "flip.tif")
        arcpy.management.Flip(self.path, flipped)
        self.assertEqual(bands(arcpy.Raster(flipped))[1], [[30, 40], [10, 20]])
        self.assertEqual(arcpy.management.GetRasterProperties(self.path, "BANDCOUNT")[0], "3")
        self.assertEqual(arcpy.management.GetRasterProperties(self.path, "MINIMUM", 2)[0], "10")
        self.assertEqual(arcpy.management.GetRasterProperties(self.path, "MAXIMUM", "Band_3")[0], "5")
        joined = os.path.join(self.directory, "joined.tif")
        arcpy.management.CompositeBands([grid(B1).save(os.path.join(self.directory, "a.tif")),
                                         grid(B2).save(os.path.join(self.directory, "b.tif"))], joined)
        self.assertEqual(bands(arcpy.Raster(joined)), [B1, B2])

    def test_mosaic_to_new_raster_band_by_band(self):
        left = sa.CompositeBand([grid([[1], [2]]), grid([[7], [8]])]).save(os.path.join(self.directory, "l.tif"))
        right = sa.CompositeBand([grid([[3], [4]], x=10.0), grid([[9], [6]], x=10.0)]).save(
            os.path.join(self.directory, "r.tif"))
        arcpy.management.MosaicToNewRaster([left, right], self.directory, "m.tif", None, "8_BIT_UNSIGNED", None, 2)
        self.assertEqual(bands(arcpy.Raster(os.path.join(self.directory, "m.tif"))), [[[1, 3], [2, 4]], [[7, 9], [8, 6]]])
        with self.assertRaises(arcpy.NotBuiltError):
            arcpy.management.MosaicToNewRaster([left, right], self.directory, "n.tif", None, "8_BIT_UNSIGNED", None, 1)


if __name__ == "__main__":
    unittest.main()
