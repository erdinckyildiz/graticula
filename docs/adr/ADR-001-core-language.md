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

## 3. What the platform actually does — reweighted after vector-first

The criteria below were drafted before two decisions landed: **features first,
then vector tiles**, and **vector-first rendering**
([product-context.md](../product-context.md)). Together they change what this
language has to be good at, and the change is large enough that scoring against
the original weights would answer the wrong question.

Trace the day-one hot path honestly:

```text
HTTP request
  → authorize
  → plan query, generate parameterised SQL
  → execute against PostgreSQL, stream rows
  → serialise: GeoJSON, or MVT (often ST_AsMVT bytes passed through)
  → cache
```

That is **an HTTP server, a PostgreSQL client and a serialiser.** With pushdown
working, very little heavy computation happens in our process at all.

### The uncomfortable consequence for C1

**The geometry engine is largely not on the hot path.**

- Feature serving: PostGIS does the spatial work; we serialise.
- MVT: either `ST_AsMVT` (PostGIS does everything) or our own lightweight
  primitives — clip, quantise, simplify — which are Tier 1 code we write
  ourselves, not GEOS topology calls.
- Heavy topology (overlay, buffer, validity) is needed for editing validation,
  geoprocessing, and providers that cannot push down. Real, but not the hot
  path.

This **demotes C1 substantially**, and it partially undermines the case built
against Go over the last two notes. If geometry calls are rare, per-call cgo
overhead stops being decisive. That correction has to be made explicitly, or the
weighting will have been chosen to protect an earlier lean rather than to answer
the question.

C1 does not disappear. In-runtime geometry still serves the owner's
defect-resolution requirement and still matters for the editing and
geoprocessing paths. It is simply no longer a throughput argument.

### Revised weighting

| # | Criterion | Weight | Change and why |
|---|---|---|---|
| C8 | **PostgreSQL driver quality** | **Critical** | Raised. Binary protocol, `COPY`, server-side cursors, cancellation, pooling. The platform is largely a very good PostgreSQL client. |
| C6 | **Streaming large result sets** | **Critical** | Raised. Features first means millions of rows streamed, never materialised (§47). |
| C5 | Memory behaviour under sustained load | High | Unchanged. Tile bursts, streaming buffers, p99 predictability. |
| C4 | Concurrency and worker model | High | Unchanged. Shapes ADR-007. |
| C9 | Operational diagnosability | High | Raised. GIS-administrator user, 2 AM test (§7). |
| C7 | Single-binary / air-gapped distribution | High | Unchanged, but see §3.1 — the story is weaker than it looks for every candidate. |
| C11 | Build and cross-compilation complexity | Medium-high | Unchanged. |
| C1 | Geometry engine access and debuggability | **Medium** | **Lowered** from the owner-driven high. Off the hot path; still matters for editing, geoprocessing and defect resolution. |
| C10 | Ecosystem and contributor pool | Medium | Unchanged. Open-source project. |
| C2 | GDAL integration quality | **Low-medium** | **Lowered.** GDAL leaves the request path for imagery; it remains for registration and file-based *vector* providers. |
| C12 | Licence compatibility | Low | Copyleft acceptable; verify, do not agonise. |
| C3 | ~~Rasterisation backend availability~~ | **Dropped** | Vector-first. There is no rasterisation backend in the core. |

### 3.1 The single-binary story is weaker than it looks — for everyone

`./gis-server` (§2) is attractive, and C7 is weighted for it. But **GDAL is a
native library in every candidate language**, so any build that includes
file-based vector providers is not a lone static binary regardless of what we
choose.

There is a real architectural option here that belongs in ADR-006 rather than
this ADR: **make GDAL-backed providers optional**, so that a PostGIS-only
deployment genuinely is one artefact and file providers are an add-on. If that
holds, C7 discriminates properly. If it does not, C7 is largely neutralised and
should be reweighted downward. Recorded as Q-28.

Do not let an aspirational single-binary story decide this ADR before that
question is answered.

## 4. Candidates assessed

Python and TypeScript/Node remain excluded per §2. Four candidates, assessed
against the revised weighting. Each stated at its strongest.

### Go

**For.** Outstanding at exactly the revised profile — HTTP concurrency,
low-overhead I/O, fast startup, small footprint. `pgx` is a first-rate
PostgreSQL driver with binary protocol and `COPY` support (C8). Genuinely static
binaries and trivial cross-compilation (C7, C11). Fast startup helps worker
recycling and cold start (ADR-007). And the strongest evidence of all: **the
modern thin-server ecosystem is Go** — pg_tileserv and Tegola do a subset of our
job, in production, which is direct proof the workload fits the language.

**Against.** Geometry via cgo (C1), and cgo also undermines the static-binary
advantage it otherwise has (C7, C11). `go-geos`'s own documentation steers
long-running servers elsewhere. Weakest of the four on the diagnostics richness
of C9. Least expressive type system for a large domain model.

### Rust

**For.** No GC, so p99 latency and memory are the most predictable of the four
(C5) — and at 1,000 services with warm per-worker state, memory efficiency
compounds. Single binary (C7). `VERIFY` Martin, in Rust, is reported the fastest
of the PostGIS tile servers, which is the same "proof the workload fits"
argument Go has. Native geometry (C1) via an independent lineage.

**Against.** Slowest to build, and Phase 0 is already long. Smallest contributor
pool for an open-source GIS project (C10) — real, given that GIS developers
cluster around Java, Python and C++. Geometry via an independent lineage rather
than a JTS port, which is a correctness risk requiring the oracle suite to
discharge.

### C# / .NET

**For.** NetTopologySuite runs in-runtime — the best position on the owner's
defect-resolution requirement, with one debugger and no marshalling layer (C1).
Npgsql is an excellent PostgreSQL driver (C8). Diagnostics are a genuine
strength: counters, dumps, live tracing (C9). Single-file publish and AOT make
C7 credible. Performance is competitive with Go for this profile.

**Against.** Smaller open-source *GIS* contributor pool than Java (C10), and a
lingering Windows association that matters for perception in an open-source
Linux-first geospatial project even where it is technically obsolete. AOT
interacts badly with reflection-heavy libraries, which needs checking before C7
is credited.

### Java

**For.** JTS is *the* reference implementation — the strongest possible C1
position, with fixes landing there first. The largest GIS contributor pool of
any candidate by a wide margin (C10): GeoServer, GeoTools and much of the
enterprise GIS world. Best-in-class observability with JFR and heap dumps (C9).
Mature streaming and driver ecosystem.

**Against.** Distribution is the weakest (C7) — a JVM, or jlink/GraalVM work.
Startup cost hurts worker recycling and cold start. And a soft but real risk:
being "the Java GIS server" invites comparison with GeoServer, which is the
architecture we exist to reconsider.

**One objection I had prepared and am withdrawing.** JVM memory footprint was
going to count against Java at 1,000 services — but under ADR-007 services are
not processes. We will run a small number of workers, so per-runtime overhead
multiplies by worker count, not service count. The objection does not survive
its own arithmetic.

## 5. Narrowing for the prototype

The four split cleanly along one axis:

| Camp | Candidates | Bets on |
|---|---|---|
| **Native** | Go, Rust | Distribution, footprint, latency predictability, proven in this exact workload |
| **Managed** | .NET, Java | Geometry in-runtime, diagnosability, contributor pool, development speed |

**The question the prototype must answer is whether the managed-runtime cost is
real at our scale, or a reflex.** Everything else is secondary and can be argued
on paper.

**Prototype Go and C# / .NET.**

Reasoning, stated so it can be attacked:

- **Go over Rust as the native representative.** Faster to build, and Phase 0 is
  already long. Rust's advantages over Go — memory and p99 — are precisely what
  the prototype will measure in Go; if Go loses to .NET on those axes, Rust is
  not the answer either, and if Go wins narrowly on them, Rust is the obvious
  escalation. Prototyping Rust first would cost more and decide less.
- **.NET over Java as the managed representative.** NTS is close enough to JTS
  that C1 barely separates them, while .NET is clearly ahead on C7 distribution
  — the criterion where the managed camp is weakest. Testing the managed camp at
  its strongest on its weakest criterion is the more informative experiment.
  Java's real advantage is C10 contributor pool, which is a judgement call no
  benchmark settles.

**Explicit escalation triggers** — these are commitments, not caveats:

- If Go wins on memory or p99 by a **narrow** margin, prototype Rust before
  deciding: Rust would likely widen a narrow native-camp win into a decisive one.
- If .NET wins and C1 proves more load-bearing than §3 predicts, reconsider Java
  on the strength of JTS being the reference implementation.
- If the two are close on **all** measured criteria, the decision falls to C10
  and C9 — where Java leads on contributor pool and .NET on tooling — and the
  ADR should say so rather than pretending the benchmark decided it.

### The tension, recorded rather than resolved

The owner's stated requirement (fix defects in-house, geometry debuggable in our
own debugger) points at **.NET or Java**. The deployment model (`./gis-server`,
air-gapped, small footprint, fast worker startup) points at **Go or Rust**.

Vector-first weakened the first pull by taking geometry off the hot path. Q-28
may weaken the second by revealing that no candidate ships a single binary once
GDAL is involved. **Both pulls could turn out to be softer than they look**,
which would leave the decision to C8, C6, C9 and C10 — a much less romantic set
of criteria, and the honest one.

## 6. Decision

**Pending — narrowed, not decided.** Go and C#/.NET proceed to the prototype at
`experiments/lang-slice/`. Rust and Java remain live under the escalation
triggers in §5. Status stays `REQUIRES PROTOTYPE`.

Recording what would make this ADR wrong, before the numbers arrive: if the
prototype shows both candidates comfortably within our latency and memory
targets — which §3 suggests is plausible, since the hot path may be
database-bound — then **this ADR should not pretend the benchmark decided it.**
It should say the performance criteria did not discriminate, and decide on C9,
C10 and C11 instead. An ADR that manufactures a performance justification for a
preference is worse than one that admits the preference.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-001 | The tile path is CPU-bound enough for language performance to matter materially. **Now doubtful** — with `ST_AsMVT` pushdown the hot path may be dominated by database and network time, in which case all four candidates are adequate and this ADR turns on secondary criteria | `UNVALIDATED` |
| A-002 | A single-binary distribution is genuinely valuable for air-gapped installs | `UNVALIDATED` |
| A-005 | In-runtime geometry meaningfully reduces defect resolution time versus FFI | `UNVALIDATED` |
| A-016 | GDAL-backed providers can be made optional, so a PostGIS-only deployment is genuinely one artefact (Q-28) | `UNVALIDATED` — if false, C7 is largely neutralised | 

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
