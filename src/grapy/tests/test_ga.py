"""arcpy.ga in grapy, against answers worked out by hand — ADR-224.

Two points, (0,0) holding 0 and (100,100) holding 100, on a 50-unit grid over their extent: cell centres (25,75) and
(75,75) in the north row, (25,25) and (75,25) in the south row.
"""
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.ga as ga  # noqa: E402


def cells(path, nodata=-1):
    return [[round(v, 4) for v in row] for row in arcpy.RasterToNumPyArray(arcpy.Raster(path), nodata_to_value=nodata).tolist()]


class Ga(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None
        self.two = self.points("two", [(0, 0, 0.0), (100, 100, 100.0)])

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def points(self, name, rows):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(path, "z", "DOUBLE")
        with arcpy.da.InsertCursor(path, ["z", "SHAPE@"]) as written:
            for x, y, z in rows:
                written.insertRow((z, arcpy.PointGeometry(arcpy.Point(x, y), 3857)))
        return path

    def out(self, name):
        return os.path.join(self.directory, name)

    def test_idw_to_a_raster_with_a_circular_neighbourhood(self):
        # Radius 200 reaches both points from every centre. Power 2: at (25,25), d² = 1250 to (0,0) and 11250 to
        # (100,100): 100·(1/11250) / (1/1250 + 1/11250) = 10; (75,75) mirrors it at 90; the other two are
        # equidistant, 50.
        circle = ga.SearchNeighborhoodStandardCircular(200, 0, 2, 1, "ONE_SECTOR")
        result = ga.IDW(self.two, "z", "", self.out("idw.tif"), 50, 2, circle)
        self.assertEqual(result.getOutput(0), "")
        self.assertEqual(cells(result.getOutput(1)), [[50.0, 90.0], [10.0, 50.0]])
        # One neighbour at most: (75,75) takes (100,100) alone and (25,25) takes (0,0) alone.
        one = ga.IDW(self.two, "z", "", self.out("one.tif"), 50, 2, ga.SearchNeighborhoodStandard(200, 200, 0, 1, 1))
        got = cells(one.getOutput(1))
        self.assertEqual((got[0][1], got[1][0]), (100.0, 0.0))

    def test_idw_refuses_what_it_cannot_answer_as_documented(self):
        circle = ga.SearchNeighborhoodStandardCircular(200, 0, 2, 1, "ONE_SECTOR")
        # Radius 40, nbrMin 2: (25,25) holds (0,0) alone inside the circle and takes the nearest outside, (100,100),
        # as GA Neighborhood Selection documents — so the answer is the radius-200 one; (25,75) and (75,25) find none
        # inside and take both.
        near = ga.IDW(self.two, "z", "", self.out("r.tif"), 50, 2, ga.SearchNeighborhoodStandardCircular(40, 0, 2, 2))
        self.assertEqual(cells(near.getOutput(1)), [[50.0, 90.0], [10.0, 50.0]])
        with self.assertRaisesRegex(ValueError, "out_ga_layer, out_raster"):
            ga.IDW(self.two, "z", "", "", 50, 2, circle)
        refusals = [
            ({"power": None}, "power"),
            ({"search_neighborhood": None}, "search_neighborhood"),
            ({"search_neighborhood": ga.SearchNeighborhoodSmoothCircular(200, 0.5)}, "sigmoidal"),
            ({"search_neighborhood": ga.SearchNeighborhoodStandardCircular(200, 0, 2, 1, "FOUR_SECTORS")}, "sectorType"),
            ({"weight_field": "z"}, "weight_field"),
        ]
        for change, said in refusals:
            arguments = {"out_ga_layer": "", "out_raster": self.out("x.tif"), "cell_size": 50, "power": 2,
                         "search_neighborhood": circle, **change}
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                ga.IDW(self.two, "z", **arguments)

    def test_global_polynomial_fits_by_least_squares(self):
        # Four corners of z = 2x + 3y + 1 at 0 and 20; 10-unit cells, centres (5,15) (15,15) / (5,5) (15,5).
        plane = self.points("plane", [(x, y, 2 * x + 3 * y + 1) for x in (0, 20) for y in (0, 20)])
        result = ga.GlobalPolynomialInterpolation(plane, "z", "", self.out("gpi.tif"), 10, 1)
        self.assertEqual(cells(result.getOutput(1)), [[56.0, 76.0], [26.0, 46.0]])
        # Second order through z = x² sampled at 0, 10, 20 both ways: the cell centred at x = 5 is 25.
        square = self.points("square", [(x, y, x * x) for x in (0, 10, 20) for y in (0, 10, 20)])
        second = ga.GlobalPolynomialInterpolation(square, "z", "", self.out("gpi2.tif"), 10, 2)
        self.assertAlmostEqual(cells(second.getOutput(1))[1][0], 25.0, places=3)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "weight_field"):
            ga.GlobalPolynomialInterpolation(plane, "z", "", self.out("w.tif"), 10, 1, "z")

    def test_search_neighbourhoods_hold_what_they_are_given(self):
        standard = ga.SearchNeighborhoodStandard(10, 5, 45, 15, 10, "FOUR_SECTORS")
        self.assertEqual((standard.majorSemiaxis, standard.minorSemiaxis, standard.angle, standard.nbrMax,
                          standard.nbrMin, standard.sectorType), (10, 5, 45, 15, 10, "FOUR_SECTORS"))
        self.assertEqual(ga.SearchNeighborhoodStandard3D(7, 4, 2, "SIX_SECTORS").sectorType, "SIX_SECTORS")
        self.assertEqual(ga.SearchNeighborhoodSmooth(10, 5, 0, 0.2).smoothFactor, 0.2)

    def test_layer_and_kriging_tools_are_refused_with_their_reason(self):
        for name, said in (("GALayer3DToNetCDF", "3D geostatistical layer"), ("EmpiricalBayesianKriging", "semivariogram"),
                           ("SubsetFeatures", "random"), ("DirectionalTrend", "active map")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                getattr(ga, name)()


if __name__ == "__main__":
    unittest.main()
