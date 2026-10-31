# A comparison with ArcGIS Pro 3.7 (ADR-233, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> -I adr233-kriging-compare.py <scratch>
"""Prediction and variance cell by cell. Run kriging_pro.py without arguments (Pro's own grid) and kriging_grapy.py;
since grapy lays out a point interpolator's grid as Pro does, the two grids are the same."""
import numpy as np, rasterio, os, sys
d = sys.argv[1]
for n in ("pred", "var"):
    a, b = rasterio.open(os.path.join(d, "pro_k", n + ".tif")), rasterio.open(os.path.join(d, "grapy_k", n + ".tif"))
    assert a.transform == b.transform and a.shape == b.shape, (a.transform, a.shape, b.transform, b.shape)
    diff = np.abs(a.read(1).astype(float) - b.read(1).astype(float))
    print(f"{n}: {diff.size} cells, max|d| {diff.max():.3g} mean|d| {diff.mean():.3g}, within 1e-3 {int((diff < 1e-3).sum())}")
