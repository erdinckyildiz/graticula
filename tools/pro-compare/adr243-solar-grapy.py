# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr243-solar-grapy.py <scratch> <worktree>
"""ADR-243 condition 1, grapy side: the same run as solar_pro.py."""
import os, sys, time
sys.path.insert(0, sys.argv[2] + "/src/grapy")
import grapy as arcpy
from grapy.sa import *
d = sys.argv[1]; o = os.path.join(d, "grapy_solar"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
dem = os.path.join(d, "dem.tif")
tc = TimeWithinDay(172, 0, 24)
t = time.time()
g = AreaSolarRadiation(dem, 40, 200, tc, 14, 0.5, "NOINTERVAL", 1, "FROM_DEM", 32, 8, 8, "UNIFORM_SKY", 0.3, 0.5,
                       os.path.join(o, "dir.tif"), os.path.join(o, "dif.tif"), os.path.join(o, "dur.tif"))
g.save(os.path.join(o, "glb.tif"))
print("area s", time.time() - t)
import json
src = os.path.join(d, "points.gpkg")
if not os.path.exists(src):
    arcpy.management.CreateFeatureclass(src, "points", "POINT", spatial_reference=arcpy.SpatialReference(32636))
    arcpy.management.AddField(src + "/points", "pid", "LONG")
    with arcpy.da.InsertCursor(src + "/points", ["pid", "SHAPE@XY"]) as c:
        for k, p in enumerate(json.load(open(os.path.join(d, "points.json")))):
            c.insertRow((k, tuple(p)))
gp = os.path.join(o, "points.gpkg")
if os.path.exists(gp): os.remove(gp)
PointsSolarRadiation(dem, src + "/points", gp + "/pglb", 0, 40, 200, tc, 14, 0.5, "NOINTERVAL", 1,
                     "FROM_DEM", 32, 8, 8, "UNIFORM_SKY", 0.3, 0.5, gp + "/pdir", gp + "/pdif", gp + "/pdur")
for n in ("pglb", "pdir", "pdif", "pdur"):
    print(n, [r for r in arcpy.da.SearchCursor(gp + "/" + n, ["T0", "SHAPE@XY"])])
json.dump({n: [r[0] for r in arcpy.da.SearchCursor(gp + "/" + n, ["T0"])] for n in ("pglb", "pdir", "pdif", "pdur")},
          open(os.path.join(o, "points.json"), "w"))
print("fields", [f.name for f in arcpy.ListFields(gp + "/pglb")])
