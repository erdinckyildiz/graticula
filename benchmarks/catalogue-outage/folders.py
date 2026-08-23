"""A folder's own directory during an outage: does it still list what is in it."""
import io
import os
import ssl
import subprocess
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")

CTX = ssl._create_unverified_context()
BASE = os.environ.get("GRATICULA_TEST_URL", "https://127.0.0.1:8443")
PG = "gis-experiment-postgis"

PATHS = [
    ("root", "/rest/services?f=json"),
    ("turkiye", "/rest/services/turkiye?f=json"),
    ("hosted", "/rest/services/hosted?f=json"),
    ("no-such-folder", "/rest/services/nosuchfolder?f=json"),
]


def one(path, timeout=30):
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(BASE + path, context=CTX, timeout=timeout) as r:
            return r.status, r.read(), r.headers.get("X-Catalog-Age"), (time.perf_counter() - t0) * 1000
    except urllib.error.HTTPError as e:
        return (e.code, e.read(), e.headers.get("X-Catalog-Age") if e.headers else None,
                (time.perf_counter() - t0) * 1000)
    except Exception as e:  # noqa: BLE001
        return -1, str(e).encode(), None, (time.perf_counter() - t0) * 1000


def show(label):
    for name, path in PATHS:
        code, body, age, ms = one(path)
        text = " ".join(body[:400].decode("utf-8", "replace").split())
        print(f"  {label:6s} {name:15s} {code} {ms:7.0f} ms age={age} | {text[:220]}")
    sys.stdout.flush()


if __name__ == "__main__":
    print("### HEALTHY")
    show("up")

    print()
    print("### STOP", time.strftime("%H:%M:%S"))
    subprocess.run(["docker", "stop", PG], check=True, capture_output=True)
    t_down = time.time()

    # Let the breaker learn, the way any traffic would.
    one("/rest/services?f=json")

    while time.time() - t_down < 40:
        time.sleep(1)

    print(f"### DOWN, t+{time.time() - t_down:.0f}s")
    show("down")

    print()
    print("### 20 concurrent on a folder that has services, t+%.0fs" % (time.time() - t_down))
    with ThreadPoolExecutor(max_workers=20) as pool:
        out = list(pool.map(lambda _: one("/rest/services/turkiye?f=json"), range(20)))
    times = sorted(ms for _, _, _, ms in out)
    codes = {}
    for code, _, _, _ in out:
        codes[code] = codes.get(code, 0) + 1
    listed = sum(1 for _, body, _, _ in out if b"tr_ref" in body)
    print(f"  {sum(1 for t in times if t < 500)}/20 under 500 ms, median {times[10]:.0f} ms, "
          f"worst {times[-1]:.0f} ms, {codes}, {listed}/20 actually listed tr_ref")

    print()
    print("### START", time.strftime("%H:%M:%S"))
    subprocess.run(["docker", "start", PG], check=True, capture_output=True)
    time.sleep(20)
    print("### RECOVERED")
    show("up")
