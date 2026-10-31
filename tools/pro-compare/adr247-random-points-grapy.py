# A comparison with ArcGIS Pro 3.7 (ADR-247, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr247-random-points-grapy.py <scratch> <worktree>
"""ADR-247 condition 1, grapy side: the same CreateRandomPoints runs as random_pro.py, compared point by point with
Pro's, and with the rule Pro was found to follow: point k is (w[2k], w[2k+1]) of mt19937 seeded by init_genrand, each
word as (w + 0.5) / 2**32 of the extent."""
import os, sys, json, numpy as np
sys.path.insert(0, sys.argv[2] + "/src/grapy")
import grapy as arcpy
d = sys.argv[1]; o = os.path.join(d, "grapy_rand"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
pro = json.load(open(os.path.join(d, "pro_rand", "points.json")))
gp = os.path.join(o, "rand.gpkg")
if os.path.exists(gp): os.remove(gp)
for seed in (1, 7, 12345):
    arcpy.env.randomGenerator = f"{seed} MERSENNE_TWISTER"
    arcpy.management.CreateRandomPoints(gp, f"r{seed}", None, "0 0 1000 1000", 10, 0)
    g = np.array([r[0] for r in arcpy.da.SearchCursor(gp + f"/r{seed}", ["SHAPE@XY"])])
    p = np.array(pro[str(seed)][0])
    w = (np.random.RandomState(seed).randint(0, 2 ** 32, 20, dtype=np.uint32) + 0.5) / 2 ** 32 * 1000
    print(f"seed {seed}: {len(g)} points, max|grapy - Pro| {np.abs(g - p).max():.3g}; "
          f"max|rule - Pro| {np.abs(np.column_stack([w[0::2], w[1::2]]) - p).max():.3g}")
