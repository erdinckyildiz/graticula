"""arcpy.ia's synthetic aperture radar tools on grapy's SAR datasets (ADR-237), against hand-worked answers.

The data is synthetic (``sar_fixture``): a small Sentinel-1-shaped SAFE product written here, and radar scenes on an
orbit whose ground points the fixture solves independently. Sentinel scenes are never downloaded.
"""
import json
import math
import os
import sys
import tempfile
import unittest

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
sys.path.insert(0, os.path.dirname(__file__))

import grapy as arcpy  # noqa: E402 — the way a toolbox imports it
from grapy import _sar, _sartools  # noqa: E402
from grapy._runtime import ExecuteError, NotBuiltError  # noqa: E402

import sar_fixture as F  # noqa: E402


def _read(path):
    return _sar.read(path, "test")


class Geometry(unittest.TestCase):
    def test_a_point_on_the_equator_is_at_zero_doppler_when_the_sensor_crosses_it(self):
        # Hand-worked: the orbit crosses the equator at 30° E at T0, moving due north, so every point on the equator
        # is at zero Doppler then, at the range |P − S| = sqrt(a² + Rs² − 2·a·Rs·cos δ).
        m = F.meta()
        delta = 4.0
        p = _sar.ecef(30.0 + delta, 0.0, 0.0)
        t, r, _ = _sar.zero_doppler(m, p)
        a = _sar.WGS84_A
        self.assertAlmostEqual(t[0] - F.T0, 0.0, delta=1e-6)
        self.assertAlmostEqual(r[0], math.sqrt(a * a + F.RS ** 2 - 2 * a * F.RS * math.cos(math.radians(delta))),
                               delta=1e-3)

    def test_geocoding_and_back_agree_with_the_independent_solution(self):
        m = F.meta()
        rows, cols = np.array([3.0, 20.0, 45.0]), np.array([5.0, 50.0, 70.0])
        p = _sar.geocode(m, rows, cols, 0.0)
        q = np.array([F.ground(F.T1 + r * F.DT, (F.TAU0 + c / F.FS) * _sar.C / 2) for r, c in zip(rows, cols)])
        self.assertLess(np.max(np.linalg.norm(p - q, axis=1)), 0.01)
        r, c, _ = _sar.backgeocode(m, q)
        np.testing.assert_allclose(r, rows, atol=1e-3)
        np.testing.assert_allclose(c, cols, atol=1e-3)

    def test_tie_points_of_one_line_need_not_share_a_time(self):
        # A real IW GRD (S1D_IW_GRDH_1SDV_20261031T054608, ADR-237 condition 3): the 21 tie points of a line differ in
        # azimuth time by 0.55 ms across the swath. Grouped by time they made no row, and back-geocoding failed.
        m = F.meta()
        grid = np.asarray(m["grid"], float)
        spread = (grid[:, 3] - grid[:, 3].min()) / np.ptp(grid[:, 3]) * 0.55e-3 - 0.27e-3
        grid[:, 0] += spread
        m["grid"] = grid.tolist()
        self.assertEqual(len(_sar._grid_rows(m)), len(np.unique(grid[:, 2])))
        rows, cols = np.array([3.0, 20.0, 45.0]), np.array([5.0, 50.0, 70.0])
        q = np.array([F.ground(F.T1 + r * F.DT, (F.TAU0 + c / F.FS) * _sar.C / 2) for r, c in zip(rows, cols)])
        r, c, _ = _sar.backgeocode(m, q)
        np.testing.assert_allclose(r, rows, atol=1e-3)   # rows come from the orbit; the tie points give the range
        np.testing.assert_allclose(c, cols, atol=0.05)

    def test_a_point_100_m_up_is_found_where_its_range_says(self):
        m = F.meta()
        q = F.ground(F.T1 + 10 * F.DT, (F.TAU0 + 30 / F.FS) * _sar.C / 2, h=100.0)
        p = _sar.geocode(m, np.array([10.0]), np.array([30.0]), 100.0)
        self.assertLess(np.linalg.norm(p[0] - q), 0.01)


class SafeAndCalibration(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def _grd(self, **kw):
        dn = np.full((F.ROWS, F.COLS), 100, np.uint16)
        dn[10, 20] = 200
        return F.safe(self.dir, "GRD", {"VV": dn, "VH": dn // 2}, **kw)

    def test_a_safe_product_reads_as_its_bands_and_annotation(self):
        sar = _read(self._grd())
        self.assertEqual(sar.names, ["VH", "VV"])
        self.assertEqual(sar.meta["values"], "amplitude")
        self.assertEqual(sar.meta["product"], "GRD")
        self.assertAlmostEqual(sar.meta["ipf_version"], 3.31)
        self.assertEqual(len(sar.meta["orbit"]), 14)

    def test_sigma_nought_is_dn_squared_over_the_lut_interpolated_between_its_lines(self):
        out = os.path.join(self.dir, "cal.tif")
        arcpy.ia.ApplyRadiometricCalibration(self._grd(), out, "VV", "SIGMA_NOUGHT")
        band = _read(out).bands["VV"]
        # Hand-worked: the LUT is 400 at line 0 and 500 at line 59; line 10 is 400 + 100·10/59.
        a = 400 + 100 * 10 / 59
        self.assertAlmostEqual(float(band[10, 20]), 200 ** 2 / a ** 2, delta=1e-5)
        self.assertAlmostEqual(float(band[0, 0]), 100 ** 2 / 400 ** 2, delta=1e-6)
        self.assertAlmostEqual(float(band[59, 5]), 100 ** 2 / 500 ** 2, delta=1e-6)
        meta = json.load(open(out + _sar.SIDE))
        self.assertEqual((meta["calibration"], meta["values"]), ("sigma0", "intensity"))

    def test_the_first_band_is_calibrated_by_default_and_complex_stays_complex(self):
        slc = (np.full((F.ROWS, F.COLS), 30 + 40j)).astype(np.complex64)
        name = F.safe(self.dir, "SLC", {"VV": slc})
        out = os.path.join(self.dir, "b0.tif")
        arcpy.ia.ApplyRadiometricCalibration(name, out)
        sar = _read(out)
        self.assertEqual(sar.names, ["VV"])
        self.assertAlmostEqual(complex(sar.bands["VV"][5, 5]), (30 + 40j) / 300, delta=1e-6)

    def test_an_ipf_of_2_34_or_earlier_is_refused_as_documented(self):
        with self.assertRaises(ExecuteError):
            arcpy.ia.ApplyRadiometricCalibration(self._grd(ipf="002.34"), os.path.join(self.dir, "x.tif"))

    def test_where_the_noise_exceeds_the_power_pro_writes_1e_5(self):
        # ArcGIS Pro 3.7 on a real IW GRD (ADR-237 condition 3): every pixel whose DN² − η is negative reads 1e-5.
        dn = np.full((F.ROWS, F.COLS), 100, np.uint16)
        dn[10, 20] = 2   # 4 − η < 0
        name = F.safe(self.dir, "GRD", {"VV": dn})
        out = os.path.join(self.dir, "floor.tif")
        arcpy.ia.RemoveThermalNoise(name, out, "VV")
        self.assertAlmostEqual(float(_read(out).bands["VV"][10, 20]), 1e-5, delta=1e-9)
        # and calibrates it as 2**19 x 1e-5 / A² (36 floored pixels of the same scene, to seven digits)
        cal = os.path.join(self.dir, "floor_s0.tif")
        arcpy.ia.ApplyRadiometricCalibration(out, cal, "VV", "SIGMA_NOUGHT")
        a = 400.0 + 100.0 * 10 / (F.ROWS - 1)   # the fixture's sigma nought LUT at line 10
        self.assertAlmostEqual(float(_read(cal).bands["VV"][10, 20]) * a * a / 1e-5, 2 ** 19, delta=1.0)

    def test_thermal_noise_is_the_range_lut_times_the_azimuth_lut(self):
        name = self._grd(noise_az=(1.0, 2.0))
        out = os.path.join(self.dir, "tnr.tif")
        arcpy.ia.RemoveThermalNoise(name, out, "VV")
        band = _read(out).bands["VV"]
        # Hand-worked at line 10, pixel 20: range LUT 20 → 30 → 40 at pixels 0, 39, 79, so 20 + 10·20/39;
        # azimuth LUT 1 → 2 over lines 0..59, so 1 + 10/59.
        eta = (20 + 10 * 20 / 39) * (1 + 10 / 59)
        self.assertAlmostEqual(float(band[10, 20]), 200 ** 2 - eta, delta=1e-2)
        # then calibrated: (DN² − η)/A²
        cal = os.path.join(self.dir, "tnr_cal.tif")
        arcpy.ia.ApplyRadiometricCalibration(out, cal, "VV", "BETA_NOUGHT")
        self.assertAlmostEqual(float(_read(cal).bands["VV"][10, 20]), (200 ** 2 - eta) / 300 ** 2, delta=1e-6)

    def test_noise_removed_after_calibration_is_calibrated_by_the_same_lut(self):
        name = self._grd()
        cal = os.path.join(self.dir, "c.tif")
        arcpy.ia.ApplyRadiometricCalibration(name, cal, "VV", "BETA_NOUGHT")
        out = os.path.join(self.dir, "c_tnr.tif")
        arcpy.ia.RemoveThermalNoise(cal, out, "VV")
        self.assertAlmostEqual(float(_read(out).bands["VV"][0, 0]), (100 ** 2 - 20) / 300 ** 2, delta=1e-6)

    def test_a_dataset_without_tables_is_not_calibrated(self):
        path = F.write(os.path.join(self.dir, "plain.tif"), {"VV": np.ones((F.ROWS, F.COLS), np.complex64)}, F.meta())
        with self.assertRaises(ExecuteError):
            arcpy.ia.ApplyRadiometricCalibration(path, os.path.join(self.dir, "y.tif"))
        with self.assertRaises(ExecuteError):
            arcpy.ia.ApplyRadiometricCalibration(os.path.join(self.dir, "missing.tif"), os.path.join(self.dir, "y.tif"))


class DeburstAndMultilook(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def test_bursts_are_joined_at_the_middle_of_their_overlap(self):
        # Three bursts of 10 lines; each burst starts 7 lines after the one before, so 3 lines overlap; lines 0 and 9
        # of each burst are invalid. Valid times: burst 0 lines 1..8 (t 1..8), burst 1 lines 1..8 (t 8..15), burst 2
        # (t 15..22). Burst 0 ends at t 8 and burst 1 begins at t 8: the middle is 8, so t 8 comes from burst 1 —
        # and every value says which burst and line it came from.
        lpb, cols = 10, 6
        values = np.array([[100 * b + j for _ in range(cols)] for b in range(3) for j in range(lpb)], float)
        first_valid = [-1] + [1] * 8 + [-1]
        last_valid = [-1] + [4] * 8 + [-1]
        times = [F.T1 + 7 * b * F.DT for b in range(3)]
        name = F.safe(self.dir, "SLC", {"VV": values.astype(np.complex64)}, rows=30, cols=cols,
                      bursts=(lpb, times, first_valid, last_valid))
        out = os.path.join(self.dir, "deb.tif")
        arcpy.ia.Deburst(name, out)
        sar = _read(out)
        got = sar.bands["VV"][:, 2].real
        expected = [1, 2, 3, 4, 5, 6, 7, 101, 102, 103, 104, 105, 106, 107, 201, 202, 203, 204, 205, 206, 207, 208]
        np.testing.assert_array_equal(got, expected)
        self.assertTrue(np.isnan(sar.bands["VV"][0, 0]))  # sample 0 is before the first valid sample
        self.assertTrue(np.isnan(sar.bands["VV"][0, 5]))  # sample 5 is past the last
        self.assertAlmostEqual(sar.meta["first_line_time"], F.T1 + F.DT, delta=1e-9)
        self.assertNotIn("bursts", sar.meta)

    def test_deburst_needs_bursts(self):
        path = F.write(os.path.join(self.dir, "p.tif"), {"VV": np.ones((F.ROWS, F.COLS), np.complex64)}, F.meta())
        with self.assertRaises(ExecuteError):
            arcpy.ia.Deburst(path, os.path.join(self.dir, "q.tif"))

    def test_multilook_averages_intensity_over_its_blocks(self):
        z = (np.arange(F.ROWS * F.COLS).reshape(F.ROWS, F.COLS) % 7 + 1j).astype(np.complex64)
        path = F.write(os.path.join(self.dir, "slc.tif"), {"VV": z}, F.meta())
        out = os.path.join(self.dir, "ml.tif")
        arcpy.ia.Multilook(path, out, "VV", 4, 2)
        sar = _read(out)
        intensity = np.abs(z.astype(complex)) ** 2
        self.assertEqual(sar.shape, (30, 20))
        self.assertAlmostEqual(float(sar.bands["VV"][3, 5]), intensity[6:8, 20:24].mean(), delta=1e-4)
        self.assertEqual(sar.meta["looks"], [4, 2])
        self.assertAlmostEqual(sar.meta["line_interval"], 2 * F.DT)
        # the multilooked grid still geocodes: its row 3 is original rows 6 and 7, so its time is row 6.5's
        self.assertAlmostEqual(float(_sar.row_time(sar.meta, [3])[0]), F.T1 + 6.5 * F.DT, delta=1e-9)

    def test_the_default_looks_make_the_cell_about_square(self):
        # Sentinel-1 IW SLC: 2.33 m slant range at 39°, 13.9 m azimuth → 3.7 m on the ground: 4 range looks.
        meta = {"range_pixel_spacing": 2.33, "azimuth_pixel_spacing": 13.9, "incidence_angle_mid": 39.0,
                "product": "SLC"}
        self.assertEqual(_sartools.square_looks(meta), (4, 1))
        # and the page's example: a coherence range window of 10 gives an azimuth window of 3
        self.assertEqual(_sartools.square_looks(meta, 10), (10, 3))


class Units(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def _dataset(self, values, kind, scale="linear"):
        m = F.meta(rows=2, cols=2, values=kind)
        m["scale"] = scale
        return F.write(os.path.join(self.dir, f"{kind}_{scale}.tif"), {"VV": values}, m)

    def test_the_documented_conversions(self):
        path = self._dataset(np.array([[100.0, 0.01], [1.0, 0.0]], np.float32), "intensity")
        db = os.path.join(self.dir, "db.tif")
        arcpy.ia.ConvertSARUnits(path, db)
        band = _read(db).bands["VV"]
        self.assertAlmostEqual(float(band[0, 0]), 20.0, delta=1e-5)
        self.assertAlmostEqual(float(band[0, 1]), -20.0, delta=1e-5)
        self.assertTrue(np.isnan(band[1, 1]))
        back = os.path.join(self.dir, "lin.tif")
        arcpy.ia.ConvertSARUnits(db, back, "DB_TO_LINEAR")
        self.assertAlmostEqual(float(_read(back).bands["VV"][0, 0]), 100.0, delta=1e-3)
        amp = os.path.join(self.dir, "amp.tif")
        arcpy.ia.ConvertSARUnits(path, amp, "INTENSITY_TO_AMPLITUDE")
        self.assertAlmostEqual(float(_read(amp).bands["VV"][0, 0]), 10.0, delta=1e-5)
        with self.assertRaises(ExecuteError):
            arcpy.ia.ConvertSARUnits(db, os.path.join(self.dir, "x.tif"), "LINEAR_TO_DB")

    def test_complex_to_intensity_adds_the_squares(self):
        path = self._dataset(np.array([[3 + 4j, 1j], [0, 2]], np.complex64), "complex")
        out = os.path.join(self.dir, "ci.tif")
        arcpy.ia.ConvertSARUnits(path, out, "COMPLEX_TO_INTENSITY")
        self.assertAlmostEqual(float(_read(out).bands["VV"][0, 0]), 25.0, delta=1e-5)

    def test_unwrapped_phase_to_displacement_by_the_wavelength(self):
        path = self._dataset(np.array([[2 * math.pi, 0], [0, 0]], np.float32), "unwrapped_phase")
        out = os.path.join(self.dir, "d.tif")
        arcpy.ia.ConvertSARUnits(path, out, "PHASE_TO_DISPLACEMENT")
        wavelength = _sar.C / F.FREQ
        self.assertAlmostEqual(float(_read(out).bands["VV"][0, 0]), -wavelength / 2, delta=1e-6)


class Speckle(unittest.TestCase):
    def test_lee_is_the_mmse_estimate_of_its_paper(self):
        rng = np.random.default_rng(1)
        z = rng.gamma(4, 0.25, (9, 9))
        out = _sartools.lee(z, 3, "MULTIPLICATIVE_NOISE", looks=4)
        w = z[3:6, 3:6]
        mean, var = w.mean(), w.var()
        var_x = max((var - mean ** 2 / 4) / (1 / 4 + 1), 0)
        expected = mean + var_x / var * (z[4, 4] - mean)
        self.assertAlmostEqual(out[4, 4], expected, delta=1e-12)

    def test_lee_additive_subtracts_the_noise_mean_and_variance(self):
        z = np.arange(81, dtype=float).reshape(9, 9) % 5
        out = _sartools.lee(z, 3, "ADDITIVE_NOISE", noise_variance=0.25, add_mean=0.5)
        w = z[3:6, 3:6]
        mean, var = w.mean(), w.var()
        xbar = mean - 0.5
        k = max(var - 0.25, 0) / var
        self.assertAlmostEqual(out[4, 4], xbar + k * (z[4, 4] - xbar - 0.5), delta=1e-12)

    def test_refined_lee_keeps_a_step_edge_sharp(self):
        z = np.ones((15, 15))
        z[:, 8:] = 10.0
        out = _sartools.refined_lee(z, 1)
        np.testing.assert_allclose(out[3:12, 3:12], z[3:12, 3:12], atol=1e-9)
        # a 3×3 boxcar would have blurred it
        from scipy import ndimage
        self.assertGreater(np.abs(ndimage.uniform_filter(z, 3) - z)[5, 7], 1)

    def test_the_filters_leave_a_flat_field_flat_and_reduce_speckle(self):
        rng = np.random.default_rng(3)
        speckled = rng.gamma(1, 1.0, (64, 64))
        for name, f in (("kuan", lambda z: _sartools.kuan(z, 5, 1)), ("gamma", lambda z: _sartools.gamma_map(z, 5, 1)),
                        ("enhanced", lambda z: _sartools.enhanced_lee(z, 5, 1, 1)),
                        ("frost", lambda z: _sartools.frost(z, 5, 1)), ("refined", lambda z: _sartools.refined_lee(z, 1))):
            with self.subTest(name):
                np.testing.assert_allclose(f(np.full((12, 12), 3.0)), 3.0, atol=1e-9)
                self.assertLess(f(speckled).std(), 0.75 * speckled.std())

    def test_despeckle_writes_intensity_and_keeps_nodata(self):
        d = tempfile.mkdtemp()
        rng = np.random.default_rng(5)
        z = rng.gamma(1, 1.0, (F.ROWS, F.COLS)).astype(np.float32)
        z[0, 0] = np.nan
        path = F.write(os.path.join(d, "i.tif"), {"VV": z}, F.meta(values="intensity"))
        out = os.path.join(d, "f.tif")
        arcpy.ia.Despeckle(path, out, filter_type="LEE", filter_size="5x5")
        band = _read(out).bands["VV"]
        self.assertTrue(np.isnan(band[0, 0]))
        self.assertLess(np.nanstd(band), np.nanstd(z))
        with self.assertRaises(ExecuteError):
            arcpy.ia.Despeckle(path, out, filter_size="4x4")


class Interferometry(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def _pair(self, baseline=(0.0, 150.0, 80.0)):
        """A reference and a secondary on one grid, each sample exp(−i4πR/λ) of its own sensor's range to the
        cell's ground point on the ellipsoid."""
        ref_meta = F.meta(rows=24, cols=30)
        sec_meta = F.meta(rows=24, cols=30, offset=baseline)
        wavelength = _sar.C / F.FREQ
        rows, cols = np.meshgrid(np.arange(24.0), np.arange(30.0), indexing="ij")
        p = np.array([F.ground(F.T1 + r * F.DT, (F.TAU0 + c / F.FS) * _sar.C / 2)
                      for r, c in zip(rows.ravel(), cols.ravel())])
        r1 = np.linalg.norm(p - np.array([F.state(F.T1 + r * F.DT)[0] for r in rows.ravel()]), axis=1)
        t2, r2, _ = _sar.zero_doppler(sec_meta, p)
        s1 = np.exp(-4j * math.pi * r1 / wavelength).reshape(24, 30).astype(np.complex64)
        s2 = np.exp(-4j * math.pi * r2 / wavelength).reshape(24, 30).astype(np.complex64)
        a = F.write(os.path.join(self.dir, "ref.tif"), {"VV": s1}, ref_meta)
        b = F.write(os.path.join(self.dir, "sec.tif"), {"VV": s2}, sec_meta)
        return a, b, s1, s2

    def test_coherence_of_a_scene_with_itself_is_one_and_of_noise_is_low(self):
        a, b, s1, _ = self._pair()
        same = F.write(os.path.join(self.dir, "same.tif"), {"VV": s1 * np.exp(0.7j)}, F.meta(rows=24, cols=30))
        out = os.path.join(self.dir, "coh.tif")
        arcpy.ia.ComputeCoherence(a, same, out, range_window_size=5, azimuth_window_size=3)
        np.testing.assert_allclose(_read(out).bands["VV"], 1.0, atol=1e-5)
        rng = np.random.default_rng(0)
        noise = (rng.normal(size=s1.shape) + 1j * rng.normal(size=s1.shape)).astype(np.complex64)
        other = F.write(os.path.join(self.dir, "noise.tif"), {"VV": noise}, F.meta(rows=24, cols=30))
        arcpy.ia.ComputeCoherence(a, other, out, range_window_size=10, azimuth_window_size=10)
        self.assertLess(float(np.nanmean(_read(out).bands["VV"])), 0.4)

    def test_the_flat_earth_phase_is_removed_from_a_flat_earth_scene(self):
        a, b, s1, s2 = self._pair()
        raw = np.angle(s1 * np.conj(s2))
        self.assertGreater(np.ptp(np.unwrap(raw[0])), 1.0)  # the raw interferogram has fringes
        out = os.path.join(self.dir, "ifg.tif")
        flat = os.path.join(self.dir, "flat.tif")
        result = arcpy.ia.GenerateInterferogram(a, b, out, out_flattened_interferogram=flat)
        self.assertEqual(result[0], out)
        phase = np.angle(_read(out).bands["VV"].astype(complex))
        # What remains is the cubic Hermite interpolation of an orbit sampled every 10 s, 0.2 mm of range on a
        # circular orbit (h⁴/384 · Rs·ω⁴): 0.05 rad, and the same everywhere — the fringes are gone.
        self.assertLess(np.max(np.abs(phase)), 0.06)
        self.assertLess(np.ptp(phase), 0.005)
        self.assertTrue(os.path.exists(flat))

    def test_topographic_phase_on_a_flat_dem_is_the_flat_earth_phase(self):
        a, b, _, _ = self._pair()
        west, south, east, north = F.footprint(F.meta(rows=24, cols=30))
        dem = F.dem(os.path.join(self.dir, "dem.tif"), west, south, east, north, 0.0)
        out = os.path.join(self.dir, "topo.tif")
        arcpy.ia.GenerateInterferogram(a, b, out, None, "TOPO", dem, "NONE")
        self.assertLess(np.max(np.abs(np.angle(_read(out).bands["VV"].astype(complex)))), 0.06)
        with self.assertRaises(NotBuiltError):
            arcpy.ia.GenerateInterferogram(a, b, out, None, "TOPO", dem)  # GEOID needs a geoid grid

    def test_unwrapping_recovers_a_ramp_of_several_cycles(self):
        n, m = 40, 50
        rows, cols = np.mgrid[0:n, 0:m]
        ramp = 0.35 * cols + 0.2 * rows + 0.002 * cols * rows
        z = np.exp(1j * ramp).astype(np.complex64)
        path = F.write(os.path.join(self.dir, "w.tif"), {"VV": z}, F.meta(rows=n, cols=m))
        out = os.path.join(self.dir, "u.tif")
        arcpy.ia.UnwrapPhase(path, out)
        u = _read(out).bands["VV"].astype(float)
        difference = u - ramp
        self.assertLess(np.ptp(difference), 1e-3)
        self.assertAlmostEqual(difference.mean() / (2 * math.pi), round(difference.mean() / (2 * math.pi)), delta=1e-3)

    def test_unwrapping_masks_incoherent_cells(self):
        rng = np.random.default_rng(2)
        n, m = 30, 30
        phase = 0.3 * np.mgrid[0:n, 0:m][1].astype(float)
        phase[10:20, 10:20] = rng.uniform(-math.pi, math.pi, (10, 10))
        path = F.write(os.path.join(self.dir, "w2.tif"), {"VV": np.exp(1j * phase).astype(np.complex64)},
                       F.meta(rows=n, cols=m))
        out = os.path.join(self.dir, "u2.tif")
        arcpy.ia.UnwrapPhase(path, out, effective_coherence_threshold=0.6)
        u = _read(out).bands["VV"]
        self.assertTrue(np.isnan(u[15, 15]))
        self.assertFalse(np.isnan(u[2, 2]))

    def test_goldstein_smooths_noisy_phase_and_alpha_0_does_nothing(self):
        rng = np.random.default_rng(4)
        n = 64
        clean = 0.2 * np.mgrid[0:n, 0:n][1]
        noisy = np.exp(1j * (clean + rng.normal(0, 0.8, (n, n)))).astype(np.complex64)
        path = F.write(os.path.join(self.dir, "n.tif"), {"VV": noisy}, F.meta(rows=n, cols=n))
        out = os.path.join(self.dir, "g.tif")
        arcpy.ia.ApplyComplexDataFilter(path, out, alpha=1.0, inner_window_size=8, outer_window_size=32)
        filtered = _read(out).bands["VV"].astype(complex)
        before = np.abs(np.angle(noisy * np.exp(-1j * clean))).mean()
        after = np.abs(np.angle(filtered * np.exp(-1j * clean))).mean()
        self.assertLess(after, 0.6 * before)
        np.testing.assert_allclose(np.abs(filtered), np.abs(noisy), rtol=1e-5)
        arcpy.ia.ApplyComplexDataFilter(path, out, alpha=0)
        np.testing.assert_allclose(_read(out).bands["VV"], noisy, atol=1e-5)

    def test_geometric_coregistration_puts_the_secondary_on_the_reference_grid(self):
        # The secondary starts 3 lines earlier on the same orbit: its row r is the reference's row r − 3. Its values
        # are the reference row they belong to, so the coregistered secondary reads its own row number.
        ref_meta = F.meta(rows=30, cols=30)
        sec_meta = F.meta(rows=40, cols=30, first=F.T1 - 3 * F.DT)
        sec_values = (np.arange(40.0)[:, None] - 3 + 0 * np.arange(30.0)[None, :]).astype(np.complex64)
        a = F.write(os.path.join(self.dir, "r.tif"), {"VV": np.ones((30, 30), np.complex64)}, ref_meta)
        b = F.write(os.path.join(self.dir, "s.tif"), {"VV": sec_values}, sec_meta)
        out = os.path.join(self.dir, "co.tif")
        arcpy.ia.ApplyCoregistration(a, b, out, geoid="NONE")
        got = _read(out).bands["VV"].real
        np.testing.assert_allclose(got[:25], np.arange(25.0)[:, None] + 0 * got[:25], atol=0.01)

    def test_tops_data_is_refused_for_coregistration(self):
        m = F.meta(rows=10, cols=10, mode="IW")
        a = F.write(os.path.join(self.dir, "t.tif"), {"VV": np.ones((10, 10), np.complex64)}, m)
        with self.assertRaises(NotBuiltError):
            arcpy.ia.ApplyCoregistration(a, a, os.path.join(self.dir, "tc.tif"))


class Terrain(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def test_terrain_correction_places_each_cell_where_the_independent_solution_does(self):
        m = F.meta()
        cols = np.tile(np.arange(F.COLS, dtype=float), (F.ROWS, 1))
        rows = np.tile(np.arange(F.ROWS, dtype=float)[:, None], (1, F.COLS))
        path = F.write(os.path.join(self.dir, "rc.tif"), {"COL": cols.astype(np.float32), "ROW": rows.astype(np.float32)}, m)
        out = os.path.join(self.dir, "gtc.tif")
        arcpy.ia.ApplyGeometricTerrainCorrection(path, out, "COL;ROW")
        sar = _read(out)
        self.assertFalse(sar.radar)
        self.assertEqual(sar.srid, 4326)
        t = sar.transform
        found = np.argwhere(~np.isnan(sar.bands["COL"]))
        checked = 0
        for i, j in found[:: max(1, len(found) // 12)]:
            lon, lat = t.c + (j + 0.5) * t.a, t.f + (i + 0.5) * t.e
            p = _sar.ecef(lon, lat, 0.0)
            # the independent answer: the time at which (P − S)·V = 0, and the range then
            from scipy.optimize import brentq
            t0 = brentq(lambda tt: float(np.dot(p - F.state(tt)[0], F.state(tt)[1])), F.T1 - 1, F.T1 + 1, xtol=1e-12)
            r = np.linalg.norm(p - F.state(t0)[0])
            row = (t0 - F.T1) / F.DT
            col = (2 * r / _sar.C - F.TAU0) * F.FS
            if 0.5 < row < F.ROWS - 1.5 and 0.5 < col < F.COLS - 1.5:
                self.assertAlmostEqual(float(sar.bands["COL"][i, j]), col, delta=0.01)
                self.assertAlmostEqual(float(sar.bands["ROW"][i, j]), row, delta=0.01)
                checked += 1
        self.assertGreater(checked, 5)

    def _beta(self):
        m = F.meta(values="intensity")
        m["calibration"] = "beta0"
        return F.write(os.path.join(self.dir, "beta.tif"), {"VV": np.ones((F.ROWS, F.COLS), np.float32)}, m), m

    def test_flattening_a_flat_scene_gives_beta_times_tan_and_sin_of_incidence(self):
        # On flat terrain Small's areas reduce to the textbook relations: γ0 = β0·tan θ and σ0 = β0·sin θ.
        path, m = self._beta()
        west, south, east, north = F.footprint(m, margin=0.02)
        dem = F.dem(os.path.join(self.dir, "flat.tif"), west, south, east, north, 0.0, cell=0.0001)
        theta = math.radians(m["incidence_angle_mid"])
        gamma = os.path.join(self.dir, "g0.tif")
        area = os.path.join(self.dir, "area.tif")
        arcpy.ia.ApplyRadiometricTerrainFlattening(path, gamma, dem, "NONE", out_scattering_area=area)
        g = _read(gamma).bands["VV"][5:-5, 5:-5]
        self.assertAlmostEqual(float(np.nanmedian(g)) / math.tan(theta), 1.0, delta=0.03)
        self.assertTrue(os.path.exists(area))
        sigma = os.path.join(self.dir, "s0.tif")
        arcpy.ia.ApplyRadiometricTerrainFlattening(path, sigma, dem, "NONE", calibration_type="SIGMA_NOUGHT")
        s = _read(sigma).bands["VV"][5:-5, 5:-5]
        self.assertAlmostEqual(float(np.nanmedian(s)) / math.sin(theta), 1.0, delta=0.03)

    def test_flattening_needs_beta_nought_and_refuses_the_distortion_outputs(self):
        path, m = self._beta()
        west, south, east, north = F.footprint(m)
        dem = F.dem(os.path.join(self.dir, "flat2.tif"), west, south, east, north, 0.0)
        with self.assertRaises(NotBuiltError):
            arcpy.ia.ApplyRadiometricTerrainFlattening(path, os.path.join(self.dir, "x.tif"), dem, "NONE",
                                                       out_geometric_distortion=os.path.join(self.dir, "gd.tif"))
        plain = F.write(os.path.join(self.dir, "plain.tif"), {"VV": np.ones((F.ROWS, F.COLS), np.float32)},
                        F.meta(values="intensity"))
        with self.assertRaises(ExecuteError):
            arcpy.ia.ApplyRadiometricTerrainFlattening(plain, os.path.join(self.dir, "y.tif"), dem, "NONE")

    def test_the_rtc_chain_runs_a_safe_product_to_gamma_nought_on_a_map(self):
        dn = np.full((F.ROWS, F.COLS), 100, np.uint16)
        name = F.safe(self.dir, "GRD", {"VV": dn}, beta=(300.0, 300.0), noise_range=(20.0, 20.0))
        m = _read(name).meta
        west, south, east, north = F.footprint(m, margin=0.02)
        dem = F.dem(os.path.join(self.dir, "flat3.tif"), west, south, east, north, 0.0, cell=0.0001)
        out = os.path.join(self.dir, "rtc.tif")
        keep = os.path.join(self.dir, "levels")
        with arcpy.EnvManager(scratchWorkspace=self.dir):
            result = arcpy.ia.GenerateRadiometricTerrainCorrectedData(name, out, dem, "NONE",
                                                                      processing_level="TNR;CalB0", out_folder=keep)
        self.assertFalse([n for n in os.listdir(self.dir) if n.startswith("rtc_")])  # the working files are gone
        self.assertEqual(result[1], keep)
        self.assertTrue(os.path.exists(os.path.join(keep, "TNR.tif")))
        sar = _read(out)
        self.assertFalse(sar.radar)
        theta = math.radians(m["incidence_angle_mid"])
        expected = (100 ** 2 - 20) / 300 ** 2 * math.tan(theta)
        self.assertAlmostEqual(float(np.nanmedian(sar.bands["VV"])) / expected, 1.0, delta=0.05)
        with self.assertRaises(NotBuiltError):
            arcpy.ia.GenerateRadiometricTerrainCorrectedData(name, out, dem, "NONE", username="someone")

    def test_masking_with_features_and_with_a_dem(self):
        from grapy import _gpkg, da
        path = F.write(os.path.join(self.dir, "m.tif"), {"VV": np.ones((F.ROWS, F.COLS), np.float32)},
                       F.meta(values="intensity"))
        lon, lat = _sar.lonlat_grid(F.meta(), (F.ROWS, F.COLS))
        cut = float(np.median(lon))
        mask = os.path.join(self.dir, "mask.gpkg")
        _gpkg.create(mask + "/m", "Polygon", 4326, [])
        with da.InsertCursor(mask + "/m", ["SHAPE@"]) as rows:
            rows.insertRow([arcpy.Polygon(arcpy.Array([arcpy.Point(cut, -1), arcpy.Point(cut + 1, -1),
                                                       arcpy.Point(cut + 1, 1), arcpy.Point(cut, 1)]),
                                          arcpy.SpatialReference(4326))])
        out = os.path.join(self.dir, "masked.tif")
        arcpy.ia.GenerateMaskedSARRaster(path, out, None, mask + "/m", "INSIDE")
        band = _read(out).bands["VV"]
        np.testing.assert_array_equal(np.isnan(band), lon > cut)
        west, south, east, north = F.footprint(F.meta())
        dem = F.dem(os.path.join(self.dir, "coast.tif"), west, south, east, north,
                    lambda x, y: np.where(x > cut, -5.0, 20.0))
        arcpy.ia.GenerateMaskedSARRaster(path, out, in_dem_raster=dem, geoid="NONE")
        band = _read(out).bands["VV"]
        self.assertTrue(np.isnan(band[lon > cut + 0.0005]).all())
        self.assertFalse(np.isnan(band[lon < cut - 0.0005]).any())

    def test_an_orbit_file_replaces_the_orbit(self):
        m = F.meta()
        path = F.write(os.path.join(self.dir, "o.tif"), {"VV": np.ones((F.ROWS, F.COLS), np.complex64)}, m)
        moved = F.vectors((0.0, 0.0, 50.0))
        osvs = "".join(f"<OSV><UTC>UTC={_sar.iso(v[0])}</UTC><X unit=\"m\">{v[1]!r}</X><Y unit=\"m\">{v[2]!r}</Y>"
                       f"<Z unit=\"m\">{v[3]!r}</Z><VX>{v[4]!r}</VX><VY>{v[5]!r}</VY><VZ>{v[6]!r}</VZ></OSV>"
                       for v in moved)
        eof = os.path.join(self.dir, "S1A_OPER_AUX_POEORB_TEST.EOF")
        with open(eof, "w") as f:
            f.write("<Earth_Explorer_File><Data_Block><List_of_OSVs>" + osvs + "</List_of_OSVs></Data_Block>"
                    "</Earth_Explorer_File>")
        arcpy.ia.ApplyOrbitCorrection(path, "S1A_OPER_AUX_POEORB_TEST.EOF", self.dir)
        meta = json.load(open(path + _sar.SIDE))
        self.assertEqual(meta["orbit_source"], "S1A_OPER_AUX_POEORB_TEST.EOF")
        self.assertAlmostEqual(meta["orbit"][0][3], moved[0][3])
        short = os.path.join(self.dir, "short.EOF")
        with open(short, "w") as f:
            f.write("<Earth_Explorer_File><Data_Block><List_of_OSVs>" + osvs[: osvs.index("</OSV>") + 6] * 1
                    + "</List_of_OSVs></Data_Block></Earth_Explorer_File>")
        with self.assertRaises(ExecuteError):
            arcpy.ia.ApplyOrbitCorrection(path, short)


class Ocean(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()

    def test_cmod5n_reproduces_the_published_fit_to_cmod5(self):
        # Hersbach (2008), ECMWF Tech. Memo. 554, Figures 4–6, over 1–25 m/s and 17–65°: B0 of CMOD5.N at v + b
        # equals B0 of CMOD5 at v for b between 0.32 and 0.79 m/s; B1 and B2 of CMOD5.N at v + 0.7 differ from
        # CMOD5's at v by −0.003..0.002 and −0.008..0.005; CMOD5's B1 lies in −0.047..0.089 and B2 in 0.028..0.473.
        v = np.arange(1.0, 25.5, 0.5)[:, None]
        theta = np.arange(17.0, 66.0, 1.0)[None, :]
        b0, b1, b2 = _sartools.cmod_terms(v, theta, _sartools.CMOD5)
        n0, n1, n2 = _sartools.cmod_terms(v + 0.7, theta, _sartools.CMOD5N)
        self.assertGreaterEqual(b1.min(), -0.0475)
        self.assertLessEqual(b1.max(), 0.0895)
        self.assertGreaterEqual(b2.min(), 0.0275)
        self.assertLessEqual(b2.max(), 0.4735)
        self.assertGreaterEqual((n1 - b1).min(), -0.0035)
        self.assertLessEqual((n1 - b1).max(), 0.0025)
        self.assertGreaterEqual((n2 - b2).min(), -0.0085)
        self.assertLessEqual((n2 - b2).max(), 0.0055)
        db = 10 * np.log10(b0)
        self.assertGreaterEqual(db.min(), -31.7)
        self.assertLessEqual(db.max(), 2.95)
        # the speed shift b: B0N(v + b) = B0(v)
        shift = np.empty(b0.shape)
        for i in range(b0.shape[0]):
            for j in range(b0.shape[1]):
                lo, hi = 0.0, 2.0
                for _ in range(50):
                    mid = (lo + hi) / 2
                    if _sartools.cmod_terms(v[i, 0] + mid, theta[0, j])[0] < b0[i, j]:
                        lo = mid
                    else:
                        hi = mid
                shift[i, j] = lo
        self.assertGreaterEqual(shift.min(), 0.31)
        self.assertLessEqual(shift.max(), 0.80)

    def test_the_inversion_recovers_the_speed_it_was_given(self):
        speeds = np.array([3.0, 8.0, 15.0, 25.0])
        sigma = _sartools.cmod5n(speeds, 45.0, 35.0)
        np.testing.assert_allclose(_sartools.invert_cmod5n(sigma, 45.0, 35.0), speeds, atol=1e-3)

    def test_ocean_winds_from_streaks_and_cmod5n(self):
        m = F.meta(rows=60, cols=80, values="intensity")
        m["calibration"] = "sigma0"
        lon, lat = _sar.lonlat_grid(m, (60, 80))
        east = (lon - lon.mean()) * 111320 * math.cos(math.radians(lat.mean()))
        north = (lat - lat.mean()) * 110574
        frm = 30.0  # the wind comes from 30°
        across = east * math.cos(math.radians(frm)) - north * math.sin(math.radians(frm))
        _, row_bearing = _sartools._bearings(lon, lat)
        n, mm = lon.shape
        t = _sar.row_time(m, np.arange(n))
        theta = _sar.incidence(m, np.repeat(t, mm), np.tile(np.arange(mm, dtype=float), n)).reshape(n, mm)
        sigma = _sartools.cmod5n(10.0, frm - (row_bearing + 90.0), theta) * (1 + 0.15 * np.sin(2 * math.pi * across / 60))
        path = F.write(os.path.join(self.dir, "s0.tif"), {"VV": sigma.astype(np.float32)}, m)
        out = os.path.join(self.dir, "wind.tif")
        arcpy.ia.ExtractOceanWinds(path, out)
        sar = _read(out)
        u, v = sar.bands["U"][10:-10, 10:-10], sar.bands["V"][10:-10, 10:-10]
        speed = np.hypot(u, v)
        self.assertAlmostEqual(float(np.median(speed)), 10.0, delta=1.0)
        blows_to = math.degrees(math.atan2(float(np.median(u)), float(np.median(v)))) % 360
        self.assertAlmostEqual(blows_to, frm + 180.0, delta=5.0)

    def _map(self, values):
        from rasterio.transform import from_origin
        m = F.meta(values="intensity")
        m["geometry"] = "map"
        cell = 10 / 111320.0
        return _sar.write(os.path.join(self.dir, f"map{len(os.listdir(self.dir))}.tif"), {"VV": values}, m,
                          from_origin(30.0, 0.01, cell, cell), 4326)

    def test_a_bright_ship_is_found_and_measured(self):
        rng = np.random.default_rng(8)
        z = rng.gamma(4, 0.25, (200, 200)).astype(np.float32)
        z[100:103, 60:70] = 40.0  # 30 m wide, 100 m long
        out = os.path.join(self.dir, "ships.gpkg")
        arcpy.ia.DetectBrightOceanObjects(self._map(z), out + "/ships")
        from grapy import da
        with da.SearchCursor(out + "/ships", ["Length", "Width", "PixelCount"]) as rows:
            found = list(rows)
        self.assertEqual(len(found), 1)
        length, width, count = found[0]
        self.assertAlmostEqual(length, 100, delta=4)
        self.assertAlmostEqual(width, 30, delta=4)
        self.assertEqual(count, 30)

    def test_a_dark_slick_is_found_when_large_enough(self):
        rng = np.random.default_rng(9)
        z = rng.gamma(8, 0.125, (200, 200)).astype(np.float32)
        z[50:80, 50:90] *= 0.1  # 300 m × 400 m
        z[150:152, 150:152] *= 0.1  # 20 m × 20 m
        out = os.path.join(self.dir, "dark.tif")
        arcpy.ia.DetectDarkOceanAreas(self._map(z), out, "10000 SquareMeters")
        band = _read(out).bands["Dark"]
        self.assertGreater(band[55:75, 55:85].mean(), 0.95)
        self.assertEqual(band[150, 150], 0)
        self.assertLess(band[0:40, 0:40].mean(), 0.01)

    def test_sar_indices_by_their_formulas(self):
        values = {"HH": np.full((2, 2), 0.4, np.float32), "HV": np.full((2, 2), 0.1, np.float32),
                  "VH": np.full((2, 2), 0.1, np.float32), "VV": np.full((2, 2), 0.2, np.float32)}
        path = F.write(os.path.join(self.dir, "quad.tif"), values, F.meta(rows=2, cols=2, values="intensity"))
        out = os.path.join(self.dir, "idx.tif")
        for index, pair, expected in (("RVI", "HH_HV_VH_VV", 8 * 0.1 / (0.4 + 0.2 + 0.2)),
                                      ("RVI", "VV_VH", 4 * 0.1 / 0.3), ("RFDI", "HH_HV", 0.3 / 0.5),
                                      ("CSI", None, 0.2 / 0.6)):
            with self.subTest(index=index, pair=pair):
                arcpy.ia.ComputeSARIndices(path, out, index, pair)
                self.assertAlmostEqual(float(_read(out).bands[index][0, 0]), expected, delta=1e-6)

    def test_a_multitemporal_composite_stacks_three_dates(self):
        a = self._map(np.full((5, 5), 1.0, np.float32))
        b = self._map(np.full((5, 5), 2.0, np.float32))
        c = self._map(np.full((5, 5), 3.0, np.float32))
        out = os.path.join(self.dir, "mtc.tif")
        arcpy.ia.GenerateMultitemporalCoherence(a, b, c, out)
        sar = _read(out)
        self.assertEqual(sar.names, ["Red", "Green", "Blue"])
        self.assertEqual(float(sar.bands["Blue"][0, 0]), 3.0)


class Registered(unittest.TestCase):
    def test_the_radar_names_are_built_equivalent_or_refused_with_their_own_reason(self):
        for name in ("ApplyRadiometricCalibration", "RemoveThermalNoise", "Multilook", "ConvertSARUnits",
                     "GenerateMaskedSARRaster", "GenerateMultitemporalCoherence", "ApplyOrbitCorrection"):
            self.assertFalse(hasattr(getattr(arcpy.ia, name), "__grapy_equivalent__"), name)
        for name in ("Deburst", "Despeckle", "ComputeCoherence", "GenerateInterferogram", "ApplyComplexDataFilter",
                     "UnwrapPhase", "ComputeSARIndices", "DetectBrightOceanObjects", "DetectDarkOceanAreas",
                     "ExtractOceanWinds", "ApplyGeometricTerrainCorrection", "ApplyRadiometricTerrainFlattening",
                     "ApplyCoregistration", "GenerateRadiometricTerrainCorrectedData", "RadarCalibration",
                     "S1RadiometricCalibration", "S1ThermalNoiseRemoval"):
            self.assertTrue(getattr(arcpy.ia, name).__grapy_equivalent__, name)
        for name, words in (("DownloadOrbitFile", "no network"), ("ExtractVideoFramesToImages", "no video"),
                            ("VideoMultiplexer", "no video"), ("VideoMetadataToFeatureClass", "no video"),
                            ("GenerateVideoMetadataStationary", "no video")):
            with self.assertRaises(NotBuiltError) as said:
                getattr(arcpy.ia, name)()
            self.assertIn(words, str(said.exception))
            self.assertNotIn("not hold yet", str(said.exception))

    def test_radar_calibration_answers_a_raster(self):
        d = tempfile.mkdtemp()
        dn = np.full((F.ROWS, F.COLS), 100, np.uint16)
        name = F.safe(d, "GRD", {"VV": dn})
        with arcpy.EnvManager(scratchWorkspace=d):
            out = arcpy.ia.RadarCalibration(name, "SIGMA_NOUGHT")
        self.assertTrue(out.catalogPath.startswith(d))
        self.assertIsInstance(out, arcpy.Raster)
        self.assertAlmostEqual(float(out._array[0, 0]), 100 ** 2 / 400 ** 2, delta=1e-6)

    def test_the_sentinel_1_raster_functions_answer_as_their_tools_do(self):
        # The Sentinel-1 Radiometric Calibration and Thermal Noise Removal raster function pages (read 2026-10-31).
        d = tempfile.mkdtemp()
        dn = np.full((F.ROWS, F.COLS), 100, np.uint16)
        name = F.safe(d, "GRD", {"VV": dn})
        with arcpy.EnvManager(scratchWorkspace=d):
            calibrated = arcpy.ia.S1RadiometricCalibration(name, "SIGMA_NOUGHT")
            self.assertAlmostEqual(float(calibrated._array[0, 0]), 100 ** 2 / 400 ** 2, delta=1e-6)
            denoised = arcpy.ia.S1ThermalNoiseRemoval(name)
        tool = os.path.join(d, "tool.tif")
        arcpy.ia.RemoveThermalNoise(name, tool)
        self.assertIsInstance(denoised, arcpy.Raster)
        self.assertTrue(np.allclose(denoised._array, arcpy.Raster(tool)._array))
        with self.assertRaises(arcpy.ExecuteError):
            arcpy.ia.S1RadiometricCalibration(F.safe(tempfile.mkdtemp(), "GRD", {"VV": dn}, ipf="002.34"))


if __name__ == "__main__":
    unittest.main()
