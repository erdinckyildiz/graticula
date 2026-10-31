# A comparison with ArcGIS Pro 3.7 (ADR-233, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr233-kriging-grapy.py <scratch> <worktree>
"""ADR-233 condition 4, grapy side: the same run as kriging_pro.py."""
import json, os, sys
sys.path.insert(0, sys.argv[2] + "/src/grapy")
import grapy as arcpy
from grapy.sa import *
d = sys.argv[1]; o = os.path.join(d, "grapy_k"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
gp = os.path.join(d, "points_k.gpkg")
if not os.path.exists(gp):
    arcpy.management.CreateFeatureclass(gp, "points", "POINT", spatial_reference=arcpy.SpatialReference(32636))
    arcpy.management.AddField(gp + "/points", "z", "DOUBLE")
    with arcpy.da.InsertCursor(gp + "/points", ["z", "SHAPE@XY"]) as c:
        for x, y, z in json.load(open(os.path.join(d, "points_k.json"))):
            c.insertRow((z, (x, y)))
k = Kriging(gp + "/points", "z", KrigingModelOrdinary("SPHERICAL", 100, 700, 4, 0.3), 50, RadiusVariable(12),
            os.path.join(o, "var.tif"))
k.save(os.path.join(o, "pred.tif"))
print(k.extent, k.height, k.width)
