"""grapy side of ADR-234 condition 4: the same terrains as terrain_pro.py, from the same points.csv.

Run with grapy's Python: terrain_grapy.py <worktree> <work folder>. Writes <work>/grapy/<terrain>_<resolution>.csv."""
import csv
import os
import shutil
import sys

sys.path.insert(0, os.path.join(sys.argv[1], "src", "grapy"))
import grapy as arcpy  # noqa: E402
import shapely  # noqa: E402
from grapy import da  # noqa: E402

work = sys.argv[2]
out = os.path.join(work, "grapy")
os.makedirs(out, exist_ok=True)
run = os.path.join(work, "grapy_run")
shutil.rmtree(run, ignore_errors=True)
os.makedirs(run)
arcpy.env.workspace = run
gpkg = arcpy.management.CreateFileGDB(run, "data")[0]
fds = arcpy.management.CreateFeatureDataset(gpkg, "fds", 32635)[0]
pts = arcpy.management.CreateFeatureclass(fds, "pts", "POINT", has_z="ENABLED")[0]
with open(os.path.join(work, "points.csv")) as f, da.InsertCursor(pts, ["SHAPE@"]) as cursor:
    for row in csv.DictReader(f):
        cursor.insertRow([shapely.Point(float(row["X"]), float(row["Y"]), float(row["Z"]))])

WS_LEVELS = ["4 1000", "8 5000", "16 10000"]
ZT_LEVELS = ["0.5 1000", "2 5000"]
plans = [(f"ws_{m}", "WINDOWSIZE", m, WS_LEVELS) for m in ("ZMIN", "ZMAX", "ZMEAN", "ZMINMAX")]
plans.append(("zt", "ZTOLERANCE", "ZMIN", ZT_LEVELS))
for name, pyramid, method, levels in plans:
    t = arcpy.ddd.CreateTerrain(fds, name, 2, None, None, pyramid, method)[0]
    arcpy.ddd.AddTerrainPyramidLevel(t, "", levels)
    arcpy.ddd.AddFeatureClassToTerrain(t, [[pts, "Shape.Z", "masspoints", 1, "", "", True, False, "", "", False]])
    arcpy.ddd.BuildTerrain(t)
    for resolution in [0] + [float(level.split()[0]) for level in levels]:
        fc = arcpy.ddd.TerrainToPoints(t, os.path.join(gpkg, f"{name}_{str(resolution).replace('.', '_')}"),
                                       resolution, None, "POINT")[0]
        n = 0
        with open(os.path.join(out, f"{name}_{resolution:g}.csv"), "w", newline="") as f:
            w = csv.writer(f)
            w.writerow(["x", "y", "z"])
            with da.SearchCursor(fc, ["SHAPE@X", "SHAPE@Y", "SHAPE@Z"]) as rows:
                for r in rows:
                    w.writerow([f"{r[0]:.4f}", f"{r[1]:.4f}", f"{r[2]:.4f}"])
                    n += 1
        print(name, resolution, n)
    d = arcpy.Describe(t)
    print(name, "extent", d.extent.XMin, d.extent.YMin, d.extent.XMax, d.extent.YMax)
