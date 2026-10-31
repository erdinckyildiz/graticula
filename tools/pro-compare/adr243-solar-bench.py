# A benchmark (ADR-243, D-334, 2026-10-31): Area Solar Radiation time on the comparison DEM resampled to n x n; not a test.
# Usage: <grapy python> adr243-solar-bench.py <n>   (reads D:/temp/procmp-c/dem.tif; edit the path)
import sys, time, cProfile, pstats, numpy as np, rasterio
sys.path.insert(0, "D:/temp/wt-grapy-b/src/grapy")
import grapy as arcpy
from grapy.sa import AreaSolarRadiation, TimeWithinDay
n = int(sys.argv[1])
src = rasterio.open("D:/temp/procmp-c/dem.tif"); z = src.read(1).astype(np.float32)
if n != 60:
    from scipy.ndimage import zoom
    z = zoom(z, n / 60, order=1).astype(np.float32)
prof = src.profile; prof.update(width=n, height=n, transform=src.transform * src.transform.scale(60 / n, 60 / n))
p = f"D:/temp/procmp-f/solar/dem{n}.tif"
with rasterio.open(p, "w", **prof) as w: w.write(z, 1)
t = time.time()
pr = cProfile.Profile(); pr.enable()
AreaSolarRadiation(p, 40, 200, TimeWithinDay(172, 0, 24), 14, 0.5, "NOINTERVAL", 1, "FROM_DEM", 32, 8, 8,
                   "UNIFORM_SKY", 0.3, 0.5).save(f"D:/temp/procmp-f/solar/glb{n}.tif")
pr.disable()
print(n, "seconds %.1f" % (time.time() - t))
pstats.Stats(pr).sort_stats("tottime").print_stats(14)
