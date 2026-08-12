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
| Q-06 | **[OWNER]** Who is the primary user, and what is the day-one primary workload? | Owner | [product-context.md](product-context.md) |
| Q-07 | **[OWNER]** Is displacing an existing ArcGIS Server or GeoServer deployment a goal? Decides whether the compatibility layer (§51) is required or optional. | Owner | [product-context.md](product-context.md) |
| Q-08 | **[OWNER]** Does the platform own its data, or serve data registered from elsewhere? | Owner | [product-context.md](product-context.md) |

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

*(none yet — answers move here with a link to the ADR or document that settled
them, and the date)*
