# A comparison with ArcGIS Pro 3.7 (ADR-233, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr233-kriging-grid-pro.py <scratch>
"""How Pro lays out a point interpolator's grid when no extent is set: Idw at several cell sizes on points_k.shp."""
import arcpy, sys, os
from arcpy.sa import *
arcpy.CheckOutExtension("Spatial"); arcpy.env.overwriteOutput = True
shp = os.path.join(sys.argv[1], "points_k.shp")
for cell in (50, 45, 70, 300, 2000):
    r = Idw(shp, "z", cell)
    e = r.extent
    print(cell, e.XMin - 500000, e.YMin - 4428000, e.XMax - 500000, e.YMax - 4428000, r.width, r.height)
