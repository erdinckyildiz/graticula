# Product Context

**Status:** ANSWERED — the blocking owner questions are closed. Remaining `TBD`
items are secondary.

---

## Why this document exists

The master prompt (§82) requires every proposed technology to answer *"what
concrete problem does this solve?"* — but never asks that question of the
product itself. An architecture cannot be evaluated against an unstated need:
"is this too complex?" is unanswerable without knowing who operates it and what
they are trying to do.

This document holds the answer. It is an input to every ADR and the yardstick
for every review gate.

## Decisions taken by the project owner

| Topic | Decision | Consequence |
|---|---|---|
| Licensing | Open source, copyleft (GPL/AGPL) acceptable | No dependency is excluded on licence grounds. LGPL/MIT are free of friction. AGPL obligations apply over the network for a server product and must be stated explicitly wherever relevant. |
| Scale target | 100–1,000 published services | Kubernetes, mandatory Redis, message brokers and container-per-service are out of scope for the baseline. Worker pooling and DB connection budgeting are in scope and serious: a naive process-per-service model does not survive 1,000 services. |
| Core language | Genuinely open, decided by evidence | [ADR-001](adr/ADR-001-core-language.md) requires a comparison *and a prototype*. No default. |
| Build vs adopt | Own the server domain; adopt foundational libraries behind our own ports; never adopt finished GIS server products | See [build-vs-adopt-policy.md](build-vs-adopt-policy.md) |
| **Primary user** | **The GIS administrator** | Answered 2026-08-12 — see below |
| **Day-one workload** | **Features first, then vector tiles** | Answered 2026-08-12 — see below |
| **Migration posture** | **Displacing existing ArcGIS Server / GeoServer deployments is a goal** | Answered 2026-08-12 — see below |

## The primary user is the GIS administrator

Not a developer self-hosting one application, and not an end user. Someone who
administers a GIS estate on behalf of an organisation.

This is the single most consequential input to the architecture, and it moves
several things from "nice" to "required":

- **The administrative API is a first-class deliverable, not a late phase.**
  Everything the UI can do must be automatable (§39). An administrator's job is
  operating many services, and operating many of anything means scripting it.
- **Service lifecycle must be observable** (§23). "Why is this service
  degraded?" must be answerable from the platform, not from log archaeology.
- **The 2 AM test (§7) becomes the primary operational gate**, not a slogan.
  Diagnosability outranks elegance wherever they conflict.
- **Defaults must be good, because per-service tuning will not happen.** At
  1,000 services no administrator hand-tunes worker counts. This raises the
  stakes on assumption A-008 considerably.
- **Multi-user, role-based access is inherent** (§41, §42), not an add-on. An
  administrator administers *for* other people.
- Publishing is a managed, validated, reversible workflow (§38) — not a file
  drop.

## Day one: features, then vector tiles

Confirms the master prompt's own sequencing (§71–§73) and sets the walking
skeleton target:

```text
PostGIS → provider → query engine → OGC API Features over HTTP
```

Priority consequence: [ADR-008](adr/ADR-008-query-engine.md) (query engine) and
[ADR-002](adr/ADR-002-primary-data-architecture.md) rise; rendering
([ADR-004](adr/ADR-004-rendering-engine.md)) and raster
([ADR-009](adr/ADR-009-raster-engine.md)) can be decided later — but not so late
that the runtime model is fixed without accounting for their worker classes.
Deciding ADR-007 as if only feature workloads existed would be a mistake.

## Migration is a goal — the compatibility layer is a requirement

Displacing existing ArcGIS Server and GeoServer deployments is an explicit
objective. Under §51 this promotes the compatibility layer from *optional
investigation* to *product requirement*, with three consequences:

1. **It stays outside the core domain.** §51 is unambiguous, and this becomes
   more important, not less, now that the layer is required. The core domain
   must not learn the shape of anyone else's API. It is an adapter over the
   protocol-neutral internal interface — the same seam as every other protocol
   in [ADR-005](adr/ADR-005-api-architecture.md).
2. **Clean room applies with full force** (§5). A compatible API surface may be
   built from published, publicly documented protocol behaviour only. No
   proprietary source, no undocumented internals, no reverse engineering of
   protected implementation details. This constraint is not negotiable and needs
   to be restated in the compatibility layer's own design document.
3. **Migration is more than an API.** Displacement means moving service
   definitions, styles, caches and client applications. What is actually in
   scope — API compatibility only, or migration tooling as well — is now the
   open question Q-16, and it is a scoping question rather than a technical one.

Which surface to target is Q-17: an ArcGIS-compatible REST surface, standards
(WMS/WFS/WMTS) for GeoServer displacement, or both. The answer depends on which
deployments are actually being displaced, and it materially changes the work.

## Remaining open items

- `TBD` **Data ownership model.** Does the platform own its data (publish into
  it) or serve data owned by others (register existing PostGIS tables)? Given
  the migration goal, *registering existing data* now looks more likely — an
  organisation displacing GeoServer already has its data somewhere. Still needs
  confirmation; it significantly changes the publishing architecture (§38).
- `TBD` **Product name.** Working title `gis-server`.

## Why a server at all

This question must be answered in writing, because a credible answer to it
already exists in the opposite direction.

Client-side platforms such as **GeoLibre** (Tauri, MapLibre GL JS, DuckDB-WASM
Spatial, deck.gl) run a full analysis stack in the browser against remote
GeoParquet, PMTiles and COG over HTTP range requests — no server, no install, no
data leaving the machine. For a large class of published-data workloads, the
correct modern architecture is a static object store and a capable client. That
is not a hypothetical; it ships.

Under §82 the burden of proof is therefore on us. The current answer — to be
argued properly in the assessment, not asserted here — is that a server earns
its place when:

- **data changes**, so tiles and caches must be invalidated rather than
  regenerated wholesale;
- **access must be controlled** per user, per layer, per feature — a static
  bucket cannot do row-level authorization;
- **many services are managed by someone** on behalf of many consumers, which is
  precisely the primary-user answer above;
- **the query exceeds what a client can pull**, so computation must run next to
  the data;
- **editing is required**, with transactions, concurrency control and audit;
- **the environment is air-gapped or governed**, where public object storage is
  not an option.

Where none of these hold, the honest recommendation is to publish PMTiles and
skip the server. A platform that cannot say this about itself is not being
designed, it is being assumed.

This becomes a standing challenge for the Adversarial Reviewer (§6 Agent 12):

> Which parts of this platform are made unnecessary by publishing the data as
> static cloud-native formats?

Recorded as research topic `client-side-platforms.md` and referenced from
assessment §10.

## Non-goals

Stated so that reviews do not drift into them.

- Not a desktop GIS, not a data editor with a UI, not a spatial ETL suite.
- Not feature parity with any existing product (§1).
- Not a novelty exercise. Optimise for the technically strongest, simplest,
  operationally credible solution (§86).

## The standing test

Every architectural proposal is measured against two questions from the master
prompt:

> Could a GIS administrator realistically diagnose and repair this system at
> 2 AM? (§7 — Platform/Operations Architect)

> If ArcGIS Server, GeoServer, MapServer and QGIS Server had never existed as
> products, but we had every lesson learned from them, how would we design this
> today? (§86)
