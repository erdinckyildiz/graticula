# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> -I adr243-classify-compare.py <scratch>
"""Agreement between Pro's and grapy's class maps (overall accuracy and Cohen's kappa), and each against the truth."""
import numpy as np, rasterio, os, sys
d = sys.argv[1]
def kappa(a, b):
    v = np.union1d(np.unique(a), np.unique(b)); idx = {x: i for i, x in enumerate(v)}
    m = np.zeros((v.size, v.size))
    np.add.at(m, (np.vectorize(idx.get)(a), np.vectorize(idx.get)(b)), 1)
    n = m.sum(); po = np.trace(m) / n; pe = (m.sum(0) * m.sum(1)).sum() / n ** 2
    return po, (po - pe) / (1 - pe), m
truth = rasterio.open(os.path.join(d, "truth.tif")).read(1).ravel()
for n in ("ml", "svm"):
    # Pro's cells hold the class's 0-based index; its attribute table maps Value to Classvalue (here 1..5).
    p = rasterio.open(os.path.join(d, "pro_cls", n + ".tif")).read(1).ravel().astype(int) + 1
    g = rasterio.open(os.path.join(d, "grapy_cls", n + ".tif")).read(1).ravel()
    po, k, m = kappa(p, g)
    print(f"{n}: Pro vs grapy OA {po:.4f} kappa {k:.4f}; disagree {int((p != g).sum())} of {p.size}")
    print(f"   Pro vs truth OA {kappa(truth, p)[0]:.4f}  grapy vs truth OA {kappa(truth, g)[0]:.4f}")
    print("   rows Pro, cols grapy\n", m.astype(int))
