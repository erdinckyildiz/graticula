# Product Context

**Status:** PARTIAL — open items marked `TBD` must be closed before Phase 1.

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

## Open items

These are not technical disagreements for the council to settle (§84) — they are
product questions only the owner can answer.

- `TBD` **Primary user.** Who operates this? A GIS administrator in an
  organisation, a platform team serving internal applications, or a developer
  self-hosting for one application? This determines how much operational
  complexity is acceptable and what the administration surface must look like.
- `TBD` **Primary workload.** Which capability must be excellent on day one —
  vector feature serving, vector tiles, server-side rendered maps, or raster
  imagery? All four eventually, but the first vertical slice must pick one.
  Current reading of the master prompt (§71–§73) implies **features, then vector
  tiles**.
- `TBD` **Migration posture.** Is displacing an existing ArcGIS Server or
  GeoServer deployment a goal? If yes, the compatibility layer (§51) is a
  requirement rather than an option, and it changes API priorities.
- `TBD` **Data ownership model.** Does the platform own its data (publish into
  it) or serve data owned by others (register existing PostGIS tables)? This
  significantly changes the publishing architecture (§38).
- `TBD` **Product name.** Working title `gis-server`.

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
