#!/bin/sh
# Stop the datastore under a running server and record what each surface says.
#
# <b>[ADR-019](../docs/adr/ADR-019-service-catalog.md) condition 1, inherited from
# [ADR-017](../docs/adr/ADR-017-admin-api.md) condition 1</b>: *the degraded surface is tested
# by stopping the datastore. Until that test exists, §4's seam is a claim.*
# `CatalogFallbackTests` pins the policy in twelve cases against a fake store, which is what
# makes those failure modes reachable at all -- and it is not this. **Nothing in the suite
# stops a real datastore**, so what ADR-017 §6's minimal admin surface does during an actual
# outage was, until this script, written down and never watched.
#
# <b>This ends by starting the container again</b>, in a trap, so an interrupted run does not
# leave the machine's database down.
#
# Usage:  GRATICULA_TEST_PASSWORD=... tools/outage-rehearsal.sh https://127.0.0.1:8447 [container]
set -eu

ROOT=${1:?server url}
BOX=${2:-gis-experiment-postgis}
USER=${GRATICULA_TEST_USER:-ci}
PASSWORD=${GRATICULA_TEST_PASSWORD:?set GRATICULA_TEST_PASSWORD}

started=no
restore() {
  if [ "$started" = "no" ]; then
    printf '\n-- restoring %s\n' "$BOX"
    docker start "$BOX" >/dev/null 2>&1 || true
  fi
}
trap restore EXIT

say() { printf '\n== %s\n' "$1"; }

TOKEN=$(curl -sk -X POST "$ROOT/rest/auth/login" -H "Content-Type: application/json" \
  -d "{\"name\":\"$USER\",\"password\":\"$PASSWORD\"}" \
  | python -c "import sys,json;print(json.load(sys.stdin)['token'])")

# <b>Every surface ADR-017 §6 names, and two that are not in its table.</b> The two are the
# point of comparison: a data-plane route and an authenticated admin route should behave
# differently from the supervisor's minimal set, and if they do not then §6's seam is not
# where the document says it is.
probe() {  # label  method  path  auth
  if [ "$4" = "auth" ]; then
    body=$(curl -sk -X "$2" "$ROOT$3" -H "Authorization: Bearer $TOKEN" \
      --max-time 25 -w '\n%{http_code}' 2>/dev/null || printf '\n000')
  else
    body=$(curl -sk -X "$2" "$ROOT$3" --max-time 25 -w '\n%{http_code}' 2>/dev/null \
      || printf '\n000')
  fi

  code=$(printf '%s' "$body" | tail -1)
  first=$(printf '%s' "$body" | head -c 190 | tr '\n' ' ')

  printf '  %-26s %-4s %s\n' "$1" "$code" "$first"
}

walk() {
  probe "health (anonymous)"      GET /admin/health              anon
  probe "health (authenticated)"  GET /admin/health              auth
  probe "version"                 GET /admin/version             auth
  probe "certificates"            GET /admin/certificates        auth
  probe "workers"                 GET /admin/workers             auth
  probe "-- not in the table --"  GET /rest/info                 anon
  probe "the service directory"   GET /rest/services             anon
  probe "the admin layer list"    GET /admin/layers              auth
}

say "with the datastore up"
walk

say "stopping $BOX"
docker stop "$BOX" >/dev/null
started=no

# The pool notices on its next attempt rather than immediately; two seconds is enough for
# the first request below to be the one that discovers it.
sleep 2

say "with the datastore down"
walk

say "starting $BOX"
docker start "$BOX" >/dev/null
started=yes

waited=0
while [ "$waited" -lt 60 ]; do
  if docker exec "$BOX" pg_isready -q 2>/dev/null; then
    break
  fi
  sleep 1
  waited=$((waited + 1))
done

printf '  came back after %ss\n' "$waited"

# <b>The database being back is not the server being back, and the gap is
# measurable.</b> `SourceBreaker.Cooling` is ten seconds: while it is open every
# request short-circuits without touching the store, which is what stops an outage
# becoming a queue collapse -- and it means an operator who has just fixed the
# database is still refused for up to ten seconds afterwards. Walked immediately, so
# the run shows that; then walked again past the window, so the run also shows it
# clearing on its own rather than needing anything.
say "with the datastore up again, immediately"
walk

say "waiting out the breaker ten-second cooling window"
sleep 12

say "with the datastore up again, past the breaker"
walk
