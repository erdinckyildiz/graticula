# Honua Server — the closest direct peer found so far

**Status:** FIRST PASS — from the public README only. Everything marked `VERIFY`.
**Source:** <https://github.com/honua-io/honua-server>
**Raised by:** project owner, 2026-08-12
**Feeds:** Q-49 (competitive position), Q-50 (providers versus migration),
Q-17 (ArcGIS-compatible surface), [ADR-001](../adr/ADR-001-core-language.md)

---

## ⚠ Clean-room boundary — read this first

Honua Server is **Elastic License 2.0**. Its source is visible, which makes it
more tempting to read and no less dangerous to.

**Permitted:** the README, published documentation, the protocol surface it
advertises, its architectural choices as publicly described, and its observable
behaviour if we ever run it.

**Not permitted:** reading the implementation, then writing our own version of
what we read. That is a clean-room violation under our own §5 and a legal risk
under ELv2. It applies with more force here than to ArcGIS, not less, because
the code is right there.

The value in this project is *what it supports* and *which bets it took*. Both
are fully visible without opening a source file.

## 1. What it is

`VERIFY` all of the following against the current README.

> "One container exposes the same PostGIS-backed data through every major GIS
> protocol" — without duplication or ETL.

| | |
|---|---|
| Runtime | .NET 10 |
| Primary backend | PostGIS, read and write |
| Other sources | DuckDB, SQL Server, Oracle, MySQL/MariaDB, Redshift, Snowflake, Databricks — **read-only** |
| File formats | **Import only, not providers.** `VERIFY` GeoJSON, Shapefile (zip), GeoPackage, GPX, KML, WKT, FlatGeobuf, File Geodatabase, GeoParquet — "import … directly, with CRS auto-detection and PostGIS reprojection" |
| GDAL | **Not in the serving container.** `VERIFY` "the serving container ships no GDAL, while optional geoprocessing worker images bundle it separately" |
| Cache and jobs | Redis, with in-memory fallback |
| Deployment | Stateless, container-first, Kubernetes via Helm |
| Observability | OpenTelemetry |
| Licence | Elastic License 2.0, open core with paid entitlements |

**Protocols:** GeoServices REST (FeatureServer, MapServer, ImageServer,
GeometryServer, GPServer), OGC API (Features, Maps, Tiles, Coverages, Processes,
Records, EDR, Styles), classic WMS/WFS/WMTS/WCS, STAC, OData v4, MVT, 3D Tiles,
gRPC with h2c, and MCP over JSON-RPC for AI agents.

**Maturity:** `VERIFY` 4 stars, 1 fork, 4,608 commits, 56 open issues, **no
tagged releases** — nightly container builds only.

## 1a. Dependency manifest — established, 2026-08-12

**Clean-room note.** The owner authorised reading `Directory.Packages.props`.
That is central package management metadata: a list of what the project depends
on and at which version. It is not implementation, it teaches nothing about how
any problem was solved, and the boundary at the top of this document — *do not
read the implementation and then write our own version of what was read* —
remains fully in force. See §1b for the tiering this established.

Read from `trunk` (the default branch is `trunk`, not `main`). 133 packages.

### The question that was asked

**Yes — `NetTopologySuite 2.6.0`.** One minor version ahead of the 2.5.0 our
benchmark ran against. Also `NetTopologySuite.IO.Esri.Shapefile 1.2.0`,
`NetTopologySuite.IO.GeoJSON 4.0.0`, `NetTopologySuite.IO.GeoPackage 2.0.0`.

The prior inference was correct, but see §1c: the answer matters less than the
question of whether their hot path resembles ours, and it does not.

### What else the manifest establishes

| Fact | Packages | Why it matters to us |
|---|---|---|
| **No GDAL binding anywhere** | none present — no `MaxRev.Gdal`, no `OSGeo.*` | Confirms the README's claim beyond doubt, and goes further than we assumed: GDAL is not a managed dependency of *any* project in the solution. |
| **File import is done with NTS IO packages, not GDAL** | `NetTopologySuite.IO.Esri.Shapefile`, `.GeoPackage`, `.GeoJSON`, `FlatGeobuf 3.26.0`, `Parquet.Net`, `ParquetSharp`, `Apache.Arrow 22.1.0` | **This challenges our reasoning on Q-28 / A-016.** We accepted "no GDAL in the serving container" as a deployment rule while still assuming GDAL does the format work in a job worker. Shapefile, GeoPackage, FlatGeobuf and GeoParquet all have managed .NET readers. The GDAL dependency may be smaller than ADR-009 assumes. |
| **They render** | `SkiaSharp 3.119.2` + Linux native assets, `Mapsui 5.0.2` | Consistent with advertising WMS, MapServer and ImageServer. The relevant point for us is that our ADR-004 deferral is a real divergence, not a shared assumption. |
| **MVT via generated protobuf** | `Google.Protobuf 3.34.1` | `INFERRED`: they generate the MVT writer from the `.proto` rather than hand-writing it. Ours is hand-written and costs 3.8 ms at z14. No evidence either way on their cost. |
| **Multi-engine, and tested against real engines** | `Microsoft.Data.SqlClient`, `Oracle.ManagedDataAccess.Core`, `MySqlConnector`, `Snowflake.Data`, `DuckDB.NET.Data.Full`, plus `Testcontainers.MsSql / .Oracle / .MySql / .PostgreSql 4.11.0` | **Directly actionable — see D-05.** They do not require developers to install SQL Server or Oracle; the test suite starts them as containers on demand. That is the cheap repayment path for our own deferred measurement. |
| **Load testing is a first-class dependency** | `NBomber 6.3.0`, `NBomber.Http 6.2.0` | Relevant to A-037. We need a concurrency harness and there is an established .NET one. |
| **Architecture is enforced by test** | `NetArchTest.Rules 1.3.2` | Worth stealing as a practice, not a design: our build-vs-adopt tiering says "no library type in a Tier 1 signature", which is exactly the kind of rule this asserts automatically. |
| **Operational stack** | `Yarp.ReverseProxy`, `Aspire.Hosting.*`, `StackExchange.Redis`, `Serilog`, `OpenTelemetry.*`, `Confluent.Kafka`, `NATS.Net`, `Polly` | Kafka and NATS both present. Our §6 anti-overengineering list challenges exactly this; noted without judgement, since a dependency list does not show whether they are optional. |
| **Cloud-coupled** | 11 `AWSSDK.*` packages, 5 `Azure.*`, `Amazon.Lambda.*` | A different product shape from ours. Our baseline deployment target is `gis-server → PostgreSQL/PostGIS` on one machine. |

**Caveat on all of the above.** Central package management lists what the
solution may reference; it does not show which project references what, or
whether a package is optional, or whether it is used at all. Every row is a fact
about the manifest, not about the running server.

## 1b. Clean-room tiering, established by this decision

The owner authorised Tier B on 2026-08-12. Recording the tiers so the next
question does not have to relitigate the boundary.

| Tier | What | Status |
|---|---|---|
| **A** | README, published docs, protocol surface, observable behaviour of a running instance | **Permitted** since the first pass |
| **B** | Dependency manifests, CI configuration, `LICENSE` / `NOTICE`, default branch, repository metadata | **Permitted**, 2026-08-12. Facts about *what* is depended on, not *how* anything was solved |
| **C** | Directory layout, project boundaries, public API shape | **Not decided.** Medium risk: this is design, and design is what we are still deciding |
| **D** | Implementation of any subsystem we intend to write — tile encoder, geometry pipeline, provider abstraction, query translation | **Forbidden.** This is the only tier where clean room genuinely bites |

**Why the risk is not what it first appears.** Elastic License 2.0 prohibits
providing the software as a managed service, circumventing licence keys, and
removing licensing notices. It does not prohibit reading, and it explicitly
grants the right to prepare derivative works. Reading is lawful. The exposure is
**provenance**: ELv2 code cannot be relicensed under AGPL, so once we have read
how a subsystem was implemented we permanently lose the ability to demonstrate
that our version was independently created. Clean room protects the ability to
prove independence, not the ability to avoid learning facts. Tier B carries no
provenance risk because there is nothing to independently create.

GitHub reports the licence as `NOASSERTION`, which is expected: ELv2 is
source-available and not OSI-recognised.

## 1c. Why the NTS answer does not transfer

Established from the README: Honua **requires** PostGIS, and its SQL Server,
Oracle, DuckDB and warehouse sources are **read-only**.

Its primary path can therefore use `ST_AsMVT` — the 62 ms path in our benchmark
— and it may never have needed an in-process encoder at all. Our A-019 exists
because Q-50a committed us to *write-capable* Oracle and SQL Server providers,
which forces in-process encoding on engines that cannot do it in SQL.

So: Honua using NTS 2.6.0 without apparent difficulty is **not** evidence that
NTS is adequate for our hot path. It is solving a smaller problem there. Our
own measurement found NTS's general overlay at 79% and its Douglas-Peucker at
55% of a tile request
([benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md)),
and nothing in their dependency list speaks to whether they hit the same thing.

## 2. The one idea we should probably take

**PostGIS is read/write; every other source is read-only.**

We framed Q-50 as a binary: read their Oracle in place, or move their data into
our datastore. Honua takes a third path we never wrote down, and it is smaller
than either.

What read-only providers delete from our design:

- provider-dependent **transaction semantics** across three engines — isolation
  levels, locking, what a conflict looks like — which
  [ADR-008](../adr/ADR-008-query-engine.md) currently carries as a known gap
- **write-side capability negotiation** entirely
- editing concurrency against a database we do not control, and with it much of
  **A-027**, the assumption that concurrency can be correct against writes we
  never see
- **Q-41**, the companion-schema question, since bookkeeping only matters where
  we write
- the editing half of the **RLS-versus-pooling conflict** (debt D-01), though
  the read half remains

What it costs: an organisation on Oracle cannot edit through us. They edit at
source — which the owner already said is how schema changes work anyway
([data-model.md](../data-model.md) §5), so the gap is narrower than it sounds.

**This is a serious candidate answer to Q-50 and it should be written into that
question as a third option.** It preserves the owner's requirement — an Oracle
shop is served without running PostgreSQL for their data — while removing most
of what makes multi-provider expensive.

## 2a. The second idea we should take — files are imported, not served

An initial reading of "supports many formats" was wrong. **Honua does not serve
file formats as providers. It imports them into PostGIS.**

That distinction is one we had not made, and our §27 provider list gets it
wrong. Not all formats are the same kind of thing:

| Kind | Examples | As a live provider |
|---|---|---|
| **Serving formats** | COG, PMTiles, GeoParquet, FlatGeobuf | Legitimate. Designed for range-request access, with internal indexes, meant to be read in place. |
| **Interchange formats** | Shapefile, KML, GPX, File Geodatabase, GeoJSON files | **Bad.** No concurrency control, no usable indexes, encoding inconsistency, file locking, and no way to detect that someone changed them. |

§27 currently lists Shapefile, GeoPackage and GeoJSON as *providers*. They should
be **import sources** instead — registered as a job, converted into the datastore,
and served afterwards as a first-class hosted layer with real indexes and real
capability. That is better for the user than serving a shapefile badly, and it
reuses the registration machinery [ADR-011](../adr/ADR-011-job-system.md) and
[ADR-009](../adr/ADR-009-raster-engine.md) already define for raster.

It also protects [ADR-008](../adr/ADR-008-query-engine.md). Every
capability-poor provider widens the matrix that "never degrade silently" has to
cover honestly, and interchange formats are the poorest of all.

## 2b. The rule worth adopting verbatim

> `VERIFY` "the serving container ships no GDAL, while optional geoprocessing
> worker images bundle it separately."

We reached the same placement in [ADR-009](../adr/ADR-009-raster-engine.md) §2.2
— GDAL on job workers, not the request path — but this is the cleaner statement,
because it is an **architectural rule about the artefact** rather than a decision
about where code runs.

Adopting it settles something we had left open. **Q-28 asked whether GDAL-backed
providers could be made optional so that a PostGIS-only deployment is genuinely
one artefact.** Under this rule the answer is yes by construction: the serving
binary never links GDAL.

Consequences:

- **A-016 and Q-28 are answered.** The single-binary story becomes real rather
  than aspirational, which restores weight to [ADR-001](../adr/ADR-001-core-language.md)'s
  C7 criterion that §3.1 had discounted.
- **Smaller attack surface.** GDAL parses untrusted files; keeping it out of the
  process that serves public requests is a security win, not only a packaging
  one.
- **The air-gapped checklist shrinks** (Q-15). GDAL driver data belongs to the
  job worker image only.
- **It constrains the file-provider decision above**: any provider needing GDAL
  cannot be a serving provider. That is a clean test, and it lands exactly where
  §2a's line falls.

## 3. What it tells us about ADR-001

Someone built a full multi-protocol GIS server in **.NET**, one of our two
prototype candidates, with 4,608 commits behind it.

That is not a benchmark and must not be treated as one. It is evidence that the
runtime is *adequate* for this workload, which is a weaker claim than our
prototype needs to make but a real one. It slightly raises the prior on .NET and
changes nothing about the requirement to measure.

## 4. Where it bet the opposite way

The contrast is more useful than the overlap, because each difference is a place
where one of us is wrong.

| Question | Honua | Us | Note |
|---|---|---|---|
| Protocol surface | **Everything, natively** | Narrow native API plus a compatibility layer | Ours assumes a narrow surface is easier to keep correct. Theirs assumes breadth is the product. |
| Redis | Cache **and durable job queue** | Optional cache, never load-bearing; jobs in the platform store, no broker | We rejected a broker under §82. They took the dependency. |
| Kubernetes | First class, Helm charts | Deprioritised until the platform works without it (§79) | |
| Rendering | MapServer and ImageServer surfaces — rendered output | Vector-first, client renders | Different products, not different implementations of one |
| Non-PostGIS sources | Read-only | Read and write, first class | §2 above |
| Licence | ELv2 open core, no managed-service rights | Copyleft acceptable, undecided | A product versus a commons |

**The protocol breadth difference is the sharpest.** Their pitch is that one
container speaks everything. Ours is that a narrow, well-specified native API
plus honest compatibility adapters is easier to keep correct. Those cannot both
be the better answer, and neither of us has evidence.

## 5. What it means for Q-49

Q-49 asks what we do that GeoServer cannot, and why it is worth a migration.
Honua sharpens rather than answers it, in two directions.

**In our favour:** someone else independently identified the same gap —
multi-protocol access over PostGIS with an ArcGIS-compatible surface — and
committed thousands of commits to it. The gap is not imagined.

**Against us:** `VERIFY` 4,608 commits have produced 4 stars, 1 fork and no
tagged release. Whatever the gap is, it is **hard to convert into users**, and
that is a caution rather than an encouragement. It may be very new, it may be a
solo effort, it may be under-promoted — but a competitor's difficulty finding an
audience is data about the market, not only about them.

**And it narrows the space.** If our answer to Q-49 turns out to be "multi-
protocol access over PostGIS", that answer is now taken, by a project with a
four-year head start in commits and a commercial licence. Our differentiator has
to be something else.

Candidates, none tested: the runtime that holds 1,000 services on one machine;
governance as the product rather than protocol breadth; vector-first as a
deliberate reduction; genuinely open licensing against an open-core competitor.

## 5a. Warehouses — recommend against

DuckDB aside, their read-only list includes Redshift, Snowflake and Databricks.

**Recommendation: no.** Not because the drivers are hard, but because warehouses
introduce a dimension this architecture has no concept of: **every query costs
money.**

Our caching, our seeding jobs and [ADR-011](../adr/ADR-011-job-system.md)'s
"re-run it on reclaim" semantics all assume compute is roughly free. Seeding a
tile pyramid from Snowflake could generate a large bill, silently, from a job
that looks like any other. §49's resource governance counts features, bytes and
time — it has no notion of currency, and no per-source spend budget.

That is a missing dimension in the architecture, not a missing driver. If a
warehouse provider is ever wanted, the prerequisite is a cost model, not a
connector.

## 6. Also worth noting

**MCP over JSON-RPC for AI agents.** We have not considered this at all. Whether
it belongs in a GIS server is a real question — it is either a genuine emerging
surface or protocol fashion, and there is no evidence yet either way. Recorded
rather than adopted.

**OData v4.** Also absent from our thinking. Relevant to enterprise BI
integration, and cheap if the query AST is genuinely protocol-neutral
([ADR-005](../adr/ADR-005-api-architecture.md) §3) — which is a decent test of
whether our neutral interface really is neutral.

## 6a. Capability matrix

A full protocol, provider, format and operations comparison is in
[honua-capability-matrix.md](honua-capability-matrix.md), compiled 2026-08-13
from the published README and the dependency manifest — Tier A and Tier B only.
Its headline: their surface is roughly five times ours, and the two features an
ArcGIS migration is actually made of — `applyEdits` and service import — are
behind Pro and Enterprise tiers respectively, which is exactly where Q-16 and
Q-17 chose to be free.

## 7. What to do next

1. **Read their published docs properly**, not just the README, and build an
   accurate protocol-coverage comparison. Source stays closed to us.
2. **Add read-only-providers as a third option to Q-50.**
3. **Run it**, if a container is available. Observing behaviour is permitted and
   more informative than reading about it — especially for the GeoServices REST
   surface, which is Q-17's feasibility question.
4. **Do not let this become a feature-parity exercise.** §1 of the master prompt
   is explicit that the goal is architectural excellence rather than feature
   imitation, and a competitor's protocol matrix is exactly the kind of thing
   that quietly becomes a backlog.
