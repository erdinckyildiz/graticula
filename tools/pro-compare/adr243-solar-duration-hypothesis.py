# A comparison with ArcGIS Pro 3.7 (ADR-243, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Usage: <grapy python> adr243-solar-duration-hypothesis.py <scratch> <worktree>
import sys, numpy as np, rasterio
sys.path.insert(0, sys.argv[2] + "/src/grapy")
import grapy._insolation as I
orig = I.insolation
def patched(horizon, elevation, slope, aspect, sky, sectors, *a):
    d, f, u = orig(horizon, elevation, slope, aspect, sky, sectors, *a)
    # recompute duration counting only sectors in front of the surface
    from scipy import sparse
    rows_, cols_, vals = [], [], []
    for k, s in enumerate(sectors):
        hit = sky.cells_of(s.samples); rows_ += hit.tolist(); cols_ += [k]*hit.size; vals += [1.0/hit.size]*hit.size
    M = sparse.csr_matrix((vals, (rows_, cols_)), shape=(sky.zenith.size, len(sectors)))
    zen = np.array([s.zenith for s in sectors]); az = np.array([s.azimuth for s in sectors]); dur = np.array([s.duration for s in sectors])
    gap = (sparse.csr_matrix(sky.visible(horizon).astype(float)) @ M).toarray()
    front = I.cos_incidence(zen[None], az[None], slope[:, None], aspect[:, None]) > 0
    u = (dur[None] * gap * front).sum(axis=1, keepdims=True)
    return d, f, u
I.insolation = patched
import grapy._solartools as T; T._ins.insolation = patched
import grapy as arcpy
from grapy.sa import AreaSolarRadiation, TimeWithinDay
d = sys.argv[1]
AreaSolarRadiation(d + "/dem.tif", 40, 200, TimeWithinDay(172, 0, 24), 14, 0.5, "NOINTERVAL", 1, "FROM_DEM", 32, 8, 8,
                   "UNIFORM_SKY", 0.3, 0.5, None, None, d + "/hyp_dur.tif")
a = rasterio.open(d + "/pro_solar/dur.tif").read(1).astype(float); b = rasterio.open(d + "/hyp_dur.tif").read(1).astype(float)
x = (b - a)[1:]
print("no row0: max|d| %.3f mean|d| %.3f bias %+.3f p99 %.3f" % (abs(x).max(), abs(x).mean(), x.mean(), np.percentile(abs(x), 99)))
