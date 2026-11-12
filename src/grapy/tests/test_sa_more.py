"""The second batch of grapy.sa, against answers worked out by hand — ADR-224.

Rasters are written north to south with 10-unit cells on EPSG:3857, the top-left corner at (0, rows·10), so the cell
in row r and column k has its centre at (5 + 10k, rows·10 − 5 − 10r).
"""
import math
import os
import sys
import shutil
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import sa  # noqa: E402

ND = -1


def grid(values, cell=10.0, nodata=None):
    array = np.array(values)
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(array, arcpy.Point(0.0, 0.0), cell, cell, nodata)


def cells(raster, nodata=ND, places=None):
    got = arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()
    return [[round(v, places) for v in row] for row in got] if places is not None else got


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


# A surface falling 10 per cell to the east and 3 per cell to the south: every interior cell's steepest descent is
# east (drop 1 against 0.919 south-east), so the flow runs east along each row and down the eastern column.
DEM = [[100 - 10 * k - 3 * r for k in range(4)] for r in range(4)]


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, arcpy.env, "workspace", arcpy.env.workspace)
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def features(self, name, shape, rows, fields=()):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path


class More(_Fixture):
    # ---- mathematics

    def test_hyperbolic_bitwise_and_over(self):
        self.assertEqual(cells(sa.ACosH(grid([[1.0, 0.5]]))), [[0.0, ND]])
        self.assertEqual(cells(sa.ATanH(grid([[0.0, 1.0]]))), [[0.0, ND]])
        self.assertEqual(cells(sa.ASinH(grid([[0.0]]))), [[0.0]])
        twelve = grid([[12, 12.9]])
        self.assertEqual(cells(sa.BitwiseAnd(twelve, 10)), [[8, 8]])
        self.assertEqual(cells(sa.BitwiseOr(twelve, 10)), [[14, 14]])
        self.assertEqual(cells(sa.BitwiseXOr(twelve, 10)), [[6, 6]])
        self.assertEqual(cells(sa.BitwiseNot(grid([[0, 5]]))), [[-1, -6]])
        # The bit shifted past the left is dropped and the sign bit is part of the value: 3 << 31 keeps only bit 31.
        self.assertEqual(cells(sa.BitwiseLeftShift(grid([[1, 3, 1]]), grid([[31, 31, -1]])), nodata=99),
                         [[-2147483648, -2147483648, 99]])
        self.assertEqual(cells(sa.BitwiseRightShift(grid([[-8, 8]]), 1)), [[-4, 4]])
        self.assertEqual(cells(sa.Over(grid([[0, 5]]), grid([[7, 8]]))), [[7, 5]])

    # ---- local

    def test_frequencies_popularity_rank_and_weighted_sum(self):
        value = grid([[1, 2]])
        others = [grid([[1, 1]]), grid([[2, 1]]), grid([[3, 3]])]
        self.assertEqual(cells(sa.EqualToFrequency(value, others)), [[1, 0]])
        self.assertEqual(cells(sa.GreaterThanFrequency(value, others)), [[2, 1]])
        self.assertEqual(cells(sa.LessThanFrequency(value, others)), [[0, 2]])
        # Cell 1 holds 1, 1, 2; cell 2 holds 1, 2, 3 — every value once, a tie at every level.
        votes = [grid([[1, 1]]), grid([[1, 2]]), grid([[2, 3]])]
        self.assertEqual(cells(sa.Popularity(grid([[1, 1]]), votes)), [[1, ND]])
        self.assertEqual(cells(sa.Popularity(grid([[2, 2]]), votes)), [[2, ND]])
        self.assertEqual(cells(sa.Popularity(grid([[4, 0]]), votes)), [[ND, ND]])
        ranked = [grid([[17]]), grid([[8]]), grid([[11]])]
        self.assertEqual(cells(sa.Rank(grid([[3]]), ranked)), [[17]])
        self.assertEqual(cells(sa.Rank(grid([[1]]), ranked)), [[8]])
        self.assertEqual(cells(sa.Rank(grid([[4]]), ranked)), [[ND]])
        summed = sa.WeightedSum(sa.WSTable([[grid([[2.2]]), "VALUE", 0.75], [grid([[3]]), "VALUE", 0.25]]))
        self.assertAlmostEqual(cells(summed)[0][0], 2.4)

    # ---- extraction

    def test_extract_by_circle_polygon_and_points(self):
        nine = grid([[1, 2, 3], [4, 5, 6], [7, 8, 9]])
        # Centres x 5, 15, 25 and y 25, 15, 5; the circle of 10 round (15,15) takes the middle cell and its four
        # orthogonal neighbours, exactly 10 away.
        self.assertEqual(cells(sa.ExtractByCircle(nine, arcpy.Point(15, 15), 10)), [[ND, 2, ND], [4, 5, 6], [ND, 8, ND]])
        self.assertEqual(cells(sa.ExtractByCircle(nine, arcpy.Point(15, 15), 10, "OUTSIDE")), [[1, ND, 3], [ND, ND, ND], [7, ND, 9]])
        square = [arcpy.Point(0, 0), arcpy.Point(0, 20), arcpy.Point(20, 20), arcpy.Point(20, 0), arcpy.Point(0, 0)]
        self.assertEqual(cells(sa.ExtractByPolygon(nine, square)), [[ND, ND, ND], [4, 5, ND], [7, 8, ND]])
        self.assertEqual(cells(sa.ExtractByPoints(nine, [arcpy.Point(12, 3)])), [[ND, ND, ND], [ND, ND, ND], [ND, 8, ND]])

    # ---- generalization

    def test_aggregate_counts_blocks_from_the_bottom_left(self):
        nine = grid([[1, 2, 3], [4, 5, 6], [7, 8, 9]])
        # EXPAND adds a NoData row on top and a column on the right: blocks {1,2} {3} / {4,5,7,8} {6,9}.
        summed = sa.Aggregate(nine, 2)
        self.assertEqual(cells(summed), [[3, 3], [24, 15]])
        self.assertEqual((summed.extent.XMin, summed.extent.YMin, summed.extent.YMax, summed.meanCellWidth), (0.0, 0.0, 40.0, 20.0))
        self.assertEqual(cells(sa.Aggregate(nine, 2, "SUM", "TRUNCATE")), [[24]])
        self.assertEqual(cells(sa.Aggregate(nine, 2, "MEAN", "EXPAND", "NODATA")), [[ND, ND], [6.0, ND]])

    def test_region_group_numbers_regions_in_scan_order(self):
        values = grid([[1, 1, 2], [3, 1, 2], [3, 3, 1]])
        self.assertEqual(cells(sa.RegionGroup(values, "FOUR", "WITHIN", "NO_LINK")), [[1, 1, 2], [3, 1, 2], [3, 3, 4]])
        self.assertEqual(cells(sa.RegionGroup(values, "EIGHT", "WITHIN", "NO_LINK")), [[1, 1, 2], [3, 1, 2], [3, 3, 1]])
        self.assertEqual(cells(sa.RegionGroup(values, "FOUR", "WITHIN", "NO_LINK", 3)), [[1, 1, 2], [0, 1, 2], [0, 0, 3]])
        self.assertEqual(cells(sa.RegionGroup(values, "FOUR", "CROSS", "NO_LINK")), [[1, 1, 1], [1, 1, 1], [1, 1, 1]])

    # ---- density

    def test_point_and_line_density(self):
        points = self.features("p", "POINT", [(point(0, 0),), (point(20, 0),), (point(0, 20),), (point(20, 20),)])
        # A 2 × 2 grid of 10-unit cells; each centre is 7.07 from one point and 15.8 or more from the rest.
        density = sa.PointDensity(points, "NONE", 10, sa.NbrCircle(10, "MAP"), "SQUARE_MAP_UNITS")
        for row in cells(density):
            for value in row:
                self.assertAlmostEqual(value, 1 / (100 * math.pi))
        per_km = cells(sa.PointDensity(points, "NONE", 10, sa.NbrCircle(10, "MAP")))
        self.assertAlmostEqual(per_km[0][0], 1e6 / (100 * math.pi), places=3)
        lines = self.features("l", "POLYLINE", [(arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(20, 0)]), 3857),)])
        # One row of three cells centred on the line's extent's corner, as Pro 3.7 lays it (measured 2026-11-08):
        # centres (0,0), (10,0) and (20,0). The circle of 10 round (0,0) holds 0 … 10 of the line; round (10,0) all 20.
        line_density = cells(sa.LineDensity(lines, "NONE", 10, 10, "SQUARE_MAP_UNITS"))
        self.assertAlmostEqual(line_density[0][0], 10 / (100 * math.pi))
        # In kilometres per square kilometre: metres per square metre times 1000.
        self.assertAlmostEqual(cells(sa.LineDensity(lines, "NONE", 10, 10))[0][1], 1000 * 20 / (100 * math.pi), places=4)

    # ---- distance

    def test_euclidean_direction_and_allocation(self):
        source = grid([[-9, -9, -9], [-9, 1, -9], [-9, -9, -9]], nodata=-9)
        # Toward the source, in whole degrees: Pro 3.7 measured 2026-11-08 (was the bearing from the source).
        self.assertEqual(cells(sa.EucDirection(source)), [[135, 180, 225], [90, 0, 270], [45, 360, 315]])
        two = grid([[1, -9, -9], [-9, -9, -9], [-9, -9, 2]], nodata=-9)
        allocated = cells(sa.EucAllocation(two))
        self.assertEqual((allocated[0][1], allocated[1][0], allocated[2][1], allocated[1][2]), (1, 1, 2, 2))
        self.assertEqual(cells(sa.EucAllocation(two, 10))[0], [1, 1, ND])

    def test_cost_distance_back_link_and_allocation(self):
        cost = grid([[1, 1, 1], [1, 5, 1], [1, 1, 1]])
        source = grid([[1, -9, -9], [-9, -9, -9], [-9, -9, -9]], nodata=-9)
        distance = cells(sa.CostDistance(source, cost), places=4)
        # Round the costly middle: 10 east, a diagonal 1.414214 · 10 to (1,2), 10 south.
        self.assertEqual(distance[0][:3], [0.0, 10.0, 20.0])
        self.assertEqual(distance[2][2], round(34.14214, 4))
        back = cells(sa.CostBackLink(source, cost))
        self.assertEqual((back[0][0], back[0][1], back[1][0]), (0, 5, 7))
        self.assertEqual(cells(sa.CostDistance(source, cost, 15))[0], [0.0, 10.0, ND])
        holed = grid([[1, -9, 1], [1, 1, 1], [1, 1, 1]], nodata=-9)
        self.assertEqual(cells(sa.CostDistance(source, holed))[0][1], ND)
        two = grid([[1, -9, -9], [-9, -9, -9], [-9, -9, 2]], nodata=-9)
        allocated = cells(sa.CostAllocation(two, grid([[1] * 3] * 3)))
        self.assertEqual((allocated[0][1], allocated[2][1]), (1, 2))
        # D-332: the source characteristics, as PathDistance takes them; a row of cost 1 steps 10 a cell.
        row = grid([[1, 1, 1, 1]])
        start = grid([[1, -9, -9, -9]], nodata=-9)
        self.assertEqual(cells(sa.CostDistance(start, row, source_start_cost=5, source_resistance_rate=0.1,
                                               source_direction="TO_SOURCE"), places=4), [[5.0, 15.0, 26.5, 39.15]])
        self.assertEqual(cells(sa.CostAllocation(grid([[1, -9, -9, -9, 2]], nodata=-9), grid([[1] * 5]),
                                                 source_capacity=15)), [[1, 1, ND, 2, 2]])
        # FROM_SOURCE, the default: start 5 uncompounded, then 10, 11, 12.1 (D-332).
        self.assertEqual(cells(sa.CostDistance(start, row, source_start_cost=5, source_resistance_rate=0.1),
                               places=4), [[5.0, 15.0, 26.0, 38.1]])

    # ---- hydrology

    def test_flow_direction_normal_and_forced(self):
        surface = grid(DEM)
        # East everywhere; the eastern column has no lower cell to the east and falls south; the south-east corner
        # has no lower neighbour at all and flows out of the raster, diagonally.
        self.assertEqual(cells(sa.FlowDirection(surface)), [[1, 1, 1, 4], [1, 1, 1, 4], [1, 1, 1, 4], [1, 1, 1, 2]])
        self.assertEqual(cells(sa.FlowDirection(surface, "FORCE")),
                         [[32, 64, 64, 128], [16, 1, 1, 1], [16, 1, 1, 1], [8, 4, 4, 2]])
        # A positive steepest drop shared by two neighbours still needs Greenlee's unpublished table.
        with self.assertRaises(arcpy.NotBuiltError):
            sa.FlowDirection(grid([[9, 9, 9], [9, 9, 0], [9, 0, 9]]))

    def test_accumulation_basin_watershed_length_and_streams(self):
        direction = sa.FlowDirection(grid(DEM))
        accumulation = sa.FlowAccumulation(direction)
        # Each row gathers 0, 1, 2 to the east; the eastern column gathers its row and everything above it.
        self.assertEqual(cells(accumulation), [[0, 1, 2, 3], [0, 1, 2, 7], [0, 1, 2, 11], [0, 1, 2, 15]])
        self.assertEqual(cells(sa.Basin(direction)), [[1] * 4] * 4)
        pour = grid([[-9, -9, -9, -9], [-9, -9, -9, 7], [-9, -9, -9, -9], [-9, -9, -9, -9]], nodata=-9)
        self.assertEqual(cells(sa.Watershed(direction, pour)), [[7] * 4, [7] * 4, [ND] * 4, [ND] * 4])
        self.assertEqual(cells(sa.FlowLength(direction)), [[60, 50, 40, 30], [50, 40, 30, 20], [40, 30, 20, 10], [30, 20, 10, 0]])
        self.assertEqual(cells(sa.FlowLength(direction, "UPSTREAM"))[3][3], 60.0)
        streams = sa.SetNull(accumulation < 2, 1)
        self.assertEqual(cells(sa.StreamOrder(streams, direction)),
                         [[ND, ND, 1, 1], [ND, ND, 1, 2], [ND, ND, 1, 2], [ND, ND, 1, 2]])
        self.assertEqual(cells(sa.StreamOrder(streams, direction, "SHREVE")),
                         [[ND, ND, 1, 1], [ND, ND, 1, 2], [ND, ND, 1, 3], [ND, ND, 1, 4]])
        self.assertEqual(cells(sa.StreamLink(streams, direction)),
                         [[ND, ND, 1, 1], [ND, ND, 2, 3], [ND, ND, 4, 5], [ND, ND, 6, 7]])
        point_raster = grid([[-9, -9, -9, -9], [-9, 9, -9, -9], [-9, -9, -9, -9], [-9, -9, -9, -9]], nodata=-9)
        # Within 10 of (1,1) the most accumulated cell is (1,2), with 2.
        self.assertEqual(cells(sa.SnapPourPoint(point_raster, accumulation, 10))[1], [ND, ND, 9, ND])

    def test_sink_and_fill(self):
        self.assertEqual(cells(sa.Sink(grid([[1, 0], [3, 4]]))), [[ND, 1], [1, ND]])
        # Measured in ArcGIS Pro 3.7, 2026-11-08: two cells pointing at each other (1 east, 16 west), as Flow Direction
        # writes a one-cell pit, are a sink; the cell flowing into them (1 east, from the left) and one leaving the
        # raster (4 south) are not.
        self.assertEqual(cells(sa.Sink(grid([[1, 1, 16, 4]]))), [[ND, 1, 1, ND]])
        self.assertEqual(cells(sa.Fill(grid([[5, 5, 5], [5, 1, 5], [5, 5, 5]]))), [[5, 5, 5], [5, 5, 5], [5, 5, 5]])
        self.assertEqual(cells(sa.Fill(grid([[5, 5, 5], [5, 1, 2], [5, 5, 5]])))[1], [5, 2, 2])
        with self.assertRaises(arcpy.NotBuiltError):
            sa.Fill(grid([[1]]), 3)

    def test_tools_refused_by_name_say_why(self):
        for name, words in (("TabulateArea", "fields"),):
            with self.assertRaises(arcpy.NotBuiltError) as raised:
                getattr(sa, name)("a", "b")
            self.assertIn(words, str(raised.exception))


class RoundFour(_Fixture):
    def test_corridor_fuzzy_overlay_and_transpose_bits(self):
        self.assertEqual(cells(sa.Corridor(grid([[1, 2]]), grid([[10, 20]]))), [[11.0, 22.0]])
        a, b = grid([[0.2, 0.9]]), grid([[0.5, 0.4]])
        self.assertEqual(cells(sa.FuzzyOverlay([a, b], "AND")), [[0.2, 0.4]])
        self.assertEqual(cells(sa.FuzzyOverlay([a, b], "OR")), [[0.5, 0.9]])
        with self.assertRaises(arcpy.NotBuiltError):
            sa.FuzzyOverlay([a, b], None)
        # PRODUCT, SUM and GAMMA by How Fuzzy Overlay works, built after ArcGIS Pro 3.7 was measured (2026-11-08).
        self.assertEqual([round(v, 6) for v in cells(sa.FuzzyOverlay([a, b], "SUM"))[0]], [0.6, 0.94])
        # Bits 12 and 13 (4096 + 8192) move to bits 0 and 1; a fill of 8 keeps bit 3.
        bits = grid([[12288, 4096]])
        self.assertEqual(cells(sa.TransposeBits(bits)), [[3, 1]])
        self.assertEqual(cells(sa.TransposeBits(bits, [12, 13], [0, 1], 8)), [[11, 9]])

    def test_point_and_line_statistics(self):
        points = self.features("ps", "POINT", [(1.0, point(0, 0)), (3.0, point(20, 0)), (5.0, point(0, 20)),
                                               (7.0, point(20, 20))], [("v", "DOUBLE")])
        # A 2 × 2 grid of 10-unit cells; the default 3 × 3-cell rectangle reaches 15 each way, so every centre sees all
        # four points (those exactly 15 away on its edge included).
        self.assertEqual(cells(sa.PointStatistics(points, "v", 10)), [[4.0, 4.0], [4.0, 4.0]])
        self.assertEqual(cells(sa.PointStatistics(points, "v", 10, statistics_type="MEDIAN"))[0][0], 3.0)
        self.assertEqual(cells(sa.PointStatistics(points, "v", 10, statistics_type="MAJORITY"))[0][0], 1.0)
        # A circle of 10 round (5,5) holds only the point at (0,0), 7.07 away.
        self.assertEqual(cells(sa.PointStatistics(points, "v", 10, sa.NbrCircle(10, "MAP")))[1][0], 1.0)
        lines = self.features("ls", "POLYLINE", [
            (2.0, arcpy.Polyline(arcpy.Array([arcpy.Point(0, 0), arcpy.Point(20, 0)]), 3857)),
            (4.0, arcpy.Polyline(arcpy.Array([arcpy.Point(0, 10), arcpy.Point(20, 10)]), 3857))], [("v", "DOUBLE")])
        # Two rows of three cells centred on the lines' extent's corner (Pro 3.7, measured 2026-11-08): the centre
        # (10,0) is row 1, column 1. The circle of 15 round it holds both lines whole (the upper is 10 away, so
        # √125 > 10 either side of x = 10): mean 3, LENGTH Σ length·value = 120, range 2.
        self.assertAlmostEqual(cells(sa.LineStatistics(lines, "v", 10, 15))[1][1], 3.0)
        self.assertAlmostEqual(cells(sa.LineStatistics(lines, "v", 10, 15, "LENGTH"))[1][1], 120.0)  # 20·2 + 20·4: Pro 3.7
        self.assertEqual(cells(sa.LineStatistics(lines, "v", 10, 15, "RANGE"))[1][1], 2.0)
        with self.assertRaises(arcpy.NotBuiltError):
            sa.LineStatistics(lines, "v", 10, 10, "MEDIAN")

    def test_aliases_and_classified_names(self):
        self.assertIs(sa.Raster, arcpy.Raster)
        self.assertIs(sa.Point, arcpy.Point)
        self.assertIs(sa.Extent, arcpy.Extent)
        # TopoLake (ADR-242) and VfBinary (ADR-241) are built now; AreaSolarRadiation below still says why it is refused.
        with self.assertRaises(arcpy.NotBuiltError) as raised:
            sa.FeatureSolarRadiation("a")
        self.assertIn("names none of those fields", str(raised.exception))

    def test_buffered_and_an_irregular_neighbourhood(self):
        values = grid([[1, 2, 3], [4, 5, 6], [7, 8, 9]])
        self.assertIs(sa.Buffered(values), values)
        cross = os.path.join(self.directory, "cross.txt")
        with open(cross, "w", encoding="utf-8") as kernel:
            kernel.write("3 3\n0 1 0\n1 1 1\n0 2 0\n")
        # The centre's cross: 2 + 4 + 5 + 6 + 8 (the 2 in the file counts as 1, being not 0).
        self.assertEqual(cells(sa.FocalStatistics(values, sa.NbrIrregular(cross), "SUM"))[1][1], 25)
        even = os.path.join(self.directory, "even.txt")
        with open(even, "w", encoding="utf-8") as kernel:
            kernel.write("2 2\n1 1\n1 1\n")
        with self.assertRaises(arcpy.NotBuiltError):
            sa.FocalStatistics(values, sa.NbrIrregular(even), "SUM")



class Equivalents(_Fixture):
    """ADR-224 §5.6: each equivalent against a hand-worked answer of the method it names."""

    def test_kernel_density_is_silvermans_quartic_kernel(self):
        points = self.features("kd", "POINT", [(point(0, 0),), (point(20, 0),)])
        # One row of 10-unit cells centred on the points' extent's corner, as Pro 3.7 lays it (measured 2026-11-08):
        # centres (0,0), (10,0) and (20,0). At (0,0) the point there gives the kernel's peak, 3/(π·10²); (20,0) is 20
        # away, beyond the radius. At (10,0) both points lie exactly on the radius and give nothing: 0, as Pro writes.
        density = cells(sa.KernelDensity(points, "NONE", 10, 10, "SQUARE_MAP_UNITS"))
        self.assertAlmostEqual(density[0][0], 3 / (100 * math.pi))
        self.assertEqual(density[0][1], 0.0)
        self.assertAlmostEqual(density[0][2], 3 / (100 * math.pi))
        counts = cells(sa.KernelDensity(points, "NONE", 10, 10, out_cell_values="EXPECTED_COUNTS"))
        self.assertAlmostEqual(counts[0][0], 3 / math.pi)
        # Silverman's rule: the mean centre is (10,0), SD = 10, the median distance 10, so the spread is
        # min(10, 10·√(1/ln 2)) = 10 and the radius 0.9 · 10 · 2^(−0.2).
        self.assertAlmostEqual(sa._silverman_radius(np.array([0.0, 20.0]), np.array([0.0, 0.0]), np.ones(2)),
                               9 * 2 ** -0.2)
        with self.assertRaises(arcpy.NotBuiltError):
            sa.KernelDensity(points, "NONE", 10, 10, in_barriers=points)

    def test_threshold_stretch_and_principal_components(self):
        self.assertEqual(cells(sa.Threshold(grid([[0.0, 0.0, 10.0, 10.0]]))), [[0, 0, 1, 1]])
        # Measured in ArcGIS Pro 3.7, 2026-11-08: the cut is the best bin's centre, not its upper edge. Over 0 … 256 in
        # 256 bins the first bin is [0, 1) and best (it splits the 0s from the 256s); 0.7 is above its centre 0.5.
        self.assertEqual(cells(sa.Threshold(grid([[0.0, 0.0, 0.0, 0.7, 256.0, 256.0, 256.0]]))), [[0, 0, 0, 1, 1, 1, 1]])
        ramp = grid([[0.0, 5.0, 10.0]])
        # Measured in ArcGIS Pro 3.7, 2026-11-08: min and max are the output's range, and the output is floating point
        # (this pinned rounded 0…255 integers before): 0, 5, 10 stretched to 0…10 stay 0, 5, 10; to 0…255, 127.5.
        self.assertEqual(cells(sa.Stretch(ramp, "MinMax", 0, 10)), [[0, 5, 10]])
        self.assertEqual(cells(sa.Stretch(ramp, "MinMax")), [[0, 127.5, 255]])
        self.assertEqual(sa.Stretch(ramp, "MinMax").pixelType, "F32")
        # The sample standard deviation of 2, 8, 2, 8 is √12 ≈ 3.46: one either side reaches past 2 and 8, so the range
        # is held at the minimum and maximum, as Pro holds it.
        self.assertEqual(cells(sa.Stretch(grid([[2.0, 8.0, 2.0, 8.0]]), "StdDev", num_stddev=1)), [[0, 255, 0, 255]])
        # 0, 4, 8, 12: mean 6, sample σ = √(80/3) ≈ 5.164; half a σ either side is 3.418 … 8.582.
        quarter = cells(sa.Stretch(grid([[0.0, 4.0, 8.0, 12.0]]), "StdDev", num_stddev=0.5))[0]
        sd = math.sqrt(80 / 3)
        self.assertEqual(quarter[0], 0)
        self.assertAlmostEqual(quarter[1], 255 * (4 - (6 - sd / 2)) / sd, places=3)
        self.assertEqual(quarter[3], 255)
        with self.assertRaises(arcpy.NotBuiltError):
            sa.Stretch(ramp, "PercentClip")
        b1 = grid([[0.0, 1.0, 2.0, 3.0]])
        b2 = grid([[0.0, 2.0, 4.0, 6.0]])
        # All the variance lies along (1, 2)/√5. This pinned the values less the means (−7.5/√5 first) until ArcGIS
        # Pro 3.7 was measured, 2026-11-08: the bands are projected as they are, so the cells are (0, 5, 10, 15)/√5.
        first = cells(sa.PrincipalComponents([b1, b2], 1))[0]
        for got, want in zip(first, [0, 5, 10, 15]):
            self.assertAlmostEqual(got, want / math.sqrt(5), places=5)

    def test_natural_neighbor_reproduces_a_plane(self):
        # Sibson's interpolant is exact for linear data: on z = x it answers each centre's x.
        points = self.features("nn", "POINT", [(0.0, point(0, 0)), (10.0, point(10, 0)), (0.0, point(0, 10)),
                                               (10.0, point(10, 10))], [("z", "DOUBLE")])
        got = cells(sa.NaturalNeighbor(points, "z", 2.5))
        # Centres every 2.5 from 0 to 10 (the grid Pro 3.7 lays out (ADR-233 condition 4): cell centres on the points' extent); the inner ones answer their x.
        for row in got[1:-1]:
            for value, x in zip(row[1:-1], (2.5, 5.0, 7.5)):
                self.assertAlmostEqual(value, x, places=4)

    def test_kriging_solves_cressies_system(self):
        # The spherical model at half its range: 1.5·0.5 − 0.5·0.125 = 0.6875; the exponential at its range 1 − e⁻³.
        self.assertAlmostEqual(float(sa._semivariogram("SPHERICAL", 5.0, 10.0, 1.0, 0.0)), 0.6875)
        self.assertAlmostEqual(float(sa._semivariogram("EXPONENTIAL", 10.0, 10.0, 1.0, 0.0)), 1 - math.exp(-3))
        # Two points on a line under a linear model γ(h) = h/100: λ₁ = ½ − (γ₁₀ − γ₂₀)/(2γ₁₂) = 0.75 at x = 2.5.
        xs, ys, zs = np.array([0.0, 10.0]), np.array([0.0, 0.0]), np.array([0.0, 10.0])
        estimate, _ = sa._krige(xs, ys, zs, 2.5, 0.0, "LINEAR", 100.0, 1.0, 0.0, None)
        self.assertAlmostEqual(estimate, 2.5)
        # At the midpoint the weights are ½ each and μ = γ₁₀ − ½γ₁₂ = 0, so the variance is γ₁₀ = 0.05.
        self.assertAlmostEqual(sa._krige(xs, ys, zs, 5.0, 0.0, "LINEAR", 100.0, 1.0, 0.0, None)[1], 0.05)
        # Four points at the corners of a square, the middle cell at its centre: equal weights, the mean; a cell centred
        # on a point (the grid Pro 3.7 lays out (ADR-233 condition 4): cell centres on the points' extent) takes its value, kriging being exact.
        points = self.features("kr", "POINT", [(1.0, point(5, 5)), (2.0, point(15, 5)), (3.0, point(5, 15)),
                                               (4.0, point(15, 15))], [("z", "DOUBLE")])
        model = sa.KrigingModelOrdinary("SPHERICAL", 10, 20, 1, 0)
        got = cells(sa.Kriging(points, "z", model, 5))
        self.assertAlmostEqual(got[1][1], 2.5, places=5)
        self.assertAlmostEqual(got[0][0], 3.0, places=5)
        with self.assertRaises(arcpy.NotBuiltError):
            sa.Kriging(points, "z", sa.KrigingModelOrdinary("STABLE", 10, 20, 1, 0), 10)

    def test_d_infinity_is_tarbotons(self):
        east = grid([[100 - 10 * k for k in range(4)] for _ in range(4)])
        self.assertEqual(cells(sa.FlowDirection(east, flow_direction_type="DINF"))[1][1], 0.0)
        # Falling 10 per cell both east and north: s1 = s2 = 1 on the east–north-east facet, r = 45°.
        north_east = grid([[100 - 10 * k + 10 * r for k in range(4)] for r in range(4)])
        self.assertAlmostEqual(cells(sa.FlowDirection(north_east, flow_direction_type="DINF"))[1][1], 45.0, places=4)
        flat = cells(sa.FlowDirection(grid([[5.0] * 4] * 4), flow_direction_type="DINF"))
        self.assertEqual((flat[1][1], flat[0][1], flat[1][3]), (-1.0, 90.0, 0.0))


    def test_viewshed_and_visibility_by_r3_line_of_sight(self):
        ridge = grid([[0.0, 0.0, 10.0, 0.0, 0.0]])
        observer = self.features("obs", "POINT", [(point(5, 5),)])
        # The eye at 0 + 1 (OFFSETA's default). The ridge at 20 rises at (10 − 1)/20 = 0.45; behind it the cells at
        # 30 and 40 fall below that angle, and would need 1 + 0.45·30 = 14.5 and 1 + 0.45·40 = 19.
        agl = os.path.join(self.directory, "agl.tif")
        self.assertEqual(cells(sa.Viewshed(ridge, observer, out_agl_raster=agl)), [[1, 1, 1, 0, 0]])
        self.assertEqual([round(v, 6) for v in cells(arcpy.Raster(agl))[0]], [0.0, 0.0, 0.0, 14.5, 19.0])
        # A flat radius of 15 keeps the two nearest; a 3D radius of 25 reaches the ridge, √(20² + 9²) ≈ 21.9 away.
        self.assertEqual(cells(sa.Visibility(ridge, observer, outer_radius=-15)), [[1, 1, 0, 0, 0]])
        self.assertEqual(cells(sa.Visibility(ridge, observer, outer_radius=25)), [[1, 1, 1, 0, 0]])
        self.assertEqual(cells(sa.Visibility(ridge, observer, nonvisible_cell_value="NODATA")), [[1, 1, 1, ND, ND]])
        with self.assertRaises(arcpy.NotBuiltError):
            sa.Visibility(ridge, observer, analysis_type="OBSERVERS")

    def test_expand_shrink_and_nibble(self):
        two = grid([[1, 0, 0], [0, 0, 0], [0, 0, 2]])
        # The centre is reached by 1 and 2 once each: the tie goes to the lower.
        self.assertEqual(cells(sa.Expand(two, 1, [1, 2])), [[1, 1, 0], [1, 1, 2], [0, 2, 2]])
        # Within one cell width: the orthogonal neighbours only, not the diagonal centre.
        self.assertEqual(cells(sa.Expand(two, 1, [1, 2], "DISTANCE")), [[1, 1, 0], [1, 0, 2], [0, 2, 2]])
        five = grid([[5, 5, 5], [5, 5, 5], [5, 5, 7]])
        self.assertEqual(cells(sa.Shrink(five, 1, [5])), [[5, 5, 5], [5, 7, 7], [5, 7, 7]])
        values = grid([[1, 2, 3, 4]])
        mask = grid([[1, -9, -9, 1]], nodata=-9)
        self.assertEqual(cells(sa.Nibble(values, mask)), [[1, 1, 4, 4]])

    def test_filter_and_merge(self):
        nine = grid([[1.0, 2.0, 3.0], [4.0, 5.0, 6.0], [7.0, 8.0, 9.0]])
        low = cells(sa.Filter(nine))
        # The centre's mean is 5; the corner sees only 1, 2, 4 and 5.
        self.assertEqual((low[1][1], low[0][0]), (5.0, 3.0))
        high = cells(sa.Filter(nine, "HIGH"))
        # Esri's kernel, 6.8 centre, −1 sides, −0.7 corners, over the valid cells (Pro 3.7 measured 2026-11-08): 34 − 20
        # − 14 = 0 at the centre; at the corner 6.8·1 − (2 + 4) − 0.7·5 = −2.7.
        self.assertEqual(high[1][1], 0.0)
        self.assertAlmostEqual(high[0][0], -2.7)
        a, b = grid([[1, -9]], nodata=-9), grid([[3, 4]])
        self.assertEqual(cells(sa.Merge([a, b])), [[1, 4]])
        self.assertEqual(cells(sa.Merge([a, b], "LAST")), [[3, 4]])
        self.assertEqual(cells(sa.Merge([a, b], "SUM")), [[4, 4]])
        self.assertEqual(cells(sa.Merge([a, b], "MEAN")), [[2.0, 4.0]])


    def test_fuzzy_membership_functions(self):
        x = grid([[0.0, 5.0, 10.0]])
        # Gaussian about 5 with spread 0.1: exp(−0.1·25) at 0 and 10, 1 at 5.
        got = cells(sa.FuzzyMembership(x, sa.FuzzyGaussian(5, 0.1)))[0]
        self.assertAlmostEqual(got[0], math.exp(-2.5))
        self.assertEqual(got[1], 1.0)
        self.assertAlmostEqual(cells(sa.FuzzyMembership(x, sa.FuzzyNear(5, 0.1)))[0][0], 1 / 3.5)
        # Large about 5: 1/(1 + (10/5)^−5) = 32/33 at 10, ½ at the midpoint, 0 at 0.
        large = cells(sa.FuzzyMembership(x, sa.FuzzyLarge(5, 5)))[0]
        self.assertEqual((large[0], large[1]), (0.0, 0.5))
        self.assertAlmostEqual(large[2], 32 / 33)
        self.assertEqual(cells(sa.FuzzyMembership(x, sa.FuzzyLinear(0, 10)))[0], [0.0, 0.5, 1.0])
        # Mean 5, σ = √(50/3): MSSmall above the mean is 2σ/(x − 5 + 2σ).
        sigma = math.sqrt(50 / 3)
        self.assertAlmostEqual(cells(sa.FuzzyMembership(x))[0][2], 2 * sigma / (5 + 2 * sigma))
        self.assertAlmostEqual(cells(sa.FuzzyMembership(x, sa.FuzzyMSLarge()))[0][2], 1 - 2 * sigma / (5 + 2 * sigma))
        self.assertAlmostEqual(cells(sa.FuzzyMembership(x, sa.FuzzyLinear(0, 10), "VERY"))[0][1], 0.25)


    def test_profile_and_plan_curvature(self):
        xs = [5 + 10 * c for c in range(5)]
        ridge = grid([[-x * x for x in xs] for _ in range(5)])
        profile, plan = os.path.join(self.directory, "profile.tif"), os.path.join(self.directory, "plan.tif")
        sa.Curvature(ridge, 1, profile, plan)
        # At x = 25: D = ((−225 − 1225)/2 + 625)/100 = −1, G = (225 − 1225)/20 = −50, E = F = H = 0, so profile is
        # 2·D·G²/G²·100 = −200 (upwardly convex is negative) and plan is −2·E·100 = 0.
        self.assertAlmostEqual(cells(arcpy.Raster(profile))[2][2], -200.0, places=3)
        self.assertAlmostEqual(cells(arcpy.Raster(plan))[2][2], 0.0, places=3)


    def test_spline_passes_through_the_points(self):
        # Three points on the plane z = x + 2y: the regularized spline's linear trend takes them exactly and every λ is
        # 0, so each cell centre lies on the plane.
        plane = self.features("sp", "POINT", [(0.0, point(0, 0)), (20.0, point(20, 0)), (40.0, point(0, 20))],
                              [("z", "DOUBLE")])
        got = cells(sa.Spline(plane, "z", 10))
        for row, y in zip(got, (20, 10, 0)):
            for value, x in zip(row, (0, 10, 20)):
                self.assertAlmostEqual(value, x + 2 * y, places=3)
        # Tension, two points 20 apart on one row: T = a₁ is their mean and the λs are opposite, so the midpoint is the
        # mean, 5.
        pair = self.features("pr", "POINT", [(0.0, point(0, 0)), (10.0, point(20, 0))], [("z", "DOUBLE")])
        middle = cells(sa.Spline(pair, "z", 10, "TENSION"))
        self.assertAlmostEqual(middle[0][1], 5.0, places=3)
        many = self.features("many", "POINT", [(float(k), point(k, k)) for k in range(13)], [("z", "DOUBLE")])
        with self.assertRaises(arcpy.NotBuiltError):
            sa.Spline(many, "z", 1)


    def test_percentile_across_rasters(self):
        four = [grid([[v]]) for v in (1, 2, 3, 4)]
        # Integers take type 1: the 50th percentile of 1…4 is the value at rank ⌈0.5·4⌉ = 2.
        self.assertEqual(cells(sa.Percentile(four, 50)), [[2]])
        # Floating point takes type 7: position 1 + 3·0.5 = 2.5, half way between 2 and 3.
        self.assertEqual(cells(sa.Percentile([grid([[float(v)]]) for v in (1, 2, 3, 4)], 50)), [[2.5]])


    def test_flats_drain_by_garbrecht_and_martz(self):
        # A 3 × 3 flat at 5 inside a rim of 10, draining through the rim cell at 4 on the right of the middle row.
        dem = [[10, 10, 10, 10, 10], [10, 5, 5, 5, 10], [10, 5, 5, 5, 4], [10, 5, 5, 5, 10], [10, 10, 10, 10, 10]]
        # The cells beside the outlet fall to it (1, 2, 128); the six flat cells are resolved by Barnes et al.'s
        # gradients: away from the rim, (2,2) is 2 steps in and the rest 1; towards the outlet the low edges are
        # step 1. Combined: the low edges 2, (1,2) and (3,2) 2 − 1 + 4 = 5, (2,2) 2 − 2 + 4 = 4, the west column
        # 2 − 1 + 6 = 7. Each flat cell flows to its least neighbour, the first in code order on a tie.
        self.assertEqual(cells(sa.FlowDirection(grid(dem))),
                         [[2, 4, 4, 4, 8], [1, 2, 1, 2, 4], [1, 1, 1, 1, 1], [1, 128, 1, 128, 64],
                          [128, 64, 64, 64, 32]])
        # With the flat resolved, everything reaches the outlet: 24 cells flow into it.
        self.assertEqual(cells(sa.FlowAccumulation(sa.FlowDirection(grid(dem))))[2][4], 24)
        # A flat with no outlet keeps the sum of its tied directions, Esri's undefined value.
        pit = [[10] * 5, [10, 5, 5, 5, 10], [10, 5, 5, 5, 10], [10, 5, 5, 5, 10], [10] * 5]
        got = cells(sa.FlowDirection(grid(pit)))
        self.assertEqual((got[2][2], got[1][1]), (255, 7))
        flat = cells(sa.FlowDirection(grid([[5] * 4] * 4)))
        self.assertEqual([flat[1][1:3], flat[2][1:3]], [[8, 1], [2, 1]])


    def test_rescale_by_function(self):
        x = grid([[0.0, 5.0, 10.0]])
        # Linear from 0 to 10: f = 0, ½, 1, onto 1 … 10.
        self.assertEqual(cells(sa.RescaleByFunction(x, sa.TfLinear(0, 10))), [[1.0, 5.5, 10.0]])
        # Exponential with the page's fitted defaults from 1 to 10: shift 0 and base ln 10 / 10, so f = 1, √10, 10.
        got = cells(sa.RescaleByFunction(x, sa.TfExponential()))[0]
        for value, want in zip(got, (1.0, math.sqrt(10), 10.0)):
            self.assertAlmostEqual(value, want)
        # Gaussian with its default spread ln 10 · 4 / 5² about 5: f = 10⁻⁴, 1, 10⁻⁴, rescaled to 1, 10, 1.
        self.assertEqual([round(v, 6) for v in cells(sa.RescaleByFunction(x, sa.TfGaussian()))[0]], [1.0, 10.0, 1.0])
        # Thresholds at 2 and 8: the cells outside take the defaults, from_scale and to_scale, or NoData.
        wide = grid([[0.0, 2.0, 5.0, 8.0, 10.0]])
        self.assertEqual(cells(sa.RescaleByFunction(wide, sa.TfLinear(2, 8, 2, None, 8, "NODATA")), nodata=-1),
                         [[1.0, 1.0, 5.5, 10.0, -1]])


    def test_darcy_flow_and_velocity(self):
        # Head falling 10 per 10-unit cell to the east, T = 1: every face carries U = −1·(−10)/10 = 1 eastward, the
        # seepage velocity is 1/(0.5·2) = 1 due east (90°), and what enters each cell leaves it: the residual is 0.
        head = grid([[100.0 - 10 * k for k in range(4)] for _ in range(4)])
        porosity, thickness, transmissivity = grid([[0.5] * 4] * 4), grid([[2.0] * 4] * 4), grid([[1.0] * 4] * 4)
        magnitude = os.path.join(self.directory, "mag.tif")
        direction = cells(sa.DarcyVelocity(head, porosity, thickness, transmissivity, magnitude))
        self.assertAlmostEqual(direction[1][1], 90.0, places=4)
        self.assertAlmostEqual(cells(arcpy.Raster(magnitude))[1][1], 1.0, places=5)
        self.assertAlmostEqual(cells(sa.DarcyFlow(head, porosity, thickness, transmissivity))[1][1], 0.0, places=5)
        # A cell whose east neighbour has T = 3: the face between them carries the harmonic mean 2·1·3/4 = 1.5, so it
        # loses 1.5·10 = 15 east and gains 1·10 = 10 from the west: a residual of 5.
        uneven = grid([[1.0, 1.0, 1.0, 3.0]] * 4)
        self.assertAlmostEqual(cells(sa.DarcyFlow(head, porosity, thickness, uneven))[1][2], 5.0, places=4)


    def test_majority_filter(self):
        # The centre 1 has all four orthogonal neighbours at 2 (3 of 4 needed): it becomes 2. The edge cells have two
        # matching neighbours of their own value and nothing else to take.
        island = grid([[2, 2, 2], [2, 1, 2], [2, 2, 2]])
        self.assertEqual(cells(sa.MajorityFilter(island)), [[2, 2, 2], [2, 2, 2], [2, 2, 2]])
        # With EIGHT, five 3s around the centre in two groups (N, NE, E and S, SW) are not contiguous: no change.
        split = grid([[0, 0, 3, 3, 0], [0, 0, 1, 3, 0], [0, 3, 3, 0, 0], [0, 0, 0, 0, 0], [0, 0, 0, 0, 0]])
        self.assertEqual(cells(sa.MajorityFilter(split, "EIGHT"))[1][2], 1)
        joined = grid([[0, 3, 3, 3, 0], [0, 3, 1, 3, 0], [0, 0, 0, 0, 0], [0, 0, 0, 0, 0], [0, 0, 0, 0, 0]])
        # N, NE, E, NW and W hold 3 and touch in a ring: five contiguous, so the centre becomes 3.
        self.assertEqual(cells(sa.MajorityFilter(joined, "EIGHT"))[1][2], 3)


class ReclassByAsciiFile(unittest.TestCase):
    """The remap file's format as How Reclass by ASCII File works gives it (read 2026-11-02): ``in : out`` or
    ``min max : out`` lines, # comments, ascending, a shared boundary going to the lower range."""

    def setUp(self):
        self.directory = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.directory, True)
        self.addCleanup(setattr, arcpy.env, "workspace", arcpy.env.workspace)
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None

    def tearDown(self):
        arcpy.env.outputCoordinateSystem = None

    def remap(self, name, *lines):
        path = os.path.join(self.directory, name)
        with open(path, "w", encoding="utf-8") as written:
            for line in lines:
                print(line, file=written)
        return path

    def test_ranges_and_single_values_follow_the_documented_format(self):
        raster = grid([[1, 3, 4], [5, 6, 9]])
        ranges = self.remap("ranges.rmp", "# the page's own example", "1 3 : 4", "3 5 : 6", "5 7 : 8")
        # 1 and 3 are in 1 ≤ v ≤ 3; 4 and 5 in 3 < v ≤ 5; 6 in 5 < v ≤ 7; 9 is in none and keeps its value.
        self.assertEqual(cells(sa.ReclassByASCIIFile(raster, ranges)), [[4, 4, 6], [6, 8, 9]])
        self.assertEqual(cells(sa.ReclassByASCIIFile(raster, ranges, "NODATA")), [[4, 4, 6], [6, 8, ND]])
        single = self.remap("single.txt", "5 : 100", "", "  # a comment", "7 : 200", "9 : 300")
        self.assertEqual(cells(sa.ReclassByASCIIFile(raster, single)), [[1, 3, 4], [100, 6, 300]])
        saved = arcpy.ddd.ReclassByASCIIFile(raster, single, os.path.join(self.directory, "out.tif"), "NODATA")
        self.assertEqual(cells(arcpy.Raster(saved.getOutput(0))), [[ND, ND, ND], [100, ND, 300]])

    def test_a_file_mixing_values_and_ranges_is_read_as_pro_reads_it(self):
        # Measured in ArcGIS Pro 3.7, 2026-11-08: "1 : 10", "2 : 20", "3 4 : 30" over cells 1..4 gives 10, 20, 30, 30.
        raster = grid([[1, 2], [3, 4]])
        mixed = self.remap("mixed.rmp", "1 : 10", "2 : 20", "3 4 : 30")
        self.assertEqual(cells(sa.ReclassByASCIIFile(raster, mixed)), [[10, 20], [30, 30]])

    def test_what_the_pages_forbid_or_leave_open_is_refused(self):
        raster = grid([[1, 3, 4], [5, 6, 9]])
        # A file mixing single values and ranges was refused here until ArcGIS Pro 3.7 was measured reading one
        # (2026-11-08); test_a_file_mixing_values_and_ranges_is_read_as_pro_reads_it pins that.
        for name, lines in (("descending.rmp", ["5 : 1", "2 : 3"]),
                            ("overlap.rmp", ["1 4 : 1", "3 6 : 2"]), ("words.rmp", ["one : 2"]),
                            ("nocolon.rmp", ["1 2"]), ("empty.rmp", ["# nothing"])):
            with self.assertRaises(ValueError, msg=name):
                sa.ReclassByASCIIFile(raster, self.remap(name, *lines))
        with self.assertRaises(ValueError):
            sa.ReclassByASCIIFile(raster, self.remap("remap.csv", "1 : 2"))   # .rmp, .txt or .asc
        # The tool page says a floating-point output halts with an error; How it works says it is truncated.
        with self.assertRaisesRegex(arcpy.NotBuiltError, "truncated"):
            sa.ReclassByASCIIFile(raster, self.remap("float.rmp", "1 : 2.5"))
        # The output is always integer, so a floating-point value kept by DATA cannot be written.
        with self.assertRaises(arcpy.NotBuiltError):
            sa.ReclassByASCIIFile(grid([[1.5, 2.5]]), self.remap("one.asc", "1 2 : 7"))
        self.assertEqual(cells(sa.ReclassByASCIIFile(grid([[1.5, 2.5]]), self.remap("two.asc", "1 2 : 7"), "NODATA")),
                         [[7, ND]])


class RandomRasters(unittest.TestCase):
    """Create Random Raster and Create Normal Raster (tool-reference/spatial-analyst/create-random-raster.html and
    create-normal-raster.html, read 2026-11-02), built as equivalents: their pages document the distribution, the grid
    and the type, and say the random stream does not repeat even for one seed, so only those are checked."""

    def setUp(self):
        arcpy.env.outputCoordinateSystem = None
        arcpy.env.cellSize = None
        arcpy.env.snapRaster = None
        arcpy.env.extent = None

    def tearDown(self):
        self.setUp()

    def test_random_raster_is_uniform_on_zero_to_one_over_the_documented_grid(self):
        self.assertTrue(sa.CreateRandomRaster.__grapy_equivalent__)
        default = sa.CreateRandomRaster(3)
        values = arcpy.RasterToNumPyArray(default)
        self.assertEqual(values.shape, (250, 250))   # the default extent, 0 0 250 250 at the default cell size of 1
        e = default.extent
        self.assertEqual((e.XMin, e.YMin, e.XMax, e.YMax), (0.0, 0.0, 250.0, 250.0))
        self.assertEqual(values.dtype.kind, "f")   # always floating point
        self.assertTrue(((values >= 0) & (values < 1)).all())
        # Uniform on [0, 1): mean ½ and variance 1/12; over 62,500 cells the mean's standard error is 0.0012.
        self.assertAlmostEqual(float(values.mean()), 0.5, delta=0.01)
        self.assertAlmostEqual(float(values.var()), 1 / 12, delta=0.003)
        self.assertEqual(arcpy.RasterToNumPyArray(sa.CreateRandomRaster(3)).tolist(), values.tolist())
        self.assertNotEqual(arcpy.RasterToNumPyArray(sa.CreateRandomRaster(4)).tolist(), values.tolist())
        given = sa.CreateRandomRaster(1, 10, "0 0 100 50")
        self.assertEqual(arcpy.RasterToNumPyArray(given).shape, (5, 10))
        self.assertEqual(given.meanCellWidth, 10.0)
        arcpy.env.cellSize = 2
        self.assertEqual(sa.CreateRandomRaster(1).extent.XMax, 500.0)   # 250 columns of the environment's cell size
        arcpy.env.snapRaster = given
        with self.assertRaises(arcpy.NotBuiltError):
            sa.CreateRandomRaster(1)

    def test_normal_raster_has_mean_zero_and_standard_deviation_one(self):
        self.assertTrue(sa.CreateNormalRaster.__grapy_equivalent__)
        raster = sa.CreateNormalRaster()
        values = arcpy.RasterToNumPyArray(raster)
        self.assertEqual((values.shape, values.dtype.kind), ((250, 250), "f"))
        # Over 62,500 draws the mean's standard error is 0.004 and the standard deviation's 0.003.
        self.assertAlmostEqual(float(values.mean()), 0.0, delta=0.03)
        self.assertAlmostEqual(float(values.std()), 1.0, delta=0.03)
        self.assertEqual(arcpy.RasterToNumPyArray(sa.CreateNormalRaster(25, "0 0 100 75")).shape, (3, 4))


if __name__ == "__main__":
    unittest.main()
