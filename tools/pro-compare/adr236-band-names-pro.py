# A comparison with ArcGIS Pro 3.7 (ADR-236, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr236-band-names-pro.py <scratch> linear harmonic poly mk
import arcpy, sys, os
for n in sys.argv[2:]:
    print(n, arcpy.Raster(os.path.join(sys.argv[1], "pro_md", n + ".crf")).bandNames)
