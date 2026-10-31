"""Path distance, distance accumulation and least-cost paths (ADR-241), against the pages' own numbers and answers
worked out by hand.

Rasters are written north to south with 10-unit cells on EPSG:3857, the top-left corner at (0, rows·10), so the cell in
row r and column k has its centre at (5 + 10k, rows·10 − 5 − 10r).
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ia, sa  # noqa: E402

ND = -1
# The sample vertical factor table of *Adjust the encountered distance using a vertical factor*: Tobler's pace in
# hours per metre, every ten degrees.
TOBLER = [(-90, -1), (-80, -1), (-70, 2.099409721), (-60, 0.060064462), (-50, 0.009064613), (-40, 0.00263818),
          (-30, 0.001055449), (-20, 0.000500142), (-10, 0.00025934), (0, 0.000198541), (10, 0.000368021),
          (20, 0.000709735), (30, 0.001497754), (40, 0.003743755), (50, 0.012863298), (60, 0.085235529),
          (70, 2.979204206), (80, -1), (90, -1)]
# The sample horizontal factor table of *How the horizontal and vertical factors affect path distance*.
HF_SAMPLE = [(0, 1.40), (10, 2.43), (20, 2.30), (30, 3.44), (40, 1.25), (50, 1.02), (60, 0.90), (70, 0.86),
             (80, 0.25), (90, 0.78), (100, 1.49), (110, 2.35), (120, 3.32), (130, 2.39), (140, 3.18), (150, 2.13),
             (160, 1.89), (170, 1.20), (180, 2.03)]


def grid(values, cell=10.0, nodata=None):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0.0, 0.0), cell, cell, nodata)


def cells(raster, places=6):
    got = arcpy.RasterToNumPyArray(raster, nodata_to_value=ND)
    return np.round(got.astype(float), places).tolist()


def one(value, shape):
    """A raster of NoData with ``value`` at the cells given as (row, column)."""
    rows, columns, *at = shape
    out = np.full((rows, columns), -9)
    for r, k in at:
        out[r, k] = value
    return grid(out.tolist(), nodata=-9)


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def table(self, name, rows):
        path = os.path.join(self.directory, name)
        with open(path, "w", encoding="utf-8") as text:
            text.write("\n".join(f"{a} {f}" for a, f in rows) + "\n")
        return path


class Factors(_Fixture):
    def test_the_classes_are_arcpy_sa_and_arcpy_ia_both(self):
        for name in ("HfBinary", "HfForward", "HfLinear", "HfInverseLinear", "HfTable", "VfBinary", "VfLinear",
                     "VfSymLinear", "VfInverseLinear", "VfSymInverseLinear", "VfCos", "VfSec", "VfCosSec", "VfSecCos",
                     "VfHikingTime", "VfBidirHikingTime", "VfTable"):
            self.assertIs(getattr(ia, name), getattr(sa, name))
        linear = sa.VfLinear()
        self.assertEqual((linear.zeroFactor, linear.lowCutAngle, linear.highCutAngle, linear.slope),
                         (1.0, -90.0, 90.0, 0.011111))
        self.assertEqual((sa.HfInverseLinear().zeroFactor, sa.HfInverseLinear().cutAngle), (2.0, 180.0))
        self.assertEqual((sa.VfHikingTime().lowCutAngle, sa.VfHikingTime().highCutAngle), (-70.0, 70.0))

    def test_horizontal_graphs(self):
        hrma = np.array([0.0, 30.0, 45.0, 46.0, 90.0, 180.0])
        np.testing.assert_allclose(sa.HfBinary().value(hrma), [1, 1, 1, math.inf, math.inf, math.inf])
        np.testing.assert_allclose(sa.HfForward().value(hrma), [0.5, 0.5, 1, 1, math.inf, math.inf])
        # 0.5 + HRMA/90; 0.5 + 180 · 0.011111 at the back.
        np.testing.assert_allclose(sa.HfLinear().value(hrma)[[0, 4, 5]], [0.5, 1.49999, 2.49998])
        # 1 / (2 − HRMA/90): a half straight ahead, about 1 across.
        np.testing.assert_allclose(sa.HfInverseLinear().value(hrma)[[0, 4]], [0.5, 1 / 1.00001])
        np.testing.assert_allclose(sa.HfLinear(1, 60, 0.01).value(np.array([50.0, 61.0])), [1.5, math.inf])
        table = sa.HfTable(self.table("hf.txt", HF_SAMPLE))
        np.testing.assert_allclose(table.value(np.array([0.0, 5.0, 85.0, 180.0])), [1.40, 1.915, 0.515, 2.03])

    def test_vertical_graphs(self):
        np.testing.assert_allclose(sa.VfBinary().value(np.array([-30.0, 0.0, 30.0, 31.0])), [1, 1, 1, math.inf])
        np.testing.assert_allclose(sa.VfLinear().value(np.array([45.0, -45.0, -91.0])),
                                   [1.499995, 0.500005, math.inf])
        np.testing.assert_allclose(sa.VfSymLinear(1, -90, 90, 0.02).value(np.array([-30.0, 30.0])), [1.6, 1.6])
        # 1 / (1 − VRMA/45): a half at −45, 2 at 22.5, cut beyond 45.
        np.testing.assert_allclose(sa.VfInverseLinear().value(np.array([-45.0, 22.5, 46.0])),
                                   [1 / 1.99999, 1 / 0.500005, math.inf])
        np.testing.assert_allclose(sa.VfSymInverseLinear(1, -45, 45, -0.02).value(np.array([-25.0, 25.0])), [2, 2])
        sixty = np.array([-60.0, 60.0])
        np.testing.assert_allclose(sa.VfCos().value(sixty), [0.5, 0.5])
        np.testing.assert_allclose(sa.VfCos(-90, 90, 2).value(sixty), [0.25, 0.25])
        np.testing.assert_allclose(sa.VfSec().value(sixty), [2, 2])
        np.testing.assert_allclose(sa.VfCosSec().value(sixty), [0.5, 2])
        np.testing.assert_allclose(sa.VfSecCos().value(sixty), [2, 0.5])
        np.testing.assert_allclose(sa.VfCos(-50, 50).value(sixty), [math.inf, math.inf])

    def test_hiking_time_is_the_pages_table(self):
        angles = np.array([a for a, f in TOBLER if f >= 0], dtype=float)
        expected = [f for _, f in TOBLER if f >= 0]
        np.testing.assert_allclose(sa.VfHikingTime().value(angles), expected, rtol=1e-6)
        self.assertEqual(sa.VfHikingTime().value(np.array([75.0, -75.0])).tolist(), [math.inf, math.inf])
        # Out and back: the mean of the pace up and down the same slope.
        np.testing.assert_allclose(sa.VfBidirHikingTime().value(np.array([10.0, -10.0])),
                                   [(0.000368021 + 0.00025934) / 2] * 2, rtol=1e-6)
        # In feet, the pace per map unit is the pace per metre times 0.3048.
        np.testing.assert_allclose(sa.VfHikingTime().value(np.array([0.0]), 0.3048), [0.000198541 * 0.3048],
                                   rtol=1e-6)

    def test_a_table_reads_between_its_first_and_next_infinite_rows(self):
        table = sa.VfTable(self.table("vf.txt", TOBLER))
        np.testing.assert_allclose(table.value(np.array([-75.0, -65.0, 0.0, 70.0, 75.0])),
                                   [math.inf, (2.099409721 + 0.060064462) / 2, 0.000198541, 2.979204206, math.inf])
        with self.assertRaises(ValueError):
            sa.VfTable(self.table("down.txt", [(10, 1), (0, 1)]))

    def test_a_keyword_string_stands_for_its_class(self):
        row = one(1, (1, 3, (0, 0)))
        rising = grid([[0.0, 10.0, 10.0]])
        by_object = cells(sa.PathDistance(row, in_vertical_raster=rising, vertical_factor=sa.VfLinear(1, -90, 90, 0.02)))
        by_string = cells(sa.PathDistance(row, in_vertical_raster=rising, vertical_factor="LINEAR 1 -90 90 0.02"))
        self.assertEqual(by_object, by_string)
        with self.assertRaises(ValueError):
            sa.PathDistance(row, in_vertical_raster=rising, vertical_factor="UPHILL 3")


class PathDistance(_Fixture):
    def test_without_factors_it_is_cost_distance(self):
        # Understanding path distance analysis: with no surface and no factors, Path Distance is Cost Distance.
        cost = grid([[1, 1, 1], [1, 5, 1], [1, 1, 1]])
        source = one(1, (3, 3, (0, 0)))
        self.assertEqual(cells(sa.PathDistance(source, cost)), cells(sa.CostDistance(source, cost)))
        self.assertEqual(cells(sa.PathBackLink(source, cost)), cells(sa.CostBackLink(source, cost)))

    def test_surface_distance_is_the_hypotenuse(self):
        # A rise of 10 over a 10-unit step: √200 = 14.142136, then a flat 10.
        got = cells(sa.PathDistance(one(1, (1, 3, (0, 0))), in_surface_raster=grid([[0.0, 10.0, 10.0]])))
        self.assertEqual(got, [[0.0, 14.142136, 24.142136]])

    def test_the_vertical_factor_follows_the_travel_direction(self):
        source, rising = one(1, (1, 3, (0, 0))), grid([[0.0, 10.0, 10.0]])
        # VRMA atan(10/10) = 45°: VF 1 + 45 · 0.011111 = 1.499995 on the first step, 1 on the flat one.
        self.assertEqual(cells(sa.PathDistance(source, in_vertical_raster=rising, vertical_factor=sa.VfLinear())),
                         [[0.0, 14.99995, 24.99995]])
        # Travelling to the source the same step runs downhill: VRMA −45°, VF 0.500005.
        self.assertEqual(cells(sa.PathDistance(source, in_vertical_raster=rising, vertical_factor=sa.VfLinear(),
                                               source_direction="TO_SOURCE")), [[0.0, 5.00005, 15.00005]])
        # The default Binary graph cuts beyond ±30°, so the 45° step is not taken and the cell beyond is unreached.
        self.assertEqual(cells(sa.PathDistance(source, in_vertical_raster=rising)), [[0.0, ND, ND]])

    def test_the_horizontal_factor_is_taken_in_both_half_steps(self):
        # A wind to the east (90) everywhere and the Linear graph: 0.5 with it (HRMA 0), 2.49998 against it (180).
        got = cells(sa.PathDistance(one(1, (1, 3, (0, 1))), in_horizontal_raster=grid([[90.0, 90.0, 90.0]]),
                                    horizontal_factor=sa.HfLinear()))
        self.assertEqual(got, [[24.9998, 0.0, 5.0]])
        # Half the step in each cell: east 0.5 in the source cell, north (HRMA 90 → 1.49999) in the next.
        mixed = cells(sa.PathDistance(one(1, (1, 2, (0, 0))), in_horizontal_raster=grid([[90.0, 0.0]]),
                                      horizontal_factor=sa.HfLinear()))
        self.assertEqual(mixed, [[0.0, round((0.5 + 1.49999) / 2 * 10, 6)]])

    def test_hiking_time_in_hours(self):
        flat = grid([[0.0, 0.0, 0.0], [0.0, 0.0, 0.0]])
        got = cells(sa.PathDistance(one(1, (2, 3, (0, 0))), in_vertical_raster=flat,
                                    vertical_factor=sa.VfHikingTime()), places=9)
        pace = 0.000198541  # hours per metre on the flat
        self.assertAlmostEqual(got[0][2], 2 * 10 * pace, places=8)
        self.assertAlmostEqual(got[1][1], 1.414214 * 10 * pace, places=8)

    def test_nodata_on_any_input_is_a_barrier(self):
        holed = grid([[0.0, -9.0, 0.0], [0.0, 0.0, 0.0]], nodata=-9)
        got = cells(sa.PathDistance(one(1, (2, 3, (0, 0))), in_surface_raster=holed))
        self.assertEqual(got[0][1], ND)
        self.assertAlmostEqual(got[0][2], 2 * 14.14214, places=4)  # under the hole by two diagonals
        self.assertEqual(cells(sa.PathDistance(one(1, (1, 3, (0, 0))), maximum_distance=15)), [[0.0, 10.0, ND]])

    def test_allocation_and_its_optional_outputs(self):
        two = grid([[1, -9, -9, -9], [-9, -9, -9, 2]], nodata=-9)
        back = os.path.join(self.directory, "back.tif")
        distance = os.path.join(self.directory, "distance.tif")
        allocated = cells(sa.PathAllocation(two, out_distance_raster=distance, out_backlink_raster=back))
        self.assertEqual(allocated, [[1, 1, 2, 2], [1, 1, 2, 2]])
        self.assertEqual(cells(arcpy.Raster(distance))[0][:2], [0.0, 10.0])
        self.assertEqual(cells(arcpy.Raster(back))[0][:2], [0, 5])

    def test_source_characteristics_are_refused_by_name(self):
        with self.assertRaises(arcpy.NotBuiltError):
            sa.PathDistance(one(1, (1, 3, (0, 0))), source_start_cost=5)


class Accumulation(_Fixture):
    def setUp(self):
        super().setUp()
        self.centre = one(1, (5, 5, (2, 2)))

    def test_straight_and_diagonal_steps_are_exact(self):
        got = cells(sa.DistanceAccumulation(self.centre), places=4)
        self.assertEqual(got[2], [20.0, 10.0, 0.0, 10.0, 20.0])
        self.assertEqual((got[1][1], got[0][0]), (14.1421, 28.2843))

    def test_a_knights_move_is_nearer_the_straight_line_than_the_network(self):
        knight = cells(sa.DistanceAccumulation(self.centre))[0][1]
        self.assertLess(knight, 10 + 10 * 1.414214)  # the network's two steps
        self.assertGreater(knight, 10 * math.sqrt(5))  # the straight line
        large = np.full((41, 41), -9)
        large[20, 20] = 1
        got = arcpy.RasterToNumPyArray(sa.DistanceAccumulation(grid(large.tolist(), nodata=-9)))
        r, k = np.indices(large.shape)
        euclid = 10 * np.hypot(r - 20, k - 20)
        self.assertLess(float(np.max(np.abs(got - euclid)[euclid > 0] / euclid[euclid > 0])), 0.06)

    def test_back_direction_source_direction_and_location(self):
        paths = {n: os.path.join(self.directory, f"{n}.tif") for n in ("back", "direction", "location")}
        sa.DistanceAccumulation(self.centre, out_back_direction_raster=paths["back"],
                                out_source_direction_raster=paths["direction"],
                                out_source_location_raster=paths["location"])
        back = cells(arcpy.Raster(paths["back"]), places=2)
        # Due east is 90, south 180, north 360, and 0 on the source.
        self.assertEqual((back[2][2], back[2][0], back[0][2], back[4][2], back[0][0]), (0.0, 90.0, 180.0, 360.0, 135.0))
        direction = cells(arcpy.Raster(paths["direction"]), places=4)
        # From (0,1) the source lies one column east and two rows south: atan2(1, −2) = 153.4349°.
        self.assertEqual((direction[0][1], direction[2][4], direction[2][2]), (153.435, 270.0, 0.0))
        location = arcpy.Raster(paths["location"])
        self.assertEqual(location.bandCount, 2)

    def test_cost_barriers_and_source_characteristics(self):
        cost = grid([[2.0] * 5] * 5)
        self.assertEqual(cells(sa.DistanceAccumulation(self.centre, in_cost_raster=cost))[2], [40, 20, 0, 20, 40])
        self.assertEqual(cells(sa.DistanceAccumulation(self.centre, source_cost_multiplier=2))[2], [40, 20, 0, 20, 40])
        started = cells(sa.DistanceAccumulation(self.centre, source_initial_accumulation=5,
                                                source_maximum_accumulation=20))
        self.assertEqual(started[2], [ND, 15, 5, 15, ND])
        wall = one(1, (5, 5, (0, 3), (1, 3), (2, 3), (3, 3)))
        walled = cells(sa.DistanceAccumulation(self.centre, in_barrier_data=wall), places=4)
        self.assertEqual(walled[2][3], ND)
        self.assertEqual(walled[4][3], 24.1421)  # round the wall's end
        with self.assertRaises(arcpy.NotBuiltError):
            sa.DistanceAccumulation(self.centre, distance_method="GEODESIC")

    def test_corner_connected_barriers_are_thickened(self):
        # NoData touching only at a corner would let a diagonal step through; one open cell between them is closed (the upper, INFERRED).
        cost = grid([[1, 1, 1], [-9, 1, 1], [1, -9, 1]], nodata=-9)
        got = cells(sa.DistanceAccumulation(one(1, (3, 3, (2, 0))), in_cost_raster=cost))
        self.assertEqual(got[2][0], 0.0)
        self.assertEqual(got[1][1], ND)
        self.assertEqual(got[0][0], ND)

    def test_the_vertical_factor_in_the_reconstruction(self):
        rising = grid([[0.0, 10.0, 20.0]])
        got = cells(sa.DistanceAccumulation(one(1, (1, 3, (0, 0))), in_vertical_raster=rising,
                                            vertical_factor=sa.VfLinear()))
        # Only the slope at the cell entered counts: 10 · 1.499995 for each 45° step.
        np.testing.assert_allclose(got, [[0.0, 14.99995, 29.9999]], rtol=1e-6)

    def test_allocation_takes_each_cells_cheapest_source(self):
        two = grid([[1, -9, -9, -9], [-9, -9, -9, 2]], nodata=-9)
        self.assertEqual(cells(sa.DistanceAllocation(two)), [[1, 1, 2, 2], [1, 1, 2, 2]])

    def test_they_are_marked_as_equivalents(self):
        for tool in (sa.DistanceAccumulation, sa.DistanceAllocation, sa.OptimalPathAsRaster, sa.OptimalPathAsLine):
            self.assertIn("Sethian" if "Distance" in tool.__name__ else "Douglas", tool.__grapy_equivalent__)


class Paths(_Fixture):
    def setUp(self):
        super().setUp()
        cost = grid([[1, 1, 1], [1, 5, 1], [1, 1, 1]])
        source = one(1, (3, 3, (0, 0)))
        self.distance, self.backlink = sa.CostDistance(source, cost), sa.CostBackLink(source, cost)

    def test_each_cell_numbers_paths_from_three_and_the_source_one(self):
        # (2,0) runs up the west column; (2,2) up to (1,2), diagonally to (0,1), then west to the source.
        got = cells(sa.CostPath(one(7, (3, 3, (2, 0), (2, 2))), self.distance, self.backlink))
        self.assertEqual(got, [[1, 4, ND], [3, ND, 4], [3, ND, 4]])

    def test_merged_paths_are_two(self):
        destinations = one(7, (3, 3, (1, 2), (2, 2)))
        self.assertEqual(cells(sa.CostPath(destinations, self.distance, self.backlink)),
                         [[1, 2, ND], [ND, ND, 2], [ND, ND, 4]])
        # The best single path and the zone's path both start from the cheaper (1,2).
        for kind in ("BEST_SINGLE", "EACH_ZONE"):
            self.assertEqual(cells(sa.CostPath(destinations, self.distance, self.backlink, kind)),
                             [[1, 3, ND], [ND, ND, 3], [ND, ND, ND]])

    def test_as_polyline_and_over_a_flow_direction(self):
        out = os.path.join(self.directory, "paths.gpkg")
        sa.CostPathAsPolyline(one(7, (3, 3, (1, 2), (2, 2))), self.distance, self.backlink, out)
        with arcpy.da.SearchCursor(out, ["DestID", "SHAPE@"]) as rows:
            found = [(d, [(p.X, p.Y) for p in shape.getPart(0)]) for d, shape in rows]
        self.assertEqual(found, [(7, [(25.0, 15.0), (15.0, 25.0), (5.0, 25.0)])])
        # A D8 raster (1 east … 128 north-east) traced cell by cell to its sink (0).
        flow = grid([[1, 1, 0], [128, 64, 64], [64, 64, 64]])
        self.assertEqual(cells(sa.CostPath(one(1, (3, 3, (2, 0))), self.distance, flow)),
                         [[ND, 3, 1], [3, ND, ND], [3, ND, ND]])

    def test_optimal_paths_follow_the_back_direction(self):
        back = os.path.join(self.directory, "back.tif")
        accumulation = sa.DistanceAccumulation(one(1, (5, 5, (2, 2))), out_back_direction_raster=back)
        corner = one(3, (5, 5, (0, 0)))
        got = cells(sa.OptimalPathAsRaster(corner, accumulation, arcpy.Raster(back)))
        self.assertEqual([got[i][i] for i in range(3)], [1, 1, 1])
        self.assertEqual(sum(v == 1 for row in got for v in row), 3)
        two = one(3, (5, 5, (0, 0), (0, 4)))
        counted = cells(sa.OptimalPathAsRaster(two, accumulation, arcpy.Raster(back), path_type="EACH_CELL"))
        self.assertEqual(counted[2][2], 2)  # both paths end on the source
        out = os.path.join(self.directory, "optimal.gpkg")
        sa.OptimalPathAsLine(corner, accumulation, arcpy.Raster(back), out)
        with arcpy.da.SearchCursor(out, ["DestID", "PathCost", "SHAPE@"]) as rows:
            (dest, cost, shape), = list(rows)
        self.assertEqual(dest, 3)
        self.assertAlmostEqual(cost, 28.28428, places=4)
        self.assertEqual([(p.X, p.Y) for p in shape.getPart(0)], [(5.0, 45.0), (15.0, 35.0), (25.0, 25.0)])
        with self.assertRaises(arcpy.NotBuiltError):
            sa.OptimalPathAsLine(corner, accumulation, arcpy.Raster(back), out, create_network_paths="NETWORK_PATHS")


if __name__ == "__main__":
    unittest.main()
