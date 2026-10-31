# A comparison with ArcGIS Pro 3.7 (ADR-247, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: "C:/Program Files/ArcGIS/Pro/bin/Python/envs/arcgispro-py3/python.exe" adr247-random-points-pro.py <scratch>
"""ADR-247 condition 1, Pro side: CreateRandomPoints in an extent under "<seed> MERSENNE_TWISTER", twice per seed."""
import arcpy, os, sys, json
d = sys.argv[1]; o = os.path.join(d, "pro_rand"); os.makedirs(o, exist_ok=True)
arcpy.env.overwriteOutput = True
out = {}
for seed in (1, 7, 12345):
    runs = []
    for k in range(2):
        arcpy.env.randomGenerator = f"{seed} MERSENNE_TWISTER"
        name = f"r{seed}_{k}.shp"
        arcpy.management.CreateRandomPoints(o, name, None, "0 0 1000 1000", 10, 0)
        runs.append([list(r[0]) for r in arcpy.da.SearchCursor(os.path.join(o, name), ["SHAPE@XY"])])
    out[seed] = runs
    print(seed, "repeatable:", runs[0] == runs[1], runs[0][:3])
json.dump(out, open(os.path.join(o, "points.json"), "w"))
