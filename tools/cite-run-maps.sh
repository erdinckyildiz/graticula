#!/bin/sh
# OGC API Maps 1.0's executable test suite, built from source and run as a jar — ADR-175.
#
#   sh tools/cite-run-maps.sh <maps-root-url> <out-dir>
#
# OGC publishes no TEAM Engine image for ets-ogcapi-maps10 (Docker Hub `ogccite`, measured
# 2026-10-06), so cite-run.sh's REST call has nothing to call. The suite's own pom builds an
# all-in-one jar whose main class runs the TestNG suite against a properties file, and that is
# what this does: the same tests TEAM Engine would run, without TEAM Engine.
#
# Pinned to a commit rather than main, because a suite that moves under an unchanged server
# turns a baseline into a coin toss. Raising the pin is a decision, like raising a baseline.
set -eu

ROOT=${1:?the maps root, e.g. http://127.0.0.1:8445/ogc/maps/v1}
OUT=${2:?an output directory}
PIN=83d256077c8dc620c0945d84f44306cd6b7783a2
WORK=${RUNNER_TEMP:-/tmp}/ets-ogcapi-maps10

mkdir -p "$OUT"
rm -rf "$WORK"
git init -q "$WORK"
git -C "$WORK" fetch -q --depth 1 https://github.com/opengeospatial/ets-ogcapi-maps10 "$PIN"
git -C "$WORK" checkout -q FETCH_HEAD
( cd "$WORK" && mvn -q -B -DskipTests package )

cat > "$WORK/props.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE properties SYSTEM "http://java.sun.com/dtd/properties.dtd">
<properties version="1.0">
  <entry key="iut">$ROOT</entry>
  <entry key="noofcollections">3</entry>
  <entry key="tile_matrix_set">WebMercatorQuad</entry>
</properties>
EOF

java -jar "$WORK"/target/ets-ogcapi-maps10-*-aio.jar -o "$WORK/out" "$WORK/props.xml" > "$OUT/ogcapi-maps-1.0.log" 2>&1 || true
REPORT=$(find "$WORK/out" -name testng-results.xml | head -1)

if [ -z "$REPORT" ]; then
  echo "the suite wrote no testng-results.xml; its last words:" >&2
  tail -20 "$OUT/ogcapi-maps-1.0.log" >&2
  exit 1
fi

cp "$REPORT" "$OUT/ogcapi-maps-1.0.xml"
python tools/cite-count.py "$OUT/ogcapi-maps-1.0.xml"
