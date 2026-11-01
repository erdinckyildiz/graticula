# ArcGIS Pro 3.7's default XY domain for every EPSG code (D-339, 2026-11-01). A tool, not a test: it needs Pro.
# 1. <grapy python> -c "from pyproj.database import query_crs_info; ..." writes codes.txt, one EPSG code a line, of
#    every projected and geographic 2D CRS pyproj knows (5,957 on 2026-11-01).
# 2. "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" -I adr234-terrain-domains-pro.py codes.txt domains.txt
#    writes "code type xmin ymin xyresolution" for each code Pro knows (5,767; 190 unknown to Pro).
# src/grapy/grapy/_terrain_domains.txt keeps code, xmin and ymin; _terrain.default_xy_origin reads it.
# Reads ArcGIS Pro's default XY domain for each EPSG code (arcpy.SpatialReference(code).domain); prints code xmin ymin.
import sys, arcpy
codes = [int(c) for c in open(sys.argv[1]).read().split()]
out = open(sys.argv[2], "w")
known = failed = 0
for code in codes:
    try:
        sr = arcpy.SpatialReference(code)
        d = sr.domain.split()
        out.write(f"{code} {sr.type} {d[0]} {d[1]} {sr.XYResolution}\n")
        known += 1
    except Exception:
        failed += 1
out.close()
print("known", known, "not known to Pro", failed)
