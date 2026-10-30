import os
import sys
import tempfile
import unittest
import warnings

import numpy

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402


class DefineProjectionOnARaster(unittest.TestCase):
    """DefineProjection records a raster's coordinate system and leaves its cells and grid as they are, as it does a
    feature class's (found 2026-10-30: it answered that a .tif is not a GeoPackage feature class)."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.outputCoordinateSystem = None
        arcpy.NumPyArrayToRaster(numpy.arange(6, dtype="float32").reshape(2, 3), arcpy.Point(500000, 4000000), 10, 10) \
            .save(os.path.join(self.directory, "bare.tif"))

    def test_a_raster_without_one_is_given_it(self):
        arcpy.management.DefineProjection("bare.tif", arcpy.SpatialReference(32635))
        raster = arcpy.Raster(os.path.join(self.directory, "bare.tif"))
        self.assertEqual(raster.spatialReference.factoryCode, 32635)
        self.assertEqual((raster.extent.XMin, raster.extent.YMin), (500000, 4000000))
        self.assertEqual(arcpy.RasterToNumPyArray(raster)[1, 2], 5)

    def test_a_raster_with_one_has_it_replaced_with_a_warning(self):
        path = os.path.join(self.directory, "bare.tif")
        arcpy.management.DefineProjection(path, arcpy.SpatialReference(32635))
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            arcpy.management.DefineProjection(path, arcpy.SpatialReference(32636))
        self.assertEqual(arcpy.Raster(path).spatialReference.factoryCode, 32636)
        self.assertIn("001319", arcpy.GetMessages(1))


if __name__ == "__main__":
    unittest.main()
