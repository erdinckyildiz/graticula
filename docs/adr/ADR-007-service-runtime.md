# ADR-007 — Service Runtime

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — structure well supported, routing unproven |
| **Decided** | 2026-08-12 |

---

## 1. The question

*What is the modern equivalent of ArcSOC?* (§16, §80.5)

§17 gives the layering to preserve:

```text
Service Definition   durable configuration
        ↓
Service Runtime      the policy by which it is executed
        ↓
Runtime Pool         a managed set of workers
        ↓
Worker               disposable execution infrastructure
```

The historical mistake this ADR exists to avoid is conflating the top and the
bottom — letting the shape of the catalogue determine the shape of the process
tree.

## 2. The headline

> **Workers are sized to the machine, not to the catalogue.**

Worker count is a function of cores and memory. It is not a function of how many
services are published. Ten services and a thousand services run on the same
number of workers.

Everything below follows from that inversion.

## 3. What the evidence already settled

Not reopened here; recorded so the decision is traceable.

| Finding | Source |
|---|---|
| Process-per-service with a warm minimum is arithmetically dead at our scale — roughly 150 GB for 1,000 services at ArcSOC-like per-process cost | [research/arcgis-som-soc.md](../research/arcgis-som-soc.md) §3.3 |
| A distinguished central manager process is a robustness and recovery liability; Esri removed the SOM at 10.1 for exactly this | ibid. §3.1, A-011 |
| The real axis is per-service state size, binding cost and neighbour tolerance — not "shared versus dedicated" | ibid. §3.4, A-012 |
| Every prior system fragments warm state across workers and then routes blindly; QGIS Server documents the resulting misses | [research/runtime-models-compared.md](../research/runtime-models-compared.md) §3 |
| Heavy isolation is not universally necessary — GeoServer serves everything from one heap and is widely deployed | ibid. §4 |
| Warm per-service state is small: connections, schema, symbology, fonts, CRS. Not data | ibid. §2.3, A-015 |
| A threaded worker model is available; no dependency forces process-per-worker | [research/dependency-thread-safety.md](../research/dependency-thread-safety.md) |
| Vector-first removes the map rendering worker class, and takes GDAL off the request path | [product-context.md](../product-context.md) |

## 4. Decision

### 4.1 Two pools, not five worker classes

§20 hypothesised five specialised worker classes. Vector-first and the removal
of editing from our API collapse that to two.

| Pool | Workload | Model |
|---|---|---|
| **Request workers** | Feature queries, vector tiles, catalog and metadata reads | Small number of multi-tenant processes, threaded internally |
| **Job workers** | Geoprocessing, data registration and validation (GDAL), overview generation, cache seeding, plugins if any | Separate processes, isolated, restarted freely |

Feature and tile workloads are not distinguishable enough to separate: both are
database-bound reads followed by serialisation. Raster is metadata-only now.
Rendering is gone.

**Isolation is applied on the workload axis, not the service axis.** This is the
hybrid model §19 asked us to investigate, hybridised along the dimension the
evidence supports rather than the one ArcSOC used.

### 4.2 Why more than one request worker

Crash containment is a weak argument now (A-007 is `CONTESTED` and weakening).
The real reasons are:

- **Rolling recycle without downtime.** A single process cannot be drained and
  replaced without an outage.
- **Memory ceiling.** Managed runtimes behave badly with one enormous heap;
  several bounded heaps are more predictable.
- **Blast radius of a genuine fault**, which is not zero even if it is small.

So N is small — order of a handful, sized to cores and memory — and **explicitly
not derived from service count**.

### 4.3 Services do not start

There is no service startup, and therefore no thundering herd (§26).

A **service context** — connection handle, compiled schema, field metadata,
style reference, CRS transform — is bound into a worker **lazily, on first
request**, and evicted when cold. Publishing a service writes a definition to
the platform store. It does not create, start or reserve anything in a worker.

This dissolves §26's problem rather than staging around it. A node restart
brings up N workers with no bound contexts; load re-warms them at the rate real
traffic arrives.

It also makes A-003 (most services idle most of the time) **less load-bearing
than it was**: idle services cost a row in a table, not a process.

### 4.4 Affinity routing, with a bounded context budget

The gap in every prior system
([runtime-models-compared.md](../research/runtime-models-compared.md) §3): warm
state fragments across workers and the router does not know.

- The router tracks **which worker holds which service contexts.**
- A request prefers a worker already warm for its service.
- Each worker has an explicit **service context budget** — a number, not an
  emergent property. ArcSOC's `VERIFY` ~50 cached service contexts per shared
  instance is the same parameter, without routing that respects it.
- Contexts evict LRU when the budget is exceeded. Eviction is cheap if A-015
  holds; if warm state turns out to be expensive, this design weakens
  considerably.
- **Affinity degrades to plain balancing under skew.** One very hot service must
  not be pinned to one worker. That degradation boundary is the risky part.

**This is a hypothesis, and it is the least proven part of this ADR.**
`experiments/affinity-routing/` must run before it is relied on (A-014).

### 4.5 Escalation is observed, not configured

Per-service min/max instances is rejected as the control surface
([arcgis-som-soc.md](../research/arcgis-som-soc.md) §4, A-008). At 1,000
services nobody tunes it, and our primary user administers an estate rather than
a service.

Instead the system observes. A service that is consistently hot, or that
misbehaves as a neighbour, is escalated to reserved capacity by policy. The
administrator sets **policy and limits**, and can override a specific service
when they have a reason. Per-service tuning is an escape hatch, not the
interface.

### 4.6 The unit of refresh is the context, not the process

This is where §17's layering pays off concretely.

A service definition change — schema drift on a registered layer, an
administrator's schema edit on a hosted one, a style update — **invalidates the
service context, not the worker.** The worker evicts and rebinds that one
context. Nothing else on that worker is disturbed.

ArcSOC recycled instances on configuration change because instances *were*
services. Ours are not, so we do not have to.

The router already knows which workers hold the context, so invalidation targets
exactly those workers ([data-model.md](../data-model.md) §3,
[ADR-010](ADR-010-caching.md)).

### 4.7 Recycling is triggered by evidence, never by the clock

Recycle a worker on observed memory growth beyond a bound, on a crash, on an
administrator request, or on a deployment.

**Not on a schedule, and not on request count.** Scheduled recycling is a
leak-concealment device — it keeps a defective process alive as policy and
removes the pressure to find the defect.

### 4.8 Connection discipline, and the budget it produces

Two problems turn out to have one solution.

**Problem one: we must not block the DBA.** A held connection blocks DDL. On
PostgreSQL the DDL then queues every subsequent query behind it, and from the
DBA's side the table stops responding ([data-model.md](../data-model.md) §3).

**Problem two: §25's connection budget.**

```text
nodes × workers × data sources × pool size = potential connections
```

Note **data sources**, not services. Many services share one registered
database, which is what makes the arithmetic survivable at all. At four workers
and five data sources with a pool of five, that is 100 connections. At fifty
data sources it is 1,000, which is not.

**The same policy solves both:**

- Pools are **per (worker, data source)**, never per service.
- Pools **shrink to zero when idle.** An idle connection is both a wasted budget
  slot and a DDL hazard.
- Never idle in transaction.
- Statement timeouts are mandatory. A long query is a held lock.
- A **global connection cap per worker**, enforced across all pools, so a
  deployment with many data sources degrades by queueing rather than by
  exhausting the database.
- **Quiesce** is an administrative operation on a data source: drain its
  connections, hold its requests, let the DBA work, resume.

The budget must be produced per provider, since Oracle, SQL Server and
PostgreSQL price sessions differently (Q-04).

### 4.9 Backpressure

Queues are bounded (§48). When a worker's queue is full, admission control
rejects with a retry signal rather than accepting work it cannot do. Failure is
immediate and predictable, not a slow collapse.

Cancellation propagates end to end: client disconnect cancels the query
([ADR-008](ADR-008-query-engine.md) §4.4). An abandoned query is a held lock,
which is §4.8's problem again.

### 4.10 No distinguished node

Even single-node, no component is a manager that other nodes would depend on.
Routing and placement state is derivable from the platform store and local
observation. Esri removed the SOM for this reason (A-011), and baking a manager
into the single-node design is how it becomes impossible to remove later.

### 4.11 Service states, reinterpreted

§23 lists `CREATED`, `CONFIGURED`, `STARTING`, `RUNNING`, `DEGRADED`,
`DRAINING`, `STOPPED`, `FAILED`, `RESTARTING`.

Most of those are **process** states, and under §4.3 services are not processes.
A deliberate departure, stated so it is not mistaken for an oversight:

| Entity | States |
|---|---|
| **Service definition** | `DRAFT`, `PUBLISHED`, `DISABLED`, `FAILED_VALIDATION` |
| **Service health** (derived, observed) | `HEALTHY`, `DEGRADED`, `UNAVAILABLE` |
| **Data source** | `ACTIVE`, `QUIESCING`, `QUIESCED`, `UNREACHABLE` |
| **Worker** | `STARTING`, `RUNNING`, `DRAINING`, `STOPPED`, `FAILED` |

§23's requirement that transitions be observable applies to all four. The
administrator's question is "why is this service degraded", and the answer
composes from the service, its data source and the workers serving it.

## 5. Service explosion model (§24)

| Services | Request workers | Bound contexts | Processes | DB connections |
|---|---|---|---|---|
| 10 | N | ≤ 10 | N + job workers | data sources × active pools |
| 100 | N | ≤ budget × N | N + job workers | unchanged |
| 1,000 | **N** | ≤ budget × N | N + job workers | unchanged |
| 5,000 | **N** | ≤ budget × N | N + job workers | unchanged |
| 10,000 | **N** | ≤ budget × N | N + job workers | unchanged |

Worker count, process count and connection count are **flat in service count**.
What grows is rows in the platform store, catalog query cost, and — the item
routinely forgotten and routinely fatal — **monitoring cardinality** (Q-12).

At 10,000 services the pressures are catalog scale and metric cardinality, not
the runtime. That is the right place for the pressure to be, and it is a
different set of problems from the ones ArcSOC had.

`VERIFY` all of this by modelling with real numbers. The table states the
*shape* of the answer. The shape is the decision; the numbers are the condition.

## 6. Counterarguments

- **Affinity routing is unproven and it is load-bearing.** If it does not work
  we are GeoServer with extra steps: shared workers, fragmented warm state,
  blind routing. That fallback is acceptable but it is not the design.
- **Multi-tenant workers mean cross-service interference.** One expensive query
  degrades neighbours. Mitigated by per-service concurrency limits and statement
  timeouts, not eliminated. ArcSOC's dedicated instances existed partly for this
  and we are giving it up.
- **A-015 is doing a lot of work.** If warm per-service state is not small,
  context binding is not cheap, eviction hurts, and §4.3's lazy binding becomes
  a latency problem on every cold service.
- **Pools that shrink to zero pay reconnection cost.** Connection establishment
  is not free, especially on Oracle. The DDL and budget benefits may cost latency
  on cold paths, and the balance is unmeasured.
- **"Observed, not configured" is easy to write and hard to build.** Adaptive
  systems misbehave in ways configured ones do not, and they are harder to
  explain at 2 AM. A manual override must always win.

## 7. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Warm per-service state is small enough for cheap bind/unbind | — | `benchmarks/worker-model/`, A-015 |
| Affinity routing beats blind routing, and degrades safely under skew | — | `experiments/affinity-routing/`, A-014 |
| The connection budget holds at 1,000 services across three providers | — | `benchmarks/connection-budget/`, Q-04 |
| Shrink-to-zero pools do not cost unacceptable cold latency | — | `benchmarks/connection-budget/` |
| Cross-service interference is containable with per-service limits | — | `benchmarks/worker-model/` |

Empty, deliberately. The structure is decided; the numbers are the conditions.

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-003 | Most services are idle most of the time | `UNVALIDATED` — **less load-bearing now** that idle services cost a table row rather than a process |
| A-007 | Crash containment is genuinely required | `CONTESTED`, weakening — resolved per-path: job workers isolated, request workers not |
| A-008 | Administrators will not hand-tune per-service settings | `VALIDATING` — supported by prior art |
| A-011 | A distinguished central manager is a liability | `VALIDATING` — supported by prior art |
| A-012 | The real axis is state size, binding cost, neighbour tolerance | `VALIDATING` — supported by prior art |
| A-014 | Affinity routing works and degrades safely | `UNVALIDATED` — **the weakest point in this ADR** |
| A-015 | Warm per-service state is small | `UNVALIDATED` — **load-bearing** |
| A-023 | Schema fingerprint polling is cheap enough | `UNVALIDATED` |

## 9. Dependencies

**Depends on:** ADR-002 (durable state lives in the platform store, so workers
hold nothing that must survive them), ADR-008 (streaming and cancellation),
[data-model.md](../data-model.md) (layer modes, schema drift). **Not** ADR-001 —
the structure is language-independent, though N and the memory ceiling are not.

**Depended on by:** ADR-010 (L1 lifetime is context lifetime; routing and cache
are the same problem), ADR-011 (job workers), ADR-012 (no distinguished node),
ADR-006 (plugins are job workers if they exist at all), admin API (§39).

## 10. Conditions

1. **A-014 must be prototyped before affinity routing is implemented.** If it
   fails, §4.4 is replaced by plain balancing and this ADR is amended, not
   quietly ignored.
2. **A-015 must be measured before §4.3's lazy binding is relied upon.**
3. **The connection budget must be produced with real numbers, per provider**,
   before any deployment guidance is published.
4. **Manual override must exist for every adaptive behaviour** in §4.5. An
   administrator who disagrees with the system must be able to win.

## 11. Revisit triggers

- Observed idle-service ratio contradicts A-003 badly enough to matter.
- Cross-service interference appears in production shared workers.
- The connection budget is exceeded at target scale on any provider.
- Editing enters scope, which brings write concurrency and reopens §4.1.
- Affinity routing's skew degradation misbehaves under real load.

## 12. Dissent

**Giving up dedicated per-service isolation is a real loss, and the argument for
it is partly circumstantial.** GeoServer's success is evidence that a shared heap
works — but GeoServer is pure JVM, and our request workers will call into native
geometry and CRS libraries. The analogy is not exact. If our request path faults
in ways GeoServer's does not, §4.1's decision to isolate only job workers will
look optimistic.

**Second dissent: §4.5's "observed, not configured" is the least defensible
choice here.** It is the right answer for 1,000 services and the wrong answer for
an administrator trying to understand why a service behaved differently
yesterday. Adaptive systems are harder to reason about, and the 2 AM test (§7) is
our primary operational gate. The manual override in condition 4 is not a detail
— it is what keeps this decision honest.
