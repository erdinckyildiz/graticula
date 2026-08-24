"""D-08: is the 90-million-row figure an extrapolation from one point, or a slope?

The statement-timeout benchmark measured a full-extent render read at 330 ms over one
million rows and extrapolated 30 seconds at roughly 90 million. One point cannot tell a
linear cost from a superlinear one, and the difference decides whether the ceiling is
reached at 90 million rows or at nine. So the same read is measured at four sizes.

Measured at the store rather than through a face, deliberately: statement_timeout is a
PostgreSQL setting and it is the store's clock that runs out. What a face adds -- encode,
serialise, TLS -- is already decomposed in benchmarks/feature-query.
"""
import re
import subprocess
import sys
import time

sys.stdout.reconfigure(encoding="utf-8")

C = "gis-experiment-postgis"
SIZES = [1_000_000, 4_000_000, 16_000_000]


def psql(sql, timeout=3600):
    out = subprocess.run(
        ["docker", "exec", "-i", C, "psql", "-U", "gis", "-d", "gis", "-v", "ON_ERROR_STOP=1",
         "-tAc", sql],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=timeout)
    return ((out.stdout or "") + (out.stderr or "")).strip()


def timed(sql, runs=3):
    """Server-side milliseconds, taken from EXPLAIN ANALYZE rather than from the wall.

    The wall clock here includes docker exec, psql start-up and the client reading the
    rows -- tens of milliseconds that do not count against statement_timeout, which is
    the thing under measurement.
    """
    best = None
    for _ in range(runs):
        text = psql("explain (analyze, timing off, format text) " + sql)
        found = re.search(r"Execution Time: ([0-9.]+) ms", text)
        if not found:
            return None, text
        ms = float(found.group(1))
        best = ms if best is None else min(best, ms)
    return best, None


print("size,rows,build_s,index_s,render_ms,count_ms")

for n in SIZES:
    started = time.monotonic()
    psql("drop table if exists d08_scale;")
    psql(
        "create table d08_scale as select i as id, "
        "st_setsrid(st_makepoint(-180 + (i % 3600000) / 10000.0, "
        "-90 + (i % 1800000) / 10000.0), 4326) as g "
        f"from generate_series(1,{n}) i;")
    build = time.monotonic() - started

    started = time.monotonic()
    psql("create index d08_scale_gix on d08_scale using gist (g);")
    psql("analyze d08_scale;")
    index = time.monotonic() - started

    actual = psql("select count(*) from d08_scale;")

    # A full-extent render read: every geometry in the layer's own extent, in the wire
    # form a renderer receives. The bbox is the whole world, which is what "full extent"
    # means and what makes this the most expensive honest read.
    render, problem = timed(
        "select st_asbinary(g) from d08_scale "
        "where g && st_makeenvelope(-180,-90,180,90,4326)")

    counted, _ = timed("select count(*) from d08_scale")

    if problem:
        print(f"{n},{actual},{build:.1f},{index:.1f},FAILED,{problem[:120]}")
    else:
        print(f"{n},{actual},{build:.1f},{index:.1f},{render:.0f},{counted:.0f}")

    sys.stdout.flush()

psql("drop table if exists d08_scale;")
print("cleaned up")
