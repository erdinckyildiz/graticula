import os
import tempfile
"""The two questions the bands do not answer: one enormous shape, and the pool.

ADR-022 condition 3 names both -- *"nobody has measured what a realistic buffer or
relation costs, so the ten-second deadline and the two-worker pool are sized from
overlay's numbers alone"*.
"""

import concurrent.futures as futures
import io, json, os, ssl, statistics, sys, time, urllib.parse, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

ROOT = "https://127.0.0.1:8447"
GEOM = ROOT + "/rest/services/Utilities/Geometry/GeometryServer"
# The working directory. It named one laptop's scratchpad until 2026-09-02.
HERE = os.environ.get("GRATICULA_WORK") or tempfile.mkdtemp()
CONTEXT = ssl._create_unverified_context()
SRID = 3857


def sign_in():
    request = urllib.request.Request(
        ROOT + "/rest/auth/login",
        data=json.dumps({"name": "ci", "password": "console-local-run-password"}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, context=CONTEXT, timeout=30) as answer:
        return json.loads(answer.read())["token"]


TOKEN = sign_in()


def post(path, fields, timeout=180):
    body = urllib.parse.urlencode(fields).encode()
    request = urllib.request.Request(
        GEOM + path, data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded",
                 "Authorization": "Bearer " + TOKEN})
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, context=CONTEXT, timeout=timeout) as answer:
            text, status = answer.read().decode(), answer.status
    except urllib.error.HTTPError as e:
        text, status = e.read().decode(), e.code
    return (time.perf_counter() - started) * 1000, status, text


def rings(geojson):
    return {"rings": [[[x, y] for x, y, *_ in reversed(ring)]
                      for ring in geojson["coordinates"]]}


def load(band):
    shapes = []
    for line in io.open(os.path.join(HERE, f"geom-{band}.jsonl"), encoding="utf-8"):
        line = line.strip()
        if line:
            shapes.append(rings(json.loads(line)))
    return shapes


BIG = load("large")
HUGE = load("huge")


def many(shapes):
    return json.dumps({"geometryType": "esriGeometryPolygon", "geometries": shapes})


def note(text):
    try:
        document = json.loads(text)
    except ValueError:
        return text[:70]
    if "error" in document:
        return str(document["error"].get("message", ""))[:100]
    cost = document.get("cost")
    return f"engine={cost['milliseconds']}ms" if isinstance(cost, dict) else ""


print("--- one enormous polygon, alone ---")
for label, shape in [(f"{sum(len(r) for r in s['rings'])} vertices", s) for s in HUGE[:3]]:
    elapsed, status, text = post(
        "/buffer",
        {"sr": SRID, "geometries": many([shape]), "distances": "10", "f": "json"})
    print(f"buffer, one polygon of {label:22s} {status}  {elapsed:9.1f}ms  {note(text)}")

print()
print("--- the pool: how four concurrent expensive requests behave ---")
work = {"sr": SRID, "geometries": many(BIG[:30]), "distances": "10", "f": "json"}

alone, status, text = post("/buffer", work)
print(f"one alone                          {status}  {alone:9.1f}ms  {note(text)}")

for callers in (2, 4, 8):
    with futures.ThreadPoolExecutor(max_workers=callers) as pool:
        started = time.perf_counter()
        answers = list(pool.map(lambda _: post("/buffer", work), range(callers)))
        span = (time.perf_counter() - started) * 1000

    times = sorted(a[0] for a in answers)
    codes = sorted({a[1] for a in answers})

    print(f"{callers} at once   wall {span:9.1f}ms   "
          f"per-request median={statistics.median(times):8.1f}ms max={times[-1]:8.1f}ms  "
          f"statuses={codes}")
