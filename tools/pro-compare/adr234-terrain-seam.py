# A comparison with ArcGIS Pro 3.7 (ADR-234 condition 4, D-339, 2026-11-01). A tool, not a test: the Pro run needs Pro.
# Where Pro's window size levels lose points: the windows a tile seam cuts short.
#
#   <grapy python> adr234-terrain-seam.py make <work>        6,400 points on a jittered 1 m lattice, z uniform 10-60,
#                                                             over 500011-500091, 4400011-4400091 (seed 339)
#   "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr234-terrain-corner-pro.py <work> lat4 4
#       "8;16;32" "-500000,3400000"                           spacing 4: tiles of 1,789 m, seams at x 500051, y 4400051
#   <grapy python> adr234-terrain-seam.py analyse <work> <tag> [<work> <tag> ...]
#
# `analyse` lists every window a seam cuts short at level 8, whether Pro kept its lowest point, and the lowest points
# beside it (the window before it in its own tile, the one across the seam), then scores the one rule family that came
# close: a cut window loses its point when a lower point lies within b metres across the seam.
# Measured 2026-11-01 on lat4 and on s4D1000000 (procmp-f's run of the comparison's 2,500 points): Pro kept every
# point the tile rule predicts outside the cut windows, and lost the point of 13 of the 48 cut windows (8 of 21 on the
# lattice, 5 of 27 on the comparison's points; at level 16 one, at 32 none). Every lost point is the lowest of its
# window; no window holds a point Pro kept instead. b = 1.5 m explains 44 of the 48 and no b, and no extension before
# the seam, explains all: a lost window with no lower point within 2 m across, and a kept one with a lower point 0.6 m
# across, remain.
# 2026-11-04: explained (D-339) - a tile's last window runs whole past its edge and keeps only its own tile's choice,
# and the overview tops the pyramid when it is coarser than every level: adr234-terrain-seam-probe.py and
# adr234-terrain-seam-predict.py, which reproduce every ZMIN level of both runs above.
import csv
import math
import os
import sys

import numpy as np


def load(path):
    with open(path) as read:
        return np.array([[float(r["x"]), float(r["y"]), float(r["z"])] for r in csv.DictReader(read)])


def make(work):
    os.makedirs(work, exist_ok=True)
    rng = np.random.default_rng(339)
    xs, ys = np.arange(500011.25, 500091, 1.0), np.arange(4400011.25, 4400091, 1.0)
    x, y = np.meshgrid(xs, ys)
    z = rng.uniform(10, 60, x.shape).round(3)
    x = x + rng.uniform(-0.3, 0.3, x.shape)
    y = y + rng.uniform(-0.3, 0.3, y.shape)
    with open(os.path.join(work, "points.csv"), "w", newline="") as out:
        w = csv.writer(out)
        w.writerow(["PID", "X", "Y", "Z"])
        for i, (a, b, c) in enumerate(zip(x.ravel(), y.ravel(), z.ravel())):
            w.writerow([i + 1, f"{a:.3f}", f"{b:.3f}", f"{c:.3f}"])


def cut_windows(work, tag, size=8.0):
    run = os.path.join(work, "runs", tag)
    meta = dict(line.split(" ", 1) for line in open(os.path.join(run, "meta.txt")).read().splitlines())
    origin = [float(v) for v in meta["domain"].split()]
    full = load(os.path.join(run, "0.csv"))
    kept = {(round(x, 2), round(y, 2)) for x, y, _ in load(os.path.join(run, f"{size:g}.csv"))}
    tile = round(float(meta["spacing"]) * math.sqrt(200000))
    ax = origin[0] + np.floor((full[:, 0] - origin[0]) / tile) * tile
    ay = origin[1] + np.floor((full[:, 1] - origin[1]) / tile) * tile
    ix = np.floor((full[:, 0] - ax) / size + 1e-9)
    iy = np.floor((full[:, 1] - ay) / size + 1e-9)
    groups = {}
    for i, key in enumerate(zip(ax, ay, ix, iy)):
        groups.setdefault(key, []).append(i)
    out = []
    for key, members in groups.items():
        x0, y0 = key[0] + key[2] * size, key[1] + key[3] * size
        x1, y1 = min(x0 + size, key[0] + tile), min(y0 + size, key[1] + tile)
        if x1 - x0 < size or y1 - y0 < size:
            i = members[int(np.argmin(full[members, 2]))]
            out.append((tag, full, x0, x1, y0, y1, full[i], (round(full[i, 0], 2), round(full[i, 1], 2)) in kept))
    return out


def lowest(full, x0, x1, y0, y1):
    inside = (full[:, 0] >= x0) & (full[:, 0] < x1) & (full[:, 1] >= y0) & (full[:, 1] < y1)
    return float(full[inside, 2].min()) if inside.any() else float("nan")


def analyse(pairs):
    cases = []
    for work, tag in pairs:
        cases += cut_windows(work, tag)
    for tag, full, x0, x1, y0, y1, p, kept in sorted(cases, key=lambda c: (c[0], c[4], c[2])):
        across_x = x1 - x0 < 8
        before = lowest(full, x0 - 8, x0, y0, y1) if across_x else lowest(full, x0, x1, y0 - 8, y0)
        across = lowest(full, x1, x1 + 8, y0, y1) if across_x else lowest(full, x0, x1, y1, y1 + 8)
        print(f"{tag} x[{x0:.0f},{x1:.0f}) y[{y0:.0f},{y1:.0f}) own {p[2]:.3f} before {before:.3f} "
              f"across {across:.3f} {'kept' if kept else 'LOST'}")
    print(f"{len(cases)} cut windows, {sum(not c[-1] for c in cases)} lost")
    scores = []
    for b in np.arange(0, 8.01, 0.5):
        right = 0
        for tag, full, x0, x1, y0, y1, p, kept in cases:
            cx, cy = x1 - x0 < 8, y1 - y0 < 8
            low = lowest(full, x0, x1 + (b if cx else 0), y0, y1 + (b if cy else 0))
            right += (low >= p[2]) == kept
        scores.append((right, b))
    print("a lower point within b m across the seam takes the window's point: best (windows explained, b)",
          sorted(scores, reverse=True)[:3])


if __name__ == "__main__":
    if sys.argv[1] == "make":
        make(sys.argv[2])
    else:
        rest = sys.argv[2:]
        analyse(list(zip(rest[0::2], rest[1::2])))
