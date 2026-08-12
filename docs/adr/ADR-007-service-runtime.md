# ADR-007 — Service Runtime

| | |
|---|---|
| **Status** | `DRAFT` |
| **Confidence** | — |
| **Decided** | — |

---

## 1. Context

This is the ADR that answers *"what is the modern equivalent of ArcSOC?"*
(§16, §80.5) — the central architectural question of the project.

The separation demanded by §17 is the starting point:

```text
Service Definition   persistent configuration
        ↓
Service Runtime      the policy by which that definition is executed
        ↓
Runtime Pool         a managed set of workers
        ↓
Worker               disposable execution infrastructure
```

A service definition is durable. A worker is cattle. Conflating them is the
historical mistake this ADR exists to avoid repeating.

## 2. The constraint that shapes everything

The scale target is **100–1,000 services**
([product-context.md](../product-context.md)).

Any model resembling *one process per service* is dead on arrival at that
number: 1,000 OS processes, 1,000 cold starts, 1,000 sets of database
connections. §25's arithmetic is the binding constraint:

```text
nodes × workers per node × pool size per worker = potential DB connections
```

PostgreSQL does not tolerate uncontrolled multiplicative growth here. This ADR
must produce an actual connection budget with actual numbers, not a principle.

## 3. Alternatives to evaluate (§18)

1. Fully in-process execution
2. Thread / task isolation within one process
3. Shared worker processes (many services per worker)
4. Dedicated worker processes (one service, or a small set, per worker)
5. Container per service
6. **Hybrid** — shared by default, dedicated on demand (§19)

§19 says to investigate the hybrid model strongly *and not to assume it is the
answer*. That instruction is taken literally: the hybrid model is the current
lean, and it is the model this ADR must try hardest to break.

Container-per-service is almost certainly excluded by the scale target and by
the requirement to run without Kubernetes — but the exclusion must be argued,
not assumed.

## 4. Specialised workers (§20)

One runtime policy is unlikely to fit all workloads. To evaluate:

| Worker class | Distinguishing pressure |
|---|---|
| Feature | DB-bound, high request rate, small responses |
| Vector tile | CPU-bound, cacheable, bursty |
| Map rendering | CPU and memory bound, font/graphics state, long-lived caches |
| Raster | GDAL native code, large memory, I/O bound, **crash risk from malformed input** |
| Geoprocessing | Long-running, must never block request workers (§36) |

The raster case is notable: GDAL is native code processing untrusted files. That
is an argument for process isolation on that class specifically, independent of
the general model. See also ADR-001 §4.

## 5. Questions this ADR must answer

Directly from §80, plus the ones the scale target forces:

- Which services require process isolation, and on what evidence?
- Shared or dedicated workers — and who decides, the administrator or the system?
- How are workers supervised, health-checked, and crash-contained (§21)?
- On what triggers are workers recycled — lifetime, request count, memory, memory
  *growth*, config change, administrator request (§22)?
- How does request routing find a worker able to serve a given service?
- How is backpressure implemented, with bounded queues and predictable failure
  under saturation (§48)?
- How are DB connections budgeted across the whole deployment (§25)?
- What happens on mass restart — thundering herd, staged startup, lazy
  initialisation (§26)?
- What are the formal service states and how are transitions observed (§23)?

## 6. Required modelling — service explosion (§24)

Before choosing, model each candidate at 10 / 100 / 1,000 / 5,000 / 10,000
services and produce concrete estimates for: worker count, OS process count,
resident memory, CPU at idle, DB connections, cold start time, recovery time
after a node restart, cache footprint, and **monitoring cardinality**.

The last one is routinely forgotten and routinely fatal: per-service, per-worker,
per-endpoint metric labels multiply, and a metrics backend can fall over well
before the GIS server does.

5,000 and 10,000 are stress models, not requirements. A model that degrades
gracefully beyond the target is preferred; a model that makes the 100-service
case painful in order to reach 10,000 is rejected under §60.

## 7. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Process isolation cost per worker (memory, cold start) | — | `benchmarks/` — pending |
| Shared workers do not create unacceptable cross-service interference | — | pending |
| Connection budget holds at 1,000 services | — | pending, modelling |

## 8. Decision

Pending.

## 9. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-003 | Most services are idle most of the time, making shared workers viable | `UNVALIDATED` |
| A-007 | Crash containment is required in practice, not just in principle (i.e. crashes actually happen — plugins, GDAL, malformed data) | `UNVALIDATED` |
| A-008 | Administrators will not correctly hand-tune per-service worker settings, so defaults must be good and adaptive | `UNVALIDATED` |

A-003 deserves scrutiny. It is the load-bearing assumption of the shared-worker
model, and if it is wrong the hybrid design changes shape.

## 10. Dependencies

**Depends on:** ADR-001 (concurrency and isolation primitives are
language-specific), ADR-002 (where state lives).

**Depended on by:** ADR-005 (API), ADR-010 (caching — L1 is per-worker, so
worker lifetime determines cache value), ADR-011 (jobs), ADR-012 (clustering),
ADR-006 (plugin isolation).

## 11. Revisit triggers

- Observed idle-service ratio contradicts A-003.
- Cross-service interference appears in shared workers under real load.
- The connection budget is exceeded at the target scale.

## 12. Dissent

To be recorded during the debate round. §6 of the master prompt assigns the
Distributed Systems Architect the job of attacking unnecessary complexity here,
and the Adversarial Reviewer the job of breaking whatever survives.
