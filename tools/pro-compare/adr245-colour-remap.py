"""ADR-245 condition 1: ColorspaceConversion's hue scale and ZonalRemap's threshold ends in ArcGIS Pro and grapy. A
tool, not a test: it needs ArcGIS Pro and never runs in CI.

    <Pro python>    adr245-colour-remap.py inputs  <folder>
    <Pro python>    adr245-colour-remap.py pro     <folder>
    <grapy python>  adr245-colour-remap.py grapy   <folder> <worktree>
    <grapy python>  adr245-colour-remap.py compare <folder> <worktree>

The colour image is every hue at full saturation and value (360 columns), then a darker and a paler row of each, then
grey; the remap has values on and between the thresholds of overlapping records, a zone with no record and NoData.
"""
import colorsys
import os
import sys

import numpy as np

step, folder = sys.argv[1], sys.argv[2]


def colours():
    rows = []
    for v, s in ((1.0, 1.0), (0.6, 1.0), (1.0, 0.5), (0.8, 0.25)):
        rows.append([colorsys.hsv_to_rgb(h / 360.0, s, v) for h in range(360)])
    rows.append([(g / 359.0,) * 3 for g in range(360)])
    return np.rint(np.array(rows) * 255).astype(np.uint8)  # 5 x 360 x 3


if step == "inputs":
    import arcpy
    arcpy.env.overwriteOutput = True
    os.makedirs(folder, exist_ok=True)
    sr = arcpy.SpatialReference(3857)
    rgb = colours()
    paths = []
    for b in range(3):
        p = os.path.join(folder, f"c{b + 1}.tif")
        arcpy.NumPyArrayToRaster(np.ascontiguousarray(rgb[..., b]), arcpy.Point(0, 0), 1, 1).save(p)
        arcpy.management.DefineProjection(p, sr)
        paths.append(p)
    # NumPyArrayToRaster writes a uint8 array as 16-bit; copy the composite to 8-bit, as the page asks for.
    arcpy.management.CompositeBands(";".join(paths), os.path.join(folder, "rgb16.tif"))
    arcpy.management.CopyRaster(os.path.join(folder, "rgb16.tif"), os.path.join(folder, "rgb.tif"),
                                pixel_type="8_BIT_UNSIGNED")
    # Values 0, 0.5, 1 … 12 across, one row per zone 1-4; zone 4 has no record; one NoData value.
    values = np.tile(np.arange(0, 12.5, 0.5, dtype=np.float32), (4, 1))
    values[0, 3] = -9999
    zones = np.repeat(np.arange(1, 5, dtype=np.int32)[:, None], values.shape[1], axis=1)
    for name, a, nd in (("values", values, -9999), ("zones", zones, None)):
        p = os.path.join(folder, name + ".tif")
        (arcpy.NumPyArrayToRaster(a, arcpy.Point(0, 0), 1, 1, nd) if nd is not None
         else arcpy.NumPyArrayToRaster(a, arcpy.Point(0, 0), 1, 1)).save(p)
        arcpy.management.DefineProjection(p, sr)
    gpkg = os.path.join(folder, "remap.gpkg")
    if not arcpy.Exists(gpkg):
        arcpy.management.CreateSQLiteDatabase(gpkg, "GEOPACKAGE")
    table = arcpy.management.CreateTable(gpkg, "thresholds")[0]
    for field, kind in (("ZoneID", "LONG"), ("ZoneMin", "DOUBLE"), ("ZoneMax", "DOUBLE"), ("ZoneValue", "DOUBLE")):
        arcpy.management.AddField(table, field, kind)
    with arcpy.da.InsertCursor(table, ["ZoneID", "ZoneMin", "ZoneMax", "ZoneValue"]) as rows:
        for row in ([1, 2, 5, 10], [1, 5, 8, 20],           # meeting at 5
                    [2, 2, 6, 30], [2, 4, 9, 40],           # overlapping 4-6: which record wins
                    [3, None, 3, 50], [3, 9, None, 60]):    # open-ended
            rows.insertRow(row)
    arcpy.management.CopyRows(table, os.path.join(folder, "thresholds.dbf"))
    print("written")
    sys.exit(0)

if step in ("pro", "grapy"):
    if step == "grapy":
        sys.path.insert(0, os.path.join(sys.argv[3], "src", "grapy"))
        import grapy as arcpy
    else:
        import arcpy
        arcpy.CheckOutExtension("ImageAnalyst")
    arcpy.env.overwriteOutput = True
    ia = arcpy.ia
    hsv = ia.ColorspaceConversion(arcpy.Raster(os.path.join(folder, "rgb.tif")), "rgb_to_hsv")
    hsv.save(os.path.join(folder, f"{step}_hsv.tif"))
    back = ia.ColorspaceConversion(arcpy.Raster(os.path.join(folder, f"{step}_hsv.tif")), "hsv_to_rgb")
    back.save(os.path.join(folder, f"{step}_back.tif"))
    table = os.path.join(folder, "remap.gpkg", "thresholds")
    for name, zone in (("remap", os.path.join(folder, "zones.tif")), ("remap_nozone", None)):
        try:
            ia.ZonalRemap(arcpy.Raster(os.path.join(folder, "values.tif")), table,
                          arcpy.Raster(zone) if zone else None).save(os.path.join(folder, f"{step}_{name}.tif"))
            print(step, name, "written")
        except Exception as e:  # noqa: BLE001
            print(step, name, "FAILED", str(e).splitlines()[0])
    sys.exit(0)

sys.path.insert(0, os.path.join(sys.argv[3], "src", "grapy"))
import grapy as arcpy  # noqa: E402
np.set_printoptions(linewidth=220, suppress=True)


def read(name):
    raster = arcpy.Raster(os.path.join(folder, name))
    if raster.bandCount > 1:
        return np.stack([arcpy.RasterToNumPyArray(raster.getRasterBands(b + 1)).astype(float)
                         for b in range(raster.bandCount)]), raster
    a = arcpy.RasterToNumPyArray(raster).astype(float)
    return (np.where(a == raster.noDataValue, np.nan, a) if raster.noDataValue is not None else a), raster


for name in ("hsv", "back"):
    (p, pr), (g, gr) = read(f"pro_{name}.tif"), read(f"grapy_{name}.tif")
    d = np.abs(p - g)
    print(f"{name}: Pro {pr.pixelType}, grapy {gr.pixelType}; per band max |d| {d.max(axis=(1, 2))}, "
          f"cells differing {[(d[b] > 0).sum() for b in range(3)]}")
p, _ = read("pro_hsv.tif")
print("Pro's hue at 0, 60, 120, 180, 240, 300, 359 degrees:", p[0, 0, [0, 60, 120, 180, 240, 300, 359]])
print("Pro's hue row 0 every 30 degrees:", p[0, 0, ::30])
src = colours().astype(float).transpose(2, 0, 1)
b, _ = read("pro_back.tif")
print("Pro round trip RGB max |d|:", np.abs(b - src).max())
for name in ("remap", "remap_nozone"):
    if not os.path.exists(os.path.join(folder, f"pro_{name}.tif")):
        continue
    (p, pr), (g, gr) = read(f"pro_{name}.tif"), read(f"grapy_{name}.tif")
    print(f"{name}: Pro {pr.pixelType} nodata {pr.noDataValue}; grapy {gr.pixelType}")
    print(" values: ", np.arange(0, 12.5, 0.5))
    print(" Pro:\n", p)
    print(" grapy:\n", g)
