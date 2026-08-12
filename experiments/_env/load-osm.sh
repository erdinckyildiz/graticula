#!/usr/bin/env bash
# Load an OpenStreetMap extract into the experiment PostGIS.
#
# Run from experiments/_env:
#   docker compose run --rm osm2pgsql /data/../load-osm.sh
# or, more usually, via the compose service which mounts this file.
#
# Idempotent in the sense that it drops and recreates the OSM tables. It does
# not touch anything else in the database.
set -euo pipefail

PBF="${PBF:-/data/turkey-latest.osm.pbf}"
PGHOST="${PGHOST:-postgis}"
PGUSER="${PGUSER:-gis}"
PGDATABASE="${PGDATABASE:-gis}"
export PGPASSWORD="${PGPASSWORD:-gis}"

if [ ! -f "$PBF" ]; then
  echo "extract not found: $PBF" >&2
  exit 1
fi

echo "=== source ==="
ls -lh "$PBF"

# --slim with --drop keeps peak memory bounded and discards the node cache
# afterwards. --hstore keeps tags queryable, which matters because a realistic
# attribute set is part of what we are measuring, not just geometry.
echo
echo "=== osm2pgsql ==="
osm2pgsql \
  --create \
  --slim --drop \
  --hstore \
  --cache 2000 \
  --number-processes 4 \
  --host "$PGHOST" \
  --username "$PGUSER" \
  --database "$PGDATABASE" \
  "$PBF"

echo
echo "=== indexes and statistics ==="
psql -h "$PGHOST" -U "$PGUSER" -d "$PGDATABASE" -v ON_ERROR_STOP=1 <<'SQL'
-- osm2pgsql creates GiST indexes on the geometry columns already.
-- ANALYZE matters: benchmark numbers against unanalysed tables measure the
-- planner giving up, not the code under test.
ANALYZE planet_osm_polygon;
ANALYZE planet_osm_point;
ANALYZE planet_osm_line;
SQL

echo
echo "=== what we got ==="
psql -h "$PGHOST" -U "$PGUSER" -d "$PGDATABASE" -v ON_ERROR_STOP=1 <<'SQL'
\pset border 2
SELECT 'polygon' AS layer,
       count(*)                             AS features,
       sum(ST_NPoints(way))                 AS vertices,
       round(avg(ST_NPoints(way))::numeric, 1) AS avg_vertices,
       max(ST_NPoints(way))                 AS max_vertices
FROM planet_osm_polygon
UNION ALL
SELECT 'point', count(*), sum(ST_NPoints(way)), round(avg(ST_NPoints(way))::numeric,1), max(ST_NPoints(way))
FROM planet_osm_point
UNION ALL
SELECT 'line', count(*), sum(ST_NPoints(way)), round(avg(ST_NPoints(way))::numeric,1), max(ST_NPoints(way))
FROM planet_osm_line;

SELECT 'srid' AS what, ST_SRID(way)::text AS value, count(*) AS n
FROM planet_osm_polygon GROUP BY 2 ORDER BY 3 DESC LIMIT 3;

-- The geometry/CRS reality pass (docs/geometry-crs-policy.md) says invalid
-- geometry is the most common real-world GIS problem. Here is our own dataset's
-- answer, on a sample, because a full validity scan on millions of rows is
-- itself a benchmark.
SELECT 'invalid_in_10k_sample' AS what, count(*) AS n
FROM (SELECT way FROM planet_osm_polygon LIMIT 10000) s
WHERE NOT ST_IsValid(way);
SQL

echo
echo "done."
