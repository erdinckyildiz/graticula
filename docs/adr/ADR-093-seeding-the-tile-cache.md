# ADR-093 — Seeding the tile cache

| | |
|---|---|
| **Status** | `ACCEPTED` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-09-29, by owner direction. The owner asked for the vector tile face's remaining gaps to be closed one at a time; this is the fifth item on that list. The shape — a job, scoped by zoom range and area, lowest level first, resumable, cancellable, rate-limited through `ConnectionBudget` from the first version — was decided before this ADR, by [ADR-010](ADR-010-caching.md) §6, §6a, §6b and condition 3 and by [ADR-011](ADR-011-job-system.md) §3.6. What this ADR adds — the numbers, the addresses, the table shapes — is the session's design, and is marked **INFERRED** where it matters (§5). |
| **Depends on** | [ADR-010](ADR-010-caching.md), [ADR-011](ADR-011-job-system.md), [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md), [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md), [ADR-017](ADR-017-admin-api.md) |
| **Discharges** | [ADR-010](ADR-010-caching.md) condition 3 |
| **Supersedes** | — |
| **Superseded by** | — |

> Status values: `DRAFT`, `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`,
> `ACCEPTED`, `ACCEPTED WITH CONDITIONS`, `REJECTED`, `DEFERRED`, `REOPENED`.
> Confidence: `HIGH`, `MEDIUM`, `LOW`.

---

## 1. Context

Until this ADR, every vector tile was built on its first request. Nothing filled the cache before a
caller asked.

- **The first map after a change pays the whole cost.** A new service, an emptied cache and an upgrade
  that raises `TilePipeline.Version` all start from nothing. [ADR-010](ADR-010-caching.md) §6a measured
  one z12 tile at 291 ms best case and 204 MB allocated, and low levels cost the most.
- **An upgrade empties the cache on purpose.** [D-155](../architecture-debt.md) put the pipeline
  generation into every key, so a release that changes how a tile is drawn makes every cached tile
  unreachable. That is correct, and the first traffic after it rebuilds the pyramid.
- **ADR-010 §6 designed seeding and nothing built it.** Its §11a table said *not built*, condition 3 said
  *not yet applicable*, and [Q-18](../open-questions.md)'s ledger called cache lifecycle *half built*
  for this reason.

## 2. Alternatives considered

### Alternative A — a seed is a job over one service *(chosen)*

A seed is a job of a new kind, `tile.seed`, run by a job worker. It covers one service, a range of
levels and an area. It walks the tiles lowest level first, builds each through the tile route's own
code, and keeps a cursor per level.

**Argument for.**
- ADR-010 §6 and ADR-011 §1 already decided that seeding is a job and runs on job workers, never on
  request threads.
- A tile carries every layer of its service (`VectorTileEndpoints.TileAsync`). The service is the unit a
  tile is built for.
- The job system already has claims, leases, reclaim and a status surface (ADR-011 §3.2–§3.4).

**Argument against.**
- Every node runs one seed at a time. Seeds of several services queue behind each other.

### Alternative B — a seed per layer, as ADR-010 §6 worded it

**Argument for.** ADR-010 §6 says *scoped by layer*. A layer's lifetime is set per layer.

**Argument against.** A tile is not per layer. Serving builds the parts of every layer in the tile, so a
layer seed would either leave the tile a miss for its other layers or build them too. **INFERRED, listed
for confirmation:** ADR-010's *layer* was written before a service carried several layers in one tile,
and *service* is what it means now.

### Alternative C — read once, encode many tiles in process (Q-68's other branch)

**Argument for.** One query per level instead of one per tile. ADR-010 §6a names this question.

**Argument against.** It is a second way to build a tile. The owner's requirement is that a seeded tile
be the bytes a request would build, under the key a request reads. A second path is a second place for
those to differ, and [ADR-021](ADR-021-tile-encoding.md) chose `ST_AsMVT` per tile. It also needs a
measurement nobody has made.

### Alternative D — a rate limiter of the seed's own

**Argument for.** A token bucket in tiles per second is easy to explain to an operator.

**Argument against.** ADR-010 condition 3 and ADR-011 §3.6 require that jobs and requests draw from one
budget per source. A second limiter would let a seed and the maps it runs beside each take their own
share of one database, which is the overload the condition exists to prevent.

### Alternative E — seed automatically after an upgrade

**Argument for.** The cache is empty after a pipeline change, and the server knows it.

**Argument against.** The server does not know which services matter, which levels, or when the source
database can take the load. An automatic seed of every service after every upgrade is the *accident* ADR-010
§6 warns about. **Refused by owner direction** (the task's item 10): the operator starts a seed.

## 3. Counterarguments to the preferred option

- ~~**The cache's size budget still applies, and it evicts the wrong end.** `FileSystemTileCache` evicts by
  least recent use. A seed larger than the budget (2 GB by default) evicts the tiles it built first — the
  low levels, which §6a calls the most valuable. The start response says so; nothing prevents it.~~
  **Answered 2026-09-29 by owner decision, in two halves (§5.9).** The cache now evicts the **highest level
  first** — another pipeline's tiles before any, then by level descending, then least recently used — so a
  seed larger than the budget loses its top levels and keeps its low ones. And a seed is **estimated before
  it starts**: the bytes it will add, and whether it would keep them — one that would evict tiles it had
  itself built is refused unless the operator sends `force`. A seed that only makes room from other maps'
  deeper tiles is not refused: that is the cache working. The budget still applies, and a forced seed
  still evicts its own top levels; what changed is which end, and that the operator is told first.
- ~~**Serving a tile takes no permit, and a seed does.** `LayerConnections.TileSourceFor` hands out a source
  with no `ConnectionBudget` lease, so a cold tile a map asks for is not counted against the source. A seed
  is therefore more polite than the maps it runs beside. Recorded as [D-277](../architecture-debt.md).~~
  **Repaired 2026-09-29 by owner decision ([D-277](../architecture-debt.md)).** A map's cold tile takes the
  same permit a seed's does, inside the shared build, so a seed and a map compete on equal terms and a tile
  the cache answers still costs nothing. A refused cold tile is a 503 with `Retry-After`, as a refused query
  is; the owner accepted that. An expired copy on disk is not offered in its place —
  [D-278](../architecture-debt.md), which waits on [ADR-010](ADR-010-caching.md) §5.1a's undecided half.
- **The size estimate is a guess until the cache has seen the service (§5.9).** Where the cache holds fewer
  than 16 of a layer's tiles at a level, each is assumed to cost a default that doubles per level below 16.
  A first seed of a dense layer can be under-estimated and still evict; a first seed of a sparse one can be
  refused when it would have fitted, and `force` is the way through.
- ~~**On a busy server the free space is small by construction.** … most seeds of any size are refused until
  the operator forces them.~~ *(Withdrawn the same day: the fit is no longer the free space — §5.9. A warm
  cache full of other maps' deep tiles takes a seed without a refusal.)*
- **A seed that fits still evicts other maps' tiles**, the deepest first. That is what the owner's rule
  allows on purpose; the refusal is only for a seed that would lose its own.
- **The fit is a model of eviction, not a reservation.** It assumes the tiles already at a seed level are
  not read while the seed runs (within a level the least recently used goes first, and the seed's are the
  newest), and nothing holds the room once counted — maps writing shallow tiles during a long seed can
  still push its top level out. And every service's shallow tiles outrank the seed, so on a cache that is
  mostly low levels a seed of deeper ones is refused even though the whole cache is working as designed.
- **A seed that crashes its own process comes back after every restart.** A resumable job is queued again
  whenever its lease is lost (§5.4), without the retry cap a harmless job has. Somebody must cancel it.
- **The estimate is pessimistic.** It is the straight line from the rate so far, and the time since each
  level started includes pauses and any restart. It falls as the seed goes on.
- **The read-back counts only the seeded area.** §5.6's cached figures are per level over the area the last
  seed covered. They are not a cache browser, and a level never seeded is not listed.
- **A crash loses up to two seconds of counts.** The cursor is written every two seconds. Tiles built in
  that window are in the cache, and the resumed seed counts them as `present`, not `built`.

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The count is exact, and the order is lowest level first, row by row | `TileSeedPlanTests` (16 cases, including 95,258 tiles for Istanbul z0–z16, worked independently of the code) | this change |
| The walk resumes from its cursor, counts failures and goes on, pauses on an outage with backoff, skips a level no layer draws at, and stops when a checkpoint says the job is gone | `TileSeedRunTests` (17 cases, no database) | this change |
| A cached tile is counted as fresh only inside the area, for the layer, within its lifetime | `FileSystemTileCacheTests.Fresh_tiles_are_counted_inside_a_rectangle_and_nowhere_else` | this change |
| One seed per service at a time, even when six starts race; only the holder checkpoints; cancel reaches only a seed; a lost seed is queued again with its progress, however often | `TileSeedStoreTests` (Platform.Postgres) | this change. **Written and compiled; not run by the authoring session, which had no database** |
| A seeded tile is a `HIT` with the bytes a request built before the seed; over the cap is a 400 with the count; a second seed is a 409; cancel works; another folder's service is 403 or 404 | `ASeededTileIsAServedTileTests`, `AServiceIsAddressedByItsFolderTests` (conformance) | this change. **Written and compiled; not run by the authoring session, which had no server** |
| The tile route's bytes did not change | `TilePipelineVersionTests`: the hash moved, `TilePipeline.Version` did not | this change |
| The cache evicts the highest level first and another pipeline's tiles before any; within a level, least recently used | `FileSystemTileCacheTests.A_seed_larger_than_the_budget_loses_its_highest_level_and_keeps_its_lowest`, `Within_a_level_the_least_recently_used_goes_first`, `Another_pipelines_tiles_are_evicted_before_any_of_this_ones` — the first and third fail with the order put back to recency alone | 2026-09-29, §5.9 |
| The estimate: the default per level, samples replacing it at 16, present tiles adding nothing, a level no layer draws adding nothing; the fit — a seed past the free space fits when only deeper tiles make its room, a seed inside the free space fits whatever outranks it, and another service's tiles at a level inside the seed's range outrank its higher levels; the highest level that fits; no overflow | `TileSeedEstimateTests` (24 cases, each number worked by hand), `FileSystemTileCacheTests.A_layers_holding_counts_samples_by_level_and_present_tiles_inside_the_rectangle`, `The_bytes_by_level_count_only_this_pipelines_tiles` | 2026-09-29, §5.9 |
| A map's cold tile takes one permit per build and none for a cached tile; a refusal is shared by every caller waiting on the build and answered 503 | `TileBuildAdmissionTests` (6 cases, a real `ConnectionBudget`) | 2026-09-29, D-277. **The wiring — that the route passes the admission — is read, not tested** |
| The dry run and the read-back carry the estimate and the budget; a seed that does not fit is refused with `exceedsCacheBudget` | `ASeededTileIsAServedTileTests.A_seed_says_its_size_and_one_that_does_not_fit_the_cache_is_refused` (conformance) | 2026-09-29. **Written and compiled; not run by the authoring session, which had no server.** It checks the refusal only when the fixture's cache makes it reachable |
| A service over its quota evicts its own tiles in the budget's order and nobody else's, keeps the tile just written, and counts what it evicted; a seed is estimated against the quota counting only the service's own tiles, and refused with a sentence naming the quota and `force` | `FileSystemTileCacheTests.A_service_over_its_quota_evicts_its_own_tiles_highest_level_first_and_nobody_elses`, `Within_a_quota_another_pipelines_tiles_go_first`, `A_write_is_never_refused_for_the_quota_and_the_tile_just_written_is_kept`, `A_seed_larger_than_its_services_quota_is_refused_though_the_budget_has_room` | 2026-09-29, §5.9's quota, owner decision |
| A seed never takes a stale copy for a built tile | `StaleWhileErrorTests.The_build_alone_never_takes_a_stale_copy` — the build a seed and an export call has no fallback; only serving does ([ADR-010](ADR-010-caching.md) §5.1a) | 2026-09-29, D-278 |
| The quota is set, read back and cleared, and refused at zero; another folder's service is 403 or 404 | `AStaleTileStandsInForARefusedOneTests.A_services_quota_is_set_read_back_and_cleared`, `AServiceIsAddressedByItsFolderTests` (conformance) | 2026-09-29. **Written and compiled; not run by the authoring session, which had no server** |
| The default guess is near a real estate's tile sizes | — | **not measured**, INFERRED (§5.9) |
| How long a realistic estate takes to seed | — | **not measured.** [A-020](../architecture-assumptions.md) stays `UNVALIDATED` |

## 5. Decision

### 5.1 A seed is a job

- A new job kind, `tile.seed` (`JobKind.TileSeed`). Migration 61 widens `job_kind_known`.
- It declares `JobRerun.Resumable`, a fourth answer to [ADR-011](ADR-011-job-system.md) condition 2 and
  §3.4's `RESUMABLE`. A lost lease queues it again **every time**, with its progress kept.
- `TileSeeder` is the worker. It claims, holds a lease and sweeps exactly as the two geodatabase workers do,
  over the pollers' pool, one seed at a time per process. Nothing runs on a request thread.

### 5.2 Scope, and the cap

- A seed covers **one service** (all its layers), a range of levels, and an area.
- The area is an ArcGIS envelope in Web Mercator (3857, 102100, 102113, 900913) or WGS 84 degrees (4326),
  or left out for the service's whole extent — the union of its layers' extents, projected as the service
  document projects them. A layer whose extent cannot be projected refuses the default and asks for an
  extent.
- *(Amended 2026-09-29 — [ADR-096](ADR-096-a-vector-tile-service-may-be-tiled-in-another-reference.md) §5.5.)* **For a service cut on another tiling scheme, the grid is
  that scheme's**: the area may also be given in the scheme's own reference, is moved into it when given in 3857
  or 4326, and is kept in it; the levels are the scheme's, not 0–22; each level's rectangle is counted on the
  scheme's tiles (`TileSeedPlan.For(scheme, …)`). The seed records the grid's key in its job detail, and the
  worker fails a seed whose service changed grid since. A grid change cancels the service's seeds. A Web Mercator
  service's seed is counted, recorded and walked exactly as below.
- The levels default to the lowest level any layer draws at (ADR-070), up to level 14 or the highest level
  drawn, whichever is lower.
- The tile count is **exact**: the sum of each level's rectangle of the grid, with a touching tile counted.
  It is computed before anything starts.
- **The cap is 250,000 tiles** (`Graticula:TileSeedMaximumTiles`). Over it, the start is a **400** that
  carries the count, the cap, and the highest level that fits.
- **INFERRED, listed for confirmation:** 250,000 as the default; 400 rather than 413 or 422.

### 5.3 Order

- Lowest level first (ADR-010 §6a). Within a level, row by row from the north-west.
- The order is fixed, so one number per level — how many tiles from the start are done — is the whole
  cursor.

### 5.4 Resume, cancel, outage

- **Resume.** The cursor and five counts per level (`built`, `present`, `empty`, `failed`, `skipped`) are
  written at most every two seconds, at the end of each level, and at each pause. A restarted server's
  sweep queues the job again, and the next claim goes on from the cursor.
- **Cancel.** `DELETE` marks the job `cancelled` — the first time any job reaches that state. A seed on the
  same node stops at the tile it is on. A seed on another node stops at its next checkpoint, within two
  seconds. What it leaves behind is decided: cached tiles, each the tile a request would have written. An
  import's cancellation is still not decided, and the store's cancel statement names the seed kind.
- **A failed tile** is counted as `failed`, and the seed goes on.
- **A source outage** — a quiesced source, an open breaker, a full `ConnectionBudget`, or a connection that
  `SourceBreaker.Unreachable` classifies — pauses the seed and tries the same tile again. The wait is 5
  seconds, doubling to 5 minutes, and resets when a tile works. The pause and its reason are in the
  read-back.

### 5.5 Politeness and the same bytes

- **Every tile build takes a lease from `ConnectionBudget`**, keyed on the layer's connection string,
  after the quiesce and breaker checks — `LayerConnections.AdmitTileBuildAsync`, the three steps
  `BudgetedFeatureSource.LeaseAsync` takes. There is no second limiter. A build's outcome is reported to
  the source's breaker.
- **Concurrency is 2 per seed** (`Graticula:TileSeedConcurrency`), clamped to the per-source limit. With
  the defaults a seed holds at most 2 of a source's 24 permits (`Graticula:PerSourceConcurrency`). A cached
  tile takes no permit; only a build does.
- **A seeded tile is built through the route's own code.** The tile route's per-layer loop is
  `VectorTileEndpoints.LayerPartAsync`: the same key (`KeyOf`, with `TilePipeline.Version` in the path),
  the same source (`ST_AsMVT` with clip pushed into PostGIS), the same generalisation, the same
  single-flight, and the same cache write. The seed passes a permit; serving passes none. A tile already
  cached and fresh is left alone and counted as `present`.

### 5.6 Admin API and the read-back

Behind `content:publishTiles` — the privilege of `PUT /admin/layers/{name}/cache` — and addressed by
`?folder=` and name ([D-275](../architecture-debt.md)). Starting and cancelling also need the service's
owner or an administrator (ADR-075). Reading needs that the caller may read the service.

| Route | Answer |
|---|---|
| `POST /admin/services/{name}/cache/seeds` `{minZoom?, maxZoom?, extent?, force?}` | **202** with the job id, where to watch it and the size estimate; **400** over the cap, with the count; **400** marked `exceedsCacheBudget` when the seed would evict tiles it had itself built and `force` is not `true` (§5.9); since 2026-09-29 **400** marked `exceedsCacheQuota` when it would do so inside its service's quota, with `quotaEstimate` beside `estimate` (§5.9); **409** while a seed of the service is queued or running, naming it; **404/403** as every service route |
| `POST …/cache/seeds?dryRun=true` | **200** with the count per level, whether a layer draws there and each level's `estimatedBytes`; `estimate` — `bytes`, `budget`, `used`, `free`, `protectedBytes`, `evictionTarget`, `fits`, `maxZoomThatFits`, `sampled`; `quotaEstimate`, the same fields against the service's quota (where `budget` is the quota), or null for a service with none; and `refusal`, the sentence a start would be refused with, when it does not fit either. Nothing is written. Over the cap it is the start's 400 |
| `PUT /admin/services/{name}/cache/quota` `{megabytes}` | Added 2026-09-29 ([ADR-010](ADR-010-caching.md) §3): sets the service's tile cache quota, or clears it with `null`; **400** for zero or less; audited as `service.cache.quota` with what it was and became. The privilege, the address and the ownership check are this section's |
| `GET /admin/services/{name}/cache/seeds` | The service's seeds, newest first |
| `GET …/cache/seeds/{id}` | One seed: status, per level `tiles`/`done`/`built`/`present`/`empty`/`failed`/`skipped`, started and finished, the pause, and the estimated time left from the observed rate |
| `DELETE …/cache/seeds/{id}` | **200** cancelled; **409** when it has already ended |
| `GET /admin/services/{name}/cache` | [ADR-010](ADR-010-caching.md) §6b: for each level a seed has finished, when, over what area, how many tiles the area holds, and how many are cached and fresh **now** for every layer drawn there; any running seed; the defaults, the cap and the concurrency; and since 2026-09-29 `budget` — the cache's budget, what the whole cache uses and has free, and this service's share (§5.9); `quota` — the service's quota in megabytes or null, what its tiles hold, and what the quota has evicted since the process started; `stale` — how many answers were served stale ([ADR-010](ADR-010-caching.md) §5.1a) and the server's limit; and per layer its `staleSeconds` and whether it is the layer's own |

Starting and cancelling are audited as `service.cache.seed` and `service.cache.seed.cancel`. Since 2026-09-29 a
start's record carries the estimate, the free space, what outranked the seed, whether it fitted, and
`force`, so a seed that evicted its own top levels is traceable to the person who was told it would and went on. A seed is also
a job, so `/admin/jobs` lists it as `tile.seed`.

**INFERRED, listed for confirmation:** the routes' shape under `/cache`; that a service's manager, not only
the seed's owner, may cancel it.

### 5.7 The upgrade

**Nothing seeds automatically.** When `TilePipeline.Version` moves, the cache starts empty and the operator
starts a seed of the services that need one. The console's *Tile cache* page and the §5.6 read-back say so.

### 5.8 The console

> **Amended 2026-10-01 by [ADR-102](ADR-102-the-studio-item-is-one-page-and-every-setting-has-one-home.md).** The cache page is Settings › Tile layer on the item, reached by *Manage tiles* on Overview, instead of a page under each layer.

> **Amended 2026-09-30 by [ADR-101](ADR-101-studio-is-walked-the-way-a-portal-user-walks-it.md).** The page leads with what the cache does and what it holds, and a *Clear cached tiles* control (`POST /admin/services/{name}/cache/clear`). A seed is offered as *Pre-build tiles for an area*: levels as scales, the area from a map on the page itself instead of the Visualization tab's. The stale limit, quota, budget and per-level table are under *Advanced*. The mechanism below is unchanged.

The layer's *Caching* page, under *Tile cache*, has a *Seed the cache* box for the layer's service. It has
the levels (defaulted by the server), the area (the whole service, or the map's current extent when the map
is showing), *Count tiles* (the dry run), *Start*, and while a seed runs, its progress polled every two
seconds and *Cancel*. Under it is the per-level table from §5.6. Every request sends the folder.

Since 2026-09-29 the page also sets, above the seed box, the layer's **stale limit** — how many hours past its
lifetime a tile may stand in while the source is down, empty for the server's own — and the service's **cache
quota** in megabytes, empty for none; the service's use against its quota and what it has evicted are shown
under the budget line ([ADR-010](ADR-010-caching.md) §3 and §5.1a).

### 5.9 Size, and which end the cache evicts — added 2026-09-29

Owner decision, 2026-09-29: *a seed warns before it outgrows the cache, and low levels are evicted last.*

- **The estimate.** Before a seed starts, and in the dry run, the server estimates the bytes it will add
  (`TileSeedEstimate`). For each level and each layer drawn there: the tiles of the seed's rectangle not
  already cached under the key the seed writes, times the average size of that layer's cached tiles at that
  level when the cache holds at least 16 of them, and otherwise a default — **16 KB at level 16 and above,
  doubling for each level below, up to 1 MB**, per layer. Empty tiles are zero-length markers and average in
  as zero.
- **What *fits* means — amended the same day, by the coordinator's review.** The owner's worry is a seed
  evicting **its own** tiles, not a seed evicting anybody's. The first version compared the estimate with
  the free space — the budget (`Graticula:TileCacheBudgetMB`, 2,048 by default, a setting since before this
  ADR) less what the cache holds — and so refused almost every seed on a warm cache, whose LRU keeps it near
  its budget; the warning became noise. Under the eviction order below, a seed makes its room from whatever
  sits deeper than it and loses a tile of its own only when what outranks that tile does not leave room. So
  a level `L` of the seed is **kept** when the seed's bytes up to `L` fit in the free space (nothing is
  evicted), **or** when they fit, together with everything the cache holds of this pipeline at levels below
  `L`, in the 90% eviction brings it down to. The seed **fits** when its highest level is kept, and
  `maxZoomThatFits` is the highest level that is. `free` stays in the answer as information.
- **Why every level below `L`, and not only those below the seed's first level.** The coordinator's rule
  counted only the tiles below the seed's lowest level, which outrank every seed tile. Another service's
  tiles at a level **inside** the seed's range outrank the seed's higher levels too: a seed of levels 15–16
  over a cache holding other maps' level 15 loses its own level 16 before their level 15, and the narrower
  rule would call it a fit (`TileSeedEstimateTests.Tiles_held_below_a_seed_level_outrank_it_even_inside_the_seeds_range`).
  The per-level rule is the same rule applied to each level; it reduces to the coordinator's when nothing is
  held inside the seed's range.
- **The refusal.** A seed that does not fit is a **400** with `details: ["exceedsCacheBudget"]`, naming the
  estimate, the bytes that outrank its top level, the eviction target and budget, the highest level that
  keeps every tile, and — as information — the free space and use: the cap's refusal (§5.2) in shape. The cap
  is checked first and cannot be overridden; this can.
- **The override.** `"force": true` in the body starts the seed anyway, for an operator who has raised the
  budget or accepts the eviction. It is in the audit record.
- **The eviction order.** `FileSystemTileCache` evicts, down to 90% of the budget as before: tiles another
  pipeline version wrote first (they are unreachable, D-155), then the **highest level first**, then the
  least recently used within a level. A strict order and not recency weighted by level, because any finite
  weight is overtaken by a seed that runs long enough; it is bounded because every level below `z` together
  holds about a third as many tiles as `z` over any area. The cost is that a map browsing deep levels works
  in what the low levels leave free.
- **The console** shows the estimate beside the count, the refusal when it does not fit and *Seed anyway*,
  which sends `force`; the cache's budget and use are shown under the box.

- **The service's quota — added 2026-09-29, owner decision** ([ADR-010](ADR-010-caching.md) §3). A service may
  have a quota of its own, and a seed of it is measured against the quota by **the same rule**, with the quota in
  the budget's place: a level is kept when the seed's bytes up to it fit in what the quota leaves free, or when
  they fit, with **the service's own** tiles of this pipeline below it, in 90% of the quota. Only the service's
  own tiles, because a quota evicts nothing else — another service's low levels outrank nothing inside it. A
  seed that fits the budget and not the quota is a **400** marked `exceedsCacheQuota`, in the budget refusal's
  shape and naming the quota; the budget is checked first, and `force` overrides either. Every build a seed
  makes carries the quota to the write, as serving's does, so a forced seed evicts its own highest levels
  inside the quota rather than other services' tiles.
- **A refused build pauses a seed, and is never answered stale for it.** [ADR-010](ADR-010-caching.md) §5.1a,
  built the same day, answers a map's refused tile from the expired copy; that fallback is in the serving path
  only, so a seed's refused tile is the pause of §5.4 as before, and a seed's `present` never counts a stale copy.

**INFERRED, listed for confirmation:** the default guess and its shape; 16 samples as enough; 400 rather than
409 for a seed that does not fit (the owner allowed either; 400 matches the cap); the fit counting every
level below each seed level, rather than only below its first (the coordinator's wording, widened for the
case above); the 90% target rather than the budget as the line once eviction starts; a strict level order
rather than a weighted one; another pipeline's tiles first.

## 6. Consequences

**Positive.**
- The first map after a new service, an emptied cache or an upgrade can be served from the cache.
- ADR-010 condition 3 is discharged with the mechanism it named, and ADR-011 condition 1's vacuous half is
  not vacuous any more: a job now reads a data source and takes a lease to do it.
- `JobStatus.Cancelled` is reachable, for the one kind whose leftovers are decided.

**Negative.**
- One seed at a time per node, across all services.
- ~~The cache's LRU budget can evict what a large seed built first (§3).~~ *(2026-09-29: it evicts the highest
  level first now, and a seed that would evict its own tiles is refused unless forced — §5.9.)* A seed that
  fits still evicts other maps' deeper tiles, by design.
- A seed that kills its process is retried after every restart until cancelled (§3).
- A build older than migration 61 cannot list jobs once a seed row exists: it reads an unknown kind and
  throws, as a build before migration 29 did.
- How long a real estate takes to seed is still unknown (A-020).

**Ports created.** `ITileSeedStore` (platform store), beside `IJobStore`.

**State.** *Catalogue*: `tile_seed` (one row per seed: service, levels, area in 3857, concurrency, total,
pause) and `tile_seed_level` (one row per seed and level: cursor, five counts, started, finished), both
deleted with their job, and `tile_seed` with its service. *Runtime*: the worker's map of seeds it is running
on this node, one at a time.

## 7. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| A-020 | Cache seeding absorbs the provider performance gap | `UNVALIDATED` — seeding is built; how long a realistic estate takes is not measured |
| — | Two concurrent builds per seed is polite enough for a shared source | Reasoned from the per-source limit (2 of 24), not measured under load |
| — | 250,000 tiles is a useful cap | Reasoned: a metropolitan area to about level 16 (95,258 for Istanbul's box); not asked of an operator |

## 8. Dependencies

**Depends on:**
- [ADR-010](ADR-010-caching.md) §6, §6a, §6b and condition 3, which decided the shape.
- [ADR-011](ADR-011-job-system.md) §3.2–§3.7, for the claim, the lease, the re-run declaration and the
  shared budget.
- [ADR-046](ADR-046-admission-control-bounds-the-queue-not-the-wait.md), for `ConnectionBudget`.
- [ADR-070](ADR-070-a-layer-has-a-visible-scale-range.md), for which levels a layer is drawn at.
- [ADR-075](ADR-075-a-layer-is-edited-by-its-owner.md), for who may start and stop a seed.

**Depended on by:** [ADR-098](ADR-098-vector-tiles-can-be-exported-as-a-package.md), whose export walks through this seed's plan, walk, per-tile loop,
admission and cancel, and uses its estimate.

## 9. Revisit triggers

- A seed measured against a realistic estate. A-020 is validated or invalidated, and Alternative C may come
  back.
- An operator asks for seeds of several services to run at once on one node.
- ~~A seed is seen evicting its own low levels (§3). The cache budget then needs a notion of seeded tiles.~~
  *(Answered before it was seen, 2026-09-29: the highest level is evicted first — §5.9.)* What replaces it: a
  map browsing deep levels is seen starved by a seeded pyramid, or a seed's estimate is seen far from what it
  wrote.
- ~~D-277 is decided. If serving takes a permit too, a seed and a map compete on equal terms.~~ *(Decided
  2026-09-29: serving takes one, and they do.)*
- An import is made cancellable. `CancelAsync`'s kind restriction moves.

## 10. Dissent

None recorded.
