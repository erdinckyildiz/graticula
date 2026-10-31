"""Compare two folders of GeoTIFFs case by case: NoData agreement, max and mean absolute difference.

    <grapy python> compare_rasters.py <worktree> <pro folder> <grapy folder> [--show name ...]
"""
import glob
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(sys.argv[1], "src", "grapy"))
import grapy as arcpy  # noqa: E402

pro, ours = sys.argv[2], sys.argv[3]
show = sys.argv[sys.argv.index("--show") + 1:] if "--show" in sys.argv else []


def read(path):
    raster = arcpy.Raster(path)
    a = arcpy.RasterToNumPyArray(raster).astype(float)
    nd = raster.noDataValue
    mask = np.zeros(a.shape, bool) if nd is None else (a == nd)
    mask |= ~np.isfinite(a)
    return a, mask


print(f"{'case':22} {'cells':>5} {'nd-diff':>7} {'max|d|':>12} {'mean|d|':>12} {'max rel':>9}")
for path in sorted(glob.glob(os.path.join(pro, "*.tif"))):
    name = os.path.splitext(os.path.basename(path))[0]
    other = os.path.join(ours, name + ".tif")
    if not os.path.exists(other):
        print(f"{name:22} missing on grapy's side")
        continue
    a, ma = read(path)
    b, mb = read(other)
    if a.shape != b.shape:
        print(f"{name:22} shape {a.shape} vs {b.shape}")
        continue
    both = ~ma & ~mb
    d = np.abs(a - b)[both]
    rel = (d / np.maximum(np.abs(a[both]), 1e-12)) if d.size else d
    print(f"{name:22} {a.size:5d} {int((ma != mb).sum()):7d} {d.max() if d.size else 0:12.6g} "
          f"{d.mean() if d.size else 0:12.6g} {rel.max() if d.size else 0:9.4f}")
    if name in show:
        np.set_printoptions(linewidth=200, precision=4, suppress=True)
        print("  pro:\n", np.where(ma, np.nan, a))
        print("  grapy:\n", np.where(mb, np.nan, b))
