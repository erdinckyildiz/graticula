#!/bin/sh
# Run the OGC CITE engines against a server and count what they said.
#
# <b>[D-158](../docs/architecture-debt.md).</b> The two recorded runs of these suites are
# the strongest external evidence this project has, and until this script existed they were
# re-earned by somebody remembering. What is here is the whole recipe: which container,
# which package name, which parameter, and how to count an EARL report without reading it.
#
# Usage:  tools/cite-run.sh <suite> <server-url> <output-directory>
#   suite       wfs20 | ogcapi-features-1.0 | wms13
#   server-url  a **plain HTTP** address this machine answers on, without a path
#
# <b>Plain HTTP on purpose.</b> HTTPS works and costs a step: the self-signed certificate
# has to go into the container's Java truststore, which is a thing between the measurement
# and the answer. These suites test protocol conformance, not transport.
set -eu

SUITE=${1:?suite}
SERVER=${2:?server url}
OUT=${3:?output directory}

case "$SUITE" in
  wfs20)               IMAGE=ogccite/ets-wfs20;               PORT=8112; PARAM=wfs
                       ENTRY="$SERVER/wfs?service=WFS&version=2.0.0&request=GetCapabilities" ;;
  ogcapi-features-1.0) IMAGE=ogccite/ets-ogcapi-features10;   PORT=8113; PARAM=iut
                       ENTRY="$SERVER/ogc/features/v1" ;;
  wms13)               IMAGE=ogccite/ets-wms13;               PORT=8114; PARAM=capabilities-url
                       ENTRY="$SERVER/wms?service=WMS&version=1.3.0&request=GetCapabilities" ;;
  *) echo "unknown suite '$SUITE'" >&2; exit 2 ;;
esac

NAME="cite-$SUITE"
mkdir -p "$OUT"

cleanup() { docker rm -f "$NAME" >/dev/null 2>&1 || true; }
trap cleanup EXIT

cleanup

# <b>A published port and a gateway name, rather than host networking.</b> `--network host`
# is the obvious choice on a Linux runner and is silently wrong on Docker Desktop: it is
# accepted, `-p` is ignored with a warning, and the container comes up unreachable. Found
# by writing it that way first and watching TEAM Engine start and answer nothing. One form
# that works in both places is worth more than the better form that works in one.
#
# So the caller passes an address the *container* can reach --
# `http://host.docker.internal:PORT` -- and this maps the gateway for it.
docker run -d --name "$NAME" -p "$PORT:8080" \
  --add-host host.docker.internal:host-gateway "$IMAGE" >/dev/null

printf 'waiting for TEAM Engine on %s ' "$PORT"

ready=""
for attempt in $(seq 1 90); do
  if curl -sf -o /dev/null "http://localhost:$PORT/teamengine/"; then
    ready=$attempt
    break
  fi
  printf '.'
  sleep 2
done

echo

if [ -z "$ready" ]; then
  echo "TEAM Engine never answered on $PORT" >&2
  docker logs --tail 40 "$NAME" >&2 || true
  exit 1
fi

echo "ready after ${ready} tries; running $SUITE against $ENTRY"

# <b>REST rather than the web form, and it wants a credential.</b> `ogctest/ogctest` is the
# account the image ships with; it is not a secret and not ours.
encoded=$(printf '%s' "$ENTRY" | python -c "import sys,urllib.parse;print(urllib.parse.quote(sys.stdin.read(),safe=''))")

started=$(date +%s)

curl -s -u ogctest:ogctest --max-time 900 \
  "http://localhost:$PORT/teamengine/rest/suites/$SUITE/run?$PARAM=$encoded" \
  > "$OUT/$SUITE.rdf"

echo "ran in $(( $(date +%s) - started ))s, $(wc -c < "$OUT/$SUITE.rdf") bytes of EARL"

python tools/cite-count.py "$OUT/$SUITE.rdf"
