# A comparison with ArcGIS Pro 3.7 (ADR-234 condition 4, D-339, 2026-10-31). Run from the work folder after the Pro runs:
# <grapy python> adr234-terrain-corner-predict.py <tag> [<tag> ...]
"""Predict Pro's ZMIN window size levels from the rule found (tiles of round(spacing * sqrt(200000)) from the XY domain's
origin, windows from each tile's origin; an axis whose tile index reaches 23170 uses a tile centred on the data) and
count how many of Pro's kept points the prediction reproduces, per run and level."""
import csv
import math
import sys

import numpy as np


def load(p):
    with open(p) as f:
        return np.array([[float(r["x"]), float(r["y"]), float(r["z"])] for r in csv.DictReader(f)])


def anchors(v, dom, tile, lo, hi):
    idx = np.floor((v - dom) / tile)
    if idx.max() >= 23170:
        start = (lo + hi) / 2 - tile / 2
        return start + np.floor((v - start) / tile) * tile
    return dom + idx * tile


total = [0, 0]
for tag in sys.argv[1:]:
    meta = dict(l.split(" ", 1) for l in open(f"runs/{tag}/meta.txt").read().splitlines())
    dom = [float(v) for v in meta["domain"].split()]
    spacing = float(meta["spacing"])
    full = load(f"runs/{tag}/0.csv")
    tile = round(spacing * math.sqrt(200000))
    ax = anchors(full[:, 0], dom[0], tile, full[:, 0].min(), full[:, 0].max())
    ay = anchors(full[:, 1], dom[1], tile, full[:, 1].min(), full[:, 1].max())
    out = []
    for res in (4, 6, 8, 12, 16, 24, 32):
        try:
            kept = load(f"runs/{tag}/{res:g}.csv")
        except FileNotFoundError:
            continue
        ix = np.floor((full[:, 0] - ax) / res + 1e-9)
        iy = np.floor((full[:, 1] - ay) / res + 1e-9)
        key = list(zip(ax, ay, ix, iy))
        best = {}
        for i, k in enumerate(key):
            if k not in best or full[i, 2] < full[best[k], 2]:
                best[k] = i
        s = {(round(full[i, 0], 2), round(full[i, 1], 2)) for i in best.values()}
        ks = {(round(x, 2), round(y, 2)) for x, y, _ in kept}
        out.append(f"w{res}: {len(s & ks)}/{len(ks)} (pred {len(s)})")
        total[0] += len(s & ks)
        total[1] += len(ks)
    print(f"{tag:10s} T={tile}", "  ".join(out))
print("total", total)
