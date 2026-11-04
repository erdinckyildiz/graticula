# A comparison with ArcGIS Pro 3.7 (ADR-234 condition 4, D-339, 2026-11-04). A tool, not a test: the Pro run needs Pro.
# Planted points across one tile seam, to find when Pro's window size level drops a cut-short window's point.
#
#   <grapy python> adr234-terrain-seam-probe.py make <work> <design>      points.csv and design.csv for one design
#   "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr234-terrain-corner-pro.py <work> <tag> 4
#       "8;16;32" "-500000,3400000"                    spacing 4: tiles of 1,789 m, a seam at x 500051 (and y 4400051)
#   <grapy python> adr234-terrain-seam-probe.py analyse <work> <tag>    per planted row: was p kept at level 8
#
# The layout. The seam S = 500051 is the left edge of tile 559; the left tile's last 8 m window is cut to [S-5, S).
# Rows are 8 m bands of the tiles' common y grid (4400051 + 8k), every second one planted, the others background only.
# Background: a 2 m lattice over x S-40..S+40 at z 100..101. In a planted row:
#   p   the cut window's point, at (S - a, row + 4 + pa), z 50 + pz
#   q   a point across the seam, at (S + d, row + 4 + along), z 50 + dz (dz < 0: lower than p)
#   decoys at z 20 in [S-13, S-5) and [S+8, S+16) unless the design says no, so levels 16 and 32 take a decoy
#   and p and q are chosen at level 8 only.
# A design is a list of rows (a, d, along, dz, pa, pz, decoy, extra), `extra` being further "x;y;z" points relative to
# (S, row) separated by "|".
import csv
import math
import os
import sys

import numpy as np

S = 500051.0
Y0 = 4400051.0
TILE = 1789


def designs(name):
    rows = []
    if name == "grid":  # p's and q's distance from the seam, q lower by 5
        for a in (0.25, 1.0, 2.0, 3.0, 4.0, 4.75):
            for d in (0.1, 0.5, 1.0, 1.5, 2.0, 3.0, 4.0, 6.0, 7.9):
                rows.append((a, d, 0.0, -5.0, 0.0, 0.0, 1, ""))
        for a in (0.25, 2.0, 4.75):  # controls: q higher
            for d in (0.1, 1.0, 3.0):
                rows.append((a, d, 0.0, 5.0, 0.0, 0.0, 1, ""))
    elif name == "nodecoy":  # the same without decoys: p and q also take part at levels 16 and 32
        for a in (0.25, 2.0, 4.75):
            for d in (0.1, 1.0, 3.0, 6.0):
                rows.append((a, d, 0.0, -5.0, 0.0, 0.0, 0, ""))
    elif name == "dz":  # how much lower q must be
        for a in (1.0, 4.0):
            for d in (0.5, 3.0):
                for dz in (-0.001, -0.01, -0.1, -1.0, -10.0, -29.0):
                    rows.append((a, d, 0.0, dz, 0.0, 0.0, 1, ""))
    elif name == "along":  # q's offset along the seam, within the row and into the neighbour rows
        for a in (1.0, 4.0):
            for d in (0.5, 3.0):
                for along in (-3.9, -2.0, 0.0, 2.0, 3.9):
                    rows.append((a, d, along, -5.0, 0.0, 0.0, 1, ""))
    else:
        raise SystemExit(f"no design {name}")
    return rows


def make(work, name):
    os.makedirs(work, exist_ok=True)
    rng = np.random.default_rng(3392)
    rows = designs(name)
    pts = []
    n_rows = 2 * len(rows) + 2
    gx = np.arange(S - 40 + 0.5, S + 40, 2.0)
    gy = np.arange(Y0 + 0.5, Y0 + 8 * n_rows, 2.0)
    bx, by = np.meshgrid(gx, gy)
    bz = rng.uniform(100, 101, bx.shape)
    pts += list(zip(bx.ravel(), by.ravel(), bz.ravel()))
    with open(os.path.join(work, "design.csv"), "w", newline="") as out:
        w = csv.writer(out)
        w.writerow(["row", "y0", "a", "d", "along", "dz", "pa", "pz", "decoy", "extra", "px", "py", "qx", "qy"])
        for i, (a, d, along, dz, pa, pz, decoy, extra) in enumerate(rows):
            y0 = Y0 + 8 * (2 * i + 1)
            p = (S - a, y0 + 4 + pa, 50 + pz)
            q = (S + d, y0 + 4 + along, 50 + dz)
            pts += [p, q]
            if decoy:
                pts += [(S - 9 + 0.3, y0 + 2.5, 20 + 0.01 * i), (S + 12 + 0.3, y0 + 5.5, 20 + 0.01 * i + 0.005)]
            for e in filter(None, extra.split("|")):
                ex, ey, ez = (float(v) for v in e.split(";"))
                pts.append((S + ex, y0 + ey, ez))
            w.writerow([i, y0, a, d, along, dz, pa, pz, decoy, extra, f"{p[0]:.3f}", f"{p[1]:.3f}", f"{q[0]:.3f}",
                        f"{q[1]:.3f}"])
    with open(os.path.join(work, "points.csv"), "w", newline="") as out:
        w = csv.writer(out)
        w.writerow(["PID", "X", "Y", "Z"])
        for i, (x, y, z) in enumerate(pts):
            w.writerow([i + 1, f"{x:.3f}", f"{y:.3f}", f"{z:.3f}"])
    print(len(rows), "rows", len(pts), "points")


def kept_set(work, tag, level):
    with open(os.path.join(work, "runs", tag, f"{level:g}.csv")) as read:
        return {(round(float(r["x"]), 2), round(float(r["y"]), 2)) for r in csv.DictReader(read)}


def analyse(work, tag):
    k8 = kept_set(work, tag, 8)
    with open(os.path.join(work, "design.csv")) as read:
        design = list(csv.DictReader(read))
    lost = 0
    for r in design:
        p = (round(float(r["px"]), 2), round(float(r["py"]), 2))
        q = (round(float(r["qx"]), 2), round(float(r["qy"]), 2))
        pk, qk = p in k8, q in k8
        lost += not pk
        print(f"row {int(r['row']):3d} a {float(r['a']):5.2f} d {float(r['d']):5.2f} along {float(r['along']):+5.2f} "
              f"dz {float(r['dz']):+8.3f} decoy {r['decoy']} {r['extra']:12s} p {'kept' if pk else 'LOST'} "
              f"q {'kept' if qk else 'lost'}")
    print(f"{len(design)} rows, p lost in {lost}")


if __name__ == "__main__":
    if sys.argv[1] == "make":
        make(sys.argv[2], sys.argv[3])
    else:
        analyse(sys.argv[2], sys.argv[3])
