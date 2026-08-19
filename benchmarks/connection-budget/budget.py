"""Counts the connections a running server actually holds, to answer Q-04.

[Q-04](../../docs/open-questions.md) is one of the five questions carried out of
Phase 0 as blocking: *what is the concrete DB connection budget at 1,000 services,
per provider?* [ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8 gives the
formula and the policy —

    nodes × workers × data sources × pool size = potential connections

— and says **data sources, not services**, which is what makes the arithmetic
survivable. What has never existed is the numbers: the pool size constant, whether
the per-source claim holds in the code as built, and whether a pool gives its
connections back.

**This counts rather than times**, which is deliberate: a latency benchmark on a
loaded machine is how this repository produced five wrong harnesses, and a
connection census is immune to that — a backend is either open or it is not.

    python benchmarks/connection-budget/budget.py

Needs the dev server on https://127.0.0.1:8443 and the experiment PostGIS on
localhost:55432 with `max_connections` well above the concurrency below. Reads only:
every request is a `returnCountOnly` query.
"""

import concurrent.futures as futures
import json
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = "https://127.0.0.1:8443"

# <b>Well under `max_connections`.</b> The point is the *shape* of the growth, and
# forcing exhaustion on the machine the owner is using would prove one number at the
# cost of everything else running on it. Where a ceiling is asserted below rather
# than measured, it says so.
LEVELS = [1, 8, 24, 48]

# Hosted layers, all on the one datastore — the per-source claim is that these share
# a pool however many of them are driven.
HOSTED = ["hosted/look_buildings", "hosted/look_parcels", "hosted/look_editable",
          "hosted/tr_il", "hosted/tr_ilce", "hosted/tr_yol"]

# A layer on a *different* data source: registered, its own connection string, so its
# own pool. `tr_ref` is `cicorpus.shapes` in the same PostgreSQL instance, which is
# what makes both pools visible in one `pg_stat_activity`.
REGISTERED = ["turkiye/tr_ref"]


def context():
    ssl_context = ssl.create_default_context()
    ssl_context.check_hostname = False
    ssl_context.verify_mode = ssl.CERT_NONE
    return ssl_context


def token(ssl_context):
    request = urllib.request.Request(
        f"{BASE}/rest/auth/login",
        data=json.dumps({"name": "root", "password": "change-me"}).encode(),
        headers={"Content-Type": "application/json"})

    with urllib.request.urlopen(request, context=ssl_context, timeout=30) as answer:
        return json.load(answer)["token"]


def backends():
    """What the database says it is holding, by state."""
    out = subprocess.run(
        ["docker", "exec", "gis-experiment-postgis", "psql", "-U", "gis", "-d", "gis",
         "-t", "-A", "-F", "|", "-c",
         "select state, count(*) from pg_stat_activity "
         "where datname = 'gis' and application_name <> 'psql' "
         "and pid <> pg_backend_pid() group by state"],
        capture_output=True, text=True, timeout=60)

    counts = {}

    for line in out.stdout.strip().splitlines():
        if "|" in line:
            state, count = line.split("|")
            counts[state or "(null)"] = int(count)

    counts["total"] = sum(v for k, v in counts.items() if k != "total")
    return counts


def query(bearer, ssl_context, service, seconds):
    """Queries one layer for a while, and reports what came back."""
    url = (f"{BASE}/rest/services/{urllib.parse.quote(service)}/FeatureServer/0/query"
           "?where=1%3D1&returnCountOnly=true&f=json")

    request = urllib.request.Request(url, headers={"Authorization": f"Bearer {bearer}"})
    stop = time.monotonic() + seconds
    ok = 0
    failed = None

    while time.monotonic() < stop:
        try:
            with urllib.request.urlopen(request, context=ssl_context, timeout=60) as answer:
                got = json.load(answer)

            if "count" in got:
                ok += 1
            else:
                failed = failed or str(got)[:120]
        except Exception as e:      # noqa: BLE001 — any failure is the same news here
            failed = failed or f"{type(e).__name__}: {e}"

    return ok, failed


def drive(bearer, ssl_context, services, concurrency, seconds=8):
    """Drives `concurrency` clients over `services`, sampling the database throughout."""
    samples = []
    done = []

    with futures.ThreadPoolExecutor(max_workers=concurrency + 1) as pool:
        for i in range(concurrency):
            done.append(pool.submit(
                query, bearer, ssl_context, services[i % len(services)], seconds))

        # Sampled from this thread while the load runs. The peak is what the budget is
        # about; an average would hide it.
        stop = time.monotonic() + seconds

        while time.monotonic() < stop:
            samples.append(backends())
            time.sleep(0.4)

    answered = sum(f.result()[0] for f in done)
    failures = [f.result()[1] for f in done if f.result()[1]]

    peak = max((s["total"] for s in samples), default=0)
    active = max((s.get("active", 0) for s in samples), default=0)

    return {
        "concurrency": concurrency,
        "services": len(services),
        "requests": answered,
        "peakBackends": peak,
        "peakActive": active,
        "samples": len(samples),
        "failures": failures[:3],
    }


def main():
    ssl_context = context()
    bearer = token(ssl_context)

    print("Q-04: what the connection budget actually is.\n")

    rest = backends()
    print(f"At rest: {rest['total']} backends {dict((k, v) for k, v in rest.items() if k != 'total')}\n")

    rows = []

    print(f"{'what':38} {'clients':>8} {'peak backends':>14} {'peak active':>12} {'requests':>9}")
    print("-" * 86)

    # 1. One layer, one data source. Backends should track clients, not layers.
    for level in LEVELS:
        row = drive(bearer, ssl_context, HOSTED[:1], level)
        row["what"] = "1 layer, 1 data source"
        rows.append(row)
        print(f"{row['what']:38} {level:>8} {row['peakBackends']:>14} "
              f"{row['peakActive']:>12} {row['requests']:>9}")
        time.sleep(2)

    # 2. Six layers, still one data source. If pooling were per layer or per service,
    #    this would be six times the first row at the same client count.
    for level in LEVELS[1:]:
        row = drive(bearer, ssl_context, HOSTED, level)
        row["what"] = f"{len(HOSTED)} layers, 1 data source"
        rows.append(row)
        print(f"{row['what']:38} {level:>8} {row['peakBackends']:>14} "
              f"{row['peakActive']:>12} {row['requests']:>9}")
        time.sleep(2)

    # 3. Two data sources: the datastore and a registered database. Two pools.
    for level in LEVELS[1:]:
        row = drive(bearer, ssl_context, HOSTED[:3] + REGISTERED, level)
        row["what"] = "2 data sources"
        rows.append(row)
        print(f"{row['what']:38} {level:>8} {row['peakBackends']:>14} "
              f"{row['peakActive']:>12} {row['requests']:>9}")
        time.sleep(2)

    # 4. Does it give them back? Npgsql prunes idle connections above MinPoolSize
    #    after ConnectionIdleLifetime — 300 s by default, and nothing here sets it.
    #    ADR-007 §4.8 wants shrink toward a floor of zero, and `LayerConnections`
    #    says in its own remarks that this is not implemented. Measured rather than
    #    read off either.
    print("\nIdle, after the load stops — ADR-007 §4.8's shrink-toward-a-floor:")
    fall = []
    started = time.monotonic()

    while time.monotonic() - started < 390:
        counts = backends()
        seconds = round(time.monotonic() - started)
        fall.append({"after": seconds, **counts})
        print(f"  +{seconds:>4}s  {counts['total']:>3} backends "
              f"({counts.get('idle', 0)} idle)")
        time.sleep(30)

    with open("benchmarks/connection-budget/measured.json", "w", encoding="utf-8") as out:
        json.dump({"atRest": rest, "load": rows, "idle": fall}, out, indent=1)

    print("\nEverything measured is in benchmarks/connection-budget/measured.json.")

    for row in rows:
        if row["failures"]:
            print(f"\nFailures at concurrency {row['concurrency']} ({row['what']}): "
                  f"{row['failures']}", file=sys.stderr)


if __name__ == "__main__":
    main()
