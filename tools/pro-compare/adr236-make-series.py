# A comparison with ArcGIS Pro 3.7 (ADR-236, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr236-make-series.py <scratch> <worktree>
"""ADR-236 condition 3 input: one netCDF-CF series written by grapy's own writer. 20 x 20 cells at 30 m in UTM 36N,
one variable 'ndvi' every 16 days from 2014-01-01 to 2022-12-31: a seasonal cycle with a per-cell trend and noise,
and a drop of 0.25 on 2018-06-15 in the right half (for CCDC and LandTrendr)."""
import sys, numpy as np
sys.path.insert(0, sys.argv[2] + "/src/grapy")
from grapy import _mdim
from grapy._raster import _Grid
n = 20
t = np.arange(np.datetime64("2014-01-01"), np.datetime64("2023-01-01"), np.timedelta64(16, "D"))
days = (t - t[0]).astype(float)
rng = np.random.default_rng(5)
yy, xx = np.mgrid[0:n, 0:n]
trend = (yy - n / 2) * 2e-6                         # per day, from -2e-5 to +1.8e-5
amp = 0.15 + 0.01 * xx
phase = 0.3 + 0.02 * yy
data = np.empty((t.size, n, n))
for k, d in enumerate(days):
    data[k] = 0.5 + trend * d + amp * np.cos(2 * np.pi * d / 365.25 - phase) + rng.normal(0, 0.02, (n, n))
    if t[k] >= np.datetime64("2018-06-15"):
        data[k, :, n // 2:] -= 0.25
grid = _Grid(500000.0, 4430000.0, 30.0, 30.0, n, n, 32636)
coords = [{"StdTime": v} for v in t]
var = _mdim.Variable("ndvi", ["StdTime"], coords, data[:, None].astype(np.float32), np.zeros((t.size, 1, n, n), bool))
_mdim.write(_mdim.Cube(grid, [var]), sys.argv[1] + "/series.nc")
print(t.size, "slices")
