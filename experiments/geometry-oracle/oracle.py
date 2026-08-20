"""Asks the same predicate of both of v1's geometry engines and compares the answers.

Q-20 asks how many engines end up evaluating our predicates, and how divergence is
prevented. The register counts **six** and most of them are deferred with the
providers they belong to. **In v1 the count is two, and both are reachable today:**

  * **PostGIS's GEOS** — every spatial filter on a FeatureServer query becomes
    `st_intersects`, `st_contains`, `st_within`, `st_crosses`, `st_overlaps`,
    `st_touches` or `st_relate` (`PostGisFeatureSource.Predicate`).
  * **NTS's JTS port, in the overlay worker** — `GeometryServer/relation` calls
    `Geometry.Relate` and then JTS's `Disjoint`, `Intersects`, `Within`, `Touches`,
    `Crosses`, `Overlaps` (`Graticula.Overlay.Worker`, `Satisfies`).

Six predicates are answerable by both surfaces. So the divergence Q-20 warns about
is not a future-provider problem in v1: a client can ask *do these two geometries
touch* of either surface today, and until this script nothing in this repository had
ever compared the two answers.

**Both engines get the same numbers, from one definition.** Each case is written
once as coordinates and rendered twice — as Esri JSON for our surface (outer ring
clockwise, holes counter-clockwise, which is what `ArcGisGeometryReader` reads) and
as WKT for PostGIS. A case written twice by hand is a case where a disagreement can
be a typo.

    python experiments/geometry-oracle/oracle.py

Needs the dev server on https://127.0.0.1:8443 and the experiment PostGIS on
localhost:55432. Reads only: no table is created, no service is touched.

An experiment, and CLAUDE.md §1 governs it — `/experiments` is disposable and never
promoted. What survives is the table it prints and, for each disagreement, a case
small enough to reason about.
"""

import json
import ssl
import subprocess
import urllib.error
import urllib.parse
import urllib.request

# <b>Read from the environment, never written here.</b> These scripts sign in to a
# development server, and a password in a file is a password in the repository's
# history the moment the file is committed -- where removing it later removes it
# from the tip and from nowhere else. Set GRATICULA_DEV_PASSWORD before running.
DEV_PASSWORD = os.environ.get("GRATICULA_DEV_PASSWORD", "")
import os

BASE = "https://127.0.0.1:8443"
RELATION = f"{BASE}/rest/services/Utilities/Geometry/GeometryServer/relation"

# `st_dwithin` is the query path's alone, and `st_contains` is `within` with the
# arguments swapped, so it is covered by asking `within` both ways round.
#
# <b>`st_relate` is not the query path's alone, and the first version of this file said
# it was.</b> `esriGeometryRelationRelation` with a DE-9IM pattern in `relationParam`
# reaches `relation.Matches(pattern)` in the worker, and `SpatialRelation.Relate`
# reaches `st_relate(column, filter, @pattern)` in the provider — so **both engines
# answer DE-9IM**, and it is the most intricate predicate either library has. Excluding
# it left the hardest comparison unmade. `PATTERNS` below carries it.
PREDICATES = [
    ("st_intersects", "esriGeometryRelationIntersection"),
    ("st_within", "esriGeometryRelationWithin"),
    ("st_crosses", "esriGeometryRelationCross"),
    ("st_overlaps", "esriGeometryRelationOverlap"),
    ("st_touches", "esriGeometryRelationTouch"),
    ("st_disjoint", "esriGeometryRelationDisjoint"),
]

# <b>DE-9IM patterns, each chosen because it separates two cases that a named predicate
# runs together.</b> The matrix is interior/boundary/exterior of A against the same of
# B, row-major, and a pattern is nine characters of `T`, `F`, `0`, `1`, `2` or `*`.
PATTERNS = [
    ("T********", "the interiors meet at all — weaker than `overlaps`"),
    ("FF*FF****", "disjoint, spelled as a matrix"),
    ("T*F**F***", "within, spelled as a matrix"),
    ("F***T****", "boundaries touch and interiors do not — `touches` for two areas"),
    ("2********", "the interiors meet in an *area*, not a line or a point"),
    ("*T*******", "A's interior meets B's boundary"),
    ("****1****", "the boundaries share a line rather than a point"),
    ("T*T***T**", "A crosses B's boundary in both directions"),
]


def ring(*points):
    """A closed ring, given the corners once."""
    return [*points, points[0]]


def flipped(points):
    """The same ring wound the other way."""
    return list(reversed(points))


def winding(points):
    """Twice the signed area: negative is clockwise with y upwards."""
    return sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(points, points[1:]))


SQUARE = ring((0, 0), (0, 10), (10, 10), (10, 0))
RIGHT_SQUARE = ring((10, 0), (10, 10), (20, 10), (20, 0))
CORNER_SQUARE = ring((10, 10), (10, 20), (20, 20), (20, 10))
BOWTIE = ring((0, 0), (10, 10), (10, 0), (0, 10))
INNER = ring((3, 3), (3, 7), (7, 7), (7, 3))
HOLE = ring((2, 2), (2, 8), (8, 8), (8, 2))
NUDGE_APART = ring((10.000000001, 0), (10.000000001, 10), (20, 10), (20, 0))
NUDGE_OVER = ring((9.999999999, 0), (9.999999999, 10), (20, 10), (20, 0))
SLIVER = ring((0, 0), (10, 0), (10, 0.000000001), (0, 0.000000001))

# <b>The cases are the four edges Q-20 itself names</b> — validity, precision, what
# touches, empty geometries — plus the hole case, where a box answer and a
# topological answer differ.
CASES = [
    ("shared edge", ("polygon", [SQUARE]), ("polygon", [RIGHT_SQUARE]),
     "Two squares meeting along a whole edge: touches, does not overlap."),

    ("shared vertex only", ("polygon", [SQUARE]), ("polygon", [CORNER_SQUARE]),
     "Corner to corner. The boundary intersection is a point, not a line."),

    ("identical", ("polygon", [SQUARE]), ("polygon", [SQUARE]),
     "Equal polygons: within and contains hold, overlaps is false by definition."),

    ("point on the boundary", ("point", (0, 5)), ("polygon", [SQUARE]),
     "Within is false, intersects and touches are true."),

    ("point in the middle", ("point", (5, 5)), ("polygon", [SQUARE]),
     "The ordinary case, as a control: if this disagrees, the harness is wrong."),

    ("line ending on the boundary", ("line", [(-5, 5), (0, 5)]), ("polygon", [SQUARE]),
     "The line stops exactly on the edge: touches, does not cross."),

    ("line through", ("line", [(-5, 5), (15, 5)]), ("polygon", [SQUARE]),
     "Crosses is true for a line through a polygon."),

    ("line along the boundary", ("line", [(0, 2), (0, 8)]), ("polygon", [SQUARE]),
     "Collinear with an edge. Neither engine's answer here is obvious."),

    ("invalid bowtie against a square", ("polygon", [BOWTIE]), ("polygon", [HOLE]),
     "The first self-intersects. An engine may throw, repair, or answer from a "
     "broken topology — and which it does is the whole of Q-20."),

    ("invalid bowtie against itself", ("polygon", [BOWTIE]), ("polygon", [BOWTIE]),
     "An invalid geometry compared with itself."),

    ("empty against a square", ("polygon", []), ("polygon", [SQUARE]),
     "Disjoint is the interesting one: an empty set is disjoint from everything, "
     "and not every library says so."),

    ("empty against empty", ("polygon", []), ("polygon", []),
     "Two empties."),

    ("a nanometre apart", ("polygon", [SQUARE]), ("polygon", [NUDGE_APART]),
     "A gap of 1e-9 in degrees: disjoint if the engine is exact, touching if it snaps."),

    ("a nanometre overlapping", ("polygon", [SQUARE]), ("polygon", [NUDGE_OVER]),
     "An overlap of 1e-9: overlaps if exact."),

    ("collapsed sliver", ("polygon", [SLIVER]), ("polygon", [SQUARE]),
     "A polygon a nanometre tall sharing the square's base."),

    ("polygon inside a hole", ("polygon", [INNER]), ("polygon", [SQUARE, HOLE]),
     "The first sits in the second's hole, so it is *not* within it — where a "
     "bounding-box answer and a topological answer part company."),
]


# <b>Everything above is within 0-25, and web-Mercator metres run to 2×10⁷.</b>
# Floating-point divergence grows with magnitude: at 2×10⁶ the gap between
# representable doubles is about 5×10⁻¹⁰ of a metre, and the 1e-9 cases are then
# *below* the representable step — which is exactly where two libraries can round in
# opposite directions. So every case is run twice, once as written and once shifted.
MAGNITUDES = [
    (1.0, 0.0, "as written, 0-25"),
    (1.0, 2_000_000.0, "shifted to 2e6, near web-Mercator metres"),
]


def shifted(shape, scale, offset):
    """The same shape moved out to a large coordinate."""
    kind, data = shape

    def move(point):
        return (point[0] * scale + offset, point[1] * scale + offset)

    if kind == "point":
        return (kind, move(data))

    if kind == "line":
        return (kind, [move(p) for p in data])

    return (kind, [[move(p) for p in r] for r in data])


def wkt(shape):
    """WKT for PostGIS, from the case's own coordinates."""
    kind, data = shape

    if kind == "point":
        return f"POINT({data[0]} {data[1]})"

    if kind == "line":
        return "LINESTRING(" + ",".join(f"{x} {y}" for x, y in data) + ")"

    if not data:
        return "POLYGON EMPTY"

    return "POLYGON(" + ",".join(
        "(" + ",".join(f"{x} {y}" for x, y in r) + ")" for r in data) + ")"


def esri(shape):
    """Esri JSON for our surface, from the same coordinates."""
    kind, data = shape
    reference = {"wkid": 4326}

    if kind == "point":
        return {"x": data[0], "y": data[1], "spatialReference": reference}

    if kind == "line":
        return {"paths": [[[x, y] for x, y in data]], "spatialReference": reference}

    # <b>Outer ring clockwise, holes counter-clockwise, computed rather than
    # assumed.</b> `ArcGisGeometryReader` rebuilds shells and holes from winding
    # order and refuses a first ring that is counter-clockwise, so this is the
    # format and not a preference. The first version of this function reversed the
    # outer ring on the assumption that the rings above were written
    # counter-clockwise; they are written clockwise, so it produced exactly the
    # refusal the reader exists to give. Measuring the winding removes the
    # assumption — and the harness being wrong first is this repository's normal
    # order of events.
    rings = []

    for index, r in enumerate(data):
        want_clockwise = index == 0
        is_clockwise = winding(r) < 0

        rings.append([[x, y] for x, y in (r if is_clockwise == want_clockwise else flipped(r))])

    return {"rings": rings, "spatialReference": reference}


def postgis(sql):
    out = subprocess.run(
        ["docker", "exec", "gis-experiment-postgis",
         "psql", "-U", "gis", "-d", "gis", "-t", "-A", "-c", sql],
        capture_output=True, text=True, timeout=120)

    if out.returncode != 0:
        return None, " ".join(out.stderr.split())

    return {"t": True, "f": False}.get(out.stdout.strip(), out.stdout.strip()), None


def token():
    context = ssl.create_default_context()
    context.check_hostname = False
    context.verify_mode = ssl.CERT_NONE

    request = urllib.request.Request(
        f"{BASE}/rest/auth/login",
        data=json.dumps({"name": "root", "password": DEV_PASSWORD}).encode(),
        headers={"Content-Type": "application/json"})

    with urllib.request.urlopen(request, context=context, timeout=30) as answer:
        return json.load(answer)["token"], context


def ours(bearer, context, left, right, relation, pattern=None):
    """Whether our engine says the pair satisfies the relation, or the DE-9IM pattern."""
    asked = {
        "geometries1": json.dumps({"geometryType": kindname(left), "geometries": [esri(left)]}),
        "geometries2": json.dumps({"geometryType": kindname(right), "geometries": [esri(right)]}),
        "sr": "4326",
        "f": "json",
    }

    if pattern is None:
        asked["relation"] = relation
    else:
        asked["relation"] = "esriGeometryRelationRelation"
        asked["relationParam"] = pattern

    body = urllib.parse.urlencode(asked).encode()

    request = urllib.request.Request(
        RELATION, data=body,
        headers={"Content-Type": "application/x-www-form-urlencoded",
                 "Authorization": f"Bearer {bearer}"})

    try:
        with urllib.request.urlopen(request, context=context, timeout=90) as answer:
            got = json.load(answer)
    except urllib.error.HTTPError as refused:
        detail = refused.read().decode()

        try:
            said = json.loads(detail)["error"]["message"]
        except Exception:
            said = detail[:200]

        return None, f"HTTP {refused.code}: {said}"

    if "error" in got:
        return None, str(got["error"].get("message", got["error"]))[:200]

    pairs = got.get("relations", got.get("pairs", []))
    return bool(pairs), None


def kindname(shape):
    return {"point": "esriGeometryPoint",
            "line": "esriGeometryPolyline",
            "polygon": "esriGeometryPolygon"}[shape[0]]


def main():
    bearer, context = token()

    print("Q-20's oracle. v1 has two engines that answer the same predicates, and "
          "this asks both.\n")

    rows = []

    for scale, offset, magnitude in MAGNITUDES:
        for name, left_raw, right_raw, why in CASES:
            left = shifted(left_raw, scale, offset)
            right = shifted(right_raw, scale, offset)

            for sql_name, esri_name in PREDICATES:
                answer, error = postgis(
                    f"select {sql_name}(st_geomfromtext('{wkt(left)}', 4326), "
                    f"st_geomfromtext('{wkt(right)}', 4326))")

                mine, mine_error = ours(bearer, context, left, right, esri_name)

                rows.append({
                    "case": name, "magnitude": magnitude, "why": why, "predicate": sql_name,
                    "left": wkt(left), "right": wkt(right),
                    "postgis": "ERROR" if error else answer,
                    "postgisError": error,
                    "ours": "ERROR" if mine_error else mine,
                    "oursError": mine_error,
                    "agree": error is None and mine_error is None and answer == mine,
                })

            # <b>And the matrix itself</b>, which is where two implementations of the
            # same specification have the most room to differ.
            for pattern, meaning in PATTERNS:
                answer, error = postgis(
                    f"select st_relate(st_geomfromtext('{wkt(left)}', 4326), "
                    f"st_geomfromtext('{wkt(right)}', 4326), '{pattern}')")

                mine, mine_error = ours(
                    bearer, context, left, right, None, pattern=pattern)

                rows.append({
                    "case": name, "magnitude": magnitude, "why": meaning,
                    "predicate": f"st_relate '{pattern}'",
                    "left": wkt(left), "right": wkt(right),
                    "postgis": "ERROR" if error else answer,
                    "postgisError": error,
                    "ours": "ERROR" if mine_error else mine,
                    "oursError": mine_error,
                    "agree": error is None and mine_error is None and answer == mine,
                })

    disagreed = [r for r in rows if not r["agree"]]

    print(f"{len(rows)} comparisons: {len(rows) - len(disagreed)} agree, "
          f"{len(disagreed)} do not.\n")

    if disagreed:
        print(f"{'case':32} {'predicate':22} {'PostGIS':8} {'ours':8} magnitude")
        print("-" * 100)

        for r in disagreed:
            print(f"{r['case'][:30]:32} {r['predicate']:22} "
                  f"{str(r['postgis']):8} {str(r['ours']):8} {r['magnitude']}")

            for side in ("postgisError", "oursError"):
                if r[side]:
                    print(f"     {side[:-5]}: {r[side][:160]}")

    with open("experiments/geometry-oracle/answers.json", "w", encoding="utf-8") as out:
        json.dump(rows, out, indent=1)

    print("\nEvery answer, agreeing or not, is in "
          "experiments/geometry-oracle/answers.json.")


if __name__ == "__main__":
    main()
