"""Frames for ADR-234 condition 5: images whose bands are their own column and row, and a frames table both sides read.

Each image is 300 x 200, two float32 bands: band 1 the pixel's column index, band 2 its row index. A ground cell's
value in an orthorectified image therefore says which image position the software projected it to.
make_frames.py <out folder>"""
import csv
import os
import sys
import warnings

import numpy as np
import rasterio

out = sys.argv[1]
os.makedirs(os.path.join(out, "frames"), exist_ok=True)
COLS, ROWS = 300, 200
rows, cols = np.mgrid[0:ROWS, 0:COLS].astype(np.float32)
BASE = dict(CameraID="cam1", PerspectiveZ=1000, Omega=5, Phi=-3, Kappa=30, AverageZ=10, SRS=32635,
            FocalLength=50000, PrincipalX=0, PrincipalY=0, PixelSize=10, NRows=ROWS, NColumns=COLS)
# One row per convention under test; every other field as BASE.
VARIANTS = [
    ("f1_default", {}),
    ("f2_angledir_plus1", {"AngleDirection": 1}),
    ("f3_polarity_plus1", {"Polarity": 1}),
    ("f4_affine", {"A0": -1500, "A1": 10, "A2": 0, "B0": 1000, "B1": 0, "B2": -10}),   # equals the default film map
    ("f5_affine_skew", {"A0": -1400, "A1": 9.5, "A2": 1.0, "B0": 1100, "B1": 0.8, "B2": -10.5}),
    ("f6_principal", {"PrincipalX": 120, "PrincipalY": -80}),
    ("f7_radial", {"Radial": "0;0.01;0;0"}),
    ("f8_tangential", {"Tangential": "0.01;-0.005"}),
    ("f9_nadir", {"Omega": 0, "Phi": 0, "Kappa": 0}),
    ("f10_radial_tangential", {"Radial": "0;0.01;0;0", "Tangential": "0.01;-0.005"}),
    ("f11_polarity_omega180", {"Polarity": 1, "Omega": 185}),
    ("f12_angledir_omega180", {"AngleDirection": 1, "Omega": 185}),
]
fields = ["Raster", "Name", *BASE, "AngleDirection", "Polarity", "A0", "A1", "A2", "B0", "B1", "B2", "Radial",
          "Tangential", "PerspectiveX", "PerspectiveY"]
with open(os.path.join(out, "frames.csv"), "w", newline="") as f:
    w = csv.DictWriter(f, fields)
    w.writeheader()
    for i, (name, change) in enumerate(VARIANTS):
        path = os.path.join("frames", f"{name}.tif")
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            with rasterio.open(os.path.join(out, path), "w", driver="GTiff", width=COLS, height=ROWS, count=2,
                               dtype="float32") as image:
                image.write(cols, 1)
                image.write(rows, 2)
        row = dict(BASE, Raster=os.path.join(out, path), Name=name, PerspectiveX=500000 + 1000 * i,
                   PerspectiveY=4400000)
        row.update(change)
        w.writerow(row)
print(len(VARIANTS), "frames")
