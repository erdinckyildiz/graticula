"""grapy's Sentinel-1 arithmetic and geometry on a real IW GRD against ArcGIS Pro's (ADR-237 condition 3, GRD half).

The scene is 26,325 x 21,331 and grapy reads a band whole, past ADR-223's memory limit (s1_grapy_memory.py measures
that), so grapy's own functions are run here on windows of it: the noise and calibration LUTs (_sar.noise, _sar.lut)
on the measurement's digital numbers, and range-Doppler back-geocoding (_sar.backgeocode) of the cells of Pro's
terrain-corrected output, sampled in Pro's own radar-geometry sigma nought.

s1_compare.py <worktree> <SAFE folder> <Pro's out folder>"""
import glob
import os
import sys
import warnings
import xml.etree.ElementTree as ET

import numpy as np

sys.path.insert(0, os.path.join(sys.argv[1], "src", "grapy"))
import rasterio  # noqa: E402
from rasterio.windows import Window  # noqa: E402
from grapy import _sar, _sartools  # noqa: E402

warnings.simplefilter("ignore")
safe, pro = sys.argv[2], sys.argv[3]
xml = next(x for x in sorted(glob.glob(os.path.join(safe, "annotation", "*.xml"))) if "-vv-" in x)
name = os.path.splitext(os.path.basename(xml))[0]
tiff = os.path.join(safe, "measurement", name + ".tiff")
with rasterio.open(tiff) as m:
    shape = (m.height, m.width)
meta = _sar._annotation(ET.parse(xml).getroot(), "GRD", shape)
cal = _sar._calibration(os.path.join(safe, "annotation", "calibration", "calibration-" + name + ".xml"))
noise = _sar._noise(os.path.join(safe, "annotation", "calibration", "noise-" + name + ".xml"))
print("scene", shape, "IPF", _sar._ipf_version(safe))

# ---- radiometry, on windows across the swath
rng = np.random.default_rng(237)
ONLY_RADIOMETRY = os.environ.get("ONLY_RADIOMETRY")
windows = [(int(r), int(c)) for r, c in zip(rng.integers(0, shape[0] - 512, 6), rng.integers(0, shape[1] - 512, 6))]
with rasterio.open(tiff) as m, rasterio.open(os.path.join(pro, "noise.tif")) as pn, \
        rasterio.open(os.path.join(pro, "sigma0.tif")) as ps:
    print("Pro noise", pn.width, pn.height, "sigma0", ps.width, ps.height)
    for r0, c0 in windows:
        w = Window(c0, r0, 512, 512)
        dn = m.read(1, window=w).astype(np.float64)
        lines, pixels = np.arange(r0, r0 + 512), np.arange(c0, c0 + 512)
        eta = _sar.noise(noise, lines, pixels)
        power = np.maximum(dn ** 2 - eta, _sartools.NOISE_FLOOR)   # grapy's RemoveThermalNoise, on a window
        a = _sar.lut(cal, "sigma0", lines, pixels)
        floored = power == _sartools.NOISE_FLOOR   # grapy's ApplyRadiometricCalibration on noise-removed data
        sigma0 = np.where(floored, _sartools.NOISE_FLOOR * _sartools.FLOOR_CALIBRATED, power) / a ** 2
        p_noise = pn.read(1, window=w).astype(np.float64)
        p_sigma = ps.read(1, window=w).astype(np.float64)
        valid = np.isfinite(p_sigma)
        rel_n = np.abs(p_noise[valid] - power[valid]) / np.maximum(power[valid], 1e-12)
        db = 10 * np.log10(p_sigma[valid]) - 10 * np.log10(sigma0[valid])
        zero_g, zero_p = int((sigma0 == 0).sum()), int((p_sigma <= 0).sum())
        print(f"window line {r0} pixel {c0}: noise-removed power rel. diff median {np.median(rel_n):.2e} max "
              f"{rel_n.max():.2e}; sigma0 dB diff mean {db.mean():+.4f} max |.| {np.abs(db).max():.4f}; "
              f"floored grapy {int((power == _sartools.NOISE_FLOOR).sum())} Pro {int((p_noise == np.float32(1e-5)).sum())}")

# ---- geometry: Pro's terrain-corrected cells, back-geocoded by grapy, sampled in Pro's radar sigma0
if ONLY_RADIOMETRY:
    sys.exit(0)
tie = _sartools._tie_heights(meta)
with rasterio.open(os.path.join(pro, "gtc.tif")) as g, rasterio.open(os.path.join(pro, "sigma0.tif")) as ps:
    print("Pro GTC", g.width, g.height, g.crs, g.res)
    radar = None
    pick = rng.integers(2000, g.height - 2000, 40), rng.integers(2000, g.width - 2000, 40)
    diffs, all_db, used = [], [], 0
    for r0, c0 in zip(*pick):
        if used == 8:
            break
        w = Window(int(c0), int(r0), 128, 128)
        value = g.read(1, window=w).astype(np.float64).ravel()
        rows, cols = np.mgrid[r0:r0 + 128, c0:c0 + 128]
        lon, lat = rasterio.transform.xy(g.transform, rows.ravel(), cols.ravel())
        lon, lat = np.asarray(lon), np.asarray(lat)
        h = tie(lon, lat)
        rr, cc, _ = _sar.backgeocode(meta, _sar.ecef(lon, lat, h))
        inside = (rr >= 8) & (rr < shape[0] - 9) & (cc >= 8) & (cc < shape[1] - 9) & np.isfinite(value) & (value > 0)
        if inside.sum() < 0.9 * value.size:
            continue
        used += 1
        r_lo, r_hi = int(np.floor(rr[inside].min())) - 8, int(np.ceil(rr[inside].max())) + 9
        c_lo, c_hi = int(np.floor(cc[inside].min())) - 8, int(np.ceil(cc[inside].max())) + 9
        block = ps.read(1, window=Window(c_lo, r_lo, c_hi - c_lo, r_hi - r_lo)).astype(np.float64)
        target = 10 * np.log10(value[inside])

        def at(dr, dc):
            v = _sar.bilinear(block, rr[inside] - r_lo + dr, cc[inside] - c_lo + dc)
            return 10 * np.log10(np.maximum(v, 1e-10))
        db = target - at(0, 0)
        all_db.append(db)
        best = max(((np.corrcoef(target, at(dr, dc))[0, 1], dr, dc)
                    for dr in np.arange(-6, 6.01, 0.25) for dc in np.arange(-6, 6.01, 0.25)))
        diffs.append(best)
        print(f"GTC cells at row {r0} col {c0}: {inside.sum()} cells, at grapy's position dB diff mean {db.mean():+.4f} "
              f"median |.| {np.median(np.abs(db)):.4f}, correlation {np.corrcoef(target, at(0, 0))[0, 1]:.4f}; "
              f"best correlation {best[0]:.4f} at {best[1]:+.2f} lines {best[2]:+.2f} pixels")
    all_db = np.concatenate(all_db)
    print(f"GTC overall: {len(all_db)} cells, mean dB diff {all_db.mean():+.4f}, median |dB| "
          f"{np.median(np.abs(all_db)):.4f}; best shifts (lines, pixels): "
          f"{[(float(d[1]), float(d[2])) for d in diffs]}")
