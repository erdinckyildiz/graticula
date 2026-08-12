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
| A-001 | The tile path is CPU-bound enough that language performance materially affects capacity | `UNVALIDATED`, **now doubtful** | With `ST_AsMVT` pushdown the hot path may be database- and network-bound, in which case all candidates are adequate and ADR-001 turns on secondary criteria. `experiments/lang-slice` endpoints B vs C are designed to settle exactly this | ADR-001, ADR-003 |
| A-002 | Single-binary distribution is genuinely valuable for air-gapped installs, not just aesthetically pleasing | `UNVALIDATED` | Ask the owner; check real air-gapped install constraints | ADR-001 |
| A-003 | Most published services are idle most of the time, making shared workers viable | `UNVALIDATED` | Workload modelling; any real deployment telemetry we can obtain | ADR-007, ADR-010 |
| A-004 | Hot-path geometry overhead (allocation and/or FFI) is material enough to justify our own primitives | `UNVALIDATED` | `benchmarks/geometry-hotpath` | ADR-003, tile pipeline |
| A-005 | Geometry running in the same runtime meaningfully reduces defect resolution time versus FFI | `UNVALIDATED` | Judgement plus prototype experience; record honestly in `experiments/lang-slice` | ADR-001, build-vs-adopt policy |
| A-006 | One internal geometry representation can serve both the feature path and the tile path without a second conversion | `UNVALIDATED` | Prototype | ADR-003 |
| A-007 | Crash containment is required in practice, not merely in principle — workers really do die (GDAL on malformed input, plugins, OOM) | `CONTESTED`, weakening | GeoServer runs every service in one JVM with no isolation and is widely deployed successfully — evidence *against*, for managed-code paths. ArcGIS and QGIS Server run large native stacks and isolate — evidence *for*, on native paths. **Weakened further 2026-08-12:** the vector-first decision removes GDAL raster decoding from the request path, which was the strongest concrete case for isolation. Remaining candidates are registration/overview generation (job-shaped anyway), geoprocessing, and plugins. Resolve per-path via failure scenario review (§59) and fault injection, not globally | ADR-007, ADR-009, ADR-006 |
| A-008 | Administrators will not correctly hand-tune per-service worker settings, so defaults must be good and adaptive | `VALIDATING` — supported by prior art | ArcGIS Server's documented guidance asks administrators to "pare down the number of running service instances to as many as are needed", a per-service manual task at a scale where it will not happen. See [research/arcgis-som-soc.md](research/arcgis-som-soc.md) §4 (P8). Still needs a real operator's view to move to `VALIDATED`. | ADR-007 |
| A-011 | A distinguished central manager process is a robustness and recovery liability, so placement and routing state must be recoverable without one | `VALIDATING` — supported by prior art | Esri removed the SOM/SOC split at 10.1 citing robustness, reduced failure and simpler provisioning and recovery. See [research/arcgis-som-soc.md](research/arcgis-som-soc.md) §3.1 | ADR-007, ADR-012 |
| A-012 | The sharing question is really about per-service state size, binding cost and neighbour tolerance — not about "shared versus dedicated" | `VALIDATING` — supported by prior art | ArcGIS shared instances are restricted to map and image services with limited capabilities, and exclude geoprocessing; `VERIFY` ~50 cached service contexts per instance. See [research/arcgis-som-soc.md](research/arcgis-som-soc.md) §3.4 | ADR-007, §20 worker classes |
| A-013 | Our Tier 2 dependencies (GDAL, GEOS, PROJ) are thread safe enough to permit a threaded worker model | `VALIDATED` (2026-08-12) | Confirmed against upstream documentation: GEOS reentrant C API with one context per thread; PROJ one `PJ_CONTEXT` per thread; GDAL re-entrant with one dataset instance per thread. None forces process-per-worker. Three derived constraints remain live — see [research/dependency-thread-safety.md](research/dependency-thread-safety.md) §6 | ADR-007, ADR-003, ADR-009 |
| A-014 | Routing requests to workers already warm for a service materially improves L1 hit rate without wrecking load balance | `UNVALIDATED` | Prototype and benchmark under both uniform and skewed load. See [research/runtime-models-compared.md](research/runtime-models-compared.md) §3 | ADR-007, ADR-010 |
| A-015 | Per-service warm state is small — connections, schema, symbology, fonts, CRS — making bind/unbind cheap | `UNVALIDATED` | Measure. GeoServer's documented cache inventory supports it; if true it changes the shared-vs-dedicated calculation substantially | ADR-007, ADR-010 |
| A-018 | A deliberately boring platform schema can be supported across SQLite, PostgreSQL, SQL Server and Oracle at acceptable cost | `UNVALIDATED` | Design the schema and port surface, then check it against all four dialects on paper before implementing. Cost is a test matrix, not an architecture — but the matrix must actually run (Q-29) | ADR-002, ADR-011 |
| A-019 | In-process MVT encoding can meet our latency targets, since `ST_AsMVT` is unavailable on SQL Server and Oracle | `UNVALIDATED` — critical | `experiments/lang-slice` endpoint C, and `benchmarks/mvt-generation`. If this fails, the non-PostGIS providers cannot serve tiles at acceptable latency and the multi-database promise is hollow | ADR-001, ADR-008, tile pipeline |
| A-020 | Cache seeding absorbs the provider performance gap, so an expensive Oracle tile path is acceptable when paid once rather than per request | `UNVALIDATED` | Measure seed time for a realistic service set, and invalidation behaviour when data changes. See [research/hosted-datastore-and-tiles.md](research/hosted-datastore-and-tiles.md) §3 | ADR-010, ADR-008 |
| A-024 | A small residual executor plus explicit refusal is acceptable to real users, given a capability report published in advance | `UNVALIDATED` | The load-bearing assumption behind [ADR-008](adr/ADR-008-query-engine.md) §2 and §4.3. Validate against real query patterns; the evidence that invalidates it is a list of refused queries users legitimately need | ADR-008, ADR-005, Q-19 |
| A-022 | The datastore schema can be created and maintained on PostGIS, SQL Server Spatial and Oracle Spatial at acceptable cost | `UNVALIDATED` | Geometry column definition, spatial index creation and Oracle `SDO_GEOMETRY` metadata registration all differ. Design the schema against all three before implementing; PostGIS first. Follows from the no-mandatory-PostgreSQL decision — see [data-model.md](data-model.md) §2 | data model, publishing (§38) |
| A-023 | A cheap schema fingerprint can be polled often enough to detect drift on registered sources without loading the source database | `UNVALIDATED` | Measure `information_schema` query cost against a large database with many registered layers (Q-43). If too expensive, fall back to checking on error plus TTL | ADR-007, ADR-010, [data-model.md](data-model.md) §3 |
| A-021 | Filter, clip and simplify can be pushed down usefully on all three spatial dialects, with comparable enough semantics to produce equivalent tiles | `UNVALIDATED` | `VERIFY` the per-dialect table in [research/hosted-datastore-and-tiles.md](research/hosted-datastore-and-tiles.md) §2, then measure | ADR-008 |
| A-017 | Data sources will frequently be foreign and possibly read-only, so the platform cannot rely on DDL rights in them | `VALIDATING` | Follows from the confirmed migration goal — an organisation displacing GeoServer has PostGIS administered by someone else. Confirm via Q-08. Load-bearing for [ADR-002](adr/ADR-002-primary-data-architecture.md) §4.1–4.2 | ADR-002, ADR-008, publishing (§38) |
| A-016 | GDAL-backed providers can be made optional, so a PostGIS-only deployment ships as one artefact | `UNVALIDATED` | Design spike. If false, ADR-001's C7 single-binary criterion is largely neutralised for all candidates (Q-28) | ADR-001, ADR-006, deployment |
| A-010 | The 100–1,000 service target will not shift upward by an order of magnitude after launch | `UNVALIDATED` | Owner confirmation; revisit at each phase gate | ADR-007, ADR-012 |

**Priority.** A-013 is resolved — a threaded worker model is available. A-003 is the
load-bearing assumption under the shared-worker model. A-007 is now `CONTESTED`
and needs splitting into a managed-code path and a native-code path rather than
being answered once.

## Validated

| ID | Assumption | Validated | Evidence |
|---|---|---|---|
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
