"""grapy's multidimensional raster and the tools on it (ADR-236), against answers worked out by hand.

Most cases are one or two pixels over a short series, so every expected number can be checked on paper: a mean of
four values, the slope of a line drawn through exact points, Mann-Kendall's S counted pair by pair. The change
detectors (CCDC, LandTrendr) are checked on series built with a known break.
"""
import datetime
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import ia, sa  # noqa: E402
from grapy import _mdim  # noqa: E402
from grapy._raster import _Grid  # noqa: E402
from grapy._runtime import NotBuiltError  # noqa: E402

ND = -1


def cube_raster(series, times=None, dims=("StdTime",), name="v", rows=1, columns=None, mask=None, attrs=None):
    """A multidimensional Raster of one variable: ``series`` is (slices, rows·columns) or (slices,) for one pixel."""
    data = np.asarray(series, float)
    if data.ndim == 1:
        data = data[:, None]
    columns = columns or data.shape[1] // rows
    data = data.reshape(data.shape[0], rows, columns)
    if times is None:
        times = [_mdim.to_time(f"2020-01-{d + 1:02d}") for d in range(data.shape[0])]
    coords = [{dims[0]: t} for t in times]
    m = np.zeros(data.shape, bool) if mask is None else np.asarray(mask, bool).reshape(data.shape)
    grid = _Grid(0, 10 * rows, 10, 10, rows, columns, 3857)
    return _mdim.raster_of(_mdim.Cube(grid, [_mdim.Variable(name, list(dims), coords, data, m, attrs or {})]))


def monthly(years):
    return [_mdim.to_time(f"{y}-{m:02d}-15") for y in years for m in range(1, 13)]


def cells(raster, nodata=ND):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=nodata).tolist()


def slices(raster, nodata=ND):
    """(slices, rows, columns) of a multidimensional raster's first variable and first band."""
    return raster.read(nodata_to_value=nodata)[..., 0].tolist()


class Model(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def test_the_raster_says_what_it_holds(self):
        r = cube_raster([[1, 2], [3, 4], [5, 6]], columns=2, attrs={"units": "K", "long_name": "air temperature"})
        self.assertTrue(r.isMultidimensional)
        self.assertEqual(r.variables, ["v(StdTime=3)"])
        self.assertEqual(r.variableNames, ["v"])
        self.assertEqual(r.slices[1], {"variable": "v", "StdTime": "2020-01-02T00:00:00"})
        self.assertEqual(r.getDimensionNames("v"), ["StdTime"])
        self.assertEqual(r.getDimensionValues("v", "StdTime")[2], "2020-01-03T00:00:00")
        self.assertEqual(r.getVariableAttributes("v")["Unit"], "K")
        self.assertEqual(r.getDimensionAttributes("v", "StdTime")["Interval"], 1.0)
        self.assertEqual(r.read().shape, (3, 1, 2, 1))
        self.assertEqual(r.renameVariable("v", "t"), ["t"])
        self.assertEqual(r.getStatistics("t")["max"], 6.0)
        self.assertFalse(arcpy.Raster(arcpy.NumPyArrayToRaster(np.array([[1.0]]))).isMultidimensional)
        # A multidimensional raster is not a two-dimensional array; map algebra on it is refused, not read as slice 1.
        with self.assertRaises(NotBuiltError):
            r + 1

    def test_netcdf_written_is_read_by_other_readers_and_back(self):
        r = cube_raster([[1, 2], [3, 4]], columns=2, mask=[[False, True], [False, False]])
        path = r.save("cube")
        self.assertTrue(path.endswith("cube.nc"))
        import xarray as xr
        ds = xr.open_dataset(path, engine="scipy")
        self.assertEqual(str(ds["StdTime"].values[1])[:10], "2020-01-02")
        self.assertTrue(np.isnan(float(ds["v"].values[0, 0, 1])))
        ds.close()
        import rasterio
        with rasterio.open(path) as source:  # GDAL's netCDF driver: the same reference, grid and cells
            self.assertEqual(source.crs.to_epsg(), 3857)
            self.assertEqual((source.transform.c, source.transform.f, source.transform.a), (0.0, 10.0, 10.0))
            self.assertEqual(source.read(2).tolist(), [[3.0, 4.0]])
        again = arcpy.Raster(path, True)
        self.assertEqual(slices(again), [[[1.0, ND]], [[3.0, 4.0]]])
        # Without is_multidimensional: the first slice of the first variable, as a plain raster.
        self.assertEqual(cells(arcpy.Raster(path)), [[1.0, ND]])
        self.assertTrue(arcpy.Raster(path).isMultidimensional)

    def test_a_netcdf4_file_written_by_gdal_is_read(self):
        import rasterio
        import rasterio.shutil
        from rasterio.io import MemoryFile
        from rasterio.transform import from_origin
        path = os.path.join(self.directory, "gdal.nc")
        with MemoryFile() as memory:
            with memory.open(driver="GTiff", height=1, width=2, count=3, dtype="float32", nodata=-9999,
                             transform=from_origin(0, 10, 10, 10), crs="EPSG:3857") as d:
                d.write(np.arange(6, dtype="float32").reshape(3, 1, 2))
                d.update_tags(NETCDF_DIM_EXTRA="{time}", NETCDF_DIM_time_DEF="{3,6}", NETCDF_DIM_time_VALUES="{0,31,60}",
                              **{"time#units": "days since 2000-01-01", "time#standard_name": "time"})
                rasterio.shutil.copy(d, path, driver="netCDF", FORMAT="NC4")
        r = arcpy.Raster(path, True)
        self.assertEqual(r.variables, ["Band1(StdTime=3)"])
        self.assertEqual(r.getDimensionValues("Band1", "StdTime"), ["2000-01-01T00:00:00", "2000-02-01T00:00:00",
                                                                     "2000-03-01T00:00:00"])
        self.assertEqual(slices(r), [[[0.0, 1.0]], [[2.0, 3.0]], [[4.0, 5.0]]])

    def test_crf_is_refused_with_its_reason(self):
        r = cube_raster([1, 2])
        with self.assertRaisesRegex(NotBuiltError, "Cloud Raster Format"):
            r.save("out.crf")
        with self.assertRaisesRegex(NotBuiltError, "not published"):
            arcpy.Raster("in.crf", True)


class Aggregation(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        # Two years of monthly values 0 … 23 at one pixel.
        self.r = cube_raster(list(range(24)), monthly([2020, 2021]))

    def test_intervals(self):
        a = ia.AggregateMultidimensionalRaster(self.r, "StdTime", "MEAN", aggregation_def="INTERVAL_KEYWORD",
                                               interval_keyword="YEARLY")
        self.assertEqual(slices(a), [[[5.5]], [[17.5]]])
        self.assertEqual([s["StdTime"] for s in a.slices], ["2020-01-01T00:00:00", "2021-01-01T00:00:00"])
        # Each January is 0 and 12: their recurring mean is 6, dated at the first January.
        a = ia.AggregateMultidimensionalRaster(self.r, "StdTime", "MEAN", aggregation_def="INTERVAL_KEYWORD",
                                               interval_keyword="RECURRING_MONTHLY")
        self.assertEqual(len(a.slices), 12)
        self.assertEqual(slices(a)[0], [[6.0]])
        self.assertEqual(a.slices[0]["StdTime"], "2020-01-01T00:00:00")
        a = ia.AggregateMultidimensionalRaster(self.r, "StdTime", "SUM", aggregation_def="INTERVAL_VALUE",
                                               interval_value=6, interval_unit="MONTHS")
        self.assertEqual(slices(a), [[[15.0]], [[51.0]], [[87.0]], [[123.0]]])  # 0+…+5, 6+…+11 …
        a = ia.AggregateMultidimensionalRaster(self.r, "StdTime", "MAXIMUM", aggregation_def="INTERVAL_RANGES",
                                               interval_ranges=[["2020-03-01", "2020-06-30"]])
        self.assertEqual(slices(a), [[[5.0]]])
        a = sa.AggregateMultidimensionalRaster(self.r, "StdTime", "PERCENTILE", percentile_value=50,
                                               percentile_interpolation_type="LINEAR")
        self.assertEqual(slices(a), [[[11.5]]])
        a = ia.AggregateMultidimensionalRaster(self.r, "StdTime", "RANGE", dimensionless="NO_DIMENSIONS")
        self.assertEqual(a.variables, ["v()"])
        self.assertEqual(slices(a), [[[23.0]]])

    def test_nodata_and_refusals(self):
        r = cube_raster([1, 5, 9], mask=[False, True, False])
        self.assertEqual(slices(ia.AggregateMultidimensionalRaster(r, "StdTime")), [[[5.0]]])
        self.assertEqual(slices(ia.AggregateMultidimensionalRaster(r, "StdTime", ignore_nodata="NODATA")), [[[ND]]])
        with self.assertRaisesRegex(NotBuiltError, "raster function"):
            ia.AggregateMultidimensionalRaster(r, "StdTime", "CUSTOM")


class Anomalies(unittest.TestCase):
    def setUp(self):
        self.r = cube_raster([1, 2, 3, 6])  # mean 3, median 2.5, population SD √3.5

    def test_anomaly_function(self):
        self.assertEqual(slices(ia.Anomaly(self.r)), [[[-2.0]], [[-1.0]], [[0.0]], [[3.0]]])
        got = slices(sa.Anomaly(self.r, "StdTime", "PERCENT_OF_MEAN"))
        self.assertAlmostEqual(got[0][0][0], 1 / 3)
        got = slices(ia.Anomaly(self.r, "StdTime", "PERCENT_DIFFERENCE_FROM_MEAN"))
        self.assertAlmostEqual(got[0][0][0], 2 / 2)  # |1 − 3| / ((1 + 3)/2)
        # By month: the two January slices have their own mean.
        r = cube_raster([1, 3, 10], [_mdim.to_time("2020-01-01"), _mdim.to_time("2020-01-20"),
                                     _mdim.to_time("2020-02-01")])
        self.assertEqual(slices(ia.Anomaly(r, temporal_interval="MONTHLY")), [[[-1.0]], [[1.0]], [[0.0]]])

    def test_generate_multidimensional_anomaly(self):
        got = slices(ia.GenerateMultidimensionalAnomaly(self.r, None, "Z_SCORE"))
        self.assertAlmostEqual(got[0][0][0], -2 / math.sqrt(3.5))
        got = slices(sa.GenerateMultidimensionalAnomaly(self.r, "v", "DIFFERENCE_FROM_MEDIAN"))
        self.assertEqual([g[0][0] for g in got], [-1.5, -0.5, 0.5, 3.5])
        # An external mean of 2 for every slice.
        reference = arcpy.NumPyArrayToRaster(np.array([[2.0]]), arcpy.Point(0, 0), 10, 10)
        reference._grid = _Grid(0, 10, 10, 10, 1, 1, 3857)
        got = slices(ia.GenerateMultidimensionalAnomaly(self.r, None, "DIFFERENCE_FROM_MEAN", "EXTERNAL_RASTER", "DATA",
                                                        reference))
        self.assertEqual([g[0][0] for g in got], [-1.0, 0.0, 1.0, 4.0])
        with self.assertRaises(ValueError):
            ia.GenerateMultidimensionalAnomaly(self.r, None, "Z_SCORE", "EXTERNAL_RASTER", "DATA", reference)


class Arguments(unittest.TestCase):
    def test_arg_statistics_on_bands_and_on_time(self):
        bands = ia.CompositeBand([arcpy.NumPyArrayToRaster(np.array([[v]])) for v in (3, 7, 7)])
        self.assertEqual(cells(ia.ArgStatistics(bands, "MAX")), [[100.0]])  # reached twice
        self.assertEqual(cells(ia.ArgStatistics(bands, "MIN")), [[1.0]])
        times = [_mdim.to_time(t) for t in ("2019-01-01", "2020-07-02T00:00:00", "2021-01-01")]
        r = cube_raster([4, 9, 1], times)
        self.assertAlmostEqual(cells(ia.ArgStatistics(r, "MAX"))[0][0], 2020 + 183 / 366)
        r = cube_raster([1, 5, 5, 1, 5, 5, 5])
        self.assertEqual(cells(sa.ArgStatistics(r, "DURATION", 4, 6)), [[3.0]])

    def test_find_argument_statistics(self):
        times = [_mdim.to_time(f"{y}-01-01") for y in (2001, 2002, 2003, 2004)]
        r = cube_raster([2, 8, 3, 8], times)
        self.assertEqual(cells(ia.FindArgumentStatistics(r, "StdTime", statistics_type="ARGUMENT_MAX")), [[2002.0]])
        self.assertEqual(cells(ia.FindArgumentStatistics(r, "StdTime", statistics_type="ARGUMENT_MAX",
                                                         occurrence="LAST_OCCURRENCE")), [[2004.0]])
        self.assertEqual(cells(ia.FindArgumentStatistics(r, "StdTime", statistics_type="ARGUMENT_MAX",
                                                         multiple_occurrence=99)), [[99.0]])
        self.assertEqual(cells(ia.FindArgumentStatistics(r, "StdTime", statistics_type="ARGUMENT_VALUE", value=2,
                                                         comparison="GREATER_THAN")), [[2002.0]])
        with self.assertRaisesRegex(NotBuiltError, "interval keyword"):
            ia.FindArgumentStatistics(r, "StdTime", "INTERVAL_KEYWORD", "RECURRING_MONTHLY")


class Moving(unittest.TestCase):
    def test_windows_and_ends(self):
        r = cube_raster([1, 2, 3, 10])
        got = slices(ia.DimensionalMovingStatistics(r, "StdTime", 1, 1))
        self.assertEqual([g[0][0] for g in got], [1.5, 2.0, 5.0, 6.5])
        got = slices(sa.DimensionalMovingStatistics(r, "StdTime", 1, 1, statistics_type="MAXIMUM"))
        self.assertEqual([g[0][0] for g in got], [2.0, 3.0, 10.0, 10.0])
        # The circular mean of 350 and 10 is 0, not 180.
        r = cube_raster([350.0, 10.0])
        got = slices(ia.DimensionalMovingStatistics(r, "StdTime", 1, 1, statistics_type="CIRCULAR_MEAN"))
        self.assertAlmostEqual(math.cos(math.radians(got[0][0][0])), 1.0)
        r = cube_raster([1.0, 2.0, 4.0], mask=[False, True, False])
        got = slices(ia.DimensionalMovingStatistics(r, "StdTime", 1, 1, "FILL_NODATA"))
        self.assertEqual([g[0][0] for g in got], [1.0, 2.5, 4.0])
        with self.assertRaises(ValueError):
            ia.DimensionalMovingStatistics(cube_raster([1.5, 2.5]), statistics_type="MAJORITY")


class Trends(unittest.TestCase):
    def test_linear_polynomial_and_harmonic_fits(self):
        days = list(range(6))
        times = [_mdim.to_time("2020-01-01") + np.timedelta64(d, "D") for d in days]
        r = cube_raster([2 + 0.5 * d for d in days], times)
        t = ia.GenerateTrend(r, "StdTime", r2=True, slope_p_value=True)
        self.assertEqual(t.bandNames, ["Slope", "Intercept", "RMSE", "R2", "Slope_P_Value"])
        slope, intercept, rmse, r2, p = t.read()[0, 0, 0]
        self.assertAlmostEqual(slope, 0.5)
        self.assertAlmostEqual(intercept, 2.0)
        self.assertAlmostEqual(rmse, 0.0, delta=1e-9)
        self.assertAlmostEqual(r2, 1.0)
        self.assertAlmostEqual(p, 0.0, delta=1e-9)
        predicted = ia.PredictUsingTrend(t, "BY_VALUE", "2020-01-11")
        self.assertAlmostEqual(slices(predicted)[0][0][0], 7.0)
        predicted = ia.PredictUsingTrendRaster(t, None, "BY_INTERVAL", None, "2020-01-07", "2020-01-09", 1, "DAYS")
        self.assertEqual(len(predicted.slices), 3)
        self.assertAlmostEqual(slices(predicted)[2][0][0], 6.0)
        r = cube_raster([1 - d + 2 * d * d for d in days], times)
        quad = ia.GenerateTrendRaster(r, "StdTime", line_type="POLYNOMIAL", frequency=2)
        self.assertEqual(quad.bandNames, ["Polynomial_2", "Polynomial_1", "Polynomial_0", "RMSE"])
        np.testing.assert_allclose(quad.read()[0, 0, 0, :3], [2, -1, 1], atol=1e-8)
        # y = 3 + 0.01t + 2·sin(2πt/365.25) − cos(2πt/365.25), sampled every 30 days for three years.
        days = np.arange(0, 1095, 30)
        w = 2 * np.pi / 365.25
        times = [_mdim.to_time("2010-01-01") + np.timedelta64(int(d), "D") for d in days]
        r = cube_raster(3 + 0.01 * days + 2 * np.sin(w * days) - np.cos(w * days), times)
        h = ia.GenerateTrendRaster(r, "StdTime", line_type="HARMONIC", cycle_length=1, cycle_unit="YEARS")
        self.assertEqual(h.bandNames[:4], ["Slope", "Intercept", "Harmonic_sin1", "Harmonic_cos1"])
        np.testing.assert_allclose(h.read()[0, 0, 0, :4], [0.01, 3, 2, -1], atol=1e-8)

    def test_mann_kendall_by_hand(self):
        # 1, 2, 3, 4, 5 on days 0–4: S = 10 pairs all rising; var(S) = 5·4·15/18; Z = (S − 1)/√var; Sen's slope 1.
        times = [_mdim.to_time("2020-01-01") + np.timedelta64(d, "D") for d in range(5)]
        r = cube_raster([1, 2, 3, 4, 5], times)
        sen, p, s, var, z = ia.GenerateTrend(r, "StdTime", "MANN-KENDALL").read()[0, 0, 0]
        self.assertEqual((sen, s), (1.0, 10.0))
        self.assertAlmostEqual(var, 5 * 4 * 15 / 18)
        self.assertAlmostEqual(z, 9 / math.sqrt(5 * 4 * 15 / 18))
        self.assertAlmostEqual(p, 2 * (1 - 0.5 * (1 + math.erf(z / math.sqrt(2)))))
        # A tie: 1, 2, 2, 3 — S = 5, var = (4·3·13 − 2·1·9)/18.
        r = cube_raster([1, 2, 2, 3], times[:4])
        _, _, s, var, _ = ia.GenerateTrend(r, "StdTime", "MANN-KENDALL").read()[0, 0, 0]
        self.assertEqual(s, 5.0)
        self.assertAlmostEqual(var, (4 * 3 * 13 - 2 * 1 * 9) / 18)
        # Seasonal: January and July of three years, each season rising — S = 3 + 3, var = 2·(3·2·11/18).
        times = [_mdim.to_time(f"{y}-{m:02d}-01") for y in (2001, 2002, 2003) for m in (1, 7)]
        r = cube_raster([1, 10, 2, 11, 3, 12], times)
        _, _, s, var, _ = ia.GenerateTrend(r, "StdTime", "SEASONAL-KENDALL", seasonal_period="MONTHS").read()[0, 0, 0]
        self.assertEqual(s, 6.0)
        self.assertAlmostEqual(var, 2 * 3 * 2 * 11 / 18)
        with self.assertRaisesRegex(ValueError, "not a model"):
            ia.PredictUsingTrend(ia.GenerateTrend(r, "StdTime", "MANN-KENDALL"), "BY_VALUE", "2004-01-01")

    def test_trend_to_rgb(self):
        times = [_mdim.to_time("2020-01-01") + np.timedelta64(d, "D") for d in range(3)]
        r = cube_raster([[0, 10], [1, 8], [2, 6]], times, columns=2)  # slopes +1 and −2, intercepts 0 and 10
        rgb = ia.TrendToRGB(ia.GenerateTrend(r, "StdTime"))
        self.assertEqual([cells(b) for b in rgb.getRasterBands()], [[[255, 0]], [[0, 255]], [[0, 255]]])


class Components(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def test_principal_components_and_their_tables(self):
        # Two slices, the second twice the first: one component holds all the variance.
        r = cube_raster([[1, 2, 3, 4], [2, 4, 6, 8]], columns=4)
        result = ia.MultidimensionalPrincipalComponents(r, "DIMENSION_REDUCTION", "StdTime", "pc.tif", "load.csv",
                                                        "eig.csv", None, "90%")
        pc = arcpy.Raster(result[0])
        self.assertEqual(pc.bandCount, 1)
        # Scores: (x − mean)·v with v = (1, 2)/√5, the means 2.5 and 5.
        np.testing.assert_allclose(cells(pc)[0], [(-1.5 - 2 * 3) / math.sqrt(5), (-0.5 - 2 * 1) / math.sqrt(5),
                                                  (0.5 + 2 * 1) / math.sqrt(5), (1.5 + 2 * 3) / math.sqrt(5)], atol=1e-9)
        lines = open(result[2], encoding="utf-8").read().splitlines()
        self.assertEqual(lines[0], "PC,Eigenvalue,Percent,Cumulative")
        self.assertAlmostEqual(float(lines[1].split(",")[2]), 100.0)
        with self.assertRaisesRegex(NotBuiltError, "random reprojection"):
            ia.MultidimensionalPrincipalComponents(r, "SPATIAL_REDUCTION", "StdTime", "a", "b")

    def test_correlation_and_its_lags(self):
        a = cube_raster([1, 2, 3, 4])
        b = cube_raster([2, 4, 6, 8.5])
        got = cells(ia.MultidimensionalRasterCorrelation(a, b))[0][0]
        x, y = np.array([1, 2, 3, 4.0]), np.array([2, 4, 6, 8.5])
        self.assertAlmostEqual(got, float(np.corrcoef(x, y)[0, 1]))
        self.assertAlmostEqual(cells(ia.MultidimensionalRasterCorrelation(a, b, corr_method="SPEARMAN"))[0][0], 1.0)
        # The page's example: arrays 0–3 and 4–9; lag 2 compares 0–3 with 6–9.
        first = cube_raster([0, 1, 2, 3])
        second = cube_raster([4, 5, 6, 9, 7, 8])
        self.assertAlmostEqual(cells(ia.MultidimensionalRasterCorrelation(first, second, lag=2))[0][0],
                               float(np.corrcoef([0, 1, 2, 3], [6, 9, 7, 8])[0, 1]))
        out = ia.MultidimensionalRasterCorrelation(first, second, lag=1, calculate_xcorr="ALL_CROSS_CORRELATION",
                                                   calculate_pvalue="CALCULATE_P_VALUE")
        self.assertEqual(out.bandNames, ["Lag_-1", "Lag_0", "Lag_1", "P_Value_Lag_-1", "P_Value_Lag_0", "P_Value_Lag_1"])
        self.assertAlmostEqual(cells(out.getRasterBands(1))[0][0], float(np.corrcoef([1, 2, 3], [4, 5, 6])[0, 1]))


class Xarray(unittest.TestCase):
    def test_raster_to_xarray_and_back(self):
        r = cube_raster([[1, 2], [3, 4]], columns=2, mask=[[False, False], [True, False]])
        ds = ia.RasterToXarray(r)
        self.assertEqual(ds["v"].dims, ("StdTime", "y", "x"))
        self.assertEqual(ds.attrs["crs"], "EPSG:3857")
        self.assertEqual(ds["x"].values.tolist(), [5.0, 15.0])
        self.assertTrue(np.isnan(ds["v"].values[1, 0, 0]))
        hourly = ds.resample(StdTime="6h").nearest()
        back = ia.XarrayToRaster(hourly)
        self.assertEqual(back.variables, ["v(StdTime=5)"])
        self.assertEqual(slices(back)[3], [[ND, 4.0]])  # 2020-01-01T18 is nearest to 2020-01-02
        self.assertEqual(back.spatialReference.factoryCode, 3857)


class Collections(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.rasters = [arcpy.NumPyArrayToRaster(np.array([[v, v + 1]]), arcpy.Point(0, 0), 10, 10) for v in (1, 5, 3)]

    def test_filters_statistics_and_mosaics(self):
        rc = ia.RasterCollection(self.rasters, {"Name": ["a", "b", "c"], "Cloud": [10, 50, 30],
                                                "AcquisitionDate": ["2020-01-05T00:00:00", "2020-02-05T00:00:00",
                                                                    "2021-01-05T00:00:00"]})
        self.assertEqual(rc.count, 3)
        self.assertEqual(rc.fields, ["Raster", "Name", "Cloud", "AcquisitionDate"])
        self.assertEqual(rc.filterByAttribute("Cloud", "LESS_THAN", 40).getFieldValues("Name"), ["a", "c"])
        self.assertEqual(rc.filter("Cloud >= 30 AND Name <> 'c'").getFieldValues("Name"), ["b"])
        self.assertEqual(rc.filterByTime(end_time="2020-12-31T00:00:00", time_field_name="AcquisitionDate").count, 2)
        self.assertEqual(rc.filterByCalendarRange("MONTH", 1, time_field_name="AcquisitionDate",
                                                  date_time_format="%Y-%m-%dT%H:%M:%S").getFieldValues("Name"),
                         ["a", "c"])
        self.assertEqual(rc.sort("Cloud", False).getFieldValues("Name"), ["b", "c", "a"])
        self.assertEqual(sorted(rc.groupBy("Name")), ["a", "b", "c"])
        self.assertEqual(rc.summarizeField("Cloud", "MEAN"), {"MEAN": 30.0})
        self.assertEqual(cells(rc.max()), [[5, 6]])
        self.assertEqual(cells(rc.mean()), [[3.0, 4.0]])
        self.assertEqual(cells(rc.median()), [[3.0, 4.0]])
        quality = [arcpy.NumPyArrayToRaster(np.array([[q, -q]]), arcpy.Point(0, 0), 10, 10) for q in (1, 3, 2)]
        self.assertEqual(cells(rc.qualityMosaic(quality, "MAX")), [[5, 2]])
        mapped = rc.map(lambda item: {"raster": item["Raster"] * 2, "Name": item["Name"]})
        self.assertEqual(cells(mapped[1]["Raster"]), [[10, 12]])
        md = rc.toMultidimensionalRaster("Name", "AcquisitionDate")
        self.assertEqual(sorted(md.variables), ["a(StdTime=1)", "b(StdTime=1)", "c(StdTime=1)"])
        with self.assertRaisesRegex(NotBuiltError, "network"):
            ia.RasterCollection.fromSTACAPI("https://example.org")

    def test_mosaic_over_the_union_and_a_cube_as_items(self):
        west = arcpy.NumPyArrayToRaster(np.array([[1, 2]]), arcpy.Point(0, 0), 10, 10)
        east = arcpy.NumPyArrayToRaster(np.array([[7, 8]]), arcpy.Point(10, 0), 10, 10)
        rc = ia.RasterCollection([west, east])
        self.assertEqual(cells(rc.mosaic("FIRST")), [[1, 2, 8]])
        self.assertEqual(cells(rc.mosaic("LAST")), [[1, 7, 8]])
        self.assertEqual(cells(rc.mosaic("SUM")), [[1, 9, 8]])
        rc = ia.RasterCollection(cube_raster([4, 6]))
        self.assertEqual(rc.getFieldValues("StdTime")[1], datetime.datetime(2020, 1, 2))
        self.assertEqual(cells(rc.sum()), [[10.0]])


class Points(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        self.path = os.path.join(self.directory, "p.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "p.gpkg", "POINT", spatial_reference=3857)
        arcpy.management.AddField(self.path, "temp", "DOUBLE")
        arcpy.management.AddField(self.path, "when", "TEXT")
        with arcpy.da.InsertCursor(self.path, ["temp", "when", "SHAPE@XY"]) as rows:
            for value, when, xy in ((10, "2020-01-01", (0, 0)), (20, "2020-01-01", (20, 0)),
                                    (30, "2020-01-01T18:00:00", (20, 0)), (5, "2020-01-02", (0, 20))):
                rows.insertRow((value, when, xy))

    def test_each_day_is_a_slice(self):
        out = ia.InterpolateFromSpatiotemporalPoints(self.path, "daily.nc", "temp", "when", "DAILY", 10, "MEAN")
        r = arcpy.Raster(out.getOutput(0), True)
        self.assertEqual(r.getDimensionValues("temp", "StdTime"), ["2020-01-01T00:00:00", "2020-01-02T00:00:00"])
        # A 3 × 3 grid of 10 m cells from (0, 20) at the top left: day one's points fall in the bottom row.
        self.assertEqual(slices(r)[0][2], [10.0, ND, 25.0])
        out = ia.InterpolateFromSpatiotemporalPoints(self.path, "near.nc", "temp", "when", "DAILY", 10,
                                                     "NEAREST_NEIGHBOR")
        self.assertEqual(slices(arcpy.Raster(out.getOutput(0), True))[1], [[5.0] * 3] * 3)
        out = ia.InterpolateFromSpatiotemporalPoints(self.path, "idw.nc", "temp", "when", "DAILY", 10, "IDW")
        # The middle bottom cell's centre (15, 5) is √50 from (20, 0) and √250 from (0, 0); weights 1/d².
        expected = (10 / 250 + 20 / 50 + 30 / 50) / (1 / 250 + 2 / 50)
        self.assertAlmostEqual(slices(arcpy.Raster(out.getOutput(0), True))[0][2][1], expected)
        with self.assertRaisesRegex(NotBuiltError, "image"):
            ia.InterpolateFromSpatiotemporalPoints(self.path, "q.nc", "temp", "when", "DAILY", 10, "QUADRATIC")


class Change(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory

    def test_compute_change(self):
        a = arcpy.NumPyArrayToRaster(np.array([[1, 2], [3, 3]]), arcpy.Point(0, 0), 10, 10)
        b = arcpy.NumPyArrayToRaster(np.array([[1, 3], [2, 3]]), arcpy.Point(0, 0), 10, 10)
        self.assertEqual(cells(ia.ComputeChange(a, b)), [[0, 1], [-1, 0]])
        self.assertEqual(cells(ia.ComputeChange(a, b, "RELATIVE_DIFFERENCE")), [[0.0, 1 / 3], [-1 / 3, 0.0]])
        c = ia.ComputeChange(a, b, "CATEGORICAL_DIFFERENCE", filter_method="CHANGED_PIXELS_ONLY")
        self.assertEqual(cells(c), [[0, 1], [2, 0]])
        self.assertEqual([row[:5] for row in c._rat.rows], [[0, "Other", "", "", 2], [1, "2>3", "2", "3", 1],
                                                           [2, "3>2", "3", "2", 1]])
        c = ia.ComputeChangeRaster(a, b, "CATEGORICAL_DIFFERENCE", filter_method="UNCHANGED_PIXELS_ONLY")
        self.assertEqual(c._rat.rows[0][1], "No Change")
        x = ia.CompositeBand([arcpy.NumPyArrayToRaster(np.array([[v]])) for v in (1.0, 0.0)])
        y = ia.CompositeBand([arcpy.NumPyArrayToRaster(np.array([[v]])) for v in (0.0, 1.0)])
        self.assertAlmostEqual(cells(ia.ComputeChangeRaster(x, y, "SPECTRAL_ANGLE_DIFFERENCE"))[0][0], math.pi / 2)
        self.assertAlmostEqual(cells(ia.ComputeChangeRaster(x, y, "SPECTRAL_EUCLIDEAN_DISTANCE"))[0][0], math.sqrt(2))
        y = ia.CompositeBand([arcpy.NumPyArrayToRaster(np.array([[v]])) for v in (1.5, 3.0)])
        self.assertEqual(cells(ia.ComputeChangeRaster(x, y, "BAND_WITH_MOST_CHANGE")), [[2]])

    def test_ccdc_finds_the_break_it_was_given(self):
        # Eight years of monthly values on a yearly cycle; the first pixel drops by 0.3 from January 2004.
        times = monthly(range(2000, 2008))
        t = np.array([_mdim.days(x) for x in times])
        season = 0.5 + 0.2 * np.sin(2 * np.pi * t / 365.25)
        noise = np.random.default_rng(1).normal(0, 0.01, (t.size, 2))
        drop = np.where(t >= _mdim.days(_mdim.to_time("2004-01-15")), -0.3, 0.0)
        r = cube_raster(np.column_stack([season + drop, season]) + noise, times, columns=2)
        cc = ia.AnalyzeChangesUsingCCDC(r)
        self.assertEqual(cc.bandNames[-2:], ["Change", "Magnitude"])
        latest = cells(ia.DetectChangeUsingChangeAnalysis(cc, "TIME_OF_LATEST_CHANGE"))
        self.assertAlmostEqual(latest[0][0], _mdim.decimal_year(_mdim.to_time("2004-01-15")))
        self.assertEqual(latest[0][1], ND)
        self.assertEqual(cells(ia.DetectChangeUsingChangeAnalysisRaster(cc, "NUM_OF_CHANGES")), [[1.0, 0.0]])
        with self.assertRaisesRegex(NotBuiltError, "Tmask"):
            ia.AnalyzeChangesUsingCCDC(r, None, [2])

    def test_landtrendr_segments_a_disturbance_and_recovery(self):
        years = list(range(1990, 2011))
        times = [_mdim.to_time(f"{y}-07-01") for y in years]
        disturbed = [0.8 if y < 2000 else 0.3 + 0.05 * (y - 2000) for y in years]
        r = cube_raster(np.column_stack([disturbed, [0.7] * len(years)]), times, columns=2)
        lt = ia.AnalyzeChangesUsingLandTrendr(r, pvalue_threshold=0.05)
        vertex = lt.read()[:, 0, 0, 2]
        self.assertEqual([y for y, v in zip(years, vertex) if v == 1], [1990, 1999, 2000, 2010])
        np.testing.assert_allclose(lt.read()[:, 0, 0, 0], disturbed, atol=1e-9)
        # The fall from 1999 to 2000 is the only decrease; its segment starts in 1999 and ends in 2000.
        start = cells(ia.DetectChangeUsingChangeAnalysisRaster(lt, "TIME_OF_LATEST_CHANGE", 1,
                                                               change_direction="DECREASE"))
        self.assertEqual(start[0][0], 1999.0)
        end = cells(ia.DetectChangeUsingChangeAnalysisRaster(lt, "TIME_OF_LONGEST_CHANGE", 1, "END_OF_SEGMENT",
                                                             "INCREASE"))
        self.assertEqual(end[0][0], 2010.0)
        with self.assertRaises(ValueError):
            ia.DetectChangeUsingChangeAnalysisRaster(ia.AnalyzeChangesUsingCCDC(cube_raster(
                np.sin(np.arange(24)), monthly([2001, 2002]))), "TIME_OF_LONGEST_CHANGE")


class Refused(unittest.TestCase):
    def test_interpolate_raster_by_dimension_has_its_own_reason(self):
        with self.assertRaisesRegex(NotBuiltError, "no Python signature"):
            ia.InterpolateRasterByDimension()

    def test_a_trend_raster_from_elsewhere_is_refused(self):
        with self.assertRaisesRegex(NotBuiltError, "not a trend raster grapy made"):
            ia.PredictUsingTrend(cube_raster([1, 2]), "BY_VALUE", "2020-02-01")


if __name__ == "__main__":
    unittest.main()
