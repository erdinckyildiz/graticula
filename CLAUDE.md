# Project Rules

Working title: **gis-server** (final product name: TBD)

This repository designs a next-generation enterprise GIS application server from
first principles. The governing specification is
[MASTER_GIS_PLATFORM_PROMPT.md](MASTER_GIS_PLATFORM_PROMPT.md). Read it before
making architectural claims. Section references below (§n) point into it.

---

## 1. Current phase: Phase 0 — Architecture Discovery

**Do not write production code.** (§3, §70, §85)

The only code permitted right now lives under `/experiments` and `/benchmarks`,
and exists solely to answer a specific open architectural question. Experiment
code is disposable. It is never promoted to production; if an experiment
succeeds, the production implementation is written fresh under the architecture
the experiment validated.

Phase 0 ends when `docs/architecture-assessment.md` is complete, the initial
ADRs exist, and the criteria in §81 are met.

## 2. Decision hygiene

- Every architectural decision becomes an ADR under `docs/adr/`, using
  [_template.md](docs/adr/_template.md). No exceptions, no informal decisions.
- Every ADR carries a **status** (`DRAFT`, `ACCEPTED`, `ACCEPTED WITH CONDITIONS`,
  `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`, `REJECTED`, `DEFERRED`, `REOPENED`)
  and a **confidence** level (`HIGH`, `MEDIUM`, `LOW`).
- Assumptions go in [architecture-assumptions.md](docs/architecture-assumptions.md)
  with a status. Invalidating an assumption triggers review of every ADR that
  depends on it (§11).
- Uncertainty is recorded, not hidden
  ([open-questions.md](docs/open-questions.md), §61).
- Temporary compromises go in
  [architecture-debt.md](docs/architecture-debt.md) (§62). Temporary architecture
  must not silently become permanent.
- Disagreement is recorded, not smoothed over. Do not manufacture consensus (§8).
- **Inference is labelled.** Where a decision is derived by interpreting
  something the project owner said, rather than stated by them directly, record
  it as `INFERRED` and list it for confirmation. Do not write it as decided.
  Added after adversarial review F12: editing scope inverted twice in one
  session because an inference was recorded as fact, and nothing in the process
  distinguished the two.

## 3. Evidence over taste

When a question is measurable, measure it (§57). "This will be faster" is not an
argument; a benchmark is. The standing challenge to any performance claim is:

> Where is the benchmark proving this assumption?

## 4. Build vs adopt

Governed by [build-vs-adopt-policy.md](docs/build-vs-adopt-policy.md). Summary:

- **Tier 1** (server domain: service model, runtime, query engine, API, catalog,
  caching, tiling pipeline, cartographic logic) — written by us, always.
- **Tier 2** (geometry topology, projection, raster I/O, rasterization) —
  established libraries permitted, but always behind our own port interface.
  No library type may appear in a Tier 1 signature.
- **Tier 3** (finished GIS server products: MapServer, GeoServer, QGIS Server) —
  never adopted, in whole or in part.

## 5. Clean room

Existing products are studied for **publicly documented behaviour and
architectural reasoning only** (§5). Do not reproduce proprietary source,
undocumented internals, or proprietary algorithms. Any compatibility adapter
stays outside the core domain (§51).

## 6. Anti-overengineering

For every proposed technology, answer: *what concrete problem does this solve?*
If the answer is unclear, it does not go in (§82). Kubernetes, Kafka, Redis,
service mesh, event sourcing, CQRS and microservice decomposition are all
explicitly on the challenge list. The baseline deployment target is:

```text
gis-server  →  PostgreSQL/PostGIS
```

Everything beyond that must be justified by evidence, and must remain optional.

## 7. Scope decisions already taken

These came from the project owner and are inputs, not open questions. See
[product-context.md](docs/product-context.md).

- **Licensing:** open source, copyleft (GPL/AGPL) acceptable. No commercial
  closed-source distribution constraint. LGPL and MIT dependencies are
  unproblematic; obligations are still tracked in
  [DEPENDENCY-LICENSES.md](DEPENDENCY-LICENSES.md).
- **Scale target:** 100–1,000 services. Larger figures are stress models, not
  requirements. Do not make small deployments painful to serve hypothetical
  huge ones (§60).
- **Language:** genuinely open. To be decided by evidence in
  [ADR-001](docs/adr/ADR-001-core-language.md), including a prototype.

## 8. Documentation language

Repository documentation is written in English. **Conversation with the project
owner is also in English**, from 2026-08-12 at the owner's request.
