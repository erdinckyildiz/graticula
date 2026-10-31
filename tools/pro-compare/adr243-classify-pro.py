# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr243-classify-pro.py <scratch>
"""ADR-243 condition 2, Pro side: maximum likelihood and SVM trained on training.gpkg over image.tif, then ClassifyRaster."""
import arcpy, os, sys
from arcpy.ia import *
d = sys.argv[1]; o = os.path.join(d, "pro_cls"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
arcpy.CheckOutExtension("ImageAnalyst"); arcpy.CheckOutExtension("Spatial")
img = os.path.join(d, "image.tif"); tr = os.path.join(d, "training.gpkg", "main.training")
# Pro cannot open grapy's GeoPackage table for training ("table has no geometry"), so the same rectangles are written
# to a shapefile from training.json, which make_image.py wrote beside the GeoPackage.
import json
spec = json.load(open(os.path.join(d, "training.json")))
shp = os.path.join(d, "training.shp")
if arcpy.Exists(shp): arcpy.management.Delete(shp)
arcpy.management.CreateFeatureclass(d, "training.shp", "POLYGON", spatial_reference=arcpy.SpatialReference(32636))
arcpy.management.AddField(shp, "classvalue", "LONG"); arcpy.management.AddField(shp, "classname", "TEXT")
with arcpy.da.InsertCursor(shp, ["classvalue", "classname", "SHAPE@"]) as out:
    for v, bs in spec["boxes"].items():
        for x0, y0, x1, y1 in bs:
            ring = arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1), arcpy.Point(x0, y1), arcpy.Point(x0, y0)])
            out.insertRow((int(v), spec["names"][v], arcpy.Polygon(ring, arcpy.SpatialReference(32636))))
tr = shp
TrainMaximumLikelihoodClassifier(img, tr, os.path.join(o, "ml.ecd"))
ClassifyRaster(img, os.path.join(o, "ml.ecd")).save(os.path.join(o, "ml.tif"))
TrainSupportVectorMachineClassifier(img, tr, os.path.join(o, "svm.ecd"), None, 0)
ClassifyRaster(img, os.path.join(o, "svm.ecd")).save(os.path.join(o, "svm.tif"))
print("done")
