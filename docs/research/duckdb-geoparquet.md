# DuckDB and GeoParquet

**Status:** FIRST PASS — claims marked `VERIFY` are from general knowledge and
have not been checked against current documentation or measured.
**Raised by:** project owner, 2026-08-12
**Feeds:** [ADR-002](../adr/ADR-002-primary-data-architecture.md),
[ADR-008](../adr/ADR-008-query-engine.md), provider architecture (§27),
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md)

---

## 1. Framing — corrected

An earlier draft of this note framed DuckDB as "the engine for file and
object-store providers", i.e. as a GeoParquet reader. **That framing is too
narrow and the project owner rejected it.** The main data store is PostGIS. If
DuckDB only mattered for reading Parquet files, it would be a minor provider
detail, not an architectural question.

The question worth asking is the one that survives when PostGIS is present:

> Does the platform need an in-process spatial compute engine of its own,
> independent of whichever provider the data came from?

That reframes DuckDB from *a way to read a format* to **a candidate for the
platform's execution layer**. Section 3 below is rewritten around that.

GeoParquet remains worth supporting on its own merits (P1 below), but it is a
separate, much smaller decision.

`VERIFY` DuckDB is MIT-licensed, which is unproblematic under our posture.

## 2. Where each one actually fits

The mistake to avoid is treating this as a PostGIS replacement question. It is
not. The two engines are good at opposite things, and the platform has both
kinds of workload.

| Workload | Best fit | Why |
|---|---|---|
| Feature service CRUD, batch editing, optimistic concurrency (§28) | PostGIS | Transactional, row-level locking, editor tracking. DuckDB is OLAP; this is not its job. |
| Point and small-bbox queries with high request rates | PostGIS | GiST index, tuned for selective lookups. |
| Large scans, aggregation, statistics over millions of features | DuckDB | Columnar, vectorised, projection and predicate pushdown. Likely a large margin. |
| Read-only reference layers on object storage | DuckDB + GeoParquet | No database needed at all. |
| Platform metadata (services, users, jobs) | PostgreSQL or files | Needs concurrent writers. See §5 below. |

The honest conclusion from this table: **DuckDB is a compute engine, not a
store.** PostGIS keeps the transactional role — editing (§28) is a hard
requirement given the GIS-administrator user, and DuckDB is not built for it.
The two are complements, which is why the question is P3 (compute layer) rather
than "DuckDB or PostGIS".

## 3. What this means architecturally

Four distinct proposals. They must not be conflated — they have different
risk, different value, and different blast radius.

**P1 — GeoParquet as a first-class provider format.** Low risk, clearly
worthwhile, already on the §27 provider list. Not the interesting question.

**P2 — DuckDB as the executor for providers that cannot execute.** Delegate
scan, filter and aggregation over files to DuckDB rather than writing it
ourselves. Makes the capability-aware provider model (§27) have a genuinely
capable file provider instead of a degraded one.

**P3 — DuckDB as the platform's in-process compute layer.** *This is the
proposal that matters, and the one the owner is pointing at.*

§30 says push computation down intelligently — but "intelligently" implies there
is somewhere else to run it when pushing down is wrong or impossible. Today that
somewhere else would be hand-written code inside the server. DuckDB is a
candidate for being that place instead:

- **Cross-provider work.** Join a PostGIS layer to a GeoParquet reference layer,
  or to a CSV, or to a second database. No single provider can execute this. Our
  own code would have to, badly. `VERIFY` DuckDB's `postgres` scanner can read
  PostgreSQL tables directly, which would make this a SQL problem rather than an
  application-code problem.
- **A competent fallback for every provider.** The Query AST always has an
  executor. Providers push down what they can; the remainder executes in DuckDB
  rather than in bespoke server code. This makes capability negotiation a
  gradient instead of a cliff.
- **Analytical workloads that PostgreSQL is bad at.** Large scans, aggregations,
  statistics across millions of features — columnar and vectorised beats row
  storage, often by a wide margin. Feature service statistics (§28) is exactly
  this shape.
- **Geoprocessing (§36).** A long-running spatial analysis job needs a compute
  engine. Building one is not on anyone's plan; DuckDB is one.
- **Tile generation feed.** Materialising a PostGIS extract to Parquet and
  serving tiles from it is a real pattern for read-heavy layers.

The strategic argument for P3: without it, "server-side spatial computation" is
a euphemism for code we have not written yet, and every capability gap gets
filled with bespoke logic. With it, the platform has one execution engine and
the provider layer only has to answer *what can you push down*.

The strategic argument against P3: it is a large dependency at the centre of the
architecture rather than at the edge, and centre-of-architecture dependencies
are the hardest to reverse (§10).

**P4 — DuckDB as the basis for a PostgreSQL-free deployment profile.** The
`./gis-server` story: no database to install, for the developer profile (§53)
and air-gapped installs. Attractive, but it depends on P2 and P3 working and on
the metadata problem in §5, which is not solved by DuckDB. Do not promise it
before it is measured.

**Recommended sequencing:** P1 is nearly free. P3 is the question to actually
investigate. P4 follows from P3 or does not happen.

## 4. The GEOS layering objection, and why it is weaker than it first appears

`VERIFY` DuckDB's spatial extension bundles GEOS and GDAL.

The obvious objection is that adopting DuckDB does not remove GEOS from the
dependency tree — it buries it behind a SQL interface and an extension loader,
which sits badly with the owner's requirement to fix defects in-house.

**The owner's counter is correct and it defeats most of that objection:**
PostGIS already links GEOS and PROJ. We are already depending on a GEOS build we
did not choose, reached through a SQL interface, inside someone else's process.
DuckDB is not introducing a new category of dependency — it is the same
arrangement we have already accepted for the primary data store.

So the objection reduces to two narrower points, both real but both smaller:

1. **In-process versus out-of-process.** PostGIS's GEOS runs in the PostgreSQL
   server. If it faults, PostgreSQL absorbs it and our worker survives. DuckDB's
   GEOS runs in *our* address space, so a fault there is our crash. This is an
   argument about crash containment and worker isolation (ADR-007, A-007), not
   about dependency hygiene — and it applies equally to GDAL, which we are
   adopting in-process regardless.
2. **Two GEOS versions with different behaviour.** If we adopt DuckDB spatial
   *and* a direct geometry engine, the same predicate may be evaluated by two
   builds with different edge-case behaviour depending on which path a query
   takes. That is a genuine correctness hazard rather than an aesthetic one, and
   it is the reason `experiments/geometry-oracle` should be run against every
   geometry path we ship — including the one inside DuckDB, and including
   PostGIS's.

Note that point 2 already applies today without DuckDB: pushing a predicate down
to PostGIS versus evaluating it in-process uses two different GEOS builds. The
oracle suite needs to cover that regardless of what happens to DuckDB. Finding
this was worth the exercise even if DuckDB is ultimately rejected.

For the policy: DuckDB would be a **Tier 2 dependency that transitively contains
other Tier 2 dependencies**. The
[build-vs-adopt-policy](../build-vs-adopt-policy.md) should name that category,
because the same is true of PostGIS.

## 5. The concurrency question

`VERIFY` DuckDB's process model: a single process may write; concurrent writers
across processes are not supported in the way a client/server database supports
them.

This matters because of ADR-007. If workers are separate processes and each
embeds DuckDB, then:

- read-only providers are fine — multiple readers, no coordination;
- anything that writes must be routed to a single owner, which is a coordination
  requirement we do not currently have;
- platform metadata in DuckDB is therefore **unattractive**, because the admin
  API writes and multiple workers read. That is exactly the shape DuckDB is not
  built for.

So P3 (`./gis-server` with no PostgreSQL) probably needs a different answer for
metadata than for data — SQLite or files for metadata, DuckDB for data scans.
Which is more moving parts than the slogan suggests, and worth being honest
about early.

## 6. What GeoParquet cannot do

- **No feature-level update.** Columnar immutable files. Editing means rewriting
  files or maintaining a delta, which is a whole architecture of its own.
- **Spatial indexing is not the classic case.** `VERIFY` GeoParquet 1.1 added
  bbox covering columns enabling row-group pruning; DuckDB spatial `VERIFY` has
  an RTREE index for its own tables. Neither is equivalent to a GiST index for
  highly selective queries. Expect good scans, worse point lookups.
- **`VERIFY` No `ST_AsMVT` equivalent.** If true, tile encoding from a DuckDB
  provider is ours to do — which is consistent with MVT encoding sitting in
  Tier 1 anyway, but it removes one of the shortcuts the PostGIS path has.

## 7. Effect on other decisions

- **ADR-002** gains an alternative: DuckDB/file-based deployment profile
  alongside the PostgreSQL baseline. Also forces the metadata-versus-data split
  to be made explicit rather than assumed.
- **ADR-008** gains a real question: does the Query AST compile to *two* SQL
  dialects from day one? Doing so early is a good forcing function against
  accidentally building a PostGIS-shaped abstraction — the provider model (§27)
  is only genuinely capability-aware if something other than PostGIS exercises
  it.
- **A-009** ("PostgreSQL is an acceptable hard dependency") is directly
  challenged and should now be argued rather than assumed.
- **ADR-001** is barely affected: DuckDB is a C API native dependency in every
  candidate language. Binding quality varies; the decision does not turn on it.

## 8. Open questions this raises

Registered in [../open-questions.md](../open-questions.md) as **Q-19** (does the
platform need its own compute engine, and is DuckDB it), **Q-20** (how many GEOS
builds evaluate our predicates, and how is divergence prevented), **Q-21** (does
the Query AST target more than one dialect from day one), and **Q-22** (is a
PostgreSQL-free profile a goal).

## 9. What to measure

Nothing above justifies a decision yet. Required before ADR-002 or ADR-008 move
off `DRAFT`:

1. **Scan and aggregate**: DuckDB versus PostGIS on the same data, for a
   large-bbox feature query and a statistics query. Establishes whether the
   analytical advantage is real at our data sizes.
2. **Selective query**: the opposite case — small bbox, high selectivity, high
   request rate. PostGIS should win; the size of the gap decides whether DuckDB
   can touch the feature endpoint path at all.
3. **Reading a database through DuckDB** (`VERIFY` the postgres scanner): what
   does it cost versus querying directly? Load-bearing for P3 — if pulling data
   into DuckDB to compute on it is expensive, the compute-layer idea collapses
   for database-resident data, which is most data.

   **Reprioritised 2026-08-12.** With Oracle Spatial and SQL Server Spatial now
   first-class, the compute-layer argument is stronger, because those two have
   real capability gaps against PostGIS and the missing work has to execute
   somewhere. Measuring the *non-PostGIS* path matters at least as much as the
   PostGIS one — that is where the gap actually is.
4. **Cross-provider join**: PostGIS layer joined to a GeoParquet layer. Compare
   against the alternative of doing it in our own code. This is the capability
   that justifies P3 on its own if it holds up.
5. **Embedded footprint**: memory and startup cost of DuckDB per worker process.
   Directly relevant to ADR-007 at 1,000 services — a compute engine embedded in
   every worker multiplies.
6. **Object storage**: scan against remote GeoParquet over HTTP range requests,
   cold and warm. Decides whether P4 is real.

Measurement 3 is the one to run first. If it fails, P3 is dead regardless of how
good the other numbers look, because the platform's data lives in PostGIS.

Benchmark home: `benchmarks/duckdb-compute-layer/`.
