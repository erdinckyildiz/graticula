"""D-58: what a rendered thumbnail costs, beside the sample the browser draws.

That row's trigger — *when there is a renderer* — has happened, and the reason the preview
was not swapped is that `preview.js` carries measured numbers and a render per row carries
none. So they are measured here, against the same server and the same layers.

The comparison is deliberately unfair in the preview's favour: the preview is one query the
browser draws, the export is a full server-side render. What matters is whether the render
is cheap enough to put on a list of forty.
"""
import json
import ssl
import statistics
import sys
import time
import urllib.error
import urllib.request

ssl._create_default_https_context = ssl._create_unverified_context
sys.stdout.reconfigure(encoding="utf-8")

BASE = "https://127.0.0.1:8444"
RUNS = 5


def fetch(url):
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(url, timeout=60) as answer:
            body = answer.read()
            return (time.perf_counter() - started) * 1000, len(body), answer.status
    except urllib.error.HTTPError as refused:
        return (time.perf_counter() - started) * 1000, 0, refused.code
    except Exception as failed:                                    # noqa: BLE001
        return (time.perf_counter() - started) * 1000, 0, str(failed)[:40]


with urllib.request.urlopen(f"{BASE}/rest/services?f=json", timeout=30) as answer:
    catalogue = json.load(answer)

services = [s["name"] for s in catalogue.get("services", [])]
print(f"services: {services}\n")

for name in services:
    extent = None
    try:
        with urllib.request.urlopen(
                f"{BASE}/rest/services/{name}/FeatureServer/0?f=json", timeout=30) as answer:
            layer = json.load(answer)
        e = layer.get("extent") or {}
        if all(k in e for k in ("xmin", "ymin", "xmax", "ymax")):
            extent = f'{e["xmin"]},{e["ymin"]},{e["xmax"]},{e["ymax"]}'
    except Exception as failed:                                    # noqa: BLE001
        print(f"  {name}: layer document unavailable ({str(failed)[:40]})")
        continue

    # <b>What the console draws today.</b> 800 features, geometry only, simplified to the
    # precision an eighty-pixel canvas can show — the numbers preview.js states.
    preview = (f"{BASE}/rest/services/{name}/FeatureServer/0/query"
               "?where=1%3D1&outFields=&returnGeometry=true&f=json"
               "&resultRecordCount=800&maxAllowableOffset=0.01")

    # <b>What a rendered thumbnail would cost.</b> The same eighty pixels, drawn by the
    # server, over the layer's own extent.
    export = (f"{BASE}/rest/services/{name}/MapServer/export"
              f"?bbox={extent}&size=80,80&format=png&transparent=false&f=image"
              if extent else None)

    for label, url in (("preview (query)", preview), ("thumbnail (export)", export)):
        if url is None:
            print(f"  {name:<12} {label:<20} no extent, skipped")
            continue

        samples = [fetch(url) for _ in range(RUNS)]
        statuses = {s[2] for s in samples}
        times = [s[0] for s in samples]
        size = statistics.median([s[1] for s in samples])

        print(f"  {name:<12} {label:<20} "
              f"median {statistics.median(times):7.1f} ms   {size/1024:7.1f} kB   {statuses}")
