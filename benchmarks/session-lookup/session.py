"""ADR-015 condition 1: what a session lookup costs, per request.

Implemented as one indexed read against the platform store per authenticated request,
with no cache. The condition asks whether that is material at ADR-007's concurrency —
because if it is, the cache TTL that would fix it becomes a stated revocation delay
rather than an implementation detail.

**Measured as a difference, not as a number.** The lookup cannot be timed alone from
outside, so this compares the same request answered with a credential and without one on
a route that is public — everything else about the two is identical, and what is left is
the session read.
"""

import concurrent.futures as futures
import json, ssl, statistics, sys, time, urllib.request

sys.stdout.reconfigure(encoding="utf-8")

ROOT = "https://127.0.0.1:8447"
CONTEXT = ssl._create_unverified_context()


def sign_in():
    request = urllib.request.Request(
        ROOT + "/rest/auth/login",
        data=json.dumps({"name": "ci", "password": "console-local-run-password"}).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(request, context=CONTEXT, timeout=30) as answer:
        return json.loads(answer.read())["token"]


TOKEN = sign_in()


def get(path, token, timeout=60):
    headers = {"Authorization": "Bearer " + token} if token else {}
    request = urllib.request.Request(ROOT + path, headers=headers)
    started = time.perf_counter()
    try:
        with urllib.request.urlopen(request, context=CONTEXT, timeout=timeout) as answer:
            answer.read()
            return (time.perf_counter() - started) * 1000, answer.status
    except urllib.error.HTTPError as e:
        e.read()
        return (time.perf_counter() - started) * 1000, e.code


def sample(path, token, rounds):
    for _ in range(20):                       # warm the pool and the plan cache
        get(path, token)

    times = [get(path, token)[0] for _ in range(rounds)]
    times.sort()
    return times


def report(label, times):
    print(f"{label:44s} n={len(times):4d}  "
          f"p50={statistics.median(times):7.2f}ms  "
          f"p95={times[int(len(times) * 0.95)]:7.2f}ms  "
          f"min={times[0]:7.2f}ms  max={times[-1]:7.2f}ms")


# A public route, so the same request is legal with and without a credential and the
# only difference is whether a session is looked up.
PATH = "/rest/services/hosted/ci_buildings/FeatureServer/0?f=json"

print("--- one request, serially, 400 samples each ---")
anonymous = sample(PATH, None, 400)
authenticated = sample(PATH, TOKEN, 400)

report("anonymous (no session read)", anonymous)
report("authenticated (one session read)", authenticated)

difference = statistics.median(authenticated) - statistics.median(anonymous)
print(f"{'':44s}difference at p50: {difference:+7.2f}ms "
      f"({difference / statistics.median(anonymous) * 100:+.1f}%)")

print()
print("--- the same, under concurrency ---")

for callers in (1, 8, 24, 48):
    def run(token):
        def one(_):
            return get(PATH, token)[0]

        with futures.ThreadPoolExecutor(max_workers=callers) as pool:
            started = time.perf_counter()
            times = sorted(pool.map(one, range(callers * 25)))
            span = time.perf_counter() - started

        return times, len(times) / span

    without, without_rate = run(None)
    with_, with_rate = run(TOKEN)

    print(f"{callers:3d} callers  "
          f"anonymous p50={statistics.median(without):7.2f}ms {without_rate:6.0f} req/s   "
          f"authenticated p50={statistics.median(with_):7.2f}ms {with_rate:6.0f} req/s   "
          f"difference {statistics.median(with_) - statistics.median(without):+6.2f}ms")

print()
print("--- and the store's own view: how long the indexed read takes ---")
