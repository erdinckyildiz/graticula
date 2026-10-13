"""A drawn map of a dense layer at full extent: how long, for the pages it reads in.

Usage: python drawing-pages.py <base> <user> <password> <bench-connection-string> [runs]

Registers the connection as a data source, publishes its `public.polygons` (927,350 Istanbul polygons on the local
bench), clears the layer's visible range so the whole of it is in view, and times MapServer export at the layer's
full extent, 1600 x 1000. Leaves the source and layer in place for a second build to be timed against; pass
--clean to remove them.
"""
import json, ssl, sys, time, urllib.parse, urllib.request

BASE, USER, PASSWORD, CONN = sys.argv[1:5]
RUNS = int(sys.argv[5]) if len(sys.argv) > 5 and sys.argv[5].isdigit() else 3
CLEAN = "--clean" in sys.argv
CTX = ssl.create_default_context(); CTX.check_hostname = False; CTX.verify_mode = ssl.CERT_NONE


def call(method, path, token=None, body=None):
    data = None if body is None else json.dumps(body).encode()
    r = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None: r.add_header("Content-Type", "application/json")
    if token: r.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(r, context=CTX, timeout=600) as resp:
            return resp.status, resp.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()


st, body = call("POST", "/rest/auth/login", body={"name": USER, "password": PASSWORD})
TOKEN = json.loads(body)["token"]

st, body = call("GET", "/admin/datasources", TOKEN)
source = next((s["id"] for s in json.loads(body)["dataSources"] if s["name"] == "zz_bench"), None)
if CLEAN:
    call("DELETE", "/admin/layers/zz_bench_polygons", TOKEN)
    call("DELETE", "/admin/featureservices/zz_bench_polygons", TOKEN)
    if source: call("DELETE", f"/admin/datasources/{source}", TOKEN)
    print("cleaned"); sys.exit(0)
if source is None:
    st, body = call("POST", "/admin/datasources", TOKEN, {"name": "zz_bench", "connectionString": CONN})
    assert st in (200, 201), body
    source = json.loads(body)["id"]

st, _ = call("GET", "/rest/services/zz_bench_polygons/MapServer?f=json", TOKEN)
if st != 200:
    st, body = call("POST", "/admin/layers", TOKEN, {
        "name": "zz_bench_polygons", "dataSourceId": source, "schemaName": "public", "tableName": "polygons",
        "geometryColumn": "geom", "identityColumn": "fid", "objectIdColumn": "fid", "srid": 3857,
        "geometryType": "MultiPolygon", "sharing": "public"})
    assert st in (200, 201), body
call("PUT", "/admin/layers/zz_bench_polygons/visible-range", TOKEN, {"minScale": 0, "maxScale": 0})

st, body = call("GET", "/rest/services/zz_bench_polygons/MapServer?f=json", TOKEN)
ext = json.loads(body)["fullExtent"]
bbox = f"{ext['xmin']},{ext['ymin']},{ext['xmax']},{ext['ymax']}"
q = urllib.parse.urlencode({"bbox": bbox, "bboxSR": 3857, "imageSR": 3857, "size": "1600,1000",
                            "format": "png", "transparent": "true", "f": "image"})
for i in range(RUNS):
    t = time.monotonic()
    st, img = call("GET", f"/rest/services/zz_bench_polygons/MapServer/export?{q}", TOKEN)
    print(f"run {i + 1}: {st}, {len(img):,} bytes, {time.monotonic() - t:.2f} s")
