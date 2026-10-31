"""grapy's reading of fr/frames.csv against Pro's (ADR-234 condition 5): footprints, and the image position every ground
cell of Pro's orthorectified item maps to.

frames_compare.py <worktree> <work folder>. Needs frames_pro.py's output in <work>/fr/pro."""
import csv
import os
import shutil
import sqlite3
import sys
import warnings

import numpy as np

sys.path.insert(0, os.path.join(sys.argv[1], "src", "grapy"))
import grapy as arcpy  # noqa: E402
import rasterio  # noqa: E402
import shapely  # noqa: E402
from grapy import _ortho, da  # noqa: E402

work = os.path.join(sys.argv[2], "fr")
run = os.path.join(work, "grapy")
shutil.rmtree(run, ignore_errors=True)
os.makedirs(run)
gpkg = arcpy.management.CreateFileGDB(run, "frames")[0]
md = arcpy.management.CreateMosaicDataset(gpkg, "md", 32635)[0]
arcpy.management.AddRastersToMosaicDataset(md, "Frame Camera", os.path.join(work, "frames.csv"),
                                           aux_inputs="CameraFile " + os.path.join(work, "cameras.csv"))
connection = sqlite3.connect(gpkg)
models = _ortho.models(connection, "md")
connection.close()
grapy_items = {}
with da.SearchCursor(md, ["OBJECTID", "Name", "SHAPE@", "LowPS", "CenterX", "CenterY"]) as rows:
    for oid, name, shape, low, cx, cy in rows:
        grapy_items[name] = (models[oid], shape._shape if hasattr(shape, "_shape") else shape, low, cx, cy)

pro = {}
with open(os.path.join(work, "pro", "footprints.csv")) as f:
    for r in csv.DictReader(f):
        pro[r["Name"]] = (shapely.from_wkt(r["wkt"]), float(r["LowPS"]), float(r["CenterX"]), float(r["CenterY"]))

print(f"{'frame':22s} {'footprint: hausdorff m':>22s} {'area ratio':>10s} {'vertices pro':>12s} "
      f"{'cells':>7s} {'|dcol| max':>10s} {'|drow| max':>10s} {'mean':>8s}  LowPS pro/grapy  Center pro / grapy")
for name, (model, shape, low, cx, cy) in grapy_items.items():
    if name not in pro:
        print(f"{name:22s} not built by Pro")
        continue
    pshape, plow, pcx, pcy = pro[name]
    gshape = shapely.force_2d(shape)
    haus = shapely.hausdorff_distance(pshape, gshape)
    ratio = pshape.area / gshape.area
    nverts = len(shapely.get_coordinates(pshape))
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        with rasterio.open(os.path.join(work, "pro", f"{name}.tif")) as src:
            col_pro, row_pro = src.read(1), src.read(2)
            t = src.transform
            rr, cc = np.mgrid[0:src.height, 0:src.width]
            X, Y = t.c + (cc + 0.5) * t.a, t.f + (rr + 0.5) * t.e
            nodata = src.nodata
    ok = (col_pro != nodata) & np.isfinite(col_pro)
    c, r = model.project(X[ok], Y[ok], np.full(ok.sum(), model.average_z))
    # a pixel's centre (c + 0.5) holds the value c, so bilinear sampling at image position u answers u - 0.5
    inner = (c > 1) & (c < model.columns - 1) & (r > 1) & (r < model.rows - 1)
    dc = np.abs(col_pro[ok][inner] - (c[inner] - 0.5))
    dr = np.abs(row_pro[ok][inner] - (r[inner] - 0.5))
    if not inner.any():
        print(f"{name:22s} Pro's item has no cell inside the image (LowPS {plow:.6f}): not compared")
        continue
    print(f"{name:22s} {haus:22.4f} {ratio:10.5f} {nverts:12d} {int(inner.sum()):7d} {dc.max():10.4f} {dr.max():10.4f} "
          f"{np.mean(np.hypot(dc, dr)):8.4f}  {plow:.4f}/{low:.4f}  ({pcx:.2f}, {pcy:.2f}) / ({cx:.2f}, {cy:.2f})")
