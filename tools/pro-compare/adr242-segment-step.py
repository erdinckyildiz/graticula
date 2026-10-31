# A comparison with ArcGIS Pro 3.7 (ADR-242 condition 3, D-336, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
"""Segment Mean Shift's spectral scale (ADR-242 condition 3, D-336): two flat halves of a 48 x 48 8-bit image, the
right half differing by delta, segmented by Pro; prints the number of segments for each case.

pro_step.py <out folder> <spectral> <cases: "kind:a:delta" separated by commas>
kind: g (one band), rgbg (three equal bands), r (delta in band 1 of 3), rgb (delta in all three bands), rb (bands 1, 3)"""
import os
import sys
import time

import numpy as np
import arcpy
from arcpy.sa import SegmentMeanShift

folder, spectral, cases = sys.argv[1], float(sys.argv[2]), sys.argv[3].split(",")
os.makedirs(folder, exist_ok=True)
arcpy.env.overwriteOutput = True
arcpy.CheckOutExtension("Spatial")
n = 48
for case in cases:
    kind, a, delta = case.split(":")
    a, delta = float(a), float(delta)
    img = np.full((n, n), a)
    img[:, n // 2:] += delta
    if kind == "g":
        bands = [img]
    else:
        base = np.full((n, n), a)
        chosen = {"rgbg": (img, img, img), "r": (img, base, base), "rgb": (img, img, img),
                  "rb": (img, base, img), "gb": (base, img, img)}[kind]
        bands = list(chosen)
    paths = []
    tag = f"{kind}_{a:g}_{delta:g}_{spectral:g}"
    for b, arr in enumerate(bands):
        p = os.path.join(folder, f"in_{tag}_{b}.tif")
        arcpy.NumPyArrayToRaster(np.ascontiguousarray(arr.astype(np.uint8)), arcpy.Point(0, 0), 1, 1).save(p)
        paths.append(p)
    src = paths[0] if len(paths) == 1 else arcpy.management.CompositeBands(";".join(paths),
                                                                             os.path.join(folder, f"img_{tag}.tif"))[0]
    t = time.time()
    out = SegmentMeanShift(src, spectral, 15, 20, "1 2 3" if len(paths) == 3 else "1")
    path = os.path.join(folder, f"seg_{tag}.tif")
    out.save(path)
    arr = arcpy.RasterToNumPyArray(path)
    if arr.ndim == 3:
        arr = arr.reshape(arr.shape[0], -1).T
        segs = len({tuple(v) for v in arr})
    else:
        segs = len(np.unique(arr))
    print(case, spectral, "segments", segs, "%.1f s" % (time.time() - t), flush=True)
