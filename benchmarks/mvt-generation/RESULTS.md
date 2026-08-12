# MVT Generation — Results

**Run 1:** clipping. **Run 2:** simplification. **Run 3:** concurrency. All 2026-08-12.
**Settles:** A-019, A-004, A-001, **A-037**, **A-021** (PostGIS only); ADR-003 Alternative B
**Harness:** [`../harness`](../harness) · **Runner:** [`../run-tile-bench.ps1`](../run-tile-bench.ps1)

**These are the first measurements in this project.** Everything before them was
argument.

---

## Environment

| | |
|---|---|
| Host | Windows, 63.7 GB RAM, WSL2 capped at 15 GB |
| Database | PostgreSQL 16.4, PostGIS 3.4.3, GEOS 3.9.0, PROJ 7.2.1 (`NETWORK_ENABLED=OFF`) |
| Postgres config | `shared_buffers=1GB`, `work_mem=64MB`, `effective_cache_size=4GB` |
| Runtime | .NET 9, server GC, Release |
| Dataset | Geofabrik `turkey-latest.osm.pbf`, 611 MB, loaded by osm2pgsql 1.8.0 in 39m 49s |
| Table | `planet_osm_polygon` — **6,499,215 features, 77,089,382 vertices**, avg 11.9, max 215,488. All SRID 3857. 4,040 MB database. |

Warm measurements: three warm-up requests, then seven timed. Cold start is a
different experiment and mixing them would confound both.

## What was compared

| Path | What it is |
|---|---|
| **B** | `ST_AsMVT` — the PostGIS fast path, which **does not exist on SQL Server or Oracle** |
| **C** | In-process encoding, clipping with `NTS.Intersection` (the naive implementation, left in deliberately) |
| **C′** | In-process encoding, clipping with our own `RectClip` — bbox trivial-accept plus Sutherland–Hodgman |

## Results

Median milliseconds, and the stage breakdown for the in-process paths.

### z14 Istanbul, dense — 4,863 features

| Path | Median | Clip | Simplify | Transform | Encode | DB | Bytes |
|---|---|---|---|---|---|---|---|
| B `ST_AsMVT` | **62** | — | — | — | — | — | 195,958 |
| C NTS | 567 | **438.6** | 47.3 | 1.1 | 7.0 | 29.5 | 170,557 |
| C′ RectClip | **94** | **7.0** | 39.8 | 1.4 | 3.8 | 23.4 | 170,539 |

### z12 Istanbul, wide

| Path | Median | Clip | Simplify | Transform | Encode | DB | Bytes |
|---|---|---|---|---|---|---|---|
| B `ST_AsMVT` | **428** | — | — | — | — | — | 1,965,937 |
| C NTS | 6,200 | **4,727.9** | 881.8 | 68.6 | 99.7 | 108.5 | 1,604,319 |
| C′ RectClip | **1,471** | **30.7** | **807.6** | 31.4 | 132.4 | 128.6 | 1,604,170 |

### z16 Istanbul, close — sparse

| Path | Median | Clip | Simplify | Transform | Encode | DB | Bytes |
|---|---|---|---|---|---|---|---|
| B `ST_AsMVT` | **12** | — | — | — | — | — | 12,964 |
| C NTS | 417 | **348.5** | 3.3 | 13.9 | 0.6 | 19.2 | 12,344 |
| C′ RectClip | **48** | **9.5** | 3.4 | 0.07 | 0.2 | 12.0 | 12,368 |

## Findings

### 1. A-019 passes — in-process MVT encoding is viable

At the dense tile, **94 ms against 62 ms** for the PostGIS fast path. A 1.5×
gap, not an order of magnitude. With the cache absorbing repeat requests
(ADR-010), that is comfortably serviceable.

The multi-database promise is **not** hollow. Oracle and SQL Server can serve
tiles.

**With one caveat that must not be buried:** at z12 it is 1,471 ms against 428
ms — 3.4×, and 1.5 seconds is poor even when cached, because seeding pays it
too. Low zoom needs more work. See finding 4.

### 2. A-004 validated, decisively — and it was not close

> *Hot-path geometry overhead is material enough to justify our own primitives.*

`NTS.Intersection` was **79% of the entire request** at z14 and **76%** at z12.
Replacing it with a rectangle clipper took that stage from **438.6 ms to 7.0 ms
— 63× faster** — and cut the whole request 6×.

The reason is structural rather than a matter of tuning. `Intersection` runs
general polygon-polygon overlay — robust predicates, snap-rounding, the whole
OverlayNG machinery — to clip against an axis-aligned rectangle. And in a dense
urban tile most buildings are entirely *inside* the tile, so the correct answer
for them is a bounding-box comparison and no clipping at all.

### 3. What we wrote is fast. What we adopted was slow.

The most useful line in the whole run:

| Component | Origin | z14 cost |
|---|---|---|
| Tile-space transform | **ours** | 1.4 ms |
| MVT encoder | **ours** | 3.8 ms |
| Rectangle clip | **ours** | 7.0 ms |
| Douglas–Peucker simplify | NTS | 39.8 ms |
| ~~General intersection~~ | ~~NTS~~ | ~~438.6 ms~~ |

**ADR-003 Alternative B is validated by measurement rather than by preference.**
Own hot-path primitives, adopted topology — and the boundary between the two is
now empirically placed rather than argued.

It also vindicates the build-vs-adopt tiering: MVT encoding was put in Tier 1
because the format is small and it is the hottest path. It costs 3.8 ms.

### 4. The bottleneck moved — simplify was next

After fixing the clip, at z12 **simplify was 807.6 ms of 1,471 ms — 55%.**

That is NTS `DouglasPeuckerSimplifier`, which preserves more than a tile needs.
The same argument that justified `RectClip` applies again.

Acted on in run 2, below.

### 5. Output validated, not merely fast

An encoder that is fast and wrong is worthless, so both tiles were decoded and
structurally compared:

| | Ours | PostGIS |
|---|---|---|
| layer / version / extent | `polygons` / 2 / 4096 | `polygons` / 2 / 4096 |
| features | 4,854 | 4,725 |
| geometry types | polygon × 4,854 | polygon × 4,725 |
| moveto / closepath | 4,896 / 4,896 | 4,766 / 4,766 |
| malformed geometry | **0** | **0** |

Every ring opened is closed. Byte sizes differ by ~13% and feature counts by
129, both explained: we query the buffered envelope so we carry a few more
boundary features, and we place `osm_id` in the feature id rather than as a tag,
which is why our value table is 358 entries against 5,071.

**The C and C′ outputs are 170,557 and 170,539 bytes** — an 18-byte difference
across 4,854 features. Our clipper and NTS agree.

---

# Run 2 — simplify

Written against finding 4. Clipping is held constant — `RectClip` in every
variant — so the only thing that differs is simplification.

| Variant | What it is |
|---|---|
| **nts** | `DouglasPeuckerSimplifier`, default. Repairs topology: `IsValid` on every simplified polygon, `Buffer(0)` on any that fails |
| **ntsraw** | the same simplifier with `EnsureValidTopology` off. The gap between this and **nts** *is* the cost of the repair |
| **ours** | `TileSimplify` — runs **after** the tile-space transform, on the integer grid, over flat `double[]`, with no topology repair |

## The method had to change first

Run 1 measured variants in blocks, one after another. Re-running run 1's exact
configuration on the same build and the same tile produced **1,471 ms, then
949 ms, then 569 ms**. The machine carries **25–30% background CPU** from
unrelated containers, several of them in a crash-restart loop.

Run 2 therefore **interleaves**: run 1 of each variant, then run 2 of each
variant, and so on. That does not remove the noise, it distributes it evenly, so
the comparison between variants survives even though the absolute numbers do
not settle. Stage times are reported as the **minimum** across runs — for
CPU-bound work the fastest observation is the one least polluted by something
else, and finding 10 shows what the slower ones are actually measuring.

**Run 1's absolute milliseconds should be read as ±40%.** Its ratios held on
re-measurement; its absolutes did not.

## Results — 9 interleaved runs; stage columns are minima

### z12 Istanbul, wide

| Variant | Median | Min | Simplify | Transform | Clip | Encode | Features | Bytes |
|---|---|---|---|---|---|---|---|---|
| nts | 838 | 637 | **363.9** | 4.3 | 4.0 | 14.3 | 50,688 | 1,604,170 |
| ntsraw | 911 | 728 | **307.5** | 5.6 | 4.8 | 17.7 | 50,688 | 1,604,130 |
| **ours** | **456** | **291** | **7.8** | 5.1 | 4.8 | 14.0 | 50,579 | 1,600,461 |

### z14 Istanbul, dense

| Variant | Median | Min | Simplify | Transform | Clip | Encode | Features | Bytes |
|---|---|---|---|---|---|---|---|---|
| nts | 122 | 91 | **29.3** | 0.23 | 3.7 | 1.3 | 4,854 | 170,539 |
| ntsraw | 127 | 85 | **27.0** | 0.38 | 3.3 | 1.6 | 4,854 | 170,560 |
| **ours** | **83** | **35** | **0.8** | 0.28 | 3.6 | 1.4 | 4,853 | 170,548 |

### z16 Istanbul, close

| Variant | Median | Min | Simplify | Transform | Clip | Encode | Features | Bytes |
|---|---|---|---|---|---|---|---|---|
| nts | 19 | 16 | 1.5 | 0.02 | 3.0 | 0.1 | 327 | 12,368 |
| ntsraw | 39 | 18 | 1.3 | 0.02 | 3.9 | 0.1 | 327 | 12,358 |
| **ours** | **17** | **14** | **0.0** | 0.02 | 3.2 | 0.1 | 327 | 12,361 |

## Findings

### 6. The stated hypothesis was wrong, and that is the useful part

The prediction written down before this ran was that
`DouglasPeuckerSimplifier`'s topology repair was the cost — `IsValid` on every
polygon and `Buffer(0)`, a full overlay operation, hidden inside a simplifier.
It was an attractive hypothesis because it is the same shape as finding 2.

**It is wrong.** `ntsraw` is 307.5 ms against `nts`'s 363.9 ms, and at two of
three zooms `ntsraw` measured *slower* on total time. Within this noise, turning
topology repair off buys nothing: the simplified polygons are valid, `Buffer(0)`
never fires, and `IsValid` is not where the time goes.

The cost is the Douglas–Peucker walk itself over NTS `Coordinate`, which is a
`class`. A 556,728-vertex tile is 556,728 heap objects before the first distance
calculation. Finding 10 measures that directly.

Recorded prominently because the prediction was written first and the
measurement contradicted it. Unwritten, it would have quietly become "we always
knew it was allocation".

### 7. `TileSimplify` is 47x faster on the stage, 2.2x on the request

At z12, **363.9 ms to 7.8 ms**. The whole request goes from 637 ms to 291 ms
best-observed, 838 to 456 median.

Three changes, and the ordering one is the largest:

1. **Simplify after the transform, not before.** Quantising to the 4096-unit
   integer grid collapses vertices on its own. Simplifying first spends
   Douglas–Peucker on points the next stage was going to merge anyway. We were
   doing it backwards because the NTS API invites map-space simplification, and
   nothing about the API suggests the order matters.
2. **Flat `double[]` instead of `Coordinate` objects.**
3. **No topology repair** — which finding 6 says is worth nothing, so it is
   listed last rather than claimed as a win.

### 8. It is not faster by discarding more geometry

The check that matters, because a simplifier can always win on speed by emitting
less:

| | nts | ours | delta |
|---|---|---|---|
| z12 vertices out | 293,508 | 292,716 | **−0.27%** |
| z14 vertices out | 32,966 | 32,981 | **+0.05%** |
| z12 features | 50,688 | 50,579 | −109 |
| z12 bytes | 1,604,170 | 1,600,461 | −0.23% |

At z14 ours emits slightly *more* vertices than NTS. The 109 missing z12
features are 248 rings dropped for enclosing zero area on the integer grid — a
building smaller than one tile unit has no picture to contribute, and NTS keeps
it as a degenerate sliver.

Structurally validated as in run 1: `moveto` 50,791 = `closepath` 50,791, zero
malformed geometries, and every coordinate inside [−64, 4160] — the tile plus
its declared buffer, exactly.

### 9. Geometry is no longer the cost, and half the request is in no stage at all

z12, `ours`, minima across 15 runs:

| Stage | Min ms | |
|---|---|---|
| Fetch (Npgsql row read) | **73.2** | |
| Encode | 17.4 | ours |
| Simplify | 10.7 | ours |
| WKB parse | 10.4 | |
| Query | 9.6 | |
| Clip | 6.2 | ours |
| Transform | 4.6 | ours |
| **Sum of stages** | **132.1** | |
| **Total request** | **323** | |

Two things fall out. **All the geometry work we own is 21.5 ms** — clip,
simplify and transform together, out of 323. And **191 ms of the request is in
no measured stage.** That gap is not a rounding error, it is the majority of the
request, and a claim about it needs evidence like anything else.

### 10. The gap is allocation. A z12 tile allocated 404 MB.

Added `GC.GetTotalAllocatedBytes` and collection counts to the endpoint rather
than assert it:

| Tile | Variant | Allocated | Gen0/1/2 | GC pause |
|---|---|---|---|---|
| z12 | nts | **404.3 MB** | 13–16 / 2 / 1 | 53–153 ms |
| z12 | **ours** | **204.3 MB** | **7** / 2 / 1 | 18–91 ms |
| z14 | nts | 55.7 MB | 2–3 / 1 / 0 | — |
| z14 | **ours** | **34.2 MB** | 0–2 / 1 / 0 | — |
| z16 | nts | 18.3 MB | 0–2 / 0 / 0 | — |
| z16 | **ours** | **15.8 MB** | 0–1 / 0 / 0 | — |

**`TileSimplify` halves the allocation of a z12 tile**, 404 MB to 204 MB, and
halves gen0 collections. That is the mechanism behind finding 7, and it confirms
finding 6's corrected explanation: the cost was object churn, not topology
repair.

It also explains the 191 ms gap and the wild per-stage variance — a single
request's GC pause ranged 18 to 153 ms, landing inside whichever stopwatch
happened to be running. That is why stage columns are minima.

**Two hundred megabytes for one tile is the number to carry forward.** It is not
a tile-encoding fact, it is a worker-model fact. At the concurrency ADR-007
assumes, allocation rate — not CPU — sets the ceiling, and ADR-010's seeding
walks whole pyramids of exactly this. Neither ADR has a number for it. The
remaining allocation is dominated by WKB parsing building an NTS geometry graph
that exists only to be thrown away one stage later; reading WKB straight into
tile space is the shape of that fix, and it is a larger change than either
primitive written so far.

---

---

# Run 3 — concurrency, and the thing both earlier runs missed

Written against A-037, which run 2 opened: *allocation rate, not CPU, sets the
tile-serving ceiling per worker.* Single-request latency cannot answer it — a GC
pause lands on whichever request is unlucky, so at concurrency 1 it reads as
variance. The question is whether throughput stops scaling **while CPU is still
available**.

Driver is `GisBench.exe load`, a separate process so its own allocations are not
charged to the server. Server counters (`GC.GetTotalAllocatedBytes`, collection
counts, `GetTotalPauseDuration`, process CPU) are sampled either side of each
level, giving a delta over a known wall-clock window. Four adjacent tiles are
rotated so the result is not one Postgres buffer-cache entry. Machine: 8 cores,
16 threads; PostGIS in WSL capped at 6 processors; 25–30% background load.

## A-037 is validated, and it is not close

z14 dense, in-process path, no pushdown:

| conc | req/s | p50 | p99 | alloc MB/req | gen2 | **GC pause %** | **server CPU** |
|---|---|---|---|---|---|---|---|
| 1 | 17.8 | 43 | 204 | 20.0 | 86 | **28.0%** | 1.38 of 16 |
| 2 | 19.4 | 105 | 259 | 19.9 | 62 | 54.8% | 1.88 |
| 4 | 23.7 | 153 | 371 | 19.9 | 67 | 71.5% | 2.50 |
| 8 | 25.3 | 285 | 634 | 19.9 | 53 | 80.7% | 2.70 |
| 16 | 28.3 | 509 | 1129 | 20.0 | 37 | **80.9%** | **2.92 of 16** |

**Sixteen times the concurrency buys 1.6x the throughput.** At the top the
process is suspended for garbage collection **81% of wall-clock time** while
using **2.92 of 16 cores** — 18% CPU utilisation. It is not CPU-bound, it is not
database-bound, it is stopped.

The control rules out the framework: `/health`, one scalar query, sustains
**1,984 req/s at 0.0 MB per request and 0.8% GC pause**. Kestrel and Npgsql are
not the problem.

## Finding 11: a tile's cost floor is set by the largest geometry overlapping it

The z16 sweep is what gave this away. A sparse tile — 327 features, 12 KB of
output — still allocated **15.4 MB per request** and still hit 80% GC pause. A
near-empty tile cannot cost that. So the fixed floor was measured directly:

| Tile | Features out | **Vertices read** | **Vertices emitted** | Ratio |
|---|---|---|---|---|
| z16 sparse | 327 | **201,580** | 2,080 | **97x** |
| z14 dense | 4,853 | 240,699 | 32,981 | 7.3x |
| z12 wide | 50,579 | 556,212 | 292,716 | 1.9x |

And the culprits, from the database:

| Vertices | Feature |
|---|---|
| 72,919 | Türkiye |
| 52,455 | Marmara Denizi |
| 44,735 | Marmara Denizi ve Adalar Özel Çevre Koruma Bölgesi |
| 11,431 | Marmara Bölgesi |

Four administrative and sea polygons are **90% of everything read** for a tile
showing one city block. They overlap every tile in the city, so **every tile in
Istanbul pays for the outline of Turkey**, and the smaller the tile the worse the
ratio.

This is not a .NET fact or an NTS fact. Any client that selects whole geometries
by bounding box pays it, on any engine. It is also the real reason `ST_AsMVT`
wins: `ST_AsMVTGeom` clips inside the database and the full geometry never
crosses the wire.

It is the same problem [geometry-crs-policy.md](../../docs/geometry-crs-policy.md)
§6 anticipated for *serving* one huge feature — arrived at from the opposite
direction, and worse, because here the huge feature is not even wanted.

## Finding 12: A-021 validated — pushdown is not an optimisation

Two pushdown modes added, both single-request medians:

| Tile | Mode | Total ms | Alloc MB | Vertices read | Bytes |
|---|---|---|---|---|---|
| z16 | none | 303 | 19.3 | 201,580 | 12,361 |
| z16 | **clip** | **23** | **1.3** | **2,208** | 12,382 |
| z16 | simpclip | 45 | 1.3 | 2,085 | 12,371 |
| z14 | none | 164 | 35.3 | 240,699 | 170,548 |
| z14 | clip | 219 | 20.2 | 36,895 | 170,546 |
| z14 | **simpclip** | **131** | **19.7** | **32,981** | 170,035 |
| z12 | none | 873 | 214.0 | 556,212 | 1,600,461 |
| z12 | clip | 411 | 188.4 | 351,002 | 1,600,520 |
| z12 | **simpclip** | **249** | **181.9** | **293,220** | 1,590,428 |

`clip` is `ST_ClipByBox2D`; `simpclip` is `ST_Simplify` then `ST_ClipByBox2D`.
At z16 that is **13x on latency and 15x on allocation**. At z12, 249 ms against
`ST_AsMVT`'s 254 ms in the same session — parity.

Output stays sound: same 4,853 features at z14, `moveto` 4,895 = `closepath`
4,895, zero malformed.

`ST_ClipByBox2D` can return invalid geometry, which PostGIS documents. For a
tile that is the same trade our own clipper makes and the same trade
`ST_AsMVTGeom` makes. It would be unacceptable on the analytical path.

**A-021 was written as "does pushdown work usefully?" — a tuning question.** It
is not. Without it, the in-process path reads two orders of magnitude more
geometry than it emits.

## Does pushdown lift the ceiling? Partly.

z14, all three paths, at each concurrency level:

| conc | ours req/s | **+pushdown req/s** | ST_AsMVT req/s | ours GC% | **+pushdown GC%** | ST_AsMVT GC% |
|---|---|---|---|---|---|---|
| 1 | 17.8 | 28.0 | 35.8 | 28.0 | **1.5** | 0.0 |
| 2 | 19.4 | 45.0 | 51.2 | 54.8 | **2.9** | 0.1 |
| 4 | 23.7 | 61.0 | 60.4 | 71.5 | **5.6** | 0.2 |
| 8 | 25.3 | 61.6 | 81.0 | 80.7 | 39.9 | 0.1 |
| 16 | **28.3** | **69.9** | **96.3** | 80.9 | **65.6** | 0.3 |

| | ours | +pushdown | ST_AsMVT |
|---|---|---|---|
| alloc per request | 20.0 MB | **4.9 MB** | **0.1 MB** |
| p99 at conc 16 | 1,129 ms | **736 ms** | 835 ms |
| server CPU at conc 16 | 2.92 cores | 1.18 | 0.11 |

**2.5x the throughput at concurrency 16, 4x less allocation, and it closes most
of the gap** — from 29% of `ST_AsMVT`'s throughput to 73%. At concurrency 4 it
is *ahead* of `ST_AsMVT`.

**But the ceiling is raised, not removed.** GC pause is still 65.6% at
concurrency 16 and throughput still flattens between 4 and 8. 4.9 MB per request
for 36,895 vertices read is about 139 bytes per vertex — roughly three to four
copies of every coordinate: WKB bytes, then `Coordinate` objects, then a clipped
copy, then a transformed copy. **A-037 stands.**

## Finding 13: the concurrency gap is much wider than the latency gap

Run 1 reported the in-process path at 94 ms against `ST_AsMVT`'s 62 ms and
called it 1.5x. Under load that framing is too kind:

| | single request | at concurrency 16 |
|---|---|---|
| ours vs `ST_AsMVT` | 1.5x | **3.4x** |
| ours + pushdown vs `ST_AsMVT` | — | **1.4x** |

A single-request benchmark cannot see a GC ceiling, because at concurrency 1 the
pause is amortised across idle time. **A-019 was validated on a measurement that
structurally could not detect its own most important failure mode.** It survives
— with pushdown, the in-process path reaches 73% of the PostGIS fast path — but
the margin came from a change made *after* the assumption was marked validated.

Honest caveat in the other direction: `ST_AsMVT` only scales 2.7x for 16x
concurrency itself, and its server CPU stays at 0.11 cores. **Its** ceiling is
PostgreSQL, which is capped at 6 processors in WSL here. Neither number is a
capacity figure for real hardware. What is sound is the comparison between
paths, measured on the same machine in the same session.

## What run 3 changes

- **A-037 `VALIDATED`.** ADR-007 sizes workers against CPU and a context budget.
  Neither is the binding constraint on this workload.
- **A-021 `VALIDATED` on PostGIS**, and promoted from tuning to structural.
  ADR-008's per-dialect pushdown table is now load-bearing: the question for
  SQL Server and Oracle is no longer *is in-process encoding fast enough* but
  *can clip be pushed down at all*. That sharpens D-05 considerably.
- **Q-66 becomes the main line of work**, not a curiosity. Three to four copies
  per coordinate is what 4.9 MB per request buys, and flat coordinates end to
  end is the only thing that removes them.
- **Seeding (ADR-010 §6) should use pushdown unconditionally**, and a seeding
  job is exactly the workload that would otherwise sit at 80% GC pause.

---

## What this does not show

Stated plainly, because the temptation with a first good result is to over-claim.

- **Only PostGIS was measured, and that is now deliberate.** Endpoint C exists
  *because* SQL Server and Oracle lack `ST_AsMVT`, and neither has been tested.
  The numbers here are the in-process path running against the one database
  that does not need it. Neither engine is available on this machine, so the
  gap is recorded as **[D-05](../../docs/architecture-debt.md)** — accepted
  debt with a repayment trigger — rather than left as a benchmark permanently
  listed as "next".

  What survives the gap: the CPU and allocation costs measured here are ours,
  in our process, and do not depend on which engine supplied the WKB. What does
  not: those engines' WKB output, driver materialisation cost, and index
  selectivity. Note that **fetch is already the largest single stage at z12**
  (73.2 ms of 132.1 ms of measured work), and fetch is exactly the part that
  changes with the driver.
- **One machine, one dataset, one city.** Turkish OSM data at three zooms.
- **Warm only.** No cold start, no cache-miss storm, no concurrency, no p99
  under load — this is single-request latency.
- **No visual verification.** The tiles decode and are structurally sound;
  nobody has looked at them rendered.
- **`TileSimplify` does not repair topology.** Self-intersections introduced by
  simplification survive into the tile. Renderers tolerate this; anything that
  computes with the geometry must not use this path. Same boundary as the
  clipper, for the same reason.
- **Absolute latencies on this machine are unstable to ±40%**, from load outside
  the experiment. Ratios between variants measured in the same interleaved run
  are sound; a single number quoted alone from run 1 is not.
- **Concurrency was measured on a capped and contended machine.** PostGIS runs
  in WSL with 6 of 16 processors; 25–30% of the host is unrelated background
  load. Absolute req/s figures are not capacity numbers for real hardware. The
  comparison between paths, measured in one session on one machine, is sound.
- **One workload shape.** Four adjacent dense tiles, rotated, no cache in front,
  no mix of zoom levels, no feature queries competing for the same worker. A
  real server does all of those at once.
- **No `ST_AsMVT`-equivalent pushdown was tested off PostGIS.** `ST_ClipByBox2D`
  has counterparts on the other engines with different names, semantics and
  costs. Finding 12 makes that the most important unmeasured thing in the
  project — see D-05.
- **Sutherland–Hodgman has a known limitation** — it can emit degenerate
  connecting edges along the boundary for concave polygons. Tile renderers
  tolerate this. It would be unacceptable for analytical overlay, which is
  exactly why topology stays with NTS.
- **The measurement harness has now been wrong twice.** Run 1: a PowerShell
  header read indexed into a string and returned ASCII character codes,
  producing a confident and entirely wrong table — caught because a feature
  count of 52 was implausible. Run 2:
  `GC.GetAllocatedBytesForCurrentThread()` in an async handler, which resumes on
  a different pool thread after each await, so it subtracted two unrelated
  threads and reported **−14.8 MB** allocated. Caught because negative
  allocation is impossible. Both were found by a value being absurd rather than
  by review, which is luck. The harness needs the same scepticism as the thing
  it measures, and neither of these would have been caught if the wrong number
  had merely been plausible.

## Next

1. ~~**Simplify**~~ — done, run 2.
2. **WKB straight into tile space** (Q-66), skipping the NTS geometry graph.
   Findings 10 and 12 agree this is what is left: ~139 bytes per vertex read,
   three to four copies of every coordinate. Larger than either primitive so
   far, and it changes the provider interface, so it is a decision rather than
   an optimisation.
3. ~~**SQL Server and Oracle**~~ — **deferred, not dropped.** Not installed and
   not being installed; recorded as
   [D-05](../../docs/architecture-debt.md) with an explicit repayment trigger.
   Still the largest hole in the evidence.
4. ~~**Concurrency and p99**~~ — done, run 3. What remains is a capacity number
   on hardware that is not capped and contended.
5. **Visual verification** of a rendered tile. Still not done, and now the
   longest-standing gap: three rounds of optimisation and nobody has looked at
   one.
