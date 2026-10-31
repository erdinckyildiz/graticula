"""ADR-244 condition 2: ObserverPoints' Value encoding on two observers, and Viewshed2 on the same DEM, in ArcGIS Pro
and grapy. A tool, not a test: it needs ArcGIS Pro and never runs in CI.

    <Pro python>    adr244-observers.py inputs  <folder>
    <Pro python>    adr244-observers.py pro     <folder>
    <grapy python>  adr244-observers.py grapy   <folder> <worktree>
    <grapy python>  adr244-observers.py compare <folder> <worktree>

A 60 x 60 DEM at 10 m on UTM 35N with a ridge between two observers, so each sees cells the other does not.
"""
import os
import sys

import numpy as np

step, folder = sys.argv[1], sys.argv[2]
X0, Y0 = 500000.0, 4500000.0

if step == "inputs":
    import arcpy
    arcpy.env.overwriteOutput = True
    os.makedirs(folder, exist_ok=True)
    sr = arcpy.SpatialReference(32635)
    r, k = np.indices((60, 60), dtype=float)
    dem = (100 + 0.2 * r + 25 * np.exp(-((k - 30) ** 2) / 18.0) * (r > 12)
           + 12 * np.exp(-((r - 45) ** 2 + (k - 10) ** 2) / 30.0))
    path = os.path.join(folder, "dem.tif")
    arcpy.NumPyArrayToRaster(dem.astype(np.float32), arcpy.Point(X0, Y0), 10, 10).save(path)
    arcpy.management.DefineProjection(path, sr)
    pts = np.array([(X0 + 105.0, Y0 + 305.0, 2.0), (X0 + 495.0, Y0 + 405.0, 2.0)],
                   dtype=[("X", "f8"), ("Y", "f8"), ("OFFSETA", "f8")])
    shp = os.path.join(folder, "observers.shp")
    arcpy.da.NumPyArrayToFeatureClass(pts, shp, ["X", "Y"], sr)
    gpkg = os.path.join(folder, "observers.gpkg")
    if not arcpy.Exists(gpkg):
        arcpy.management.CreateSQLiteDatabase(gpkg, "GEOPACKAGE")
    arcpy.conversion.ExportFeatures(shp, gpkg + "/observers")
    print("written")
    sys.exit(0)

if step in ("pro", "grapy"):
    if step == "grapy":
        sys.path.insert(0, os.path.join(sys.argv[3], "src", "grapy"))
        import grapy as arcpy
        observers = os.path.join(folder, "observers.gpkg", "observers")
    else:
        import arcpy
        arcpy.CheckOutExtension("Spatial")
        observers = os.path.join(folder, "observers.shp")
    arcpy.env.overwriteOutput = True
    dem = os.path.join(folder, "dem.tif")
    out = arcpy.sa.ObserverPoints(dem, observers)
    out.save(os.path.join(folder, f"{step}_obs.tif"))
    fields = [f.name for f in arcpy.ListFields(os.path.join(folder, f"{step}_obs.tif"))]
    print(step, "fields", fields)
    keep = [f for f in fields if f.upper() in ("VALUE", "COUNT") or f.upper().startswith("OBS")]
    with arcpy.da.SearchCursor(os.path.join(folder, f"{step}_obs.tif"), keep) as rows:
        for row in sorted(rows):
            print(step, dict(zip(keep, row)))
    for analysis in ("FREQUENCY", "OBSERVERS"):
        arcpy.sa.Viewshed2(dem, observers, analysis_type=analysis).save(
            os.path.join(folder, f"{step}_v2_{analysis.lower()}.tif"))
    print(step, "viewshed2 written")
    sys.exit(0)

sys.path.insert(0, os.path.join(sys.argv[3], "src", "grapy"))
import grapy as arcpy  # noqa: E402


def read(name):
    raster = arcpy.Raster(os.path.join(folder, name))
    a = arcpy.RasterToNumPyArray(raster).astype(float)
    nd = raster.noDataValue
    return np.where(a == nd, np.nan, a) if nd is not None else a


for name in ("obs", "v2_frequency", "v2_observers"):
    p, g = read(f"pro_{name}.tif"), read(f"grapy_{name}.tif")
    both = ~np.isnan(p) & ~np.isnan(g)
    values = sorted(set(np.unique(p[both]).tolist()) | set(np.unique(g[both]).tolist()))
    table = {v: (int((p == v).sum()), int((g == v).sum())) for v in values}
    print(f"{name}: cells differing {int((p[both] != g[both]).sum())} of {int(both.sum())}, NoData differing "
          f"{int((np.isnan(p) != np.isnan(g)).sum())}; value: (Pro, grapy) counts {table}")
