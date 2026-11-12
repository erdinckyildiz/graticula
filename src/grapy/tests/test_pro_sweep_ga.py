"""arcpy.ga aligned to ArcGIS Pro 3.7 by the Pro sweep (tools/pro-compare/sweep, case module ga). Each test pins an
answer measured in ArcGIS Pro 3.7 on 2026-11-08 with an example small enough to work by hand."""
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.ga as ga  # noqa: E402


class _Points(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self._old = arcpy.env.workspace
        arcpy.env.workspace = self.directory
        arcpy.env.overwriteOutput = True
        self.gpkg = arcpy.management.CreateSQLiteDatabase(os.path.join(self.directory, "store.gpkg"), "GEOPACKAGE")[0]
        self.sr = arcpy.SpatialReference(32635)

    def tearDown(self):
        arcpy.env.workspace = self._old
        shutil.rmtree(self.directory, ignore_errors=True)

    def points(self, name, rows, has_z="DISABLED"):
        fc = arcpy.management.CreateFeatureclass(self.gpkg, name, "POINT", has_z=has_z, spatial_reference=self.sr)[0]
        for field, kind in (("val", "DOUBLE"), ("cat", "TEXT")):
            arcpy.management.AddField(fc, field, kind)
        with arcpy.da.InsertCursor(fc, ["val", "cat", "SHAPE@"]) as cursor:
            for x, y, z, val, cat in rows:
                point = arcpy.Point(x, y, z) if z is not None else arcpy.Point(x, y)
                cursor.insertRow((val, cat, arcpy.PointGeometry(point, self.sr, z is not None)))
        return fc

    def out(self, name):
        return os.path.join(self.gpkg, name)

    def fields(self, path):
        return [f.name for f in arcpy.ListFields(path) if f.type not in ("OID", "Geometry")]

    def rows(self, path, fields):
        with arcpy.da.SearchCursor(path, fields) as cursor:
            return [tuple(r) for r in cursor]

    def layer(self, points, power=2):
        path = os.path.join(self.directory, f"idw{power}.lyrx")
        ga.IDW(points, "val", path, power=power, search_neighborhood=ga.SearchNeighborhoodStandardCircular(500, 0, 10, 1))
        return path


THREE = [(0, 0, None, 0.0, "a"), (26, 0, None, 50.0, "b"), (0, 14, None, 100.0, "a")]


class Grid(_Points):
    def test_the_first_centre_is_on_the_lower_left_point_with_round_plus_one_cells(self):
        # Points 26 wide and 14 high, 10 m cells: round(2.6) + 1 = 4 columns and round(1.4) + 1 = 2 rows (Pro: 55
        # columns for 53.61 cells' width and 50 rows for 49.44 at 10 m on the sweep's points); the grid from (-5, -5)
        # to (35, 15). grapy had put the top-left corner on the points' (0, 14) with ceil(2.6) = 3 columns.
        points = self.points("three", THREE)
        raster = arcpy.Raster(ga.IDW(points, "val", "", os.path.join(self.directory, "idw.tif"), 10, 2,
                                     ga.SearchNeighborhoodStandardCircular(500, 0, 10, 1))[1])
        e = raster.extent
        self.assertEqual((raster.width, raster.height), (4, 2))
        self.assertEqual([round(v, 6) for v in (e.XMin, e.YMin, e.XMax, e.YMax)], [-5, -5, 35, 15])
        # The centre (0, 0) is the point itself: IDW is exact there.
        cells = arcpy.RasterToNumPyArray(raster)
        self.assertAlmostEqual(float(cells[1][0]), 0.0)


class LayerToPoints(_Points):
    def test_all_writes_the_locations_fields_then_included_predicted_error_and_no_source_id(self):
        points = self.points("three", THREE)
        out = ga.GALayerToPoints(self.layer(points), points, "val", self.out("all"), "ALL")[0]
        self.assertEqual(self.fields(out), ["val", "cat", "Included", "Predicted", "Error"])
        # IDW is exact at its own points: each prediction is the value, the error 0.
        self.assertEqual(self.rows(out, ["Included", "Predicted", "Error"]),
                         [("Yes", 0.0, 0.0), ("Yes", 50.0, 0.0), ("Yes", 100.0, 0.0)])

    def test_fid_only_writes_the_z_field_predicted_error_source_id_included(self):
        points = self.points("three", THREE)
        out = ga.GALayerToPoints(self.layer(points), points, "val", self.out("fid"), "FID_ONLY")[0]
        self.assertEqual(self.fields(out), ["val", "Predicted", "Error", "Source_ID", "Included"])
        self.assertEqual(self.rows(out, ["Source_ID"]), [(1,), (2,), (3,)])
        bare = ga.GALayerToPoints(self.layer(points), points, None, self.out("bare"), "FID_ONLY")[0]
        self.assertEqual(self.fields(bare), ["Predicted", "Source_ID", "Included"])


class LayerToContour(_Points):
    def test_one_feature_per_value_with_classes_and_value(self):
        # A plane rising 1 a metre eastwards: the breaks 10 and 20 are each one line, north to south. Pro 3.7 writes
        # each break as one feature, Classes 1, 2 … from the lowest, its value in Value.
        plane = self.points("plane", [(x, y, None, float(x), "a") for x in (0, 30) for y in (0, 30)])
        layer = os.path.join(self.directory, "gpi.lyrx")
        ga.GlobalPolynomialInterpolation(plane, "val", layer, power=1)
        out = ga.GALayerToContour(layer, "CONTOUR", self.out("contours"), "DRAFT", "MANUAL", None, [20, 10])[0]
        self.assertEqual(self.fields(out), ["Classes", "Value"])
        rows = self.rows(out, ["Classes", "Value", "SHAPE@"])
        self.assertEqual([(c, v) for c, v, _ in rows], [(1, 10.0), (2, 20.0)])
        for _, value, shape in rows:
            xs = [p.X for part in shape for p in part]
            self.assertAlmostEqual(min(xs), value, places=4)
            self.assertAlmostEqual(max(xs), value, places=4)


class Compare(_Points):
    def test_the_table_is_pros_with_perc_error_and_rows_in_rank_order(self):
        # Four points; leave-one-out IDW of power 1 and 2. PERC_ERROR = (1 − RMSE / SD) × 100 with SD the sample
        # standard deviation: Pro answered −12.143 for RMSE 29.921 over SD 26.681 in the sweep.
        points = self.points("four", [(0, 0, None, 0.0, "a"), (10, 0, None, 10.0, "a"), (0, 10, None, 30.0, "a"),
                                      (10, 10, None, 20.0, "a")])
        first, second = self.layer(points, 1), self.layer(points, 2)
        table = ga.CompareGeostatisticalLayers([second, first], self.out("cmp"), None, "SINGLE", "ACCURACY")[0]
        self.assertEqual(self.fields(table), ["GA_LYR", "RANK", "DESCR", "INCLUDED", "RMSE", "ME", "MAX_ERROR",
                                              "PERC_ERROR"])
        rows = self.rows(table, ["GA_LYR", "RANK", "DESCR", "INCLUDED", "RMSE", "PERC_ERROR"])
        self.assertEqual([r[1] for r in rows], [1, 2])
        self.assertEqual({r[2] for r in rows}, {"Inverse Distance Weighted"})
        sd = (sum((v - 15.0) ** 2 for v in (0.0, 10.0, 30.0, 20.0)) / 3) ** 0.5
        for row in rows:
            self.assertAlmostEqual(row[5], (1 - row[4] / sd) * 100, places=9)
        average = ga.CompareGeostatisticalLayers([first, second], self.out("avg"), None, "AVERAGE_RANK", None, None,
                                                 [["ACCURACY", 1], ["BIAS", 2], ["WORST_CASE", 1]])[0]
        self.assertEqual(self.fields(average)[-4:], ["RMS_RANK", "ME_RANK", "MAXE_RANK", "AVE_RANK"])


class ModelParameter(_Points):
    def test_an_idw_layers_power_is_answered_as_pro_writes_it(self):
        points = self.points("three", THREE)
        layer = self.layer(points, 3)
        path = "/model[@name = 'IDW']/value[@name = 'Power']"
        self.assertEqual(ga.GAGetModelParameter(layer, path)[0], "3")
        model = os.path.join(self.directory, "power1.xml")
        ga.GASetModelParameter(layer, path, "1", model)
        self.assertEqual(ga.GAGetModelParameter(model, path)[0], "1")


class Voxels(_Points):
    def test_idw3d_writes_z_y_x_and_cross_validation_with_source_id_and_included(self):
        from scipy.io import netcdf_file
        points = self.points("p3", [(0, 0, 0, 1.0, "a"), (10, 0, 0, 2.0, "b"), (0, 10, 5, 3.0, "a"),
                                    (10, 10, 5, 4.0, "b")], has_z="ENABLED")
        nc = os.path.join(self.directory, "v.nc")
        ga.IDW3D(points, "val", nc, 2, 1, self.out("cv"), "10 Meters", "10 Meters", "5 Meters", None, None, None,
                 ga.SearchNeighborhoodStandard3D(100, 10, 1))
        data = netcdf_file(nc, "r", mmap=False)
        self.assertEqual(data.variables["val"].dimensions, ("z", "y", "x"))
        self.assertEqual(list(data.variables["z"][:]), [0.0, 5.0])
        data.close()
        self.assertEqual(self.fields(self.out("cv")), ["Measured", "Predicted", "Error", "Source_ID", "Included"])
        self.assertEqual(self.rows(self.out("cv"), ["Source_ID", "Included"]), [(1, "Yes"), (2, "Yes"), (3, "Yes"),
                                                                                (4, "Yes")])


if __name__ == "__main__":
    unittest.main()
