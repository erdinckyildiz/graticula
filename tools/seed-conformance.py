#!/usr/bin/env python3
"""Creates the fixtures the conformance suite needs, through the public API.

**D-29's open half.** The conformance suite walks the request sequence a real
ArcGIS client makes, and it needs a server with things published on it: a
multi-layer service, a group layer, a tile service, an editable layer, a layer
with enough features to page through. Those existed only because somebody made
them by hand on one machine, so CI could not run about a sixth of the suite --
and it was the sixth that checks the compatibility claim end to end.

**Everything here goes through the same HTTP API a client uses.** Nothing writes
to the database directly. That is slower and it is the point: a seed that
reached past the API could publish something the API cannot, and the suite would
then be testing a state the product cannot reach.

**Idempotent by name.** Re-running it against a server that already has these
skips what exists rather than failing or duplicating, so it is safe on a
developer machine as well as on a fresh CI container.

Usage:
    python tools/seed-conformance.py --url https://127.0.0.1:8443 \\
        --name root --password '...' [--setup-token TOKEN] [--prefix ci]

With --setup-token it performs the first-start bootstrap and creates the
account; without it the account must already exist. The token is printed once to
the server's log on first start.

It prints the environment variables the suite wants, so a caller can eval them.
"""

import argparse
import json
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

TIMEOUT = 60


class Server:
    """The admin and REST surfaces, over one session."""

    def __init__(self, url, insecure=True):
        self.url = url.rstrip("/")
        context = ssl._create_unverified_context() if insecure else None
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPSHandler(context=context)
        )
        self.token = None

    def call(self, method, path, body=None, form=None, expect=(200, 201)):
        """One request. Returns (status, parsed-or-text)."""
        data = None
        headers = {}

        if body is not None:
            data = json.dumps(body).encode()
            headers["Content-Type"] = "application/json"
        elif form is not None:
            data = urllib.parse.urlencode(form).encode()
            headers["Content-Type"] = "application/x-www-form-urlencoded"

        if self.token:
            headers["Authorization"] = "Bearer " + self.token

        request = urllib.request.Request(
            self.url + path, data=data, headers=headers, method=method
        )

        try:
            with self.opener.open(request, timeout=TIMEOUT) as response:
                raw = response.read().decode("utf-8", "replace")
                status = response.status
        except urllib.error.HTTPError as failure:
            raw = failure.read().decode("utf-8", "replace")
            status = failure.code

        try:
            parsed = json.loads(raw) if raw else None
        except json.JSONDecodeError:
            parsed = raw

        if expect and status not in expect:
            raise SystemExit(
                f"{method} {path} answered {status}, expected one of {expect}:\n{raw[:600]}"
            )

        return status, parsed

    def wait(self, seconds=120):
        """Blocks until the server answers its liveness probe."""
        deadline = time.monotonic() + seconds

        while time.monotonic() < deadline:
            try:
                request = urllib.request.Request(self.url + "/healthz/live")
                with self.opener.open(request, timeout=5) as response:
                    if response.status == 200:
                        return
            except Exception:
                time.sleep(1)

        raise SystemExit(f"{self.url} did not answer /healthz/live within {seconds}s.")

    def bootstrap(self, token, name, password):
        """The first-start account, using the single-use token from the log."""
        status, body = self.call(
            "POST",
            "/rest/setup",
            body={"token": token, "name": name, "password": password},
            expect=(200, 201, 400, 403, 409),
        )

        if status in (200, 201):
            print(f"  created the first administrator '{name}'", file=sys.stderr)
            return

        # Already set up is not an error when the script is idempotent.
        print(f"  setup answered {status}; assuming the account already exists",
              file=sys.stderr)

    def sign_in(self, name, password):
        _, body = self.call(
            "POST", "/rest/auth/login", body={"name": name, "password": password}
        )
        self.token = body["token"]

    # ---- catalogue ----

    def service_layers(self, address):
        """The layer list from a published service, or None if it has none."""
        status, body = self.call(
            "GET", f"/rest/services/{address}/FeatureServer?f=json", expect=None
        )

        if status != 200 or not isinstance(body, dict):
            return None

        return body.get("layers") or []

    def layers(self):
        """Published layer names.

        <b>Layers, not /admin/services.</b> That route lists the *system*
        services -- today only the geometry one -- and a published feature
        service does not appear there. Checking it found nothing, so every run
        after the first tried to create what already existed and failed with 409.
        D-39 records the same two meanings of the word "service" as a defect in
        the admin surface.
        """
        _, body = self.call("GET", "/admin/layers")
        rows = body.get("layers", []) if isinstance(body, dict) else body

        return {row.get("name") for row in rows if row.get("name")}


def define(server, name, geometry, fields, sharing="public", service=None,
           parent=None, cache=None):
    """An empty hosted layer, created from a schema rather than a file."""
    design = {
        "name": name,
        "geometryType": geometry,
        "fields": fields,
        "sharing": sharing,
    }

    if service:
        design["serviceName"] = service

    if parent is not None:
        design["parentLayerId"] = parent

    if cache is not None:
        design["cacheSeconds"] = cache

    _, body = server.call("POST", "/admin/hosted/define", body=design)

    return body


def ensure_layer(server, existing, name, geometry, fields, **rest):
    """Creates a layer if it is not already published.

    <b>Separate from loading its features, and that separation is the fix for a
    real bug.</b> The first version created a layer and loaded it inside one
    `if not exists`, so a run that failed between the two left an empty layer --
    and every later run skipped both steps, because the layer existed. The seed
    reported success and eighty conformance tests failed on a fixture with no
    rows in it.
    """
    if name in existing:
        return False

    define(server, name, geometry, fields, **rest)
    existing.add(name)

    return True


def ensure_features(server, address, layer_name, features):
    """Loads features only when the layer has none, and settles the description.

    <b>The refresh is not optional and not a sleep.</b> A layer's extent is
    derived from its rows and cached with its description, so a layer that was
    empty a second ago still reports `extent: null` after the rows land. Two edit
    tests place their feature at the centre of that extent and died reading it
    -- then passed on the next run, which is the worst way for a race to
    present. `POST /admin/layers/{name}/refresh` exists for exactly this and the
    server's own message points at it.
    """
    layer = index_of(server, address, layer_name)

    _, count = server.call(
        "GET",
        f"/rest/services/{address}/FeatureServer/{layer}"
        "/query?where=1%3D1&returnCountOnly=true&f=json",
    )

    if (count or {}).get("count", 0) > 0:
        return False

    add(server, address, layer, features)

    server.call(
        "POST",
        f"/admin/layers/{urllib.parse.quote(layer_name)}/refresh",
        expect=(200, 202, 204),
    )

    settle(server, address, layer, layer_name)

    return True


def settle(server, address, layer, layer_name, seconds=30):
    """Blocks until the layer document reports the extent its rows imply."""
    deadline = time.monotonic() + seconds

    while time.monotonic() < deadline:
        status, document = server.call(
            "GET", f"/rest/services/{address}/FeatureServer/{layer}?f=json", expect=None
        )

        if status == 200 and isinstance(document, dict):
            extent = document.get("extent")

            if isinstance(extent, dict) and extent.get("xmin") is not None:
                return

        time.sleep(0.5)

    raise SystemExit(
        f"{address}/{layer} ({layer_name}) still reports no extent {seconds}s after its "
        "features were loaded and its description was refreshed. The conformance suite "
        "places features at the centre of that extent and cannot run without it."
    )


def index_of(server, address, layer_name):
    """The layer index a name sits at inside a service.

    <b>Never assume zero.</b> Layer indices are handed out once and never reused
    -- a saved web map references them, so recycling one would silently repoint
    somebody's map at different data (D-34). Delete the only layer in a service
    and the next one is index 1. The seed learned this by answering
    "the service 'ci_buildings' has no layer 0. It has 1: 1 (ci_buildings)".
    """
    for row in server.service_layers(address) or []:
        if row.get("name") == layer_name:
            return row["id"]

    raise SystemExit(f"{address} has no layer named '{layer_name}'.")


def add(server, address, layer, features):
    """Loads features through addFeatures, and checks that they landed.

    <b>200 is not success here, and trusting it cost an afternoon.</b>
    `addFeatures` answers 200 with a per-feature results array; the first version
    of this script ignored it, so a seed that inserted nothing reported success
    and eighty conformance tests failed with things like "no tile with any
    content was found". The actual message was in the response body all along:
    the layer is 3857 and the geometry declared 4326, and this server refuses to
    reproject on write because moving geometry as a side effect of saving it is
    not something a client can detect.
    """
    _, body = server.call(
        "POST",
        f"/rest/services/{address}/FeatureServer/{layer}/addFeatures",
        form={"features": json.dumps(features), "f": "json"},
    )

    results = (body or {}).get("addResults") or []

    if len(results) != len(features):
        raise SystemExit(
            f"addFeatures on {address}/{layer} was sent {len(features)} features and "
            f"reported on {len(results)}:\n{json.dumps(body)[:600]}"
        )

    failed = [row for row in results if not row.get("success")]

    if failed:
        raise SystemExit(
            f"{len(failed)} of {len(features)} features were refused by "
            f"{address}/{layer}:\n{json.dumps(failed[0])[:600]}"
        )


# <b>Web Mercator, because a hosted layer is created in it and this server does
# not reproject on write.</b> These are metres around Ankara: x near 3.66
# million, y near 4.86 million. Sending degrees answered
# "the geometry declares spatial reference 4326 and the layer is 3857".
SRID = 3857
ORIGIN_X = 3_657_800.0
ORIGIN_Y = 4_862_900.0


def square(x, y, size, attributes):
    """One axis-aligned polygon, wound the way ArcGIS wants."""
    # <b>Clockwise, and the server is right to insist.</b> ArcGIS reads a
    # counter-clockwise first ring as a hole, and a hole before its shell is
    # nonsense -- so counter-clockwise squares were refused outright rather than
    # quietly stored inside out. Anticlockwise here cost twelve features and a
    # confusing "no tile with any content" three suites away.
    return {
        "geometry": {
            "rings": [[
                [x, y],
                [x, y + size],
                [x + size, y + size],
                [x + size, y],
                [x, y],
            ]],
            "spatialReference": {"wkid": SRID},
        },
        "attributes": attributes,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="https://127.0.0.1:8443")
    parser.add_argument("--name", default="root")
    parser.add_argument("--password", required=True)
    parser.add_argument("--setup-token", default=None)
    parser.add_argument(
        "--prefix",
        default="ci",
        help="Prepended to every created name, so a seeded developer machine "
             "keeps its own fixtures.",
    )
    # <b>Two fixtures the console suite needs and this script cannot invent.</b>
    # A connection string it may register a second time, and a raster on the
    # server's own filesystem. Both are optional: without them the fixtures they
    # feed are skipped rather than the run failing, because every other suite here
    # is happy without them.
    parser.add_argument("--datastore-connection", default=None)
    parser.add_argument("--raster", default=None)

    parser.add_argument("--github-env", default=None,
                        help="Append the variables to this file as well.")

    options = parser.parse_args()

    server = Server(options.url)

    print(f"waiting for {options.url}", file=sys.stderr)
    server.wait()

    if options.setup_token:
        server.bootstrap(options.setup_token, options.name, options.password)

    server.sign_in(options.name, options.password)
    print("  signed in", file=sys.stderr)

    prefix = options.prefix
    datastore_connection = options.datastore_connection
    raster_path = options.raster
    existing = server.layers()

    # ---- the queryable layer: polygons, several features, an integer field ----
    #
    # <b>Eight features, not three.</b> The paging tests ask for a page and then
    # the next one and check they do not overlap; with three rows a broken
    # resultOffset still looks right.
    queryable = f"{prefix}_buildings"

    ensure_layer(server, existing, queryable, "Polygon", [
        {"name": "name", "type": "Text"},
        {"name": "floors", "type": "Integer"},
        {"name": "built", "type": "Integer"},
    ])

    if ensure_features(server, f"hosted/{queryable}", queryable, [
        square(ORIGIN_X + (i * 900), ORIGIN_Y + (i * 900), 400, {
            "name": f"Block {chr(ord('A') + i)}",
            "floors": 2 + i,
            "built": 1970 + (i * 5),
        })
        for i in range(8)
    ]):
        print(f"  hosted/{queryable}: 8 polygons", file=sys.stderr)

    # ---- what the console suite needs, which is people and a second source ----
    #
    # <b>The browser suite is written against a working deployment, and CI's had one
    # administrator and one data source — 2026-08-25.</b> Nine of its tests failed on
    # that alone: a Members screen with nobody to remove offers no Remove button, a
    # group has nobody to add, and a sources table with one row has no second row to
    # click. None of that is a defect in the console; it is a console with nothing in
    # it.

    # <b>Two members, and one of them an administrator.</b> Removing the only
    # administrator is refused — correctly — so a suite that tests the removal panel
    # needs a second one to make the refusal reachable rather than universal.
    for who, role in ((f"{prefix}_second_admin", "administrator"),
                      (f"{prefix}_ordinary", "user")):
        server.call(
            "POST",
            "/admin/members",
            body={"name": who, "role": role, "userType": "unrestricted"},
            expect=(200, 201, 409),
        )

    print(f"  members: {prefix}_second_admin (administrator), {prefix}_ordinary (user)",
          file=sys.stderr)

    # <b>A second data source, so the sources table has a second row.</b> It points at
    # the same database the datastore uses, which is the honest thing for a fixture:
    # the screen under test lists registrations, and one registration of a reachable
    # database is a registration.
    if datastore_connection:
        _, made = server.call(
            "POST",
            "/admin/datasources",
            body={"name": f"{prefix}_second_source",
                  "connectionString": datastore_connection},
            expect=(200, 201, 409),
        )

        print(f"  a second data source: {prefix}_second_source", file=sys.stderr)

        # <b>And a layer on it, because the screen under test is about a source that
        # cannot be removed.</b> `DataSourceScreenTests` clicks Remove and expects the
        # console to refuse and say which layers are in the way. The datastore has no
        # Remove button at all — `console.js` omits it by name — so the only source that
        # can carry that test is a registered one with something published from it.
        #
        # Which table does not matter, so the first one the source reports with a
        # geometry column is taken. It is the same database the datastore uses, so the
        # tables are the ones seeded above; publishing one through this registration is
        # a second reference to a table, which is exactly what a registered source is.
        # <b>The id, whether it was just created or was already there.</b> A second run
        # answers 409 with an error rather than a record, so reading the id out of the
        # response worked exactly once and then silently skipped everything below it —
        # which is how the second source sat at zero layers through three seedings.
        source = (made or {}).get("id") if isinstance(made, dict) else None

        if not source:
            _, sources = server.call("GET", "/admin/datasources", expect=(200,))

            source = next(
                (row["id"] for row in (sources or {}).get("dataSources", [])
                 if row.get("name") == f"{prefix}_second_source"),
                None)

        if source:
            _, seen = server.call(
                "GET", f"/admin/datasources/{source}/capability", expect=(200,))

            # <b>An already-published table is fine, and looking for an unclaimed one
            # was the wrong instinct.</b> Every table in this database is claimed by
            # the layers seeded above, and the two free ones this workflow makes are
            # created *after* seeding — so the first version found nothing and silently
            # left the source empty. Publishing a second layer over the same table
            # through a different registration is not a workaround: it is what a
            # registered source is, and it gives this one the layer count the screen
            # under test is about.
            for table in (seen or {}).get("tables", []):
                if not table.get("geometryColumn") or not table.get("identityCandidates"):
                    continue

                server.call(
                    "POST",
                    "/admin/layers",
                    body={
                        "name": f"{prefix}_on_second",
                        "dataSourceId": source,
                        "schemaName": table["schemaName"],
                        "tableName": table["tableName"],
                        "geometryColumn": table["geometryColumn"],
                        "identityColumn": table["identityCandidates"][0],
                        "objectIdColumn": table.get("objectIdColumn"),
                        "srid": table["srid"],
                        "geometryType": table.get("geometryType") or "Polygon",

                        # <b>In `hosted` and private, and both were wrong at first.</b>
                        # Published without a folder it landed at the **root**, where
                        # `ArcGisDiscoveryTests` asserts every service is a
                        # `FeatureServer` — a published layer also answers as a
                        # `MapServer`, so the root listing gained a type that assertion
                        # forbids. And shared with the organisation it changed what a
                        # stranger counts at each scope, which
                        # `ContentScopeConformanceTests` measures exactly.
                        #
                        # A fixture that exists to give a data source a layer count has
                        # no business being visible to anybody: private, beside the
                        # others.
                        "folder": "hosted",
                        "sharing": "private",
                    },
                    expect=(200, 201, 409),
                )

                print(f"  a layer on {prefix}_second_source, so it is a source in use",
                      file=sys.stderr)
                break

    # <b>An image service, from a raster this repository already carries.</b>
    # `ServiceScopeTests` asserts that a private service says so on its own page and
    # needs an *image* service to do it, because the feature and image catalogues are
    # separate and asserting one would pass with the other broken. The corpus tif is
    # 3 kB and tracked.
    if raster_path:
        server.call(
            "POST",
            "/admin/coverages",
            body={"name": f"{prefix}_imagery", "folder": "hosted", "path": raster_path},
            expect=(200, 201, 409),
        )

        print(f"  an image service: hosted/{prefix}_imagery", file=sys.stderr)

    # ---- three fixtures the gate suites need and nothing else creates ----
    #
    # <b>Every one of these is a suite that FAILS rather than skips when its subject
    # is missing — 2026-08-25.</b> That is deliberate and correct: a conformance test
    # that goes quiet when there is nothing to check proves nothing. What it means is
    # that the fixtures have to exist, and until the first CI run reached this suite
    # nobody had found out that they did not.

    # <b>A temporal layer: exactly one Date column.</b> `Q-129` records that the time
    # dimension is derived from the schema — one `Date` column or no dimension at all —
    # so a layer with two dates would publish nothing and a layer with none is what
    # every other fixture here already is.
    temporal = f"{prefix}_observations"

    ensure_layer(server, existing, temporal, "Point", [
        {"name": "station", "type": "Text"},
        {"name": "reading", "type": "Integer"},
        {"name": "observed", "type": "Date"},
    ])

    if ensure_features(server, f"hosted/{temporal}", temporal, [
        {
            "geometry": {
                "x": ORIGIN_X + (i * 700),
                "y": ORIGIN_Y + (i * 700),
                "spatialReference": {"wkid": 3857},
            },
            "attributes": {
                "station": f"Station {i}",
                "reading": 10 + i,

                # Whole seconds and distinct, because the suite asks for an exact
                # instant and asserts the feature carrying it comes back. Epoch
                # milliseconds is what the ArcGIS surface takes.
                "observed": 1755000000000 + (i * 86400000),
            },
        }
        for i in range(6)
    ]):
        print(f"  hosted/{temporal}: 6 points with one date column", file=sys.stderr)

    # <b>A stored symbology, so `drawingInfo` is read rather than generated.</b> The
    # gate suite looks for a layer whose `drawingInfoGenerated` is **false** and
    # compares what the feature face and the map face publish for it; where the
    # appearance is generated both faces generate the same thing and the comparison
    # proves nothing.
    #
    # <b>The layer's `symbology`, not the service's `style` — and the first attempt
    # used the wrong one.</b> `PUT /admin/services/{name}/style` stores the canonical
    # MapLibre document a client fetches; `drawingInfoGenerated` is decided by
    # `FeatureServerMetadataWriter.Drawing`, which reads the **layer's** symbology.
    # The call succeeded, nothing was wrong with it, and the flag never moved.
    #
    # <b>An Esri `drawingInfo` rather than a MapLibre document</b>, because the
    # endpoint takes either and this is the shorter of the two to get exactly right —
    # the conversion is what is under test elsewhere, not here.
    server.call(
        "PUT",
        f"/admin/layers/{queryable}/symbology",
        body={
            "renderer": {
                "type": "simple",
                "symbol": {
                    "type": "esriSFS",
                    "style": "esriSFSSolid",
                    "color": [204, 187, 68, 255],
                    "outline": {
                        "type": "esriSLS",
                        "style": "esriSLSSolid",
                        "color": [68, 51, 17, 255],
                        "width": 1,
                    },
                },
            },
        },
        expect=(200, 204),
    )

    print(f"  hosted/{queryable}: a stored symbology", file=sys.stderr)

    # <b>The two free tables `AmbiguousLayerNameTests` needs are made with SQL, not
    # here — and the first attempt is worth recording.</b> Defining two layers and
    # then unpublishing them does leave their tables behind, which is true and is what
    # D-34 records. What it also leaves behind is **two empty services in the
    # directory whose layers answer 404**, because there is no way to delete a
    # service: `/admin/layers/{name}` unpublishes and nothing removes the container.
    # The conformance suite caught it immediately — *"is still in the services
    # directory"* — so the fixture was creating the defect it then tripped over.
    # That gap is [D-157](../docs/architecture-debt.md); the tables now come from
    # `tools/ci-free-tables.sql`, which touches no catalogue at all.

    # ---- a layer whose answer does not fit in one write ----
    #
    # <b>`AdmissionControlConformanceTests` needs this and CI had nothing to give
    # it — 2026-08-25.</b> That suite refuses rather than skips when
    # `GRATICULA_TEST_LARGE` is unset, and its own notes say why the queryable layer
    # will not do: eight rows answer in 2.3 kilobytes, which is **one write**, so a
    # test about a response being cut in half would pass identically under the
    # behaviour it exists to rule out.
    #
    # <b>Six hundred squares with three text fields, which is a few hundred
    # kilobytes.</b> Enough to span several writes and still cheap to seed — this
    # step already takes seconds and the point is a body, not a benchmark.
    large = f"{prefix}_many"

    ensure_layer(server, existing, large, "Polygon", [
        {"name": "name", "type": "Text"},
        {"name": "note", "type": "Text"},
        {"name": "kind", "type": "Text"},
    ])

    if ensure_features(server, f"hosted/{large}", large, [
        square(ORIGIN_X + ((i % 25) * 300), ORIGIN_Y + ((i // 25) * 300), 120, {
            "name": f"Parcel {i:04d}",

            # Padding with a purpose: the body has to be large enough that the
            # server writes it in more than one go, and short attributes would need
            # far more rows to get there.
            "note": f"seeded for admission control, row {i:04d}, " + ("x" * 120),
            "kind": "residential" if i % 3 else "commercial",
        })
        for i in range(600)
    ]):
        print(f"  hosted/{large}: 600 polygons, for the admission-control body",
              file=sys.stderr)

    # <b>A classified symbology, because nothing else here has one.</b> The other
    # stored style is a `simple` renderer, so every legend this suite could ask for
    # drew one swatch and Q-131's answer -- a row per class -- had no end-to-end case
    # at all. `kind` is already two values on this layer, which is what a unique-value
    # renderer wants and what makes the legend three rows: the two named classes and
    # the fallback.
    server.call(
        "PUT",
        f"/admin/layers/{large}/symbology",
        body={
            "renderer": {
                "type": "uniqueValue",
                "field1": "kind",
                "defaultSymbol": {
                    "type": "esriSFS",
                    "style": "esriSFSSolid",
                    "color": [204, 204, 204, 255],
                },
                "uniqueValueInfos": [
                    {
                        "value": "residential",
                        "label": "Residential",
                        "symbol": {
                            "type": "esriSFS",
                            "style": "esriSFSSolid",
                            "color": [230, 120, 60, 255],
                        },
                    },
                    {
                        "value": "commercial",
                        "label": "Commercial",
                        "symbol": {
                            "type": "esriSFS",
                            "style": "esriSFSSolid",
                            "color": [60, 120, 230, 255],
                        },
                    },
                ],
            },
        },
        expect=(200, 204),
    )

    print(f"  hosted/{large}: a symbology that classifies on `kind`", file=sys.stderr)

    # ---- the tile service: a layer with a cache lifetime ----
    tiles = f"{prefix}_parcels"

    ensure_layer(server, existing, tiles, "Polygon", [
        {"name": "parcel", "type": "Text"},
        {"name": "area_m2", "type": "Integer"},
    ], cache=60)

    if ensure_features(server, f"hosted/{tiles}", tiles, [
        square(ORIGIN_X + (i * 220), ORIGIN_Y + (i * 220), 150, {
            "parcel": f"P-{1000 + i}",
            "area_m2": 400 + (i * 25),
        })
        for i in range(12)
    ]):
        print(f"  hosted/{tiles}: 12 polygons, 60s cache", file=sys.stderr)

    # ---- the editable layer ----
    #
    # <b>It starts with three points, and leaving it empty was wrong.</b> The
    # edit tests add, update and delete their own rows -- so seeding looked like
    # a way to let a failed cleanup masquerade as a pass. But they place their
    # feature at the centre of the layer's own extent, and a layer with no rows
    # has no extent: the document reports null and the tests died reading it.
    # Three rows well away from where the tests write is the smaller compromise,
    # and it is the shape a real editable layer has anyway.
    editable = f"{prefix}_editable"

    ensure_layer(server, existing, editable, "Point", [
        {"name": "label", "type": "Text"},
        {"name": "reading", "type": "Integer"},
    ])

    # <b>The three points spread in both directions, and they used to share a y.</b> With one
    # latitude between them the layer's extent had zero height, so `ThumbnailFramingTests` — which
    # asks that a picture be filled by the features it draws — measured 96 % of the width and 4 %
    # of the height and failed on every CI run from the day it was written. It passed on
    # developers' machines only because a write test had left a stray feature behind, which gave
    # the layer a second dimension by accident.
    #
    # <b>300 by 200 is the canvas's own 3:2.</b> Three points on that diagonal fill a 336x224
    # thumbnail in both axes, which is the property the test exists to check; a collinear layer
    # cannot satisfy it however the frame is computed, because the ink is one row of markers.
    if ensure_features(server, f"hosted/{editable}", editable, [
        {
            "geometry": {"x": ORIGIN_X + (i * 300), "y": ORIGIN_Y - 3_000 + (i * 200),
                         "spatialReference": {"wkid": SRID}},
            "attributes": {"label": f"Station {i}", "reading": 10 + i},
        }
        for i in range(3)
    ]):
        print(f"  hosted/{editable}: three points, for an extent", file=sys.stderr)

    # ---- the multi-layer service, with a group inside it ----
    #
    # <b>Three layers of different geometry types.</b> One test asserts that each
    # layer reports its own type and its own fields, which passes trivially if
    # they are all the same.
    multi = f"{prefix}_EarlyAlert"
    address = f"hosted/{multi}"

    # <b>Step by step rather than all or nothing.</b> The first version created
    # the whole service inside one `if`, so a failure half way through left a
    # service with two layers and no group -- and every later run skipped the
    # block entirely, because the thing it checked for existed. Each piece now
    # checks for itself.
    ensure_layer(server, existing, f"{multi}_sites", "Point", [
        {"name": "site", "type": "Text"},
        {"name": "severity", "type": "Integer"},
    ], service=multi)

    if ensure_features(server, address, f"{multi}_sites", [
        {
            "geometry": {"x": ORIGIN_X + (i * 900), "y": ORIGIN_Y + 4_000,
                         "spatialReference": {"wkid": SRID}},
            "attributes": {"site": f"Site {i}", "severity": i % 4},
        }
        for i in range(6)
    ]):
        print(f"  {address}: six points on the sites layer", file=sys.stderr)

    ensure_layer(server, existing, f"{multi}_routes", "LineString", [
        {"name": "route", "type": "Text"},
    ], service=multi)

    if ensure_features(server, address, f"{multi}_routes", [
        {
            "geometry": {
                "paths": [[
                    [ORIGIN_X + (i * 700), ORIGIN_Y + 6_000],
                    [ORIGIN_X + (i * 700) + 500, ORIGIN_Y + 6_400],
                    [ORIGIN_X + (i * 700) + 900, ORIGIN_Y + 6_100],
                ]],
                "spatialReference": {"wkid": SRID},
            },
            "attributes": {"route": f"Route {i}"},
        }
        for i in range(4)
    ]):
        print(f"  {address}: four routes", file=sys.stderr)

    # <b>The group's layer id comes from the service document, not from the
    # response that created it.</b> That response carries a GUID; parentLayerId
    # is the integer index within the service, and sending the GUID answered 400
    # -- which is how the group ended up empty on the first attempt.
    published = server.service_layers(address) or []

    group = next(
        (row for row in published
         if row.get("name") == "Reports" and row.get("type") == "Group Layer"),
        None,
    )

    if group is None:
        server.call("POST", f"/admin/services/{multi}/groups",
                    body={"name": "Reports", "folder": "hosted"})

        published = server.service_layers(address) or []

        group = next(
            (row for row in published
             if row.get("name") == "Reports" and row.get("type") == "Group Layer"),
            None,
        )

        if group is None:
            raise SystemExit(
                f"the group layer was created in {address} but does not appear in its "
                "service document."
            )

        print(f"  {address}: group 'Reports' at layer {group['id']}", file=sys.stderr)

    ensure_layer(server, existing, f"{multi}_reports", "Polygon", [
        {"name": "report", "type": "Text"},
        {"name": "raised", "type": "Integer"},
    ], service=multi, parent=group["id"])

    if ensure_features(server, address, f"{multi}_reports", [
        square(ORIGIN_X + (i * 600), ORIGIN_Y + 8_000, 300, {
            "report": f"Report {i}",
            "raised": 2020 + i,
        })
        for i in range(5)
    ]):
        print(f"  {address}: a polygon layer under the group, five features",
              file=sys.stderr)

    # ---- the geometry service is shared with the organisation ----
    #
    # Several tests assert that an anonymous client gets 404 from it and a
    # signed-in one does not. A private geometry service makes both halves fail.
    server.call(
        "PUT",
        "/admin/services/Geometry/sharing",
        body={"sharing": "organization"},
        expect=(200, 204, 404),
    )

    variables = {
        # <b>`GRATICULA_`, and it was `GISSERVER_` until 2026-08-25.</b>
        # [ADR-032](../docs/adr/ADR-032-the-product-is-named-graticula.md) renamed the
        # product on 2026-08-17 and this file was missed, so this script announced five
        # variables under the old name and the suite read five under the new one. Nothing
        # errored: the tests simply learned nothing about what had been seeded and
        # refused with *"no published layer has features to identify"*, which reads like
        # a seeding failure and was a naming one.
        #
        # Found by the first CI run this repository ever completed. Locally the variables
        # are set by hand, so the mismatch had no way to show.
        "GRATICULA_TEST_QUERYABLE": f"hosted/{queryable}",
        "GRATICULA_TEST_TILE_SERVICE": f"hosted/{tiles}",
        "GRATICULA_TEST_EDITABLE": f"hosted/{editable}",
        "GRATICULA_TEST_MULTILAYER": f"hosted/{multi}",
        "GRATICULA_TEST_GROUPED": f"hosted/{multi}",
        "GRATICULA_TEST_LARGE": f"hosted/{large}",
        "GRATICULA_TEST_TEMPORAL": f"hosted/{temporal}",
    }

    for key, value in variables.items():
        print(f"{key}={value}")

    if options.github_env:
        with open(options.github_env, "a", encoding="utf-8") as handle:
            for key, value in variables.items():
                handle.write(f"{key}={value}\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
