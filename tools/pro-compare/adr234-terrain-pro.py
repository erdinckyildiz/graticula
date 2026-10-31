"""ArcGIS Pro side of ADR-234 condition 4: terrains built from points.csv, each level's points written to CSV.

Run with Pro's Python: terrain_pro.py <work folder>. Writes <work>/pro/<terrain>_<resolution>.csv (x, y, z)."""
import csv
import os
import sys

import arcpy

work = sys.argv[1]
arcpy.CheckOutExtension("3D")
arcpy.env.overwriteOutput = True
out = os.path.join(work, "pro")
os.makedirs(out, exist_ok=True)
gdb = os.path.join(work, "pro_terrain.gdb")
if arcpy.Exists(gdb):
    arcpy.management.Delete(gdb)
arcpy.management.CreateFileGDB(work, "pro_terrain.gdb")
sr = arcpy.SpatialReference(32635)
fds = arcpy.management.CreateFeatureDataset(gdb, "fds", sr)[0]
pts = arcpy.management.XYTableToPoint(os.path.join(work, "points.csv"), os.path.join(fds, "pts"), "X", "Y", "Z", sr)[0]
print("points", arcpy.management.GetCount(pts)[0])

WS_LEVELS = ["4 1000", "8 5000", "16 10000"]
ZT_LEVELS = ["0.5 1000", "2 5000"]
plans = [(f"ws_{m}", "WINDOWSIZE", m, WS_LEVELS) for m in ("ZMIN", "ZMAX", "ZMEAN", "ZMINMAX")]
plans.append(("zt", "ZTOLERANCE", "ZMIN", ZT_LEVELS))
for name, pyramid, method, levels in plans:
    t = arcpy.ddd.CreateTerrain(fds, name, 2, None, None, pyramid, method)[0]
    arcpy.ddd.AddTerrainPyramidLevel(t, "", levels)
    arcpy.ddd.AddFeatureClassToTerrain(t, [[pts, "Shape", "masspoints", 1, 0, levels[-1].split()[0], True, False, "", "", False]])
    arcpy.ddd.BuildTerrain(t)
    for resolution in [0] + [float(l.split()[0]) for l in levels]:
        fc = arcpy.ddd.TerrainToPoints(t, os.path.join(gdb, f"{name}_{str(resolution).replace('.', '_')}"),
                                       resolution, None, "POINT")[0]
        with open(os.path.join(out, f"{name}_{resolution:g}.csv"), "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["x", "y", "z"])
            with arcpy.da.SearchCursor(fc, ["SHAPE@X", "SHAPE@Y", "SHAPE@Z"]) as rows:
                for r in rows:
                    w.writerow([f"{r[0]:.4f}", f"{r[1]:.4f}", f"{r[2]:.4f}"])
        print(name, resolution, arcpy.management.GetCount(fc)[0])
    d = arcpy.Describe(t)
    print(name, "extent", d.extent.XMin, d.extent.YMin, d.extent.XMax, d.extent.YMax)
