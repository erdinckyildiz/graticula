"""
What the slowest legitimate request on this server actually costs.

D-08 / performance gate F3: the statement timeout is 30 seconds because a number was
needed, and Q-04 wants a measured one. This drives every read face at the sizes a real
client asks for, and reports the distribution -- so the ceiling can be chosen against
numbers rather than against a guess.

Sequential and warmed, because the question is *how long does one honest request take*,
not *what happens under load*. Load is ADR-046's question and it has its own measurement.
"""

import json
import ssl
import statistics
import sys
import time
import urllib.request

ssl._create_default_https_context = ssl._create_unverified_context
sys.stdout.reconfigure(encoding="utf-8")

BASE = "https://127.0.0.1:8443"
RUNS = 5


def once(path):
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(BASE + path, timeout=120) as answer:
            size = len(answer.read())
            return time.perf_counter() - started, size, answer.status
    except Exception as broken:
        return time.perf_counter() - started, 0, getattr(broken, "code", -1)


def measure(label, path):
    # One warm-up outside the median, so a cold plan is not reported as the cost.
    once(path)

    seen = [once(path) for _ in range(RUNS)]
    times = sorted(t for t, _, _ in seen)
    size = seen[-1][1]
    status = seen[-1][2]

    return {
        "label": label,
        "median_ms": statistics.median(times) * 1000,
        "worst_ms": max(times) * 1000,
        "bytes": size,
        "status": status,
    }


CASES = [
    # The ArcGIS face, at the record counts a client actually sends.
    ("ArcGIS query, 1,000 lines, geometry",
     "/rest/services/hosted/tr_yol/FeatureServer/0/query"
     "?where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=1000&f=json"),
    ("ArcGIS query, 1,000 polygons, geometry",
     "/rest/services/hosted/tr_ilce/FeatureServer/0/query"
     "?where=1%3D1&outFields=*&returnGeometry=true&resultRecordCount=1000&f=json"),
    ("ArcGIS count over 46,041",
     "/rest/services/hosted/tr_yol/FeatureServer/0/query"
     "?where=1%3D1&returnCountOnly=true&f=json"),
    ("ArcGIS extent over 46,041",
     "/rest/services/hosted/tr_yol/FeatureServer/0/query"
     "?where=1%3D1&returnExtentOnly=true&f=json"),

    # WFS, which pays for the count as well as the page (F3).
    ("WFS GetFeature, 1,000 lines",
     "/wfs?service=WFS&version=2.0.0&request=GetFeature"
     "&typeNames=graticula:tr_yol&count=1000"),
    ("WFS GetFeature, whole layer",
     "/wfs?service=WFS&version=2.0.0&request=GetFeature"
     "&typeNames=graticula:tr_ilce&count=25280"),

    # OGC API Features.
    ("OGC items, 1,000 lines",
     "/ogc/features/v1/collections/tr_yol/items?limit=1000&f=json"),

    # The rendering faces, at the sizes a print or a screen asks for.
    ("WMS GetMap 1024x1024, 46,041 lines",
     "/wms?service=WMS&version=1.3.0&request=GetMap&layers=tr_yol&styles="
     "&crs=EPSG:4326&bbox=35,25,42,45&width=1024&height=1024&format=image/png"),
    ("WMS GetMap 4096x4096, 46,041 lines",
     "/wms?service=WMS&version=1.3.0&request=GetMap&layers=tr_yol&styles="
     "&crs=EPSG:4326&bbox=35,25,42,45&width=4096&height=4096&format=image/png"),
    ("MapServer export 4000x3000, 25,280 polygons",
     "/rest/services/hosted/tr_ilce/MapServer/export"
     "?bbox=25,35,45,42&size=4000,3000&format=png&f=image"),

    # A cold vector tile at a zoom that actually holds something.
    ("VectorTile z8 cold-ish",
     "/rest/services/hosted/tr_yol/VectorTileServer/tile/8/95/146.pbf"),
]


def main():
    print(f"{'case':46s} {'median':>10s} {'worst':>10s} {'bytes':>10s}  status")
    print("-" * 92)

    worst = None

    for label, path in CASES:
        got = measure(label, path)

        print(f"{got['label'][:46]:46s} {got['median_ms']:9.1f}ms {got['worst_ms']:9.1f}ms "
              f"{got['bytes']:10d}  {got['status']}")

        if got["status"] == 200 and (worst is None or got["worst_ms"] > worst[1]):
            worst = (got["label"], got["worst_ms"])

    print()

    if worst:
        seconds = worst[1] / 1000
        print(f"slowest legitimate request measured: {worst[0]} at {seconds:.2f} s")
        print(f"the 30 s statement timeout is {30 / seconds:.0f}x that")


if __name__ == "__main__":
    main()
