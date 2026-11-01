#!/usr/bin/env python3
"""Runs Topo to Raster and Segment Mean Shift inside grapy's fence, and prints peak address space and time.

Why this exists
---------------
D-333 measured both tools on Windows, where grapy's fence does not exist, by the process's peak commit. ADR-223's
memory limit is the **Linux fence's address space** (RLIMIT_AS, 2 GiB by default), and the two are not the same
number: Windows commits what a library reserves, Linux counts it against the limit only while it is mapped, and a
library such as SuperLU halves its first reservation when the allocation fails. So whether a run fits the fence is a
Linux measurement. This script is it, as `tools/measure_point_cloud_fence.py` is for point cloud training; it is run by
`.github/workflows/measure.yml` on a GitHub runner, where the fence is the one the server's image puts up.

What it does
------------
1. Unfenced, not measured: writes the inputs into the run's own folder, the only place the fence lets a tool read.
   *Topo to Raster*: ``--topo-points`` spot heights (default 10,000) sampled with seed 1 from D-333's own input, the
   3DEP 1/3" DEM of Mt Tamalpais remade from USGS's public tile (USGS_13_n38w123, read in place over HTTPS: the crop
   -122.6201..-122.4349 E, 37.8481..37.9500 N warped bilinearly to a 10 km square of 10 m cells in EPSG:26910 from
   (528000, 4191000), its western strip outside the crop 0 — D-333's ``tam1000.tif`` to within 1e-44 m) —
   or, with ``--dem synthetic`` or when the tile cannot be read, a fractal surface of the same size and relief, which
   is smoother: Topo to Raster takes 31 s on it where it takes 255 s on the real one, its sinks being fewer;
   and the output grid ``--topo-grid`` cells square (default 400, 25 m: D-333's run). *Segment Mean Shift*: a 3-band
   8-bit image of each ``--segment-sizes`` (default 1,280 and 2,048 square) of 40-pixel blocks in 64 colours with
   noise, the image `tools/pro-compare/adr242-segment-time.py` timed.
2. Fenced: a toolbox whose one tool calls the tool, run by ``grapy.runner`` exactly as the server runs a user's tool,
   with ADR-223's rlimits (2 GiB of address space and 600 s of CPU by default) and the environment ToolSandbox.cs gives
   a user tool (one BLAS thread).
3. Prints per run: whether it finished, the fence it reports, CPU and wall seconds, peak resident set and peak address
   space (``VmPeak``, read from ``/proc/<pid>/status`` by this unfenced parent). ``VmPeak`` is what 2 GiB bounds.

A run that does not fit is a result: past the address space it fails with an allocation error and is reported with
its message, past the CPU limit it is killed by SIGXCPU. Linux only, as the fence is; ``--unfenced`` checks the script
on any system without measuring anything. Usage:

    python tools/measure_fence.py [--cases topo segment] [--topo-grid 400] [--topo-points 10000] [--dem tamalpais]
                                  [--segment-sizes 1280 2048] [--memory-gib 2] [--cpu-seconds 600] [--unfenced]
"""

import argparse
import json
import os
import signal
import subprocess
import sys
import tempfile
import textwrap
import threading
import time

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from measure_point_cloud_fence import GRAPY, _children_rusage, _status  # noqa: E402

sys.path.insert(0, GRAPY)

SIDE_M = 10_000.0          # the synthetic terrain's side, as the 3DEP crop D-333 used
LEFT, BOTTOM = 528_000.0, 4_191_000.0   # that crop's corner in EPSG:26910 (UTM 10N), so the numbers read alike


def synthetic_terrain(cells=1000, seed=7, relief=800.0):
    """A fractal surface of ``cells`` square: spectral synthesis with amplitude 1/f^1.6, scaled to ``relief`` m."""
    rng = np.random.default_rng(seed)
    fy = np.fft.fftfreq(cells)[:, None]
    fx = np.fft.rfftfreq(cells)[None, :]
    f = np.hypot(fx, fy)
    f[0, 0] = 1.0
    spectrum = (rng.normal(size=f.shape) + 1j * rng.normal(size=f.shape)) / f ** 1.6
    spectrum[0, 0] = 0
    z = np.fft.irfft2(spectrum, s=(cells, cells))
    return (z - z.min()) / (z.max() - z.min()) * relief


TILE = "https://prd-tnm.s3.amazonaws.com/StagedProducts/Elevation/13/TIFF/current/n38w123/USGS_13_n38w123.tif"
CROP = (-122.62009259242146, 37.84814814832371, -122.43490740703706, 37.95000000028512)


def tamalpais():
    """D-333's DEM: the 3DEP tile's crop, warped to the 10 km UTM square, 0 outside the crop."""
    import rasterio
    from rasterio.transform import from_origin
    from rasterio.warp import Resampling, reproject
    from rasterio.windows import from_bounds
    with rasterio.Env(GDAL_DISABLE_READDIR_ON_OPEN="EMPTY_DIR"), rasterio.open(TILE) as src:
        window = from_bounds(*CROP, src.transform).round_offsets().round_lengths()
        crop = src.read(1, window=window)
        transform, crs = src.window_transform(window), src.crs
    z = np.zeros((1000, 1000), np.float32)
    reproject(crop, z, src_transform=transform, src_crs=crs, dst_transform=from_origin(LEFT, BOTTOM + SIDE_M, 10, 10),
              dst_crs="EPSG:26910", resampling=Resampling.bilinear, dst_nodata=0)
    return z


def prepare_topo(run, points, dem="tamalpais"):
    import grapy as arcpy
    source = dem
    if dem == "tamalpais":
        try:
            z = tamalpais()
        except Exception as error:      # a runner without the network: say so, and measure the synthetic surface
            print(f"the 3DEP tile could not be read ({error}); measuring the synthetic surface instead", flush=True)
            z, source = synthetic_terrain(), "synthetic (the 3DEP tile could not be read)"
    else:
        z = synthetic_terrain()
    cells = z.shape[0]
    cell = SIDE_M / cells
    rng = np.random.default_rng(1)
    index = rng.choice(z.size, points, replace=False)
    rows, cols = np.unravel_index(index, z.shape)
    arcpy.management.CreateFeatureclass(run, "spots.gpkg", "POINT", spatial_reference=26910)
    path = os.path.join(run, "spots.gpkg")
    arcpy.management.AddField(path, "z", "DOUBLE")
    with arcpy.da.InsertCursor(path, ["SHAPE@XY", "z"]) as cursor:
        for r, c in zip(rows.tolist(), cols.tolist()):
            cursor.insertRow([(LEFT + (c + 0.5) * cell, BOTTOM + SIDE_M - (r + 0.5) * cell), float(z[r, c])])
    return {"points": points, "dem": source, "relief_m": round(float(z.max() - z.min()), 1)}


def prepare_segment(run, size):
    import rasterio
    from rasterio.transform import from_origin
    rng = np.random.default_rng(242)
    r, k = np.indices((size, size), dtype=float)
    palette = rng.integers(20, 235, size=(64, 3))
    image = palette[((r // 40) * 8 + (k // 40)).astype(int) % 64].astype(float) + rng.normal(0, 6, (size, size, 3))
    image = np.clip(image, 0, 255).astype(np.uint8)
    name = f"img{size}.tif"
    with rasterio.open(os.path.join(run, name), "w", driver="GTiff", width=size, height=size, count=3, dtype="uint8",
                       crs="EPSG:3857", transform=from_origin(0, size, 1, 1)) as out:
        out.write(np.moveaxis(image, -1, 0))
    return name


TOPO = '''
import grapy as arcpy
from grapy import sa

class Toolbox:
    def __init__(self):
        self.tools = [Measure]

class Measure:
    def getParameterInfo(self):
        return []

    def execute(self, parameters, messages):
        import time
        started = time.perf_counter()
        out = sa.TopoToRaster([sa.TopoPointElevation([["spots.gpkg", "z"]])], {cell}, "{extent}", data_type="SPOT")
        out.save("topo.tif")
        messages.addMessage("MEASURE TopoToRaster in %.1f s of wall time" % (time.perf_counter() - started))
'''

SEGMENT = '''
import grapy as arcpy
from grapy import sa

class Toolbox:
    def __init__(self):
        self.tools = [Measure]

class Measure:
    def getParameterInfo(self):
        return []

    def execute(self, parameters, messages):
        import time
        started = time.perf_counter()
        sa.SegmentMeanShift("{image}", 15.5, 15, 20).save("seg_{image}")
        messages.addMessage("MEASURE SegmentMeanShift in %.1f s of wall time" % (time.perf_counter() - started))
'''


def fenced(run, label, source, memory, cpu, unfenced=False):
    """One tool run inside the fence; answers what the parent saw of it."""
    with open(os.path.join(run, "measure.pyt"), "w") as out:
        out.write(textwrap.dedent(source))
    with open(os.path.join(run, "run.json"), "w") as out:
        json.dump({"mode": "run", "toolbox": "measure.pyt", "tool": "Measure",
                   "limits": {"memory": memory, "cpu": cpu, "processes": 1024}}, out)
    result_path = os.path.join(run, "result.json")
    if os.path.exists(result_path):
        os.remove(result_path)
    environment = {k: v for k, v in os.environ.items() if k in ("PATH", "SYSTEMROOT")}
    environment["PYTHONPATH"] = GRAPY
    environment.update({"OPENBLAS_NUM_THREADS": "1", "OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "1",
                        "MALLOC_ARENA_MAX": "2", "PYTHONDONTWRITEBYTECODE": "1"})
    if unfenced:
        environment["GRAPY_UNFENCED"] = "1"
    before = _children_rusage()
    started = time.perf_counter()
    child = subprocess.Popen(
        [sys.executable, "-c",
         f"import sys; sys.path.insert(0, {GRAPY!r}); from grapy.runner import main; sys.exit(main({run!r}))"],
        env=environment)
    peak = {"VmPeak": 0, "VmHWM": 0}

    def watch():
        while child.poll() is None:
            seen = _status(child.pid)
            if seen:
                for k, v in seen.items():
                    peak[k] = max(peak[k], v)
            time.sleep(0.2)

    watcher = threading.Thread(target=watch, daemon=True)
    watcher.start()
    code = child.wait()
    watcher.join()
    wall = time.perf_counter() - started
    after = _children_rusage()
    report = {
        "run": label,
        "fenced": not unfenced,
        "exit": code if code >= 0 else f"signal {signal.Signals(-code).name}",
        "cpu_seconds": round((after.ru_utime - before.ru_utime) + (after.ru_stime - before.ru_stime), 1)
        if after else None,
        "wall_seconds": round(wall, 1),
        "peak_address_space_mb": round(peak["VmPeak"] / 1024, 1),
        "peak_resident_seen_mb": round(peak["VmHWM"] / 1024, 1),
        "limit_address_space_mb": memory // (1 << 20),
        "limit_cpu_seconds": cpu,
    }
    if os.path.exists(result_path):
        with open(result_path) as read:
            result = json.load(read)
        report["ok"] = bool(result.get("ok"))
        report["fence"] = result.get("fence")
        report["messages"] = [m.get("description", "") for m in result.get("messages", [])][-4:]
        if result.get("error"):
            report["error"] = result["error"]
    else:
        report["ok"] = False
        report["error"] = "no result.json: the process was ended before it could write one"
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--cases", nargs="+", default=["topo", "segment"], choices=["topo", "segment"])
    parser.add_argument("--topo-grid", type=int, default=400)
    parser.add_argument("--topo-points", type=int, default=10_000)
    parser.add_argument("--dem", default="tamalpais", choices=["tamalpais", "synthetic"])
    parser.add_argument("--segment-sizes", type=int, nargs="+", default=[1280, 2048])
    parser.add_argument("--memory-gib", type=float, default=2.0)
    parser.add_argument("--cpu-seconds", type=int, default=600)
    parser.add_argument("--unfenced", action="store_true",
                        help="check the script itself without the fence (any system); not a measurement")
    args = parser.parse_args(argv)
    if sys.platform != "linux" and not args.unfenced:
        print("the fence is Linux's; run this on Linux (measure.yml does), or pass --unfenced to check the script",
              file=sys.stderr)
        return 2
    memory = int(args.memory_gib * (1 << 30))
    reports, prepared = [], {}
    if "topo" in args.cases:
        run = tempfile.mkdtemp(prefix="topofence-")
        prepared["topo"] = prepare_topo(run, args.topo_points, args.dem)
        cell = SIDE_M / args.topo_grid
        extent = f"{LEFT} {BOTTOM} {LEFT + SIDE_M} {BOTTOM + SIDE_M}"
        label = f"TopoToRaster {args.topo_points} points, {args.topo_grid} x {args.topo_grid}"
        print(f"{label} inside the fence", flush=True)
        report = fenced(run, label, TOPO.format(cell=cell, extent=extent), memory, args.cpu_seconds, args.unfenced)
        print(json.dumps(report), flush=True)
        reports.append(report)
    if "segment" in args.cases:
        run = tempfile.mkdtemp(prefix="segfence-")
        for size in args.segment_sizes:
            image = prepare_segment(run, size)
            label = f"SegmentMeanShift {size} x {size}"
            print(f"{label} inside the fence", flush=True)
            report = fenced(run, label, SEGMENT.format(image=image), memory, args.cpu_seconds, args.unfenced)
            print(json.dumps(report), flush=True)
            reports.append(report)

    print()
    print(f"{'run':<44} {'ok':<5} {'cpu s':>7} {'wall s':>7} {'VmPeak MB':>10}  exit")
    for r in reports:
        print(f"{r['run']:<44} {str(r['ok']):<5} {str(r['cpu_seconds']):>7} {r['wall_seconds']:>7} "
              f"{r['peak_address_space_mb']:>10}  {r['exit']}")
    print(f"limits: {memory // (1 << 20)} MB of address space, {args.cpu_seconds} s of CPU; {json.dumps(prepared)}")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a") as out:
            out.write("### Topo to Raster and Segment Mean Shift inside the fence (D-333)\n\n")
            out.write("| run | finished | CPU s | wall s | peak address space MB | peak RSS MB | exit |\n")
            out.write("|---|---|---|---|---|---|---|\n")
            for r in reports:
                out.write(f"| {r['run']} | {r['ok']} | {r['cpu_seconds']} | {r['wall_seconds']} | "
                          f"{r['peak_address_space_mb']} | {r['peak_resident_seen_mb']} | {r['exit']} |\n")
            out.write(f"\n{json.dumps(prepared)}\n")
    # A run that does not fit is a result, not a failure of this script; the job stays green so its numbers are read.
    return 0


if __name__ == "__main__":
    sys.exit(main())
