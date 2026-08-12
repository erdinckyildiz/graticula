# gis-server

*(working title)*

A next-generation enterprise GIS application server, designed from first
principles.

**This repository currently contains no production code, by design.**

---

## What this is

An attempt to answer one question honestly:

> If we were designing an enterprise GIS application server from scratch today,
> with every lesson learned from ArcGIS Server, ArcSOC, GeoServer, MapServer,
> QGIS Server, PostGIS and the modern cloud-native geospatial ecosystem — but
> none of their historical constraints — what should it look like?

Not a clone of any of them. The full specification governing the work is
[MASTER_GIS_PLATFORM_PROMPT.md](MASTER_GIS_PLATFORM_PROMPT.md).

## Current phase

**Phase 0 — Architecture Discovery.** Research, independent analysis, structured
debate, prototypes where uncertain, benchmarks where measurable, and recorded
decisions. Implementation begins only after this phase passes review (§70, §81,
§85).

## Where to start reading

| Document | What it holds |
|---|---|
| [CLAUDE.md](CLAUDE.md) | The working rules. Read first. |
| [docs/product-context.md](docs/product-context.md) | What we are building and for whom. Has open items. |
| [docs/build-vs-adopt-policy.md](docs/build-vs-adopt-policy.md) | What we write ourselves, what we adopt, and where the seam goes. |
| [docs/architecture-assessment.md](docs/architecture-assessment.md) | The Phase 0 deliverable. Outline only so far. |
| [docs/adr/](docs/adr/) | Twelve architecture decisions, all still open. |
| [docs/open-questions.md](docs/open-questions.md) | What we do not know yet. |
| [docs/architecture-assumptions.md](docs/architecture-assumptions.md) | What we are betting on, and how each bet gets settled. |
| [docs/architecture-completeness.md](docs/architecture-completeness.md) | How far each area has actually been taken. |

## Design constraints already fixed

- **Baseline deployment is `gis-server` → PostgreSQL/PostGIS.** Nothing more may
  be *required* to run the platform. Kubernetes, Redis and message brokers are
  optional at most, and must earn their place with evidence (§82).
- **Scale target: 100–1,000 published services.** Larger numbers are stress
  models, not requirements. Small deployments must not be made painful to serve
  hypothetical large ones (§60).
- **Open source, copyleft acceptable.** No dependency is excluded on licence
  grounds; obligations are tracked in
  [DEPENDENCY-LICENSES.md](DEPENDENCY-LICENSES.md).
- **Must run air-gapped**, on Linux and Windows, on a laptop and on a server.
- **Every decision is reversible** (§10), including the language. Sunk cost is
  not an argument.

## Repository layout

```text
docs/           architecture documents and registers
docs/adr/       architecture decision records
docs/research/  raw research notes on existing systems
experiments/    disposable code that answers architectural questions
benchmarks/     repeatable measurements that settle disagreements
```

## Licence

Not yet chosen. See [DEPENDENCY-LICENSES.md](DEPENDENCY-LICENSES.md); the
decision is open and material, since an AGPL choice carries network-use
obligations for a server product.
