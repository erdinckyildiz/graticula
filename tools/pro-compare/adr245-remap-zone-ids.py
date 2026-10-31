"""ADR-245 condition 1, second table: which records ZonalRemap applies with and without a zone raster, and what
background_value and default_output_value each answer. Run after adr245-colour-remap.py's inputs step; a tool, not a
test (it needs ArcGIS Pro). Pro first, since it writes the table:

    <Pro python>    adr245-remap-zone-ids.py pro   <folder>
    <grapy python>  adr245-remap-zone-ids.py grapy <folder> <worktree>
"""
import os, sys, numpy as np
side, folder = sys.argv[1], sys.argv[2]
if side == "grapy":
    sys.path.insert(0, os.path.join(sys.argv[3], "src", "grapy")); import grapy as arcpy
else:
    import arcpy; arcpy.CheckOutExtension("ImageAnalyst")
arcpy.env.overwriteOutput = True
ia = arcpy.ia
gpkg = os.path.join(folder, "remap.gpkg")
if side == "pro":
    t = arcpy.management.CreateTable(gpkg, "t2")[0]
    for field, kind in (("ZoneID", "LONG"), ("ZoneMin", "DOUBLE"), ("ZoneMax", "DOUBLE"), ("ZoneValue", "DOUBLE")):
        arcpy.management.AddField(t, field, kind)
    with arcpy.da.InsertCursor(t, ["ZoneID", "ZoneMin", "ZoneMax", "ZoneValue"]) as rows:
        rows.insertRow([0, 1, 3, 70]); rows.insertRow([None, 4, 6, 80]); rows.insertRow([1, 7, 9, 90]); rows.insertRow([2, 1, 3, None])
v = arcpy.Raster(os.path.join(folder, "values.tif")); z = arcpy.Raster(os.path.join(folder, "zones.tif"))
t = os.path.join(gpkg, "t2"); t1 = os.path.join(gpkg, "thresholds")
for name, call in (("nz_t2", lambda: ia.ZonalRemap(v, t)),
                   ("z_t2", lambda: ia.ZonalRemap(v, t, z)),
                   ("z_bg_def", lambda: ia.ZonalRemap(v, t1, z, background_value=7, default_output_value=99)),
                   ("nz_bg_def", lambda: ia.ZonalRemap(v, t1, None, background_value=7, default_output_value=99))):
    a = arcpy.RasterToNumPyArray(call(), nodata_to_value=-1)
    print(side, name); print(np.array2string(np.asarray(a, float), max_line_width=220, suppress_small=True))
