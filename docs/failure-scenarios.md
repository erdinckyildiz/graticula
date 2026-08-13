# Failure Scenarios

**Status:** FIRST PASS — every §59 scenario walked. **Ten new gaps found**, three
of them large.
**Required by:** §59, and a Phase 0 exit item after adversarial review F9
**Method:** for each scenario — what happens under the current design, what
should happen, and what is missing.

---

## Summary of what this found

The exercise was worth more than expected. Walking failures surfaced gaps that
reading decisions did not.

| # | Gap | Severity |
|---|---|---|
| **N5** | ~~§21's runtime supervisor was never designed.~~ **CLOSED 2026-08-12** - [runtime-supervisor.md](runtime-supervisor.md) | **Severe** |
| **N6** | **The L3 cache has no size budget and no eviction.** It will fill any disk. | **Severe** |
| **N8** | **TLS is not mentioned anywhere** — not in security, deployment or air-gapped planning. | **Severe** |
| N1 | A service context must be self-sufficient for serving, and that was never stated | High |
| N4 | Concurrency is limited per service, never per data source | High |
| N9 | Rolling upgrade needs expand-and-contract migration discipline | High |
| N3 | No circuit breaker per data source | Medium |
| N7 | Import jobs need staging and atomic commit to survive a partition | Medium |
| N2 | L3 lookup should not require the cache index | Medium |
| N10 | Stale-while-error is a decision we had not made | Medium |

---

## 1. Platform store unavailable

**The scenario F9 named, and the one that constrains the most.**

Everything routes through it: service definitions, routing state, cache index,
job records, roles, styles.

**What happens now:** undefined. Nothing in nine ADRs says.

**What should happen:** *degraded read-only service from already-bound contexts.*
No publishing, no new bindings, no jobs, no administration.

That target is achievable, but only if a constraint holds that we never stated:

> **N1 — a service context must be self-sufficient for serving.**

If a bound context has to consult the platform store to answer a request, the
outage takes everything down rather than freezing it. So a context carries its
definition, its schema, its style reference, **and its effective authorization
data**. [ADR-007](adr/ADR-007-service-runtime.md) §4.3 lists the first three and
not the last.

**Authorization fails closed.** Anything not already resolved in the context is
denied, never allowed. An outage must not become an access-control bypass.

> **N2 — L3 cache lookup should not require the index.**

If the cache path is derivable from the key, a store outage costs cache
*management* but not cache *reads*. If lookup needs an index row, the outage
converts every request into a miss at precisely the moment the source may also
be unreachable. Cheap to design in now, painful later.
[ADR-010](adr/ADR-010-caching.md) does not say which it is.

**Jobs stop safely.** Leases cannot be renewed, so job workers abort at their
next checkpoint — which [ADR-011](adr/ADR-011-job-system.md) §3.9 already
requires for a different reason.

## 2. Data source unavailable

**Contained by design.** Pools are per data source
([ADR-007](adr/ADR-007-service-runtime.md) §4.8), so one dead database does not
consume another's capacity. The service goes `UNAVAILABLE`, its data source goes
`UNREACHABLE`, and the state machine in §4.11 already expresses both.

Two things are missing.

> **N3 — there is no circuit breaker.**

Nothing stops us retrying a dead database on every request, which turns an
outage into a connection storm at the moment recovery is being attempted. A
per-source breaker with backoff is standard and absent.

> **N10 — stale-while-error is a decision we have not made.**

The cache holds tiles for the unreachable source. Serving them is the moment the
cache earns its cost. But [ADR-010](adr/ADR-010-caching.md)'s TTL says they are
expired.

**Proposed: serve stale during a source outage, with an explicit header and a
metric.** Stale data with a warning beats an error, for a read workload. This
must be a stated policy rather than an accident of implementation, and it must
never apply to the *wrong* class of invalidation — a purged entry stays purged
even if the source is down, because that path includes permission changes.

## 3. Data source slow

**Classically worse than unavailable**, and this design does not fully escape
that.

Requests pile up. The per-source pool exhausts, then requests queue, then the
worker's bounded queue fills and admission control rejects
([ADR-007](adr/ADR-007-service-runtime.md) §4.9). Statement timeouts bound the
individual query.

But the affected service's slowness consumes **shared worker capacity** while it
happens, which is debt D-04 in acute form.

> **N4 — concurrency is limited per service, never per data source.**

§49 gives per-service limits. ADR-007 §4.8 gives per-source connection pools.
Neither gives a **per-source concurrency limit on the request path** — and since
many services share one database, twenty slow services on one slow source can
saturate a worker while each individually respects its own limit.

The pool size is an accidental limit, but it is sized for throughput, not for
blast radius. They should be separate numbers.

## 4. Worker crash

Contexts are lost and rebind elsewhere on next request — cheap if A-015 holds.
L1 is lost. In-flight requests die. Pinned contexts survive because §4.12
requires presence in at least K workers.

Then the largest gap in this document:

> **N5 — nothing restarts the worker.**
>
> §21 requires a runtime supervisor handling worker startup and shutdown, health
> monitoring, crash detection, restart, draining, recycling, memory monitoring,
> CPU monitoring, stuck-request detection, concurrency enforcement and resource
> governance.
>
> **It has no ADR, no design and no owner.** ADR-007 names worker *states* and
> assumes something drives them.

This is a whole master-prompt section that fell through. Several ADR-007
decisions — recycling on memory growth, draining, quiescing a data source,
observed escalation — are all supervisor behaviours, written as if the mechanism
existed.

**Disposition: the supervisor needs its own design before Phase 1.** It is not a
detail of ADR-007; ADR-007 depends on it.

Also unresolved: **does the router detect a dead worker before or after routing
to it?** The difference is a clean 503 versus a hung client.

## 5. Job worker partition

[ADR-011](adr/ADR-011-job-system.md) §3.9 handles the core: the lease expires,
another worker reclaims, and the partitioned worker aborts at its next
checkpoint.

> **N7 — but side effects between checkpoints can collide.**

Two workers writing the same tile is benign. Two workers running the same import
is not.

**Rule: jobs with external side effects write to a staging location and commit
atomically.** An import writes to a staging table or a temporary path and swaps
on completion. Not stated in ADR-011, and it is the difference between a
duplicated dataset and a clean retry.

## 6. Disk full

Cache writes fail. Job conversions fail. **Amended 2026-08-12 (Q-70):** the
platform store is PostgreSQL, and since Q-69 it sits beside a mandatory
datastore — very likely on the same disk. So disk-full now takes the platform
store, the hosted data and the cache together, rather than the cache alone.
Scenario 1 applies, and recovery needs disk. **That is a worse failure than the
SQLite version this paragraph originally described**, and the co-location it
assumes is exactly what the single-enterprise-server profile recommends.

> **N6 — the L3 cache has no size budget and no eviction policy.**

[ADR-010](adr/ADR-010-caching.md) discusses layers, keys, invalidation and
seeding, and never mentions **how large the cache is allowed to get**.

A vector tile cache across 1,000 services, seeded to a useful zoom, is unbounded
by nature. Without a budget and eviction it will fill any disk given time, and
"the GIS server filled the disk" is a memorable first incident.

Needs: a configurable size budget, LRU or cost-based eviction, per-service quotas
so one layer cannot consume the whole cache, and **cache writes that fail soft** —
a full cache degrades to no-cache, never to an error.

## 7. Out of memory

The OS kills the worker, so this reduces to scenario 4 — including N5.

Causes worth naming: a streaming result that is not truly streaming, an oversized
single geometry, a context budget set too high. The first is prevented by
[ADR-008](adr/ADR-008-query-engine.md) §4.4, the third by F6's resource-based
budget. **The second has no answer** — a single feature larger than available
memory is a real thing in GIS data and §49's response-size limit refuses the
request rather than handling the feature.

## 8. CPU saturation

Admission control rejects, which is the designed behaviour (§48). The concern is
fairness: without N4's per-source limit and D-04's per-tenant limits, saturation
is distributed by luck rather than by policy.

## 9. Huge, invalid or malformed input

**Malformed raster** is handled — validated at registration in an isolated job
worker ([ADR-009](adr/ADR-009-raster-engine.md) §2.2).

**Invalid geometry is not**, which G4 already found and this scenario confirms
from a different direction: it is not only a correctness question but an
availability one, since a repair attempt on a pathological geometry can consume
unbounded CPU.

**A single oversized geometry** makes a layer partly unusable rather than making
one request fail. Needs a policy: refuse, simplify, or exclude with a warning.

## 10. Certificate expiry

> **N8 — TLS appears nowhere in the architecture.**

Not in [security.md](security.md), not in deployment, not in the air-gapped
planning. Yet we have TLS on: our serving endpoint, connections to data sources,
object storage access, and the COG proxy's outbound fetches
([ADR-009](adr/ADR-009-raster-engine.md) §2.4).

Specific problems:

- **Air-gapped means no ACME.** Certificate rotation is manual, and it must not
  require a restart, because a restart at 2 AM to install a certificate is the
  kind of thing that gets a product removed.
- **Data source certificate expiry** looks like scenario 2 with a confusing
  error, and the diagnosis must say so plainly.
- **Outbound trust for the COG proxy** is a configuration surface nobody has
  designed, and it interacts with SSRF (security.md §6).

## 11. Configuration corruption

The export/import format ([ADR-002](adr/ADR-002-primary-data-architecture.md)
§4.3) is the recovery path, which is one of the better-covered scenarios. Q-48's
backup-consistency question is the remaining hole: a platform store restored
against a mismatched datastore produces services pointing at tables that do not
match their definitions.

## 12. Partial upgrade and rolling deployment failure

Mixed versions serve simultaneously. Three collisions:

- **Plan identity may differ between versions**, so every cache entry silently
  becomes unreachable mid-deployment (Q-45).
- **A schema migration applied by the new version is visible to the old one**,
  which may not understand it.
- **The export/import format may differ.**

> **N9 — migrations must be expand-and-contract.**

A migration may only add in the version that introduces it; removal happens a
version later, once no old instance is running. Standard discipline, unstated,
and impossible to retrofit after the first migration that breaks it.

This is the third independent argument that **upgrade needs an ADR before
Phase 1** — G8 said so, Q-45 implied it, and this scenario confirms it.

---

## Consequences

**Three severe gaps, none of which is a refinement of an existing decision:**

1. **The runtime supervisor (N5)** — a whole §21 section with no design. ADR-007
   depends on behaviour nobody has assigned to a component.
2. **Cache size and eviction (N6)** — ADR-010 designed everything about the cache
   except how big it is allowed to be.
3. **TLS (N8)** — absent from the architecture entirely, and it interacts with
   air-gapped operation, the COG proxy and SSRF.

**Two constraints that must be written into existing decisions:**

- **N1** — a service context is self-sufficient for serving, and authorization
  fails closed.
- **N9** — migrations are expand-and-contract from the first one.

**Three additions to existing designs:** the per-source circuit breaker (N3), the
per-source concurrency limit (N4), and staging with atomic commit for jobs with
side effects (N7).

**Two decisions we had not realised we needed:** L3 lookup without the index
(N2), and stale-while-error (N10).

## Not yet walked

Network partition between nodes, since clustering is deferred (ADR-012), though
N9 and the invalidation window already touch it. Broken plugin, since ADR-006
admits none. Clock skew, which affects leases and TTLs and was not on §59's list
but should have been.
