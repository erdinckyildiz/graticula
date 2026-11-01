"""ADR-250 condition 3: build and adjust a parcel fabric of some thousands of parcels in grapy, and record the time
and the memory against ADR-223's limits for a user tool (2 GB of memory, 10 minutes of CPU, 30 of wall clock).

    D:/temp/grapy-env/Scripts/python.exe benchmarks/parcel-fabric-scale/measure.py <work folder> <columns> <rows>

A grid of columns x rows lots, 20 m by 30 m, in UTM 32N (EPSG:32632, so the adjustment runs on the ellipsoid with a
scale that is not 1); every corner moved up to 0.2 m at random (seeded), so the drawn fabric and its dimensions
disagree. One record per row of lots. Each step is timed with the process's CPU and wall clock and the peak working
set: create and insert, BuildParcelFabric (the lines and points from the polygons), then the lines' Distance
and Direction written from the undisturbed grid, four corner points XYZ constrained, a weighted adjustment, and its
application. Prints one JSON document.
"""
import json
import os
import random
import shutil
import sys
import time

work, columns, rows_ = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "src", "grapy"))
import grapy as arcpy  # noqa: E402
from grapy import _parcel as pf  # noqa: E402



def peak_mb():
    """The process's peak working set (Windows) or maximum resident set (elsewhere), in MB."""
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes

        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD),
                        ("PeakWorkingSetSize", ctypes.c_size_t), ("WorkingSetSize", ctypes.c_size_t),
                        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t), ("QuotaPagedPoolUsage", ctypes.c_size_t),
                        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t), ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
                        ("PagefileUsage", ctypes.c_size_t), ("PeakPagefileUsage", ctypes.c_size_t)]
        counters = Counters()
        counters.cb = ctypes.sizeof(Counters)
        current = ctypes.windll.kernel32.GetCurrentProcess
        current.restype = wintypes.HANDLE
        info = ctypes.windll.psapi.GetProcessMemoryInfo
        info.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
        info(current(), ctypes.byref(counters), counters.cb)
        return counters.PeakWorkingSetSize / 1048576
    import resource
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024
W, H, X0, Y0 = 20.0, 30.0, 700000.0, 5600000.0
JITTER = 0.2
random.seed(250)
steps = []


def step(name, call):
    cpu, wall = time.process_time(), time.perf_counter()
    answer = call()
    steps.append({"step": name, "wall_s": round(time.perf_counter() - wall, 2),
                  "cpu_s": round(time.process_time() - cpu, 2), "peak_mb": round(peak_mb(), 1)})
    print(steps[-1], file=sys.stderr, flush=True)
    return answer


if os.path.exists(work):
    shutil.rmtree(work)
os.makedirs(work)
corner = {}
for i in range(columns + 1):
    for j in range(rows_ + 1):
        corner[i, j] = (X0 + i * W + random.uniform(-JITTER, JITTER), Y0 + j * H + random.uniform(-JITTER, JITTER))
true = {(i, j): (X0 + i * W, Y0 + j * H) for i in range(columns + 1) for j in range(rows_ + 1)}


def create():
    gpkg = arcpy.management.CreateFileGDB(work, "scale")[0]
    fds = arcpy.management.CreateFeatureDataset(gpkg, "Parcels", 32632)[0]
    fab = arcpy.parcel.CreateParcelFabric(fds, "Fab")[0]
    arcpy.parcel.AddParcelType(fab, "Lot")
    with arcpy.da.InsertCursor(f"{fds}/Fab_Records", ["Name"]) as cursor:
        for j in range(rows_):
            cursor.insertRow([f"R{j}"])
    with arcpy.da.SearchCursor(f"{fds}/Fab_Records", ["Name", "GlobalID"]) as cursor:
        records = dict(cursor)
    with arcpy.da.InsertCursor(f"{fds}/Lot", ["Name", "CreatedByRecord", "SHAPE@"]) as cursor:
        for j in range(rows_):
            for i in range(columns):
                ring = [corner[i, j], corner[i + 1, j], corner[i + 1, j + 1], corner[i, j + 1], corner[i, j]]
                cursor.insertRow([f"L{i}_{j}", records[f"R{j}"],
                                  "POLYGON((" + ", ".join(f"{x!r} {y!r}" for x, y in ring) + "))"])
    return gpkg, fds, fab


gpkg, fds, fab = step("create and insert", create)
step("BuildParcelFabric", lambda: arcpy.parcel.BuildParcelFabric(fab))
counts = {"parcels": columns * rows_,
          "lines": int(arcpy.management.GetCount(f"{fds}/Lot_Lines")[0]),
          "points": int(arcpy.management.GetCount(f"{fds}/Fab_Points")[0])}
print(counts, file=sys.stderr, flush=True)
by_place = {(round(x, 6), round(y, 6)): key for key, (x, y) in corner.items()}


def dimensions():
    import math
    with arcpy.da.UpdateCursor(f"{fds}/Lot_Lines", ["SHAPE@", "Distance", "Direction"]) as cursor:
        for shape, _, _ in cursor:
            a = by_place[(round(shape.firstPoint.X, 6), round(shape.firstPoint.Y, 6))]
            b = by_place[(round(shape.lastPoint.X, 6), round(shape.lastPoint.Y, 6))]
            (xa, ya), (xb, yb) = true[a], true[b]
            # the undisturbed grid's dimensions, as a grid length (UTM's scale makes them a little long on the
            # ellipsoid, which the four constrained corners then hold against)
            cursor.updateRow([shape, math.hypot(xb - xa, yb - ya),
                              math.degrees(math.atan2(xb - xa, yb - ya)) % 360.0])
    ends = {corner[0, 0], corner[columns, 0], corner[0, rows_], corner[columns, rows_]}
    with arcpy.da.UpdateCursor(f"{fds}/Fab_Points", ["SHAPE@", "AdjustmentConstraint", "X", "Y"]) as cursor:
        for shape, _, _, _ in cursor:
            place = (shape.firstPoint.X, shape.firstPoint.Y)
            key = by_place[(round(place[0], 6), round(place[1], 6))]
            if place in ends:
                cursor.updateRow([shape, pf.XYZ_CONSTRAINED, true[key][0], true[key][1]])


step("dimensions and four controls", dimensions)
result = step("AnalyzeParcelsByLeastSquaresAdjustment", lambda: arcpy.parcel.AnalyzeParcelsByLeastSquaresAdjustment(
    fab, "WEIGHTED_LEAST_SQUARES"))
messages = arcpy.GetMessages()
step("ApplyParcelLeastSquaresAdjustment", lambda: arcpy.parcel.ApplyParcelLeastSquaresAdjustment(fab, None))
print(json.dumps({"columns": columns, "rows": rows_, **counts, "steps": steps,
                  "messages": [m for m in messages.splitlines() if m.startswith(("Number", "Degrees", "Standard",
                                                                                  "SOLUTION"))]}, indent=1))
