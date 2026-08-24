"""D-31: overlay with the worker pool busy, rather than one request on an idle machine."""
import io
import json
import math
import os
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

CTX = ssl._create_unverified_context()
BASE = os.environ.get("GRATICULA_TEST_URL", "https://127.0.0.1:8443")
PATH = "/rest/services/Utilities/Geometry/GeometryServer/intersect"

TOKEN = None


def sign_in():
    """The geometry service is not visible anonymously, so the probe signs in like a client."""
    body = urllib.parse.urlencode({
        "username": os.environ.get("GRATICULA_TEST_USER", "root"),
        "password": os.environ["GRATICULA_TEST_PASSWORD"],
        "f": "json",
    }).encode()

    with urllib.request.urlopen(BASE + "/rest/generateToken", body, context=CTX, timeout=30) as r:
        return json.loads(r.read())["token"]


def ring(cx, cy, r, n, wobble):
    """A closed ring with n vertices, wobbled so the two operands interact on every edge."""
    pts = []
    for i in range(n):
        a = 2 * math.pi * i / n
        rr = r * (1.0 + wobble * math.sin(a * 37.0))
        pts.append([round(cx + rr * math.cos(a), 6), round(cy + rr * math.sin(a), 6)])
    # <b>Clockwise, which is what ArcGIS reads as an outer ring.</b> Built counter-clockwise
    # above because the angle increases, so it is reversed here rather than counted backwards.
    pts.reverse()
    pts.append(pts[0])
    return pts


def payload(vertices):
    left = {"rings": [ring(0.0, 0.0, 1.0, vertices, 0.02)], "spatialReference": {"wkid": 4326}}
    right = {"rings": [ring(0.05, 0.0, 1.0, vertices, 0.02)], "spatialReference": {"wkid": 4326}}

    return urllib.parse.urlencode({
        "sr": "4326",
        "geometries": json.dumps({"geometryType": "esriGeometryPolygon", "geometries": [left]}),
        "geometry": json.dumps(right),
        "f": "json",
    }).encode()


def one(body, timeout=120):
    t0 = time.perf_counter()
    try:
        request = urllib.request.Request(BASE + PATH, data=body, headers={
            "Content-Type": "application/x-www-form-urlencoded",
            "Authorization": "Bearer " + TOKEN,
        })
        with urllib.request.urlopen(request, context=CTX, timeout=timeout) as r:
            payload = r.read()
            return r.status, len(payload), (time.perf_counter() - t0) * 1000, payload[:200]
    except urllib.error.HTTPError as e:
        return e.code, 0, (time.perf_counter() - t0) * 1000, e.read()[:200]
    except Exception as e:  # noqa: BLE001
        return -1, 0, (time.perf_counter() - t0) * 1000, str(e).encode()[:200]


def workers():
    """The overlay worker processes, and what they are holding."""
    out = subprocess.run(
        ["powershell", "-NoProfile", "-Command",
         "Get-Process -Name Graticula.Overlay.Worker -ErrorAction SilentlyContinue | "
         "ForEach-Object { '{0} {1}' -f $_.Id, [math]::Round($_.WorkingSet64/1MB) }"],
        capture_output=True, text=True)

    return [l.strip() for l in out.stdout.splitlines() if l.strip()]


def burst(label, body, n):
    before = workers()

    with ThreadPoolExecutor(max_workers=n) as pool:
        out = list(pool.map(lambda _: one(body), range(n)))

    during = workers()

    times = sorted(ms for _, _, ms, _ in out)
    codes = {}
    for code, _, _, _ in out:
        codes[code] = codes.get(code, 0) + 1

    print(f"  {label:22s} n={n:2d}  median {times[len(times)//2]:8.0f} ms   "
          f"worst {times[-1]:8.0f} ms   {codes}")
    print(f"      workers before: {before or 'none'}")
    print(f"      workers after:  {during or 'none'}")
    sys.stdout.flush()

    return times, codes, out


if __name__ == "__main__":
    TOKEN = sign_in()

    print("### one request on an idle server, to find a size worth measuring")

    for vertices in (60_000, 150_000, 300_000):
        body = payload(vertices)
        code, size, ms, head = one(body)
        print(f"  {vertices:6d} vertices  {code}  {ms:8.0f} ms  {size} bytes  {head[:90]}")
        sys.stdout.flush()

    print()
    print("### the pool bounds two, so 1, 2, 4 and 8 at once")

    body = payload(int(os.environ.get("D31_VERTICES", "20000")))

    for n in (1, 2, 4, 8):
        burst("intersect", body, n)
        time.sleep(2)

    print()
    print("### after the load, are the workers still there and what do they hold")

    for wait in (0, 5, 15, 30):
        if wait:
            time.sleep(wait)
        print(f"  t+{wait:2d}s  {workers() or 'none'}")
