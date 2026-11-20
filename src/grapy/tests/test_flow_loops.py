"""D-413: two cells whose flow directions point at each other — what FlowDirection writes for a one-cell pit — are both
sinks to FlowAccumulation, FlowLength, StreamLink and StreamOrder, and Watershed follows them as they point, as ArcGIS
Pro 3.7 answers (measured 2026-11-20 with tools/pro-compare/d413-loop.py on Pro's own direction raster of the sweep's
DEM: all eight outputs equal cell for cell). grapy refused the pair as a loop before."""
import os
import shutil
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it


class TwoCellLoop(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, arcpy.env, "outputCoordinateSystem", arcpy.env.outputCoordinateSystem)
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(32635)
        # One row of four 10 m cells: c0 → c1, c1 → c2 and c2 → c1 (the pair), c3 → c2.
        self.fd = arcpy.NumPyArrayToRaster(np.array([[1, 1, 16, 16]], dtype="int32"), arcpy.Point(500000, 4500000),
                                           10, 10)

    def cells(self, raster):
        array = arcpy.RasterToNumPyArray(raster).astype("float64")
        if raster.noDataValue is not None:
            array = np.where(array == float(raster.noDataValue), np.nan, array)
        return array[0].tolist()

    def test_accumulation_and_length_treat_both_cells_as_sinks(self):
        self.assertEqual(self.cells(arcpy.sa.FlowAccumulation(self.fd)), [0.0, 1.0, 1.0, 0.0])
        self.assertEqual(self.cells(arcpy.sa.FlowLength(self.fd, "DOWNSTREAM")), [10.0, 0.0, 0.0, 10.0])

    def test_a_watershed_on_one_cell_of_the_pair_holds_the_other(self):
        gpkg = os.path.join(self.directory, "p.gpkg")
        pour = arcpy.management.CreateFeatureclass(gpkg, "pour", "POINT", spatial_reference=32635)[0]
        arcpy.management.AddField(pour, "pid", "LONG")
        with arcpy.da.InsertCursor(pour, ["pid", "SHAPE@XY"]) as cursor:
            cursor.insertRow((7, (500025.0, 4500005.0)))
        self.assertEqual(self.cells(arcpy.sa.Watershed(self.fd, pour, "pid")), [7.0, 7.0, 7.0, 7.0])


if __name__ == "__main__":
    unittest.main()
