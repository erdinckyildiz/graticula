# ADR-005 — API Architecture

| | |
|---|---|
| **Status** | **`REOPENED` 2026-08-13 by Q-88** — v1 ships ArcGIS only and OGC API Features moves to v2, which **inverts this ADR's central decision**. See §0. Previously: `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` |
| **Decided** | 2026-08-12 |

---

## 0. `REOPENED` 2026-08-13 — v1 is ArcGIS-first

This ADR decided **OGC API Features 1+2+3 as the native surface**, with legacy
protocols in a compatibility layer *outside the core domain*.
[Q-88](../open-questions.md) decided v1 ships **ArcGIS FeatureServer,
VectorTileServer and GeometryServer only**, with OGC API Features in v2.

**That inverts the central decision.** It is not an amendment and should not be
absorbed as one.

**It resolves review finding A10 by choosing it.** A10 found the compatibility
layer had become more capable than the product it wraps — by accretion, in four
places, without a decision. It is now the product surface **deliberately**, which
is defensible where drift was not. Master prompt §51's *outside the core domain*
boundary must be amended to match, or suspended for v1.

**It is consistent with why the product exists.** Q-49's answer is *the ArcGIS
Server exit path*; speaking ArcGIS natively executes that thesis rather than
compromising it. `VERIFY`: implementing a publicly documented REST API is
ordinarily permissible and is the same clean-room position the compatibility
layer already held — emphasis changes, legality does not. CLAUDE.md §5 still
forbids proprietary source and undocumented internals.

**And it makes §3's protocol-neutral internal interface speculative.** §3.2a
argued six faces would be *"the hardest available test"* of whether that
interface is genuinely neutral. **v1 has one face**, so the interface would be
built and never exercised — which is §82's *what concrete problem does this
solve?* answered with *a problem we will have later*. Raised as **Q-89**, with a
recommendation to build the ArcGIS surface directly and extract the interface
when OGC arrives.

**A-026 stops being load-bearing.** It asked whether OGC API Features plus
extensions can express §28. v1 does not ask.

**What survives unchanged:** §3.3a and §3.3b's per-service decisions,
§3.3b's GeometryServer caps, the never-degrade-silently capability report, and
the write-surface analysis — all of which are about ArcGIS surfaces or are
protocol-independent.

---

## 1. Context

How OGC API Features, the legacy OWS protocols, and any compatibility surface
map onto the internal domain.

§8 states the governing rule: **external protocols must not dictate internal
domain architecture.** The risk here is the reverse of the usual one — OGC
specifications are detailed enough to leak into the core if the seam is not
deliberate.

Three inputs have accumulated since this ADR was stubbed:

- **The compatibility layer is a requirement, not an option** (Q-07). Displacing
  ArcGIS Server and GeoServer is a confirmed goal.
- **[ADR-008](ADR-008-query-engine.md) refuses unsupported queries rather than
  answering them slowly**, conditional on a capability report published per
  layer. That report is an API surface and it belongs here.
- **Editing is in scope** (Q-42, corrected 2026-08-12). Feature write endpoints
  exist, and the write surface is an open question — see §3.7.

## 3.2a. Full protocol parity — amended 2026-08-13 (Q-78)

The owner put sixteen further protocols in scope. **The surface map is
[protocol-surface.md](../protocol-surface.md)**, and its central finding governs
how this ADR should be read from here: **twenty-nine protocol faces sit over ten
engines**, eight of which are already decided or in flight.

The consequence for §3's protocol-neutral internal interface is direct. That
interface was asserted rather than proven, and it is about to carry **six faces
over the feature engine alone** — OGC API Features, WFS-T, ArcGIS FeatureServer,
OData v4, gRPC and MCP — with different query languages, identity models,
transaction semantics and error conventions.

**If §3 is right, faces five and six are cheap. If it leaks, this will find out.**
That is a better outcome than the protocols themselves, and A-026 now carries it.

## 3.3a. ArcGIS service types — amended 2026-08-13 (Q-17a)

[Q-17](../open-questions.md) excluded MapServer, ImageServer, GeometryServer and
GPServer together, on the grounds that *those produce rendered images*. **That
justification is false for two of the four**, and the error was found by the
owner rather than by review — it is the first real defect the §63 contradiction
sweep would have caught.

| Service | v1 | Reason |
|---|---|---|
| **FeatureServer** | **In**, including `applyEdits` | Q-17. The reason the compatibility layer exists |
| **GeometryServer** | **In — and the owner calls it crucial (2026-08-13), so it is v1 core rather than a cheap addition to the compatibility layer.** See §3.3b for what that costs | Returns geometry, not images. A thin REST surface over PROJ and NetTopologySuite, both of which [ADR-003](ADR-003-geometry-engine.md) already puts in-process. ~~Near-zero marginal cost~~ — **struck 2026-08-15 by the contradiction sweep, and it was the sentence that made GeometryServer look cheap when scope was set.** Measurement ([benchmarks/geometry-overlay](../../benchmarks/geometry-overlay/RESULTS.md)) found a 6,408-vertex adversarial input costing 153 seconds and 16.7 GB, and the run took the host down. A-042 is `INVALIDATED`; [ADR-022](ADR-022-geometry-server.md) ships the linear half and the overlay half is blocked on [Q-97](../open-questions.md). The linear half really is cheap; the half that made the service worth having is not. ArcGIS Enterprise portals configure a geometry service URL that clients call — so omitting it partially defeats Q-17's own goal. `VERIFY` how much ArcGIS JS API 4.x still needs it |
| **GPServer** | **In, after the Python SDK** (Q-17b) | The *no toolbox* objection was answered by the owner within hours: **the toolbox is not ours to write.** Tools are Python, the ArcPy and PyQGIS model. That reopens [ADR-006](ADR-006-plugin-model.md) by the trigger it named, and is gated behind Q-74 (data boundary), Q-75 (sandbox) and Q-76 (dependencies) |
| **ImageServer** | **In, decomposed** (Q-17c) | Reopens [ADR-009](ADR-009-raster-engine.md). Split per operation: `identify`, `getSamples`, histograms, `download` and footprint `query` are near-free; tiled access is medium; `exportImage`, raster functions and dynamic mosaicking need a raster rendering engine and are their own decision (Q-77) |
| **MapServer** | **Out of v1** | Needs the rendering engine. [ADR-004](ADR-004-rendering-engine.md) §0 — the owner wants this eventually and prefers it to WMS |

### 3.3b. GeometryServer is crucial — and it is a compute endpoint, not a data endpoint

**Owner, 2026-08-13: “GeometryServer is crucial.”** That raises it from a cheap
addition to v1 core, and it changes what has to be true about it.

Everything else in the compatibility layer *reads data we hold*. GeometryServer
**runs computation on geometry the caller supplies**, which is a different shape
with three consequences.

**1. It exposes the exact operation run 1 measured as pathological.**
[benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md)
found `NTS.Intersection` at **438.6 ms — 79% of a whole tile request** — and the
fix was to stop calling it, replacing rectangle clipping with `RectClip`. **That
escape route does not exist here.** GeometryServer's `intersect`, `difference`,
`union` and `cut` are general polygon overlay against arbitrary caller geometry;
there is no special case to exploit. We are publishing the slow operation as an
API.

**2. It is a denial-of-service surface by construction.** A caller may post
Türkiye's outline — 72,919 vertices, which is real data in our own test set —
and ask to intersect it with something. Overlay is superlinear. Required, not
optional: a **vertex-count cap per request**, a **wall-clock timeout per
operation**, and a **total-vertices cap across a batch**, since these operations
accept arrays. This is adjacent to
[geometry-crs-policy.md](../geometry-crs-policy.md) §6 but not the same problem:
§6 governs *serving* a geometry too large to return; this governs *computing* on
one.

**3. It sits on the request path, so ADR-007's worker model owns it.** Unlike
geoprocessing (Q-17b), which is explicitly asynchronous and job-worker-bound,
GeometryServer is synchronous and answers in the request. A-037 measured
allocation as the binding constraint at 80.9% GC pause, and overlay on large
inputs allocates heavily while the caller chooses the size. **Same lesson as
attachments in [ADR-013](ADR-013-feature-service-data-model.md) §4a: any endpoint
where the caller picks our allocation size needs a cap, not a hope.**

**What this does not change:** the implementation is still a thin surface over
PROJ and NetTopologySuite, both already in-process per ADR-003. It is cheap to
*build*. It is not cheap to *operate*. Those are different claims and both are
true.

### 3.3c. ArcGIS geometry mapping, and where `Polyline` lives

`Polyline` is a **wire-format type, not a domain type.** The domain speaks OGC
Simple Features (`GisServer.Geometry.GeometryKind` records why); ArcGIS shapes
exist only in this adapter.

| ArcGIS | Domain | Notes |
|---|---|---|
| `esriGeometryPoint` | `Point` | |
| `esriGeometryMultipoint` | `MultiPoint` | |
| `esriGeometryPolyline` — `paths` | `LineString` (1 path) or `MultiLineString` (n) | |
| `esriGeometryPolygon` — `rings` | `Polygon` (1 shell) or `MultiPolygon` (n) | Ring **winding order** carries the shell/hole distinction; classification happens here |
| `esriGeometryEnvelope` | `Envelope` | A query input, not a stored geometry |

**Outbound is lossless and mechanical.** A `MultiLineString` becomes a Polyline
with n paths; a `MultiPolygon` becomes a Polygon whose rings are ordered
shell-then-holes per part.

**Inbound is ambiguous, and the rule is that the column decides.** A Polyline
with exactly one path could become `LineString` or a single-part
`MultiLineString`, and PostGIS columns are typed — writing `MULTILINESTRING`
into a `geometry(LineString, 3857)` column fails. So: **the target column's
declared type chooses the form**, and an untyped `geometry` column gets the
simpler one. The same rule governs Polygon, where ring classification runs first.

#### True curves

ArcGIS supports `curvePaths` and `curveRings`, and clients send them when a
layer advertises `supportsTrueCurve`.

**We declare `supportsTrueCurve: false`**, and that is not a new decision — it
falls out of [geometry-crs-policy.md](../geometry-crs-policy.md) §5, which
already requires curves to be linearised on output with a documented tolerance
and declared in the capability report. Declaring `false` makes well-behaved
ArcGIS clients densify before sending, which is exactly the behaviour §5 wants,
obtained by telling the truth rather than by policing input.

Two consequences that do need stating:

- **Curve input is refused, not silently linearised.** §5's write rule is that a
  linearised curve must never be written back — it would replace an exact arc
  with a polyline and nobody would notice until a survey disagreed. A client that
  sends `curvePaths` anyway gets an explicit refusal naming the reason.
- **A registered column holding `CIRCULARSTRING` or `CURVEPOLYGON` yields a
  layer whose geometry is read-only.** We linearise on read, and
  [ADR-008](ADR-008-query-engine.md) §4.5a's *lossy on read means not writable*
  then applies automatically. The capability report says so rather than failing
  at the first edit.

Hosted layers never hit this, because we own the schema and do not create curve
columns.

**The pattern worth extracting:** Q-17 bundled four decisions under one sentence
of reasoning. Bundled decisions inherit the weakest justification in the bundle
and nobody notices, because the sentence reads as though it covers everything.
Per-service is more work to write and the only way to be right.

### 3.3d. The single-operation edit endpoints — added 2026-08-15

The owner asked whether this server has CRUD the way it has query. It does:
`applyEdits` has carried adds, updates and deletes since
[ADR-013](ADR-013-feature-service-data-model.md), with per-feature results and
all-or-nothing by default. **But only `applyEdits`.**

ArcGIS also offers `addFeatures`, `updateFeatures` and `deleteFeatures`, and a
client written against the 10.x documentation posts to those — and got a 404
from a server that could do exactly what was asked. **A 404 on a route the
server implements under another name is the worst kind of compatibility gap**:
it reads as a missing capability rather than a missing alias.

All three are now mapped, as **thin rewrites onto the same batch**. `features`
becomes adds or updates; `objectIds` becomes deletes. One writer, one audit
path, one place where rollback is decided — a second implementation of editing
is how the two drift on the rule that matters.

**Each answers with only its own results array**, which is what ArcGIS does and
what an older client parses; three arrays with two of them empty is a different
document. The results still go through the same merge as `applyEdits`, so a
feature the parser rejected keeps its position: a client matches results to its
own features by index, and dropping one shifts every later result onto the wrong
feature — silently, and in the client rather than here.

**`deleteFeatures` refuses a `where` clause, and that is a deliberate departure
from ArcGIS.** Deleting by predicate removes an unknown number of features and
this server has no versioning and no soft delete to undo it with; one mistyped
clause takes a layer. The refusal names the alternative — run the clause through
`/query` with `returnIdsOnly=true`, look at what it selects, pass those ids. A
refusal costs a round trip and a wiped layer costs the data, so the asymmetry
decides it. Revisit when there is something to undo with.

**Still absent from the edit surface**, listed so it is a known gap rather than
a discovered one: `updateAttachment` (add and delete exist), `globalId` (always
null), editor tracking, domains and subtypes ([Q-58c](../open-questions.md)),
and versioned or disconnected editing.

## 2. Alternatives

### Alternative A — Protocol adapters over a protocol-neutral internal interface

**For.** One internal model, many surfaces. A required compatibility layer stays
outside the core (§51). New protocols are additive.

**Against.** An extra hop. And a neutral interface tends to drift toward being
shaped by whichever protocol was implemented first.

### Alternative B — Native OGC API core, legacy protocols as adapters

**For.** Less indirection for the primary surface.

**Against.** Makes OGC API Features the internal model. When the compatibility
layer needs something the spec does not express — and it will, since ArcGIS REST
covers more ground — the core has to be bent. This is §8's warning arriving in
practice.

### Alternative C — Internal model shaped directly by OGC resource semantics

**Excluded.** Directly contradicts §8, and the compatibility requirement makes it
actively dangerous rather than merely inelegant.

## 3. Decision

**Alternative A.** Protocol adapters over a protocol-neutral internal service
interface.

### 3.1 The native API is OGC API Features, Parts 1 + 2 + 3

Owner direction, 2026-08-12: WFS is heavy and dated; ArcGIS REST is pleasant to
use; OGC API Features is the modern equivalent and is what we build on.

`VERIFY` the part structure, because "we support OGC API Features" is
under-specified on its own:

| Part | Status | Decision |
|---|---|---|
| **Part 1 — Core** | Standard | **Required.** But thin on its own: collections, items, bbox, datetime, limit. |
| **Part 2 — CRS by Reference** | Standard | **Required.** A GIS server without CRS negotiation is not one. |
| **Part 3 — Filtering / CQL2** | Standard | **Required.** Real filtering lives here. Claiming Part 1 alone means a service with no usable filter. |
| **Part 4 — Create, Replace, Update, Delete** | **Draft** | **In scope, surface undecided.** Editing is in scope, but Part 4 is not yet a standard. Committing to a draft means tracking a moving specification. See §3.7 and Q-44. |

Part 3 is also where [ADR-008](ADR-008-query-engine.md)'s filter model meets the
wire, so CQL2 is the external form of the query AST's filter — not a separate
parser bolted on.

### 3.2 Extensions are additive and clearly marked

OGC API Features does not cover everything a feature service needs. §28 requires
statistics, aggregation, relationships, domains, subtypes, attachments and
editor tracking; ArcGIS REST has `outStatistics`,
`groupByFieldsForStatistics`, `returnDistinctValues`, `returnCountOnly`,
relationships, domains and subtypes. There is no standard OGC equivalent for
most of it.

So we extend, under a binding rule:

> **An extension may add. It may never change the meaning of anything standard.**
> A client that speaks only Part 1 + 2 + 3 must work against our API with no
> modification and no awareness that extensions exist.

Extensions live under a clearly namespaced path or an explicitly prefixed
parameter, are described in the OpenAPI document, and are individually
discoverable. A client must be able to tell what it is relying on.

This is how we get ArcGIS REST's capability without ArcGIS REST's
non-standardness, and it keeps §50 honest.

### 3.3 Legacy protocols live in the compatibility layer

WMS, WFS and WMTS are **not core**. They are adapters over the same internal
interface, in the compatibility layer (§51), for migration.

> **Stale text below, corrected 2026-08-13** ([independent review 3](../reviews/independent-review-3-synthesis.md) A6). This section still
> said *"Which ArcGIS-compatible surface to offer, if any, is still Q-17"* and
> listed GeometryServer and GPServer as excluded because they *"produce rendered
> images"* — which §3.3a established is false for both. **§0, §3.3a and §3.3b
> are authoritative; the paragraphs below are retained for history.**

**ArcGIS-compatible surface confirmed 2026-08-12 (Q-17): full FeatureServer,
including edits.** Query plus `applyEdits`, `addFeatures`, `updateFeatures` and
`deleteFeatures`. The reasoning: definition import (Q-16) moves the server
configuration and does nothing for the clients already pointing at the old one,
and re-pointing dozens of applications is the reason migrations stall. Q-50a
already gave us write capability on all three engines, so the backend exists.

**Not** MapServer, ImageServer, GeometryServer or GPServer. Those produce
rendered images, which vector-first removed and Q-47 kept out of v1.

This is the largest compatibility commitment made, and it reaches past the API
into the data model. ArcGIS FeatureServer carries assumptions we do not yet
have: a stable integer `objectId` per feature (Q-57), `globalId`, attachments,
relationships, domains, subtypes and editor-tracking fields (Q-58), and
`applyEdits` transaction semantics including `rollbackOnFailure`.

`objectId` is the sharp one. A registered Oracle table with a composite or UUID
key has no such thing, and synthesising one that is stable across requests and
restarts means either a mapping table in a database we may not own, or a
deterministic derivation that is hard for composite keys. **That lands on the
data model rather than on this ADR.**

Clean room applies with full force (§5): published protocol behaviour only.

**Scope narrowed 2026-08-12 (Q-47): WMS is out of v1.** Rendering it needs a
rasteriser, and vector-first removed that from the platform deliberately. So the
v1 compatibility layer covers **WFS** — features, which we can serve — and
**WMTS only where it carries vector tiles**, which we already produce. Raster
map images are not offered.

That is a real reduction in migration reach and it is documented in
[product-context.md](../product-context.md) rather than left to be discovered.

The reasoning is the same one applied to WMS under vector-first: dropping them
would restrict migration to organisations that can also replace their clients,
and desktop GIS, older web applications and third-party tools speak WFS.

Being in the compatibility layer means they may be less capable than the native
API, and that is acceptable and should be documented rather than hidden. What is
not acceptable is a legacy protocol's shape reaching the core.

Which ArcGIS-compatible surface to offer, if any, is still Q-17.

### 3.4 The capability report

[ADR-008](ADR-008-query-engine.md) §2 chose to refuse unsupported queries rather
than degrade silently, and made that conditional on clients being able to find
out in advance. That condition is discharged here.

- **Per collection**, a capability resource states which filter operators,
  spatial predicates, aggregations, sort and pagination semantics are available,
  and which are refused.
- **Extended 2026-08-12** by [geometry-crs-policy.md](../geometry-crs-policy.md):
  it also carries the layer's **validity summary**, its **dimensionality** and
  whether tiles drop Z, **curve handling** and the linearisation tolerance, the
  **collation and case-sensitivity** behaviour of string matching, and the
  **datum transformation pipeline** in use. Each is a provider difference a
  client can otherwise only discover by getting a wrong answer.
- It is **derived from the provider's capability negotiation**
  ([ADR-008](ADR-008-query-engine.md) §4.2), not hand-maintained. A hand-written
  capability document drifts from reality and is worse than none.
- It is reachable from the collection resource and reflected in the OpenAPI
  description where the description can express it.

OGC API Features being OpenAPI-described is genuinely useful here: much of this
is machine-readable in a form clients already consume.

### 3.5 The error model carries the explanation

A refusal must be actionable. It names the unsupported operation, the provider
that cannot perform it, and an alternative where one exists. A bare `400` makes
ADR-008's choice user-hostile instead of honest.

`VERIFY` RFC 9457 problem details as the error format — it is the modern
convention and OGC API guidance leans on it.

### 3.6 Content negotiation and versioning

- **Content negotiation** by `Accept` header, with a format query parameter as a
  fallback for browsers and simple clients. GeoJSON is the default feature
  encoding; MVT for tiles.
- **Versioning by URL path segment** for the API as a whole. Header-based
  version negotiation is more elegant and worse to debug, and the 2 AM test (§7)
  outranks elegance. A URL in a log tells you which version was called.
- **Extensions version independently of the core surface**, so adding an
  extension never forces a core version bump.

### 3.7 The write surface is open, and the reason is awkward

Editing is in scope. The natural home is OGC API Features Part 4 — and `VERIFY`
Part 4 is still a **draft**, not an approved standard.

That leaves three options, none clean:

1. **Implement the Part 4 draft.** Standards-aligned, and we track a
   specification that can still change under us. Breaking changes to a published
   write API are far worse than breaking changes to a read API.
2. **Our own write extension**, under §3.2's additive rule, migrating to Part 4
   when it stabilises. Stable for clients now, non-standard in the meantime, and
   a migration to do later.
3. **Both**, with the extension as the stable surface and Part 4 offered as it
   firms up.

Recommendation: **option 2, designed to converge on Part 4.** Follow the draft's
resource model and semantics closely enough that migration is a rename rather
than a redesign, but do not publish a surface whose contract we cannot control.

Recorded as **Q-44**. This is a genuine trade and it should be decided
deliberately, not by default.

### 3.8 Schema editing and data editing are different questions

Clarified by the owner, 2026-08-12, and worth stating precisely because
conflating them produced a wrong conclusion earlier.

| | Where it happens | Who initiates |
|---|---|---|
| **Schema** — table structure, columns, types | Registered layers: **in the source database**, by the DBA. Hosted layers: through our administrative API, where we own the schema | Not our feature API in either case |
| **Data** — features, attributes, geometry | **Our API.** In scope. | Clients, through us |

Schema changes on registered sources are handled by drift detection and refresh
([data-model.md](../data-model.md) §3), not by offering DDL over the feature
API. On hosted layers we can coordinate the change ourselves
([research/runtime-schema-evolution.md](../research/runtime-schema-evolution.md)).

**The remaining complication is that data writes can also bypass us.** Anyone
with database credentials — QGIS, a script, a DBA — can write rows directly.
This is not a designated path, but it is physically possible and it will happen.

So our bookkeeping cannot assume it sees every change. Editor tracking, change
history and any concurrency scheme built on our own record of events will miss
direct writes.

Three possible responses:

1. **Accept incompleteness.** Editor tracking records what came through us and
   says so. Optimistic concurrency relies on a database-level version column or
   row version rather than our own record, so it stays correct even when we did
   not see the write. Honest and limited.
2. **Push bookkeeping into the database** — triggers or a companion schema
   holding version and audit columns, so any writer updates it. This is how
   ArcGIS gets it right for referenced data, via the geodatabase schema it
   controls. It requires DDL rights, so it is available on the datastore and on
   registered sources where granted. **This is Q-41, which just became
   important again.**
3. **Require all edits through us.** Not enforceable — anyone with database
   credentials can bypass us — so a design that depends on it is a design that
   is silently wrong.

Recommendation: **1 as the baseline, 2 where we have write rights.** Optimistic
concurrency must be built on something the database maintains, not on something
we remember. That rule holds in both cases and it is the part that must not be
got wrong: a concurrency check that trusts our own event log produces silent
lost updates the first time someone edits around us.

## 4. Counterarguments

- **The neutral interface will be shaped by OGC API Features anyway**, because it
  is the first and primary surface. This is Alternative A's real weakness and the
  only defence is discipline: whenever the compatibility layer needs something
  the internal interface cannot express, that is a signal the interface has
  drifted, and it must be fixed rather than worked around in the adapter.
- **Extensions fragment the ecosystem.** Every extension is a thing clients must
  learn, and "standard plus extensions" is how vendors historically made
  standards meaningless. §3.2's additive-only rule is the mitigation, and it
  must be enforced in review rather than trusted.
- **Three surfaces is a lot** — native, legacy compatibility, possibly ArcGIS
  REST. Each needs conformance testing. Q-17 should narrow this, not expand it.

## 5. Consequences

**Positive.** The core learns no protocol's shape. Legacy support is possible
without contaminating the domain. The capability report has a home. Standards
conformance is real rather than nominal, because Part 3 is included.

**Negative.** An extra layer between the domain and every response. Extensions
are a permanent maintenance and documentation burden. Multiple surfaces multiply
conformance testing.

**Ports created.** The **internal service interface** that every protocol adapter
consumes. Its shape is the thing to defend.

## 6. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-026 | OGC API Features Parts 1+2+3 plus additive extensions can express what §28 requires, without bending the standard's meaning | `UNVALIDATED` |
| A-027 | Optimistic concurrency can be made correct against edits we never see, by relying on database-maintained versioning rather than our own records | `UNVALIDATED` — **load-bearing for editing** |

## 7. Dependencies

**Depends on:** [ADR-008](ADR-008-query-engine.md) (capability model, filter
model), [ADR-007](ADR-007-service-runtime.md) (backpressure surfaces as HTTP
behaviour).

**Depended on by:** the compatibility layer (§51), admin API (§39), tile
pipeline, ADR-006.

## 8. Conditions

> **Deferred with the decision, recorded 2026-08-15.** These four are conditions
> on **OGC API Features 1+2+3**, which [v1-scope](../v1-scope.md) §4 moved to v2
> — that move is what left this ADR `REOPENED`. They are not v1 work and were
> being counted as outstanding alongside conditions that are. They come back the
> moment OGC API Features does, unchanged, and none of them is retracted.
>
> The one that survives the deferral in spirit is condition 2 — *the capability
> report is generated, never hand-maintained* — because ADR-008 §2 makes the
> same demand for the ArcGIS surface that v1 does ship. It is tracked there.


1. **Part 3 ships with Part 1.** Publishing a filterless feature service and
   calling it OGC API Features conformant would be technically true and
   practically useless. *(Deferred with the decision — this is a condition on OGC API Features, which [v1-scope](../v1-scope.md) §4 moved to v2, and that move is what left this ADR `REOPENED`. Not retracted; it returns unchanged when the decision does.)*

2. **The capability report is generated, never hand-maintained.** *(Deferred with the decision — this is a condition on OGC API Features, which [v1-scope](../v1-scope.md) §4 moved to v2, and that move is what left this ADR `REOPENED`. Not retracted; it returns unchanged when the decision does.)*

3. **Every extension is reviewed against the additive-only rule** in §3.2 before
   it ships. This is the rule most likely to erode quietly. *(Deferred with the decision — this is a condition on OGC API Features, which [v1-scope](../v1-scope.md) §4 moved to v2, and that move is what left this ADR `REOPENED`. Not retracted; it returns unchanged when the decision does.)*

4. **Optimistic concurrency must be built on database-maintained state** (§3.8),
   never on our own record of what we saw. Getting this wrong produces silent
   lost updates, which is the worst defect class an editing API can have. *(Deferred with the decision — this is a condition on OGC API Features, which [v1-scope](../v1-scope.md) §4 moved to v2, and that move is what left this ADR `REOPENED`. Not retracted; it returns unchanged when the decision does.)*

## 9. Revisit triggers

- Part 4 becomes a standard, which would resolve Q-44 toward alignment.
- Q-17 answers that an ArcGIS-compatible REST surface is required, which would
  make it a third first-class surface rather than a compatibility adapter.
- The internal interface proves unable to express something the compatibility
  layer needs.

## 10. Dissent

**The extension mechanism is the weak point, and history is against us.**
"Standard, plus our extensions" is precisely how vendors have historically
turned standards into marketing claims. We are doing it for good reasons — the
standard genuinely does not cover statistics or aggregation — but the reasoning
that justifies the first extension justifies the tenth, and by then the standard
is a veneer.

The additive-only rule in §3.2 is the whole defence, and rules like it survive
only as long as someone enforces them. Worth revisiting at the first review gate
with a count of how many extensions exist and whether a standard-only client
still gets a useful service.
