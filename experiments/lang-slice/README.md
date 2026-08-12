# Experiment: lang-slice

**Question it answers:** [ADR-001](../../docs/adr/ADR-001-core-language.md) —
which core language, on measured evidence rather than taste (§14, §57).
**Assumptions it settles:** A-001, A-002, and partially A-005.
**Candidates:** Go and C# / .NET. Rust and Java on escalation triggers, see
ADR-001 §5.

**Status:** SPECIFIED, not yet built.

---

## 1. The one thing this experiment must not do

It must not be run in a way that produces the answer someone already wants.

Both implementations must be written to the same specification, tuned to a
comparable degree, and measured on identical hardware and data. If one is
written carefully and the other in a hurry, the result is a measurement of
effort, not of language — and it will be quoted for years as though it were the
latter.

If we cannot commit to tuning both fairly, we should not run it and should
decide ADR-001 on secondary criteria instead. That is a legitimate outcome.

## 2. The slice

Identical in both languages. Deliberately narrow — this is not a small GIS
server, it is the thinnest path that exercises what the day-one workload
actually does.

```text
HTTP request
  → parse and validate parameters
  → parameterised SQL against PostGIS
  → stream result
  → serialise
  → HTTP response
```

Three endpoints:

| # | Endpoint | Purpose |
|---|---|---|
| **A** | `GET /collections/{id}/items?bbox=&limit=` → streamed GeoJSON | The feature path. Tests C6 streaming and C8 driver quality. |
| **B** | `GET /tiles/{z}/{x}/{y}.mvt` via `ST_AsMVT` | The pushdown tile path. Mostly measures how efficiently the language moves bytes from socket to socket. |
| **C** | `GET /tiles-local/{z}/{x}/{y}.mvt`, geometry fetched as WKB and **encoded in-process** | The in-process tile path. |

### Why endpoint C is the important one

B and C exist to be compared with each other.

Under vector-first, C is the only place where language CPU performance still
plausibly decides anything: clipping, quantising, simplifying and protobuf
encoding, in our process, thousands of geometries per tile.

**If B and C perform comparably, the platform is database-bound and ADR-001 does
not turn on performance at all.** That would be the single most useful result
this experiment can produce, and it is a real possibility rather than a
rhetorical hedge. It also directly settles A-001.

## 3. Prohibited shortcuts

Each of these would invalidate the comparison, and each is tempting:

- Buffering the whole result set before writing the response. Endpoint A must
  stream — that is the point of it.
- Using a different PostgreSQL driver mode between implementations (one binary
  protocol, one text).
- Caching anything. No L1, no HTTP caching, no prepared-statement warmth that
  only one side gets.
- Connection pool sizes that differ. Fix the pool size explicitly and identically.
- Different JSON or protobuf libraries chosen for convenience rather than because
  each is the idiomatic best-in-class choice for its language. Record what was
  chosen and why.
- Running the two on different machines, or against different database instances.

## 4. Dataset

Real data, not synthetic. Synthetic uniform points make every implementation
look good and hide exactly the behaviour we care about.

Requirements:

- One polygon layer with **at least 1 million features** and realistically
  irregular vertex counts — administrative boundaries or building footprints.
- One point layer of similar cardinality.
- One line layer with long, high-vertex geometries — roads or hydrography. These
  are where clipping and simplification actually cost something.
- Indexed as we would index in production (GiST), `ANALYZE`d, and identical for
  both runs.

Record the exact source, extract date, feature counts and total vertex counts.
`VERIFY` OpenStreetMap extracts are the obvious source and are licence-clean for
benchmarking; confirm before use.

## 5. Measurements

| Metric | Why |
|---|---|
| Throughput (req/s) at fixed concurrency | Headline capacity |
| p50 / p95 / **p99** latency | p99 is the one that matters. Averages hide GC pauses. |
| Peak RSS under sustained load | C5, and it compounds across workers |
| Allocation rate / GC pause distribution | The managed-camp question, stated precisely |
| Cold start to first successful response | ADR-007 worker recycling and lazy start |
| Artefact size, and whether it is one file | C7, with the Q-28 caveat that GDAL is excluded here |
| Behaviour at saturation | Does it degrade predictably or fall over? Feeds §48 backpressure |

Run each at three concurrency levels including one deliberately past saturation.
A language that degrades gracefully under overload is worth more to a GIS
administrator at 2 AM than one that is 15% faster below it.

### Recorded, not measured

Written down honestly by whoever implements each side, including the parts that
are unflattering:

- Time to working implementation.
- Where the language fought back.
- What the diagnosability experience was actually like when something was slow —
  this is C9, and it is best assessed by having genuinely needed it.
- Whether the idiomatic solution was also the fast solution, or whether
  performance required unidiomatic code. This predicts what the codebase looks
  like in three years.

## 6. Reporting

`RESULTS.md` in this directory containing: hardware, OS, database version and
configuration, dataset description, both implementations' library choices, raw
numbers, and the honest notes from §5.

Then a summary into [ADR-001](../../docs/adr/ADR-001-core-language.md) §4
(Evidence) — including, if that is what the numbers say, **"the performance
criteria did not discriminate."**

## 7. Afterwards

This code is deleted, or left here permanently marked as disposable. It is never
promoted to production (§56, [CLAUDE.md](../../CLAUDE.md) §1). If it validates an
approach, production is written fresh.
