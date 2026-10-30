"""Geostatistical layers in grapy (ADR-233), against answers worked out by hand or in closed form.

Kriging cases are small enough to solve on paper: two points and the midpoint between them (ordinary), one point and a
known mean (simple), a plane that universal kriging must reproduce exactly. Semivariogram models are checked at t = h/a
= 0.5 and 1 against their formulas; the fit against a set whose weighted least squares optimum is a fraction.
"""
import json
import math
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
import grapy.ga as ga  # noqa: E402
from grapy import _geostat  # noqa: E402


def cells(path):
    return [[round(v, 4) for v in row] for row in arcpy.RasterToNumPyArray(arcpy.Raster(path), nodata_to_value=-1).tolist()]


def spherical(h, a=100.0, c=1.0):
    t = min(h / a, 1.0)
    return c * (1.5 * t - 0.5 * t ** 3)


class Geostat(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def points(self, name, rows, extra=None):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(path, "z", "DOUBLE")
        if extra:
            arcpy.management.AddField(path, extra, "DOUBLE")
        with arcpy.da.InsertCursor(path, ["z", "SHAPE@"] + ([extra] if extra else [])) as written:
            for row in rows:
                x, y, z = row[:3]
                written.insertRow((z, arcpy.PointGeometry(arcpy.Point(x, y), 3857), *row[3:]))
        return path

    def model(self, name, method, parameters, neighborhood=None):
        path = os.path.join(self.directory, name + ".json")
        with open(path, "w", encoding="utf-8") as out:
            json.dump({"format": "grapy-geostatistical-layer", "version": 1, "method": method,
                       "parameters": parameters, "neighborhood": neighborhood}, out)
        return path

    def kriging(self, name, data, semivariogram, kind="ORDINARY", **more):
        source = self.model(name + "_model", "KRIGING", {"kind": kind, "semivariogram": semivariogram, **more})
        return ga.GACreateGeostatisticalLayer(source, f"{data} X=Shape Y=Shape F1=z", name).getOutput(0)

    def out(self, name):
        return os.path.join(self.directory, name)

    # ---- semivariogram models

    def test_semivariogram_models_answer_their_formulas(self):
        half = {"SPHERICAL": 0.6875, "PENTASPHERICAL": 15 / 16 - 5 / 32 + 3 / 256,
                "TETRASPHERICAL": (2 / math.pi) * (math.pi / 6 + 0.5 * math.sqrt(0.75) + (2 / 3) * 0.5 * 0.75 ** 1.5),
                "CIRCULAR": 1 - (2 / math.pi) * math.acos(0.5) + (2 / math.pi) * 0.5 * math.sqrt(0.75),
                "EXPONENTIAL": 1 - math.exp(-1.5), "GAUSSIAN": 1 - math.exp(-0.75),
                "RATIONAL_QUADRATIC": 4.75 / 5.75}
        for model, expected in half.items():
            self.assertAlmostEqual(float(_geostat.shape(model, 0.5)), expected, places=12, msg=model)
        for model in ("SPHERICAL", "PENTASPHERICAL", "TETRASPHERICAL", "CIRCULAR"):
            self.assertAlmostEqual(float(_geostat.shape(model, 1.0)), 1.0, places=12)
            self.assertAlmostEqual(float(_geostat.shape(model, 1.7)), 1.0, places=12)
        # Stable with α = 1 is the exponential, α = 2 the Gaussian.
        self.assertAlmostEqual(float(_geostat.shape("STABLE", 0.5, 1)), 1 - math.exp(-1.5), places=12)
        self.assertAlmostEqual(float(_geostat.shape("STABLE", 0.5, 2)), 1 - math.exp(-0.75), places=12)
        # K-Bessel with ν = 1/2 is e^{−Ωt}, Ω = ln 20 so that the range holds 95 % of the sill.
        self.assertAlmostEqual(float(_geostat.shape("K_BESSEL", 1.0, 0.5)), 0.95, places=9)
        self.assertAlmostEqual(float(_geostat.shape("K_BESSEL", 0.5, 0.5)), 1 - math.sqrt(0.05), places=9)
        for model, said in (("J_BESSEL", "Bessel function's argument"), ("HOLE_EFFECT", "sine's period")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                _geostat.shape(model, 0.5)
        # γ(0) = 0 whatever the nugget; beyond 0 the nugget is added; anisotropy divides by the minor range across.
        model = {"model": "SPHERICAL", "range": 100, "partialSill": 1, "nugget": 0.25}
        self.assertEqual(float(_geostat.gamma(model, 0, 0)), 0.0)
        self.assertAlmostEqual(float(_geostat.gamma(model, 50, 0)), 0.25 + 0.6875, places=12)
        across = {**model, "minorRange": 50, "direction": 0}   # major axis north–south
        self.assertAlmostEqual(float(_geostat.gamma(across, 0, 50)), 0.25 + 0.6875, places=12)
        self.assertAlmostEqual(float(_geostat.gamma(across, 25, 0)), 0.25 + 0.6875, places=12)

    def test_the_empirical_semivariogram_and_its_weighted_least_squares_fit(self):
        # z = 0, 1, 0, 1 along a line: lag 1 holds three pairs differing by 1 (γ̂ = 0.5), lag 2 two equal pairs (0),
        # lag 3 one pair differing by 1 (0.5).
        xs, ys, zs = (_geostat.np.array(v, dtype=float) for v in ([0, 1, 2, 3], [0, 0, 0, 0], [0, 1, 0, 1]))
        model = {"model": "SPHERICAL", "range": 0.5, "partialSill": 0.2, "nugget": None}
        got = _geostat.empirical(model, xs, ys, zs, 1.0, 3)
        self.assertEqual([(round(h, 9), round(g, 9), n) for h, g, n in got], [(1.0, 0.5, 3), (2.0, 0.0, 2), (3.0, 0.5, 1)])
        # Every lag is past the range, so γ is the constant c₀ + 0.2; Cressie's cost 3(0.5/γ − 1)² + 2 + (0.5/γ − 1)²
        # is least at γ = 0.5: the nugget is 0.3.
        fitted = _geostat.fit(model, xs, ys, zs, 1.0, 3)
        self.assertAlmostEqual(fitted["nugget"], 0.3, places=6)
        self.assertEqual(fitted["auto"], {"range": False, "partialSill": False, "nugget": True})

    # ---- kriging

    def test_ordinary_kriging_at_the_midpoint_of_two_points(self):
        # Spherical, range 100, sill 1, no nugget; (0,0) = 2 and (60,0) = 8. By symmetry λ = ½ each: 5. The variance
        # is 2γ(30) − γ(60)/2.
        data = self.points("two", [(0, 0, 2.0), (60, 0, 8.0)])
        layer = self.kriging("ok", data, {"model": "SPHERICAL", "range": 100, "partialSill": 1, "nugget": 0})
        self.assertAlmostEqual(ga.GACalculateZValue(layer, "30 0").getOutput(0), 5.0, places=10)
        prediction, error = _geostat.open_layer(layer, "t").predict(30, 0)
        self.assertAlmostEqual(error ** 2, 2 * spherical(30) - spherical(60) / 2, places=10)
        # At a data point kriging returns the datum and no error — with a nugget too, γ(0) being 0.
        self.assertEqual(_geostat.open_layer(layer, "t").predict(60, 0), (8.0, 0.0))
        nugget = self.kriging("okn", data, {"model": "SPHERICAL", "range": 100, "partialSill": 1, "nugget": 0.5})
        value, error = _geostat.open_layer(nugget, "t").predict(0, 0)
        self.assertAlmostEqual(value, 2.0, places=10)
        self.assertAlmostEqual(error, 0.0, places=6)

    def test_simple_kriging_from_one_point_and_a_known_mean(self):
        # Exponential, range 30, sill 2; one point 10 at the origin, mean 4; at distance 10, C = 2e⁻¹ so λ = e⁻¹:
        # 4 + 6e⁻¹, variance 2 − 2e⁻².
        data = self.points("one", [(0, 0, 10.0)])
        layer = self.kriging("sk", data, {"model": "EXPONENTIAL", "range": 30, "partialSill": 2, "nugget": 0},
                             kind="SIMPLE", mean=4)
        value, error = _geostat.open_layer(layer, "t").predict(10, 0)
        self.assertAlmostEqual(value, 4 + 6 * math.exp(-1), places=10)
        self.assertAlmostEqual(error ** 2, 2 - 2 * math.exp(-2), places=10)

    def test_universal_kriging_reproduces_a_plane(self):
        # Linear drift: the weights sum to 1 and reproduce x and y, so data on z = 2x + 3y + 1 are reproduced
        # everywhere.
        rows = [(x, y, 2 * x + 3 * y + 1) for x, y in ((0, 0), (40, 5), (10, 30), (35, 40), (20, 20), (5, 45))]
        layer = self.kriging("uk", self.points("plane", rows), {"model": "GAUSSIAN", "range": 50, "partialSill": 3,
                                                                "nugget": 0}, kind="UNIVERSAL", drift="LINEAR")
        for x, y in ((12, 17), (30, 2), (44, 44)):
            self.assertAlmostEqual(ga.GACalculateZValue(layer, (x, y)).getOutput(0), 2 * x + 3 * y + 1, places=6)

    def test_cokriging_with_no_cross_covariance_is_ordinary_kriging(self):
        primary = self.points("p", [(0, 0, 1.0), (50, 10, 4.0), (20, 60, 2.0), (70, 70, 6.0)])
        secondary = self.points("s", [(10, 10, 100.0), (60, 40, -50.0), (30, 30, 7.0)])
        direct = {"model": "SPHERICAL", "range": 80, "partialSill": 2, "nugget": 0}
        ok = self.kriging("ok4", primary, direct)
        source = self.model("ck_model", "COKRIGING", {"semivariograms": {
            "primary": direct, "secondary": {"model": "EXPONENTIAL", "range": 40, "partialSill": 900, "nugget": 0},
            "cross": {"model": "SPHERICAL", "range": 80, "partialSill": 0, "nugget": 0}}})
        datasets = f"{primary} X=Shape Y=Shape F1=z;{secondary} X=Shape Y=Shape F1=z"
        ck = ga.GACreateGeostatisticalLayer(source, datasets, "ck").getOutput(0)
        for place in ((25, 25), (60, 5), (50, 10)):
            a = _geostat.open_layer(ok, "t").predict(*place)
            b = _geostat.open_layer(ck, "t").predict(*place)
            self.assertAlmostEqual(a[0], b[0], places=8)
            self.assertAlmostEqual(a[1], b[1], places=8)
        self.assertEqual(ga.GeostatisticalDatasets(ck).dataset2, secondary)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "cross-semivariogram"):
            bad = self.model("ck_bad", "COKRIGING", {"semivariograms": {"primary": direct, "secondary": direct,
                                                                       "cross": {"model": "SPHERICAL"}}})
            ga.GACreateGeostatisticalLayer(bad, datasets, "ck2")

    def test_a_model_fitted_to_data_and_its_parameters_by_path(self):
        rows = [(x * 10.0, y * 10.0, math.sin(x / 2.0) + math.cos(y / 3.0)) for x in range(6) for y in range(6)]
        data = self.points("grid", rows)
        layer = self.kriging("fit", data, {"model": "SPHERICAL"})
        nugget = "/model[@name = 'Kriging']/model[@name = 'Variogram']/value[@name = 'Nugget']"
        sill = "/model[@name = 'Kriging']/model[@name = 'Variogram']/model[@name = 'VariogramModel']/value[@name = 'Sill']"
        fitted = _geostat.open_layer(layer, "t").parameters["semivariogram"]
        self.assertEqual(ga.GAGetModelParameter(layer, f"{nugget};{sill}").getOutput(0),
                         f"{fitted['nugget']!r};{fitted['partialSill']!r}")
        self.assertEqual(ga.GAGetModelParameter(layer, nugget + "/@auto").getOutput(0), "true")
        changed = ga.GASetModelParameter(layer, nugget, "1", self.out("changed.json")).getOutput(0)
        self.assertEqual(ga.GAGetModelParameter(changed, nugget).getOutput(0), "1.0")
        self.assertEqual(ga.GAGetModelParameter(changed, nugget + "/@auto").getOutput(0), "false")
        # A held nugget stays when the model makes a layer again; the auto range and sill are fitted again.
        again = ga.GACreateGeostatisticalLayer(changed, ga.GeostatisticalDatasets(layer), "again").getOutput(0)
        self.assertEqual(_geostat.open_layer(again, "t").parameters["semivariogram"]["nugget"], 1.0)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "model_param_xpath"):
            ga.GAGetModelParameter(layer, "/model[@name = 'Kriging']/value[@name = 'Shape']")
        with open(self.out("esri.xml"), "w") as out:
            out.write("<model name='Kriging'/>")
        with self.assertRaisesRegex(arcpy.NotBuiltError, "not a published format"):
            ga.GALayerToGrid(self.out("esri.xml"), self.out("g.tif"))
        with self.assertRaisesRegex(ValueError, "no data"):
            ga.GALayerToGrid(self.model("bare", "KRIGING", {}), self.out("g.tif"))

    # ---- the interpolators as layers

    def test_idw_as_a_layer_and_its_grid(self):
        data = self.points("two", [(0, 0, 0.0), (100, 100, 100.0)])
        circle = ga.SearchNeighborhoodStandardCircular(200, 0, 2, 1, "ONE_SECTOR")
        result = ga.IDW(data, "z", "idw_layer", self.out("idw.tif"), 50, 2, circle)
        self.assertEqual(result.getOutput(0), "idw_layer")
        grid = ga.GALayerToGrid("idw_layer", self.out("grid.tif"), 50).getOutput(0)
        self.assertEqual(cells(grid), cells(result.getOutput(1)))
        self.assertAlmostEqual(ga.GACalculateZValue("idw_layer", "25 25").getOutput(0), 10.0, places=10)
        saved = ga.IDW(data, "z", self.out("idw_layer.json"), "", 50, 2, circle).getOutput(0)
        self.assertEqual(json.load(open(saved))["parameters"], {"power": 2.0})
        datasets = ga.GeostatisticalDatasets(saved)
        self.assertEqual((datasets.dataset1, datasets.dataset1Field, datasets.dataset1WeightField), (data, "z", ""))

    def test_block_averaging_takes_the_mean_of_each_cells_predictions(self):
        # Second order through z = x² on 0, 10, 20: the cell centred at x = 5 is 25 with one prediction, and
        # (2.5² + 7.5²) / 2 = 31.25 with two across.
        square = self.points("square", [(x, y, x * x) for x in (0, 10, 20) for y in (0, 10, 20)])
        ga.GlobalPolynomialInterpolation(square, "z", "gpi", "", None, 2)
        one = cells(ga.GALayerToGrid("gpi", self.out("one.tif"), 10).getOutput(0))
        two = cells(ga.GALayerToGrid("gpi", self.out("two.tif"), 10, 2, 1).getOutput(0))
        self.assertAlmostEqual(one[1][0], 25.0, places=3)
        self.assertAlmostEqual(two[1][0], 31.25, places=3)

    def test_local_polynomial_and_radial_basis_functions(self):
        plane = self.points("plane", [(x, y, 2 * x + 3 * y + 1) for x in (0, 10, 20, 30) for y in (0, 10, 20, 30)])
        circle = ga.SearchNeighborhoodStandardCircular(100, 0, 16, 3)
        # A first-order local fit of a plane is the plane, whatever the kernel weights.
        ga.LocalPolynomialInterpolation(plane, "z", "lpi", "", None, 1, circle, "EPANECHNIKOV", 25)
        self.assertAlmostEqual(ga.GACalculateZValue("lpi", "12 7").getOutput(0), 2 * 12 + 3 * 7 + 1, places=8)
        # Order 0 with the constant kernel is the mean of the points within the bandwidth: at (0,0) with bandwidth
        # 10, the points (0,0) (10,0) (0,10) — z 1, 21, 31 — mean 53/3.
        ga.LocalPolynomialInterpolation(plane, "z", "lpi0", "", None, 0, circle, "CONSTANT", 10)
        self.assertAlmostEqual(ga.GACalculateZValue("lpi0", "0 0").getOutput(0), 53 / 3, places=8)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "kernel_function"):
            ga.LocalPolynomialInterpolation(plane, "z", "x", "", None, 1, circle, "GAUSSIAN", 25)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "bandwidth"):
            ga.LocalPolynomialInterpolation(plane, "z", "x", "", None, 1, circle, "QUARTIC")
        # Radial basis functions are exact at the points; the thin-plate spline with its linear trend is the plane.
        for kind in ("THIN_PLATE_SPLINE", "MULTIQUADRIC_FUNCTION", "INVERSE_MULTIQUADRIC_FUNCTION",
                     "COMPLETELY_REGULARIZED_SPLINE", "SPLINE_WITH_TENSION"):
            ga.RadialBasisFunctions(plane, "z", "rbf", "", None, circle, kind, 0.05)
            self.assertAlmostEqual(ga.GACalculateZValue("rbf", "10 20").getOutput(0), 81.0, places=6, msg=kind)
        ga.RadialBasisFunctions(plane, "z", "tps", "", None, circle, "THIN_PLATE_SPLINE", 1)
        self.assertAlmostEqual(ga.GACalculateZValue("tps", "13 4").getOutput(0), 2 * 13 + 3 * 4 + 1, places=6)
        # Two points: by symmetry every function answers their mean halfway.
        two = self.points("two", [(0, 0, 3.0), (10, 0, 7.0)])
        ga.RadialBasisFunctions(two, "z", "mq", "", None, ga.SearchNeighborhoodStandardCircular(50, 0, 2, 1),
                                "MULTIQUADRIC_FUNCTION", 4)
        self.assertAlmostEqual(ga.GACalculateZValue("mq", "5 0").getOutput(0), 5.0, places=10)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "small_scale_parameter"):
            ga.RadialBasisFunctions(plane, "z", "x", "", None, circle, "THIN_PLATE_SPLINE", "")

    def test_an_elliptical_neighbourhood_and_neighbourhood_selection(self):
        rows = [(10, 0, 1.0), (0, 10, 2.0), (30, 0, 3.0), (0, 30, 4.0), (60, 0, 5.0)]
        data = self.points("cross", rows)
        # Major axis east–west (angle 90, clockwise from north), semiaxes 40 and 15: inside are (10,0), (0,10),
        # (30,0); with nbrMax 2 the nearest by the ellipse's measure, (10,0) at 0.25 and (0,10) at 0.667.
        ga.GANeighborhoodSelection(data, "picked", "0 0", 2, 1, 15, 40, 90, "ONE_SECTOR")
        with arcpy.da.SearchCursor("picked", ["z"]) as found:
            self.assertEqual(sorted(r[0] for r in found), [1.0, 2.0])
        # nbrMin 5 adds the nearest outside the ellipse: (0,30) at 2.0 and (60,0) at 1.5.
        ga.GANeighborhoodSelection(data, "all", "0 0", 3, 5, 15, 40, 90)
        self.assertEqual(int(arcpy.management.GetCount("all").getOutput(0)), 5)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "sector"):
            ga.GANeighborhoodSelection(data, "s", "0 0", 3, 1, 15, 40, 90, "FOUR_SECTORS")

    # ---- outputs

    def test_rasters_points_and_contours_from_a_kriging_layer(self):
        rows = [(0, 0, 1.0), (100, 0, 3.0), (0, 100, 5.0), (100, 100, 2.0), (50, 50, 4.0)]
        data = self.points("five", rows)
        layer = self.kriging("k5", data, {"model": "EXPONENTIAL", "range": 120, "partialSill": 2, "nugget": 0.1})
        made = ga.GALayerToRasters(layer, self.out("pred.tif"), "PREDICTION", None, 50, 1, 1,
                                   [[self.out("se.tif"), "PREDICTION_STANDARD_ERROR", ""],
                                    [self.out("q.tif"), "QUANTILE", 0.5],
                                    [self.out("p.tif"), "PROBABILITY", 3]])
        written = made.getOutput(1).split(";")
        prediction, error = _geostat.open_layer(layer, "t").predict(25, 75)
        self.assertAlmostEqual(cells(made.getOutput(0))[0][0], round(prediction, 4), places=3)
        self.assertAlmostEqual(cells(written[0])[0][0], round(error, 4), places=3)
        self.assertEqual(cells(written[1]), cells(made.getOutput(0)))
        from scipy.stats import norm
        self.assertAlmostEqual(cells(written[2])[0][0], norm.sf((3 - prediction) / error), places=3)
        with self.assertRaisesRegex(arcpy.NotBuiltError, "block kriging"):
            ga.GALayerToRasters(layer, self.out("b.tif"), "PREDICTION_STANDARD_ERROR", None, 50, 2, 2)
        # At points: the prediction, its error, and against a field the error predicted − measured.
        locations = self.points("at", [(0, 0, 0.5), (25, 75, 9.0)])
        points = ga.GALayerToPoints(layer, locations, "z", self.out("at_out")).getOutput(0)
        with arcpy.da.SearchCursor(points, ["Source_ID", "Included", "Predicted", "StdError", "Error", "z"]) as found:
            got = list(found)
        self.assertEqual(got[0][:3], (1, "Yes", 1.0))
        self.assertAlmostEqual(got[0][4], 0.5, places=8)
        self.assertAlmostEqual(got[1][2], prediction, places=8)
        self.assertAlmostEqual(got[1][4], prediction - 9.0, places=8)
        # Contours of a plane z = x by global polynomial: one line at 50, every vertex at x = 50.
        line = self.points("line", [(x, y, float(x)) for x in (0, 50, 100) for y in (0, 100)])
        ga.GlobalPolynomialInterpolation(line, "z", "gx", "", None, 1)
        contours = ga.GALayerToContour("gx", "CONTOUR", self.out("contours"), "DRAFT", "MANUAL", None, [50]).getOutput(0)
        with arcpy.da.SearchCursor(contours, ["Contour", "SHAPE@"]) as found:
            got = list(found)
        self.assertEqual(len(got), 1)
        self.assertEqual(got[0][0], 50.0)
        xs = [p.X for part in got[0][1] for p in part]
        self.assertTrue(all(abs(x - 50) < 1e-6 for x in xs))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "contour_type"):
            ga.GALayerToContour("gx", "FILLED_CONTOUR", self.out("f"))

    def test_cross_validation_by_hand(self):
        # IDW, power 1, every point a neighbour; z = 0, 10, 20 at x = 0, 10, 20. Leaving out x = 0: (10/10 + 20/20) /
        # (1/10 + 1/20) = 13.333; x = 10: 10; x = 20: (0/20 + 10/10) / 0.15 = 6.667. Errors 13.333, 0, −13.333.
        data = self.points("row", [(0, 0, 0.0), (10, 0, 10.0), (20, 0, 20.0)])
        ga.IDW(data, "z", "row_idw", "", None, 1, ga.SearchNeighborhoodStandardCircular(100, 0, 3, 1))
        result = ga.CrossValidation("row_idw", self.out("cv"))
        self.assertIsInstance(result, arcpy.CrossValidationResult)
        self.assertEqual(result.count, 3)
        self.assertAlmostEqual(result.meanError, 0.0, places=10)
        self.assertAlmostEqual(result.rootMeanSquare, math.sqrt(2 * (40 / 3) ** 2 / 3), places=10)
        self.assertIsNone(result.rootMeanSquareStandardized)
        with arcpy.da.SearchCursor(result.getOutput(0), ["Measured", "Predicted", "Error"]) as found:
            got = [tuple(round(v, 6) for v in r) for r in found]
        self.assertEqual(got, [(0.0, 13.333333, 13.333333), (10.0, 10.0, 0.0), (20.0, 6.666667, -13.333333)])
        # Kriging: the standardized statistics follow from the errors and the standard errors.
        krig = self.kriging("rk", data, {"model": "SPHERICAL", "range": 30, "partialSill": 1, "nugget": 0})
        cv = ga.CrossValidation(krig)
        layer = _geostat.open_layer(krig, "t")
        each = [layer.predict(x, 0, leave_out=i) for i, x in enumerate((0, 10, 20))]
        errors = [p[0] - z for p, z in zip(each, (0, 10, 20))]
        self.assertAlmostEqual(cv.averageStandard, sum(p[1] for p in each) / 3, places=10)
        self.assertAlmostEqual(cv.rootMeanSquareStandardized,
                               math.sqrt(sum((e / p[1]) ** 2 for e, p in zip(errors, each)) / 3), places=10)

    def test_comparing_layers_ranks_them(self):
        data = self.points("row5", [(0, 0, 0.0), (10, 0, 10.0), (20, 0, 20.0), (30, 0, 30.0), (40, 0, 45.0)])
        circle = ga.SearchNeighborhoodStandardCircular(100, 0, 5, 1)
        for power in (1, 2, 3):
            ga.IDW(data, "z", f"p{power}", "", None, power, circle)
        rmse = {p: ga.CrossValidation(f"p{p}").rootMeanSquare for p in (1, 2, 3)}
        expected = sorted(rmse, key=rmse.get)
        result = ga.CompareGeostatisticalLayers("p1;p2;p3", self.out("cmp"), "best")
        with arcpy.da.SearchCursor(result.getOutput(0), ["Layer", "RANK", "Included", "RMSE"]) as found:
            ranks = {r[0]: r[1] for r in found}
        self.assertEqual([ranks[f"p{p}"] for p in expected], [1, 2, 3])
        self.assertEqual(result.getOutput(1), "best")
        # Excluding the best by MAX_RMSE leaves two, ranked 1 and 2.
        excluded = ga.CompareGeostatisticalLayers("p1;p2;p3", self.out("cmp2"), "", "SINGLE", "ACCURACY", None, None,
                                                  [["MAX_RMSE", (rmse[expected[0]] + rmse[expected[1]]) / 2]])
        with arcpy.da.SearchCursor(excluded.getOutput(0), ["Layer", "RANK", "Included"]) as found:
            rows = {r[0]: (r[1], r[2]) for r in found}
        self.assertEqual(rows[f"p{expected[0]}"], (1, "Yes"))
        self.assertEqual(rows[f"p{expected[2]}"], (None, "No"))
        with self.assertRaisesRegex(arcpy.NotBuiltError, "same words"):
            ga.CompareGeostatisticalLayers("p1;p2", self.out("cmp3"), "", "SINGLE", "ACCURACY", None, None,
                                           [["MAX_MEAN_ERROR", -1]])
        with self.assertRaisesRegex(ValueError, "standard errors"):
            ga.CompareGeostatisticalLayers("p1;p2", self.out("cmp4"), "", "SINGLE", "PRECISION")
        self.assertEqual(ga._competition([12, 14, 14, 15, 16, 16, 18]), [1, 2, 2, 4, 5, 5, 7])

    def test_moving_window_kriging_is_exact_at_its_data(self):
        rows = [(x * 10.0, y * 10.0, x + 2.0 * y + (x * y) % 3) for x in range(4) for y in range(4)]
        data = self.points("mw", rows)
        source = self.model("mw_model", "KRIGING", {"kind": "ORDINARY", "semivariogram": {"model": "SPHERICAL"}})
        at = self.points("mw_at", [(10.0, 20.0, 0.0)])
        out = ga.GAMovingWindowKriging(source, f"{data} z", at, 8, self.out("mw_out")).getOutput(0)
        with arcpy.da.SearchCursor(out, ["Included", "Predicted", "Range"]) as found:
            included, predicted, fitted_range = next(iter(found))
        self.assertEqual(included, "Yes")
        self.assertAlmostEqual(predicted, 1 + 4 + 2 % 3, places=6)
        self.assertGreater(fitted_range, 0)
        with self.assertRaisesRegex(ValueError, "more than 10"):
            ga.GAMovingWindowKriging(source, f"{self.points('few', rows[:5])} z", at, 4, self.out("mw2"))

    def test_what_stays_refused_says_why(self):
        for name, said in (("GASemivariogramSensitivity", "random"), ("DensifySamplingNetwork", "updated"),
                           ("ExploratoryInterpolation", "empirical Bayesian"), ("ArealInterpolationLayerToPolygons",
                                                                               "areal kriging")):
            with self.assertRaisesRegex(arcpy.NotBuiltError, said):
                getattr(ga, name)()


if __name__ == "__main__":
    unittest.main()
