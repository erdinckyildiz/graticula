"""The solar radiation tools on Fu and Rich's model — ADR-243.

The expected values are computed here from the equations of Esri's *How solar radiation is calculated* (equations 2,
3 and 4) with the sun placed by Spencer's (1971) declination in solar time, independently of the module's code: on a
flat, unobstructed plane every sun map sector's gap fraction is 1 and its angle of incidence is the sun's zenith angle.
"""
import datetime as dt
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))

import grapy as arcpy  # noqa: E402
from grapy import _insolation, _solar  # noqa: E402


def spencer(day):
    g = 2 * math.pi * (day - 1) / 365.0
    return math.degrees(0.006918 - 0.399912 * math.cos(g) + 0.070257 * math.sin(g) - 0.006758 * math.cos(2 * g)
                        + 0.000907 * math.sin(2 * g) - 0.002697 * math.cos(3 * g) + 0.00148 * math.sin(3 * g))


def dem(values, cell=10.0, srid=32633, x0=500000.0, y0=4500000.0):
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(srid)
    try:
        return arcpy.NumPyArrayToRaster(np.array(values, float), arcpy.Point(x0, y0), cell, cell)
    finally:
        arcpy.env.outputCoordinateSystem = None


def cells(raster):
    return arcpy.RasterToNumPyArray(raster, nodata_to_value=-9999.0)


def plane(gx, gy, size=15, cell=10.0):
    """z rising gx per unit east and gy per unit north."""
    r, c = np.indices((size, size))
    x, y = (c - size // 2) * cell, -(r - size // 2) * cell
    return dem(gx * x + gy * y + 100.0, cell)


class _Quiet(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.mkdtemp()
        arcpy.env.workspace = self.directory
        arcpy.env.extent = None


class Formula(_Quiet):
    def test_direct_on_a_flat_plane_follows_equations_2_to_4(self):
        """One sector at solar noon on 21 June at 40° N: Dir = 1367 · β^(1/cos θ) · 1 h · cos θ, at sea level."""
        flat = dem(np.zeros((5, 5)))
        out = arcpy.sa.AreaSolarRadiation(flat, 40, 200, arcpy.sa.TimeWithinDay(172, 11.5, 12.5), 14, 1,
                                          "NOINTERVAL", 1, "FLAT_SURFACE", 32, 8, 8, "UNIFORM_SKY", 0.0, 0.5)
        theta = math.radians(40 - spencer(172))
        expected = 1367 * 0.5 ** (1 / math.cos(theta)) * math.cos(theta)
        self.assertAlmostEqual(float(cells(out)[2, 2]), expected, delta=0.05)
        self.assertAlmostEqual(expected, 636.2, delta=0.5)  # the hand computation, θ ≈ 16.55°

    def test_elevation_shortens_the_optical_path(self):
        """Equation 3's elevation term: at 1000 m, m = exp(−0.118 − 0.001638)/cos θ."""
        high = dem(np.full((5, 5), 1000.0))
        out = arcpy.sa.AreaSolarRadiation(high, 40, 200, arcpy.sa.TimeWithinDay(172, 11.5, 12.5), 14, 1,
                                          "NOINTERVAL", 1, "FLAT_SURFACE", 32, 8, 8, "UNIFORM_SKY", 0.0, 0.5)
        theta = math.radians(40 - spencer(172))
        m = math.exp(-0.000118 * 1000 - 1.638e-9 * 1000 ** 2) / math.cos(theta)
        self.assertAlmostEqual(float(cells(out)[2, 2]), 1367 * 0.5 ** m * math.cos(theta), delta=0.05)

    def test_a_whole_day_sums_its_half_hour_sectors(self):
        """The sectors of a day on a flat plane: Σ 1367 · β^m(θ) · SunDur · cos θ, partial sectors at sunrise and
        sunset taking their sunlit part."""
        flat = dem(np.zeros((5, 5)))
        out = arcpy.sa.AreaSolarRadiation(flat, 40, 400, arcpy.sa.TimeWithinDay(100, 0, 24), 14, 0.5,
                                          "NOINTERVAL", 1, "FLAT_SURFACE", 32, 8, 8, "UNIFORM_SKY", 0.0, 0.5)
        dec = spencer(100)
        h0 = math.degrees(math.acos(-math.tan(math.radians(40)) * math.tan(math.radians(dec)))) / 15
        rise, set_ = 12 - h0, 12 + h0
        total, t = 0.0, rise
        while t < set_ - 1e-9:
            a, b = t, min(t + 0.5, set_)
            h = math.radians(15 * ((a + b) / 2 - 12))
            cz = (math.sin(math.radians(40)) * math.sin(math.radians(dec))
                  + math.cos(math.radians(40)) * math.cos(math.radians(dec)) * math.cos(h))
            z = math.degrees(math.acos(cz))
            m = 1 / cz if z < 80 else 1 / (cz + 0.50572 * (96.07995 - z) ** -1.6364)
            total += 1367 * 0.5 ** m * (b - a) * cz
            t += 0.5
        self.assertAlmostEqual(float(cells(out)[2, 2]), total, delta=total * 1e-6)

    def test_a_horizon_reads_the_nearest_cell_at_its_own_distance(self):
        # ArcGIS Pro 3.7's durations follow a horizon traced over the nearest cell, measured to that cell's centre, a
        # ray stopping at the outermost cells' centres (D-337, tools/pro-compare/adr243-solar-horizon.py): on a 60 x 60
        # DEM the steep flanks' durations moved from up to 1.6 h off Pro's to 0.86 h. On the plane z = 10 * column
        # (30 m cells), from the centre of an 11 x 11 grid at 22.5 degrees, the steps' nearest cells are (4, 5), (3, 6),
        # (2, 6), (1, 7) and (0, 7); the highest angle is to (3, 6), atan(10 / (30 * sqrt 5)) - where sampling the
        # plane itself would give atan(sin 22.5 / 3) in every step.
        z = np.tile(np.arange(11) * 10.0, (11, 1))
        h = _insolation.horizons(z, 30.0, 30.0, [5.0], [5.0], [50.0], 16)
        self.assertAlmostEqual(h[0, 1], math.degrees(math.atan(10 / (30 * math.sqrt(5)))), 9)
        self.assertNotAlmostEqual(h[0, 1], math.degrees(math.atan(math.sin(math.radians(22.5)) / 3)), 2)
        self.assertEqual(h[0, 12], 0.0)   # downhill to the west: nothing above the cell
        sky = _insolation.Sky(40)
        horizon = np.array([[0.0] * 15 + [30.0] + [0.0] * 16])
        np.testing.assert_array_equal(sky.visible_by_cell(horizon)[np.argsort(sky.wedge_order(32)[0])].T,
                                      sky.visible(horizon))

    def test_sky_weights_sum_to_one(self):
        for model in ("UNIFORM_SKY", "STANDARD_OVERCAST_SKY"):
            self.assertAlmostEqual(float(_insolation.weights(8, 16, model).sum() * 16), 1.0, places=12)

    def test_diffuse_on_a_flat_open_plane(self):
        """Equations 5 and 6 on a flat plane with the whole sky visible: Dif = R_glb·Dur · P_dif · Σ Weight · cos θc,
        and for the uniform sky Σ Weight · cos θc over 8 rings is close to ∫ cos θ sin θ dθ = 1/2."""
        flat = dem(np.zeros((5, 5)))
        config = arcpy.sa.TimeWithinDay(172, 11.5, 12.5)
        direct_path = os.path.join(self.directory, "direct.tif")
        diffuse_path = os.path.join(self.directory, "diffuse.tif")
        arcpy.sa.AreaSolarRadiation(flat, 40, 200, config, 14, 1, "NOINTERVAL", 1, "FLAT_SURFACE", 32, 8, 8,
                                    "UNIFORM_SKY", 0.3, 0.5, direct_path, diffuse_path)
        theta = math.radians(40 - spencer(172))
        normal = 1367 * 0.5 ** (1 / math.cos(theta))
        rings = np.arange(8)
        t1, t2 = np.radians(rings * 11.25), np.radians((rings + 1) * 11.25)
        share = float(((np.cos(t1) - np.cos(t2)) * np.cos(np.radians((rings + 0.5) * 11.25))).sum())
        self.assertAlmostEqual(share, 0.5, delta=0.01)
        expected = normal / 0.7 * 0.3 * share
        self.assertAlmostEqual(float(cells(arcpy.Raster(diffuse_path))[2, 2]), expected, delta=0.05)
        self.assertAlmostEqual(float(cells(arcpy.Raster(direct_path))[2, 2]), normal * math.cos(theta), delta=0.05)


class Terrain(_Quiet):
    def test_a_north_facing_slope_receives_less_than_a_south_facing_one(self):
        """At 45° N over the year's special days, a slope facing south (rising to the north) is the sunnier."""
        south = arcpy.sa.AreaSolarRadiation(plane(0, 0.4), 45, 200, arcpy.sa.TimeSpecialDays(), 14, 1)
        north = arcpy.sa.AreaSolarRadiation(plane(0, -0.4), 45, 200, arcpy.sa.TimeSpecialDays(), 14, 1)
        flat = arcpy.sa.AreaSolarRadiation(plane(0, 0), 45, 200, arcpy.sa.TimeSpecialDays(), 14, 1)
        s, n, f = (float(cells(r)[7, 7]) for r in (south, north, flat))
        self.assertGreater(s, f)
        self.assertGreater(f, n)

    def test_a_pit_is_shaded(self):
        """A cell at the bottom of a deep pit sees less sky and less sun than the same cell in the open."""
        open_ = np.zeros((11, 11))
        pit = open_.copy()
        pit[3:8, 3:8] = -60
        pit[5, 5] = -60
        config = arcpy.sa.TimeWithinDay(80, 0, 24)
        a = cells(arcpy.sa.AreaSolarRadiation(dem(open_), 40, 200, config, 14, 1, "NOINTERVAL", 1, "FLAT_SURFACE"))
        b = cells(arcpy.sa.AreaSolarRadiation(dem(pit), 40, 200, config, 14, 1, "NOINTERVAL", 1, "FLAT_SURFACE"))
        self.assertLess(float(b[5, 5]), 0.6 * float(a[5, 5]))

    def test_horizon_of_a_wall(self):
        """A wall 10 units high 20 units north of a cell: the horizon to the north is atan(10/20)."""
        z = np.zeros((9, 9))
        z[2, :] = 10.0
        h = _insolation.horizons(z, 10.0, 10.0, np.array([4.0]), np.array([4.0]), np.array([0.0]), 8)
        self.assertAlmostEqual(h[0, 0], math.degrees(math.atan(10 / 20)), places=6)
        self.assertAlmostEqual(h[0, 4], 0.0, places=9)  # nothing to the south


class Intervals(_Quiet):
    def test_bands_sum_to_the_total(self):
        flat = plane(0.1, 0.2, size=7)
        config = arcpy.sa.TimeWithinDay(200, 6, 18)
        total = cells(arcpy.sa.AreaSolarRadiation(flat, 30, 200, config, 14, 2))
        bands = arcpy.sa.AreaSolarRadiation(flat, 30, 200, config, 14, 2, "INTERVAL")
        stack = np.stack([arcpy.RasterToNumPyArray(b) for b in bands.getRasterBands()])
        self.assertEqual(stack.shape[0], 6)
        self.assertAlmostEqual(float(stack[:, 3, 3].sum()), float(total[3, 3]), delta=1e-2)

    def test_whole_year_has_twelve_months(self):
        intervals, by_hour = _insolation.day_intervals(arcpy.sa.TimeWholeYear(2024), 14)
        self.assertFalse(by_hour)
        self.assertEqual(len(intervals), 12)
        self.assertEqual(sum(len(d) for _, d in intervals), 366)

    def test_multiple_days_wrap_into_the_next_year(self):
        intervals, _ = _insolation.day_intervals(arcpy.sa.TimeMultipleDays(2023, 365, 31), 14)
        days = [d for _, ds in intervals for d in ds]
        self.assertEqual(len(days), 31)  # December 31 to January 30, ending at 0:00 on January 31
        self.assertEqual(days[0], 365)

    def test_time_class_defaults(self):
        t = arcpy.sa.TimeMultipleDays()
        self.assertEqual((t.startDay, t.endDay), (5, 160))
        w = arcpy.sa.TimeWithinDay()
        self.assertEqual((w.day, w.startTime, w.endTime), (183, 0.0, 24.0))
        self.assertIs(arcpy.ia.TimeWholeYear, arcpy.sa.TimeWholeYear)


class Points(_Quiet):
    def test_points_match_the_area_tool(self):
        surface = plane(0.2, -0.1, size=9)
        path = os.path.join(self.directory, "where.gpkg")
        arcpy.management.CreateFeatureclass(self.directory, "where.gpkg", "POINT", spatial_reference=32633)
        x, y = 500000.0 + 45.0, 4500000.0 + 45.0  # the centre of cell (4, 4)
        with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
            rows.insertRow([arcpy.PointGeometry(arcpy.Point(x, y), arcpy.SpatialReference(32633))])
        config = arcpy.sa.TimeWithinDay(150, 0, 24)
        out = os.path.join(self.directory, "global.gpkg")
        arcpy.sa.PointsSolarRadiation(surface, path, out, 0, 35, 200, config, 14, 1)
        with arcpy.da.SearchCursor(out, ["T0"]) as rows:
            value = next(iter(rows))[0]
        area = cells(arcpy.sa.AreaSolarRadiation(surface, 35, 200, config, 14, 1))
        self.assertAlmostEqual(value, float(area[4, 4]), delta=1e-3 * value)


class Graphics(_Quiet):
    def test_open_sky_and_maps(self):
        sun = os.path.join(self.directory, "sun.tif")
        skymap = os.path.join(self.directory, "sky.tif")
        view = arcpy.sa.SolarRadiationGraphics(dem(np.zeros((7, 7))), None, 64, 0, 32, 40,
                                               arcpy.sa.TimeMultipleDays(2023, 100, 130), 14, 0.5, sun, 8, 16, skymap)
        v = arcpy.RasterToNumPyArray(view, nodata_to_value=-1)
        self.assertEqual(v.shape, (64, 64))
        self.assertEqual(set(np.unique(v)) - {-1}, {1})
        self.assertEqual(v[0, 0], -1)  # a corner is outside the hemisphere
        s = arcpy.RasterToNumPyArray(arcpy.Raster(skymap), nodata_to_value=-1)
        self.assertEqual(len(set(np.unique(s)) - {-1}), 8 * 16)
        self.assertGreater(int((arcpy.RasterToNumPyArray(arcpy.Raster(sun), nodata_to_value=0) > 0).sum()), 0)


class RasterTool(_Quiet):
    def test_flat_plane_follows_the_formula_each_half_hour(self):
        """Direct radiation on a flat plane over one day: Σ 1367 · β^(1/cos z) · 0.5 h · cos z / 1000 kWh/m², the sun
        placed at the middle of each half hour by NOAA's equations, at the cell's longitude and latitude."""
        flat = dem(np.zeros((5, 5)))
        out = arcpy.sa.RasterSolarRadiation(flat, "6/21/2024 12:00:00 AM", "6/21/2024 11:59:59 PM",
                                            diffuse_proportion=0.0, transmittivity=0.6)
        lon, lat = _solar.place(500025.0, 4500025.0, 32633)
        total = 0.0
        start = dt.datetime(2024, 6, 21)
        for k in range(48):
            _, elevation = _solar.position(start + dt.timedelta(minutes=30 * k + 15), lon, lat)
            if elevation > 0:
                z = 90 - elevation
                m = 1 / math.cos(math.radians(z)) if z < 80 else 1 / (
                    math.cos(math.radians(z)) + 0.50572 * (96.07995 - z) ** -1.6364)
                total += 1367 * 0.6 ** m * 0.5 * math.cos(math.radians(z)) / 1000
        self.assertAlmostEqual(float(cells(out)[2, 2]), total, delta=1e-4)

    def test_intervals_make_a_multidimensional_raster(self):
        surface = plane(0.1, 0.3, size=5)
        total = cells(arcpy.sa.RasterSolarRadiation(surface, "3/1/2024 12:00 AM", "3/7/2024 12:00 AM"))
        cube = arcpy.sa.RasterSolarRadiation(surface, "3/1/2024 12:00 AM", "3/7/2024 12:00 AM",
                                             use_time_interval="INTERVAL", interval_unit="DAY", interval=2)
        variable = cube._cube.variable("global_solar_radiation")
        self.assertEqual(len(variable.coords), 3)
        self.assertAlmostEqual(float(variable.data[:, 0, 2, 2].sum()), float(total[2, 2]), delta=1e-4)

    def test_feature_solar_radiation_is_refused_for_its_fields(self):
        from grapy._runtime import NotBuiltError
        with self.assertRaises(NotBuiltError) as caught:
            arcpy.sa.FeatureSolarRadiation("dem", "f", "id", "t", "1/1/2024", "1/2/2024")
        self.assertIn("names none of those fields", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
