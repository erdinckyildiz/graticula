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
style reference, CRS transform, **and effective authorization data** — is bound
into a worker **lazily, on first request**, and evicted when cold.

**Constraint added 2026-08-12** ([failure-scenarios.md](../failure-scenarios.md)
N1): **a bound context must be self-sufficient for serving.** If answering a
request requires consulting the platform store, a store outage takes everything
down rather than freezing it in a degraded read-only mode. Authorization data is
part of the context for this reason, and **anything not already resolved in the
context fails closed** — an outage must never become an access-control bypass. Publishing a service writes a definition to
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
- Each worker has an explicit **context budget**. **Amended after adversarial
  review (F6): the budget is a resource budget — retained bytes, or a weighted
  count — not a count of services.** A service over a 500-million-row table with
  three CRS transforms and a large style is not one unit of anything, and a
  worker holding fifty of those behaves nothing like one holding fifty point
  layers. Counting services is the same category error we criticised ArcSOC for
  in §3, in a different currency. `benchmarks/worker-model` must measure context
  weight distribution; if it is wide, count-based budgeting is invalid.
- Contexts evict LRU when the budget is exceeded. Eviction is cheap if A-015
  holds; if warm state turns out to be expensive, this design weakens
  considerably.
- **Affinity degrades to plain balancing under skew.** One very hot service must
  not be pinned to one worker. That degradation boundary is the risky part.

**This is a hypothesis, and it is the least proven part of this ADR.**
`experiments/affinity-routing/` must run before it is relied on (A-014).

**Amended after adversarial review (F2): it is also a control system, and
control systems oscillate.** Lazy binding, LRU eviction, affinity preference,
skew degradation and auto-pinning are five interacting feedback mechanisms with
no damping specified — no hysteresis on pinning, no minimum residency, no cost
accounting for a rebind, no stability criterion.

The failure path is not hypothetical:

```text
A gets busy → auto-pinned → pin budget pressure evicts B's pin
→ B's contexts evict under LRU → B rebinds constantly, slows
→ B looks hot → B auto-pins, evicting C → …
```

And "degrades to plain balancing under skew" is an unspecified switching rule
between two control regimes, which is exactly where systems flap. At what
threshold, measured how, over what window, with what transition behaviour?

**The experiment's success criterion is convergence, not hit rate.** It must
test sustained skew, oscillating skew, and a slow ramp across the budget
boundary. If stability cannot be demonstrated, the correct answer is plain
balancing with pinning as the only affinity — simpler, and provably stable.

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

**Correction, 2026-08-12.** An earlier version of this section claimed an idle
connection is a DDL hazard. **It is not**, and the distinction matters because
it changes the policy.

`VERIFY` on PostgreSQL, `ALTER TABLE` needs `ACCESS EXCLUSIVE`, which conflicts
with the `ACCESS SHARE` held by a *transaction that has touched the table*:

| State | Blocks DDL? |
|---|---|
| Running query on the table | Yes |
| Idle in transaction, having touched the table | Yes |
| Open connection, no transaction | **No** |

`VERIFY` the same principle holds on SQL Server (schema-stability locks are held
during execution, not by idle sessions) and Oracle (DDL conflicts with active
transactions).

So **connection pooling itself is not the hazard. Long transactions and
idle-in-transaction are.** Shrink-to-zero is a *budget* policy, not a lock
policy, and it should be an idle timeout rather than aggressive closing.

**The policy:**

- Pools are **per (worker, data source)**, never per service.
  **Conflict resolved 2026-08-12** — [security.md](../security.md) §2. Pools stay
  per data source and do **not** fragment per principal. RLS delegation becomes
  an opt-in provider capability using **transaction-scoped** identity switching,
  so any connection can serve any principal and the identity cannot outlive the
  commit.

  One consequence lands on this section: a delegated query runs inside an
  explicit transaction, which holds `ACCESS SHARE` for its duration — exactly
  what §5b says must be short. Streaming a large result under delegation
  therefore holds that lock for the whole stream. Recorded as A-036 with three
  partial mitigations, none free.
- **Never idle in transaction.** This is the actual lock discipline, and it is
  non-negotiable.
- Statement timeouts are mandatory. A long query is a held lock.
- Pools **shrink toward a floor after an idle period** — the floor is zero by
  default and configurable upward, see §4.12. This is budget management, not
  lock avoidance, so the timeout can be generous.
- A **global connection cap per worker**, enforced across all pools, so a
  deployment with many data sources degrades by queueing rather than by
  exhausting the database.
- **A per-source concurrency limit on the request path**, separate from the pool
  size (N4). §49 limits per service and pools limit per source, but many
  services share one database — so twenty slow services on one slow source can
  saturate a worker while each respects its own limit. Pool size is sized for
  throughput; this number is sized for blast radius, and they should not be the
  same number.
- **A circuit breaker per data source** (N3), with backoff. Without one, an
  outage becomes a connection storm at exactly the moment recovery is being
  attempted.
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

### 4.12 Pinning — keeping a hot service permanently warm

§4.3 binds contexts lazily and §4.4 evicts them LRU. A service under constant
load must not pay either cost. This is ArcSOC's dedicated-instance problem, and
it needs an answer.

**ArcGIS's answer:** dedicated instances with a minimum above zero. `VERIFY`
"if you set this parameter to three instances, there will always be at least
three instances running on ArcSOC processes at any given time, even when the
service is not being used." Recommended for services under a service-level
agreement, under heavy use, or that are compute-intensive. Setting the minimum
to zero saves memory at the cost of a cold first request.

**Our answer: pin the context, not a process.**

| | ArcGIS dedicated instance | Our pinned context |
|---|---|---|
| What is held | An `ArcSOC` process | A service context |
| `VERIFY` cost | ~100–200 MB | Connections, compiled schema, style reference, CRS transform — order of megabytes |
| Granularity | Whole process per service | One entry in a worker's context table |

Same intent, roughly two orders of magnitude cheaper. That difference is what
makes it affordable to pin the services that matter without recreating the
arithmetic that killed process-per-service (§3).

**What pinning does:**

1. **Exempt from LRU eviction.** A pinned context stays bound.
2. **Pre-bound rather than lazily bound.** Bound when a worker starts, so the
   first request after a recycle is not cold.
3. **Present in at least K workers**, so a single worker recycle does not leave
   the service cold. K defaults to more than one for pinned services.
4. **A connection floor on its data source.** The pool for that source keeps a
   minimum warm rather than shrinking to zero — this is the "keep a resource
   permanently open" part, and §4.8's correction is what makes it safe.

**Pinning is bounded, and pins compete.** There is a global pin budget per
worker, sized against the context budget. If everything is pinned, nothing is —
that is the ArcSOC failure mode arriving through a different door. When the
budget is exceeded, the administrator is told which pins are contending rather
than having one silently dropped.

**Who decides.** Both, and the order matters:

- **Default: unpinned.** Lazy binding and LRU, which is right for the long tail.
- **Observed: auto-pin.** A service consistently hot enough to be rebound
  constantly is pinned by policy (§4.5). Recorded and visible, never silent.
- **Administrator: explicit pin.** This is condition 4's manual override made
  concrete, and it is the mechanism for a service under an SLA — exactly
  ArcGIS's documented use case.

**This is a per-service configuration knob, which A-008 warned against.** The
resolution is that it is an *exception* surface, not the primary one. ArcGIS's
mistake was making min/max instances the interface for every service, so
administrators had to tune a thousand of them. Ours is unpinned by default,
observed in the middle, and hand-pinned for the handful that carry an SLA. A
knob nobody has to touch is not the same as a knob that does not exist.

**Recorded as A-025**: that a small number of pinned contexts is enough for real
deployments, and administrators will not simply pin everything. If they do, the
budget in the paragraph above is what keeps the system standing, and the
guidance has failed.

### 4.13 The supervisor this ADR depends on does not exist yet

**Found by the failure scenario pass, 2026-08-12**
([failure-scenarios.md](../failure-scenarios.md) N5). Recorded here because it
is this ADR's dependency and this ADR did not notice it.

§21 requires a runtime supervisor: worker startup and shutdown, health
monitoring, crash detection, restart, draining, recycling, memory monitoring,
CPU monitoring, stuck-request detection, concurrency enforcement, resource
governance.

**Designed 2026-08-12** - [runtime-supervisor.md](../runtime-supervisor.md).
The gap was real: this ADR named worker *states* and assumed something drove
them, with recycling (§4.7), draining, quiescing (§4.8) and observed escalation
(§4.5) all written as supervisor behaviours before the supervisor existed.

The design in one line: **a management-plane failure must not become a
data-plane failure.** A per-node supervisor process, tiny and serving no
requests, with the platform (systemd, Kubernetes, a Windows service) keeping
*it* alive. Workers survive its death and a restarted supervisor re-adopts them.

Three things worth carrying back here:

- **It is not the distinguished node §4.10 forbids.** ArcSOC's SOM was a
  *site-wide* manager other machines depended on. This is per node and local; a
  node with an unhealthy supervisor is one degraded node, not a degraded site.
- **Heartbeats detect death, not stuckness.** A heartbeat thread beats happily
  while every request thread is deadlocked. Workers therefore report the **age
  of their oldest in-flight request**, and that is what stuck detection watches.
- **The supervisor is not the router.** Combining them would make the component
  that must not crash into the component handling every request. Where the
  routing decision lives is now Q-63, and it should be settled by
  `experiments/affinity-routing` rather than assumed.

The loose end is answered: the supervisor marks a worker unavailable on crash
detection and whatever routes reads that state, so detection happens **before**
routing - provided the routing component observes supervisor state, which is a
constraint on Q-63's answer.

### 4.14 Allocation rate, which this ADR sizes nothing against

**Added 2026-08-12 from measurement**, [benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md) findings 10 and run 3.

**Measured under concurrency the same day.** At concurrency 16 on the tile path
the process was suspended for garbage collection **80.9% of wall-clock time
while using 2.92 of 16 cores**. Sixteen times the concurrency bought 1.6x the
throughput. Pushing clip and simplify into the database (A-021) raises the
ceiling to 69.9 req/s and 4.9 MB per request, but 65.6% GC pause remains. A
control endpoint doing one scalar query sustained 1,984 req/s at 0.0 MB per
request, so this is the workload, not the framework.

**A-037 is `VALIDATED`.** §4.1 sizes workers against cores. On this workload a
worker saturates at under one fifth of its cores. Everything below stands as
written; what follows is what it costs.

A single z12 vector tile request allocates **204 MB**, after the optimisation
that halved it from 404 MB. That is one request, on one tile, single-threaded.

Everything in §4 sizes workers against CPU and against a per-worker service
context budget. Neither is necessarily the binding constraint. At the
concurrency this ADR assumes, allocation rate plausibly sets the ceiling first —
and unlike CPU it is not visible in a per-request latency measurement, because
the pause lands on whichever request is unlucky. The measured GC pauses of
18–153 ms were distributed across stages in a way that made individual stage
timings unusable until minima were taken.

Three consequences, none of them yet quantified:

- **Worker sizing (§4.1) has no allocation term.** A worker that is CPU-idle can
  still be GC-bound. `A-037` records this and it is `UNVALIDATED`.
- **Server GC's heap grows with core count.** The measurement ran with
  `ServerGarbageCollection`, which is right for throughput and means per-worker
  memory is not the flat number §4.4's context budget assumes.
- **Seeding (ADR-010 §6) walks whole tile pyramids** of exactly this cost, on
  job workers that §4.2 deliberately gave less memory than request workers.

This does not change any decision in §4. It records that a load-bearing number
is missing, so that it is not discovered during a capacity incident instead.

### 4.15 A second, bounded pool for attachment streaming

**Added 2026-08-13 by [ADR-013](ADR-013-feature-service-data-model.md) §4b.**

Attachments are stored in the database, so serving one holds a pooled connection
for as long as the client takes to receive the bytes. A slow client — or a
malicious one reading at one byte per second — holds it indefinitely, and enough
of them exhaust the pool. **The whole layer stops serving, not just
attachments.** This is slowloris pointed at §4.8's connection budget.

Attachment reads therefore draw from a **separate, small, bounded pool**,
isolated from the query budget. Exhausting it degrades attachments only.

Two things this ADR does not yet have: a number for that pool, and evidence that
isolation alone is enough. Both are `A-041`. If it is insufficient, the fallback
is buffer-and-release above a size threshold, which reintroduces the memory
problem streaming exists to avoid — and ADR-013 §9 makes that the trigger to
reopen the storage decision rather than patch it.

### 4.16 Certificate rotation must not restart a worker

**Added 2026-08-13 by [ADR-014](ADR-014-tls-and-certificates.md) §2b.**

§4.3 binds service contexts lazily and §4.4 keeps them warm, with affinity
routing existing specifically to preserve that warmth. **A worker restart evicts
every warm context on it.**

So installing or rotating a TLS certificate by restarting would trigger the
cold-start storm this section is built to avoid — on a schedule, for a reason
unrelated to any service, and typically at whatever hour the certificate happens
to expire. Certificates therefore take effect on the next handshake, with
existing connections finishing on the old one. `A-044`.

**A second, smaller interaction.** §4.8 shrinks idle pools to zero. With TLS
required on remote data sources (ADR-014 §3), every pool refill now pays a
handshake rather than a plain connect. That belongs in Q-04's connection budget
measurement rather than being assumed negligible.

### 4.17 Worker introspection must expose allocation, not only CPU

**Added 2026-08-13 by [ADR-017](ADR-017-admin-api.md) §3.2**, and it is that
ADR's largest consequence rather than a detail of it.

Walking the *this service is slow* scenario end to end showed that the standard
worker view — CPU, memory, request rate, latency — **cannot explain the failure
mode this runtime actually has.** A-037 measured **80.9% GC pause at 18% CPU
utilisation**: an administrator looking at a CPU graph sees an idle worker and
concludes the problem is elsewhere.

So `/admin/workers/{id}` exposes **allocation rate and GC pause share** alongside
the usual counters, and they are not optional extras — they are the two numbers
that make §4.14's ceiling visible. `A-050` asks whether they are affordable as
continuous metrics rather than benchmark instruments; the benchmark harness read
them per request without difficulty, which is encouraging but not the same test.

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

1. **A-014 must be prototyped before affinity routing is implemented, and the
   prototype must demonstrate stability under adversarial load, not merely a
   better hit rate** (adversarial review F2). If it fails, §4.4 is replaced by
   plain balancing and this ADR is amended, not quietly ignored.
1a. **The context budget is validated as a resource budget** (F6) before it is
   implemented as a count.
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
