"""Inputs for the ADR-241 comparison: GeoTIFFs on EPSG:3857, 10-unit cells, written by grapy and read by both sides.

Run with grapy's Python: adr241-pathdist-inputs.py <worktree> <out folder>. A tool, not a test (needs Pro to be useful).
"""
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(sys.argv[1], "src", "grapy"))
import grapy as arcpy  # noqa: E402

out = sys.argv[2]
os.makedirs(out, exist_ok=True)
arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)


def save(name, values, nodata=None):
    if isinstance(values, tuple):
        values, nodata = values
    raster = arcpy.NumPyArrayToRaster(np.asarray(values), arcpy.Point(0.0, 0.0), 10.0, 10.0, nodata)
    raster.save(os.path.join(out, name + ".tif"))


def one(value, rows, columns, *at):
    a = np.full((rows, columns), -9, dtype=np.int32)
    for r, k in at:
        a[r, k] = value
    return a, -9


# The unit tests' small grids.
save("src_1x3", one(1, 1, 3, (0, 0)))
save("rise_1x3", np.array([[0.0, 10.0, 10.0]], dtype=np.float32))
save("rise2_1x3", np.array([[0.0, 10.0, 20.0]], dtype=np.float32))
save("src_1x3_mid", one(1, 1, 3, (0, 1)))
save("wind_east_1x3", np.full((1, 3), 90.0, dtype=np.float32))
save("src_3x3", one(1, 3, 3, (0, 0)))
save("cost_3x3", np.array([[1, 1, 1], [1, 5, 1], [1, 1, 1]], dtype=np.float32))
# Diagonal reading (h): a 10 rise on the diagonal step (0,0) -> (1,1).
save("diag_surface_3x3", np.array([[0, 0, 0], [0, 10, 0], [0, 0, 0]], dtype=np.float32))
# VRMA resolution (i): a 1-unit rise over 10 is 5.7106 deg; 0.25 rounding makes it 5.75.
save("rise1_1x3", np.array([[0.0, 1.0, 2.0]], dtype=np.float32))
save("src_5x5", one(1, 5, 5, (2, 2)))
large = one(1, 41, 41, (20, 20))
save("src_41", large)

# A 21 x 21 field: a hill, a varying cost, a wind turning across the grid.
n = 21
r, k = np.indices((n, n), dtype=float)
hill = 40.0 * np.exp(-((r - 8) ** 2 + (k - 12) ** 2) / 30.0) + 1.5 * k
save("surface_21", hill.astype(np.float32))
save("cost_21", (1.0 + 0.5 * ((r + 2 * k) % 5)).astype(np.float32))
save("wind_21", ((30.0 + 6.0 * r + 3.0 * k) % 360).astype(np.float32))
save("src_21", one(1, n, n, (18, 3)))
save("dst_21", one(5, n, n, (2, 17), (10, 19)))
print("written to", out)

# Probes for the reconstruction: a uniform cost, a flat surface and a wall on the 41 grid.
save("ones_41", np.ones((41, 41), dtype=np.float32))
save("flat_41", np.zeros((41, 41), dtype=np.float32))
wall = np.full((41, 41), -9, dtype=np.int32)
wall[5:30, 26] = 1
save("wall_41", (wall, -9))
save("ramp_41", (np.indices((41, 41))[1] * 2.0).astype(np.float32))
save("north_41", np.zeros((41, 41), dtype=np.float32))
