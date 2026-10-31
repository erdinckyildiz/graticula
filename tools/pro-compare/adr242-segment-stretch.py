# A comparison with ArcGIS Pro 3.7 (ADR-242 condition 3, D-336, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
"""Segment Mean Shift (ADR-242 condition 3, D-336): does Pro stretch a band before its range kernel?

Two flat halves of a 48 x 48 one-band 8-bit image, the right half delta brighter — a difference Pro separates
(measured: 100 and 106) — with and without a 6 x 6 patch of 255 in a corner. A per-band min/max or standard deviation
stretch would shrink the halves' difference by the patch's range and merge them; no stretch leaves the answer as it
was. Prints the number of segments in the halves (the patch's own segment not counted).

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr242-segment-stretch.py <out folder>
"""
import os
import sys

import numpy as np
import arcpy
from arcpy.sa import SegmentMeanShift

folder = sys.argv[1]
os.makedirs(folder, exist_ok=True)
arcpy.env.overwriteOutput = True
arcpy.CheckOutExtension("Spatial")
n = 48
for a, delta in ((100, 6), (30, 10), (100, 2)):
    for patch in (False, True):
        img = np.full((n, n), float(a))
        img[:, n // 2:] += delta
        if patch:
            img[:6, :6] = 255
        tag = f"{a}_{delta}_{int(patch)}"
        p = os.path.join(folder, f"in_{tag}.tif")
        arcpy.NumPyArrayToRaster(np.ascontiguousarray(img.astype(np.uint8)), arcpy.Point(0, 0), 1, 1).save(p)
        out = SegmentMeanShift(p, 15.5, 15, 20, "1")
        path = os.path.join(folder, f"seg_{tag}.tif")
        out.save(path)
        seg = arcpy.RasterToNumPyArray(path)
        halves = seg[12:, :]          # below the patch
        print(a, delta, "patch" if patch else "plain", "segments in the halves", len(np.unique(halves)),
              "left", np.unique(halves[:, :24]).tolist()[:3], "right", np.unique(halves[:, 24:]).tolist()[:3],
              flush=True)
