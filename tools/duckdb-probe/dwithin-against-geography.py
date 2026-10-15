"""DWithin on a GeoParquet layer in degrees, against PostGIS's geography on the same features — ADR-066 condition 3.

Asks a server's layer for the features within each distance of a point, then fetches every feature in a box wider
than the circle and asks PostGIS `st_dwithin(…::geography)` the same question about those geometries, and prints how
the two sets differ. PostGIS is reached through a command that reads SQL on stdin and prints one value, given in
DWITHIN_PSQL (for the showcase, an ssh into its datastore container running `psql -A -t`).

    DWITHIN_PSQL="ssh host docker exec -i db psql -U gis -d gis -A -t" \\
        python dwithin-against-geography.py https://server/rest/services/x/FeatureServer/0 28.9784 41.0082 150 1000
"""
import json
import os
import shlex
import subprocess
import sys
import urllib.parse
import urllib.request

layer, lon, lat = sys.argv[1].rstrip("/"), float(sys.argv[2]), float(sys.argv[3])
distances = [float(d) for d in sys.argv[4:]] or [150.0, 1000.0]
psql = shlex.split(os.environ["DWITHIN_PSQL"])


def get(params):
    with urllib.request.urlopen(layer + "/query?" + urllib.parse.urlencode(params), timeout=120) as r:
        return json.loads(r.read())


for metres in distances:
    served = get({"geometry": f"{lon},{lat}", "geometryType": "esriGeometryPoint", "inSR": 4326,
                  "distance": metres, "units": "esriSRUnit_Meter", "spatialRel": "esriSpatialRelIntersects",
                  "returnIdsOnly": "true", "f": "json"})
    if "error" in served:
        sys.exit("server refused: " + served["error"].get("message", ""))
    ids = set(served["objectIds"] or [])

    # Every feature in a box comfortably wider than the circle, with its geometry, for PostGIS to judge.
    pad = metres / 70_000
    box = f"{lon - pad},{lat - pad},{lon + pad},{lat + pad}"
    candidates, offset = [], 0
    while True:
        page = get({"geometry": box, "geometryType": "esriGeometryEnvelope", "inSR": 4326, "outSR": 4326,
                    "outFields": "objectid", "resultOffset": offset, "resultRecordCount": 2000, "f": "geojson"})
        feats = page.get("features", [])
        if not feats:
            break
        candidates += [(f["properties"]["objectid"], json.dumps(f["geometry"])) for f in feats if f.get("geometry")]
        offset += len(feats)

    rows = ",\n".join(f"({oid}, '{g}')" for oid, g in candidates)
    sql = (f"select string_agg(id::text, ',' order by id) from (values {rows}) as t(id, g) "
           f"where st_dwithin(st_setsrid(st_geomfromgeojson(g), 4326)::geography, "
           f"st_setsrid(st_makepoint({lon}, {lat}), 4326)::geography, {metres});")
    out = subprocess.run(psql, input=sql, capture_output=True, text=True, check=True).stdout.strip()
    truth = {int(x) for x in out.split(",") if x}

    print(f"{metres:g} m: candidates {len(candidates)}, served {len(ids)}, PostGIS geography {len(truth)}, "
          f"served not in PostGIS {len(ids - truth)}, PostGIS not served {len(truth - ids)}")
