"""ADR-052 condition 1: a CIM renderer that ArcGIS Pro wrote, with an opaque colour and a half-transparent one.

A polygon layer whose fill is red at 50 % opacity and whose outline is black and opaque, saved by Pro as a .lyrx; the
renderer inside it is printed as Pro wrote it. Run with Pro's Python (measured with Pro 3.7, 2026-10-14):

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" pro-written-cim.py <out folder>

It writes <out folder>/pro-written-renderer.json, which is what PUT /admin/layers/{name}/symbology takes.
"""
import json
import os
import shutil
import sys

import arcpy

out = sys.argv[1]
gdb = os.path.join(out, "cim_probe.gdb")
if os.path.exists(gdb):
    shutil.rmtree(gdb)
arcpy.management.CreateFileGDB(out, "cim_probe.gdb")
arcpy.management.CreateFeatureclass(gdb, "squares", "POLYGON", spatial_reference=arcpy.SpatialReference(4326))

lyrx = os.path.join(out, "squares.lyrx")
arcpy.management.MakeFeatureLayer(os.path.join(gdb, "squares"), "squares")
arcpy.management.SaveToLayerFile("squares", lyrx)

layer_file = arcpy.mp.LayerFile(lyrx)
layer = layer_file.listLayers()[0]
symbology = layer.symbology
symbology.renderer.symbol.color = {"RGB": [200, 30, 30, 50]}
symbology.renderer.symbol.outlineColor = {"RGB": [0, 0, 0, 100]}
layer.symbology = symbology
layer_file.save()

document = json.load(open(lyrx, encoding="utf-8"))
renderer = document["layerDefinitions"][0]["renderer"]
json.dump(renderer, open(os.path.join(out, "pro-written-renderer.json"), "w", encoding="utf-8"))
for part in renderer["symbol"]["symbol"]["symbolLayers"]:
    print(part["type"], part["color"])
