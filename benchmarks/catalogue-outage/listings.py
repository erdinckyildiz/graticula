"""D-127 first axis: what a listing costs, concurrently, 45 seconds into an outage."""
import io
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
PG = "gis-experiment-postgis"
CONCURRENCY = 20

LISTINGS = [
    ("REST-directory", "/rest/services?f=json"),
    ("WFS-caps", "/wfs?SERVICE=WFS&REQUEST=GetCapabilities&VERSION=2.0.0"),
    ("WMS-caps", "/wms?SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0"),
    ("OGC-collections", "/ogc/features/v1/collections?f=json"),
    ("Portal-search", "/sharing/rest/search?q=*&f=json"),
    ("token", "__TOKEN__"),
]


def one(path, timeout=30):
    t0 = time.perf_counter()
    try:
        if path == "__TOKEN__":
            body = urllib.parse.urlencode(
                {
                    "username": os.environ.get("GRATICULA_TEST_USER", "root"),
                    "password": os.environ["GRATICULA_TEST_PASSWORD"],
                    "f": "json",
                }).encode()
            with urllib.request.urlopen(
                    BASE + "/rest/generateToken", body, context=CTX, timeout=timeout) as r:
                return r.status, r.read(), r.headers.get("X-Catalog-Age"), (time.perf_counter() - t0) * 1000
        with urllib.request.urlopen(BASE + path, context=CTX, timeout=timeout) as r:
            return r.status, r.read(), r.headers.get("X-Catalog-Age"), (time.perf_counter() - t0) * 1000
    except urllib.error.HTTPError as e:
        return (e.code, e.read(), e.headers.get("X-Catalog-Age") if e.headers else None,
                (time.perf_counter() - t0) * 1000)
    except Exception as e:  # noqa: BLE001
        return -1, str(e).encode(), None, (time.perf_counter() - t0) * 1000


def burst(name, path):
    with ThreadPoolExecutor(max_workers=CONCURRENCY) as pool:
        out = list(pool.map(lambda _: one(path), range(CONCURRENCY)))

    times = sorted(ms for _, _, _, ms in out)
    codes = {}
    ages = set()
    for code, _, age, _ in out:
        codes[code] = codes.get(code, 0) + 1
        if age is not None:
            ages.add(age)

    instant = sum(1 for t in times if t < 500)
    print(f"  {name:16s} {instant:2d}/{CONCURRENCY} under 500 ms   "
          f"median {times[len(times) // 2]:7.0f} ms   worst {times[-1]:7.0f} ms   "
          f"{codes}   age={sorted(ages) or '-'}")
    sys.stdout.flush()
    return instant, times[len(times) // 2], codes


def brief(b, n=200):
    text = b[:600].decode("utf-8", "replace")
    return " ".join(text.split())[:n]


if __name__ == "__main__":
    print("### WARM (every listing read once, so there is something to remember)")
    for name, path in LISTINGS:
        code, body, age, ms = one(path)
        print(f"  {name:16s} {code} {ms:7.0f} ms")
        if code != 200:
            print(f"      {brief(body)}")
    sys.stdout.flush()

    print()
    print("### STOP", time.strftime("%H:%M:%S"))
    subprocess.run(["docker", "stop", PG], check=True, capture_output=True)
    t_down = time.time()

    # One request per face first, so the breaker learns the store is gone the way it would
    # in a real deployment rather than from the burst itself.
    print("### one request each, immediately after the stop")
    for name, path in LISTINGS:
        code, body, age, ms = one(path)
        print(f"  {name:16s} {code} {ms:7.0f} ms   age={age}")
    sys.stdout.flush()

    while time.time() - t_down < 45:
        time.sleep(1)

    print()
    print(f"### {CONCURRENCY} concurrent, at t+{time.time() - t_down:.0f}s into the outage")
    for name, path in LISTINGS:
        burst(name, path)

    print()
    print("### one body from each face while down")
    for name, path in LISTINGS:
        code, body, age, ms = one(path)
        print(f"  {name:16s} {code} {ms:6.0f} ms age={age} | {brief(body)}")
    sys.stdout.flush()

    print()
    print("### START", time.strftime("%H:%M:%S"))
    subprocess.run(["docker", "start", PG], check=True, capture_output=True)
    t_up = time.time()

    back = {}
    while time.time() - t_up < 120 and len(back) < len(LISTINGS):
        for name, path in LISTINGS:
            if name in back:
                continue
            code, body, age, ms = one(path)
            if code == 200 and age is None:
                back[name] = time.time() - t_up
        time.sleep(1)

    print("### first fresh 200 after the store returned")
    for name, _ in LISTINGS:
        v = back.get(name)
        print(f"  {name:16s} {'%.1fs' % v if v is not None else 'NOT RECOVERED in 120s'}")
