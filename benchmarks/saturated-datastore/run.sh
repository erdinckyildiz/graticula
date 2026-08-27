#!/bin/sh
# Tile throughput against a datastore that is out of CPU.
#
# <b>[ADR-021](../../docs/adr/ADR-021-tiles-are-encoded-by-postgis.md) condition 1</b>:
# *the datastore-saturation case is untested and is the strongest surviving argument against
# this decision. `ST_AsMVT` puts every byte of tile cost inside PostgreSQL. On the benchmark
# machine PostgreSQL had headroom, so the comparison never saw the case where it does not ...
# Before this server is recommended for a deployment where the datastore is the constraint,
# measure tile throughput against a saturated datastore. If the encoder wins there, this ADR
# is reopened.*
#
# The two paths are the ones the 2026-08-12 harness already served, so this compares the same
# two things that decision compared:
#
#   /tiles/{z}/{x}/{y}.mvt        ST_AsMVT -- every byte of encode inside PostgreSQL
#   /tiles-local/{z}/{x}/{y}.mvt  geometry out, encoded in .NET -- encode in a tier that scales
#
# <b>Saturation is CPU, and it is made by PostgreSQL work that does no encoding.</b> A load
# that also encoded would move the answer: what has to be scarce is the resource `ST_AsMVT`
# competes for. The generators run `ST_Area`/`ST_Perimeter` over the polygon table, which is
# geometry CPU and nothing else.
#
# Usage:  benchmarks/saturated-datastore/run.sh [rounds] [load-workers]
set -eu

ROUNDS=${1:-40}
WORKERS=${2:-8}

# <b>5080, because that is the port the harness binds and it does not take another.</b>
# `Program.cs` ends with `app.Run("http://0.0.0.0:5080")`, which overrides `ASPNETCORE_URLS`
# and `--urls` both -- so a run that set either started a process that bound 5080 anyway, or
# failed to bind because one was already there. This reuses a harness that is already
# answering rather than starting a second.
PORT=${PORT:-5080}
BOX=${BOX:-gis-experiment-postgis}
CONN=${GISBENCH_CONN:-"Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis;Maximum Pool Size=32;Minimum Pool Size=8;No Reset On Close=true"}

WORK=$(mktemp -d)
command -v cygpath >/dev/null 2>&1 && WORK=$(cygpath -m "$WORK")

cleanup() {
  [ -n "${HARNESS:-}" ] && kill "$HARNESS" 2>/dev/null || true
  [ -f "$WORK/load.pids" ] && while read -r p; do kill "$p" 2>/dev/null || true; done < "$WORK/load.pids"
  rm -rf "$WORK"
}
trap cleanup EXIT

say() { printf '\n== %s\n' "$1"; }

# ---------------------------------------------------------------- the harness
say "the benchmark harness"

if curl -s --max-time 3 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
  echo "  already answering on $PORT, reusing it"
else
  echo "  starting one"

  GISBENCH_CONN="$CONN" \
    dotnet benchmarks/harness/bin/Release/net9.0/GisBench.dll > "$WORK/harness.log" 2>&1 &
  HARNESS=$!

  waited=0
  while [ "$waited" -lt 40 ]; do
    if curl -s --max-time 2 "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then break; fi
    sleep 1
    waited=$((waited + 1))
  done

  if [ "$waited" -ge 40 ]; then
    echo "  the harness did not answer. Its log:"
    tail -20 "$WORK/harness.log"
    exit 1
  fi
fi

curl -s --max-time 5 "http://127.0.0.1:$PORT/health" | head -c 120
echo

# <b>One tile, chosen for having work in it.</b> A tile over the sea encodes nothing and
# would compare two ways of returning an empty answer. z=12 over Istanbul is dense polygon.
Z=12; X=2394; Y=1550

# ---------------------------------------------------------------- measuring
timed() {  # path  rounds  -> milliseconds per request, and bytes
  path=$1; rounds=$2
  total=0; bytes=0; failed=0

  # Two warm-ups that are not counted: the first request of a path pays for a plan and a
  # connection, and counting it measures the warm-up rather than the path.
  curl -s --max-time 60 "http://127.0.0.1:$PORT$path" > /dev/null 2>&1 || true
  curl -s --max-time 60 "http://127.0.0.1:$PORT$path" > /dev/null 2>&1 || true

  i=0
  while [ "$i" -lt "$rounds" ]; do
    out=$(curl -s --max-time 60 -o "$WORK/tile.mvt" \
      -w '%{time_total} %{size_download} %{http_code}' \
      "http://127.0.0.1:$PORT$path" 2>/dev/null || echo "0 0 000")

    set -- $out
    [ "$3" = "200" ] || failed=$((failed + 1))
    total=$(python -c "print($total + $1 * 1000)")
    bytes=$2
    i=$((i + 1))
  done

  python -c "print('%8.1f ms   %7d B   %d failed' % ($total / $rounds, $bytes, $failed))"
}

walk() {
  printf '  %-14s %s\n' "ST_AsMVT" "$(timed "/tiles/$Z/$X/$Y.mvt" "$ROUNDS")"
  printf '  %-14s %s\n' "local encode" "$(timed "/tiles-local/$Z/$X/$Y.mvt" "$ROUNDS")"
}

say "with the datastore idle"
walk

# ---------------------------------------------------------------- saturation
say "saturating the datastore with $WORKERS geometry-CPU workers"

: > "$WORK/load.pids"

i=0
while [ "$i" -lt "$WORKERS" ]; do
  docker exec -e PGPASSWORD=gis "$BOX" psql -U gis -d gis -q -c \
    "select sum(st_area(way)) + sum(st_perimeter(way)) from planet_osm_polygon;" \
    > /dev/null 2>&1 &
  echo $! >> "$WORK/load.pids"
  i=$((i + 1))
done

# Let the workers get into the CPU before measuring against them.
sleep 6

busy=$(docker exec -e PGPASSWORD=gis "$BOX" psql -U gis -d gis -t -A -c \
  "select count(*) from pg_stat_activity where state = 'active' and query like '%st_area%';" \
  2>/dev/null || echo "?")

echo "  active geometry queries in the datastore: $busy"

say "with the datastore saturated"
walk

say "letting the load finish"
while read -r p; do wait "$p" 2>/dev/null || true; done < "$WORK/load.pids"
: > "$WORK/load.pids"

say "with the datastore idle again"
walk
