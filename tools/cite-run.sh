#!/bin/sh
# Run the OGC CITE engines against a server and count what they said.
#
# <b>[D-158](../docs/architecture-debt.md).</b> The two recorded runs of these suites are
# the strongest external evidence this project has, and until this script existed they were
# re-earned by somebody remembering. What is here is the whole recipe: which container,
# which package name, which parameter, and how to count an EARL report without reading it.
#
# Usage:  tools/cite-run.sh <suite> <server-url> <output-directory>
#   suite       wfs20 | ogcapi-features-1.0 | wms13 | wms11 | wfs11 | wcs20 | wmts10
#               | ogcapi-tiles-1.0
#   server-url  a **plain HTTP** address this machine answers on, without a path
#
# <b>Plain HTTP on purpose.</b> HTTPS works and costs a step: the self-signed certificate
# has to go into the container's Java truststore, which is a thing between the measurement
# and the answer. These suites test protocol conformance, not transport.
set -eu

SUITE=${1:?suite}
SERVER=${2:?server url}
OUT=${3:?output directory}
CODE=
EXTRA=

quote() { printf '%s' "$1" | python -c "import sys,urllib.parse;print(urllib.parse.quote(sys.stdin.read(),safe=''))"; }

case "$SUITE" in
  wfs20)               IMAGE=ogccite/ets-wfs20;               PORT=8112; PARAM=wfs
                       ENTRY="$SERVER/wfs?service=WFS&version=2.0.0&request=GetCapabilities" ;;
  ogcapi-features-1.0) IMAGE=ogccite/ets-ogcapi-features10;   PORT=8113; PARAM=iut
                       ENTRY="$SERVER/ogc/features/v1" ;;
  wms13)               IMAGE=ogccite/ets-wms13;               PORT=8114; PARAM=capabilities-url
                       ENTRY="$SERVER/wms?service=WMS&version=1.3.0&request=GetCapabilities" ;;
  # <b>The second five, 2026-10-04</b> -- the surfaces that existed and that nothing outside
  # this repository had ever checked: WMS 1.1.1, WFS 1.1.0, WCS 2.0.1, WMTS 1.0.0 and OGC API
  # Tiles. WMTS is pointed at the public image service cite.yml makes, because the suite asks
  # for image tiles and the server-wide /wmts serves vector ones.
  #
  # <b>Three of them are not called what their image is called.</b> The REST name is the
  # suite's `ets-code` in its pom.xml -- `wfs`, `wcs`, `wmts` -- and the first run, which
  # guessed `wfs11`, `wcs20` and `wmts10`, got three 404s. CODE is that name; SUITE stays
  # the name of the report and of the baseline.
  wms11)               IMAGE=ogccite/ets-wms11;               PORT=8115; PARAM=capabilities-url
                       ENTRY="$SERVER/wms?service=WMS&version=1.1.1&request=GetCapabilities" ;;
  wfs11)               IMAGE=ogccite/ets-wfs11;               PORT=8116; PARAM=capabilities-url; CODE=wfs
                       ENTRY="$SERVER/wfs?service=WFS&version=1.1.0&request=GetCapabilities" ;;
  wcs20)               IMAGE=ogccite/ets-wcs20;               PORT=8117; PARAM=url; CODE=wcs
                       ENTRY="$SERVER/wcs?service=WCS&version=2.0.1&request=GetCapabilities" ;;
  wmts10)              IMAGE=ogccite/ets-wmts10;              PORT=8118; PARAM=capabilities-url; CODE=wmts
                       ENTRY="$SERVER/rest/services/hosted/cite_imagery/ImageServer/WMTS/1.0.0/WMTSCapabilities.xml" ;;
  # <b>The Tiles suite will not start its core tests without a tile to fetch.</b> *A tile
  # matrix set definition uri was not found in the test inputs* was all the first run said.
  # Its SuiteFixtureListener reads five more keys -- the OGC URI of a registered tile matrix
  # set, a URL template for tiles under it, a level, and a row and column range -- and they
  # name the seeder's `ci_parcels` at level 10, row 387, column 605: the one tile there that
  # has its twelve polygons in it. Level 0 answers 204, which is right for an empty tile and
  # useless as a fixture.
  ogcapi-tiles-1.0)    IMAGE=ogccite/ets-ogcapi-tiles10;      PORT=8119; PARAM=iut
                       ENTRY="$SERVER/ogc/tiles/v1"
                       EXTRA="tilematrixsetdefinitionuri=$(quote "http://www.opengis.net/def/tilematrixset/OGC/1.0/WebMercatorQuad")"
                       EXTRA="$EXTRA&urltemplatefortiles=$(quote "$SERVER/ogc/tiles/v1/collections/hosted.ci_parcels/tiles/WebMercatorQuad/{tileMatrix}/{tileRow}/{tileCol}")"
                       EXTRA="$EXTRA&tilematrix=10&mintilerow=387&maxtilerow=387&mintilecol=605&maxtilecol=605" ;;
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
query="$PARAM=$(quote "$ENTRY")${EXTRA:+&$EXTRA}"

started=$(date +%s)

curl -s -u ogctest:ogctest --max-time 900 \
  "http://localhost:$PORT/teamengine/rest/suites/${CODE:-$SUITE}/run?$query" \
  > "$OUT/$SUITE.rdf"

echo "ran in $(( $(date +%s) - started ))s, $(wc -c < "$OUT/$SUITE.rdf") bytes of EARL"

# <b>When the engine refuses the run, what it would have accepted.</b> A suite's REST name
# and its parameter names are not documented in one place, and a wrong one comes back as an
# HTML page rather than a report. So a run with no outcome prints the suites this container
# knows and the form of the one asked for -- the answer to *what should I have sent*, from
# the engine itself rather than from memory.
if ! grep -q 'earl:outcome' "$OUT/$SUITE.rdf"; then
  echo "--- the suites this engine knows:"
  curl -s -u ogctest:ogctest "http://localhost:$PORT/teamengine/rest/suites" \
    | sed -e 's/<[^>]*>/ /g' | tr -s ' \n' ' ' | cut -c1-1500
  echo
  echo "--- and what $SUITE asks for:"
  curl -s -u ogctest:ogctest "http://localhost:$PORT/teamengine/rest/suites/${CODE:-$SUITE}" \
    | grep -oiE '(name|id)="[^"]+"|<(label|h[1-4]|p|dt|dd|li)[^>]*>[^<]{2,200}' | head -60
fi

python tools/cite-count.py "$OUT/$SUITE.rdf"
