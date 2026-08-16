#!/usr/bin/env python3
"""Where a feature query spends its time, measured from inside the process.

D-30, and finding F1 of the §66 performance gate: runs 1-4 measured tiles and
overlay, and the word "query" appears in neither results document. A black-box
probe was run for that gate and found throughput plateauing at 5-7x for 24x the
concurrency -- and said so from outside, where an allocation ceiling, a pool
limit and a contended host are indistinguishable. The gate's conclusion was that
this needs in-process instrumentation. This is that.

**The numbers come from the server, not from this script.** The server logs one
line per query decomposing it into lookup, prepare, driver, decode and
serialise; this drives the load, reads those lines back and aggregates them.
What the client times is only used for throughput, and only after the control
run below.

**The control run is not optional.** F4: a harness in this project has been
wrong three times, and the third looked entirely plausible -- the first version
of the black-box probe reported a regression that was its own client's 66 MB/s
ceiling. So every load figure here is taken beside a control against a path the
server barely touches, at the same concurrency and payload size. If the control
plateaus too, the plateau is this script.

Usage:
    # start the server with:  Logging__LogLevel__query=Debug
    python benchmarks/feature-query/run.py --layer buildings --log trace.log
"""

import argparse
import http.client
import io
import json
import re
import ssl
import statistics
import sys
import threading
import time
import urllib.parse

TIMING = re.compile(
    r"query (?P<layer>\S+): (?P<total>\d+)us total = (?P<lookup>\d+) lookup "
    r"\+ (?P<prepare>\d+) prepare \+ (?P<driver>\d+) driver \+ (?P<decode>\d+) decode "
    r"\+ (?P<serialise>\d+) serialise, (?P<rows>\d+) rows, (?P<vertices>\d+) vertices, "
    r"(?P<bytes>\d+) bytes out"
)

PHASES = ("lookup", "prepare", "driver", "decode", "serialise")


def connect(host, port):
    return http.client.HTTPSConnection(
        host, port, context=ssl._create_unverified_context(), timeout=60)


def fetch(connection, path):
    connection.request("GET", path)
    response = connection.getresponse()
    body = response.read()

    if response.status != 200:
        raise SystemExit(f"{path} answered {response.status}: {body[:200]!r}")

    return len(body)


def sequential(host, port, path, repeats):
    """Latency at concurrency one, which is where the phases are attributable."""
    connection = connect(host, port)
    latencies = []

    for _ in range(repeats):
        started = time.perf_counter()
        fetch(connection, path)
        latencies.append((time.perf_counter() - started) * 1000)

    connection.close()

    return latencies


def concurrent(host, port, path, workers, seconds):
    """Throughput and bytes, for the scaling question."""
    stop = threading.Event()
    counts = [0] * workers
    volume = [0] * workers

    def run(index):
        connection = connect(host, port)

        while not stop.is_set():
            volume[index] += fetch(connection, path)
            counts[index] += 1

        connection.close()

    threads = [threading.Thread(target=run, args=(i,), daemon=True) for i in range(workers)]

    started = time.perf_counter()

    for thread in threads:
        thread.start()

    time.sleep(seconds)
    stop.set()

    for thread in threads:
        thread.join(timeout=30)

    elapsed = time.perf_counter() - started

    return sum(counts) / elapsed, sum(volume) / elapsed / 1e6


def read_timings(path):
    """Every timing line in the log, bucketed by row count.

    **Bucketed rather than offset-marked, and the first version was wrong.** It
    noted the log's size before each case and read forward from there -- but the
    server's stdout is block-buffered when redirected to a file, so lines arrive
    late and in batches. The one-feature case was reported with 1,000 rows and
    410 kB, which are the previous case's numbers. Row count is a reliable
    discriminator here because the cases were chosen to differ in it, and taking
    the last N of each bucket drops the warm-up.
    """
    buckets = {}

    with io.open(path, encoding="utf-8", errors="replace") as handle:
        for line in handle:
            match = TIMING.search(line)

            if not match:
                continue

            row = {k: int(v) for k, v in match.groupdict().items() if k != "layer"}
            buckets.setdefault(row["rows"], []).append(row)

    return buckets


def summarise(rows):
    if not rows:
        return None

    out = {"n": len(rows), "rows": rows[0]["rows"], "vertices": rows[0]["vertices"],
           "bytes": rows[0]["bytes"]}

    for key in ("total",) + PHASES:
        values = sorted(r[key] for r in rows)
        out[key] = {
            "p50": values[len(values) // 2],
            "p95": values[min(len(values) - 1, int(len(values) * 0.95))],
        }

    return out


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8443)
    parser.add_argument("--layer", default="buildings",
                        help="A published service whose layer 0 has enough rows.")
    parser.add_argument("--log", default="trace.log",
                        help="The server's stdout, with Logging__LogLevel__query=Debug.")
    parser.add_argument("--repeats", type=int, default=40)
    parser.add_argument("--seconds", type=float, default=4.0)
    parser.add_argument("--concurrency", default="1,2,4,8,16,24")

    options = parser.parse_args()

    base = f"/rest/services/{options.layer}/FeatureServer/0/query"

    cases = {
        "1 feature": f"{base}?where=1%3D1&resultRecordCount=1&outFields=*&f=json",
        "100 features": f"{base}?where=1%3D1&resultRecordCount=100&outFields=*&f=json",
        "1000 features": f"{base}?where=1%3D1&resultRecordCount=1000&outFields=*&f=json",
        "count only": f"{base}?where=1%3D1&returnCountOnly=true&f=json",
    }

    # <b>A path the server barely touches, at a comparable payload.</b> F4's
    # rule: if this plateaus with the others, the plateau is the client.
    control = "/rest/info?f=json"

    results = {"phases": {}, "throughput": {}, "control": {}}

    print("warming up", file=sys.stderr)

    for path in cases.values():
        sequential(options.host, options.port, path, 8)

    expected = {"1 feature": 1, "100 features": 100, "1000 features": 1000}
    client = {}

    for name, path in cases.items():
        print(f"phases: {name}", file=sys.stderr)
        client[name] = sequential(options.host, options.port, path, options.repeats)

    # <b>Read once, after everything, and bucket by row count.</b> The server's
    # stdout is block-buffered when redirected, so anything that marks an offset
    # before a case reads the case before it.
    time.sleep(2.0)
    buckets = read_timings(options.log)

    for name, latencies in client.items():
        rows = buckets.get(expected.get(name), [])[-options.repeats:]

        results["phases"][name] = {
            "client_p50_ms": round(statistics.median(latencies), 3),
            "server": summarise(rows),
        }

    # <b>count only is not instrumented, and saying so is the point.</b> It
    # takes AlternateShapeAsync, which never reaches the traced block, so the
    # server logs nothing for it. Reporting a blank rather than omitting the row
    # keeps the gap visible.
    results["phases"]["count only"] = {
        "client_p50_ms": round(
            statistics.median(
                sequential(options.host, options.port, cases["count only"], options.repeats)), 3),
        "server": None,
        "note": "not instrumented: returnCountOnly takes a different path",
    }

    cases.pop("count only")

    for name, path in list(cases.items()) + [("control /rest/info", control)]:
        target = results["control"] if name.startswith("control") else results["throughput"]
        target[name] = {}

        for workers in [int(c) for c in options.concurrency.split(",")]:
            rate, mbs = concurrent(options.host, options.port, path, workers, options.seconds)
            target[name][workers] = {"rps": round(rate, 1), "mb_s": round(mbs, 1)}
            print(f"  {name} @{workers}: {rate:.0f} rps, {mbs:.1f} MB/s", file=sys.stderr)

    print(json.dumps(results, indent=1))

    return 0


if __name__ == "__main__":
    sys.exit(main())
