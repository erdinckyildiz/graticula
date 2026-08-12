# Multi-Database Consequences

**Status:** ANALYSIS — written 2026-08-12 in response to an owner decision.
**Trigger:** PostgreSQL is not mandatory. Oracle Spatial and SQL Server Spatial
are supported alongside PostGIS.
**Invalidates:** A-009. **Reopens:** [ADR-002](../adr/ADR-002-primary-data-architecture.md).
**Materially changes:** ADR-008, ADR-011, ADR-001, ADR-010, §25 connection
budgeting.

---

## 1. Why this is bigger than it looks

On the surface this is §27 restated: the master prompt already lists SQL Server
Spatial and Oracle Spatial as providers. What changed is that they moved from
*"extensible providers we might add"* to **first-class targets that must work**,
and that **PostgreSQL may be absent entirely.**

That second half is the consequential one, and the reasoning behind it is
stronger than I had credited. **ArcGIS Server deployments run heavily on SQL
Server and Oracle.** Displacing them is a confirmed goal (Q-07). Requiring such
an organisation to stand up PostgreSQL — a database they may have no operational
expertise in, no backup tooling for, and no approval to run — purely to hold our
service definitions is a serious adoption barrier standing directly in front of
our stated migration target.

Two separate things are affected, and conflating them causes bad decisions:

| | What it is | What changed |
|---|---|---|
| **Platform store** | Where *our* state lives: services, roles, jobs, styles | Cannot assume PostgreSQL |
| **Data providers** | Where the *customer's* spatial data lives | Three first-class spatial dialects, not one |

## 2. The platform store

### 2.1 The abstraction I refused is now justified — and cheaper than feared

[ADR-002](../adr/ADR-002-primary-data-architecture.md) §4.4 declined to build a
multi-store abstraction, citing §82. That reasoning was sound given the
information at the time and is now void.

But the cost is lower than the earlier refusal implied, for a specific reason:

> **Platform metadata is not spatial and not high-volume.** Service definitions,
> roles, jobs, style documents, cache index. A few thousand rows of ordinary
> relational data, written at administrator pace.

Supporting several backends for boring relational data is a well-trodden
enterprise problem. What we need is a **dialect** abstraction, not a
**capability** abstraction — all four candidate stores have transactions,
constraints, indexes and concurrent readers. The cost is a test matrix and a set
of migration scripts, not an architecture.

That is a genuinely different proposition from the SQLite-versus-PostgreSQL
portability that §4.4 rejected, where the capability gap was real.

### 2.2 What must now be avoided in platform SQL

This is the practical output, and it must be settled **before any metadata SQL
is written**:

- **No `JSONB` operators.** JSON storage exists everywhere; PostgreSQL's operator
  set does not. Store documents as text or the local JSON type; query them in
  the application, or model the queryable parts as columns.
- **No `LISTEN`/`NOTIFY`.** This is the significant loss. Cross-node change
  notification cannot depend on it, which affects the event architecture (§45)
  and cache invalidation. Polling a change-sequence column is the portable
  answer and should be assumed.
- **No arrays**, no PostgreSQL-specific range or enum types.
- **`SKIP LOCKED` needs care.** PostgreSQL and Oracle both have
  `FOR UPDATE SKIP LOCKED`; SQL Server expresses the same intent as
  `WITH (UPDLOCK, READPAST)`. The pattern is portable, the syntax is not — which
  matters for [ADR-011](../adr/ADR-011-job-system.md)'s in-database queue.
- **Identity, sequences, upsert and `RETURNING` all differ.** Ordinary dialect
  work, but it must live behind the store module rather than being sprinkled
  through the codebase.
- **Case sensitivity and identifier quoting differ**, particularly Oracle. Pick
  conservative naming once and never fight it.

### 2.3 The candidate set

| Store | Role | Argument |
|---|---|---|
| **SQLite** | Default for developer and single-node small | Zero install, ships with the binary, makes `./gis-server` real, backup is a file copy. WAL mode handles many readers with one writer, which matches an administrator-paced write rate. |
| **PostgreSQL** | Default for PostGIS shops and multi-node | Already present when the data is PostGIS. |
| **SQL Server** | Enterprise, especially ArcGIS migrations | Removes the adoption barrier for the exact organisations we target. |
| **Oracle** | Enterprise, especially ArcGIS migrations | Same. |

SQLite alone does not close the gap. Many organisations have policy requiring
persistent state to live in managed, backed-up, highly available database
infrastructure — *"it is a file on the application server"* fails that audit.
And SQLite cannot hold shared state for multiple nodes.

### 2.4 The honest cost

Four backends means four migration paths, four dialect implementations and a
four-way test matrix that must actually be run, not aspired to. **An untested
backend is a broken backend**, and claiming support for one we do not exercise
in CI is worse than not claiming it.

A phased approach is defensible — SQLite and PostgreSQL implemented first, SQL
Server and Oracle added when migration work demands them — **provided the port
design is validated against all four dialects on paper now.** Designing against
two and discovering the third does not fit is the standard way this goes wrong.

## 3. Data providers — the larger consequence

### 3.1 `ST_AsMVT` does not exist outside PostGIS

This is the single most consequential technical fact in this note.

The thin-server architecture we admired
([postgis-thin-servers.md](postgis-thin-servers.md)) leans on `ST_AsMVT` to push
tile encoding into the database. **SQL Server Spatial and Oracle Spatial have no
equivalent.**

Therefore **in-process MVT encoding is mandatory, not an alternative.** For two
of our three first-class spatial providers it is the only path.

Consequences that cascade:

- **[ADR-001](../adr/ADR-001-core-language.md): `experiments/lang-slice`
  endpoint C is promoted from comparison to primary path.** It was designed to
  test whether in-process encoding matters; it is now the required path for most
  enterprise deployments.
- **A-001 becomes considerably more likely to be true.** The tile path is
  CPU-bound in our process whenever the provider cannot encode. Language
  performance matters more than §3 of ADR-001 concluded a day ago.
- **C1 is partially re-elevated.** Our own hot-path geometry primitives — clip,
  quantise, simplify — are now on the critical path for SQL Server and Oracle
  deployments, which strengthens A-004 and the argument for owning them
  ([build-vs-adopt-policy.md](../build-vs-adopt-policy.md) §4).
- **`benchmarks/mvt-generation/`** changes from "which is faster" to "what does
  the non-PostGIS path cost us", which is a more important question.

### 3.2 Capability negotiation stops being optional

The three spatial dialects differ substantially in function coverage, indexing,
SRID handling and geometry validity semantics. PostGIS is the most capable by a
wide margin.

[ADR-008](../adr/ADR-008-query-engine.md) therefore cannot ship a
lowest-common-denominator query engine — that would waste PostGIS — nor a
PostGIS-shaped one with degraded fallbacks bolted on. **Q-21 is answered: the
Query AST targets multiple dialects from day one**, and capability negotiation
is core rather than a later refinement.

This is uncomfortable but healthy. An abstraction exercised by one implementation
is not an abstraction; it is a wrapper that will not survive contact with the
second.

### 3.3 The compute-layer argument gets stronger

If SQL Server and Oracle cannot execute part of a query plan, the work has to
happen somewhere. Today that somewhere is "code we have not written".

That is exactly the argument for **P3** in
[duckdb-geoparquet.md](duckdb-geoparquet.md) — an in-process compute engine as
the fallback executor for capability gaps. Q-19 was interesting before; with
three providers of unequal capability it becomes load-bearing.

Note also that the earlier measurement priority shifts. Reading *PostGIS*
through DuckDB was the first benchmark to run; reading **SQL Server or Oracle**
through it now matters as much, because that is where the capability gap is.

### 3.4 Connection budgeting gets three profiles

§25's arithmetic — `nodes × workers × pool = connections` — now has three
different cost profiles. Oracle and SQL Server sessions are not priced like
PostgreSQL backends, and the licensing implications of connection count differ
too. [ADR-007](../adr/ADR-007-service-runtime.md)'s budget must be produced per
provider, not once.

### 3.5 Editing semantics differ

Feature service CRUD (§28) with transactions, optimistic concurrency and editor
tracking behaves differently across the three: isolation levels, locking
behaviour, and what a conflict looks like. This lands on ADR-008 and the feature
service design, and it is the kind of difference that produces
provider-dependent bugs rather than provider-dependent features.

## 4. Licensing note

Oracle and SQL Server drivers carry their own terms, and Oracle client
libraries have historically had redistribution restrictions. Rows must be added
to [DEPENDENCY-LICENSES.md](../../DEPENDENCY-LICENSES.md) and verified before
either provider is implemented. We are a client, so the customer's database
licence is theirs — but our ability to *redistribute a driver* is ours.

## 5. What this does not change

Worth stating, so the reopening does not spread further than it should:

- **ADR-002's core reasoning survives.** A database is still the source of truth
  and files are still an export format; the runtime-admin-API collision with
  file-based configuration is unaffected. Only §4.1 and §4.4 change.
- **The platform store is still logically distinct from data source databases.**
  This decision strengthens that separation rather than weakening it — the
  customer's Oracle may be untouchable, and now their platform store might be a
  different engine entirely from their spatial data.
- **The state inventory (ADR-002 §5) stands** unchanged.

## 6. New and changed questions

| # | Question |
|---|---|
| Q-29 | Which platform stores ship in v1, and which are designed-for-but-deferred? An untested backend is a broken backend. |
| Q-30 | How is cross-node change notification done without `LISTEN`/`NOTIFY`? Polling a change sequence is the portable default; confirm it is adequate. |
| Q-31 | Does the feature service expose provider capability differences to clients, or hide them behind a uniform contract that degrades silently? Hiding them is friendlier and lies; exposing them is honest and leaks. |
| Q-21 | **Answered: yes.** The Query AST targets multiple dialects from day one. |
| Q-22 | **Reframed.** No longer "is a PostgreSQL-free profile a goal" but "which stores must be supported, and in what order". |
