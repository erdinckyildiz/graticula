# A comparison with ArcGIS Pro 3.7 (ADR-236, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> -I adr236-change-compare.py <scratch>
"""Change dates detected by Pro and grapy, cell by cell (decimal years; NaN where no change)."""
import numpy as np, os, sys
d = sys.argv[1]
np.set_printoptions(linewidth=200, precision=3, suppress=True)
for n in ("ccdc_most_recent", "ccdc_largest", "lt_most_recent", "lt_largest"):
    p, g = np.load(os.path.join(d, "pro_md", n + ".npy")).astype(float), np.load(os.path.join(d, "grapy_md", n + ".npy"))[0]
    both = np.isfinite(p) & np.isfinite(g)
    dd = np.abs(p - g)[both]
    print(f"{n}: changed Pro {int(np.isfinite(p).sum())} grapy {int(np.isfinite(g).sum())} both {int(both.sum())} "
          f"| same date {int((dd < 0.5).sum())} | max|d| {dd.max() if dd.size else np.nan:.1f} mean|d| {dd.mean() if dd.size else np.nan:.1f} (days once both are days since 1899-12-30) "
          f"| Pro dates {np.unique(np.round(p[np.isfinite(p)], 3))[:8]} grapy {np.unique(np.round(g[np.isfinite(g)], 3))[:8]}")
# Against the planted break: the right half drops on 2018-06-15, so CCDC should date it at the next slice, and
# LandTrendr's drop segment begins in 2017. The left half has none.
import datetime as dt
first = dt.date(2014, 1, 1) + dt.timedelta(days=16 * -(-(dt.date(2018, 6, 15) - dt.date(2014, 1, 1)).days // 16))
BREAK_OLE = float((first - dt.date(1899, 12, 30)).days)
BREAK_YEAR = first.year + (first - dt.date(first.year, 1, 1)).days / (dt.date(first.year + 1, 1, 1) - dt.date(first.year, 1, 1)).days
print("planted break slice", first, BREAK_OLE, round(BREAK_YEAR, 4))
def year_of(v, ole):
    if not np.isfinite(v): return np.nan
    if ole: return (dt.date(1899, 12, 30) + dt.timedelta(days=float(v))).year
    return int(np.floor(v))
for n in ("ccdc_most_recent", "ccdc_largest", "lt_most_recent", "lt_largest"):
    p, g = np.load(os.path.join(d, "pro_md", n + ".npy")).astype(float), np.load(os.path.join(d, "grapy_md", n + ".npy"))[0]
    pro_ole = np.nanmax(p) > 10000; gra_ole = np.nanmax(g) > 10000
    if n.startswith("ccdc"):
        hit = lambda a, ole: np.isclose(a, BREAK_OLE if ole else BREAK_YEAR, atol=0.5 if ole else 0.0014)
        rp, rg = hit(p[:, 10:], pro_ole).sum(), hit(g[:, 10:], gra_ole).sum()
        same = hit(p, pro_ole) == hit(g, gra_ole)
    else:
        yp = np.vectorize(lambda v: year_of(v, pro_ole))(p); yg = np.vectorize(lambda v: year_of(v, gra_ole))(g)
        rp, rg = (yp[:, 10:] == 2017).sum(), (yg[:, 10:] == 2017).sum()
        same = yp == yg
    print(f"{n}: right half at the planted break Pro {rp}/200 grapy {rg}/200; left half changes Pro "
          f"{int(np.isfinite(p[:, :10]).sum())} grapy {int(np.isfinite(g[:, :10]).sum())}; cells agreeing {int(same.sum())}/400")
