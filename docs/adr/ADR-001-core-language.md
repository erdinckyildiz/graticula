# ADR-001 — Core Language

| | |
|---|---|
| **Status** | `REQUIRES PROTOTYPE` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

Every other structural decision narrows once this one is made: which geometry
engines are reachable without FFI, which rasterization backends have maintained
bindings, what the concurrency model looks like, how workers are isolated and
recycled, what deployment and air-gapped distribution cost, and who can
contribute.

The master prompt (§14) forbids assuming a language. The project owner has
confirmed there is no constraint: the decision is to be made on evidence.

This ADR gates ADR-002, ADR-003 and ADR-008. It should be settled first.

## 2. Candidates

Per §14: Go, Rust, C#/.NET, Java, TypeScript/Node.js, Python.

Initial reading — to be argued properly, not asserted:

- **Python** is implausible as the core request-serving language for this
  workload (CPU-bound tile and render paths, GIL, per-request overhead). It
  remains highly plausible as a *geoprocessing extension* language. The
  polyglot question (§80.2) is separate from this ADR and must not be conflated
  with it.
- **TypeScript/Node.js** faces the same CPU-bound objection with weaker
  native-geometry options.
- **Go, Rust, C#/.NET, Java** are the serious candidates.

## 3. Evaluation criteria

Weighting to be argued before scoring, so that weights are not chosen to fit a
preferred outcome.

| # | Criterion | Why it matters here |
|---|---|---|
| C1 | Geometry stack runtime affinity | See §4 below. Added at the project owner's request. |
| C2 | Raster/GDAL integration quality | GDAL is native in every language; what differs is binding maturity, memory-model friction and crash containment. |
| C3 | Rasterization backend availability | Skia / Cairo / equivalent, with maintained bindings. |
| C4 | Concurrency and worker-isolation model | Directly shapes ADR-007. Green threads, async, OS threads, process supervision ergonomics. |
| C5 | Memory behaviour under sustained load | GC pause profile, allocation pressure on the tile path, predictability with large geometry batches. Relates to worker recycling (§22). |
| C6 | Streaming large result sets | Millions of features must stream, never materialise (§47). |
| C7 | Single-binary / air-gapped distribution | §2 requires `./gis-server` to be credible and air-gapped installs to work. |
| C8 | Database driver quality | PostgreSQL binary protocol, COPY, cursors, connection pooling, timeout and cancellation semantics. |
| C9 | Operational diagnosability | Profiling, heap dumps, live stack inspection at 2 AM. |
| C10 | Ecosystem and contributor pool | Open-source project; who can realistically contribute. |
| C11 | Build and cross-compilation complexity | With native dependencies in the mix, this is not a footnote. |
| C12 | Licence compatibility of the runtime and its ecosystem | Copyleft is acceptable, so this is low-friction — but must be checked, not assumed. |

## 4. Criterion C1 — geometry stack runtime affinity

Recorded explicitly because it originates from a product requirement, not from
technical preference.

The project owner requires that defects be diagnosable and fixable in-house
(see [build-vs-adopt-policy.md](../build-vs-adopt-policy.md) §2). A geometry
stack running inside the same runtime is materially easier to diagnose than one
reached across a C++ FFI boundary: single debugger, single stack trace, no
marshalling layer to suspect, no separate native toolchain to build and ship.

| Language | Common geometry path | Affinity |
|---|---|---|
| Java | JTS — native Java, the reference implementation of this algorithm family | In-runtime |
| C# / .NET | NetTopologySuite — managed port of JTS | In-runtime |
| Rust | `geo` / `i_overlay` — native, independent lineage rather than a JTS port | In-runtime, different risk profile |
| Go | Typically cgo → GEOS; native options (`orb`, `go-geom`) are thinner on topology | Across FFI |

**Restated 2026-08-12 after
[research/geometry-projection-libs.md](../research/geometry-projection-libs.md).**
The original phrasing — "is the geometry stack in the same runtime" — is too
crude. `VERIFY` `go-geos` is concurrency-safe, using GEOS's `*_r` functions with
locking, and exposes the full GEOS surface. FFI is not inherently unsafe or
incomplete.

The accurate criterion is narrower and measurable:

> Can we reach a mature, complete geometry engine **without paying per-call FFI
> overhead on the tile hot path**, and can we debug it in the same process and
> debugger as our own code?

Against that, Go's position is still weakest, but for specific documented
reasons rather than a general objection: `go-geos`'s own documentation cites
"expensive function call overhead, more complex memory management and trickier
cross-compilation", and states it suits short-lived programs, recommending
`go-geom` "for long-running processes with less stringent geometry function
requirements". We are a long-running server with stringent requirements — the
combination its documentation steers away from.

Also note the family tree: JTS is the reference implementation, GEOS and NTS are
ports of it, and Rust's `geo`/`i_overlay` is an independent lineage. Choosing
among Java, .NET and Go is largely choosing a port of the same algorithms;
choosing Rust is choosing a different implementation family, with the
correctness upside and risk that implies.

**Do not over-weight C1.** It is one criterion of twelve, and its weight depends
entirely on A-004 — whether the tile hot path is geometry-call-bound at all. If
the path turns out to be dominated by database time or serialisation, C1's
weight collapses. `benchmarks/geometry-hotpath/` must run before the weighting
is fixed, or we will have picked weights to fit a preferred answer.

Raster does not discriminate: GDAL is a native dependency everywhere. Whatever
we pick, the raster subsystem crosses a native boundary, which is an argument
for isolating raster work in its own worker class regardless of language
(feeds ADR-007, ADR-009).

## 5. Required prototype

A paper comparison cannot settle this. Build the same vertical slice in the two
strongest candidates after the paper round and measure it.

**Slice:** PostGIS table → feature query → (a) GeoJSON response, (b) MVT tile
response, over HTTP.

**Measure:** p50/p95/p99 latency, throughput, allocation and peak RSS under
sustained load, cold start, binary/image size, and — recorded honestly —
implementation effort and friction.

**Location:** `experiments/lang-slice/`. Same dataset, same queries, same
hardware, same tile set. Anything less is not a comparison.

Prototype code is disposable and is never promoted to production.

## 6. Decision

Pending.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-001 | The tile and render paths are CPU-bound enough for language performance to matter materially | `UNVALIDATED` |
| A-002 | A single-binary distribution is genuinely valuable for air-gapped installs | `UNVALIDATED` |
| A-005 | In-runtime geometry meaningfully reduces defect resolution time versus FFI | `UNVALIDATED` |

## 8. Dependencies

**Depended on by:** ADR-002, ADR-003, ADR-004, ADR-007, ADR-008, ADR-009.

## 9. Revisit triggers

- The prototype shows less than a materially significant gap between candidates
  on C1–C6, making secondary criteria decisive.
- The chosen language's geometry or GDAL binding becomes unmaintained.
- A polyglot boundary (§80.2) proves necessary for a worker class, which would
  change what "core language" means.

## 10. Dissent

To be recorded during the debate round.
