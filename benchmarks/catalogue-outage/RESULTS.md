# D-127 — what a listing costs while the catalogue is unreachable

**Run 2026-08-23.** PostgreSQL 17 in `gis-experiment-postgis`, one Graticula worker, the
development catalogue: eleven services across `hosted` and `turkiye`. The outage is
`docker stop` on the container — on this host that leaves the published port bound and
unanswered, so a connect attempt blackholes for about four seconds rather than being
refused, which is the case
[ADR-026](../../docs/adr/ADR-026-serving-through-a-platform-store-outage.md) is about and
the expensive one.

[D-127](../../docs/architecture-debt.md)'s first axis said there was **no degraded listing
capability at all**: `CatalogFallback` could resolve one named service and could not list,
so every face that begins by enumerating went down with the store. The
[second failure gate](../../docs/reviews/failure-gate-2.md) measured it — 45 seconds into
an outage, twenty concurrent requests, **0 of 20 served instantly, about four seconds
each**.

## What was measured

Twenty concurrent requests per face, at t+45 s into the outage, after one request per
face has already met the failure — so the breaker has learnt what a real deployment's
traffic would have taught it. `listings.py` and `folders.py` in this directory; both take
`GRATICULA_TEST_URL`, `GRATICULA_TEST_USER` and `GRATICULA_TEST_PASSWORD` from the
environment.

### Before — the listing has no memory

| Face | Under 500 ms | Median | Worst |
|---|---:|---:|---:|
| `/rest/services` | 0/20 | ~4,000 ms | — |
| WFS `GetCapabilities` | 0/20 | ~4,000 ms | — |
| WMS `GetCapabilities` | 0/20 | ~4,000 ms | — |
| OGC `/collections` | 0/20 | ~4,000 ms | — |
| Portal search | 0/20 | ~4,000 ms | — |

From the failure gate's own run. Every face pays its own blackholed connect, and the cost
of the outage grows with traffic instead of staying flat.

### After the listing itself is remembered, and before the projector was guarded

| Face | Under 500 ms | Median | Worst | Status |
|---|---:|---:|---:|---|
| `/rest/services` | 19/20 | 13 ms | 4,021 ms | 200 |
| WFS `GetCapabilities` | 20/20 | 14 ms | 25 ms | 200 |
| WMS `GetCapabilities` | **0/20** | **4,021 ms** | 4,024 ms | 200 |
| OGC `/collections` | **0/20** | **timed out at 30 s** | — | no answer |
| Portal search | 19/20 | 10 ms | 4,010 ms | 200 |

**This intermediate run is the reason the repair is in two parts, and it is why it is
recorded rather than skipped.** The listing was being served from memory in every row
above — and two faces still cost four seconds or never answered. Sequentially the same
faces answered in **6.0 s (WFS)** and **8.0 s (WMS)**, which is one and two blackholed
connects respectively.

What was left was not the listing. `EX_GeographicBoundingBox` is mandatory on a WMS 1.3.0
named layer and `LatLonBoundingBox` on a 1.1.1 one, and a layer not already in WGS 84
needs a round trip to get one — so a capabilities document makes **one projection call per
distinct spatial reference it lists**, and each call waited out a connect nothing would
answer. OGC Features projects per collection, which is why it never finished inside
thirty seconds.

### After — the listing is remembered and the projector is behind the breaker

| Face | Under 500 ms | Median | Worst | Age header |
|---|---:|---:|---:|---|
| `/rest/services` | 19/20 | 10 ms | 4,020 ms | yes |
| WFS `GetCapabilities` | 20/20 | 14 ms | 27 ms | yes |
| WMS `GetCapabilities` | 19/20 | 13 ms | 4,013 ms | yes |
| OGC `/collections` | 20/20 | 14 ms | 21 ms | yes |
| Portal search | 20/20 | 11 ms | 15 ms | yes |

The 4-second worst case is **one prober per cooling window**, which is the breaker
working: `SourceBreaker` lets a single caller through when its ten seconds expire and
refuses the rest from memory. It is not nineteen requests each paying their own connect,
which is what the first table is.

Sequentially, with the same store down: 14 ms, 19 ms, 5 ms, 15 ms, 3 ms.

### A folder's directory, which is what the row measured

`folders.py`, twenty concurrent on `/rest/services/turkiye` at t+44 s:

**20/20 under 500 ms, median 10 ms, worst 15 ms, and 20/20 actually listed the service.**
The document is byte-identical to the healthy one.

### Recovery

First **fresh** 200 — the `X-Catalog-Age` header gone, not merely a 200 — after
`docker start`:

| Face | Fresh again |
|---|---:|
| `/rest/services` | 10.8 s |
| WFS `GetCapabilities` | 10.9 s |
| WMS `GetCapabilities` | 10.9 s |
| OGC `/collections` | 10.9 s |
| Portal search | 10.9 s |
| `generateToken` | 1.2 s |

One cooling window, which is what it should be: the breaker will not re-probe a source
before its ten seconds are up, so a face that is being served from memory keeps being
served from memory until then. Nothing needed a restart.

## What this does not fix, measured in the same run

**`/rest/generateToken` costs about four seconds per request and refuses, twenty times out
of twenty.** That is unchanged by this work — it measured the same before it — and it is
correct to refuse: a token minted from a remembered password hash is exactly the stale
authorization `CatalogFallback` exists to prevent. What is not correct is paying a
blackholed connect for each one; that is [D-131](../../docs/architecture-debt.md)'s shape
on a path the breaker is not consulted from, and it is recorded rather than repaired here.

**A folder nothing knows about answers 200 with an empty directory while blind, where a
healthy server answers 404.** Deliberate: the register that says which folders exist
cannot be read, and a 404 would be a claim this server is in no position to make. The
same reasoning `FolderExistsAsync` already had for its own failure.

**A blind directory omits the system services and the image services.** Their catalogues
have no memory in front of them, so asking during an outage buys a four-second wait and an
exception. A directory missing `Utilities` is smaller than the truth; one that takes four
seconds to say so is worse for every client that browses it — the same argument the folder
register's own catch already made.
