"""ADR-127 condition 1: ArcGIS Pro exports an image service on this server to a file, and the file holds the values.

    "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" export-raster.py <…/ImageServer> <token|-> <source.tif> <out folder>

Copy Raster is the tool behind Export Raster. The service is opened by its address (with ?token= when it is not
public), written to a GeoTIFF, and compared cell by cell with the file the service was published from. Measured with
Pro 3.7 on 2026-10-14.
"""
import os
import sys

import arcpy
import numpy

service, token, source, out = sys.argv[1:5]
arcpy.env.overwriteOutput = True
address = service if token == "-" else f"{service}?token={token}"
written = os.path.join(out, "pro-export.tif")

arcpy.management.CopyRaster(address, written)
print("messages:", arcpy.GetMessages(1) or "none")

for label, path in (("service", address), ("export", written), ("source", source)):
    d = arcpy.Describe(path)
    print(f"{label}: {d.width}x{d.height} bands {d.bandCount} pixel {getattr(d, 'pixelType', '?')} "
          f"sr {d.spatialReference.factoryCode} extent {d.extent.XMin:.4f} {d.extent.YMin:.4f} {d.extent.XMax:.4f} {d.extent.YMax:.4f}")

exported = arcpy.RasterToNumPyArray(written)
original = arcpy.RasterToNumPyArray(source)
print("export min/max", exported.min(), exported.max(), "source min/max", original.min(), original.max())
if exported.shape == original.shape:
    differ = int(numpy.count_nonzero(exported != original))
    print(f"cells that differ: {differ} of {exported.size}")
else:
    print("shapes differ:", exported.shape, original.shape)
