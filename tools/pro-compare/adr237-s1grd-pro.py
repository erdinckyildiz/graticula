"""ArcGIS Pro side of ADR-237 condition 3 (GRD half): thermal noise removal, sigma nought calibration and geometric
terrain correction on the ellipsoid, VV, of a real Sentinel-1 IW GRD SAFE. Each step's output is copied to a GeoTIFF.

s1_pro.py <SAFE folder> <out folder>"""
import os
import sys
import time

import arcpy

safe, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
arcpy.CheckOutExtension("ImageAnalyst")
arcpy.env.overwriteOutput = True
manifest = os.path.join(safe, "manifest.safe")
steps = []


def step(name, fn):
    t = time.time()
    result = fn()
    steps.append((name, time.time() - t))
    print(name, f"{time.time() - t:.1f} s", arcpy.GetMessages(1)[-300:] if arcpy.GetMessages(1) else "")
    return result


def save(raster, path):
    raster.save(path)
    return path


noise = step("RemoveThermalNoise", lambda: save(arcpy.ia.RemoveThermalNoise(manifest, polarization_bands="VV"),
                                                os.path.join(out, "noise.crf")))
cal = step("ApplyRadiometricCalibration", lambda: save(arcpy.ia.ApplyRadiometricCalibration(
    noise, polarization_bands="VV", calibration_type="SIGMA_NOUGHT"), os.path.join(out, "sigma0.crf")))
gtc = step("ApplyGeometricTerrainCorrection", lambda: save(arcpy.ia.ApplyGeometricTerrainCorrection(
    cal, polarization_bands="VV"), os.path.join(out, "gtc.crf")))
for name in ("noise", "sigma0", "gtc"):
    step(f"CopyRaster {name}", lambda name=name: arcpy.management.CopyRaster(
        os.path.join(out, f"{name}.crf"), os.path.join(out, f"{name}.tif"), pixel_type="32_BIT_FLOAT"))
    d = arcpy.Describe(os.path.join(out, f"{name}.tif"))
    print(name, d.width, d.height, d.meanCellWidth, d.spatialReference.name, d.extent)
