# ADR-039 — WFS 2.0, read-only, is the first surface after v1

**Status:** ACCEPTED WITH CONDITIONS
**Confidence:** MEDIUM
**Date:** 2026-08-19

---

## 1. Context

Owner decision, 2026-08-19: *"WFS'i destekleme ile başlayalım."*

**This answers half of [Q-94](../open-questions.md), and not the half the register recommended.**
Q-94 found that v1 serves one of the two markets the product claims. The organisation whose ArcGIS
licence lapsed has ArcGIS clients and ArcGIS-shaped data, and v1 is built for it. The larger half —
the one that never could afford ArcGIS — runs QGIS, and *"a QGIS user's reflex is WFS and WMS, not
`/rest/services`."* Q-94's recommendation was to make **OGC API Features** the first thing after v1.
The owner chose WFS. Both reach the same market; they differ in cost, and in which clients arrive
without being told to configure anything.

**v1 does not grow.** [v1-scope.md](../v1-scope.md) §3d keeps WFS deferred and this ADR does not
amend it. WFS is the first item of what comes *after* v1, begun while v1's carried debts are open,
by the owner's ordering. That is recorded here rather than by editing the cut, because v1-scope.md
is the one document in this repository that ever subtracted anything, and a decision to work outside
it is not a licence to widen it.

## 2. What is decided elsewhere and is not re-decided here

- **[ADR-005](ADR-005-api-architecture.md) §3.1** — the native API is OGC API Features, on the
  owner's own direction of 2026-08-12: *"WFS is heavy and dated; ArcGIS REST is pleasant to use;
  OGC API Features is the modern equivalent."* That judgement is not withdrawn here. What changes is
  the order, not the ranking.
- **ADR-005 §3.3, with §51** — WMS, WFS and WMTS are **compatibility adapters over the internal
  interface**, outside the core domain. So this surface gets its own project, and nothing in
  `Graticula.Core` learns a WFS word.
- **[ADR-018](ADR-018-authorization-and-roles.md) §6, and `SpatialFilter`'s own note** — a third
  party's wire vocabulary stays in its adapter. `esriSpatialRelIntersects` never reached the domain
  and neither does `fes:Intersects`.
- **[ADR-008](ADR-008-query-engine.md)** owns filtering, capability negotiation and the refusal
  model.
- **[ADR-013](ADR-013-feature-service-data-model.md) and [Q-57](../open-questions.md)** own feature
  identity.

## 3. The questions this answers

Three: what *supporting WFS* is bounded to mean, where the surface lives, and **what a WFS filter
compiles into** — which is the only one of the three with an architectural consequence.

## 4. Alternatives

### Alternative A — OGC API Features first, WFS after (the register's own recommendation)

**Argument for.** ADR-005 already made it the native API, so it is the surface this architecture was
designed around rather than one it tolerates. It is JSON end to end: `GeoJsonFeatures` exists, and
there is no XML parser, no schema generation, no GML writer, and no second filter language if CQL2's
text form reuses the shape `WhereClause` already has. Roughly a fifth of the work below. QGIS 3.x and
GDAL both speak it.

**Argument against.** It reaches the clients that were written recently. WFS is what is already
configured in the desktop installs, the older web applications, the third-party tools and the
procurement documents — which is the population Q-94 is about. A modern protocol that nobody has yet
pointed at us is not an arrival path.

**Rejected by owner decision, not by argument.** The argument above is not refuted; see §10.

### Alternative B — a WFS that speaks only GeoJSON

**Argument for.** Half the cost, and every operation below still exists.
`outputFormat=application/json` is widely supported.

**Argument against.** **It is not a WFS to the client's default code path.** GML 3.2 is WFS 2.0's
mandatory output format (`VERIFY` — OGC 09-025r2 clause 7.9.1, and the *Simple WFS* conformance
class), so a client that asks for the default gets a refusal, and a refusal is not a connection. The
whole point of this surface is that an existing QGIS connects without being told to change a setting,
and this alternative fails at exactly that point. Rejected.

### Alternative C — WFS 2.0 with Transaction (WFS-T) now

**Argument for.** The write model is built — `FeatureEdits`, `PostGisFeatureWriter`, per-feature
savepoints, column whitelisting — so the engine half is nearly free. And where our peer puts ArcGIS
`applyEdits` behind a paid tier, we would have two free write paths rather than one.

**Argument against.** It doubles the XML surface at the moment we have none of it, and it puts an
XML-driven *write* path in front of an XML reader nothing has yet attacked. **Deferred, not
rejected** — §9 names what brings it back.

### Alternative D — WFS 2.0, read-only, GML 3.2 and GeoJSON

Chosen. Stated in §5.

## 5. Decision

**A new project, `Graticula.Api.Wfs`**, holding the whole surface: request binding, the Filter
Encoding front end, the GML 3.2 writer and the capabilities document. It depends on
`Graticula.Core`; nothing in `Graticula.Core` depends on it.

**Version 2.0.0 only.** A client asking for 1.1.0 or 1.0.0 gets a version-negotiation exception
report naming what is offered — not a wrong answer in a shape it recognises.

**Six operations:** `GetCapabilities`, `DescribeFeatureType`, `ListStoredQueries`,
`DescribeStoredQueries`, `GetFeature` — with both ad-hoc queries and the `GetFeatureById` stored
query — and `GetPropertyValue`. `Transaction`, `LockFeature`, joins and every other stored query are
out, and the capabilities document says so rather than staying silent.

> **AMENDED 2026-08-19: `GetPropertyValue` was excluded here and the capabilities declared
> `ImplementsBasicWFS` TRUE. Basic WFS requires it.** So this ADR advertised a conformance class it
> had just refused to implement, which is the failure §6 warns about, in the one document a client
> trusts most. The conformance suite said so in as many words: *the mandatory GetPropertyValue
> operation is missing*, and *ImplementsMinimumXPath must be TRUE for all conforming Basic WFS
> implementations*.
>
> **The declaration was made true rather than made smaller**, because the class is what tells a
> client it may send a filter at all and this server does answer filters. Declaring Simple WFS only
> would have hidden a capability in order to correct a claim. What it cost: a `wfs:ValueCollection`
> writer over the same query, the abbreviated XPath forms a `ValueReference` may take — including
> `@gml:id` — and `resolve` advertised with the two values that mean the same thing here.

**Both encodings, because the capabilities would otherwise be a lie.** WFS defines every operation
twice — as a query string and as an XML document — and a client with a real filter switches to POST
when the query string will not carry it. The XML form is reduced to the same key-value pairs the
KVP form arrives as and handed to one binder, so version negotiation, format negotiation, paging and
every refusal message behave identically whichever way the request came in. Declaring
`XMLEncoding` TRUE and implementing one encoding is the failure §6 warns about, in the document a
client trusts most.

**One endpoint, `/wfs`, for the whole server.** A WFS client's model is a single service URL whose
capabilities list the feature types. The URL an administrator pastes is the same one for every
layer, which is the property `/rest/services` has and a per-service endpoint would lose.

> **AMENDED 2026-08-19: one namespace, not one per folder — reversed by measurement.**
> This paragraph made each folder a namespace prefix, so type names read `hosted:tr_yol` and
> `turkiye:tr_il`. Nothing in WFS requires that; it was chosen because it reads better.
>
> **It cost 264 of the OGC conformance suite's tests.** `DescribeFeatureType` naming no feature
> types has to answer for all of them at once, and across two namespaces the specification's own
> answer is a schema of `xsd:import` elements rather than a schema of declarations. The suite
> builds its model from exactly that request — `SuiteFixtureListener` prepares
> `?service=WFS&version=2.0.0&request=DescribeFeatureType` and hands the URI to the schema
> model — so it found no feature types at all and every filter test reported *no schema can be
> found*. Measured: 71 passed and 336 failed with folders as namespaces; 297 passed and 94 failed
> with one.
>
> **What changed and what did not.** Feature types are now `graticula:<layer>` in the single
> namespace `urn:graticula:ns`. Layer names are unique across this server already — that is what
> makes `/admin/layers/{name}` a key — so nothing collides. The folder moved to the feature type's
> `Title`, where a person browsing the capabilities still sees it, and it is gone from the type
> name, where only a machine ever read it. **The prefix is still resolved rather than read**: a
> request may bind its own prefix through `NAMESPACES` and that binding is honoured, which is the
> defect the same suite found first.
>
> **Recorded as a reversal rather than a correction.** The original reasoning was not wrong about
> readability. It was wrong about cost, and the cost was invisible to every client that reads the
> capabilities before asking — GDAL included. Only a suite that asks the awkward question found it.

**Two output formats.** `application/gml+xml; version=3.2` is the default, `application/geo+json`
is offered beside it. **The GML writer lives in the adapter, not in `Core/Formats`** — GML is a
protocol format here and GeoJSON is not, which is the §51 line drawn at a file rather than in prose.

**The filter is parsed by us into the domain, and never into SQL.** Filter Encoding 2.0 becomes
`SpatialFilter` and `ParsedWhere` through the same two steps `WhereClause` applies: identifiers
matched against the layer's real columns, operators from a fixed table, every literal bound.

**And that forces the seam [ADR-008](ADR-008-query-engine.md) deferred.** `WhereClause` today is a
single pass that appends SQL while it parses; its own remark describes *an expression tree and an
emitter rebuilding the statement from it*, and there is no tree ([D-117](../architecture-debt.md)).
One front end can live with that. Two cannot — the alternative is a compatibility adapter that emits
SQL, which is the thing §51 exists to prevent. **So the emitter is extracted first and both front
ends target it:** `WhereClause` keeps its grammar, the Filter Encoding front end gets its own, and
one place turns a predicate into parameterised SQL. This is ADR-008's query AST arriving because a
second caller demanded it rather than because it was anticipated.

**What that does and does not mean, because the wording above can be read too strongly.** The
adapter builds a tree and hands it to `PredicateSql`; it never assembles SQL itself, which is what
*never into SQL* means here. `FeatureQuery.Where` is still a `ParsedWhere` and this ADR does not
change that — moving the query model's own boundary onto the tree is
[D-40](../architecture-debt.md), it belongs to [ADR-008](ADR-008-query-engine.md) §4a-i, and it is
not paid here. The ArcGIS surface already works this way and WFS joins it rather than departing
from it. **Done 2026-08-19**, with every existing where-clause test passing unchanged through the
extracted emitter — which was the falsification the debt row asked for.

**XML is read defensively from the first line.** DTD processing prohibited, no external entity
resolution, no XInclude, bounded nesting depth and bounded document size. This is
[D-41](../architecture-debt.md)'s lesson applied before the fact instead of after it: the input is a
language, and a language reaching a parser nobody hardened is the shape that finding had.

**Identity uses Q-57's asymmetry rather than fighting it.** A feature is `{typeName}.{id}`, where
`id` is the layer's nominated identity column and is string-valued where the column is. Q-57
recorded that FeatureServer's unique-integer-OID requirement makes a UUID-keyed or text-keyed
registered table unservable through the compatibility layer — **through WFS those tables become
servable, and this is the first surface on which they are.**

**Sharing is the catalogue's, not a new one.** Every route carries a `SharingGoverned` marker or
`/admin/routes` lists it as ungoverned and a test fails. `GetCapabilities` **filters** to what the
caller may see, the way the services directory does, rather than refusing — so an anonymous client
sees the public feature types and learns nothing about the rest.

**And it obeys the capability limits, which took a correction.** A service whose `servesFeatures` is
false answers 404 at the ArcGIS door; until 2026-08-20 this surface read its layers regardless.
`VisibleAsync` applied three of the four checks the rest of the server applies and not the fourth,
so an operator who had closed the feature face was still serving it. **Sharing was never the hole
— configuration was**, and the difference matters: the callers were entitled to the data and the
operator had said not through this product. Fixed the day the REST directory started linking here,
which is how it was found. [D-123](../architecture-debt.md).

**The surface is discoverable from the directory, added 2026-08-20 on the owner's question.** An
ArcGIS Server directory prints `JSON | SOAP | WMS | WFS` on each service page and that line is how
anybody learns a service speaks a second protocol; this one had spoken WFS for a day without the
word appearing anywhere a person browses. A feature service page now offers its capabilities and a
layer page its own `DescribeFeatureType`. **The link is built by `WfsEndpoints` rather than by the
renderer**, because the WFS type name is the layer's own name and the ArcGIS layer id is a number:
deriving one from the other is a guess, and a page that advertises a type this server does not
publish is worse than a page that says nothing. Asserted for every layer of every service by
`WfsConformanceTests`, including that a group layer offers none.

## 6. Consequences

- **Every feature type now publishes `ows:WGS84BoundingBox` —
  [Q-125](../open-questions.md), 2026-08-25.** §5 wrote it only for layers already in
  4326, on the recorded ground that projecting an extent costs a round trip per layer
  and this document must stay cheap. **The cost was wrong.** `IProjector` takes a list,
  so extents batch by source reference: a thousand layers over a handful of references
  cost a handful of calls. WMS had been doing exactly that since its own capabilities
  document was written, one assembly away, because `EX_GeographicBoundingBox` is
  mandatory there — the surface that *could* omit the element is the one that got the
  wrong answer, and being allowed to skip the work is what stopped anybody costing it.
  The batching is now `GeographicExtents`, used by both, and WMS's own copy is gone.
  **What did not change is the rule the old behaviour was protecting**: the element
  carries a *geographic* extent or nothing, never the layer's own easting under a WGS 84
  label. `WfsFeatureType` therefore carries `Geographic` beside `Extent` rather than
  overloading one field. Measured on eight 3857 layers: 0 boxes before, 8 after, and the
  document answers in 23 ms.

**Positive.** A non-Esri client can read this server for the first time. Q-57's identity asymmetry
inverts in our favour. ADR-008's emitter seam gets built by a second caller demanding it, which is
§82's own test of whether it was needed. And this is **face two over the feature engine**:
[A-026](../architecture-assumptions.md) has been `UNVALIDATED` since it was written, on the grounds
that an abstraction exercised by one implementation is not an abstraction, and this is the first
thing that exercises it.

**Negative.** An XML subsystem where there was none, with the attack surface that implies, in a
repository whose Security gate has already failed once on caller text reaching an interpreter
unparsed. GML 3.2 is large and dated, and what ships will be a subset of it. **We have no external
conformance evidence and no route to any** — the peer this product is measured against publishes
1117 of 1117 across 13 OGC CITE suites, and our conformance suite is tests we wrote against our own
reading of a specification ([Q-122](../open-questions.md)). And the surface grows while the §66 gates
stand at FAIL, which is the cost §1 records the owner accepting.

**Ports created.** None. No Tier 2 dependency is adopted: `System.Xml` is the base library, and GML
is written from `Graticula.Geometries` types rather than from a geometry library.

## 6b. The suite re-run, 2026-08-21: 297 of 391 became 405 of 420

**§6 above records a number and not a breakdown, and that was the whole problem.** The
2026-08-19 run left 94 failures and nobody wrote down what they were, so for two days
the only thing anybody could say about this surface was a figure that was already
stale. Re-run on the owner's instruction. Evidence:
[cite-wfs20-2026-08-21.rdf](../reviews/cite-wfs20-2026-08-21.rdf).

**The first re-run measured 104 of 420, which reads worse than 94 of 391 and is not.**
Both the numerator and the denominator moved: `hosted/wms_time_demo` was published on
2026-08-20, and this suite repeats every assertion against every feature type, so one
more layer adds assertions to both columns. **That is the property that makes a raw
count from this suite almost meaningless**, and the same property is what turns a
single missing operator into twenty-five failures.

**Five causes, and the arithmetic is the finding.**

| Was | Cause | Now |
|---|---|---|
| 25 | An unknown `ResourceId` answered 400. WFS 2.0 §7.9.2.4.2 says it selects nothing | **Fixed** — an empty collection |
| 25 | `Not(PropertyIsNull(geom))`: `DescribeFeatureType` publishes the geometry, the filter refused it | **Fixed** — the null predicates accept it, comparisons still do not |
| 24 | `PropertyIsNull(gml:name)`: GML's inherited properties were not modelled | **Fixed** — matched by the namespace, answered without SQL |
| 13 | Two `ResourceId` children of one `Filter` returned one feature | **Fixed** — Filter Encoding §7.11's implicit union |
| 4 | A date literal reached PostgreSQL as text and the response was **500** | **Refusal corrected**, limitation kept — [Q-124](../open-questions.md) |

**Four fixes, and none of them is large.** `PropertyIsNotNull` was one case in a switch
and one argument, because `AttributePredicate.IsNull` already carried a `Negated` flag
for the ArcGIS front end. The implicit union was `FirstOrDefault()` reading the first of
two siblings. The unknown identifier needed a predicate meaning *nothing matches*, which
the query model had no way to say — an empty `OneOf` is correctly refused — so
`AttributePredicate.MatchesNothing` exists now and emits `false`.

**The 500 is the one worth keeping.** A filter comparing a timestamp column to a literal
raised `42883`, which `ErrorResponse` classified as *the database does not have a
function this server needs; the usual cause is PostGIS not being installed*. The
database was healthy and the caller's filter was the problem. **That is the third time
that file has confused a caller's mistake for a connectivity failure** — its own
comments record 42883-for-schema and the timeout arriving *wearing the connectivity
costume* — and the pattern is now: `42883` whose message begins *operator does not
exist* is a 400 that says so. **The limitation itself is unchanged and deliberate**:
neither front end converts a date literal, so both give the same answer to the same
question (Q-124).

**Fifteen remain, and they are singles rather than families.** Four are Q-124's date
literals, honestly refused now. Two want 404 where a stored query gives 400, two are
`GetFeatureById` returning no identifier, four are exception-code XPath mismatches, one
emits `next` on a hits-only response, and one accepts a `GetCapabilities` with no
`service` parameter that should be refused. **None is a family, so none of them costs
twenty-five.**

**What this says about the number in §6.** 76% became 96% in an afternoon, and the work
was four small changes. The failures were never evidence that this surface was far from
conforming; they were evidence that nobody had read the report. A count from a
conformance suite is not a measurement until somebody groups it.

## 6c. The suite re-run, 2026-08-23: 420 of 420

**Every assertion the WFS 2.0 CITE suite makes about this surface passes.** Evidence:
[cite-wfs20-2026-08-23.rdf](../reviews/cite-wfs20-2026-08-23.rdf) — 420 passed, 0 failed,
390 untested. The untested are the conformance classes this decision declares FALSE:
Transaction, LockFeature and standard joins.

**§6b said the remaining fifteen were singles rather than families and that was almost
right.** Nine distinct defects, and one of them accounted for four of the fifteen. In the
order they were repaired:

| Was | Defect | Repair |
|---|---|---|
| 1 | A `GetCapabilities` with no `service` was answered | OWS Common makes it required; a missing one is `MissingParameterValue` |
| 2 | `gml:boundedBy` in a comparison answered `InvalidParameterValue` | It is a property every GML feature has, so the refusal is `OperationProcessingFailed` — *understood, and no* |
| 2 | `valueReference=""` answered `MissingParameterValue` | Present-and-blank is `InvalidParameterValue`; the binder keeps the empty string so the two can be told apart |
| 2 | `GetFeatureById` answered with a `wfs:FeatureCollection` | §7.9.3.6: the feature is the document |
| 2 | An identifier naming nothing answered 400, or 200 with an empty collection | 404, for all three shapes of *not there* |
| 1 | No `next` or `previous` on a partial result set | §7.7.4.4.1, built from the request's own query string |
| 1 | No `next` on a hits-only response | A hits response is page zero — and the link states `resultType=results`, or a client loops on it |
| 4 | A date literal reached PostgreSQL as text | [Q-124](../open-questions.md), answered — see below |

**The date literals are the finding, because §6b defended keeping them broken.** Its own
words: *the limitation itself is unchanged and deliberate — neither front end converts a
date literal, so both give the same answer to the same question*. The symmetry argument
was sound and the conclusion was wrong, for a reason visible from outside this decision:
**the OGC API Features face had been converting dates correctly since it was written.**
There were three front ends, not two, and the one nobody compared against had already
answered the question — `DateTimeOffset.TryParse` with `AssumeUniversal |
AdjustToUniversal`, and Npgsql maps a `DateTimeOffset` to `timestamptz`, which PostgreSQL
compares with a `timestamp` column as well.

So both remaining front ends convert now, and the symmetry §6b wanted is a symmetry of
working rather than of failing. The ArcGIS `where` clause needed one more thing than the
WFS filter did: `WhereClause.TryParse` knew column *names* and not types, so it takes an
optional map of them. Optional, because a caller that does not know the types gets exactly
the behaviour it had.

**What this cost and what it bought.** Seven changes, none large, and the surface went
from 96% to 100%. **What it did not buy is a standing number** — this ran by hand in a
container, [D-63](../architecture-debt.md) means nothing runs it again, and a number
earned that way decays. `WfsCiteRepairTests` asserts all nine repairs against the running
server through the suite that does run, so a regression is caught by something other than
somebody remembering to start Docker.

**And two of the nine repairs were wrong on the first attempt, both caught by measuring
rather than by reading.** The paging link preserved every parameter, so a hits response
linked to itself — same document, same link, for ever — and the first version of the
`next` guard then removed the hits link entirely, which is what the suite had asked for.
Both are asserted now, in the same test, because *the attribute is present* and *the
attribute is useful* are different claims and only the first is what a schema checks.

## 7. Conditions

1. **A real QGIS connects to `/wfs` and draws a layer, and a real GDAL `ogr2ogr` reads one.** This
   is the acceptance criterion — not our own tests agreeing with each other, which is what Q-94
   named as owed for the ArcGIS path and is still unpaid there.

   **PARTLY DISCHARGED 2026-08-19 — the GDAL half is paid and the QGIS half is not.** GDAL 3.11.3
   turned out to be on the development machine already, inside ArcGIS Pro's Python environment, and
   [tools/wfs-client-probe.py](../../tools/wfs-client-probe.py) drives its WFS client against this
   server. **GDAL opens the service, lists all twelve feature types, reads their fields out of
   DescribeFeatureType, asks `resultType=hits` for a count, reads features, phrases its own
   `fes:Filter` and its own `BBOX`, and converts to GeoJSON, GeoPackage and Shapefile.** Every check
   passes on a 4326 line layer, a 3857 polygon layer and a registered layer in a second folder.

   **The axis order is the finding that matters**, because it is the one nothing here could have
   checked alone: GDAL places the first feature of `hosted:tr_il` at 35.04, 39.06 — in Turkey — and
   the parcels layer at 3 657 875, 4 862 975 in web-mercator metres. Had the GML been written
   longitude-first under a latitude-first reference, GDAL would have transposed it silently and the
   data would be in the Gulf of Guinea with no error anywhere.

   **And it answered [A-074](../architecture-assumptions.md), which was the premise under a
   rejection.** GDAL sent 394 `GetFeature` requests and put an `outputFormat` on none of them: it
   takes the default, which is GML. So §4 Alternative B was rejected for the right reason.

   **A real ArcGIS Pro connected and drew, 2026-08-19.** Pro 3.6.0 added the endpoint as a WFS
   server over verified TLS, listed all twelve feature types, and drew `tr_il` and `tr_kara` on a
   map. So the *person opening a map* half of this condition is answered by a desktop GIS, and the
   axis order is now confirmed by a third independent implementation: the provinces are over
   Turkey. Two smaller things came with it. **The folder shows where it was moved to** — the layer
   list reads `hosted / tr_il`, which is what the title is for now that the type name is flat. And
   **a layer looked truncated and the server had said otherwise**: the client's own request limit
   was 3,000, our response carried `numberMatched="5433"` beside `numberReturned="3000"`, and
   raising the limit drew the rest. The response was honest and the client did not surface it,
   which is worth knowing before the next report of missing features.

   **What is still owed is QGIS specifically, and the reason it still matters is not stubbornness.**
   The QGIS 3.28.0 on this machine is a 694 KB leftover with no application to run, so it needs
   installing. It is not interchangeable with the two clients that have now worked: QGIS implements
   WFS itself rather than through GDAL, and it is the client of the population that justified
   building WFS ahead of OGC API Features in §1. Pro and GDAL between them show the documents are
   readable; neither shows that the market this was built for can read them.
2. **DISCHARGED 2026-08-20 by [wfs-filter-review-1](../reviews/wfs-filter-review-1.md), and it
   was worth every line of the condition.** An independent reviewer, working against the running
   server rather than the source, **took the process down twice with a single unauthenticated
   223 KB POST**: nested GML collections recursed past the stack because the depth guard stopped at
   the boundary between two readers ([D-122](../architecture-debt.md)). Two smaller findings came
   with it — an XML `request` attribute could override the operation its own root element named, so
   the two encodings disagreed; and the capabilities' abstract still denied `GetPropertyValue`
   hours after §5 started advertising it. All three are fixed and the reviewer's own payload now
   answers an exception report.

   **What it found sound is what the coverage claim now rests on**, and it was tried rather than
   read: injection refused down all ten paths into SQL, every partial-filter refusal holding
   (`Or`/`Not`/two spatial/bbox with spatial/`matchCase="false"`/unknown element), DTD, XXE,
   entity expansion and the size bounds, no file read and no request this server could be made to
   issue, and KVP against XML agreeing everywhere except the one finding.

   **One area is reported as not fully tested and is not counted as covered**: anonymous against
   authenticated indistinguishability end to end, because the reviewer had no credentials and the
   login throttle correctly refused guessing. It was assessed from the code and the residual —
   a timing difference against a layer that exists and is hidden — is named in the report rather
   than left out of it.

   **The lesson is the one §67 exists for.** The author's own tests covered filter nesting and
   stopped exactly where the geometry began, and the remark in `SafeXml` said the tree was counted
   by *the reader* while there were two. A self-review reads the code's account of itself, and that
   account is what a self-review cannot audit.
3. **DISCHARGED 2026-08-19, and the falsification found the test rather than the code.**
   `SafeXmlTests` attacks the reader with an external entity, an entity-expansion bomb, a document
   past the character bound and a filter past the depth bound, and asserts the settings directly.
   **Falsified by flipping `DtdProcessing` to `Parse` and restoring the resolver — and on the first
   run only two of the three went red.** The external-entity test pointed at `/etc/passwd` on a
   Windows machine, so it had been failing on a missing file rather than on a refusal and would
   have passed with the defence removed. It now writes its own file with a sentinel in it, and all
   three go red. **The same run found a real defect:** `MaxCharactersFromEntities` was `0`, which
   in `XmlReaderSettings` means *no limit* rather than *no characters* — the strictest-looking
   value is the one that turns the bound off, and the remark beside it claimed a protection it was
   not providing. That is D-41's shape again, caught this time by attacking the code instead of
   reading it.
4. **DISCHARGED 2026-08-19, and it found a hole in an older guarantee.** Both `/wfs` routes carry
   `SharingGovernedExtensions.ByFiltering`, and `/admin/routes` reports `ungoverned: 0` over 69
   routes including them — verified against the running server. **It reported the same zero before
   the repair, about 67 routes, because its filter was `/rest/services` and WFS is not under it.**
   The markers were applied and nothing read them: ADR-018 condition 5 was being kept for one
   surface and reported for all of them. [D-119](../architecture-debt.md), fixed the same day, with
   the conformance suite's *audit covers every kind of route* test now naming `/wfs` so that
   forgetting the next surface fails instead of passing.
5. **DISCHARGED 2026-08-19 by [tools/wfs-schema-check.sh](../../tools/wfs-schema-check.sh)**, which
   validates this server's documents against `schemas.opengis.net` with `xmllint` — something that
   is not us, reading the specification's own schemas. Every document for all twelve public feature
   types validates: capabilities, both stored-query documents, the exception report, each
   DescribeFeatureType as an XML Schema, and each feature collection against `wfs.xsd` **plus this
   server's own generated schema**, which also tests that the schema we publish describes the
   features we serve. **It found two violations on its first run**, both invisible to every test we
   had written because every test we had written came from the same misreading: `ows:ServiceProvider`
   was missing its required `ServiceContact`, and the Filter Encoding capability elements were
   spelled `fes:IdCapabilities` where the schema says `fes:Id_Capabilities`. A third gap surfaced
   from the same work — the feature collection carried no `xsi:schemaLocation`, so nothing could
   find the application schema — and is now written.
6. **DISCHARGED 2026-08-19, the day it was written.** ADR-005 §3.1 and §3.3 record that the adapter
   arrived before the native API it adapts. §3.1 now says the native API is the **third** face over
   the feature engine rather than the first, and that the judgement about WFS being heavy and dated
   is untouched by the change of order; §3.3 says the interface its adapters sit over has never been
   exercised by a native surface, which is A-026 rather than a description. Discharged immediately
   because the alternative was a document that goes false while somebody writes code against it,
   and this repository has recorded that failure four times.

## 8. Assumptions this decision rests on

| ID | Assumption | Status |
|---|---|---|
| [A-026](../architecture-assumptions.md) | The protocol-neutral internal interface can carry faces with different query languages, identity models and error conventions | `UNVALIDATED` — **this is its second face and therefore its first real test** |
| [A-074](../architecture-assumptions.md) | A QGIS or GDAL client asking for the default output format gets GML 3.2, so a GeoJSON-only WFS would not have connected | `UNVALIDATED` — it is the premise under §4 Alternative B, and it is a day of testing rather than an argument |

## 9. Revisit triggers

- **Condition 1 fails** — a real QGIS does not connect, or connects and draws nothing. Then the
  surface is wrong rather than incomplete, and Alternative A is reconsidered with evidence.
- **The second front end does not fit the extracted emitter**, or fitting it requires a third
  emitter. A-026 is then false, and ADR-005 §3's protocol-neutral claim with it.
- **Somebody asks to edit through WFS.** Alternative C returns, with the XML reader already proven
  by then rather than new.
- **OGC API Features is asked for before WFS ships.** Then §1's ordering was wrong and the cheaper
  surface should have gone first.

## 10. Dissent

**The register recommended the other protocol, and it was not refuted.** Q-94's recommendation was
OGC API Features first; ADR-005 §3.1 records the owner's own earlier judgement that WFS is *heavy
and dated*. Both stand. This decision overrides them by ordering rather than by argument, and the
ADR does not pretend otherwise.

**This is the second time an adapter has been built before the thing it adapts.** ArcGIS
FeatureServer was the first, by Q-88. Each time, the compatibility surface defines the seam the
native API will later have to fit. If the third face turns out to be expensive, we will have
discovered that A-026 is false after building two faces on the assumption that it is true — which is
late, and is the risk this ADR is knowingly taking.
