# ADR-042 — OGC API Features, and the inversion it unwinds

| | |
|---|---|
| **Status** | `ACCEPTED WITH CONDITIONS` |
| **Confidence** | `HIGH` for the decision, which was already taken · `MEDIUM` for the conformance claim until §7 condition 3 runs |
| **Decided** | 2026-08-20 |
| **Supersedes** | — |
| **Superseded by** | — |
| **Amends** | [ADR-005](ADR-005-api-architecture.md) — this is the surface it chose, arriving; [v1-scope](../v1-scope.md) §4's inversion is unwound rather than re-argued |

---

## 0. This one resumes a plan rather than reversing one

[ADR-041](ADR-041-the-map-renderer.md) §0 had to open by recording that it
contradicted something the owner had said. **This ADR has the opposite problem:
nothing here is new, and the interesting question is why it took so long.**

- **[ADR-005](ADR-005-api-architecture.md) chose OGC API Features 1+2+3 as the
  *native* surface**, with ArcGIS and the OGC classics in a compatibility layer
  outside the core domain (§51).
- **[v1-scope](../v1-scope.md) §4 inverted that** on 2026-08-13: v1 ships ArcGIS
  only, OGC API Features moves to v2, and **ADR-005 is `REOPENED`** because the
  inversion is not an amendment.
- **[Q-94](../open-questions.md) recommended OGC API Features as the first surface
  after v1**, and the owner chose WFS instead
  ([ADR-039](ADR-039-wfs-is-the-first-surface-after-v1.md) §1).
- **2026-08-20, owner decision:** *"ogc features api da implemente edelim."*

So the decision this records is *when*, not *whether*. What follows is the shape.

## 1. Context

**Four faces now serve the same layers and none of them is the one this project
chose.** FeatureServer, VectorTileServer, WFS 2.0 and — since this morning — WMS
1.3.0 and MapServer. Every one is a compatibility surface for somebody else's
product or somebody else's decade.

**OGC API Features is the current standard and it is the one a new client
reaches for.** It is JSON over HTTP with links, no XML, no capabilities document,
no axis-order default trap: CRS84 is longitude first, which is what GeoJSON means
and what every web developer assumes. QGIS, GDAL, the OpenLayers and MapLibre
ecosystems, and every *"give me a REST API for my data"* tool speak it.

**And most of it already exists here.** The catalogue, sharing, `FeatureQuery`
with extent, offset, ordering and reprojection, and — from the WFS face — a
GeoJSON writer. What this decision buys is the vocabulary and the link graph.

## 2. The questions this answers

1. Which parts, and which conformance classes.
2. Where it lives, since a landing page URL is the one thing a client keeps.
3. What a collection is, given that four other faces already name the same layer.
4. What is refused rather than approximated.

## 3. Alternatives

### Alternative A — do not build it, and point OGC clients at WFS

**Argument for.** WFS 2.0 works, ships GeoJSON as well as GML, and was built five
days ago. Every client that speaks OGC API Features also speaks WFS — QGIS and
GDAL both do. So this buys nothing a client cannot already get.

**Argument against.** *Also speaks WFS* is true of the desktop tools and false of
everything written since about 2019. A JavaScript developer handed a WFS URL
writes an XML parser or gives up; handed an OGC API Features URL they call
`fetch`. And the argument is one this project already rejected twice, in ADR-005
and in Q-94's recommendation — reaching for it now would be re-deciding a
question by not answering it.

### Alternative B — Part 1 Core only, CRS84 and nothing else (rejected)

**Argument for.** It is the smallest conforming thing. One reference system, no
`crs` parameter, no `storageCrs`, no `Content-Crs` header.

**Argument against.** This server holds layers in EPSG:3857 and will hold them in
national grids. A client asking for data in the CRS it will draw in gets a
transformation it did not ask for and cannot switch off, and the round trip
through WGS 84 loses precision on every one of them. Reprojection is the
database's here, so Part 2 costs a parameter and a header rather than a
capability.

### Alternative C — Parts 1 and 2, with HTML and OpenAPI (chosen)

**Argument for.** It is what a conforming server looks like to the tools that
check. The `html` class is what makes the API browsable, which is how a person
verifies that their data is really there; `oas30` is what a generated client
reads. Both are cheap here: the HTML renders through the directory this server
already has, and the OpenAPI document describes seven paths.

**Argument against, and it is the honest one.** Claiming a conformance class is a
promise, and two of these are promises about documents rather than about data. An
`oas30` claim with a stub definition, or an `html` claim with pages that drop
their links, is worse than not claiming them — §7 condition 3 exists because of
exactly that.

### Alternative D — add Part 3, CQL2 filtering

**Argument against, and it is why this is a separate decision.** CQL2 is a query
language with its own grammar, two encodings (text and JSON), and a spatial and
temporal function set. It is not a parameter. It is also the place where
[ADR-008](ADR-008-query-engine.md)'s expression tree would finally earn its third
caller, so it is worth doing properly rather than partly.
[Q-132](../open-questions.md).

## 4. Evidence

| Claim | Evidence | Source |
|---|---|---|
| The data path needed nothing new | `FeatureQuery` already carries extent, offset, ordering, field selection and `outSrid`; the only addition was a shared GeoJSON writer | this repository |
| A second caller demanded the writer seam | Geometry-to-GeoJSON lived privately inside `GeoJsonFeatureCollectionWriter`. It is now `Graticula.Core.Formats.GeoJsonWriter` and both faces use it — the same shape ADR-008's predicate emitter got when WFS became its second caller | this repository |
| Both axis conventions work and agree | `bbox=32,39,33,40` in CRS84 and `bbox=39,32,40,33` in `EPSG/0/4326` both match 46 features of `tr_il`, measured against the running server 2026-08-20 | §7 condition 1 |
| Part 2 reprojects rather than relabels | `crs=…EPSG/0/3857` on `look_parcels` returns `[3657800, 4862900]` with `Content-Crs: <…EPSG/0/3857>`; the same request in CRS84 returns degrees | as above |
| External conformance | **Absent.** §7 condition 3 | — |

## 5. Decision

**Build OGC API Features Part 1 (Core) and Part 2 (CRS), read-only, with the
`html` and `oas30` classes.**

### 5.1 Where it lives

`/ogc/features/v1`. **Versioned in the path**, because the OGC API family versions
its parts independently and a landing page is the one URL a client is given and
keeps. It leaves room for `/ogc/tiles/v1` and `/ogc/styles/v1` beside it without
either of them being the thing that has to move.

**Not at the server root.** The root already redirects to the REST services
directory, and a server whose landing page is a different protocol depending on
the year is a server nobody can bookmark.

### 5.2 A collection is a layer, under the name it already has

The collection id is the layer's own name — the same string as a WFS
`typeName` without its prefix, a WMS `LAYERS` value and a MapServer layer name.
**Four faces, one name.** An operator who learns it once should not learn it four
times.

### 5.3 Sharing and limits are the catalogue's

The same four checks every other face applies: the service is running, the caller
may see it, they hold the privilege, and the feature face is switched on
([D-123](../architecture-debt.md)). **A collection a caller may not see is absent,
not forbidden** — a 403 would name a private layer to a stranger, so the answer is
404 with the same wording as a name that was never published.

### 5.4 What the items resource honours

`limit`, `offset`, `bbox`, `bbox-crs`, `crs`, `datetime`, and **any property of
the collection as a parameter of its own name**, which is the whole of Core's
attribute filtering (§7.15.4). A property value is converted to the column's type
before it is compared, because a text comparison against an integer column is an
error at the database rather than an empty result.

**`limit` is clamped rather than refused**, and it is the one parameter on this
server that is. §7.15.3 says the server may return fewer and the client learns the
real number from `numberReturned`; refusing would break every client that sends a
large limit knowing it will be capped.

**Everything else unknown is refused.** §7.15.5 requires it, and the reason is
this project's usual one: a client that mis-spells `datetime` and receives every
feature has been told its filter worked.

### 5.5 Paging is ordered by identity, always

`offset` paging is only sound against a stable order, and OGC API Features does
not make the client ask for one. So every items query is ordered by the layer's
identity column. Without it, a feature inserted between page one and page two
pushes another off the end of page two, where nobody ever sees it.

### 5.6 Refusals are HTTP status codes, and this is the first face where that is true

WFS and WMS both answer their refusals with **200** and an exception document
inside, because their clients were written that way and several never read the
body of a 4xx. OGC API Features is HTTP-native: §7.3 puts the outcome in the
status and the explanation in an RFC 7807 body. **So a proxy, a log and a monitor
can all see a refusal on this surface**, which is not true of any other face this
server has.

### 5.7 What is not built

- **Part 3, CQL2 filtering.** [Q-132](../open-questions.md), and it is where
  ADR-008's expression tree would earn its third caller.

  **This contravenes [ADR-005](ADR-005-api-architecture.md) condition 1**, which
  says *Part 3 ships with Part 1* and calls a filterless feature service
  *"technically true and practically useless"*. That condition was deferred while
  OGC API Features was out of scope and returns the moment it is built — so it
  returned today, and it is unmet. **Recorded here rather than left for the
  register to notice.** The mitigation is real and partial: Part 1 §7.15.4's
  per-property equality is implemented, with `bbox` and `datetime`, so what shipped
  is not filterless. What it cannot express is `or`, a range, or a spatial
  predicate. **Either Part 3 gets built or condition 1 gets amended by a decision.**
- **Part 4, transactions.** This surface is read-only, as WFS is.
- **`sortby`.** It is not in Part 1 and the ordering is fixed at identity anyway;
  offering it would mean re-opening §5.5's paging guarantee.
- **A path per collection in the OpenAPI document.** §7.3 allows either, and one
  path per collection makes the definition grow with the catalogue — at 100–1,000
  services that is a document every client downloads and nobody reads.

## 6. Consequences

**Positive.** The surface ADR-005 chose exists. A client written this decade can
read this server with `fetch` and no XML. **And ADR-005 can re-close**, which
takes [Q-89](../open-questions.md) with it: the protocol-neutral internal
interface question is now answerable with evidence rather than by faith, because
there are five faces over one query model and it is visible how much of each is
vocabulary.

**Negative.** A fifth surface, on a product whose §66 gates still stand at FAIL —
the same cost ADR-039, ADR-040 and ADR-041 each recorded, taken a fourth time.
Two more conformance classes claimed, which are two more promises. And the
`html` face renders through the REST directory, so the OGC pages inherit a look
designed for a different protocol.

**Ports created.** None. One extracted:
`Graticula.Core.Formats.GeoJsonWriter`.

**And two of ADR-005's conditions came back unmet.** Deferring OGC API Features
deferred four of its conditions with a note saying they return unchanged when the
decision does. They returned: condition 1 wants Part 3 shipped with Part 1, and
condition 2 wants the conformance list generated rather than hand-maintained —
`OgcNames.ConformsTo` is an array of five URIs somebody has to keep true. **Both are
live and unmet**, which is a deferral doing its job rather than quietly becoming a
retraction.

## 7. Conditions

1. **DISCHARGED 2026-08-20. `OgcFeaturesConformanceTests`, eighteen tests against
   the live process.** The link graph is **followed** rather than constructed —
   every resource is reached from the landing page by its `rel`, because a test
   that builds `/conformance` itself passes against a landing page whose links all
   point at the wrong host. Both axis conventions are asserted against a
   deliberately non-square, off-centre box, since a square one passes with the axes
   swapped. A feature is fetched back by the id its collection gave it, which is
   the only thing that member is for. Paging follows `next` and asserts no id
   repeats. An unknown parameter, a negative limit, a three-number bbox and an
   unparseable datetime are each a 400 with an RFC 7807 body, and an unknown
   collection is a 404.
2. **DISCHARGED 2026-08-20.** Every HTML representation answers 200 with
   `text/html` and contains links, and the collections page names the collection —
   asserted for the landing page, conformance, collections, one collection and its
   items.
3. **DISCHARGED 2026-08-20 by running it. `ogccite/ets-ogcapi-features10`: 0
   failed, 25 skipped, and one Part 2 assertion still red.** The passing count
   varies between runs — 202 and 306 on two consecutive ones — because the suite
   samples collections and the suites share a server ([D-75](../architecture-debt.md)).

   **The first run was 215 passed and 13 failed, and every one of the thirteen was
   a real defect.** Three causes:

   - **A zero-area `bbox` was refused.** The rule was copied from the WMS face,
     where an image genuinely needs area; an intersection test does not, and this
     server holds two layers whose whole extent is a horizontal line. A client
     sending the published extent back as its `bbox` was refused for doing the
     obvious thing.
   - **A valid `bbox` produced a 503.** `bbox=-180,-90,180,-85` is legitimate in
     CRS84 and untransformable into Web Mercator, which is undefined below −85.06°;
     PostGIS answered *transform: tolerance condition error*. The fix is general
     rather than a table of areas of use: **the filter is intersected with the
     collection's own extent**, which cannot change which features match and is
     transformable by construction.
   - **`bbox` was typed as a string in the OpenAPI document**, where Part 1
     §7.15.3 types it as an array of four or six numbers. A generated client built
     from that schema sends the whole box as one opaque value.

   **And a fourth, found while fixing the first three, which is the one worth
   keeping.** A client sending a collection's own extent as its `bbox` got **no
   features at all** from three of five layers — the query guaranteed to match
   everything. The extent is produced by projecting the data one way and the filter
   is projected back the other; the two disagree in the last digits, every feature
   sits exactly on an edge, and every edge test fails. **A transformed box is now
   widened by a micro-degree**, which is far below anything stored and far above a
   round trip's error.

   **What is still red is `verifyBboxCrsParameter`**, and the cause is understood:
   the widening applies where a transformation happens and not where none does, so
   the geographic and projected spellings of one box can disagree about a feature
   within ten centimetres of its edge. [Q-133](../open-questions.md). It is a
   consistency question between two transformation paths, not a missing feature.

   **GREEN 2026-08-23, and the widening is gone.** [Q-133](../open-questions.md)
   asked whether a bounding box should mean the same thing in every reference system
   it can be written in, and the answer is yes, taken by repairing the other end.
   Evidence: [cite-ogcapi10-2026-08-23.rdf](../reviews/cite-ogcapi10-2026-08-23.rdf) —
   **1,268 passed, 6 failed, 78 untested against all thirteen collections**, where the
   earlier runs sampled a few and reported 306 of 332. `verifyBboxCrsParameter` is not
   among the failures.

   **The defect was never in the filter.** It was that the *published extent did not
   contain its own data* once round-tripped, and a filter epsilon was a way of hiding
   that from one direction only. So the extent is published rounded outward to six
   decimals — about a tenth of a metre, far below anything stored and orders of
   magnitude above a round trip's error — and **every filter is compared exactly, in
   every reference system**. Part 1 §7.13 asks for an extent and does not ask for it
   to be tight; an extent is an upper bound, and a generous one costs a client nothing
   while an exact one that excludes its own features costs it everything.

   **The alternative was to widen both spellings.** That also makes them agree, and it
   does it by making every filter in the product ten centimetres wrong — a false
   positive on a feature outside the box the client asked for, in the one operation a
   client uses to decide what is inside something. A document is the right place for an
   approximation because a document states where the data is; a filter is not, because
   a filter is a question with an exact answer.

   **And the repair was wrong once, in a way only a measurement found.** Rounding at
   the point of printing left the filter path clamping the request to the *un*rounded
   extent, which erased the rounding before the transformation and put the original
   defect straight back — the six-feature layer answered its own extent with four. **The
   invariant is that the extent a client is given is the extent the server clamps to**,
   and one number satisfies it where two do not. `OgcExtentConformanceTests` states it
   from outside, and it was checked by disabling the rounding and watching two of its
   three tests fail with the layers named.

   **The six remaining failures are the suite's bound, not this server's defect, and
   that was measured rather than assumed.** All six are one assertion —
   *numberMatched (5433) does not match the number of features in all responses (50)* —
   on the three collections larger than fifty features that the suite did not skip. The
   request log shows what happened: for `tr_il` the suite asked for five pages, stopped,
   and compared its fifty against a matched count of 5,433. For `tr_yer` a different
   test walked all 143 pages and got all 1,421. Walking every page of every collection
   yields exactly the number each says it matched, which is now asserted by
   `OgcExtentConformanceTests` precisely so that this can be settled without the suite.
   **A red result from an external suite is evidence and not a verdict** — and this
   repository has twice spent hours treating one as the other in the opposite direction.
4. **DISCHARGED 2026-08-20. [ADR-005](ADR-005-api-architecture.md) is re-closed**,
   with a §0b that measures rather than asserts. It had been `REOPENED` since
   2026-08-13 on the grounds that v1 had one face and the decision assumed several;
   there are five, so the reason expired rather than being argued away.

   **And §51's boundary turned out to need neither amending nor suspending.** §0
   said the compatibility layer becoming the product surface meant *outside the core
   domain* had to give. It did not: every face, including the ArcGIS ones, lives in
   its own project outside `Graticula.Core`, and the architecture suite fails the
   build if that reverses. What v1 changed was which face a user reaches for.
5. **DISCHARGED 2026-08-20, and the recommendation it carried was right.**
   [Q-89](../open-questions.md) asked whether to build the protocol-neutral interface
   on faith or extract it when the second caller arrived. Measured across five faces:
   **zero SQL in any adapter project**, eight `FeatureQuery` construction sites all
   in the host, and `TierBoundaryTests` still green.

   **The interface was never built on faith and never needed to be.** It was
   extracted twice, each time under pressure from a real second caller —
   `AttributePredicate`/`PredicateSql` when WFS became the second consumer of the
   where-clause emitter, and `GeoJsonWriter` when this surface became the second
   writer of GeoJSON. Each cost a day.

   **What building ArcGIS first did cost is four names.** `ObjectIdColumn`,
   `IsArcGisServable`, `FeatureQuery.ObjectIds` and `FeatureEdits.ObjectId` are one
   protocol's vocabulary sitting in Tier 1 — [D-124](../architecture-debt.md).
   MASTER §8's rule held for the architecture and leaked in the naming, which is a
   far smaller bill than an abstraction exercised by nothing.

## 8. Assumptions

| ID | Assumption | Status |
|---|---|---|
| A-078 | A client that speaks OGC API Features can read this server without knowing anything about its other faces — the link graph from the landing page is sufficient | `UNVALIDATED` — condition 3, and the CITE suite is exactly a client that starts at the landing page and follows links |

## 9. Revisit triggers

- **Somebody asks to write through this surface.** Part 4 is a different
  decision and this ADR builds none of it.
- **A client asks for CQL2.** Q-132, and it should be reopened deliberately
  rather than answered with a partial filter parameter.
- **The collection list stops being the layer list.** The moment a collection is
  anything but a view of a published layer — a join, a subset, a saved query —
  §5.2's *four faces, one name* has gone and the naming needs deciding again.

## 10. Dissent

**This is the fifth protocol surface in two days.** ADR-039 recorded the cost
once, ADR-040 twice, ADR-041 three times, and the sentence has not changed:
Phase 1 does not end with the carried debts open, and each surface makes the pile
they sit under larger. The difference here — and it is a real one — is that this
surface was always the plan, so building it closes a reopened ADR rather than
opening a new one. **That is an argument about direction and not about capacity**,
and capacity is what the §66 gates measure.
