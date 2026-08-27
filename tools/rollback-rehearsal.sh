#!/bin/sh
# ADR-016 condition 2: a rollback is rehearsed, not assumed.
#
# Upgrade, roll back before contract, confirm the previous version serves correctly.
# The store is a copy of a real one with real layers in it, because "serves correctly"
# cannot be shown against an empty schema.
set -u
cd "c:/Personal/Projects/GIS" || exit 1
S="C:/Users/Erdinc/AppData/Local/Temp/claude/c--Personal-Projects-GIS/9db7f59f-6624-459d-b69c-f9b00b1599e7/scratchpad"
FROM=gisconsole
TO=gisrollback
PORT=8452
PG="Host=localhost;Port=55432;Database=gis;Username=gis;Password=gis;Search Path=$TO,public"
KEY="3hYx5gzbV49eNInQ51/4FCwDCEnT3MgsxpIhue0o+8Y="

psql() {
  docker exec -e PGPASSWORD=gis gis-experiment-postgis psql -U gis -d gis -t -A -c "$1"
}

stop() {
  pid=$(netstat -ano 2>/dev/null | grep -E ":$PORT .*LISTENING" | awk '{print $5}' | head -1)
  [ -n "${pid:-}" ] && taskkill //PID "$pid" //F >/dev/null 2>&1
  sleep 2
}

serve() {   # log-suffix
  Graticula__PlatformStore="$PG" Graticula__SecretKey="$KEY" Graticula__Port=$PORT \
    nohup dotnet run --project src/Graticula.Host --no-build --no-launch-profile \
    > "$S/rollback-$1.log" 2>&1 &
  sleep 16
}

echo "=== a copy of a real store, with its layers ==="
psql "drop schema if exists $TO cascade;" >/dev/null
docker exec -e PGPASSWORD=gis gis-experiment-postgis \
  pg_dump -U gis -d gis -n "$FROM" --no-owner --no-acl \
  | sed "s/\\b$FROM\\./$TO./g; s/CREATE SCHEMA $FROM;/CREATE SCHEMA $TO;/" \
  | docker exec -i -e PGPASSWORD=gis gis-experiment-postgis psql -U gis -d gis -q 2>&1 | tail -2

echo "layers in the copy: $(psql "select count(*) from $TO.layer;")"
echo "stamp: $(psql "select applied_version || ' / min reader ' || minimum_reader_version from $TO.platform_schema;")"

echo
echo "=== 1. this build serves the copy ==="
stop; serve before
printf "  /rest/info            "; curl -sk -o /dev/null -w "%{http_code}\n" --max-time 6 "https://127.0.0.1:$PORT/rest/info"
BEFORE=$(curl -sk --max-time 8 "https://127.0.0.1:$PORT/rest/services/hosted/ci_buildings/FeatureServer/0?f=json")
printf "  the layer document    %s bytes\n" "$(printf '%s' "$BEFORE" | wc -c)"
printf "  its name              %s\n" "$(printf '%s' "$BEFORE" | python -c "import sys,json;print(json.load(sys.stdin).get('name','-'))" 2>/dev/null)"
stop

echo
echo "=== 2. somebody upgrades the store past this build, expand only ==="
psql "update $TO.platform_schema set applied_version = 41, minimum_reader_version = 1;" >/dev/null
echo "  stamp: $(psql "select applied_version || ' / min reader ' || minimum_reader_version from $TO.platform_schema;")"

echo
echo "=== 3. the previous version is rolled back to, and has to serve ==="
serve after
grep -m1 "compatible\|is built for schema" "$S/rollback-after.log" | sed 's/^/  /'
printf "  /rest/info            "; curl -sk -o /dev/null -w "%{http_code}\n" --max-time 6 "https://127.0.0.1:$PORT/rest/info"
AFTER=$(curl -sk --max-time 8 "https://127.0.0.1:$PORT/rest/services/hosted/ci_buildings/FeatureServer/0?f=json")
printf "  the layer document    %s bytes\n" "$(printf '%s' "$AFTER" | wc -c)"

printf "  a query              "
curl -sk -o "$S/rollback-query.json" -w "%{http_code}" --max-time 12 \
  "https://127.0.0.1:$PORT/rest/services/hosted/ci_buildings/FeatureServer/0/query?where=1%3D1&outFields=*&f=json"
printf "  %s features\n" "$(python -c "
import json,io
try:
    d=json.load(io.open(r'$S/rollback-query.json',encoding='utf-8'))
    print(len(d.get('features',[])))
except Exception as e:
    print('-')
")"

echo
if [ "$BEFORE" = "$AFTER" ]; then
  echo "  the two documents are byte-identical"
else
  echo "  THE DOCUMENTS DIFFER"
  printf '%s' "$BEFORE" > "$S/rollback-before.json"
  printf '%s' "$AFTER" > "$S/rollback-after.json"
fi

echo
echo "=== 4. and a contract past it is refused ==="
stop
psql "update $TO.platform_schema set applied_version = 41, minimum_reader_version = 40;" >/dev/null
Graticula__PlatformStore="$PG" Graticula__SecretKey="$KEY" Graticula__Port=$PORT \
  timeout 40 dotnet run --project src/Graticula.Host --no-build --no-launch-profile 2>&1 \
  | grep -m1 "is built for schema" | sed 's/^/  /'

echo
echo "=== cleaning up ==="
stop
psql "drop schema if exists $TO cascade;" >/dev/null
echo "done"
