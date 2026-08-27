#!/bin/sh
# Real OSM polygons in three vertex bands, as GeoJSON, for the geometry benchmark.
set -u
OUT="C:/Users/Erdinc/AppData/Local/Temp/claude/c--Personal-Projects-GIS/9db7f59f-6624-459d-b69c-f9b00b1599e7/scratchpad"

band() { # name low high count
  docker exec -e PGPASSWORD=gis gis-experiment-postgis psql -U gis -d gis -t -A -c \
    "select st_asgeojson(way) from public.planet_osm_polygon
      where st_npoints(way) between $2 and $3 and geometrytype(way) = 'POLYGON'
      limit $4;" > "$OUT/geom-$1.jsonl"
  printf "%-8s %s shapes\n" "$1" "$(grep -c . "$OUT/geom-$1.jsonl")"
}

band small  5    20   60
band medium 200  400  60
band large  2000 6000 60
