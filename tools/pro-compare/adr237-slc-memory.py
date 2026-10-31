"""grapy's memory on a synthetic full-size Sentinel-1 IW SLC (D-328, ADR-237 condition 3): run one SAR tool in a child
process and watch the child's committed memory, killing it once it passes a ceiling, so no measurement can take the
machine with it. Windows only (psapi).

A venv's python.exe is a launcher that starts the real interpreter as its own child, so watching the launcher's PID
watches the wrong process (the first version of this harness did, and never fired). The child here writes its own
PID to a file before anything else; the watchdog opens that process and watches it, and kills it and the launcher.

    adr237-slc-memory.py <worktree> <work folder> <step> [stop at MB, default 3000]

Steps: baseline (the imports alone), make (a 9-burst IW SLC, 13,500 x 21,000 complex int16 as a real one is, and its annotation), deburst,
secondary (the debursted scene again on an orbit 150 m away, noise added), coherence, interferogram, goldstein,
coregister (both as stripmap, through hard links: TOPS is refused), multilook, sigma0, dark, bright, winds, flatten
(on a 1,500-row strip of the multilooked scene: memory is per block, time is per facet), unwrap-<rows> (a square
interferogram of rows x rows, the solver being global), watchdog (allocates until it is stopped)."""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

worktree, work, step = sys.argv[1], sys.argv[2], sys.argv[3]
stop_mb = float(sys.argv[4]) if len(sys.argv) > 4 else 3000
os.makedirs(work, exist_ok=True)
pid_file = os.path.join(work, f"{step}.pid")
if os.path.exists(pid_file):
    os.remove(pid_file)

PRELUDE = f"""
import os, sys, time
open({pid_file!r}, "w").write(str(os.getpid()))
sys.path.insert(0, {os.path.join(worktree, 'src', 'grapy')!r})
sys.path.insert(0, {os.path.join(worktree, 'src', 'grapy', 'tests')!r})
import numpy as np
import grapy as arcpy
from grapy import _sar
W = {work!r}
def p(name):
    return os.path.join(W, name)
started = time.process_time()
"""

STEPS = {
    "baseline": """
import scipy.ndimage, scipy.interpolate, rasterio, shapely, pyproj
""",
    "watchdog": """
held = []
while True:
    held.append(np.ones(25_000_000))
    time.sleep(0.05)
""",
    "make": """
import shutil, rasterio
from rasterio.windows import Window
import sar_fixture as F
ROWS, COLS, LPB, BURSTS, OVERLAP = 13_500, 21_000, 1_500, 9, 150
if os.path.exists(p("S1A_TEST.SAFE")):
    shutil.rmtree(p("S1A_TEST.SAFE"))
first_valid = [-1] * 20 + [80] * (LPB - 40) + [-1] * 20
last_valid = [-1] * 20 + [COLS - 80] * (LPB - 40) + [-1] * 20
times = [F.T1 + k * (LPB - OVERLAP) * F.DT for k in range(BURSTS)]
name = F.safe(W, "SLC", {"VV": np.zeros((2, 2), np.complex64)}, rows=ROWS, cols=COLS,
              bursts=(LPB, times, first_valid, last_valid))
tiff = [os.path.join(name, "measurement", f) for f in os.listdir(os.path.join(name, "measurement"))][0]
os.remove(tiff)
rng = np.random.default_rng(1)
with _sar._quiet(), rasterio.Env(GDAL_CACHEMAX=64), rasterio.open(tiff, "w", driver="GTiff", width=COLS, height=ROWS, count=1, dtype="complex_int16",
                                  tiled=True, blockxsize=512, blockysize=512) as out:
    for r0 in range(0, ROWS, 500):
        n = min(500, ROWS - r0)
        z = (rng.normal(0, 60, (n, COLS)) + 1j * rng.normal(0, 60, (n, COLS))) * np.exp(1j * 0.01 * np.arange(COLS))
        out.write(z.astype(np.complex64), 1, window=Window(0, r0, COLS, n))
print("made", tiff, os.path.getsize(tiff) / 2 ** 20, "MB")
""",
    "deburst": """
arcpy.ia.Deburst(p("S1A_TEST.SAFE"), p("deb.tif"))
""",
    "secondary": """
import json, sar_fixture as F
sar = _sar.read(p("deb.tif"), "m")
meta = sar.copy_meta(orbit=F.vectors((0.0, 150.0, 80.0)))
rng = np.random.default_rng(2)
def blocks():
    for r0, r1 in _sar.row_blocks(sar.shape):
        z = sar.bands.window("VV", r0, r1)
        yield r0, 0, {"VV": z * np.exp(0.3j) + (rng.normal(0, 20, z.shape) + 1j * rng.normal(0, 20, z.shape))}
_sar.write_blocks(p("deb2.tif"), ["VV"], "complex64", sar.shape, meta, blocks())
for k in ("deb", "deb2"):
    if not os.path.exists(p(k + "_sm.tif")):
        os.link(p(k + ".tif"), p(k + "_sm.tif"))
    m = json.load(open(p(k + ".tif") + _sar.SIDE))
    m["mode"] = "SM"
    json.dump(m, open(p(k + "_sm.tif") + _sar.SIDE, "w"))
""",
    "coherence": """
arcpy.ia.ComputeCoherence(p("deb.tif"), p("deb2.tif"), p("coh.tif"))
""",
    "interferogram": """
arcpy.ia.GenerateInterferogram(p("deb.tif"), p("deb2.tif"), p("ifg.tif"), out_flattened_interferogram=p("ifg_flat.tif"))
""",
    "goldstein": """
arcpy.ia.ApplyComplexDataFilter(p("ifg.tif"), p("gold.tif"))
""",
    "coregister": """
arcpy.ia.ApplyCoregistration(p("deb_sm.tif"), p("deb2_sm.tif"), p("coreg.tif"), geoid="NONE")
""",
    "multilook": """
arcpy.ia.Multilook(p("deb.tif"), p("ml.tif"))
""",
    "sigma0": """
import json
sar = _sar.read(p("ml.tif"), "m")
m = sar.copy_meta(calibration="sigma0")
def blocks():
    for r0, r1 in _sar.row_blocks(sar.shape):
        yield r0, 0, {"VV": sar.bands.window("VV", r0, r1) / 3600.0 * 0.05}
_sar.write_blocks(p("s0.tif"), ["VV"], "float32", sar.shape, m, blocks())
m["calibration"] = "beta0"
strip = 1500
def strip_blocks():
    for r0, r1 in _sar.row_blocks((strip, sar.shape[1])):
        yield r0, 0, {"VV": sar.bands.window("VV", r0, r1) / 3600.0 * 0.05}
m["lines"] = m["lines"][:strip]
_sar.write_blocks(p("b0strip.tif"), ["VV"], "float32", (strip, sar.shape[1]), m, strip_blocks())
""",
    "dark": """
arcpy.ia.DetectDarkOceanAreas(p("ml.tif"), p("dark.tif"))
""",
    "bright": """
arcpy.ia.DetectBrightOceanObjects(p("ml.tif"), p("bright.gpkg") + "/ships")
""",
    "winds": """
arcpy.ia.ExtractOceanWinds(p("s0.tif"), p("winds.tif"))
""",
    "flatten": """
import sar_fixture as F
from grapy import _sartools
sar = _sar.read(p("b0strip.tif"), "m")
west, south, east, north = _sartools._footprint(sar.meta, sar.shape, None)
if not os.path.exists(p("dem.tif")):
    F.dem(p("dem.tif"), west - 0.02, south - 0.02, east + 0.02, north + 0.02,
          lambda x, y: 400 + 250 * np.sin(x * 800) * np.cos(y * 600), cell=0.00027)
arcpy.ia.ApplyRadiometricTerrainFlattening(p("b0strip.tif"), p("rtf.tif"), p("dem.tif"), "NONE")
""",
    "unwrap": """
import sar_fixture as F
n = int({rows})
rows, cols = np.mgrid[0:n, 0:n]
z = np.exp(1j * (0.05 * cols + 0.03 * rows)).astype(np.complex64)
del rows, cols
_sar.write(p(f"w{{n}}.tif"), {{"VV": z}}, F.meta(rows=n, cols=n))
del z
started = time.process_time()
arcpy.ia.UnwrapPhase(p(f"w{{n}}.tif"), p(f"u{{n}}.tif"))
""",
}

REPORT = """
elapsed = time.process_time() - started
import ctypes, ctypes.wintypes as wt
class C(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD)] + [(n, ctypes.c_size_t) for n in (
        "PeakWorkingSetSize", "WorkingSetSize", "a", "b", "c", "d", "PagefileUsage", "PeakPagefileUsage", "PrivateUsage")]
c = C(); c.cb = ctypes.sizeof(C)
k = ctypes.windll.kernel32
k.GetCurrentProcess.restype = ctypes.c_void_p
k.K32GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.DWORD]
k.K32GetProcessMemoryInfo(k.GetCurrentProcess(), ctypes.byref(c), c.cb)
print(f"SELF peak commit {c.PeakPagefileUsage / 2 ** 20:.0f} MB, peak working set {c.PeakWorkingSetSize / 2 ** 20:.0f} MB,"
      f" CPU {elapsed:.0f} s")
"""

if step.startswith("unwrap-"):
    body = STEPS["unwrap"].format(rows=step.split("-", 1)[1])
else:
    body = STEPS[step]
child = PRELUDE + body + REPORT


class Counters(ctypes.Structure):
    _fields_ = [("cb", wt.DWORD), ("PageFaultCount", wt.DWORD), ("PeakWorkingSetSize", ctypes.c_size_t),
                ("WorkingSetSize", ctypes.c_size_t), ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
                ("QuotaPagedPoolUsage", ctypes.c_size_t), ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
                ("QuotaNonPagedPoolUsage", ctypes.c_size_t), ("PagefileUsage", ctypes.c_size_t),
                ("PeakPagefileUsage", ctypes.c_size_t), ("PrivateUsage", ctypes.c_size_t)]


kernel, psapi = ctypes.windll.kernel32, ctypes.windll.psapi
kernel.OpenProcess.restype = ctypes.c_void_p
kernel.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint]
psapi.GetProcessMemoryInfo.argtypes = [ctypes.c_void_p, ctypes.c_void_p, wt.DWORD]
# as the server's sandbox runs a user tool (ToolSandbox.cs): OpenBLAS's per-thread buffers alone are about 1 GB of
# commit on a 16-thread machine, which is the machine's and not grapy's
environment = dict(os.environ, OPENBLAS_NUM_THREADS="1")
launcher = subprocess.Popen([sys.executable, "-c", child], stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
                            env=environment)
start = time.time()
handle = None
while handle is None and launcher.poll() is None and time.time() - start < 60:
    try:
        real = int(open(pid_file).read())
        handle = kernel.OpenProcess(0x1000 | 0x0001, False, real)   # query limited information, terminate
    except (OSError, ValueError):
        time.sleep(0.05)
if handle is None:
    print(launcher.communicate()[0][-3000:])
    sys.exit(f"the child never reported its PID; nothing was watched")
print(f"watching the interpreter, PID {real} (the launcher is {launcher.pid})", flush=True)
counters = Counters()
counters.cb = ctypes.sizeof(Counters)
peak, stopped = 0.0, False
while launcher.poll() is None:
    if psapi.GetProcessMemoryInfo(handle, ctypes.byref(counters), counters.cb):
        peak = max(peak, counters.PeakPagefileUsage / 2 ** 20)
        if counters.PrivateUsage / 2 ** 20 > stop_mb:
            kernel.TerminateProcess(handle, 1)
            launcher.kill()
            stopped = True
            break
    time.sleep(0.1)
output = launcher.communicate()[0]
print(output[-3000:])
print(f"{step}: watched peak commit {peak:.0f} MB after {time.time() - start:.1f} s wall; "
      f"{'STOPPED past ' + str(int(stop_mb)) + ' MB' if stopped else 'ran to the end'}; ADR-223 limit 2048 MB")
