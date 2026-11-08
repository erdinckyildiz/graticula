"""The arcpy.sa and arcpy.ia names no test had called, each against an answer worked out by hand — ADR-224.

Rasters are written north to south with 10-unit cells on EPSG:3857, the bottom-left corner at the origin, as in
test_sa_more. The expected values are the documented formula evaluated by hand (or by ``math``) at the cell's value, not
grapy's own code run twice.
"""
import math
import os
import sys
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ia, sa  # noqa: E402
from test_sa_more import ND, _Fixture, cells, grid, point  # noqa: E402


class OneInput(_Fixture):
    def check(self, tool, values, expected, places=9):
        got = cells(tool(grid([values])), nodata=ND)[0]
        self.assertEqual(len(got), len(expected))
        for g, e in zip(got, expected):
            self.assertAlmostEqual(g, e, places=places, msg=f"{tool.__name__}({values})")

    def test_trigonometry_and_its_domains(self):
        # ASin and ACos are NoData outside −1 … 1, their documented domain.
        self.check(sa.ASin, [-1.0, 0.5, 2.0], [-math.pi / 2, math.asin(0.5), ND])
        self.check(sa.ACos, [-1.0, 0.5, 2.0], [math.pi, math.acos(0.5), ND])
        self.check(sa.ATan, [-1.0, 0.0, 1.0], [-math.pi / 4, 0.0, math.pi / 4])
        self.check(sa.SinH, [-1.0, 0.0, 2.0], [math.sinh(-1), 0.0, math.sinh(2)])
        self.check(sa.CosH, [-1.0, 0.0, 2.0], [math.cosh(-1), 1.0, math.cosh(2)])
        self.check(sa.TanH, [-1.0, 0.0, 2.0], [math.tanh(-1), 0.0, math.tanh(2)])

    def test_atan2_takes_y_first(self):
        # ATan2(y, x): the angle of (x, y) — (1, 1) is π/4, (−1, 0) is π, (0, −1) is −π/2.
        got = cells(sa.ATan2(grid([[1.0, 0.0, -1.0]]), grid([[1.0, -1.0, 0.0]])))[0]
        for g, e in zip(got, [math.pi / 4, math.pi, -math.pi / 2]):
            self.assertAlmostEqual(g, e)

    def test_powers_and_logarithms(self):
        self.check(sa.Exp2, [0.0, 3.0, -1.0], [1.0, 8.0, 0.5])
        self.check(sa.Exp10, [0.0, 2.0, -1.0], [1.0, 100.0, 0.1])
        # Log2 and Log10 are NoData at and below zero.
        self.check(sa.Log2, [8.0, 0.5, 0.0, -4.0], [3.0, -1.0, ND, ND])
        self.check(sa.Log10, [1000.0, 0.01, 0.0], [3.0, -2.0, ND])

    def test_rounding_and_negation(self):
        self.check(sa.RoundUp, [1.2, -1.2, 3.0], [2.0, -1.0, 3.0])
        self.check(sa.RoundDown, [1.8, -1.2, 3.0], [1.0, -2.0, 3.0])
        negated = sa.Negate(grid([[3, -2, 0]]))
        self.assertTrue(negated.isInteger)  # an integer input stays integer, as documented
        self.assertEqual(cells(negated), [[-3, 2, 0]])

    def test_boolean_not_and_nodata(self):
        a = grid([[0, 5, -9]], nodata=-9)
        self.assertEqual(cells(sa.BooleanNot(a)), [[1, 0, ND]])


class TwoInputs(_Fixture):
    def setUp(self):
        super().setUp()
        self.a = grid([[1, 2, 3, -9]], nodata=-9)
        self.b = grid([[2, 2, 1, 5]])

    def test_comparisons_answer_one_and_zero_and_keep_nodata(self):
        a, b = self.a, self.b
        self.assertEqual(cells(sa.GreaterThan(a, b)), [[0, 0, 1, ND]])
        self.assertEqual(cells(sa.GreaterThanEqual(a, b)), [[0, 1, 1, ND]])
        self.assertEqual(cells(sa.LessThan(a, b)), [[1, 0, 0, ND]])
        self.assertEqual(cells(sa.LessThanEqual(a, b)), [[1, 1, 0, ND]])
        self.assertEqual(cells(sa.EqualTo(a, b)), [[0, 1, 0, ND]])
        self.assertEqual(cells(sa.NotEqual(a, b)), [[1, 0, 1, ND]])
        # A constant on either side.
        self.assertEqual(cells(sa.GreaterThan(a, 2)), [[0, 0, 1, ND]])

    def test_boolean_operators_treat_any_non_zero_as_true(self):
        a = grid([[0, 0, 3, 4, -9]], nodata=-9)
        b = grid([[0, 7, 0, -1, 1]])
        self.assertEqual(cells(sa.BooleanAnd(a, b)), [[0, 0, 0, 1, ND]])
        self.assertEqual(cells(sa.BooleanOr(a, b)), [[0, 1, 1, 1, ND]])
        self.assertEqual(cells(sa.BooleanXOr(a, b)), [[0, 1, 1, 0, ND]])

    def test_diff_keeps_the_first_where_they_differ_and_zero_where_equal(self):
        self.assertEqual(cells(sa.Diff(self.a, self.b)), [[1, 0, 3, ND]])


class Selection(_Fixture):
    def test_test_answers_the_where_clause(self):
        a = grid([[1, 5, 9, -9]], nodata=-9)
        self.assertEqual(cells(sa.Test(a, "VALUE > 4")), [[0, 1, 1, ND]])
        self.assertEqual(cells(sa.Test(a, "VALUE >= 5 AND VALUE < 9")), [[0, 1, 0, ND]])

    def test_pick_takes_the_input_the_position_names(self):
        position = grid([[1, 2, 3, 0]])
        first, second = grid([[10, 20, 30, 40]]), grid([[11, 21, 31, 41]])
        # Position 3 names the constant; 0 names none and is NoData.
        self.assertEqual(cells(sa.Pick(position, [first, second, 99])), [[10, 21, 99, ND]])

    def test_lowest_position_counts_from_one(self):
        a, b, c = grid([[5, 1, 3]]), grid([[2, 4, 3]]), grid([[9, 9, 1]])
        self.assertEqual(cells(sa.LowestPosition([a, b, c])), [[2, 1, 3]])
        # With a constant among them.
        self.assertEqual(cells(sa.LowestPosition([a, 2.5])), [[2, 1, 2]])

    def test_extract_by_rectangle_inside_and_outside(self):
        # Cell centres 5, 15, 25, 35 along x; the rectangle 10 … 30 holds the middle two.
        a = grid([[1, 2, 3, 4]])
        self.assertEqual(cells(sa.ExtractByRectangle(a, "10 0 30 10")), [[ND, 2, 3, ND]])
        self.assertEqual(cells(sa.ExtractByRectangle(a, arcpy.Extent(10, 0, 30, 10), "OUTSIDE")), [[1, ND, ND, 4]])


class Neighbourhoods(_Fixture):
    def test_nbr_annulus_counts_the_ring_only(self):
        # Inner 1, outer 2 cells: the cells with 1 < d² ≤ 4 — the four diagonals (d² = 2) and the four two away
        # (d² = 4) — eight cells; the four direct neighbours (d² = 1) and the centre are left out.
        ones = grid([[1] * 5] * 5)
        got = cells(sa.FocalStatistics(ones, sa.NbrAnnulus(1, 2, "CELL"), "SUM"))
        self.assertEqual(got[2][2], 8)
        marked = np.zeros((5, 5), dtype=int)
        marked[2, 3] = 100  # a direct neighbour of the centre: inside the inner radius, so not counted
        marked[1, 1] = 7    # a diagonal: counted
        self.assertEqual(cells(sa.FocalStatistics(grid(marked.tolist()), sa.NbrAnnulus(1, 2, "CELL"), "SUM"))[2][2], 7)
        with self.assertRaises(arcpy.NotBuiltError):
            sa.FocalStatistics(ones, sa.NbrAnnulus(10, 20, "MAP"), "SUM")

    def test_search_radii_choose_the_points(self):
        # Points at x = 0, 10 and 100 on y = 0 with z = x; the cell at x = 0 is the point's own value either way.
        points = self.features("r", "POINT", [(0.0, point(0, 0)), (10.0, point(10, 0)), (100.0, point(100, 0))],
                               [("z", "DOUBLE")])
        # The grid puts its cell centres on the points' extent (ADR-233 condition 4): x = 0, 10, 20 … 100.
        # RadiusVariable(1): only the nearest point. The centre at x = 30 is nearest (10, 0).
        nearest = cells(sa.Idw(points, "z", 10, 2, sa.RadiusVariable(1)))[0]
        self.assertEqual(nearest[3], 10.0)
        self.assertEqual(nearest[-1], 100.0)
        # RadiusVariable(2): the two nearest, inverse-square weighted. At x = 20: d = 20 and 10, w = 1/400, 1/100,
        # so z = (0/400 + 10/100) / (1/400 + 1/100) = 0.1 / 0.0125 = 8.
        two = cells(sa.Idw(points, "z", 10, 2, sa.RadiusVariable(2)))[0]
        self.assertAlmostEqual(two[2], 8.0)
        # RadiusFixed(20): every point within 20 — none from x = 50, 40 from (10, 0) and 50 from (100, 0) — and
        # NoData there with no minimum.
        fixed = cells(sa.Idw(points, "z", 10, 2, sa.RadiusFixed(20, 0)))[0]
        self.assertEqual(fixed[5], ND)
        # A minimum of 1 widens the search to the nearest point.
        widened = cells(sa.Idw(points, "z", 10, 2, sa.RadiusFixed(20, 1)))[0]
        self.assertNotEqual(widened[5], ND)
        self.assertEqual(sa.RadiusFixed(20, 1).minNumberofPoints, 1)
        self.assertEqual(sa.RadiusVariable().numberofPoints, 12)


class Kriging(_Fixture):
    def test_universal_kriging_follows_a_linear_drift(self):
        # z = 2x + y + a small wobble: universal kriging with a linear drift removes the trend first, so between the
        # points it stays within the wobble of the plane, where ordinary kriging would pull towards the mean.
        rows, k = [], 0
        for x in range(0, 50, 10):
            for y in range(0, 50, 10):
                wobble = 0.3 if k % 2 else -0.3
                rows.append((2.0 * x + y + wobble, point(x, y)))
                k += 1
        points = self.features("uk", "POINT", rows, [("z", "DOUBLE")])
        model = sa.KrigingModelUniversal("LINEARDRIFT", 10)
        self.assertEqual(model.semivariogramType, "LINEARDRIFT")
        out = sa.Kriging(points, "z", model, 5)
        got = arcpy.RasterToNumPyArray(out)
        extent = out.extent
        xs = extent.XMin + 2.5 + 5 * np.arange(got.shape[1])
        ys = extent.YMax - 2.5 - 5 * np.arange(got.shape[0])
        gx, gy = np.meshgrid(xs, ys)
        inner = (gx > 5) & (gx < 35) & (gy > 5) & (gy < 35)
        self.assertLess(np.abs(got - (2 * gx + gy))[inner].max(), 1.0)
        # The model string names the same class.
        again = arcpy.RasterToNumPyArray(sa.Kriging(points, "z", "LINEARDRIFT,10", 5))
        np.testing.assert_allclose(again, got, atol=1e-6)


class Transformations(_Fixture):
    """RescaleByFunction maps f(x) linearly so its smallest value over the cells is from_scale (1) and its largest
    to_scale (10). Each f below is the formula from *Formulas for the transformation functions* evaluated at 1 … 5."""

    X = [1.0, 2.0, 3.0, 4.0, 5.0]

    def check(self, function, f):
        lo, hi = min(f), max(f)
        expected = [1 + (v - lo) / (hi - lo) * 9 for v in f]
        got = cells(sa.RescaleByFunction(grid([self.X]), function))[0]
        for g, e in zip(got, expected):
            self.assertAlmostEqual(g, e, places=6, msg=type(function).__name__)

    def test_large_and_small(self):
        # Midpoint defaults to (1 + 5)/2 = 3, spread to 5.
        self.check(sa.TfLarge(), [1 / (1 + (x / 3) ** -5) for x in self.X])
        self.check(sa.TfSmall(), [1 / (1 + (x / 3) ** 5) for x in self.X])
        self.assertEqual(sa.TfLarge().spread, 5)

    def test_logarithm_power_and_near(self):
        self.check(sa.TfLogarithm(0, 1), [math.log(x) for x in self.X])
        self.check(sa.TfPower(0, 2), [x * x for x in self.X])
        self.check(sa.TfNear(3, 1), [1 / (1 + (x - 3) ** 2) for x in self.X])

    def test_logistic_growth_and_decay(self):
        # Growth with yIntercept 1 %: a = 100/1 − 1 = 99, b = ln 99 / (3 − 1).
        a, b = 99.0, math.log(99) / 2
        self.check(sa.TfLogisticGrowth(1, 5), [100 / (1 + a * math.exp(-(x - 1) * b)) for x in self.X])
        self.assertEqual(sa.TfLogisticGrowth().yInterceptPercent, 1.0)
        # Decay with yIntercept 99 %: a = 100/99 − 1, b = ln a / 2 (negative), f = 100 / (1 + a·e^((x − 1)·b)).
        a = 100 / 99 - 1
        b = math.log(a) / 2
        self.check(sa.TfLogisticDecay(1, 5), [100 / (1 + a * math.exp((x - 1) * b)) for x in self.X])
        self.assertEqual(sa.TfLogisticDecay().yInterceptPercent, 99.0)

    def test_ms_large_and_ms_small(self):
        # Mean 3 and population standard deviation √2 of 1 … 5; multipliers 1 and 1 by default.
        m, s = 3.0, math.sqrt(2)
        self.check(sa.TfMSLarge(), [1 - s / (x - m + s) if x > m else 0.0 for x in self.X])
        self.check(sa.TfMSSmall(), [s / (x - m + s) if x > m else 1.0 for x in self.X])

    def test_symmetric_linear_peaks_in_the_middle(self):
        # From 1 to 5 the peak is at 3: 0, ½, 1, ½, 0.
        self.check(sa.TfSymmetricLinear(1, 5), [0.0, 0.5, 1.0, 0.5, 0.0])


class Fuzzy(_Fixture):
    def test_fuzzy_small(self):
        # 1 / (1 + (x/m)^s) with m = 2, s = 2: 0.8, 0.5, 0.2.
        got = cells(sa.FuzzyMembership(grid([[1.0, 2.0, 4.0]]), sa.FuzzySmall(2, 2)))[0]
        for g, e in zip(got, [0.8, 0.5, 0.2]):
            self.assertAlmostEqual(g, e)
        self.assertEqual(sa.FuzzySmall().spread, 5)

    def test_fuzzy_ms_small_is_the_default_function(self):
        # Mean 3, population σ √2 of 1 … 5; a = 1·3, b = 2·√2: 1 at and below 3, then b / (x − a + b).
        x = grid([[1.0, 2.0, 3.0, 4.0, 5.0]])
        b = 2 * math.sqrt(2)
        expected = [1.0, 1.0, 1.0, b / (1 + b), b / (2 + b)]
        for function in (sa.FuzzyMSSmall(), None):
            got = cells(sa.FuzzyMembership(x, function))[0]
            for g, e in zip(got, expected):
                self.assertAlmostEqual(g, e)


class TopoExclusion(_Fixture):
    def test_points_inside_an_exclusion_area_are_ignored(self):
        # A plane z = 50 + 0.1x + 0.05y sampled every 20, with the spot at (105, 105) lowered by 8. Excluding a square
        # round that spot leaves the plane there: 50 + 10.5 + 5.25 = 65.75.
        rows = []
        for x in range(5, 200, 20):
            for y in range(5, 200, 20):
                z = 50 + 0.1 * x + 0.05 * y - (8 if (x, y) == (105, 105) else 0)
                rows.append((z, point(x, y)))
        spots = self.features("spots", "POINT", rows, [("z", "DOUBLE")])
        square = arcpy.Polygon(arcpy.Array([arcpy.Point(*p) for p in ((95, 95), (95, 115), (115, 115), (115, 95))]),
                               3857)
        exclusion = self.features("excl", "POLYGON", [(square,)])
        # The cell holding (105, 105): column 10, row 9 of a 20 × 20 grid whose top is y = 200.
        common = dict(data_type="SPOT", enforce="NO_ENFORCE")
        kept = arcpy.RasterToNumPyArray(sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]])], 10, "0 0 200 200",
                                                        **common))[9, 10]
        excluded = arcpy.RasterToNumPyArray(sa.TopoToRaster([sa.TopoPointElevation([[spots, "z"]]),
                                                             sa.TopoExclusion([exclusion])], 10, "0 0 200 200",
                                                            **common))[9, 10]
        self.assertLess(kept, 63.0)
        self.assertAlmostEqual(float(excluded), 65.75, delta=0.5)


class ImageAnalystShares(unittest.TestCase):
    """arcpy.ia documents the same functions and classes as arcpy.sa; grapy answers them with the same objects, so one
    call through arcpy.ia shows the name reaches the tested tool."""

    SHARED = ("ACos", "ASin", "ATan", "ATan2", "BooleanAnd", "BooleanNot", "BooleanOr", "BooleanXOr", "CosH", "Diff",
              "EqualTo", "Exp10", "Exp2", "FuzzyMSSmall", "FuzzySmall", "GreaterThan", "GreaterThanEqual",
              "KrigingModelUniversal", "LessThan", "LessThanEqual", "Log10", "Log2", "LowestPosition", "NbrAnnulus",
              "Negate", "NotEqual", "Pick", "RadiusFixed", "RadiusVariable", "RoundDown", "RoundUp", "SinH", "TanH",
              "Test", "TfLarge", "TfLogarithm", "TfLogisticDecay", "TfLogisticGrowth", "TfMSLarge", "TfMSSmall",
              "TfNear", "TfPower", "TfSmall", "TfSymmetricLinear", "TopoExclusion")

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def test_every_shared_name_is_sas(self):
        for name in self.SHARED:
            self.assertIs(getattr(ia, name), getattr(sa, name), name)

    def test_a_call_through_ia(self):
        self.assertEqual(cells(ia.Exp2(grid([[3.0]]))), [[8.0]])
        self.assertEqual(cells(ia.BooleanXOr(grid([[1, 0]]), grid([[1, 1]]))), [[0, 1]])
        self.assertEqual(cells(ia.Pick(grid([[2]]), [5, 6])), [[6]])


if __name__ == "__main__":
    unittest.main()
