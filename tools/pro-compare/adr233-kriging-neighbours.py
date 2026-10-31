# A comparison with ArcGIS Pro 3.7 (ADR-233, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr233-kriging-neighbours.py <scratch> <worktree> pro_k
"""Which points does Pro use? grapy's kriging (sa._krige) at Pro's cell centres with the corner points kept or left out."""
import sys, json, numpy as np, rasterio
sys.path.insert(0, sys.argv[2] + "/src/grapy")
from grapy import sa
d = sys.argv[1]
pts = np.array(json.load(open(d + "/points_k.json")))
a = rasterio.open(d + "/" + sys.argv[3] + "/pred.tif"); P = a.read(1).astype(float)
V = rasterio.open(d + "/" + sys.argv[3] + "/var.tif").read(1).astype(float)
for label, use in (("all points", pts), ("corners left out", pts[4:])):
    pred = np.zeros(P.shape); var = np.zeros(P.shape)
    for r in range(P.shape[0]):
        for c in range(P.shape[1]):
            x, y = a.xy(r, c)
            dd = np.hypot(use[:, 0] - x, use[:, 1] - y); idx = np.argsort(dd, kind="stable")[:12]
            pred[r, c], var[r, c] = sa._krige(use[idx, 0], use[idx, 1], use[idx, 2], x, y, "SPHERICAL", 700.0, 4.0, 0.3, None)
    print(label, "pred max|d| %.3g mean|d| %.3g; var max|d| %.3g mean|d| %.3g" % (np.abs(pred - P).max(), np.abs(pred - P).mean(), np.abs(var - V).max(), np.abs(var - V).mean()))
# Where the default-grid run still differs: list the cells and the 12th/13th neighbour distances there.
use = pts
bad = []
for r in range(P.shape[0]):
    for c in range(P.shape[1]):
        x, y = a.xy(r, c)
        dd = np.hypot(use[:, 0] - x, use[:, 1] - y); order = np.argsort(dd, kind="stable"); idx = order[:12]
        p, v = sa._krige(use[idx, 0], use[idx, 1], use[idx, 2], x, y, "SPHERICAL", 700.0, 4.0, 0.3, None)
        if abs(p - P[r, c]) > 1e-3:
            bad.append((r, c, round(p - P[r, c], 4), round(dd[order[11]], 2), round(dd[order[12]], 2)))
print(len(bad), "cells differ by more than 1e-3"); print(bad[:20])
# Does swapping the 12th neighbour for the 13th, or using 11 or 13, reproduce Pro at those cells?
fixed = {"13th for 12th": 0, "nearest 13": 0, "nearest 11": 0}
for r, c, *_ in bad:
    x, y = a.xy(r, c)
    dd = np.hypot(use[:, 0] - x, use[:, 1] - y); order = np.argsort(dd, kind="stable")
    for name, idx in (("13th for 12th", list(order[:11]) + [order[12]]), ("nearest 13", order[:13]), ("nearest 11", order[:11])):
        idx = np.array(idx)
        p, _ = sa._krige(use[idx, 0], use[idx, 1], use[idx, 2], x, y, "SPHERICAL", 700.0, 4.0, 0.3, None)
        fixed[name] += abs(p - P[r, c]) < 1e-3
print("of", len(bad), fixed)
mask = np.ones(P.shape, bool)
for r, c, *_ in bad: mask[r, c] = False
gp_, gv_ = np.zeros(P.shape), np.zeros(P.shape)
for r in range(P.shape[0]):
    for c in range(P.shape[1]):
        x, y = a.xy(r, c); dd = np.hypot(use[:, 0] - x, use[:, 1] - y); idx = np.argsort(dd, kind="stable")[:12]
        gp_[r, c], gv_[r, c] = sa._krige(use[idx, 0], use[idx, 1], use[idx, 2], x, y, "SPHERICAL", 700.0, 4.0, 0.3, None)
print("same neighbours: %d cells, pred max|d| %.2g, variance max|d| %.2g (Pro writes float32)" % (mask.sum(), np.abs(gp_ - P)[mask].max(), np.abs(gv_ - V)[mask].max()))
dv = np.abs(gv_ - V); dv[~mask] = 0
print("variance |d| > 1e-4 at", int((dv > 1e-4).sum()), "cells; worst", [(int(r), int(c), round(float(dv[r, c]), 4), round(float(V[r, c]), 4)) for r, c in zip(*np.unravel_index(np.argsort(dv, axis=None)[-5:], dv.shape))])
# The geostatistical layer's own kriging (_geostat._krige, standard one-sector neighbourhood of 12 in a circle wider
# than the data) at Pro's cell centres: the same model held.
from grapy import _geostat
model = {"kind": "ORDINARY", "semivariogram": {"model": "SPHERICAL", "range": 700.0, "partialSill": 4.0, "nugget": 0.3}}
nbr = {"majorSemiaxis": 1e5, "minorSemiaxis": 1e5, "angle": 0.0, "nbrMax": 12, "nbrMin": 2}
gap, gav = np.zeros(P.shape), np.zeros(P.shape)
for r in range(P.shape[0]):
    for c in range(P.shape[1]):
        x, y = a.xy(r, c); idx = _geostat.select(nbr, use[:, 0], use[:, 1], x, y)
        gap[r, c], se = _geostat._krige(model, use[idx, 0], use[idx, 1], use[idx, 2], x, y); gav[r, c] = se * se
print("GA layer path: pred max|d| %.3g mean|d| %.3g, within 1e-3 %d of %d; variance (se^2) max|d| %.3g mean|d| %.3g"
      % (np.abs(gap - P).max(), np.abs(gap - P).mean(), (np.abs(gap - P) < 1e-3).sum(), P.size, np.abs(gav - V).max(), np.abs(gav - V).mean()))
