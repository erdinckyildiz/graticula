"""GetMap under load, with the server's own runtime counters either side.

ADR-041 condition 1 / A-076. ADR-004 deferred rendering on a measurement --
80.9% GC pause at 18% CPU on a lighter workload -- so this answers with one.
"""
import json, ssl, sys, time, urllib.request, urllib.parse
from concurrent.futures import ThreadPoolExecutor

CTX = ssl._create_unverified_context()
BASE = "https://127.0.0.1:8443"
USER, PASSWORD = "root", "change-me"

def token():
    body = urllib.parse.urlencode(
        {"username": USER, "password": PASSWORD, "f": "json"}).encode()
    with urllib.request.urlopen(BASE + "/rest/generateToken", body, context=CTX) as r:
        return json.load(r)["token"]

TOKEN = token()

def health():
    q = urllib.request.Request(BASE + "/admin/health",
                               headers={"Authorization": "Bearer " + TOKEN})
    with urllib.request.urlopen(q, context=CTX, timeout=60) as r:
        return json.load(r)["runtime"]

def get(url):
    start = time.perf_counter()
    with urllib.request.urlopen(BASE + url, context=CTX, timeout=300) as r:
        n = len(r.read())
    return (time.perf_counter() - start) * 1000, n

def run(url, count, workers, label):
    get(url)  # warm: the first call opens a connection and describes the layer
    before = health()
    t0 = time.perf_counter()
    with ThreadPoolExecutor(max_workers=workers) as pool:
        results = list(pool.map(lambda _: get(url), range(count)))
    wall = time.perf_counter() - t0
    after = health()

    times = sorted(r[0] for r in results)
    alloc = after["allocatedBytes"] - before["allocatedBytes"]
    pause = after["gcPauseMilliseconds"] - before["gcPauseMilliseconds"]
    cpu = after["cpuMilliseconds"] - before["cpuMilliseconds"]
    gen0 = after["gen0"] - before["gen0"]
    gen2 = after["gen2"] - before["gen2"]

    print(f"| {label} | {count} | {workers} | "
          f"{times[len(times)//2]:.0f} | {times[int(len(times)*0.95)]:.0f} | "
          f"{count/wall:.1f} | "
          f"{alloc/count/1_048_576:.1f} | {pause/(wall*1000)*100:.1f}% | "
          f"{cpu/(wall*1000)/after['cores']*100:.0f}% | {gen0}/{gen2} | "
          f"{results[0][1]/1024:.0f} |")

if __name__ == "__main__":
    print("| Map | Requests | Concurrency | p50 ms | p95 ms | req/s | "
          "MB alloc/req | GC pause | CPU | gen0/gen2 | KB |")
    print("|---|---|---|---|---|---|---|---|---|---|---|")
    for label, url, count, workers in json.load(open(sys.argv[1])):
        run(url, count, workers, label)
