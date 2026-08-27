"""ADR-022 condition 4: what a request at the vertex cap actually costs.

500,000 vertices is `GeometryServerEndpoints.MaximumVertices`, and the condition says it
came from the corpus and a JSON size estimate rather than from a measurement. This builds
requests at and around the cap out of real polygons and asks what happens -- alone, and
under concurrency, which is the half the condition names.
"""

import concurrent.futures as futures
import io, json, os, ssl, statistics, sys, time, urllib.parse, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

ROOT = "https://127.0.0.1:8447"
GEOM = ROOT + "/rest/services/Utilities/Geometry/GeometryServer"
HERE = ("C:/Users/Erdinc/AppData/Local/Temp/claude/c--Personal-Projects-GIS/"
        "9db7f59f-6624-459d-b69c-f9b00b1599e7/scratchpad")
CONTEXT = ssl._create_unverified_context()
SRID = 3857
CAP = 500_000


def sign_in():
    request = urllib.request.Request(
        ROOT + "/rest/auth/login",
        data=json.dumps({"name": "ci", "password": "console-local-run-password"}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, context=CONTEXT, timeout=30) as answer:
        return json.loads(answer.read())["token"]


TOKEN = sign_in()


def post(path, fields, timeout=300):
    body = urllib.parse.urlencode(fields).encode()
    request = urllib.request.Request(
        GEOM + path, data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded",
                 "Authorization": "Bearer " + TOKEN})
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, context=CONTEXT, timeout=timeout) as answer:
            return (time.perf_counter() - started) * 1000, answer.status, answer.read().decode()
    except urllib.error.HTTPError as e:
        return (time.perf_counter() - started) * 1000, e.code, e.read().decode()
    except Exception as e:                                            # noqa: BLE001
        return (time.perf_counter() - started) * 1000, 0, f"{type(e).__name__}: {e}"


def rings(geojson):
    return {"rings": [[[x, y] for x, y, *_ in reversed(ring)]
                      for ring in geojson["coordinates"]]}


def load(band):
    return [rings(json.loads(l))
            for l in io.open(os.path.join(HERE, f"geom-{band}.jsonl"), encoding="utf-8")
            if l.strip()]


LARGE = load("large")
HUGE = load("huge")


def count(shape):
    return sum(len(r) for r in shape["rings"])


def build(target):
    """A geometry list of about `target` vertices, made of real shapes repeated."""
    shapes, total, i = [], 0, 0
    while total < target:
        shape = LARGE[i % len(LARGE)]
        shapes.append(shape)
        total += count(shape)
        i += 1
    return shapes, total


def many(shapes):
    return json.dumps({"geometryType": "esriGeometryPolygon", "geometries": shapes})


def note(text):
    try:
        document = json.loads(text)
    except ValueError:
        return text[:80]
    if "error" in document:
        return str(document["error"].get("message", ""))[:110]
    cost = document.get("cost")
    return f"engine={cost['milliseconds']}ms" if isinstance(cost, dict) else "ok"


print("--- a request at, under and over the cap ---")
print(f"the cap is GeometryServerEndpoints.MaximumVertices = {CAP:,}")
print()

for fraction in (0.1, 0.25, 0.5, 0.9, 1.0, 1.1):
    shapes, total = build(int(CAP * fraction))
    body = many(shapes)

    elapsed, status, text = post(
        "/simplify", {"sr": SRID, "geometries": body, "f": "json"})

    print(f"simplify {total:>8,} vertices in {len(shapes):>4} shapes  "
          f"({len(body) / 1_048_576:5.1f} MB of JSON)  "
          f"{status}  {elapsed:9.1f}ms  {note(text)}")

print()
print("--- the same sizes through buffer, which is the expensive one ---")
for fraction in (0.1, 0.25, 0.5, 1.0):
    shapes, total = build(int(CAP * fraction))
    elapsed, status, text = post(
        "/buffer",
        {"sr": SRID, "geometries": many(shapes), "distances": "10", "f": "json"})
    print(f"buffer   {total:>8,} vertices in {len(shapes):>4} shapes  "
          f"{status}  {elapsed:9.1f}ms  {note(text)}")

print()
print("--- at the cap, under concurrency: the half the condition names ---")
shapes, total = build(CAP)
work = {"sr": SRID, "geometries": many(shapes), "f": "json"}
print(f"each request carries {total:,} vertices")

for callers in (1, 2, 4):
    with futures.ThreadPoolExecutor(max_workers=callers) as pool:
        started = time.perf_counter()
        answers = list(pool.map(lambda _: post("/simplify", work), range(callers)))
        span = (time.perf_counter() - started) * 1000

    times = sorted(a[0] for a in answers)
    codes = sorted({a[1] for a in answers})
    print(f"{callers} at once  wall {span:9.1f}ms  median={statistics.median(times):9.1f}ms  "
          f"max={times[-1]:9.1f}ms  statuses={codes}  {note(answers[0][2])}")
