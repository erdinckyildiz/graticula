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

## 1a. Does it use NetTopologySuite? — `INFERRED`, not established

Asked 2026-08-12, after our own tile benchmark found NTS's general overlay and
Douglas-Peucker to be 79% and 55% of a tile request respectively
([benchmarks/mvt-generation/RESULTS.md](../../benchmarks/mvt-generation/RESULTS.md)).
If the closest .NET peer hit the same wall, that is worth knowing.

**The README does not say.** It names .NET 10, PostGIS 3.4-3.6 and Redis, and
describes a Geometry Service doing "buffer, project, intersect" plus MVT output,
without naming any geometry library. Establishing it definitively means reading
`Directory.Packages.props` or the `.csproj` files, which is a call for the
project owner rather than an inference: a package manifest is arguably build
metadata rather than implementation, but the boundary at the top of this
document was drawn deliberately strictly and it is not ours to loosen.

**`INFERRED`, high confidence:** yes, it uses NetTopologySuite. On .NET there is
no serious alternative for buffer and intersect — NTS is the only maintained
port of JTS, and Npgsql's spatial plugin is `Npgsql.NetTopologySuite`, so any
.NET service reading PostGIS geometry as objects arrives at NTS almost by
default. Labelled `INFERRED` under the CLAUDE.md rule and not to be cited as
fact.

**What matters more than the answer, and is established:** Honua **requires**
PostGIS, and its SQL Server, Oracle, DuckDB and warehouse sources are
**read-only**. That means its primary path can use `ST_AsMVT` — the 62 ms path
in our benchmark — and it may never have needed the in-process encoder at all.

So the comparison does not transfer. Our A-019 exists because we promised
first-class *write-capable* Oracle and SQL Server providers (Q-50a), which
commits us to encoding tiles in-process on engines that cannot do it in SQL.
Honua declined that commitment. Whichever geometry library it uses, it is
solving a smaller problem on the hot path, and finding that it uses NTS
happily would not be evidence that NTS is adequate for ours.

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
