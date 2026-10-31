#!/usr/bin/env python3
"""Trains RandLA-Net and Point Transformer V3 for one epoch each inside grapy's fence, and prints peak memory and time.

Why this exists
---------------
ADR-249 condition 1 asks for point cloud training measured inside ADR-223's limits. D-341 measured it on a real 3DEP
tile (1.44 million points, 8,192-point blocks of 15 m) on Windows, where the fence does not exist, and so could not say
whether a training step fits the **Linux fence's 2 GB** — the fence bounds address space (RLIMIT_AS), not Windows
commit, and Windows commit is what that run counted. This script is the Linux half: it is run by
`.github/workflows/measure.yml` on a GitHub runner, where grapy's fence is the same one the server's image puts up.

What it does
------------
1. Unfenced, in this process: writes a synthetic LAS of ``--points`` points (default 1,440,000, D-341's count) over
   120 × 120 m in EPSG:3857 — ground (class 2), buildings (6), trees (5) and a little unclassified (1), at about 100
   points per square metre — makes a LAS dataset of it, and prepares classification training data with 15 m blocks of
   at most 8,192 points: the western 60 % for training and 60-70 % for validation, as D-341 divided its tile. None of
   this is what is measured.
2. Fenced, once per architecture: a toolbox whose one tool calls ``arcpy.ddd.TrainPointCloudClassificationModel`` for
   one epoch, run by ``grapy.runner`` exactly as the server runs a user's tool — Landlock, seccomp and ADR-223's
   rlimits: 2 GiB of address space and 600 s of CPU by default. The training data lives in the run's own folder,
   the only place the fence lets a tool read and write.
3. Prints, per architecture: whether the run finished, the fence it reports, CPU seconds and wall seconds (the child's
   own, from ``RUSAGE_CHILDREN``), its peak resident set (``ru_maxrss``) and its peak address space (``VmPeak``, read
   from ``/proc/<pid>/status`` by this unfenced parent while the child runs, since the fence closes ``/proc`` to the
   child itself). ``VmPeak`` is the number the 2 GiB limit is compared with.

A run that exceeds the address-space limit fails inside torch with an allocation error and is reported with its
message; a run that exceeds the CPU limit is killed by SIGXCPU and is reported by its signal. Either is a result,
not a fault of the script.

Linux only: the fence is. Usage:

    python tools/measure_point_cloud_fence.py [--points 1440000] [--epoch-iterations 10] [--memory-gib 2]
                                              [--cpu-seconds 600] [--architectures RANDLANET POINT_TRANSFORMER_V3]

``--epoch-iterations`` is the tool's own parameter, the percentage of training blocks one epoch visits; 10 is what
D-341 timed. Nothing is downloaded and nothing is written outside a temporary folder.
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

try:
    import resource         # Linux's; absent on Windows, where only --unfenced runs
except ImportError:
    resource = None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GRAPY = os.path.join(ROOT, "src", "grapy")
sys.path.insert(0, GRAPY)

ORIGIN = (-13627400.0, 4548200.0)     # a corner in EPSG:3857 metres; the numbers are arbitrary, the units are not
SIDE = 120.0


def synthetic_cloud(path, n, seed=7):
    """A city block of ``n`` points: ground, box buildings with roofs and walls, round trees, some unclassified."""
    import laspy
    from pyproj import CRS

    rng = np.random.default_rng(seed)
    x0, y0 = ORIGIN

    def ground_z(x, y):
        return 10.0 + 0.02 * (x - x0) + 0.01 * (y - y0)

    buildings = []
    while len(buildings) < 14:
        w, d = rng.uniform(8, 25, 2)
        bx, by = rng.uniform(x0 + 2, x0 + SIDE - 2 - w), rng.uniform(y0 + 2, y0 + SIDE - 2 - d)
        box = (bx, by, bx + w, by + d)
        if all(box[2] + 3 < o[0] or o[2] + 3 < box[0] or box[3] + 3 < o[1] or o[3] + 3 < box[1] for o in buildings):
            buildings.append(box)
    heights = rng.uniform(6, 60, len(buildings))
    trees = rng.uniform((x0 + 1, y0 + 1), (x0 + SIDE - 1, y0 + SIDE - 1), (60, 2))

    def inside_any(x, y):
        hit = np.zeros(len(x), bool)
        for a, b, c, d in buildings:
            hit |= (x >= a) & (x <= c) & (y >= b) & (y <= d)
        return hit

    n_building, n_tree, n_other = int(n * 0.33), int(n * 0.12), int(n * 0.03)
    n_ground = n - n_building - n_tree - n_other
    parts = []

    # Ground everywhere but under a roof: oversample and keep what falls outside the footprints.
    gx = rng.uniform(x0, x0 + SIDE, int(n_ground * 1.4))
    gy = rng.uniform(y0, y0 + SIDE, len(gx))
    keep = ~inside_any(gx, gy)
    gx, gy = gx[keep][:n_ground], gy[keep][:n_ground]
    parts.append((gx, gy, ground_z(gx, gy) + rng.normal(0, 0.05, len(gx)), 2))

    # Buildings: three quarters on the roof, a quarter on the walls, shared by footprint area.
    areas = np.array([(c - a) * (d - b) for a, b, c, d in buildings])
    counts = np.floor(n_building * areas / areas.sum()).astype(int)
    counts[0] += n_building - counts.sum()
    for (a, b, c, d), h, m in zip(buildings, heights, counts):
        roof = int(m * 0.75)
        rx, ry = rng.uniform(a, c, roof), rng.uniform(b, d, roof)
        parts.append((rx, ry, ground_z(rx, ry) + h + rng.normal(0, 0.05, roof), 6))
        wall = m - roof
        t = rng.uniform(0, 2 * ((c - a) + (d - b)), wall)
        wx = np.where(t < c - a, a + t, np.where(t < (c - a) + (d - b), c,
                      np.where(t < 2 * (c - a) + (d - b), c - (t - (c - a) - (d - b)), a)))
        wy = np.where(t < c - a, b, np.where(t < (c - a) + (d - b), b + (t - (c - a)),
                      np.where(t < 2 * (c - a) + (d - b), d, d - (t - 2 * (c - a) - (d - b)))))
        parts.append((wx, wy, ground_z(wx, wy) + rng.uniform(0, h, wall), 6))

    # Trees: points in a crown of radius 2-4 m on a 3-6 m trunk.
    k = rng.integers(0, len(trees), n_tree)
    radius = rng.uniform(2, 4, len(trees))[k]
    trunk = rng.uniform(3, 6, len(trees))[k]
    u = rng.normal(size=(n_tree, 3))
    u *= (radius * rng.uniform(0, 1, n_tree) ** (1 / 3) / np.linalg.norm(u, axis=1))[:, None]
    tx, ty = trees[k, 0] + u[:, 0], trees[k, 1] + u[:, 1]
    parts.append((tx, ty, ground_z(trees[k, 0], trees[k, 1]) + trunk + radius + u[:, 2], 5))

    # Unclassified: scattered low clutter.
    ox, oy = rng.uniform(x0, x0 + SIDE, n_other), rng.uniform(y0, y0 + SIDE, n_other)
    parts.append((ox, oy, ground_z(ox, oy) + rng.uniform(0, 3, n_other), 1))

    x = np.concatenate([p[0] for p in parts])
    y = np.concatenate([p[1] for p in parts])
    z = np.concatenate([p[2] for p in parts])
    c = np.concatenate([np.full(len(p[0]), p[3], np.uint8) for p in parts])
    base = {2: 40, 6: 90, 5: 25, 1: 60}
    intensity = np.clip(np.vectorize(base.get)(c) + rng.normal(0, 8, len(c)), 0, 65535).astype(np.uint16)

    header = laspy.LasHeader(point_format=1, version="1.2")
    header.scales = [0.001, 0.001, 0.001]
    header.offsets = [x0, y0, 0]
    header.add_crs(CRS.from_epsg(3857))
    las = laspy.LasData(header)
    las.x, las.y, las.z = x, y, z
    las.classification = c
    las.intensity = intensity
    las.return_number = np.ones(len(x), np.uint8)
    las.number_of_returns = np.ones(len(x), np.uint8)
    las.write(path)
    return {str(code): int((c == code).sum()) for code in (1, 2, 5, 6)}


def rectangle(arcpy, folder, name, x_from, x_to):
    x0, y0 = ORIGIN
    arcpy.management.CreateFeatureclass(folder, f"{name}.gpkg", "POLYGON", spatial_reference=3857)
    path = os.path.join(folder, f"{name}.gpkg")
    a, b = x0 + x_from * SIDE, x0 + x_to * SIDE
    with arcpy.da.InsertCursor(path, ["SHAPE@"]) as rows:
        rows.insertRow([arcpy.Polygon(arcpy.Array([arcpy.Point(a, y0), arcpy.Point(b, y0), arcpy.Point(b, y0 + SIDE),
                                                   arcpy.Point(a, y0 + SIDE), arcpy.Point(a, y0)]),
                                      arcpy.SpatialReference(3857))])
    return path


def prepare(run, points):
    """Step 1, unfenced: the cloud, its LAS dataset, the boundaries and the training data, all in ``run``."""
    import grapy as arcpy
    arcpy.env.workspace = run
    lidar = os.path.join(run, "lidar")
    os.makedirs(lidar)
    started = time.perf_counter()
    classes = synthetic_cloud(os.path.join(lidar, "tile.las"), points)
    lasd = arcpy.management.CreateLasDataset(lidar, os.path.join(run, "scene.lasd"))[0]
    training = rectangle(arcpy, run, "training", 0.0, 0.6)
    validation = rectangle(arcpy, run, "validation", 0.6, 0.7)
    folder = arcpy.ddd.PreparePointCloudTrainingData(lasd, "15 Meters", os.path.join(run, "train.pctd"), training,
                                                     validation_boundary=validation, block_point_limit=8192)[0]
    from grapy import _dlpoints
    with open(os.path.join(folder, _dlpoints.MANIFEST), encoding="utf-8") as read:
        manifest = json.load(read)
    splits = {k: {"blocks": v["blocks"], "points": v["points"]} for k, v in manifest["splits"].items()}
    return {"points": points, "classes": classes, "splits": splits,
            "seconds": round(time.perf_counter() - started, 1)}


TOOLBOX = '''
import grapy as arcpy

class Toolbox:
    def __init__(self):
        self.tools = [Learn]

class Learn:
    def getParameterInfo(self):
        return []

    def execute(self, parameters, messages):
        import time
        started = time.perf_counter()
        arcpy.ddd.TrainPointCloudClassificationModel(
            "train.pctd", ".", "m_{arch}", attributes=["INTENSITY"], max_epochs=1, epoch_iterations={iterations},
            learning_rate={rate}, batch_size=2, early_stop="NO_EARLY_STOP", architecture="{arch}")
        messages.addMessage("MEASURE trained in %.1f s of wall time" % (time.perf_counter() - started))
'''


def _status(pid):
    try:
        with open(f"/proc/{pid}/status") as read:
            fields = dict(line.split(":", 1) for line in read if ":" in line)
        return {k: int(fields[k].split()[0]) for k in ("VmPeak", "VmHWM") if k in fields}    # kB
    except (OSError, ValueError):
        return None


def _children_rusage():
    if resource is None:
        return None
    return resource.getrusage(resource.RUSAGE_CHILDREN)


def fenced_epoch(run, arch, iterations, rate, memory, cpu, unfenced=False):
    """Step 2: one epoch of ``arch`` inside the fence; answers what the parent saw of it."""
    with open(os.path.join(run, "learn.pyt"), "w") as out:
        out.write(textwrap.dedent(TOOLBOX).format(arch=arch, iterations=iterations, rate=rate))
    with open(os.path.join(run, "run.json"), "w") as out:
        json.dump({"mode": "run", "toolbox": "learn.pyt", "tool": "Learn",
                   "limits": {"memory": memory, "cpu": cpu, "processes": 1024}}, out)
    result_path = os.path.join(run, "result.json")
    if os.path.exists(result_path):
        os.remove(result_path)
    # The environment grapy's own fence test gives a run (test_grapy._run), and no more.
    environment = {k: v for k, v in os.environ.items() if k in ("PATH", "SYSTEMROOT")}
    environment["PYTHONPATH"] = GRAPY
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
        "architecture": arch,
        "fenced": not unfenced,
        "exit": code if code >= 0 else f"signal {signal.Signals(-code).name}",
        "cpu_seconds": round((after.ru_utime - before.ru_utime) + (after.ru_stime - before.ru_stime), 1)
        if after else None,
        "wall_seconds": round(wall, 1),
        # ru_maxrss of RUSAGE_CHILDREN is the largest of every child so far, so the second architecture's figure is a
        # maximum over both; VmHWM, polled per child, is that child's own.
        "peak_rss_mb": round(after.ru_maxrss / 1024, 1) if after else None,
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
        report["messages"] = [m.get("description", "") for m in result.get("messages", [])][-6:]
        if result.get("error"):
            report["error"] = result["error"]
    else:
        report["ok"] = False
        report["error"] = "no result.json: the process was ended before it could write one"
    return report


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--points", type=int, default=1_440_000)
    parser.add_argument("--epoch-iterations", type=float, default=10.0)
    parser.add_argument("--learning-rate", type=float, default=0.001,
                        help="given, so that the learning-rate range test is not part of what is timed")
    parser.add_argument("--memory-gib", type=float, default=2.0)
    parser.add_argument("--cpu-seconds", type=int, default=600)
    parser.add_argument("--architectures", nargs="+", default=["RANDLANET", "POINT_TRANSFORMER_V3"])
    parser.add_argument("--unfenced", action="store_true",
                        help="check the script itself without the fence (any system); what it prints is not a "
                             "measurement of the fence")
    args = parser.parse_args(argv)
    if sys.platform != "linux" and not args.unfenced:
        print("the fence is Linux's; run this on Linux (measure.yml does), or pass --unfenced to check the script",
              file=sys.stderr)
        return 2

    run = tempfile.mkdtemp(prefix="pcfence-")
    print(f"preparing {args.points:,} synthetic points in {run} (unfenced, not measured)", flush=True)
    prepared = prepare(run, args.points)
    print(json.dumps({"prepared": prepared}), flush=True)

    memory = int(args.memory_gib * (1 << 30))
    reports = []
    for arch in args.architectures:
        print(f"training {arch} for one epoch inside the fence", flush=True)
        report = fenced_epoch(run, arch, args.epoch_iterations, args.learning_rate, memory, args.cpu_seconds,
                              args.unfenced)
        print(json.dumps(report), flush=True)
        reports.append(report)

    print()
    print(f"{'architecture':<22} {'ok':<5} {'cpu s':>7} {'wall s':>7} {'VmPeak MB':>10} {'RSS MB':>8}  exit")
    for r in reports:
        print(f"{r['architecture']:<22} {str(r['ok']):<5} {str(r['cpu_seconds']):>7} {r['wall_seconds']:>7} "
              f"{r['peak_address_space_mb']:>10} {str(r['peak_rss_mb']):>8}  {r['exit']}")
    print(f"limits: {memory // (1 << 20)} MB of address space, {args.cpu_seconds} s of CPU; "
          f"training blocks {prepared['splits']['training']['blocks']}, "
          f"validation blocks {prepared['splits']['validation']['blocks']}, "
          f"{args.epoch_iterations:g} % of the training blocks per epoch")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a") as out:
            out.write("### Point cloud training inside the fence (ADR-249 condition 1)\n\n")
            out.write("| architecture | finished | CPU s | wall s | peak address space MB | peak RSS MB | exit |\n")
            out.write("|---|---|---|---|---|---|---|\n")
            for r in reports:
                out.write(f"| {r['architecture']} | {r['ok']} | {r['cpu_seconds']} | {r['wall_seconds']} | "
                          f"{r['peak_address_space_mb']} | {r['peak_rss_mb']} | {r['exit']} |\n")
            out.write(f"\n{json.dumps(prepared)}\n")
    # A run that does not fit is a result, not a failure of this script: the table and the summary carry it, and the
    # job stays green so its numbers are read rather than mistaken for a broken workflow.
    return 0


if __name__ == "__main__":
    sys.exit(main())
