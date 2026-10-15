"""ADR-107 condition 3: ArcGIS Pro reads the files this server exported, and says what reference they are in and where
each point is.

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" read-exports.py <dataset> [<dataset> ...]

A dataset is anything arcpy opens: a .shp, a table inside a .gpkg (`file.gpkg/main.layer`), a feature class inside a
.gdb. Measured with Pro 3.7 on 2026-10-14.
"""
import sys

import arcpy

for path in sys.argv[1:]:
    described = arcpy.Describe(path)
    reference = described.spatialReference
    print(path)
    print("   reference", reference.factoryCode, reference.name, reference.type)
    with arcpy.da.SearchCursor(path, ["name", "SHAPE@XY"]) as rows:
        for name, (x, y) in rows:
            print(f"   {name}: x {x:.6f}  y {y:.6f}")
