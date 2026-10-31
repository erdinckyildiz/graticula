# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> -I adr243-solar-make-dem.py <scratch>
"""Synthetic inputs for the ADR-243 solar comparison: a 60 x 60 DEM at 30 m in UTM 36N near 40 N, and 12 points."""
import numpy as np, rasterio, json, sys
from rasterio.transform import from_origin
out = sys.argv[1]
n, cell = 60, 30.0
x0, y0 = 500000.0, 4430000.0
yy, xx = np.mgrid[0:n, 0:n].astype(float)
z = (400 + 250*np.exp(-(((xx-25)**2 + (yy-30)**2)/(2*9.0**2)))      # hill
     + 120*np.exp(-((xx-48)**2)/(2*4.0**2))                         # N-S ridge in the east
     - 80*np.exp(-(((xx-10)**2 + (yy-12)**2)/(2*5.0**2)))           # pit
     + 1.5*yy)                                                       # gentle slope down to the north
with rasterio.open(out + "/dem.tif", "w", driver="GTiff", width=n, height=n, count=1, dtype="float32",
                   crs="EPSG:32636", transform=from_origin(x0, y0, cell, cell)) as d:
    d.write(z.astype("float32"), 1)
pts = [(x0 + (c+0.5)*cell, y0 - (r+0.5)*cell) for r, c in
       [(30,25),(30,18),(30,32),(22,25),(38,25),(12,10),(5,5),(50,50),(30,48),(30,44),(45,12),(55,30)]]
json.dump(pts, open(out + "/points.json", "w"))
print(z.min(), z.max())
