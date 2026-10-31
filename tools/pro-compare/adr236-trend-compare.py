# A comparison with ArcGIS Pro 3.7 (ADR-236, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> -I adr236-trend-compare.py <scratch> linear harmonic poly mk
"""Band by band difference between pro_md/<name>.npy and grapy_md/<name>.npy."""
import numpy as np, os, sys
d = sys.argv[1]
names = {"linear": ["Slope", "Intercept", "RMSE", "R2", "P_Value"],
         "harmonic": ["Slope", "Intercept", "H1", "H2", "RMSE", "R2", "P_Value"],
         "mk": ["Sens_Slope", "P_Value", "MK_Score", "S_Variance", "Z_Score"]}
for n in sys.argv[2:]:
    p, g = np.load(os.path.join(d, "pro_md", n + ".npy")), np.load(os.path.join(d, "grapy_md", n + ".npy"))
    if p.ndim == 2: p = p[None]
    print(n, "pro", p.shape, "grapy", g.shape)
    for b in range(min(p.shape[0], g.shape[0])):
        x, y = p[b].astype(float), g[b].astype(float)
        both = np.isfinite(x) & np.isfinite(y)
        dd = np.abs(x - y)[both]
        label = names.get(n, [str(i) for i in range(20)])[b] if b < len(names.get(n, [])) or n not in names else str(b)
        print(f"  {label:10s} pro mean {np.nanmean(x):+.6g} grapy mean {np.nanmean(y):+.6g} max|d| {dd.max() if dd.size else np.nan:.3g} "
              f"mean|d| {dd.mean() if dd.size else np.nan:.3g} | NaN pro {int(np.isnan(x).sum())} grapy {int(np.isnan(y).sum())}")
