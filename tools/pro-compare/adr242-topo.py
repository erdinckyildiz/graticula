"""ADR-242 condition 3: Topo to Raster on the same contours and spot heights in ArcGIS Pro and grapy. A tool, not a
test: it needs ArcGIS Pro and never runs in CI.

    <Pro python>    adr242-topo.py inputs <folder>              # the true surface, its contours and spot heights
    <Pro python>    adr242-topo.py pro    <folder> <size>       # Pro's Topo to Raster, timed
    <grapy python>  adr242-topo.py grapy  <folder> <size> <worktree>
    <grapy python>  adr242-topo.py compare <folder> <size> <worktree>

The surface is analytic (two Gaussian hills on a tilted plane with a valley), so each answer is also measured against
the truth. <size> is the output's cells per side over the same 2,000 m square (cell 2000/size).
"""
import os
import sys
import time

import numpy as np

step, folder = sys.argv[1], sys.argv[2]
EXTENT = (0.0, 0.0, 2000.0, 2000.0)


def truth(x, y):
    return (200 + 0.03 * x + 0.02 * y
            + 120 * np.exp(-((x - 600) ** 2 + (y - 1300) ** 2) / (2 * 250.0 ** 2))
            + 80 * np.exp(-((x - 1400) ** 2 + (y - 700) ** 2) / (2 * 300.0 ** 2))
            - 30 * np.exp(-((x - y) ** 2) / (2 * 120.0 ** 2)))


if step == "inputs":
    import arcpy
    arcpy.CheckOutExtension("Spatial")
    arcpy.env.overwriteOutput = True
    os.makedirs(folder, exist_ok=True)
    sr = arcpy.SpatialReference(3857)
    n = 400  # a fine 5 m truth to draw contours from
    xs = 2.5 + 5 * np.arange(n)
    gx, gy = np.meshgrid(xs, 2000 - xs)
    dem = arcpy.NumPyArrayToRaster(truth(gx, gy).astype(np.float32), arcpy.Point(0, 0), 5, 5)
    arcpy.management.DefineProjection(dem, sr)
    dem.save(os.path.join(folder, "truth5.tif"))
    arcpy.sa.Contour(os.path.join(folder, "truth5.tif"), os.path.join(folder, "contours.shp"), 10, 0)
    rng = np.random.default_rng(242)
    px, py = rng.uniform(20, 1980, 120), rng.uniform(20, 1980, 120)
    pts = np.array([(x, y, truth(x, y)) for x, y in zip(px, py)],
                   dtype=[("X", "f8"), ("Y", "f8"), ("z", "f8")])
    arcpy.da.NumPyArrayToFeatureClass(pts, os.path.join(folder, "spots.shp"), ["X", "Y"], sr)
    gpkg = os.path.join(folder, "topo.gpkg")
    if not arcpy.Exists(gpkg):
        arcpy.management.CreateSQLiteDatabase(gpkg, "GEOPACKAGE")
    arcpy.conversion.ExportFeatures(os.path.join(folder, "contours.shp"), gpkg + "/contours")
    arcpy.conversion.ExportFeatures(os.path.join(folder, "spots.shp"), gpkg + "/spots")
    print("contours", arcpy.management.GetCount(gpkg + "/contours")[0], "spots 120")
    sys.exit(0)

size = int(sys.argv[3])
cell = 2000.0 / size
out = os.path.join(folder, f"{step}_topo_{size}.tif")
if step in ("pro", "grapy"):
    if step == "grapy":
        sys.path.insert(0, os.path.join(sys.argv[4], "src", "grapy"))
        import grapy as arcpy
    else:
        import arcpy
        arcpy.CheckOutExtension("Spatial")
    arcpy.env.overwriteOutput = True
    arcpy.env.outputCoordinateSystem = arcpy.SpatialReference(3857)
    sa = arcpy.sa
    # The same features: Pro reads the shapefiles (its Topo to Raster fails on a GeoPackage input, "create table"),
    # grapy the GeoPackage exported from them.
    where = (lambda n: os.path.join(folder, n + ".shp")) if step == "pro" else         (lambda n: os.path.join(folder, "topo.gpkg", n))
    inputs = [sa.TopoContour([[where("contours"), "Contour"]]), sa.TopoPointElevation([[where("spots"), "z"]])]
    t = time.perf_counter()
    result = sa.TopoToRaster(inputs, cell, "0 0 2000 2000")
    result.save(out)
    print(f"{step} {size}x{size} cell {cell:g}: {time.perf_counter() - t:.1f} s")
    sys.exit(0)

sys.path.insert(0, os.path.join(sys.argv[4], "src", "grapy"))
import grapy as arcpy  # noqa: E402
xs = cell / 2 + cell * np.arange(size)
gx, gy = np.meshgrid(xs, 2000 - xs)
true = truth(gx, gy)
read = {s: arcpy.RasterToNumPyArray(arcpy.Raster(os.path.join(folder, f"{s}_topo_{size}.tif"))).astype(float)
        for s in ("pro", "grapy")}
inner = np.zeros(true.shape, bool)
m = max(1, size // 20)
inner[m:-m, m:-m] = True
for name, (a, b) in {"pro - grapy": (read["pro"], read["grapy"]), "pro - truth": (read["pro"], true),
                     "grapy - truth": (read["grapy"], true)}.items():
    d = np.abs(a - b)
    print(f"{name:14} max {d.max():8.3f}  mean {d.mean():7.3f}  rms {np.sqrt((d ** 2).mean()):7.3f}"
          f"  inner max {d[inner].max():8.3f} mean {d[inner].mean():7.3f}")
