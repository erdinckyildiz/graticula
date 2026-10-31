# A comparison with ArcGIS Pro 3.7 (ADR-233, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr233-kriging-pro.py <scratch> [same-grid]
"""ADR-233 condition 4, Pro side: arcpy.sa.Kriging, ordinary, spherical, range 700, partial sill 4, nugget 0.3 held,
the nearest 12 points (RadiusVariable(12)), 50 m cells; prediction and variance."""
import arcpy, json, os, sys
from arcpy.sa import *
d = sys.argv[1]; o = os.path.join(d, "pro_k"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True; arcpy.CheckOutExtension("Spatial")
shp = os.path.join(d, "points_k.shp")
if not arcpy.Exists(shp):
    arcpy.management.CreateFeatureclass(d, "points_k.shp", "POINT", spatial_reference=arcpy.SpatialReference(32636))
    arcpy.management.AddField(shp, "z", "DOUBLE")
    with arcpy.da.InsertCursor(shp, ["z", "SHAPE@XY"]) as c:
        for x, y, z in json.load(open(os.path.join(d, "points_k.json"))):
            c.insertRow((z, (x, y)))
# Two runs: Pro's own grid (default), and grapy's grid (the points' extent exactly) so the cells coincide.
if len(sys.argv) > 2 and sys.argv[2] == "same-grid":
    arcpy.env.extent = arcpy.Extent(500000, 4428000, 502000, 4430000)
    o = os.path.join(d, "pro_k_same"); os.makedirs(o, exist_ok=True)
k = Kriging(shp, "z", KrigingModelOrdinary("SPHERICAL", 100, 700, 4, 0.3), 50, RadiusVariable(12),
            os.path.join(o, "var.tif"))
k.save(os.path.join(o, "pred.tif"))
print(k.extent, k.meanCellWidth, k.height, k.width)
