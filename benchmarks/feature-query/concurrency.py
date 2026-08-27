"""D-30's unpaid half: the query decomposition under concurrency.

The path was decomposed on 2026-08-16 and re-measured 2026-08-26, both serially, and the
row's trigger says the concurrency half is still owed. This drives the same one-row query
at several concurrencies with the query logger at Debug, and reads the server's own
per-request breakdown out of its log rather than timing from outside — so what is reported
is *which component grows*, which is the question, rather than *how much slower it got*,
which a wall clock already answers.

Usage:  python benchmarks/feature-query/concurrency.py <server> <layer-url> <log-file>
"""

import concurrent.futures as futures
import io
import json
import os
import re
import ssl
import statistics
import sys
import time
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")

CONTEXT = ssl._create_unverified_context()

# The shape Log.QueryTimings writes. Microseconds throughout.
LINE = re.compile(
    r"query (?P<layer>\S+): (?P<total>\d+)us total = (?P<lookup>\d+) lookup "
    r"\+ (?P<prepare>\d+) prepare \+ (?P<driver>\d+) driver \+ (?P<decode>\d+) decode "
    r"\+ (?P<serialise>\d+) serialise, (?P<rows>\d+) rows")

PARTS = ("total", "lookup", "prepare", "driver", "decode", "serialise")


def token(server, user, password):
    request = urllib.request.Request(
        server + "/rest/auth/login",
        data=json.dumps({"name": user, "password": password}).encode(),
        headers={"Content-Type": "application/json"})

    with urllib.request.urlopen(request, context=CONTEXT, timeout=30) as answer:
        return json.loads(answer.read())["token"]


def get(server, path, bearer):
    """One request, timed. Returns the milliseconds and what went wrong, if anything.

    <b>The failure is returned rather than swallowed, and the first version swallowed it.</b>
    That version reported a median of 0.3 ms at every concurrency and no traced requests, and
    the obvious reading -- *the query logger is not at Debug* -- was wrong: every request was
    failing instantly and the harness was timing the failure. A benchmark that cannot say
    *nothing happened* reports a very fast nothing.
    """
    request = urllib.request.Request(server + path, headers={"Authorization": "Bearer " + bearer})
    started = time.perf_counter()

    try:
        with urllib.request.urlopen(request, context=CONTEXT, timeout=120) as answer:
            answer.read()
        return (time.perf_counter() - started) * 1000, None
    except Exception as problem:                                      # noqa: BLE001
        return (time.perf_counter() - started) * 1000, f"{type(problem).__name__}: {problem}"


def tail(path):
    """Where the log ends now, so a round reads only its own lines."""
    return os.path.getsize(path)


def since(path, mark):
    with io.open(path, encoding="utf-8", errors="replace") as handle:
        handle.seek(mark)
        return [m.groupdict() for m in (LINE.search(l) for l in handle) if m]


def main(argv):
    if len(argv) < 3:
        print(__doc__)
        return 2

    server, layer, log = argv[0], argv[1], argv[2]
    user = os.environ.get("GRATICULA_TEST_USER", "ci")
    password = os.environ["GRATICULA_TEST_PASSWORD"]

    bearer = token(server, user, password)
    path = f"{layer}/query?where=1%3D1&resultRecordCount=1&outFields=*&f=json"

    _, problem = get(server, path, bearer)

    if problem:
        print(f"the first request failed, so nothing below would mean anything: {problem}")
        return 1

    print(f"{'callers':>8} {'requests':>9} {'wall p50':>9} "
          + " ".join(f"{p:>10}" for p in PARTS) + "   traced")

    for callers in (1, 4, 8, 16, 32):
        get(server, path, bearer)                       # warm the context and the pool

        mark = tail(log)

        with futures.ThreadPoolExecutor(max_workers=callers) as pool:
            answers = list(pool.map(lambda _: get(server, path, bearer), range(callers * 20)))

        broke = [problem for _, problem in answers if problem]

        if broke:
            print(f"{callers:>8} {len(answers):>9}   {len(broke)} of them failed: {broke[0][:70]}")
            continue

        wall = sorted(elapsed for elapsed, _ in answers)

        time.sleep(1.5)                                 # the log is written asynchronously

        traced = since(log, mark)

        if not traced:
            print(f"{callers:>8} {len(wall):>9} {statistics.median(wall):>8.1f}ms "
                  "  -- no traced request; is the query logger at Debug?")
            continue

        medians = {
            part: statistics.median([int(t[part]) for t in traced]) / 1000.0
            for part in PARTS
        }

        print(f"{callers:>8} {len(wall):>9} {statistics.median(wall):>8.1f}ms "
              + " ".join(f"{medians[p]:>9.2f}ms" for p in PARTS)
              + f"   {len(traced)}")

    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
