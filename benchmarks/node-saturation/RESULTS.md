# Where one node saturates — Graticula's process or PostgreSQL's

Measured 2026-10-22 for [ADR-012](../../docs/adr/ADR-012-clustering.md) condition 1, after the owner set the purpose
of a second node as sharing the work: *a second node adds throughput only where the node, not the database, is what
fills first.* Disposable rig; the numbers are the record.

## Setup

- One PC: i7-11800H, 8 cores / 16 logical. **PostgreSQL pinned to logical 0–5, Graticula to 6–11, the generator to
  12–15** (`ProcessorAffinity`), so each side has three physical cores and neither borrows the other's. A laptop with
  other applications open: the absolute numbers are this machine's; the ratios are the finding.
- The local fixture (18540, bound role, schema 104), logging at `Warning` (see benchmark-rig-logging-cap).
- Data: 840,596 OSM buildings of Türkiye, copied from `bench.public.polygons` into the datastore without the 86,754
  administrative boundaries (one polygon is the whole country and meets every box), published as `hosted/bench_hosted`.
- Each request is aimed at one of 5,000 random building centroids:
  - **query** — buildings in a 500 m box, `outFields=*`, at most 200, JSON;
  - **tile** — a vector tile at zoom 15–18 containing the point, shifted up to three tiles each way, so a tile almost
    never repeats and the cache is written but rarely read;
  - **export** — a 512×512 PNG of a 0.6–4 km box;
  - **mix** — two queries, three tiles and one export in six.
- `satload` (this folder): 3 s warm-up, then 20 s per concurrency; node CPU from `Graticula.Host`'s processor time,
  PostgreSQL's as the increases of every `postgres` process sampled twice a second.

## Results

| Workload | Peak req/s | Graticula cores at peak (of 6) | PostgreSQL cores at peak (of 6) | Fills first |
|---|---|---|---|---|
| control `/healthz/live` | 77,944 | 5.80 | 0.67 | — the rig is not the ceiling |
| query | 1,389 | 3.65 | **5.64** | **PostgreSQL** |
| tile (uncached) | 1,851 | 1.84 | **5.77** | **PostgreSQL** |
| export (map image) | 143 | **5.80** | 2.96 | **Graticula** |
| mix | 659 | **5.48** | 4.56 | **Graticula**, PostgreSQL close |

CPU per request at the peak:

| Workload | Graticula ms | PostgreSQL ms |
|---|---|---|
| query | 2.6 | 4.1 |
| tile | 1.0 | 3.1 |
| export | 41 | 22 |
| mix | 8.4 | 7.0 |

No request failed at any level. The full tables are in `measured-2026-10-22.txt`.

## What it says

1. **Feature queries and uncached vector tiles are the database's work.** A tile is made inside PostgreSQL; the node
   passes it on. A second Graticula node adds nothing to either until the store grows — more cores or a read replica,
   ADR-012's revisit trigger. Repeated tiles come from the tile cache and never reach the store, which this run
   deliberately avoided.
2. **Map images are the node's work**, at 41 ms of node CPU each against 22 of the store's: a second node nearly
   doubles them. Image services and the geometry service are expected to be the same and were not measured.
3. **In the mix both fill together**, the node needing about 1.2 times the store's CPU: nodes and store have to grow
   together, roughly in that ratio.

## Not measured

The showcase or any server-class machine; the image service, the geometry service and exports to file; a cached tile
workload; PostgreSQL's connections at the peak (the budget bounded them, nothing was refused).

## Re-measured 2026-10-31 — with connections, on a Release build

Measured again for the owner's *"Onayla, ölç ve kur"*, closing what the first run left out: **PostgreSQL's
connections at the peak**, a **Release** build, and CPU counted for **this fixture's processes only** — the first run
summed every `postgres` and every `Graticula.Host` on the machine, and other sessions' fixtures run on it. Own
PostgreSQL 16 cluster (`max_connections` 300), the same 840,596 buildings published as `hosted/bench_store`, the same
pinning (PostgreSQL 0–5, the node 6–11, the generator 12–15), logging at `Warning`. Beside each run an authenticated
`/rest/whoami` every two seconds and `pg_stat_activity` sampled for the database. Full tables:
`measured-2026-10-31.txt`.

| Workload | Peak req/s | Graticula cores (of 6) | PostgreSQL cores (of 6) | Fills first | Node ms / PG ms per request |
|---|---|---|---|---|---|
| control `/healthz/live` | 62,799 | 5.58 | 0.73 (the request log's `COPY`) | — | — |
| query | 1,351 | 3.22 | **5.35** | **PostgreSQL** | 2.4 / 4.0 |
| tile (uncached) | 1,239 | 1.72 | **4.85** | **PostgreSQL** | 1.4 / 3.9 |
| export (map image) | 97 | 4.30 | 2.88 | **neither — the per-source admission bound** | 44 / 30 |
| mix | 377 | 3.40 | 3.22 | **neither — the per-source admission bound** | 9.0 / 8.5 |

- **Connections at the peak: 53–58** backends for the database, 22–35 of them active at once — the budget's 24 per
  source and 24 for the platform store, well under the 297 usable. **No breaker opened** (0 lines in the node's log).
  **The authenticated whoami answered 200 throughout**, slowest 770 ms under the query peak.
- **What is new: the map image and the mix are held by admission before either CPU fills.** At 128 callers both
  refuse with 503 — 116 and 324 refusals, every one `ConnectionBudgetFullException` (*24 in flight against that
  data source and 96 more waiting*). A map image holds its source's permit while it draws, so at one node 24 images
  in flight is the ceiling, at 4.3 of 6 node cores. **A second node doubles that bound across the site (48 against
  one PostgreSQL)**, which is where a second node's gain on map images comes from — and what the store then has to
  hold, since `Graticula:PerSourceConcurrency` is per node.
- The machine was shared: about 22 % of its CPU was other sessions' at the start, and they are on every core. The
  ratios are the finding, as before.

**So, plainly: for feature queries and uncached vector tiles the node is not the limit** — PostgreSQL fills first, by
about 1.7 to 2.8 times the node's CPU per request, and a second node adds nothing to them until the store grows. For
map images the node's CPU and the per-node admission bound are the limit, and a second node is the answer.
