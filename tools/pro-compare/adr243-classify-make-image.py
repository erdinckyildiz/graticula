# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr243-classify-make-image.py <scratch> <worktree>
"""ADR-243 condition 2 inputs: a 4-band 120 x 120 uint8 image of five overlapping classes, and training polygons
(classvalue, classname) in a GeoPackage, written by grapy. Same seed every time."""
import os, sys, numpy as np, rasterio
from rasterio.transform import from_origin
d, wt = sys.argv[1], sys.argv[2]
rng = np.random.default_rng(7)
n = 120
yy, xx = np.mgrid[0:n, 0:n]
cls = np.ones((n, n), int)                                   # 1 water
cls[(xx >= 40)] = 2                                          # 2 forest
cls[(xx >= 80)] = 3                                          # 3 grass
cls[(yy >= 60) & (xx >= 40)] = 4                             # 4 soil
cls[((xx - 30) ** 2 + (yy - 90) ** 2) < 18 ** 2] = 5        # 5 urban
means = {1: [40, 35, 25, 15], 2: [45, 60, 40, 120], 3: [70, 90, 70, 140], 4: [110, 100, 90, 110], 5: [120, 115, 110, 90]}
covs = {1: 6, 2: 12, 3: 14, 4: 16, 5: 22}
img = np.zeros((4, n, n))
for c, m in means.items():
    k = cls == c
    a = rng.normal(size=(4, 4)) * 0.4 + np.eye(4)
    noise = (a @ rng.normal(size=(4, k.sum()))) * covs[c]
    img[:, k] = np.array(m)[:, None] + noise
img = np.clip(np.rint(img), 0, 255).astype("uint8")
with rasterio.open(os.path.join(d, "image.tif"), "w", driver="GTiff", width=n, height=n, count=4, dtype="uint8",
                   crs="EPSG:32636", transform=from_origin(500000, 4430000, 10, 10)) as f:
    f.write(img)
with rasterio.open(os.path.join(d, "truth.tif"), "w", driver="GTiff", width=n, height=n, count=1, dtype="uint8",
                   crs="EPSG:32636", transform=from_origin(500000, 4430000, 10, 10)) as f:
    f.write(cls.astype("uint8"), 1)
sys.path.insert(0, wt + "/src/grapy")
import grapy as arcpy
gp = os.path.join(d, "training.gpkg")
if os.path.exists(gp): os.remove(gp)
arcpy.management.CreateFeatureclass(gp, "training", "POLYGON", spatial_reference=arcpy.SpatialReference(32636))
arcpy.management.AddField(gp + "/training", "classvalue", "LONG")
arcpy.management.AddField(gp + "/training", "classname", "TEXT")
boxes = {1: [(5, 5, 15, 15), (5, 70, 12, 80)], 2: [(50, 10, 62, 20), (60, 35, 70, 45)], 3: [(90, 10, 100, 22), (100, 40, 110, 50)],
         4: [(50, 70, 60, 80), (95, 95, 108, 105)], 5: [(25, 85, 35, 95)]}
names = {1: "water", 2: "forest", 3: "grass", 4: "soil", 5: "urban"}
def ring(c0, r0, c1, r1):
    x0, y0 = 500000 + c0 * 10, 4430000 - r0 * 10; x1, y1 = 500000 + c1 * 10, 4430000 - r1 * 10
    return arcpy.Polygon(arcpy.Array([arcpy.Point(x0, y0), arcpy.Point(x1, y0), arcpy.Point(x1, y1), arcpy.Point(x0, y1), arcpy.Point(x0, y0)]),
                         arcpy.SpatialReference(32636))
with arcpy.da.InsertCursor(gp + "/training", ["classvalue", "classname", "SHAPE@"]) as c:
    for v, bs in boxes.items():
        for b in bs:
            c.insertRow((v, names[v], ring(*b)))
import json
json.dump({"boxes": {str(v): [[500000 + b[0] * 10, 4430000 - b[1] * 10, 500000 + b[2] * 10, 4430000 - b[3] * 10] for b in bs]
                     for v, bs in boxes.items()}, "names": {str(k): v for k, v in names.items()}},
          open(os.path.join(d, "training.json"), "w"))
print("ok")
