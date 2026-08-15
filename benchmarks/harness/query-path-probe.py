"""A black-box probe of the feature query path, and its own ceiling.

Written for the §66 performance gate, 2026-08-15, because runs 1-4 measured
tiles and overlay and the word "query" appears in neither results document.

It is a probe and not a benchmark, and the difference matters. It measures from
outside the process, so it cannot distinguish an allocation ceiling from a
connection pool limit from a contended host. What it can say is whether
throughput scales, and the answer on this machine was: plateaus at 5-7x for 24x
concurrency, with run-to-run variance up to 2.2x at one concurrency.

READ THIS BEFORE BELIEVING A RESULT FROM IT. The first run of this probe
reported the 1,000-feature case regressing past concurrency 8. It was pushing
71-77 MB/s, and a control run against a file-served glyph range put this
client's own ceiling at 66 MB/s. The regression was the client. That is the
third time a harness in this project has been wrong, and the first one that
looked entirely plausible — so: every load result gets a control run against a
path the server barely touches, at the same concurrency and payload size,
before it is believed.
"""

import http.client
import ssl
import statistics
import sys
import threading
import time

HOST, PORT = "127.0.0.1", 8443
ctx = ssl._create_unverified_context()

CASES = {
    "1 feature":      "/rest/services/buildings/FeatureServer/0/query?where=1%3D1&resultRecordCount=1&outFields=*&f=json",
    "100 features":   "/rest/services/buildings/FeatureServer/0/query?where=1%3D1&resultRecordCount=100&outFields=*&f=json",
    "1000 features":  "/rest/services/buildings/FeatureServer/0/query?where=1%3D1&resultRecordCount=1000&outFields=*&f=json",
    "count only":     "/rest/services/buildings/FeatureServer/0/query?where=1%3D1&returnCountOnly=true&f=json",
}


def worker(path, stop, latencies, bytes_seen, lock):
    c = http.client.HTTPSConnection(HOST, PORT, context=ctx, timeout=60)
    c.connect()
    local, total = [], 0
    try:
        while not stop.is_set():
            t = time.perf_counter()
            c.request("GET", path)
            r = c.getresponse()
            body = r.read()
            local.append((time.perf_counter() - t) * 1000)
            total += len(body)
    except Exception:
        pass
    finally:
        c.close()
        with lock:
            latencies.extend(local)
            bytes_seen[0] += total


def measure(path, concurrency, seconds=4.0):
    stop = threading.Event()
    latencies, seen, lock = [], [0], threading.Lock()

    threads = [threading.Thread(target=worker, args=(path, stop, latencies, seen, lock))
               for _ in range(concurrency)]

    start = time.perf_counter()
    for t in threads:
        t.start()
    time.sleep(seconds)
    stop.set()
    for t in threads:
        t.join()
    elapsed = time.perf_counter() - start

    if not latencies:
        return None

    latencies.sort()
    return {
        "rps": len(latencies) / elapsed,
        "p50": statistics.median(latencies),
        "p99": latencies[min(len(latencies) - 1, int(len(latencies) * 0.99))],
        "n": len(latencies),
        "kb": seen[0] / len(latencies) / 1024,
    }


if __name__ == "__main__":
    only = sys.argv[1] if len(sys.argv) > 1 else None

    for name, path in CASES.items():
        if only and only not in name:
            continue

        print(f"\n{name}")
        print(f"  {'conc':>4}  {'req/s':>8}  {'scale':>6}  {'p50 ms':>7}  {'p99 ms':>7}  {'KB':>6}")

        base = None
        for c in (1, 2, 4, 8, 16):
            m = measure(path, c)
            if m is None:
                print(f"  {c:>4}  failed")
                continue
            if base is None:
                base = m["rps"]
            print(f"  {c:>4}  {m['rps']:>8.1f}  {m['rps'] / base:>5.2f}x  "
                  f"{m['p50']:>7.1f}  {m['p99']:>7.1f}  {m['kb']:>6.1f}")
