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

SOURCE=$(curl -sk "$ROOT/admin/datasources" -H "Authorization: Bearer $TOKEN" \
  | python -c "import sys,json;print(json.load(sys.stdin)['dataSources'][0]['id'])")

echo "layer=$LAYER  source=$SOURCE"
echo

step() {  # scenario  n  method  path  what-it-answers
  code=$(curl -sk -o /dev/null -w "%{http_code}" -X "$3" \
    "$ROOT$4" -H "Authorization: Bearer $TOKEN" --max-time 20 || echo 000)

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
step 3.1 3 GET  "/admin/datasources/$SOURCE/drift"        "has the source changed"
step 3.1 4 POST "/admin/layers/$LAYER/cache/invalidate"   "fix it, scoped"

echo
echo '3.2 "This service is slow"'
step 3.2 1 GET  "/admin/layers/$LAYER/health"             "latency, error rate, request rate"
step 3.2 2 GET  "/admin/layers/$LAYER/capability"         "is a filter being refused"
step 3.2 3 GET  "/admin/workers"                          "which worker holds the context"
step 3.2 4 GET  "/admin/workers/1"                        "allocation and GC pause share"
step 3.2 5 GET  "/admin/datasources/$SOURCE/pool"         "pool saturation and wait"
step 3.2 6 POST "/admin/layers/$LAYER/pin"                "pin the context"

echo
echo '3.3 "Registration failed"'
step 3.3 2 POST "/admin/datasources/test"                 "re-run the probe, create nothing"
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
