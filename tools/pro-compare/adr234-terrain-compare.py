"""Compare Pro's and grapy's terrain levels (ADR-234 condition 4). analyse.py <work folder>."""
import csv
import os
import sys

import numpy as np
from scipy.spatial import Delaunay

work = sys.argv[1]


def load(side, name):
    with open(os.path.join(work, side, name + ".csv")) as f:
        return np.array([[float(r["x"]), float(r["y"]), float(r["z"])] for r in csv.DictReader(f)])


def keys(a):
    return {(round(x, 2), round(y, 2)) for x, y, _ in a}


full = load("pro", "ws_ZMIN_0")
zof = {(round(x, 2), round(y, 2)): z for x, y, z in full}
for method in ("ZMIN", "ZMAX", "ZMEAN", "ZMINMAX"):
    for res in (4, 8, 16):
        p, g = keys(load("pro", f"ws_{method}_{res}")), keys(load("grapy", f"ws_{method}_{res}"))
        print(f"{method:8s} {res:3d}  pro {len(p):5d}  grapy {len(g):5d}  both {len(p & g):5d}  "
              f"pro only {len(p - g):4d}  grapy only {len(g - p):4d}")

# z-tolerance: every full-resolution point's distance from each level's surface, and the count kept
pts = full
for res in (0.5, 2):
    for side in ("pro", "grapy"):
        lvl = load(side, f"zt_{res:g}")
        corner = lvl[:, :2].min(axis=0)   # Qhull drops points on projected coordinates; triangulate from the corner
        tri = Delaunay(lvl[:, :2] - corner)
        s = tri.find_simplex(pts[:, :2] - corner)
        inside = s >= 0
        t = tri.transform[s[inside]]
        b = np.einsum("ijk,ik->ij", t[:, :2, :], pts[inside, :2] - corner - t[:, 2, :])
        w = np.column_stack([b, 1 - b.sum(axis=1)])
        z = (w * lvl[tri.simplices[s[inside]], 2]).sum(axis=1)
        e = np.abs(z - pts[inside, 2])
        print(f"zt {res:4g} {side:5s} kept {len(lvl):5d}  outside hull {int((~inside).sum())}  "
              f"max |dz| {e.max():.4f}  mean |dz| {e.mean():.4f}  over tolerance {int((e > res + 1e-6).sum())}")
    a, b2 = keys(load("pro", f"zt_{res:g}")), keys(load("grapy", f"zt_{res:g}"))
    print(f"zt {res:4g} shared points {len(a & b2)}")

# grapy's own selection (_terrain.window_thin, coarsest level first) run from Pro's window corner, found by search:
# with the corner Pro uses, does grapy's rule keep Pro's points?
if len(sys.argv) > 2:
    sys.path.insert(0, os.path.join(sys.argv[2], "src", "grapy"))
    from grapy import _terrain
    with open(os.path.join(work, "points.csv")) as f:
        pts = np.array([[float(r["X"]), float(r["Y"]), float(r["Z"])] for r in csv.DictReader(f)])
    corner = {4: (2, 2), 8: (6, 2), 16: (14, 10)}   # Pro's corner modulo each window, from probe_origin.py
    for method in ("ZMIN", "ZMAX", "ZMEAN", "ZMINMAX"):
        coarser = np.zeros(0, np.int64)
        for res in (16, 8, 4):
            chosen = _terrain.window_thin(pts, np.zeros(len(pts), bool), res, corner[res], method)
            coarser = np.union1d(chosen, coarser)
            mine, pro = keys(pts[coarser]), keys(load("pro", f"ws_{method}_{res}"))
            print(f"grapy's rule at Pro's corner {method:8s} {res:3d}  pro {len(pro):5d}  grapy {len(mine):5d}  "
                  f"same {len(pro & mine):5d}  differ {len(pro ^ mine)}")

# the z-tolerance levels' heights against each other, on a 0.5 m grid inside both hulls
gx, gy = np.meshgrid(np.arange(500000.25, 500100, 0.5), np.arange(4400000.25, 4400100, 0.5))
grid = np.column_stack([gx.ravel(), gy.ravel()])


def heights(level):
    corner = level[:, :2].min(axis=0)
    tri = Delaunay(level[:, :2] - corner)
    s = tri.find_simplex(grid - corner)
    out = np.full(len(grid), np.nan)
    ok = s >= 0
    t = tri.transform[s[ok]]
    b = np.einsum("ijk,ik->ij", t[:, :2, :], grid[ok] - corner - t[:, 2, :])
    out[ok] = (np.column_stack([b, 1 - b.sum(axis=1)]) * level[tri.simplices[s[ok]], 2]).sum(axis=1)
    return out


for res in (0.5, 2):
    d = np.abs(heights(load("pro", f"zt_{res:g}")) - heights(load("grapy", f"zt_{res:g}")))
    print(f"zt {res:4g} Pro's level against grapy's on {int(np.isfinite(d).sum())} cells: max {np.nanmax(d):.4f} "
          f"mean {np.nanmean(d):.4f}")
