# A comparison with ArcGIS Pro 3.7 (ADR-234 condition 4, D-339, 2026-11-04). Run from a work folder after the Pro runs
# (adr234-terrain-corner-pro.py, adr234-terrain-seam-probe.py):
#   <grapy python> adr234-terrain-seam-predict.py <tag>@<levels "8;16;32">[@<method>[@<max overview size>]] ...
"""Predict Pro's window size levels with grapy's own selection (``_terrain.window_levels``: every level's last windows
running past the tile's edge, the coarsest level's only under a coarser overview, whose points every level keeps) from
the XY domain the run recorded, and count how many of Pro's kept points it reproduces per level, beside the rule before 2026-11-04
(windows cut short at the tile's edge). Printed per level: Pro's count / predicted / in common."""
import csv
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "grapy"))
from grapy import _terrain  # noqa: E402


def load(path):
    with open(path) as read:
        return np.array([[float(r["x"]), float(r["y"]), float(r["z"])] for r in csv.DictReader(read)])


def keys(full, which):
    return {(round(full[k, 0], 2), round(full[k, 1], 2)) for k in which}


def anchors(v, origin, tile):
    """``_terrain.window_anchors`` from the domain the run recorded: tiles from its origin, centred on the data on an
    axis whose tile index reaches 23,170."""
    index = np.floor((v - origin) / tile)
    if index.max() >= _terrain._TILE_INDEX_LIMIT:
        start = (v.min() + v.max()) / 2 - tile / 2
        return start + np.floor((v - start) / tile) * tile
    return origin + index * tile


def predict(tag, levels, method, overview, seam):
    meta = dict(line.split(" ", 1) for line in open(f"runs/{tag}/meta.txt").read().splitlines())
    domain = [float(v) for v in meta["domain"].split()]
    spacing = float(meta["spacing"])
    tile = float(round(spacing * math.sqrt(200000)))
    full = load(f"runs/{tag}/0.csv")
    starts = tuple(anchors(full[:, a], domain[a], tile) for a in (0, 1))
    none = np.zeros(len(full), bool)
    if seam:
        chosen = _terrain.window_levels(full, none, starts, tile, levels, method, overview)
    else:   # the rule before 2026-11-04: windows cut short at the tile's edge, no overview
        chosen = {r: _terrain.window_thin(full, none, r, starts, method) for r in levels}
    out = []
    for resolution in levels:
        kept = set().union(*(keys(full, chosen[r]) for r in levels if r >= resolution))
        level = load(f"runs/{tag}/{resolution:g}.csv")
        pro = keys(level, range(len(level)))
        out.append((resolution, len(pro), len(kept), len(pro & kept)))
    return out


if __name__ == "__main__":
    totals = {True: [0, 0, 0], False: [0, 0, 0]}
    for spec in sys.argv[1:]:
        parts = spec.split("@")
        tag, levels = parts[0], [float(v) for v in parts[1].split(";")]
        method = parts[2] if len(parts) > 2 else "ZMIN"
        overview = float(parts[3]) if len(parts) > 3 else None
        line = []
        for seam in (True, False):
            rows = predict(tag, levels, method, overview, seam)
            for r in rows:
                totals[seam][0] += r[1]
                totals[seam][1] += r[2]
                totals[seam][2] += r[3]
            line.append(("now " if seam else "before ") + " ".join(f"w{r[0]:g} {r[1]}/{r[2]}/{r[3]}" for r in rows))
        print(f"{tag:12s} {method:7s}", " | ".join(line))
    for seam in (True, False):
        pro, pred, common = totals[seam]
        print("now" if seam else "before", f"Pro {pro}, predicted {pred}, in common {common}")
