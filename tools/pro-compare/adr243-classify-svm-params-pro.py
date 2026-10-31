# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr243-classify-svm-params-pro.py <scratch>   (needs image2.tif and image3.tif, see its docstring)
"""Do Pro's SVM C and gamma change with the data? Train on two other images and print what Pro chose.

The two images were made from image.tif with rasterio, with the same profile:
image2.tif = clip(image // 2 + integers(0, 30) drawn by numpy.random.default_rng(3), 0, 255) as uint8;
image3.tif = image * 3.7 + 11 as float32."""
import arcpy, os, sys, json
from arcpy.ia import *
d = sys.argv[1]; o = os.path.join(d, "pro_cls"); arcpy.env.overwriteOutput = True
arcpy.CheckOutExtension("ImageAnalyst")
for name in ("image2", "image3"):
    ecd = os.path.join(o, f"svm_{name}.ecd")
    TrainSupportVectorMachineClassifier(os.path.join(d, name + ".tif"), os.path.join(d, "training.shp"), ecd, None, 0)
    ClassifyRaster(os.path.join(d, name + ".tif"), ecd).save(os.path.join(o, f"svm_{name}.tif"))
    D = json.load(open(ecd))["Definitions"][0]
    print(name, {k: D[k] for k in ("kernel_type", "gamma", "costC", "Mins", "Maxs", "CrossValidationRate", "total_sv")})
