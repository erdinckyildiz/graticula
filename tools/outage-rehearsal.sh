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

LEDGER=$(mktemp)
FAILURES=$(mktemp)
SEEN="$LEDGER.start"
: > "$SEEN"

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

  # <b>Recorded as well as printed, because a rehearsal that only prints cannot fail.</b>
  # ADR-026 condition 4 asks for the outage test to be something CI *runs*, and a step that
  # is green whatever the server did is not that.
  printf '%s\t%s\n' "$1" "$code" >> "$SEEN"
}

# What a phase saw, or the empty string if it did not probe that.
#
# <b>awk with an exact field match, not grep.</b> Written first with a Perl-mode grep and
# a quoted-literal pattern, which returned nothing for every label on this machine -- and
# because an empty answer reads as *did not probe that*, some expectations passed by never
# being evaluated while others failed loudly. A helper that silently answers *nothing* is
# the same trap as a check that cannot fail, and it took a run with the ledger printed to
# see it.
saw() {
  awk -F"\t" -v want="$1" '$1 == want { seen = $2 } END { print seen }' "$SEEN"
}

phase() { SEEN="$LEDGER.$1"; : > "$SEEN"; }

expect() {  # what  code  why
  got=$(saw "$1")

  if [ "$got" != "$2" ]; then
    printf 'WRONG  %s answered %s and should have answered %s\n         %s\n' \
      "$1" "${got:-nothing}" "$2" "$3" >> "$FAILURES"
  fi
}

refused() {  # what  why
  got=$(saw "$1")

  case "$got" in
    2*) printf 'WRONG  %s answered %s and should have been refused\n         %s\n' \
          "$1" "$got" "$2" >> "$FAILURES" ;;
  esac
}

# <b>The serving surface, which is what [ADR-026](../docs/adr/ADR-026-serving-through-a-platform-store-outage.md)
# is actually about.</b> Its §2 states the case in one sentence: *a registered layer in the
# customer's own PostGIS goes dark because our bookkeeping database is restarting.* The
# catalogue fallback exists so that it does not. What the fallback can and cannot buy depends
# on where the rows live, and this walk reads that rather than assuming it:
#
#   - a **public** service should still answer while the store is blind, from the remembered
#     catalogue entry;
#   - a **private** one should not, before or during;
#   - and for a **hosted** layer the data is in the same database as the platform store, so
#     the catalogue can remember what to serve and there is nothing left to serve it from.
#     That is not a defect of the fallback; it is the shape of the baseline deployment, where
#     Q-69 and Q-70 put the platform store inside the datastore.
serve() {
  probe "public service document"  GET "/rest/services/hosted/$PUBLIC/FeatureServer?f=json" anon
  probe "public service rows"      GET \
    "/rest/services/hosted/$PUBLIC/FeatureServer/0/query?where=1%3D1&resultRecordCount=1&f=json" anon
  probe "private service"          GET "/rest/services/hosted/$PRIVATE/FeatureServer?f=json" anon
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
  serve
}

# The two layers this walks with, taken from the store while it is up rather than named here.
PUBLIC=$(curl -sk "$ROOT/admin/layers" -H "Authorization: Bearer $TOKEN" \
  | python -c "import sys,json;print(next(l['name'] for l in json.load(sys.stdin)['layers'] if l['sharing']=='public'))")

PRIVATE=$(curl -sk "$ROOT/admin/layers" -H "Authorization: Bearer $TOKEN" \
  | python -c "import sys,json;print(next((l['name'] for l in json.load(sys.stdin)['layers'] if l['sharing']!='public'), 'no-private-layer'))")

echo "public=$PUBLIC  private=$PRIVATE"

say "with the datastore up"
phase up
walk

say "stopping $BOX"
docker stop "$BOX" >/dev/null
started=no

# The pool notices on its next attempt rather than immediately; two seconds is enough for
# the first request below to be the one that discovers it.
sleep 2

say "with the datastore down"
phase down
walk

# <b>Again, past the shape cache, and this is the phase that catches things.</b> The walk
# above runs about two seconds after the walk that preceded it, so every layer's shape is
# still inside `ServiceContexts.Lifetime` and the document above was answered without the
# store being asked at all. That is a real behaviour and worth walking -- but it is not
# ADR-026's promise, and for months it was the only thing this script measured: the fallback
# that serves a remembered shape when the describe actually fails was never once reached
# here. It was broken the whole time, and the run that found it found it by accident, because
# something happened to evict an entry first. D-224.
#
# <b>Thirty-one seconds, which is the cache's own lifetime and one.</b> Long, and the reason
# it is affordable is that this is the only place the fallback is watched end to end.
say "with the datastore down, past the shape cache"
sleep 31

phase blind
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
phase back
walk

say "waiting out the breaker ten-second cooling window"
sleep 12

say "with the datastore up again, past the breaker"
phase cooled
walk

# ---------------------------------------------------------------- the verdict
#
# <b>What [ADR-026](../docs/adr/ADR-026-serving-through-a-platform-store-outage.md) decided,
# read back from the run rather than from the document.</b> *Public-only, while blind*: while
# the platform store cannot be reached, a service shared publicly is still described from the
# remembered catalogue entry, and one that is not is refused rather than guessed at.
say "verdict"

SEEN="$LEDGER.down"
expect "health (anonymous)" 200 \
  "ADR-017 6: the management plane must survive a data-plane failure, and health is the whole
         of the minimal surface."
expect "public service document" 200 \
  "ADR-026: a publicly shared service is described from the remembered catalogue entry while
         the store is blind. A 404 here means the fallback did not fire."
refused "private service" \
  "ADR-026 is public-ONLY while blind. Serving a service whose scope this server cannot
         currently confirm is the failure the whole decision exists to avoid."

# <b>The same expectations, now that the store has actually been asked.</b> Everything the
# `down` block asserts is asserted again here against a walk that could not have been answered
# from a warm shape -- which is the difference between watching the fallback and watching the
# cache in front of it.
SEEN="$LEDGER.blind"
expect "health (anonymous)" 200   "ADR-017 6 does not stop applying because the outage lasted longer than a cache."
expect "public service document" 200   "ADR-026 and D-127: the shape this server last read is what describes a public service
         while the store is blind. This is the assertion the 'down' phase above cannot make,
         because two seconds after a successful read nothing has been asked of the store."
refused "private service"   "Public-ONLY while blind, for as long as blind lasts."

SEEN="$LEDGER.cooled"
expect "health (anonymous)" 200 "The server must come back on its own."
expect "public service document" 200 "The server must come back on its own."
expect "public service rows" 200 \
  "Rows come from the datastore, so they return with it -- past the breaker's window there is
         nothing left degraded."
expect "the admin layer list" 200 \
  "An administrator who was signed in before the outage can work again afterwards without
         signing in, once the breaker has cooled."

if [ -s "$FAILURES" ]; then
  echo
  cat "$FAILURES"
  echo
  echo "The outage did not behave the way ADR-026 and ADR-017 6 say it does."
  rm -f "$LEDGER" "$LEDGER".* "$FAILURES"
  exit 1
fi

echo "  every expectation held: public-only while blind, and recovery without help."
rm -f "$LEDGER" "$LEDGER".* "$FAILURES"
