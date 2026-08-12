# ADR-005 — API Architecture

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `MEDIUM-HIGH` |
| **Decided** | 2026-08-12 |

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

1. **Part 3 ships with Part 1.** Publishing a filterless feature service and
   calling it OGC API Features conformant would be technically true and
   practically useless.
2. **The capability report is generated, never hand-maintained.**
3. **Every extension is reviewed against the additive-only rule** in §3.2 before
   it ships. This is the rule most likely to erode quietly.
4. **Optimistic concurrency must be built on database-maintained state** (§3.8),
   never on our own record of what we saw. Getting this wrong produces silent
   lost updates, which is the worst defect class an editing API can have.

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
