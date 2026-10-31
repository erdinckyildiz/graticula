# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr243-classify-svm-rbf.py <scratch> <worktree> <C> <gamma>   (Pro's C and gamma: 2 32)
"""Does an RBF C-SVC with C = 2, gamma = 32 on cells scaled by the pixel type's range (/255) reproduce Pro's map?"""
import sys, os, numpy as np, rasterio
sys.path.insert(0, sys.argv[2] + "/src/grapy")
from grapy import _classify as C
d = sys.argv[1]
data, mask, grid, extra = C._stack(os.path.join(d, "image.tif"), None, "x")
labels, names = C._training(os.path.join(d, "training.gpkg") + "/training", grid, "x")
x, y = C._samples(data, mask, labels, 0)
z = x / 255.0
truth = rasterio.open(os.path.join(d, "truth.tif")).read(1)
zx = rasterio.open(os.path.join(d, "image.tif")).read().reshape(4, -1).T / 255.0
values = sorted(set(y.tolist()))
c, gamma = float(sys.argv[3]), float(sys.argv[4])
def rbf(a, b):
    return np.exp(-gamma * ((a[:, None, :] - b[None, :, :]) ** 2).sum(-1))
votes = np.zeros((zx.shape[0], len(values)))
from scipy.optimize import minimize
nsv = 0
for i in range(len(values)):
    for j in range(i + 1, len(values)):
        pick = (y == values[i]) | (y == values[j]); zz = z[pick]; yy = np.where(y[pick] == values[i], 1.0, -1.0)
        K = rbf(zz, zz); Q = yy[:, None] * yy[None] * K; n = len(yy)
        res = minimize(lambda a: 0.5 * a @ Q @ a - a.sum(), np.zeros(n), jac=lambda a: Q @ a - 1, method="SLSQP",
                       bounds=[(0, c)] * n, constraints=[{"type": "eq", "fun": lambda a: a @ yy, "jac": lambda a: yy}],
                       options={"maxiter": 1000, "ftol": 1e-12})
        a = res.x; sv = a > 1e-7 * c; free = sv & (a < c * (1 - 1e-6)); nsv += sv.sum()
        f = (a * yy) @ K
        b = np.mean(yy[free] - f[free])
        dec = rbf(zx, zz[sv]) @ (a[sv] * yy[sv]) + b
        votes[:, i] += dec > 0; votes[:, j] += dec <= 0
pred = np.asarray(values)[np.argmax(votes, axis=1)].reshape(truth.shape)
p = rasterio.open(os.path.join(d, "pro_cls", "svm.tif")).read(1) + 1
print("C", c, "gamma", gamma, "SV (pairwise sum)", nsv, "vs truth %.4f vs Pro %.4f" % ((pred == truth).mean(), (pred == p).mean()))
