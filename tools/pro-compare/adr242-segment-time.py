# A benchmark (ADR-242, D-333, 2026-10-31): Segment Mean Shift time and Python-allocated memory on synthetic images; not a test.
# Usage: <grapy python> adr242-segment-time.py 320,640,1280   (writes images under D:/temp/procmp-f; edit the paths)
import sys, time, os, numpy as np, rasterio, tracemalloc
from rasterio.transform import from_origin
sys.path.insert(0, "D:/temp/wt-grapy-b/src/grapy")
import grapy as arcpy
for n in [int(v) for v in sys.argv[1].split(",")]:
    rng = np.random.default_rng(242)
    r, k = np.indices((n, n), dtype=float)
    palette = rng.integers(20, 235, size=(64, 3))
    img = palette[((r // 40) * 8 + (k // 40)).astype(int) % 64].astype(float) + rng.normal(0, 6, (n, n, 3))
    img = np.clip(img, 0, 255).astype(np.uint8)
    p = f"D:/temp/procmp-f/img{n}.tif"
    with rasterio.open(p, "w", driver="GTiff", width=n, height=n, count=3, dtype="uint8", crs="EPSG:3857",
                       transform=from_origin(0, n, 1, 1)) as w:
        w.write(np.moveaxis(img, -1, 0))
    tracemalloc.start(); t = time.time()
    out = arcpy.sa.SegmentMeanShift(p, 15.5, 15, 20)
    out.save(f"D:/temp/procmp-f/seg{n}.tif")
    peak = tracemalloc.get_traced_memory()[1] / 2**20; tracemalloc.stop()
    print(f"SegmentMeanShift {n}x{n}: {time.time() - t:.1f} s, peak python-allocated {peak:.0f} MiB", flush=True)
