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
        D-28 records the same two meanings of the word "service" as a defect in
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

    if ensure_features(server, f"hosted/{editable}", editable, [
        {
            "geometry": {"x": ORIGIN_X + (i * 300), "y": ORIGIN_Y - 3_000,
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
        "GISSERVER_TEST_QUERYABLE": f"hosted/{queryable}",
        "GISSERVER_TEST_TILE_SERVICE": f"hosted/{tiles}",
        "GISSERVER_TEST_EDITABLE": f"hosted/{editable}",
        "GISSERVER_TEST_MULTILAYER": f"hosted/{multi}",
        "GISSERVER_TEST_GROUPED": f"hosted/{multi}",
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
