#!/bin/sh
# Install and run the bundle on a network with no route off it.
#
# <b>[ADR-016](../docs/adr/ADR-016-packaging-deployment-upgrade.md) condition 3</b>: *the
# bundle is tested by installing on a machine with no network route, which is the only way
# [Q-15](../docs/open-questions.md) gets tested rather than asserted.*
#
# <b>`--internal`, not `--network none`, and the difference is the whole test.</b> A container
# with no network at all cannot reach its database either, so it proves that a server with no
# database does not start -- which nobody doubted. What Q-15 asks is whether anything is
# *fetched from the internet*: PROJ grids, GDAL driver data, fonts, a telemetry endpoint, a
# certificate authority's OCSP responder. A Docker `--internal` network gives container-to-
# container routing and no default gateway, which is a datacentre with no way out.
#
# The datastore is started on the same network, so this is an air-gapped *deployment* rather
# than an isolated process.
#
# Usage:  tools/airgap-rehearsal.sh [server-image]
set -eu

IMAGE=${1:-graticula-d19:local}
NET=graticula-airgap
PG=airgap-postgis
APP=airgap-server
KEY=$(openssl rand -base64 32)

cleanup() {
  docker rm -f "$APP" "$PG" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT

say() { printf '\n== %s\n' "$1"; }

say "a network with no way out"
docker network rm "$NET" >/dev/null 2>&1 || true
docker network create --internal "$NET" >/dev/null
printf '  internal=%s\n' "$(docker network inspect "$NET" --format '{{.Internal}}')"

say "the datastore, on that network and nowhere else"
docker run -d --name "$PG" --network "$NET" \
  -e POSTGRES_USER=gis -e POSTGRES_PASSWORD=gis -e POSTGRES_DB=gis \
  postgis/postgis:16-3.4 >/dev/null

# <b>Over TCP, not `pg_isready` on the socket.</b> The official image starts PostgreSQL
# on the unix socket alone for its initialisation phase and restarts it afterwards for
# real, so a socket check answers *ready* while nothing is listening on TCP -- which is
# how the first run of this got `connection refused` from a database it had just been
# told was up. `-h` makes pg_isready use the network, which is what the server will use.
#
# <b>And not `/dev/tcp`, which was the second attempt.</b> That is a bash feature and the
# server image's shell is not bash, so the probe failed every time and the wait timed out
# against a database that was serving.
waited=0
while [ "$waited" -lt 90 ]; do
  if docker exec "$PG" pg_isready -q -h "$PG" -p 5432 -U gis 2>/dev/null; then
    break
  fi
  sleep 2
  waited=$((waited + 2))
done

if [ "$waited" -ge 90 ]; then
  echo "  the datastore never became ready"
  docker logs --tail 20 "$PG"
  exit 1
fi

printf '  ready after %ss\n' "$waited"

# <b>Proving the isolation before trusting it.</b> A test that assumes the network is cut and
# is wrong proves nothing, and this repository has written that mistake down enough times.
say "confirming there is no way out"
if docker run --rm --network "$NET" --entrypoint sh "$IMAGE" -c \
     'timeout 8 getent hosts nuget.org || timeout 8 getent hosts cdn.proj.org' >/dev/null 2>&1
then
  echo "  A NAME RESOLVED. This network is not isolated and the run below would prove nothing."
  exit 1
fi

echo "  nuget.org and cdn.proj.org do not resolve"

STORE="Host=$PG;Port=5432;Database=gis;Username=gis;Password=gis;Search Path=gisserver,public"

say "migrating with no route off the network"
docker run --rm --name "$APP-migrate" --network "$NET" \
  -e Graticula__PlatformStore="$STORE" \
  -e Graticula__SecretKey="$KEY" \
  "$IMAGE" migrate --apply 2>&1 | tail -4

say "serving"
docker run -d --name "$APP" --network "$NET" \
  -e Graticula__PlatformStore="$STORE" \
  -e Graticula__SecretKey="$KEY" \
  -e Graticula__HostName=airgap-server \
  "$IMAGE" >/dev/null

waited=0
while [ "$waited" -lt 60 ]; do
  # <b>`curl`, because the image has it and does not have `wget`.</b> The first version
  # probed with wget, got nothing every time, and reported a server that was listening as
  # NOT SERVING -- the log said `Listening on https://0.0.0.0:8443` while the verdict said
  # the opposite. A probe that cannot succeed is indistinguishable from a server that cannot
  # answer.
  if docker run --rm --network "$NET" --entrypoint sh "$IMAGE" -c \
       "curl -sk --max-time 5 https://$APP:8443/rest/info" >/dev/null 2>&1
  then
    break
  fi
  sleep 2
  waited=$((waited + 2))
done

say "verdict"

if [ "$waited" -ge 60 ]; then
  echo "NOT SERVING after ${waited}s on an isolated network. Its log:"
  docker logs --tail 30 "$APP"
  exit 1
fi

echo "answered after ${waited}s, with no route off the network:"
docker run --rm --network "$NET" --entrypoint sh "$IMAGE" -c \
  "curl -sk https://$APP:8443/rest/info" 2>/dev/null | head -c 220
echo

# <b>Started is not working, and the difference is the rest of this script.</b> A fresh
# store refuses everything until an administrator exists, so the answer above is a 503 that
# proves the listener and nothing else. Setting the server up and serving a real request is
# what makes this an air-gapped *deployment* rather than an air-gapped process.
say "setting it up, offline"

TOKEN=$(docker logs "$APP" 2>&1 | grep -A2 "One-time setup token" | tail -1 | tr -d " \r")

if [ -z "$TOKEN" ]; then
  echo "  no setup token in the log; cannot continue"
  exit 1
fi

printf '  token taken from the log: %s...\n' "$(echo "$TOKEN" | cut -c1-8)"

docker run --rm --network "$NET" --entrypoint sh "$IMAGE" -c \
  "curl -sk -X POST https://$APP:8443/rest/setup -H 'Content-Type: application/json' \
   -d '{\"token\":\"$TOKEN\",\"name\":\"airgap\",\"password\":\"airgap-rehearsal-pw\"}' \
   -w ' [%{http_code}]'" 2>/dev/null | head -c 200
echo

say "serving a real request, offline"
docker run --rm --network "$NET" --entrypoint sh "$IMAGE" -c \
  "curl -sk https://$APP:8443/rest/services -w ' [%{http_code}]'" 2>/dev/null | head -c 200
echo

# <b>The one thing Q-15 is really about.</b> A server that started may still have wanted
# something it could not reach and carried on quietly. Its own log is where that shows.
say "what it said about anything it could not reach"
complaints=$(docker logs "$APP" 2>&1 \
  | grep -iE "unreachable|could not|failed to (fetch|download|resolve)|telemetry|no such host" \
  | grep -viE "platform store is unreachable" | head -8)

if [ -z "$complaints" ]; then
  echo "  nothing. It wanted nothing it could not reach."
else
  echo "$complaints"
fi
