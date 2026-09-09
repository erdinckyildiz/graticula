#!/bin/sh
# Walk ADR-017 §3's four scenarios against a running server, in order.
#
# <b>ADR-017 condition 2</b>: *every §3 scenario is walkable against the built skeleton, in
# order, with no step requiring a log file. A step that needs `docker logs` is a missing
# endpoint.* This is that walk, as a script rather than an afternoon, so the answer can be
# re-earned rather than remembered.
#
# Usage:  GRATICULA_TEST_PASSWORD=... tools/walk-adr017.sh https://127.0.0.1:8447
set -eu

ROOT=${1:?server url}
USER=${GRATICULA_TEST_USER:-ci}
PASSWORD=${GRATICULA_TEST_PASSWORD:?set GRATICULA_TEST_PASSWORD}

TOKEN=$(curl -sk -X POST "$ROOT/rest/auth/login" -H "Content-Type: application/json" \
  -d "{\"name\":\"$USER\",\"password\":\"$PASSWORD\"}" \
  | python -c "import sys,json;print(json.load(sys.stdin)['token'])")

LAYER=$(curl -sk "$ROOT/admin/layers" -H "Authorization: Bearer $TOKEN" \
  | python -c "import sys,json;print(json.load(sys.stdin)['layers'][0]['name'])")

SERVICE=$(curl -sk "$ROOT/admin/layers" -H "Authorization: Bearer $TOKEN" \
  | python -c "
import sys, json
one = json.load(sys.stdin)['layers'][0]
print((one['folder'] + '/' if one['folder'] else '') + one['service'])
")

case "$SERVICE" in
  */*) CAPABILITIES="/admin/services/${SERVICE##*/}/capabilities?folder=${SERVICE%/*}" ;;
  *)   CAPABILITIES="/admin/services/$SERVICE/capabilities" ;;
esac

SOURCE=$(curl -sk "$ROOT/admin/datasources" -H "Authorization: Bearer $TOKEN" \
  | python -c "import sys,json;print(json.load(sys.stdin)['dataSources'][0]['id'])")

echo "layer=$LAYER  source=$SOURCE"
echo

step() {  # scenario  n  method  path  what-it-answers  [body]
  # <b>A body, for the one step that takes one.</b> Without it POST /admin/datasources/test
  # answered 400 and this walk reported it beside the routes that are not there -- a probe
  # that works, filed as a probe that is missing, by the walk written to stop exactly that.
  if [ $# -ge 6 ] && [ -n "$6" ]; then
    code=$(curl -sk -o /dev/null -w "%{http_code}" -X "$3" \
      "$ROOT$4" -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
      -d "$6" --max-time 20 || echo 000)
  else
    code=$(curl -sk -o /dev/null -w "%{http_code}" -X "$3" \
      "$ROOT$4" -H "Authorization: Bearer $TOKEN" --max-time 20 || echo 000)
  fi

  case "$code" in
    2*) verdict="answers" ;;
    404) verdict="NOT THERE" ;;
    405) verdict="wrong verb" ;;
    *)   verdict="$code" ;;
  esac

  printf '%-5s %s  %-9s %-52s %s\n' "$1" "$2" "$verdict" "$3 $4" "$5"
}

echo '3.1 "The map is showing old data"'
step 3.1 1 GET  "/admin/layers/$LAYER"                    "hosted or registered, which source"
step 3.1 2 GET  "/admin/layers/$LAYER/cache"              "when each level was generated"

# <b>Step 3.1.2 is read as well as counted, for the reason step 3.4.1 is.</b> The 405 says
# only that there is no per-layer cache document. It does not say whether the *tile* answers
# the question this step asks -- when was this generated -- and the tile is the one party
# that would know. A stored tile carrying HIT and max-age and no Age is <b>D-248</b>, and
# that is a correctness fault rather than a missing screen: a cache in front restarts the
# lifetime from its own receipt, so the staleness this scenario is about is one we cause.
# The root tile is asked because every service has that address; the first call fills the
# store and the second is the one that is read.
curl -sk -o /dev/null --max-time 20 \
  "$ROOT/rest/services/$SERVICE/VectorTileServer/tile/0/0/0.pbf" \
  -H "Authorization: Bearer $TOKEN" 2>/dev/null || true

printf '%-5s %s  ' 3.1 2
curl -sk -D - -o /dev/null --max-time 20 \
  "$ROOT/rest/services/$SERVICE/VectorTileServer/tile/0/0/0.pbf" \
  -H "Authorization: Bearer $TOKEN" 2>/dev/null | python -c "
import sys

seen = {}
for line in sys.stdin.read().splitlines():
    if ':' in line:
        name, _, value = line.partition(':')
        seen[name.strip().lower()] = value.strip()

if seen.get('x-tile-cache') != 'HIT':
    print('%-9s %s' % ('not read', 'the root tile did not come back from the store'))
elif 'age' in seen:
    print('%-9s %s' % ('says it', 'Age: ' + seen['age']))
else:
    print('%-9s served as %s, and carries no Age and no Last-Modified (D-248)'
          % ('SAYS NOTHING', seen.get('cache-control') or 'a cached tile'))
"
step 3.1 3 GET  "/admin/datasources/$SOURCE/drift"        "has the source changed"
# <b>Step 3.1.4 is an address, and the address is not the act.</b> The scoped invalidation
# ADR-010 6b asks for does not exist. The unscoped one does, at
# POST /admin/layers/{name}/refresh, which purges a layer's tiles and reports the count --
# and it is deliberately not walked here, because a walk that can be run on demand must not
# empty a cache to prove that it can.
step 3.1 4 POST "/admin/layers/$LAYER/cache/invalidate"   "fix it, scoped"

echo
echo '3.2 "This service is slow"'
step 3.2 1 GET  "/admin/layers/$LAYER/health"             "latency, error rate, request rate"
step 3.2 2 GET  "/admin/layers/$LAYER/capability"         "is a filter being refused"

# And where that half is answered, which needs the folder: without it a service inside one
# answers 404 at the single 3.2 address that exists, which is how a reader concludes the
# server cannot say.
step 3.2 2 GET  "$CAPABILITIES"                           "  ^ answered here instead"
step 3.2 3 GET  "/admin/workers"                          "which worker holds the context"
step 3.2 4 GET  "/admin/workers/1"                        "allocation and GC pause share"
step 3.2 5 GET  "/admin/datasources/$SOURCE/pool"         "pool saturation and wait"
step 3.2 6 POST "/admin/layers/$LAYER/pin"                "pin the context"

echo
echo '3.3 "Registration failed"'
step 3.3 2 POST "/admin/datasources/test"                 "re-run the probe, create nothing" \
  "{\"name\":\"walk\",\"connectionString\":\"Host=no-such-host.invalid;Database=x;Username=x;Password=x\"}"

# <b>And it answers 200 with `CannotConnect`, which is the point of the step.</b> The status
# code is the probe's, not the source's: a source that cannot be reached is a finding this
# endpoint made successfully, and the reason is in the body. Reading the code alone here
# would repeat the mistake step 3.4.1 documents, in the other direction.
step 3.3 3 GET  "/admin/datasources/$SOURCE/capability"   "what we could do with this source"
step 3.3 4 GET  "/admin/jobs"                             "the two things that are jobs"

echo
echo '3.4 "Everything stopped at 03:14"'
step 3.4 1 GET  "/admin/health"                           "certificate expired at 03:14"

# <b>Step 3.4.1 is the one step whose status code is not the answer.</b> The route answered
# 200 on 2026-08-27 and said nothing about a certificate, which is how a walk that reads only
# codes can call a scenario walkable while the operator meeting it learns nothing. So this
# step is read rather than counted.
printf '%-5s %s  ' 3.4 1
curl -sk "$ROOT/admin/health" -H "Authorization: Bearer $TOKEN" | python -c "
import sys, json
d = json.load(sys.stdin)
c = d.get('servingCertificate')
if not c:
    print('%-9s %s' % ('SAYS NOTHING', 'no servingCertificate in the health document'))
elif 'state' not in c or 'notAfter' not in c:
    print('%-9s %s' % ('PARTIAL', 'servingCertificate carries %s' % sorted(c)))
else:
    print('%-9s %s, expires %s (%s days)'
          % ('names it', c['state'], c['notAfter'], c['daysRemaining']))
"
step 3.4 2 GET  "/admin/certificates"                     "every certificate, with expiry"
step 3.4 3 PUT  "/admin/certificates/serving"             "install the replacement, no restart"

echo
echo 'and what /admin/health already carries, which decides how much of 3.2 and 3.4 is missing:'
curl -sk "$ROOT/admin/health" -H "Authorization: Bearer $TOKEN" \
  | python -c "
import sys, json
d = json.load(sys.stdin)
for key in sorted(d):
    value = d[key]
    if isinstance(value, dict):
        print(f'  {key}: {sorted(value)}')
    elif isinstance(value, list):
        print(f'  {key}: [{len(value)}]')
    else:
        print(f'  {key}: {value}')
"
