# ADR-008 — Query Engine

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM` |
| **Decided** | 2026-08-12 |

---

## 1. Context

Features first ([product-context.md](../product-context.md)), so this is the
day-one engine. It must serve:

- three first-class spatial dialects — PostGIS, SQL Server Spatial, Oracle
  Spatial — of unequal capability;
- file providers with far less capability again: GeoPackage, FlatGeobuf,
  Shapefile, GeoParquet;
- both hosted and registered layers ([data-model.md](../data-model.md));
- the vector tile path, since `ST_AsMVT` exists only in PostGIS;
- millions of features, streamed, never materialised (§47).

It must never concatenate untrusted input into SQL (§29), and it must push
computation down *intelligently* rather than blindly (§30).

**Correction, 2026-08-12.** An earlier version of this ADR said editing was out
of scope. That was a wrong inference — the owner's statement about editing
happening in the database concerned **table structure**, not feature data.

**Feature data editing is in scope** (Q-42). This ADR is written around the read
path, which is right for the day-one workload, but the write path belongs to the
same engine and brings back what the earlier version dismissed:
provider-dependent transaction semantics, isolation levels, locking behaviour
and what a conflict looks like across PostGIS, SQL Server and Oracle.

Those differences produce provider-dependent *bugs* rather than
provider-dependent features, so they must be designed against explicitly rather
than discovered. **The write path is a gap in this ADR and needs its own pass**
before implementation, covering transactions, batch edits, and the concurrency
rule in [ADR-005](ADR-005-api-architecture.md) §3.8 — that optimistic
concurrency is built on database-maintained state, never on our own record.

## 2. The organising principle

> **Never degrade silently.**

A query engine spanning providers of unequal capability has three possible
behaviours when a provider cannot do something:

1. **Hide it** — pull the data back and do the work in-process, transparently.
2. **Refuse it** — fail with a clear explanation.
3. **Announce it** — declare per-layer what is supported, and refuse the rest.

Option 1 is how these systems earn a reputation for being unpredictably slow. A
filter that pushes down against PostGIS and silently drags a million rows across
the network against Oracle is the same API with a thousand-fold difference in
cost, and nothing tells the operator which they got.

**We choose 3, with a narrow, explicitly bounded amount of 1.** This is also the
answer to Q-31, which asked whether we expose provider capability differences:
we expose them, by publishing a capability report per layer and by failing
loudly rather than degrading quietly.

### 2a. Amended after adversarial review (F3) — the default is per surface

The principle above is a good greenfield policy and **a hostile migration
policy**, and displacing GeoServer and ArcGIS Server is a confirmed goal.

The capability report is no help to a migration. The client was written three
years ago against a server that answered the query. It migrates, it breaks, and
the error explains — correctly and uselessly — that the report was available.

So the principle survives and the default changes:

| Surface | Default | Reasoning |
|---|---|---|
| **Native API** | **Refuse** | The client is being written now. Honest limits produce better software. |
| **Compatibility layer** | **Best effort** | The client cannot be changed. Slow-but-working beats broken. |

Under best effort the residual executes in-process where it can, and **every
degradation is logged, counted as a metric, and surfaced in a warning header.**
The cost stays visible to the operator even though it is paid rather than
refused — which preserves what §2 was actually protecting.

The mode is a per-service policy with a documented default per surface, so an
administrator can force refusal on a compatibility service that is being abused,
or force best-effort on a native service during a transition.

**This is a genuine weakening of §2, recorded as such.** The principle was
written before the migration requirement was fully absorbed, and it was too pure
to survive it.

## 3. Alternatives considered

### Alternative A — Query AST compiled per provider, with capability negotiation

**For.** One domain model, many backends. Capability differences become explicit
data rather than scattered conditionals. Parameterisation is structural rather
than a discipline. New providers are additive.

**Against.** An abstraction is a place where capability goes to be lost. It risks
becoming a lowest common denominator that wastes PostGIS.

### Alternative B — Direct provider-specific query construction

**Excluded 2026-08-12.** With three spatial dialects plus file providers this
means N implementations of every feature, each drifting. Q-21 answered: the AST
targets multiple dialects from day one.

### Alternative C — Adopt an existing query or expression framework

**For.** Less code.

**Against.** General-purpose frameworks do not model spatial capability
negotiation, which is the actual problem. We would fight it at exactly the point
that matters. Rejected — but the *parser* for CQL2 is a different question and
adoption there is reasonable.

## 4. Decision

**Alternative A**, with the following shape.

### 4.1 The AST is a domain model, not a SQL builder

The query model is expressed in GIS terms: collection, attribute filter, spatial
predicate, bbox, CRS, field selection, sort, pagination, aggregation, tile
envelope.

**It must contain no SQL concepts.** The moment it does, it stops being able to
target GeoParquet, FlatGeobuf, or anything that is not a database — and those
are on the provider list (§27).

### 4.2 Plan, negotiate, split

```text
Query AST
   ↓  plan
Logical plan
   ↓  negotiate with the provider
┌──────────────────────┬──────────────────────┐
│ pushed-down fragment │ residual             │
│ executes in the      │ executes in-process,  │
│ provider             │ or is refused         │
└──────────────────────┴──────────────────────┘
```

The provider answers a **capability query**, not a yes/no: which predicates,
which spatial operations, which aggregations, what sort and pagination
semantics, what write and DDL rights ([data-model.md](../data-model.md) §2).

### 4.3 The residual is deliberately small

In-process residual execution is limited to operations that are cheap on a
stream: attribute filters on already-fetched rows, projection, limit, and simple
ordering on bounded result sets.

**Anything else is refused with an explanatory error** naming the provider, the
unsupported operation, and — where one exists — the alternative.

This is the anti-overengineering position (§82). We are not building a
distributed query executor because we have not been asked for one, and building
one would hide exactly the cost differences operators need to see.

**Q-19 (DuckDB as a general compute layer) is deferred, not rejected.** It
becomes justified only when we can point at real refused queries that users
actually need. That evidence does not exist yet, and adopting a compute engine
at the centre of the architecture on the strength of a plausible argument is the
kind of decision §10 warns is hardest to reverse.

### 4.4 Streaming is mandatory, and cancellation is part of it

Results stream from the provider to the response body. Nothing materialises.

Per dialect: server-side cursors or the nearest equivalent. Backpressure must
propagate from the HTTP client through to the database read, so a slow consumer
slows the read rather than filling a buffer.

**Cancellation is a correctness requirement, not tidiness.** When a client
disconnects, the database query is cancelled. An abandoned query holds locks,
and a held lock is precisely what blocks the DBA's DDL
([ADR-007](ADR-007-service-runtime.md) §5b). A query engine that leaks abandoned
queries recreates the problem the runtime discipline exists to prevent.

### 4.5 Pagination is keyset, behind an opaque token

Offset pagination is O(n) at depth and unstable when rows change underneath.
Keyset pagination is stable and cheap but requires a total order.

- Next-page links carry an **opaque cursor token**. Clients do not construct it.
- OGC API Features' `offset` is supported for spec conformance and documented as
  degrading with depth.
- The token encodes the sort key, the plan fingerprint and the layer's schema
  fingerprint. **If the schema drifts under a paging client
  ([data-model.md](../data-model.md) §3), the token is rejected with a clear
  error rather than silently returning incoherent pages.**

### 4.6 Safety

- **Parameterise everything.** Values never reach SQL as text.
- **Identifiers are whitelisted, never passed through.** Table, schema and column
  names come from the service definition, matched by identity. A user-supplied
  string is never used as an identifier, even after escaping.
- **The filter parser is an attack surface.** CQL2 parsing is bounded: maximum
  nesting depth, maximum term count, maximum literal size. A parser without
  limits is a denial-of-service primitive.
- **Geometry input is an attack surface.** Filter geometries are bounded by
  vertex count and extent before they reach a provider. Geometry bombs are named
  explicitly in §54.

### 4.7 Governance is planned, not policed

Per-service limits (§49) — maximum features, maximum response bytes, statement
timeout, maximum filter geometry complexity — are enforced **in the plan**,
pushed down as `LIMIT` and statement timeouts wherever the provider supports
them.

Enforcing a limit after the work has been done protects the client and not the
server, which is the wrong way round.

### 4.8 The tile path is a specialisation, not a separate engine

A tile request is a query with a tile envelope: bbox filter, clip, simplify,
transform to tile space, encode.

- Filter, clip and simplify push down where the dialect supports them
  ([research/hosted-datastore-and-tiles.md](../research/hosted-datastore-and-tiles.md)
  §2). All three spatial dialects do, with different function names and
  different semantics.
- Tile-space transform and protobuf encoding are ours in every case.
- `ST_AsMVT` is an optional PostGIS fast path, adopted only if
  `benchmarks/mvt-generation/` shows the gain justifies a second code path, and
  only behind a conformance test proving the two produce equivalent tiles.

## 5. Counterarguments to this decision

- **Refusing queries is a worse user experience than answering them slowly.**
  True, and it is a deliberate trade. The mitigation is that the capability
  report tells a client what will be refused *before* it asks, so refusal is
  predictable rather than a surprise. If that mitigation is not built, this
  decision becomes user-hostile.
- **The residual boundary will be relitigated constantly.** Every refused query
  is a feature request. The boundary needs to be written down and defended, or
  it will drift outward until we have built the distributed executor we declined
  to build.
- **Keyset pagination is harder than it looks** with arbitrary sorts, nullable
  columns, and no guaranteed unique key. Some layers may have no usable total
  order, and those fall back to offset with its known weaknesses.
- **A domain AST with no SQL concepts is easy to state and hard to hold.** The
  first time a provider needs something the model cannot express, the pressure
  will be to leak SQL into it.

## 6. Evidence

| Claim | Evidence | Source |
|---|---|---|
| Filter, clip, simplify push down on all three dialects | — | `VERIFY` — A-021, table in hosted-datastore-and-tiles §2 |
| In-process MVT encoding meets latency targets | — | `experiments/lang-slice` endpoint C, A-019 |
| `ST_AsMVT` gain justifies a second path | — | `benchmarks/mvt-generation/` |
| Streaming holds at millions of features per dialect | — | `benchmarks/feature-query/` |

Status is `ACCEPTED WITH CONDITIONS` rather than `ACCEPTED` because these rows
are empty. The **structure** is decided; the **numbers** are not.

## 7. Consequences

**Positive.** One query model over unequal providers. Capability differences are
visible instead of hidden. Injection is prevented structurally. The tile path
reuses the query path rather than duplicating it. Cancellation discipline
supports the runtime's obligation not to block a DBA.

**Negative.** Some queries that a naive engine would answer are refused. The
residual boundary needs continuous defence. Three dialect compilers to write and
test, plus file providers. Keyset pagination is genuinely harder than offset.

**Ports created.** The **provider port**: capability description, plan
compilation, streaming execution, cancellation. Every provider implements it,
including the datastore — which is a provider we own rather than a special case.

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-019 | In-process MVT encoding meets latency targets | `UNVALIDATED` — critical |
| A-021 | Filter, clip and simplify push down usefully on all three dialects | `UNVALIDATED` |
| A-024 | A small residual executor plus explicit refusal is acceptable to real users, given a capability report | `UNVALIDATED` |

## 9. Dependencies

**Depends on:** ADR-002 (where state lives), [data-model.md](../data-model.md)
(layer modes, write capability). **Not** ADR-001 — the structure here is
language-independent, which is why it could be decided first.

**Depended on by:** ADR-005 (API), ADR-010 (cache keys derive from plan
identity), ADR-011 (long queries become jobs), tile pipeline, feature services
(§28).

## 10. Conditions

1. **The capability report must ship with the first refusal.** Refusing without
   telling clients in advance what will be refused makes §2's choice
   indefensible.
1a. **A second dialect compiler exists from Phase 1** and runs in CI (adversarial
   review F1). No query engine feature is complete until it compiles on two
   dialects. An abstraction with one implementation is a wrapper, and this ADR
   says so itself — the condition makes that statement testable rather than
   aspirational.
2. **The residual boundary is written down** — an explicit list of what executes
   in-process — and changes to it are ADR amendments, not implementation
   choices.
3. **A-021 must be verified** before the tile path is implemented. If the three
   dialects' `simplify` differ enough to produce visibly different tiles, §4.8
   needs rethinking.

## 11. Revisit triggers

- Refused queries turn out to be common and legitimate in real use — that is the
  evidence that reopens Q-19 and the compute layer.
- A provider appears that cannot express the capability model.
- Streaming benchmarks fail on any dialect at target scale.
- Editing enters scope, which would reopen this ADR substantially.

## 12. Dissent

**The DuckDB compute layer has a real case and is being deferred on a
process argument rather than a technical one.** With three providers of unequal
capability, capability gaps are certain, and something must eventually fill
them. Deferring means we will refuse queries we could have answered, and the
first users to hit that will be right to complain.

The counter-argument — that adopting a compute engine at the centre of the
architecture before we can name the queries it would rescue is exactly what §82
forbids — is the one that wins here. But it wins on discipline, not on
substance, and it should be revisited as soon as there is a list of real refused
queries rather than an argument that there will be.
