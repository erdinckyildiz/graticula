"""ADR-106 conditions 2 and 3: ArcGIS Pro reads this server's exports of a layer whose field names and values are
Turkish, and says which fields carry a domain.

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" read-turkish-exports.py <folder> <name>

<folder> holds <name>.gpkg, gdb/<name>.gdb, shp/<name>.shp, <name>.kml and <name>.esrijson as the export route wrote
them. KML goes through Pro's KML To Layer and the Feature Collection through JSON To Features, which are the tools a
Pro user opens them with. Measured with Pro 3.7 on 2026-10-14.
"""
import os
import sys
import tempfile

import arcpy

folder, name = sys.argv[1:3]
arcpy.env.overwriteOutput = True
work = tempfile.mkdtemp(prefix="pro-reads-")


def show(label, path, workspace=None):
    print(f"== {label}")
    fields = [f for f in arcpy.ListFields(path) if f.type not in ("Geometry", "OID") and not f.name.lower().startswith("shape")]
    for f in fields:
        print(f"   field {f.name!r} {f.type} domain={f.domain!r}")
    with arcpy.da.SearchCursor(path, [f.name for f in fields]) as rows:
        for row in rows:
            print("   row", row)
    if workspace:
        for d in arcpy.da.ListDomains(workspace):
            print(f"   domain {d.name!r} {d.domainType} {d.codedValues or d.range}")


gpkg = os.path.join(folder, f"{name}.gpkg")
show("GeoPackage", os.path.join(gpkg, f"main.{name}"), gpkg)
gdb = os.path.join(folder, "gdb", f"{name}.gdb")
show("File Geodatabase", os.path.join(gdb, name), gdb)
show("Shapefile", os.path.join(folder, "shp", f"{name}.shp"))

arcpy.conversion.KMLToLayer(os.path.join(folder, f"{name}.kml"), work, "from_kml")
kml_gdb = os.path.join(work, "from_kml.gdb")
arcpy.env.workspace = kml_gdb
for dataset in arcpy.ListDatasets() or [""]:
    for fc in arcpy.ListFeatureClasses(feature_dataset=dataset):
        show(f"KML To Layer: {fc}", os.path.join(kml_gdb, dataset, fc))

json_gdb = arcpy.management.CreateFileGDB(work, "from_json").getOutput(0)
arcpy.conversion.JSONToFeatures(os.path.join(folder, f"{name}.json"), os.path.join(json_gdb, "from_json"))
show("JSON To Features", os.path.join(json_gdb, "from_json"))
