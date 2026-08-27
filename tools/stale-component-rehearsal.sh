#!/bin/sh
# ADR-016 condition 1, the undischarged half: a stale component against a newer store.
#
# The condition says this "needs two published image tags". It does not: what makes a
# component stale is the store's `minimum_reader_version` standing above the version the
# component was built for, and that can be arranged on a scratch schema.
set -u

# <b>The key and the connection string come from the environment, and that is not a style
# choice.</b> Both were literals in this file until 2026-08-27, when a pre-push scan found
# them: `Graticula:SecretKey` is the AES-256 key that seals every registered data source's
# credentials (ADR-032, layer 2), so a key in a public repository is a key nobody may ever use for
# anything real -- and the likeliest way that happens is somebody copying it out of a script
# like this one. See D-191 in docs/architecture-debt.md.
#
# Set them before running:
#   export GRATICULA_TEST_PG='Host=...;Port=...;Database=...;Username=...;Password=...'
#   export GRATICULA_SECRET_KEY="$(openssl rand -base64 32)"
: "${GRATICULA_TEST_PG:?set GRATICULA_TEST_PG to the platform store connection string}"
: "${GRATICULA_SECRET_KEY:?set GRATICULA_SECRET_KEY, e.g. from: openssl rand -base64 32}"
cd "c:/Personal/Projects/GIS" || exit 1
S="C:/Users/Erdinc/AppData/Local/Temp/claude/c--Personal-Projects-GIS/9db7f59f-6624-459d-b69c-f9b00b1599e7/scratchpad"
SCHEMA=gisstale
PG="$GRATICULA_TEST_PG;Search Path=$SCHEMA,public"

psql() {
  docker exec -e PGPASSWORD=gis gis-experiment-postgis psql -U gis -d gis -t -A -c "$1"
}

echo "=== a scratch store, migrated to what this build expects ==="
psql "drop schema if exists $SCHEMA cascade; create schema $SCHEMA;" >/dev/null
Graticula__PlatformStore="$PG" \
Graticula__SecretKey="$GRATICULA_SECRET_KEY" \
  dotnet run --project src/Graticula.Host --no-build --no-launch-profile -- migrate --apply 2>&1 | tail -3

echo
echo "stamp: $(psql "select applied_version || ' / min reader ' || minimum_reader_version from $SCHEMA.platform_schema;")"

echo
echo "=== the store moves ahead of this build, past a contract ==="
psql "update $SCHEMA.platform_schema set applied_version = 40, minimum_reader_version = 39;" >/dev/null
echo "stamp: $(psql "select applied_version || ' / min reader ' || minimum_reader_version from $SCHEMA.platform_schema;")"

echo
echo "--- what the server says when it is started against it ---"
Graticula__PlatformStore="$PG" \
Graticula__SecretKey="$GRATICULA_SECRET_KEY" \
Graticula__Port=8451 \
  timeout 40 dotnet run --project src/Graticula.Host --no-build --no-launch-profile 2>&1 | head -12
echo "(exit $?)"

echo
echo "=== and the safe direction: the store ahead by expand only ==="
psql "update $SCHEMA.platform_schema set applied_version = 40, minimum_reader_version = 1;" >/dev/null
echo "stamp: $(psql "select applied_version || ' / min reader ' || minimum_reader_version from $SCHEMA.platform_schema;")"

Graticula__PlatformStore="$PG" \
Graticula__SecretKey="$GRATICULA_SECRET_KEY" \
Graticula__Port=8451 \
  timeout 25 dotnet run --project src/Graticula.Host --no-build --no-launch-profile > "$S/stale-expand.log" 2>&1 &
sleep 18
printf "the server answers: "
curl -sk -o /dev/null -w "%{http_code}\n" --max-time 5 https://127.0.0.1:8451/rest/info || echo "down"
head -6 "$S/stale-expand.log"

pid=$(netstat -ano 2>/dev/null | grep -E ":8451 .*LISTENING" | awk '{print $5}' | head -1)
[ -n "${pid:-}" ] && taskkill //PID "$pid" //F >/dev/null 2>&1

echo
echo "=== cleaning up ==="
psql "drop schema if exists $SCHEMA cascade;" >/dev/null
echo "done"
