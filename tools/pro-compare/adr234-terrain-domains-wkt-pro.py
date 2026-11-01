# ArcGIS Pro 3.7's default XY domain for the EPSG codes Pro does not know by number, given as ESRI WKT (D-339,
# 2026-11-01). A tool, not a test: it needs Pro.
# 1. <grapy python> -c "..." writes unknown.json: [code, name, pyproj's WKT1_ESRI] for every projected and geographic
#    2D EPSG CRS pyproj knows and src/grapy/grapy/_terrain_domains.txt does not hold (190 on 2026-11-01; pyproj writes
#    no ESRI WKT for 10 of them).
# 2. "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" -I adr234-terrain-domains-wkt-pro.py
#    unknown.json out.txt writes "code xmin ymin factorycode type" for each WKT Pro loads (131), "code FAIL" for the
#    rest (49: Pro refuses the WKT, so no terrain can be built in Pro in that system and there is nothing to follow).
# The loaded ones are the second section of src/grapy/grapy/_terrain_domains.txt. Checked on 3207 and 3152 with
# adr234-terrain-corner-pro.py (WKT_FILE): the rule from these origins reproduces all 553 of Pro's kept points.
import json
import sys

import arcpy

rows = json.load(open(sys.argv[1]))
ok = fail = 0
with open(sys.argv[2], "w") as out:
    for code, name, wkt in rows:
        try:
            sr = arcpy.SpatialReference()
            sr.loadFromString(wkt)
            d = sr.domain.split()
            out.write(f"{code} {d[0]} {d[1]} {sr.factoryCode} {sr.type}\n")
            ok += 1
        except Exception as error:
            out.write(f"{code} FAIL {type(error).__name__}\n")
            fail += 1
print("loaded", ok, "failed", fail)
