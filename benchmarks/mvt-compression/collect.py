# Collects real MVT tiles from the showcase for the ADR-068 §9 benchmark.
# For each VectorTileServer: the tiles covering the centre of its full extent at a spread of
# zooms, plus the eight neighbours, so dense and sparse tiles are both in the corpus.
import json, math, os, ssl, sys, urllib.request

BASE = "https://demo.graticula.com/rest/services/"
SERVICES = sys.argv[1:] or []
OUT = os.path.join(os.path.dirname(__file__), "corpus")
os.makedirs(OUT, exist_ok=True)
ctx = ssl.create_default_context()


def get(url):
    req = urllib.request.Request(url, headers={"Accept-Encoding": "identity"})
    with urllib.request.urlopen(req, context=ctx, timeout=60) as r:
        return r.status, r.read()


R = 6378137.0
def tile_of(x, y, z):
    n = 2 ** z
    tx = int((x + math.pi * R) / (2 * math.pi * R) * n)
    ty = int((math.pi * R - y) / (2 * math.pi * R) * n)
    return max(0, min(n - 1, tx)), max(0, min(n - 1, ty))


kept = 0
for svc in SERVICES:
    status, body = get(BASE + svc + "/VectorTileServer?f=json")
    doc = json.loads(body)
    ext = doc.get("fullExtent") or doc.get("initialExtent")
    if not ext:
        print("no extent", svc); continue
    cx, cy = (ext["xmin"] + ext["xmax"]) / 2, (ext["ymin"] + ext["ymax"]) / 2
    width = max(ext["xmax"] - ext["xmin"], 1.0)
    # the zoom at which the extent is about one tile, then deeper
    z0 = max(0, min(22, int(math.log2(2 * math.pi * R / width))))
    for z in sorted({z0, z0 + 2, z0 + 4, z0 + 6, 14, 16}):
        if z > 18: continue
        tx, ty = tile_of(cx, cy, z)
        for dx in (-1, 0, 1):
            for dy in (-1, 0, 1):
                x, y = tx + dx, ty + dy
                if x < 0 or y < 0 or x >= 2 ** z or y >= 2 ** z: continue
                try:
                    status, tile = get(f"{BASE}{svc}/VectorTileServer/tile/{z}/{y}/{x}.pbf")
                except Exception as e:
                    print("fail", svc, z, x, y, e); continue
                if status != 200 or not tile: continue
                name = f"{svc.replace('/', '__')}__{z}_{x}_{y}.pbf"
                open(os.path.join(OUT, name), "wb").write(tile)
                kept += 1
    print(svc, "done")
print("kept", kept)
