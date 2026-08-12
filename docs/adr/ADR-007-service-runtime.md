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

**Updated 2026-08-12 after the ArcSOC investigation**
([research/arcgis-som-soc.md](../research/arcgis-som-soc.md)):

- **Option 4 with a warm minimum is excluded by arithmetic, not preference.**
  `VERIFY` an ArcSOC process costs roughly 100–200 MB; at 1,000 services with a
  minimum of one instance each, that is ~150 GB of resident memory to have
  services merely *available*. Esri added shared instances at 10.7 for exactly
  this reason. Our §24 explosion test has this answer in advance.
- **Option 6 gains real support**: the incumbent converged on shared-plus-
  dedicated under production pressure. §19 still forbids assuming it is our
  answer, so this raises the standard of proof rather than settling the matter.
- **The evaluation axis is wrong.** "Shared or dedicated" is a symptom. Esri's
  own limits on shared instances — map and image services only, geoprocessing
  excluded, `VERIFY` ~50 cached service contexts per instance — reveal the real
  axis:

  > How much per-service state must a worker hold, how expensive is it to bind
  > and unbind, and does the workload tolerate a neighbour?

  That question has a different answer per workload class, which is the §20
  specialised-worker hypothesis with evidence behind it. §4 below is the section
  that matters.
- **No distinguished central manager process.** Esri removed the SOM at 10.1 and
  named robustness, failure reduction and simpler provisioning and recovery as
  the reasons. Placement and routing state must be recoverable without a special
  node — a constraint on the single-node design too, not only on ADR-012.
- **Per-service min/max instances is rejected as the primary control surface.**
  It does not survive 1,000 services or a GIS-administrator user. Policy plus
  observation, with per-service override as an escape hatch.
- **Session-pinned workers (Esri's non-pooled services) are not reproduced.**
  Stateful editing belongs to database transactions and optimistic concurrency
  (§28), not to process affinity. That problem has dissolved rather than moved.

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
- **How does request routing find a worker able to serve a given service —
  and does it prefer one already warm for it?** Elevated after
  [research/runtime-models-compared.md](../research/runtime-models-compared.md)
  §3. Every prior system fragments warm state across workers and then routes
  blindly; QGIS Server documents the resulting cache misses explicitly. Affinity
  routing plus a bounded per-worker service-context budget is the most promising
  specific idea from the research phase — and it must be prototyped, not
  believed. It trades load balance against cache locality, and must degrade to
  plain balancing under skew.
- How is backpressure implemented, with bounded queues and predictable failure
  under saturation (§48)?
- How are DB connections budgeted across the whole deployment (§25)?
- What happens on mass restart — thundering herd, staged startup, lazy
  initialisation (§26)?
- What are the formal service states and how are transitions observed (§23)?

## 5a. Blocking precondition — dependency thread safety

`VERIFY` QGIS Server's entire process architecture follows from one fact: its
classes are not thread safe, so multiprocessing is mandatory. A Tier 2
dependency dictated the runtime model.

**Thread-safety guarantees for GDAL, GEOS and PROJ must be established before
this ADR is decided**, precisely and per API rather than in general terms. This
is a blocking precondition, not a footnote — discovering it during
implementation would invalidate the decision.

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
| Process-per-service with a warm minimum does not reach 1,000 services | `VERIFY` ~100–200 MB per ArcSOC process; Esri introduced shared instances at 10.7 citing memory | [research/arcgis-som-soc.md](../research/arcgis-som-soc.md) §3.3 |
| A central manager process is a robustness and recovery liability | Esri removed SOM/SOC at 10.1, citing exactly this | [research/arcgis-som-soc.md](../research/arcgis-som-soc.md) §3.1 |
| Sharing fails for workloads holding heavy or exclusive state | Geoprocessing is excluded from ArcGIS shared instance pools | [research/arcgis-som-soc.md](../research/arcgis-som-soc.md) §3.4 |
| Fragmented warm state plus blind routing causes real cache misses | QGIS Server documents per-process caches with randomly assigned requests | [research/runtime-models-compared.md](../research/runtime-models-compared.md) §2.2, §3 |
| Heavy isolation is not universally necessary | GeoServer serves all services from one JVM heap, thread per request, and is widely deployed | [research/runtime-models-compared.md](../research/runtime-models-compared.md) §2.3, §4 |
| Warm per-service state is smaller than process-per-service models assume | GeoServer caches store connections, feature type definitions, external graphics, fonts and CRS definitions — not data | [research/runtime-models-compared.md](../research/runtime-models-compared.md) §2.3 |
| Splitting by protocol is the wrong decomposition axis | GeoServer Cloud splits per OWS service and needs a message bus to repair catalog consistency | [research/runtime-models-compared.md](../research/runtime-models-compared.md) §2.4 |
| Process isolation cost per worker (memory, cold start) — *our* numbers | — | `benchmarks/` — pending |
| Shared workers do not create unacceptable cross-service interference | — | pending |
| Connection budget holds at 1,000 services | — | pending, modelling |

## 8. Decision

Pending.

## 9. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-003 | Most services are idle most of the time, making shared workers viable | `UNVALIDATED` |
| A-007 | Crash containment is required in practice, not just in principle (i.e. crashes actually happen — plugins, GDAL, malformed data) | `CONTESTED` — GeoServer runs every service in one JVM with no isolation and is widely deployed successfully, which is evidence *against* for managed-code paths; ArcGIS and QGIS Server run large native stacks and isolate, which is evidence *for* on native paths. Resolution is per-path, not global. |
| A-008 | Administrators will not correctly hand-tune per-service worker settings, so defaults must be good and adaptive | `SUPPORTED` — ArcGIS Server's own guidance asks administrators to "pare down the number of running service instances", a per-service manual task that does not scale to 1,000 services |

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
