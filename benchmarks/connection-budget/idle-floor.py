"""What the pools settle at after load stops, and which pool the floor is in.

[D-110](../../docs/architecture-debt.md) measured it once and the number was
**sixteen, of which eight last ran the job claim**: the background workers claim on
the pool that serves requests, round-robin, so every connection in it is touched
again before `ConnectionIdleLifetime` can prune it. A pool that prunes correctly
cannot prune one somebody keeps knocking on.

The backoff (2026-08-23) made the knocking fifteen times rarer and left the floor
where it was. This script measures the half that moves it: the pollers on a pool of
their own, sized one connection per `JobKind`, so the shared pool can reach the floor
of zero [ADR-007](../../docs/adr/ADR-007-service-runtime.md) §4.8 claims.

    python benchmarks/connection-budget/idle-floor.py

Needs the dev server on https://127.0.0.1:8443 and the experiment PostGIS on
localhost:55432. Reads only. `GRATICULA_DEV_PASSWORD` must be set — a password in a
file is a password in the history the moment the file is committed.

**Attribution is by `application_name`, not by reading the query text.** The original
measurement had to recognise the claim statement to say whose sessions those were;
the pollers' pool names itself `graticula-jobs`, so the same question is a `where`
clause and stays true when the statement is rewritten.
"""

import json
import subprocess
import sys
import time

sys.path.insert(0, __file__.rsplit("\\", 1)[0].rsplit("/", 1)[0])

from budget import HOSTED, context, token, drive  # noqa: E402

# How long to watch after the load stops, and how often. The row's own figure for a
# pool draining was 184 seconds, so five minutes is enough to see a floor rather than
# a slope, and fifteen seconds is fine enough to see when it arrives.
WATCH_SECONDS = 300
SAMPLE_SECONDS = 15


def by_pool():
    """Idle backends per pool, named by what each pool calls itself."""
    out = subprocess.run(
        ["docker", "exec", "gis-experiment-postgis", "psql", "-U", "gis", "-d", "gis",
         "-t", "-A", "-F", "|", "-c",
         "select coalesce(nullif(application_name, ''), '(shared)'), count(*), "
         "round(max(extract(epoch from now() - state_change))::numeric, 1) "
         "from pg_stat_activity where datname = 'gis' and application_name <> 'psql' "
         "and pid <> pg_backend_pid() group by 1"],
        capture_output=True, text=True, timeout=60)

    pools = {}

    for line in out.stdout.strip().splitlines():
        if line.count("|") == 2:
            name, count, oldest = line.split("|")
            pools[name] = {"backends": int(count), "oldestIdleSeconds": float(oldest)}

    return pools


def main():
    ssl_context = context()
    bearer = token(ssl_context)
    known = HOSTED

    print(f"driving {len(known)} service(s) at 24 clients for 20 s")
    load = drive(bearer, ssl_context, known, concurrency=24, seconds=20)
    print(f"  peak backends under load: {load['peakBackends']}")

    print(f"watching for {WATCH_SECONDS} s, every {SAMPLE_SECONDS} s")

    trail = []
    started = time.monotonic()

    while time.monotonic() - started < WATCH_SECONDS:
        elapsed = round(time.monotonic() - started)
        pools = by_pool()
        trail.append({"t": elapsed, "pools": pools})

        print("  t+%3ds  " % elapsed + "  ".join(
            f"{name}={info['backends']} (idle {info['oldestIdleSeconds']:.0f}s)"
            for name, info in sorted(pools.items())))

        time.sleep(SAMPLE_SECONDS)

    final = trail[-1]["pools"]

    print()
    print("floor after %d s:" % WATCH_SECONDS)

    for name, info in sorted(final.items()):
        print(f"  {name:16} {info['backends']}")

    if "(shared)" not in final:
        print("  (shared)         0   <- the pool that serves requests reached zero")

    with open(__file__.replace("idle-floor.py", "idle-floor.json"), "w") as f:
        json.dump({"underLoad": load, "trail": trail}, f, indent=2)


if __name__ == "__main__":
    main()
