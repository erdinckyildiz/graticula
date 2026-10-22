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
