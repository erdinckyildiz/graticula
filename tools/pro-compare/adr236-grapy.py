# A comparison with ArcGIS Pro 3.7 (ADR-236, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr236-grapy.py <scratch> <worktree> [linear harmonic poly mk ccdc landtrendr]
"""ADR-236 condition 3, grapy side: the same runs as mdim_pro.py, dumped to npy as (bands, rows, columns)."""
import os, sys, numpy as np, time
sys.path.insert(0, sys.argv[2] + "/src/grapy")
import grapy as arcpy
from grapy.ia import *
d = sys.argv[1]; o = os.path.join(d, "grapy_md"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
nc = os.path.join(d, "series.nc")
which = sys.argv[3:] or ["linear", "harmonic", "mk", "ccdc", "landtrendr"]
def arrays(r):
    cube = getattr(r, "_cube", None)
    if cube is None:
        a = np.stack([np.where(m, np.nan, x) for x, m in zip(r._arrays, r._masks)]) if hasattr(r, "_arrays") else None
        return a, []
    v = cube.variable()
    a = np.where(v.mask, np.nan, v.data.astype(float))
    return a.reshape(-1, *a.shape[2:]), v.names_of_bands()
def dump(name, r):
    a, names = arrays(r)
    np.save(os.path.join(o, name + ".npy"), a)
    print(name, "bands", names, "shape", a.shape)
t = time.time()
if "linear" in which:
    dump("linear", GenerateTrendRaster(nc, "StdTime", "ndvi", "LINEAR", None, "DATA", None, "YEARS", "RMSE", "R2", "SLOPEPVALUE"))
if "harmonic" in which:
    dump("harmonic", GenerateTrendRaster(nc, "StdTime", "ndvi", "HARMONIC", 1, "DATA", 1, "YEARS", "RMSE", "R2", "SLOPEPVALUE"))
if "poly" in which:
    dump("poly", GenerateTrendRaster(nc, "StdTime", "ndvi", "POLYNOMIAL", 2, "DATA", None, "YEARS", "RMSE", "R2", "SLOPEPVALUE"))
if "mk" in which:
    dump("mk", GenerateTrendRaster(nc, "StdTime", "ndvi", "MANN-KENDALL", None, "DATA"))
if "ccdc" in which:
    c = AnalyzeChangesUsingCCDC(nc, None, None, 0.99, 6, 1); c.save(os.path.join(o, "ccdc.nc"))
    dump("ccdc_most_recent", DetectChangeUsingChangeAnalysisRaster(os.path.join(o, "ccdc.nc"), "TIME_OF_LATEST_CHANGE", 1))
    dump("ccdc_largest", DetectChangeUsingChangeAnalysisRaster(os.path.join(o, "ccdc.nc"), "TIME_OF_LARGEST_CHANGE", 1))
if "landtrendr" in which:
    c = AnalyzeChangesUsingLandTrendr(nc); c.save(os.path.join(o, "lt.nc"))
    dump("lt_most_recent", DetectChangeUsingChangeAnalysisRaster(os.path.join(o, "lt.nc"), "TIME_OF_LATEST_CHANGE", 1))
    dump("lt_largest", DetectChangeUsingChangeAnalysisRaster(os.path.join(o, "lt.nc"), "TIME_OF_LARGEST_CHANGE", 1))
print("s", time.time() - t)
