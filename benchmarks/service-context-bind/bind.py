"""ADR-007 condition 2 / A-015: what binding a service context costs.

A-015 is *per-service warm state is small — connections, schema, symbology, fonts, CRS —
making bind/unbind cheap*, and ADR-007 condition 2 says it must be measured before §4.3's
lazy binding is relied upon. Lazy binding's whole cost is the **cold** bind, so that is
what this times: `ServiceContexts.Lifetime` is thirty seconds, so a request after
thirty-one seconds of quiet rebuilds the context and the next one does not.

**The same request either way.** Nothing else about it changes, so the difference is the
bind.
"""

import json, ssl, statistics, sys, time, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

ROOT = "https://127.0.0.1:8447"
CONTEXT = ssl._create_unverified_context()
TTL = 30


def sign_in():
    request = urllib.request.Request(
        ROOT + "/rest/auth/login",
        data=json.dumps({"name": "ci", "password": "console-local-run-password"}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, context=CONTEXT, timeout=30) as answer:
        return json.loads(answer.read())["token"]


TOKEN = sign_in()


def get(path, timeout=60):
    request = urllib.request.Request(ROOT + path, headers={"Authorization": "Bearer " + TOKEN})
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, context=CONTEXT, timeout=timeout) as answer:
            body = answer.read()
            return (time.perf_counter() - started) * 1000, answer.status, len(body)
    except urllib.error.HTTPError as e:
        e.read()
        return (time.perf_counter() - started) * 1000, e.code, 0


def health():
    return json.loads(
        urllib.request.urlopen(
            urllib.request.Request(
                ROOT + "/admin/health", headers={"Authorization": "Bearer " + TOKEN}),
            context=CONTEXT, timeout=30).read())


def layers():
    document = json.loads(
        urllib.request.urlopen(
            urllib.request.Request(
                ROOT + "/admin/layers", headers={"Authorization": "Bearer " + TOKEN}),
            context=CONTEXT, timeout=30).read())

    return [(l["name"], l["url"]) for l in document["layers"] if l.get("url")]


LAYERS = layers()
print(f"{len(LAYERS)} layers, ServiceContexts.Lifetime = {TTL}s")
print()

cold, warm = [], []

for round_ in range(3):
    print(f"round {round_ + 1}: waiting {TTL + 1}s for every context to expire", flush=True)
    time.sleep(TTL + 1)

    for name, url in LAYERS:
        first, status, _ = get(url + "?f=json")

        if status != 200:
            print(f"  {name:26s} {status}, skipped")
            continue

        second, _, _ = get(url + "?f=json")
        third, _, _ = get(url + "?f=json")

        cold.append(first)
        warm.extend([second, third])

        print(f"  {name:26s} cold={first:8.1f}ms  warm={second:7.1f}ms {third:7.1f}ms  "
              f"bind={first - min(second, third):+8.1f}ms")

cold.sort()
warm.sort()

print()
print(f"cold  n={len(cold):3d}  p50={statistics.median(cold):8.1f}ms  "
      f"min={cold[0]:8.1f}ms  max={cold[-1]:8.1f}ms")
print(f"warm  n={len(warm):3d}  p50={statistics.median(warm):8.1f}ms  "
      f"min={warm[0]:8.1f}ms  max={warm[-1]:8.1f}ms")
print(f"bind cost at p50: {statistics.median(cold) - statistics.median(warm):+.1f}ms "
      f"({(statistics.median(cold) / statistics.median(warm) - 1) * 100:+.0f}%)")

print()
print("--- what the warm state weighs ---")

time.sleep(TTL + 1)
before = health()["runtime"]

for _, url in LAYERS:
    get(url + "?f=json")

after = health()["runtime"]

print(f"heap before touching every layer: {before['heapBytes']:,} bytes")
print(f"heap after:                       {after['heapBytes']:,} bytes")
print(f"difference:                       {after['heapBytes'] - before['heapBytes']:+,} bytes "
      f"over {len(LAYERS)} layers")
print("  (a heap reading is not an allocation count: the GC decides when to give memory")
print("   back, so this is an upper bound with noise in it rather than a measurement.)")
