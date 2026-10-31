# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> -I adr243-solar-compare.py <scratch>
"""Compare pro_solar/*.tif with grapy_solar/*.tif cell by cell."""
import numpy as np, rasterio, sys, os
d = sys.argv[1]
for n in ("glb", "dir", "dif", "dur"):
    a = rasterio.open(os.path.join(d, "pro_solar", n + ".tif")); b = rasterio.open(os.path.join(d, "grapy_solar", n + ".tif"))
    pa, pb = a.read(1, masked=True).astype(float), b.read(1, masked=True).astype(float)
    assert a.transform == b.transform, (a.transform, b.transform)
    diff = pb - pa
    inner = diff[1:-1, 1:-1]
    rel = np.abs(diff) / np.maximum(np.abs(pa), 1e-9)
    print(f"{n}: dtype pro={a.dtypes[0]} grapy={b.dtypes[0]} pro mean {pa.mean():.3f} grapy mean {pb.mean():.3f} | "
          f"max|d| {np.abs(diff).max():.3f} mean|d| {np.abs(diff).mean():.3f} mean d {diff.mean():+.3f} "
          f"max rel {rel.max():.4f} | interior max|d| {np.abs(inner).max():.3f} mean|d| {np.abs(inner).mean():.3f}")
    i = np.unravel_index(np.argmax(np.abs(diff)), diff.shape); print("   worst at", i, pa[i], pb[i])
import json
p = json.load(open(os.path.join(d, "pro_solar", "points.json"))); g = json.load(open(os.path.join(d, "grapy_solar", "points.json")))
for n in p:
    a, b = np.array(p[n]), np.array(g[n])
    print(f"points {n}: max|d| {np.abs(b-a).max():.3f} mean|d| {np.abs(b-a).mean():.3f} max rel {(np.abs(b-a)/a).max():.4f}")
