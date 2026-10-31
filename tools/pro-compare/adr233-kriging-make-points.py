# A comparison with ArcGIS Pro 3.7 (ADR-233, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> -I adr233-kriging-make-points.py <scratch>
"""ADR-233 condition 4 input: 60 points in a 2,000 m square (corners included so both sides take the same extent),
z from a smooth field plus noise. Written to points_k.json; each side builds its own feature class from it."""
import json, sys, numpy as np
rng = np.random.default_rng(23)
x0, y0 = 500000.0, 4428000.0
pts = [(0, 0), (2000, 0), (0, 2000), (2000, 2000)] + [tuple(v) for v in rng.uniform(50, 1950, (56, 2)).round(1)]
out = []
for x, y in pts:
    z = 10 + 3 * np.sin(x / 400.0) + 2 * np.cos(y / 300.0) + 0.002 * x + rng.normal(0, 0.4)
    out.append((x0 + x, y0 + y, round(float(z), 4)))
json.dump(out, open(sys.argv[1] + "/points_k.json", "w"))
