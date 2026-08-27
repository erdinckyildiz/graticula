#!/bin/sh
# ADR-016 condition 1, the undischarged half: a stale component against a newer store.
#
# The condition says this "needs two published image tags". It does not: what makes a
# component stale is the store's `minimum_reader_version` standing above the version the
# component was built for, and that can be arranged on a scratch schema.
set -u
cd "c:/Personal/Projects/GIS" || exit 1
S="C:/Users/Erdinc/AppData/Local/Temp/claude/c--Personal-Projects-GIS/9db7f59f-6624-459d-b69c-f9b00b1599e7/scratchpad"
SCHEMA=gisstale
PG="Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis;Search Path=$SCHEMA,public"

psql() {
  docker exec -e PGPASSWORD=gis gis-experiment-postgis psql -U gis -d gis -t -A -c "$1"
}

echo "=== a scratch store, migrated to what this build expects ==="
psql "drop schema if exists $SCHEMA cascade; create schema $SCHEMA;" >/dev/null
Graticula__PlatformStore="$PG" \
Graticula__SecretKey="3hYx5gzbV49eNInQ51/4FCwDCEnT3MgsxpIhue0o+8Y=" \
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
Graticula__SecretKey="3hYx5gzbV49eNInQ51/4FCwDCEnT3MgsxpIhue0o+8Y=" \
Graticula__Port=8451 \
  timeout 40 dotnet run --project src/Graticula.Host --no-build --no-launch-profile 2>&1 | head -12
echo "(exit $?)"

echo
echo "=== and the safe direction: the store ahead by expand only ==="
psql "update $SCHEMA.platform_schema set applied_version = 40, minimum_reader_version = 1;" >/dev/null
echo "stamp: $(psql "select applied_version || ' / min reader ' || minimum_reader_version from $SCHEMA.platform_schema;")"

Graticula__PlatformStore="$PG" \
Graticula__SecretKey="3hYx5gzbV49eNInQ51/4FCwDCEnT3MgsxpIhue0o+8Y=" \
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
