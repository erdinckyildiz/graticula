# A comparison with ArcGIS Pro 3.7 (ADR-243 condition 1, D-337, 2026-10-31). A tool, not a test: it needs Pro and is not run in CI.
# Pro side: Area Solar Radiation one hour at a time (4-20 h) on dem.tif, to see which hours the durations differ in.
import arcpy, os, sys
from arcpy.sa import *
arcpy.env.overwriteOutput = True
arcpy.CheckOutExtension("Spatial")
o = "D:/temp/procmp-f/solar/pro_h"; os.makedirs(o, exist_ok=True)
dem = "D:/temp/procmp-c/dem.tif"
for h in range(4, 20):
    g = AreaSolarRadiation(dem, 40, 200, TimeWithinDay(172, h, h + 1), 14, 0.5, "NOINTERVAL", 1, "FROM_DEM", 32, 8, 8,
                           "UNIFORM_SKY", 0.3, 0.5, os.path.join(o, f"dir{h}.tif"), None, os.path.join(o, f"dur{h}.tif"))
    print(h, flush=True)

# grapy side (run with grapy's python):
# import sys, os
# sys.path.insert(0, "D:/temp/wt-grapy-b/src/grapy")
# from grapy.sa import AreaSolarRadiation, TimeWithinDay
# o = "D:/temp/procmp-f/solar/grapy_h"; os.makedirs(o, exist_ok=True)
# for h in range(4, 20):
#     AreaSolarRadiation("D:/temp/procmp-c/dem.tif", 40, 200, TimeWithinDay(172, h, h + 1), 14, 0.5, "NOINTERVAL", 1, "FROM_DEM", 32, 8, 8,
#                        "UNIFORM_SKY", 0.3, 0.5, os.path.join(o, f"dir{h}.tif"), None, os.path.join(o, f"dur{h}.tif"))
#     print(h, flush=True)

# capture.py (grapy's python) saves the horizons and sectors grapy used, for adr243-solar-horizon.py:
# import sys, pickle, numpy as np
# sys.path.insert(0, "D:/temp/wt-grapy-b/src/grapy")
# import grapy._insolation as I
# import grapy._solartools as T
# orig = I.insolation
# cap = {}
# def patched(horizon, elevation, slope, aspect, sky, sectors, *a):
#     cap.update(horizon=horizon, elevation=elevation, slope=slope, aspect=aspect, sky_size=sky.size,
#                sectors=[(s.zenith, s.azimuth, s.duration, s.band, list(s.samples), s.day) for s in sectors], args=a)
#     return orig(horizon, elevation, slope, aspect, sky, sectors, *a)
# I.insolation = patched; T._ins.insolation = patched
# from grapy.sa import AreaSolarRadiation, TimeWithinDay
# AreaSolarRadiation("D:/temp/procmp-c/dem.tif", 40, 200, TimeWithinDay(172, 0, 24), 14, 0.5, "NOINTERVAL", 1, "FROM_DEM", 32, 8, 8,
#                    "UNIFORM_SKY", 0.3, 0.5, None, None, "D:/temp/procmp-f/solar/dur_g.tif")
# pickle.dump(cap, open("cap.pkl", "wb"))
# print(cap["horizon"].shape, len(cap["sectors"]), cap["args"])
