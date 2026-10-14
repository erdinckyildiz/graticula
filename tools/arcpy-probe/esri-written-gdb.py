"""An Esri-written file geodatabase for ADR-065 condition 3 and ADR-087 condition 1.

pipes: a subtype field `kind` (1 Main, 2 Service); `material` takes Material on Main and ServiceMaterial on Service;
`diameter` takes the range Diameter on both. valves: `material` takes Material too, so one domain is shared by two
classes. Written by ArcGIS Pro's own arcpy, so the GDB_Items XML is Esri's, not GDAL's.

Run with Pro's Python (measured with Pro 3.7, 2026-10-14):

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" esri-written-gdb.py <out folder>

then import-and-read.py with the zip it writes. The archive is not committed (Q-138); this script is how to make it.
"""
import os
import shutil
import sys
import zipfile

import arcpy

out = sys.argv[1]
gdb = os.path.join(out, "esri_written.gdb")
if os.path.exists(gdb):
    shutil.rmtree(gdb)
arcpy.management.CreateFileGDB(out, "esri_written.gdb")
sr = arcpy.SpatialReference(4326)

arcpy.management.CreateDomain(gdb, "Material", "Pipe material", "TEXT", "CODED")
for code, name in [("CI", "Cast iron"), ("DI", "Ductile iron"), ("PE", "Polyethylene")]:
    arcpy.management.AddCodedValueToDomain(gdb, "Material", code, name)
arcpy.management.CreateDomain(gdb, "ServiceMaterial", "Service line material", "TEXT", "CODED")
for code, name in [("PE", "Polyethylene"), ("CU", "Copper")]:
    arcpy.management.AddCodedValueToDomain(gdb, "ServiceMaterial", code, name)
arcpy.management.CreateDomain(gdb, "Diameter", "Nominal diameter, mm", "SHORT", "RANGE")
arcpy.management.SetValueForRangeDomain(gdb, "Diameter", 50, 2000)

pipes = os.path.join(gdb, "pipes")
arcpy.management.CreateFeatureclass(gdb, "pipes", "POLYLINE", spatial_reference=sr)
arcpy.management.AddField(pipes, "kind", "SHORT")
arcpy.management.AddField(pipes, "material", "TEXT", field_length=8)
arcpy.management.AddField(pipes, "diameter", "SHORT")
arcpy.management.AddField(pipes, "label", "TEXT", field_length=20)
arcpy.management.SetSubtypeField(pipes, "kind")
arcpy.management.AddSubtype(pipes, 1, "Main")
arcpy.management.AddSubtype(pipes, 2, "Service")
arcpy.management.SetDefaultSubtype(pipes, 1)
arcpy.management.AssignDomainToField(pipes, "material", "Material", "1: Main")
arcpy.management.AssignDomainToField(pipes, "material", "ServiceMaterial", "2: Service")
arcpy.management.AssignDomainToField(pipes, "diameter", "Diameter", ["1: Main", "2: Service"])

valves = os.path.join(gdb, "valves")
arcpy.management.CreateFeatureclass(gdb, "valves", "POINT", spatial_reference=sr)
arcpy.management.AddField(valves, "material", "TEXT", field_length=8)
arcpy.management.AssignDomainToField(valves, "material", "Material")

with arcpy.da.InsertCursor(pipes, ["SHAPE@", "kind", "material", "diameter", "label"]) as rows:
    line = lambda a, b: arcpy.Polyline(arcpy.Array([arcpy.Point(*a), arcpy.Point(*b)]), sr)
    rows.insertRow([line((29.00, 41.00), (29.01, 41.00)), 1, "DI", 600, "A"])
    rows.insertRow([line((29.01, 41.00), (29.01, 41.01)), 2, "CU", 50, "B"])
with arcpy.da.InsertCursor(valves, ["SHAPE@XY", "material"]) as rows:
    rows.insertRow([(29.005, 41.0), "CI"])

archive = os.path.join(out, "esri_written.gdb.zip")
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(gdb):
        for f in files:
            if not f.endswith(".lock"):
                full = os.path.join(root, f)
                z.write(full, os.path.relpath(full, out))
print("wrote", archive, os.path.getsize(archive))
for d in arcpy.da.ListDomains(gdb):
    print("domain", d.name, d.domainType, d.codedValues or d.range)
for k, v in arcpy.da.ListSubtypes(pipes).items():
    print("subtype", k, v["Name"], {f: (x[1].name if x[1] else None) for f, x in v["FieldValues"].items() if f in ("material", "diameter")})
