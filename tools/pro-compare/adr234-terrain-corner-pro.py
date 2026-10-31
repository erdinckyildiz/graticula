# A comparison with ArcGIS Pro 3.7 (ADR-234 condition 4, D-339, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr234-terrain-corner-pro.py <work> <tag> <spacing> "4;8;16" <xmin,ymin|default> [dx,dy]  (points.csv from adr234-terrain-points.py)
"""Where Pro's terrain windows start (ADR-234 condition 4, D-339): a ZMIN window size terrain from points.csv,
with the XY domain's origin, the average point spacing, the levels and a point shift each set on the command line.

pro_corner.py <work> <tag> <spacing> <levels "4;8;16"> <domain "xmin,ymin" or "default"> [dx,dy] [xyres]"""
import csv
import os
import sys

import arcpy

work, tag, spacing, levels, domain = sys.argv[1], sys.argv[2], float(sys.argv[3]), sys.argv[4], sys.argv[5]
dx, dy = (float(v) for v in (sys.argv[6] if len(sys.argv) > 6 else "0,0").split(","))
arcpy.CheckOutExtension("3D")
arcpy.env.overwriteOutput = True
out = os.path.join(work, "runs", tag)
os.makedirs(out, exist_ok=True)
gdb = os.path.join(out, "t.gdb")
if arcpy.Exists(gdb):
    arcpy.management.Delete(gdb)
arcpy.management.CreateFileGDB(out, "t.gdb")
sr = arcpy.SpatialReference(32635)
if len(sys.argv) > 7:
    sr.XYResolution = float(sys.argv[7])
    sr.XYTolerance = float(sys.argv[7]) * 10
if domain != "default":
    xmin, ymin = (float(v) for v in domain.split(","))
    width = float(os.environ.get("DOMAIN_WIDTH", "1e7"))
    sr.setDomain(xmin, xmin + width, ymin, ymin + width)
fds = arcpy.management.CreateFeatureDataset(gdb, "fds", sr)[0]
shifted = os.path.join(out, "points.csv")
with open(os.path.join(work, "points.csv")) as f, open(shifted, "w", newline="") as g:
    w = csv.writer(g)
    w.writerow(["PID", "X", "Y", "Z"])
    for r in csv.DictReader(f):
        w.writerow([r["PID"], f"{float(r['X']) + dx:.3f}", f"{float(r['Y']) + dy:.3f}", r["Z"]])
pts = arcpy.management.XYTableToPoint(shifted, os.path.join(fds, "pts"), "X", "Y", "Z", sr)[0]
d = arcpy.Describe(fds).spatialReference
res = [float(v) for v in levels.split(";")]
t = arcpy.ddd.CreateTerrain(fds, "t", spacing, None, None, "WINDOWSIZE", "ZMIN")[0]
arcpy.ddd.AddTerrainPyramidLevel(t, "", [f"{r:g} {1000 * (i + 1)}" for i, r in enumerate(res)])
arcpy.ddd.AddFeatureClassToTerrain(t, [[pts, "Shape", "masspoints", 1, 0, f"{res[-1]:g}", True, False, "", "", False]])
arcpy.ddd.BuildTerrain(t)
ext = arcpy.Describe(t).extent
with open(os.path.join(out, "meta.txt"), "w") as f:
    f.write(f"domain {d.domain}\nres {d.XYResolution}\nspacing {spacing}\nextent {ext.XMin} {ext.YMin} {ext.XMax} {ext.YMax}\n")
for r in [0] + res:
    fc = arcpy.ddd.TerrainToPoints(t, os.path.join(gdb, f"l_{str(r).replace('.', '_')}"), r, None, "POINT")[0]
    with open(os.path.join(out, f"{r:g}.csv"), "w", newline="") as f:
        w = csv.writer(f)
        w.writerow(["x", "y", "z"])
        with arcpy.da.SearchCursor(fc, ["SHAPE@X", "SHAPE@Y", "SHAPE@Z"]) as rows:
            for row in rows:
                w.writerow([f"{row[0]:.4f}", f"{row[1]:.4f}", f"{row[2]:.4f}"])
print(tag, "done", d.domain)
