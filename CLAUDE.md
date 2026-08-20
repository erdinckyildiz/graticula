# Project Rules

Named **Graticula** by owner decision 2026-08-17 —
[ADR-032](docs/adr/ADR-032-the-product-is-named-graticula.md). A *graticule* is the net
of meridians and parallels on a map, and `Graticula` is the Medieval Latin word English
borrowed it from. It replaces the working title `gis-server`, which survives in exactly
two places: where a document is recording its own history, and where the name is data
rather than an identifier — the `GisServer:*` configuration keys, still read so that no
existing deployment has to be reconfigured to start, and the default schema `gisserver`,
which is a deployment's choice and not the product's identity. Both are ADR-032 §5.
Namespaces, assemblies, projects and the solution were renamed with the product.

This repository designs a next-generation enterprise GIS application server from
first principles. The governing specification is
[MASTER_GIS_PLATFORM_PROMPT.md](MASTER_GIS_PLATFORM_PROMPT.md). Read it before
making architectural claims. Section references below (§n) point into it.

---

## 1. Current phase: Phase 1 — Implementation

**Phase 0 ended 2026-08-13 by owner decision.** Production code is written under
`/src`, with tests under `/tests`.

Scope is [docs/v1-scope.md](docs/v1-scope.md), which is authoritative: **PostGIS
only, ArcGIS FeatureServer, VectorTileServer and GeometryServer, hosted and
registered data.** Where any other document disagrees, v1-scope wins until that
document is amended.

**Phase 0 did not end because its criteria were met.** It ended because the
remaining criteria were judged unanswerable without running code, and that
judgement is recorded rather than implied. Of §81's sixteen:

- **Met:** the assessment exists (though stale — see *carried*), the ADRs exist
  with none `DRAFT`, the failure-scenario pass, the geometry and CRS pass, the
  2 AM scenarios, and Q-49.
- **Dissolved:** Q-49's *test with real GIS teams*, on the grounds that a gift
  owes no market case. **This silently removed the validation path for A-003 and
  five other assumptions** — see *carried*.
- **Carried into Phase 1, and they are debts rather than completions:** the §66
  review gates — **not none, and not all: some have run and some are still
  waiting, and several of those that ran reported failures that are only partly
  repaired.** Which is which is maintained in one place, the §66 table in
  [architecture-completeness.md](docs/architecture-completeness.md), and is read
  there rather than restated here. This sentence used to carry the tally itself
  and was wrong for a day, which is how it came to say where the tally lives
  instead; the contradiction sweep — **round 3 run
  2026-08-20** ([contradiction-sweep-3.md](docs/reviews/contradiction-sweep-3.md)),
  from the other direction: each decision against the code that now exists. Round 2
  ([contradiction-sweep-2.md](docs/reviews/contradiction-sweep-2.md)) stayed open
  because it did not cover the §66 gates, the ADR conditions or A-003. Round 3 read
  the conditions and left the gates to the gates, which now run; **what it found is a
  propagation failure rather than a contradiction** — decisions taken in one file and
  left standing in every document that restated them — and that is
  [D-126](docs/architecture-debt.md) and the general form
  [D-130](docs/architecture-debt.md), neither of them closed;
  [architecture-assessment.md](docs/architecture-assessment.md), **repaired
  2026-08-15** — not by rewriting it, which would have erased the record of what
  was believed before anything was built, but by a header that says section by
  section which parts are still true and what superseded the rest; the ADR conditions — **counted by
  [tools/conditions.py](tools/conditions.py) rather than estimated.** The old
  sentence guessed at both halves, said "roughly twenty-five" with none met, and
  was wrong by more than double with several already met. A number nobody can
  reproduce is a number nobody maintains, and the count moves every time an ADR
  is written, so **the figure is not written here at all** — run the tool, or read
  [the status page](docs/status.html), which `tools/registers-check.py` compares
  against the registers — **locally, and so far only locally.** This sentence said
  *which CI checks on every push* until 2026-08-18, when the Actions history was
  read for the first time: nine runs, every one a 0-second `startup_failure`, so
  no workflow in this repository has ever executed a step. The check is real and
  the workflow that would run it is written; what was not true is that anything
  ran it. [D-63](docs/architecture-debt.md). `tools/registers-check.py` now fails the build if a count reappears
  in this file, because it did, and it was stale within a day; **A-003**, the load-bearing assumption under
  ADR-007, which now has no validation route; and every finding in
  [independent review 3](docs/reviews/independent-review-3-synthesis.md) not
  removed by the v1 cut.

**The rule that replaces "do not write production code":** a `carried` item does
not become finished by being carried. Each is tracked in
[architecture-completeness.md](docs/architecture-completeness.md), and Phase 1
does not end with any of them still open.

`/experiments` and `/benchmarks` remain disposable and are **never promoted**.
Where an experiment validated a design — `RectClip`, `TileSimplify`, the MVT
encoder — the production implementation is written fresh, and the experiment
becomes a specification with a measured target attached.

## 2. Decision hygiene

- Every architectural decision becomes an ADR under `docs/adr/`, using
  [_template.md](docs/adr/_template.md). No exceptions, no informal decisions.
- Every ADR carries a **status** (`DRAFT`, `ACCEPTED`, `ACCEPTED WITH CONDITIONS`,
  `REQUIRES PROTOTYPE`, `REQUIRES BENCHMARK`, `REJECTED`, `DEFERRED`, `REOPENED`)
  and a **confidence** level (`HIGH`, `MEDIUM`, `LOW`).
- **A condition is discharged by an emphasised marker in its own text** —
  `**DISCHARGED` or `*(Discharged …)*` — anywhere in the item, and
  `PARTLY DISCHARGED` is a third state rather than a synonym for the first.
  [tools/conditions.py](tools/conditions.py) is the only implementation of that
  rule and [tools/status-page.py](tools/status-page.py) imports it. Added
  2026-08-15 after the two disagreed: the count was 22 of 99 and the truth was
  24, because three notes saying a condition was met sat past the first 200
  characters of a paragraph-long item, and one *partly* met was being counted as
  done. A condition may also be **deferred with its decision** — marked
  `*(Deferred …)*` — when v1 removed the thing it is a condition on. That is a
  third state, not a synonym for open: counting it beside live work makes the
  pile look larger than it is and makes the two indistinguishable to whoever is
  choosing what to do next.
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

**Do not reproduce** proprietary source, undocumented internals, protected
implementation details, or proprietary algorithms (§5). Any compatibility adapter
stays outside the core domain (§51).

The line is *reproduce*, and that is the line §5 draws. Until 2026-08-16 this
section also said existing products are studied "for publicly documented
behaviour and architectural reasoning only" — a restriction on **reading** that
§5 does not impose and that this repository was not applying consistently
anyway, since the conformance scripts and CI workflows it recommended reading are
themselves code. [ADR-030](docs/adr/ADR-030-reading-the-reference-implementation.md)
removes that tightening for the anonymised reference checkout under
`REFERENCES/`, by owner decision, and attaches three conditions: derivations are
disclosed in the text that carries them, reads are logged in
[reference-reading-log.md](docs/research/reference-reading-log.md), and a public
specification — not the reference — is the citation for anything a public
specification defines. The reference's own product name and `LICENSE` file are
scrubbed; neither may be cited as evidence.

Reading is not adopting. **Tier 3 is unchanged** (§4): a finished GIS server is
still never embedded, forked or vendored.

## 6. Anti-overengineering

For every proposed technology, answer: *what concrete problem does this solve?*
If the answer is unclear, it does not go in (§82). Kubernetes, Kafka, Redis,
service mesh, event sourcing, CQRS and microservice decomposition are all
explicitly on the challenge list. The baseline deployment target is:

```text
graticula  →  PostgreSQL/PostGIS
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

**Repository documentation is written in English**, and that has not changed:
ADRs, registers, reviews, code comments and commit messages stay English, because
the project is given away and its reasoning has to be readable by whoever picks
it up.

**Conversation with the project owner is in Turkish**, from 2026-08-15 at the
owner's request. It was English from 2026-08-12, also at their request; the
earlier line is left here rather than overwritten, because a rule that changes
twice is worth being able to see change.
