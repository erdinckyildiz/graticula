"""The raster functions ADR-245 builds from pages that answered 404 on 2026-10-29 and were read again on 2026-10-31,
against the pages' formulas and worked examples, published tables, and answers worked out by hand."""
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import da, ddd, ia, sa  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

ND = -9999


def grid(values, cell=10.0, nodata=None, left=0.0):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    return arcpy.NumPyArrayToRaster(np.array(values), arcpy.Point(left, 0.0), cell, cell, nodata)


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


class _Base(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        for name in ("extent", "cellSize", "mask", "snapRaster", "outputCoordinateSystem"):
            setattr(arcpy.env, name, None)

    def tearDown(self):
        for name in ("extent", "cellSize", "mask", "snapRaster", "outputCoordinateSystem"):
            setattr(arcpy.env, name, None)


class Weather(_Base):
    def test_heat_index_is_the_pages_regression_and_matches_the_nws_table(self):
        # NWS heat index table: 90 °F at 50 % relative humidity reads 95 °F; the regression gives 94.597.
        out = ia.HeatIndex(grid([[90.0]]), grid([[50.0]]))
        self.assertAlmostEqual(cells(out)[0][0], 94.5969412, places=3)
        celsius = ia.HeatIndex(grid([[(90.0 - 32) * 5 / 9]]), grid([[50.0]]), "Celsius", "Celsius")
        self.assertAlmostEqual(cells(celsius)[0][0], (94.5969412 - 32) * 5 / 9, places=3)
        kelvin = sa.HeatIndex(grid([[90.0]]), grid([[50.0]]), "", "Kelvin")
        self.assertAlmostEqual(cells(kelvin)[0][0], (94.5969412 - 32) * 5 / 9 + 273.15, places=3)

    def test_wind_chill_is_the_pages_formula_and_matches_the_nws_chart(self):
        # NWS wind chill chart: 0 °F with a 15 mph wind reads −19 °F.
        out = ia.WindChill(grid([[0.0]]), grid([[15.0]]))
        self.assertAlmostEqual(cells(out)[0][0], -19.3979541, places=3)
        metric = ia.WindChill(grid([[-160.0 / 9]]), grid([[15 * 1.609344]]), "Celsius", "km/h", "Fahrenheit")
        self.assertAlmostEqual(cells(metric)[0][0], -19.3979541, places=3)
        knots = sa.WindChill(grid([[0.0]]), grid([[15 * 1609.344 / 1852]]), "", "knots", "")
        self.assertAlmostEqual(cells(knots)[0][0], -19.3979541, places=3)

    def test_unit_conversion_uses_the_units_definitions(self):
        self.assertAlmostEqual(cells(ia.UnitConversion(grid([[1.0]]), "MetersPerSecond", "KilometersPerHour"))[0][0],
                               3.6, places=5)
        self.assertAlmostEqual(cells(ia.UnitConversion(grid([[0.0]]), "Celsius", "Kelvin"))[0][0], 273.15, places=3)
        self.assertAlmostEqual(cells(sa.UnitConversion(grid([[1.0]]), "Miles", "Meters"))[0][0], 1609.344, places=2)
        self.assertAlmostEqual(cells(sa.UnitConversion(grid([[1.0]]), "Knots", "MetersPerSecond"))[0][0],
                               1852 / 3600, places=6)
        with self.assertRaises(ExecuteError):
            ia.UnitConversion(grid([[1.0]]), "Meters", "Kelvin")
        with self.assertRaises(ExecuteError):
            ia.UnitConversion(grid([[1.0]]))


class Bands(_Base):
    def test_gndvi_and_spectral_conversion_follow_their_pages(self):
        bands = ia.CompositeBand([grid([[0.1]]), grid([[0.2]]), grid([[0.3]]), grid([[0.5]])])
        self.assertAlmostEqual(cells(ia.GNDVI(bands, 4, 2))[0][0], 0.3 / 0.7, places=6)
        three = ia.CompositeBand([grid([[1.0]]), grid([[2.0]]), grid([[3.0]])])
        # The page's matrix: band 1 = 0.1 b1 + 0.9 b2, band 2 = 0.3 b1 + 0.7 b3, band 3 = 0.1 b1 + 0.1 b2 + 0.8 b3.
        out = ia.SpectralConversion(three, [0.1, 0.9, 0, 0.3, 0, 0.7, 0.1, 0.1, 0.8])
        got = [cells(b)[0][0] for b in out.getRasterBands()]
        np.testing.assert_allclose(got, [1.9, 2.4, 2.7], atol=1e-5)
        with self.assertRaises(ExecuteError):
            ia.SpectralConversion(three, [1, 2])

    def test_vector_field_converts_by_the_raster_functions_formulas(self):
        uv = ia.VectorField(grid([[0.0, 1.0]]), grid([[1.0, 0.0]]), "Vector-UV", "Geographic", "Vector-MagDir")
        mag, direction = uv.getRasterBands()
        np.testing.assert_allclose(cells(mag)[0], [1.0, 1.0], atol=1e-6)
        np.testing.assert_allclose(cells(direction)[0], [0.0, 90.0], atol=1e-5)  # north, then east
        md = sa.VectorField(grid([[2.0]]), grid([[90.0]]), "Vector-MagDir", "Arithmetic", "Vector-UV")
        u, v = md.getRasterBands()
        self.assertAlmostEqual(cells(u)[0][0], 0.0, delta=1e-5)
        self.assertAlmostEqual(cells(v)[0][0], 2.0, places=5)
        with self.assertRaises(NotBuiltError):
            ia.VectorField(grid([[1.0]]), grid([[1.0]]), "Unknown")

    def test_colorspace_conversion_answers_as_pro_does(self):
        def rgb(*pixels):
            return ia.CompositeBand([grid(np.array([[p[i] for p in pixels]], np.uint8)) for i in range(3)])
        colours = [(255, 0, 0), (255, 255, 0), (0, 255, 0), (0, 255, 255), (0, 0, 255), (255, 0, 255), (128, 128, 128)]
        hsv = ia.ColorspaceConversion(rgb(*colours))
        h, s, v = (cells(b)[0] for b in hsv.getRasterBands())
        # ArcGIS Pro 3.7, measured 2026-10-31: degrees × 240/360, red 240 (grey 0), magenta 200 — the page lists red 0
        # and magenta 201. On every whole-degree hue at four saturations and values Pro and grapy agree exactly.
        self.assertEqual(h, [240, 40, 80, 120, 160, 200, 0])
        self.assertEqual(s, [255] * 6 + [0])
        self.assertEqual(v, [255] * 6 + [128])
        back = sa.ColorspaceConversion(hsv, "hsv_to_rgb")
        got = np.array(list(zip(*(cells(b)[0] for b in back.getRasterBands()))))
        # The six pure hues come back exactly; Pro's round trip is within 3 levels over every hue, and grapy's
        # inverse within 1 level of Pro's.
        np.testing.assert_allclose(got, np.array(colours), atol=0)
        with self.assertRaises(ExecuteError):
            ia.ColorspaceConversion(ia.CompositeBand([grid([[0.5]]), grid([[0.5]]), grid([[0.5]])]))

    def test_colormap_to_rgb_reads_the_files_colour_table(self):
        import rasterio
        path = os.path.join(self.directory, "palette.tif")
        with rasterio.open(path, "w", driver="GTiff", width=3, height=1, count=1, dtype="uint8", crs="EPSG:3857",
                           transform=rasterio.transform.from_origin(0, 10, 10, 10)) as out:
            out.write(np.array([[1, 2, 1]], np.uint8), 1)
            out.write_colormap(1, {1: (255, 0, 0, 255), 2: (10, 20, 30, 255)})
        red, green, blue = ia.ColormapToRGB(path).getRasterBands()
        self.assertEqual(cells(red)[0], [255, 10, 255])
        self.assertEqual(cells(green)[0], [0, 20, 0])
        self.assertEqual(cells(blue)[0], [0, 30, 0])
        with self.assertRaises(ExecuteError):
            ia.ColormapToRGB(grid([[1, 2]]))


class Lists(_Base):
    def test_in_list_keeps_the_values_found_in_any_other_input(self):
        out = ia.InList(grid([[1, 2, 3]]), [grid([[3, 2, 9]]), grid([[1, 0, 0]])])
        self.assertEqual(cells(out), [[1, 2, ND]])
        self.assertTrue(out.isInteger)
        self.assertEqual(cells(sa.InList(grid([[7, 8]]), [8])), [[ND, 8]])

    def test_zonal_remap_applies_each_zones_thresholds(self):
        gdb = arcpy.management.CreateFileGDB(self.directory, "z")[0]
        table = arcpy.management.CreateTable(gdb, "thresholds")[0]
        for field, kind in (("ZoneID", "LONG"), ("ZoneMin", "DOUBLE"), ("ZoneMax", "DOUBLE"), ("ZoneValue", "DOUBLE")):
            arcpy.management.AddField(table, field, kind)
        with da.InsertCursor(table, ["ZoneID", "ZoneMin", "ZoneMax", "ZoneValue"]) as rows:
            rows.insertRow([1, 0, 4, 10])
            rows.insertRow([1, 4, 8, 20])
            rows.insertRow([2, 0, 100, None])
        values = grid([[1, 5, 9], [1, 5, 9], [1, 5, 9]])
        zones = grid([[1, 1, 1], [2, 2, 2], [3, 3, 3]])
        out = ia.ZonalRemap(values, table, zones)
        # Zone 1: 1 → 10, 5 → 20, 9 matches nothing and keeps the background, 0; zone 2's record has no output value
        # → 255; zone 3 has no record → 0 (Pro 3.7's rules, measured 2026-10-31).
        self.assertEqual(cells(out), [[10, 20, 0], [255, 255, 255], [0, 0, 0]])
        self.assertEqual(cells(sa.ZonalRemap(values, table, zones, where_clause="ZoneValue = 10")),
                         [[10, 0, 0], [0, 0, 0], [0, 0, 0]])

    def test_zonal_remap_is_pros_on_its_threshold_ends(self):
        # tools/pro-compare/adr245-colour-remap.py's table and values, and ArcGIS Pro 3.7's answer, 2026-10-31: both
        # ends exclusive, the later of two overlapping records wins, an unmatched pixel keeps the background (7), the
        # default (99) is only for a record without an output value, NoData stays NoData, and without a zone raster only
        # a record without a zone id applies. Rows 1–3 are Pro's answer as measured; row 4 joins a record Pro was run
        # on in a second table (an empty output value over 1–3) to this one.
        gdb = arcpy.management.CreateFileGDB(self.directory, "t")[0]
        table = arcpy.management.CreateTable(gdb, "thresholds")[0]
        for field, kind in (("ZoneID", "LONG"), ("ZoneMin", "DOUBLE"), ("ZoneMax", "DOUBLE"), ("ZoneValue", "DOUBLE")):
            arcpy.management.AddField(table, field, kind)
        with da.InsertCursor(table, ["ZoneID", "ZoneMin", "ZoneMax", "ZoneValue"]) as rows:
            for row in ([1, 2, 5, 10], [1, 5, 8, 20], [2, 2, 6, 30], [2, 4, 9, 40], [3, None, 3, 50], [3, 9, None, 60],
                        [None, 4, 6, 80], [4, 1, 3, None]):
                rows.insertRow(row)
        v = np.tile(np.arange(0, 12.5, 0.5), (4, 1))
        v[0, 3] = -9999
        values = grid(v, nodata=-9999)
        zones = grid(np.repeat(np.arange(1, 5)[:, None], 25, axis=1))
        got = cells(ia.ZonalRemap(values, table, zones, background_value=7, default_output_value=99))
        pro = [[7, 7, 7, ND, 7] + [10] * 5 + [7] + [20] * 5 + [7] * 9,
               [7] * 5 + [30] * 4 + [40] * 9 + [7] * 7,
               [50] * 6 + [7] * 13 + [60] * 6,
               [7] * 3 + [99] * 3 + [7] * 19]
        self.assertEqual(got, pro)
        alone = cells(ia.ZonalRemap(values, table, background_value=7))
        self.assertEqual(alone[1], [7] * 9 + [80] * 3 + [7] * 13)


class Filters(_Base):
    def test_speckle_leaves_a_constant_and_smooths_noise(self):
        flat = sa.Speckle(grid(np.full((5, 5), 7.0)))
        np.testing.assert_allclose(cells(flat), np.full((5, 5), 7.0), atol=1e-4)
        noisy = np.random.default_rng(1).gamma(4.0, 25.0, (20, 20))
        for kind in ("Lee", "EnhancedLee", "Frost", "Kuan", "Gamma MAP", "Refined Lee"):
            out = np.array(cells(ia.Speckle(grid(noisy), kind, "5x5", nlooks=4, damp_factor=1)))
            self.assertLess(out.std(), noisy.std(), kind)

    def test_elevation_void_fill_fills_each_void_from_its_border(self):
        z = np.tile(np.arange(5, dtype=float) * 10, (5, 1))
        z[2, 2] = ND
        r = grid(z, nodata=ND)
        # The void's eight neighbours lie symmetrically on the plane z = 10 × column: the fill is the plane's value.
        self.assertAlmostEqual(cells(ia.ElevationVoidFill(r, 0))[2][2], 20.0, places=4)
        self.assertEqual(cells(sa.ElevationVoidFill(r, 5))[2][2], ND)   # a 10-unit void is wider than 5
        self.assertEqual(cells(sa.ElevationVoidFill(r, -1))[2][2], ND)  # −1 fills nothing

    def test_resample_methods(self):
        r = grid([[1, 2], [3, 4]])
        self.assertEqual(cells(ia.Resample(r, "NearestNeighbor", None, 5)),
                         [[1, 1, 2, 2], [1, 1, 2, 2], [3, 3, 4, 4], [3, 3, 4, 4]])
        big = grid(np.arange(16, dtype=float).reshape(4, 4))
        np.testing.assert_allclose(cells(sa.Resample(big, "Average", None, 20)), [[2.5, 4.5], [10.5, 12.5]])
        self.assertEqual(cells(sa.Resample(big, "Maximum", None, 20)), [[5, 7], [13, 15]])
        ramp = grid(np.tile(np.arange(8, dtype=float) * 10, (8, 1)))
        bilinear = cells(ia.Resample(ramp, "Bilinear", None, 20))
        np.testing.assert_allclose(bilinear[1], [5, 25, 45, 65], atol=1e-9)
        cubic = cells(ia.Resample(ramp, "Cubic", None, 20))
        np.testing.assert_allclose(cubic[1][1:3], [25, 45], atol=1e-9)  # cubic convolution keeps a line
        self.assertEqual(cells(ia.Resample(grid([[1, 1, 2, 2]]), "Majority", None, 20)), [[1, 2]])
        with self.assertRaises(NotBuiltError):
            ia.Resample(r, "BilinearGaussBlur", None, 5)
        with self.assertRaises(NotBuiltError):
            ia.Resample(r, "NearestNeighbor", 3, 5)


class Unmixing(_Base):
    def test_linear_unmixing_recovers_the_fractions_of_two_class_spectra(self):
        a, b = np.array([10.0, 0.0, 5.0]), np.array([0.0, 10.0, 5.0])
        pixels = [a, b, 0.5 * a + 0.5 * b, 0.25 * a + 0.75 * b]
        raster = ia.CompositeBand([grid([[p[i] for p in pixels]]) for i in range(3)])
        gdb = arcpy.management.CreateFileGDB(self.directory, "t")[0]
        fc = arcpy.management.CreateFeatureclass(gdb, "training", "POLYGON", spatial_reference=3857)[0]
        arcpy.management.AddField(fc, "classname", "TEXT")
        arcpy.management.AddField(fc, "classvalue", "LONG")
        with da.InsertCursor(fc, ["classname", "classvalue", "SHAPE@"]) as rows:
            for name, value, x in (("a", 1, 0), ("b", 2, 10)):
                ring = arcpy.Array([arcpy.Point(x + 1, 1), arcpy.Point(x + 9, 1), arcpy.Point(x + 9, 9),
                                    arcpy.Point(x + 1, 9)])
                rows.insertRow([name, value, arcpy.Polygon(ring, arcpy.SpatialReference(3857))])
        for option in (None, "SUM_TO_ONE", "NON_NEGATIVE", "SUM_TO_ONE;NON_NEGATIVE"):
            out = ia.LinearUnmixing(raster, fc, option)
            fa, fb = (cells(band)[0] for band in out.getRasterBands())
            np.testing.assert_allclose(fa, [1, 0, 0.5, 0.25], atol=1e-3, err_msg=str(option))
            np.testing.assert_allclose(fb, [0, 1, 0.5, 0.75], atol=1e-3, err_msg=str(option))
        with self.assertRaises(NotBuiltError):
            ia.LinearUnmixing(raster, "training.ecd")


class Environment(_Base):
    def test_apply_environment_copies_and_applies_extent_cell_size_and_constants(self):
        r = grid(np.arange(16).reshape(4, 4))
        self.assertEqual(cells(ia.ApplyEnvironment(r)), cells(r))
        arcpy.env.extent = arcpy.Extent(10, 10, 30, 30)
        self.assertEqual(cells(sa.ApplyEnvironment(r)), [[5, 6], [9, 10]])
        arcpy.env.extent = None
        arcpy.env.cellSize = 20
        # Each new centre falls on an edge between input cells and takes the cell right of and below it.
        self.assertEqual(cells(sa.ApplyEnvironment(r)), [[5, 7], [13, 15]])
        arcpy.env.extent = arcpy.Extent(0, 0, 30, 20)
        arcpy.env.cellSize = 10
        self.assertEqual(cells(ia.ApplyEnvironment(5)), [[5, 5, 5], [5, 5, 5]])
        arcpy.env.extent = arcpy.env.cellSize = None
        arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(4326)
        with self.assertRaises(NotBuiltError):
            ia.ApplyEnvironment(r)


class Blocks(_Base):
    def test_pixel_block_collection_walks_the_raster_in_blocks(self):
        r = grid(np.arange(25, dtype=float).reshape(5, 5))
        blocks = ia.PixelBlockCollection(r, (2, 2))
        self.assertEqual(blocks.size, (3, 3))
        first = blocks[0][0]
        self.assertEqual(first.getData().shape, (2, 2, 1))
        self.assertEqual(first.getData()[:, :, 0].tolist(), [[0, 1], [5, 6]])
        self.assertEqual(first.cell_size, (10.0, 10.0))
        self.assertEqual((first.tlc.X, first.tlc.Y), (0.0, 50.0))
        self.assertEqual(blocks[2, 2].getData().shape, (1, 1, 1))
        self.assertEqual(sum(1 for _ in blocks), 9)
        self.assertEqual(len(list(blocks.reset())), 9)
        pair = ia.PixelBlockCollection([r, r], (5, 5), nodata_to_values=-1)
        self.assertEqual(len(pair[0][0]), 2)
        overlapping = ia.PixelBlockCollection(r, (2, 2), stride=(1, 1))
        self.assertEqual(overlapping.size, (5, 5))
        holes = ia.PixelBlockCollection(grid([[1, ND]], nodata=ND), (1, 2), nodata_to_values=-1)
        self.assertEqual(holes[0][0].getData()[:, :, 0].tolist(), [[1, -1]])
        block = ia.PixelBlock(np.zeros((4, 2, 1)), arcpy.Extent(0, 0, 20, 40))
        self.assertEqual(block.cell_size, (10.0, 10.0))

    def test_raster_cell_iterator_runs_the_pages_examples(self):
        source = grid(np.arange(9, dtype=float).reshape(3, 3))
        out = arcpy.Raster(source.getRasterInfo())
        with sa.RasterCellIterator({"rasters": [source, out], "padding": 1}) as rci:
            for r, c in rci:
                v = source[r - 1, c - 1]
                out[r, c] = math.nan if math.isnan(v) else v
        self.assertEqual(cells(out), [[ND, ND, ND], [ND, 0, 1], [ND, 3, 4]])
        rows = arcpy.Raster(source.getRasterInfo())
        with sa.RasterCellIterator({"rasters": [rows]}) as rci:
            for r, c in rci:
                rows[r, c] = r
        self.assertEqual(cells(rows), [[0, 0, 0], [1, 1, 1], [2, 2, 2]])
        stream = grid([[1, ND, 1]], nodata=ND)
        seen = list(sa.RasterCellIterator({"rasters": [stream], "skipNoData": [stream]}))
        self.assertEqual(seen, [(0, 0), (0, 2)])


class Sensors(_Base):
    def rpc_image(self):
        import rasterio
        from rasterio.rpc import RPC
        zero = [0.0] * 20
        one = [1.0] + [0.0] * 19
        sample_num = list(zero)
        sample_num[1] = 1.0
        sample_num[3] = 0.1  # an oblique view: a point 100 m up is seen 5 pixels east
        line_num = list(zero)
        line_num[2] = -1.0
        rpc = RPC(height_off=0, height_scale=100, lat_off=40, lat_scale=0.01, line_den_coeff=one,
                  line_num_coeff=line_num, line_off=50, line_scale=50, long_off=30, long_scale=0.01,
                  samp_den_coeff=one, samp_num_coeff=sample_num, samp_off=50, samp_scale=50)
        path = os.path.join(self.directory, "rpc.tif")
        with rasterio.open(path, "w", driver="GTiff", width=101, height=101, count=1, dtype="uint8", rpcs=rpc) as out:
            out.write(np.zeros((101, 101), np.uint8), 1)
        return path

    def test_mensuration_measures_through_the_rpc_model(self):
        path = self.rpc_image()
        m = ia.Mensuration(path)
        centre = m.point((50.5, 50.5))
        self.assertAlmostEqual(centre.lon, 30.0, places=7)
        self.assertAlmostEqual(centre.lat, 40.0, places=7)
        self.assertEqual(centre.sensor_name, "RPC")
        east = m.distance((50.5, 50.5), (100.5, 50.5))  # 0.01° of longitude at 40° N
        self.assertAlmostEqual(east.distance, 111320 * math.cos(math.radians(40)) * 0.01, delta=3)
        self.assertAlmostEqual(east.azimuth, 90.0, delta=0.01)
        square = m.area([(50.5, 50.5), (100.5, 50.5), (100.5, 0.5), (50.5, 0.5)])
        self.assertAlmostEqual(square.area, east.distance * 1111.0, delta=square.area * 0.01)
        self.assertAlmostEqual(m.height((50.5, 50.5), (55.5, 50.5), ia.Mensuration.base_to_top).height, 100.0,
                               places=2)
        with self.assertRaises(NotBuiltError):
            m.height((50.5, 50.5), (55.5, 50.5), ia.Mensuration.base_to_shadow)
        self.assertIs(arcpy.Mensuration, ia.Mensuration)
        with self.assertRaises(ExecuteError):
            ia.Mensuration(grid([[1]]))

    def test_get_utm_from_location_answers_the_pages_example(self):
        sr = arcpy.GetUTMFromLocation(-100.9, 40)
        self.assertEqual(sr.name, "WGS_1984_UTM_Zone_14N")
        self.assertEqual(sr.factoryCode, 32614)
        south = arcpy.GetUTMFromLocation(151.2, -33.9)
        self.assertEqual((south.name, south.factoryCode), ("WGS_1984_UTM_Zone_56S", 32756))


class LegacySurface(_Base):
    def setUp(self):
        super().setUp()
        self.gpkg = arcpy.management.CreateFileGDB(self.directory, "data")[0]
        self.ramp = grid(np.tile(np.arange(5, dtype=float) * 10 + 5, (5, 1)))  # z = x at every cell centre

    def features(self, name, kind, shapes, has_z=False):
        path = arcpy.management.CreateFeatureclass(self.gpkg, name, kind, has_z="ENABLED" if has_z else "DISABLED",
                                                   spatial_reference=3857)[0]
        with da.InsertCursor(path, ["SHAPE@"]) as rows:
            for shape in shapes:
                rows.insertRow([shape])
        return path

    def test_surface_spot_writes_the_surface_value_into_spot(self):
        points = self.features("p", "POINT", [arcpy.PointGeometry(arcpy.Point(15, 15)), arcpy.PointGeometry(arcpy.Point(99, 99))])
        ddd.SurfaceSpot(self.ramp, points, z_factor=2)
        with da.SearchCursor(points, ["Spot"]) as rows:
            self.assertEqual([r[0] for r in rows], [30.0, None])

    def test_surface_length_on_a_raster_and_a_tin(self):
        line = arcpy.Polyline(arcpy.Array([arcpy.Point(5, 25), arcpy.Point(35, 25)]))
        lines = self.features("l", "POLYLINE", [line])
        ddd.SurfaceLength(self.ramp, lines)
        ddd.SurfaceLength(self.ramp, lines, "Doubled", z_factor=2)
        with da.SearchCursor(lines, ["SURFACE_LENGTH", "Doubled"]) as rows:
            length, doubled = next(iter(rows))
        self.assertAlmostEqual(length, math.hypot(30, 30), places=6)
        self.assertAlmostEqual(doubled, math.hypot(30, 60), places=6)
        import shapely
        mass = self.features("m", "POINT", [shapely.Point(x, y, x + y) for x, y in
                                            ((0, 0), (10, 0), (10, 10), (0, 10), (5, 5))], has_z=True)
        tin = os.path.join(self.directory, "plane")
        ddd.CreateTin(tin, 3857, [[mass, "Shape.Z", "masspoints", ""]])
        square = arcpy.Polygon(arcpy.Array([arcpy.Point(2, 2), arcpy.Point(8, 2), arcpy.Point(8, 8), arcpy.Point(2, 8)]))
        polygons = self.features("g", "POLYGON", [square])
        ddd.SurfaceLength(tin, polygons, "Perimeter3D")
        with da.SearchCursor(polygons, ["Perimeter3D"]) as rows:
            self.assertAlmostEqual(next(iter(rows))[0], 4 * math.hypot(6, 6), places=6)
        with self.assertRaises(ExecuteError):
            ddd.SurfaceLength(self.ramp, self.features("q", "POINT", [arcpy.PointGeometry(arcpy.Point(1, 1))]))


class Refusals(_Base):
    def test_the_names_kept_refused_say_why_by_name(self):
        for module, name, words in ((ia, "MNDWI", "no reference page"), (sa, "FloatDivide", "no reference page"),
                                    (ia, "ContrastBrightness", "no formula"), (ia, "LabelBadBands", "IsBadBand"),
                                    (ia, "VectorFieldRenderer", "rendering"), (sa, "AspectSlope", "coded")):
            with self.assertRaises(NotBuiltError) as said:
                getattr(module, name)()
            self.assertIn(words, str(said.exception))
            self.assertNotIn("404 on 2026-10-29", str(said.exception))


if __name__ == "__main__":
    unittest.main()
