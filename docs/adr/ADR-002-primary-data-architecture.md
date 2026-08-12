# ADR-002 — Primary Data Architecture

| | |
|---|---|
| **Status** | `REOPENED` → re-decided, `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-12, amended same day |

---

## 1. Context

Where does the platform's own durable state live — service definitions, catalog,
roles, data source registrations, style documents, job records, cache metadata —
and how does that relate to the spatial data it serves?

§80.29 and §80.30 demand an explicit split between persistent and ephemeral
state. ADR-012 additionally requires every ADR to declare which of its state is
node-local and which is shared, since collecting that inventory late is what
makes clustering expensive.

Inputs that constrain this decision:

- **Baseline deployment is `gis-server → PostgreSQL/PostGIS`** (§2).
- **Primary user is the GIS administrator**, so the administrative API is
  first-class and configuration changes happen **at runtime**
  ([product-context.md](../product-context.md)).
- **Migration is a goal**, so data will frequently live in databases the
  organisation already owns and administers.
- **Vector-first** adds two new kinds of state: style documents, and glyph and
  sprite assets.

## 2. Alternatives considered

### Alternative A — Platform metadata in the same database as spatial data

**For.** One thing to install, back up and monitor, which serves §2's simplicity
requirement directly. Publishing can validate a table's existence in the same
transaction that registers the service.

**Against.** It assumes we own the database holding the data. Under the
migration goal that assumption fails often: an organisation displacing GeoServer
has PostGIS instances administered by a DBA who may grant us `SELECT` and
nothing else. A platform that requires DDL rights on the customer's data
database is a platform that does not get installed.

### Alternative B — File-based configuration, no database

**For.** Genuinely attractive for air-gapped operation, reproducible
environments, GitOps promotion between staging and production, and
version-controlled review of changes. No migration system needed.

**Against.** It collides head-on with the primary user. A GIS administrator
driving a runtime administrative API (§39) means **the server writes its own
configuration**, concurrently, under load, while serving. That is GeoServer's
data directory model, and we have direct evidence of where it leads:
GeoServer Cloud needed a `RemoteGeoServerEventBridge` over a message bus purely
to keep catalog state consistent once there was more than one node
([research/runtime-models-compared.md](../research/runtime-models-compared.md)
§2.4).

Choosing files as the source of truth means inventing a synchronisation
mechanism later. §82 says do not adopt a message bus without a concrete problem;
this alternative manufactures exactly that problem.

### Alternative C — Dedicated platform database, data sources registered separately

**For.** Survives the migration case: data source databases can be foreign,
read-only, multiple, and not ours. Clean ownership boundary. Concurrent runtime
writes, transactions and constraints are what a database is for. Clustering
needs no new mechanism, because shared state already lives somewhere shared.

**Against.** Two logical databases where a small deployment wanted one. Mitigated
by allowing both to be the same PostgreSQL *instance* in the baseline.

### Alternative D — Embedded store (SQLite) single-node, database when clustered

**For.** Removes PostgreSQL as an install prerequisite. Supports `./gis-server`.

**Against.** Two stores means two schema migration paths, two backup stories, two
sets of bugs, and a class of defects that only appears in one deployment shape.
Under §82 the question is what concrete problem it solves — and for the baseline
the answer is *none*, because the spatial data is in PostGIS already, so
PostgreSQL is present regardless.

It only pays off if **all** data is file-based, which is the DuckDB P4 scenario
([research/duckdb-geoparquet.md](../research/duckdb-geoparquet.md)) and is
governed by Q-22, not by this ADR.

## 3. Counterarguments to the preferred option

Against Alternative C, honestly:

- **It makes PostgreSQL a hard dependency for the platform itself**, not just
  for the default data store. A file-only or DuckDB-only deployment becomes a
  reopening of this ADR rather than a configuration flag. That is a real
  reduction in flexibility and it is chosen deliberately.
- **The GitOps and reproducibility benefits of Alternative B are genuine** and
  we are giving them up as the primary mechanism. §5 recovers most of them
  through export/import, but not all: a file that is generated is not a file
  that is authored, and reviewing a diff of exported state is less pleasant than
  reviewing a diff of hand-written configuration.
- **Two databases is a real operational cost** in the small case — one more
  connection string, one more thing to get wrong at 2 AM. Allowing co-location
  in one instance softens this but does not remove it.

## 4. Decision

**A relational database is the platform's single source of truth for all durable
platform state, and it is not necessarily PostgreSQL.** Alternative C, with the
following shape:

> **AMENDED 2026-08-12.** The owner decided PostgreSQL is not mandatory; Oracle
> Spatial and SQL Server Spatial are first-class. A-009 is `INVALIDATED` and
> §4.1 and §4.4 below are superseded by §4a. Everything else in this decision
> survives — see
> [research/multi-database-consequences.md](../research/multi-database-consequences.md)
> §5 for why the reopening stops there.
>
> The condition in §9.2 required this to be revisited *before any metadata SQL is
> written*. Nothing had been written. The register worked.

1. ~~**The platform database is logically distinct from data source databases.**
   The baseline deployment may put both in one PostgreSQL instance — as separate
   databases, or as a separate schema — and that should be the default a small
   install gets. **The architecture must never assume co-location.**~~
   *Superseded by §4a; the separation itself stands and is strengthened.*
2. **Data sources are registered connections**, first-class entities in the
   platform database. They may be foreign, read-only, numerous, and of different
   provider types. We require no DDL rights on them, and no rights at all beyond
   what the registered service needs.
3. **Files are an export and import format, not the source of truth.** Full or
   partial platform state serialises to a documented, human-readable,
   diff-friendly format, and imports back. This serves air-gapped promotion,
   GitOps-style review, disaster recovery, and migration intake — without
   introducing concurrent writers to a filesystem.
4. ~~**Metadata access lives in one module**, not behind a dialect abstraction.~~
   *Superseded by §4a. The reasoning was sound on the information available —
   §82 forbids paying for unrequested portability — and it is now void, because
   the portability has been requested for a concrete reason.*
5. **Schema migrations exist from day one**, versioned, forward-only by default,
   with an explicit and tested rollback story. Q-13 is thereby forced early
   rather than discovered late.
6. **Large binary assets are not database rows.** Glyph ranges, sprite sheets and
   tile cache contents live on a filesystem or object store, with their metadata
   and identity in the platform database.
7. **Secrets are encrypted at rest** in the platform database, with the
   encryption key supplied externally at startup — environment, file or an
   external secret store. Data source credentials must never be readable by
   someone with a database dump. External secret managers are an optional
   provider, never a requirement (§2).

## 4a. Amendment — the platform store is portable

Replaces §4.1 and §4.4.

1. **The platform store is a port with a deliberately boring surface.**
   Supported implementations: **SQLite** (embedded default for developer and
   single-node small installs), **PostgreSQL**, **SQL Server**, **Oracle**. No
   spatial types, no exotic SQL, nothing that only one engine can do.
2. **The platform store is independent of the data source engines.** A customer
   may run Oracle Spatial for data and SQLite for the platform, or SQL Server
   for both. Co-location is a deployment convenience, never an assumption. This
   strengthens the separation in the original §4.1 rather than weakening it.
3. **Portability constraints are binding on all platform SQL**, and are settled
   before the first statement is written:
   no `JSONB` operators · no `LISTEN`/`NOTIFY` · no arrays or PostgreSQL-specific
   types · `SKIP LOCKED` expressed per dialect (`FOR UPDATE SKIP LOCKED` on
   PostgreSQL and Oracle, `WITH (UPDLOCK, READPAST)` on SQL Server) · identity,
   upsert and `RETURNING` behind the store module · conservative identifier
   naming for Oracle.
4. **Losing `LISTEN`/`NOTIFY` is the real cost.** Cross-node change notification
   must be portable, so polling a change-sequence column is the assumed
   mechanism. This affects the event architecture (§45) and cache invalidation.
   Recorded as Q-30.
5. **Phasing is permitted; unvalidated design is not.** SQLite and PostgreSQL
   may ship first with SQL Server and Oracle following, **provided the port
   design is checked against all four dialects on paper now.** Designing against
   two and discovering the third does not fit is the standard failure here.
6. **An untested backend is a broken backend.** Any store claimed as supported
   runs in CI against the full migration and integration suite, or it is
   documented as unsupported. Q-29 decides which ship in v1.

## 5. State inventory

Required by ADR-012 and §80.29–30. This is the deliverable that makes clustering
possible later without a redesign.

### Durable and shared — the platform database

Service definitions and catalog · stable service identities (§37) · data source
registrations · style documents · users, roles and grants · job records and
history · publishing history and rollback points · cache index and invalidation
metadata · schema version.

### Durable but not in the database — filesystem or object store

Glyph ranges and sprite sheets · tile cache contents (L3) · uploaded artefacts
awaiting registration.

Each has an identity row in the platform database. **The bytes are node-local
unless placed on shared storage**, and that is the main thing clustering will
have to confront.

### Ephemeral and node-local

L1 caches and warm per-service state · worker process state · in-flight requests
and their queues · connection pools · prepared statements · PROJ and GEOS
per-thread contexts
([research/dependency-thread-safety.md](../research/dependency-thread-safety.md)).

### The honest ambiguity

**Cache contents are the awkward case.** The index is shared; the bytes may not
be. A multi-node deployment either shares storage, replicates, or accepts that a
tile cached on node A is a miss on node B. This ADR does not decide it — it
records that the ambiguity exists and belongs to ADR-010 and ADR-012, so that it
is not discovered during a clustering project.

## 6. Consequences

**Positive.**

- Concurrent runtime administration works, with transactions and constraints,
  because that is what databases do.
- The migration case is supported: foreign, read-only, multiple data sources.
- Clustering needs no new synchronisation mechanism for platform state — the
  GeoServer Cloud message bus problem does not arise.
- The state inventory above is produced now, cheaply, rather than reconstructed
  later.
- Publishing (§38) gets validation and rollback for free from transactions.

**Negative.**

- PostgreSQL becomes a hard dependency of the platform, not only of the default
  data store. A PostgreSQL-free profile is a reopening of this ADR.
- Two logical databases in the small case, even when co-located.
- We own a migration system, an export/import format and their compatibility
  guarantees from day one.
- Losing files-as-source-of-truth costs some of the GitOps experience, only
  partly recovered by §4.3.

**Ports created.** The **platform store port** (§4a.1) — a deliberately narrow
relational surface with SQLite, PostgreSQL, SQL Server and Oracle
implementations. This is a Tier 2 seam in the sense of
[build-vs-adopt-policy.md](../build-vs-adopt-policy.md), and it exists because
portability was requested for a concrete reason, not anticipated speculatively.

**Additional negative consequence from the amendment.** Four backends means four
migration paths and a four-way test matrix that must actually run. This is
ongoing cost for the life of the project, and it buys removal of an adoption
barrier in front of our primary migration target.

## 7. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-009 | PostgreSQL/PostGIS is an acceptable hard dependency for the baseline | `INVALIDATED` 2026-08-12 — superseded by A-018 |
| A-018 | A deliberately boring platform schema can be supported across SQLite, PostgreSQL, SQL Server and Oracle at acceptable cost | `UNVALIDATED` — **this decision now depends on it** |
| A-017 | Data sources will frequently be foreign and possibly read-only, so the platform cannot rely on DDL rights in them | `VALIDATING` — follows from the confirmed migration goal; confirm with Q-08 |

## 8. Dependencies

**Depends on:** ADR-001 only weakly — the decision is about where state lives,
not how it is accessed. Deliberately taken ahead of the language decision.

**Depended on by:** ADR-007 (what workers may cache versus must read), ADR-010
(cache index location), ADR-011 (job persistence), ADR-012 (the §5 inventory is
its precondition), publishing (§38), admin API (§39), authorization (§42).

## 9. Conditions

This ADR is `ACCEPTED WITH CONDITIONS`. The conditions:

1. ~~A-009 must be confirmed.~~ **Fired 2026-08-12.** A-009 was invalidated and
   this ADR reopened, exactly as the condition specified. See §4a.
2. ~~Q-22 must be answered.~~ **Fired at the same time**, and the timing held:
   the revisit happened before any metadata SQL existed.
2a. **A-018 must hold.** If a portable boring schema across four engines proves
   more expensive than §4a assumes, the supported set shrinks — and the
   documentation shrinks with it rather than claiming untested support.
3. **The export/import format must be specified before the admin API ships.**
   Retrofitting a serialisation format onto an established schema produces a
   format shaped by the schema rather than by the operator, and it is the kind
   of thing that never gets fixed.

## 10. Revisit triggers

- Q-22 answers that PostgreSQL-free is a goal.
- Q-08 answers that the platform owns its data exclusively, which would weaken
  A-017 and make Alternative A viable again.
- Platform database write contention appears at 1,000 services — measurable, and
  it would indicate the metadata model is wrong rather than the store.
- Any requirement for the platform to operate with **no** database at all.

## 11. Dissent

Recorded rather than smoothed over (§8).

**The file-based case is stronger than the decision gives it credit for.** For
air-gapped, regulated and infrastructure-as-code environments, configuration
that lives in Git and deploys immutably is not merely convenient — it is how
some organisations are permitted to operate. §4.3's export/import recovers the
mechanics but not the posture: a system whose truth lives in a database and
exports files is a different thing from one whose truth lives in files.

The decision rests on the primary user being a GIS administrator making runtime
changes. **If that user turns out to be wrong — if real deployments are
configured once by a platform team and rarely touched — Alternative B becomes
the better answer and this ADR should be reopened without embarrassment.**
