# A comparison with ArcGIS Pro 3.7 (ADR-236, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr236-pro.py <scratch> [linear harmonic poly mk ccdc landtrendr]
"""ADR-236 condition 3, Pro side: trend rasters, CCDC and LandTrendr on series.nc (written by grapy), dumped to npz."""
import arcpy, os, sys, numpy as np, json
from arcpy.ia import *
d = sys.argv[1]; o = os.path.join(d, "pro_md"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
arcpy.CheckOutExtension("ImageAnalyst")
nc = os.path.join(d, "series.nc")
src = arcpy.Raster(nc, True)
# CCDC fails on the netCDF itself (ERROR 999999), so the change tools read a CRF copy of the same slices.
crf = os.path.join(o, "series.crf")
if not arcpy.Exists(crf) and ("ccdc" in sys.argv[2:] or "landtrendr" in sys.argv[2:]):
    arcpy.management.CopyRaster(nc, crf, process_as_multidimensional="ALL_SLICES")
print("Pro reads:", src.variables, src.slices[:2] if hasattr(src, "slices") else "", src.bandCount)
which = sys.argv[2:] or ["linear", "harmonic", "mk", "ccdc", "landtrendr"]
out = {}
def dump(name, r):
    path = os.path.join(o, name + ".crf"); r.save(path); r = arcpy.Raster(path)
    names = [b for b in r.bandNames] if hasattr(r, "bandNames") else []
    arr = arcpy.RasterToNumPyArray(r, nodata_to_value=np.nan)
    np.save(os.path.join(o, name + ".npy"), arr)
    print(name, "bands", names, "shape", arr.shape, "md", r.isMultidimensional)
    return r
if "linear" in which:
    dump("linear", GenerateTrendRaster(nc, "StdTime", "ndvi", "LINEAR", None, "DATA", None, "YEARS", "RMSE", "R2", "SLOPEPVALUE"))
if "harmonic" in which:
    dump("harmonic", GenerateTrendRaster(nc, "StdTime", "ndvi", "HARMONIC", 1, "DATA", 1, "YEARS", "RMSE", "R2", "SLOPEPVALUE"))
if "poly" in which:
    dump("poly", GenerateTrendRaster(nc, "StdTime", "ndvi", "POLYNOMIAL", 2, "DATA", None, "YEARS", "RMSE", "R2", "SLOPEPVALUE"))
if "mk" in which:
    dump("mk", GenerateTrendRaster(nc, "StdTime", "ndvi", "MANN-KENDALL", None, "DATA"))
if "ccdc" in which:
    if not arcpy.Exists(os.path.join(o, "ccdc.crf")):
        AnalyzeChangesUsingCCDC(crf, None, None, 0.99, 6, 1).save(os.path.join(o, "ccdc.crf"))
    for mode in ("MOST_RECENT", "LARGEST"):
        dump("ccdc_" + mode.lower(), DetectChangeUsingChangeAnalysisRaster(os.path.join(o, "ccdc.crf"), "TIME_OF_LATEST_CHANGE" if mode == "MOST_RECENT" else "TIME_OF_LARGEST_CHANGE", max_number_changes=1))
if "landtrendr" in which:
    if not arcpy.Exists(os.path.join(o, "lt.crf")):
        AnalyzeChangesUsingLandTrendr(crf).save(os.path.join(o, "lt.crf"))
    for mode in ("MOST_RECENT", "LARGEST"):
        dump("lt_" + mode.lower(), DetectChangeUsingChangeAnalysisRaster(os.path.join(o, "lt.crf"), "TIME_OF_LATEST_CHANGE" if mode == "MOST_RECENT" else "TIME_OF_LARGEST_CHANGE", max_number_changes=1))
