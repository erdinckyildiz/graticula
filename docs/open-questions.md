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
| Q-02 | What is the modern equivalent of ArcSOC — shared, dedicated, or hybrid workers? | Council | ADR-007 |
| Q-03 | Where does each class of geometry work execute: our code, the adopted engine, or the database? | Council | ADR-003, ADR-008 |
| Q-04 | What is the concrete DB connection budget at 1,000 services? | Council | ADR-007 |
| Q-08 | **[OWNER]** Does the platform own its data, or serve data registered from elsewhere? Migration goal makes *registering existing data* the more likely answer, but it is unconfirmed and it reshapes publishing (§38). | Owner | [product-context.md](product-context.md) |
| Q-16 | **[OWNER]** Is migration tooling in scope, or API compatibility only? Moving service definitions, styles, caches and client apps is a much larger commitment than serving a compatible API. | Owner | [product-context.md](product-context.md) |
| Q-17 | **[OWNER]** Which compatibility surface — an ArcGIS-compatible REST surface, standards-based (WMS/WFS/WMTS) for GeoServer displacement, or both? Depends on which deployments are actually being displaced. | Owner | ADR-005, compatibility layer design |
| Q-18 | What genuinely justifies a server over static cloud-native publishing **and over a stateless thin server plus a capable database**? Sharpened by [research/postgis-thin-servers.md](research/postgis-thin-servers.md): pg_tileserv deletes publishing, authorization, geoprocessing and MVT encoding with one constraint. Each subsystem we keep must be individually defensible. | Council | [product-context.md](product-context.md), assessment §8-§10 |
| Q-19 | Does the platform need an in-process spatial compute engine of its own, independent of the provider — and is DuckDB it? The central question of [research/duckdb-geoparquet.md](research/duckdb-geoparquet.md) (P3). | Council | ADR-008, ADR-002 |
| Q-20 | How many distinct GEOS builds end up evaluating our predicates (PostGIS's, ours, possibly DuckDB's), and how do we prevent behavioural divergence between them? Applies today, before any DuckDB decision. | Council | ADR-003, `experiments/geometry-oracle` |
| Q-23 | Can a PROJ `PJ` transformation object be used from more than one thread, or is it thread-affine? Decides whether prepared transformations are shared or duplicated per thread — tile and render hot path, and it changes what L1 costs. | Council | ADR-010, [research/dependency-thread-safety.md](research/dependency-thread-safety.md) §4 |
| Q-24 | ~~Do we require GDAL 3.10 or later for read-only raster thread safety?~~ **Largely moot after the vector-first decision** — GDAL leaves the request path for imagery. Retained only because file-based *vector* providers still hit GDAL per request, and RFC 101 is raster-only, so it does not help them. | Council | ADR-009, deployment |

## Open, not blocking

| # | Question | Owner | Notes |
|---|---|---|---|
| Q-09 | Should the platform be polyglot (§80.2)? | Council | Distinct from ADR-001. Most likely relevant to geoprocessing extensions. Must not be used to dodge the core language decision. |
| Q-10 | Do we ship a third-party plugin system at all, or only internal extension points? | Council | ADR-006 option 5. §82 says an unrequested extension system is a large permanent cost. |
| Q-11 | Where does the service catalog's identity model come from — who mints stable IDs, and how do renames work (§37)? | Council | Cheap to get right now, expensive later. |
| Q-12 | How is monitoring cardinality bounded at 1,000 services? | Council | Per-service × per-worker × per-endpoint labels multiply. The metrics backend can fail before the GIS server does. |
| Q-13 | What is the upgrade and rollback story, including DB schema migrations (§80.35–37)? | Council | **Forced early by ADR-002 §4.5** — the platform owns a schema from day one, so this can no longer be deferred. Also now covers the export/import format's compatibility guarantees. |
| Q-14 | Which CRS set must be supported at launch, and is on-the-fly reprojection a hot path or a convenience? | Council | Affects caching keys and tile pipeline design. |
| Q-29 | Which platform stores ship in v1, and which are designed-for-but-deferred? SQLite and PostgreSQL first is defensible; claiming SQL Server or Oracle support without CI coverage is not. | Council → Owner for priority | ADR-002 §4a.6 |
| Q-30 | How is cross-node change notification done without `LISTEN`/`NOTIFY`? Polling a change-sequence column is the portable default; confirm it is adequate for cache invalidation latency. | Council | ADR-002 §4a.4, ADR-010, §45 |
| Q-32 | **[OWNER]** Is a managed hosted datastore in scope for v1, or a later phase? It is the largest single addition currently on the table and it overlaps with publishing (§38). | Owner | [research/hosted-datastore-and-tiles.md](research/hosted-datastore-and-tiles.md) §4 |
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
| Q-31 | Does the feature service expose provider capability differences, or hide them? | **Exposed, as a product distinction rather than a matrix: hosted data gets full capability, registered data gets whatever its provider supports.** Confirm in ADR-005 and ADR-008. | 2026-08-12 | [research/hosted-datastore-and-tiles.md](research/hosted-datastore-and-tiles.md) §4 |
| Q-21 | Does the Query AST target more than one SQL dialect from day one? | **Yes, necessarily.** Oracle Spatial and SQL Server Spatial are first-class alongside PostGIS. Capability negotiation is core, not a refinement. | 2026-08-12 | [ADR-008](adr/ADR-008-query-engine.md), [research/multi-database-consequences.md](research/multi-database-consequences.md) |
| Q-22 | Is a PostgreSQL-free deployment profile a goal? | **Yes.** PostgreSQL is not mandatory. Reframed into Q-29 (which stores ship when). A-009 invalidated. | 2026-08-12 | [product-context.md](product-context.md), [ADR-002](adr/ADR-002-primary-data-architecture.md) §4a |
| Q-05 | Does platform metadata live in PostgreSQL, in files, or both? | **A relational store is the source of truth; files are an export/import format.** Amended same day: the store is portable across SQLite, PostgreSQL, SQL Server and Oracle. The platform database is logically distinct from data source databases, which may be foreign and read-only. `ACCEPTED WITH CONDITIONS` — see the conditions in ADR-002 §9. | 2026-08-12 | [ADR-002](adr/ADR-002-primary-data-architecture.md) |
| Q-07 | Is displacing existing ArcGIS Server / GeoServer deployments a goal? | **Yes.** The compatibility layer (§51) is therefore a product requirement, not an option — still strictly outside the core domain, still strictly clean room. Raises Q-16 and Q-17. | 2026-08-12 | [product-context.md](product-context.md) |
