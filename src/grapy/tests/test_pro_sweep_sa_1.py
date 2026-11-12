"""arcpy.sa, ACos … MTVI2, as ArcGIS Pro 3.7 answers it — measured in ArcGIS Pro 3.7, 2026-11-08, by the Pro sweep
(tools/pro-compare/sweep/cases/sa_1.py, results/sa_1.md). Each test pins Pro's answer by an example worked by hand.

Rasters are written north to south with 10-unit cells on EPSG:3857, the lower-left corner at (0, 0).
"""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import sa  # noqa: E402

ND = -1


def grid(values, cell=10.0, nodata=None):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    try:
        return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(0.0, 0.0), cell, cell, nodata)
    finally:
        arcpy.env.outputCoordinateSystem = None


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


class _Fixture(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def features(self, name, shape, rows, fields=()):
        path = os.path.join(self.directory, f"{name}.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, f"{name}.gpkg", shape, spatial_reference=3857)
        for field, kind in fields:
            arcpy.management.AddField(path, field, kind)
        with arcpy.da.InsertCursor(path, [f for f, _ in fields] + ["SHAPE@"]) as written:
            for row in rows:
                written.insertRow(row)
        return path


def point(x, y):
    return arcpy.PointGeometry(arcpy.Point(x, y), 3857)


def line(*xy):
    return arcpy.Polyline(arcpy.Array([arcpy.Point(x, y) for x, y in xy]), 3857)


class Local(_Fixture):
    def test_cell_statistics_majority_tie_is_the_lowest(self):
        # Three different values: no majority, and Pro answers the smallest.
        a, b, c = grid([[7, 1]]), grid([[3, 1]]), grid([[5, 2]])
        self.assertEqual(cells(sa.CellStatistics([a, b, c], "MAJORITY")), [[3, 1]])
        self.assertEqual(cells(sa.CellStatistics([a, b, c], "MINORITY")), [[3, 2]])

    def test_arg_statistics_counts_bands_from_zero(self):
        bands = [grid([[1.0, 9.0]]), grid([[5.0, 2.0]]), grid([[3.0, 4.0]])]
        out = sa.ArgStatistics(bands, "MAX")
        self.assertTrue(out.isInteger)
        self.assertEqual(cells(out), [[1, 0]])

    def test_arithmetic_answers_floating_point(self):
        out = sa.Arithmetic(grid([[2, 3]]), grid([[4, 5]]), "Multiply")
        self.assertFalse(out.isInteger)
        self.assertEqual(cells(out), [[8.0, 15.0]])

    def test_fuzzy_overlay_product_sum_gamma(self):
        a, b = grid([[0.5]]), grid([[0.4]])
        self.assertAlmostEqual(cells(sa.FuzzyOverlay([a, b], "PRODUCT"))[0][0], 0.2)
        self.assertAlmostEqual(cells(sa.FuzzyOverlay([a, b], "SUM"))[0][0], 0.7)        # 1 − 0.5 · 0.6
        self.assertAlmostEqual(cells(sa.FuzzyOverlay([a, b], "GAMMA", 0.5))[0][0], math.sqrt(0.7 * 0.2))

    def test_lookup_of_count_without_a_table_is_floating_point(self):
        out = sa.Lookup(grid([[1, 1], [2, 1]]), "Count")
        self.assertFalse(out.isInteger)
        self.assertEqual(cells(out), [[3.0, 3.0], [1.0, 3.0]])

    def test_heat_index_below_80_is_the_simple_formula(self):
        # T 70 °F, RH 50 %: ½ (70 + 61 + 1.2 · 2 + 0.094 · 50) = 69.05, whose mean with T is below 80.
        self.assertAlmostEqual(cells(sa.HeatIndex(grid([[70.0]]), grid([[50.0]])))[0][0], 69.05, places=4)


class Surface(_Fixture):
    def test_aspect_is_nodata_on_the_rim_but_not_beside_a_single_hole(self):
        values = [[float(10 * k + r) for k in range(5)] for r in range(5)]
        values[2][2] = -9999.0
        out = cells(sa.Aspect(grid(values, nodata=-9999.0)))
        nodata = [[int(v == ND) for v in row] for row in out]
        # Re-measured in ArcGIS Pro 3.7 on this raster, 2026-11-12: the outer ring and the hole are NoData, and every
        # neighbour of the hole is computed (this test had said the four edge neighbours were NoData, which Pro does
        # not do; each keeps at least half of every side's 1-2-1 weight). Pro: 266.122 at (1,1), 275.711 at (1,2).
        self.assertEqual(nodata, [[1, 1, 1, 1, 1], [1, 0, 0, 0, 1], [1, 0, 1, 0, 1], [1, 0, 0, 0, 1], [1, 1, 1, 1, 1]])
        self.assertAlmostEqual(out[1][1], 266.122, places=2)
        self.assertAlmostEqual(out[1][2], 275.711, places=2)
        self.assertAlmostEqual(out[1][3], 284.697, places=2)

    def test_hillshade_is_truncated(self):
        # z = x on 10-unit cells: a 45° slope facing west; 255 · (cos 45° cos 45° + sin 45° sin 45° cos 45°) = 217.65.
        plane = grid([[0, 10, 20, 30]] * 4)
        self.assertEqual(cells(sa.Hillshade(plane))[1][1], 217)

    def test_filter_high_is_esris_kernel_over_the_valid_cells(self):
        nine = grid([[1.0, 2.0, 3.0], [4.0, -9999.0, 6.0], [7.0, 8.0, 9.0]], nodata=-9999.0)
        high = cells(sa.Filter(nine, "HIGH"))
        # The NoData centre gets a value: −(2 + 4 + 6 + 8) − 0.7 · (1 + 3 + 7 + 9) = −34.
        self.assertAlmostEqual(high[1][1], -34.0)
        # A corner: 6.8 · 1 − 2 − 4 (the centre is NoData) = 0.8.
        self.assertAlmostEqual(high[0][0], 0.8)

    def test_focal_statistics_fills_a_nodata_cell_under_data(self):
        nine = grid([[1.0, 2.0, 3.0], [4.0, -9999.0, 6.0], [7.0, 8.0, 9.0]], nodata=-9999.0)
        self.assertEqual(cells(sa.FocalStatistics(nine, sa.NbrRectangle(3, 3, "CELL"), "MEAN"))[1][1], 5.0)
        self.assertEqual(cells(sa.FocalStatistics(nine, sa.NbrRectangle(3, 3, "CELL"), "MEAN", "NODATA"))[1][1], ND)

    def test_block_statistics_majority_of_integers_is_integer(self):
        out = sa.BlockStatistics(grid([[1, 1], [2, 1]]), sa.NbrRectangle(2, 2, "CELL"), "MAJORITY")
        self.assertTrue(out.isInteger)


class Extraction(_Fixture):
    def test_extract_by_points_on_a_corner_takes_the_cell_above_right(self):
        r = grid([[1, 2], [3, 4]])
        # (10, 10) is the corner of all four cells: Pro takes the upper right.
        self.assertEqual(cells(sa.ExtractByPoints(r, [arcpy.Point(10, 10)])), [[ND, 2], [ND, ND]])

    def test_extract_by_mask_covers_the_masks_extent(self):
        r = grid([[float(4 * i + k) for k in range(4)] for i in range(4)])
        mask = self.features("m", "POLYGON", [(arcpy.Polygon(arcpy.Array([arcpy.Point(10, 10), arcpy.Point(10, 30),
                                                                          arcpy.Point(25, 30), arcpy.Point(25, 10)]),
                                                             3857),)])
        out = sa.ExtractByMask(r, mask)
        self.assertEqual((out.extent.XMin, out.extent.YMin, out.extent.XMax, out.extent.YMax), (10, 10, 30, 30))
        self.assertEqual(cells(out), [[5.0, 6.0], [9.0, 10.0]])

    def test_extract_values_to_points_writes_null_for_nodata(self):
        r = grid([[1.0, -9999.0]], nodata=-9999.0)
        pts = self.features("p", "POINT", [(point(5, 5),), (point(15, 5),)])
        out = os.path.join(self.directory, "o.gpkg")
        sa.ExtractValuesToPoints(pts, r, out, "NONE", "ALL")   # ALL on a raster with no table: RASTERVALU alone
        with arcpy.da.SearchCursor(out, ["RASTERVALU"]) as rows:
            self.assertEqual([v for (v,) in rows], [1.0, None])

    def test_interpolate_shape_runs_to_the_raster_edge(self):
        r = grid([[1.0, 2.0], [3.0, 4.0]])
        lines = self.features("l", "POLYLINE", [(line((0, 10), (20, 10)),)])
        out = os.path.join(self.directory, "z.gpkg")
        sa.InterpolateShape(r, lines, out, 10)
        with arcpy.da.SearchCursor(out, ["SHAPE@"]) as rows:
            zs = [[round(p.Z, 6) for part in shape for p in part] for (shape,) in rows]
        # Along the line between the rows (the mean of the two): flat from each outer centre to the edge, linear between
        # them. 20 units at a step of 10 is cut into floor(20 / 10) + 1 = 3 equal pieces, as Pro densifies.
        self.assertEqual(zs, [[2.0, 2.166667, 2.833333, 3.0]])


class Distance(_Fixture):
    def test_euclidean_extent_snaps_to_whole_cells(self):
        src = self.features("s", "POINT", [(point(5.5, 5.5),), (point(44.5, 25.5),)])
        out = sa.EucDistance(src, None, 10)
        e = out.extent
        self.assertEqual((e.XMin, e.YMin, e.XMax, e.YMax), (0, 0, 50, 30))

    def test_kernel_density_grid_is_centred_on_the_points_corner(self):
        pts = self.features("k", "POINT", [(point(3, 4),), (point(26, 18),)])
        e = sa.KernelDensity(pts, "NONE", 10, 30).extent
        # round(23 / 10) + 1 = 3 columns from 3 − 5; round(14 / 10) + 1 = 2 rows from 4 − 5.
        self.assertEqual((e.XMin, e.YMin, e.XMax, e.YMax), (-2, -1, 28, 19))

    def test_cost_path_as_polyline_writes_path_cost(self):
        cost = grid([[1.0, 1.0, 1.0]])
        src = self.features("c", "POINT", [(point(5, 5),)])
        dst = self.features("d", "POINT", [(7, point(25, 5))], [("v", "LONG")])
        backlink = os.path.join(self.directory, "bl.tif")
        distance = sa.CostDistance(src, cost, None, backlink)
        out = os.path.join(self.directory, "path.gpkg")
        sa.CostPathAsPolyline(dst, distance, backlink, out, "BEST_SINGLE", "v")
        fields = [f.name for f in arcpy.ListFields(out) if f.type not in ("OID", "Geometry")]
        self.assertEqual(fields[:2], ["PathCost", "DestID"])
        with arcpy.da.SearchCursor(out, ["PathCost", "DestID"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [(20.0, 7)])


class Contours(_Fixture):
    def test_contour_writes_id_highest_first(self):
        r = grid([[float(10 * k) for k in range(5)]] * 3)
        out = os.path.join(self.directory, "c.gpkg")
        sa.Contour(r, out, 10, 5)
        with arcpy.da.SearchCursor(out, ["Id", "Contour"]) as rows:
            self.assertEqual([tuple(r) for r in rows], [(1, 35.0), (2, 25.0), (3, 15.0), (4, 5.0)])


class Bands(_Fixture):
    def test_numpy_arrays_of_three_dimensions_are_multiband(self):
        cube = np.arange(12, dtype="float32").reshape(3, 2, 2)
        r = grid(cube)
        self.assertEqual(r.bandCount, 3)
        self.assertEqual(arcpy.RasterToNumPyArray(r).tolist(), cube.tolist())

    def test_msavi_and_gvitm_formulas(self):
        bands = sa.CompositeBand([grid([[float(k) / 10]]) for k in range(1, 8)])
        nir, red = 0.4, 0.3
        self.assertAlmostEqual(cells(sa.MSAVI(bands, 4, 3))[0][0],
                               0.5 * (2 * nir + 1 - math.sqrt((2 * nir + 1) ** 2 - 8 * (nir - red))), places=6)
        b = [k / 10 for k in range(1, 8)]
        expected = -0.2848 * b[0] - 0.2435 * b[1] - 0.5436 * b[2] + 0.7243 * b[3] + 0.084 * b[4] - 0.18 * b[6]
        self.assertAlmostEqual(cells(sa.GVITM(bands))[0][0], expected, places=6)

    def test_band_arithmetic_user_expression(self):
        bands = sa.CompositeBand([grid([[1.0]]), grid([[2.0]]), grid([[3.0]]), grid([[5.0]])])
        self.assertAlmostEqual(cells(sa.BandArithmetic(bands, "(B4 - B3) / (B4 + B3) * B1", 0))[0][0], 0.25)

    def test_colorspace_halves_round_up_exactly(self):
        rgb = sa.CompositeBand([grid(np.array([[v]], dtype="uint8")) for v in (220, 207, 204)])
        hue, saturation, value = (cells(b)[0][0] for b in sa.ColorspaceConversion(rgb).getRasterBands())
        # Hue 11.25° is 7.5 on Pro's 0…240 scale, rounded up to 8; S = 16/220 · 255 = 18.5 → 19.
        self.assertEqual((hue, saturation, value), (8, 19, 220))


class Environment(_Fixture):
    def tearDown(self):
        arcpy.env.extent = None
        arcpy.env.cellSize = None

    def test_apply_environment_lays_the_grid_from_the_extents_lower_left(self):
        r = grid([[float(10 * i + k) for k in range(6)] for i in range(6)])     # 0 … 60 on each side
        arcpy.env.extent = arcpy.Extent(10, 10, 40, 45)
        arcpy.env.cellSize = 20
        out = sa.ApplyEnvironment(r)
        e = out.extent
        self.assertEqual((e.XMin, e.YMin, e.XMax, e.YMax), (10, 10, 50, 50))
        # Centres (20, 40), (40, 40), (20, 20), (40, 20): each on a corner, taking the cell to its right and below.
        self.assertEqual(cells(out), [[22.0, 24.0], [42.0, 44.0]])


if __name__ == "__main__":
    unittest.main()
