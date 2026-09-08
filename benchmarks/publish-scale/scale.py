#!/usr/bin/env python3
"""ADR-057 conditions 1 and 3: the name check on a full folder, and a large composition.

Two questions the decision left open with the same shape -- *this is fine on
three of them, and nobody has looked at a thousand*:

**Condition 1.** 5e asks the server whether a name is free while somebody types.
Whether that is one indexed query or a listing walked in the browser decides
whether the screen is usable on a full server. The implementation is one query
(`FindServiceAtAsync`); this measures it against a folder holding a thousand
services, and against the same folder holding three, so the difference between
an index lookup and a scan would be visible if there were one.

**Condition 3.** 5h writes a service, its groups and its layers in one
transaction. Every composition anybody has published held three things. A
service assembled from a whole database is the case where one long transaction
stops being free.

Usage:
    python benchmarks/publish-scale/scale.py --url https://127.0.0.1:8451 \\
        --user ci --password '...' --source <data source id> \\
        --schema hosted --table ci_many_009223eb --geometry geom \\
        --identity objectid --srid 3857 [--services 1000] [--layers 1000]

It creates what it measures and removes it again. Nothing here is a fixture
anybody else depends on.
"""

import argparse
import json
import ssl
import statistics
import sys
import time
import urllib.error
import urllib.request

TIMEOUT = 600


class Server:
    """The admin surface, over one session."""

    def __init__(self, url):
        self.url = url.rstrip("/")
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPSHandler(context=ssl._create_unverified_context())
        )
        self.token = None

    def call(self, method, path, body=None, expect=(200, 201)):
        """One request. Returns (status, parsed-or-text, seconds)."""
        data = json.dumps(body).encode() if body is not None else None
        headers = {"Content-Type": "application/json"} if data else {}

        if self.token:
            headers["Authorization"] = "Bearer " + self.token

        request = urllib.request.Request(
            self.url + path, data=data, headers=headers, method=method
        )

        began = time.perf_counter()

        try:
            with self.opener.open(request, timeout=TIMEOUT) as answer:
                text = answer.read().decode()
                status = answer.status
        except urllib.error.HTTPError as refused:
            text = refused.read().decode()
            status = refused.code

        took = time.perf_counter() - began

        try:
            parsed = json.loads(text) if text else None
        except json.JSONDecodeError:
            parsed = text

        if expect and status not in expect:
            raise SystemExit(f"{method} {path} answered {status}: {text[:400]}")

        return status, parsed, took

    def sign_in(self, name, password):
        _, body, _ = self.call(
            "POST", "/rest/auth/login", {"name": name, "password": password}
        )

        self.token = body["token"]


def layer(name, options, at=None):
    """One composition entry naming the table this run was pointed at.

    <b>The same table in every service, and that is legal since migration 40.</b>
    `layer_table_unique_in_service` is per service, so a thousand services may
    each serve it. Within one composition it may appear once, which is why the
    thousand-layer half needs a thousand tables rather than one repeated.
    """
    return {
        "name": name,
        "dataSourceId": options.source,
        "schemaName": at[0] if at else options.schema,
        "tableName": at[1] if at else options.table,
        "geometryColumn": options.geometry,
        "identityColumn": options.identity,
        "srid": options.srid,
        "geometryType": "MultiPolygon",
    }


def percentiles(times):
    """The shape of a set of timings, in milliseconds."""
    ms = sorted(t * 1000 for t in times)

    return {
        "n": len(ms),
        "min": round(ms[0], 3),
        "median": round(statistics.median(ms), 3),
        "p95": round(ms[int(len(ms) * 0.95) - 1], 3),
        "max": round(ms[-1], 3),
    }


def name_check(server, folder, tries):
    """How long the check takes for a free name and for a taken one."""
    free = []
    taken = []

    for i in range(tries):
        _, _, took = server.call(
            "GET", f"/admin/publish/name?name=ZZZNoSuchName{i}&folder={folder}"
        )

        free.append(took)

    for i in range(tries):
        _, _, took = server.call(
            "GET", f"/admin/publish/name?name=zzzscale{i}&folder={folder}"
        )

        taken.append(took)

    return {"free": percentiles(free), "taken": percentiles(taken)}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--user", required=True)
    parser.add_argument("--password", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--schema", required=True)
    parser.add_argument("--table", required=True)
    parser.add_argument("--geometry", default="geom")
    parser.add_argument("--identity", default="objectid")
    parser.add_argument("--srid", type=int, default=3857)
    parser.add_argument("--folder", default="zzzscale")
    parser.add_argument("--services", type=int, default=1000)
    parser.add_argument("--layers", type=int, default=1000)
    parser.add_argument("--tries", type=int, default=40)
    parser.add_argument(
        "--tables",
        default="",
        help="A file of schema.table lines for the composition half. Without it "
             "that half is skipped, because one table cannot appear in one "
             "service twice.",
    )
    parser.add_argument("--out", default="benchmarks/publish-scale/measured.json")

    options = parser.parse_args()

    server = Server(options.url)
    server.sign_in(options.user, options.password)

    measured = {"url": options.url, "at": time.strftime("%Y-%m-%dT%H:%M:%S")}

    # <b>Before, so the comparison is against this same server rather than
    # against a number from another machine.</b> An empty folder and a full one
    # differ in one thing; anything else measured elsewhere differs in many.
    measured["empty_folder"] = name_check(server, options.folder, options.tries)

    print(f"empty folder: {json.dumps(measured['empty_folder'])}")

    made = []

    began = time.perf_counter()

    for i in range(options.services):
        _, _, _ = server.call(
            "POST",
            "/admin/publish",
            {
                "name": f"zzzscale{i}",
                "folder": options.folder,
                "sharing": "private",
                "nodes": [{"layer": layer(f"only{i}", options)}],
            },
            expect=(200, 201),
        )

        made.append(f"zzzscale{i}")

        if (i + 1) % 100 == 0:
            print(f"  {i + 1} services in {time.perf_counter() - began:.1f}s")

    measured["services"] = {
        "count": len(made),
        "seconds": round(time.perf_counter() - began, 2),
    }

    try:
        measured["full_folder"] = name_check(server, options.folder, options.tries)
        print(f"full folder: {json.dumps(measured['full_folder'])}")

        if options.tables:
            with open(options.tables, encoding="utf-8") as lines:
                tables = [
                    line.strip().split(".", 1)
                    for line in lines
                    if line.strip()
                ][: options.layers]

            nodes = [
                {"layer": layer(f"wide{i}", options, at)}
                for i, at in enumerate(tables)
            ]

            status, body, took = server.call(
                "POST",
                "/admin/publish",
                {
                    "name": "zzzwide",
                    "folder": options.folder,
                    "sharing": "private",
                    "nodes": nodes,
                },
                expect=(200, 201),
            )

            measured["composition"] = {
                "layers": len(nodes),
                "seconds": round(took, 3),
                "status": status,
            }

            print(f"composition of {len(nodes)}: {took:.2f}s")

            server.call(
                "DELETE",
                f"/admin/featureservices/zzzwide?folder={options.folder}&drop=false",
                expect=(200, 204, 404, 409),
            )
    finally:
        # <b>Removed even when the measurement failed.</b> A thousand services
        # left in a fixture is a fixture nobody can read afterwards.
        began = time.perf_counter()

        for name in made:
            server.call(
                "DELETE",
                f"/admin/featureservices/{name}?folder={options.folder}&drop=true",
                expect=(200, 204, 404, 409),
            )

        measured["torn_down_seconds"] = round(time.perf_counter() - began, 2)

    with open(options.out, "w", encoding="utf-8") as out:
        json.dump(measured, out, indent=2)
        out.write("\n")

    print(json.dumps(measured, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
