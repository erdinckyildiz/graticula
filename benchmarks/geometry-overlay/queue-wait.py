"""D-31: long work in a small body, so the ten-second queue wait can be reached."""
import io
import json
import math
import os
import sys
import time
import urllib.parse
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location(
    "concurrency",
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "concurrency.py"))
probe = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(probe)

UNION = "/rest/services/Utilities/Geometry/GeometryServer/union"


def blobs(count, vertices, radius=0.02):
    """Overlapping rings on a grid, which is what makes a union expensive."""
    side = int(math.sqrt(count)) + 1
    out = []

    for i in range(count):
        cx = (i % side) * radius * 1.4
        cy = (i // side) * radius * 1.4
        out.append({"rings": [probe.ring(cx, cy, radius, vertices, 0.05)],
                    "spatialReference": {"wkid": 4326}})

    return out


def union(count, vertices, timeout=180):
    body = urllib.parse.urlencode({
        "sr": "4326",
        "geometries": json.dumps(
            {"geometryType": "esriGeometryPolygon", "geometries": blobs(count, vertices)}),
        "f": "json",
    }).encode()

    t0 = time.perf_counter()

    request = urllib.request.Request(probe.BASE + UNION, data=body, headers={
        "Content-Type": "application/x-www-form-urlencoded",
        "Authorization": "Bearer " + probe.TOKEN,
    })

    try:
        with urllib.request.urlopen(request, context=probe.CTX, timeout=timeout) as r:
            return r.status, len(r.read()), (time.perf_counter() - t0) * 1000, len(body), b""
    except Exception as e:  # noqa: BLE001
        payload = e.read()[:200] if hasattr(e, "read") else str(e).encode()[:200]
        return getattr(e, "code", -1), 0, (time.perf_counter() - t0) * 1000, len(body), payload


if __name__ == "__main__":
    probe.TOKEN = probe.sign_in()

    from concurrent.futures import ThreadPoolExecutor

    count, vertices = 4000, 60

    code, size, ms, sent, head = union(count, vertices)
    print(f"### one union of {count} rings alone: {code} in {ms:.0f} ms, sent {sent/1e6:.1f} MB")
    sys.stdout.flush()

    # Two workers, so with work this long the fourth request must wait past the ten seconds the
    # service advertises as its queue wait.
    for n in (4, 6):
        with ThreadPoolExecutor(max_workers=n) as pool:
            out = list(pool.map(lambda _: union(count, vertices), range(n)))

        times = sorted(r[2] for r in out)
        codes = {}
        for r in out:
            codes[r[0]] = codes.get(r[0], 0) + 1

        print(f"  n={n}  median {times[len(times)//2]:8.0f} ms  worst {times[-1]:8.0f} ms  {codes}")

        for c, _, t, _, head in sorted(out, key=lambda r: r[2]):
            if c != 200:
                print(f"      refused after {t:.0f} ms: {head[:200]}")

        print(f"      workers: {probe.workers()}")
        sys.stdout.flush()
        time.sleep(3)
