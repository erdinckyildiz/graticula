"""Synthetic mass points for the terrain comparison (ADR-234 condition 4): one CSV both Pro and grapy read."""
import csv
import sys

import numpy as np

out = sys.argv[1]
rng = np.random.default_rng(234)
n = 2500
x = np.round(rng.uniform(0, 100, n), 2)
y = np.round(rng.uniform(0, 100, n), 2)
z = 50 + 8 * np.sin(x / 13.0) + 6 * np.cos(y / 9.0) + 0.05 * x + rng.normal(0, 0.6, n)
z = np.round(z, 3)
X = 500000.0 + x
Y = 4400000.0 + y
with open(out, "w", newline="") as f:
    w = csv.writer(f)
    w.writerow(["PID", "X", "Y", "Z"])
    for i in range(n):
        w.writerow([i + 1, f"{X[i]:.2f}", f"{Y[i]:.2f}", f"{z[i]:.3f}"])
print(n, "points")
