# Open Questions Register

Do not hide uncertainty (§61). A question belongs here from the moment it is
noticed, not once it becomes convenient to answer.

**Ownership:** questions marked **[OWNER]** are product or business decisions and
are the only ones escalated to the project owner (§84). Everything else the
council investigates, prototypes, benchmarks and decides on its own.

---

## Blocking Phase 0

| # | Question | Owner | Resolves in |
|---|---|---|---|
| Q-01 | Which language, on measured evidence? | Council | ADR-001 |
| Q-03 | Where does each class of geometry work execute: our code, the adopted engine, or the database? | Council | ADR-003, ADR-008 |
| Q-04 | What is the concrete DB connection budget at 1,000 services, **per provider**? [ADR-007](adr/ADR-007-service-runtime.md) §4.8 gives the formula and the policy — pools per data source not per service, shrink-to-zero when idle, global cap per worker. The numbers are still missing and are a stated condition. | Council | ADR-007, `benchmarks/connection-budget/` |
| Q-08 | **[OWNER]** Does the platform own its data, or serve data registered from elsewhere? Migration goal makes *registering existing data* the more likely answer, but it is unconfirmed and it reshapes publishing (§38). | Owner | [product-context.md](product-context.md) |
| Q-16 | **[OWNER]** Is migration tooling in scope, or API compatibility only? Moving service definitions, styles, caches and client apps is a much larger commitment than serving a compatible API. | Owner | [product-context.md](product-context.md) |
| Q-17 | **[OWNER]** Which compatibility surface — an ArcGIS-compatible REST surface, standards-based (WMS/WFS/WMTS) for GeoServer displacement, or both? Depends on which deployments are actually being displaced. | Owner | ADR-005, compatibility layer design |
| Q-18 | What genuinely justifies a server over static cloud-native publishing **and over a stateless thin server plus a capable database**? Sharpened by [research/postgis-thin-servers.md](research/postgis-thin-servers.md): pg_tileserv deletes publishing, authorization, geoprocessing and MVT encoding with one constraint. Each subsystem we keep must be individually defensible. | Council | [product-context.md](product-context.md), assessment §8-§10 |
| Q-20 | How many distinct GEOS builds end up evaluating our predicates (PostGIS's, ours, possibly DuckDB's), and how do we prevent behavioural divergence between them? Applies today, before any DuckDB decision. | Council | ADR-003, `experiments/geometry-oracle` |
| Q-23 | Can a PROJ `PJ` transformation object be used from more than one thread, or is it thread-affine? Decides whether prepared transformations are shared or duplicated per thread — tile and render hot path, and it changes what L1 costs. | Council | ADR-010, [research/dependency-thread-safety.md](research/dependency-thread-safety.md) §4 |
| Q-24 | ~~Do we require GDAL 3.10 or later for read-only raster thread safety?~~ **Largely moot after the vector-first decision** — GDAL leaves the request path for imagery. Retained only because file-based *vector* providers still hit GDAL per request, and RFC 101 is raster-only, so it does not help them. | Council | ADR-009, deployment |

## Open, not blocking

| # | Question | Owner | Notes |
|---|---|---|---|
| Q-09 | Should the platform be polyglot (§80.2)? | Council | Distinct from ADR-001. Most likely relevant to geoprocessing extensions. Must not be used to dodge the core language decision. |
| Q-10 | Do we ship a third-party plugin system at all, or only internal extension points? | Council | ADR-006 option 5. §82 says an unrequested extension system is a large permanent cost. |
| Q-11 | Where does the service catalog's identity model come from — who mints stable IDs, and how do renames work (§37)? | Council | Cheap to get right now, expensive later. |
| Q-12 | How is monitoring cardinality bounded at 1,000 services? | Council | **Elevated by [ADR-007](adr/ADR-007-service-runtime.md) §5.** With worker, process and connection counts flat in service count, cardinality and catalog scale become the *only* things that grow. At 10,000 services this is the binding constraint, not the runtime. |
| Q-13 | What is the upgrade and rollback story, including DB schema migrations (§80.35–37)? | Council | **Forced early by ADR-002 §4.5** — the platform owns a schema from day one, so this can no longer be deferred. Also now covers the export/import format's compatibility guarantees. |
| Q-14 | Which CRS set must be supported at launch, and is on-the-fly reprojection a hot path or a convenience? | Council | Affects caching keys and tile pipeline design. |
| Q-29 | Which platform stores ship in v1, and which are designed-for-but-deferred? SQLite and PostgreSQL first is defensible; claiming SQL Server or Oracle support without CI coverage is not. | Council → Owner for priority | ADR-002 §4a.6 |
| Q-30 | How is cross-node change notification done without `LISTEN`/`NOTIFY`? Polling a change-sequence column is the portable default; confirm it is adequate for cache invalidation latency. | Council | ADR-002 §4a.4, ADR-010, §45 |
| Q-32 | **[OWNER]** The datastore is confirmed as part of the model. Remaining: is it implemented in v1 or a later phase? Registered-only is a coherent v1 if hosting slips. | Owner | [data-model.md](data-model.md) |
| Q-41 | Do we offer an optional companion schema in a registered database where granted rights, to hold versioning and editor-tracking bookkeeping? **Important again now that editing is in scope.** It is how ArcGIS gives referenced data advanced capability, and it is the difference between concurrency that survives direct database writes and concurrency that does not. | Council | [research/arcgis-datastore-model.md](research/arcgis-datastore-model.md) §4, [ADR-005](adr/ADR-005-api-architecture.md) §3.8 |
| Q-19 | **Deferred, not rejected.** Does the platform need an in-process compute engine (DuckDB) for capability gaps? [ADR-008](adr/ADR-008-query-engine.md) §4.3 declines to adopt one before we can name the queries it would rescue. **The evidence that reopens it is a list of real refused queries users legitimately need.** Recorded as dissent in ADR-008 §12. | Council | ADR-008 |
| Q-44 | What is the write surface? OGC API Features Part 4 is still a **draft**. Options: implement the draft and track a moving spec, ship our own additive extension and converge later, or both. [ADR-005](adr/ADR-005-api-architecture.md) §3.7 recommends the second — breaking changes to a published write API are far worse than to a read API. | Council | ADR-005 |
| Q-43 | What is the schema-drift polling interval, and what does it cost against a large registered database with many layers? If too expensive, fall back to check-on-error plus TTL. | Council | [data-model.md](data-model.md) §3, A-023 |
| Q-39 | If a registered source happens to be writable, do we offer hosted-grade capability there automatically or only on explicit opt-in? Automatic is friendlier and surprises DBAs. Opt-in is safer and gets forgotten. | Council | [data-model.md](data-model.md) §5 |
| Q-33 | If a datastore exists, does hosting copy data or replicate it continuously? Copy is simple and goes stale; continuous replication is a synchronisation product nobody asked for. Copy with explicit refresh is the likely answer, but it must be chosen rather than defaulted into. | Council | publishing (§38) |
| Q-34 | Are pre-generalised geometry tables per zoom level a datastore-only feature, or attempted on registered sources where we happen to have write access? | Council | ADR-008, ADR-010 |
| Q-35 | Which runtime schema changes do we offer, per dialect? Needs DDL cost and locking behaviour verified on all three engines first. Classification proposed in [research/runtime-schema-evolution.md](research/runtime-schema-evolution.md) §5. | Council | publishing (§38), ADR-008 |
| Q-36 | Can the service definition describe fields the physical table does not have — computed, aliased, role-hidden? Cheap to allow now, expensive to retrofit. | Council | data model (§37, §38) |
| Q-37 | What is the contract for a request in flight across a schema change? | Council | ADR-007, publishing (§38) |
| Q-28 | Can GDAL-backed providers be made optional, so that a PostGIS-only deployment is genuinely one artefact? If not, the single-binary criterion (ADR-001 C7) is largely neutralised for every candidate, since GDAL is native everywhere. | Council | ADR-001 §3.1, ADR-006, deployment |
| Q-27 | With imagery delivered as COG over range requests, how is per-layer authorization enforced — signed expiring URLs, a range-request proxy, or a hybrid? Signed URLs cannot express row-level rules or immediate revocation and may not exist on an air-gapped filesystem; proxying puts terabyte-scale bandwidth back through the server. | Council | ADR-009 §1a, security |
| Q-15 | What does "air-gapped" concretely require — offline PROJ grids, GDAL driver data, font bundles, no telemetry? | Council | Currently a slogan in the master prompt; needs a concrete checklist. |

## Answered

| # | Question | Answer | Date | Recorded in |
|---|---|---|---|---|
| Q-06a | Who is the primary user? | **The GIS administrator.** Promotes the admin API, service lifecycle observability, RBAC and good defaults to first-class requirements. | 2026-08-12 | [product-context.md](product-context.md) |
| Q-06b | What is the day-one workload? | **Features first, then vector tiles.** Confirms the §71–§73 sequencing and the walking-skeleton target. | 2026-08-12 | [product-context.md](product-context.md) |
| Q-25 | Do we support the MapLibre GL Style Spec as a style format? | **Effectively yes.** The vector-first decision makes the client the renderer, and MapLibre style is the format it speaks. Styles are stored and served, not evaluated. Formal confirmation belongs in ADR-004. | 2026-08-12 | [product-context.md](product-context.md) |
| Q-26 | How is cross-tile label consistency achieved? | **Closed, not answered.** Labels are placed client-side. This is no longer a problem the platform has. | 2026-08-12 | [product-context.md](product-context.md) |
| Q-31 | Does the feature service expose provider capability differences, or hide them? | **Expose them.** A capability report is published per layer, and unsupported operations are refused with an explanation rather than answered slowly by dragging data back. The organising principle is *never degrade silently*. Conditional on the capability report shipping with the first refusal. | 2026-08-12 (an earlier answer on 2026-08-12 was withdrawn as a misreading of the ArcGIS model) | [ADR-008](adr/ADR-008-query-engine.md) §2 |
| Q-02 | What is the modern equivalent of ArcSOC? | **Workers are sized to the machine, not to the catalogue.** A small number of multi-tenant request workers, threaded, with service contexts bound lazily and evicted LRU under a bounded per-worker budget; separate isolated job workers for geoprocessing, GDAL registration and plugins. Isolation on the workload axis, not the service axis. Services do not start, so there is no thundering herd. `ACCEPTED WITH CONDITIONS`. | 2026-08-12 | [ADR-007](adr/ADR-007-service-runtime.md) |
| Q-38 | Can a layer move between registered and hosted, keeping its identity? | **No.** Editing is done against the database directly, with QGIS. | 2026-08-12 | [data-model.md](data-model.md) |
| Q-40 | Do we accept data uploads / hosting at all? | **Yes.** The datastore is a first-class store and an organisation may run entirely hosted. It may also have no GIS database of its own, so datastore-only is a supported deployment. | 2026-08-12 | [data-model.md](data-model.md) §4 |
| Q-42 | Is feature editing through our API in scope? | **Yes.** An earlier answer of "no" was a wrong inference: the owner's statement about editing happening in the database concerned **table structure**, not feature data. Schema changes on registered layers happen at the source; feature data editing goes through our API. §28's CRUD, batch editing, editor tracking and optimistic concurrency are all in scope. | 2026-08-12 (corrected same day) | [data-model.md](data-model.md) §5, [ADR-005](adr/ADR-005-api-architecture.md) §3.8 |
| Q-33 | Does hosting copy once or track the source? | **Dissolved.** Hosted data is the system of record, not a copy of anything. Derived artefacts are openly derived and have a refresh policy. | 2026-08-12 | [data-model.md](data-model.md) §1 |
| Q-21 | Does the Query AST target more than one SQL dialect from day one? | **Yes, necessarily.** Oracle Spatial and SQL Server Spatial are first-class alongside PostGIS. Capability negotiation is core, not a refinement. | 2026-08-12 | [ADR-008](adr/ADR-008-query-engine.md), [research/multi-database-consequences.md](research/multi-database-consequences.md) |
| Q-22 | Is a PostgreSQL-free deployment profile a goal? | **Yes.** PostgreSQL is not mandatory. Reframed into Q-29 (which stores ship when). A-009 invalidated. | 2026-08-12 | [product-context.md](product-context.md), [ADR-002](adr/ADR-002-primary-data-architecture.md) §4a |
| Q-05 | Does platform metadata live in PostgreSQL, in files, or both? | **A relational store is the source of truth; files are an export/import format.** Amended same day: the store is portable across SQLite, PostgreSQL, SQL Server and Oracle. The platform database is logically distinct from data source databases, which may be foreign and read-only. `ACCEPTED WITH CONDITIONS` — see the conditions in ADR-002 §9. | 2026-08-12 | [ADR-002](adr/ADR-002-primary-data-architecture.md) |
| Q-07 | Is displacing existing ArcGIS Server / GeoServer deployments a goal? | **Yes.** The compatibility layer (§51) is therefore a product requirement, not an option — still strictly outside the core domain, still strictly clean room. Raises Q-16 and Q-17. | 2026-08-12 | [product-context.md](product-context.md) |
