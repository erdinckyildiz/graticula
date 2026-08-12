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
| Q-05 | Does platform metadata live in PostgreSQL, in files, or both? | Council | ADR-002 |
| Q-08 | **[OWNER]** Does the platform own its data, or serve data registered from elsewhere? Migration goal makes *registering existing data* the more likely answer, but it is unconfirmed and it reshapes publishing (§38). | Owner | [product-context.md](product-context.md) |
| Q-16 | **[OWNER]** Is migration tooling in scope, or API compatibility only? Moving service definitions, styles, caches and client apps is a much larger commitment than serving a compatible API. | Owner | [product-context.md](product-context.md) |
| Q-17 | **[OWNER]** Which compatibility surface — an ArcGIS-compatible REST surface, standards-based (WMS/WFS/WMTS) for GeoServer displacement, or both? Depends on which deployments are actually being displaced. | Owner | ADR-005, compatibility layer design |
| Q-18 | What genuinely justifies a server over static cloud-native publishing **and over a stateless thin server plus a capable database**? Sharpened by [research/postgis-thin-servers.md](research/postgis-thin-servers.md): pg_tileserv deletes publishing, authorization, geoprocessing and MVT encoding with one constraint. Each subsystem we keep must be individually defensible. | Council | [product-context.md](product-context.md), assessment §8-§10 |
| Q-19 | Does the platform need an in-process spatial compute engine of its own, independent of the provider — and is DuckDB it? The central question of [research/duckdb-geoparquet.md](research/duckdb-geoparquet.md) (P3). | Council | ADR-008, ADR-002 |
| Q-20 | How many distinct GEOS builds end up evaluating our predicates (PostGIS's, ours, possibly DuckDB's), and how do we prevent behavioural divergence between them? Applies today, before any DuckDB decision. | Council | ADR-003, `experiments/geometry-oracle` |
| Q-21 | Does the Query AST target more than one SQL dialect from day one, to keep the provider abstraction honest rather than PostGIS-shaped? | Council | ADR-008 |
| Q-22 | Is a PostgreSQL-free deployment profile a goal, or is PostGIS an acceptable hard requirement? Challenges A-009. | Council → possibly Owner | ADR-002 |
| Q-23 | Can a PROJ `PJ` transformation object be used from more than one thread, or is it thread-affine? Decides whether prepared transformations are shared or duplicated per thread — tile and render hot path, and it changes what L1 costs. | Council | ADR-010, [research/dependency-thread-safety.md](research/dependency-thread-safety.md) §4 |
| Q-24 | Do we require GDAL 3.10 or later for read-only raster thread safety? A hard minimum version constrains air-gapped and distro-packaged deployments. | Council | ADR-009, deployment |

## Open, not blocking

| # | Question | Owner | Notes |
|---|---|---|---|
| Q-09 | Should the platform be polyglot (§80.2)? | Council | Distinct from ADR-001. Most likely relevant to geoprocessing extensions. Must not be used to dodge the core language decision. |
| Q-10 | Do we ship a third-party plugin system at all, or only internal extension points? | Council | ADR-006 option 5. §82 says an unrequested extension system is a large permanent cost. |
| Q-11 | Where does the service catalog's identity model come from — who mints stable IDs, and how do renames work (§37)? | Council | Cheap to get right now, expensive later. |
| Q-12 | How is monitoring cardinality bounded at 1,000 services? | Council | Per-service × per-worker × per-endpoint labels multiply. The metrics backend can fail before the GIS server does. |
| Q-13 | What is the upgrade and rollback story, including DB schema migrations (§80.35–37)? | Council | Frequently deferred, then discovered to constrain the data model. |
| Q-14 | Which CRS set must be supported at launch, and is on-the-fly reprojection a hot path or a convenience? | Council | Affects caching keys and tile pipeline design. |
| Q-15 | What does "air-gapped" concretely require — offline PROJ grids, GDAL driver data, font bundles, no telemetry? | Council | Currently a slogan in the master prompt; needs a concrete checklist. |

## Answered

| # | Question | Answer | Date | Recorded in |
|---|---|---|---|---|
| Q-06a | Who is the primary user? | **The GIS administrator.** Promotes the admin API, service lifecycle observability, RBAC and good defaults to first-class requirements. | 2026-08-12 | [product-context.md](product-context.md) |
| Q-06b | What is the day-one workload? | **Features first, then vector tiles.** Confirms the §71–§73 sequencing and the walking-skeleton target. | 2026-08-12 | [product-context.md](product-context.md) |
| Q-07 | Is displacing existing ArcGIS Server / GeoServer deployments a goal? | **Yes.** The compatibility layer (§51) is therefore a product requirement, not an option — still strictly outside the core domain, still strictly clean room. Raises Q-16 and Q-17. | 2026-08-12 | [product-context.md](product-context.md) |
