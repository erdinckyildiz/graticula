#!/usr/bin/env python3
"""ADR-059 condition 1: does quiescing actually let the DBA's DDL through?

**The feature refuses requests. That is not the same as freeing the lock**, and
the difference is the whole of this condition. ADR-059 §5c was written claiming
the pool close frees it and that turned out to be wrong — an idle pooled
connection does not block `ALTER TABLE` at all — so what remains to be shown is
the thing the refusal is supposed to do: with new queries refused, the running
ones drain and the DDL takes its lock.

Three timings, in one run against one table:

1. **Idle.** `ALTER TABLE` with nothing happening, for a floor.
2. **Under load, not quiesced.** Concurrent readers against the layer while the
   DDL is issued. This is the case D-08 measured from the other side.
3. **Under load, quiesced.** The same load offered, the source held, the DDL
   issued.

`lock_timeout` bounds each attempt so a blocked one fails rather than hanging,
and the failure is a result rather than an error: *it did not get the lock* is
what case 2 is expected to say.

Usage:
    python benchmarks/quiesce/ddl-under-load.py --url https://127.0.0.1:8451 \\
        --user ci --password '...' --layer hosted/ci_buildings \\
        --pg 'Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis' \\
        --container gis-experiment-postgis --table hosted.ci_buildings_xxxx
"""

import argparse
import json
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.request

TIMEOUT = 120


class Server:
    """The admin and REST surfaces, over one session."""

    def __init__(self, url):
        self.url = url.rstrip("/")
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPSHandler(context=ssl._create_unverified_context())
        )
        self.token = None

    def call(self, method, path, body=None):
        data = json.dumps(body).encode() if body is not None else None
        headers = {"Content-Type": "application/json"} if data else {}

        if self.token:
            headers["Authorization"] = "Bearer " + self.token

        request = urllib.request.Request(
            self.url + path, data=data, headers=headers, method=method
        )

        try:
            with self.opener.open(request, timeout=TIMEOUT) as answer:
                return answer.status, answer.read().decode()
        except urllib.error.HTTPError as refused:
            return refused.code, refused.read().decode()

    def sign_in(self, name, password):
        status, body = self.call(
            "POST", "/rest/auth/login", {"name": name, "password": password}
        )

        if status != 200:
            raise SystemExit(f"sign-in answered {status}: {body[:300]}")

        self.token = json.loads(body)["token"]


def alter(container, table, column, seconds=10):
    """One ALTER, timed, with a lock timeout so a blocked one reports rather than hangs."""
    began = time.perf_counter()

    done = subprocess.run(
        ["docker", "exec", "-i", container, "psql", "-U", "gis", "-d", "gis", "-c",
         f"set lock_timeout='{seconds}s'; "
         f"alter table {table} add column if not exists {column} text;"],
        capture_output=True, text=True, check=False)

    return {
        "seconds": round(time.perf_counter() - began, 3),
        "got_the_lock": done.returncode == 0,
        "said": (done.stdout + done.stderr).strip().splitlines()[-1][:120],
    }


class Load:
    """Readers against one layer, until told to stop."""

    def __init__(self, server, layer, threads):
        self.server = server
        self.layer = layer
        self.threads = threads
        self.stop = threading.Event()
        self.answers = {"ok": 0, "refused": 0}
        self._lock = threading.Lock()
        self._workers = []

    def _read(self):
        path = (f"/rest/services/{self.layer}/FeatureServer/0/query"
                "?where=1%3D1&outFields=*&resultRecordCount=1000&f=json")

        while not self.stop.is_set():
            status, _ = self.server.call("GET", path)

            with self._lock:
                self.answers["ok" if status == 200 else "refused"] += 1

    def __enter__(self):
        for _ in range(self.threads):
            worker = threading.Thread(target=self._read, daemon=True)
            worker.start()
            self._workers.append(worker)

        # Long enough for every thread to have a query in flight.
        time.sleep(2)
        return self

    def __exit__(self, *_):
        self.stop.set()

        for worker in self._workers:
            worker.join(timeout=5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--url", required=True)
    parser.add_argument("--user", required=True)
    parser.add_argument("--password", required=True)
    parser.add_argument("--layer", required=True, help="folder/service of a layer to load")
    parser.add_argument("--container", default="gis-experiment-postgis")
    parser.add_argument("--table", required=True, help="schema.table the layer reads")
    parser.add_argument("--source", required=True, help="the data source id to quiesce")
    parser.add_argument("--threads", type=int, default=8)
    parser.add_argument("--out", default="benchmarks/quiesce/measured.json")

    options = parser.parse_args()

    server = Server(options.url)
    server.sign_in(options.user, options.password)

    measured = {"at": time.strftime("%Y-%m-%dT%H:%M:%S"), "threads": options.threads}

    measured["idle"] = alter(options.container, options.table, "zzz_q_idle")
    print("idle:", measured["idle"])

    with Load(server, options.layer, options.threads) as load:
        measured["under_load"] = alter(options.container, options.table, "zzz_q_load")
        measured["under_load"]["requests"] = dict(load.answers)

    print("under load:", measured["under_load"])

    with Load(server, options.layer, options.threads) as load:
        status, said = server.call(
            "POST", f"/admin/datasources/{options.source}/quiesce",
            {"seconds": 60, "why": "a quiesce benchmark"})

        if status != 200:
            raise SystemExit(f"quiesce answered {status}: {said[:300]}")

        # <b>A moment for the requests already running to finish.</b> Quiesce refuses new ones;
        # it does not kill a read that is already streaming, which ADR-059 §4 states as a cost.
        time.sleep(3)

        measured["quiesced"] = alter(options.container, options.table, "zzz_q_held")
        measured["quiesced"]["requests"] = dict(load.answers)

        server.call("DELETE", f"/admin/datasources/{options.source}/quiesce")

    print("quiesced:", measured["quiesced"])

    subprocess.run(
        ["docker", "exec", "-i", options.container, "psql", "-U", "gis", "-d", "gis", "-c",
         f"alter table {options.table} "
         "drop column if exists zzz_q_idle, "
         "drop column if exists zzz_q_load, "
         "drop column if exists zzz_q_held;"],
        capture_output=True, text=True, check=False)

    with open(options.out, "w", encoding="utf-8") as out:
        json.dump(measured, out, indent=2)
        out.write("\n")

    print(json.dumps(measured, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
