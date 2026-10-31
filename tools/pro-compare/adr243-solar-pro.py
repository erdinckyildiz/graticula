# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr243-solar-pro.py <scratch>
"""ADR-243 condition 1, Pro side: Area and Points Solar Radiation on dem.tif, day 172, whole day, defaults."""
import arcpy, json, os, sys
from arcpy.sa import *
d = sys.argv[1]; o = os.path.join(d, "pro_solar"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
arcpy.CheckOutExtension("Spatial")
sr = arcpy.SpatialReference(32636)
shp = os.path.join(d, "points.shp")
if not arcpy.Exists(shp):
    arcpy.management.CreateFeatureclass(d, "points.shp", "POINT", spatial_reference=sr)
    arcpy.management.AddField(shp, "pid", "LONG")
    with arcpy.da.InsertCursor(shp, ["pid", "SHAPE@XY"]) as c:
        for k, p in enumerate(json.load(open(os.path.join(d, "points.json")))):
            c.insertRow((k, tuple(p)))
dem = os.path.join(d, "dem.tif")
tc = TimeWithinDay(172, 0, 24)
g = AreaSolarRadiation(dem, 40, 200, tc, 14, 0.5, "NOINTERVAL", 1, "FROM_DEM", 32, 8, 8, "UNIFORM_SKY", 0.3, 0.5,
                       os.path.join(o, "dir.tif"), os.path.join(o, "dif.tif"), os.path.join(o, "dur.tif"))
g.save(os.path.join(o, "glb.tif"))
arcpy.sa.PointsSolarRadiation(dem, shp, os.path.join(o, "pglb.shp"), 0, 40, 200, tc, 14, 0.5, "NOINTERVAL", 1,
                              "FROM_DEM", 32, 8, 8, "UNIFORM_SKY", 0.3, 0.5, os.path.join(o, "pdir.shp"),
                              os.path.join(o, "pdif.shp"), os.path.join(o, "pdur.shp"))
for n in ("pglb", "pdir", "pdif", "pdur"):
    print(n, [f.name for f in arcpy.ListFields(os.path.join(o, n + ".shp"))])
    print([r for r in arcpy.da.SearchCursor(os.path.join(o, n + ".shp"), ["FID", "T0", "SHAPE@XY"])])
json.dump({n: [r[1] for r in sorted(arcpy.da.SearchCursor(os.path.join(o, n + ".shp"), ["FID", "T0"]))]
           for n in ("pglb", "pdir", "pdif", "pdur")}, open(os.path.join(o, "points.json"), "w"))
