# Map rendering — what a drawn map costs on this runtime

**Run 2026-08-20** against the dev server and the experiment PostGIS, to answer
[A-076](../../docs/architecture-assumptions.md) and discharge
[ADR-041](../../docs/adr/ADR-041-the-map-renderer.md) condition 1.

## Why this benchmark exists

[ADR-004](../../docs/adr/ADR-004-rendering-engine.md) deferred server-side rendering
twice, and its **strongest surviving objection was a measured number**:
[mvt-generation](../mvt-generation/RESULTS.md) run 3 found **80.9% of wall time in GC
pause at 18% CPU** on a workload lighter than rendering, and ADR-007 §4.14 records
that worker sizing has no allocation term. ADR-041 was decided anyway, and its §10
says so plainly: *building before measuring is backwards by CLAUDE.md §3's own
standard*.

**So this is the measurement owed.** It was taken the same day the surface was built,
against the same process a client talks to.

## Method

`render-bench.py` (session scratchpad — forty lines of `urllib` and a thread pool)
sends `GetMap` at a fixed URL and reads `/admin/health`'s runtime counters either side
of the run. Those counters are the server's own: `GC.GetTotalAllocatedBytes`,
`GC.GetTotalPauseDuration`, `Process.TotalProcessorTime`, and the collection counts.

One warm request first, so the figures are not measuring a cold layer describe
(D-17: 4–6 ms) or a new connection.

**Machine:** 16 cores, server GC on, PostGIS in a container on the same host.
**Layers:** `hosted/tr_yol` (46,041 lines), `hosted/tr_ilce` (25,280 polygons),
`hosted/tr_il` (5,433 lines) — real data, whole-country extents, so every request
draws the entire layer.

## What it costs

| Map | Requests | Concurrency | p50 ms | p95 ms | req/s | MB alloc/req | GC pause | CPU | gen0/gen2 | KB out |
|---|---|---|---|---|---|---|---|---|---|---|
| tr_yol 1024², one at a time | 30 | 1 | 220 | 244 | 4.5 | ~0 | 0.4% | 3% | 44/2 | 76 |
| tr_yol 1024², eight at once | 80 | 8 | 501 | 652 | 15.6 | 1.1 | 2.3% | 16% | 69/0 | 76 |
| tr_ilce 1024², one at a time | 30 | 1 | 425 | 462 | 2.3 | 4.7 | 0.2% | 2% | 54/0 | 169 |
| tr_ilce 1024², eight at once | 80 | 8 | 905 | 1102 | 8.6 | 4.7 | 1.8% | 11% | 122/2 | 169 |
| tr_il 256², eight at once | 200 | 8 | 255 | 337 | 30.5 | 1.1 | 1.0% | 5% | 25/0 | 13 |

**GC pause is between 0.1% and 2.3%.** ADR-004's objection was 80.9%. The number that
deferred this decision for eight days is off by a factor of forty in the direction
that makes the decision safe, and it is now measured rather than argued.

## The result that decides where the cost actually is

The same features, same extent, same simplification tolerance — once as a map, once as
the FeatureServer JSON the compatibility face already serves:

| What | p50 ms | MB alloc/req | Response |
|---|---|---|---|
| `tr_ilce` **as JSON features** (`query`, geometry, 50,000 cap) | **808** | **11.8** | 3.7 MB |
| `tr_ilce` **as a 1024² PNG map** | **426** | **4.7** | 169 KB |
| `tr_ilce` as a 4096² PNG map | 1,111 | 6.4 | 934 KB |
| `tr_ilce` as a 4096² PNG map, four at once | 1,582 | 6.4 | 934 KB |

**Drawing the map is cheaper than serving the same features as JSON** — half the wall
clock, 40% of the allocation, and one twentieth of the bytes on the wire. The
rendering never serialises a coordinate to text, and that turns out to dominate.

**And sixteen times the pixels costs 2.6× the time.** 1024² to 4096² is 16× the area
and 1024² to 4096² is 1,111 ms against 426 ms. The rasteriser is not the bottleneck:
the query and the geometry decode are, and those are costs this server already paid on
every FeatureServer request before ADR-041 existed.

## What this does not say

- **Allocation is approximate.** `GC.GetTotalAllocatedBytes(precise: false)` under
  server GC reports per-heap budgets rather than exact bytes, and the `~0` in the first
  row is that imprecision rather than a request that allocated nothing.
- **One machine, one dataset, one container.** The absolute milliseconds are this
  laptop's. What travels is the *ratios* — map against JSON, 4096² against 1024², GC
  pause against wall clock — and those are what the objection was about.
- **No labels in the heaviest rows.** `tr_yol` and `tr_ilce` carry generated
  symbology, which has no `symbol` layer. Label placement is measured nowhere here and
  is the one part of the pipeline whose cost grows with feature count in a way the
  rasteriser's does not.
- **Nothing about a thousand services.** [ADR-007](../../docs/adr/ADR-007-worker-model.md)'s
  worker model still has no allocation term, and this says a map is cheap rather than
  that a fleet of them is.

## What follows

**A-076 is `VALIDATED`.** A CPU rasteriser meets the latency a WMS client expects at
typical sizes, on this runtime, without the allocation profile ADR-007 §4.14 cannot
account for.

**And one thing worth doing with it:** ADR-041 §5 assumed rendering would be the
expensive half and built the pipeline to avoid allocating per feature for exactly that
reason. The buffers were the right call and the reason was wrong — the expense is
upstream, in the query. A future optimisation belongs there, not in the canvas.
