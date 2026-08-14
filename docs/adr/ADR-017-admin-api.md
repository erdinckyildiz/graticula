# ADR-017 — Admin API Shape

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` — the shape is decided, the surface is not |
| **Decided** | 2026-08-13 |
| **Answers** | §39 shape · blocker **B6** · writes F5's four 2 AM scenarios |

---

## 1. Context

**The primary user of this product is the GIS administrator** (Q-06a), and until
now we had designed almost nothing they touch. The
[exit plan](../phase-0-exit-plan.md) found this while auditing its own blocker
list: the walking skeleton is defined as *published through the admin API*, and
the admin API was recorded as not started.

This ADR decides **shape, not surface**. Enough that publish, list and inspect
have somewhere to live, that ADR-015's user, role, session and key operations
have a home, and that the skeleton can be built. The full endpoint catalogue is
Phase 1 work.

---

## 2. Method — designed against the 2 AM scenarios, not as a CRUD list

Adversarial review **F5** asked for three scenarios written end to end — stale
tiles, a slow service, a failed registration — and set a sharp test:

> What does the administrator see, from which endpoint, composed from what? If
> it cannot be written, the observability model is missing rather than deferred.

**A CRUD list derived from our nouns would pass no such test.** So the surface is
derived from the scenarios instead, and the nouns fall out of them. ADR-014
supplied a fourth scenario that is more predictable than the other three, so
four are walked.

---

## 3. The four scenarios, walked

### 3.1 "The map is showing old data"

**What the administrator knows:** a user complained. Nothing else.

| Step | Endpoint | Answers |
|---|---|---|
| 1 | `GET /admin/layers/{id}` | Is this layer hosted or registered? Which data source? |
| 2 | `GET /admin/layers/{id}/cache` | When was each zoom level last generated? What is the invalidation policy — TTL, event, manual? |
| 3 | `GET /admin/datasources/{id}/drift` | Has the source schema or content changed since we last looked? (A-023's fingerprint) |
| 4 | `POST /admin/layers/{id}/cache/invalidate` | Fix it, scoped to a bbox or zoom range rather than all-or-nothing |

**What this forces into existence:** the cache index must record *generation
time per tile set*, not merely hold bytes, and ADR-010's coherence policy must be
**readable per layer** rather than living in configuration. A best-effort
coherence guarantee that an administrator cannot inspect is indistinguishable
from a bug.

### 3.2 "This service is slow"

| Step | Endpoint | Answers |
|---|---|---|
| 1 | `GET /admin/layers/{id}/health` | Recent latency distribution, error rate, request rate |
| 2 | `GET /admin/layers/{id}/capability` | Is a filter being **refused** and the client retrying? Is it falling back? (ADR-008 §2) |
| 3 | `GET /admin/workers` | Which worker holds this service's context? Is it warm or being evicted repeatedly? |
| 4 | `GET /admin/workers/{id}` | **Allocation rate and GC pause share** — A-037 measured 80.9% GC pause at 18% CPU, so a CPU graph alone will show an idle worker and explain nothing |
| 5 | `GET /admin/datasources/{id}/pool` | Pool saturation, wait time, and whether attachment streaming is holding connections (ADR-013 §4b) |
| 6 | `POST /admin/layers/{id}/pin` | Pin the context if thrash is the cause (ADR-007 §4.12) |

**What this forces into existence:** worker introspection must expose
**allocation and GC pause**, not just CPU and memory. This is the single most
important consequence in this ADR, and it comes directly from run 3 — the
default observability stack would have shown a healthy worker throughout.

### 3.3 "Registration failed"

| Step | Endpoint | Answers |
|---|---|---|
| 1 | `GET /admin/jobs/{id}` | Registration is an interactive-class job (ADR-011). Status, phase, and the failing step |
| 2 | `GET /admin/jobs/{id}/log` | The provider's actual error, not a wrapped one |
| 3 | `POST /admin/datasources/test` | Re-run connectivity, credentials, TLS and privilege checks **without creating anything** |
| 4 | `GET /admin/datasources/{id}/capability` | What we *could* do with this source, given granted rights — the honest list, before publishing anything |

**What this forces into existence:** a **dry-run test endpoint that creates no
state**, and a distinction between *cannot connect*, *connected but insufficient
privilege*, and *connected, privileged, but the geometry is unusable* — which
[geometry-crs-policy.md](../geometry-crs-policy.md) can already produce. One
generic failure covering all three is what makes registration hostile.

### 3.4 "Everything stopped at 03:14"

The scenario [ADR-014](ADR-014-tls-and-certificates.md) §2c added, and the only
one with a known date in advance.

| Step | Endpoint | Answers |
|---|---|---|
| 1 | `GET /admin/health` | **Certificate expired at 03:14** — stated, not inferred from a TLS handshake error |
| 2 | `GET /admin/certificates` | Every certificate we hold, with expiry and days remaining: serving, data-source clients, trust anchors |
| 3 | `PUT /admin/certificates/serving` | Install the replacement — effective on the next handshake, **no restart** (ADR-014 §2b, A-044) |

**What this forces into existence:** expiry is surfaced **before** it bites,
30/7/1 days, and the supervisor owns it. An outage with a known date that
surprises the operator is a design failure, not bad luck.

---

## 4. The resource model that falls out

Not a wish list — every noun below is required by §3 or by an accepted ADR.

| Group | Resources | Source |
|---|---|---|
| **Content** | layers, hosted items, styles, capability reports | Q-17, Q-25, ADR-008 §2 |
| **Sources** | datasources, their capability, drift, pool state, test | ADR-002, A-023, ADR-007 §5b |
| **Runtime** | workers, service contexts, pins, drain | ADR-007 |
| **Work** | jobs, logs, cancel, retry, seeding | ADR-011, ADR-010 §6 |
| **Cache** | per-layer state, invalidate, size and budget | ADR-010, N6 |
| **Identity** | users, roles, grants, sessions, API keys | ADR-015 |
| **Transport** | certificates, trust anchors | ADR-014 |
| **Lifecycle** | version status, migrate, contract, backup | ADR-016 |
| **Tools** | Python geoprocessing tools, and who may publish them | Q-17b, ADR-015 §7 |

---

## 5. Shape decisions

### 5a. A separate, separately-versioned prefix

`/admin/…`, distinct from the data APIs and versioned independently, because the
admin surface will change far faster than OGC API Features does.

**ADR-015 already forbids compatibility tokens here.** An ArcGIS token arrives in
a query string and leaks into logs and `Referer` headers; a credential that leaky
must not reach the surface that can create administrators.

### 5b. Long operations return a job, uniformly

Publishing, registration, seeding, migration and backup are all long-running.
Every one returns **`202` and a job identifier**, and `/admin/jobs/{id}` is the
single place progress is observed — the same resource §3.3 already needs.

One async pattern, not one per operation. A surface with three different ways to
wait is a surface nobody automates.

### 5c. Runtime state changes go through the API; only bootstrap lives in config

This principle was arrived at three times independently and is worth stating
once: certificates (ADR-014 §2b), migrations (ADR-016 §4), and pinning
(ADR-007 §4.12) are all API operations, not file edits.

**Config holds what is needed to start**: listen address, platform store
connection, bootstrap token. Everything else is state, is auditable, and survives
a container replacement ([ADR-016](ADR-016-packaging-deployment-upgrade.md) §3).

### 5d. Every mutating call is audited

Principal, source address, resource, before and after. ADR-015 supplies the
subject. Without this, the ownership model in security.md §2.0 has no way to
answer *who shared this publicly*.

### 5e. The capability report is a first-class resource, not a diagnostic

ADR-008 §2 promises *never degrade silently*, and §3.2 and §3.3 both reach for it
before anything else. **A promise a client cannot query is not a promise**, so it
is a resource with a stable shape — per layer and per data source — rather than
prose in an error message.

---

## 6. The degraded-mode problem, which is new

**The platform store lives in the datastore** (Q-69, Q-70). So if the datastore
is down, the admin API loses users, sessions, layer definitions and job
records — **at exactly the moment an administrator most needs to look.**

The runtime supervisor already survives independently and holds a governing
principle: *a management-plane failure must not become a data-plane failure.*
**This is that principle inverted**: a data-plane failure must not blind the
management plane.

So a **minimal admin surface is served by the supervisor and works with no
platform store at all**:

| Endpoint | Works without the platform store because |
|---|---|
| `GET /admin/health` | The supervisor observes workers directly |
| `GET /admin/version` | Read from the image |
| `GET /admin/certificates` | Held on the secret volume, not in the database |
| `GET /admin/workers` | The supervisor's own knowledge |
| Bootstrap authentication | A break-glass path, audited to disk, valid only while the store is unreachable |

Everything else returns a clear *the platform store is unavailable, and here is
what is known* rather than a generic 500. **A 500 from the admin API during an
outage is the worst possible response**, because it removes the only tool the
administrator has left.

## 7. Deliberately not decided here

- The full endpoint catalogue, request and response schemas — Phase 1.
- Whether there is a web console. `honua-console` exists as a separate product;
  ours is an API-first decision and a UI, if any, is a client of it.
- **D-04 multi-tenant resource isolation.** Still unaddressed, and §3.2's
  worker introspection will make one tenant's impact on another *visible* without
  making it *controllable*.

## 8. Consequences

- **F5's exit criterion is met** — four scenarios written end to end, and each
  produced a requirement the CRUD-first version would have missed.
- **Worker introspection must expose allocation and GC pause** (§3.2). The
  largest single consequence, straight from A-037.
- The cache index must record generation time per tile set (§3.1).
- A no-state dry-run test endpoint is required for registration (§3.3).
- The supervisor gains a minimal HTTP surface (§6).
- ADR-010's coherence policy must be readable per layer, not configuration-only.

## 9. Conditions

1. **The degraded surface is tested by stopping the datastore**, and the API must
   answer usefully rather than 500.
   **DISCHARGED 2026-08-14, and it failed the first time in three ways.** The
   test was run by `docker stop` on the PostGIS container. What it found:
   *(a)* **`/healthz/live` returned 503** — a liveness probe that depends on the
   database tells an orchestrator to kill the container during a database
   outage, turning an outage into a restart loop and destroying the only process
   still able to answer anything. Liveness is now ahead of all middleware and
   touches nothing; readiness is a separate endpoint that does depend on the
   store, because the two must be allowed to disagree.
   *(b)* **`/admin/health` never ran at all.** The authentication middleware
   queries the store for every request, including anonymous ones, so it failed
   before any endpoint was reached — taking down precisely the surface that
   exists to be reachable when the store is not. It now yields anonymous with no
   grants on an unreachable store, which fails *closed*: an unvalidatable token
   is not honoured and anonymous holds no privilege.
   *(c)* **The 503 blamed the wrong database**, saying *the layer's data source,
   not the server* while the platform store was what had failed. It now names
   the two endpoints that distinguish them.
   Verified after the fixes: liveness 200, readiness 503 with a reason,
   `/admin/health` reporting `degraded` and why.
2. **Every §3 scenario is walkable against the built skeleton**, in order, with
   no step requiring a log file. A step that needs `docker logs` is a missing
   endpoint.
3. **The break-glass path in §6 cannot be used while the platform store is
   reachable**, tested — otherwise it is an authentication bypass.
   **NOT YET APPLICABLE: the break-glass path is not built** (A-051), so there
   is nothing to bypass. The consequence, found while discharging condition 1,
   is that `/admin/health` must be anonymous — sessions live in the store, so
   during the outage it exists for, nobody can authenticate. That made its error
   detail a disclosure to anyone who could reach the port, and it named the
   store's host and port until it was noticed. The detail is now shown only to a
   caller holding `admin:manageServer`, which during an outage is nobody.
   **That is a real reduction in the endpoint's usefulness, and it is what the
   break-glass path is for.**

## 10. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-050 | Per-worker allocation rate and GC pause can be exposed cheaply enough to sample continuously | `UNVALIDATED` — §3.2 depends on it. The benchmark harness read these per request via `GC.GetTotalAllocatedBytes` and collection counts, so the mechanism exists; whether it is affordable as a always-on metric rather than a benchmark instrument is untested |
| A-051 | A supervisor-served admin surface can authenticate without the platform store, without becoming a bypass | `UNVALIDATED` — §6's break-glass path. This is the classic shape of an authentication bypass, and the mitigation — only valid while the store is unreachable — is a condition someone must implement correctly |

## 11. Dissent

**Against designing from scenarios.** It risks a surface that answers four
questions well and the fifth not at all, where a resource-complete CRUD API would
be uniform. The counter is that F5's test is the right one and a uniform API that
cannot answer *why is this slow* is uniformly useless. §4's resource model is
resource-shaped; only its *priorities* came from the scenarios.

**Against the break-glass path (§6).** It is an authentication bypass with a
condition attached, and conditions get relaxed. The alternative — an admin API
that dies with the datastore — is worse, because Q-69 made that datastore
mandatory and therefore made this failure mode universal rather than unusual.
