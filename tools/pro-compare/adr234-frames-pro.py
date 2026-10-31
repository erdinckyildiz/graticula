"""ArcGIS Pro side of ADR-234 condition 5: a Frame Camera mosaic dataset from fr/frames.csv; each item's footprint and
its orthorectified image (whose bands are the image column and row Pro sampled at each ground cell).

frames_pro.py <work folder>. Writes <work>/fr/pro/footprints.csv and <work>/fr/pro/<name>.tif."""
import csv
import os
import sys

import arcpy

work = os.path.join(sys.argv[1], "fr")
out = os.path.join(work, "pro")
os.makedirs(out, exist_ok=True)
arcpy.env.overwriteOutput = True
gdb = os.path.join(out, "frames.gdb")
if arcpy.Exists(gdb):
    arcpy.management.Delete(gdb)
arcpy.management.CreateFileGDB(out, "frames.gdb")
sr = arcpy.SpatialReference(32635)
md = arcpy.management.CreateMosaicDataset(gdb, "md", sr, 2, "32_BIT_FLOAT")[0]
arcpy.management.AddRastersToMosaicDataset(md, "Frame Camera", os.path.join(work, "frames.csv"), aux_inputs="CameraFile " + os.path.join(work, "cameras.csv"))
print(arcpy.GetMessages())
with open(os.path.join(out, "footprints.csv"), "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["Name", "LowPS", "CenterX", "CenterY", "wkt", "HighPS"])
    with arcpy.da.SearchCursor(md, ["Name", "LowPS", "CenterX", "CenterY", "SHAPE@WKT", "HighPS"]) as rows:
        for r in rows:
            w.writerow(r)
            print(r[0], r[1], r[2], r[3])
arcpy.env.cellSize = 0.2
arcpy.env.resamplingMethod = "BILINEAR"
with arcpy.da.SearchCursor(md, ["OBJECTID", "Name", "SHAPE@"]) as rows:
    for oid, name, shape in list(rows):
        layer = arcpy.management.MakeMosaicLayer(md, f"lyr{oid}", f"OBJECTID = {oid}")[0]
        arcpy.env.extent = shape.extent
        arcpy.management.CopyRaster(layer, os.path.join(out, f"{name}.tif"), pixel_type="32_BIT_FLOAT",
                                    nodata_value="-9999")
        arcpy.env.extent = None
        print("copied", name)
