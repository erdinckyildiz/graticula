# Architecture Assumption Register

Every assumption an architectural decision rests on is recorded here (§11).

**Statuses:** `UNVALIDATED` · `VALIDATING` · `VALIDATED` · `CONTESTED` · `INVALIDATED` · `SUPERSEDED`

`CONTESTED` means evidence points both ways and the assumption is probably
stated at the wrong granularity. It is a signal to split the assumption, not to
pick a side.

**The rule that gives this register teeth:** invalidating an assumption triggers
review of every ADR listed in its *Depended on by* column. An assumption that no
ADR depends on is either mislabelled or unnecessary.

---

## Open assumptions

| ID | Assumption | Status | How it gets validated | Depended on by |
|---|---|---|---|---|
| A-001 | The tile path is CPU-bound enough that language performance materially affects capacity | **`VALIDATED` 2026-08-12** | Confirmed by [benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md). With `ST_AsMVT` unavailable on two of three providers, in-process encoding is real CPU work: 94 ms for a dense tile of which only 23 ms is database time. The earlier note that it might be database-bound applied only to the PostGIS fast path | ADR-001, ADR-003 |
| A-002 | Single-binary distribution is genuinely valuable for air-gapped installs, not just aesthetically pleasing | `UNVALIDATED` | Ask the owner; check real air-gapped install constraints | ADR-001 |
| A-003 | Most published services are idle most of the time, making shared workers viable | `UNVALIDATED` | Workload modelling; any real deployment telemetry we can obtain | ADR-007, ADR-010 |
| A-004 | Hot-path geometry overhead is material enough to justify our own primitives | **`VALIDATED` 2026-08-12, decisively** | [benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md). `NTS.Intersection` was **79% of the whole request**; our rectangle clipper took that stage from 438.6 ms to 7.0 ms, a 63x reduction. What we wrote is fast (transform 1.4 ms, encoder 3.8 ms); what we adopted was slow | ADR-003, tile pipeline |
| A-005 | Geometry running in the same runtime meaningfully reduces defect resolution time versus FFI | `UNVALIDATED` | Judgement plus prototype experience; record honestly in `experiments/lang-slice` | ADR-001, build-vs-adopt policy |
| A-006 | One internal geometry representation can serve both the feature path and the tile path without a second conversion | `UNVALIDATED` | Prototype | ADR-003 |
| A-007 | Crash containment is required in practice, not merely in principle — workers really do die (GDAL on malformed input, plugins, OOM) | `CONTESTED`, weakening | GeoServer runs every service in one JVM with no isolation and is widely deployed successfully — evidence *against*, for managed-code paths. ArcGIS and QGIS Server run large native stacks and isolate — evidence *for*, on native paths. **Weakened further 2026-08-12:** the vector-first decision removes GDAL raster decoding from the request path, which was the strongest concrete case for isolation. Remaining candidates are registration/overview generation (job-shaped anyway), geoprocessing, and plugins. Resolve per-path via failure scenario review (§59) and fault injection, not globally | ADR-007, ADR-009, ADR-006 |
| A-008 | Administrators will not correctly hand-tune per-service worker settings, so defaults must be good and adaptive | `VALIDATING` — supported by prior art | ArcGIS Server's documented guidance asks administrators to "pare down the number of running service instances to as many as are needed", a per-service manual task at a scale where it will not happen. See [research/arcgis-som-soc.md](research/arcgis-som-soc.md) §4 (P8). Still needs a real operator's view to move to `VALIDATED`. | ADR-007 |
| A-011 | A distinguished central manager process is a robustness and recovery liability, so placement and routing state must be recoverable without one | `VALIDATING` — supported by prior art | Esri removed the SOM/SOC split at 10.1 citing robustness, reduced failure and simpler provisioning and recovery. See [research/arcgis-som-soc.md](research/arcgis-som-soc.md) §3.1 | ADR-007, ADR-012 |
| A-012 | The sharing question is really about per-service state size, binding cost and neighbour tolerance — not about "shared versus dedicated" | `VALIDATING` — supported by prior art | ArcGIS shared instances are restricted to map and image services with limited capabilities, and exclude geoprocessing; `VERIFY` ~50 cached service contexts per instance. See [research/arcgis-som-soc.md](research/arcgis-som-soc.md) §3.4 | ADR-007, §20 worker classes |
| A-013 | Our Tier 2 dependencies (GDAL, GEOS, PROJ) are thread safe enough to permit a threaded worker model | `VALIDATED` (2026-08-12) | Confirmed against upstream documentation: GEOS reentrant C API with one context per thread; PROJ one `PJ_CONTEXT` per thread; GDAL re-entrant with one dataset instance per thread. None forces process-per-worker. Three derived constraints remain live — see [research/dependency-thread-safety.md](research/dependency-thread-safety.md) §6 | ADR-007, ADR-003, ADR-009 |
| A-014 | Routing requests to workers already warm for a service materially improves L1 hit rate without wrecking load balance | `UNVALIDATED` | Prototype and benchmark under both uniform and skewed load. See [research/runtime-models-compared.md](research/runtime-models-compared.md) §3 | ADR-007, ADR-010 |
| A-015 | Per-service warm state is small — connections, schema, symbology, fonts, CRS — making bind/unbind cheap | `UNVALIDATED` | Measure. GeoServer's documented cache inventory supports it; if true it changes the shared-vs-dedicated calculation substantially | ADR-007, ADR-010 |
| A-018 | A deliberately boring platform schema can be supported across **SQLite and PostgreSQL** at acceptable cost | `UNVALIDATED` — much weaker after Q-51 | Narrowed from four engines to two on 2026-08-12. Two dialects for non-spatial relational data is ordinary work rather than a structural risk. The four-way CI matrix and four job-claim implementations are gone | ADR-002, ADR-011 |
| A-019 | In-process MVT encoding can meet our latency targets | **`VALIDATED` 2026-08-12, with a caveat** | [benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md): 94 ms against `ST_AsMVT`'s 62 ms on a dense z14 tile, so the multi-database promise holds. **Caveat:** at z12 it is 1,471 ms against 428 ms, and simplify is 55% of that. Low zoom needs work, and neither SQL Server nor Oracle has been measured | ADR-001, ADR-008, tile pipeline |
| A-020 | Cache seeding absorbs the provider performance gap, so an expensive Oracle tile path is acceptable when paid once rather than per request | `UNVALIDATED` | Measure seed time for a realistic service set, and invalidation behaviour when data changes. See [research/hosted-datastore-and-tiles.md](research/hosted-datastore-and-tiles.md) §3 | ADR-010, ADR-008 |
| A-036 | RLS delegation and large streaming reads do not collide often — the layers wanting row-level security are not usually the layers wanting bulk export | `UNVALIDATED` | A delegated query runs in an explicit transaction holding `ACCESS SHARE` for the whole stream, which is exactly what ADR-007 §5b says must be short. Mitigations: statement timeouts, a lower feature cap on delegated layers, and delegation being per layer. If the same layers want both, this needs a better answer. See [security.md](security.md) §2.4 | ADR-007, ADR-008, security |
| A-035 | Data source count stays low relative to service count — many services share one registered database | `UNVALIDATED` — **unbounded** | [ADR-007](adr/ADR-007-service-runtime.md) §4.8's connection budget survives only because pools are per data source rather than per service. But nothing bounds source count, and a large enterprise plausibly registers a departmental database per team. Fifty sources at four workers and a pool of three is 600 connections — the same shape of number §4.8 was supposed to have eliminated. Auto-discovery as a first-class publishing mode makes high counts *more* likely. Raised by adversarial review F8 | ADR-007, Q-04 |
| A-032 | COG range-request traffic is view-proportional, so proxying it costs about what serving tiles costs | `UNVALIDATED` | [ADR-009](adr/ADR-009-raster-engine.md) §2.4 rests on it. A client fetches overview blocks for what is on screen, not the dataset — but this is a reasoned estimate, not a measurement. If imagery traffic dwarfs tile traffic the default flips to signed URLs | ADR-009 |
| A-033 | Target clients can render COG directly, so serving one format suffices | `UNVALIDATED` | True of modern web mapping stacks, false of simple WMTS consumers and older desktop tools — some of which are exactly what we are displacing. See [ADR-009](adr/ADR-009-raster-engine.md) §10 | ADR-009, compatibility layer |
| A-034 | Internal ports designed for our own use will be good enough to become a plugin contract later, without a redesign | `UNVALIDATED` | The risk is that internals grow assumptions no clean boundary can be drawn around, so the answer becomes "we cannot" rather than "we chose not to". Check at each phase gate | ADR-006 |
| A-030 | Lease expiry plus checkpoint verification prevents one job running twice across nodes | `UNVALIDATED` | A partitioned worker can keep working on a job whose lease was reclaimed elsewhere. Mitigated by verifying the lease at checkpoints. Test with induced partitions | ADR-011 |
| A-031 | Job rate stays low enough for a database-backed queue — jobs per minute, not messages per second | `UNVALIDATED` | If false, [ADR-011](adr/ADR-011-job-system.md) §3.1's rejection of an external broker reopens. Measure against a realistic estate doing registration, seeding and geoprocessing together | ADR-011 |
| A-028 | Administrators can and will declare layer volatility usefully, and keep it roughly accurate | `UNVALIDATED` | Unlike worker tuning, this is domain knowledge only the administrator has, so asking is reasonable. The risk is that it is set once at registration and never revisited while the data's real cadence changes | ADR-010 |
| A-029 | Tiles, not feature query responses, are where cache reuse actually is | `UNVALIDATED` | [ADR-010](adr/ADR-010-caching.md) §2 declines to cache feature responses thoroughly because their key space is unbounded. ArcGIS built a dedicated store for exactly that, so this is a real bet. Check against production traffic before treating it as settled | ADR-010 |
| A-026 | OGC API Features Parts 1+2+3 plus additive extensions can express what §28 requires, without bending the standard's meaning | `UNVALIDATED` | Map §28's requirements — statistics, aggregation, relationships, domains, subtypes, attachments, editor tracking — against the standard and count what must be an extension. A large extension set means the standard is a veneer and [ADR-005](adr/ADR-005-api-architecture.md) §10's dissent was right | ADR-005 |
| A-027 | Optimistic concurrency can be made correct against writes we never see, by relying on database-maintained versioning rather than our own event record | `UNVALIDATED` — **load-bearing for editing** | Design the concurrency check against a database-maintained row version on each provider, then test it by writing around the API. Getting this wrong produces silent lost updates | ADR-005, ADR-008, §28 |
| A-025 | A small number of pinned service contexts suffices for real deployments, and administrators will not pin everything | `UNVALIDATED` | Observe pin counts in real use. The global pin budget in [ADR-007](adr/ADR-007-service-runtime.md) §4.12 is the backstop if this is wrong; needing the backstop routinely means the guidance failed | ADR-007 |
| A-024 | A small residual executor plus explicit refusal is acceptable to real users, given a capability report published in advance | `UNVALIDATED` | The load-bearing assumption behind [ADR-008](adr/ADR-008-query-engine.md) §2 and §4.3. Validate against real query patterns; the evidence that invalidates it is a list of refused queries users legitimately need | ADR-008, ADR-005, Q-19 |
| A-022 | ~~The datastore schema can be created on PostGIS, SQL Server Spatial and Oracle Spatial at acceptable cost~~ | `SUPERSEDED` 2026-08-12 | **Retired by Q-32.** The datastore is PostGIS only, shipped as a managed appliance. The three-engine requirement was an over-application of the no-mandatory-PostgreSQL decision: that objection was about the operational burden of running a database, and a managed appliance removes most of it. Removes three spatial DDL implementations | — |
| A-023 | A cheap schema fingerprint can be polled often enough to detect drift on registered sources without loading the source database | `UNVALIDATED` | Measure `information_schema` query cost against a large database with many registered layers (Q-43). If too expensive, fall back to checking on error plus TTL | ADR-007, ADR-010, [data-model.md](data-model.md) §3 |
| A-021 | Filter, clip and simplify can be pushed down usefully on all three spatial dialects, with comparable enough semantics to produce equivalent tiles | `UNVALIDATED` | `VERIFY` the per-dialect table in [research/hosted-datastore-and-tiles.md](research/hosted-datastore-and-tiles.md) §2, then measure | ADR-008 |
| A-017 | Data sources will frequently be foreign and possibly read-only, so the platform cannot rely on DDL rights in them | `VALIDATING` | Follows from the confirmed migration goal — an organisation displacing GeoServer has PostGIS administered by someone else. Confirm via Q-08. Load-bearing for [ADR-002](adr/ADR-002-primary-data-architecture.md) §4.1–4.2 | ADR-002, ADR-008, publishing (§38) |
| A-016 | GDAL-backed providers can be made optional, so a PostGIS-only deployment ships as one artefact | `VALIDATED` by design decision, 2026-08-12 | Adopted as a rule rather than tested as a hope: **the serving container ships no GDAL**; it lives only in the job worker image. Any provider requiring GDAL is therefore an import source, not a serving provider. See [research/honua-server.md](research/honua-server.md) §2b | ADR-001 C7, ADR-009, deployment, Q-15 |
| A-037 | Allocation rate, not CPU, sets the tile-serving ceiling per worker | `UNVALIDATED` — **new 2026-08-12** | A concurrency benchmark on the tile path, watching allocation rate and GC pause rather than single-request latency. Opened by [benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md) finding 10: one z12 tile allocates 204 MB and single-request GC pauses reached 153 ms. If true, ADR-007 §4.1's worker sizing needs an allocation term it does not have | ADR-007 §4.14, ADR-010 §6a |
| A-010 | The 100–1,000 service target will not shift upward by an order of magnitude after launch | `UNVALIDATED` | Owner confirmation; revisit at each phase gate | ADR-007, ADR-012 |

**Priority.** A-013 is resolved — a threaded worker model is available. A-003 is the
load-bearing assumption under the shared-worker model. **A-037 is new and is the
first assumption this project found by measuring rather than by reasoning**: it
was not on anyone's list until a tile turned out to allocate 204 MB. A-007 is now `CONTESTED`
and needs splitting into a managed-code path and a native-code path rather than
being answered once.

## Failure impact — the load-bearing five

Added after adversarial review F4. The register's "depended on by" column is a
link, not an impact analysis, and one assumption failing can partially reverse a
flagship decision without that being written anywhere.

| ID | If it is false | What actually changes |
|---|---|---|
| **A-015** warm state is small | Context bind/unbind is expensive | ADR-007 §4.3 lazy binding becomes a cold-start latency problem; §4.4 eviction becomes costly so the budget shrinks; §4.12 pinning becomes the norm rather than the exception — **which recreates per-service resource allocation, the thing §3 said killed ArcSOC.** ADR-010's L1 model changes shape. This single assumption partially reverses ADR-007. |
| **A-019** in-process MVT meets latency targets — *validated on PostGIS 2026-08-12, the two engines this row is about are still unmeasured* | Tiles from SQL Server and Oracle are too slow | The multi-database promise is hollow for tiles. ADR-008 §4.8's tile path needs a different answer, ADR-010's seeding becomes mandatory rather than an optimisation, and ADR-001's weighting shifts again. |
| **A-014** affinity routing works and is stable | Blind routing | ADR-007 §4.4 is deleted; we become GeoServer with extra steps. Acceptable, but L1 hit rates and therefore ADR-010's value both fall, and the pinning budget becomes the only affinity mechanism. |
| **A-024** refusal plus a capability report is acceptable | Users reject it | ADR-008 §2's central choice fails. Combined with F3's amendment the fallback exists, but the native API would have to adopt best-effort too, which removes the principle entirely. |
| **A-027** concurrency correct against unseen writes | Silent lost updates | The worst defect class an editing API can have. Editing cannot ship. Q-41's companion schema stops being optional. |

## Validated

| ID | Assumption | Validated | Evidence |
|---|---|---|---|
| A-019 | In-process MVT encoding meets latency targets | 2026-08-12 | [benchmarks/mvt-generation/RESULTS.md](../benchmarks/mvt-generation/RESULTS.md). **94 ms against 62 ms** for `ST_AsMVT` at a dense z14 tile with 4,863 features — 1.5x, not an order of magnitude. The multi-database promise is not hollow. **Caveat:** at z12 it is 1,471 ms against 428 ms, and simplify is now 55% of that. Low zoom needs more work. |
| A-004 | Hot-path geometry overhead justifies our own primitives | 2026-08-12 | Same run, and it was not close. `NTS.Intersection` was **79% of the entire request**. Our rectangle clipper took that stage from **438.6 ms to 7.0 ms**. Our transform costs 1.4 ms and our MVT encoder 3.8 ms — **what we wrote is fast, what we adopted was slow.** |
| A-001 | The tile path is CPU-bound enough that language performance materially affects capacity | 2026-08-12 | Same run. The doubt recorded against this assumption came from `ST_AsMVT` pushdown, which exists only on PostGIS. On the path that has to work everywhere, 94 ms of a dense tile is ours and 23 ms is the database. It is CPU-bound in our process. This does **not** retroactively validate the language choice — ADR-001 §6 still stands: no language comparison was run. |
| A-013 | GDAL, GEOS and PROJ permit a threaded worker model | 2026-08-12 | [research/dependency-thread-safety.md](research/dependency-thread-safety.md). Deliberately left in the open table too: the headline is validated, but three derived constraints are still live design work — per-thread context lifecycle, GDAL dataset thread-affinity, and two contention points (PROJ grid cache mutex, GDAL block cache under concurrent writes). |

## Invalidated / superseded

| ID | Assumption | Status | What happened |
|---|---|---|---|
| A-009 | PostgreSQL/PostGIS is an acceptable hard dependency for the baseline deployment | `INVALIDATED` 2026-08-12 | The owner decided PostgreSQL is not mandatory: Oracle Spatial and SQL Server Spatial are first-class. The reasoning is that ArcGIS Server deployments run heavily on those engines, and requiring PostgreSQL purely for our metadata puts a barrier in front of the migration target. Superseded by **A-018**. Triggered a reopening of [ADR-002](adr/ADR-002-primary-data-architecture.md) — which its own §9.2 condition had anticipated, and the timing held: no metadata SQL had been written. **The register worked as designed, on its first real test.** |

---

## Recording rules

1. An assumption enters this register the moment an ADR relies on it — not later.
2. Every row names a concrete validation method. "We will see" is not a method.
3. Status changes are made here first, then propagated to the dependent ADRs.
4. Do not delete invalidated assumptions. Move them down and record what
   replaced them; the history is how we avoid rediscovering the same mistake.
