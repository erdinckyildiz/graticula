"""ADR-022 condition 3: what the six operations §2b added actually cost.

Real OSM polygons in three vertex bands, through the running GeometryServer, timed
from outside — which is the number an operator's ten-second deadline is compared
against. The server reports its own `cost.milliseconds` beside it, so the difference
between the two is the request's overhead rather than the geometry's.
"""

import io, json, os, ssl, statistics, sys, time, urllib.parse, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

ROOT = "https://127.0.0.1:8447"
GEOM = ROOT + "/rest/services/Utilities/Geometry/GeometryServer"
HERE = ("C:/Users/Erdinc/AppData/Local/Temp/claude/c--Personal-Projects-GIS/"
        "9db7f59f-6624-459d-b69c-f9b00b1599e7/scratchpad")
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


def post(path, fields, timeout=120):
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
    """A GeoJSON polygon as ArcGIS rings.

    <b>Every ring is reversed, and it has to be.</b> GeoJSON winds an outer ring
    counter-clockwise and a hole clockwise; ArcGIS is the other way round, and this server
    refuses a counter-clockwise first ring rather than guessing -- *"which ArcGIS reads as a
    hole"*. Reversing each ring converts both roles at once. Found by the refusal, which is
    the behaviour working.
    """
    return {"rings": [[[x, y] for x, y, *_ in reversed(ring)]
                      for ring in geojson["coordinates"]]}


BANDS = {}
for band in ("small", "medium", "large"):
    shapes = []
    for line in io.open(os.path.join(HERE, f"geom-{band}.jsonl"), encoding="utf-8"):
        line = line.strip()
        if line:
            shapes.append(rings(json.loads(line)))
    BANDS[band] = shapes


def vertices(shape):
    return sum(len(r) for r in shape["rings"])


print("corpus: public.planet_osm_polygon, 6,499,215 polygons, 77,089,382 vertices")
for band, shapes in BANDS.items():
    counts = sorted(vertices(s) for s in shapes)
    print(f"  {band:7s} {len(shapes)} shapes  "
          f"vertices min={counts[0]} median={statistics.median(counts):.0f} max={counts[-1]}")


def many(shapes):
    return json.dumps({"geometryType": "esriGeometryPolygon", "geometries": shapes})


def run(label, path, fields, rounds=5, timeout=120):
    wall, engine, status, note = [], [], None, ""

    for _ in range(rounds):
        elapsed, status, text = post(path, fields, timeout)
        wall.append(elapsed)

        try:
            document = json.loads(text)
        except ValueError:
            note = text[:70]
            continue

        if "error" in document:
            note = str(document["error"].get("message", ""))[:70]
            break

        if isinstance(document.get("cost"), dict):
            engine.append(document["cost"].get("milliseconds", 0))

    wall.sort()
    inside = f"engine={statistics.median(engine):7.0f}ms" if engine else "engine=       -"
    print(f"{label:38s} {status}  wall median={statistics.median(wall):8.1f}ms  "
          f"max={wall[-1]:8.1f}ms  {inside}  {note}")

    return statistics.median(wall)


print()
print("--- buffer, by input size and by width ---")
for band in ("small", "medium", "large"):
    for width in (10, 500):
        run(f"buffer 30 {band}, {width} m", "/buffer",
            {"sr": SRID, "geometries": many(BANDS[band][:30]),
             "distances": str(width), "f": "json"}, rounds=3)

print()
print("--- relation, by pair count ---")
for band in ("small", "medium", "large"):
    for n in (10, 30):
        run(f"relation {n}x{n} {band} ({n * n} pairs)", "/relation",
            {"sr": SRID,
             "geometries1": many(BANDS[band][:n]),
             "geometries2": many(BANDS[band][30:30 + n]),
             "relation": "esriGeometryRelationIntersection", "f": "json"}, rounds=3)

print()
print("--- the other four, on the largest band ---")
big = BANDS["large"]

run("union 30 large", "/union",
    {"sr": SRID, "geometries": many(big[:30]), "f": "json"}, rounds=3)

run("simplify 30 large", "/simplify",
    {"sr": SRID, "geometries": many(big[:30]), "f": "json"}, rounds=3)

run("offset 30 large, 10 m", "/offset",
    {"sr": SRID, "geometries": many(big[:30]), "offsetDistance": "10", "f": "json"}, rounds=3)

run("distance, two large", "/distance",
    {"sr": SRID,
     "geometry1": json.dumps(big[0]),
     "geometry2": json.dumps(big[1]),
     "f": "json"}, rounds=3)

first = big[0]["rings"][0]
xs = [p[0] for p in first]
ys = [p[1] for p in first]
mid = (min(ys) + max(ys)) / 2

run("cut, one large by a line across it", "/cut",
    {"sr": SRID,
     "target": json.dumps(big[0]),
     "cutter": json.dumps({"paths": [[[min(xs) - 1000, mid], [max(xs) + 1000, mid]]]}),
     "f": "json"}, rounds=3)

print()
print("--- the adversarial one: the corpus's largest polygon ---")
