# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr243-classify-svm-exact-linear.py <scratch> <worktree>
"""Is grapy's linear SVM weak because SMO stops early, or because the kernel is linear? Solve each pair's dual exactly."""
import sys, os, json, numpy as np, rasterio
sys.path.insert(0, sys.argv[2] + "/src/grapy")
from scipy.optimize import minimize
from grapy import _classify as C
d = sys.argv[1]
doc = json.load(open(os.path.join(d, "grapy_cls", "svm.ecd")))
img = rasterio.open(os.path.join(d, "image.tif")).read().astype(float)
truth = rasterio.open(os.path.join(d, "truth.tif")).read(1)
x_all = img.reshape(4, -1).T
import grapy as arcpy
data, mask, grid, extra = C._stack(os.path.join(d, "image.tif"), None, "x")
labels, names = C._training(os.path.join(d, "training.gpkg") + "/training", grid, "x")
x, y = C._samples(data, mask, labels, 0)
centre, scale = np.asarray(doc["centre"]), np.asarray(doc["scale"])
z = (x - centre) / scale
values = sorted(set(y.tolist()))
def dual(zz, yy, c=1.0, kernel=lambda a, b: a @ b.T):
    K = kernel(zz, zz); Q = (yy[:, None] * yy[None, :]) * K; n = len(yy)
    res = minimize(lambda a: 0.5 * a @ Q @ a - a.sum(), np.zeros(n), jac=lambda a: Q @ a - 1, method="SLSQP",
                   bounds=[(0, c)] * n, constraints=[{"type": "eq", "fun": lambda a: a @ yy, "jac": lambda a: yy}],
                   options={"maxiter": 500, "ftol": 1e-10})
    a = res.x; sv = (a > 1e-6) & (a < c - 1e-6)
    f = (a * yy) @ K
    b = np.mean(yy[sv] - f[sv]) if sv.any() else 0.0
    return a, b, 0.5 * a @ Q @ a - a.sum()
machines = []
for m in doc["machines"]:
    pick = (y == m["first"]) | (y == m["second"]); zz = z[pick]; yy = np.where(y[pick] == m["first"], 1.0, -1.0)
    a, b, obj = dual(zz, yy)
    w = (a * yy) @ zz
    # grapy's objective from its own w (primal): 0.5|w|^2 + C sum hinge
    gw, gb = np.asarray(m["weights"]), m["bias"]
    prim = lambda w_, b_: 0.5 * w_ @ w_ + np.maximum(0, 1 - yy * (zz @ w_ + b_)).sum()
    print(m["first"], m["second"], "n", len(yy), "primal grapy %.2f exact %.2f" % (prim(gw, gb), prim(w, b)),
          "train acc grapy %.3f exact %.3f" % (((zz @ gw + gb > 0) == (yy > 0)).mean(), ((zz @ w + b > 0) == (yy > 0)).mean()))
    machines.append({"first": m["first"], "second": m["second"], "weights": w.tolist(), "bias": float(b)})
zx = (x_all - centre) / scale
idx = C._svm_vote(zx, machines, values)
pred = np.asarray(values)[idx].reshape(truth.shape)
print("exact linear vs truth OA %.4f" % (pred == truth).mean())
p = rasterio.open(os.path.join(d, "pro_cls", "svm.tif")).read(1) + 1
print("exact linear vs Pro OA %.4f" % (pred == p).mean())
