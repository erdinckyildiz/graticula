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

## Two nodes against one — ADR-012 condition 2, measured 2026-11-01

The throughput half of condition 2's prototype, after the 2026-10-31 attempt was set aside (the machine at 90 %).
Own PostgreSQL 16 cluster (port 55575, `max_connections` 300, nothing else on it), the current Release build, logging at
`Warning`, the same 840,596 buildings published as `hosted/bench_store`. **Pinned with psutil**: PostgreSQL logical 0–5
(three physical cores), node A 6–9, node B 10–13 (two physical cores each), the generator 14–15 — so "two nodes" is
twice the node cores against the same store. Both nodes share one server directory (phase 1). Each workload started only
once the whole machine was under 20 % busy over 10 s; **during the runs other sessions' processes still used 21–45 % of
the machine on average** (Windows Defender, a Disk Cleanup, another session's fixture), measured by the rig's watcher as
everything that is not this cluster, these nodes or the generator. One node, two nodes, one, two; 20 s per level.
Full tables: `two-nodes-2026-11-01.txt`.

| Workload | Callers | 1 node req/s (rep 1 · 2) | 2 nodes req/s (rep 1 · 2) | 1 node: node / PG cores | 2 nodes: node / PG cores | refusals at 128 (1 · 2 nodes) |
|---|---|---|---|---|---|---|
| mix | 32 | 238 · 216 | 212 · 236 | 3.5–3.7 / 4.5–4.7 | 3.5–3.8 / 4.6–5.0 | |
| mix | 64 | 249 · 239 | 241 · 248 | 3.7–3.8 / 4.6–4.7 | 3.5–3.9 / 4.6–4.9 | |
| mix | 128 | 261 · 238 | 324\* · 232 | 3.8 / 4.6–4.7 | 3.6–3.8 / 4.6–5.0 | 207, 168 · 1,670\*, 115 |
| map image | 32 | 44 · 46 | 50 · 62 | 3.8–3.9 / 3.9–4.0 | 4.1–4.9 / 4.6–5.4 | |
| map image | 64 | 42 · 45 | 55 · 58 | 3.6–3.9 / 3.6–4.0 | 4.5–4.7 / 4.9–5.0 | |
| map image | 128 | 42 · 46 | 55 · 57 | 3.3–3.9 / 3.4–4.0 | 4.7 / 5.0–5.1 | 130, 32 · 0, 0 |

\* The first two-node mix run at 128 callers counted 1,670 refusals answered fast, which is what lifts its request rate;
the second refused 115. Read the 64-caller row for the mix.

No request errored; the authenticated `whoami` answered 200 on every node throughout; connections peaked at 58–61 with
one node and 77–78 (mix) and 116 (map images) with two, against 297 usable; no breaker.

**So, plainly:**

1. **A second node does not raise the mix on this machine** — 239–249 requests a second at 64 callers with one node,
   241–248 with two. With one node the node used 3.7–3.8 of its 4 logical cores and PostgreSQL 4.6–4.7 of its 6; with
   two the nodes together used no more (3.5–3.9 of 8) and PostgreSQL 4.6–4.9: **the store is what fills**, at the
   1.2× node-to-store ratio condition 1 measured, and doubling the nodes adds nothing until the store grows.
2. **A second node raises map images by about a quarter to a third** — 42–46 to 55–62 requests a second — and removes
   the refusals at 128 callers (130 and 32 with one node, none with two): one node is held by its own cores (3.6–3.9 of
   4) and its per-source admission bound; with two, PostgreSQL rises to 4.9–5.4 of 6 and becomes the limit in turn.
3. What condition 1 predicted holds through two real processes: nodes pay for rendering, not for queries and tiles,
   and the sizing rule is nodes and store together.
