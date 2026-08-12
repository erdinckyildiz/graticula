# ADR-010 — Caching

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-12 |

---

## 1. Context

Caching became more important than it was when this ADR was stubbed.

`ST_AsMVT` exists only in PostGIS, so tiles from SQL Server and Oracle are built
in-process. **The cache is the mechanism that absorbs that difference**, not the
encoder ([research/hosted-datastore-and-tiles.md](../research/hosted-datastore-and-tiles.md)
§3). An expensive Oracle tile is acceptable if it is built once at seed time
rather than per request.

It also got simpler. Vector-first removed raster tile caches and pyramids —
historically the largest and worst-behaved cache in a GIS server.

And it acquired a problem it cannot fully solve. **Feature writes can bypass us
entirely**: anyone with database credentials can change data we have cached, and
we will not know ([ADR-005](ADR-005-api-architecture.md) §3.8).

## 2. What is cached — and the restraint that matters

| Content | Cached? | Why |
|---|---|---|
| **Vector tiles** | **Yes, primarily** | Bounded, enumerable key space: `z/x/y` per layer. Expensive to build. This is the real cache. |
| Catalog and metadata | Yes | Small, hot, cheap to hold. |
| Glyph and sprite assets | Yes, trivially | Static. Effectively immutable content. |
| Capability reports | Yes | Derived from provider negotiation; changes rarely. |
| **Feature query responses** | **Only opportunistically, short TTL** | See below. |

**The restraint:** feature query responses have an unbounded key space. Arbitrary
CQL2 filters, arbitrary bbox, arbitrary field selection, arbitrary sort. Caching
them thoroughly means caching almost nothing twice while consuming everything.

`VERIFY` ArcGIS caches query responses in a dedicated object store
([research/arcgis-datastore-model.md](../research/arcgis-datastore-model.md) §3),
so it is not a foolish idea — but it is a different bet from ours, and ours is
that tiles are where the reuse is.

So: feature responses get a short-TTL opportunistic cache for the *identical
repeated request* case, and nothing more ambitious until evidence says otherwise.

**Negative caching is included.** Empty tiles are extremely common in sparse
layers and regenerating them is pure waste. An empty tile is cached as a marker
rather than as bytes.

## 3. Layers

| Layer | Where | Mandatory? |
|---|---|---|
| **L1** | Worker memory, attached to the **service context** | Yes |
| **L2** | Distributed cache (Redis or similar) | **No, and never** (§44) |
| **L3** | Filesystem or object store | Yes |

### L1 is context-scoped, not process-scoped

[ADR-007](ADR-007-service-runtime.md) §4.3 and §4.4 settle this: contexts bind
lazily and evict LRU under a bounded budget, so **L1 lifetime is context
lifetime** — shorter and far more predictable than process lifetime, and
unaffected by worker recycling.

**L1 and routing are the same data structure.** The router knows which worker
holds which context; that is the same knowledge as knowing where a warm cache
lives. This is what makes affinity routing worth building — it is a cache
decision as much as a routing decision.

### L2 stays optional, permanently

§44 requires that Redis not be mandatory for small installations. Stronger than
that: **L2 must never become load-bearing.** A deployment that works with L2 and
fails without it has made an optional component mandatory by accident.

L2's only legitimate job is sharing L3 index state and invalidation signals
across nodes, and even that must have a working degraded mode (§7).

### L3 has a size budget — added after the failure scenario pass

**Found by [failure-scenarios.md](../failure-scenarios.md) N6, and it was a real
omission:** this ADR designed layers, keys, invalidation and seeding, and never
said **how large the cache is allowed to get.**

A vector tile cache across 1,000 services, seeded to a useful zoom, is unbounded
by nature. Without a budget it fills any disk given time, and "the GIS server
filled the disk" is a memorable first incident.

Required:

- a **configurable total size budget**, with LRU or cost-based eviction;
- **per-service quotas**, so one layer cannot consume the whole cache;
- **cache writes fail soft** — a full cache degrades to no-cache, never to an
  error.

The last one matters most. A cache is an optimisation, and an optimisation that
can fail a request is a liability.

### L3 lookup should not need the index (N2)

If the storage path is derivable from the cache key, a platform store outage
costs cache *management* but not cache *reads*. If lookup requires an index row,
that outage turns every request into a miss at exactly the moment the source may
also be unreachable. Cheap to design in now; painful later.

### L3 is the tile cache

Filesystem by default, object store where available. `VERIFY` PMTiles and
MBTiles as container formats — both are designed for exactly this and would save
us inventing a layout.

## 4. Cache keys

The key is **the compiled plan's identity, plus the layer's schema
fingerprint** — not the request URL.

**Completed 2026-08-12** ([security.md](../security.md) §3), after G5 found the
key had no authorization context and a cache hit could therefore cross an
authorization boundary.

The naive fix — put the principal in the key — is correct and catastrophic,
because every user then gets their own tile. The resolution splits by kind:

- **Uniform authorization** (layer visibility, allow or deny) is checked
  **before the cache lookup**. All authorized users share one entry, which is
  the overwhelmingly common case for tiles.
- **Varying authorization** (row filters, field visibility) becomes part of the
  key as a **grant fingerprint** — a hash of the effective authorization that
  affects the output, not the principal.

The governing rule: *a cache entry may be shared by any two requests that would
produce byte-identical output under their own authorization; if that cannot be
proven from the key, it is not shared.*

This also makes permission-change invalidation structural rather than a sweep —
changing the effective grant changes the key, so old entries become unreachable.

Two consequences, both good:

- Semantically identical requests that differ in URL form share one entry.
- **Schema drift invalidates structurally.** When the fingerprint changes, every
  key derived from it is unreachable. No sweep to run, no entries to hunt down.
  The old bytes become garbage to be collected rather than stale data to be
  found.

For tiles the plan identity is small and stable: layer, `z/x/y`, and the
generalisation parameters for that zoom.

## 5. Invalidation — the hard half

### 5.1 Wrong is not the same as stale

| Situation | Severity | Behaviour |
|---|---|---|
| Data changed | **Stale** | May be served while it refreshes |
| Field added | **Stale** | May be served |
| Field removed or retyped | **Wrong** | Must be purged before it can be served again |
| Style or generalisation parameters changed | **Wrong** | Purge |
| Layer unpublished or permissions changed | **Wrong** | Purge, and this one is a security matter |

The distinction is not cosmetic. Serving stale data is a freshness compromise;
serving wrong data is a correctness failure, and in the permissions case a
disclosure. The cache must know which it is holding.

### 5.1a Stale-while-error — serve stale during a source outage

**A decision we had not made** ([failure-scenarios.md](../failure-scenarios.md)
N10). When a data source is unreachable, the cache holds tiles for it and TTL
says they are expired.

**Serve them, with an explicit header and a metric.** This is the moment the
cache earns its cost, and stale data with a warning beats an error for a read
workload.

**With one exception that is not negotiable:** this never applies to the
*wrong* class in §5.1. A purged entry stays purged even if the source is down,
because that path includes permission changes, and serving a purged tile during
an outage would turn an availability event into a disclosure.

### 5.2 The problem we cannot fully solve

**We do not see all writes.** Feature edits can arrive through our API, where we
know, or directly against the database, where we do not.

Honest position: **cache coherence cannot be guaranteed for data we do not
control.** Everything below is mitigation, not a guarantee, and it should be
documented that way rather than implied away.

Four mechanisms, in order of precision:

1. **Explicit invalidation.** Our own writes invalidate exactly. So does the
   admin API, and so does the QGIS extension after a direct edit
   ([data-model.md](../data-model.md) §5). This is the fast, exact path and it
   is why the extension has an architectural role rather than being a
   convenience.
2. **Change detection where the provider allows it.** Change tracking, a
   modified-timestamp column, or triggers — different on every engine and
   requiring permissions we may not have. Available on the datastore and on
   registered sources where granted.
3. **Schema-drift polling.** Already required for service refresh (A-023), and
   it catches structural change for free.
4. **TTL.** The floor. Crude, always available, always correct in the limited
   sense that staleness is bounded.

**TTL is the only mechanism that works everywhere**, so it is the default and
the others are accelerations. A design that assumes 1 or 2 is available is a
design that breaks on the read-only Oracle we said we would support.

### 5.3 Volatility is declared per layer

TTL cannot be one global number. A cadastral reference layer that changes twice
a year and an incident layer that changes every minute need opposite answers.

So **volatility is a per-layer property**, set by the administrator at
registration and adjustable later. It drives the default TTL, whether seeding is
worthwhile, and how aggressively negative results are cached.

This is a per-service knob, which A-008 warns about — but unlike worker tuning,
**the administrator actually knows this answer** and nobody else does. It is
domain knowledge, not performance tuning, and asking for it is reasonable.

Recorded as A-028.

## 6. Seeding

With vector-first, tile seeding is now the whole of the seeding problem rather
than part of it.

- Seeding is a **job** ([ADR-011](ADR-011-job-system.md)), running on job
  workers, not stealing request capacity.
- Scoped by layer, zoom range and area of interest. Seeding a global layer to
  z18 is not a plan, it is an accident.
- **Resumable and cancellable.** A seed across 1,000 services is a long-running
  operation and it will be interrupted.
- **Rate-limited against the source.** Seeding must not become the thing that
  overloads the customer's Oracle. This is the same courtesy as the connection
  discipline in [ADR-007](ADR-007-service-runtime.md) §4.8.
- Progress and cost are visible: tiles built, tiles remaining, estimated time.

**The honest question this ADR cannot yet answer:** how long does seeding a
realistic estate take, per provider? That is `benchmarks/tile-seeding/` and it
determines whether A-020 — that seeding absorbs the provider gap — is true or
wishful.

## 7. Multi-node

Inherited from [ADR-002](ADR-002-primary-data-architecture.md) §5 and owed to
ADR-012.

- **The cache index is shared** — it lives in the platform store.
- **The cache bytes are node-local** unless placed on shared storage.

So a multi-node deployment either shares L3 storage, replicates it, or accepts
that a tile cached on one node is a miss on another. **Accepting the miss is a
legitimate choice** and should be the default: a miss costs a rebuild, not an
error, and shared storage is a dependency we should not require.

Invalidation must reach every node. Without portable `LISTEN`/`NOTIFY`
([ADR-002](ADR-002-primary-data-architecture.md) §4a.4), that is a polled
invalidation sequence in the platform store — the same polling machinery as
schema drift. **The invalidation delay is therefore bounded by the poll
interval, and that bound must be documented**, because for the "wrong" class in
§5.1 it is a window during which incorrect data is served.

That window is the strongest argument for L2 as an optional accelerator: with
it, invalidation propagates immediately. Without it, correctness is preserved but
the window is real.

## 8. Counterarguments

- **Not caching feature responses properly may be a mistake.** ArcGIS built a
  whole store for it. If real workloads turn out to be dominated by repeated
  identical feature queries rather than tiles, §2's restraint is wrong.
- **Per-layer volatility will be set once and never revisited.** Declared
  volatility drifts from actual volatility, and then TTLs are wrong in both
  directions. Detecting the drift is possible and not designed here.
- **The invalidation window in §7 is a real correctness gap** for the "wrong"
  class, and the mitigation is an optional component. That is uncomfortable and
  should stay uncomfortable rather than being argued away.
- **Cache-key-by-plan-identity assumes plan compilation is deterministic and
  stable.** If the planner's output changes across versions, every key changes,
  and a deployment silently rebuilds its entire cache. That needs to be a
  deliberate, visible event.

## 9. Consequences

**Positive.** The provider performance gap has a mechanism to absorb it. Schema
drift invalidates structurally rather than by sweep. L1 and routing share one
structure. Redis is genuinely optional. Raster caching is simply gone.

**Negative.** Coherence is best-effort for registered data, and that must be
documented rather than implied away. TTL as the floor means bounded staleness is
normal. Seeding is a real subsystem with progress, resumability and rate
limiting. The multi-node invalidation window exists.

## 10. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-020 | Seeding absorbs the provider performance gap | `UNVALIDATED` — `benchmarks/tile-seeding/` |
| A-023 | Schema fingerprint polling is cheap enough | `UNVALIDATED` |
| A-028 | Administrators can and will declare layer volatility usefully | `UNVALIDATED` |
| A-029 | Tiles, not feature responses, are where cache reuse actually is | `UNVALIDATED` — the restraint in §2 rests on it |

## 11. Dependencies

**Depends on:** [ADR-007](ADR-007-service-runtime.md) (context lifetime,
routing), [ADR-008](ADR-008-query-engine.md) (plan identity as key),
[ADR-002](ADR-002-primary-data-architecture.md) (index location, state
inventory).

**Depended on by:** [ADR-011](ADR-011-job-system.md) (seeding is a job),
ADR-012 (bytes are node-local), tile pipeline, admin API (§39).

## 12. Conditions

1. **TTL must work with no other mechanism available.** The read-only Oracle
   case is the test: if the design only works with change detection, it does not
   work.
2. **The invalidation delay must be documented as a number**, not described as
   "eventual". An operator needs to know the window.
3. **Seeding must be rate-limited against the source from the first version.**
   Retrofitting politeness after someone's production database has been
   saturated is too late.
4. **A-029 must be checked against real traffic** before the §2 restraint is
   treated as settled.

## 13. Revisit triggers

- Repeated identical feature queries dominate real traffic (invalidates A-029).
- Seeding proves too slow to absorb the provider gap (invalidates A-020).
- The multi-node invalidation window causes a real incident.
- L2 starts being required rather than helpful — that is a design failure and
  should be treated as one.

## 14. Dissent

**Best-effort coherence is a weaker promise than a GIS administrator will
assume.** Told that data is cached, an operator reasonably expects the cache to
follow the data. Ours follows the data we see, and bounds the rest with a timer.

That is the honest consequence of supporting registered sources we do not
control, and the alternative — requiring all writes through us — is not
enforceable. But it should be stated plainly in operator documentation rather
than buried, because the first time someone edits a table directly and sees old
tiles for ten minutes, they will consider it a bug. It is a documented
limitation, and the difference between those two things is entirely whether we
said so first.
