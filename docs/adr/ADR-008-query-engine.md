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

**Confirmed unavoidable 2026-08-12 (Q-50a).** The owner chose full read/write
providers over read-only providers, which was the option that would have deleted
this gap entirely. So three transaction semantics, three isolation models and
three definitions of a conflict are now committed work rather than a risk to be
avoided, and this pass is a precondition for implementation rather than a
nice-to-have.

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

### 4.5a Lossy on read means not writable

From [geometry-crs-policy.md](../geometry-crs-policy.md), which found the same
problem in five places and resolved it once.

> **Any representation that discarded information on read must not be the basis
> of a write.**

Z and M dropped by a 2D format. A curve linearised for GeoJSON or MVT.
Coordinate precision lost to tile quantisation. In each case a client does
something entirely reasonable - read a feature, change an attribute, save it -
and destroys geometry it never knew was there.

**The write path enforces this, rather than trusting clients.** A write is
refused when the geometry it carries came from a lossy representation, unless
the client supplies the full-fidelity geometry. Concretely: geometry that
arrived via a tile is never a write source, and a client that read 2D must not
write back over 3D.

This is the geometry counterpart of [ADR-005](ADR-005-api-architecture.md)
§3.8's rule that optimistic concurrency rests on database-maintained state
rather than on what we remember - both are cases where trusting the client
produces silent data loss.

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

**Refined 2026-08-12 (Q-56).** A single response-size limit is the wrong
granularity when one feature is enormous - a national coastline as one polygon
makes the *layer* unusable rather than failing one call. Three tiers instead:

| Situation | Behaviour |
|---|---|
| Over the limit because of many features | Paginate, as designed |
| Over the limit because of **one** feature | Return that feature alone with a warning header. The limit protects the server; a single feature the user asked for is not an attack. |
| One feature over the absolute cap | Refuse **that feature**, identified by id - not the query |

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


**Measured 2026-08-12** —
[benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md).
In-process encoding costs **94 ms against `ST_AsMVT`'s 62 ms** on a dense z14
tile of 4,863 features, so the fallback path is viable and the multi-database
promise holds. Two things the measurement changed:

- **Clipping must be ours, not the geometry engine's.** `NTS.Intersection` was
  79% of the whole request; a rectangle clipper made it 1.6%, a 63x reduction on
  that stage. Point 2 above therefore understates the split: it is not only
  filter/clip/simplify pushdown that matters, it is that where we *cannot* push
  down, the clip must still not be a general overlay.
- **The equivalence requirement in point 4 is met for polygons on PostGIS.** The
  two paths produced output 18 bytes apart across 4,854 features, both decoding
  with zero malformed geometries.

**Run 3, 2026-08-12 — pushdown is structural, not tuning.** A z16 tile with 327
features and 12 KB of output was reading **201,580 vertices to emit 2,080**. Four
administrative polygons — Türkiye at 72,919 points, Marmara Denizi at 52,455,
and two protection zones — overlap every tile in the city, so every tile in
Istanbul was paying for the outline of Turkey. `ST_Simplify` + `ST_ClipByBox2D`
in the database: **13x on latency and 15x on allocation** at z16, and parity with
`ST_AsMVT` at z12.

Two consequences for this ADR:

- **§4.8's tile path requires pushdown of clip; it is not an optimisation.**
  Without it the fallback path reads two orders of magnitude more geometry than
  it emits, and no amount of in-process work recovers that.
- **The per-dialect pushdown table is now load-bearing.** The question for SQL
  Server and Oracle is no longer *is in-process encoding fast enough* — run 1
  answered that — but *can clip be pushed down at all, cheaply, without
  mangling geometry*. `.STIntersection()` and `SDO_GEOM.SDO_INTERSECTION` are
  the candidates and neither has been measured. Recorded as **A-039**.

### Amendment, 2026-08-13 — Q-80, Q-81: the dialect count doubles

MySQL, MariaDB and DuckDB are in scope as providers. Counting properly, the
Query AST now targets **six** SQL dialects rather than the three Q-21 settled:
PostGIS, SQL Server, Oracle, MySQL 8, MariaDB, DuckDB.

**Two corrections to how they arrived.** MySQL and MariaDB were listed as one
row on the capability matrix; they are **two providers**. MySQL 8's spatial
support is Boost.Geometry-backed with a reasonably complete OGC surface, while
MariaDB's diverged and covers less. One capability declaration for both would
have one of them claiming the other's abilities. And DuckDB is **not a
registered-source provider** — see Q-81; it is the file-format query engine, and
placing it there avoids pointing concurrent request traffic at a single-writer
embedded OLAP engine.

**What six dialects actually costs**, per engine and none of it optional:

- SQL generation from the AST, and a **capability declaration** — which is what
  §2's refusal model consumes. Six engines with different function coverage
  means the capability report is doing far more work than it was designed for.
- Type and SRID mapping. **MySQL 8 honours the SRS definition's axis order**, so
  EPSG:4326 is latitude-first — a well-known footgun, and a direct concern for
  [geometry-crs-policy.md](../geometry-crs-policy.md).
- A CI entry running against the **real engine**, not a mock. Testcontainers
  covers MySQL and MariaDB;
  [research/honua-capability-matrix.md](../research/honua-capability-matrix.md)
  §5 found that route while looking for something else.
- If writable (Q-82): a transaction model, a concurrency model and a definition
  of a conflict, all under A-027.

**And the cost that is not per-engine but global — Q-20.** Six providers means
**six geometry implementations** evaluating our predicates: PostGIS's GEOS,
DuckDB's GEOS, MySQL's Boost.Geometry, MariaDB's, SQL Server's, Oracle's
SDO_GEOM, plus NetTopologySuite in our own process. They disagree at the edges
on validity, precision, what *touches*, and empty-geometry results.

**§2's never-degrade-silently principle does not cover this.** Refusing an
unsupported operation is honest. Quietly returning a *different answer* on a
different provider is not, and no capability report catches it, because every
engine claims to support `intersects`.

### Amendment, 2026-08-12 — Q-67: tiles come only from hosted data

**Owner decision, taken after run 3 rather than before it.** Vector tiles are
served only from data hosted in the datastore. Registered Oracle, SQL Server and
foreign PostGIS layers serve features — including full ArcGIS FeatureServer
edits — and never tiles.

Since the datastore is PostGIS-only (Q-32), **every tile source now has
`ST_AsMVT`, `ST_AsMVTGeom` and `ST_ClipByBox2D` available**. That collapses most
of this section:

- The three-dialect tile path is gone. A-039 is `SUPERSEDED`; the tile half of
  A-021 is closed.
- A-019 stays true and stops being load-bearing. Whether we keep our own encoder
  at all is now **Q-68**, to be settled by measuring read-once-encode-many
  against repeated `ST_AsMVT` calls — the seeding case — rather than by citing
  the Tier 1 rule.
- The measured cost of the decision is the difference between the two paths
  under load: 96.3 req/s at 0.1 MB per request, against 28.3 req/s at 20 MB and
  80.9% GC pause.

**What does not collapse.** Filter and predicate pushdown on all three dialects
is untouched and remains this ADR's core problem — it belongs to the feature
path, which Q-06b makes the day-one workload. And finding 11's giant-geometry
floor still applies to bbox feature queries on every engine, where it is
semantically correct rather than waste: the user asked for features intersecting
the box, and Turkiye does intersect it. That case is governed by
[geometry-crs-policy.md](../geometry-crs-policy.md) §6's three tiers, not by
clipping.

**The capability report carries this.** ADR-008 §2's principle — never degrade
silently — already provides the mechanism: a registered layer's capability
report states that tiles are unavailable and why, rather than a tile endpoint
existing and failing.

## 4a. The query AST exists — 2026-08-15

**Owner request: *"I wanted something similar to this, with all capabilities."***
Building ArcGIS's query page meant listing which parameters to offer, and the
answer was embarrassing: `where` accepted the literal `1=1` and nothing else.
The most used capability of the whole API was a text box that refused every
value a person would type into it.

**The refusal was correct and the reason had a shelf life.** §4.6 said parsing
SQL fragments from a request is how injection happens, and that the answer was
the AST this ADR describes. That was true. It stayed true for as long as the AST
did not exist, and the cost was not neutral: a server whose `where` does not work
is not a FeatureServer with a gap, it is a FeatureServer nobody can use.

**`WhereClause` is that AST, narrowed to what ArcGIS's parameter needs.**
Recursive descent over comparisons, `LIKE`, `IN`, `BETWEEN`, `IS NULL`,
`AND`/`OR`/`NOT` and parentheses. What matters is what it does with the result:

- **Nothing the caller wrote reaches SQL as text.** Identifiers are *matched*
  against the layer's real columns and the emitted name is the one we already
  had; operators come from a fixed table; every literal is a bound parameter.
  The emitted string is ours.
- **What is absent is absent by construction**, not by a blocklist. There is no
  grammar rule for `;`, comments, subqueries, function calls, arithmetic or
  column-to-column comparison — so none of them can arrive by an escape nobody
  thought of. Twelve real injection techniques are asserted refused, and the
  conformance suite checks the refusal is a **400 from us** rather than a 500
  from PostgreSQL, because a 500 would mean the parser had already failed.
- **Two limits are denial-of-service controls rather than tidiness.** Clause
  length is capped, and so is parenthesis depth: recursive descent recurses once
  per bracket, and a stack overflow in .NET cannot be caught and takes the
  process down.

**What is still not in the grammar, and would be safe to add:** arithmetic and
scalar functions. Both are shapes rather than holes. They are absent because
nobody has asked, and adding either is a deliberate act with its own tests.

### 4b. Everything else the query operation documents

The same pass implemented the rest of the parameters, each against PostGIS:

| Parameter | How |
|---|---|
| `objectIds` | `= any(@ids)`, bound as an array, capped at 10,000 |
| `spatialRel` × 9 | the PostGIS predicate, always behind an explicit `&&` — `ST_Relate` has no built-in index test and would otherwise scan the table |
| `relationParam` | `ST_Relate` with a checked nine-character DE-9IM pattern |
| `geometryType` × 5 | ArcGIS geometry JSON through the existing reader; the comma syntax only for envelopes and points, which is all it is defined for |
| `distance` + `units` | `ST_DWithin` on the **filter**, six units converted to metres |
| `returnIdsOnly` | ids of the whole answer set, deliberately not capped by the page size — capping it defeats the only reason to ask |
| `returnExtentOnly` | `ST_Extent` and `count(*)` in **one** statement, so the box and the number describe the same set |
| `returnDistinctValues` | `distinct on` the requested fields, with the identity excluded — including it would make every row distinct and the parameter a no-op that looked like it worked |
| `outStatistics`, `groupByFieldsForStatistics`, `havingClause` | aggregates from an enum, never from caller text; output names restricted to plain identifiers |
| `geometryPrecision` | `ST_ReducePrecision` |
| `maxAllowableOffset` | `ST_SimplifyPreserveTopology` — the plain simplifier can produce a self-intersecting polygon, and the caller asked for a smaller shape, not an invalid one |
| `outSR` / `defaultSR` | `ST_Transform` in the select, **and the response reports the reference it is actually in** |

**Transform, then generalise, then round.** `maxAllowableOffset` is specified in
the *output* reference, so simplifying before transforming applies a tolerance
in degrees to metres and is wrong by a factor of a hundred thousand.

**`inSR` is still refused when it differs from the layer**, and the asymmetry
with `outSR` is the point. Output reprojection is a transform applied to the
answer. Input reprojection would mean comparing a filter in one reference
against data in another, and skipping it produces **no error and no features** —
the boxes never meet. That is the defect that made every 4326 tile silently
empty (Q-96), and it is not one to reintroduce on the query path.

**One defect this found in itself.** The first implementation bound the filter
geometry as plain WKB, which carries no spatial reference, so PostGIS refused
every real predicate with *"Operation on mixed SRID geometries"*. The `&&`
operator does **not** check the SRID — so the index-only relations answered
happily while `within`, `touches`, `overlaps`, `crosses`, `relate` and
`dwithin` all errored. A partial implementation that looked complete, and the
reason `Every_spatial_relationship_is_answered_rather_than_refused` walks all
nine rather than sampling.

**What is refused, and none of it for effort:** `time` (no layer declares
`timeInfo`), `fullText` (needs a tsvector column and an index on somebody
else's table), `gdbVersion` (no version tree), `historicMoment` (no history),
`returnZ`/`returnM` (geometry is stored without them), percentile statistics
(needs an ordered-set aggregate), `quantizationParameters`, and `uniqueIds`.
Each appears on the query page **present and disabled with its reason**, rather
than missing.

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
