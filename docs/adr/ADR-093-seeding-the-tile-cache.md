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

- **The cache's size budget still applies, and it evicts the wrong end.** `FileSystemTileCache` evicts by
  least recent use. A seed larger than the budget (2 GB by default) evicts the tiles it built first — the
  low levels, which §6a calls the most valuable. The start response says so; nothing prevents it.
- **Serving a tile takes no permit, and a seed does.** `LayerConnections.TileSourceFor` hands out a source
  with no `ConnectionBudget` lease, so a cold tile a map asks for is not counted against the source. A seed
  is therefore more polite than the maps it runs beside. Recorded as [D-277](../architecture-debt.md).
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
| `POST /admin/services/{name}/cache/seeds` `{minZoom?, maxZoom?, extent?}` | **202** with the job id and where to watch it; **400** over the cap, with the count; **409** while a seed of the service is queued or running, naming it; **404/403** as every service route |
| `POST …/cache/seeds?dryRun=true` | **200** with the count per level and whether a layer draws there; nothing is written |
| `GET /admin/services/{name}/cache/seeds` | The service's seeds, newest first |
| `GET …/cache/seeds/{id}` | One seed: status, per level `tiles`/`done`/`built`/`present`/`empty`/`failed`/`skipped`, started and finished, the pause, and the estimated time left from the observed rate |
| `DELETE …/cache/seeds/{id}` | **200** cancelled; **409** when it has already ended |
| `GET /admin/services/{name}/cache` | [ADR-010](ADR-010-caching.md) §6b: for each level a seed has finished, when, over what area, how many tiles the area holds, and how many are cached and fresh **now** for every layer drawn there; any running seed; the defaults, the cap and the concurrency |

Starting and cancelling are audited as `service.cache.seed` and `service.cache.seed.cancel`. A seed is also
a job, so `/admin/jobs` lists it as `tile.seed`.

**INFERRED, listed for confirmation:** the routes' shape under `/cache`; that a service's manager, not only
the seed's owner, may cancel it.

### 5.7 The upgrade

**Nothing seeds automatically.** When `TilePipeline.Version` moves, the cache starts empty and the operator
starts a seed of the services that need one. The console's *Tile cache* page and the §5.6 read-back say so.

### 5.8 The console

The layer's *Caching* page, under *Tile cache*, has a *Seed the cache* box for the layer's service. It has
the levels (defaulted by the server), the area (the whole service, or the map's current extent when the map
is showing), *Count tiles* (the dry run), *Start*, and while a seed runs, its progress polled every two
seconds and *Cancel*. Under it is the per-level table from §5.6. Every request sends the folder.

## 6. Consequences

**Positive.**
- The first map after a new service, an emptied cache or an upgrade can be served from the cache.
- ADR-010 condition 3 is discharged with the mechanism it named, and ADR-011 condition 1's vacuous half is
  not vacuous any more: a job now reads a data source and takes a lease to do it.
- `JobStatus.Cancelled` is reachable, for the one kind whose leftovers are decided.

**Negative.**
- One seed at a time per node, across all services.
- The cache's LRU budget can evict what a large seed built first (§3).
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

**Depended on by:** —

## 9. Revisit triggers

- A seed measured against a realistic estate. A-020 is validated or invalidated, and Alternative C may come
  back.
- An operator asks for seeds of several services to run at once on one node.
- A seed is seen evicting its own low levels (§3). The cache budget then needs a notion of seeded tiles.
- D-277 is decided. If serving takes a permit too, a seed and a map compete on equal terms.
- An import is made cancellable. `CancelAsync`'s kind restriction moves.

## 10. Dissent

None recorded.
