# ADR-011 — Job System

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` |
| **Decided** | 2026-08-12 |

---

## 1. Context

This ADR started life as "geoprocessing" (§36). It is now considerably more
central than that.

[ADR-007](ADR-007-service-runtime.md) §4.1 made the job pool **the platform's
isolation boundary**. Everything long-running, untrusted or crash-prone runs
there:

| Job kind | Why it is a job |
|---|---|
| **Data registration and validation** | GDAL against untrusted files. Crash-prone, and the *first thing an administrator does*. |
| **Overview generation** | Long, CPU and I/O heavy |
| **Tile cache seeding** | Long, and must be rate-limited against the source ([ADR-010](ADR-010-caching.md) §6) |
| **Geoprocessing** | §36, the original scope |
| **Schema operations on hosted layers** | DDL can be slow; ArcGIS built an async path for exactly this |
| **Plugins**, if ADR-006 admits any | Untrusted code |

**Registration being a job puts this ADR on the critical path for the very first
thing a user does.** It cannot be a later phase.

## 2. Not all jobs are OGC API Processes

A distinction worth making early, because conflating them would drag an external
specification into internal operations.

| | Surface | Examples |
|---|---|---|
| **User-facing processing** | OGC API Processes | Geoprocessing, analysis |
| **Administrative operations** | Admin API (§39) | Registration, validation, seeding, overview generation, schema changes |

One engine, two surfaces. OGC API Processes describes the first; it has no
business describing the second. This is §8 applied to jobs: the external
protocol does not dictate the internal model.

## 3. Decision

### 3.1 The queue lives in the platform store

Alternative 3 — an external broker — is **rejected**.

§82 requires a concrete problem before adopting infrastructure. The platform
store already exists, already has transactions, and already holds durable shared
state ([ADR-002](ADR-002-primary-data-architecture.md)). A broker would add an
operational dependency, a second consistency model, and a second thing to back
up, in exchange for throughput we do not need. Our job rate is measured in jobs
per minute, not messages per second.

Alternative 2 — in-process scheduler with database-persisted state — is what we
are actually building; the distinction from alternative 1 dissolves once state
is durable.

### 3.2 Claiming is per dialect

The platform store is portable across four engines
([ADR-002](ADR-002-primary-data-architecture.md) §4a), so the claim mechanism is
not one statement:

| Store | Claim |
|---|---|
| PostgreSQL | `SELECT … FOR UPDATE SKIP LOCKED` |
| Oracle | `SELECT … FOR UPDATE SKIP LOCKED` |
| SQL Server | `WITH (UPDLOCK, READPAST)` |
| SQLite | Single writer; no contention to skip, so a plain guarded update suffices |

`VERIFY` each of these against the exact versions we support. The *pattern* is
portable; the syntax is not, and that is the store module's problem rather than
the job engine's.

### 3.3 Wake-up is polling, and the interval is a documented number

`LISTEN`/`NOTIFY` is not portable
([ADR-002](ADR-002-primary-data-architecture.md) §4a.4), so workers poll.

The interval is a trade between latency and load, and it must be **a documented
number rather than an implementation detail**, because it is the floor on how
long an administrator waits after pressing a button. A registration that takes
two seconds but starts five seconds late is a five-second product.

Mitigation for the interactive case: when a job is submitted through the admin
API on the same node, the local worker may be nudged directly. This is an
optimisation and correctness must not depend on it.

### 3.4 Leases and heartbeats, and the constraint they impose

A claimed job holds a **lease**, renewed by heartbeat. If a worker dies, the
lease expires and another worker reclaims the job.

That is standard, and it imposes a requirement that is not:

> **Every job type must declare how it behaves when re-run.**

| Declaration | Meaning | On reclaim |
|---|---|---|
| `IDEMPOTENT` | Safe to run again from the start | Restart |
| `RESUMABLE` | Has durable checkpoints | Resume from the last checkpoint |
| `NEITHER` | Re-running would duplicate or corrupt | **Mark failed, require an operator decision** |

Most of our jobs are naturally in the first two categories. Seeding is resumable
by tile. Overview generation is idempotent. Validation is idempotent.

`NEITHER` must be rare and visible. A job system that silently re-runs
non-idempotent work is a data-corruption mechanism with a progress bar.

### 3.5 Classes, not priority numbers

Priority numbers get gamed. Within a year everything is priority one.

Instead, **job classes with separate concurrency budgets**:

| Class | Examples | Budget |
|---|---|---|
| **Interactive** | Registration, validation, schema change | Reserved slots. A human is waiting. |
| **Background** | Seeding, overview generation | Bounded, yields to interactive |
| **User** | Geoprocessing | Bounded, fair-shared between users |

The point is that **a large seed must never starve a registration.** Reserved
capacity does that structurally; a priority field only does it if everyone is
honest.

Fair-sharing within the user class also matters: one user submitting a hundred
jobs must not lock out everyone else.

### 3.6 Politeness toward the source database

From [ADR-010](ADR-010-caching.md) §6, and it generalises beyond seeding.

**Every job that touches a registered data source is rate-limited against it.**
An unthrottled seed across a large estate is a denial-of-service attack on the
customer's Oracle, delivered by us and with our name on it.

Concretely: per-source concurrency limits shared across all jobs, configurable,
with a conservative default. Same discipline as
[ADR-007](ADR-007-service-runtime.md) §4.8's connection budget, and it draws
from the same budget rather than a separate one — jobs and requests compete for
the same database.

**Must be in the first version.** Retrofitting politeness after someone's
production database has been saturated is too late to matter.

### 3.7 Progress, cancellation, and results

- **Progress is reported in domain terms**, not percentages of unknown wholes.
  "4,120 of 51,400 tiles" beats "8%" because an operator can estimate from it.
- **Cancellation is cooperative and prompt.** Jobs check for cancellation at
  checkpoint boundaries. A job that cannot be cancelled within a bounded time is
  a defect.
- **Large results do not live in the job record.** A row holds the state, timing
  and a reference; the payload goes to L3 storage. Job tables that accumulate
  blobs become the reason nobody can back up the platform store.

### 3.8 Retention is a policy, not an afterthought

Job history grows without bound and nobody notices until it is a problem.

- Completed jobs retained for a configurable window, then pruned.
- **Failed jobs retained longer than successful ones.** They are the ones
  someone will ask about.
- Pruning is itself a job, and it is subject to the same rules.

### 3.9 Multi-node

Job records are durable shared state in the platform store, so multi-node works
without new machinery — the claim mechanism in §3.2 already provides mutual
exclusion.

**A job must not run twice concurrently.** The lease enforces it. What the lease
cannot prevent is a partitioned worker continuing to work on a job whose lease
has expired and been reclaimed elsewhere. The mitigation is that jobs verify
their lease at checkpoints and abort if they have lost it — which is the same
checkpoint machinery §3.4 already requires. Recorded as A-030.

## 4. Counterarguments

- **A database queue does not scale like a broker.** True, and irrelevant at our
  job rate. It becomes relevant if job volume grows by orders of magnitude, and
  that is the revisit trigger.
- **Polling adds latency to every job.** §3.3 mitigates it for the interactive
  case and documents it otherwise. The honest position is that this design
  optimises for having no broker over having minimum latency.
- **Four claim implementations is four places to get locking wrong**, and
  locking bugs surface under load, in production, rarely. This is the strongest
  argument against the portable platform store, and it lands here rather than in
  ADR-002.
- **Reserved capacity per class wastes capacity** when a class is idle. Accepted
  deliberately: an idle reserved slot is cheaper than a registration queued
  behind a six-hour seed.

## 5. Consequences

**Positive.** No broker, no new operational dependency. Registration, seeding
and geoprocessing share one engine with one set of semantics. The isolation
boundary is a real boundary. Politeness toward customer databases is structural
rather than aspirational.

**Negative.** Polling latency. Four claim implementations to write and test.
Every job type must declare its re-run behaviour, which is a discipline that can
erode. Job history is a store that needs managing.

**Ports created.** The **job store port**, part of the platform store module —
claim, heartbeat, complete, fail, list. Deliberately narrow.

## 6. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-030 | Lease expiry plus checkpoint verification is sufficient to prevent concurrent execution of one job across nodes | `UNVALIDATED` |
| A-031 | Job rate stays low enough that a database-backed queue is adequate — jobs per minute, not messages per second | `UNVALIDATED` |

## 7. Dependencies

**Depends on:** [ADR-002](ADR-002-primary-data-architecture.md) (the platform
store and its four dialects), [ADR-007](ADR-007-service-runtime.md) (job workers
are the isolation boundary, and share the connection budget).

**Depended on by:** [ADR-010](ADR-010-caching.md) (seeding), publishing and
registration (§38), ADR-009 (overview generation, validation), ADR-006 (plugins
run as jobs), admin API (§39), geoprocessing (§36).

## 8. Conditions

1. **Rate limiting against source databases ships in version one.** Not a
   follow-up.
2. **Every job type declares its re-run behaviour** before it is registered.
   There is no default, because a wrong default here corrupts data.
3. **The polling interval is documented as a number.**
4. **Job payloads never live in the job table.**

## 9. Revisit triggers

- Job volume rises to a rate where a database queue is the bottleneck
  (invalidates A-031).
- A job is observed running twice concurrently (invalidates A-030).
- A class-based budget proves too rigid and real workloads need finer control.

## 10. Dissent

**Making registration a job is right and it costs us something real.** A
synchronous registration that returns an answer in two seconds is a better
experience than an asynchronous one that returns a job identifier and a polling
URL, and most registrations will be fast.

The counter-argument holds — GDAL on an untrusted file is exactly the work that
must not run in a request worker, and ArcGIS added an async path for schema
operations because they hit the same wall — but the cost is a worse first
five minutes for every new user.

Worth mitigating rather than accepting: a job that completes within a short
window could return its result directly, with the polling URL as the fallback
for anything slower. That is a small piece of design that would repay itself,
and it is not in this ADR.
