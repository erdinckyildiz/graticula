"""Where Pro's window size windows start: ZMIN terrains from points.csv shifted by (dx, dy), levels given.

terrain_pro_origin.py <work> <tag> <dx> <dy> <levels as "5;10;20"> [spacing]"""
import csv
import os
import sys

import arcpy

work, tag, dx, dy, levels = sys.argv[1], sys.argv[2], float(sys.argv[3]), float(sys.argv[4]), sys.argv[5]
spacing = float(sys.argv[6]) if len(sys.argv) > 6 else 2
arcpy.CheckOutExtension("3D")
arcpy.env.overwriteOutput = True
out = os.path.join(work, "pro_origin", tag)
os.makedirs(out, exist_ok=True)
gdb = os.path.join(out, "t.gdb")
if arcpy.Exists(gdb):
    arcpy.management.Delete(gdb)
arcpy.management.CreateFileGDB(out, "t.gdb")
sr = arcpy.SpatialReference(32635)
if len(sys.argv) > 7:
    xmin, ymin = (float(v) for v in sys.argv[7].split(","))
    sr.setDomain(xmin, xmin + 1e9, ymin, ymin + 1e9)
print(tag, "domain", sr.domain)
fds = arcpy.management.CreateFeatureDataset(gdb, "fds", sr)[0]
shifted = os.path.join(out, "points.csv")
with open(os.path.join(work, "points.csv")) as f, open(shifted, "w", newline="") as g:
    w = csv.writer(g)
    w.writerow(["PID", "X", "Y", "Z"])
    for r in csv.DictReader(f):
        w.writerow([r["PID"], f"{float(r['X']) + dx:.2f}", f"{float(r['Y']) + dy:.2f}", r["Z"]])
pts = arcpy.management.XYTableToPoint(shifted, os.path.join(fds, "pts"), "X", "Y", "Z", sr)[0]
res = [float(v) for v in levels.split(";")]
t = arcpy.ddd.CreateTerrain(fds, "t", spacing, None, None, "WINDOWSIZE", os.environ.get("WSMETHOD", "ZMIN"))[0]
arcpy.ddd.AddTerrainPyramidLevel(t, "", [f"{r:g} {1000 * (i + 1)}" for i, r in enumerate(res)])
arcpy.ddd.AddFeatureClassToTerrain(t, [[pts, "Shape", "masspoints", 1, 0, f"{res[-1]:g}", True, False, "", "", False]])
arcpy.ddd.BuildTerrain(t)
for r in [0] + res:
    fc = arcpy.ddd.TerrainToPoints(t, os.path.join(gdb, f"l_{str(r).replace('.', '_')}"), r, None, "POINT")[0]
    with open(os.path.join(out, f"{r:g}.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["x", "y", "z"])
        with arcpy.da.SearchCursor(fc, ["SHAPE@X", "SHAPE@Y", "SHAPE@Z"]) as rows:
            for row in rows:
                w.writerow([f"{row[0]:.4f}", f"{row[1]:.4f}", f"{row[2]:.4f}"])
print(tag, "done")
